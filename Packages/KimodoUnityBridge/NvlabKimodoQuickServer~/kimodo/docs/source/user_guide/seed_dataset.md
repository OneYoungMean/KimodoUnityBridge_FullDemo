# Loading BONES-SEED BVH data

The [BONES-SEED dataset](https://huggingface.co/datasets/bones-studio/seed) is a publicly available optical motion-capture dataset distributed as BVH files with the [SOMA 77-joint skeleton](../key_concepts/skeleton.md). This page walks through the steps to parse a SEED BVH file and convert it into Kimodo's internal motion representation.

This is a similar pipeline used by the benchmark to extract ground-truth motions from SEED data (see the [benchmark pipeline](../benchmark/pipeline.md)).

## Step-by-Step Conversion

### 1. Parse the BVH file

`parse_bvh_motion` reads a BVH file and returns local joint rotation matrices, root translation (in meters), and the source frame rate.

```python
from kimodo.skeleton.bvh import parse_bvh_motion

local_rot_mats, root_trans, bvh_fps = parse_bvh_motion(bvh_path)
```

### 2. Subsample to 30 FPS

Kimodo operates at 30 Hz. If the source BVH has a different frame rate (120 FPS for BONES-SEED), subsample by striding:

```python
fps = 30
step = round(bvh_fps / fps)
root_trans = root_trans[::step]
local_rot_mats = local_rot_mats[::step]
```

### 3. Convert to the standard T-pose

The SEED BVH rest pose differs from Kimodo's canonical T-pose. The `to_standard_tpose` function remaps the local rotations accordingly and returns both local and global rotation matrices:

```python
from kimodo.skeleton import SOMASkeleton77

skeleton = SOMASkeleton77()
local_rot_mats, global_rot_mats = skeleton.to_standard_tpose(local_rot_mats)
```

### 4. Compute Kimodo motion features

Build the motion feature tensor used by the model. The feature layout is described in [Motion representation](../key_concepts/motion_representation.md).

```python
from kimodo.motion_rep import KimodoMotionRep

motion_rep = KimodoMotionRep(skeleton, fps)
feats = motion_rep(local_rot_mats, root_trans, to_normalize=False)
```

### 5. Canonicalize (optionally) and recover the motion dictionary

Canonicalize so that the motion starts at the origin facing +Z, then invert the features back into a full motion dictionary:

```python
can_feats = motion_rep.canonicalize(feats)
motion_dict = motion_rep.inverse(can_feats, is_normalized=False)
```

`motion_dict` is a dictionary with keys such as `local_rot_mats`, `global_rot_mats`, `posed_joints`, `root_positions`, `smooth_root_pos`, `foot_contacts`, etc. See [Output formats](output_formats.md) for details on the Kimodo NPZ layout.

## Full script

```python
from kimodo.motion_rep import KimodoMotionRep
from kimodo.skeleton import SOMASkeleton77
from kimodo.skeleton.bvh import parse_bvh_motion

# 1. Parse BVH
local_rot_mats, root_trans, bvh_fps = parse_bvh_motion(bvh_path)

# 2. Subsample to 30 fps
fps = 30
step = round(bvh_fps / fps)
root_trans = root_trans[::step]
local_rot_mats = local_rot_mats[::step]

# 3. Convert to standard T-pose
skeleton = SOMASkeleton77()
local_rot_mats, global_rot_mats = skeleton.to_standard_tpose(local_rot_mats)

# 4. Compute motion features
motion_rep = KimodoMotionRep(skeleton, fps)
feats = motion_rep(local_rot_mats, root_trans, to_normalize=False)

# 5. Canonicalize and get the full motion dictionary
can_feats = motion_rep.canonicalize(feats)
motion_dict = motion_rep.inverse(can_feats, is_normalized=False)
```
