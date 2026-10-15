"""How closely does a retargeted clip follow the VIDEO — measured, not eyeballed.

  python pipeline/eval_fidelity.py --spec action_specs/<motion>.json \
         --clip clips/<new_clip> [--vs clips/<legacy_clip>] [--out report.json]

The retarget is projected into the plate through the plate's own camera
(recovered by retarget_smplx.py, stored in motion.npz) and compared with
the 2D keypoints ViTPose detected on the video frames (smplx.npz `kp2d`).
That evidence is independent of any 3D retarget.

Measured per frame, per body segment (upper arms, forearms, thighs, shins,
shoulder line, hip line, torso, head):
  the ANGLE between the segment on screen in the retarget and in the
  video. Angles, not pixel distances: a Mixamo character is not the
  performer's size or shape, so joint positions can never coincide, but a
  forearm that points where the performer's forearm points reads right.
  Foreshortened segments (short on screen) and low-confidence keypoints
  are down-weighted/excluded — their 2D direction is noise.

Also reported: foot skate while planted (frames GVHMR's own contact
detector flags, not either retarget's locks), how far planted soles sit
off the floor, and jitter (mean joint jerk). A legacy clip (`--vs`, curves.json from apply_mixamo_fk) is
measured the same way. Legacy clips are in-place, so they are evaluated
with the new clip's ground trajectory added, which isolates the POSE.
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path

import numpy as np

REPO = Path(__file__).resolve().parents[1]

# COCO-17 (ViTPose) index -> Mixamo bone whose head is that joint
COCO = {5: "LeftArm", 6: "RightArm", 7: "LeftForeArm", 8: "RightForeArm",
        9: "LeftHand", 10: "RightHand", 11: "LeftUpLeg", 12: "RightUpLeg",
        13: "LeftLeg", 14: "RightLeg", 15: "LeftFoot", 16: "RightFoot"}
SEGMENTS = {
    "L upper arm": (5, 7), "R upper arm": (6, 8), "L forearm": (7, 9), "R forearm": (8, 10),
    "L thigh": (11, 13), "R thigh": (12, 14), "L shin": (13, 15), "R shin": (14, 16),
    "shoulders": (5, 6), "hips": (11, 12), "torso": ("mid_hip", "mid_sh"), "head": ("mid_sh", 0),
}
GROUPS = {"arms": ["L upper arm", "R upper arm", "L forearm", "R forearm"],
          "legs": ["L thigh", "R thigh", "L shin", "R shin"],
          "torso": ["shoulders", "hips", "torso"], "head": ["head"]}


def rpath(p) -> Path:
    p = Path(p)
    return p if p.is_absolute() else (REPO / p)


def load_clip(clip_dir: Path):
    """World joint positions (T, B, 3) + bone names, from motion.npz or curves.json."""
    m = clip_dir / "motion.npz"
    if m.exists():
        z = np.load(m)
        return [str(n) for n in z["bone_names"]], z["joints_world"], z
    frames = json.loads((clip_dir / "curves.json").read_text(encoding="utf-8"))["frames"]
    names = list(frames[0]["bones"].keys())
    J = np.array([[fr["bones"][n]["world_location"] for n in names] for fr in frames])
    return names, J, None


def face_point(names, J, prefix):
    """A nose proxy: in front of and above the skull-base Head joint, along
    the head's own frame (from Head and HeadTop_End positions)."""
    h = J[:, names.index(prefix + "Head")]
    top = J[:, names.index(prefix + "HeadTop_End")]
    neck = J[:, names.index(prefix + "Neck")]
    up = top - h
    up /= np.linalg.norm(up, axis=-1, keepdims=True)
    return h + up * 0.07, h, neck


def project(P, cam_mw, K):
    """Blender world points (..., 3) -> pixels (..., 2) through a Blender camera."""
    R, C = cam_mw[:3, :3], cam_mw[:3, 3]
    pc = np.einsum("ji,...j->...i", R, P - C)          # camera coords (x right, y up, -z fwd)
    x, y, z = pc[..., 0], -pc[..., 1], -pc[..., 2]      # OpenCV
    z = np.maximum(z, 1e-6)
    return np.stack([K[0, 0] * x / z + K[0, 2], K[1, 1] * y / z + K[1, 2]], -1)


