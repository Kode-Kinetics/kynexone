# M3: cost-accounting constraint validation (placeholder, NOT built)

Migration M1 (`CostAccountingFoundation`) deliberately leaves two things for a later migration, M3. **Do not create M3
until both conditions hold:**

1. the WP-A validator has shipped (every door that writes a cost-centre code or reference validates it), and
2. `scripts/cost-accounting-preflight-audit.sql` returns **0 in every row** on production (repair first if it does not).

The same audit script is also the gate on the production deploy of M1 itself: the foreign keys M1 adds are `NOT VALID`,
and PostgreSQL still checks a row's reference again whenever that column is written, so a legacy dangling id would fail the
next edit of its row.

## Why these four CHECKs are not in M1

Even `NOT VALID`, PostgreSQL re-evaluates a CHECK on every INSERT **and UPDATE** of a row. A legacy cost centre with an odd
code could then not be renamed, and a legacy ledger row with a lowercase currency could not take an `erp_*` status update. M1 must change no
behaviour, so they wait. The other CHECKs in M1 read only new, all-NULL columns (or defaulted ones) and cannot reject any legacy row.

## M3 SQL (exact)

```sql
SET LOCAL lock_timeout = '5s';

-- 1. The four CHECKs, added NOT VALID first so the table lock is short, then validated.
ALTER TABLE cost_centers ADD CONSTRAINT ck_cost_centers__code_format
  CHECK (code ~ '^[A-Za-z0-9][A-Za-z0-9._-]{0,29}$') NOT VALID;
ALTER TABLE finance_gl_entries ADD CONSTRAINT ck_finance_gl_entries__amount_nonnegative
  CHECK (amount >= 0) NOT VALID;
ALTER TABLE finance_gl_entries ADD CONSTRAINT ck_finance_gl_entries__has_account
  CHECK ((debit_account <> '' OR credit_account <> '')) NOT VALID;
ALTER TABLE finance_gl_entries ADD CONSTRAINT ck_finance_gl_entries__currency_format
  CHECK (currency ~ '^[A-Z]{3}$') NOT VALID;

ALTER TABLE cost_centers VALIDATE CONSTRAINT ck_cost_centers__code_format;
ALTER TABLE finance_gl_entries VALIDATE CONSTRAINT ck_finance_gl_entries__amount_nonnegative;
ALTER TABLE finance_gl_entries VALIDATE CONSTRAINT ck_finance_gl_entries__has_account;
ALTER TABLE finance_gl_entries VALIDATE CONSTRAINT ck_finance_gl_entries__currency_format;

-- 2. Validate the NOT VALID foreign keys M1 created.
ALTER TABLE departments              VALIDATE CONSTRAINT fk_departments__cost_center;
ALTER TABLE positions                VALIDATE CONSTRAINT fk_positions__cost_center;
ALTER TABLE employees                VALIDATE CONSTRAINT fk_employees__cost_center;
ALTER TABLE timesheet_entries        VALIDATE CONSTRAINT fk_timesheet_entries__cost_center;
ALTER TABLE gl_account_mappings      VALIDATE CONSTRAINT fk_gl_account_mappings__segment_cost_center;
ALTER TABLE payroll_earnings         VALIDATE CONSTRAINT fk_payroll_earnings__cost_center;
ALTER TABLE payroll_deductions       VALIDATE CONSTRAINT fk_payroll_deductions__cost_center;
ALTER TABLE payroll_slips            VALIDATE CONSTRAINT fk_payroll_slips__cost_center;
ALTER TABLE eosb_provision_snapshots VALIDATE CONSTRAINT fk_eosb_provision_snapshots__cost_center;
```

The model needs the four CHECKs added back to `CostAccountingModelConfiguration` (`amount`, `has_account` and `currency_format`
PostgreSQL-only for the regex one; none of them in SQLite-created test databases) in the same change, so the snapshot stays honest.
`Down` drops the four CHECKs and leaves the foreign keys validated.
