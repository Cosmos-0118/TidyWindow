# data/backup layout

This folder is reserved for Reset Rescue backup assets.

- `schemas/`: manifest schema notes and versioned examples (v1 lives in `docs/backup.md`).
- `samples/`: sample manifests and `.rrarchive` test artifacts used by automation/tests.

Keep large sample archives out of source control; prefer small manifests and trimmed samples for CI.
