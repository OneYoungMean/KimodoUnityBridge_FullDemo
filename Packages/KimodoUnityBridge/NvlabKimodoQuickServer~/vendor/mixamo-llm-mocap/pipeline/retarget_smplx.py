"""SMPL-X rotation retarget: GVHMR's joint ROTATIONS -> any Mixamo rig.

  python pipeline/retarget_smplx.py --spec action_specs/<motion>.json
         [--rig-profile rig_profiles/ybot.json] [--out-dir clips/<clip>]

Reads the estimator's SMPL-X parameters (smplx.npz, written by
estimate_pose_gvhmr.py) and writes <clip_dir>/motion.npz — quaternions
for every bone, Hips location, and the plate's camera — which
bl_motion.py keys onto the rig headlessly (`apply`, `render`).

Why rotations and not landmarks
  The legacy lift (lift_to_mixamo.py) reduces the body to 33 points
  and rebuilds the pose by aiming bones at them. Positions cannot say
  how a forearm, a chest or a head is TWISTED, so twist was invented:
  the spine was synthesized from the hip/shoulder lines, the hand was
  aimed along the forearm, the head re-aimed from gaze heuristics.
  SMPL-X already solved all of that. Here every Mixamo bone receives the
  rotation of its SMPL-X joint:

      P_b(t) = S_j(t) . A_b . B_b             (armature space)

  S_j  world rotation of SMPL-X joint j (identity at the SMPL-X rest)
  B_b  rest rotation of Mixamo bone b
  A_b  rest alignment: the minimal rotation taking the Mixamo bone's
       rest direction onto the SMPL-X one, for the LIMBS only. It
       cancels rest-pose differences (SMPL-X arms hang ~16 deg below a
       T-pose) so they don't bias every frame. Feet are aligned in yaw
       only, on the foot's visible heel-to-toes axis. Spine, neck, head
       and collars are left unaligned: there the skeletons differ in how
       they divide the body, not in pose, and "correcting" that injects
       error (a hunched spine, raised shoulders).

  A Mixamo armature's own space (inside its X+90 import rotation) is
  already SMPL-X's: X left, Y up, Z forward — no axis conversion.

What cannot be copied, and is solved instead
  - proportions: the root follows the performer's trajectory scaled by
    the LEG-length ratio; each leg is re-solved (two-bone IK, knee kept
    in the source's plane) so the ankle lands where the performer's
    scaled leg puts it.
  - the floor: GVHMR's world trajectory drifts vertically (a performer
    floating 15 cm above the floor mid-clip is typical). The camera
    removes the drift; frames with a planted foot put the lowest sole on
    the floor; flight frames keep the source arc.
  - foot contacts: a foot is planted when the network's contact detector
    says so, when its sole sits still on the floor, when it does not move
    on screen, or when it carries the body alone (the other foot well
    up). Where the body stands and a free foot hovers just above the
    floor, its leg sets it down (height only). Planted segments lock flat, or pivot on the ball (heel raised) or
    briefly on the heel (toes up), with short ramps in and out; a real
    slide is left to slide, and no free foot goes under the floor.
  - fingers: GVHMR estimates none. detect_hands.py reads each hand on a
    zoomed crop of the plate; every finger joint bends by the measured
    angle. Where the detector lost the hand, a relaxed curl, closing into
    the rig-validated fist inside the spec's `fists` windows.
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import smplx_body as SB  # noqa: E402

REPO = Path(__file__).resolve().parents[1]

# SMPL-X body joint -> Mixamo bone (without the "mixamorig:" prefix).
JOINT_TO_BONE = {
    0: "Hips", 1: "LeftUpLeg", 2: "RightUpLeg", 3: "Spine", 4: "LeftLeg", 5: "RightLeg",
    6: "Spine1", 7: "LeftFoot", 8: "RightFoot", 9: "Spine2", 10: "LeftToeBase",
    11: "RightToeBase", 12: "Neck", 13: "LeftShoulder", 14: "RightShoulder", 15: "Head",
    16: "LeftArm", 17: "RightArm", 18: "LeftForeArm", 19: "RightForeArm",
    20: "LeftHand", 21: "RightHand",
}
# Joints whose rest DIRECTION is aligned (joint -> the child it points at).
ALIGN = {1: 4, 2: 5, 4: 7, 5: 8, 16: 18, 17: 19, 18: 20, 19: 21}
# Feet are aligned in YAW only (both rigs stand flat-footed at rest; only
# the toe-out differs), measured on the foot's visible axis — see
# foot_yaw_offset().
FEET = {7: "L", 8: "R"}
# Leaves carry their parent's alignment (a hand continues its forearm,
# toes continue their foot) — aligning a leaf on its own would measure
# the parent's geometry, not a pose difference.
INHERIT = {10: 7, 11: 8, 20: 18, 21: 19}

LEGS = {"L": (1, 4, 7, 10), "R": (2, 5, 8, 11)}          # hip, knee, ankle, ball (SMPL-X)
STATIC = {"L": (0, 1), "R": (2, 3)}                        # static_conf columns (ankle, ball)


def rpath(p) -> Path:
    p = Path(p)
    return p if p.is_absolute() else (REPO / p)


def smootherstep(x):
    x = np.clip(x, 0.0, 1.0)
    return x * x * x * (x * (x * 6.0 - 15.0) + 10.0)


def gaussian_smooth(x: np.ndarray, sigma: float) -> np.ndarray:
    """Gaussian smoothing along axis 0 with edge padding (no scipy)."""
    if sigma <= 0:
        return np.array(x, copy=True)
    r = int(np.ceil(3 * sigma))
    k = np.exp(-0.5 * (np.arange(-r, r + 1) / sigma) ** 2)
    k /= k.sum()
    pad = np.concatenate([np.repeat(x[:1], r, 0), x, np.repeat(x[-1:], r, 0)], 0)
    out = np.zeros_like(np.asarray(x, dtype=np.float64))
    for i, w in enumerate(k):
        out += w * pad[i:i + x.shape[0]]
    return out


def runs(mask: np.ndarray):
    """(start, end_inclusive) of each True run."""
    out, start = [], None
    for i, m in enumerate(mask):
        if m and start is None:
            start = i
        elif not m and start is not None:
            out.append((start, i - 1))
            start = None
    if start is not None:
        out.append((start, len(mask) - 1))
    return out


def clean_mask(mask: np.ndarray, min_len: int, max_gap: int) -> np.ndarray:
    """Fill short gaps, then drop runs shorter than min_len."""
    m = mask.copy()
    for a, b in runs(~m):
        if a > 0 and b < len(m) - 1 and (b - a + 1) <= max_gap:
            m[a:b + 1] = True
    for a, b in runs(m):
        if b - a + 1 < min_len:
            m[a:b + 1] = False
    return m


# ---------------------------------------------------------------------------
# the target rig

class Rig:
    """A Mixamo armature as retargeting needs it: rest rotations, rest
    head positions (METRES, armature space) and the parent table."""

    def __init__(self, profile: dict):
        if "bones" not in profile:
            raise SystemExit("rig profile has no bone table — run:  blender -b <rig>_rest.blend "
                             "-P pipeline/bl_motion.py -- dump-rig --profile <profile.json>")
        bones = profile["bones"]
        self.names = [b["name"] for b in bones]
        self.index = {n: i for i, n in enumerate(self.names)}
        self.parent = np.array([self.index[b["parent"]] if b["parent"] else -1 for b in bones])
        M = np.array([b["matrix_local"] for b in bones], dtype=np.float64)
        aw = np.array(profile["armature_matrix_world"], dtype=np.float64)
        self.unit = 1.0 / float(np.linalg.norm(aw[:3, 0]))     # armature units per metre (100 on FBX)
        self.arm_rot = aw[:3, :3] / np.linalg.norm(aw[:3, 0])  # armature axes -> Blender world axes
        self.arm_loc = aw[:3, 3]
        self.R = M[:, :3, :3]
        self.head = M[:, :3, 3] / self.unit
        self.rest_rel = np.empty((len(bones), 3, 3))
        for i, p in enumerate(self.parent):
            self.rest_rel[i] = self.R[i] if p < 0 else self.R[p].T @ self.R[i]
        self.floor = float(profile.get("mesh_floor_y", 0.0)) / self.unit
        self.prefix = "mixamorig:" if any(n.startswith("mixamorig:") for n in self.names) else ""
        self.root = self.bone("Hips")
        self.joint_bone = {j: self.bone(b) for j, b in JOINT_TO_BONE.items()}
        missing = [JOINT_TO_BONE[j] for j, b in self.joint_bone.items() if b is None]
        if missing:
            raise SystemExit(f"rig lacks bones {missing}")

    def bone(self, short):
        return self.index.get(self.prefix + short, self.index.get(short))

    def leg_length(self, side="Left"):
        a, b, c = (self.head[self.bone(f"{side}{n}")] for n in ("UpLeg", "Leg", "Foot"))
        return float(np.linalg.norm(b - a) + np.linalg.norm(c - b))

    def chain_positions(self, P: np.ndarray, root_pos: np.ndarray) -> np.ndarray:
        """Head position of every bone (T,B,3) from world rotations P (T,B,3,3)."""
        T, B = P.shape[:2]
        pos = np.empty((T, B, 3))
        for i in range(B):
            p = self.parent[i]
            if p < 0:
                pos[:, i] = root_pos
            else:
                local = self.R[p].T @ (self.head[i] - self.head[p])
                pos[:, i] = pos[:, p] + np.einsum("tij,j->ti", P[:, p], local)
        return pos

    def attached(self, P, pos, b, rest_point):
        """World position of a point rigidly attached to bone b (rest coords)."""
        local = self.R[b].T @ (np.asarray(rest_point, dtype=np.float64) - self.head[b])
        return pos[:, b] + np.einsum("tij,j->ti", P[:, b], local)


# ---------------------------------------------------------------------------
# procedural hands (no finger data in GVHMR)

def fist_quats() -> dict:
    """The rig-validated procedural fist of apply_mixamo_fk (curl = +X on
    this skeleton, thumb folds on a negative-X mix). Local rotations."""
    seg1 = (0.766044, 0.642788, 0.0, 0.0)
    seg2 = (0.67559, 0.737277, 0.0, 0.0)
    seg3 = (0.906308, 0.422618, 0.0, 0.0)
    out = {}
    for side, zs in (("Left", -1.0), ("Right", 1.0)):
        out[f"{side}HandThumb1"] = (0.961, 0.069, 0.165 * zs, 0.207 * zs)
        out[f"{side}HandThumb2"] = (0.766, -0.399, 0.161 * zs, 0.476 * zs)
        out[f"{side}HandThumb3"] = (0.940, -0.210, 0.136 * zs, 0.231 * zs)
        for fn in ("Index", "Middle", "Ring", "Pinky"):
            out[f"{side}Hand{fn}1"] = seg1
            out[f"{side}Hand{fn}2"] = seg2
            out[f"{side}Hand{fn}3"] = seg3
    return out


# MediaPipe hand landmark ids per finger: base (CMC / MCP) to tip
FINGERS = {"Thumb": (1, 2, 3, 4), "Index": (5, 6, 7, 8), "Middle": (9, 10, 11, 12),
           "Ring": (13, 14, 15, 16), "Pinky": (17, 18, 19, 20)}


def _angle(a: np.ndarray, b: np.ndarray) -> np.ndarray:
    c = np.sum(a * b, -1) / np.maximum(np.linalg.norm(a, axis=-1) * np.linalg.norm(b, axis=-1), 1e-9)
    return np.arccos(np.clip(c, -1.0, 1.0))


def hand_flexion(src: dict, rs: dict, min_score: float = 0.5):
    """Finger joint flexion from the video, per hand (L, R): (T, 5, 3)
    angles in radians at the destination clock — five fingers in FINGERS
    order, three joints each (MCP, PIP, DIP; thumb CMC, MCP, IP) — and a
    (T,) weight: how much to trust them.

    Angles between consecutive bones of MediaPipe's METRIC landmarks,
    which do not depend on the view. Confident frames only, a running
    median over them (a hand misread for a frame is not a twitch), gaps
    interpolated, and the weight falls to zero across gaps longer than a
    few frames — there the spec's fists and the relaxed curl take over.
    None without detect_hands.py output."""
    if "hands" not in src:
        return None
    world = np.asarray(src["hands"]["world"], dtype=np.float64)
    score = np.asarray(src["hands"]["score"], dtype=np.float64)
    n = world.shape[0]
    u = np.clip(rs["src_frame"] - 1.0, 0, n - 1)
    out = {}
    for k, side in enumerate(("L", "R")):
        ok = score[:, k] >= min_score
        idx = np.nonzero(ok)[0]
        if len(idx) < 5:
            continue
        W = world[:, k]
        ang = np.zeros((n, 5, 3))
        for f, (a, b, c, d) in enumerate(FINGERS.values()):
            base = W[:, a] - W[:, 0]
            s1, s2, s3 = W[:, b] - W[:, a], W[:, c] - W[:, b], W[:, d] - W[:, c]
            ang[:, f] = np.stack([_angle(base, s1), _angle(s1, s2), _angle(s2, s3)], 1)
        sm = np.empty_like(ang)
        good = ang[idx]
        med = np.stack([np.median(good[max(0, i - 2):i + 3], axis=0) for i in range(len(idx))])
        for f in range(5):
            for j in range(3):
                sm[:, f, j] = gaussian_smooth(np.interp(np.arange(n), idx, med[:, f, j]), 1.0)
        i0 = np.floor(u).astype(int)
        i1 = np.minimum(i0 + 1, n - 1)
        w = (u - i0)[:, None, None]
        weight = gaussian_smooth(ok.astype(float), 1.5)
        out[side] = (sm[i0] * (1 - w) + sm[i1] * w, np.interp(u, np.arange(n), weight))
    return out or None


def finger_quats(flex: np.ndarray, fists: dict, side: str) -> dict:
    """Local rotations of one hand's finger bones from its flexion angles
    (T, 5, 3). Fingers bend about the axis of the rig-validated fist (the
    bone's local +X on Mixamo skeletons), each joint capped a little past
    the fist's own angle. The thumb's fist folds on a mix of axes: it
    follows a single curl, its MCP and IP flexion over the fist's."""
    pre = "Left" if side == "L" else "Right"
    T = flex.shape[0]
    ident = np.tile([1.0, 0.0, 0.0, 0.0], (T, 1))
    out = {}
    for f, name in enumerate(FINGERS):
        fq = [np.asarray(fists[f"{pre}Hand{name}{j}"], dtype=np.float64) for j in (1, 2, 3)]
        fq = [q / np.linalg.norm(q) for q in fq]
        fist_ang = [2.0 * np.arccos(np.clip(q[0], -1.0, 1.0)) for q in fq]
        if name == "Thumb":
            curl = np.clip((flex[:, f, 1] + flex[:, f, 2]) / (fist_ang[1] + fist_ang[2]), 0.0, 1.0)
            for j in range(3):
                out[f"{pre}Hand{name}{j + 1}"] = SB.slerp(ident, np.tile(fq[j], (T, 1)), curl)
            continue
        for j in range(3):
            axis = fq[j][1:] / max(np.linalg.norm(fq[j][1:]), 1e-9)
            a = np.clip(flex[:, f, j], 0.0, 1.15 * fist_ang[j])
            out[f"{pre}Hand{name}{j + 1}"] = np.concatenate([np.cos(a / 2)[:, None], np.sin(a / 2)[:, None] * axis], 1)
    return out


def window_amount(frames_src: np.ndarray, rise, fall) -> np.ndarray:
    """0 -> 1 over `rise` (src frames), 1 -> 0 over `fall`, smooth edges."""
    up = smootherstep((frames_src - rise[0]) / max(1e-6, rise[1] - rise[0]))
    down = 1.0 - smootherstep((frames_src - fall[0]) / max(1e-6, fall[1] - fall[0]))
    return np.minimum(up, down)


# ---------------------------------------------------------------------------
# the retarget

def load_source(path: Path) -> dict:
    z = np.load(path, allow_pickle=False)
    src = {k: z[k] for k in z.files}
    src["foot"] = json.loads(str(src["foot"]))
    hands = path.with_name("hands2d.npz")           # detect_hands.py
    if hands.exists():
        h = np.load(hands)
        if h["world"].shape[0] == src["body_pose"].shape[0]:
            src["hands"] = {"world": h["world"], "score": h["score"]}
    return src


def catmull_rom(x: np.ndarray, u: np.ndarray) -> np.ndarray:
    """Interpolate samples x[0..n-1] (axis 0) at fractional positions u with
    a C1 Catmull-Rom spline. Linear interpolation between 24 fps samples
    puts a velocity kink at every source frame — a jerk spike every fourth
    frame at 30 fps, which reads as a faint stutter."""
    n = x.shape[0]
    i = np.clip(np.floor(u).astype(int), 0, n - 1)
    t = (u - i).reshape(-1, *([1] * (x.ndim - 1)))
    p0, p1 = x[np.clip(i - 1, 0, n - 1)], x[i]
    p2, p3 = x[np.clip(i + 1, 0, n - 1)], x[np.clip(i + 2, 0, n - 1)]
    return 0.5 * (2 * p1 + (p2 - p0) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t ** 2
                  + (3 * p1 - p0 - 3 * p2 + p3) * t ** 3)


def one_euro(x: np.ndarray, fps: float, min_cutoff: float, beta: float, d_cutoff: float = 1.0):
    """Zero-phase One-Euro filter along axis 0 of (T, J, D): an adaptive
    low-pass whose cutoff rises with speed. Holds and slow drifts are
    smoothed hard (that is where estimator jitter shows); a punch or a kick
    raises the cutoff and passes almost untouched — a fixed smoothing
    window cannot do both (docs/PITFALLS.md #18, #32). Run forward then
    backward so it adds no lag."""
    def alpha(cutoff):
        tau = 1.0 / (2.0 * np.pi * cutoff)
        return 1.0 / (1.0 + tau * fps)

    def one_pass(v):
        out = np.empty_like(v)
        out[0] = v[0]
        dx_prev = np.zeros(v.shape[1:])
        ad = alpha(d_cutoff)
        for t in range(1, v.shape[0]):
            dx = (v[t] - out[t - 1]) * fps
            dx_hat = ad * dx + (1.0 - ad) * dx_prev
            speed = np.linalg.norm(dx_hat, axis=-1, keepdims=True)
            a = alpha(min_cutoff + beta * speed)
            out[t] = a * v[t] + (1.0 - a) * out[t - 1]
            dx_prev = dx_hat
        return out

    return one_pass(one_pass(x)[::-1])[::-1]


def resample_source(src: dict, dst_fps: float, smooth: dict | None = None):
    """Source clock (plate fps) -> destination clock. Local rotations are
    interpolated as unrolled quaternions on a C1 spline (then normalised),
    translation on the same spline, confidences linearly. Optional One-Euro
    smoothing runs on the source samples first (`smooth`)."""
    fps = float(src["fps"])
    n = src["body_pose"].shape[0]
    local_aa = np.concatenate([src["global_orient"].reshape(n, 1, 3),
                               src["body_pose"].reshape(n, 21, 3)], axis=1)
    q = SB.quat_unroll(SB.matrix_to_quat(SB.axis_angle_to_matrix(local_aa)))
    transl = np.asarray(src["transl"], dtype=np.float64)
    if smooth:
        q = one_euro(q, fps, float(smooth["min_cutoff"]), float(smooth["beta"]))
        q /= np.linalg.norm(q, axis=-1, keepdims=True)
        transl = one_euro(transl[:, None, :], fps, float(smooth["min_cutoff"]) * 1.5,
                          float(smooth["beta"]) * 2.0)[:, 0]
    duration = (n - 1) / fps
    n_dst = int(round(duration * dst_fps)) + 1
    u = np.clip(np.arange(n_dst) / dst_fps * fps, 0, n - 1)
    qd = catmull_rom(q, u)
    qd /= np.linalg.norm(qd, axis=-1, keepdims=True)
    i0 = np.floor(u).astype(int)
    i1 = np.minimum(i0 + 1, n - 1)
    w = u - i0
    static = src["static_conf"]
    if np.isnan(static).any():
        static = np.zeros_like(static)
    static_d = static[i0] * (1 - w)[:, None] + static[i1] * w[:, None]
    return {"local": SB.quat_to_matrix(qd), "transl": catmull_rom(transl, u),
            "static": static_d, "src_frame": u + 1.0, "n": n_dst}


def static_camera(src: dict, rest_joints: np.ndarray, frames: slice):
    """The plate's (static) camera in GVHMR's world: camera->world rotation
    and position, fitted over `frames` (rotation: chordal L2 mean)."""
    Rw = SB.axis_angle_to_matrix(src["global_orient"])
    Rc = SB.axis_angle_to_matrix(src["incam_global_orient"])
    pw = rest_joints[0] + src["transl"]
    pc = rest_joints[0] + src["incam_transl"]
    Rc2w = Rw @ np.transpose(Rc, (0, 2, 1))
    U, _, Vt = np.linalg.svd(Rc2w[frames].sum(0))
    R = U @ Vt
    if np.linalg.det(R) < 0:
        U[:, -1] *= -1
        R = U @ Vt
    t = (pw[frames] - np.einsum("ij,tj->ti", R, pc[frames])).mean(0)
    return R, t


def ramp_weights(segs, T: int, ramp: int, select=None):
    """Per-frame weight and owning segment of contact segments: 1 inside,
    easing to 0 over `ramp` frames outside; the nearest segment wins."""
    w = np.zeros(T)
    owner = np.full(T, -1)
    for k, (a, b) in enumerate(segs):
        if select is not None and not select(k):
            continue
        for t in range(max(0, a - ramp), min(T, b + ramp + 1)):
            d = 0 if a <= t <= b else (a - t if t < a else t - b)
            wt = 1.0 if d == 0 else float(smootherstep(1.0 - d / (ramp + 1)))
            if wt > w[t]:
                w[t], owner[t] = wt, k
    return w, owner


def median_track(p: np.ndarray, conf: np.ndarray, min_conf: float, r: int = 2) -> np.ndarray:
    """Running median of a 2D keypoint track over its confident frames
    (window 2r+1), NaN elsewhere: a keypoint that jumps for a frame or two
    (MediaPipe swapping feet mid-turn, a detector glitch) does not become a
    fast foot."""
    n = len(p)
    out = np.full((n, 2), np.nan)
    good = conf >= min_conf
    for t in np.nonzero(good)[0]:
        a, b = max(0, t - r), min(n, t + r + 1)
        g = good[a:b]
        if g.sum() >= 2:
            out[t] = np.median(p[a:b][g], axis=0)
    return out


def still_in_image(src: dict, rs: dict, still_px: float, min_conf: float = 0.5):
    """(T, 2) bool per foot (L, R) at the destination clock: the foot does
    not move ON SCREEN. The camera is static, so a foot still in the image
    is still in the world — the most direct contact evidence there is, and
    independent of GVHMR's contact flag and of the 3D fit (both of which
    can move a foot the video shows planted). The median speed, in pixels
    per second, of ViTPose's ankle and — when detect_feet.py ran —
    MediaPipe's heel and toe tip, on median-filtered tracks.

    Tried and dropped: counting a foot as down when only its ball or heel
    is still (a pivot). It merged a kung-fu stance with the next one across
    a turn on the heel and cost leg accuracy, for no gain on the spin
    plate, whose pivots single_support() already covers."""
    fps = float(src["fps"])
    kp = np.asarray(src["kp2d"], dtype=np.float64)
    n = kp.shape[0]
    u = np.clip(np.round(rs["src_frame"] - 1.0).astype(int), 0, n - 1)
    out = np.zeros((rs["n"], 2), dtype=bool)

    def speed(x):
        v = np.full(n, np.nan)
        v[1:] = np.linalg.norm(np.diff(x, axis=0), axis=1) * fps
        return gaussian_smooth(np.where(np.isnan(v), 1e6, v), 1.0)

    for k in range(2):
        tracks = [kp[:, 15 + k]]
        if "feet2d" in src:
            f2 = np.asarray(src["feet2d"], dtype=np.float64)
            tracks += [f2[:, 2 * k], f2[:, 2 * k + 1]]
        speeds = [speed(median_track(t[:, :2], t[:, 2], min_conf)) for t in tracks]
        out[:, k] = np.median(np.stack(speeds, 1), axis=1)[u] < still_px
    return out


def floor_envelope(h: np.ndarray, fps: float, half_s: float = 1.5) -> np.ndarray:
    """The floor under a height track that drifts slowly: the lowest value
    within +-`half_s` seconds (a jump or a kick lasts less than that)."""
    w = max(1, int(round(half_s * fps)))
    return np.array([h[max(0, t - w):t + w + 1].min() for t in range(len(h))])


def single_support(local, transl, rj, foot, fps, opts, lift=None):
    """The foot that carries the body alone, and the joints to pin for it.

    One foot well above the other (a kick, a knee, a step) means the lower
    one is standing, whatever the network's contact flag says and however
    it slides in the world track: through the spin plate's kicks GVHMR
    slides the support foot at up to 2 m/s while the video shows it fixed,
    pivoting on the ball, and holds the take-off foot of the jump 10-13 cm
    up. Not when the lower foot is off the floor itself (a jump, 18 cm and
    up): the floor is the lower envelope of the lower foot's height, which
    follows the world track's slow vertical drift (`lift`, the camera's
    height correction, removes most of it). These contacts are inferred:
    they lock the foot, they do not set the floor's height.

    Returns (T, 4) pin weights in static_conf order (L ankle, L ball,
    R ankle, R ball) — the ball alone when the heel is up (the foot turns
    about it) — and (T, 2) bool per foot.
    """
    P, R = SB.fk_local(local, transl, rj)
    if lift is not None:
        P = P + np.asarray(lift, dtype=np.float64)[:, None, None] * np.array([0.0, 1.0, 0.0])
    T = P.shape[0]
    low, heel_h, ball_h = {}, {}, {}
    for side, (_, _, ank, ball) in LEGS.items():
        f = foot[side]

        def att(j, pt):
            return P[:, j] + np.einsum("tij,j->ti", R[:, j], np.asarray(pt, dtype=np.float64) - rj[j])
        heel_h[side] = att(ank, [rj[ank, 0], f["sole_y"], rj[ank, 2]])[:, 1]
        ball_h[side] = np.minimum(att(ank, [rj[ball, 0], f["sole_y"], rj[ball, 2]])[:, 1],
                                  att(ball, [rj[ball, 0], f["sole_y"], f["toe_tip_z"]])[:, 1])
        low[side] = np.minimum(heel_h[side], ball_h[side])
    floor = floor_envelope(np.minimum(low["L"], low["R"]), fps)
    swing, near = float(opts["contact_swing_height"]), float(opts["contact_support_height"])
    weights = np.zeros((T, 4))
    support = np.zeros((T, 2), dtype=bool)
    for k, (side, other) in enumerate((("L", "R"), ("R", "L"))):
        on = clean_mask((low[other] - low[side] > swing) & (low[side] - floor < near), 3, 2)
        support[:, k] = on
        heel_up = heel_h[side] - ball_h[side] > 0.02
        weights[:, 2 * k] = (on & ~heel_up).astype(float)
        weights[:, 2 * k + 1] = on.astype(float)
    return weights, support


def source_contacts(Ps, Rs, rj, foot, static, fps, opts, floor_known=False, still2d=None, support=None):
    """When is each foot planted? GVHMR's own contact detector, CHECKED
    against the geometry.

    `static_conf` is the network's probability that a joint is not moving.
    It is right most of the time and confidently wrong at two moments that
    matter: a foot that slides back under the body (kung-fu plate, end: the
    right foot travels 0.2 m while the network still calls it static — a
    lock there left the character in a frog stance for the whole T-pose),
    and a raised foot held still. So a point only counts as planted when
    the network says static AND it is slow AND its sole is within a few
    centimetres of the lowest sole of that frame.

    With `floor_known` (the source has been grounded, floor at y = 0) a
    second, purely geometric rule is OR-ed in: a sole within a few
    centimetres of the floor that is not moving is planted whatever the
    network says. On the spin plate the network's confidence stays under
    0.4 for 200 frames of turning footwork with the feet on the floor.

    A foot still on screen is planted when its sole is within
    `contact_still_height` of the floor (the estimate can leave a planted
    foot 6-9 cm up, the spin plate's landing), and a single-support foot
    (`single_support`) is planted, full stop.

    Returns per foot the heel and ball masks, plus the lowest source sole
    per frame (the grounding reference).
    """
    thr = float(opts["contact_threshold"])
    vmax = float(opts["contact_max_speed"])
    hmax = float(opts["contact_max_height"])

    def attached(j, point):
        return Ps[:, j] + np.einsum("tij,j->ti", Rs[:, j], np.asarray(point, dtype=np.float64) - rj[j])

    pts = {}
    for side, (hip, knee, ank, ball) in LEGS.items():
        f = foot[side]
        pts[side] = {"heel": attached(ank, [rj[ank, 0], f["sole_y"], rj[ank, 2]]),
                     "ball": attached(ank, [rj[ball, 0], f["sole_y"], rj[ball, 2]]),
                     "tip": attached(ball, [rj[ball, 0], f["sole_y"], f["toe_tip_z"]])}
    low = np.min(np.stack([p[:, 1] for side in pts for p in pts[side].values()], 1), 1)
    # heights above the floor for the image's evidence: the known floor, or
    # before it is known the lower envelope of the lowest sole (the height
    # track is camera-true, so it drifts little)
    floor = np.zeros(len(low)) if floor_known else floor_envelope(low, fps)

    def speed(x):
        v = np.zeros(len(x))
        v[1:] = np.linalg.norm(np.diff(x[:, [0, 2]], axis=0), axis=1) * fps
        v[0] = v[1] if len(v) > 1 else 0.0
        return gaussian_smooth(v, 1.0)

    out = {}
    for side in LEGS:
        ca, cb = STATIC[side]
        heel_h = pts[side]["heel"][:, 1] - low
        ball_h = np.minimum(pts[side]["ball"][:, 1], pts[side]["tip"][:, 1]) - low
        v_heel, v_ball = speed(pts[side]["heel"]), speed(pts[side]["ball"])
        # A heel can be perfectly still and still be UP (a boxer's rear foot
        # on its ball): only a heel near the floor makes a flat contact.
        flat_h = float(opts["contact_heel_height"])
        heel_c = (static[:, ca] > thr) & (v_heel < vmax) & (heel_h < flat_h)
        ball_c = (static[:, cb] > thr) & (v_ball < vmax) & (ball_h < hmax)
        # A foot the network holds still, and that is not moving, is planted
        # even when its sole is not flat on the floor (an extended leg's foot
        # rolled on an edge, a toe-up heel plant): it locks on its lowest point.
        still = ((np.maximum(static[:, ca], static[:, cb]) > 0.8)
                 & (np.minimum(v_heel, v_ball) < 0.5 * vmax)
                 & (np.minimum(heel_h, ball_h) < hmax))
        if still2d is not None:
            # still on screen and low: planted, whatever the 3D track says
            sole = np.minimum(pts[side]["heel"][:, 1], np.minimum(pts[side]["ball"][:, 1], pts[side]["tip"][:, 1]))
            still |= still2d[:, 0 if side == "L" else 1] & (sole - floor < float(opts["contact_still_height"]))
        if support is not None:
            still |= support[:, 0 if side == "L" else 1]
        if floor_known:
            g_h, g_v = float(opts["contact_geo_height"]), float(opts["contact_geo_speed"])
            heel_c |= (pts[side]["heel"][:, 1] < g_h) & (v_heel < g_v)
            ball_c |= (np.minimum(pts[side]["ball"][:, 1], pts[side]["tip"][:, 1]) < g_h) & (v_ball < g_v)
        heel_c, ball_c = clean_mask(heel_c, 3, 2), clean_mask(ball_c, 3, 2)
        out[side] = {"heel": heel_c, "ball": ball_c,
                     # flat = heel AND ball near the floor; otherwise a pivot
                     "flat": heel_c & (ball_h < flat_h + 0.02),
                     "heel_lower": heel_h <= ball_h,
                     "any": clean_mask(heel_c | ball_c | still, 3, 2)}
    return out, low


def camera_drift(src, rj, rs, R_c2w, t_c2w, Ry, opts):
    """Camera-derived pelvis minus GVHMR's world pelvis, per destination
    frame, in the normalised source frame (T, 3).

    GVHMR's world trajectory is integrated from predicted velocities and
    drifts — measured over whole takes that end where they began: 0.3 m in
    depth (fight), 0.4 m sideways (kung-fu), 0.07-0.12 m vertically (spin,
    combo). The camera-frame position is noisy frame to frame but does not
    drift: after this correction the feet of every plate come back to the
    height they started at within 1 cm, and the combo's jump is 0.22 m
    instead of the 0.10 m the world track reports.
    """
    n = src["transl"].shape[0]
    pc = rj[0] + gaussian_smooth(src["incam_transl"], float(opts["camera_smooth"]))
    u = rs["src_frame"] - 1.0
    i0 = np.clip(np.floor(u).astype(int), 0, n - 1)
    i1 = np.minimum(i0 + 1, n - 1)
    w = (u - i0)[:, None]
    pc = pc[i0] * (1 - w) + pc[i1] * w                     # camera-frame pelvis, destination clock
    pw = rj[0] + rs["transl"]                               # world pelvis (after pin_static_feet)
    d = np.einsum("ij,tj->ti", R_c2w, pc) + t_c2w - pw
    return np.einsum("ij,tj->ti", Ry, d)


def foot_tilt_bias(local, rj, static, max_deg: float = 12.0, min_frames: int = 10):
    """Per foot (L, R), the constant local rotation that lays GVHMR's foot
    flat when it stands still: its median tilt, pitch and roll, over the
    frames the network calls the whole foot static (ankle and ball > 0.8).

    GVHMR's feet stand 0-10 degrees toes-up and rolled a few degrees on the
    five plates, differently per foot but steadily through a take. A flat
    lock lays the foot flat anyway; the bias then shows twice: a
    4-frame flap as each lock ramps in and out, and a toes-up swing foot.
    Removing the take's own bias from every frame fixes both. Capped at
    `max_deg`; identity with too few static frames."""
    _, R = SB.fk_local(local, np.zeros((local.shape[0], 3)), rj)
    out = {}
    for side, (ank, (ca, cb)) in (("L", (7, STATIC["L"])), ("R", (8, STATIC["R"]))):
        still = (static[:, ca] > 0.8) & (static[:, cb] > 0.8)
        if still.sum() < min_frames:
            out[side] = np.eye(3)
            continue
        up_local = np.einsum("tji,j->ti", R[still, ank], [0.0, 1.0, 0.0])     # world up, in the foot's frame
        m = np.median(up_local, axis=0)
        m /= np.linalg.norm(m)
        C = SB.align_rotation(np.array([0.0, 1.0, 0.0]), m)
        ang = np.degrees(np.arccos(np.clip(m[1], -1.0, 1.0)))
        if ang > max_deg:                                  # keep the direction, cap the size
            C = SB.align_rotation(np.array([0.0, 1.0, 0.0]),
                                  np.array([0.0, 1.0, 0.0]) * np.cos(np.radians(max_deg))
                                  + (m - np.array([0.0, m[1], 0.0])) / max(np.linalg.norm(m - np.array([0.0, m[1], 0.0])), 1e-9)
                                  * np.sin(np.radians(max_deg)))
        out[side] = C
    return out


def image_anchor(src, side, frames_src, cam_pos, R_cam, K, ankle_y, min_conf=0.7, max_spread_px=12.0):
    """Where the video puts a planted foot: the camera ray through the
    detected ankle (ViTPose, median over the lock's frames), met by the
    horizontal plane at the performer's ankle height above the floor, in
    the rig's armature space. None when the ankle is not seen well or
    moves on screen (not a planted foot to anchor).

    Opt-in (`contact_image_anchor`): flat locks move across the line of
    sight to where the plate shows the ankle, capped, and only as far as
    the leg reaches without lowering the hips more than 1.5 cm. It puts
    planted feet exactly on the performer's in the overlay, but a rig with
    wider hips than the performer (the Y Bot) must then angle its legs
    away from the video's: leg angles came out 0.3 deg better on two plates
    and 0.2-0.6 deg worse on three."""
    kp = np.asarray(src["kp2d"], dtype=np.float64)
    n = kp.shape[0]
    fr = np.unique(np.clip(np.round(np.asarray(frames_src) - 1.0).astype(int), 0, n - 1))
    a = kp[fr, 15 if side == "L" else 16]
    a = a[a[:, 2] >= min_conf]
    if len(a) < 5:
        return None
    uv = np.median(a[:, :2], axis=0)
    if np.median(np.linalg.norm(a[:, :2] - uv, axis=1)) > max_spread_px:
        return None
    ray = R_cam @ np.array([(uv[0] - K[0, 2]) / K[0, 0], (uv[1] - K[1, 2]) / K[1, 1], 1.0])
    if ray[1] > -1e-6:                             # looking up: no floor down there
        return None
    t = (ankle_y - cam_pos[1]) / ray[1]
    return cam_pos + t * ray if t > 0 else None


def pin_static_feet(local, transl, rj, static):
    """Keep the feet GVHMR calls static still in the world, by moving the
    body instead (horizontal only; the floor solver owns height).

    GVHMR does this itself in its post-processing, but on ITS pose. The 2D
    refinement changes the legs frame by frame: a foot that stands still in
    the image then creeps in the world (measured: 12 cm over 0.8 s on the
    kung-fu plate). Re-applying the same rule on the refined pose restores
    it. Returns the corrected translation.
    """
    P, _ = SB.fk_local(local, transl, rj)
    joints = [7, 10, 8, 11]                                  # ankles and balls, static_conf order
    ok = np.minimum(static[1:, :4], static[:-1, :4]) > 0.5   # static in both frames
    d = P[1:, joints] - P[:-1, joints]                       # (T-1, 4, 3)
    d[:, :, 1] = 0.0
    # The anchor is the stillest flagged joint, not an average: a foot that
    # starts to lift while still flagged static would drag the other one.
    mag = np.where(ok, np.linalg.norm(d, axis=-1), np.inf)
    k = mag.argmin(1)
    step = np.where(np.isfinite(mag.min(1))[:, None], d[np.arange(len(d)), k], 0.0)
    return transl - np.concatenate([np.zeros((1, 3)), np.cumsum(step, 0)])


def bridge_contacts(contacts, Ps, max_gap: int = 12, max_move: float = 0.03):
    """Join two contact runs of the same foot when the foot did not move
    in between (a dip in the network's confidence, not a step).

    Left split, a planted foot is two footfalls: each takes its own camera
    correction, and the difference between them — 12 cm on the kung-fu
    plate — slides a foot that never moved.
    """
    ank = {"L": 7, "R": 8}
    for side, c in contacts.items():
        xz = Ps[:, ank[side]][:, [0, 2]]
        segs = runs(c["any"])
        for (a0, b0), (a1, b1) in zip(segs[:-1], segs[1:]):
            if a1 - b0 - 1 <= max_gap and np.linalg.norm(xz[a1] - xz[b0]) < max_move \
                    and np.ptp(xz[b0:a1 + 1], axis=0).max() < max_move:
                c["any"][b0 + 1:a1] = True
    return contacts


def camera_footfalls(d, contacts, opts):
    """Horizontal drift correction applied one FOOTFALL at a time.

    Correcting the root continuously would drag every planted foot with
    it (skate). Instead each contact segment takes the camera correction
    measured while it lasts as a CONSTANT offset: the foot lands where the
    camera saw it and stays there. The root follows the planted feet
    (weighted by contact); with no foot down it follows the camera itself.
    Drift is paid for in the length of steps, never in slides.

    Returns the root correction (T, 3) (vertical untouched) and the
    per-footfall offsets {side: [(3,), ...]}.
    """
    d = d.copy()
    d[:, 1] = 0.0
    T = d.shape[0]
    ramp = int(opts["contact_ramp"])
    num, den = np.zeros((T, 3)), np.zeros(T)
    deltas = {}
    for side, c in contacts.items():
        segs = runs(c["any"])
        # A foot is placed when it lands: the camera correction measured then
        # is the footfall's. Later disagreement while it stays planted is the
        # camera's depth estimate breathing with the pose (a deep stance
        # moved it 0.2 m on the kung-fu plate), not the foot moving.
        land = int(opts.get("camera_landing_frames", 6))
        deltas[side] = [np.median(d[a:min(b + 1, a + land)], 0) for a, b in segs]
        wt, owner = ramp_weights(segs, T, ramp)
        m = owner >= 0
        if m.any():
            num[m] += wt[m, None] * np.array(deltas[side])[owner[m]]
            den[m] += wt[m]
    planted = np.clip(den, 0.0, 1.0)[:, None]
    feet = np.where(den[:, None] > 1e-6, num / np.maximum(den[:, None], 1e-6), d)
    return gaussian_smooth(planted * feet + (1.0 - planted) * d, 1.5), deltas


def foot_yaw_offset(rig, rj, foot, side):
    """Toe-out difference between the performer's rest foot and the rig's.

    Measured on the foot a viewer sees — heel to toes on the SMPL-X mesh —
    not on the ankle->ball JOINT direction, which reads 18-22 degrees of
    toe-out on a template whose feet point 5-6 degrees out. Aligning on the
    joint direction turned every retargeted foot out by ~15 degrees.
    The rig's foot axis is its Foot->ToeBase->Toe_End bone line.
    """
    f = foot[side]
    ank, ball = (7, 10) if side == "L" else (8, 11)
    if "heel" in f:
        d_s = 0.5 * (np.asarray(f["big_toe"]) + np.asarray(f["small_toe"])) - np.asarray(f["heel"])
    else:                                            # smplx.npz from before the mesh points
        d_s = rj[ball] - rj[ank]
    pre = "Left" if side == "L" else "Right"
    b_foot, b_end = rig.bone(pre + "Foot"), rig.bone(pre + "Toe_End") or rig.bone(pre + "ToeBase")
    d_t = rig.head[b_end] - rig.head[b_foot]
    return float(np.arctan2(d_s[0], d_s[2]) - np.arctan2(d_t[0], d_t[2]))


def rotate_subtree(P, rig, bone, delta):
    """Pre-multiply world rotation `delta` (T,3,3) onto `bone` and every
    bone below it, so a corrected limb carries its hand and fingers."""
    todo = [bone]
    while todo:
        b = todo.pop()
        P[:, b] = delta @ P[:, b]
        todo += [i for i in range(len(rig.parent)) if rig.parent[i] == b]


def two_bone(root, mid, target, l1, l2, fallback_pole):
    """Vectorised two-bone solve. Returns (new mid, reached end); the bend
    stays in the plane the current `mid` defines."""
    d = target - root
    dist = np.linalg.norm(d, axis=-1)
    lo, hi = abs(l1 - l2) * 1.001 + 1e-6, (l1 + l2) * 0.9995
    dc = np.clip(dist, lo, hi)
    n = d / np.maximum(dist, 1e-9)[:, None]
    pole = (mid - root) - n * np.sum((mid - root) * n, -1, keepdims=True)
    pn = np.linalg.norm(pole, axis=-1, keepdims=True)
    fb = fallback_pole - n * np.sum(fallback_pole * n, -1, keepdims=True)
    pole = np.where(pn > 1e-6, pole, fb)
    pole /= np.maximum(np.linalg.norm(pole, axis=-1, keepdims=True), 1e-12)
    cos_a = np.clip((l1 * l1 + dc * dc - l2 * l2) / (2 * l1 * dc), -1, 1)
    sin_a = np.sqrt(1 - cos_a * cos_a)
    new_mid = root + l1 * (n * cos_a[:, None] + pole * sin_a[:, None])
    return new_mid, root + n * dc[:, None], int(np.count_nonzero(dist > hi))


def retarget(spec: dict, src: dict, rig: Rig, opts: dict) -> dict:
    dst_fps = float(spec.get("dst_fps", 30))
    rs = resample_source(src, dst_fps, opts.get("smooth"))
    T = rs["n"]
    rj = np.asarray(src["rest_joints"], dtype=np.float64)
    B = len(rig.names)

    # -- 1. source FK, heading + origin normalisation ------------------------
    n_src = src["global_orient"].shape[0]
    foot = src["foot"]
    # GVHMR's steady foot tilt, removed from every frame (feet stand flat)
    tilt = foot_tilt_bias(rs["local"], rj, rs["static"]) if opts.get("foot_tilt_fix", True) else {}
    for side, C in tilt.items():
        j = 7 if side == "L" else 8
        rs["local"][:, j] = rs["local"][:, j] @ C
    R_c2w, t_c2w = static_camera(src, rj, slice(0, max(2, min(n_src, int(opts["camera_fit_frames"])))))
    use_cam = bool(opts.get("camera_root", True))
    still2d = still_in_image(src, rs, float(opts["contact_still_px"]))
    # the camera's height correction does not depend on horizontal pinning
    lift = camera_drift(src, rj, rs, R_c2w, t_c2w, np.eye(3), opts)[:, 1] if use_cam else None
    sup_w, support = single_support(rs["local"], rs["transl"], rj, foot, dst_fps, opts, lift)
    if opts.get("pin_static_feet", True):
        pin_w = np.maximum(np.maximum(rs["static"][:, :4], np.repeat(still2d.astype(float), 2, axis=1)), sup_w)
        pinned = pin_static_feet(rs["local"], rs["transl"], rj, pin_w)
        # The correction switches anchor as the support passes from one foot
        # to the other; smoothed over a few frames (the correction, never the
        # motion), the body does not jolt at the hand-over (spin plate: hips
        # jerk doubled without it).
        corr = gaussian_smooth(pinned - rs["transl"], float(opts["pin_smooth"]))
        rs["transl"] = rs["transl"] + corr
    Ps, Rs = SB.fk_local(rs["local"], rs["transl"], rj)
    h0 = float(SB.heading(Rs[0, 0]))
    Ry = SB.yaw_matrix(np.array(-h0))
    origin = np.array([Ps[0, 0, 0], 0.0, Ps[0, 0, 2]])
    Ps = np.einsum("ij,tkj->tki", Ry, Ps - origin)
    Rs = np.einsum("ij,tkjl->tkil", Ry, Rs)
    src_floor_rest = min(foot["L"]["sole_y"], foot["R"]["sole_y"])
    static = rs["static"]

    # -- 2. camera-consistent trajectory, contacts, floor ---------------------
    d_cam = camera_drift(src, rj, rs, R_c2w, t_c2w, Ry, opts) if use_cam else np.zeros((T, 3))
    # Height: the camera sees it directly (image row x depth), so GVHMR's
    # vertical drift is removed continuously — it moves no foot sideways.
    Ps[:, :, 1] += d_cam[:, 1:2]
    # Floor: on every frame a foot is planted (network + geometry), the
    # lowest sole goes on the floor; flight keeps the (camera-true) arc.
    # Contacts that SET the floor's height are the observed ones only: a
    # single-support foot is inferred, and the estimate can hold it 10-13 cm
    # up (spin take-off) — grounding on it would lower the whole body (and
    # the jump that follows) below where the camera sees it. Its leg reaches
    # the floor instead (the lock, below).
    contacts, low_src = source_contacts(Ps, Rs, rj, foot, static, dst_fps, opts, still2d=still2d)
    # The body stands (it is not in the air) while its lower sole is within
    # `contact_support_height` of the floor under it — camera-true heights,
    # so that envelope drifts little. Used to set down feet hovering just
    # above the floor (the final leg solve), NOT to ground the body: putting
    # the lower foot on the floor on every such frame moved the body off the
    # plate's camera (spin jump 10 px low, feet pulled onto an estimate that
    # holds the wrong one down).
    on_floor = low_src - floor_envelope(low_src, dst_fps) < float(opts["contact_support_height"])
    grounded = contacts["L"]["any"] | contacts["R"]["any"]
    if grounded.sum() >= 2:
        idx = np.nonzero(grounded)[0]
        g = gaussian_smooth(np.interp(np.arange(T), idx, -low_src[idx]), float(opts["ground_sigma"]))
    else:
        g = np.full(T, -low_src.min())
    Ps[:, :, 1] += g[:, None]
    # Second contact pass against the now-known floor: a sole on the floor
    # that does not move is planted, whatever the network says.
    observed, _ = source_contacts(Ps, Rs, rj, foot, static, dst_fps, opts, floor_known=True, still2d=still2d)
    contacts, _ = source_contacts(Ps, Rs, rj, foot, static, dst_fps, opts, floor_known=True, still2d=still2d,
                                  support=support)
    contacts = bridge_contacts(contacts, Ps)
    grounded = contacts["L"]["any"] | contacts["R"]["any"]
    grounded_obs = grounded & ~(support.any(1) & ~(observed["L"]["any"] | observed["R"]["any"]))
    if use_cam:
        cam_corr, deltas = camera_footfalls(d_cam, contacts, opts)
    else:
        cam_corr = np.zeros((T, 3))
        deltas = {side: [np.zeros(3)] * len(runs(c["any"])) for side, c in contacts.items()}
    Ps = Ps + cam_corr[:, None, :]

    # -- 3. scale + rest alignment -----------------------------------------
    src_leg = float(np.mean([np.linalg.norm(rj[k] - rj[h]) + np.linalg.norm(rj[a] - rj[k])
                             for h, k, a, _ in LEGS.values()]))
    s = float(np.mean([rig.leg_length("Left"), rig.leg_length("Right")])) / src_leg
    align = np.tile(np.eye(3), (B, 1, 1))
    for j, child in ALIGN.items():
        b, bc = rig.joint_bone[j], rig.joint_bone[child]
        align[b] = SB.align_rotation(rig.head[bc] - rig.head[b], rj[child] - rj[j])
    foot_yaw = {side: foot_yaw_offset(rig, rj, src["foot"], side) for side in ("L", "R")}
    for j, side in FEET.items():
        align[rig.joint_bone[j]] = SB.yaw_matrix(np.array(foot_yaw[side]))
    for j, parent_j in INHERIT.items():
        align[rig.joint_bone[j]] = align[rig.joint_bone[parent_j]]

    # -- 4. world rotations of every bone ------------------------------------
    fists = fist_quats()
    src_frames = rs["src_frame"]
    fist_amt = np.zeros(T)
    if spec.get("fists"):
        fist_amt = window_amount(src_frames, spec["fists"]["rise_src"], spec["fists"]["fall_src"])
    hand_amt = np.maximum(float(opts["hand_relaxed"]), fist_amt)
    # fingers from the video where the hand detector saw them
    flex = hand_flexion(src, rs) if opts.get("hand_detect", True) else None
    seen = {}
    for side_, (fl, wt) in (flex or {}).items():
        for bone, q in finger_quats(fl, fists, side_).items():
            seen[bone] = (q, wt)
    P = np.empty((T, B, 3, 3))
    bone_joint = {b: j for j, b in rig.joint_bone.items()}
    for i in range(B):
        if i in bone_joint:
            P[:, i] = Rs[:, bone_joint[i]] @ align[i] @ rig.R[i]
        else:
            p = rig.parent[i]
            local = np.tile(np.eye(3), (T, 1, 1))
            short = rig.names[i][len(rig.prefix):]
            if short in fists:
                fq = np.asarray(fists[short], dtype=np.float64)
                qt = SB.slerp(np.tile([1.0, 0, 0, 0], (T, 1)), np.tile(fq / np.linalg.norm(fq), (T, 1)), hand_amt)
                if short in seen:
                    qt = SB.slerp(qt, seen[short][0], seen[short][1])
                local = SB.quat_to_matrix(qt)
            P[:, i] = (P[:, p] if p >= 0 else np.eye(3)) @ rig.rest_rel[i] @ local

    # -- 5. root: the performer's trajectory, scaled; rest maps to rest ------
    src_pelvis_h = rj[0, 1] - src_floor_rest
    tgt_hips_h = rig.head[rig.root][1] - rig.floor
    root = Ps[:, 0] * s
    root[:, 1] += rig.floor + tgt_hips_h - s * src_pelvis_h
    root[:, [0, 2]] += rig.head[rig.root][[0, 2]]
    pos = rig.chain_positions(P, root)

    # -- 6. legs: ankle where the performer's scaled leg puts it -------------
    ik_clamped = 0

    def solve_leg(side, ankle_target):
        """Two-bone solve of one leg onto `ankle_target`, knee kept in the
        plane the rotation copy chose. Thigh and shin are re-aimed with the
        minimal rotation; the foot keeps its absolute (world) rotation."""
        nonlocal ik_clamped
        hip_j, knee_j, ank_j, _ = LEGS[side]
        b_hip, b_knee, b_ank = rig.joint_bone[hip_j], rig.joint_bone[knee_j], rig.joint_bone[ank_j]
        l1 = float(np.linalg.norm(rig.head[b_knee] - rig.head[b_hip]))
        l2 = float(np.linalg.norm(rig.head[b_ank] - rig.head[b_knee]))
        cur = rig.chain_positions(P, root)
        hip_p, knee_p = cur[:, b_hip], cur[:, b_knee]
        fwd = np.einsum("tij,j->ti", P[:, rig.root] @ rig.R[rig.root].T, np.array([0, 0, 1.0]))
        knee_new, ank_new, n_cl = two_bone(hip_p, knee_p, ankle_target, l1, l2, fwd)
        ik_clamped += n_cl
        P[:, b_hip] = SB.align_rotation(knee_p - hip_p, knee_new - hip_p) @ P[:, b_hip]
        shin = np.einsum("tij,j->ti", P[:, b_knee] @ rig.R[b_knee].T, rig.head[b_ank] - rig.head[b_knee])
        P[:, b_knee] = SB.align_rotation(shin, ank_new - knee_new) @ P[:, b_knee]

    for side, (hip_j, knee_j, ank_j, _) in LEGS.items():
        b_hip = rig.joint_bone[hip_j]
        want = pos[:, b_hip] + (Ps[:, ank_j] - Ps[:, hip_j]) * s
        solve_leg(side, want)
    pos = rig.chain_positions(P, root)

    # -- 6b. authored arm overrides (spec `arm_overrides`) --------------------
    # For beats where a human's read of the video beats the estimator — an
    # arm hidden behind the torso comes back from any monocular estimator as
    # a guess. Same schema as the legacy lift: wrist/elbow targets in METRES
    # in the hip basis (x = character left, y = back, z = up), ramped. The
    # arm is re-solved with its own bone lengths and the hand rides along.
    for ov in spec.get("arm_overrides", []):
        ramp_src = ov.get("ramp_src", 6)
        amt = window_amount(src_frames, [ov["src"][0], ov["src"][0] + ramp_src],
                            [ov["src"][1] - ramp_src, ov["src"][1]])
        if not (amt > 1e-4).any():
            continue
        side = "Left" if ov["side"] == "left" else "Right"
        b_up, b_fore, b_hand = rig.bone(side + "Arm"), rig.bone(side + "ForeArm"), rig.bone(side + "Hand")
        cur = rig.chain_positions(P, root)
        Q = P[:, rig.root] @ rig.R[rig.root].T               # pelvis frame (x left, y up, z fwd)

        def hip_local(v):
            v = np.array([v[0], v[2], -v[1]], dtype=np.float64)   # (left, back, up) -> (left, up, fwd)
            return cur[:, rig.root] + np.einsum("tij,j->ti", Q, v)

        sh = cur[:, b_up]
        l1 = float(np.linalg.norm(rig.head[b_fore] - rig.head[b_up]))
        l2 = float(np.linalg.norm(rig.head[b_hand] - rig.head[b_fore]))
        e_t = sh + (hip_local(ov["elbow_local"]) - sh) / np.maximum(
            np.linalg.norm(hip_local(ov["elbow_local"]) - sh, axis=1, keepdims=True), 1e-9) * l1
        w_t = hip_local(ov["wrist_local"])
        elbow, wrist, _ = two_bone(sh, e_t, w_t, l1, l2, np.tile([0.0, -1.0, 0.0], (T, 1)))
        m = amt > 1e-4
        for b, a_pos, want in ((b_up, sh, elbow), (b_fore, None, wrist)):
            cur = rig.chain_positions(P, root)
            start = cur[:, b]
            child = b_fore if b == b_up else b_hand
            have = cur[:, child] - start
            full = SB.align_rotation(have, want - start)
            q = SB.slerp(np.tile([1.0, 0, 0, 0], (T, 1)), SB.matrix_to_quat(full), amt)
            delta = SB.quat_to_matrix(q)
            delta[~m] = np.eye(3)
            rotate_subtree(P, rig, b, delta)
    pos = rig.chain_positions(P, root)

    # -- 7. target grounding + contact locks ---------------------------------
    tgt_probes = {}
    for side, (hip_j, knee_j, ank_j, ball_j) in LEGS.items():
        b_ank, b_ball = rig.joint_bone[ank_j], rig.joint_bone[ball_j]
        toe_end = rig.bone(("Left" if side == "L" else "Right") + "Toe_End")
        ha, hb = rig.head[b_ank], rig.head[b_ball]
        ht = rig.head[toe_end] if toe_end is not None else hb + (hb - ha) * 0.6
        tgt_probes[side] = [(b_ank, [ha[0], rig.floor, ha[2]]),
                            (b_ank, [hb[0], rig.floor, hb[2]]),
                            (b_ball, [ht[0], rig.floor, ht[2]])]

    def lowest(pos_):
        ys = [rig.attached(P, pos_, b, p)[:, 1] for side in tgt_probes for b, p in tgt_probes[side]]
        return np.min(np.stack(ys, 1), 1)

    low = lowest(pos)
    if grounded_obs.sum() >= 2:
        idx = np.nonzero(grounded_obs)[0]
        pull = gaussian_smooth(np.interp(np.arange(T), idx, -low[idx]), 2.0)
    else:
        pull = np.full(T, -low.min())
    root[:, 1] += pull
    pos = rig.chain_positions(P, root)
    # The plate's camera in the rig's (scaled, grounded) armature space:
    # static, placed where frame 1 puts the body relative to it.
    C = Ry @ (t_c2w - origin) + cam_corr[0]
    C[1] += g[0] + d_cam[0, 1]
    R_cam = Ry @ R_c2w                              # OpenCV camera axes -> normalised source world
    cam_pos_arm = C * s
    cam_pos_arm[1] += rig.floor + tgt_hips_h - s * src_pelvis_h + pull[0]
    cam_pos_arm[[0, 2]] += rig.head[rig.root][[0, 2]]
    K_src = np.asarray(src["K"], dtype=np.float64)

    ramp = int(opts["contact_ramp"])
    contact_report = {}
    contact_masks = np.zeros((T, 2), dtype=bool)
    feet = {}
    for k, (side, (hip_j, knee_j, ank_j, ball_j)) in enumerate(LEGS.items()):
        b_ank, b_ball = rig.joint_bone[ank_j], rig.joint_bone[ball_j]
        flat_c, c = contacts[side]["flat"], contacts[side]["any"]
        segs0 = runs(c)
        flat0 = [flat_c[a_:b_ + 1].mean() >= 0.5 for a_, b_ in segs0]
        # a pivot locks whichever point carries the weight: the heel for a
        # toe-up plant, the ball for a raised heel
        heel0 = [(not fl) and contacts[side]["heel_lower"][a_:b_ + 1].mean() > 0.5
                 for (a_, b_), fl in zip(segs0, flat0)]
        # A toe-up heel plant is a moment: a heel strike, a turn on the heel.
        # Held longer, it is the estimator's reading, not the performer's —
        # GVHMR returns the planted rear foot of a long stance with its toes
        # 20-30 degrees up and rolled on its edge (kung-fu bow stance, 4 s)
        # where the video shows it flat. The image cannot tell the two apart
        # (the difference is in depth); weight-bearing can: lay it flat.
        long_heel = int(round(float(opts["contact_heel_max_s"]) * dst_fps))
        laid_flat = 0
        for k0, (a_, b_) in enumerate(segs0):
            if heel0[k0] and b_ - a_ + 1 > long_heel:
                flat0[k0], heel0[k0] = True, False
                laid_flat += 1
        # A lock holds one point still for the whole segment, so a segment
        # whose planted point creeps in the source (a foot slid across the
        # floor, slow enough to pass as "static") is cut where the drift
        # exceeds `contact_max_drift`: each piece locks where the foot is.
        f_src = src["foot"][side]
        refs = {"ankle": Ps[:, ank_j],
                "heel": Ps[:, ank_j] + np.einsum("tij,j->ti", Rs[:, ank_j],
                                                 np.array([rj[ank_j, 0], f_src["sole_y"], rj[ank_j, 2]]) - rj[ank_j]),
                "ball": Ps[:, ank_j] + np.einsum("tij,j->ti", Rs[:, ank_j],
                                                 np.array([rj[ball_j, 0], f_src["sole_y"], rj[ball_j, 2]]) - rj[ank_j])}
        max_drift = float(opts["contact_max_drift"])
        slide_v = float(opts["contact_slide_speed"])
        # the heading of the source foot laid flat (a foot rolled onto its
        # edge keeps the direction it points in; its tilted axis does not)
        R_lay = SB.align_rotation(np.einsum("tij,j->ti", Rs[:, ank_j], [0.0, 1.0, 0.0]),
                                  np.tile([0.0, 1.0, 0.0], (T, 1))) @ Rs[:, ank_j]
        lay_yaw = np.unwrap(SB.heading(R_lay))
        yaw_s = gaussian_smooth(lay_yaw, 2.0)
        max_turn = np.radians(float(opts["contact_max_turn_deg"]))
        segs, flat, on_heel, parent = [], [], [], []
        for k0, ((a_, b_), fl, oh) in enumerate(zip(segs0, flat0, heel0)):
            ref = refs["ankle" if fl else "heel" if oh else "ball"][:, [0, 2]]
            # speed over a third of a second: frame-to-frame jitter of a
            # planted foot averages out, a real slide does not
            i_lo = np.maximum(np.arange(T) - 5, 0)
            i_hi = np.minimum(np.arange(T) + 5, T - 1)
            v = np.linalg.norm(ref[i_hi] - ref[i_lo], axis=1) / np.maximum(i_hi - i_lo, 1) * dst_fps
            # frames where the planted point slides are left free: the foot
            # follows the source there (smoothly), and only the still runs
            # lock. A foot the IMAGE shows still counts as still even when
            # the 3D track creeps.
            steady = (v < slide_v) | still2d[:, k] | support[:, k]
            for a2, b2 in runs(steady[a_:b_ + 1]):
                a2, b2 = a2 + a_, b2 + a_
                start, t = a2, a2
                while t <= b2:
                    drifted = (np.linalg.norm(ref[t] - ref[start]) > max_drift
                               and not (still2d[t, k] or support[t, k]))
                    # A flat foot that turns (a stance pivoting on its heel
                    # or ball: the ankle barely moves) gets a new heading
                    # for each piece, instead of one heading for both stances.
                    turned = fl and abs(yaw_s[t] - yaw_s[start]) > max_turn
                    if drifted or turned:
                        if t - 1 - start >= 2:
                            segs.append((start, t - 1)); flat.append(fl); on_heel.append(oh); parent.append(k0)
                        start = t
                    t += 1
                if b2 - start >= 2:
                    segs.append((start, b2)); flat.append(fl); on_heel.append(oh); parent.append(k0)
        c = np.zeros(T, dtype=bool)
        for a_, b_ in segs:
            c[a_:b_ + 1] = True
        contact_masks[:, k] = c
        # A planted foot is flat on the floor at a FIXED heading: the rig
        # rest foot, turned to the heading of the source foot laid flat (a
        # foot rolled onto its edge keeps the direction it points in; the
        # heading of its tilted axis does not), median over the stance.
        ha, hb = rig.head[b_ank], rig.head[b_ball]
        yaw_align = foot_yaw[side]
        src_yaw = np.unwrap(SB.heading(Rs[:, ank_j]))
        seg_yaw = [float(np.median(lay_yaw[a_:b_ + 1])) for a_, b_ in segs]
        flat_w, flat_owner = ramp_weights(segs, T, ramp, lambda k_: flat[k_])
        toe_w, _ = ramp_weights(segs, T, ramp, lambda k_: not on_heel[k_])
        if segs:
            yaw = np.where(flat_owner >= 0, np.array(seg_yaw + [0.0])[flat_owner], src_yaw)
            flatA = SB.yaw_matrix(yaw + yaw_align) @ rig.R[b_ank]
            P[:, b_ank] = SB.quat_to_matrix(SB.slerp(SB.matrix_to_quat(P[:, b_ank]),
                                                     SB.matrix_to_quat(flatA), flat_w))
            # toes lie on the floor in any contact, along the foot heading
            foot_heading = SB.heading(P[:, b_ank] @ rig.R[b_ank].T)
            flatB = SB.yaw_matrix(foot_heading) @ rig.R[b_ball]
            P[:, b_ball] = SB.quat_to_matrix(SB.slerp(SB.matrix_to_quat(P[:, b_ball]),
                                                      SB.matrix_to_quat(flatB), toe_w))
        # Each footfall sits where the camera saw it: its own correction
        # minus what the root already carries at that frame.
        resid = [s * (np.asarray(deltas[side][parent[k_]])[None, :] - cam_corr[a_:b_ + 1])
                 for k_, (a_, b_) in enumerate(segs)]
        b_hip_, b_knee_ = rig.joint_bone[hip_j], rig.joint_bone[knee_j]
        feet[side] = dict(b_ank=b_ank, b_ball=b_ball, b_hip=b_hip_, b_knee=b_knee_,
                          leg_len=float(np.linalg.norm(rig.head[b_knee_] - rig.head[b_hip_])
                                        + np.linalg.norm(rig.head[b_ank] - rig.head[b_knee_])),
                          segs=segs, flat=flat, on_heel=on_heel,
                          ha=ha, hb=hb, w=ramp_weights(segs, T, ramp), resid=resid)
        contact_report[side] = {"segments": len(segs), "flat_segments": int(sum(flat)),
                                "heel_pivots": int(sum(on_heel)),
                                "long_heel_plants_laid_flat": laid_flat,
                                "detail": [[int(a_), int(b_), "flat" if fl else "heel" if oh else "ball"]
                                           for (a_, b_), fl, oh in zip(segs, flat, on_heel)],
                                "planted_fraction": round(float(c.mean()), 3)}

    # Lock positions, from the grounded free pose: the ankle of a flat foot
    # (at rest ankle height: sole on the floor), the ball of a pivoting one.
    pos = rig.chain_positions(P, root)
    for side, f in feet.items():
        ankle_free = pos[:, f["b_ank"]]
        ball_free = rig.attached(P, pos, f["b_ank"], [f["hb"][0], rig.floor, f["hb"][2]])
        f["locks"] = []
        heel_free = rig.attached(P, pos, f["b_ank"], [f["ha"][0], rig.floor, f["ha"][2]])
        f["anchor_cm"] = []
        for (a_, b_), is_flat, on_heel_, r_ in zip(f["segs"], f["flat"], f["on_heel"], f["resid"]):
            ref = (ankle_free if is_flat else heel_free if on_heel_ else ball_free)[a_:b_ + 1]
            lk = np.median(ref + r_, 0)
            lk[1] = f["ha"][1] if is_flat else rig.floor
            if is_flat and opts.get("contact_image_anchor", False):
                d = image_anchor(src, side, src_frames[a_:b_ + 1], cam_pos_arm, R_cam, K_src,
                                 rig.floor + s * src["foot"][side]["ankle_h"])
                if d is not None:
                    shift = d[[0, 2]] - lk[[0, 2]]
                    # only across the line of sight: along it, the ray meets
                    # the floor at a grazing 12-19 degrees, and 1 cm of error
                    # in the ankle's height is 3-4 cm of depth
                    view = (d - cam_pos_arm)[[0, 2]]
                    view /= max(float(np.linalg.norm(view)), 1e-9)
                    shift = shift - np.dot(shift, view) * view
                    n = float(np.linalg.norm(shift))
                    cap = float(opts["contact_anchor_max"])
                    if n > cap:
                        shift *= cap / n
                    # ...and only as far as the leg reaches without lowering
                    # the hips more than 1.5 cm: the rear leg of a kung-fu bow
                    # stance is straight, and a foot set 5 cm wider there
                    # dropped the hips to their cap and bent both legs
                    hips = pos[a_:b_ + 1, f["b_hip"]]
                    L2 = (0.998 * f["leg_len"]) ** 2

                    def hips_drop(target):
                        v = target[None] - hips
                        h2 = v[:, 0] ** 2 + v[:, 2] ** 2
                        return float(np.max(np.maximum(-v[:, 1] - np.sqrt(np.maximum(L2 - h2, 0.0)), 0.0)))
                    base = hips_drop(lk)
                    allowed = min(base + float(opts["contact_anchor_drop"]),
                                  max(base, float(opts["max_hips_drop"]) - 0.01))
                    for alpha in (1.0, 0.75, 0.5, 0.25, 0.0):
                        trial = lk.copy()
                        trial[[0, 2]] += alpha * shift
                        if hips_drop(trial) <= allowed:
                            break
                    shift = alpha * shift
                    lk[[0, 2]] += shift
                    f["anchor_cm"].append(round(float(np.linalg.norm(shift)) * 100, 1))
            f["locks"].append(lk)

    def ankle_targets(pos_):
        """Free ankles move with the body; locked ones stay put in the world."""
        out = {}
        for side, f in feet.items():
            ankle_free = pos_[:, f["b_ank"]]
            if not f["segs"]:
                out[side] = ankle_free.copy()
                continue
            ball_free = rig.attached(P, pos_, f["b_ank"], [f["hb"][0], rig.floor, f["hb"][2]])
            heel_free = rig.attached(P, pos_, f["b_ank"], [f["ha"][0], rig.floor, f["ha"][2]])
            w, owner = f["w"]
            off = np.zeros((T, 3))
            for t in np.nonzero(owner >= 0)[0]:
                k_ = owner[t]
                a_, b_ = f["segs"][k_]
                tt = min(max(t, a_), b_)                 # ramps hold the boundary offset
                ref = (ankle_free if f["flat"][k_] else heel_free if f["on_heel"][k_] else ball_free)[tt]
                off[t] = (f["locks"][k_] - ref) * w[t]
            out[side] = ankle_free + off
        return out

    # A locked foot the leg cannot reach means the pelvis is too high for
    # that stance: lower it (smoothly) instead of letting the foot float.
    tg = ankle_targets(pos)
    drop = np.zeros(T)
    for side, f in feet.items():
        l1 = float(np.linalg.norm(rig.head[f["b_knee"]] - rig.head[f["b_hip"]]))
        l2 = float(np.linalg.norm(rig.head[f["b_ank"]] - rig.head[f["b_knee"]]))
        L = (l1 + l2) * 0.998
        v = tg[side] - pos[:, f["b_hip"]]
        h2 = v[:, 0] ** 2 + v[:, 2] ** 2
        need = -v[:, 1] - np.sqrt(np.maximum(L * L - h2, 0.0))
        drop = np.maximum(drop, np.where(np.linalg.norm(v, axis=1) > L, np.maximum(need, 0.0), 0.0))
    if drop.any():
        # capped: a lock that would need more is a lock in the wrong place,
        # and a sinking character is worse than a foot short of it
        wide = np.array([drop[max(0, t - 3):t + 4].max() for t in range(T)])
        drop = gaussian_smooth(np.minimum(wide, float(opts["max_hips_drop"])), 1.5)
        root[:, 1] -= drop
        pos = rig.chain_positions(P, root)
    tg = ankle_targets(pos)
    # A free foot the estimate carries under the floor (turning footwork
    # on the spin plate, 4 cm) is lifted by its own leg, not by the body:
    # raising the hips for it would float the other foot.
    for side, f in feet.items():
        ys = np.min(np.stack([rig.attached(P, pos, b, p)[:, 1] for b, p in tgt_probes[side]], 1), 1)
        sink = np.maximum(rig.floor - ys - float(opts["sink_tolerance"]), 0.0) * (1.0 - f["w"][0])
        if sink.any():
            sink = gaussian_smooth(np.array([sink[max(0, t - 2):t + 3].max() for t in range(T)]), 1.0)
            tg[side] = tg[side] + sink[:, None] * np.array([0.0, 1.0, 0.0])
        # A free foot hovering just above the floor while the body stands
        # (turning footwork: the estimate lifts one foot 3-6 cm the video
        # shows down) is set on it by its own leg — height only, it is not
        # locked anywhere. Fades out between 2 and 6 cm, so a foot lifting
        # off is not held.
        if opts.get("floor_snap", True):
            gap = ys - rig.floor
            wsnap = np.clip((float(opts["floor_snap_height"]) - gap) / (float(opts["floor_snap_height"]) - 0.02), 0.0, 1.0)
            wsnap = gaussian_smooth(wsnap * (gap > 0) * on_floor * (1.0 - f["w"][0]), 1.0)
            tg[side] = tg[side] - (gap * wsnap)[:, None] * np.array([0.0, 1.0, 0.0])
    for side in feet:
        solve_leg(side, tg[side])
    pos = rig.chain_positions(P, root)
    reach_err = {side: round(float(np.linalg.norm(pos[:, f["b_ank"]] - tg[side], axis=1).max() * 100), 2)
                 for side, f in feet.items()}

    # -- 8. rest blends (T-pose bookends lock to the exact rig rest) ---------
    rest_w = np.zeros(T)
    if spec.get("rest_blend_end"):
        rb = spec["rest_blend_end"]
        rest_w = np.maximum(rest_w, smootherstep((src_frames - rb["start_src"]) / max(1e-6, rb["full_src"] - rb["start_src"])))
    if spec.get("rest_blend_start"):
        rb = spec["rest_blend_start"]
        rest_w = np.maximum(rest_w, 1.0 - smootherstep((src_frames - rb["full_src"]) / max(1e-6, rb["release_src"] - rb["full_src"])))

    # -- 9. world rotations -> local pose quaternions -------------------------
    basis = np.empty((T, B, 3, 3))
    for i in range(B):
        p = rig.parent[i]
        parent = P[:, p] if p >= 0 else np.eye(3)
        basis[:, i] = np.swapaxes(parent @ rig.rest_rel[i], -1, -2) @ P[:, i]
    quat = SB.matrix_to_quat(basis)                                        # (T, B, 4)
    hips_loc = np.einsum("ji,tj->ti", rig.R[rig.root], root - rig.head[rig.root]) * rig.unit
    if rest_w.any():
        # Upper body only: the arms, spine and head settle into the rig's
        # exact T-pose, while the legs and root keep the performer's real
        # stance. Forcing the legs to rest would drag planted feet across
        # the floor during the blend.
        spine = rig.bone("Spine")
        upper = np.zeros(B, dtype=bool)
        for i in range(B):
            k_ = i
            while k_ >= 0 and not upper[i]:
                upper[i] = k_ == spine
                k_ = rig.parent[k_]
        n_up = int(upper.sum())
        ident = np.tile([1.0, 0.0, 0.0, 0.0], (T, n_up, 1))
        quat[:, upper] = SB.slerp(quat[:, upper], ident, np.repeat(rest_w[:, None], n_up, 1))
    quat = SB.quat_unroll(quat)

    # final pose for QA / framing (recomputed from what will be keyed)
    Pq = np.empty((T, B, 3, 3))
    Lq = SB.quat_to_matrix(quat)
    for i in range(B):
        p = rig.parent[i]
        Pq[:, i] = (Pq[:, p] if p >= 0 else np.eye(3)) @ rig.rest_rel[i] @ Lq[:, i]
    root_final = rig.head[rig.root] + np.einsum("ij,tj->ti", rig.R[rig.root], hips_loc / rig.unit)
    pos_final = rig.chain_positions(Pq, root_final)

    def to_world(p_arm_m):   # armature space (m) -> Blender world (m)
        return np.einsum("ij,...j->...i", rig.arm_rot, p_arm_m) + rig.arm_loc

    joints_world = to_world(pos_final)

    # -- 10. the plate's camera, in the same (scaled, grounded) world --------
    # (placed in section 7; the camera stays where it was at frame 1)
    R_bl = rig.arm_rot @ R_cam @ np.diag([1.0, -1.0, -1.0])   # Blender camera looks down -Z, +Y up
    cam_mw = np.eye(4)
    cam_mw[:3, :3] = R_bl
    cam_mw[:3, 3] = to_world(cam_pos_arm)

    # -- 11. QA numbers ---------------------------------------------------------
    qa = {"scale": round(s, 4), "frames": T, "ik_clamped_frames": ik_clamped,
          "single_support_frames": {"L": int(support[:, 0].sum()), "R": int(support[:, 1].sum())},
          "image_anchor_cm": {side: f.get("anchor_cm", []) for side, f in feet.items()},
          "foot_tilt_fix_deg": {side: round(float(np.degrees(np.arccos(np.clip((np.trace(C) - 1) / 2, -1, 1)))), 1)
                                for side, C in tilt.items()},
          "contact_reach_error_cm": reach_err, "hips_drop_cm": round(float(drop.max() * 100), 2),
          "ground_drift_m": [round(float(g.min()), 3), round(float(g.max()), 3)],
          "target_pull_m": [round(float(pull.min()), 3), round(float(pull.max()), 3)],
          "grounded_fraction": round(float(grounded.mean()), 3), "contacts": contact_report,
          "heading0_deg": round(float(np.degrees(h0)), 2),
          "camera_root": {"footfalls": int(sum(len(v) for v in deltas.values())),
                          "end_correction_m": [round(float(v), 3) for v in cam_corr[-1]],
                          "max_correction_m": round(float(np.abs(cam_corr).max()), 3),
                          "max_footfall_offset_m": round(float(max(
                              [np.abs(r_).max() / s for f in feet.values() for r_ in f["resid"]] or [0.0])), 3)}}
    feet_w = {side: joints_world[:, rig.joint_bone[LEGS[side][2]]] for side in LEGS}
    skate = {}
    for k, side in enumerate(LEGS):
        m = contact_masks[:, k]
        v = np.linalg.norm(np.diff(feet_w[side][:, :2], axis=0), axis=1) * dst_fps
        mm = m[1:] & m[:-1]
        skate[side] = round(float(np.median(v[mm]) * 100), 2) if mm.any() else None
    qa["contact_skate_cm_s"] = skate
    qa["travel_m"] = round(float(np.linalg.norm(joints_world[-1, rig.root, :2] - joints_world[0, rig.root, :2])), 3)

    return {
        "bone_names": np.array(rig.names), "quat": quat.astype(np.float64),
        "hips_location": hips_loc, "fps": np.array(dst_fps), "root": np.array(rig.names[rig.root]),
        "action_name": np.array(spec.get("action_name", "SMPLX_Retarget")),
        "hips_world": joints_world[:, rig.root], "joints_world": joints_world,
        "contacts": contact_masks, "rest_amount": rest_w, "fist_amount": fist_amt,
        "src_frame": src_frames,
        "cam_K": np.asarray(src["K"], dtype=np.float64),
        "cam_wh": np.array([int(src["width"]), int(src["height"])]),
        "cam_matrix_world": cam_mw,
        "qa": np.array(json.dumps(qa)),
        "_face_dir": to_world(np.einsum("tij,j->ti", Pq[:, rig.bone("Head")] @ rig.R[rig.bone("Head")].T, [0, 0, 1.0])) - rig.arm_loc,
        "_body_fwd": to_world(np.einsum("tij,j->ti", Pq[:, rig.root] @ rig.R[rig.root].T, [0, 0, 1.0])) - rig.arm_loc,
    }


def write_curves(out: dict, path: Path) -> None:
    """curves.json in apply_mixamo_fk.dump_curves' format, so the legacy
    QA / compare tools read an SMPL-X clip without Blender."""
    names = [str(n) for n in out["bone_names"]]
    root = str(out["root"])
    frames = []
    for t in range(out["quat"].shape[0]):
        bones = {}
        for b, n in enumerate(names):
            loc = out["hips_location"][t] if n == root else (0.0, 0.0, 0.0)
            bones[n] = {"location": [round(float(v), 6) for v in loc],
                        "rotation_quaternion": [round(float(v), 6) for v in out["quat"][t, b]],
                        "world_location": [round(float(v), 6) for v in out["joints_world"][t, b]]}
        fd, bf = out["_face_dir"][t], out["_body_fwd"][t]
        frames.append({"frame": t + 1, "bones": bones,
                       "face_dir": [round(float(v), 5) for v in fd / np.linalg.norm(fd)],
                       "body_forward": [round(float(v), 5) for v in bf / np.linalg.norm(bf)]})
    path.write_text(json.dumps({"frames": frames}), encoding="utf-8")


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--spec", required=True)
    ap.add_argument("--smplx", help="SMPL-X parameters (default: spec 'smplx', else smplx.npz next to the landmarks)")
    ap.add_argument("--rig-profile", help="override the spec's rig_profile")
    ap.add_argument("--out-dir", help="override the spec's clip_dir")
    ap.add_argument("--no-curves", action="store_true", help="skip curves.json")
    args = ap.parse_args()

    spec = json.loads(rpath(args.spec).read_text(encoding="utf-8"))
    smplx_path = rpath(args.smplx or spec.get("smplx") or (rpath(spec["landmarks"]).parent / "smplx.npz"))
    if not smplx_path.exists():
        raise SystemExit(f"{smplx_path} not found — run estimate_pose_gvhmr.py on the plate first")
    prof_path = rpath(args.rig_profile or spec.get("rig_profile") or "rig_profile.json")
    rig = Rig(json.loads(prof_path.read_text(encoding="utf-8")))
    opts = {"contact_threshold": 0.5, "contact_max_speed": 0.3, "contact_max_height": 0.06,
            "contact_geo_height": 0.025, "contact_geo_speed": 0.2, "contact_heel_height": 0.03,
            "contact_max_drift": 0.04, "contact_slide_speed": 0.12, "contact_still_px": 15.0,
            "contact_still_height": 0.12, "contact_swing_height": 0.15, "contact_support_height": 0.14,
            "contact_max_turn_deg": 20.0, "floor_snap_height": 0.06, "pin_smooth": 1.0, "sink_tolerance": 0.01, "contact_anchor_max": 0.12, "contact_anchor_drop": 0.015,
            "max_hips_drop": 0.05,
            "contact_heel_max_s": 0.5, "ground_sigma": 3.0, "contact_ramp": 4, "hand_relaxed": 0.3,
            "camera_fit_frames": 12, "camera_root": True, "camera_smooth": 5.0}
    opts.update(spec.get("smplx_retarget", {}))

    src = load_source(smplx_path)
    out = retarget(spec, src, rig, opts)
    clip_dir = rpath(args.out_dir or spec["clip_dir"])
    clip_dir.mkdir(parents=True, exist_ok=True)
    np.savez_compressed(clip_dir / "motion.npz", **{k: v for k, v in out.items() if not k.startswith("_")})
    if not args.no_curves:
        write_curves(out, clip_dir / "curves.json")
    qa = json.loads(str(out["qa"]))
    print(f"wrote {clip_dir / 'motion.npz'}  ({qa['frames']} frames @ {float(out['fps']):.0f} fps, "
          f"rig {prof_path.name}, scale {qa['scale']})")
    print(json.dumps(qa, indent=1))


if __name__ == "__main__":
    main()
