# Install

Everything the pipeline needs, from a bare Windows machine to a first
retarget. Developed and validated on: Windows 10, RTX 4080 (16 GB),
Blender 5.1.2, Python 3.10 (GVHMR venv via `uv`). Linux works with the
same steps minus the Windows-specific wheel notes.

## 0. Prerequisites

| Thing | Version | Notes |
|---|---|---|
| NVIDIA GPU + driver | ~8 GB VRAM min | CUDA 12.1 wheels are used |
| Blender | **5.1+** | the Blender MCP add-on requires it |
| Python | any (for `uv`) | the GVHMR venv is created as 3.10 |
| [uv](https://docs.astral.sh/uv/) | recent | manages the 3.10 venv painlessly |
| git, ffmpeg | recent | |

## 1. Mixamo character (Adobe — bring your own)

Any Mixamo character with the standard `mixamorig` skeleton works;
Y Bot is the reference the pipeline was developed on.

1. Log in at https://www.mixamo.com → Characters → pick a character.
2. Download: Format **FBX Binary**, Pose **T-pose** (with skin).
3. Save as `<repo>\ybot.fbx` (any name — pass it via `--fbx`).
4. Build + validate the working scene — this also writes
   `rig_profile.json`, the measured proportions every later stage uses:

```
"C:\Program Files\Blender Foundation\Blender 5.1\blender.exe" ^
    --background --python pipeline\setup_rig.py -- --fbx ybot.fbx --out ybot_rest.blend
```

Expected output ends with `rig_profile.json written — hip ... ground ...`
and `saved ... bones ... 30 fps`. If validation fails, the FBX is not a
plain Mixamo T-pose character download.

## 2. Blender MCP (official, Blender Lab)

The FK apply runs inside a live Blender through the official add-on's
bridge socket (`localhost:9876`).

1. In Blender: **Edit → Preferences → System → Network → Allow Online
   Access** (off by default — the server refuses to start without it).
2. Install the add-on from https://www.blender.org/lab/mcp-server/ —
   drag the install link into Blender **twice** (first adds the Blender
   Lab extension repository, second installs), or download the zip and
   use *Install from Disk*.
3. The server autostarts ~1 s after Blender launches (check
   Preferences → Add-ons → MCP: "Server is running").

For an **agent driving Blender**: either register the MCP server with
your client —

```
claude mcp add blender -s user -- uvx --from "git+https://projects.blender.org/lab/blender_mcp.git#subdirectory=mcp" --with "mcp[cli]<2" blender-mcp
```

(the `mcp[cli]<2` pin matters: the 2.x Python SDK removed
`mcp.server.fastmcp`, which the server imports) — or skip MCP entirely
and use `pipeline\blender_exec.py <script.py> [timeout]`, which speaks
the add-on's socket protocol directly.

## 2b. MediaPipe (foot keypoints for the SMPL-X path)

`detect_feet.py` and `detect_hands.py` need `mediapipe` and
`opencv-python` in any Python on the machine (not the GVHMR venv):
`pip install mediapipe opencv-python`. The Pose Landmarker model
(`pose_landmarker_heavy.task`, ~30 MB) and the Hand Landmarker model
(`hand_landmarker.task`, ~8 MB) are downloaded to `tools/models/` on
first use. Without MediaPipe the estimator still runs; the refinement
then fits no heel/toe points and the fingers keep a relaxed curl (fists
inside the spec's `fists` windows).

## 3. GVHMR (the estimator)

```
git clone https://github.com/zju3dv/GVHMR tools/GVHMR
uv python install 3.10
uv venv --python 3.10 tools/GVHMR/.venv
uv pip install --python tools/GVHMR/.venv/Scripts/python.exe -r docs/requirements_gvhmr_windows.txt
uv pip install --python tools/GVHMR/.venv/Scripts/python.exe --no-deps -e tools/GVHMR
uv pip install --python tools/GVHMR/.venv/Scripts/python.exe yacs
```

`docs/requirements_gvhmr_windows.txt` is GVHMR's requirements with
Windows fixes baked in (pinned `pytorch3d` Linux wheel dropped,
`chumpy`/`cython_bbox` dropped — see the file header). On Linux you can
use GVHMR's own `requirements.txt` unchanged.

**pytorch3d on Windows** (needed by GVHMR's core, not just its
renderer): install a community wheel matching
torch 2.3.0 + cu121 + cp310 + win_amd64 from
https://miropsota.github.io/torch_packages_builder/pytorch3d/ :

```
uv pip install --python tools/GVHMR/.venv/Scripts/python.exe --no-deps "https://github.com/MiroPsota/torch_packages_builder/releases/download/pytorch3d-0.7.6/pytorch3d-0.7.6%2Bpt2.3.0cu121-cp310-cp310-win_amd64.whl"
```

**Checkpoints** (~5.3 GB). Google Drive blocks scripted downloads;
the HuggingFace mirror works:

```
BASE=https://huggingface.co/camenduru/GVHMR/resolve/main
CK=tools/GVHMR/inputs/checkpoints
curl -L $BASE/gvhmr/gvhmr_siga24_release.ckpt   -o $CK/gvhmr/gvhmr_siga24_release.ckpt --create-dirs
curl -L $BASE/yolo/yolov8x.pt                   -o $CK/yolo/yolov8x.pt --create-dirs
curl -L $BASE/vitpose/vitpose-h-multi-coco.pth  -o $CK/vitpose/vitpose-h-multi-coco.pth --create-dirs
curl -L "$BASE/hmr2/epoch%3D10-step%3D25000.ckpt" -o "$CK/hmr2/epoch=10-step=25000.ckpt" --create-dirs
```

(dpvo.pth is only needed for moving cameras; plates are locked.)

**SMPL-X body model** (license-gated, free registration): register at
https://smpl-x.is.tue.mpg.de/ , download **SMPL-X v1.1 (NPZ+PKL)** and
copy `SMPLX_NEUTRAL.npz` to
`tools/GVHMR/inputs/checkpoints/body_models/smplx/`. GVHMR's network
asserts on this file at startup — nothing runs without it.

**Smoke test** (must print your GPU and no tracebacks):

```
tools/GVHMR/.venv/Scripts/python.exe -c "import torch; print(torch.cuda.get_device_name(0)); from hmr4d.utils.smplx_utils import make_smplx; from hmr4d.model.gvhmr.gvhmr_pl_demo import DemoPL; from hmr4d.utils.preproc import Tracker; print('GVHMR import chain OK')"
```

If a different GVHMR root is preferred, set the env var `GVHMR_ROOT`.

## 4. Verify end-to-end

With Blender open on `ybot_rest.blend`, run your first plate through
the five stages (README quickstart step 3 / docs/PIPELINE.md). Sanity
reference values from the author's accepted clips:

- estimator bind wrist span on a T-pose: ~1.25–1.44 m
- QA gate: 0 world-bound violations, max hip Z step well under
  0.12 m/frame (accepted clips: 0.026–0.061), single-support foot
  skate 0.000 m, end-frame rest hand error ~0.001 m, verdict PASS.
