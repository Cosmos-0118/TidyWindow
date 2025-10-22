#!/usr/bin/env python3
from __future__ import annotations

import argparse
import collections
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import DefaultDict, Iterable, List, Optional

try:  # Optional dependency; fall back to lightweight parser if unavailable.
    import yaml  # type: ignore
except ModuleNotFoundError:  # pragma: no cover - exercised only without PyYAML
    yaml = None  # type: ignore


@dataclass(frozen=True)
class PackageOccurrence:
    package_id: str
    file_path: Path
    index: int


def read_package_ids(path: Path) -> List[str]:
    if yaml is not None:
        data = yaml.safe_load(path.read_text(encoding="utf-8")) or {}
        packages = data.get("packages", [])
        return [
            str(pkg.get("id", "")).strip() for pkg in packages
            if isinstance(pkg, dict) and pkg.get("id")
        ]

    ids: List[str] = []
    with path.open(encoding="utf-8") as fh:
        for line in fh:
            stripped = line.strip()
            if stripped.startswith("- id:"):
                candidate = stripped.split(":", 1)[1].strip().strip('"\'')
                if candidate:
                    ids.append(candidate)
    return ids


def collect_duplicates(
    package_files: Iterable[Path]
) -> DefaultDict[str, List[PackageOccurrence]]:
    occurrences: DefaultDict[
        str, List[PackageOccurrence]] = collections.defaultdict(list)
    for package_file in package_files:
        ids = read_package_ids(package_file)
        for index, package_id in enumerate(ids, start=1):
            occurrences[package_id].append(
                PackageOccurrence(package_id, package_file, index))
    return collections.defaultdict(list, {
        pid: occ
        for pid, occ in occurrences.items() if len(occ) > 1
    })


def iter_package_files(root: Path, glob: str) -> Iterable[Path]:
    package_dir = root / "data" / "catalog" / "packages"
    if not package_dir.is_dir():
        raise FileNotFoundError(f"Package directory not found: {package_dir}")
    return sorted(package_dir.glob(glob))


def format_report(
        duplicates: DefaultDict[str, List[PackageOccurrence]]) -> str:
    lines: List[str] = []
    for package_id in sorted(duplicates):
        lines.append(f"{package_id} ({len(duplicates[package_id])}x)")
        for occ in duplicates[package_id]:
            relative = occ.file_path.relative_to(
                Path.cwd()) if occ.file_path.is_absolute() else occ.file_path
            lines.append(f"  - {relative} [entry #{occ.index}]")
    return "\n".join(lines)


def main(argv: Optional[List[str]] = None) -> int:
    parser = argparse.ArgumentParser(
        description="Detect duplicate package entries across YAML catalogs.")
    parser.add_argument("--root",
                        type=Path,
                        default=Path(__file__).resolve().parents[1],
                        help="Repository root (defaults to project root)")
    parser.add_argument("--glob",
                        default="*.yml",
                        help="Glob pattern for package catalog files")
    parser.add_argument(
        "--strict",
        action="store_true",
        help="Exit with non-zero status when duplicates are found")
    args = parser.parse_args(argv)

    package_files = list(iter_package_files(args.root, args.glob))
    if not package_files:
        print("No package catalog files found.", file=sys.stderr)
        return 1

    duplicates = collect_duplicates(package_files)
    if not duplicates:
        print("No duplicate package IDs detected.")
        return 0

    print("Duplicate package IDs detected:\n")
    print(format_report(duplicates))

    return 1 if args.strict else 0


if __name__ == "__main__":
    sys.exit(main())
