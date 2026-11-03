"""Generic clip QA: numeric checks against a clip's dumped curves.json,
driven by the action_spec.

Usage:
  python pipeline\\qa_clip.py --spec action_specs\\<motion>.json

Numbers can pass while the motion is wrong — stills and a human eye are
still the last word. This catches the classes of failure that burn
passes: exploded bones, hip pops, foot skate on the support, a drifting
in-place root, an end pose that is not rest, grounded "airborne" frames.
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path

import numpy as np

REPO = Path(__file__).resolve().parents[1]
GROUND_Z = 0.105
REST_LW = (0.7378, 1.4357)
REST_RW = (-0.7378, 1.4356)


def use_profile(path=None) -> str:
    """This character's measured rest geometry (see setup_rig.py)."""
    global GROUND_Z, REST_LW, REST_RW
    p = (Path(path) if Path(path).is_absolute() else REPO / path) if path else (REPO / "rig_profile.json")
    if not p.exists():
        if path:
            raise SystemExit(f"rig profile not found: {p}")
        return "built-in Y Bot"
    _p = json.loads(p.read_text(encoding="utf-8"))
    GROUND_Z = float(_p["ground_z"])
    REST_LW = (_p["rest"]["l_wrist"][0], _p["rest"]["l_wrist"][2])
    REST_RW = (_p["rest"]["r_wrist"][0], _p["rest"]["r_wrist"][2])
    return p.name


