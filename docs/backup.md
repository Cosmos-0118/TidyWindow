# Reset Rescue Backup Specification

## Purpose

Document the archive and manifest format used by Reset Rescue to backup and restore user data and app data ahead of OS resets or migrations.

## Archive Container

- File extension: `.rrarchive`
- Container: zip package with LZNT1-compressed payload segments
- Contents:
    - `manifest.json` (required)
    - `payload/` (files, app data, registry exports)
    - `meta/` (logs, reports)
- Integrity: SHA-256 chunked hashes; default chunk size 4 MB stored in the manifest for resume and verification.

    ### Automation helper (`automation/scripts/reset-rescue.ps1`)
    - Modes: `Backup` (default) or `Restore`.
    - Parameters:
        - `SourcePaths`: Paths to stage (files or directories; supports VSS when `-CreateSnapshot`).
        - `TargetPath`: Staging/output directory (default `%TEMP%/reset-rescue-staging`).
        - `RegistryKeys`: Optional registry keys to export to `meta/registry_*.reg`.
        - `CreateSnapshot`: Best-effort VSS snapshot for locked files (logs warnings if unavailable).
        - `RestoreRoot`: Required when `-Mode Restore`; copies staged payload back honoring `-Conflict`.
        - `Conflict`: `Overwrite|Rename|Skip|BackupExisting` (default `Rename`).
        - `LogPath`: Log file (default `%TEMP%/reset-rescue.log`).
    - Output: emits compact JSON `{ status, copied, skipped, registryExports, snapshotId, logPath, errors }` to stdout for the invoker; always appends human-readable lines to the log file.

    - Copy semantics:
        - Direct copy via `Copy-Item`; falls back to `robocopy /COPY:DAT /R:2 /W:2 /MT` for locked files.
        - Snapshot-aware path resolution when VSS succeeds (uses `GLOBALROOT` device path).
        - Registry export uses `reg.exe export` with per-key files.

## Manifest Schema (v1)

Top-level fields:

- `manifestVersion`: `1`
- `createdUtc`: ISO 8601 UTC timestamp
- `generator`: app version + commit hash
- `archiveFormat`: `rrarchive` | `zip`
- `policies`: conflict strategy (`overwrite` | `rename` | `skip` | `backup`), long-path aware flag, oneDrive handling (`download` | `metadata`), vssRequired flag
- `hash`: `algo` (`SHA256`), `chunkSizeBytes`
- `profiles`: detected user profiles with `sid`, `name`, `root`, `knownFolders`
- `apps`: inventory items with `id`, `name`, `type` (`Win32` | `MSI` | `Store` | `Portable`), `version`, `installLocation`, `dataPaths`, `registryKeys`
- `entries`: array of file/data items

Entry fields:

- `id`: stable identifier used for resume
- `type`: `file` | `directory` | `registry`
- `sourcePath`: original absolute path
- `targetPath`: relative path inside archive payload
- `sizeBytes`, `lastWriteTimeUtc`
- `hash`: `chunks` (array of SHA-256 per chunk), `full` (optional full SHA-256)
- `acl`: `owner`, `sddl`, `preserve` (bool)
- `attributes`: DOS/NTFS flags (e.g., `Hidden`, `System`, `Archive`)
- `vss`: snapshot ID when captured from VSS
- `appId`: optional link to `apps` entry when file belongs to an app

## Capture & Guardrails

- VSS: attempt snapshot for locked files; fall back to direct copy with warning.
- Path safety: prefer long-path-aware APIs; record original paths for reconciliation.
- Space check: require 120% of projected size on destination.
- ACLs: preserve when possible; downgrade gracefully when target does not support ACL.
- OneDrive: option to hydrate files or capture metadata-only; flag choice in `policies`.

## Restore Behavior

- Path reconciliation: map archived `sourcePath` to current profile roots and drive letters.
- Conflicts: obey `policies.conflict`; default `rename` with suffix `-backup` to avoid overwrite.
- Validation: recompute chunk hashes and compare to manifest; log mismatches.
- Partial restore: filter by `profiles`, `apps`, or specific `entries`.

## Reporting

- Include `meta/report.html` summarizing successes, skips, and errors.
- Log VSS usage, conflicts resolved, and any downgraded ACLs.

## Storage Layout

- `data/backup/schemas/`: manifest schema notes and future versions.
- `data/backup/samples/`: sample manifests and example `.rrarchive` artifacts for testing.
- See `data/backup/README.md` for layout guidance.