def keypoints_2d(names, J, cam_mw, K, prefix):
    """(T, 17, 2) projected COCO-style points for a retargeted clip (nose from the face proxy)."""
    T = J.shape[0]
    out = np.full((T, 17, 2), np.nan)
    for c, bone in COCO.items():
        out[:, c] = project(J[:, names.index(prefix + bone)], cam_mw, K)
    nose, _, _ = face_point(names, J, prefix)
    out[:, 0] = project(nose, cam_mw, K)
    return out


def seg_vec(kp, seg):
    def pt(k):
        if k == "mid_hip":
            return 0.5 * (kp[:, 11] + kp[:, 12])
        if k == "mid_sh":
            return 0.5 * (kp[:, 5] + kp[:, 6])
        return kp[:, k]
    return pt(seg[1]) - pt(seg[0])


def seg_conf(conf, seg):
    def c(k):
        if k == "mid_hip":
            return np.minimum(conf[:, 11], conf[:, 12])
        if k == "mid_sh":
            return np.minimum(conf[:, 5], conf[:, 6])
        return conf[:, k]
    return np.minimum(c(seg[0]), c(seg[1]))


def angle_errors(kp_ret, kp_vid, conf, min_conf=0.6, min_len_frac=0.35):
    """Per-segment per-frame angle error (deg), NaN where the video segment
    is unreliable (low confidence, or foreshortened below `min_len_frac` of
    its own 90th-percentile screen length)."""
    out = {}
    for name, seg in SEGMENTS.items():
        a, b = seg_vec(kp_ret, seg), seg_vec(kp_vid, seg)
        la, lb = np.linalg.norm(a, axis=-1), np.linalg.norm(b, axis=-1)
        ok = (seg_conf(conf, seg) >= min_conf) & (lb >= min_len_frac * np.nanpercentile(lb, 90)) & (la > 1.0)
        cosang = np.sum(a * b, -1) / np.maximum(la * lb, 1e-9)
        err = np.degrees(np.arccos(np.clip(cosang, -1, 1)))
        out[name] = np.where(ok, err, np.nan)
    return out


def summarize(errs):
    allv = np.concatenate([v[~np.isnan(v)] for v in errs.values()])
    rep = {"overall_mean_deg": round(float(allv.mean()), 2),
           "overall_median_deg": round(float(np.median(allv)), 2),
           "overall_p90_deg": round(float(np.percentile(allv, 90)), 2),
           "frames_over_20deg_pct": round(float((allv > 20).mean() * 100), 1)}
    rep["groups_mean_deg"] = {g: round(float(np.nanmean(np.concatenate([errs[s] for s in segs]))), 2)
                              for g, segs in GROUPS.items()}
    rep["segments_mean_deg"] = {s: round(float(np.nanmean(v)), 2) for s, v in errs.items()}
    return rep


FOOT_REST = {"ankle": 0.105, "ball": 0.033}   # Y Bot; overridden by --rig-profile


def contact_metrics(names, J, contacts, prefix, fps, floor=0.0):
    """Skate (cm/s, median over planted frames) and sole height error (cm)."""
    rep = {}
    for k, side in enumerate(("Left", "Right")):
        ank = J[:, names.index(prefix + side + "Foot")]
        ball = J[:, names.index(prefix + side + "ToeBase")]
        c = contacts[:, k]
        mm = c[1:] & c[:-1]
        va = np.linalg.norm(np.diff(ank[:, :2], axis=0), axis=1) * fps * 100
        vb = np.linalg.norm(np.diff(ball[:, :2], axis=0), axis=1) * fps * 100
        v = np.minimum(va, vb)          # a pivot moves one of the two, a skate moves both
        rep[side] = {"skate_median_cm_s": round(float(np.median(v[mm])), 2) if mm.any() else None,
                     "skate_p90_cm_s": round(float(np.percentile(v[mm], 90)), 2) if mm.any() else None,
                     "lowest_point_cm": round(float(np.minimum(ank[:, 2] - FOOT_REST["ankle"],
                                                                ball[:, 2] - FOOT_REST["ball"])[c].mean() * 100), 2)}
    return rep