def rpath(p) -> Path:
    p = Path(p)
    return p if p.is_absolute() else (REPO / p)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--spec", required=True, type=Path)
    args = ap.parse_args()
    spec = json.loads(rpath(args.spec).read_text(encoding="utf-8"))
    print("profile:", use_profile(spec.get("rig_profile")))
    clip_dir = rpath(spec["clip_dir"])
    curves = json.loads((clip_dir / "curves.json").read_text(encoding="utf-8"))
    frames = curves["frames"]
    n = len(frames)
    src_fps = float(spec.get("src_fps", 24))
    dst_fps = float(spec.get("dst_fps", 30))

    def w(i, bone):
        return np.array(frames[i]["bones"][bone]["world_location"], dtype=float)

    names = list(frames[0]["bones"].keys())
    # A staged character stands off the origin by design; widen the
    # explosion test around its stage mark instead of around (0, 0).
    BOUND = 2.0 + abs(float(spec.get("stage_x", 0.0))) + abs(float(spec.get("stage_y", 0.0)))
    ok = True

    # 1. World bounds, all bones all frames.
    bad = []
    for i in range(n):
        for b in names:
            p = w(i, b)
            if abs(p[0]) > BOUND or abs(p[1]) > BOUND or not (-0.5 < p[2] < 3):
                bad.append((i + 1, b, p.round(2).tolist()))
    print(f"[{'OK' if not bad else 'FAIL'}] world bounds: {len(bad)} violations" + (f" e.g. {bad[:3]}" if bad else ""))
    ok &= not bad

    # 2. Hip Z continuity.
    hz = np.array([w(i, "mixamorig:Hips")[2] for i in range(n)])
    steps = np.abs(np.diff(hz))
    print(f"[{'OK' if steps.max() < 0.12 else 'FAIL'}] hip Z step: max {steps.max():.3f} m @ f{steps.argmax()+2} (limit 0.12) | range {hz.min():.3f}-{hz.max():.3f}")
    ok &= steps.max() < 0.12

    # 3. In-place root.
    hx = np.array([w(i, "mixamorig:Hips")[0] for i in range(n)])
    hy = np.array([w(i, "mixamorig:Hips")[1] for i in range(n)])
    travel = float(np.hypot(hx[-1] - hx[0], hy[-1] - hy[0]))
    drift = float(max(np.ptp(hx), np.ptp(hy)))
    if spec.get("root_motion"):
        # This clip is meant to travel (two fighters closing distance);
        # what would be a defect in a solo in-place clip is the point here.
        print(f"[OK] root motion: end-to-end hip travel {travel:.4f} m, range {drift:.3f} m "
              f"(stage x {spec.get('stage_x', 0.0):+.2f})")
    else:
        print(f"[{'OK' if travel < 0.05 else 'WARN'}] in-place: end-to-end hip XZ travel {travel:.4f} m, max drift {drift:.3f} m")

    # 4. Support feet during plant windows.
    for wnd in spec.get("plant", []):
        a = int(round((wnd["src"][0] - 1) * dst_fps / src_fps + 1))
        b = int(round((wnd["src"][1] - 1) * dst_fps / src_fps + 1))
        b = min(b, n)
        sup = wnd["support"]
        if sup == "none":
            zs = np.array([[w(i, "mixamorig:LeftFoot")[2], w(i, "mixamorig:RightFoot")[2]]
                           for i in range(a - 1, b)])
            clear = zs.min(axis=1).max()  # highest point of the lower foot
            tag = "OK" if clear > 0.12 else "WARN"
            print(f"[{tag}] airborne dest {a}-{b}: lower-foot peak clearance {clear:.3f} m (want > 0.12)")
            continue
        feet = ["mixamorig:LeftFoot", "mixamorig:RightFoot"] if sup == "both" else (
            ["mixamorig:LeftFoot"] if sup == "left" else ["mixamorig:RightFoot"])
        for foot in feet:
            zs = np.array([w(i, foot)[2] for i in range(a - 1, b)])
            xy = np.array([w(i, foot)[:2] for i in range(a - 1, b)])
            # For "both", each foot may legitimately step; judge height only.
            zerr = np.abs(zs - GROUND_Z).max()
            skate = float(np.ptp(xy, axis=0).max()) if sup != "both" else float("nan")
            tag = "OK" if zerr < 0.06 else "WARN"
            extra = "" if sup == "both" else f", XZ wander {skate:.3f} m"
            print(f"[{tag}] plant {sup:5s} dest {a}-{b} {foot.split(':')[1]}: |z-{GROUND_Z}| max {zerr:.3f}{extra}")

    # 5. End pose == rest.
    lh, rh = w(n - 1, "mixamorig:LeftHand"), w(n - 1, "mixamorig:RightHand")
    # Hips-relative: with root motion the character ends the clip on a
    # different spot on the floor, and an absolute comparison would call
    # a perfect T-pose a failure.
    hip_end = w(n - 1, "mixamorig:Hips")
    lh = lh - np.array([hip_end[0], hip_end[1], 0.0])
    rh = rh - np.array([hip_end[0], hip_end[1], 0.0])
    rest_err = max(abs(lh[0] - REST_LW[0]) + abs(lh[2] - REST_LW[1]), abs(rh[0] - REST_RW[0]) + abs(rh[2] - REST_RW[1]))
    print(f"[{'OK' if rest_err < 0.03 else 'FAIL'}] end frame is rest: hand error {rest_err:.4f} (hips z {hz[-1]:.4f})")
    ok &= rest_err < 0.03

    # 6. Fast-bone pops: largest single-frame world jump per key bone.
    for b in ("mixamorig:LeftHand", "mixamorig:RightHand", "mixamorig:LeftFoot", "mixamorig:RightFoot", "mixamorig:Head"):
        ps = np.array([w(i, b) for i in range(n)])
        d = np.linalg.norm(np.diff(ps, axis=0), axis=1)
        tag = "OK" if d.max() < 0.30 else "WARN"
        print(f"[{tag}] max frame-jump {b.split(':')[1]}: {d.max():.3f} m @ f{int(d.argmax()) + 2}")

    # 7. Gaze. Where a character looks is invisible to every positional
    #    check above: a head rigidly following its chest, or staring at
    #    the sky, passes them all.
    if "face_dir" in frames[0]:
        face = np.array([f["face_dir"] for f in frames], dtype=float)
        body = np.array([f["body_forward"] for f in frames], dtype=float)
        fy = np.degrees(np.arctan2(face[:, 0], -face[:, 1]))
        by = np.degrees(np.arctan2(body[:, 0], -body[:, 1]))
        elev = np.degrees(np.arcsin(np.clip(face[:, 2], -1, 1)))
        off = (fy - by + 180.0) % 360.0 - 180.0
        locked = float(np.std(off)) < 2.0
        print(f"[{'WARN' if locked else 'OK'}] gaze vs chest: mean {off.mean():+.1f} deg, std {off.std():.1f}"
              + (" — head appears LOCKED to the chest (no independent head motion;"
                 " is the estimator emitting a gaze?)" if locked else ""))
        print(f"[{'WARN' if elev.mean() > 12 else 'OK'}] gaze elevation: mean {elev.mean():+.1f} deg,"
              f" max |elev| {float(np.abs(elev).max()):.1f}"
              + ("  (+ = looking up)" if elev.mean() > 12 else ""))
    else:
        print("[WARN] gaze: curves.json has no face_dir — re-dump curves to enable gaze checks")

    print("\nverdict:", "PASS (visual review still required)" if ok else "HARD FAILURES — fix before showing")


if __name__ == "__main__":
    main()
