"""SMPL-X body maths in plain numpy: shaped rest skeleton, forward
kinematics, rotation helpers.

The retarget (retarget_smplx.py) runs on the estimator's SMPL-X
PARAMETERS — per-joint rotations, not joint positions. A position track
cannot describe how a forearm, a chest or a head is twisted; the
rotations can, so they are what gets transferred to the Mixamo rig.

Conventions
  SMPL-X canonical axes: X = character left, Y = up, Z = character
  forward. At the rest pose every joint's WORLD rotation is identity, so
  a joint's FK world rotation is exactly "how far this body part turned
  away from rest".

  A Mixamo armature, inside its X+90 / 0.01 import transform, uses the
  same axes (X left, Y up, Z forward) in centimetres — the retarget needs
  no axis conversion, only the unit.

Nothing here needs torch or the gated SMPL-X model file at retarget
time: the estimator bakes the performer's shaped rest joints and foot
geometry into smplx.npz (see estimate_pose_gvhmr.py).
"""

from __future__ import annotations

import numpy as np

# SMPL-X body joints (the first 22 of the 55), in model order.
NAMES = [
    "pelvis", "L_hip", "R_hip", "spine1", "L_knee", "R_knee", "spine2",
    "L_ankle", "R_ankle", "spine3", "L_foot", "R_foot", "neck", "L_collar",
    "R_collar", "head", "L_shoulder", "R_shoulder", "L_elbow", "R_elbow",
    "L_wrist", "R_wrist",
]
PARENT = np.array([-1, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 9, 9, 12, 13, 14, 16, 17, 18, 19])
J = {n: i for i, n in enumerate(NAMES)}

# GVHMR's static-confidence head, in the network's joint order
# (hmr4d/model/gvhmr/utils/postprocess.py).
STATIC_JOINTS = ["L_ankle", "L_foot", "R_ankle", "R_foot", "L_wrist", "R_wrist"]

# SMPL-X mesh vertices of the foot keypoints (the smplx package's
# vertex_ids['smplx'], OpenPose foot convention). The ankle->ball JOINT
# direction is not the foot's axis: on the template it reads 18-22 degrees
# of toe-out where the heel->toes line of the mesh reads 5-6.
FOOT_VERTS = {"L": {"heel": 8846, "big_toe": 5770, "small_toe": 5780},
              "R": {"heel": 8635, "big_toe": 8463, "small_toe": 8474}}


# --------------------------------------------------------------------------
# rotations

def axis_angle_to_matrix(aa: np.ndarray) -> np.ndarray:
    """(..., 3) axis-angle -> (..., 3, 3), Rodrigues, vectorised."""
    aa = np.asarray(aa, dtype=np.float64)
    theta = np.linalg.norm(aa, axis=-1, keepdims=True)
    k = aa / np.maximum(theta, 1e-12)
    x, y, z = k[..., 0], k[..., 1], k[..., 2]
    zero = np.zeros_like(x)
    K = np.stack([zero, -z, y, z, zero, -x, -y, x, zero], -1).reshape(aa.shape[:-1] + (3, 3))
    s = np.sin(theta)[..., None]
    c = np.cos(theta)[..., None]
    eye = np.broadcast_to(np.eye(3), K.shape)
    return eye + s * K + (1.0 - c) * (K @ K)


def matrix_to_quat(R: np.ndarray) -> np.ndarray:
    """(..., 3, 3) -> (..., 4) quaternions, WXYZ (Blender order), w >= 0."""
    R = np.asarray(R, dtype=np.float64)
    m = R
    t = m[..., 0, 0] + m[..., 1, 1] + m[..., 2, 2]
    q = np.empty(R.shape[:-2] + (4,))
    c0 = t > 0.0
    c1 = ~c0 & (m[..., 0, 0] >= m[..., 1, 1]) & (m[..., 0, 0] >= m[..., 2, 2])
    c2 = ~c0 & ~c1 & (m[..., 1, 1] >= m[..., 2, 2])
    c3 = ~c0 & ~c1 & ~c2
    s = np.sqrt(np.maximum(t + 1.0, 1e-20)) * 2.0
    q0 = np.stack([0.25 * s, (m[..., 2, 1] - m[..., 1, 2]) / s,
                   (m[..., 0, 2] - m[..., 2, 0]) / s, (m[..., 1, 0] - m[..., 0, 1]) / s], -1)
    s = np.sqrt(np.maximum(1.0 + m[..., 0, 0] - m[..., 1, 1] - m[..., 2, 2], 1e-20)) * 2.0
    q1 = np.stack([(m[..., 2, 1] - m[..., 1, 2]) / s, 0.25 * s,
                   (m[..., 0, 1] + m[..., 1, 0]) / s, (m[..., 0, 2] + m[..., 2, 0]) / s], -1)
    s = np.sqrt(np.maximum(1.0 - m[..., 0, 0] + m[..., 1, 1] - m[..., 2, 2], 1e-20)) * 2.0
    q2 = np.stack([(m[..., 0, 2] - m[..., 2, 0]) / s, (m[..., 0, 1] + m[..., 1, 0]) / s,
                   0.25 * s, (m[..., 1, 2] + m[..., 2, 1]) / s], -1)
    s = np.sqrt(np.maximum(1.0 - m[..., 0, 0] - m[..., 1, 1] + m[..., 2, 2], 1e-20)) * 2.0
    q3 = np.stack([(m[..., 1, 0] - m[..., 0, 1]) / s, (m[..., 0, 2] + m[..., 2, 0]) / s,
                   (m[..., 1, 2] + m[..., 2, 1]) / s, 0.25 * s], -1)
    q[c0], q[c1], q[c2], q[c3] = q0[c0], q1[c1], q2[c2], q3[c3]
    q /= np.linalg.norm(q, axis=-1, keepdims=True)
    return np.where(q[..., :1] < 0.0, -q, q)


