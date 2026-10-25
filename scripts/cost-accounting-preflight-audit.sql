-- Cost-centre accounting: pre-deploy audit (T-043). READ-ONLY: a single SELECT, safe to run against production.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 -f scripts/cost-accounting-preflight-audit.sql
--
-- Run it BEFORE deploying migration M1 (`CostAccountingFoundation`) and again before M3
-- (docs/schema/M3_COST_ACCOUNTING_VALIDATION.md). The deploy is gated on every `violations` value being 0, or on a repair
-- script having been run first. Columns M1 creates do not exist yet on a pre-M1 database; their rows are simply absent.
--
-- fk:*     rows whose cost-centre reference matches no cost centre. M1 adds these foreign keys NOT VALID: legacy rows are not
--          checked at deploy time, but PostgreSQL checks the row again whenever its reference column is written. M3 VALIDATEs them.
--          Match rule = the key's own: (tenant_id, id); finance_gl_entries and companies.default_cost_center_id also match company.
-- check:*  rows that would violate a CHECK held back for M3 (it would reject an UPDATE of the legacy row even when NOT VALID).
--
-- Output: one row per check, ordered; rows with violations > 0 are the ones to repair.

WITH checks (label, tbl, col, predicate) AS (VALUES
  ('fk:departments.cost_center_id',            'departments',              'cost_center_id',
     $p$t.cost_center_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM cost_centers c WHERE c.tenant_id = t.tenant_id AND c.id = t.cost_center_id)$p$),
  ('fk:positions.cost_center_id',              'positions',                'cost_center_id',
     $p$t.cost_center_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM cost_centers c WHERE c.tenant_id = t.tenant_id AND c.id = t.cost_center_id)$p$),
  ('fk:employees.cost_center_id',              'employees',                'cost_center_id',
     $p$t.cost_center_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM cost_centers c WHERE c.tenant_id = t.tenant_id AND c.id = t.cost_center_id)$p$),
  ('fk:timesheet_entries.cost_center_id',      'timesheet_entries',        'cost_center_id',
     $p$t.cost_center_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM cost_centers c WHERE c.tenant_id = t.tenant_id AND c.id = t.cost_center_id)$p$),
  ('fk:gl_account_mappings.segment_cost_center_id', 'gl_account_mappings',  'segment_cost_center_id',
     $p$t.segment_cost_center_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM cost_centers c WHERE c.tenant_id = t.tenant_id AND c.id = t.segment_cost_center_id)$p$),
  ('fk:cost_centers.parent_id',                'cost_centers',             'parent_id',
     $p$t.parent_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM cost_centers c WHERE c.tenant_id = t.tenant_id AND c.id = t.parent_id)$p$),
  ('fk:payroll_earnings.cost_center_id',       'payroll_earnings',         'cost_center_id',
     $p$t.cost_center_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM cost_centers c WHERE c.tenant_id = t.tenant_id AND c.id = t.cost_center_id)$p$),
  ('fk:payroll_deductions.cost_center_id',     'payroll_deductions',       'cost_center_id',
     $p$t.cost_center_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM cost_centers c WHERE c.tenant_id = t.tenant_id AND c.id = t.cost_center_id)$p$),
  ('fk:payroll_slips.cost_center_id',          'payroll_slips',            'cost_center_id',
     $p$t.cost_center_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM cost_centers c WHERE c.tenant_id = t.tenant_id AND c.id = t.cost_center_id)$p$),
  ('fk:eosb_provision_snapshots.cost_center_id','eosb_provision_snapshots','cost_center_id',
     $p$t.cost_center_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM cost_centers c WHERE c.tenant_id = t.tenant_id AND c.id = t.cost_center_id)$p$),
  ('fk:companies.default_cost_center_id',      'companies',                'default_cost_center_id',
     $p$t.default_cost_center_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM cost_centers c WHERE c.tenant_id = t.tenant_id AND c.company_id = t.id AND c.id = t.default_cost_center_id)$p$),
  ('fk:finance_gl_entries.cost_center_id',     'finance_gl_entries',       'cost_center_id',
     $p$t.cost_center_id IS NOT NULL AND (t.company_id IS NULL OR NOT EXISTS (SELECT 1 FROM cost_centers c WHERE c.tenant_id = t.tenant_id AND c.company_id = t.company_id AND c.id = t.cost_center_id))$p$),
  ('check:cost_centers.code_format',           'cost_centers',             'code',
     $p$t.code !~ '^[A-Za-z0-9][A-Za-z0-9._-]{0,29}$'$p$),
  ('check:finance_gl_entries.amount_nonnegative','finance_gl_entries',     'amount',
     $p$t.amount < 0$p$),
  ('check:finance_gl_entries.has_account',     'finance_gl_entries',       'debit_account',
     $p$t.debit_account = '' AND t.credit_account = ''$p$),
  ('check:finance_gl_entries.currency_format', 'finance_gl_entries',       'currency',
     $p$t.currency !~ '^[A-Z]{3}$'$p$)
),
present AS (   -- skip a check whose column does not exist yet (a pre-M1 database)
  SELECT c.* FROM checks c
  JOIN information_schema.columns i
    ON i.table_schema = 'public' AND i.table_name = c.tbl AND i.column_name = c.col
)
SELECT label AS check_name,
       (xpath('/row/c/text()',
              query_to_xml(format('SELECT count(*) AS c FROM public.%I t WHERE %s', tbl, predicate), false, true, '')))[1]::text::bigint AS violations
FROM present
ORDER BY label;
