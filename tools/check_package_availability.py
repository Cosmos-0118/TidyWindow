#!/usr/bin/env python3
from __future__ import annotations

import argparse
import collections
import json
import os
import re
import shlex
import shutil
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable, List, Optional, Sequence, Tuple

try:
    import yaml  # type: ignore
except ModuleNotFoundError as exc:
    print(
        "PyYAML is required to run this script. Install it with 'pip install pyyaml'.",
        file=sys.stderr,
    )
    raise SystemExit(1) from exc

WINGET_NOT_FOUND_MARKERS = (
    "no package found matching input criteria",
    "no packages found matching input criteria",
    "no package found matching the input criteria",
    "no application found matching input criteria",
    "no app found matching input criteria",
)

CHOCO_NOT_FOUND_MARKERS = (
    "0 packages found",
    "no packages found",
    "not installed. cannot find",
)

SCOOP_NOT_FOUND_MARKERS = (
    "couldn't find manifest",
    "could not find manifest",
    "no matches found",
)

CLI_NAME_BY_MANAGER = {
    "winget": "winget",
    "choco": "choco",
    "chocolatey": "choco",
    "scoop": "scoop",
}


def _find_windows_shim(executable: str) -> Optional[Tuple[str, str]]:
    path_env = os.environ.get("PATH", "")
    if not path_env:
        return None
    extensions = [".exe", ".bat", ".cmd", ".ps1"]
    for directory in path_env.split(os.pathsep):
        if not directory:
            continue
        base = Path(directory.strip('"'))
        for suffix in extensions:
            candidate = base / f"{executable}{suffix}"
            if candidate.exists():
                return str(candidate), suffix.lower()
    return None


def _prepare_command(command: Sequence[str]) -> List[str]:
    if not command:
        raise ValueError("Command sequence cannot be empty")

    executable = command[0]
    resolved = shutil.which(executable)
    if resolved:
        return [resolved, *command[1:]]

    if sys.platform.startswith("win"):
        shim = _find_windows_shim(executable)
        if shim:
            path, suffix = shim
            if suffix == ".ps1":
                shell = shutil.which("pwsh") or shutil.which("powershell")
                if shell:
                    return [
                        shell,
                        "-NoLogo",
                        "-NoProfile",
                        "-ExecutionPolicy",
                        "Bypass",
                        "-File",
                        path,
                        *command[1:],
                    ]
            return [path, *command[1:]]

    return list(command)


@dataclass(frozen=True)
class PackageEntry:
    package_id: str
    manager: str
    command: str
    name: str
    file_path: Path
    index: int


@dataclass(frozen=True)
class CheckResult:
    entry: PackageEntry
    manager_identifier: Optional[str]
    status: str
    message: str
    return_code: Optional[int]


def iter_package_files(root: Path, glob: str) -> Sequence[Path]:
    package_dir = root / "data" / "catalog" / "packages"
    if not package_dir.is_dir():
        raise FileNotFoundError(f"Package directory not found: {package_dir}")
    return sorted(package_dir.glob(glob))


def load_packages(files: Sequence[Path]) -> List[PackageEntry]:
    entries: List[PackageEntry] = []
    for file_path in files:
        raw = yaml.safe_load(file_path.read_text(encoding="utf-8")) or {}
        packages = raw.get("packages", [])
        for idx, pkg in enumerate(packages, start=1):
            if not isinstance(pkg, dict):
                continue
            package_id = str(pkg.get("id", "")).strip()
            manager = str(pkg.get("manager", "")).strip()
            command = str(pkg.get("command", "")).strip()
            name = str(pkg.get("name", "")).strip()
            if not package_id or not manager:
                continue
            entries.append(
                PackageEntry(
                    package_id=package_id,
                    manager=manager,
                    command=command,
                    name=name,
                    file_path=file_path,
                    index=idx,
                ))
    return entries


def split_command(command: str) -> List[str]:
    if not command:
        return []
    try:
        return shlex.split(command, posix=False)
    except ValueError:
        return command.split()


def extract_manager_identifier(entry: PackageEntry) -> Optional[str]:
    manager = entry.manager.lower()
    command = entry.command
    if manager == "winget":
        return extract_winget_identifier(command)
    if manager in {"choco", "chocolatey"}:
        return extract_choco_identifier(command)
    if manager == "scoop":
        return extract_scoop_identifier(command)
    return None


def extract_winget_identifier(command: str) -> Optional[str]:
    if not command:
        return None
    regex_match = re.search(r"--id(?:=|\s+)([\w\.\-]+)",
                            command,
                            flags=re.IGNORECASE)
    if regex_match:
        return regex_match.group(1)
    tokens = split_command(command)
    for idx, token in enumerate(tokens):
        if token.lower() != "winget":
            continue
        index = idx + 1
        if index >= len(tokens):
            return None
        verb = tokens[index].lower()
        if verb not in {"install", "upgrade", "show", "display", "list"}:
            continue
        index += 1
        while index < len(tokens):
            candidate = tokens[index]
            if not candidate or candidate.startswith("-"):
                index += 1
                continue
            return candidate.strip("\"'")
    return None


