#!/usr/bin/env python
"""
Kimodo Unity Bridge Server

Persistent process for Unity Editor:
- Loads kimodo model once
- Listens on TCP socket for newline-delimited JSON requests
- Responds with newline-delimited JSON over the same TCP connection
"""

import argparse
import json
import os
from pathlib import Path
import socket
import sys
import threading
import time
import traceback
from dataclasses import dataclass
from typing import Any

import numpy as np

from kimodo.bridge import quickserver_assets as assets


def _default_bridge_log_path(root: str) -> str:
    if not root:
        return ""
    return os.path.join(root, "log", "bridge_server.log")


def _detect_total_vram_gb() -> float:
    """Total VRAM of cuda:0 in GiB, or 0.0 when no usable CUDA device exists."""
    try:
        import torch

        if torch.cuda.is_available() and torch.cuda.device_count() > 0:
            props = torch.cuda.get_device_properties(0)
            return float(props.total_memory) / (1024 ** 3)
    except Exception:
        pass
    return 0.0


def _write_text_atomic(path: str, content: str) -> None:
    dir_path = os.path.dirname(path)
    if dir_path:
        os.makedirs(dir_path, exist_ok=True)
    tmp_path = f"{path}.tmp.{os.getpid()}"
    with open(tmp_path, "w", encoding="utf-8", newline="\n") as f:
        f.write(content)
        f.flush()
        os.fsync(f.fileno())
    for _ in range(30):
        try:
            os.replace(tmp_path, path)
            return
        except PermissionError:
            time.sleep(0.05)
    # Fallback for Windows file sharing edge-cases: overwrite directly.
    for _ in range(30):
        try:
            with open(path, "w", encoding="utf-8", newline="\n") as f:
                f.write(content)
                f.flush()
                os.fsync(f.fileno())
            try:
                os.remove(tmp_path)
            except Exception:
                pass
            return
        except PermissionError:
            time.sleep(0.05)
    raise PermissionError(f"Failed to write file after retries: {path}")


def _out(obj):
    sys.stdout.write(json.dumps(obj) + "\n")
    sys.stdout.flush()


def _log(msg: str):
    line = str(msg)
    log_path = os.environ.get("KIMODO_BRIDGE_LOG", "")
    direct_only = os.environ.get("KIMODO_BRIDGE_LOG_DIRECT_ONLY", "").strip() == "1"
    sys.stderr.write(line + "\n")
    sys.stderr.flush()
    if direct_only:
        return
    if not log_path:
        root = os.environ.get("KIMODO_ROOT_PATH", "")
        if root:
            log_path = _default_bridge_log_path(root)
    if not log_path:
        return
    try:
        log_dir = os.path.dirname(log_path)
        if log_dir:
            os.makedirs(log_dir, exist_ok=True)
        with open(log_path, "a", encoding="utf-8") as f:
            f.write(line + "\n")
    except Exception:
        pass


class _BridgeAssetLogger:
    def log(self, message: str) -> None:
        _log(message)


@dataclass(frozen=True)
class _BridgeProvisionPlan:
    resolved_model: assets.ResolvedModel
    models_root: Path
    using_external_models: bool
    highvram: bool
    encoder_route: str


def _env_flag(name: str, default: bool = False) -> bool:
    raw = os.environ.get(name, "").strip().lower()
    if not raw:
        return bool(default)
    return raw not in ("0", "false", "no", "off")


def _ensure_asset_ready(
    asset: assets.AssetSpec,
    target_dir: Path,
    logger: _BridgeAssetLogger,
    recovery_flag_dir: Path,
    download_counter: list[int],
    *,
    allow_download: bool,
) -> None:
    if assets.asset_is_ready(asset, target_dir):
        logger.log(f"[SKIP] {asset.label} ready: {target_dir}")
        return
    if target_dir.exists():
        logger.log(f"[INFO] {asset.label} exists but looks incomplete, refreshing: {target_dir}")
    assets.ensure_asset_present(
        asset,
        target_dir,
        logger,
        recovery_flag_dir,
        download_counter,
        allow_download=allow_download,
    )