def jitter(J, fps):
    """Mean joint jerk magnitude (m/s^3) — lower is smoother."""
    jerk = np.diff(J, 3, axis=0) * fps ** 3
    return round(float(np.linalg.norm(jerk, axis=-1).mean()), 1)


def foot_direction_errors(names, J_src, cam_mw, K, prefix, feet_kp, min_conf=0.6, min_len_px=12.0):
    """On-screen angle between each foot of the clip (ankle -> toe tip:
    Foot head -> Toe_End head) and the performer's foot as MediaPipe sees
    it (ankle 27/28 -> toe tip 31/32), per plate frame. NaN where the
    detection is weak or the foot points at the camera (too short on
    screen for its direction to mean anything)."""
    out = {}
    for side, (a_id, t_id) in (("L foot", (27, 31)), ("R foot", (28, 32))):
        pre = "Left" if side[0] == "L" else "Right"
        a = project(J_src[:, names.index(prefix + pre + "Foot")], cam_mw, K)
        t = project(J_src[:, names.index(prefix + pre + "Toe_End")], cam_mw, K)
        va, vb = t - a, feet_kp[:, t_id, :2] - feet_kp[:, a_id, :2]
        conf = np.minimum(feet_kp[:, a_id, 2] * feet_kp[:, a_id, 3], feet_kp[:, t_id, 2] * feet_kp[:, t_id, 3])
        la, lb = np.linalg.norm(va, axis=-1), np.linalg.norm(vb, axis=-1)
        ok = (conf >= min_conf) & (lb >= min_len_px) & (la > 1.0)
        cosang = np.sum(va * vb, -1) / np.maximum(la * lb, 1e-9)
        out[side] = np.where(ok, np.degrees(np.arccos(np.clip(cosang, -1, 1))), np.nan)
    return out


