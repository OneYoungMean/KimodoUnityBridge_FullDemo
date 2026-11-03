"""Refine GVHMR's SMPL-X pose against the video's own 2D keypoints.

  tools\\GVHMR\\.venv\\Scripts\\python.exe pipeline\\refine_smplx.py \\
      --smplx plates\\<plate>\\smplx.npz [--out plates\\<plate>\\smplx_refined.npz]

GVHMR regresses a smooth, plausible motion — and, being a regressor
trained on typical motion, it pulls the EXTREMES of fast limbs toward the
mean: on the fight plate the kicking ankle at the apex sits 35-65 px
(0.10-0.18 m) below where ViTPose sees it in the very frames GVHMR was
fed. The legacy lift paid for this by hand (`leg_pose` corrections sized
from a comparison tool). This stage fixes it at the source, in the spirit
of SMPLify: the body pose is optimised so the model's COCO-17 keypoints,
projected through the plate's camera, land on the detected ones — while

  - staying close to GVHMR's pose (the prior: depth, twist, anything a
    2D view cannot see is left to the estimator),
  - keeping the CORRECTION smooth in time (the motion itself is never
    smoothed: a strike keeps its snap),
  - trusting a keypoint only as much as its detection confidence, with a
    robust loss so one bad detection cannot drag a limb.

Keypoints come from the same mesh-based COCO-17 regressor GVHMR uses, so
the model and the detector name the same anatomical points (a SMPL-X
joint centre is not a COCO keypoint: the hips differ by ~8 cm on screen).

Writes a new smplx.npz (same schema) with the refined body pose and
camera-frame root; the world-frame root orientation receives the same
correction. Runs in the GVHMR venv (torch + CUDA).
"""

from __future__ import annotations

import argparse
import os
import sys
from pathlib import Path

import numpy as np

REPO = Path(__file__).resolve().parents[1]
GVHMR_ROOT = Path(os.environ.get("GVHMR_ROOT", REPO / "tools" / "GVHMR"))


def rpath(p) -> Path:
    p = Path(p)
    return p if p.is_absolute() else (REPO / p)


# COCO-17: nose, eyes, ears, shoulders, elbows, wrists, hips, knees, ankles.
# Face points span ~25 px on a full-body plate: a 3 px detection error is a
# 10-degree head turn. They get a small weight, and the head and neck a
# strong prior, so the gaze stays GVHMR's (which sees the whole face).
KP_WEIGHT = np.array([0.15, 0.15, 0.15, 0.15, 0.15] + [1.0] * 12, dtype=np.float32)
# Heel and toe tip of each foot (detect_feet.py): they say which way a foot
# points and whether its heel is up. COCO-17 stops at the ankle.
FOOT_WEIGHT = 1.0
# prior weight per body joint (SMPL-X body_pose order, joints 1..21)
JOINT_PRIOR = np.ones(21, dtype=np.float32)
JOINT_PRIOR[[11, 14]] = 6.0          # neck, head
JOINT_PRIOR[[2, 5, 8]] = 2.0         # spine1-3: the torso is well estimated; keep its shape


def aa_to_mat(aa):
    import torch
    theta = torch.linalg.norm(aa, dim=-1, keepdim=True).clamp_min(1e-8)
    k = aa / theta
    K = torch.zeros(aa.shape[:-1] + (3, 3), device=aa.device, dtype=aa.dtype)
    K[..., 0, 1], K[..., 0, 2] = -k[..., 2], k[..., 1]
    K[..., 1, 0], K[..., 1, 2] = k[..., 2], -k[..., 0]
    K[..., 2, 0], K[..., 2, 1] = -k[..., 1], k[..., 0]
    s, c = torch.sin(theta)[..., None], torch.cos(theta)[..., None]
    eye = torch.eye(3, device=aa.device, dtype=aa.dtype).expand_as(K)
    return eye + s * K + (1 - c) * (K @ K)


def mat_to_aa(R):
    from pytorch3d.transforms import matrix_to_axis_angle
    return matrix_to_axis_angle(R)


def gaussian_time(x, sigma: float):
    """Gaussian filter along dim 0 (time) with edge padding."""
    import torch
    if sigma <= 0:
        return x
    r = int(np.ceil(3 * sigma))
    k = torch.exp(-0.5 * (torch.arange(-r, r + 1, device=x.device, dtype=x.dtype) / sigma) ** 2)
    k = k / k.sum()
    pad = torch.cat([x[:1].expand(r, *x.shape[1:]), x, x[-1:].expand(r, *x.shape[1:])], 0)
    return sum(k[i] * pad[i:i + x.shape[0]] for i in range(2 * r + 1))


