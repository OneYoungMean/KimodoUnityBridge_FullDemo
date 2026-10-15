"""Blender side of the SMPL-X retarget — runs HEADLESS (no MCP, no UI).

  blender -b <rig>_rest.blend -P pipeline/bl_motion.py -- dump-rig --profile rig_profiles/ybot.json
  blender -b <rig>_rest.blend -P pipeline/bl_motion.py -- apply   --motion clips/<clip>/motion.npz [--save out.blend]
  blender -b <rig>_rest.blend -P pipeline/bl_motion.py -- render  --motion clips/<clip>/motion.npz --view front --out <dir>
  blender -b <rig>_rest.blend -P pipeline/bl_motion.py -- render  --motion ... --view camera  (the plate's own camera,
                                                                transparent film, for the overlay)
  blender -b <rig>_rest.blend -P pipeline/bl_motion.py -- render  --curves clips/<old>/curves.json ...
                                                                (re-key a legacy clip's dumped curves, for A/B)

`dump-rig` adds the armature's rest matrices (every bone, armature
space, centimetres) and the foot geometry to a rig profile, which is all
retarget_smplx.py needs from Blender. `apply` writes the retargeted
motion as keyframes in bulk (fcurve foreach_set — seconds, not the
minutes a keyframe_insert loop takes) and saves the .blend. `render`
applies and renders a PNG sequence through a temporary camera; nothing
is ever saved into the rest scene.

Everything the retarget computes is FK: quaternions on every bone,
location on Hips only (docs/RIG.md).
"""

from __future__ import annotations

import argparse
import json
import math
import sys
from pathlib import Path

import bpy
import numpy as np
from mathutils import Matrix, Vector

REPO = Path(__file__).resolve().parents[1]
LINEAR = 1  # Keyframe.interpolation enum index


def rpath(p) -> Path:
    p = Path(p)
    return p if p.is_absolute() else (REPO / p)


def armature(name=None):
    if name:
        arm = bpy.data.objects.get(name)
        if arm is None or arm.type != "ARMATURE":
            raise SystemExit(f"armature {name!r} not in the scene")
        return arm
    arms = [o for o in bpy.data.objects if o.type == "ARMATURE"]
    if len(arms) != 1:
        raise SystemExit(f"expected one armature, found {[a.name for a in arms]} — pass --armature")
    return arms[0]


# ---------------------------------------------------------------------------
# dump-rig

def rig_table(arm) -> dict:
    """What retarget_smplx.py needs from a rig, as JSON-able data: every
    bone's rest matrix in armature space (parents first), the armature's
    world matrix, and the rest mesh's floor and height (armature units)."""
    ordered, seen = [], set()

    def walk(b):
        if b.name in seen:
            return
        seen.add(b.name)
        ordered.append(b)
        for c in b.children:
            walk(c)

    for b in arm.data.bones:
        if b.parent is None:
            walk(b)
    bones = [{"name": b.name, "parent": b.parent.name if b.parent else None,
              "matrix_local": [[round(v, 7) for v in row] for row in b.matrix_local],
              "tail_local": [round(v, 5) for v in b.tail_local]} for b in ordered]
    inv = arm.matrix_world.inverted()
    lo, hi = float("inf"), float("-inf")
    meshes = [o for o in bpy.data.objects if o.type == "MESH" and o.find_armature() is arm]
    for m in meshes:
        M = inv @ m.matrix_world
        for v in m.data.vertices:
            y = (M @ v.co).y
            lo, hi = min(lo, y), max(hi, y)
    return {"armature": arm.name,
            "armature_matrix_world": [[round(v, 7) for v in row] for row in arm.matrix_world],
            "bones": bones,
            "mesh_floor_y": round(lo, 4),          # armature units (cm on a Mixamo FBX import)
            "mesh_height": round(hi - lo, 4),
            "meshes": [m.name for m in meshes]}


def dump_rig(args) -> None:
    arm = armature(args.armature)
    out = rpath(args.profile)
    prof = json.loads(out.read_text(encoding="utf-8")) if out.exists() else {}
    prof.update(rig_table(arm))
    out.write_text(json.dumps(prof, indent=1), encoding="utf-8")
    print(f"dump-rig: {len(prof['bones'])} bones, floor {prof['mesh_floor_y']:.3f}, "
          f"height {prof['mesh_height']:.2f} -> {out}")


