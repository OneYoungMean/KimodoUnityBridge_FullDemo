"""Create a smaller, standard-compatible SMPL-X NPZ for the local GVHMR asset."""

from __future__ import annotations

import argparse
from pathlib import Path

import numpy as np


def build(source: Path, destination: Path) -> None:
    with np.load(source, allow_pickle=True) as source_data:
        payload = {}
        for name in source_data.files:
            array = source_data[name]
            if name == "shapedirs":
                array = array[:, :, :310]
            if array.dtype == np.float64:
                array = array.astype(np.float32)
            payload[name] = array
    destination.parent.mkdir(parents=True, exist_ok=True)
    np.savez_compressed(destination, **payload)
    print(f"wrote {destination} ({destination.stat().st_size:,} bytes)")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("source", type=Path)
    parser.add_argument("destination", type=Path)
    args = parser.parse_args()
    build(args.source, args.destination)


if __name__ == "__main__":
    main()
