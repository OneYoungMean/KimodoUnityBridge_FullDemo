from __future__ import annotations

from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from core import mocap_assets


class MocapAssetTests(unittest.TestCase):
    def test_uses_bundled_gvhmr_source_before_network_clone(self):
        with tempfile.TemporaryDirectory() as temp:
            temp_root = Path(temp)
            bundled = temp_root / "vendor" / "GVHMR"
            root = temp_root / "models" / "GVHMR"
            source = bundled / "hmr4d" / "model" / "gvhmr" / "gvhmr_pl_demo.py"
            source.parent.mkdir(parents=True)
            source.write_text("bundled", encoding="utf-8")
            local_model = root / "inputs" / "checkpoints" / "body_models" / "smplx" / "SMPLX_NEUTRAL_LITE_V437.npz"
            local_model.parent.mkdir(parents=True)
            local_model.write_bytes(b"local compact asset")

            def fake_download(**kwargs):
                target = Path(kwargs["local_dir"]) / kwargs["filename"]
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(b"asset")
                return str(target)

            with patch.object(mocap_assets, "BUNDLED_GVHMR_ROOT", bundled), patch.object(
                mocap_assets, "_download_repository"
            ) as download_repo, patch("huggingface_hub.hf_hub_download", side_effect=fake_download):
                mocap_assets.ensure_gvhmr_assets(root)

            self.assertEqual(
                (root / "hmr4d" / "model" / "gvhmr" / "gvhmr_pl_demo.py").read_text(encoding="utf-8"),
                "bundled",
            )
            download_repo.assert_not_called()

    def test_requires_local_smplx_asset_without_downloading_it(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            (root / "hmr4d" / "model" / "gvhmr").mkdir(parents=True)
            (root / "hmr4d" / "model" / "gvhmr" / "gvhmr_pl_demo.py").write_text("", encoding="utf-8")
            with patch.object(mocap_assets, "CHECKPOINTS", {}), patch.object(
                mocap_assets, "BUNDLED_SMPLX_LITE", root / "missing-lite.npz"
            ), patch("huggingface_hub.hf_hub_download") as download:
                with self.assertRaisesRegex(RuntimeError, "Bundled lightweight SMPL-X V437 asset"):
                    mocap_assets.ensure_gvhmr_assets(root)
        download.assert_not_called()

    def test_installs_bundled_v437_asset_into_gvhmr_inputs(self):
        with tempfile.TemporaryDirectory() as temp:
            temp_root = Path(temp)
            bundled = temp_root / "vendor" / "GVHMR"
            source = bundled / "hmr4d" / "model" / "gvhmr" / "gvhmr_pl_demo.py"
            source.parent.mkdir(parents=True)
            source.write_text("bundled", encoding="utf-8")
            lite_asset = temp_root / "SMPLX_NEUTRAL_LITE_V437.npz"
            lite_asset.write_bytes(b"compact smplx")
            root = temp_root / "models" / "GVHMR"

            with patch.object(mocap_assets, "BUNDLED_GVHMR_ROOT", bundled), patch.object(
                mocap_assets, "BUNDLED_SMPLX_LITE", lite_asset
            ), patch.object(mocap_assets, "CHECKPOINTS", {}):
                mocap_assets.ensure_gvhmr_assets(root)

            installed = root / "inputs" / "checkpoints" / "body_models" / "smplx" / "SMPLX_NEUTRAL_LITE_V437.npz"
            self.assertEqual(installed.read_bytes(), b"compact smplx")

    def test_retries_checkpoint_from_second_public_mirror(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            source = root / "hmr4d" / "model" / "gvhmr" / "gvhmr_pl_demo.py"
            source.parent.mkdir(parents=True)
            source.write_text("", encoding="utf-8")
            calls = []

            def fake_download(**kwargs):
                calls.append(kwargs["repo_id"])
                if len(calls) == 1:
                    raise OSError("primary mirror unavailable")
                target = Path(kwargs["local_dir"]) / kwargs["filename"]
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(b"asset")
                return str(target)

            with patch.object(mocap_assets, "CHECKPOINTS", {"gvhmr.ckpt": "gvhmr.ckpt"}), patch(
                "huggingface_hub.hf_hub_download", side_effect=fake_download
            ):
                # Provide the model so only checkpoint mirror fallback is exercised.
                model = root / "inputs" / "checkpoints" / "body_models" / "smplx" / "SMPLX_NEUTRAL_LITE_V437.npz"
                model.parent.mkdir(parents=True)
                model.write_bytes(b"smplx")
                mocap_assets.ensure_gvhmr_assets(root)

        self.assertEqual(calls, list(mocap_assets.CHECKPOINT_REPOSITORIES))


if __name__ == "__main__":
    unittest.main()
