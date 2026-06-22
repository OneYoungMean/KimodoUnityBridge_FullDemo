# Constraints JSON Format

The `--constraints` flag in the CLI expects a JSON file containing a list of constraint objects.
It is easiest to look at the examples provided with the demo to see how these are formatted. These can be seen for various model types in `kimodo/assets/demo/examples`.

> Tip: the easiest way to get a valid constraints file is to create constraints in the interactive demo and to click on `Save Constraints`.

## High-Level Structure

- The file is a JSON array: `[{...}, {...}, ...]`
- Each element is an object with at least:
  - `type` (string)
    - `root2d`, `fullbody`, `left-hand`, `right-hand`, `left-foot`, `right-foot`, `end-effector`
  - `frame_indices` (array of integers): 0-based frame indices within the generated clip.


```{note}
For SOMA models, constraints may be authored or displayed on the full `somaskel77` skeleton, but Kimodo converts them to the reduced `somaskel30` representation before passing them to the model. See the [skeleton](../key_concepts/skeleton.md) section for more details.
```

## Coordinate Space and Units

All spatial values in constraints use the same coordinate system as Kimodo's internal motion representation:

- **Axes**: **Y-up**, with locomotion on the **XZ ground plane**. The Y axis points up, X and Z span the horizontal ground plane.
- **Units**: **Meters**. Joint positions, root translations, and 2D root coordinates are all in meters.

### Canonicalization

During training, every motion is *canonicalized* so that the (smoothed) root starts at the XZ origin `(0, 0)` at frame 0.
The initial body heading (facing direction) is randomly rotated and passed to the model as an explicit input (`first_heading_angle`), so the model is robust to arbitrary initial orientations.

At inference, constraints should be authored **relative to this canonical origin**:
- `smooth_root_2d` values at frame 0 should be at `(0, 0)`, with subsequent frames expressing displacement from there.
- `root_positions` XZ components follow the same convention; Y is the **absolute hip height above the ground** (typically ~0.9 m for a standing pose, lower for crouching/sitting).
- `first_heading_angle` (a generation parameter, not part of the constraints JSON) defaults to `0.0` radians (facing +Z) but can be set to any value to change the initial facing direction.

### Field-specific notes

| Field | Space | Notes |
|-------|-------|-------|
| `smooth_root_2d` | `[x, z]` ground plane (meters) | Relative to the canonical origin. |
| `root_positions` | `[x, y, z]` (meters) | Y is absolute hip height above ground. XZ relative to canonical origin. |
| `global_root_heading` | `[cos(θ), sin(θ)]` | **Not** a raw radian value — must be a 2-element cosine/sine pair per frame (i.e. the heading direction vector). |
| `local_joints_rot` | axis-angle (radians) | Local joint rotations in the skeleton's rest-pose frame. |

### Constraints not at frame 0

Adding a constraint at frame 0 is **not** required. If the first constrained frame is later in the sequence (e.g. frame 45), Kimodo generates the initial frames freely from its learned distribution, starting near XZ = (0, 0) with the heading set by `first_heading_angle`. The constraint just needs to be reachable from that starting configuration given the text prompt and motion duration.

## Constraint Types
Depending on `type`, additional fields are required or optional. All numeric arrays are plain nested JSON lists. In the following definitions `T` is the number of constrainted frames (i.e., number of `frame_indices`) and `J` is the number of skeleton joints.


### `root2d`
This captures 2D root waypoints and 2D root paths. It requires:

- `smooth_root_2d` (array shapes `[T, 2]`): Smoothed root positions `[x, z]` on the ground plane at the given `frame_indices`.

and optionally:
- `global_root_heading` (array shapes `[T, 2]`): Global root heading direction `[cos, sin]` at the given `frame_indices`.

### `fullbody`
This captures full-body keyframe constraints on joint positions. It includes:

- `local_joints_rot` (array shaped `[T, J, 3]`): Per-frame per-joint **axis-angle** local rotations (radians). Constraint joint positions will be derived from these.
- `root_positions` (array shaped `[T, 3]`): Root (hips) translation `[x, y, z]`.
- `smooth_root_2d` (optional; array of `[T, 2]`): Smoothed root positions `[x, z]`. If omitted, it is taken as the `[x, z]` components of `root_positions`.

Note the `local_joint_rot` will not explicitly be constrained, the constraint will be on the joint positions that results from FK with the given joint rotations.

### `left-hand` / `right-hand` / `left-foot` / `right-foot`
Captures end-effector constraints on the hand/feet joint positions and global rotations.

These use the same fields as `fullbody`. However, under the hood these will only affect the corresponding end-effectors and hips. Each of these types is a shorthand for `end-effector` with pre-set joint names.

### `end-effector`
A general end-effector constraint that requires an additional field:

- `joint_names` (array of strings): Which end-effectors to constrain (e.g. `["left_hand"]`, `["right_foot", "left_foot"]`). Available names depend on the skeleton; see the skeleton's `expand_joint_names()` for the full mapping.

Otherwise uses the same fields as `fullbody` (`local_joints_rot`, `root_positions`, optional `smooth_root_2d`).

## Examples

### Root 2D waypoints

```json
[
  {
    "type": "root2d",
    "frame_indices": [0, 30, 60],
    "smooth_root_2d": [[0.0, 0.0], [0.5, 0.0], [1.0, 0.1]]
  }
]
```

### Full-body keyframe

```json
[
  {
    "type": "fullbody",
    "frame_indices": [60],
    "root_positions": [[0.0, 0.96, 1.5]],
    "local_joints_rot": [[[0.0, 0.0, 0.0], "... one [3] per joint ..."]]
  }
]
```

Here `root_positions` places the hips at x=0, y=0.96 m (standing height), z=1.5 m forward from the origin. `local_joints_rot` is a `[T, J, 3]` array of axis-angle rotations for every joint in the skeleton.
