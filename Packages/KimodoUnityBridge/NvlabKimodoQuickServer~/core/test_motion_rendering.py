from __future__ import annotations

import unittest
from unittest.mock import patch

import numpy as np

from core import motion_rendering


class MotionRenderingTests(unittest.TestCase):
    def test_render_skeleton_mp4_uses_unimate_renderer_and_returns_bytes(self):
        fake_frames = np.zeros((2, 16, 16, 3), dtype=np.uint8)

        class FakePlotting:
            @staticmethod
            def render_skeleton_motion(parents, positions, **kwargs):
                self_seen["parents"] = parents
                self_seen["positions"] = positions
                self_seen["kwargs"] = kwargs
                return fake_frames

        self_seen = {}
        with patch.object(motion_rendering, "_load_plotting", return_value=FakePlotting):
            result = motion_rendering.render_skeleton_mp4(
                np.zeros((2, 2, 3), dtype=np.float32),
                [-1, 0],
                30,
                title="walk",
            )

        self.assertTrue(result.startswith(b"\x00\x00\x00"))
        self.assertEqual(self_seen["parents"], (-1, 0))
        self.assertEqual(self_seen["positions"].shape, (2, 2, 3))
        self.assertEqual(self_seen["kwargs"]["title"], "walk")

    def test_render_skeleton_mp4_rejects_invalid_parent_count(self):
        with self.assertRaisesRegex(ValueError, "parents has 1 entries"):
            motion_rendering.render_skeleton_mp4(
                np.zeros((1, 2, 3), dtype=np.float32),
                [-1],
                30,
            )


if __name__ == "__main__":
    unittest.main()