# MediaPipe BlazePose ids of the foot keypoints fitted: heel and toe tip,
# per foot, in FOOT_ORDER. SMPL-X counterparts are mesh vertices (smplx
# vertex_ids, OpenPose foot convention); the toe tip sits between the big
# and the second toe, so it blends the big- and small-toe vertices.
FOOT_ORDER = [("L", "heel", 29), ("L", "toe", 31), ("R", "heel", 30), ("R", "toe", 32)]
FOOT_VIDS = {"L": {"heel": 8846, "big": 5770, "small": 5780},
             "R": {"heel": 8635, "big": 8463, "small": 8474}}


def feet_targets(feet2d: np.ndarray, kp2d: np.ndarray, max_ankle_px: float = 25.0) -> np.ndarray:
    """(T, 4, 3) heel/toe targets (px, conf) from MediaPipe landmarks. A
    foot whose MediaPipe ankle strays from ViTPose's ankle is a tracking
    slip on that frame: its targets get zero weight."""
    T = min(feet2d.shape[0], kp2d.shape[0])
    out = np.zeros((kp2d.shape[0], 4, 3))
    for i, (side, _, mp_id) in enumerate(FOOT_ORDER):
        mp_ank, vit_ank = (27, 15) if side == "L" else (28, 16)
        conf = feet2d[:T, mp_id, 2] * feet2d[:T, mp_id, 3]
        slip = np.linalg.norm(feet2d[:T, mp_ank, :2] - kp2d[:T, vit_ank, :2], axis=-1) > max_ankle_px
        conf = np.where(slip & (kp2d[:T, vit_ank, 2] > 0.5), 0.0, conf)
        out[:T, i, :2] = feet2d[:T, mp_id, :2]
        out[:T, i, 2] = conf
    return out


def make_model(device):
    """SMPL-X evaluated on just the vertices the fit needs: GVHMR's COCO-17
    regressor support plus the six foot vertices."""
    import torch
    from einops import einsum
    from hmr4d import PROJ_ROOT
    from hmr4d.utils.body_model.smplx_lite import SmplxLite

    class CocoFeet(SmplxLite):
        def __init__(self):
            super().__init__()
            smplx2smpl = torch.load(PROJ_ROOT / "hmr4d/utils/body_model/smplx2smpl_sparse.pt")
            reg = torch.load(PROJ_ROOT / "hmr4d/utils/body_model/smpl_coco17_J_regressor.pt")
            m = torch.matmul(reg, smplx2smpl.to_dense())
            jids, vids = torch.where(m != 0)
            wmat = torch.zeros(len(vids), 17)
            for i, (j, v) in enumerate(zip(jids, vids)):
                wmat[i, j] = m[j, v]
            self.register_buffer("w_coco", wmat, False)
            self.n_coco = len(vids)
            feet = [FOOT_VIDS[sd][k] for sd in ("L", "R") for k in ("heel", "big", "small")]
            allv = torch.cat([vids, torch.tensor(feet)])
            self.v_template = self.v_template[allv].clone()
            self.shapedirs = self.shapedirs[allv].clone()
            self.posedirs = self.posedirs[:, allv].clone()
            self.lbs_weights = self.lbs_weights[allv].clone()

        def forward(self, body_pose, betas, global_orient, transl):
            verts = super().forward(body_pose, betas, global_orient, transl)
            coco = einsum(self.w_coco, verts[..., :self.n_coco, :], "v j, ... v c -> ... j c")
            f = verts[..., self.n_coco:, :]                    # L heel, big, small, R heel, big, small
            feet = torch.stack([f[..., 0, :], 0.7 * f[..., 1, :] + 0.3 * f[..., 2, :],
                                f[..., 3, :], 0.7 * f[..., 4, :] + 0.3 * f[..., 5, :]], -2)
            return torch.cat([coco, feet], -2)                 # (..., 21, 3)

    return CocoFeet().to(device)


