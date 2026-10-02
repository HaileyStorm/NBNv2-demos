# Beads backend migration — 2026-10-01

Canonical tracker: this repository's `.beads`, prefix `demos`. Beads 1.3.1,
matching pinned Dolt 2.2.0, embedded single-writer mode. Viewer 0.25.2 consumes the explicit JSONL export.

Preserved source counts: 97 issues (0 tombstones),
114 comments, 57 original dependency rows,
83 labels and 167 historical events.
Source semantic snapshot: `2a50e446c81cdd36a174e65fcb71e45d9eaaea9938942e394cbf49b1240e47ac`.

Original SQLite files, runtime files and config are retained outside the repository under
`<CODEX_HOME>/state/beads-upgrade/20261001-01a0f8a3/cutover-baselines/nbnv2-demos`.
Full local native backup destination: `<CODEX_HOME>/state/beads-backups/nbnv2-demos`.
Isolated migration, full-backup restore, restarted create/comment/update/close/read,
and tombstone export checks passed before installation. Synthetic proof records are not installed.
Native/archive verification SHA-256: `4db444771c5a4a3f9a1f3b4ef0d4508de365950478ef2e45bad0764e1cd67902`.

All original tables, schemas, rows, raw source JSONL and ID mappings are retained in the
ignored Dolt `legacy_beads_*` tables and full backup. Original exact timestamp strings,
deletion provenance, `crystallizes` and `quality_score` are also retained in per-issue
`metadata.codex_legacy_beads_v1`; native SQL dates have UTC whole-second precision.
This is a preservation migration, not a claim of an unchanged legacy schema.

Restored 29 later export-only issues (68 SQLite issues became 97), 35 comments, nine dependencies and 32 labels. All original SQLite rows, source JSONL bytes and comment-ID reconciliation mappings are retained.

No Dolt cloud remote or off-host parity is asserted. JSONL is not a complete backup;
follow README.md for explicit export, local backup and safe task takeover. Migration
evidence and live acceptance are recorded in the local upgrade CHECKPOINT.md.
