"""Review videos for an SMPL-X clip — rendered headlessly, no Blender UI or MCP.

  python pipeline/render_review.py --spec action_specs/<motion>.json --clip clips/<clip> \
         [--legacy clips/<legacy_clip>] [--scene ybot_rest.blend] [--views front,three_quarter]

Writes into the clip folder, per view (front, three_quarter):
  review_<view>.mp4   a 2x2 grid, time-aligned and labelled:
                        SOURCE plate          | OVERLAY
                        BEFORE (legacy clip)  | AFTER (this clip)
                      OVERLAY is this clip rendered through the PLATE'S OWN
                      CAMERA (recovered by retarget_smplx.py) and composited
                      over the video: where the character and the performer
                      disagree, you see it. Without --legacy the bottom row
                      shows this clip from two angles.
  review_details.mp4  the hands and the feet, zoomed: for each, the plate
                      and the overlay side by side, following the wrist or
                      the ankle. On a full-body plate a hand is 30-50 px;
                      this is where fingers, fists and foot placement can
                      be judged. The plate camera's view is rendered at
                      twice the plate's resolution for it.
Frames are rendered with bl_motion.py in background Blender and removed
afterwards (--keep-frames to keep them). The legacy clip, if given, is
re-keyed from its curves.json onto the same rig and rendered with the
same camera and look, so the two differ by their motion only.
"""

from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
from pathlib import Path

import av
import cv2
import numpy as np

REPO = Path(__file__).resolve().parents[1]
BLENDER_CANDIDATES = [
    os.environ.get("BLENDER", ""),
    r"C:\Program Files\Blender Foundation\Blender 5.1\blender.exe",
    r"C:\Program Files\Blender Foundation\Blender 5.2\blender.exe",
    "/Applications/Blender.app/Contents/MacOS/Blender",
    "blender",
]


def rpath(p) -> Path:
    p = Path(p)
    return p if p.is_absolute() else (REPO / p)


def blender_exe(arg=None) -> str:
    for c in ([arg] if arg else []) + BLENDER_CANDIDATES:
        if c and (Path(c).exists() or shutil.which(c)):
            return c
    raise SystemExit("Blender not found — pass --blender or set BLENDER")


def render(blender, scene, src_flag, src_path, view, out_dir, extra=()):
    out_dir.mkdir(parents=True, exist_ok=True)
    cmd = [blender, "-b", str(scene), "--python", str(REPO / "pipeline" / "bl_motion.py"), "--",
           "render", src_flag, str(src_path), "--view", view, "--out", str(out_dir), *extra]
    res = subprocess.run(cmd, capture_output=True, text=True)
    if res.returncode != 0 or not any(out_dir.glob("f*.png")):
        sys.stderr.write(res.stdout[-3000:] + res.stderr[-3000:])
        raise SystemExit(f"render failed: {view} {src_path}")
    return sorted(out_dir.glob("f*.png"))


def read_plate(video: Path):
    cap = cv2.VideoCapture(str(video))
    fps = cap.get(cv2.CAP_PROP_FPS) or 24.0
    frames = []
    while True:
        ok, im = cap.read()
        if not ok:
            break
        frames.append(im)
    cap.release()
    return frames, fps


def label(img, text, sub=None):
    """Title (+ subtitle) on a translucent dark band, legible on any frame."""
    (tw, _), _ = cv2.getTextSize(text, cv2.FONT_HERSHEY_SIMPLEX, 0.75, 2)
    sw = cv2.getTextSize(sub, cv2.FONT_HERSHEY_SIMPLEX, 0.5, 1)[0][0] if sub else 0
    x1, y1 = 10 + max(tw, sw) + 20, (68 if sub else 46)
    band = img[8:y1, 8:x1].astype(np.float32)
    img[8:y1, 8:x1] = (band * 0.35 + np.array([28, 24, 22], np.float32) * 0.65).astype(np.uint8)
    cv2.putText(img, text, (18, 34), cv2.FONT_HERSHEY_SIMPLEX, 0.75, (255, 255, 255), 2, cv2.LINE_AA)
    if sub:
        cv2.putText(img, sub, (18, 58), cv2.FONT_HERSHEY_SIMPLEX, 0.5, (215, 215, 215), 1, cv2.LINE_AA)
    return img


