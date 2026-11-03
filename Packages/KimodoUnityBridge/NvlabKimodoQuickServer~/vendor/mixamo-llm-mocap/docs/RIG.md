# Mixamo rig conventions

The conventions below hold for every Mixamo character (same skeleton,
same spaces, same channel rules). The concrete numbers are the **Y Bot
reference measurements**; your own character's numbers are measured
into `rig_profile.json` by `pipeline/setup_rig.py` and override them
throughout the pipeline.

Measured facts about the Mixamo Y Bot as imported by Blender from FBX.
The pipeline's constants (`REST`/`LEN` in `pipeline/lift_to_mixamo.py`)
come from these measurements. `pipeline/setup_rig.py` validates a fresh
import against them.

## Scene

| Object | Role |
|---|---|
| `Armature` | only transform root — rotation X = 90°, scale 0.01 |
| `Alpha_Surface` | body skin (17k verts, max 4 influences/vert) |
| `Alpha_Joints` | decorative joint spheres (ignore for motion) |

Nothing else. No cameras, no lights, no empties, no constraints.

## Spaces and units (the part that breaks people)

Mixamo FBX is **Y-up centimeters**; the import wraps it in the
armature's X=90° rotation and 0.01 scale:

| Space | Up | Character forward | Character left | Units |
|---|---|---|---|---|
| Bone/pose local (Mixamo) | +Y | +Z (bone aim) | +X | **cm** |
| World after import | +Z | **−Y** | +X | m |
| Estimator landmarks | −Y (y is down) | −Z (z<0 toward camera) | +X | m |

Consequences the code lives by:

- Animate **pose bones**, never the armature object.
- **Hips is the only bone that translates.** Its `location` is
  pose-local **centimeters**: `x → world X`, `y → world Z (height)`,
  `z → world −Y (forward)`. A hip at world height h keys
  `location.y = (h − 0.99792) * 100`.
- Every other bone: `rotation_quaternion (w, x, y, z)` only; location
  stays zero (keyed at zero every frame so stale channels can't leak).
- 30 fps, integer frames, frame 1 = first pose, linear interpolation.
- Measure world positions ONLY as
  `(arm.matrix_world @ pose_bone.matrix).to_translation()` after
  `view_layer.update()`. (`pb.head`, or composing pose matrices through
  `matrix_world`, both give wrong answers on this rig.)
- Aim bones in **armature space** (centimeters). Building a world-space
  matrix (scale 0.01) and writing it onto a pose bone sends limbs 100×
  away.
- **No Blender IK, ever.** Constraint IK explodes this rig (feet at
  −81 m historically). Feet are planted by searching the hip height
  until the support foot reaches ground, plus Z-only foot flattening.
- Blender 5.x slotted actions: fcurves live in
  `action.layers[].strips[].channelbag(slot)` — old `action.fcurves`
  is gone.

## Bones (65, prefix `mixamorig:` — never rename)

```
Hips → Spine → Spine1 → Spine2 → {Neck → Head → HeadTop_End,
                                  L/R Shoulder → Arm → ForeArm → Hand → 5×(1..4 fingers)}
Hips → L/R UpLeg → Leg → Foot → ToeBase → Toe_End
```

Tip bones (`*4`, `HeadTop_End`, `*Toe_End`) carry no skin weights —
leave them at rest (the pipeline skips keying them).

## Key measurements (world meters, rest)

| Landmark | Value |
|---|---|
| Hips | (0, 0, **0.998**) |
| Head | (0, 0, 1.60) |
| Hands | (±0.738, 0.062, 1.436) — wrist span 1.476 |
| Feet (ankle) | (±0.091, 0.026, **0.105**) ← ground contact height |
| Flat-foot ball / toe z | ~0.034 / ~0.031 |
| Ball-plant (heel up) toe z | ~0.03, ankle ~0.11 |

| Chain | Length (m) |
|---|---|
| Shoulder (clavicle) | 0.129 — you cannot "move the socket" more than this |
| Upper arm / forearm / hand | 0.274 / 0.276 / 0.110 |
| Thigh / shin / foot | 0.406 / 0.421 / 0.157 |
| Spine1 / Spine2 / Neck / Head | 0.135 / 0.123 / 0.108 / 0.196 |

Full-arm reach (arm+forearm) ≈ 0.55 m; with hand ≈ 0.66 m.
A ~90° knee puts the ankle ≈ `sqrt(thigh² + shin²)` ≈ 0.58 m from the
hip — closer than that folds the calf into the thigh.