# ---------------------------------------------------------------------------
# apply

def make_action(arm, name):
    if name in bpy.data.actions:
        bpy.data.actions.remove(bpy.data.actions[name])
    action = bpy.data.actions.new(name)
    action.use_fake_user = True
    if arm.animation_data is None:
        arm.animation_data_create()
    arm.animation_data.action = action
    if hasattr(action, "slots"):                  # Blender 4.4+: layer / slot / channelbag
        slot = action.slots.new(id_type="OBJECT", name=arm.name)
        arm.animation_data.action_slot = slot
        strip = action.layers.new("Layer").strips.new(type="KEYFRAME")
        return action, strip.channelbag(slot, ensure=True)
    return action, action


def key_curve(holder, path, index, group, frames, values):
    fc = holder.fcurves.new(path, index=index)
    try:
        fc.group = group
    except Exception:
        pass
    n = len(frames)
    fc.keyframe_points.add(n)
    co = np.empty(n * 2, dtype=np.float64)
    co[0::2] = frames
    co[1::2] = values
    fc.keyframe_points.foreach_set("co", co)
    fc.keyframe_points.foreach_set("interpolation", [LINEAR] * n)
    fc.update()


def apply_motion(arm, motion: dict, action_name: str | None = None):
    """Key a motion dict (from motion.npz or legacy curves) onto `arm`."""
    names = [str(n) for n in motion["bone_names"]]
    quats = np.asarray(motion["quat"], dtype=np.float64)          # (T, B, 4) wxyz
    hips = np.asarray(motion["hips_location"], dtype=np.float64)  # (T, 3) pose-local
    T = quats.shape[0]
    name = action_name or str(motion.get("action_name", "SMPLX_Retarget"))
    action, holder = make_action(arm, name)
    frames = np.arange(1, T + 1, dtype=np.float64)
    missing = []
    for b, bn in enumerate(names):
        pb = arm.pose.bones.get(bn)
        if pb is None:
            missing.append(bn)
            continue
        pb.rotation_mode = "QUATERNION"
        grp = None
        if hasattr(holder, "groups"):                 # one channel group per bone, like a keyed rig
            grp = holder.groups.get(bn) or holder.groups.new(bn)
        base = f'pose.bones["{bn}"]'
        for c in range(4):
            key_curve(holder, base + ".rotation_quaternion", c, grp, frames, quats[:, b, c])
        loc = hips if bn == str(motion.get("root", "mixamorig:Hips")) else np.zeros((T, 3))
        for c in range(3):
            key_curve(holder, base + ".location", c, grp, frames, loc[:, c])
    scene = bpy.context.scene
    scene.render.fps = int(round(float(motion.get("fps", 30))))
    scene.frame_start = 1
    scene.frame_end = T
    if missing:
        print(f"apply: {len(missing)} bones not in this rig (skipped): {missing[:6]}")
    return action


def load_motion(path: Path) -> dict:
    z = np.load(path, allow_pickle=False)
    return {k: z[k] for k in z.files}


def load_curves(path: Path) -> dict:
    """A legacy clip's curves.json (apply_mixamo_fk.dump_curves) as a
    motion dict — lets old and new pipelines render through one path."""
    frames = json.loads(path.read_text(encoding="utf-8"))["frames"]
    names = list(frames[0]["bones"].keys())
    q = np.array([[fr["bones"][n]["rotation_quaternion"] for n in names] for fr in frames])
    hips = np.array([fr["bones"]["mixamorig:Hips"]["location"] for fr in frames])
    return {"bone_names": np.array(names), "quat": q, "hips_location": hips, "fps": 30,
            "root": "mixamorig:Hips"}


def cmd_apply(args) -> None:
    arm = armature(args.armature)
    motion = load_motion(rpath(args.motion)) if args.motion else load_curves(rpath(args.curves))
    action = apply_motion(arm, motion, args.action)
    print(f"apply: action {action.name!r}, {bpy.context.scene.frame_end} frames")
    if args.save:
        bpy.ops.wm.save_as_mainfile(filepath=str(rpath(args.save)), copy=True)
        print("saved", rpath(args.save))


