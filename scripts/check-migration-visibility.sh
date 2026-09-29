#!/usr/bin/env bash
#
# GATE: every migration file on disk must be visible to `dotnet ef`.
#
# WHY THIS EXISTS
# ---------------
# A migration hand-written without `dotnet ef migrations add` has no
# [Migration]/[DbContextAttribute] pair. EF's MigrationsAssembly discovers migrations by
# scanning for that attribute, so such a file is INVISIBLE: `dotnet ef database update`
# exits 0 having silently skipped it, and so does everything built on EF —
# `--migrate`, the CI migrate job, /health/ready's pending-migration count and the
# Schema Gates job. Every tool we own is blind at once and nothing reports an error.
#
# That happened here. Commit 15148d0 (2026-07-13) added three such files. They stayed
# invisible for 70 days:
#   20260713061000_AddSalaryStructureEligibilityAndVersioning
#   20260713062000_BackfillEmployeeChangeApprovalRequests
#   20260713073000_AddApprovalQueueAccountability
# Measured at the time of the fix: 72 migration files on disk, 69 visible to EF.
#
# This gate is the only check in the pipeline that does not ask EF what the migrations
# are. It asks the FILESYSTEM, then asks EF, and fails if the two disagree. A check
# that shares its source of truth with the thing it is checking cannot catch this class
# of bug, which is precisely why it went unnoticed for so long.
#
# Run:  scripts/check-migration-visibility.sh [path/to/Project.csproj]
# Exit: 0 = parity, 1 = discrepancy (names the files), 2 = could not run the check.

set -euo pipefail

PROJECT="${1:-backend-dotnet/Zayra.Api/Zayra.Api.csproj}"

if [ ! -f "$PROJECT" ]; then
  echo "::error::check-migration-visibility: project not found: $PROJECT" >&2
  exit 2
fi

MIGRATIONS_DIR="$(dirname "$PROJECT")/Migrations"
if [ ! -d "$MIGRATIONS_DIR" ]; then
  echo "::error::check-migration-visibility: no Migrations directory at $MIGRATIONS_DIR" >&2
  exit 2
fi

workdir="$(mktemp -d)"
trap 'rm -rf "$workdir"' EXIT

# ── Source of truth 1: the filesystem ────────────────────────────────────────────
# Every *.cs in Migrations/ that is not a .Designer.cs and not the model snapshot is a
# migration. Designer files carry metadata for a migration; the snapshot is not one.
find "$MIGRATIONS_DIR" -maxdepth 1 -name '*.cs' \
  ! -name '*.Designer.cs' \
  ! -name '*ModelSnapshot.cs' \
  -exec basename {} .cs \; | sort > "$workdir/on-disk.txt"

disk_count=$(wc -l < "$workdir/on-disk.txt" | tr -d ' ')
if [ "$disk_count" -eq 0 ]; then
  # Fail closed. An empty result here almost certainly means the glob or the path is
  # wrong, and a gate that silently compares two empty lists is the vacuous-pass failure
  # mode this repo has already shipped once.
  echo "::error::check-migration-visibility: found ZERO migration files in $MIGRATIONS_DIR — refusing to pass vacuously." >&2
  exit 2
fi

# ── Source of truth 2: EF Core ───────────────────────────────────────────────────
# --no-connect: this is a static parity check, it must not need a database.
# Never --no-build: `dotnet ef --no-build` reads a stale compiled assembly and reports
# migrations that the current source does not have (and vice versa).
#
# --context is REQUIRED, not optional. The assembly now declares two DbContexts —
# ZayraDbContext (the live schema, EF-migrated) and KynexDbContext (the V2 rebuild, whose
# schema is the hand-written baseline SQL and which therefore has NO EF migrations at all).
# Without --context, dotnet-ef refuses to guess:
#     "More than one DbContext was found. Specify which one to use."
# and exits non-zero with that message on STDOUT, so the error file this script prints is
# EMPTY and the failure reads as inscrutable. That is exactly how it presented on PR #86.
#
# ZayraDbContext is the right answer here on purpose: this gate compares Migrations/*.cs
# against what EF can see, and every one of those files belongs to ZayraDbContext.
if ! dotnet tool run dotnet-ef migrations list --no-connect \
     --project "$PROJECT" --context ZayraDbContext \
     > "$workdir/ef-raw.txt" 2> "$workdir/ef-err.txt"; then
  echo "::error::check-migration-visibility: 'dotnet ef migrations list' failed. Output:" >&2
  # dotnet-ef writes its own diagnostics to stdout, not stderr — print both or lose the reason.
  cat "$workdir/ef-err.txt" >&2
  cat "$workdir/ef-raw.txt" >&2
  exit 2
fi

# The command prints prose after the list ("Pending status not shown…"), so keep only
# lines that look like a migration id: 14 digits, underscore, name.
grep -E '^[0-9]{14}_[A-Za-z0-9_]+$' "$workdir/ef-raw.txt" | sort > "$workdir/ef-visible.txt" || true
ef_count=$(wc -l < "$workdir/ef-visible.txt" | tr -d ' ')

invisible="$(comm -23 "$workdir/on-disk.txt" "$workdir/ef-visible.txt")"
phantom="$(comm -13 "$workdir/on-disk.txt" "$workdir/ef-visible.txt")"

echo "Migration files on disk : $disk_count"
echo "Visible to dotnet ef    : $ef_count"

if [ -z "$invisible" ] && [ -z "$phantom" ]; then
  echo "PASS — every migration on disk is visible to EF."
  exit 0
fi

echo "" >&2
echo "::error::check-migration-visibility: EF's view of the migrations does not match the filesystem." >&2

if [ -n "$invisible" ]; then
  echo "" >&2
  echo "  INVISIBLE TO EF ($(echo "$invisible" | wc -l | tr -d ' ') file(s)) — these exist on disk but EF will NOT apply them." >&2
  echo "  'dotnet ef database update' will exit 0 having skipped them, and /health/ready will not count them:" >&2
  echo "$invisible" | sed 's/^/    - /' >&2
  echo "" >&2
  echo "  FIX: each migration class needs both attributes, e.g." >&2
  echo "    [Microsoft.EntityFrameworkCore.Infrastructure.DbContextAttribute(typeof(ZayraDbContext))]" >&2
  echo "    [Migration(\"20260713062000_BackfillEmployeeChangeApprovalRequests\")]" >&2
  echo "  (the argument is the FILENAME without .cs). Generate new migrations with" >&2
  echo "  'dotnet ef migrations add' rather than writing the file by hand." >&2
fi

if [ -n "$phantom" ]; then
  echo "" >&2
  echo "  KNOWN TO EF BUT NOT ON DISK ($(echo "$phantom" | wc -l | tr -d ' ') file(s)) — a [Migration] id that does not match its filename," >&2
  echo "  or a duplicate id. EF applies the id, the filesystem says otherwise:" >&2
  echo "$phantom" | sed 's/^/    - /' >&2
fi

echo "" >&2
exit 1
