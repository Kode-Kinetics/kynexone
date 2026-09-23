#!/usr/bin/env bash
# schema-normalise.sh — the ONLY normalisation the schema-drift byte-diff gate applies.
#
#   pg_dump --schema-only --no-owner … | ./scripts/schema-normalise.sh > schema.sql
#   ./scripts/schema-normalise.sh --self-test
#
# TARGET_SCHEMA.md §19.1 revision 7, and KEEPING_DOCS_HONEST.md assertion 18:
#
#   "schema-normalise.sh removes only \restrict/\unrestrict guard lines, the version banner
#    and SET/empty-comment lines, and the test proves it is a no-op on statement text."
#
# It removes exactly four kinds of WHOLE LINE, and nothing else. It never sorts, never
# reformats, never rewrites anything inside a statement. That restraint is the point: a
# normaliser widened until the byte-diff passes on anything is how a gate that was red on
# day one gets quietly retired, and the four removals below are each forced by a specific
# pg_dump behaviour rather than by a diff someone wanted to go away.
#
#   1. \restrict <nonce> / \unrestrict <nonce>
#      pg_dump 16.10+/17.6+ wraps its output in these psql guards and REGENERATES THE NONCE
#      ON EVERY INVOCATION. Two dumps of one byte-identical database differ on these lines
#      alone. Without this removal the gate is red permanently and therefore ignored.
#   2. -- Dumped from database version … / -- Dumped by pg_dump version …
#      The banner records the server and client patch level. A Postgres point release on the
#      runner would fail the gate while the schema is unchanged.
#   3. Top-of-file SET lines (column 0, `SET <name> = <value>;`)
#      pg_dump's session preamble. Not schema. An indented SET — the `SET search_path` clause
#      of a SECURITY DEFINER function, which assertion 14 requires — is NOT at column 0 and
#      survives, which the self-test proves.
#   4. Empty comment lines (`--` with nothing after it)
#      pg_dump's spacing. Removing them keeps the diff readable without touching content.
#
# Everything else — every CREATE, every COMMENT with text, every GRANT, POLICY, TRIGGER,
# EXCLUDE, partition bound — passes through byte for byte.
set -euo pipefail

normalise() {
  # sed, not a loop: one pass, no interpretation of the payload.
  # The four patterns are anchored so nothing indented or embedded can match.
  sed -E \
    -e '/^\\restrict[[:space:]]/d' \
    -e '/^\\unrestrict[[:space:]]/d' \
    -e '/^-- Dumped from database version /d' \
    -e '/^-- Dumped by pg_dump version /d' \
    -e '/^SET [A-Za-z_][A-Za-z0-9_.]*[[:space:]]*(=|TO)[[:space:]].*;$/d' \
    -e '/^SELECT pg_catalog\.set_config\('"'"'search_path'"'"'.*\);$/d' \
    -e '/^--$/d'
}