# ---------------------------------------------------------------------------
# render

def setup_look(scene, transparent: bool, floor: bool, arm):
    scene.render.engine = "BLENDER_WORKBENCH"
    sh = scene.display.shading
    sh.light = "STUDIO"
    try:
        sh.studio_light = "paint.sl"      # soft top light: lights the floor, not only the body
    except Exception:
        pass
    sh.color_type = "MATERIAL"
    sh.show_shadows = True
    sh.shadow_intensity = 0.8
    sh.show_cavity = True
    sh.cavity_type = "BOTH"
    try:
        sh.show_specular_highlight = True
    except Exception:
        pass
    scene.display.light_direction = (-0.3, 0.5, -0.8)   # casts the shadow back-right onto a lit floor
    scene.display.shadow_shift = 0.02
    scene.render.film_transparent = transparent
    scene.render.image_settings.file_format = "PNG"
    scene.render.image_settings.color_mode = "RGBA" if transparent else "RGB"
    scene.view_settings.view_transform = "Standard"
    world = scene.world or bpy.data.worlds.new("World")
    scene.world = world
    world.color = (0.80, 0.81, 0.84)
    if floor and not transparent:
        me = bpy.data.meshes.new("QA_Floor")
        s = 30.0
        me.from_pydata([(-s, -s, 0), (s, -s, 0), (s, s, 0), (-s, s, 0)], [], [(0, 1, 2, 3)])
        ob = bpy.data.objects.new("QA_Floor", me)
        mat = bpy.data.materials.new("QA_FloorMat")
        mat.diffuse_color = (1.0, 1.0, 1.0, 1.0)
        me.materials.append(mat)
        scene.collection.objects.link(ob)
        # a 50 cm grid of thin lines so travel and foot skate read on screen
        gm = bpy.data.meshes.new("QA_Grid")
        verts, faces = [], []
        w = 0.006
        for k in range(-12, 13):
            x = k * 0.5
            for (a0, a1, b0, b1) in ((x - w, x + w, -6.0, 6.0), (-6.0, 6.0, x - w, x + w)):
                i = len(verts)
                verts += [(a0, b0, 0.001), (a1, b0, 0.001), (a1, b1, 0.001), (a0, b1, 0.001)]
                faces.append((i, i + 1, i + 2, i + 3))
        gm.from_pydata(verts, [], faces)
        go = bpy.data.objects.new("QA_Grid", gm)
        gmat = bpy.data.materials.new("QA_GridMat")
        gmat.diffuse_color = (0.66, 0.67, 0.70, 1.0)
        gm.materials.append(gmat)
        scene.collection.objects.link(go)
    # One fixed character look, so any two renders (old vs new pipeline)
    # differ by the motion and nothing else. Y Bot's joint spheres stay
    # light; everything else is the body tone.
    for m in bpy.data.objects:
        if m.type == "MESH" and m.find_armature() is arm:
            tone = (0.93, 0.93, 0.95, 1.0) if "Joints" in m.name else (0.30, 0.46, 0.78, 1.0)
            for slot in m.material_slots:
                if slot.material:
                    slot.material.diffuse_color = tone


