-- =============================================================================
-- 022_constraints_cross.sql
-- KynexOne baseline schema — TARGET_SCHEMA.md revision 6.
--
-- The SIX foreign keys of the §8.2 register that belong to neither constraints
-- file, because their CHILD is a domain A–F table and their PARENT is a domain
-- G–R table. 020_constraints_a_f.sql could not declare them (the parent did not
-- exist yet) and 021_constraints_g_r.sql does not own them (the child is not in
-- its scope). They are listed verbatim at the foot of 020, by register number,
-- with the ON DELETE each one carries; this file is that list, implemented.
--
--   41  employee_assignments.approval_request_id -> approval_requests   O  RESTRICT
--   43  employee_salaries.approval_request_id    -> approval_requests   O  RESTRICT
--   51  employee_documents.leave_request_id      -> leave_requests      O  SET NULL
--   57  payroll_runs.source_import_job_id        -> background_jobs     O  SET NULL
--   58  payroll_runs.approval_request_id         -> approval_requests   O  RESTRICT
--   68  payroll_slip_lines.loan_installment_id   -> loan_installments   O  RESTRICT
--
-- Apply order: after 020 and 021, before 040_indexes.sql (which carries the
-- covering indexes for five of these six; see the note on row 68 there).
--
-- RULES APPLIED, identical to 020 and 021 (§8.1):
--   * ON UPDATE RESTRICT on every row, without exception: keys are uuid and are
--     never updated.
--   * Composite on (tenant_id, x_id) REFERENCES x (tenant_id, id), so no row can
--     point across tenants even through an application bug.
--   * A composite ON DELETE SET NULL names its column. Plain SET NULL would null
--     tenant_id as well, which is NOT NULL, so the delete would fail at runtime
--     instead of at DDL time. PG15+ syntax; production is Neon PG 17.11.
--
-- ROW 57 — the question 020 left open, decided.
--   020 flagged that payroll_runs.source_import_job_id could not be composite
--   "unless background_jobs carries UNIQUE NULLS NOT DISTINCT (tenant_id, id)".
--   It does not need to. 010_platform.sql already declares
--       CONSTRAINT uq_background_jobs__tenant_id UNIQUE (tenant_id, id)
--   and PostgreSQL accepts a plain UNIQUE over nullable columns as a foreign-key
--   target. NULLS NOT DISTINCT would add nothing here, because background_jobs.id
--   is the primary key: (tenant_id, id) is therefore unique whatever tenant_id
--   holds, and there is no duplicate-NULL row for the index to fail to exclude.
--
--   The constraint is therefore declared COMPOSITE, like every other row in the
--   register. Its one behavioural consequence, which is the correct one: under
--   MATCH SIMPLE a payroll run (tenant_id NOT NULL) can only ever point at a
--   background job that carries the SAME tenant_id. A platform-tier job, which
--   is what a NULL tenant_id means, is unreachable from a tenant payroll run —
--   exactly the isolation the composite convention exists to buy. A run's import
--   job is by definition a tenant job.
-- =============================================================================


-- -----------------------------------------------------------------------------
-- Register rows 41 and 43 — the two dated employee tables and their approval.
--
-- §11.1: employee_assignments and employee_salaries have no status of their own;
-- the approval IS their state, and the row exists only once the change is applied.
-- RESTRICT, because an approval decision that produced a salary or an assignment
-- is audited evidence and must not disappear underneath it.
-- Optional (O): rows created by the initial import or by a direct HR act carry no
-- approval request.
-- -----------------------------------------------------------------------------

ALTER TABLE employee_assignments
    ADD CONSTRAINT fk_employee_assignments__approval_request_id
    FOREIGN KEY (tenant_id, approval_request_id)
    REFERENCES approval_requests (tenant_id, id)
    ON UPDATE RESTRICT ON DELETE RESTRICT;

COMMENT ON CONSTRAINT fk_employee_assignments__approval_request_id ON employee_assignments IS
    '§8.2 row 41. RESTRICT: the approval is the assignment''s only state (§11.1).';