def refine(z: dict, iters: int = 400, w_prior: float = 4.0, w_smooth: float = 200.0,
           huber_m: float = 0.04, min_conf: float = 0.35, post_sigma: float = 1.0,
           feet2d: np.ndarray | None = None, w_floor: float = 3000.0,
           device: str = "cuda", log=print) -> dict:
    import torch
    os.chdir(GVHMR_ROOT)
    sys.path.insert(0, str(GVHMR_ROOT))

    T = z["body_pose"].shape[0]
    f32 = lambda a: torch.tensor(np.asarray(a), dtype=torch.float32, device=device)
    model = make_model(device)
    betas = f32(z["betas"])[None].repeat(T, 1)
    bp0 = f32(z["incam_body_pose"])
    go0 = f32(z["incam_global_orient"])
    tr0 = f32(z["incam_transl"])
    K = f32(z["K"])
    ft = feet_targets(feet2d, z["kp2d"]) if feet2d is not None else np.zeros((T, 4, 3))
    kp = f32(np.concatenate([z["kp2d"], ft], 1))            # (T, 21, 3): COCO-17 + heels/toes
    conf = kp[..., 2]
    kw = np.concatenate([KP_WEIGHT, np.full(4, FOOT_WEIGHT, dtype=np.float32)])
    w = torch.where(conf >= min_conf, conf ** 2, torch.zeros_like(conf)) * f32(kw)[None]

    # World "up" seen from the camera (GVHMR's world is gravity-aligned; the
    # camera is static, so one direction for the whole take).
    Rw = aa_to_mat(f32(z["global_orient"]))
    M = Rw @ aa_to_mat(go0).transpose(-1, -2)                  # world-from-camera, per frame
    up_c = M.transpose(-1, -2)[:, :, 1]                          # (T, 3): the retarget's up, per frame
    # Both feet planted (GVHMR's contact detector): the floor is flat, so
    # their lowest points share one height. A 2D fit cannot see depth, and
    # without this it tilts a wide stance — measured on the kung-fu plate:
    # one foot 12 cm above the other, not a pixel off in the image.
    st = np.nan_to_num(np.asarray(z["static_conf"], dtype=np.float64))
    both = np.minimum(st[:, [0, 1]].max(1), st[:, [2, 3]].max(1))
    w_both = f32(np.where(both > 0.5, both, 0.0))

    with torch.no_grad():
        J0 = model(body_pose=bp0, betas=betas, global_orient=go0, transl=tr0)   # (T, 21, 3) camera
    depth = J0[..., 2].mean(-1, keepdim=True).clamp_min(0.5)                   # per-frame subject depth
    px_per_m = K[0, 0] / depth                                                 # (T, 1)

    jp = f32(JOINT_PRIOR)[None]
    d_bp = torch.zeros_like(bp0, requires_grad=True)
    d_go = torch.zeros_like(go0, requires_grad=True)
    d_tr = torch.zeros_like(tr0, requires_grad=True)
    opt = torch.optim.Adam([d_bp, d_go, d_tr], lr=0.01)

    def reproj(J):
        z_ = J[..., 2].clamp_min(0.1)
        return torch.stack([K[0, 0] * J[..., 0] / z_ + K[0, 2], K[1, 1] * J[..., 1] / z_ + K[1, 2]], -1)

    def second_diff(x):
        return x[2:] - 2 * x[1:-1] + x[:-2]

    def loss_terms():
        R0 = aa_to_mat(go0)
        go = mat_to_aa(aa_to_mat(d_go) @ R0)
        J = model(body_pose=bp0 + d_bp, betas=betas, global_orient=go, transl=tr0 + d_tr)
        r = (reproj(J) - kp[..., :2]).norm(dim=-1) / px_per_m                  # metres at the subject
        hub = torch.where(r < huber_m, 0.5 * r ** 2, huber_m * (r - 0.5 * huber_m))
        l2d = (w * hub).sum() / w.sum().clamp_min(1.0)
        lprior = (((d_bp.view(T, 21, 3) ** 2).sum(-1) * jp).mean() / 3.0 + 2.0 * (d_go ** 2).mean()
                  + 4.0 * (d_tr ** 2).mean())
        h = (J[:, 17:21] * up_c[:, None, :]).sum(-1)                   # heights of heels / toe tips
        low_l, low_r = torch.minimum(h[:, 0], h[:, 1]), torch.minimum(h[:, 2], h[:, 3])
        lfloor = (w_both * (low_l - low_r) ** 2).sum() / w_both.sum().clamp_min(1.0)
        lsm = (second_diff(d_bp) ** 2).mean() + (second_diff(d_go) ** 2).mean() + 10.0 * (second_diff(d_tr) ** 2).mean()
        return l2d, lprior, lsm, r, lfloor

    with torch.no_grad():
        _, _, _, r_before, fl_before = loss_terms()
    for it in range(iters):
        opt.zero_grad()
        l2d, lprior, lsm, _, lfloor = loss_terms()
        loss = 1000.0 * l2d + w_prior * lprior + w_smooth * lsm + w_floor * lfloor
        loss.backward()
        opt.step()
        if it in (0, iters // 2, iters - 1):
            log(f"  iter {it:4d}  2D {l2d.item() * 1000:.3f}  prior {lprior.item():.5f}  smooth {lsm.item():.6f}")
    with torch.no_grad():
        # A correction that lives for one frame is detector noise; one that
        # lasts (a kick apex spans 5-8 frames) is signal. Low-pass the
        # correction itself, never the motion.
        for d in (d_bp, d_go, d_tr):
            d.copy_(gaussian_time(d, float(post_sigma)))
        _, _, _, r_after, fl_after = loss_terms()
        go_new = mat_to_aa(aa_to_mat(d_go) @ aa_to_mat(go0))

    valid = (w > 0)
    fv = valid[:, 17:]
    stats = {"feet_before_cm": float(r_before[:, 17:][fv].mean() * 100) if fv.any() else float("nan"),
             "feet_after_cm": float(r_after[:, 17:][fv].mean() * 100) if fv.any() else float("nan"),
             "reproj_before_cm": float((r_before[valid]).mean() * 100),
             "reproj_after_cm": float((r_after[valid]).mean() * 100),
             "reproj_p95_before_cm": float(torch.quantile(r_before[valid], 0.95) * 100),
             "reproj_p95_after_cm": float(torch.quantile(r_after[valid], 0.95) * 100),
             "planted_feet_level_cm": [float(fl_before.sqrt() * 100), float(fl_after.sqrt() * 100)],
             "max_joint_change_deg": float(torch.linalg.norm(d_bp.view(T, 21, 3), dim=-1).max() * 57.2958)}

    out = dict(z)
    bp_new = (bp0 + d_bp).detach().cpu().numpy().astype(np.float64)
    out["body_pose"] = bp_new
    out["incam_body_pose"] = bp_new
    out["incam_global_orient"] = go_new.cpu().numpy().astype(np.float64)
    out["incam_transl"] = (tr0 + d_tr).detach().cpu().numpy().astype(np.float64)
    # world root: the same rotation correction, carried through the per-frame
    # world-from-camera rotation GVHMR implies (R_w = M R_c, M fixed)
    Rw = aa_to_mat(f32(z["global_orient"]))
    Rc_old, Rc_new = aa_to_mat(go0), aa_to_mat(go_new)
    Rw_new = Rw @ Rc_old.transpose(-1, -2) @ Rc_new
    out["global_orient"] = mat_to_aa(Rw_new).cpu().numpy().astype(np.float64)
    # ...and the root's move: the pelvis shifted by d_tr in the camera frame,
    # so by M d_tr in the world (rotations turn the body about the pelvis)
    out["transl"] = (f32(z["transl"]) + torch.einsum("tij,tj->ti", M, d_tr.detach())).cpu().numpy().astype(np.float64)
    out["refined"] = np.array(True)
    out["feet2d"] = ft                                       # heel/toe targets used (px, conf)
    return out, stats


def main():
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--smplx", required=True)
    ap.add_argument("--out", help="default: <smplx>_refined.npz beside the input")
    ap.add_argument("--iters", type=int, default=400)
    ap.add_argument("--prior", type=float, default=4.0, help="pull toward GVHMR's pose (higher = closer)")
    ap.add_argument("--smooth", type=float, default=200.0, help="smoothness of the correction over time")
    ap.add_argument("--feet", help="feet2d.npz from detect_feet.py (default: next to --smplx)")
    args = ap.parse_args()
    src = rpath(args.smplx)
    z = dict(np.load(src, allow_pickle=False))
    feet = rpath(args.feet) if args.feet else src.with_name("feet2d.npz")
    feet2d = np.load(feet)["kp"] if feet.exists() else None
    print(f"foot keypoints: {feet if feet2d is not None else 'none (run detect_feet.py for heel/toe fitting)'}")
    out, stats = refine(z, iters=args.iters, w_prior=args.prior, w_smooth=args.smooth, feet2d=feet2d)
    dst = rpath(args.out) if args.out else src.with_name(src.stem + "_refined.npz")
    np.savez_compressed(dst, **out)
    print(f"wrote {dst}")
    print("  keypoint error at the subject: mean {reproj_before_cm:.1f} -> {reproj_after_cm:.1f} cm, "
          "p95 {reproj_p95_before_cm:.1f} -> {reproj_p95_after_cm:.1f} cm; heel/toe "
          "{feet_before_cm:.1f} -> {feet_after_cm:.1f} cm; largest joint change "
          "{max_joint_change_deg:.1f} deg".format(**stats))


if __name__ == "__main__":
    main()