def cmd_render(args) -> None:
    scene = bpy.context.scene
    arm = armature(args.armature)
    motion = load_motion(rpath(args.motion)) if args.motion else load_curves(rpath(args.curves))
    apply_motion(arm, motion, args.action)
    T = scene.frame_end
    if args.frames:
        a, b = (int(x) for x in args.frames.split(":"))
        scene.frame_start, scene.frame_end = max(1, a), min(T, b)

    cam_data = bpy.data.cameras.new("QA_Camera")
    cam = bpy.data.objects.new("QA_Camera", cam_data)
    scene.collection.objects.link(cam)
    scene.camera = cam
    transparent = args.view == "camera"
    setup_look(scene, transparent, floor=not transparent, arm=arm)

    if args.view == "camera":
        # The plate's own camera: intrinsics K and a static pose, both
        # written by retarget_smplx.py into the motion file in Blender
        # world coordinates. Renders the character where the performer is.
        K = np.asarray(motion["cam_K"], dtype=np.float64)
        W, H = int(motion["cam_wh"][0]), int(motion["cam_wh"][1])
        scene.render.resolution_x, scene.render.resolution_y = W, H
        cam_data.sensor_fit = "HORIZONTAL"
        cam_data.sensor_width = 36.0
        cam_data.lens = float(K[0, 0]) * 36.0 / W
        cam_data.shift_x = -(float(K[0, 2]) - W / 2.0) / W
        cam_data.shift_y = (float(K[1, 2]) - H / 2.0) / W
        cam_data.clip_start, cam_data.clip_end = 0.05, 100.0
        M = np.asarray(motion["cam_matrix_world"], dtype=np.float64)
        cam.matrix_world = Matrix([list(r) for r in M])
    else:
        # 16:9, framed like a 4:3 50 mm shot vertically (a 2.5 m tall window
        # at 4.6 m) so a jump or a head-high kick stays in frame
        scene.render.resolution_x, scene.render.resolution_y = 1280, 720
        cam_data.lens = 37.0
        # Frame on the hips' mean ground position over the clip so a
        # travelling performance stays in shot; fixed for the whole clip.
        hips_w = np.asarray(motion.get("hips_world", np.zeros((T, 3))), dtype=np.float64)
        cx, cy = (float(np.median(hips_w[:, 0])), float(np.median(hips_w[:, 1]))) if len(hips_w) else (0.0, 0.0)
        dist = args.distance
        if args.view == "front":
            cam.location = Vector((cx, cy - dist, 1.02))
            cam.rotation_euler = (math.radians(88.0), 0.0, 0.0)
        elif args.view == "side":
            cam.location = Vector((cx + dist, cy, 1.02))
            cam.rotation_euler = (math.radians(88.0), 0.0, math.radians(90.0))
        else:  # three_quarter, slightly above
            a = math.radians(35.0)
            cam.location = Vector((cx + dist * math.sin(a), cy - dist * math.cos(a), 1.55))
            cam.rotation_euler = (math.radians(81.0), 0.0, a)
    # --scale renders more pixels of the same shot (framing is resolution-
    # independent): the zoomed hand and foot crops of render_review.py
    scene.render.resolution_percentage = int(round(100 * args.scale))
    out = rpath(args.out)
    out.mkdir(parents=True, exist_ok=True)
    scene.render.filepath = str(out / "f####")
    bpy.ops.render.render(animation=True)
    print(f"render: {scene.frame_end - scene.frame_start + 1} frames ({args.view}) -> {out}")


def main() -> None:
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    ap = argparse.ArgumentParser(prog="bl_motion.py")
    sub = ap.add_subparsers(dest="cmd", required=True)
    d = sub.add_parser("dump-rig")
    d.add_argument("--profile", required=True)
    d.add_argument("--armature")
    a = sub.add_parser("apply")
    r = sub.add_parser("render")
    for p in (a, r):
        g = p.add_mutually_exclusive_group(required=True)
        g.add_argument("--motion", help="motion.npz from retarget_smplx.py")
        g.add_argument("--curves", help="a legacy clip's curves.json")
        p.add_argument("--armature")
        p.add_argument("--action")
    a.add_argument("--save")
    r.add_argument("--view", default="front", choices=["front", "side", "three_quarter", "camera"])
    r.add_argument("--out", required=True)
    r.add_argument("--frames", help="first:last (1-based, inclusive)")
    r.add_argument("--distance", type=float, default=4.6)
    r.add_argument("--scale", type=float, default=1.0, help="resolution multiplier (2 = twice the pixels per side)")
    args = ap.parse_args(argv)
    {"dump-rig": dump_rig, "apply": cmd_apply, "render": cmd_render}[args.cmd](args)


if __name__ == "__main__":
    try:
        main()
    except SystemExit as e:
        if e.code not in (0, None):
            print(f"ERROR: {e}", file=sys.stderr)
            sys.stdout.flush()
            import os
            os._exit(2)
        raise