def quat_to_matrix(q: np.ndarray) -> np.ndarray:
    """(..., 4) WXYZ -> (..., 3, 3)."""
    q = np.asarray(q, dtype=np.float64)
    q = q / np.maximum(np.linalg.norm(q, axis=-1, keepdims=True), 1e-12)
    w, x, y, z = q[..., 0], q[..., 1], q[..., 2], q[..., 3]
    return np.stack([
        1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w),
        2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w),
        2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y),
    ], -1).reshape(q.shape[:-1] + (3, 3))


def quat_unroll(q: np.ndarray) -> np.ndarray:
    """Flip signs along axis 0 so consecutive keys take the short way round
    (q and -q are the same rotation; an interpolating player is not told)."""
    q = np.array(q, dtype=np.float64, copy=True)
    for t in range(1, q.shape[0]):
        flip = np.sum(q[t] * q[t - 1], axis=-1) < 0.0
        q[t][flip] *= -1.0
    return q


def slerp(q0: np.ndarray, q1: np.ndarray, u: np.ndarray) -> np.ndarray:
    """Spherical interpolation, broadcast over leading dims; u in [0, 1]."""
    q0 = np.asarray(q0, dtype=np.float64)
    q1 = np.asarray(q1, dtype=np.float64)
    u = np.asarray(u, dtype=np.float64)[..., None]
    d = np.sum(q0 * q1, axis=-1, keepdims=True)
    q1 = np.where(d < 0.0, -q1, q1)
    d = np.abs(d)
    theta = np.arccos(np.clip(d, -1.0, 1.0))
    s = np.sin(theta)
    small = s < 1e-6
    w0 = np.where(small, 1.0 - u, np.sin((1.0 - u) * theta) / np.where(small, 1.0, s))
    w1 = np.where(small, u, np.sin(u * theta) / np.where(small, 1.0, s))
    out = w0 * q0 + w1 * q1
    return out / np.linalg.norm(out, axis=-1, keepdims=True)


def align_rotation(src: np.ndarray, dst: np.ndarray) -> np.ndarray:
    """Minimal rotation carrying unit vector(s) `src` onto `dst`,
    vectorised over leading dims."""
    src = np.asarray(src, dtype=np.float64)
    dst = np.asarray(dst, dtype=np.float64)
    src = src / np.maximum(np.linalg.norm(src, axis=-1, keepdims=True), 1e-12)
    dst = dst / np.maximum(np.linalg.norm(dst, axis=-1, keepdims=True), 1e-12)
    v = np.cross(src, dst)
    c = np.sum(src * dst, axis=-1)
    s2 = np.sum(v * v, axis=-1)
    x, y, z = v[..., 0], v[..., 1], v[..., 2]
    zero = np.zeros_like(x)
    K = np.stack([zero, -z, y, z, zero, -x, -y, x, zero], -1).reshape(v.shape[:-1] + (3, 3))
    f = np.where(s2 > 1e-18, (1.0 - c) / np.maximum(s2, 1e-18), 0.0)[..., None, None]
    R = np.broadcast_to(np.eye(3), K.shape) + K + (K @ K) * f
    # antiparallel: rotate pi about any axis perpendicular to src
    anti = (s2 <= 1e-18) & (c < 0.0)
    if np.any(anti):
        a = src[anti]
        seed = np.where(np.abs(a[:, :1]) > 0.9, [[0.0, 1.0, 0.0]], [[1.0, 0.0, 0.0]])
        ax = np.cross(a, seed)
        ax /= np.linalg.norm(ax, axis=-1, keepdims=True)
        R = np.array(R, copy=True)
        R[anti] = 2.0 * ax[:, :, None] * ax[:, None, :] - np.eye(3)
    return R


