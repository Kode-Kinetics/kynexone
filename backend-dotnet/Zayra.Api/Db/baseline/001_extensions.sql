-- =============================================================================
-- 001_extensions.sql
-- KynexOne baseline — PostgreSQL extensions.
--
-- Source of truth: TARGET_SCHEMA.md revision 6, §19.1 (file layout).
-- Idempotent: safe to re-run against a database that already has them.
--
-- btree_gist          — required by every `EXCLUDE USING gist` in this baseline.
--                       Effective-dated tables exclude on (scalar =, daterange &&)
--                       and statutory_rule_bands / nitaqat_grid on (uuid =, numrange &&);
--                       gist has no native operator class for uuid / text / date
--                       scalars without it (CONVENTIONS.md §5, TARGET_SCHEMA.md §1).
-- pg_trgm             — required by §19.4 H1: the employee list/search GIN index over
--                       employee_number ‖ name_en ‖ name_ar ‖ work_email. Created here
--                       because the index pass (040_indexes.sql) must not create extensions.
-- pgcrypto            — digest()/gen_random_bytes() for the hash columns the design
--                       stores as lowercase hex (files.sha256, payroll_slips.payslip_sha256,
--                       auth_tokens.token_hash, audit_logs.envelope_hash).
--                       NOTE: gen_random_uuid() is core in PG13+, and keys are UUIDv7
--                       generated app-side (CONVENTIONS.md §2), so pgcrypto is NOT the
--                       key generator here.
-- pg_stat_statements  — §19.1/§19.6 operational readiness. CREATE EXTENSION succeeds
--                       without shared_preload_libraries; the view only returns rows once
--                       the library is preloaded, which is a server-config task, not DDL.
-- =============================================================================

CREATE EXTENSION IF NOT EXISTS btree_gist;
CREATE EXTENSION IF NOT EXISTS pg_trgm;
CREATE EXTENSION IF NOT EXISTS pgcrypto;
CREATE EXTENSION IF NOT EXISTS pg_stat_statements;

-- -----------------------------------------------------------------------------
-- DESIGN DEFECT (reported, not guessed):
-- TARGET_SCHEMA.md §19.2 specifies `app.resolve_login(tenant_slug citext, email citext)`,
-- but §19.1's extension list does not include `citext`, and §2 models the email columns
-- as plain identifiers (`users.normalized_email`, `platform_users.email`).
-- This baseline does NOT create citext: the tables below use `text` and rely on the
-- application's normalisation, consistent with the `normalized_email` column name.
-- If §19.2 is taken literally, citext must be added here AND the two email columns
-- retyped. That is an owner/architect call, not one to make silently.
-- -----------------------------------------------------------------------------
