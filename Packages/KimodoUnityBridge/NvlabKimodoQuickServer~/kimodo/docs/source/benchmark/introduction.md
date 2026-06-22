# Benchmark Introduction

We provide a benchmark to evaluate text-to-motion and constrained motion generation on a shared test suite.
For reproducibility, all test content is stored on disk as folders and files, so anyone can run exactly the same cases.
The benchmark test suite is available to download from HuggingFace at [`nvidia/Kimodo-Motion-Gen-Benchmark`](https://huggingface.co/datasets/nvidia/Kimodo-Motion-Gen-Benchmark) and is currently set up for use with models trained on the [SOMA](https://github.com/NVlabs/SOMA-X) body skeleton.

The benchmark contains text prompts, durations, and constraint configurations for a variety of test cases, but **not** the ground-truth motion data itself. The ground-truth motions are derived from the [BONES-SEED dataset](https://huggingface.co/datasets/bones-studio/seed), which has its own license you should consider. So to construct the full benchmark motions, you must download the BONES-SEED dataset separately and run our `create_benchmark` script to populate the test suite with ground-truth motions. 

Constructing the benchmark with `create_benchmark` is the first step in the full [Evaluation Pipeline](pipeline.md), which is described in detail on the next page. In addition to the benchmark test cases, we provide code to run generation with Kimodo and compute a variety of [metrics](metrics.md) measuring motion quality, text alignment, and constraint following. While this open-sourced public test suite is not the exact same used in the [Kimodo tech report](https://research.nvidia.com/labs/sil/projects/kimodo/assets/kimodo_tech_report.pdf) (Sec. 6.1), the evaluation metrics are the same and evaluation methodology is similar.

On this page, we describe the overall structure of the test suite and details of the different test cases. Then in subsequent pages, we describe how to run the full [evaluation pipeline](pipeline.md), detail the [metrics](metrics.md), and finally provide the [results](results.md) of Kimodo-SOMA-RP and Kimodo-SOMA-SEED on the benchmark.

## Dataset Splits
To evaluate a model on the benchmark, it should be trained with the [provided splits](https://huggingface.co/datasets/nvidia/Kimodo-Motion-Gen-Benchmark/tree/main/splits) for the [BONES-SEED dataset](https://huggingface.co/datasets/bones-studio/seed).

The different splits are defined in:

- `train_split_paths.txt` - filenames of training data
- `test_content_split_paths.txt` - filenames for test split containing new semantic "content". This split contains motions with `content_name` (from the BONES-SEED metadata) that are not seen in the training split. This tests model generalization to new semantic motion types, e.g. for text-to-motion generalization.
- `test_repetition_split_paths.txt` - filenames for test split containing new motions from content that was seen in training. This split contains motions where the `content_name` is contained in the training split, but the exact motion itself was not seen. This tests a model's ability to generalize to novel performances of a familiar motion type, e.g., for constraint-following generalization.

The training split should be used for training, while the two test splits (`content` and `repetition`) are used in the test suite, as described below. Note that the test cases in the benchmark do not cover the entire content and repetition test splits, instead we strategically sample a subset that maximizes content diversity.

## Test Suite Structure

The full test suite contains 22,474 test cases spanning text and constraint-conditioned motion generation. 
The suite is organized hierarchically to logically group together test cases, so the evaluation pipeline can be run on a subset of the benchmark instead of the full thing, if desired.

After the benchmark has been constructed and motions generated for the model to evaluate, a **test case** is a single folder containing:

- `meta.json` (**required**): text prompt(s) and duration(s),
- `constraints.json` (**optional**): constraints for controlled generation, using the [constraints format](../user_guide/constraints.md),
- `gt_motion.npz` (**optional**): ground-truth/reference motion, using the [NPZ output format](../user_guide/output_formats.md),
- `motion.npz` (**optional**): output of the model given the `meta.json` prompt/duration and optional `constraints.json`, using the same [NPZ output format](../user_guide/output_formats.md).

In addition to being used in the evaluation pipeline, each test case can be:

- loaded in the interactive demo through **Load Example** for visualization,
- loaded in `kimodo_gen` with `--input_folder` for generation from folder-defined inputs.

### Benchmark Folder Hierarchy

The full suite is organized as follows:

```text
testsuite
├── content
│   ├── constraints_notext
│   │   ├── end-effectors
│   │   ├── fullbody
│   │   ├── mixture
│   │   └── root
│   ├── constraints_withtext
│   │   ├── end-effectors
│   │   ├── fullbody
│   │   ├── mixture
│   │   └── root
│   └── text2motion
│       ├── overview
│       ├── timeline_multi
│       └── timeline_single
└── repetition
    ├── constraints_notext
    │   ├── end-effectors
    │   ├── fullbody
    │   ├── mixture
    │   └── root
    ├── constraints_withtext
    │   ├── end-effectors
    │   ├── fullbody
    │   ├── mixture
    │   └── root
    └── text2motion
        ├── overview
        ├── timeline_multi
        └── timeline_single
```

At the highest level, the test suite is organized by the test split used. As discussed previously, `content` refers to the test split with held out semantic categories of motion, while `repetition` refers to held out motions from semantic categories seen during training. 

Within each test split, test cases are organized into:

* `text2motion`: test cases with only text prompts as input (no constraints)
* `constraints_notext`:  test cases with only constraints as input (no text prompt)
* `constraints_withtext`: test cases with both prompt and constraints as input

### Text2Motion Test Cases

These test cases are pure text-to-motion with no constraints as input. `text2motion` test cases exclusively use prompts derived from our [SEED timeline annotations](https://huggingface.co/datasets/nvidia/SEED-Timeline-Annotations). It contains three types of test cases:

* `overview`: medium-detail prompt that describes a full motion. Corresponds to `overview_description` in the [NVIDIA SEED timelines](https://huggingface.co/datasets/nvidia/SEED-Timeline-Annotations) or equivalently `content_natural_desc_4` in the [BONES SEED](https://huggingface.co/datasets/bones-studio/seed) metadata.
* `timeline_single`: fine-grained prompt describing a single segment of a timeline annotation. Corresponds to a single event in a SEED timeline.
* `timeline_multi`: fine-grained prompt describing multiple subsequent segments of a timeline annotation. Corresponds to multiple contiguous events in a SEED timeline, which have been concatenated with an LLM to get a single natural text description.

### Constrained Test Cases

Constrained test cases provide a constraint input either without a text prompt (i.e., `constraints_notext`) or with an `overview` text prompt (i.e., `constraints_withtext`). The different types of constraint categories mirror the [constraint types support by Kimodo](../key_concepts/constraints.md) and include:

* `fullbody`: constrains all joint positions in the skeleton at specific frames
* `end-effectors`: constraints the position and rotations of hand and/or feet joints at specific frames
* `root`: constraints the 2D root position and optionally heading on a path or at specific frames
* `mixture`: evaluates compositional control when multiple constraint families are combined

Within each constraint type in the hierarchy are multiple subtypes that vary the constraint sparsity patterns (either in time or in space). So the hierarchy of a `constraint` folder is:

```text
constraints_XX
├── end-effectors
│   ├── feet_posrot          # feet only constraints
│   ├── hands_feet_posrot    # hands + feet constraints
│   └── hands_posrot         # hands only constraints
├── fullbody
│   ├── inbetweening         # constraints at start and end only
│   └── random               # constraints at random frames
├── mixture
│   ├── root_ee_hands_feet_posrot_fullbody    # mix of (1) root trajectory, (2) hand + foot, and (3) full-body 
│   ├── root_ee_hands_posrot                  # mix of (1) root keyframe, and (2) hands
│   ├── root_ee_hands_posrot_fullbody         # mix of (1) root keyframe, (2) hands, and (3) full-body
│   └── root_path_fullbody                    # mix of (1) root trajectory, and (2) full-body
└── root
    ├── path_2dpos             # root trajectory position
    ├── path_2dposrot          # root trajecotry position + heading
    ├── waypoint_2dpos         # root waypoint position
    └── waypoint_2dposrot      # root waypoint position + heading
```

### Indexed Test Cases in Leaf Folders

Each leaf folder contains indexed test cases (`0000`, `0001`, `0002`, ...).
For example:

```text
end-effectors/feet_posrot/
├── 0000/
├── 0001/
├── 0002/
...
└── 0255/
```

Each index folder is one standalone test case with its own `meta.json`, optional `constraints.json`, optional `gt_motion.npz`, and optional `motion.npz`.
