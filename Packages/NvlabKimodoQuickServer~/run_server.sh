#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" >/dev/null 2>&1 && pwd)"
ROOT_DIR="${SCRIPT_DIR}"
UV_BIN="${ROOT_DIR}/program/exe/uv/uv.exe"

if [[ -n "${KIMODO_TEST_VENV_PATH:-}" ]]; then
  echo "[ERROR] KIMODO_TEST_VENV_PATH has been removed. Use KIMODO_VENV_PATH."
  exit 1
fi
if [[ -n "${KIMODO_TEST_SETUP_DEVICE:-}" ]]; then
  echo "[ERROR] KIMODO_TEST_SETUP_DEVICE has been removed. Use KIMODO_SETUP_DEVICE."
  exit 1
fi
if [[ -n "${KIMODO_CPU_TEXT_ENCODER:-}" ]]; then
  echo "[ERROR] KIMODO_CPU_TEXT_ENCODER has been removed. QuickServer now auto-selects the local INT8 text encoder route."
  exit 1
fi
if [[ -n "${CHECKPOINT_DIR:-}" ]]; then
  echo "[ERROR] CHECKPOINT_DIR has been removed. Use KIMODO_MODELS_ROOT."
  exit 1
fi

if [[ ! -x "${UV_BIN}" ]]; then
  echo "[ERROR] Missing bundled uv: ${UV_BIN}"
  exit 1
fi

ARGS=("$@")
HAS_VENV_ARG=0
for arg in "${ARGS[@]}"; do
  if [[ "${arg}" == "--venv" ]]; then
    HAS_VENV_ARG=1
    break
  fi
done
if [[ -n "${KIMODO_VENV_PATH:-}" && "${HAS_VENV_ARG}" -eq 0 ]]; then
  ARGS+=("--venv" "${KIMODO_VENV_PATH}")
fi

exec "${UV_BIN}" run --python 3.12 --no-project python "${ROOT_DIR}/quickserver.py" "${ARGS[@]}"
