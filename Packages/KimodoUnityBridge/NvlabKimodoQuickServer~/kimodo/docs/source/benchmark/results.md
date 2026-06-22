# Kimodo Results

On this page, we report the results for the latest Kimodo models on the benchmark test suite. These results are reproducible with the [evaluation pipeline](pipeline.md) and should be used when comparing against other models. Note that the reported numbers differ from the numbers in the [tech report](https://research.nvidia.com/labs/sil/projects/kimodo/assets/kimodo_tech_report.pdf) (Sec. 6) due to differences in skeleton, test suite composition, and evaluation details.

To reproduce these results or evaluate your own model, follow the [evaluation pipeline](pipeline.md) and use `parse_folder --format md` to generate summary tables in markdown format.

**Note on reproducibility**: to exactly reproduce the results in the tables below, use batch size 1 when generating with Kimodo (i.e., when running `generate_eval.py`). This way, every test case is individually seeded according to `meta.json`. The reported results were computed using LLM2Vec in the default `bfloat16` precision. However, the Kimodo-SOMA-v1.1 and TMR models were actually trained with `float32` embeddings, so if you want to get the best possible performance (and you have enough VRAM), you can include `--text_encoder_fp32` when running the generation and embedding steps, even though the results will not match the tables here.

Results are reported on the two splits described in [the introduction](introduction.md#dataset-splits):

- **Content**: test cases with novel semantic content not present in training (e.g. unseen action categories).
- **Repetition**: content categories seen during training, but specific motion clips are held out and unseen. Note that due to the annotations in Bones Rigplay and SEED datasets, the text prompts in this test split have already been seen during training.

For each split, we also report metrics for the ground truth motion. These rows serve as an empirical upper bound for motion quality, and deviations between ground truth and generated metrics highlight where the model can improve.

We split results for each model into two tables corresponding to different test cases in the test suite:

- **Text-Following**: `overview`, `timeline_single`, and `timeline_multi`
- **Constrained**: `constraints_withtext`, `constraints_notext`

<!-- 
## Kimodo-SOMA-SEED-v1.1
These results are for the Kimodo model trained on the public [BONES-SEED](https://huggingface.co/datasets/bones-studio/seed) dataset. The results are comparable to any model trained on SEED that uses our recommended splits [described in the introduction](introduction.md#dataset-splits).

### Text-Following Evaluation

|  | Overview R@3↑ | Overview FID↓ | Overview Skate↓ | Overview Contact↑ | Timeline single R@3↑ | Timeline single FID↓ | Timeline single Skate↓ | Timeline single Contact↑ | Timeline multi R@3↑ | Timeline multi FID↓ | Timeline multi Skate↓ | Timeline multi Contact↑ |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| **Content** Ground Truth | 89.09 | 0.000 | 1.849 | 1.000 | 86.26 | 0.000 | 1.789 | 1.000 | 88.47 | 0.000 | 1.711 | 1.000 |
| **Content** Kimodo | 81.13 | 0.035 | 4.077 | 0.977 | 73.17 | 0.028 | 3.873 | 0.980 | 80.10 | 0.032 | 3.685 | 0.981 |
| **Repetition** Ground Truth | 93.91 | 0.000 | 2.106 | 1.000 | 90.13 | 0.000 | 2.037 | 1.000 | 94.49 | 0.000 | 1.931 | 1.000 |
| **Repetition** Kimodo | 90.92 | 0.004 | 4.573 | 0.972 | 80.38 | 0.007 | 4.442 | 0.976 | 92.58 | 0.006 | 4.199 | 0.974 |


### Constrained Evaluation

|  | With text FB Pos↓ | With text EE Pos↓ | With text EE Rot↓ | With text 2D Root↓ | With text Pelvis@95% | Without text FB Pos↓ | Without text EE Pos↓ | Without text EE Rot↓ | Without text 2D Root↓ | Without text Pelvis@95% |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| **Content** Ground Truth | 0.000 | 0.000 | - | 2.362 | 3.30 | 0.000 | 0.000 | - | 2.408 | 3.33 |
| **Content** Kimodo | 1.316 | 1.762 | - | 3.064 | 5.62 | 1.277 | 1.691 | - | 2.952 | 5.56 |
| **Repetition** Ground Truth | 0.000 | 0.000 | - | 2.220 | 3.35 | 0.000 | 0.000 | - | 2.195 | 3.34 |
| **Repetition** Kimodo | 1.226 | 1.778 | - | 2.913 | 5.65 | 1.200 | 1.620 | - | 2.624 | 4.85 |


## Kimodo-SOMA-RP-v1.1
These results are for the Kimodo model trained on the full (proprietary) Bones Rigplay dataset which is a superset of BONES-SEED. Though the training split is larger, the model is not trained on the SEED test splits to ensure a fair comparison.

### Text-Following Evaluation

|  | Overview R@3↑ | Overview FID↓ | Overview Skate↓ | Overview Contact↑ | Timeline single R@3↑ | Timeline single FID↓ | Timeline single Skate↓ | Timeline single Contact↑ | Timeline multi R@3↑ | Timeline multi FID↓ | Timeline multi Skate↓ | Timeline multi Contact↑ |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| **Content** Ground Truth | 89.09 | 0.000 | 1.849 | 1.000 | 86.26 | 0.000 | 1.789 | 1.000 | 88.47 | 0.000 | 1.711 | 1.000 |
| **Content** Kimodo | 83.32 | 0.025 | 3.641 | 0.982 | 78.08 | 0.026 | 3.523 | 0.984 | 84.79 | 0.028 | 3.278 | 0.985 |
| **Repetition** Ground Truth | 93.91 | 0.000 | 2.106 | 1.000 | 90.13 | 0.000 | 2.037 | 1.000 | 94.49 | 0.000 | 1.931 | 1.000 |
| **Repetition** Kimodo | 87.90 | 0.008 | 4.103 | 0.977 | 77.02 | 0.011 | 3.938 | 0.981 | 88.59 | 0.009 | 3.727 | 0.980 |


### Constrained Evaluation

|  | With text FB Pos↓ | With text EE Pos↓ | With text EE Rot↓ | With text 2D Root↓ | With text Pelvis@95% | Without text FB Pos↓ | Without text EE Pos↓ | Without text EE Rot↓ | Without text 2D Root↓ | Without text Pelvis@95% |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| **Content** Ground Truth | 0.000 | 0.000 | - | 2.362 | 3.30 | 0.000 | 0.000 | - | 2.408 | 3.33 |
| **Content** Kimodo | 1.126 | 1.398 | - | 2.819 | 4.78 | 1.129 | 1.382 | - | 2.714 | 4.54 |
| **Repetition** Ground Truth | 0.000 | 0.000 | - | 2.220 | 3.35 | 0.000 | 0.000 | - | 2.195 | 3.34 |
| **Repetition** Kimodo | 1.078 | 1.377 | - | 2.621 | 4.69 | 1.088 | 1.370 | - | 2.478 | 4.44 | 
-->


## Quantitative Results

Results are reported for two models:

- **Kimodo-SOMA-SEED-v1.1**:  trained on the public [BONES-SEED](https://huggingface.co/datasets/bones-studio/seed) dataset. The results are comparable to any model trained on SEED that uses our recommended splits [described in the introduction](introduction.md#dataset-splits).
- **Kimodo-SOMA-RP-v1.1**: trained on the full (proprietary) Bones Rigplay dataset which is a superset of BONES-SEED. Though the training split is larger, the model is not trained on the SEED test splits to ensure a fair comparison.

### Text-Following Evaluation

|  | Overview R@3↑ | Overview FID↓ | Overview Skate↓ | Overview Contact↑ | Timeline single R@3↑ | Timeline single FID↓ | Timeline single Skate↓ | Timeline single Contact↑ | Timeline multi R@3↑ | Timeline multi FID↓ | Timeline multi Skate↓ | Timeline multi Contact↑ |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| **Content** Ground Truth | 89.09 | 0.000 | 1.849 | 1.000 | 86.26 | 0.000 | 1.789 | 1.000 | 88.47 | 0.000 | 1.711 | 1.000 |
| **Content** Kimodo-SOMA-SEED-v1.1 | 81.13 | 0.035 | 4.077 | 0.977 | 73.17 | 0.028 | 3.873 | 0.980 | 80.10 | 0.032 | 3.685 | 0.981 |
| **Content** Kimodo-SOMA-RP-v1.1 | 83.32 | 0.025 | 3.641 | 0.982 | 78.08 | 0.026 | 3.523 | 0.984 | 84.79 | 0.028 | 3.278 | 0.985 |
| **Repetition** Ground Truth | 93.91 | 0.000 | 2.106 | 1.000 | 90.13 | 0.000 | 2.037 | 1.000 | 94.49 | 0.000 | 1.931 | 1.000 |
| **Repetition** Kimodo-SOMA-SEED-v1.1 | 90.92 | 0.004 | 4.573 | 0.972 | 80.38 | 0.007 | 4.442 | 0.976 | 92.58 | 0.006 | 4.199 | 0.974 |
| **Repetition** Kimodo-SOMA-RP-v1.1 | 87.90 | 0.008 | 4.103 | 0.977 | 77.02 | 0.011 | 3.938 | 0.981 | 88.59 | 0.009 | 3.727 | 0.980 |

### Constrained Evaluation

|  | With text FB Pos↓ | With text EE Pos↓ | With text EE Rot↓ | With text 2D Root↓ | With text Pelvis@95% | Without text FB Pos↓ | Without text EE Pos↓ | Without text EE Rot↓ | Without text 2D Root↓ | Without text Pelvis@95% |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| **Content** Ground Truth | 0.000 | 0.000 | - | 2.362 | 3.30 | 0.000 | 0.000 | - | 2.408 | 3.33 |
| **Content** Kimodo-SOMA-SEED-v1.1 | 1.316 | 1.762 | - | 3.064 | 5.62 | 1.277 | 1.691 | - | 2.952 | 5.56 |
| **Content** Kimodo-SOMA-RP-v1.1 | 1.126 | 1.398 | - | 2.819 | 4.78 | 1.129 | 1.382 | - | 2.714 | 4.54 |
| **Repetition** Ground Truth | 0.000 | 0.000 | - | 2.220 | 3.35 | 0.000 | 0.000 | - | 2.195 | 3.34 |
| **Repetition** Kimodo-SOMA-SEED-v1.1 | 1.226 | 1.778 | - | 2.913 | 5.65 | 1.200 | 1.620 | - | 2.624 | 4.85 |
| **Repetition** Kimodo-SOMA-RP-v1.1 | 1.078 | 1.377 | - | 2.621 | 4.69 | 1.088 | 1.370 | - | 2.478 | 4.44 |