def _build_bridge_provision_plan(
    kimodo_root: str,
    requested_model: str,
    *,
    run_device: str | None,
    total_vram_gb: float,
) -> _BridgeProvisionPlan:
    root_path = Path(kimodo_root).resolve()
    resolved_model = assets.resolve_main_model(requested_model)
    models_root, using_external_models = assets.resolve_models_root(
        root_path,
        os.environ.get("KIMODO_MODELS_ROOT"),
    )
    highvram = _env_flag("KIMODO_HIGHVRAM", False)
    hints = assets.normalize_runtime_hints(run_device)
    encoder_route = assets.choose_prepare_encoder_route(
        highvram,
        hints,
        total_vram_gb=total_vram_gb,
    )
    return _BridgeProvisionPlan(
        resolved_model=resolved_model,
        models_root=models_root,
        using_external_models=using_external_models,
        highvram=highvram,
        encoder_route=encoder_route,
    )


def _apply_bridge_runtime_env(kimodo_root: str, plan: _BridgeProvisionPlan) -> None:
    root_path = Path(kimodo_root).resolve()
    source_root = root_path / "kimodo"
    if not (source_root / "pyproject.toml").is_file():
        source_root = root_path
    runtime_env = assets.build_runtime_env(
        root_dir=root_path,
        source_root=source_root,
        models_root=plan.models_root,
        highvram=plan.highvram,
        hints=assets.normalize_runtime_hints("cpu" if plan.encoder_route == "int8" else None),
        encoder_route=plan.encoder_route,
    )
    assets.scrub_removed_runtime_env(os.environ)
    os.environ.update(runtime_env)


def _provision_bridge_assets(
    kimodo_root: str,
    requested_model: str,
    *,
    run_device: str | None,
    total_vram_gb: float,
) -> _BridgeProvisionPlan:
    plan = _build_bridge_provision_plan(
        kimodo_root,
        requested_model,
        run_device=run_device,
        total_vram_gb=total_vram_gb,
    )
    logger = _BridgeAssetLogger()
    recovery_flag_dir = Path(kimodo_root).resolve() / "archive" / "recovery_flags"
    recycle_dir = Path(kimodo_root).resolve() / "archive" / "recycle"
    allow_download = not plan.using_external_models
    download_counter = [0]

    _apply_bridge_runtime_env(kimodo_root, plan)
    if allow_download:
        plan.models_root.mkdir(parents=True, exist_ok=True)

    logger.log(
        f"[bridge] asset plan: model={plan.resolved_model.local_name} "
        f"models_root={plan.models_root} encoder_route={plan.encoder_route} "
        f"external_models_root={plan.using_external_models}"
    )

    main_asset = assets.AssetSpec(
        label="main model",
        local_dir_name=plan.resolved_model.local_name,
        modelscope_repo=plan.resolved_model.modelscope_repo,
        huggingface_repo=plan.resolved_model.huggingface_repo,
    )
    main_dir = assets.local_model_dir(plan.models_root, plan.resolved_model)
    _ensure_asset_ready(
        main_asset,
        main_dir,
        logger,
        recovery_flag_dir,
        download_counter,
        allow_download=allow_download,
    )

    if plan.encoder_route == "int8":
        encoder_assets = [assets.INT8_ASSET]
    elif plan.encoder_route == "full":
        encoder_assets = [assets.FULL_BASE_ASSET, assets.FULL_PEFT_ASSET]
    else:
        encoder_assets = [assets.NF4_ASSET]

    for encoder_asset in encoder_assets:
        _ensure_asset_ready(
            encoder_asset,
            plan.models_root / encoder_asset.local_dir_name,
            logger,
            recovery_flag_dir,
            download_counter,
            allow_download=allow_download,
        )

    if assets.should_inject_once(
        recovery_flag_dir,
        "model_missing_after_download",
        "KIMODO_TEST_INJECT_MODEL_MISSING_AFTER_DOWNLOAD_ONCE",
    ):
        logger.log(f"[TEST] Injected model-missing-once by archiving downloaded asset dir: {main_dir}")
        assets.archive_path(main_dir, recycle_dir)

    logger.log(
        f"[bridge] asset plan complete: model={plan.resolved_model.local_name} "
        f"encoder_route={plan.encoder_route} downloads={download_counter[0]}"
    )
    return plan