def fit_h(img, h):
    return cv2.resize(img, (int(round(img.shape[1] * h / img.shape[0])) // 2 * 2, h),
                      interpolation=cv2.INTER_AREA)


def encode(path: Path, frames_iter, fps: int, crf: int = 26):
    first = next(frames_iter)
    h, w = first.shape[:2]
    container = av.open(str(path), "w")
    stream = container.add_stream("libx264", rate=fps)
    stream.width, stream.height = w, h
    stream.pix_fmt = "yuv420p"
    stream.options = {"crf": str(crf), "preset": "slow", "movflags": "+faststart"}

    def put(img):
        fr = av.VideoFrame.from_ndarray(cv2.cvtColor(img, cv2.COLOR_BGR2RGB), format="rgb24")
        for pkt in stream.encode(fr):
            container.mux(pkt)

    put(first)
    for img in frames_iter:
        put(img)
    for pkt in stream.encode():
        container.mux(pkt)
    container.close()


def part_tracks(kp2d: np.ndarray, min_conf: float = 0.3):
    """Crop boxes for the detail video, per source frame: (T, 4, 3) centre
    x, y and half-size in plate pixels for left hand, right hand, left
    foot, right foot. Sizes are fixed per clip (from the usual forearm and
    shin lengths on screen), centres follow the wrist (a third of a
    forearm beyond it) and the ankle (a little below it), smoothed so the
    view does not shake with the detector."""
    T = kp2d.shape[0]
    out = np.zeros((T, 4, 3))

    def usual(a, b):
        ok = (kp2d[:, a, 2] >= min_conf) & (kp2d[:, b, 2] >= min_conf)
        d = np.linalg.norm(kp2d[:, a, :2] - kp2d[:, b, :2], axis=1)
        return float(np.percentile(d[ok], 75)) if ok.sum() > 3 else 80.0

    def track(x, conf):
        t = np.arange(T)
        good = conf >= min_conf
        if good.sum() < 2:
            return np.full(T, np.nanmean(x) if np.isfinite(x).any() else 0.0)
        y = np.interp(t, t[good], x[good])
        k = np.exp(-0.5 * (np.arange(-6, 7) / 2.0) ** 2)
        return np.convolve(np.pad(y, 6, mode="edge"), k / k.sum(), mode="valid")

    for k, (w, e) in enumerate(((9, 7), (10, 8))):
        fore = usual(w, e)
        d = kp2d[:, w, :2] - kp2d[:, e, :2]
        c = kp2d[:, w, :2] + d / np.maximum(np.linalg.norm(d, axis=1, keepdims=True), 1e-6) * fore / 3.0
        conf = np.minimum(kp2d[:, w, 2], kp2d[:, e, 2])
        out[:, k, 0], out[:, k, 1], out[:, k, 2] = track(c[:, 0], conf), track(c[:, 1], conf), max(0.55 * fore, 30.0)
    for k, (a, kn) in enumerate(((15, 13), (16, 14))):
        shin = usual(a, kn)
        conf = kp2d[:, a, 2]
        out[:, 2 + k, 0] = track(kp2d[:, a, 0], conf)
        out[:, 2 + k, 1] = track(kp2d[:, a, 1] + 0.12 * shin, conf)
        out[:, 2 + k, 2] = max(0.42 * shin, 36.0)
    return out


def tag(img, text):
    """A small caption at the bottom-left: the zoomed tiles are too small
    for the grid's title bands."""
    (tw, th), _ = cv2.getTextSize(text, cv2.FONT_HERSHEY_SIMPLEX, 0.45, 1)
    h = img.shape[0]
    band = img[h - th - 14:h - 4, 4:tw + 16].astype(np.float32)
    img[h - th - 14:h - 4, 4:tw + 16] = (band * 0.35 + np.array([28, 24, 22], np.float32) * 0.65).astype(np.uint8)
    cv2.putText(img, text, (10, h - 10), cv2.FONT_HERSHEY_SIMPLEX, 0.45, (255, 255, 255), 1, cv2.LINE_AA)
    return img


def zoom(img, cx, cy, half, size, scale=1.0):
    """Square crop of `img` (which is `scale` times the plate's resolution)
    around a plate-pixel box, resized to size x size; edge pixels pad it
    where it leaves the frame."""
    M = np.array([[size / (2 * half * scale), 0, -(cx - half) * size / (2 * half)],
                  [0, size / (2 * half * scale), -(cy - half) * size / (2 * half)]])
    return cv2.warpAffine(img, M, (size, size), flags=cv2.INTER_AREA if scale > 1 else cv2.INTER_CUBIC,
                          borderMode=cv2.BORDER_REPLICATE)


def main():
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--spec", required=True)
    ap.add_argument("--clip", required=True, help="clip dir holding motion.npz")
    ap.add_argument("--legacy", help="legacy clip dir (curves.json) to show next to it")
    ap.add_argument("--scene", default="ybot_rest.blend", help="the rig's rest scene (.blend)")
    ap.add_argument("--video", help="source plate (default: first .mp4 next to the spec's landmarks)")
    ap.add_argument("--views", default="front,three_quarter")
    ap.add_argument("--blender")
    ap.add_argument("--keep-frames", action="store_true")
    ap.add_argument("--reuse-frames", action="store_true", help="skip rendering where frames exist")
    ap.add_argument("--no-details", action="store_true", help="skip review_details.mp4")
    args = ap.parse_args()

    spec = json.loads(rpath(args.spec).read_text(encoding="utf-8"))
    clip = rpath(args.clip)
    motion = clip / "motion.npz"
    if not motion.exists():
        raise SystemExit(f"{motion} missing — run retarget_smplx.py first")
    video = rpath(args.video) if args.video else sorted(rpath(spec["landmarks"]).parent.glob("*.mp4"))[0]
    blender = blender_exe(args.blender)
    scene = rpath(args.scene)
    plate, plate_fps = read_plate(video)
    fps = int(round(float(np.load(motion)["fps"])))
    work = clip / "_frames"
    views = [v for v in args.views.split(",") if v]

    def frames_of(flag, path, view, name):
        d = work / name
        if args.reuse_frames and any(d.glob("f*.png")):
            return sorted(d.glob("f*.png"))
        shutil.rmtree(d, ignore_errors=True)
        return render(blender, scene, flag, path, view, d)

    details = not args.no_details
    cam_scale = 2 if details else 1

    def frames_cam(name):
        d = work / name
        if args.reuse_frames and any(d.glob("f*.png")):
            return sorted(d.glob("f*.png"))
        shutil.rmtree(d, ignore_errors=True)
        return render(blender, scene, "--motion", motion, "camera", d, ("--scale", str(cam_scale)))

    cam = frames_cam("new_camera")
    new = {v: frames_of("--motion", motion, v, f"new_{v}") for v in views}
    old = ({v: frames_of("--curves", rpath(args.legacy) / "curves.json", v, f"old_{v}") for v in views}
           if args.legacy else None)
    W, H = 640, 360                                   # one cell of the 2x2 grid

    def plate_at(i):
        return plate[min(len(plate) - 1, int(round(i / fps * plate_fps)))]

    def cell(img):
        return cv2.resize(img, (W, H), interpolation=cv2.INTER_AREA)

    def overlay(i):
        src = plate_at(i).astype(np.float32)
        rgba = cv2.imread(str(cam[min(i, len(cam) - 1)]), cv2.IMREAD_UNCHANGED).astype(np.float32)
        if rgba.shape[:2] != src.shape[:2]:
            rgba = cv2.resize(rgba, (src.shape[1], src.shape[0]))
        a = rgba[..., 3:4] / 255.0 * 0.62
        return (src * (1 - a) + rgba[..., :3] * a).astype(np.uint8)

    def grid(view):
        for i in range(len(cam)):
            tl = label(cell(plate_at(i)), "SOURCE VIDEO")
            tr = label(cell(overlay(i)), "OVERLAY", "new retarget seen by the plate's camera")
            if old is not None:
                bl = label(cell(cv2.imread(str(old[view][min(i, len(old[view]) - 1)]))),
                           "BEFORE", "landmark lift + FK aim")
                br = label(cell(cv2.imread(str(new[view][min(i, len(new[view]) - 1)]))),
                           "AFTER", "SMPL-X rotation retarget")
            else:
                other = [v for v in views if v != view] or [view]
                bl = label(cell(cv2.imread(str(new[view][min(i, len(new[view]) - 1)]))),
                           "AFTER", view.replace("_", "-"))
                br = label(cell(cv2.imread(str(new[other[0]][min(i, len(new[other[0]]) - 1)]))),
                           "AFTER", other[0].replace("_", "-"))
            g = np.vstack([np.hstack([tl, tr]), np.hstack([bl, br])])
            g[H - 1:H + 1, :] = 24
            g[:, W - 1:W + 1] = 24
            yield g

    for view in views:
        out = clip / f"review_{view}.mp4"
        encode(out, grid(view), fps, crf=27)
        print("wrote", out)
        if old is None:
            break                                     # one grid already shows both views

    if details:
        smplx = rpath(spec.get("smplx") or (rpath(spec["landmarks"]).parent / "smplx.npz"))
        kp2d = np.load(smplx)["kp2d"]
        boxes = part_tracks(kp2d)
        Z = 300                                       # one zoomed square
        names = ("LEFT HAND", "RIGHT HAND", "LEFT FOOT", "RIGHT FOOT")

        def detail(i):
            j = min(kp2d.shape[0] - 1, int(round(i / fps * plate_fps)))
            src = plate_at(i)
            big = cv2.resize(src, None, fx=cam_scale, fy=cam_scale, interpolation=cv2.INTER_CUBIC).astype(np.float32)
            rgba = cv2.imread(str(cam[min(i, len(cam) - 1)]), cv2.IMREAD_UNCHANGED).astype(np.float32)
            if rgba.shape[:2] != big.shape[:2]:
                rgba = cv2.resize(rgba, (big.shape[1], big.shape[0]))
            a = rgba[..., 3:4] / 255.0 * 0.62
            over = (big * (1 - a) + rgba[..., :3] * a).astype(np.uint8)
            tiles = []
            for k in range(4):
                cx, cy, half = boxes[j, k]
                pl = tag(zoom(src, cx, cy, half, Z), f"{names[k]} - video")
                ov = tag(zoom(over, cx, cy, half, Z, cam_scale), f"{names[k]} - retarget over the video")
                t = np.hstack([pl, ov])
                t[:, Z - 1:Z + 1] = 24
                tiles.append(t)
            g = np.vstack([np.hstack([tiles[0], tiles[1]]), np.hstack([tiles[2], tiles[3]])])
            g[Z - 2:Z + 2, :] = 24
            g[:, 2 * Z - 2:2 * Z + 2] = 24
            return g

        out = clip / "review_details.mp4"
        encode(out, (detail(i) for i in range(len(cam))), fps, crf=25)
        print("wrote", out)

    if not args.keep_frames:
        shutil.rmtree(work, ignore_errors=True)


if __name__ == "__main__":
    main()