def yaw_matrix(angle: np.ndarray) -> np.ndarray:
    """Rotation(s) about +Y (up) by `angle` radians."""
    a = np.asarray(angle, dtype=np.float64)
    c, s = np.cos(a), np.sin(a)
    one, zero = np.ones_like(a), np.zeros_like(a)
    return np.stack([c, zero, s, zero, one, zero, -s, zero, c], -1).reshape(a.shape + (3, 3))


def heading(R: np.ndarray) -> np.ndarray:
    """Yaw angle (radians about +Y) of a body frame's forward (+Z) axis."""
    f = R[..., :, 2]
    return np.arctan2(f[..., 0], f[..., 2])


# --------------------------------------------------------------------------
# the body model (estimator side only: needs the gated SMPLX_NEUTRAL.npz)

def shaped_rest(model_npz, betas) -> dict:
    """Performer-shaped rest skeleton + the foot geometry the ground
    solver needs, from SMPLX_NEUTRAL.npz and 10 betas.

    Joints are regressed from the SHAPED rest mesh — exactly how SMPL-X
    places them — so the skeleton has the performer's real proportions
    (leg length, shoulder width), not the template's.
    """
    z = np.load(model_npz, allow_pickle=True)
    betas = np.asarray(betas, dtype=np.float64).reshape(-1)
    nb = betas.shape[0]
    if "compact_format" in z.files:
        # The pose-only QuickServer route needs the shaped rest joints, not a
        # full mesh.  The compact V437 asset stores these regressions directly.
        joints = z["J_template"].astype(np.float64) + np.einsum(
            "jck,k->jc", z["J_shapedirs"][:, :, :nb].astype(np.float64), betas
        )
        return {"joints": joints[:22]}
    v = z["v_template"].astype(np.float64) + np.einsum(
        "vck,k->vc", z["shapedirs"][:, :, :nb].astype(np.float64), betas)
    Jr = z["J_regressor"].astype(np.float64)
    joints = Jr[:22] @ v
    parents = z["kintree_table"][0][:22].astype(np.int64)
    parents[0] = -1
    if not np.array_equal(parents, PARENT):
        raise ValueError(f"unexpected SMPL-X kinematic tree {parents.tolist()}")
    # Sole = the lowest vertices under each foot. The ankle and the ball
    # joints sit INSIDE the foot; their height above the sole is what maps
    # a planted SMPL-X foot onto a planted Mixamo foot.
    weights = z["weights"].astype(np.float64)
    foot = {}
    for side, (ank, ball) in (("L", (7, 10)), ("R", (8, 11))):
        owned = (weights[:, ank] + weights[:, ball]) > 0.5
        sole = float(v[owned, 1].min())
        heel, big, small = (v[FOOT_VERTS[side][k]] for k in ("heel", "big_toe", "small_toe"))
        foot[side] = {"sole_y": sole,
                      "ankle_h": float(joints[ank, 1] - sole),
                      "ball_h": float(joints[ball, 1] - sole),
                      "toe_tip_z": float(v[owned, 2].max()),
                      "heel_z": float(v[owned, 2].min()),
                      # the foot as a reader sees it: heel to toes, on the mesh
                      "heel": heel.tolist(), "big_toe": big.tolist(), "small_toe": small.tolist()}
    return {"joints": joints, "foot": foot, "floor_y": float(min(foot["L"]["sole_y"], foot["R"]["sole_y"])),
            "height": float(v[:, 1].max() - v[:, 1].min())}


def fk(global_orient: np.ndarray, body_pose: np.ndarray, transl: np.ndarray,
       rest_joints: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    """SMPL-X forward kinematics for the 22 body joints.

    global_orient (T,3), body_pose (T,63) axis-angle; transl (T,3).
    Returns world joint positions (T,22,3) and world rotations (T,22,3,3).
    SMPL-X rotates the whole body about the ROOT JOINT and then adds
    `transl`, so the pelvis sits at rest_joints[0] + transl.
    """
    T = global_orient.shape[0]
    local = axis_angle_to_matrix(np.concatenate(
        [global_orient.reshape(T, 1, 3), body_pose.reshape(T, 21, 3)], axis=1))
    return fk_local(local, transl, rest_joints)


def fk_local(local: np.ndarray, transl: np.ndarray, rest_joints: np.ndarray):
    """FK from local rotation matrices (T,22,3,3)."""
    T = local.shape[0]
    R = np.empty((T, 22, 3, 3))
    P = np.empty((T, 22, 3))
    R[:, 0] = local[:, 0]
    P[:, 0] = rest_joints[0] + transl
    for j in range(1, 22):
        p = PARENT[j]
        R[:, j] = R[:, p] @ local[:, j]
        P[:, j] = P[:, p] + np.einsum("tij,j->ti", R[:, p], rest_joints[j] - rest_joints[p])
    return P, R


def rest_direction(rest_joints: np.ndarray, j: int, child: int) -> np.ndarray:
    d = rest_joints[child] - rest_joints[j]
    return d / np.linalg.norm(d)