ALTER TABLE employee_salaries
    ADD CONSTRAINT fk_employee_salaries__approval_request_id
    FOREIGN KEY (tenant_id, approval_request_id)
    REFERENCES approval_requests (tenant_id, id)
    ON UPDATE RESTRICT ON DELETE RESTRICT;

COMMENT ON CONSTRAINT fk_employee_salaries__approval_request_id ON employee_salaries IS
    '§8.2 row 43. RESTRICT: the approval is the salary change''s only state (§11.1).';


-- -----------------------------------------------------------------------------
-- Register row 51 — a letter or certificate generated for a leave request.
--
-- SET NULL, not RESTRICT: the document is evidence in its own right and outlives
-- the request that occasioned it. §8.1's SET NULL rule — "only where the column
-- is genuinely optional and the fact survives the loss".
-- -----------------------------------------------------------------------------

ALTER TABLE employee_documents
    ADD CONSTRAINT fk_employee_documents__leave_request_id
    FOREIGN KEY (tenant_id, leave_request_id)
    REFERENCES leave_requests (tenant_id, id)
    ON UPDATE RESTRICT ON DELETE SET NULL (leave_request_id);

COMMENT ON CONSTRAINT fk_employee_documents__leave_request_id ON employee_documents IS
    '§8.2 row 51. SET NULL (leave_request_id) only — a bare SET NULL would null tenant_id, which is NOT NULL.';


-- -----------------------------------------------------------------------------
-- Register row 57 — the import job a run was built from. See the header.
-- -----------------------------------------------------------------------------

ALTER TABLE payroll_runs
    ADD CONSTRAINT fk_payroll_runs__source_import_job_id
    FOREIGN KEY (tenant_id, source_import_job_id)
    REFERENCES background_jobs (tenant_id, id)
    ON UPDATE RESTRICT ON DELETE SET NULL (source_import_job_id);

COMMENT ON CONSTRAINT fk_payroll_runs__source_import_job_id ON payroll_runs IS
    '§8.2 row 57. COMPOSITE after all: background_jobs already carries UNIQUE (tenant_id, id), '
    'which PostgreSQL accepts as an FK target over a nullable tenant_id. NULLS NOT DISTINCT is '
    'unnecessary because id is the primary key. A platform-tier job (tenant_id NULL) is therefore '
    'unreachable from a tenant run, which is the intended isolation.';


-- -----------------------------------------------------------------------------
-- Register row 58 — the run's own approval.
--
-- RESTRICT: payroll_runs is the money path. §10.1 routes Processed -> Approved
-- and PendingFinanceReview -> Approved through the approval engine, so the
-- decision row is the evidence for a run that was paid.
-- -----------------------------------------------------------------------------

ALTER TABLE payroll_runs
    ADD CONSTRAINT fk_payroll_runs__approval_request_id
    FOREIGN KEY (tenant_id, approval_request_id)
    REFERENCES approval_requests (tenant_id, id)
    ON UPDATE RESTRICT ON DELETE RESTRICT;

COMMENT ON CONSTRAINT fk_payroll_runs__approval_request_id ON payroll_runs IS
    '§8.2 row 58. RESTRICT: the decision that approved a paid run is audited evidence.';


-- -----------------------------------------------------------------------------
-- Register row 68 — the installment a slip line recovered.
--
-- This is the surviving half of one of the two cycles §8.4 broke: the line points
-- at the installment, and loan_installments no longer carries recovered_slip_line_id.
-- "Which line recovered this installment" is answered from this column, so it needs
-- an index (040) as well as the constraint.
-- RESTRICT, because a recovery is a financial fact.
-- -----------------------------------------------------------------------------

ALTER TABLE payroll_slip_lines
    ADD CONSTRAINT fk_payroll_slip_lines__loan_installment_id
    FOREIGN KEY (tenant_id, loan_installment_id)
    REFERENCES loan_installments (tenant_id, id)
    ON UPDATE RESTRICT ON DELETE RESTRICT;

COMMENT ON CONSTRAINT fk_payroll_slip_lines__loan_installment_id ON payroll_slip_lines IS
    '§8.2 row 68. The surviving direction of the cycle §8.4 broke; read parent-side, so 040 indexes it.';
