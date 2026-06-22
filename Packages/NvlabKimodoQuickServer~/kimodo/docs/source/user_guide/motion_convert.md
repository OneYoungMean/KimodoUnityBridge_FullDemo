# Motion Format Conversion

The `kimodo_convert` command converts between the formats described in [Output formats](output_formats.md): **Kimodo NPZ**, **AMASS NPZ** (SMPL-X), **SOMA BVH**, and **G1 MuJoCo CSV**.

## Frame rate (30 Hz Kimodo NPZ)

Any conversion **to Kimodo NPZ** (from AMASS, SOMA BVH, or G1 CSV) **writes motion at 30 Hz**, matching Kimodo’s common generation rate. If the detected source rate differs, the tool **resamples** along time, then derived channels (contacts, smooth root, heading) are recomputed via forward kinematics.

If resampling is required, a **warning** is emitted with the assumed source rate, input/output frame counts, and a reminder that `--source-fps` sets the **source** rate if autodetection is wrong. When the source is already ~30 Hz with the same frame count, no warning is shown (motion is only re-derived via FK for consistency).

<details>
<summary>Resampling strategy details</summary>

The resampler picks one of two strategies based on the ratio `source_fps / target_fps`:

- **Integer-ratio fast path** — When the ratio is close to an integer ≥ 2 (within a tolerance of 0.05), the resampler simply takes every *step*-th frame (`frames[::step]`). For example, 120 Hz → 30 Hz has ratio 4, so every 4th frame is kept. This is exact and very fast.
- **Interpolation fallback** — Otherwise, the output timeline is linearly spaced over the input range. Root positions are linearly interpolated, and local joint rotations are interpolated via quaternion slerp. This handles arbitrary rate conversions (e.g. 50 Hz → 30 Hz).

In both cases, `complete_motion_dict` is re-run at the target rate so that all derived channels (velocities, foot contacts, heading, smooth root) stay consistent with the new frame spacing.

</details>

## Usage

```bash
kimodo_convert INPUT OUTPUT [options]
```

Formats are inferred from file extensions and (for `.npz`) from file contents. You can override with `--from` and `--to`.

### Supported conversions

| From | To | Notes |
|------|-----|--------|
| AMASS `.npz` | Kimodo `.npz` | SMPL-X, 22 joints. Uses `--z-up` by default (same as Kimodo’s AMASS export). |
| Kimodo `.npz` | AMASS `.npz` | Requires `local_rot_mats` with 22 joints (SMPL-X). |
| SOMA `.bvh` | Kimodo `.npz` | Expects a **Kimodo-exported** SOMA BVH (same hierarchy as `save_motion_bvh`). If the BVH uses the standard T-pose as rest pose, pass in `--bvh_standard_tpose`. |
| Kimodo `.npz` | SOMA `.bvh` | Accepts 77 joints (SOMA full) or 30 joints (somaskel30, auto-expanded to 77 with relaxed-hand rest poses). If you want the output BVH to use the standard T-pose as rest pose, pass in `--bvh_standard_tpose`. |
| G1 `.csv` | Kimodo `.npz` | Rows of shape `(36,)` = root xyz + root quat + 29 joint angles (see [output_formats](output_formats.md#csv-format-for-kimodo-g1)). |
| Kimodo `.npz` | G1 `.csv` | Requires 34 joints (G1). |

### Common options

- **`--source-fps`**: Source motion frame rate in Hz (used before resampling to 30 Hz for Kimodo NPZ). If omitted, the tool auto-detects from `mocap_frame_rate` (AMASS), `Frame Time` (BVH), or defaults to **30** Hz. The legacy `--fps` alias is still accepted for backward compatibility.
- **`--no-z-up`**: For AMASS, disable the Y-up ↔ Z-up transform (treat data as already in Kimodo Y-up, +Z forward).
- **`--mujoco-rest-zero`**: For G1 CSV, match the `mujoco_rest_zero` flag used when the CSV was written (see `MujocoQposConverter.dict_to_qpos`).
- **`--bvh_standard_tpose`**: If input or output is BVH: the BVH file uses the standard T-pose as its rest pose instead of the BONES-SEED rest pose.

### Examples

```bash
# AMASS → Kimodo NPZ
kimodo_convert motion_amass.npz motion_kimodo.npz

# Kimodo NPZ → AMASS
kimodo_convert motion_kimodo.npz motion_out_amass.npz

# Kimodo SOMA NPZ → BVH
kimodo_convert motion_kimodo.npz motion.bvh

# BVH → Kimodo NPZ
kimodo_convert motion.bvh motion_kimodo.npz

# G1 CSV → Kimodo NPZ
kimodo_convert motion.csv motion_kimodo.npz

# Kimodo G1 NPZ → CSV
kimodo_convert motion_kimodo.npz motion.csv
```

When both input and output are `.npz`, the tool assumes **AMASS → Kimodo** if the input is AMASS, and **Kimodo → AMASS** if the input is already a Kimodo NPZ. Use `--from` / `--to` if you need to disambiguate.

## Limitations

- **BVH import** is intended for BVHs produced by Kimodo (`Root` wrapper + SOMA77 joint names) and is also compatible with the BONES-SEED dataset, which uses the same skeleton hierarchy. Arbitrary BVH files with different joint names or hierarchies may not work.
- **G1 CSV** encodes only the degrees of freedom exposed in MuJoCo; the inverse path reconstructs local rotations from those angles (same convention as `to_qpos`).