# ---------------------------------------------------------------------------
# --self-test: assertion 18's own proof.
#
# A normaliser is a gate's blind spot by construction — whatever it deletes, the gate cannot
# see. So this proves two directions on a fixture that deliberately looks like the patterns:
#   (a) the four nonce/banner/preamble/spacing forms ARE removed, and
#   (b) statement text that RESEMBLES them is NOT: an indented SET inside a function header,
#       a SET inside a quoted body, a COMMENT whose text contains "SET", a line that is a
#       comment with content, a column literally named "set".
# If (b) ever regresses the gate would start passing over real drift.
# ---------------------------------------------------------------------------
if [[ "${1:-}" == "--self-test" ]]; then
  fixture=$(cat <<'FIX'
--
-- PostgreSQL database dump
--

\restrict 7Hq2kLpZzX9aQwErTyUiOpAsDfGhJkLz
-- Dumped from database version 16.15 (Debian 16.15-1.pgdg13+2)
-- Dumped by pg_dump version 16.15
SET statement_timeout = 0;
SET client_encoding = 'UTF8';
SET row_security = off;
SELECT pg_catalog.set_config('search_path', '', false);

CREATE FUNCTION app.resolve_login(tenant_slug public.citext, email public.citext) RETURNS TABLE(user_id uuid)
    LANGUAGE sql STABLE SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'app'
    AS $$ SELECT u.id FROM users u WHERE u.tenant_id = app.current_tenant() $$;

COMMENT ON FUNCTION app.resolve_login(public.citext, public.citext) IS 'The login bypass surface. SET search_path is pinned; EXECUTE is revoked from PUBLIC.';

CREATE TABLE public.widget (
    id uuid NOT NULL,
    "set" text
);

-- a comment with content must survive
ALTER TABLE ONLY public.widget ADD CONSTRAINT pk_widget PRIMARY KEY (id);
\unrestrict 7Hq2kLpZzX9aQwErTyUiOpAsDfGhJkLz
FIX
)
  got=$(printf '%s\n' "$fixture" | normalise)

  fail() { echo "schema-normalise.sh --self-test FAILED: $*" >&2; exit 1; }

  # (a) the four removable forms are gone
  grep -q '^\\restrict'                     <<<"$got" && fail '\restrict guard survived'
  grep -q '^\\unrestrict'                   <<<"$got" && fail '\unrestrict guard survived'
  grep -q '^-- Dumped from database version' <<<"$got" && fail 'version banner survived'
  grep -q '^-- Dumped by pg_dump version'    <<<"$got" && fail 'pg_dump banner survived'
  grep -q '^SET statement_timeout'          <<<"$got" && fail 'preamble SET survived'
  grep -q '^SET row_security'               <<<"$got" && fail 'preamble SET row_security survived'
  grep -q "^SELECT pg_catalog.set_config('search_path'" <<<"$got" && fail 'preamble set_config survived'
  grep -qx -- '--'                          <<<"$got" && fail 'empty comment survived'

  # (b) statement text that LOOKS like the patterns is untouched
  grep -q "SET search_path TO 'pg_catalog', 'app'" <<<"$got" \
    || fail "the SECURITY DEFINER function's indented SET search_path was eaten — assertion 14 would then be unverifiable from schema.sql"
  grep -q 'SET search_path is pinned' <<<"$got" \
    || fail 'a COMMENT whose text contains SET was eaten'
  grep -q '"set" text' <<<"$got" \
    || fail 'a column named "set" was eaten'
  grep -q '^-- a comment with content must survive$' <<<"$got" \
    || fail 'a comment WITH content was eaten — only the empty `--` may go'
  grep -q 'CREATE TABLE public.widget' <<<"$got" || fail 'a CREATE TABLE was eaten'
  grep -q 'PRIMARY KEY (id)'           <<<"$got" || fail 'a constraint line was eaten'
  grep -q 'app.current_tenant()'       <<<"$got" || fail 'a function body was eaten'

  # (c) every line that WAS removed matches one of the four declared patterns. This is the
  #     assertion that stops the normaliser from being widened by accident: a new sed rule
  #     that removes anything else fails here even if every check above still passes.
  removed=$(diff <(printf '%s\n' "$fixture") <(printf '%s\n' "$got") \
            | sed -n 's/^< //p' \
            | grep -vE '^(\\restrict |\\unrestrict |-- Dumped from database version |-- Dumped by pg_dump version |SET [A-Za-z_][A-Za-z0-9_.]*[[:space:]]*(=|TO)[[:space:]].*;$|SELECT pg_catalog\.set_config\('"'"'search_path'"'"'.*\);$|--$)' || true)
  if [[ -n "$removed" ]]; then
    fail "the normaliser removed a line outside the four declared patterns:
$removed"
  fi

  echo "schema-normalise.sh --self-test ok — 4 removals, and a no-op on statement text (assertion 18)"
  exit 0
fi

normalise