def extract_choco_identifier(command: str) -> Optional[str]:
    if not command:
        return None
    tokens = split_command(command)
    for idx, token in enumerate(tokens):
        if token.lower() != "choco":
            continue
        verb_index = idx + 1
        if verb_index >= len(tokens):
            return None
        verb = tokens[verb_index].lower()
        if verb not in {"install", "upgrade", "info", "search", "uninstall"}:
            continue
        candidate_index = verb_index + 1
        while candidate_index < len(tokens):
            candidate = tokens[candidate_index]
            if candidate.startswith("-"):
                candidate_index += 1
                continue
            return candidate.strip("\"'")
    return None


def extract_scoop_identifier(command: str) -> Optional[str]:
    if not command:
        return None
    tokens = split_command(command)
    for idx, token in enumerate(tokens):
        if token.lower() != "scoop":
            continue
        verb_index = idx + 1
        if verb_index >= len(tokens):
            return None
        verb = tokens[verb_index].lower()
        if verb not in {"install", "update", "upgrade", "info", "search"}:
            continue
        candidate_index = verb_index + 1
        while candidate_index < len(tokens):
            candidate = tokens[candidate_index]
            if candidate.startswith("-"):
                candidate_index += 1
                continue
            return candidate.strip("\"'")
    return None


def build_check_command(manager: str, identifier: str) -> Sequence[str]:
    manager_key = manager.lower()
    if manager_key == "winget":
        return [
            "winget",
            "show",
            "--id",
            identifier,
            "--exact",
            "--disable-interactivity",
            "--source",
            "winget",
        ]
    if manager_key in {"choco", "chocolatey"}:
        return [
            "choco",
            "search",
            identifier,
            "--exact",
            "--limit-output",
            "--id-only",
        ]
    if manager_key == "scoop":
        return ["scoop", "search", identifier]
    raise ValueError(f"Unsupported manager: {manager}")


def interpret_manager_result(manager: str, identifier: str, return_code: int,
                             output: str) -> Tuple[str, str]:
    normalized_output = output.lower()
    manager_key = manager.lower()
    if manager_key == "winget":
        if any(marker in normalized_output
               for marker in WINGET_NOT_FOUND_MARKERS):
            snippet = summarize_output(output)
            return "not-found", (
                "winget reported no matching package. Output: "
                f"{snippet}")
        if return_code != 0:
            return "error", summarize_output(output)
        return "ok", "winget show located the package."
    if manager_key in {"choco", "chocolatey"}:
        if any(marker in normalized_output
               for marker in CHOCO_NOT_FOUND_MARKERS):
            snippet = summarize_output(output)
            return "not-found", (
                "Chocolatey reported no matching package. Output: "
                f"{snippet}")
        if return_code != 0:
            return "error", summarize_output(output)
        return "ok", "Chocolatey info located the package."
    if manager_key == "scoop":
        if any(marker in normalized_output
               for marker in SCOOP_NOT_FOUND_MARKERS):
            snippet = summarize_output(output)
            return "not-found", ("Scoop reported no matching package. Output: "
                                 f"{snippet}")
        lines = [line.strip() for line in output.splitlines() if line.strip()]
        if return_code != 0:
            return "error", summarize_output(output)
        if not lines:
            snippet = summarize_output(output)
            return "not-found", ("Scoop returned no search results. Output: "
                                 f"{snippet}")
        identifier_lower = identifier.lower()
        for line in lines:
            if line.lower().startswith(identifier_lower + " ") or \
                    line.lower().startswith(identifier_lower + "(") or \
                    line.lower() == identifier_lower:
                return "ok", "Scoop search located the package."
        return "not-found", summarize_output(output)
    return "skipped", "No verifier implemented for this manager."


def summarize_output(output: str, limit: int = 200) -> str:
    if not output:
        return ""
    snippet = " ".join(output.split())
    if len(snippet) <= limit:
        return snippet
    return snippet[:limit].rstrip() + "..."


def check_package(entry: PackageEntry, timeout: int) -> CheckResult:
    manager_identifier = extract_manager_identifier(entry)
    if not manager_identifier:
        return CheckResult(
            entry, None, "skipped",
            "Unable to determine manager-specific identifier from command.",
            None)

    cli_name = CLI_NAME_BY_MANAGER.get(entry.manager.lower())
    if not cli_name:
        return CheckResult(
            entry, manager_identifier, "skipped",
            f"No CLI mapping registered for manager '{entry.manager}'.", None)

    try:
        command = build_check_command(entry.manager, manager_identifier)
    except ValueError as exc:
        return CheckResult(entry, manager_identifier, "skipped", str(exc),
                           None)

    prepared = _prepare_command(command)

    try:
        completed = subprocess.run(
            prepared,
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=timeout,
            check=False,
        )
    except FileNotFoundError:
        return CheckResult(
            entry, manager_identifier, "error",
            f"Failed to start '{cli_name}'. Ensure it is installed.", None)
    except subprocess.TimeoutExpired:
        return CheckResult(
            entry, manager_identifier, "error",
            f"Verification command exceeded the {timeout}s timeout.", None)

    combined_output = (completed.stdout or "") + (completed.stderr or "")
    status, message = interpret_manager_result(entry.manager,
                                               manager_identifier,
                                               completed.returncode,
                                               combined_output)
    return CheckResult(entry, manager_identifier, status, message,
                       completed.returncode)


