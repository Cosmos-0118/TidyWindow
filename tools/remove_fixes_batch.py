#!/usr/bin/env python3
"""Trim resolved entries from fixes.json in consistent batches."""

from __future__ import annotations

import argparse
import json
from pathlib import Path


def load_fixes(path: Path) -> list[dict]:
    if not path.exists():
        raise FileNotFoundError(f"Cannot find fixes file at {path}")
    with path.open("r", encoding="utf-8") as fh:
        data = json.load(fh)
    if not isinstance(data, list):
        raise ValueError("Expected top-level JSON array in fixes.json")
    return data


def save_fixes(path: Path, data: list[dict]) -> None:
    with path.open("w", encoding="utf-8") as fh:
        json.dump(data, fh, indent=2)
        fh.write("\n")


def trim_entries(fixes: list[dict], count: int) -> list[dict]:
    if count <= 0:
        return fixes
    if count > len(fixes):
        raise ValueError(
            f"Requested removal of {count} entries but only {len(fixes)} remain"
        )
    return fixes[count:]


def main() -> None:
    parser = argparse.ArgumentParser(
        description=
        "Remove the first N entries from fixes.json once they are resolved.")
    parser.add_argument(
        "count",
        type=int,
        nargs="?",
        default=20,
        help="Number of leading fixes to remove (default: 20)",
    )
    parser.add_argument(
        "--fixes",
        type=Path,
        default=Path("fixes.json"),
        help="Path to fixes.json (default: ./fixes.json)",
    )
    args = parser.parse_args()

    fixes = load_fixes(args.fixes)
    trimmed = trim_entries(fixes, args.count)
    if len(trimmed) == len(fixes):
        print("No entries removed; nothing to do.")
        return

    save_fixes(args.fixes, trimmed)
    print(f"Removed {args.count} entries. Remaining: {len(trimmed)}")


if __name__ == "__main__":
    main()
