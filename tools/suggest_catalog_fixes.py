#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import re
import shutil
import subprocess
import sys
from dataclasses import dataclass
from difflib import SequenceMatcher
from pathlib import Path
from typing import Dict, Iterable, List, Optional, Sequence, Tuple

from check_package_availability import (
    PackageEntry,
    extract_manager_identifier,
)


@dataclass
class ResultRecord:
    entry: PackageEntry
    status: str
    message: str
    manager_identifier: Optional[str]


@dataclass
class SearchCandidate:
    manager: str
    identifier: str
    name: str
    metadata: Dict[str, str]
    query: str
    raw: str


@dataclass
class ScoredSuggestion:
    score: float
    manager: str
    identifier: str
    name: str
    metadata: Dict[str, str]
    query: str
    raw: str


SEARCH_CLI = {
    "winget": "winget",
    "choco": "choco",
    "chocolatey": "choco",
    "scoop": "scoop",
}


class SearchError(RuntimeError):
    pass


def parse_args(argv: Optional[Sequence[str]]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=
        "Suggest replacement catalog commands for failing package entries.")
    parser.add_argument(
        "--input",
        type=Path,
        required=True,
        help="Path to JSON output produced by check_package_availability.py",
    )
    parser.add_argument(
        "--root",
        type=Path,
        default=Path(__file__).resolve().parents[1],
        help="Repository root for resolving catalog paths.",
    )
    parser.add_argument(
        "--manager",
        dest="managers",
        action="append",
        default=[],
        help="Filter failing entries by their current manager (repeatable).",
    )
    parser.add_argument(
        "--package-id",
        dest="package_ids",
        action="append",
        default=[],
        help="Filter failing entries by catalog package id (repeatable).",
    )
    parser.add_argument(
        "--search-manager",
        dest="search_managers",
        action="append",
        default=[],
        help="Restrict searches to specific managers (repeatable).",
    )
    parser.add_argument(
        "--max-suggestions",
        type=int,
        default=3,
        help=
        "Maximum suggestions to print for each failing entry (defaults to 3).",
    )
    parser.add_argument(
        "--timeout",
        type=int,
        default=25,
        help="Per search timeout in seconds (defaults to 25).",
    )
    return parser.parse_args(argv)


def load_results(path: Path, root: Path) -> List[ResultRecord]:
    payload = json.loads(path.read_text(encoding="utf-8"))
    records: List[ResultRecord] = []
    for raw in payload:
        file_part = raw.get("file_path", "")
        file_path = Path(file_part)
        if not file_path.is_absolute():
            file_path = root / file_path
        entry = PackageEntry(
            package_id=str(raw.get("package_id", "")),
            manager=str(raw.get("manager", "")),
            command=str(raw.get("command", "")),
            name=str(raw.get("name", "")),
            file_path=file_path,
            index=int(raw.get("index", 0)),
        )
        records.append(
            ResultRecord(
                entry=entry,
                status=str(raw.get("status", "")),
                message=str(raw.get("message", "")),
                manager_identifier=(raw.get("manager_identifier") or None),
            ))
    return records


def filter_records(records: Iterable[ResultRecord], managers: Sequence[str],
                   package_ids: Sequence[str]) -> List[ResultRecord]:
    manager_filters = {m.lower() for m in managers if m}
    package_filters = {p.lower() for p in package_ids if p}
    filtered: List[ResultRecord] = []
    for record in records:
        entry = record.entry
        if manager_filters and entry.manager.lower() not in manager_filters:
            continue
        if package_filters and entry.package_id.lower() not in package_filters:
            continue
        if record.status.lower() not in {"not-found", "error"}:
            continue
        filtered.append(record)
    return filtered


def resolve_search_managers(search_managers: Sequence[str]) -> List[str]:
    if search_managers:
        resolved = []
        for item in search_managers:
            key = item.lower()
            if key in SEARCH_CLI:
                value = SEARCH_CLI[key]
                resolved.append("choco" if value == "choco" else key)
        return sorted(set(resolved), key=lambda m: (m != "winget", m))
    return ["winget", "choco", "scoop"]


def run_search_command(command: Sequence[str],
                       timeout: int) -> Tuple[int, str]:
    completed = subprocess.run(
        command,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=timeout,
        check=False,
    )
    combined = (completed.stdout or "") + (completed.stderr or "")
    return completed.returncode, combined