def _rotation_mats_to_quat_wxyz(rot_mats: np.ndarray) -> np.ndarray:
    m = rot_mats.astype(np.float32, copy=False).reshape(-1, 3, 3)
    q = np.zeros((m.shape[0], 4), dtype=np.float32)

    tr = m[:, 0, 0] + m[:, 1, 1] + m[:, 2, 2]
    mask_t = tr > 0.0
    if np.any(mask_t):
        s = np.sqrt(tr[mask_t] + 1.0) * 2.0
        q[mask_t, 0] = 0.25 * s
        q[mask_t, 1] = (m[mask_t, 2, 1] - m[mask_t, 1, 2]) / s
        q[mask_t, 2] = (m[mask_t, 0, 2] - m[mask_t, 2, 0]) / s
        q[mask_t, 3] = (m[mask_t, 1, 0] - m[mask_t, 0, 1]) / s

    mask_x = (~mask_t) & (m[:, 0, 0] > m[:, 1, 1]) & (m[:, 0, 0] > m[:, 2, 2])
    if np.any(mask_x):
        s = np.sqrt(1.0 + m[mask_x, 0, 0] - m[mask_x, 1, 1] - m[mask_x, 2, 2]) * 2.0
        q[mask_x, 0] = (m[mask_x, 2, 1] - m[mask_x, 1, 2]) / s
        q[mask_x, 1] = 0.25 * s
        q[mask_x, 2] = (m[mask_x, 0, 1] + m[mask_x, 1, 0]) / s
        q[mask_x, 3] = (m[mask_x, 0, 2] + m[mask_x, 2, 0]) / s

    mask_y = (~mask_t) & (~mask_x) & (m[:, 1, 1] > m[:, 2, 2])
    if np.any(mask_y):
        s = np.sqrt(1.0 + m[mask_y, 1, 1] - m[mask_y, 0, 0] - m[mask_y, 2, 2]) * 2.0
        q[mask_y, 0] = (m[mask_y, 0, 2] - m[mask_y, 2, 0]) / s
        q[mask_y, 1] = (m[mask_y, 0, 1] + m[mask_y, 1, 0]) / s
        q[mask_y, 2] = 0.25 * s
        q[mask_y, 3] = (m[mask_y, 1, 2] + m[mask_y, 2, 1]) / s

    mask_z = (~mask_t) & (~mask_x) & (~mask_y)
    if np.any(mask_z):
        s = np.sqrt(1.0 + m[mask_z, 2, 2] - m[mask_z, 0, 0] - m[mask_z, 1, 1]) * 2.0
        q[mask_z, 0] = (m[mask_z, 1, 0] - m[mask_z, 0, 1]) / s
        q[mask_z, 1] = (m[mask_z, 0, 2] + m[mask_z, 2, 0]) / s
        q[mask_z, 2] = (m[mask_z, 1, 2] + m[mask_z, 2, 1]) / s
        q[mask_z, 3] = 0.25 * s

    norm = np.linalg.norm(q, axis=1, keepdims=True)
    norm[norm < 1e-8] = 1.0
    q = q / norm
    return q.reshape(*rot_mats.shape[:-2], 4)


def _resolve_skeleton_for_joint_count(skeleton, num_joints: int):
    if skeleton is None:
        return None
    if hasattr(skeleton, "somaskel77") and int(num_joints) == 77:
        return skeleton.somaskel77
    if hasattr(skeleton, "somaskel30") and int(num_joints) == 30:
        return skeleton.somaskel30
    return skeleton


