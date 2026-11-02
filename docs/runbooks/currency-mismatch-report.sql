-- READ-ONLY report: rows whose currency differs from their employing company's default currency.
-- For the owner's review only. It changes nothing; there is no data migration.
--
-- Background: several payroll and organisation tables used to default their currency to a hard-coded literal
-- (AED, SAR or USD). A company trading in another currency could therefore hold rows in the wrong one. New rows now
-- take the company's currency when saved (CompanyCurrencyDefaults). Existing rows are left as they are.
--
-- Run as a role that can read the tenant's tables (set app.tenant_id first if row-level security is on):
--   SET app.tenant_id = '<tenant uuid>';
--   \i docs/runbooks/currency-mismatch-report.sql
-- A mismatch is not always an error (a company may deliberately pay some staff in another currency); review each.

WITH company AS (
    SELECT id AS company_id, tenant_id, upper(default_currency) AS company_currency
    FROM companies
)
-- Tables that carry the company directly.
SELECT 'salary_structures' AS source, t.id::text AS row_id, t.tenant_id, t.company_id, upper(t.currency) AS row_currency, c.company_currency
FROM salary_structures t JOIN company c ON c.company_id = t.company_id AND c.tenant_id = t.tenant_id
WHERE upper(t.currency) <> c.company_currency
UNION ALL
SELECT 'grades', t.id::text, t.tenant_id, t.company_id, upper(t.currency), c.company_currency
FROM grades t JOIN company c ON c.company_id = t.company_id AND c.tenant_id = t.tenant_id
WHERE upper(t.currency) <> c.company_currency
UNION ALL
SELECT 'positions', t.id::text, t.tenant_id, t.company_id, upper(t.currency), c.company_currency
FROM positions t JOIN company c ON c.company_id = t.company_id AND c.tenant_id = t.tenant_id
WHERE upper(t.currency) <> c.company_currency
UNION ALL
SELECT 'benefit_plans', t.id::text, t.tenant_id, t.company_id, upper(t.currency), c.company_currency
FROM benefit_plans t JOIN company c ON c.company_id = t.company_id AND c.tenant_id = t.tenant_id
WHERE upper(t.currency) <> c.company_currency
UNION ALL
SELECT 'payroll_opening_balances', t.id::text, t.tenant_id, t.company_id, upper(t.currency), c.company_currency
FROM payroll_opening_balances t JOIN company c ON c.company_id = t.company_id AND c.tenant_id = t.tenant_id
WHERE upper(t.currency) <> c.company_currency
UNION ALL
SELECT 'eosb_provision_snapshots', t.id::text, t.tenant_id, t.company_id, upper(t.currency), c.company_currency
FROM eosb_provision_snapshots t JOIN company c ON c.company_id = t.company_id AND c.tenant_id = t.tenant_id
WHERE upper(t.currency) <> c.company_currency
UNION ALL
SELECT 'gl_adjustment_requests', t.id::text, t.tenant_id, t.company_id, upper(t.currency), c.company_currency
FROM gl_adjustment_requests t JOIN company c ON c.company_id = t.company_id AND c.tenant_id = t.tenant_id
WHERE upper(t.currency) <> c.company_currency
UNION ALL
SELECT 'employee_final_settlements', t.id::text, t.tenant_id, t.company_id, upper(t.currency), c.company_currency
FROM employee_final_settlements t JOIN company c ON c.company_id = t.company_id AND c.tenant_id = t.tenant_id
WHERE upper(t.currency) <> c.company_currency
UNION ALL
SELECT 'employee_contracts', t.id::text, t.tenant_id, t.company_id, upper(t.currency_code), c.company_currency
FROM employee_contracts t JOIN company c ON c.company_id = t.company_id AND c.tenant_id = t.tenant_id
WHERE upper(t.currency_code) <> c.company_currency
-- Tables reached through the employee.
UNION ALL
SELECT 'employee_payroll_profiles', t.id::text, t.tenant_id, e.company_id, upper(t.salary_currency), c.company_currency
FROM employee_payroll_profiles t
JOIN employees e ON e.id = t.employee_id AND e.tenant_id = t.tenant_id
JOIN company c ON c.company_id = e.company_id AND c.tenant_id = t.tenant_id
WHERE upper(t.salary_currency) <> c.company_currency
UNION ALL
SELECT 'employee_salary_structures', t.id::text, t.tenant_id, e.company_id, upper(t.currency), c.company_currency
FROM employee_salary_structures t
JOIN employees e ON e.id = t.employee_id AND e.tenant_id = t.tenant_id
JOIN company c ON c.company_id = e.company_id AND c.tenant_id = t.tenant_id
WHERE upper(t.currency) <> c.company_currency
UNION ALL
SELECT 'overtime_calculations', t.id::text, t.tenant_id, e.company_id, upper(t.currency), c.company_currency
FROM overtime_calculations t
JOIN employees e ON e.id = t.employee_id AND e.tenant_id = t.tenant_id
JOIN company c ON c.company_id = e.company_id AND c.tenant_id = t.tenant_id
WHERE upper(t.currency) <> c.company_currency
ORDER BY 1, 3, 4;

-- Summary by table and currency pair (run after the detail query if the detail is long):
-- SELECT source, row_currency, company_currency, count(*) FROM (<the query above>) m GROUP BY 1, 2, 3 ORDER BY 1;