def render_results(results: Sequence[CheckResult], root: Path) -> None:
    header = f"{'STATUS':<10} {'PACKAGE':<24} {'MANAGER':<10} {'MANAGER-ID':<28} SOURCE"
    print(header)
    print("-" * len(header))
    for result in results:
        entry = result.entry
        try:
            relative = entry.file_path.relative_to(root)
        except ValueError:
            relative = entry.file_path
        source = f"{relative}#{entry.index}"
        manager_identifier = result.manager_identifier or "-"
        print(
            f"{result.status.upper():<10} {entry.package_id:<24} {entry.manager:<10} {manager_identifier:<28} {source}"
        )
        if result.status.lower() != "ok":
            print(f"  {result.message}")

    counts = collections.Counter(res.status for res in results)
    print("\nSummary:")
    for key in ("ok", "not-found", "error", "skipped"):
        if counts.get(key):
            print(f"  {key}: {counts[key]}")


def render_results_json(results: Sequence[CheckResult], root: Path) -> None:
    data = []
    for result in results:
        entry = result.entry
        try:
            relative_path = entry.file_path.relative_to(root)
            file_path = str(relative_path)
        except ValueError:
            file_path = str(entry.file_path)
        data.append({
            "package_id": entry.package_id,
            "manager": entry.manager,
            "command": entry.command,
            "name": entry.name,
            "file_path": file_path,
            "index": entry.index,
            "status": result.status,
            "message": result.message,
            "manager_identifier": result.manager_identifier,
            "return_code": result.return_code,
        })
    json.dump(data, sys.stdout, indent=2)
    print()


def parse_args(argv: Optional[Sequence[str]]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=
        "Verify that catalog package entries resolve for their configured package manager IDs."
    )
    parser.add_argument(
        "--root",
        type=Path,
        default=Path(__file__).resolve().parents[1],
        help="Repository root (defaults to project root)",
    )
    parser.add_argument(
        "--glob",
        default="*.yml",
        help="Glob pattern for catalog files (defaults to '*.yml')",
    )
    parser.add_argument(
        "--format",
        choices=("table", "json"),
        default="table",
        help="Output format (defaults to 'table').",
    )
    parser.add_argument(
        "--manager",
        dest="managers",
        action="append",
        default=[],
        help="Restrict checks to one or more managers (repeatable).",
    )
    parser.add_argument(
        "--package-id",
        dest="package_ids",
        action="append",
        default=[],
        help="Restrict checks to specific catalog package IDs (repeatable).",
    )
    parser.add_argument(
        "--timeout",
        type=int,
        default=25,
        help="Per-package timeout in seconds (defaults to 25).",
    )
    parser.add_argument(
        "--strict",
        action="store_true",
        help="Treat skipped packages as failures in the exit code.",
    )
    return parser.parse_args(argv)


def filter_entries(entries: Iterable[PackageEntry], managers: Sequence[str],
                   package_ids: Sequence[str]) -> List[PackageEntry]:
    manager_filters = {m.lower() for m in managers if m}
    package_filters = {p.lower() for p in package_ids if p}
    filtered: List[PackageEntry] = []
    for entry in entries:
        if manager_filters and entry.manager.lower() not in manager_filters:
            continue
        if package_filters and entry.package_id.lower() not in package_filters:
            continue
        filtered.append(entry)
    return filtered


def main(argv: Optional[Sequence[str]] = None) -> int:
    args = parse_args(argv)

    package_files = list(iter_package_files(args.root, args.glob))
    if not package_files:
        print("No catalog files matched the provided glob.", file=sys.stderr)
        return 1

    entries = load_packages(package_files)
    entries = filter_entries(entries, args.managers, args.package_ids)
    if not entries:
        print("No catalog entries matched the provided filters.")
        return 0

    total = len(entries)
    results: List[CheckResult] = []
    # Provide a minimal progress indicator so long runs show activity.
    show_progress = args.format == "table"
    for index, entry in enumerate(entries, start=1):
        prefix = f"[{index}/{total}]"
        line = ""
        if show_progress:
            line = f"{prefix} Checking {entry.package_id} ({entry.manager})..."
            print(line, end="", flush=True)
        result = check_package(entry, args.timeout)
        status_label = result.status.upper()
        if show_progress:
            final_line = f"{prefix} {entry.package_id} -> {status_label}"
            padding = " " * max(0, len(line) - len(final_line))
            print(f"\r{final_line}{padding}", flush=True)
        results.append(result)

    if show_progress:
        print()
    if args.format == "json":
        render_results_json(results, args.root)
    else:
        render_results(results, args.root)

    has_failure = any(res.status in {"not-found", "error"} for res in results)
    if args.strict:
        has_failure = has_failure or any(res.status == "skipped"
                                         for res in results)

    return 1 if has_failure else 0


if __name__ == "__main__":
    sys.exit(main())