def evaluate(spec, clip_dir, vs_dir=None, smplx=None):
    names, J, mot = load_clip(clip_dir)
    if mot is None:
        raise SystemExit(f"{clip_dir} has no motion.npz — the reference clip must come from retarget_smplx.py")
    prefix = "mixamorig:" if names[0].startswith("mixamorig:") else ""
    src = np.load(rpath(smplx or spec.get("smplx") or (rpath(spec["landmarks"]).parent / "smplx.npz")))
    kp = src["kp2d"]                                  # (n_src, 17, 3), source clock
    fps_dst = float(mot["fps"])
    n_src = kp.shape[0]
    fps_src = float(src["fps"])
    # retarget sampled at the exact source times (linear in time)
    u = np.arange(n_src) / fps_src * fps_dst
    i0 = np.clip(np.floor(u).astype(int), 0, J.shape[0] - 1)
    i1 = np.minimum(i0 + 1, J.shape[0] - 1)
    w = (u - i0)[:, None, None]

    def at_src(X):
        return X[i0] * (1 - w) + X[i1] * w

    cam_mw, K = mot["cam_matrix_world"], mot["cam_K"]
    # Which frames a foot is planted: GVHMR's own contact flag on the plate
    # (static confidence > 0.9 on the ankle or the ball), resampled to the
    # clip clock. Independent of either retarget's own locks, so the slide
    # it measures cannot be zero by construction.
    st = np.asarray(src["static_conf"], dtype=np.float64)
    si = np.clip(np.round(np.arange(J.shape[0]) * fps_src / fps_dst).astype(int), 0, st.shape[0] - 1)
    contacts = np.stack([np.maximum(st[si, 0], st[si, 1]) > 0.9,
                         np.maximum(st[si, 2], st[si, 3]) > 0.9], 1)
    result = {}
    clips = {"new": (names, J)}
    if vs_dir is not None:
        n2, J2 = load_clip(vs_dir)[:2]
        T = min(J2.shape[0], J.shape[0])
        J2 = J2[:T].copy()
        # in-place legacy clip: give it the new clip's ground trajectory so
        # only the pose is compared
        r_new = J[:T, names.index(prefix + "Hips"), :2]
        r_old = J2[:, n2.index(prefix + "Hips"), :2]
        J2[:, :, :2] += (r_new - r_old)[:, None, :]
        clips["legacy"] = (n2, J2)
    feet_path = rpath(smplx or spec.get("smplx") or (rpath(spec["landmarks"]).parent / "smplx.npz")).with_name("feet2d.npz")
    feet_kp = np.load(feet_path)["kp"] if feet_path.exists() else None
    for key, (nm, JJ) in clips.items():
        JJs = at_src(JJ[: J.shape[0]]) if JJ.shape[0] >= J.shape[0] else \
            at_src(np.concatenate([JJ, np.repeat(JJ[-1:], J.shape[0] - JJ.shape[0], 0)]))
        kp_r = keypoints_2d(nm, JJs, cam_mw, K, prefix)
        errs = angle_errors(kp_r, kp[..., :2], kp[..., 2])
        rep = summarize(errs)
        if feet_kp is not None:
            fe = foot_direction_errors(nm, JJs, cam_mw, K, prefix, feet_kp[: JJs.shape[0]])
            allf = np.concatenate([v[~np.isnan(v)] for v in fe.values()])
            rep["feet_mean_deg"] = round(float(allf.mean()), 2) if len(allf) else None
            rep["feet_p90_deg"] = round(float(np.percentile(allf, 90)), 2) if len(allf) else None
        rep["per_frame_mean_deg"] = [None if np.all(np.isnan(r)) else round(float(np.nanmean(r)), 2)
                                     for r in np.stack(list(errs.values()), 1)]
        rep["contacts"] = contact_metrics(nm, JJ, contacts[: JJ.shape[0]], prefix, fps_dst)
        rep["jitter_jerk"] = jitter(JJ, fps_dst)
        result[key] = rep
    return result


def main():
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--spec", required=True)
    ap.add_argument("--clip", required=True, help="clip dir with motion.npz (retarget_smplx.py)")
    ap.add_argument("--vs", help="legacy clip dir (curves.json) to measure the same way")
    ap.add_argument("--smplx")
    ap.add_argument("--rig-profile", help="rig profile for the rest ankle/ball heights (default: spec's, else Y Bot)")
    ap.add_argument("--out")
    args = ap.parse_args()
    spec = json.loads(rpath(args.spec).read_text(encoding="utf-8"))
    prof = args.rig_profile or spec.get("rig_profile")
    if prof and rpath(prof).exists():
        rest = json.loads(rpath(prof).read_text(encoding="utf-8"))["rest"]
        FOOT_REST.update(ankle=float(rest["l_ankle"][2]), ball=float(rest["l_foot"][2]))
    res = evaluate(spec, rpath(args.clip), rpath(args.vs) if args.vs else None, args.smplx)
    for key, rep in res.items():
        print(f"[{key}] 2D segment angle vs video: mean {rep['overall_mean_deg']} deg, "
              f"median {rep['overall_median_deg']}, p90 {rep['overall_p90_deg']}, "
              f">20deg {rep['frames_over_20deg_pct']}%")
        print(f"        groups {rep['groups_mean_deg']} | feet (ankle->toe vs MediaPipe) "
              f"mean {rep.get('feet_mean_deg')} p90 {rep.get('feet_p90_deg')}")
        print(f"        contacts {rep['contacts']} | jerk {rep['jitter_jerk']}")
    if args.out:
        rpath(args.out).write_text(json.dumps(res, indent=1), encoding="utf-8")
        print("wrote", rpath(args.out))


if __name__ == "__main__":
    main()
