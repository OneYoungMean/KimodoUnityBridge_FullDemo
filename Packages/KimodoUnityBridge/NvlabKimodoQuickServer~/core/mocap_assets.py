from __future__ import annotations

from pathlib import Path
import shutil
import subprocess
import urllib.request
import zipfile


GVHMR_REPOSITORY = "https://github.com/zju3dv/GVHMR/archive/refs/heads/main.zip"
BUNDLED_GVHMR_ROOT = Path(__file__).resolve().parents[1] / "vendor" / "GVHMR"
BUNDLED_SMPLX_LITE = Path(__file__).resolve().parent / "assets" / "SMPLX_NEUTRAL_LITE_V437.npz"
CHECKPOINT_REPOSITORIES = ("camenduru/GVHMR", "ryanrudes/gvhmr")
CHECKPOINTS = {
    "gvhmr/gvhmr_siga24_release.ckpt": "gvhmr/gvhmr_siga24_release.ckpt",
    "yolo/yolov8x.pt": "yolo/yolov8x.pt",
    "vitpose/vitpose-h-multi-coco.pth": "vitpose/vitpose-h-multi-coco.pth",
    "hmr2/epoch=10-step=25000.ckpt": "hmr2/epoch=10-step=25000.ckpt",
}


def ensure_gvhmr_assets(gvhmr_root: Path) -> None:
    if not (gvhmr_root / "hmr4d" / "model" / "gvhmr" / "gvhmr_pl_demo.py").is_file():
        if (BUNDLED_GVHMR_ROOT / "hmr4d" / "model" / "gvhmr" / "gvhmr_pl_demo.py").is_file():
            shutil.copytree(
                BUNDLED_GVHMR_ROOT,
                gvhmr_root,
                dirs_exist_ok=True,
                ignore=shutil.ignore_patterns(".git", "__pycache__", "*.pyc", "inputs", "outputs"),
            )
        else:
            _download_repository(gvhmr_root)

    target_smplx_lite = (
        gvhmr_root / "inputs" / "checkpoints" / "body_models" / "smplx" / "SMPLX_NEUTRAL_LITE_V437.npz"
    )
    if not target_smplx_lite.is_file() and BUNDLED_SMPLX_LITE.is_file():
        target_smplx_lite.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(BUNDLED_SMPLX_LITE, target_smplx_lite)

    try:
        from huggingface_hub import hf_hub_download
    except ImportError as exc:
        raise RuntimeError("The QuickServer environment is missing huggingface_hub.") from exc

    checkpoint_root = gvhmr_root / "inputs" / "checkpoints"
    for filename, relative_path in CHECKPOINTS.items():
        target = checkpoint_root / relative_path
        if not target.is_file():
            errors = []
            for repository in CHECKPOINT_REPOSITORIES:
                try:
                    hf_hub_download(repo_id=repository, filename=filename, local_dir=checkpoint_root)
                except Exception as exc:
                    errors.append(f"{repository}: {exc}")
                    continue
                if target.is_file():
                    break
            if not target.is_file():
                raise RuntimeError(
                    f"Could not download GVHMR checkpoint {filename} from the public mirrors: "
                    + "; ".join(errors)
                )

    smplx_model = checkpoint_root / "body_models" / "smplx" / "SMPLX_NEUTRAL_LITE_V437.npz"
    if not smplx_model.is_file():
        raise RuntimeError(
            f"Bundled lightweight SMPL-X V437 asset was not found: {smplx_model}. "
            "Restore SMPLX_NEUTRAL_LITE_V437.npz beside the GVHMR checkpoints."
        )


def _download_repository(gvhmmr_root: Path) -> None:
    archive_path = gvhmmr_root.parent / "GVHMR-main.zip"
    gvhmmr_root.parent.mkdir(parents=True, exist_ok=True)
    git = shutil.which("git")
    if git:
        subprocess.run(
            [git, "clone", "--depth", "1", "https://github.com/zju3dv/GVHMR.git", str(gvhmmr_root)],
            check=True,
            capture_output=True,
            text=True,
            timeout=600,
        )
        return

    if not zipfile.is_zipfile(archive_path):
        request = urllib.request.Request(GVHMR_REPOSITORY, headers={"User-Agent": "KimodoQuickServer"})
        with urllib.request.urlopen(request, timeout=60) as response, archive_path.open("wb") as output:
            while chunk := response.read(1024 * 1024):
                output.write(chunk)
    if not zipfile.is_zipfile(archive_path):
        raise RuntimeError(
            f"GVHMR source archive download was incomplete: {archive_path}. "
            "Install Git or retry with a network route that allows complete GitHub downloads."
        )

    with zipfile.ZipFile(archive_path) as archive:
        members = archive.infolist()
        root_name = "GVHMR-main/"
        for member in members:
            relative = member.filename.removeprefix(root_name)
            if not relative or relative == member.filename:
                continue
            destination = (gvhmmr_root / relative).resolve()
            if not destination.is_relative_to(gvhmmr_root.resolve()):
                raise RuntimeError("GVHMR source archive contains an unsafe path.")
            if member.is_dir():
                destination.mkdir(parents=True, exist_ok=True)
            else:
                destination.parent.mkdir(parents=True, exist_ok=True)
                with archive.open(member) as source, destination.open("wb") as output:
                    while chunk := source.read(1024 * 1024):
                        output.write(chunk)
