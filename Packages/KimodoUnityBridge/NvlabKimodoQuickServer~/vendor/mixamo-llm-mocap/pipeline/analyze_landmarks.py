"""Numeric beat detection over a landmarks.json — the tool that writes
the beat sheet's evidence column. Run this BEFORE writing an action_spec
and take every limb decision from these numbers, never from
screen-reading stills (facing the camera, viewer-left is the
character's RIGHT — every screen-read limb guess in this project's
history was wrong at least once).

Usage:
  python pipeline\\analyze_landmarks.py --landmarks plates\\<plate>\\landmarks.json [--step 6]

Prints a per-N-frame table plus detected events:
  - pelvis baseline / dips (ducks, crouches) / peaks (jumps)
  - windows where both feet are airborne (true flight)
  - single-leg-up windows (kicks, knees) with the acting side
  - punch windows per arm (wrist depth toward camera, z < -0.32)
  - T-pose spans (wrist span > 1.22 m) — bind candidates
  - shoulder-line yaw (facing wobble vs punch rotation)

True joint heights need `pelvis_height` in the landmarks (the GVHMR
estimator writes it); without it, heights fall back to hip-relative.
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path

import numpy as np

REPO = Path(__file__).resolve().parents[1]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--landmarks", required=True, type=Path)
    ap.add_argument("--step", type=int, default=6)
    args = ap.parse_args()
    lm = args.landmarks if args.landmarks.is_absolute() else REPO / args.landmarks
    d = json.loads(lm.read_text(encoding="utf-8"))
    F = d["frames"]
    n = len(F)

    def w(i, name):
        l = F[i]["world"][name]
        return np.array([l["x"], l["y"], l["z"]])

    has_ph = "pelvis_height" in F[0]
    ph = np.array([f.get("pelvis_height", 0.0) for f in F])

    def height(i, name):
        # y is down and per-frame mid-hip centered; pelvis_height restores ground.
        return (ph[i] if has_ph else 0.0) - w(i, name)[1] - (0.0 if has_ph else 0.0)

    la_h = np.array([height(i, "left_ankle") for i in range(n)])
    ra_h = np.array([height(i, "right_ankle") for i in range(n)])
    lk_h = np.array([height(i, "left_knee") for i in range(n)])
    rk_h = np.array([height(i, "right_knee") for i in range(n)])
    lw_z = np.array([w(i, "left_wrist")[2] for i in range(n)])
    rw_z = np.array([w(i, "right_wrist")[2] for i in range(n)])
    span = np.array([np.linalg.norm(w(i, "left_wrist")[[0, 2]] - w(i, "right_wrist")[[0, 2]]) for i in range(n)])
    yaw = np.array([np.degrees(np.arctan2(-(w(i, "left_shoulder") - w(i, "right_shoulder"))[2],
                                          (w(i, "left_shoulder") - w(i, "right_shoulder"))[0])) for i in range(n)])

    print("  f |  ph   | laH   raH  | lkH   rkH  | lwZ    rwZ   | span  yaw")
    for i in range(0, n, max(1, args.step)):
        print(f"{i+1:4d} | {ph[i]:.3f} | {la_h[i]:.2f}  {ra_h[i]:.2f} | {lk_h[i]:.2f}  {rk_h[i]:.2f} "
              f"| {lw_z[i]:+.2f}  {rw_z[i]:+.2f} | {span[i]:.2f}  {yaw[i]:+4.0f}")

    def segments(idx, gap=3):
        return np.split(idx, np.where(np.diff(idx) > gap)[0] + 1) if len(idx) else []

    if has_ph:
        base = float(np.median(ph[: max(10, n // 12)]))
        print(f"\npelvis baseline {base:.3f} | min {ph.min():.3f} @ f{ph.argmin()+1} | max {ph.max():.3f} @ f{ph.argmax()+1}")
        air = np.where((la_h > 0.25) & (ra_h > 0.25))[0]
        for s in segments(air):
            print(f"both feet airborne (>0.25): f{s[0]+1}-f{s[-1]+1} "
                  f"(peak lower-ankle {np.minimum(la_h, ra_h)[s].max():.2f} m)")
        for nm, up, other in (("left", la_h, ra_h), ("right", ra_h, la_h)):
            leg = np.where((up > 0.30) & (other < 0.15))[0]
            for s in segments(leg):
                print(f"{nm} leg up on {('right' if nm == 'left' else 'left')} support: "
                      f"f{s[0]+1}-f{s[-1]+1} (ankle peak {up[s].max():.2f} @ f{s[up[s].argmax()]+1})")

    for nm, z in (("left", lw_z), ("right", rw_z)):
        for s in segments(np.where(z < -0.32)[0]):
            print(f"{nm} wrist toward camera (z<-0.32): f{s[0]+1}-f{s[-1]+1} (min {z[s].min():+.2f} @ f{s[z[s].argmin()]+1})")

    for s in segments(np.where(span > 1.22)[0], gap=4):
        print(f"T-pose span (>1.22 m): f{s[0]+1}-f{s[-1]+1}")
    print(f"shoulder-line yaw range: {yaw.min():+.0f}..{yaw.max():+.0f} deg "
          "(punches rotate shoulders ±40-60 without turning the hips)")


if __name__ == "__main__":
    main()
