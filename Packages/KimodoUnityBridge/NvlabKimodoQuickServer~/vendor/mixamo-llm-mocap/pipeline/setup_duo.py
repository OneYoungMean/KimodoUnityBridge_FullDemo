"""Build a multi-character rest scene — two (or more) Mixamo characters
in one Blender file, each with its own measured rig profile.

  "C:\\Program Files\\Blender Foundation\\Blender 5.1\\blender.exe" ^
      --background --python pipeline\\setup_duo.py -- ^
      --char YBot=ybot.fbx --char Ninja=ninja_rest.blend ^
      --out duel_rest.blend

Each `--char Name=source` takes a Mixamo FBX (imported) or an existing
rest .blend built by setup_rig.py (appended). Every character gets:

  - its armature renamed `Armature_<Name>` (bone names untouched — the
    pipeline's whole contract is that they stay `mixamorig:*`)
  - its own `rig_profiles/<name>.json`, so the lift/apply/QA use THAT
    character's proportions rather than one global profile

**Both armature objects stay at the origin with identity transforms.**
Stage placement (who stands where) is not an object transform: it is
baked into the animation by the lift's `stage_x`, exactly like the root
trajectory. Keeping the objects at the origin is what lets the FK apply
go on aiming bones in armature space with no offset bookkeeping — the
one place this rig punishes mistakes (docs/RIG.md).
"""

from __future__ import annotations

import argparse
import json
import math
import sys
from pathlib import Path

import bpy

REPO = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(Path(__file__).resolve().parent))

from setup_rig import dump_rig_profile  # noqa: E402


def load_character(source: Path) -> list:
    """Import an FBX or append every object from a rest .blend."""
    before = set(bpy.data.objects)
    if source.suffix.lower() == ".fbx":
        bpy.ops.import_scene.fbx(filepath=str(source))
    elif source.suffix.lower() == ".blend":
        with bpy.data.libraries.load(str(source), link=False) as (src, dst):
            dst.objects = list(src.objects)
        for obj in dst.objects:
            if obj is not None:
                bpy.context.scene.collection.objects.link(obj)
    else:
        raise SystemExit(f"unsupported character source: {source}")
    return [o for o in bpy.data.objects if o not in before]


def validate(arm, name: str) -> None:
    errors = []
    if abs(arm.rotation_euler[0] - math.pi / 2) > 1e-3:
        errors.append(f"armature X rotation {math.degrees(arm.rotation_euler[0]):.1f} deg, expected 90")
    if abs(arm.scale[0] - 0.01) > 1e-5:
        errors.append(f"armature scale {arm.scale[0]}, expected 0.01")
    if "mixamorig:Hips" not in arm.pose.bones:
        errors.append("mixamorig:Hips missing (never strip the mixamorig: prefix)")
    bad = [b.name for b in arm.pose.bones if not b.name.startswith("mixamorig:")]
    if bad:
        errors.append(f"non-mixamorig bones: {bad[:5]}")
    if errors:
        raise SystemExit(f"character '{name}' does not match Mixamo conventions:\n  " + "\n  ".join(errors))
    if len(arm.pose.bones) != 65:
        print(f"WARNING: {name} has {len(arm.pose.bones)} pose bones (standard is 65)")


def main() -> None:
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    ap = argparse.ArgumentParser()
    ap.add_argument("--char", action="append", required=True,
                    help="Name=source.fbx or Name=rest.blend (repeat per character)")
    ap.add_argument("--out", default="duel_rest.blend")
    ap.add_argument("--profile-dir", default="rig_profiles")
    args = ap.parse_args(argv)

    def rp(p) -> Path:
        p = Path(p)
        return p if p.is_absolute() else REPO / p

    bpy.ops.wm.read_factory_settings(use_empty=True)
    scene = bpy.context.scene
    scene.render.fps = 30
    scene.frame_start = 1

    profile_dir = rp(args.profile_dir)
    profile_dir.mkdir(parents=True, exist_ok=True)
    summary = {}

    for entry in args.char:
        if "=" not in entry:
            raise SystemExit(f"--char expects Name=source, got {entry!r}")
        name, source = entry.split("=", 1)
        src = rp(source)
        if not src.exists():
            raise SystemExit(f"character source not found: {src}")

        created = load_character(src)
        arms = [o for o in created if o.type == "ARMATURE"]
        if len(arms) != 1:
            raise SystemExit(f"expected exactly one armature in {src.name}, found {len(arms)}")
        arm = arms[0]
        validate(arm, name)

        arm.name = f"Armature_{name}"
        arm.location = (0.0, 0.0, 0.0)
        for obj in created:
            if obj.type == "MESH":
                obj.name = f"{name}_{obj.name.split('_', 1)[-1] if obj.name.startswith(name) else obj.name}"
        # Drop anything that is not the armature or its meshes: the scene
        # must stay renderable by the pipeline's temporary-camera path.
        for obj in created:
            if obj.type not in ("ARMATURE", "MESH"):
                bpy.data.objects.remove(obj)

        bpy.context.view_layer.update()
        profile_path = profile_dir / f"{name.lower()}.json"
        profile = dump_rig_profile(arm, profile_path)
        summary[name] = {
            "armature": arm.name,
            "profile": str(profile_path.relative_to(REPO)).replace("\\", "/"),
            "hip_height": profile["hip_height"],
            "ground_z": profile["ground_z"],
            "arm_reach": round(profile["lengths"]["l_arm"] + profile["lengths"]["l_fore"], 4),
            "leg_length": round(profile["lengths"]["l_upleg"] + profile["lengths"]["l_leg"], 4),
            "meshes": profile["character"],
        }
        print(f"{name}: {arm.name}, hip {profile['hip_height']:.3f} m, "
              f"arm {summary[name]['arm_reach']:.3f} m, leg {summary[name]['leg_length']:.3f} m")

    out = rp(args.out)
    bpy.ops.wm.save_as_mainfile(filepath=str(out))
    (profile_dir / "_scene.json").write_text(json.dumps(summary, indent=1), encoding="utf-8")
    print(f"saved {out} with {len(summary)} characters: {', '.join(summary)}")


if __name__ == "__main__":
    main()
