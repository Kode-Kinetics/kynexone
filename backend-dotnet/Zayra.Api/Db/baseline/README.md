# `Db/baseline/` — the hand-written SQL baseline

76 tables, applied in **filename order**. `TARGET_SCHEMA.md` §19.1 (revision 7) is the design.

## Before you open a schema PR

```bash
./scripts/schema-gates.sh              # every gate, including the self-test   (~45s)
./scripts/schema-gates.sh --fast       # skip the self-test                    (~15s)
./scripts/schema-drift-gate.sh --write # regenerate schema.sql after a DDL change
```

Docker and nothing else. One throwaway `postgres:16` container is started, shared by every gate
and destroyed on exit. No real database is reachable from it. CI runs the **same scripts** against
its `services: postgres` container (`SCHEMA_GATE_MODE=direct`, job `baseline-schema-gates`), so
"it passed locally" and "it passed in CI" cannot diverge.

## What is in here

| | |
|---|---|
| `001` … `060_*.sql` | the DDL, applied in filename order. `001`/`002` are bootstrap — `002` creates the roles, so it runs as the superuser; everything after runs as `kynex_migrator` with `SET ROLE kynex_owner`, the way the deploy applies it. |
| `schema.sql` | the canonical `pg_dump --schema-only --no-owner`, normalised and committed. **Never hand-edited.** The byte-diff gate compares a fresh build against it, and that is what catches the triggers, policies, partitions, `EXCLUDE`s, grants and `COMMENT`s that EF cannot see. |
| `tests/070_rls_proof.sql` | the RLS and partition proofs, including the counter-proof that `security_invoker` is what holds the door. |
| `tests/080_schema_ratchets.sql` | only what `070` does not assert: declared policy **shape** vs live policy text, the manifest closing arithmetically, table comment + owner + retention, index justification, heap. |
| `tests/verify_*.sh` | the per-file verification scripts from the authoring passes. |

## Two rules

1. **Any DDL change regenerates `schema.sql`, in the same commit.** The diff to that file *is* your
   change, and it is the most reviewable thing a schema PR produces.
2. **Do not widen `scripts/schema-normalise.sh`** to make a red diff go green. Whatever it deletes,
   the gate is blind to. `--self-test` will refuse.

Full runbook: `HOW_TO_CHANGE_THE_SCHEMA.md` (see `KEEPING_DOCS_HONEST.md` §7 — these seven
documents still live outside the repository and are due to move to `docs/schema/`).
