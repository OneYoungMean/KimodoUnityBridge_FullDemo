# Metrics

The benchmark evaluates generated motion along three axes:

- **Motion quality** -- foot-skate and contact-consistency metrics,
- **Constraint following** -- position error for root, end-effector, and full-body constraints,
- **Text alignment** -- TMR retrieval and distributional metrics.

Metrics are implemented in `kimodo/metrics/` and orchestrated by `benchmark/evaluate_folder.py`.
The protocol is aligned with the [tech report](https://research.nvidia.com/labs/sil/projects/kimodo/assets/kimodo_tech_report.pdf) (Sec. 6.1, "Evaluation Metrics").

## Evaluation Protocol

The evaluation pipeline runs two passes over each group of test cases:

1. **Generated pass** -- evaluates `motion.npz` with all metrics (foot skate, contact consistency, constraint following) and, when TMR embeddings are available, computes retrieval and FID scores.
2. **Ground-truth pass** -- evaluates `gt_motion.npz` with the same motion-quality and constraint metrics. TMR retrieval metrics are not recomputed in this pass.

Running both passes enables side-by-side comparison: the GT row serves as an empirical upper bound for motion quality, and deviations between GT and generated metrics highlight where the model can improve. See [Evaluation pipeline](pipeline.md) for the full workflow.

## Metrics Reference

The table below lists every key written to `metrics.json`. Detailed descriptions follow in subsequent sections.

| Key | Category | Unit | Direction |
| --- | --- | --- | --- |
| `foot_skate_from_height` | Motion quality | m/s | Lower is better |
| `foot_skate_from_pred_contacts` | Motion quality | m/s | Lower is better |
| `foot_skate_max_vel` | Motion quality | m/s | Lower is better |
| `foot_skate_ratio` | Motion quality | ratio (0--1) | Lower is better |
| `foot_contact_consistency` | Motion quality | ratio (0--1) | Higher is better |
| `constraint_root2d_err` | Constraint follow | m | Lower is better |
| `constraint_root2d_err_p95` | Constraint follow | m | Lower is better |
| `constraint_root2d_acc` | Constraint follow | ratio (0--1) | Higher is better |
| `constraint_fullbody_keyframe` | Constraint follow | m | Lower is better |
| `constraint_end_effector` | Constraint follow | m | Lower is better |
| `TMR/t2m_sim` | Text alignment | score (0--1) | Higher is better |
| `TMR/t2m_R/R01` ... `R10` | Text alignment | % | Higher is better |
| `TMR/t2m_R/MedR` | Text alignment | rank | Lower is better |
| `TMR/FID/gen_text` | Text alignment | distance | Lower is better |
| `TMR/FID/gen_gt` | Text alignment | distance | Lower is better |
| `TMR/FID/gt_text` | Text alignment | distance | Lower is better |
| `TMR/m2m_sim` | Text alignment | score (0--1) | Higher is better |
| `TMR/t2m_gt_sim` | Text alignment | score (0--1) | Higher is better |
| `TMR/m2m_R/R01` ... `R10` | Text alignment | % | Higher is better |
| `TMR/t2m_gt_R/R01` ... `R10` | Text alignment | % | Higher is better |

:::{note}
Raw metric values are stored in SI units (meters for positions, m/s for velocities).
The summary tables printed by `benchmark/parse_folder.py` convert constraint position errors to **cm** and foot-skate velocities to **cm/s** for readability.
:::

### Foot Skating Metrics

Foot skating measures how much a foot slides along the ground when it should be in static contact with the ground. Four complementary metrics capture different aspects of this artifact.

- **`foot_skate_from_height`** (m/s, lower is better):
  Mean velocity of the **toe joints** (left toe, right toe) on frames where the toe height is below a floor threshold (`height_thresh = 0.05 m`).
  This metric does not rely on predicted contact labels -- it uses a geometric criterion (Y-coordinate < threshold) to identify ground-contact frames.

- **`foot_skate_from_pred_contacts`** (m/s, lower is better):
  Mean velocity of all **four foot joints** (left/right heel and toe) on frames where the model predicts contact via the `foot_contacts` output.
  Unlike `foot_skate_from_height`, this metric trusts the model's own contact predictions and measures all four foot joints rather than toes only.

- **`foot_skate_max_vel`** (m/s, lower is better):
  Maximum velocity across all four foot joints and all time steps where predicted contact is active.
  This captures worst-case slip spikes that mean-based metrics can hide.

- **`foot_skate_ratio`** (ratio 0--1, lower is better):
  Fraction of ground-contact frames where toe velocity exceeds a threshold (`vel_thresh = 0.2 m/s`). A frame counts as ground contact when the toe is below `height_thresh = 0.05 m` on both the current and the next frame. Inspired by the [GMD](https://github.com/korrawe/guided-motion-diffusion) skating metric.

### Contact Consistency Metric

- **`foot_contact_consistency`** (ratio 0--1, higher is better):
  Agreement between the model's predicted foot contacts and a heuristic contact detector based on joint height and velocity (`vel_thresh = 0.15 m/s`, `height_thresh = 0.10 m`).
  Computed as accuracy (`1 - incorrect_ratio`) over all time steps and four contact channels.
  A score of 1.0 means perfect agreement between predicted and heuristic contacts.
  As noted in the [tech report](https://research.nvidia.com/labs/sil/projects/kimodo/assets/kimodo_tech_report.pdf), this metric provides important context for interpreting the contact-based foot-skate metrics above: if contact consistency is low, `foot_skate_from_pred_contacts` may be unreliable.

### Constraint-Following Metrics

Constraint metrics are computed only when the test case includes a `constraints.json` file. The `ContraintFollow` metric class dispatches by [constraint type](../key_concepts/constraints.md):

- **`constraint_end_effector`** (m, lower is better):
  Mean Euclidean distance between target end-effector positions and generated joint positions at the constrained frames.
  Only position-constrained joints are evaluated (rotation targets are not measured by this metric).

- **`constraint_fullbody_keyframe`** (m, lower is better):
  Mean per-joint Euclidean distance between target and generated full-body joint positions at keyframes.
  The error is averaged over all joints and all keyframe frames.

- **`constraint_root2d_err`** (m, lower is better):
  Mean 2D Euclidean distance (in the XZ ground plane) between target and generated root positions at constrained frames.

- **`constraint_root2d_err_p95`** (m, lower is better):
  95th percentile of the per-frame root 2D error across all samples in a group.
  Computed during aggregation by `evaluate_folder.py` to capture tail-end failures that the mean can mask.

- **`constraint_root2d_acc`** (ratio 0--1, higher is better):
  Fraction of constrained root frames where the 2D position error is within a distance threshold (`root_threshold = 0.10 m`).

### TMR-Based Metrics

Text alignment is evaluated using [TMR](https://mathis.petrovich.fr/tmr/) (Text-to-Motion Retrieval), a separate encoder model that maps both text and motion into a shared embedding space. TMR is not used for generation -- it is loaded only for evaluation (see `kimodo/model/tmr.py`).

We release a version of TMR retrained on the full Rigplay dataset as [`TMR-SOMA-RP-v1`](https://huggingface.co/nvidia/TMR-SOMA-RP-v1). The original TMR was trained on HumanML3D; our retrained variant uses the same architecture but is trained on the Rigplay motion dataset, SOMA skeleton, and with [LLM2Vec](https://github.com/McGill-NLP/llm2vec) text embeddings.

#### Similarity Scores

TMR encodes each text prompt and each motion clip into a unit-length embedding vector. Cosine similarity between text and motion embeddings is rescaled to a [0, 1] range:

```
score = cosine_similarity / 2 + 0.5
```

Three per-test-case similarity scores are recorded:

- **`TMR/t2m_sim`** (0--1, higher is better): similarity between the text prompt and the generated motion.
- **`TMR/m2m_sim`** (0--1, higher is better): similarity between the generated and ground-truth motions (only when GT is available).
- **`TMR/t2m_gt_sim`** (0--1, higher is better): similarity between the text prompt and the GT motion (only when GT is available).

#### R-precision (Retrieval Accuracy)

R-precision measures whether the correct motion can be retrieved from a pool given its corresponding text query.
For each text query in the evaluation group, all motions are ranked by TMR similarity.
R@k is the percentage of queries where the correct motion appears in the top k results.

Reported keys: `TMR/t2m_R/R01`, `R02`, `R03`, `R05`, `R10` (%), and `TMR/t2m_R/MedR` (median rank, lower is better) correspond to retrieval accuracy when using generated motions.

When ground-truth motions are available, analogous retrieval metrics are computed for motion-to-GT-motion (`TMR/m2m_R/...`) and text-to-GT-motion (`TMR/t2m_gt_R/...`).

:::{note}
Near-duplicate text prompts can artificially penalize retrieval ranking. The evaluation handles this by grouping prompts whose text-text similarity exceeds a threshold of 0.99 and treating any motion in that group as a valid match.
:::

#### FID (Frechet Inception Distance)

FID measures distributional distance between two sets of TMR embeddings by fitting a multivariate Gaussian to each set and computing the Frechet distance. Three FID variants are reported:

- **`TMR/FID/gen_gt`**: distance between generated-motion and GT-motion embeddings (only when GT is available). This is the FID metric that is typically reported in the motion generation literature.
- **`TMR/FID/gen_text`**: distance between generated-motion embeddings and text embeddings. 
- **`TMR/FID/gt_text`**: distance between GT-motion and text embeddings (only when GT is available).

Lower values indicate that the two distributions are more similar. FID requires at least 2 samples; groups with fewer samples report `NaN`.

#### Per-Test-Case Retrieval

In addition to the aggregate metrics above, each test case's `metrics.json` includes a `tmr` block with single motion retrieval results:

- `t2m_rank`: the rank of the correct motion when retrieving with this test case's text query.
- `top5_retrieved`: the top-5 retrieved motions (sample IDs and text prompts) for inspection.

## JSON Output Format

Below is a representative `metrics.json` written by `evaluate_folder.py` for a single test case with mixed constraints (root + end-effector + full-body) and TMR embeddings:

```json
{
  "num_motions": 1,
  "folder": "...",
  "per_motion_mean_gen": {
    "foot_skate_from_height": 0.3144,
    "foot_skate_from_pred_contacts": 0.0672,
    "foot_skate_max_vel": 0.2109,
    "foot_contact_consistency": 0.9522,
    "foot_skate_ratio": 0.2182,
    "constraint_end_effector": 0.0286,
    "constraint_root2d_err": 0.0534,
    "constraint_root2d_acc": 1.0,
    "constraint_fullbody_keyframe": 0.0324,
    "TMR/t2m_sim": 0.8209
  },
  "per_motion_mean_gt": {
    "foot_skate_from_height": 0.2361,
    "foot_skate_from_pred_contacts": 0.0269,
    "foot_skate_max_vel": 0.1459,
    "foot_contact_consistency": 1.0,
    "foot_skate_ratio": 0.1402,
    "constraint_end_effector": 9.82e-07,
    "constraint_root2d_err": 0.0407,
    "constraint_root2d_acc": 1.0,
    "constraint_fullbody_keyframe": 8.73e-07
  },
  "tmr": {
    "t2m_rank": 2,
    "text": "A person is swiftly performing a dance move by moving their hands and legs.",
    "top5_retrieved": [
      {
        "id": "0231",
        "text": "A person is performing dance steps while stepping back and forward..."
      },
      {
        "id": "0029",
        "text": "A person is swiftly performing a dance move by moving their hands and legs."
      }
    ]
  }
}
```

Group-level aggregate JSONs (`<group_name>.json`) have the same structure but with `num_motions > 1`, averaged per-motion metrics, additional keys like `constraint_root2d_err_p95`, and a `tmr` block containing the aggregate retrieval and FID scores:

```json
{
  "num_motions": 256,
  "folder": "...",
  "per_motion_mean_gen": {
    "foot_skate_from_height": 0.1742,
    "foot_skate_from_pred_contacts": 0.0611,
    "foot_skate_max_vel": 0.3747,
    "foot_contact_consistency": 0.9483,
    "foot_skate_ratio": 0.1499,
    "constraint_end_effector": 0.0367,
    "constraint_root2d_err": 0.0495,
    "constraint_root2d_acc": 0.9212,
    "constraint_fullbody_keyframe": 0.0324,
    "constraint_root2d_err_p95": 0.1115
  },
  "per_motion_mean_gt": {
    "foot_skate_from_height": 0.1617,
    "foot_skate_from_pred_contacts": 0.0235,
    "foot_skate_max_vel": 0.1185,
    "foot_contact_consistency": 1.0,
    "foot_skate_ratio": 0.1214,
    "constraint_end_effector": 1.48e-06,
    "constraint_root2d_err": 0.0376,
    "constraint_root2d_acc": 1.0,
    "constraint_fullbody_keyframe": 1.16e-06,
    "constraint_root2d_err_p95": 0.0602
  },
  "tmr": {
    "TMR/t2m_sim": 0.8742,
    "TMR/t2m_R/R01": 75.39,
    "TMR/t2m_R/R02": 85.55,
    "TMR/t2m_R/R03": 88.28,
    "TMR/t2m_R/R05": 90.23,
    "TMR/t2m_R/R10": 93.36,
    "TMR/t2m_R/MedR": 1.0,
    "TMR/t2m_R/len": 256.0,
    "TMR/FID/gen_text": 0.1442,
    "TMR/m2m_R/R01": 94.53,
    "TMR/m2m_R/R02": 97.66,
    "TMR/m2m_R/R03": 98.05,
    "TMR/m2m_R/R05": 98.83,
    "TMR/m2m_R/R10": 99.22,
    "TMR/m2m_R/MedR": 1.0,
    "TMR/m2m_R/len": 256.0,
    "TMR/t2m_gt_R/R01": 80.47,
    "TMR/t2m_gt_R/R02": 88.28,
    "TMR/t2m_gt_R/R03": 91.02,
    "TMR/t2m_gt_R/R05": 92.58,
    "TMR/t2m_gt_R/R10": 94.53,
    "TMR/t2m_gt_R/MedR": 1.0,
    "TMR/t2m_gt_R/len": 256.0,
    "TMR/FID/gen_gt": 0.0387,
    "TMR/FID/gt_text": 0.1349
  }
}
```