def search_winget(query: str, timeout: int) -> List[SearchCandidate]:
    command = [
        "winget",
        "search",
        query,
        "--source",
        "winget",
        "--disable-interactivity",
        "--accept-source-agreements",
    ]
    code, output = run_search_command(command, timeout)
    if code != 0 and not output.strip():
        raise SearchError("winget search returned no output")
    return parse_winget_output(output, query)


def parse_winget_output(output: str, query: str) -> List[SearchCandidate]:
    lines = [line.rstrip() for line in output.splitlines() if line.strip()]
    results: List[SearchCandidate] = []
    for line in lines:
        if line.lower().startswith("name ") or set(line.strip()) == {"-"}:
            continue
        columns = re.split(r"\s{2,}", line.strip())
        if len(columns) < 2:
            continue
        name = columns[0]
        identifier = columns[1]
        metadata: Dict[str, str] = {}
        if len(columns) >= 3:
            metadata["version"] = columns[2]
        if len(columns) >= 4:
            metadata["source"] = columns[3]
        results.append(
            SearchCandidate(
                manager="winget",
                identifier=identifier,
                name=name,
                metadata=metadata,
                query=query,
                raw=line,
            ))
    return results


def search_choco(query: str, timeout: int) -> List[SearchCandidate]:
    command = [
        "choco",
        "search",
        query,
        "--page=0",
        "--page-size=30",
        "--order-by-popularity",
        "--no-color",
        "--limit-output",
    ]
    code, output = run_search_command(command, timeout)
    if "no packages found" in output.lower():
        return []
    if code != 0 and not output.strip():
        raise SearchError("choco search returned no output")
    results: List[SearchCandidate] = []
    for line in output.splitlines():
        line = line.strip()
        if not line or "|" not in line:
            continue
        parts = line.split("|")
        identifier = parts[0].strip()
        version = parts[1].strip() if len(parts) > 1 else ""
        results.append(
            SearchCandidate(
                manager="choco",
                identifier=identifier,
                name=identifier,
                metadata={"version": version},
                query=query,
                raw=line,
            ))
    return results


def search_scoop(query: str, timeout: int) -> List[SearchCandidate]:
    command = ["scoop", "search", query]
    code, output = run_search_command(command, timeout)
    if code != 0 and not output.strip():
        raise SearchError("scoop search returned no output")
    results: List[SearchCandidate] = []
    for line in output.splitlines():
        columns = re.split(r"\s{2,}", line.strip())
        if len(columns) < 1:
            continue
        identifier = columns[0]
        bucket = columns[1] if len(columns) >= 2 else ""
        name = identifier
        metadata = {"bucket": bucket} if bucket else {}
        results.append(
            SearchCandidate(
                manager="scoop",
                identifier=identifier,
                name=name,
                metadata=metadata,
                query=query,
                raw=line,
            ))
    return results


SEARCH_FUNCTIONS = {
    "winget": search_winget,
    "choco": search_choco,
    "scoop": search_scoop,
}


def dedupe_candidates(
        candidates: Iterable[SearchCandidate]) -> List[SearchCandidate]:
    seen: Dict[Tuple[str, str], SearchCandidate] = {}
    for candidate in candidates:
        key = (candidate.manager, candidate.identifier.lower())
        if key not in seen:
            seen[key] = candidate
    return list(seen.values())


def compute_similarity(a: str, b: str) -> float:
    if not a or not b:
        return 0.0
    return SequenceMatcher(None, a.lower(), b.lower()).ratio()


def score_candidates(
        entry: PackageEntry, manager_identifier: Optional[str],
        candidates: Iterable[SearchCandidate]) -> List[ScoredSuggestion]:
    scored: List[ScoredSuggestion] = []
    for candidate in candidates:
        scores = [compute_similarity(entry.package_id, candidate.identifier)]
        if entry.name:
            scores.append(compute_similarity(entry.name, candidate.name))
            scores.append(compute_similarity(entry.name, candidate.identifier))
        if manager_identifier:
            scores.append(
                compute_similarity(manager_identifier, candidate.identifier))
        base = max(scores or [0.0])
        if candidate.identifier.lower() == entry.package_id.lower():
            base = min(base + 0.2, 1.0)
        if manager_identifier and candidate.identifier.lower(
        ) == manager_identifier.lower():
            base = min(base + 0.1, 1.0)
        if candidate.manager == entry.manager:
            base = min(base + 0.05, 1.0)
        scored.append(
            ScoredSuggestion(
                score=base,
                manager=candidate.manager,
                identifier=candidate.identifier,
                name=candidate.name,
                metadata=candidate.metadata,
                query=candidate.query,
                raw=candidate.raw,
            ))
    scored.sort(key=lambda item: item.score, reverse=True)
    return scored


