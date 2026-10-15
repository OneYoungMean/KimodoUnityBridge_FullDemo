from __future__ import annotations

import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

from core import mocap_analysis


class MocapAnalysisTests(unittest.TestCase):
    def test_defaults_to_bundled_checkout(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            media = Path(temp) / "clip.mp4"
            media.write_bytes(b"video")
            bundled_root = Path(mocap_analysis.__file__).resolve().parents[1] / "vendor" / "mixamo-llm-mocap"
            completed = subprocess.CompletedProcess([], 0, '{"status":"done","poses":[{"frame":0}]}', "")
            with patch.dict(os.environ, {"KIMODO_MOCAP_ROOT": "", "KIMODO_MOCAP_PYTHON": ""}), patch(
                "core.mocap_analysis.ensure_gvhmr_assets"
            ), patch("core.mocap_analysis.subprocess.run", return_value=completed) as run:
                result = mocap_analysis.analyze_media("video", str(media))
        self.assertEqual(result["status"], "done")
        self.assertEqual(run.call_args.kwargs["cwd"], str(bundled_root.resolve()))

    def test_rejects_unknown_media_extension(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            media = Path(temp) / "clip.gif"
            media.write_bytes(b"image")
            with self.assertRaises(ValueError):
                mocap_analysis.analyze_media("picture", str(media))

    def test_runs_worker_and_returns_pose_payload(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp) / "mocap"
            (root / "pipeline").mkdir(parents=True)
            (root / "pipeline" / "estimate_pose_gvhmr.py").write_text("", encoding="utf-8")
            python = Path(temp) / "python.exe"
            python.write_text("", encoding="utf-8")
            media = Path(temp) / "clip.mp4"
            media.write_bytes(b"video")
            payload = {"status": "done", "poses": [{"frame": 0}]}
            completed = subprocess.CompletedProcess([], 0, '{"status":"done","poses":[{"frame":0}]}', "")
            with patch.dict(os.environ, {
                "KIMODO_MOCAP_ROOT": str(root), "KIMODO_MOCAP_PYTHON": str(python),
            }), patch("core.mocap_analysis.ensure_gvhmr_assets"), patch(
                "core.mocap_analysis.subprocess.run", return_value=completed
            ) as run:
                result = mocap_analysis.analyze_media("video", str(media), "left")

        self.assertEqual(result, payload)
        self.assertEqual(run.call_args.args[0][-2:], ["--person", "left"])
        self.assertEqual(run.call_args.kwargs["cwd"], str(root.resolve()))
        self.assertEqual(run.call_args.kwargs["env"]["TORCH_FORCE_NO_WEIGHTS_ONLY_LOAD"], "1")


if __name__ == "__main__":
    unittest.main()
