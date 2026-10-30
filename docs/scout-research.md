# Scout: filename and path search

Research and implementation decision, 29 October 2026. The first version searches names and paths, as requested; it does not read document contents.

## Decision

Use Everything's local index through its official SDK for whole-computer search. Put SDK calls in a separate, cancellable worker process so an unavailable or stalled native backend cannot freeze or terminate the WinUI app. Provide a native .NET folder search when Everything is absent. Installing the index engine is an explicit action using TidyWindow's package queue; it is not a prerequisite for searching a folder.

Filename discovery and relevance are separate: an index finds candidates; local scoring places exact names, prefixes, word matches, substrings, abbreviations and bounded typo matches in order. No cloud requests, embeddings, file uploads, or content extraction are needed for this workload.

## Alternatives checked

| Backend | Strength | Constraint | Decision |
| --- | --- | --- | --- |
| Everything SDK | Established local filename index, NTFS/USN updates, direct metadata access | Everything must be running; its service normally handles privileged NTFS access | Primary whole-computer backend |
| Windows Search | Built into Windows; supports property/content queries | Coverage depends on indexed locations; Classic covers only selected user locations by default | Not the primary whole-drive filename backend |
| `fd` | Mature Rust implementation, parallel directory traversal, permissive license | Each cold search still walks directories; hidden and ignored files are excluded unless requested | Good prior art; avoid shipping a second executable for a fallback already supported by .NET enumeration |
| Direct MFT/USN implementation | Fast NTFS discovery and incremental changes | Raw-volume privileges, journal rollover, hard links, reparse points, multiple volumes, service and IPC lifecycle become our maintenance burden | Do not introduce a new privileged service for this first page |
| New Rust MFT engines (UFFS, find-my-files, UltraSearch) | Promising local engines and open source | Younger integration contracts, differing service/broker requirements; published benchmark claims are not measurements on this machine | Revisit after comparative testing; not selected solely from README speed claims |
| SQLite FTS5 | Good persistent substring/text indexing; trigram tokenizer available | Does not discover filesystem changes; adds storage/index maintenance, and short queries need separate handling | Not needed alongside Everything's existing filename index |

The old Deep Scan was a largest-files analyzer rather than an instant filename index. Its permanent-delete and ownership-changing behavior is not part of Scout. Scout's first version offers opening a containing folder and copying a path.

## Product and correctness requirements

- A simple search field, location/type filters and virtualized results, responsive to narrow windows.
- Cancel stale searches while typing; never block the UI on native queries or directory enumeration.
- Distinguish a missing index, an indexing engine, inaccessible directories and zero matches.
- Show only a bounded result set and explain candidate limits. Ranking cannot recover an item excluded by an upstream candidate query.
- Folder discovery skips reparse traversal and offline/cloud-only entries. It respects the current account's permissions and reports skipped locations; it never takes ownership or changes ACLs.
- Query metadata stays local. Do not silently enable networking, HTTP servers or startup settings.

## Primary sources

- [Everything SDK and background-process requirement](https://www.voidtools.com/support/everything/sdk/)
- [Everything service and NTFS access model](https://www.voidtools.com/support/everything/everything_service/)
- [Everything license](https://www.voidtools.com/License.txt)
- [Windows Search indexing modes and coverage](https://support.microsoft.com/en-us/windows/experience/performance-optimization/search-indexing-in-windows)
- [Windows Search SQL](https://learn.microsoft.com/en-us/windows/win32/search/-search-sql-windowssearch-entry)
- [fd source, options and benchmark caveats](https://github.com/sharkdp/fd)
- [Microsoft's MFT enumeration API](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_enum_usn_data)
- [SQLite FTS5/trigram documentation](https://sqlite.org/fts5.html)
- [UFFS](https://github.com/skyllc-ai/UltraFastFileSearch), [find-my-files](https://github.com/P4suta/find-my-files), [UltraSearch](https://github.com/Dicklesworthstone/ultrasearch)

Third-party performance numbers are workload-specific. Record our own candidate counts, cold discovery times and warm-query timings in the test output before making performance claims.

## Local validation

- Warm local matching/ranking over 200,001 generated metadata entries: 72.8 ms on this machine. This excludes filesystem discovery and is not a whole-drive benchmark.
- Official portable Everything 1.4.1.1032 archive verified against voidtools' published SHA-256. Ran it with an isolated configuration containing only a test folder, no volumes/service/server; stopped that test process afterwards.
- Real SDK/worker end-to-end queries: `budget` (2 results, 231.5 ms including initial process/runtime startup), transposed `budegt` (2, 108.3 ms), `budget reports` (2, 106.2 ms), `readme` (1, 111.4 ms). Tiny fixture timings validate integration, not performance at millions of records.
- Tests cover exact/prefix/path/phrase/Unicode/typo ranking, bounds, cancellation, real folder enumeration and watcher create/delete reconciliation. Whole-drive cold-index timings depend on Everything's configuration and have not been measured here.
- The final self-contained worker also passed a 2,100-file fixture: 150 bounded results, exact filename first even past the broad candidate cap, visible candidate-limit flag, type filtering and scope isolation. Its three end-to-end test queries took 119.5–164.6 ms. The published worker includes its .NET runtime and loads the SDK from its own assets directory.

## Initial limits

The default stable Everything instance must be running and its index coverage depends on its configuration. Alternate/beta instance names are not supported. Home/This PC use that index when available; explicitly choosing a folder always scans that folder directly, including locations Everything has not indexed. A first folder scan discovers entries asynchronously; later queries reuse them. File metadata changes and newly moved directory trees may require Refresh. Discovery and watcher queues are bounded (400,000 entries and 10,000 pending changes), with visible limits/errors rather than silent completeness claims.

Relevance is deterministic metadata ranking, not semantic/content search. Everything's bounded candidate retrieval can omit relevant non-exact matches; local scoring cannot rank omitted entries. This implementation is a measured first version, not a claim of universally optimal ranking or speed.

## Installed-engine readiness regression

The Everything service runs as `Everything.exe` in Windows session zero. It does not replace the user's IPC/index process. Scout now ignores session-zero and other-session processes when deciding whether to start the installed executable with `-startup`. It distinguishes missing, installed/unavailable, indexing and ready states, waits briefly for initial IPC startup, refreshes after queued operations, and polls pending readiness while Scout is visible. Installed-but-unavailable offers retry; indexing and queued installation disable setup; ready hides setup. No install-success preference is used as a substitute for real engine readiness.

Native UI binding checks covered missing, queued, indexing, ready and unavailable transitions. A live test started this system's installed Everything user process and passed a whole-PC lookup for `TidyWindow.slnx`. The user's `wuther` query also returned results. A cold query was slower (4.54 seconds); a repeated query's native exact + broad stages completed in 73 ms, so cold/warm behavior remains relevant.

The live check exposed an unbounded abbreviation-regex cost across full paths. Fuzzy retrieval now targets names, uses atomic gaps rather than backtracking gaps, and is skipped once an exact name/stem exists. Exact and literal path matching remain available. Unused file-attribute metadata is no longer requested. Framework-dependent and self-contained worker build/publish directories are separate so their runtime files cannot contaminate each other's source output.