def _extract_flat_local_rot_quats(model, output, sample_index: int):
    local_rot = None

    if output.get("local_rot_mats") is not None:
        candidate = output["local_rot_mats"][sample_index]
        if candidate is not None:
            local_rot = np.asarray(candidate, dtype=np.float32)

    if local_rot is None and output.get("global_rot_mats") is not None:
        try:
            from kimodo.skeleton import global_rots_to_local_rots
            import torch

            global_rot = np.asarray(output["global_rot_mats"][sample_index], dtype=np.float32)
            num_joints = int(np.asarray(output["posed_joints"]).shape[2])
            skeleton = _resolve_skeleton_for_joint_count(model.skeleton, num_joints)
            if skeleton is not None:
                global_rot_t = torch.from_numpy(global_rot)
                if hasattr(skeleton, "joint_parents") and isinstance(skeleton.joint_parents, torch.Tensor):
                    global_rot_t = global_rot_t.to(skeleton.joint_parents.device)
                local_rot_t = global_rots_to_local_rots(global_rot_t, skeleton)
                local_rot = local_rot_t.detach().cpu().numpy().astype(np.float32, copy=False)
        except Exception as exc:
            _out({"status": "progress", "message": f"rotation fallback failed: {exc}"})

    if local_rot is None:
        return None

    q_wxyz = _rotation_mats_to_quat_wxyz(local_rot)
    return q_wxyz.reshape(-1).tolist()


def _parents_and_names(model, num_joints: int):
    parents = None
    names = None
    skel = _resolve_skeleton_for_joint_count(getattr(model, "skeleton", None), num_joints)
    if skel is not None:
        if hasattr(skel, "joint_parents"):
            try:
                jp = skel.joint_parents
                if hasattr(jp, "detach"):
                    jp = jp.detach().cpu().tolist()
                elif hasattr(jp, "cpu"):
                    jp = jp.cpu().tolist()
                parents = [int(x) for x in jp]
            except Exception:
                parents = None
        if hasattr(skel, "bone_order_names"):
            try:
                names = [str(x) for x in list(skel.bone_order_names)]
            except Exception:
                names = None

    if not parents or len(parents) != num_joints:
        parents = [-1] + [i for i in range(num_joints - 1)]
    if not names or len(names) != num_joints:
        names = [f"joint_{i}" for i in range(num_joints)]
    return parents, names


def _load_constraints(constraints_json: str, model):
    if not constraints_json:
        return []

    from kimodo.constraints import load_constraints_lst

    text = constraints_json.strip() if isinstance(constraints_json, str) else constraints_json
    if not isinstance(text, str) or not text:
        return []
    if text[0] not in ("[", "{"):
        raise ValueError(
            "constraints_json must be inline JSON (array/object). File path input is no longer supported."
        )

    try:
        parsed = json.loads(text)
    except Exception as ex:
        raise ValueError(f"Invalid inline constraints_json payload: {ex}") from ex

    if isinstance(parsed, dict):
        parsed = [parsed]
    if not isinstance(parsed, list):
        raise ValueError("constraints_json inline payload must be JSON array/object.")

    return load_constraints_lst(parsed, model.skeleton)


@dataclass
class UnityMotionJsonResult:
    num_frames: int
    num_joints: int
    fps: int
    joint_names: list[str]
    joint_parents: list[int]
    joints: list[float]
    local_rot_quats: list[float] | None
    texts: list[str]
    skeleton: str

    @classmethod
    def from_model_output(cls, model: Any, output: dict, prompt: str, sample_index: int = 0):
        sample_joints = np.asarray(output["posed_joints"][sample_index], dtype=np.float32)
        flat_joints = sample_joints.reshape(-1).tolist()
        joint_count = int(sample_joints.shape[1])
        parents, joint_names = _parents_and_names(model, joint_count)
        local_rot_quats = _extract_flat_local_rot_quats(model, output, sample_index)
        return cls(
            num_frames=int(sample_joints.shape[0]),
            num_joints=joint_count,
            fps=int(model.fps),
            joint_names=joint_names,
            joint_parents=parents,
            joints=flat_joints,
            local_rot_quats=local_rot_quats,
            texts=[prompt],
            skeleton=getattr(getattr(model, "skeleton", None), "name", "unknown"),
        )

    def to_compact_json(self) -> str:
        payload = {
            "num_frames": self.num_frames,
            "num_joints": self.num_joints,
            "fps": self.fps,
            "joint_names": self.joint_names,
            "joint_parents": self.joint_parents,
            "joints": self.joints,
            "local_rot_quats": self.local_rot_quats,
            "texts": self.texts,
            "skeleton": self.skeleton,
        }
        return json.dumps(payload, separators=(",", ":"))