def build_install_command(manager: str, identifier: str) -> str:
    if manager == "winget":
        return f"winget install --id {identifier} --exact --source winget --disable-interactivity"
    if manager == "choco":
        return f"choco install {identifier} -y"
    if manager == "scoop":
        return f"scoop install {identifier}"
    return f"{manager} install {identifier}"


def gather_queries(record: ResultRecord) -> List[str]:
    entry = record.entry
    manager_identifier = record.manager_identifier or extract_manager_identifier(
        entry)
    if manager_identifier and not record.manager_identifier:
        record.manager_identifier = manager_identifier
    queries: List[str] = []
    seen: set[str] = set()
    for value in [entry.package_id, record.manager_identifier, entry.name]:
        text = (value or "").strip()
        key = text.lower()
        if text and key not in seen:
            queries.append(text)
            seen.add(key)
    return queries or [entry.package_id]


def search_for_record(
        record: ResultRecord, managers: Sequence[str],
        timeout: int) -> Tuple[List[ScoredSuggestion], List[str]]:
    entry = record.entry
    queries = gather_queries(record)
    scored: List[ScoredSuggestion] = []
    notes: List[str] = []
    for manager in managers:
        cli = SEARCH_CLI.get(manager, manager)
        if shutil.which(cli) is None:
            notes.append(f"{manager}: CLI '{cli}' is not available on PATH.")
            continue
        search_fn = SEARCH_FUNCTIONS.get(manager)
        if not search_fn:
            notes.append(f"{manager}: no search implementation available.")
            continue
        manager_candidates: List[SearchCandidate] = []
        last_error: Optional[str] = None
        for query in queries:
            try:
                manager_candidates = search_fn(query, timeout)
            except (SearchError, subprocess.TimeoutExpired) as exc:
                last_error = str(exc)
                continue
            if manager_candidates:
                break
        if not manager_candidates:
            if last_error:
                notes.append(f"{manager}: {last_error}")
            else:
                notes.append(
                    f"{manager}: no matches for queries {', '.join(queries)}")
            continue
        manager_candidates = dedupe_candidates(manager_candidates)
        scored.extend(
            score_candidates(entry, record.manager_identifier,
                             manager_candidates))
    scored.sort(key=lambda item: item.score, reverse=True)
    return scored, notes


def format_metadata(metadata: Dict[str, str]) -> str:
    if not metadata:
        return ""
    parts = [f"{key}={value}" for key, value in metadata.items() if value]
    return ", ".join(parts)


def render_record(record: ResultRecord, suggestions: List[ScoredSuggestion],
                  notes: Sequence[str], limit: int) -> None:
    entry = record.entry
    location = f"{entry.file_path}#{entry.index}" if entry.index else str(
        entry.file_path)
    header = f"{entry.package_id} ({entry.manager}) -> {record.status.upper()}"
    print(header)
    if record.message:
        print(f"  Reason: {record.message}")
    print(f"  Catalog: {location}")
    if record.manager_identifier:
        print(f"  Manager ID: {record.manager_identifier}")
    if suggestions:
        print("  Suggestions:")
        for idx, suggestion in enumerate(suggestions[:limit], start=1):
            metadata = format_metadata(suggestion.metadata)
            command = build_install_command(suggestion.manager,
                                            suggestion.identifier)
            line = f"    {idx}. {suggestion.manager}: {command}"
            print(line)
            print(
                f"       -> {suggestion.name} (score {suggestion.score:.2f}, query '{suggestion.query}')"
            )
            if metadata:
                print(f"       -> {metadata}")
    else:
        print("  Suggestions: none")
    if notes:
        print("  Notes:")
        for note in notes:
            print(f"    - {note}")
    print()


def main(argv: Optional[Sequence[str]] = None) -> int:
    args = parse_args(argv)
    if not args.input.exists():
        print(f"Input JSON not found: {args.input}", file=sys.stderr)
        return 1

    records = load_results(args.input, args.root)
    records = filter_records(records, args.managers, args.package_ids)
    if not records:
        print("No failing entries matched the provided filters.")
        return 0

    search_managers = resolve_search_managers(args.search_managers)

    for record in records:
        suggestions, notes = search_for_record(record, search_managers,
                                               args.timeout)
        render_record(record, suggestions, notes, args.max_suggestions)

    return 0


if __name__ == "__main__":
    sys.exit(main())