def _generate(req: dict, model):
    from kimodo.tools import seed_everything

    prompt = str(req.get("prompt", "A person walks forward.")).strip()
    if not prompt.endswith("."):
        prompt += "."

    duration = float(req.get("duration", 5.0))
    seed = req.get("seed", None)
    diffusion_steps = int(req.get("diffusion_steps", 100))
    constraints_path = req.get("constraints_json", "")

    if seed is not None:
        seed_everything(int(seed))

    num_frames = max(1, int(duration * float(model.fps)))
    constraints = _load_constraints(constraints_path, model)

    _out({"status": "progress", "message": f"Running diffusion ({diffusion_steps} steps)..."})
    output = model(
        [prompt],
        [num_frames],
        constraint_lst=constraints,
        num_denoising_steps=diffusion_steps,
        num_samples=1,
        multi_prompt=True,
        num_transition_frames=5,
        post_processing=True,
        return_numpy=True,
    )

    motion_data = UnityMotionJsonResult.from_model_output(model, output, prompt, sample_index=0)
    _out({"status": "done", "motion_json_compact": motion_data.to_compact_json()})


def main():
    parser = argparse.ArgumentParser(description="Kimodo Unity Bridge Server")
    parser.add_argument("--model", default="Kimodo-SOMA-RP-v1")
    parser.add_argument("--device", default=None)
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=0)
    parser.add_argument("--kimodo-root", default=None)
    args = parser.parse_args()
    _log(
        f"[bridge] bootstrap start pid={os.getpid()} model={args.model} "
        f"kimodo_root={args.kimodo_root or ''}"
    )

    server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    server.bind((args.host, int(args.port)))
    server.listen(16)

    host, port = server.getsockname()
    kimodo_root = args.kimodo_root or os.environ.get("KIMODO_ROOT_PATH") or os.getcwd()
    port_file = os.path.join(kimodo_root, "serverport")
    _write_text_atomic(port_file, f"{host}:{port}\n")
    os.environ["KIMODO_BRIDGE_LOG"] = _default_bridge_log_path(kimodo_root)
    _log(f"[bridge] listening host={host} port={port} model={args.model}")

    state = {
        "model": None,
        "fps": 30,
        "device": "unknown",
        "loading": True,
        "error": "",
        "loading_message": "Importing Kimodo...",
        "loading_started_at": time.time(),
    }
    state_lock = threading.Lock()
    idle_timeout_seconds = max(0, int(float(os.environ.get("KIMODO_IDLE_TIMEOUT_SEC", "600"))))
    last_command_ts = time.time()
    last_command_lock = threading.Lock()
    active_command_count = 0
    active_command_lock = threading.Lock()
    quitting = False
    quitting_lock = threading.Lock()

    def _set_loading_message(message: str):
        with state_lock:
            if state["loading"]:
                state["loading_message"] = str(message)

    def _load_model_worker():
        _set_loading_message("Importing Kimodo...")
        _out({"status": "loading", "message": "Importing Kimodo..."})
        _log("[bridge] load stage: importing torch and kimodo...")
        try:
            import torch
            _log("[bridge] load stage: torch imported.")
            from kimodo import load_model
            _log("[bridge] load stage: kimodo imported.")
        except Exception as exc:
            with state_lock:
                state["error"] = f"Failed to import kimodo: {exc}"
                state["loading"] = False
            _log(f"[bridge] load error {state['error']}")
            return

        # --- Three-tier VRAM detection ---
        # <2 GB total       → kimodo on CPU, text encoder via local Torch INT8
        # >=2 GB & <6 GB    → kimodo on cuda:0, text encoder via local Torch INT8
        # >=6 GB            → kimodo on cuda:0, text encoder via local LLM2Vec
        total_vram_gb = _detect_total_vram_gb()
        _log(f"[bridge] detected total VRAM: {total_vram_gb:.2f} GB")

        # Determine kimodo device.
        if args.device:
            device = args.device
            # Soft CUDA fallback: honor explicit --device only when GPU exists.
            if str(device).lower().startswith("cuda") and not torch.cuda.is_available():
                _log(f"[bridge] requested device '{device}' but CUDA is unavailable; falling back to CPU.")
                _out({"status": "loading", "message": f"CUDA unavailable; running on CPU instead of {device}."})
                device = "cpu"
        else:
            # Auto-decide based on tier.
            if total_vram_gb < 2.0 or not torch.cuda.is_available():
                device = "cpu"
            else:
                device = "cuda:0"

        _set_loading_message("Checking local models...")
        _out({"status": "loading", "message": "Checking local models..."})
        try:
            provision_plan = _provision_bridge_assets(
                kimodo_root,
                args.model,
                run_device=device,
                total_vram_gb=total_vram_gb,
            )
        except Exception as exc:
            with state_lock:
                state["error"] = f"Model prepare failed: {exc}\n{traceback.format_exc()}"
                state["loading"] = False
            _log(f"[bridge] model prepare error {exc}")
            return

        use_int8_encoder = provision_plan.encoder_route == "int8"
        _log(f"[bridge] tier decision: vram={total_vram_gb:.2f}GB device={device} encoder_route={provision_plan.encoder_route}")

        if use_int8_encoder:
            _log("[bridge] local INT8 text encoder selected (tier: vram<6G).")
        else:
            _log("[bridge] local LLM2Vec text encoder selected (tier: vram>=6G).")

        resolved_model_name = provision_plan.resolved_model.local_name
        _set_loading_message(f"Loading {resolved_model_name} on {device}...")
        _out({"status": "loading", "message": f"Loading {resolved_model_name} on {device}..."})
        _log(f"[bridge] load stage: load_model start model={resolved_model_name} device={device}")
        try:
            model = load_model(resolved_model_name, device=device)
        except Exception as exc:
            with state_lock:
                state["error"] = f"Model load failed: {exc}\n{traceback.format_exc()}"
                state["loading"] = False
            _log(f"[bridge] load error {exc}")
            return
        _log("[bridge] load stage: load_model done.")

        with state_lock:
            state["model"] = model
            state["fps"] = int(model.fps)
            state["device"] = device
            state["loading"] = False
            state["error"] = ""

        _log(f"[bridge] ready host={host} port={port} model={resolved_model_name} device={device}")
        _out({
            "status": "ready",
            "model": resolved_model_name,
            "device": device,
            "fps": int(model.fps),
            "host": host,
            "port": int(port)
        })

    threading.Thread(target=_load_model_worker, daemon=True).start()

    def _is_quitting() -> bool:
        with quitting_lock:
            return quitting

    def _set_quitting() -> None:
        nonlocal quitting
        with quitting_lock:
            quitting = True

    def _touch_last_command() -> None:
        nonlocal last_command_ts
        with last_command_lock:
            last_command_ts = time.time()

    def _command_started() -> None:
        nonlocal active_command_count
        with active_command_lock:
            active_command_count += 1

    def _command_finished() -> None:
        nonlocal active_command_count
        with active_command_lock:
            active_command_count = max(0, active_command_count - 1)

    def _idle_watchdog_worker():
        while not _is_quitting():
            time.sleep(1.0)
            with active_command_lock:
                busy = active_command_count > 0
            if busy:
                continue
            with last_command_lock:
                idle_seconds = time.time() - last_command_ts
            if idle_timeout_seconds > 0 and idle_seconds >= idle_timeout_seconds:
                _log(
                    f"[bridge] idle timeout reached: {int(idle_seconds)}s >= {idle_timeout_seconds}s, shutting down"
                )
                _set_quitting()
                try:
                    server.close()
                except Exception:
                    pass
                return

    threading.Thread(target=_idle_watchdog_worker, daemon=True).start()

    try:
        while not _is_quitting():
            try:
                conn, _addr = server.accept()
            except OSError:
                if _is_quitting():
                    break
                raise
            _log(f"[bridge] accept {_addr}")
            with conn:
                file = conn.makefile("rwb")
                while not _is_quitting():
                    try:
                        line = file.readline()
                    except (ConnectionResetError, BrokenPipeError, OSError):
                        break
                    if not line:
                        break

                    try:
                        req = json.loads(line.decode("utf-8").strip())
                    except Exception as exc:
                        resp = {"status": "error", "message": f"Bad JSON: {exc}"}
                        file.write((json.dumps(resp) + "\n").encode("utf-8"))
                        file.flush()
                        continue

                    cmd = req.get("cmd", "")
                    _touch_last_command()
                    _log(f"[bridge] cmd={cmd}")
                    stage = f"cmd:{cmd}" if cmd else "cmd:unknown"
                    _command_started()
                    try:
                        if cmd == "ping":
                            with state_lock:
                                if state["loading"]:
                                    resp = {"status": "loading", "message": "Model is loading."}
                                elif state["error"]:
                                    resp = {"status": "error", "message": state["error"]}
                                else:
                                    resp = {"status": "pong"}
                        elif cmd == "generate":
                            with state_lock:
                                loading = state["loading"]
                                load_error = state["error"]
                                model = state["model"]
                                fps = state["fps"]

                            if loading:
                                resp = {"status": "loading", "message": "Model is loading."}
                                file.write((json.dumps(resp) + "\n").encode("utf-8"))
                                file.flush()
                                continue
                            if load_error:
                                resp = {"status": "error", "message": load_error}
                                file.write((json.dumps(resp) + "\n").encode("utf-8"))
                                file.flush()
                                continue
                            if model is None:
                                resp = {"status": "error", "message": "Model not available."}
                                file.write((json.dumps(resp) + "\n").encode("utf-8"))
                                file.flush()
                                continue

                            from kimodo.tools import seed_everything
                            prompt = str(req.get("prompt", "A person walks forward.")).strip()
                            if not prompt.endswith("."):
                                prompt += "."
                            duration = float(req.get("duration", 5.0))
                            seed = req.get("seed", None)
                            diffusion_steps = int(req.get("diffusion_steps", 100))
                            constraints_path = req.get("constraints_json", "")

                            if seed is not None:
                                seed_everything(int(seed))

                            num_frames = max(1, int(duration * float(fps)))
                            constraints = _load_constraints(constraints_path, model)

                            output = model(
                                [prompt],
                                [num_frames],
                                constraint_lst=constraints,
                                num_denoising_steps=diffusion_steps,
                                num_samples=1,
                                multi_prompt=True,
                                num_transition_frames=5,
                                post_processing=True,
                                return_numpy=True,
                            )

                            motion_data = UnityMotionJsonResult.from_model_output(model, output, prompt, sample_index=0)
                            resp = {"status": "done", "motion_json_compact": motion_data.to_compact_json()}
                        elif cmd == "quit":
                            resp = {"status": "bye"}
                            _set_quitting()
                            try:
                                server.close()
                            except Exception:
                                pass
                        else:
                            resp = {"status": "error", "message": f"Unknown cmd: {cmd!r}"}
                    except Exception as exc:
                        resp = {
                            "status": "error",
                            "message": str(exc),
                            "server_message": f"Bridge exception while handling {stage}",
                            "error_type": type(exc).__name__,
                            "stage": stage,
                            "traceback": traceback.format_exc(),
                        }
                        _log(f"[bridge] error {exc}")
                    finally:
                        _command_finished()

                    try:
                        file.write((json.dumps(resp) + "\n").encode("utf-8"))
                        file.flush()
                    except (ConnectionResetError, BrokenPipeError, OSError):
                        break
    finally:
        try:
            server.close()
        except Exception:
            pass
        try:
            if os.path.exists(port_file):
                os.remove(port_file)
        except Exception:
            _log("[bridge] shutdown")
            pass


if __name__ == "__main__":
    main()


