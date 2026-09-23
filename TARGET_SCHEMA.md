# KynexOne target schema: KSA core baseline

Status: **revision 7 — approved design of record; the baseline DDL slice is written.** Written 2026-09-22 against `integration` (`backend-dotnet/Zayra.Api/Data/ZayraDbContext.cs`, 323 DbSets); reviewed by two quality audits against the owner's 22-point standard, three adversarial passes, and — in revision 7 — by the first implementation of the baseline as hand-written SQL.
Owner's frame: the database is rebuilt empty on a fresh baseline, no data carried over, KSA core only. The 75-table cap was dropped by owner ruling (decision 9) in favour of the principle — no duplicate tables, nothing kept that nothing uses, every table traceable to a capability. Final count: 76.

**Revision 7 answers the implementation.** Two engineers wrote the 76-table baseline as hand-written SQL (`backend-dotnet/Zayra.Api/Db/baseline/`) and reported, in the files themselves, twelve places where revision 6 contradicted itself or stopped short of something the DDL could not avoid deciding. All twelve are settled here, and **the SQL is not changed by this revision** except where a decision below explicitly names a column rename or a missing constraint as follow-up work. The twelve: **(1) units** — every attendance, overtime and timesheet duration is **whole minutes**, leave stays in **days** (§2.K, §11.2, §15); **(2) constraint naming** — `ck_<table>__<assertion>` is the rule, and §9's 36 `chk_<table>_<column>` names are a closed, frozen legacy set because C# and CI assert against them (§9); **(3) eight ungoverned closed sets** get §9 rows (rows 37–44); **(4) §18 vs §2** — the three typed statutory identifiers stay and §18's external-id trio is additive, not a rename (§18); **(5) period naming** — one form, `year` + `month` as two `smallint` columns, role-prefixed only where a row carries more than one period (§2.F/H/I/N); **(6) tier letters** — `payroll_slips`, `payroll_slip_lines` and `payroll_issues` are corrected to tier **T** rather than given a `company_id` §8 never registered (§2.F); **(7) tenant FK gaps** — `gl_mappings`, `approval_workflows` and `document_templates` gain a direct `tenant_id → tenants` FK (§8.2 rows 159–161); **(8) the unstated but necessary** — `tenant_settings` PK, `employees.work_email`, `text` not `citext`, six implied uniques and nine introduced columns, each adopted or rejected by name (§2, §20); **(9) two real defects the design never addressed** — a NULL subject silently disables a gist EXCLUDE, and a composite `ON DELETE SET NULL` must name its column, which sets a **PostgreSQL 15 floor** (§19.1, §19.5, CONVENTIONS §3 and §5); **(10)** the §19.1 drift gate normalises away `pg_dump` 16+'s `\restrict` nonce lines; **(11)** §2's stale per-domain header counts are corrected to §1's table; **(12) FK arithmetic** — the register is **165 constraints, one per row**, of which 156 are declared, 6 are cross-domain and 3 are new (§8.2). Two more came from the trigger and index pass and are folded in: **(13)** `payroll_slip_lines.gosi_branch` and `.gosi_payer` are governed too, not only their `statutory_rules` twins, because `trg_gosi_filing_totals` pivots on them and **warns rather than blocks**, which makes an unconstrained value a silent shortfall in a filed GOSI return (§9 rows 37–38, §11.2); and **(14)** an `Opening` run gains **Draft → Voided** and **Locked → Voided**, because a go-live import is precisely the thing that gets redone and revision 6's wording left deleting the row as the only escape (§10.1). The same pass corrected §19.4's index arithmetic to **152 of 165 covered, 13 deliberately not**, and split the `granted_by` that appeared on both the indexed and unindexed lists. Eight more came from the roles, partition and policy pass: **(15)** §19.2's shape arithmetic did not close — the real split is **62 / 5 / 2 / 4 / 2 / 1 = 76**, `tenants` cannot express shape (a) at all, and `data_protection_keys` is grant-policed and in no shape; **(16)** `background_job_items.tenant_id` is nullable and was missing from shape (b), so **the nullable-tenant set is seven**; **(17)** `app.resolve_login` as specified was a **cross-tenant credential read**, and the confinement guard that closes it is now part of the specification; **(18)** a table cannot be converted to partitioned, so the baseline rebuilds each parent and restates nine FKs that `LIKE` will not copy; **(19)** `timestamptz` partition bounds must carry an explicit `+00`, and `DEFAULT` partitions stay outside the maintenance template on purpose; **(20)** three more secret columns, plus the `pg_stat_statements` revoke that no policy covers; **(21)** `app.is_platform()` must not be `SECURITY DEFINER` and must test `'MEMBER'`, not `'USAGE'`; **(22)** the deploy preconditions — a bootstrap role holding `CREATEROLE` **and** `BYPASSRLS`, and an application handed only `kynex_app`.

**Revision 2** answered an adversarial review: `PayrollOpeningBalances` was wrongly called dead (it is live in `MigrationImportController.cs:444,596,598,626`), which had deleted mid-year go-live; the payroll-slip witness columns were restored; every "dead" row was evidenced against the repo's own orphan register. 62 → 72 tables.

**Revision 3 answers the modelling audit** (`AUDIT_MODELLING.md`), whose verdict — "a table catalogue, not yet a data model" — is accepted. Revisions 1 and 2 specified what exists and stopped before specifying how it behaves. This revision adds the behaviour: a delete rule for every foreign key (§8), an enumerated domain for every status column (§9), a state machine per lifecycle entity (§10), an authoritative-fact and invariant register (§11), lifecycle, soft delete and a retention matrix as data (§12), type, currency, timezone and calendar decisions (§13), the audit columns the merged per-module logs need (§14), a justification or an invariant for every denormalized total (§15), the polymorphic-pointer rules (§16), the coupling map and porting order (§17), and the integration-surface decision (§18). Two FK cycles are broken outright (§8.4). 72 → **74 tables**: `retention_policies` and `company_pay_policies`, both taken because behaviour correctness needed them, as §7 explains.

**Revision 6 applies the final review** (approve-with-changes, 76 tables, build may start with the baseline DDL slice) and six documentation defects found during the schema-docs re-sync. Ten findings are folded in: both baseline views are `security_invoker` with CI assertions, because a view owned by a `BYPASSRLS` role would otherwise have leaked every tenant's balances (F1); `app.is_platform()` now requires real role membership and the threat model is stated (F2a); `app.resolve_login` is keyed on tenant + email with `EXECUTE` revoked from `PUBLIC` and a pinned `search_path` (F2b); a `kynex_migrator` login role exists, with the `BYPASSRLS` assertion rewritten to walk `pg_auth_members` transitively (F3); `retention_policies` moves to policy shape (b) and the nullable-tenant set is six, with the ratchet asserting the *shape* per table (F4); partition children are policed, un-granted and no longer excluded from the ratchet, with the detach-and-reattach recovery written down (F5); the tenant GUC is carried by an `AsyncLocal` ambient and a connection interceptor, folded into the first command batch (F6); `trg_run_totals` is conditioned on the status transition and a crashed run releases its claimed inputs (F7); the leave debit takes an advisory lock, because `REPEATABLE READ` does not detect write skew (F8); audit rows carry per-component hashes so the envelope can be recomputed after erasure, `ip` and `user_agent` move inside `personal_data`, and sequence gaps are recorded rather than alerted (F9); the approvals inbox gets the two denormalised approver columns its index needs (F10). The retention conflict between §12.4 and §19.3 is resolved at 24 months, the direction that does not over-retain.

**Revision 5 writes the platform half.** The owner has ruled on the platform audit: the hard table cap is dropped in favour of the principle (no duplicate tables, nothing kept that nothing uses, every table traceable to a capability), so 76 stands and no cut from §7 is taken; Postgres RLS is adopted; the baseline is authored as hand-written SQL DDL with the existing CI schema gates kept verbatim; and five tables are partitioned by month from the baseline. §19 specifies all of it, and four documentation defects are fixed: the nullable-tenant risk now names the right five tables, the porting risk states 76, row stamping is stated once in the conventions, and `platform_users` has a retention row.

**Revision 4 applies the owner's decisions.** Q1-Q8 are settled and are written as decisions, not questions (§5): uuid employee key; platform operators in their **own** `platform_users` table so no nullable-tenant row sits in the client-data tier; payroll **refuses** an employee whose GOSI cohort is unknown; enterprise SSO/SCIM dropped for now; per-user permission overrides replaced by custom roles; tenants may exceed a statutory minimum but never override the law; mid-year go-live supported through the Opening run; timesheets in scope. One further decision from the second adversarial review (F5): **delegated granting authority keeps its own table**, because it is live at three endpoints (`Controllers/AccessController.cs:464,472,484`) and `user_roles.granted_by` cannot express scope, sub-delegation or expiry. 74 → **76 tables**, which is above the 75 the owner set; §7 reports that honestly and lists, in priority order, what would be cut if the cap is enforced.

§19 is the platform design — DDL authoring, RLS, partitioning, indexes, transactions, operations — approved by the owner (decision 10) and specified but not yet built. It was written to attach rather than to displace, so nothing in Parts I and II moved to accommodate it.

## 1. Summary

**323 tables become 76 tables.** That is 47 kept (renamed or reshaped), 123 merged, 37 folded into JSON, 105 dropped because their module is out of scope, and 11 dropped as dead.

**The count is 76, and the hard cap is gone.** The owner has replaced the 75-table limit with the principle — no duplicate tables, nothing kept that nothing uses, every table traceable to a capability (decision 9) — so nothing is cut to fit a number, and §7 keeps the record of the cuts considered and rejected. Every table above 74 was added on an explicit decision. Revision 3 added `company_pay_policies` (contractual money out of JSON and under the dated discipline) and `retention_policies` (the PDPL matrix as data). Revision 4 adds `platform_users` (the owner's Q2 answer) and `permission_grantor_records` (the F5 decision). None is a convenience table, none duplicates another, and nothing here is kept that nothing uses. §7 states the honest number, the priority order in which tables would be cut if the cap is enforced as a hard limit, and what each cut costs.

| Domain | Tables |
|---|---|
| A. Platform, tenancy and shared infrastructure | 7 |
| B. Identity and access | 8 |
| C. Organisation | 8 |
| D. Employees, history, documents and letters | 7 |
| E. Statutory reference | 2 |
| F. Payroll | 6 |
| G. WPS | 2 |
| H. GL export | 4 |
| I. Loans and advances | 2 |
| J. Leave | 3 |
| K. Attendance, overtime and timesheets | 9 |
| L. End of service and final settlement | 3 |
| M. Nitaqat | 2 |
| N. GOSI registration and filing | 2 |
| O. Approvals | 4 |
| P. Notifications | 2 |
| Q. Audit | 3 |
| R. Jobs | 2 |
| **Total** | **76** |

### Conventions that apply to every table

- **Keys.** Every primary key is a `uuid`, generated app-side as UUIDv7 so inserts stay index-local. This includes `employees.id`. Today there are three employee key forms: `int Id`, `Guid PublicId`, and a `Guid EmployeeId` in 12 places pointing at PublicId (EmployeeContract, Visa/Passport/WorkPermit, EmployeeLoan, SalaryAdvance, EmployeeBonus and others). The baseline keeps one, `employee_id uuid`. People see `employee_number`.
- **Tenant isolation.** Every tenant-tier table has `tenant_id uuid NOT NULL` and `UNIQUE (tenant_id, id)`. Every FK between tenant tables is composite: `(tenant_id, x_id) REFERENCES x (tenant_id, id)`. Company-tier tables carry `company_id`, FK'd `(tenant_id, company_id)`. A row can never point across tenants, even through a bug.
- **Tiers.** *platform* = no tenant. *reference* = seeded by the baseline, read-only to tenants. *tenant* = `tenant_id`. *company* = `tenant_id` + `company_id` (legal entity / MOL establishment).
- **Types.** Money `numeric(18,2)` in the currency of record (SAR). Rates `numeric(9,6)`. Times `timestamptz`. Payroll dates `date`.
- **Effective dating (stated once, applies everywhere).** Every effective-dated table uses `effective_from date NOT NULL` and `effective_to date NULL`, both **inclusive**. NULL `effective_to` means open-ended. Every range expression in constraints, queries and rule resolution is `daterange(effective_from, COALESCE(effective_to, 'infinity'::date), '[]')` — inclusive on both ends, without exception. No-overlap is enforced by `EXCLUDE USING gist (tenant_id WITH =, <subject> WITH =, <that range> WITH &&)`, which needs `btree_gist`. Resolution for a date D is the single row whose range contains D.
  **A nullable subject column must be wrapped in `COALESCE` to a sentinel (revision 7 decision 9a).** This is the first of the two real defects the implementation found, and it is a *silent* one: an `EXCLUDE` constraint **skips any row whose operand is NULL**, so a nullable column in the subject list does not merely weaken the guarantee, it exempts exactly the rows that hold NULL. On `retention_policies`, where `tenant_id IS NULL` marks the **platform default row**, the effect was that the only rows free to overlap were the ones the entire retention engine reads when a tenant has no override — two overlapping platform defaults for one entity, accepted by the database, resolving non-deterministically, silently changing how long personal data is kept. Nothing would have raised. The rule is therefore: `COALESCE(tenant_id, '00000000-0000-0000-0000-000000000000'::uuid) WITH =` for a nullable uuid subject and `COALESCE(col, '') WITH =` for a nullable text dimension (`statutory_rules.gosi_branch`, `.payer`). The same trap applies to `UNIQUE`, where the fix is `UNIQUE NULLS NOT DISTINCT` (PG 15, §19.1) and which is why the nullable-tenant and nullable-company uniques are all written that way. CI asserts it: **every `EXCLUDE` and every `UNIQUE` whose column list contains a nullable column must use `COALESCE` or `NULLS NOT DISTINCT`**, which is checkable from `pg_constraint` and `information_schema` without reading the design. The baseline test suite must include boundary tests: a row ending on the 30th and the next starting on the 31st; a payroll run on the exact `effective_from`; a rule whose `effective_to` equals the period end; and an attempted overlapping insert that must be rejected by the database, not by the application.
- **Frozen rows.** Slips, slip lines, WPS lines, GOSI filings, EOS calculations and settlement lines hold snapshots, not live joins. A trigger rejects UPDATE and DELETE once the parent is `Locked`, `Filed` or `Approved`.
- **Append-only tables.** `leave_ledger`, `audit_logs`, `payroll_audit_logs` and `retention_purge_audits` have BEFORE UPDATE/DELETE triggers that raise. Corrections are reversing rows.
- **Idempotency.** Every async or import writer carries an `idempotency_key` with a unique index, noted per table. **On the five partitioned tables the unique must contain the partition key** — see §19.3, which restates `attendance_punches` and `audit_logs` correctly.
- **Numbering.** No sequence-like counter ever lives in a JSON settings blob. All of them live in `number_sequences` and are allocated with `UPDATE … RETURNING` in one statement.
- **Referential behaviour.** Every foreign key declares cardinality, optionality and `ON DELETE` in §8; none is left to the ORM default. The defaults of this design are: `RESTRICT` for anything financial, filed or audited; `CASCADE` only where the child is a part of its parent (a slip's lines, a batch's lines, a journal's lines, a job's items), always paired with a `BEFORE DELETE` guard on the parent for frozen states; `SET NULL` only where the column is genuinely optional. `ON UPDATE` is `RESTRICT` everywhere, because no key is ever updated. **There are no foreign-key cycles** (§8.4); if one is ever introduced it must be `DEFERRABLE INITIALLY DEFERRED` with a declared insertion order.
- **Enumerations.** One mechanism, no exceptions: every status, kind, purpose, category and type column is `text` with a named `CHECK (col IN (…))` constraint, mirrored by a C# constants class of the same name, and listed in §9. This follows what the live database already does (`Migrations/20260624000003_AddStatusCheckConstraints.cs:37,40`). No PostgreSQL enums (they make adding a value a migration with a lock) and no lookup tables for closed sets. The only lookup tables are the open, tenant-editable catalogues that already exist: `permissions`, `pay_components`, `leave_types`, `cost_centers`, `designations`, `grades`.
- **State changes.** No status column is ever written by a free string assignment. Every lifecycle entity in §10 has an enumerated transition set, a named actor, and a stated enforcement point (database trigger, `CHECK`, or a single service method with a test). Illegal transitions fail loudly.
- **Soft delete is the exception, not the pattern.** Only `tenants`, `employees`, `users` and `companies` carry soft-delete and retention columns, because only those four have legal traces that outlive the record (§12). Every other table is hard-deleted or immutable, so a ported controller never has to guess whether a filter is missing.
- **Currency.** SAR is the currency of record. Each legal entity carries `companies.currency_code char(3) NOT NULL DEFAULT 'SAR'`, and **every money column in the design is in that company's currency**. No per-row currency column exists anywhere, and multi-currency payroll is explicitly not in this baseline.
- **Time and calendar.** `tenants.timezone_id` (IANA, validated, default `Asia/Riyadh`) with an optional `companies.timezone_id` override anchors every business day. `attendance_days.work_date`, `overtime_requests.work_date`, `timesheet_entries.work_date` and the `payroll_runs` year/month period are **local dates in that timezone**, never derived from UTC at read time. Hijri dates are derived, never stored: `HijriDateService` feeds the Ramadan and statutory working-hours rules (`Infrastructure/Attendance/AttendanceService.cs:43-45`) from the same local date. §13 states this in full.
- **Bounded text.** Identifier and format-bearing columns are length-bounded (§13.3); free-text notes are unbounded `text`.
- **Row stamping, stated once.** Every mutable table carries `created_at timestamptz NOT NULL DEFAULT now()`, `created_by uuid`, `updated_at timestamptz`, `updated_by uuid` — maintained by one `trg_row_stamp` BEFORE INSERT/UPDATE trigger, not by application code, so a raw SQL fix or a background job cannot skip it. `created_by`/`updated_by` are plain uuids, not FKs, so a purged actor never blocks a business row, and they are never the audit trail: §14's `audit_logs` is. **Exempt** are the append-only and immutable tables, which carry `created_at` and their own actor column only (`leave_ledger`, `audit_logs`, `payroll_audit_logs`, `retention_purge_audits`, `attendance_punches`, `payroll_slip_lines`, `wps_lines`, `final_settlement_lines`, `gl_journal_lines`, `nitaqat_snapshots`, `background_job_items`), and the reference tables, which carry `effective_from` instead. `updated_at` is never used as a concurrency token; `xmin` is (§19.5).

## 2. Target tables

Legend for **Tier**: P = platform, R = reference, T = tenant, C = company.

### A. Platform, tenancy and shared infrastructure (7)
| Table | Purpose | Key columns | FKs | Tier | Replaces |
|---|---|---|---|---|---|
| `tenants` | Customer account, plan gating and tenancy lifecycle | slug (unique), name, `status` (§9), `timezone_id` (IANA, validated), `plan_code`, `plan_limits jsonb` (max_employees, max_users, max_companies), `plan_expires_at`, `enabled_modules text[]`, **lifecycle: `soft_deleted_at`, `purged_at`** (both read by `Infrastructure/Retention/Rules/SoftDeletedTenantRule.cs:94`) | none | P | Tenants, TenantSubscriptions, TenantFeatureFlags |
| `tenant_settings` | **The one settings row per tenant**, written **per section** | `id uuid` **PK** with `UNIQUE (tenant_id)` carrying the 1:1 — *not* `tenant_id` as a natural PK, which CONVENTIONS §2 forbids and which would also break the universal `UNIQUE (tenant_id, id)` of §3 (revision 7 decision 8a); `sections jsonb` with keys `general, hr, payroll, localization, branding, security, lookups, leave, loans, overtime, notification_templates, document_requirements, help_texts`, and `section_versions jsonb` holding one version per key. Writes are `jsonb_set` on a single key guarded by that key's version, so two admins editing different sections never clobber each other, and a whole-row PUT is not an API the service exposes. Each section is a versioned C# record validated on write | tenants | T | TenantHrConfigs, TenantLocalizationSettings, TenantBrandings, TenantFieldHelpTexts, SystemSettings, SecuritySettings, MasterData*, Loan/Advance/Overtime policies, NotificationTemplates, ComplianceRequirements, LeaveBlackoutDates, FiscalYears, PayrollGroups |
| `number_sequences` | Every human-facing number | company_id NULL = tenant-wide, `scope_key` ('employee_no','letter_no','run_no','wps_batch_no','settlement_no','gosi_filing_no','timesheet_no'), prefix, pattern, `next_value bigint`, `reset_period` ('None','Year','Month'), period_key; UNIQUE (tenant, company, scope_key, period_key) | companies | T | EmployeeIdRules, NumberingRules |
| `files` | Every stored blob, with its purge state | storage_key (unique), bucket, mime, size_bytes, `sha256`, purpose ('EmployeeDocument','Payslip','WpsSif','GlExport','BankConfirmation','Import','LetterPdf'), uploaded_by, `retention_until`, `purge_state` ('Active','PendingPurge','Purged'), purged_at | users | T | (new; storage keys were scattered across EmployeeDocuments, BankTransferFiles, GL and WPS batches) |
| `retention_policies` | **The retention matrix as data**, one row per entity (§12.4) | `entity_name`, `legal_basis`, `minimum_retention_months`, `trigger_event` ('SoftDelete','Separation','RecordDate','Expiry'), `disposition` ('Anonymise','Purge','Keep'), `owner_role`, `rule_key` (matches `RetentionRuleKeys`), source_reference, effective_from/to, tenant_id NULL = platform default; a tenant row may only **lengthen** a platform period (CHECK against the platform row at write) | tenants (nullable) | R/T | (new; the day counts live in `Infrastructure/Retention/DataRetentionOptions.cs:46,61` today) |
| `platform_users` | **Platform operators, in their own table** (owner decision, Q2): no nullable-tenant row ever sits in a client-data table | email (globally unique), full_name, password_hash, `status` (§9), `platform_role`, mfa_enabled, mfa_secret_encrypted, mfa_recovery_hashes, failed_login_count, lockout_end, last_login_at, `deleted_at` | none | P | PlatformUsers |
| `data_protection_keys` | ASP.NET Data Protection key ring (encrypts MFA secrets and tokens) | friendly_name, xml | none | P | DataProtectionKeys |

Keeping plan gating is justified: `TenantSubscriptions` and `TenantFeatureFlags` have 47 references between them and drive module gating and seat limits. That becomes three columns on `tenants`. Invoices, payments and pricing go.
`files` exists because PDPL erasure has to be executable: a purge job flips `purge_state`, deletes the blob and writes `retention_purge_audits`, while the referencing row keeps the hash as evidence that a document existed.

### B. Identity and access (8)
| Table | Purpose | Key columns | FKs | Tier | Replaces |
|---|---|---|---|---|---|
| `users` | Every **tenant** login: staff users and employees (ESS). `tenant_id` is NOT NULL — platform operators live in `platform_users` | normalized_email (unique per tenant), password_hash, `status` (§9), lockout_end, failed_login_count, `mfa_enabled, mfa_secret_encrypted, mfa_recovery_hashes jsonb`, `employee_id` (nullable, unique), `deleted_at`, `notification_prefs jsonb` | tenants, (tenant_id, employee_id) → employees | T | Users, EmployeeUserAccounts, Employee*NotificationPreferences |
| `roles` | Tenant roles, seeded per tenant at provisioning | code with **`UNIQUE (tenant_id, code)`**, name, is_system | tenants | T | Roles |
| `permissions` | Permission catalogue, including `access.grant.*` | code (unique), module, description | none | R | Permissions |
| `role_permissions` | Role ↔ permission | role_id, permission_code, **`UNIQUE (tenant_id, role_id, permission_code)`** — the join row has no meaning twice | roles, permissions | T | RolePermissions |
| `user_roles` | Role grant with **data scope** and **expiry** | user_id, role_id, `scope_company_id, scope_branch_id, scope_department_id` (NULL = whole tenant), `granted_by`, granted_at, `expires_at` | users, roles, companies, branches, departments | T | UserRoles, UserEntityAccesses, UserPermissionOverrides (decision 5: a per-user grant becomes a custom role) |
| `permission_grantor_records` | **Delegated granting authority, kept as its own table** (decision F5): who may grant what, whether they may sub-delegate, until when, and why | `grantor_user_id`, `permission_scope` ('all', a module prefix, or an explicit key list), `can_sub_delegate`, `granted_by_user_id`, `expires_at`, `reason`, `is_active`, revoked_at/by | users | T | PermissionGrantorRecords |
| `auth_sessions` | **One row per device**, not per login. Serves both subject kinds | `subject_kind` ('Tenant','Platform') with `user_id` and `platform_user_id` nullable and an XOR CHECK, `device_id` with `UNIQUE (subject_kind, COALESCE(user_id, platform_user_id), device_id)`, `refresh_token_hash` (rotated in place), `previous_token_hash` (reuse detection), expires_at, revoked_at, `push_token`, push_platform, last_seen_at, ip, user_agent | users, platform_users | T/P | RefreshTokens, EmployeeMobileDevices |
| `auth_tokens` | Single-use tokens, both subject kinds | `subject_kind`, `user_id` / `platform_user_id` (XOR CHECK), `purpose` ('PasswordReset','MfaChallenge','Invitation','EmailConfirm'), token_hash (unique), expires_at, consumed_at, attempts | users, platform_users | T/P | PasswordResetTokens, MfaChallengeTokens, EmployeeUserAccounts.Invitation* |

The push token sits on the device row, so refresh-token rotation and re-login never lose a device's push registration, and one revoked session cannot silence another device.

**Why `auth_sessions` and `auth_tokens` serve both subject kinds rather than being duplicated.** The owner's reason for a separate `platform_users` is that no nullable-tenant row should sit in a *client-data* table; sessions and one-time tokens are authentication infrastructure, not client data. Giving platform operators their own session and token tables would cost two more tables and two more copies of rotation, reuse-detection and lockout logic — the exact duplication the "no duplicate tables" principle forbids. The XOR CHECK makes the subject unambiguous, and `tenant_id` is nullable **only** on these two infrastructure tables, which §19's RLS work will treat as platform-tier.

**Delegated granting authority is not `user_roles.granted_by`.** `granted_by` records who made one grant. `permission_grantor_records` records that a person *may* grant, over a named scope, with or without sub-delegation, until a date, for a reason — a different fact, live at `Controllers/AccessController.cs:464,472,484` (list, add, revoke) and in User Management in the web app, and load-bearing in `Infrastructure/Auth/AccessManagementService.cs:418,1337,1373,1392` where the caller's own grant is checked before a grant is allowed.

### C. Organisation (8)
| Table | Purpose | Key columns | FKs | Tier | Replaces |
|---|---|---|---|---|---|
| `companies` | Legal entity and MOL establishment | name_en/ar, cr_number, `mol_establishment_no`, **`gosi_registration_no` with `UNIQUE (tenant_id, gosi_registration_no)`** so registrations and filings can FK to it (§11.5), `nitaqat_activity_code`, `wps_bank_code`, `wps_mol_id`, `currency_code char(3)`, `timezone_id` (optional override), `go_live_year smallint` + `go_live_month smallint` (the go-live period as two columns, not a named concept), `soft_deleted_at`, `settings jsonb` (non-money company overrides only; **pay policy has moved out**) | tenants | T | Companies, NitaqatEstablishmentProfiles, CompanyCutovers, CompanyComplianceProfiles, GCCComplianceSettings |
| `company_pay_policies` | **Contractual, above-floor pay parameters, out of JSON** and under the same dated discipline as everything else | company_id, `policy_key` (from the seeded allow-list that `ClientRateDefinition` used to hold), `pay_component_code` (nullable), `rate numeric(9,6)`, `amount numeric(18,2)`, `value_json jsonb` bounded by `CHECK (pg_column_size(value_json) <= 8192)` and a schema validated on write, effective_from/to with the standard gist no-overlap, approved_by, source_reference | companies, pay_components | C | CompanyRatePolicies |
| `branches` | Site | name, city, address, lat/lng, `geofence_radius_m`, holiday_calendar_code | companies | C | Branches, Locations |
| `departments` | Org unit | name, parent_id, `cost_center_id` (optional) | companies, cost_centers, self | C | Departments |
| `cost_centers` | **Real table**, because GL, timesheets and project costing all key on it | code (unique per company), name, parent_id, `gl_segment`, is_active | companies, self | C | CostCenters |
| `designations` | Job title | title_en/ar, `occupation_code` (MHRSD/GOSI occupation, needed for Saudization-restricted jobs) | tenants | T | Designations |
| `grades` | Grade and pay band | code with **`UNIQUE (tenant_id, code)`**, name, min/max basic, `pay_scale jsonb` | tenants | T | Grades, GradePayScaleComponents, SalaryStructures |
| `public_holidays` | Holiday calendars | `calendar_code`, **`holiday_date`** (not `date`: a reserved word, and CONVENTIONS §1's `<noun>_date` rule), name, is_paid; tenant_id NULL = platform KSA default; **`UNIQUE NULLS NOT DISTINCT (tenant_id, calendar_code, holiday_date)`** so the H8 sweep cannot double-count a day | tenants (nullable) | R/T | PublicHolidayCalendars, PublicHolidays |

Cost centre is a uuid FK from `departments`, `employee_assignments`, `timesheet_entries`, `payroll_inputs` and `gl_journal_lines`. A text code on departments, as revision 1 proposed, cannot carry a project or GL segment and would have to be re-created the first time a customer costs one employee to two centres.

### D. Employees, history, documents and letters (7)
| Table | Purpose | Key columns | FKs | Tier | Replaces |
|---|---|---|---|---|---|
| `employees` | Person and current identity | employee_number (unique per tenant), **`work_email`** (nullable; it is the fourth term of the H1 search index of §19.4, which revision 6 specified without ever defining the column — revision 7 decision 8b), **`status`: 'Draft','Invited','Active','Suspended','Offboarded','Archived' (§9, §10.7 — `Suspended` and `Invited` are live values in `Models/Employee.cs:5-23` and revision 2 lost them)**, names en/ar, gender, dob, `nationality_code`, `national_id` / `iqama_no` / border_no, `joining_date`, **`gosi_first_registered_on`** (cohort driver; NULL blocks the slip), `wps_eligible`, `nitaqat_weight_override` + reason, `eos_service_start_date`, `eos_prior_paid_amount`, separation_date; **lifecycle and PDPL: `deleted_at`, `retention_until`, `privacy_status` ('Normal','PendingErasure','Anonymised','MergedDuplicate'), `redacted_at`** (all four read by `Infrastructure/Retention/Rules/ExpiredEmployeeRecordRule.cs:74`) | tenants | T | Employees, EmployeeDrafts, EmployeeEosbOpeningBalances, NitaqatEmployeeWeightOverrides |
| `employee_assignments` | **Effective-dated** placement | employee_id, effective_from/to, company_id, branch_id, department_id, designation_id, grade_id, manager_employee_id, `cost_center_id`, pay_group, employment_status, change_reason, approval_request_id | employees, companies, branches, departments, cost_centers, designations, grades, approval_requests | C | EmployeeHistories, EmployeeStatusHistories, ReportingLines, EmployeeTransferRequests |
| `employee_salaries` | **Effective-dated** pay | employee_id, effective_from/to, basic, housing, transport, `components jsonb` (amounts only; the currency is the company's, §13.4), housing_in_kind, change_reason, approval_request_id | employees, approval_requests | T | EmployeeSalaryStructures |
| `employee_contracts` | **Effective-dated** contract | employee_id, effective_from/to, contract_type, start/end, probation_end, weekly_hours, notice_days, `qiwa_contract_no` (text; **kept** — see §18: the external-id trio is additive, not a rename), `end_date` (never `end`, a reserved word), document_id | employees, employee_documents | T | EmployeeContracts |
| `employee_bank_accounts` | **Effective-dated** IBAN | employee_id, effective_from/to, iban (checksum-validated), bank_code, account_holder_name, payment_method | employees | T | EmployeePayrollProfiles (bank) |
| `employee_documents` | Every employee file, expiring ID document and issued letter | employee_id, `doc_type` ('Iqama','Passport','Visa','WorkPermit','Contract','Letter','Medical','Other'), document_number, issue/expiry, issuing_country, `file_id`, version, `supersedes_id`, status; letters add template_id, template_version, letter_number, `verification_code`; leave_request_id for sick notes | employees, files, document_templates, leave_requests, self | T | EmployeeDocuments, EmployeeDocumentVersions, Visa/Passport/WorkPermitRecords, EmployeeComplianceRecords, ComplianceRenewals, IssuedLetters, LeaveAttachments |
| `document_templates` | Letter, payslip and contract templates (en/ar) | `kind` ('Letter','Payslip','Contract'), code, `version int` (immutable once used), body_en/ar, merge_fields jsonb, company_id NULL = tenant-wide; **`UNIQUE NULLS NOT DISTINCT (tenant_id, company_id, kind, code, version)`** — without it "version 3 of OFFER_LETTER" is not one row, and the `template_version` snapshots on `payroll_slips` and `employee_documents` would not resolve to one body | tenants, companies | T | HrLetterTemplates, PayslipTemplates, ContractTemplates |

Expiry reminders are a job reading `employee_documents.expiry_date` into `notifications`. No reminder table.

### E. Statutory reference (2)
| Table | Purpose | Key columns | FKs | Tier | Replaces |
|---|---|---|---|---|---|
| `statutory_rules` | **One** effective-dated statutory table: GOSI, EOS, overtime, leave pay, Nitaqat weights, WPS parameters | country_code, `family`, rule_key, dimensions `nationality_class` ('Saudi','GCC','NonSaudi','Any'), **`cohort`** ('Legacy','Entrant2024','Any'), **`gosi_branch` (§9 row 37) and `payer` (§9 row 38) — both enumerated from revision 7; revision 6 left them as closed sets with no domain anywhere, which §7 forbids**; `rate`, `value_json`, `wage_floor`, `wage_cap`, effective_from/to, **`rules_version`** (e.g. `SA-GOSI-2025.07`), source_reference, verified_by/at; UNIQUE (country, family, rule_key, dims, effective_from) + the standard gist no-overlap | none | R | StatutoryRules, GosiContributionRules, CountryPayrollRules, OvertimeMultipliers, NitaqatWeightRules |
| `statutory_rule_bands` | **Band-shaped rules**, which a single rate column cannot express | statutory_rule_id, `band_key` with **`UNIQUE (statutory_rule_id, band_key)`** — a key that repeats inside one rule cannot be resolved by key, `unit` ('ServiceYears','SickDays','ContributoryWage'), `lower_bound numeric`, `upper_bound numeric` NULL = open, inclusive lower / exclusive upper, `rate`, `amount`, `value_json`, band_order; `EXCLUDE USING gist (statutory_rule_id WITH =, numrange(lower_bound, COALESCE(upper_bound,'infinity'), '[)') WITH &&)` | statutory_rules | R | (new; previously implicit in code) |

Bands carry: EOS Art. 84 (half a month per year for years 0–5, a full month thereafter), Art. 85 resignation fractions (none under 2 years, one third for 2–5, two thirds for 5–10, full above 10), Art. 117 sick pay (first 30 days at 100%, next 60 at 75%, next 30 unpaid), and any wage-banded GOSI step. **Nitaqat band thresholds stay in the typed `nitaqat_grid`**, because they are keyed by activity × size tier, which the `statutory_rules` dimension set does not carry; forcing them into `value_json` would lose the FK and the query shape. `nitaqat_grid` applies the same band discipline: `min_saudization_pct` / `max_saudization_pct` with an EXCLUDE on the numrange per activity, size tier and version. This is a deliberate, stated deviation from the review's wording, not an omission.

**KSA GOSI seeding (baseline seeder).** Every value below must be confirmed against the current GOSI circular before go-live; the code already flags this `[COUNSEL]` and raises `WARN_GOSI_ENTRANT_COHORT_NOT_MODELLED`.
- *Legacy cohort* (Saudis first insured before 3 July 2024): Annuities 9% + 9%, SANED 0.75% + 0.75%, Occupational Hazards 2% employer.
- *Entrant2024 cohort* (first insured on or after 3 July 2024): Annuities rises 0.5 pp per payer each July from 2025 towards 11% + 11%. Each step is its own effective-dated row.
- *Non-Saudi*: Occupational Hazards 2% employer only.
- *GCC nationals*: home-country rates under the extension-of-protection scheme; until confirmed, the run blocks rather than guesses.
- *Wage bounds*: contributory floor and cap (currently SAR 1,500 / 45,000), housing-in-kind deemed at 25% of basic.
- *Cohort resolution* uses `employees.gosi_first_registered_on`, cross-checked against `employee_gosi_registrations`. NULL raises a blocking `payroll_issues` row. The code never defaults to a cohort.

There are **no tenant overrides** of statutory rows. Contractual enhancements above the floor live in `companies.settings.pay_policy`, and the engine applies `max(statutory, contractual)`.

### F. Payroll (6)
| Table | Purpose | Key columns | FKs | Tier | Replaces |
|---|---|---|---|---|---|
| `pay_components` | Component catalogue | code, name_en/ar, `kind` ('Earning','Deduction','EmployerContribution','Info'), `gosi_contributory`, `eos_eligible`, `prorate`, `gl_driver`, is_system (seeded BASIC, HOUSING, TRANSPORT, OT, GOSI_ANN_EE/ER, SANED_EE/ER, OH_ER, LOAN, ADVANCE, UNPAID_LEAVE, ABSENCE…) | tenants | T | PayComponents, SalaryComponents, GlDrivers |
| `payroll_runs` | Run header | company_id, year, month, `run_type` ('Regular','OffCycle','FinalSettlement','Correction',**'Opening'**), parent_run_id, `status` (§9), `selection jsonb`, totals as four explicit columns — `total_gross`, `total_deductions`, `total_net`, `total_employer_statutory` (all `numeric(18,2)`) — plus `employee_count int` and `selected_employee_count int` (the run's exit condition, §10.1), `rules_version`, `attendance_locked_range daterange` (+ CHECK that it lies inside the run period), `source_system` and `source_import_job_id` (Opening runs), calculated_at, approved_at, locked_at, void_reason, approval_request_id, `idempotency_key`; UNIQUE (tenant, company, year, month, run_type) WHERE run_type IN ('Regular','Opening') AND status <> 'Voided' | companies, self, background_jobs, approval_requests | C | PayrollRuns, PayrollApprovals, PayrollOpeningBalances (as the Opening run), CompanyCutovers (go-live period lives on companies) |
| `payroll_slips` | **One slip per employee per run**, frozen, with the reconstruction witnesses | run_id, employee_id; identity snapshot: employee_number, name, department, designation, nationality_class, **gosi_cohort**, **employer_gosi_registration_no**, iban, bank_code; period: paid_from/to, paid_days, period_days, proration_denominator_days, proration_basis, proration_factor; **witnesses: `gosi_base_policy`, `full_basic`, `full_housing`, `full_transport`, `contributory_wage`**; totals: gross, deductions, net, employee_statutory_total, employer_statutory_total, loan_deductions, arrears_amount, `is_final_wage_month`; **YTD set: `ytd_gross, ytd_deductions, ytd_net, ytd_employee_statutory, ytd_employer_statutory, ytd_contributory_wage`**; document: payslip_number, `payslip_file_id`, `payslip_sha256`, `template_id`, `template_version`, published_at, language; inclusion_status; UNIQUE (tenant, run_id, employee_id) | payroll_runs, employees, files, document_templates | **T** | PayrollSlips, Payslips, PayrollRunEmployees, PayrollRunEmployeeSelections |
| `payroll_slip_lines` | **One line table.** It is the *only* side of both former cycles: the pointers to it were removed from `payroll_inputs` and `loan_installments` (§8.4) | slip_id, pay_component_code, kind, amount, quantity, rate; **GOSI freeze:** `gosi_branch` (§9 row 37), `gosi_payer` (§9 row 38) — both enumerated, because `trg_gosi_filing_totals` pivots on them and warns rather than blocks, `applied_contributory_wage`, `statutory_rule_id`, `statutory_rule_band_id`, `rules_version`; `source_type`, `payroll_input_id`, `loan_installment_id`; `source_system`, `source_record_id` (Opening runs); `cost_center_id`, gl_driver | payroll_slips, pay_components, statutory_rules, statutory_rule_bands, payroll_inputs, loan_installments, cost_centers | **T** | PayrollEarnings, PayrollDeductions, PayslipComponents, OpeningBalanceOrigins |
| `payroll_inputs` | Pending variable pay, **with the covered period on the row** | employee_id, company_id, `run_year`, `run_month` (when it is paid), **`covered_year`, `covered_month`** (the period it belongs to; equal to run\_\* for ordinary inputs), `kind` ('Adjustment','Arrears','Receivable','Overtime','UnpaidLeave','Absence','LeaveEncashment','Bonus'), pay_component_code, `entitled_amount`, `previously_settled_amount`, `amount` (what this row pays: entitled − previously settled), **`gosi_basis_delta`** (the contributory-wage change this backdated amount causes in the covered period, so the GOSI recalculation is data, not arithmetic in a service), cost_center_id, `source_type`, `source_id`, `revision int NOT NULL DEFAULT 1`, status ('Pending','Claimed','Consumed','Cancelled'), `target_run_type`, claimed_by_run_id, claimed_at, consumed_run_id (**`consumed_line_id` removed: the line points at the input, not both ways**) | employees, companies, pay_components, cost_centers, payroll_runs | C | PayrollAdjustments, PayrollArrearsLines, PayrollEmployeeReceivables, PayrollRunConsumptions, Attendance/Leave/OvertimePayrollImpacts |
| `payroll_issues` | Validation findings **and standing readiness gaps** | `run_id` **nullable** (NULL = a standing gap that blocks any run), employee_id, severity ('Block','Warn'), code (`GOSI_COHORT_UNKNOWN`, `IBAN_MISSING`, `ORG_ESTABLISHMENT_MISSING`, `SALARY_HELD`…), `gap_type`, message, evidence jsonb, detected_at, `resolved_at`, `override_by`, `override_reason`, `override_at` (Warn only; Block is never overridable) | payroll_runs, employees, users | **T** | PayrollValidationResults, PayrollValidationOverrides, EmployeeImportGaps |

**The tier letter on the three slip tables is corrected to T (revision 7 decision 6).** Revisions 3–6 marked `payroll_slips`, `payroll_slip_lines` and `payroll_issues` tier **C**, but gave none of them a `company_id` column and registered no `(tenant_id, company_id)` FK for any of them in §8 — so the letter asserted a column that did not exist. There were two ways to close that, and the register wins over the letter: **they are tenant-tier**, and the legal entity is reached transitively (`payroll_slip_lines` → `payroll_slips` → `payroll_runs.company_id`). Adding `company_id` was rejected for three reasons: it would be a fourth copy of a fact `payroll_runs` already owns, §11's one-writer rule forbids that without a named invariant; `payroll_issues.run_id` is **nullable** by design (a standing readiness gap belongs to a person, not to a run or a company), so the column would be nullable and the FK unenforced exactly where it mattered; and RLS is unaffected either way, because §19.2 shape (a) filters on `tenant_id` alone for tenant and company tier alike. Reports that group payroll by legal entity join through the run, which every payroll query already does. `payroll_inputs` keeps its `company_id` — it is `NOT NULL` there, it is the paying entity before any run exists, and §8 row 71 registers the FK.

**Period naming, one form (revision 7 decision 5).** A period is **always two `smallint` columns**, `year` and `month`, both `NOT NULL`, with `CHECK (month BETWEEN 1 AND 12)`. There is **no bare `period` column anywhere** and no period stored as a `date`. A role prefix is added **only** when one row carries more than one period, or when the period is a schedule point rather than the row's own accounting period: `payroll_inputs.run_year`/`run_month` and `covered_year`/`covered_month`; `loans.start_year`/`start_month`; `loan_installments.due_year`/`due_month`. Everything else — `payroll_runs`, `gosi_filings`, `gl_journals`, `gl_period_closes` — is bare `year` + `month`. *Baseline follow-up:* the DDL as written spells the two GL tables `period_year`/`period_month` and the two loan columns `start_period_*`/`due_period_*`; those four renames are the only SQL change this decision requires, and they must land before `schema.sql` is committed.

**`payroll_inputs` uniqueness and claim semantics — the two things revision 1 got wrong.**
- Uniqueness is `UNIQUE (tenant_id, source_type, source_id, covered_year, covered_month, pay_component_code, revision)`. It deliberately is **not** global on `(source_type, source_id)`: two successive backdated increments for the same covered period, settled in different payroll months, are legal and common, and the live code refuses a global unique for exactly that reason. The covered period plus the revision keeps both rows distinct and both auditable.
- Cancelling an input **bumps `revision`** and inserts the replacement rather than reusing the key, so a cancelled row never burns the identity its replacement needs.
- Claiming is a single statement, never read-then-write: `UPDATE payroll_inputs SET status='Claimed', claimed_by_run_id=$run, claimed_at=now() WHERE tenant_id=$t AND company_id=$c AND status='Pending' AND (covered_year, covered_month) <= ($y,$m) AND (target_run_type IS NULL OR target_run_type=$rt) RETURNING id`. A partial index `(tenant_id, company_id, covered_year, covered_month) WHERE status='Pending'` keeps it cheap. A crashed run releases its claim by `status='Pending'` where `claimed_by_run_id` is a voided run, so a retry is idempotent and two concurrent runs can never both consume an input.

`payroll_audit_logs` (section Q) records every payroll state change.

**Mid-year go-live is the `Opening` run type, with zero new tables.** An import creates one `payroll_runs` row per company with `run_type='Opening'`, `status='Locked'` on acceptance, and one `payroll_slips` row per employee carrying the YTD columns and, through `payroll_slip_lines`, the per-component opening amounts with `source_system` and `source_record_id` provenance. An Opening run is **non-payable and WPS-excluded** (a CHECK forbids `status='Paid'`, and WPS batch creation rejects `run_type='Opening'`), it does not post to GL, and the first real run reads its YTD figures as the opening carry-forward. Loan and leave openings stay where they belong, in `loans.opening_outstanding` and `leave_ledger` 'Opening' rows, and EOS opening stays on `employees`.

### G. WPS (2)
| Table | Purpose | Key columns | FKs | Tier | Replaces |
|---|---|---|---|---|---|
| `wps_batches` | One SIF file as filed, plus resubmissions | run_id, company_id, batch_number (from `number_sequences`), format_version, status ('Generated','Submitted','Accepted','PartiallyRejected','Rejected'), `file_id`, `file_sha256`, employee_count, total_amount, `submission_reference` (**kept**, §18), submitted/acknowledged/rejected_at, `resubmission_of_id`, generated_by | payroll_runs, companies, files, self | C | PayrollPaymentBatches, WPSFileBatches, BankTransferFiles |
| `wps_lines` | **SIF line frozen as filed** | batch_id, slip_id, employee_id; snapshot: id_number, employee_number, iban, bank_code, basic, housing, other_earnings, deductions, net, mol_id; bank result: `bank_status`, bank_reference, confirmed_amount, reason_code, value_date, confirmation_job_id | wps_batches, payroll_slips, employees, background_jobs | C | SIFFileRecords, PayrollPaymentRecords, BankPaymentConfirmations |

### H. GL export (4)
| Table | Purpose | Key columns | FKs | Tier | Replaces |
|---|---|---|---|---|---|
| `gl_mappings` | Driver → debit/credit account codes (the ERP owns the chart) | company_id NULL = default, gl_driver, cost_center_id NULL, debit_account, credit_account | **tenants**, companies, cost_centers | T | GlAccounts, GlAccountMappings |
| `gl_journals` | Journal per source event | company_id, `source_type` ('PayrollRun','FinalSettlement','LoanDisbursement','Reversal'), source_id, `year` + `month` (the period, as two smallints — never a bare `period`), status, `file_id`, export_file_sha256, erp_reference, reversal_of_id, `idempotency_key` UNIQUE (tenant, source_type, source_id, reversal_of_id) | companies, files, self | C | GlJournalExports, FinanceGlEntries (header) |
| `gl_journal_lines` | Balanced lines, **with a cost-centre segment** | journal_id, account, `cost_center_id`, project_code (text until `projects` exists), debit, credit, description; CHECK debit×credit = 0; balance enforced by a deferred trigger | gl_journals, cost_centers | C | GlJournalExportLines, FinanceGlEntries (lines) |
| `gl_period_closes` | Period lock, kept as its own record | company_id, `year` + `month`, status ('Open','Closed','Reopened'), closed_at/by, reopened_at/by, reopen_reason; `UNIQUE (tenant_id, company_id, year, month)` | companies, users | C | GlPeriodCloses |

Period close is a table, not a status on a journal: closing a period is a statement about a period with no journal in it too, and reopening one is an auditable event finance will be asked about.

### I. Loans and advances (2)
| Table | Purpose | Key columns | FKs | Tier | Replaces |
|---|---|---|---|---|---|
| `loans` | Loan **or** salary advance | employee_id, `kind` ('Loan','Advance'), type_code, principal, installment_count, `start_year` + `start_month`, status, outstanding, `opening_outstanding`, approval_request_id | employees, approval_requests | T | EmployeeLoans, SalaryAdvances |
| `loan_installments` | Schedule and recoveries | loan_id, **`installment_number int`** with `UNIQUE (tenant_id, loan_id, installment_number)` (adopted, revision 7 decision 8e: a schedule whose rows have no ordinal cannot be shown, re-generated idempotently, or reconciled against a recovery), `due_year` + `due_month`, amount, `kind` ('Scheduled','EarlySettlement','FinalSettlement','Waiver'), status ('Due','Recovered','Waived','Cancelled'), `recovered_at` (**`recovered_slip_line_id` and `settlement_line_id` removed**: recovery is found from `payroll_slip_lines.loan_installment_id` and `final_settlement_lines.loan_installment_id`, both indexed — §8.4) | loans | T | LoanInstallments, AdvanceInstallments, LoanSettlements |

### J. Leave (3)
| Table | Purpose | Key columns | FKs | Tier | Replaces |
|---|---|---|---|---|---|
| `leave_types` | Type plus policy | code, name_en/ar, is_statutory, `pay_rule_key` (resolves to `statutory_rules` + bands for tiered sick pay), `policy jsonb` (entitlement by service years, accrual, carry-forward, eligibility, gender, once-in-service); seeded KSA set: Annual (21/30), Sick, Maternity, Paternity, Marriage, Bereavement, Hajj, Exam, Iddah, Unpaid, CompOff | tenants | T | LeaveTypes, LeavePolicies, LeavePolicyEligibilities, LeaveAccrualRules |
| `leave_requests` | Request or encashment | employee_id, leave_type_id, `request_kind` ('Leave','Encashment'), start/end, days, `day_breakdown jsonb`, status, approval_request_id | employees, leave_types, approval_requests | T | LeaveRequests, LeaveRequestDates, LeaveEncashmentRequests |
| `leave_ledger` | **Append-only** ledger | employee_id, leave_type_id, entry_date, `entry_type` ('Opening','Accrual','Debit','Reversal','CarryForward','Expiry','Encashment','Adjustment'), days (±), source_type + source_id, reason, created_by, `idempotency_key` UNIQUE | employees, leave_types | T | LeaveBalanceTransactions, EmployeeLeaveBalances (now the view `v_leave_balances`), CompOffCredits, CompOffUsages, OvertimeCompOffConversions |

### K. Attendance, overtime and timesheets (9)

**The unit rule for this domain, stated once (revision 7 decision 1).** Every worked, scheduled, late, early, absent, overtime and booked duration in domain K is stored as **whole minutes**, as an `integer`, and the column is named `<what>_minutes`. **There is no `hours` column anywhere in the baseline.** Revision 6 said `hours` in §2.K, §11.2 and §15 and `minutes` in the same tables' other columns, which made the timesheet reconciliation a unit conversion (`timesheet_hours × 60 − attendance_minutes`) pretending to be a subtraction, and made `total_hours = SUM(entries.hours)` a `numeric` sum of values the attendance side could never equal exactly. In minutes both sides of the reconciliation are the same integer unit, the variance is a plain subtraction, and no rounding rule has to be agreed between the attendance engine and the timesheet engine. Hours are a **presentation** concern: the UI divides by 60 at the edge. **Leave is the one duration that is not minutes** — leave is granted and consumed in days including half days, so `leave_requests.days` and `leave_ledger.days` stay `numeric(9,2)`. `employee_contracts.weekly_hours` also stays `numeric`: it is a contractual term, not a measured duration.

| Table | Purpose | Key columns | FKs | Tier | Replaces |
|---|---|---|---|---|---|
| `shifts` | Shift definition and rules | code, start/end, break, weekly_off_days, `rules jsonb` (grace, late/early, Ramadan hours) | tenants | T | ShiftDefinitions, ShiftPolicies, AttendancePolicies |
| `shift_assignments` | Effective-dated roster | employee_id, shift_id, effective_from/to | employees, shifts | T | ShiftAssignments |
| `attendance_devices` | Registered terminal | branch_id, serial, name, api_key_hash, last_seen_at, **`sync_watermark`** (the highest accepted `occurred_at`, so a resumed sync is bounded and gap-detectable) and **`recent_nonces jsonb`** (the replay window, §18) | branches | C | AttendanceDevices |
| `attendance_punches` | Raw immutable events. **Partitioned monthly by `occurred_at`** (§19.3) | employee_id, occurred_at, `direction` (§9 row 40), `source` ('Device','Mobile','Import','Correction'), device_id, `external_id` (the device's own record id; `UNIQUE (tenant_id, device_id, external_id, occurred_at)`), lat/lng, approval_request_id, **`UNIQUE (tenant_id, idempotency_key, occurred_at)`** (the partition key must be in the unique; it is also the `ON CONFLICT` target for batch ingest) | employees, attendance_devices, approval_requests | T | AttendanceRawEvents |
| `attendance_days` | Computed day. **Partitioned monthly by `work_date`** | employee_id, work_date, shift_id, first_in, last_out, `scheduled_minutes`, `worked_minutes`, `break_minutes`, `late_minutes`, `early_out_minutes`, `overtime_minutes`, `absent_minutes` (all `integer`, all `>= 0`), `computed_at`, status, `exceptions jsonb`, **`locked_run_id`**; UNIQUE (tenant, employee, work_date) | employees, shifts, payroll_runs | T | AttendanceDailyRecords, AttendanceRecords, AbsenceRecords, AttendanceExceptions, AttendanceLockPeriods |
| `overtime_requests` | OT claim with a frozen calculation | employee_id, work_date, **`overtime_minutes int`** (not `hours`), `ot_type` (§9 row 39), `basic_hourly_rate`, `multiplier`, amount, `rules_version`, statutory_rule_id, `payout` ('Pay','CompOff'), status, reason, decided_at, approval_request_id | employees, statutory_rules, approval_requests | T | OvertimeRequests, OvertimeCalculations |
| `timesheets` | Period timesheet per employee | employee_id, company_id, **`timesheet_number`** (from `number_sequences` scope\_key `'timesheet_no'`, which §2.A already lists, with `UNIQUE (tenant_id, timesheet_number)`), period_start/end, status ('Draft','Submitted','Approved','Rejected','Locked'), **`total_minutes int`** (not `total_hours`), submitted_at, decided_at, approval_request_id, `locked_run_id`; `UNIQUE (tenant_id, employee_id, period_start)` | employees, companies, approval_requests, payroll_runs | C | Timesheets |
| `timesheet_entries` | **Relational** line, the project/client dimension. **Partitioned monthly by `work_date`** | timesheet_id, work_date, **`minutes int`** (not `hours`), `cost_center_id`, `project_code` (text until `projects` lands), task, billable, `rate_source`, notes | timesheets, cost_centers | C | TimesheetEntries |
| `timesheet_day_reconciliations` | Timesheet day vs attendance day | timesheet_id, `attendance_day_id` + `work_date` (together the composite FK into the partitioned `attendance_days`, §19.3), **`timesheet_minutes`, `attendance_minutes`, `variance_minutes`** — all three `integer`, so the variance is a subtraction and not a unit conversion (stored because it is the queried exception list; invariant in §11.2), explanation, status ('Open','Explained','Accepted','Rejected'), resolved_by/at; `UNIQUE (tenant_id, timesheet_id, work_date)` | timesheets, attendance_days | C | TimesheetDayReconciliations |

**Attendance locking is kept.** The lock lives on `payroll_runs.attendance_locked_range` (a `daterange` with a CHECK that it falls inside the run period), `attendance_days.locked_run_id` marks each locked day, and a trigger rejects any insert or update of `attendance_punches`, `attendance_days`, `overtime_requests` or `timesheet_entries` whose date falls inside a locked range of a non-voided run. Voiding a run clears the lock in the same transaction. Zero new tables, and the guarantee the old `AttendanceLockPeriods` gave is stronger, because the lock now names the run that owns it.
Timesheet approvals use the one approval engine: `'Timesheet'` is a registered value of `approval_workflows.request_type`.

### L. End of service and final settlement (3)
| Table | Purpose | Key columns | FKs | Tier | Replaces |
|---|---|---|---|---|---|
| `eos_calculations` | EOS computation with a rules snapshot | employee_id, calculation_date, `separation_reason` — the four Labour Law articles, enumerated in §9 row 44 as `Art84`, `Art85`, `Art87`, `Art77` — service_start, service_days, excluded_unpaid_days, last_wage_basis, eligible_wage, amount, prior_paid_deducted, `rules_version`, **`rules_snapshot jsonb`** (the resolved bands, copied), status ('Estimate','Final'), settlement_id | employees, final_settlements | T | EOSBCalculations |
| `final_settlements` | Separation case and settlement header | employee_id, company_id, `separation_type` (§9 row 41), notice_given/served, last_working_day, `clearance jsonb`, status, gross, deductions, net, paid_via_run_id, settlement_number, approval_request_id | employees, companies, payroll_runs, approval_requests | C | EmployeeFinalSettlements, EmployeeOffboardings |
| `final_settlement_lines` | Settlement components | settlement_id, `kind` ('EOS','LeaveEncashment','UnpaidSalary','NoticePay','Art77Compensation','LoanRecovery','OtherDeduction'), amount, `loan_installment_id` (typed FK, replacing the polymorphic pointer for the one case that needed it), source_type + source_id (§16), rules_version | final_settlements, loan_installments | C | FinalSettlementLines |

### M. Nitaqat (2)
| Table | Purpose | Key columns | FKs | Tier | Replaces |
|---|---|---|---|---|---|
| `nitaqat_grid` | Band thresholds, banded like the statutory bands | activity_code, activity_name_en/ar, size_tier with headcount_min/max, band ('Platinum','HighGreen','MidGreen','LowGreen','Red'), `min_saudization_pct`, `max_saudization_pct`, effective_from/to, `grid_version`; EXCLUDE on the pct numrange per activity, size tier and version | none | R | NitaqatActivities, NitaqatSizeTiers, NitaqatBandThresholds |
| `nitaqat_snapshots` | Point-in-time standing per establishment | company_id, as_of_date, activity_code, size_tier, saudi_weighted, total_weighted, achieved_pct, band, `grid_version`, rules_version, **`employee_breakdown jsonb`** (employee_id, weight, reason: the drill-down behind the KPI) | companies | C | NitaqatStandingSnapshots |

### N. GOSI registration and filing (2)
| Table | Purpose | Key columns | FKs | Tier | Replaces |
|---|---|---|---|---|---|
| `employee_gosi_registrations` | **Effective-dated** GOSI registration, at the grain GOSI actually files on | employee_id, company_id, `gosi_registration_no` (the establishment), `gosi_employee_no` (**kept**, §18), effective_from/to, **`registered_contributory_wage`** (what GOSI holds, which is not always what payroll computes), `occupation_code`, `registered_on`, `deregistered_on`, status; standard gist no-overlap | employees, companies | C | EmployeePayrollProfiles (SocialInsuranceReference, MolId) |
| `gosi_filings` | The monthly GOSI return as filed, per establishment | company_id, `gosi_registration_no`, year, month, `rules_version`, status ('Draft','Filed','Reconciled','Disputed'), employee_count, **totals per branch × payer as seven explicit columns**: `annuities_employee`, `annuities_employer`, `saned_employee`, `saned_employer`, `occupational_hazards_employer`, `total_contributory_wage`, `total_amount` (all `numeric(18,2)`); `file_id` + `file_sha256`, filed_at/by, `gosi_invoice_amount`, `variance_amount`, variance_reason, **`revision int NOT NULL DEFAULT 1`** (a corrected return increments it and supersedes its predecessor); UNIQUE (tenant_id, company_id, gosi_registration_no, year, month, revision) | companies, files | C | (new; GOSI reconciliation had no persisted grain) |

Why these two exist: GOSI is invoiced per establishment against the wage GOSI holds on file, not against the wage payroll happens to compute. Without the registered wage and the filed return, a variance against the GOSI invoice cannot be explained, and the reconciliation service has nothing to reconcile against. `payroll_slips.employer_gosi_registration_no` is snapshotted so a slip can always be traced to the return that carried it.

### O. Approvals (4)
| Table | Purpose | Key columns | FKs | Tier | Replaces |
|---|---|---|---|---|---|
| `approval_workflows` | One definition per request type | `request_type` ('Leave','LeaveCancel','Overtime','Loan','Advance','PayrollRun','FinalSettlement','ProfileChange','Transfer','SalaryChange','LetterRequest','AttendanceCorrection','**Timesheet**'), company_id NULL, `steps jsonb` [{order, approver_rule, amount_threshold, sla_hours}], is_active | **tenants**, companies | T | ApprovalWorkflows, ApprovalWorkflowSteps, ApprovalPolicies, ApprovalPolicySteps, ApprovalAuthorities |
| `approval_requests` | One instance | request_type, subject_type + subject_id, requester_user_id, employee_id, `payload jsonb`, workflow_id + `workflow_snapshot jsonb`, current_step, status, due_at, **`current_approver_user_id` and `current_approver_employee_id`** — denormalised from the current step of `workflow_snapshot` so the inbox has something to index (§15, §19.4 H4); maintained by the approval engine on every step advance, return and delegation, in the same `SaveChanges`, and never written elsewhere | approval_workflows, users, employees | T | ApprovalRequests, EmployeeChangeRequests, EmployeeProfileChangeRequests, EmployeeDocumentRequests, and the per-module approval tables |
| `approval_actions` | Decisions and comments | request_id, step, actor_user_id, `on_behalf_of_user_id`, action ('Approve','Reject','Return','Comment','Escalate'), comment | approval_requests, users | T | ApprovalDecisions, AttendanceCorrectionApprovals |
| `approval_delegations` | Time-boxed delegation | delegator_user_id, delegate_user_id, from/to, request_types[] | users | T | ApprovalDelegations, LeaveDelegations |

### P. Notifications (2)
| Table | Purpose | Key columns | FKs | Tier | Replaces |
|---|---|---|---|---|---|
| `notifications` | In-app inbox item | user_id, category, title, body, link, read_at, source_type + source_id, `idempotency_key` UNIQUE | users | T | Notifications, EmployeeNotifications, ComplianceReminders, EmployeeActionItems |
| `notification_deliveries` | Outbound email / SMS / push attempt | notification_id, `channel` (§9 row 42 — the three channels this row's purpose names were never enumerated before revision 7), destination (masked), status, attempts, next_attempt_at, provider_message_id, last_error | notifications | T | NotificationDeliveries |

### Q. Audit (3)
| Table | Purpose | Key columns | FKs | Tier | Replaces |
|---|---|---|---|---|---|
| `audit_logs` | One log for everything except payroll money movement. **Writes are unchained; integrity comes from periodic checkpoints**. **Partitioned monthly by `created_at`** (§19.3) | `record_kind` ('Event','Checkpoint'), tenant_id NULL (platform), company_id, category ('Auth','Admin','Employee','Leave','Attendance','Loan','Document','ESS'), `created_at` (**the monthly partition key**, §19.3), **`chain_key`** (the Merkle chain a row belongs to — one per tenant, `'platform'` for tenant-less rows; adopted in revision 7, because §19.3 already made it half of `UNIQUE (chain_key, seq, created_at)` while §Q's column list never defined it), `seq bigint` from a per-tenant sequence (uniqueness of `seq` is guaranteed at allocation, not by the partitioned unique), action, entity, entity_id, actor_user_id, `correlation_id` (one HTTP request or job run, so a decision, its projection and its notification reassemble), `on_behalf_of_user_id`, `hash_algorithm`, **`before jsonb` and `after jsonb`** (the old/new values the six merged per-module logs carry today — `Models/LoansAdvancesBonuses.cs:127-128`, `Models/Leave.cs:413`, `Models/EmployeeHistory.cs:11-12` — inside the hashed envelope and sharing the purge path with `personal_data`), `envelope_hash` (hash of the immutable envelope, including hashes of the personal payload and of before/after), `personal_data jsonb` (**purgeable**, and it carries `ip` and `user_agent` — both personal data under PDPL, which revision 5 left as standalone columns that erasure would have stranded), `personal_data_erased_at`, plus **`personal_data_hash`, `before_hash`, `after_hash`** so the envelope hash can be **recomputed** after erasure instead of merely compared; checkpoint rows carry `covers_seq_from/to`, **`covers_created_from/to`** (the checkpointer is bounded by time as well as by `seq`: a `seq`-only walk prunes no partitions and scans an ever-growing range), `root_hash` (Merkle root over the envelope hashes in the range), `prev_checkpoint_hash` and **`observed_gaps int8[]`** | none (FK-free by design, so rows outlive purges) | T/P | AuditLogs, AdminAuditLogs, the Attendance/Leave/Loan/Advance/Overtime/Compliance/ESS logs, LoginActivities, EmployeePayslipAccessLogs |
| `payroll_audit_logs` | **Separate, trigger-protected, row-chained** payroll log | tenant_id, run_id, entity, entity_id, action, `before jsonb`, `after jsonb`, metadata jsonb, user_id, `correlation_id`, seq, prev_hash, entry_hash, hash_algorithm; `trg_payroll_audit_logs_append_only` (exists today, carried into the baseline) | none | T | PayrollAuditLogs |
| `retention_purge_audits` | PDPL retention and purge evidence. Its writers are restored in §12 | job_id (plain uuid, **FK-free like the other audit tables** so evidence outlives the job), `rule_key` (FK-free match to `retention_policies.rule_key`), entity, entity_id, `disposition` (the §9 row 36 set), `outcome` (§9 row 43), dry_run, retention_until, `correlation_id`, details jsonb; append-only | none | T | RetentionPurgeAudits |

`envelope_hash = H(immutable columns ‖ personal_data_hash ‖ before_hash ‖ after_hash)`, and every checkpoint root is built from `envelope_hash` values, so erasure changes nothing a verifier needs.

Why the general log is unchained: a per-row `prev_hash` serialises every audited write in a tenant behind one chain head, and this product writes an audit row on every login. A batch checkpointer (every N rows or M seconds) computes the Merkle root over a `seq` range and links checkpoints to each other, which gives tamper evidence at checkpoint granularity without a hot row. PDPL erasure then works: `personal_data` is nulled, `personal_data_erased_at` is set, the `envelope_hash` and therefore every checkpoint still verify, and `retention_purge_audits` records the erasure. The payroll log stays row-chained, because its write rate is low and its evidentiary bar is highest.

### R. Jobs (2)
| Table | Purpose | Key columns | FKs | Tier | Replaces |
|---|---|---|---|---|---|
| `background_jobs` | Async, resumable job (imports, opening-balance import, bank confirmations, device sync, Nitaqat snapshot, accrual/carry-forward/expiry, expiry reminders, retention purge, audit checkpointing) | kind, status (§9), `correlation_id`, `idempotency_key` UNIQUE, payload jsonb, **`progress_current int NOT NULL DEFAULT 0` + `progress_total int NULL`** (adopted, revision 7 decision 8e: revision 6's single `progress` column could not answer "how far through what", which is the whole point of an observable job; `progress_total` is nullable because a streaming import does not know its denominator until it finishes), attempts, `lease_owner`, `heartbeat_at`, `source_file_id`, source_file_sha256, result jsonb | tenants (nullable), files | T/P | BackgroundJobs, WorkerHeartbeats, MigrationImportBatches, AttendanceImportBatches, AttendanceDeviceSyncLogs |
| `background_job_items` | Per-row outcome. **Partitioned monthly by `created_at`** | job_id, `created_at`, row_ref, status (§9), `correlation_id`, error_code, error_message, entity_id | background_jobs | T/P | BackgroundJobItems, AttendanceImportErrors |

### Complete list (76)
1 tenants · 2 tenant_settings · 3 number_sequences · 4 files · 5 retention_policies · 6 platform_users · 7 data_protection_keys · 8 users · 9 roles · 10 permissions · 11 role_permissions · 12 user_roles · 13 permission_grantor_records · 14 auth_sessions · 15 auth_tokens · 16 companies · 17 company_pay_policies · 18 branches · 19 departments · 20 cost_centers · 21 designations · 22 grades · 23 public_holidays · 24 employees · 25 employee_assignments · 26 employee_salaries · 27 employee_contracts · 28 employee_bank_accounts · 29 employee_documents · 30 document_templates · 31 statutory_rules · 32 statutory_rule_bands · 33 pay_components · 34 payroll_runs · 35 payroll_slips · 36 payroll_slip_lines · 37 payroll_inputs · 38 payroll_issues · 39 wps_batches · 40 wps_lines · 41 gl_mappings · 42 gl_journals · 43 gl_journal_lines · 44 gl_period_closes · 45 loans · 46 loan_installments · 47 leave_types · 48 leave_requests · 49 leave_ledger · 50 shifts · 51 shift_assignments · 52 attendance_devices · 53 attendance_punches · 54 attendance_days · 55 overtime_requests · 56 timesheets · 57 timesheet_entries · 58 timesheet_day_reconciliations · 59 eos_calculations · 60 final_settlements · 61 final_settlement_lines · 62 nitaqat_grid · 63 nitaqat_snapshots · 64 employee_gosi_registrations · 65 gosi_filings · 66 approval_workflows · 67 approval_requests · 68 approval_actions · 69 approval_delegations · 70 notifications · 71 notification_deliveries · 72 audit_logs · 73 payroll_audit_logs · 74 retention_purge_audits · 75 background_jobs · 76 background_job_items

Views (not tables): `v_leave_balances` (SUM over `leave_ledger`) and `v_employee_current` (employee joined to the current assignment, salary and bank rows).

### Reference data delivered by the baseline migration or seeders
The `permissions` catalogue. `statutory_rules` and `statutory_rule_bands` (KSA GOSI legacy and Entrant2024, EOS Art. 84/85/87, overtime Art. 107, sick-pay tiers Art. 117, Nitaqat weights, wage floor and cap). `nitaqat_grid`. `public_holidays` (platform KSA calendar). `retention_policies` (the platform retention matrix of §12.4). Per-tenant provisioning seeds `roles` and `role_permissions`, `pay_components`, `gl_mappings` defaults, `number_sequences`, `leave_types`, default `approval_workflows` and `tenant_settings`.

## 3. Mapping of all 323 current tables

Counts: KEPT-AS 47 · MERGED-INTO 123 · REPLACED-BY-JSON-in 37 · DROPPED (module out of scope) 105 · DROPPED (dead) 11 = 323.
Two further rows changed in revision 4: `PlatformUsers` and `PermissionGrantorRecords` are KEPT-AS rather than merged, on the owner's Q2 and the F5 decision. Three rows changed in revision 3: `CompanyRatePolicies` and `CompanyStatutoryOverrides` move out of JSON into the new `company_pay_policies` table, and `EmployeeDependents` is dropped rather than kept as a JSON column (see §4.14).

### 3a. Every DROPPED (dead) row, with evidence

Revision 1 called five rows dead that are live. Every remaining dead row was re-verified by grep over the 765 .cs files outside `Data/ZayraDbContext.cs`, `Models/`, `Domain/` and `Migrations/`, and every one of the eleven also appears in the repo's own orphan register, `Zayra.Api.Tests/Security/OrphanEntityRatchetTests.cs`, which pins 29 entities that "have a table and no code" and may only shrink.

| Current table | Evidence | Why it is not carried forward |
|---|---|---|
| AttendanceDeviceConnectors | 0 refs; orphan register line 71 | Device sync runs through `background_jobs` |
| AttendanceGeofences | 0 refs; register line 76, with the comment at lines 73-75 calling it "a second, richer model of a geofence… competing with Location.GeofenceRadiusMeters, which IS the one written by the Setup screen" | The live geofence moves to `branches` |
| AttendanceLocations | 0 refs; register line 77 | The live entity is `Location` → `branches` |
| AttendanceRules | 0 refs; register line 80, "a generic RuleValueJson rule engine. There is no rule engine" | Shift rules live in `shifts.rules` |
| OvertimeRules | 0 refs; register line 81, same note | Overtime rules are `statutory_rules` plus settings |
| OvertimeAdjustments | 0 refs; register line 82 | An adjustment is a `payroll_inputs` row |
| LeaveModificationRequests | 0 refs; register line 92 | A modification is a new `approval_requests` instance |
| PayrollAllowances | 0 refs; register line 97 | Allowances are `payroll_slip_lines` |
| PayrollCycles | 0 refs; register line 98 | The period is the run's year and month |
| PayrollExceptions | 0 refs; register line 99 | Exceptions are `payroll_issues` |
| ESSDashboardPreferences | 0 refs; register line 117 | ESS layout is client-side |

Five rows revision 1 misclassified, now corrected: `PayrollOpeningBalances` (live: MigrationImportController.cs:444,596,598,626), `OpeningBalanceOrigins` (MigrationImportController.OpeningBalances.cs:269,288; ParallelRunController.cs:244,287), `EmployeeImportGaps` (EmployeesController.cs:1226,3315,3356; EmployeeReadinessQuery.cs:34), `FiscalYears` (Admin/SetupSettingsController.cs:236-263) and `PayrollGroups` (PayrollController.cs:6604,6614). `PermissionGrantorRecords` and `EmployeeActionItems`, also called dead in revision 1, are live in AccessManagementService.cs and EmployeeSelfServiceController.cs:108 and are now merged, not dropped.

### 3b. Full mapping

| # | Current table | Disposition | Target / note |
|---|---|---|---|
| 1 | AbsenceRecords | MERGED-INTO | attendance_days (status='Absent') |
| 2 | AbsenceRegularizationRequests | MERGED-INTO | approval_requests (type='AttendanceCorrection') |
| 3 | AdminAuditLogs | MERGED-INTO | audit_logs (category='Admin') |
| 4 | AdvanceApprovals | MERGED-INTO | approval_requests (type='Advance') |
| 5 | AdvanceAuditLogs | MERGED-INTO | audit_logs |
| 6 | AdvanceInstallments | MERGED-INTO | loan_installments |
| 7 | AdvancePolicies | REPLACED-BY-JSON-in | tenant_settings.loans |
| 8 | AIHRQueryCaches | DROPPED (module out of scope) | AI |
| 9 | AIHRQueryLogs | DROPPED (module out of scope) | AI |
| 10 | AIInsights | DROPPED (module out of scope) | AI |
| 11 | AIModelConfigs | DROPPED (module out of scope) | AI |
| 12 | AIRecommendations | DROPPED (module out of scope) | AI |
| 13 | ApplicationEvents | DROPPED (module out of scope) | recruitment |
| 14 | AppraisalAppeals | DROPPED (module out of scope) | performance |
| 15 | AppraisalCalibrations | DROPPED (module out of scope) | performance |
| 16 | AppraisalCompetencyRatings | DROPPED (module out of scope) | performance |
| 17 | AppraisalReviews | DROPPED (module out of scope) | performance |
| 18 | AppraisalScoreBreakdowns | DROPPED (module out of scope) | performance |
| 19 | ApprovalAuthorities | REPLACED-BY-JSON-in | approval_workflows.steps (amount thresholds) |
| 20 | ApprovalDecisions | MERGED-INTO | approval_actions |
| 21 | ApprovalDelegations | KEPT-AS | approval_delegations |
| 22 | ApprovalPolicies | MERGED-INTO | approval_workflows. Retired by F1: ApprovalPoliciesController answers 410 on every verb |
| 23 | ApprovalPolicySteps | REPLACED-BY-JSON-in | approval_workflows.steps. Orphan today (OrphanEntityRatchetTests.cs:114), part of the frozen ApprovalPolicy model |
| 24 | ApprovalRequests | KEPT-AS | approval_requests |
| 25 | ApprovalWorkflows | KEPT-AS | approval_workflows |
| 26 | ApprovalWorkflowSteps | REPLACED-BY-JSON-in | approval_workflows.steps |
| 27 | AssessmentQuestions | DROPPED (module out of scope) | recruitment |
| 28 | AssessmentTemplates | DROPPED (module out of scope) | recruitment |
| 29 | AttendanceAIInsights | DROPPED (module out of scope) | AI |
| 30 | AttendanceAuditLogs | MERGED-INTO | audit_logs |
| 31 | AttendanceCorrectionApprovals | MERGED-INTO | approval_actions |
| 32 | AttendanceDailyRecords | MERGED-INTO | attendance_days |
| 33 | AttendanceDeviceConnectors | DROPPED (dead) | orphan register OrphanEntityRatchetTests.cs:71; 0 references |
| 34 | AttendanceDevices | KEPT-AS | attendance_devices |
| 35 | AttendanceDeviceSyncLogs | MERGED-INTO | background_jobs (kind='DeviceSync') |
| 36 | AttendanceExceptions | REPLACED-BY-JSON-in | attendance_days.exceptions |
| 37 | AttendanceGeofences | DROPPED (dead) | orphan register OrphanEntityRatchetTests.cs:76 (comment at 73-75): a second, unused geofence model. The live one is Location.GeofenceRadiusMeters -> branches.geofence_radius_m |
| 38 | AttendanceImportBatches | MERGED-INTO | background_jobs (kind='AttendanceImport') |
| 39 | AttendanceImportErrors | MERGED-INTO | background_job_items |
| 40 | AttendanceLocations | DROPPED (dead) | orphan register OrphanEntityRatchetTests.cs:77. The live entity is Location -> branches |
| 41 | AttendanceLockPeriods | MERGED-INTO | attendance_days.locked_run_id (lock follows payroll period) |
| 42 | AttendancePayrollImpacts | MERGED-INTO | payroll_inputs (source_type='Attendance') |
| 43 | AttendancePolicies | REPLACED-BY-JSON-in | shifts.rules |
| 44 | AttendanceRawEvents | MERGED-INTO | attendance_punches |
| 45 | AttendanceRecords | MERGED-INTO | attendance_days |
| 46 | AttendanceRegularizationRequests | MERGED-INTO | approval_requests (type='AttendanceCorrection') -> attendance_punches (source='Correction') |
| 47 | AttendanceRules | DROPPED (dead) | orphan register OrphanEntityRatchetTests.cs:80: a generic rule engine with no engine; 0 references |
| 48 | AuditLogs | KEPT-AS | audit_logs |
| 49 | BackgroundJobItems | KEPT-AS | background_job_items |
| 50 | BackgroundJobs | KEPT-AS | background_jobs |
| 51 | BankPaymentConfirmations | MERGED-INTO | wps_lines (bank_status, bank_reference, confirmed_amount) + background_job_items for the import |
| 52 | BankTransferFiles | MERGED-INTO | wps_batches (file_id, file_sha256). Orphan today (OrphanEntityRatchetTests.cs:96) |
| 53 | BenefitContributions | DROPPED (module out of scope) | benefits |
| 54 | BenefitEligibilityRules | DROPPED (module out of scope) | benefits |
| 55 | BenefitEnrollments | DROPPED (module out of scope) | benefits |
| 56 | BenefitPayrollDeductionLinks | DROPPED (module out of scope) | benefits |
| 57 | BenefitPlans | DROPPED (module out of scope) | benefits |
| 58 | BonusApprovals | DROPPED (module out of scope) | bonus planning |
| 59 | BonusAuditLogs | DROPPED (module out of scope) | bonus planning |
| 60 | BonusBatches | DROPPED (module out of scope) | bonus planning |
| 61 | BonusRecommendations | DROPPED (module out of scope) | bonus planning |
| 62 | BonusTypes | DROPPED (module out of scope) | bonus planning |
| 63 | Branches | KEPT-AS | branches |
| 64 | BurnoutRiskSignals | DROPPED (module out of scope) | AI |
| 65 | CandidateAIScores | DROPPED (module out of scope) | recruitment |
| 66 | CandidateAssessments | DROPPED (module out of scope) | recruitment |
| 67 | CandidateDocuments | DROPPED (module out of scope) | recruitment |
| 68 | Candidates | DROPPED (module out of scope) | recruitment |
| 69 | ClientRateDefinitions | DROPPED (module out of scope) | client billing rates; deferred with the projects / project_rates pair (see section 7) |
| 70 | Companies | KEPT-AS | companies |
| 71 | CompanyComplianceProfiles | REPLACED-BY-JSON-in | companies.settings |
| 72 | CompanyCutovers | MERGED-INTO | companies (go_live_period) |
| 73 | CompanyRatePolicies | MERGED-INTO | company_pay_policies (relational and effective-dated, with the same gist no-overlap as every other dated table) |
| 74 | CompanyStatutoryOverrides | MERGED-INTO | company_pay_policies, above-floor enhancements only. LIVE today: RatesController.cs:185,256,271. The statutory-override surface itself is removed, see open question 6 |
| 75 | CompanyTaxPolicies | DROPPED (module out of scope) | income-tax policy; KSA has no employee income tax |
| 76 | Competencies | DROPPED (module out of scope) | performance |
| 77 | ComplianceAIInsights | DROPPED (module out of scope) | AI |
| 78 | ComplianceAuditLogs | MERGED-INTO | audit_logs |
| 79 | ComplianceReminders | MERGED-INTO | notifications (generated from employee_documents.expiry_date) |
| 80 | ComplianceRenewals | MERGED-INTO | employee_documents (supersedes_id chain) |
| 81 | ComplianceRequirements | REPLACED-BY-JSON-in | tenant_settings.document_requirements |
| 82 | CompOffCredits | MERGED-INTO | leave_ledger (leave_type 'CompOff', entry_type='Credit') |
| 83 | CompOffUsages | MERGED-INTO | leave_ledger (entry_type='Debit') |
| 84 | ContinuousFeedback | DROPPED (module out of scope) | feedback |
| 85 | ContractTemplates | MERGED-INTO | document_templates (kind='Contract') |
| 86 | CostCenters | KEPT-AS | cost_centers |
| 87 | CountryPayrollRules | MERGED-INTO | statutory_rules |
| 88 | DataProtectionKeys | KEPT-AS | data_protection_keys |
| 89 | Departments | KEPT-AS | departments |
| 90 | DepartmentStaffingBudgets | DROPPED (module out of scope) | workforce planning |
| 91 | Designations | KEPT-AS | designations |
| 92 | DocTypes | DROPPED (module out of scope) | AI document RAG |
| 93 | DocumentChunks | DROPPED (module out of scope) | AI document RAG |
| 94 | EmployeeActionItems | MERGED-INTO | notifications + approval_requests (the ESS to-do list derives). LIVE: EmployeeSelfServiceController.cs:108 |
| 95 | EmployeeAIQueryLogs | DROPPED (module out of scope) | AI |
| 96 | EmployeeAnnouncements | DROPPED (module out of scope) | announcements |
| 97 | EmployeeBonuses | DROPPED (module out of scope) | bonus planning (a one-off bonus is still payable as payroll_inputs kind='Adjustment') |
| 98 | EmployeeChangeRequests | MERGED-INTO | approval_requests (type='ProfileChange', payload) |
| 99 | EmployeeChurnPredictions | DROPPED (module out of scope) | AI |
| 100 | EmployeeComplianceRecords | MERGED-INTO | employee_documents |
| 101 | EmployeeContracts | KEPT-AS | employee_contracts |
| 102 | EmployeeDependents | DROPPED (module out of scope) | dependants belong with medical cover, which is out of scope. Orphan today (OrphanEntityRatchetTests.cs:108) with no consumer anywhere in Controllers/Infrastructure/Application, and nothing in the KSA GOSI or payroll path reads them. Revision 2 kept the column speculatively; revision 3 removes it. If counsel says GOSI dependant reporting is required, it returns as a table, not a blob |
| 103 | EmployeeDocumentRequests | MERGED-INTO | approval_requests (type='LetterRequest') |
| 104 | EmployeeDocuments | KEPT-AS | employee_documents |
| 105 | EmployeeDocumentVersions | MERGED-INTO | employee_documents (supersedes_id, version) |
| 106 | EmployeeDrafts | MERGED-INTO | employees (status='Draft') |
| 107 | EmployeeEosbOpeningBalances | MERGED-INTO | employees (eos_service_start_date, eos_prior_paid_amount) |
| 108 | EmployeeFinalSettlements | KEPT-AS | final_settlements |
| 109 | EmployeeGoals | DROPPED (module out of scope) | performance |
| 110 | EmployeeHistories | MERGED-INTO | employee_assignments |
| 111 | EmployeeIdRules | REPLACED-BY-JSON-in | tenant_settings.numbering |
| 112 | EmployeeImportGaps | MERGED-INTO | payroll_issues (standing readiness gap, run_id NULL, gap_type). LIVE: EmployeesController.cs:1226,3315,3356; EmployeeReadinessQuery.cs:34 |
| 113 | EmployeeLeaveBalances | MERGED-INTO | leave_ledger (balance = SUM; view v_leave_balances) |
| 114 | EmployeeLoans | MERGED-INTO | loans (kind='Loan') |
| 115 | EmployeeMobileDevices | MERGED-INTO | auth_sessions (device_id, push_token, platform) |
| 116 | EmployeeNotificationCategoryPreferences | REPLACED-BY-JSON-in | users.notification_prefs |
| 117 | EmployeeNotificationPreferences | REPLACED-BY-JSON-in | users.notification_prefs |
| 118 | EmployeeNotifications | MERGED-INTO | notifications |
| 119 | EmployeeOffboardings | MERGED-INTO | final_settlements (separation case: reason, notice, last_working_day, clearance) |
| 120 | EmployeePayrollProfiles | MERGED-INTO | employee_bank_accounts (IBAN, bank, routing) + employee_gosi_registrations (GOSI/MOL ids) + employees (wps_eligible) |
| 121 | EmployeePayslipAccessLogs | MERGED-INTO | audit_logs (action='payslip.viewed') |
| 122 | EmployeePolicyAcknowledgements | DROPPED (module out of scope) | policy acknowledgement (1 ref) |
| 123 | EmployeeProfileChangeRequests | MERGED-INTO | approval_requests (type='ProfileChange', payload) |
| 124 | EmployeeRiskScores | DROPPED (module out of scope) | AI |
| 125 | Employees | KEPT-AS | employees |
| 126 | EmployeeSalaryStructures | MERGED-INTO | employee_salaries |
| 127 | EmployeeSelfServiceAuditLogs | MERGED-INTO | audit_logs |
| 128 | EmployeeSentimentPulses | DROPPED (module out of scope) | AI |
| 129 | EmployeeStatusHistories | MERGED-INTO | employee_assignments (employment_status) |
| 130 | EmployeeTransferRequests | MERGED-INTO | approval_requests (type='Transfer') -> employee_assignments |
| 131 | EmployeeUserAccounts | MERGED-INTO | users.employee_id (+ invitation in auth_tokens purpose='Invitation') |
| 132 | EnterpriseIdentityProvisioningEvents | DROPPED (module out of scope) | SCIM provisioning, see open question 4 |
| 133 | EOSBCalculations | KEPT-AS | eos_calculations |
| 134 | ESSDashboardPreferences | DROPPED (dead) | orphan register OrphanEntityRatchetTests.cs:117; 0 references outside Models/Data/Migrations |
| 135 | Feedback360 | DROPPED (module out of scope) | 360 feedback |
| 136 | FinalSettlementLines | KEPT-AS | final_settlement_lines |
| 137 | FinanceGlEntries | MERGED-INTO | gl_journals / gl_journal_lines |
| 138 | FiscalYears | REPLACED-BY-JSON-in | tenant_settings.payroll.fiscal_years. LIVE CRUD: Admin/SetupSettingsController.cs:236-263 |
| 139 | GCCComplianceSettings | REPLACED-BY-JSON-in | companies.settings (WPS agent/MOL code) + tenant_settings.payroll |
| 140 | GlAccountMappings | MERGED-INTO | gl_mappings |
| 141 | GlAccounts | MERGED-INTO | gl_mappings (account codes; the ERP owns the chart) |
| 142 | GlDrivers | MERGED-INTO | pay_components.gl_driver |
| 143 | GlJournalExportLines | MERGED-INTO | gl_journal_lines |
| 144 | GlJournalExports | MERGED-INTO | gl_journals |
| 145 | GlPeriodCloses | KEPT-AS | gl_period_closes |
| 146 | GoalProgressUpdates | DROPPED (module out of scope) | performance |
| 147 | GosiContributionRules | MERGED-INTO | statutory_rules + statutory_rule_bands (family='GOSI') |
| 148 | GradePayScaleComponents | REPLACED-BY-JSON-in | grades.pay_scale |
| 149 | Grades | KEPT-AS | grades |
| 150 | HrLetterTemplates | MERGED-INTO | document_templates (kind='Letter') |
| 151 | HRRequestAttachments | DROPPED (module out of scope) | HR ticket centre |
| 152 | HRRequestCategories | DROPPED (module out of scope) | HR ticket centre |
| 153 | HRRequestComments | DROPPED (module out of scope) | HR ticket centre |
| 154 | HRRequests | DROPPED (module out of scope) | HR ticket centre; letter requests survive as approval_requests |
| 155 | HRRequestSLAs | DROPPED (module out of scope) | HR ticket centre |
| 156 | IncrementRecommendations | DROPPED (module out of scope) | increment planning |
| 157 | InterviewFeedbacks | DROPPED (module out of scope) | recruitment |
| 158 | InterviewSchedules | DROPPED (module out of scope) | recruitment |
| 159 | IssuedLetters | MERGED-INTO | employee_documents (doc_type='Letter', template_id, letter_number, verification_code) |
| 160 | JobApplications | DROPPED (module out of scope) | recruitment |
| 161 | JobOpenings | DROPPED (module out of scope) | recruitment |
| 162 | LeaveAccrualRules | REPLACED-BY-JSON-in | leave_types.policy.accrual. Orphan today (OrphanEntityRatchetTests.cs:90) and flagged in the register as the one orphan with a future: the accrual, carry-forward and expiry jobs do not exist and must be built on background_jobs |
| 163 | LeaveAIInsights | DROPPED (module out of scope) | AI |
| 164 | LeaveApprovals | MERGED-INTO | approval_requests (type='Leave') |
| 165 | LeaveAttachments | MERGED-INTO | employee_documents (leave_request_id). Orphan today (OrphanEntityRatchetTests.cs:91); carried forward because sick-note evidence is required |
| 166 | LeaveAuditLogs | MERGED-INTO | audit_logs |
| 167 | LeaveBalanceTransactions | MERGED-INTO | leave_ledger |
| 168 | LeaveBlackoutDates | REPLACED-BY-JSON-in | tenant_settings.leave.blackouts |
| 169 | LeaveCancellationRequests | MERGED-INTO | approval_requests (type='LeaveCancel') |
| 170 | LeaveDelegations | MERGED-INTO | approval_delegations |
| 171 | LeaveEncashmentRequests | MERGED-INTO | leave_requests (request_kind='Encashment') |
| 172 | LeaveModificationRequests | DROPPED (dead) | orphan register OrphanEntityRatchetTests.cs:92; 0 references |
| 173 | LeavePayrollImpacts | MERGED-INTO | payroll_inputs (source_type='Leave') |
| 174 | LeavePolicies | REPLACED-BY-JSON-in | leave_types.policy |
| 175 | LeavePolicyEligibilities | REPLACED-BY-JSON-in | leave_types.policy.eligibility |
| 176 | LeaveRequestDates | REPLACED-BY-JSON-in | leave_requests.day_breakdown. Orphan today (OrphanEntityRatchetTests.cs:93) |
| 177 | LeaveRequests | KEPT-AS | leave_requests |
| 178 | LeaveTypes | KEPT-AS | leave_types |
| 179 | LoanApprovals | MERGED-INTO | approval_requests (type='Loan') |
| 180 | LoanAuditLogs | MERGED-INTO | audit_logs |
| 181 | LoanInstallments | MERGED-INTO | loan_installments |
| 182 | LoanPolicies | REPLACED-BY-JSON-in | tenant_settings.loans |
| 183 | LoanSettlements | MERGED-INTO | loan_installments (kind='EarlySettlement'/'FinalSettlement') |
| 184 | LoanTypes | REPLACED-BY-JSON-in | tenant_settings.loans |
| 185 | Locations | MERGED-INTO | branches (address, geofence_radius_m: this is the LIVE geofence, written by the Setup screen) |
| 186 | LoginActivities | MERGED-INTO | audit_logs (category='Auth') |
| 187 | ManpowerRequisitions | DROPPED (module out of scope) | recruitment |
| 188 | MasterDataTypes | REPLACED-BY-JSON-in | tenant_settings.lookups |
| 189 | MasterDataValues | REPLACED-BY-JSON-in | tenant_settings.lookups |
| 190 | MfaChallengeTokens | MERGED-INTO | auth_tokens (purpose='MfaChallenge') |
| 191 | MigrationImportBatches | MERGED-INTO | background_jobs (kind='Import') |
| 192 | NitaqatActivities | MERGED-INTO | nitaqat_grid |
| 193 | NitaqatBandThresholds | MERGED-INTO | nitaqat_grid |
| 194 | NitaqatEmployeeWeightOverrides | MERGED-INTO | employees (nitaqat_weight_override, nitaqat_weight_reason) |
| 195 | NitaqatEstablishmentProfiles | MERGED-INTO | companies (mol_establishment_no, nitaqat_activity_code) |
| 196 | NitaqatSizeTiers | MERGED-INTO | nitaqat_grid (size_tier with headcount range) |
| 197 | NitaqatStandingSnapshots | KEPT-AS | nitaqat_snapshots |
| 198 | NitaqatWeightRules | MERGED-INTO | statutory_rules (family='Nitaqat') |
| 199 | NotificationDeliveries | KEPT-AS | notification_deliveries |
| 200 | Notifications | KEPT-AS | notifications |
| 201 | NotificationTemplates | REPLACED-BY-JSON-in | tenant_settings.notification_templates (defaults in code) |
| 202 | NumberingRules | REPLACED-BY-JSON-in | tenant_settings.numbering |
| 203 | OfferApprovals | DROPPED (module out of scope) | recruitment |
| 204 | OfferLetters | DROPPED (module out of scope) | recruitment |
| 205 | OnboardingChecklists | DROPPED (module out of scope) | onboarding |
| 206 | OnboardingChecklistTemplateTasks | DROPPED (module out of scope) | onboarding |
| 207 | OnboardingTasks | DROPPED (module out of scope) | onboarding |
| 208 | OpeningBalanceOrigins | MERGED-INTO | payroll_slip_lines.source_system / source_record_id on the Opening run. LIVE: MigrationImportController.OpeningBalances.cs:269,288; ParallelRunController.cs:244,287 |
| 209 | OvertimeAdjustments | DROPPED (dead) | orphan register OrphanEntityRatchetTests.cs:82; 0 references |
| 210 | OvertimeApprovals | MERGED-INTO | approval_requests (type='Overtime') |
| 211 | OvertimeAuditLogs | MERGED-INTO | audit_logs |
| 212 | OvertimeBudgets | DROPPED (module out of scope) | overtime budgeting (workforce planning) |
| 213 | OvertimeCalculations | MERGED-INTO | overtime_requests (frozen calc columns + rules_version) |
| 214 | OvertimeCompOffConversions | MERGED-INTO | leave_ledger (source_type='Overtime') |
| 215 | OvertimeMultipliers | MERGED-INTO | statutory_rules (family='Overtime') + tenant_settings.overtime |
| 216 | OvertimePayrollImpacts | MERGED-INTO | payroll_inputs (source_type='Overtime') |
| 217 | OvertimePolicies | REPLACED-BY-JSON-in | tenant_settings.overtime |
| 218 | OvertimeRequests | KEPT-AS | overtime_requests |
| 219 | OvertimeRules | DROPPED (dead) | orphan register OrphanEntityRatchetTests.cs:81; 0 references |
| 220 | OvertimeTypes | REPLACED-BY-JSON-in | tenant_settings.overtime.types |
| 221 | PassportRecords | MERGED-INTO | employee_documents (doc_type='Passport') |
| 222 | PasswordResetTokens | MERGED-INTO | auth_tokens (purpose='PasswordReset') |
| 223 | PayComponents | KEPT-AS | pay_components |
| 224 | PayrollAdjustments | MERGED-INTO | payroll_inputs (kind='Adjustment') |
| 225 | PayrollAIValidationResults | DROPPED (module out of scope) | AI |
| 226 | PayrollAllowances | DROPPED (dead) | orphan register OrphanEntityRatchetTests.cs:97; 0 references |
| 227 | PayrollApprovals | MERGED-INTO | approval_requests (type='PayrollRun') |
| 228 | PayrollArrearsLines | MERGED-INTO | payroll_inputs (kind='Arrears') |
| 229 | PayrollAuditLogs | KEPT-AS | payroll_audit_logs |
| 230 | PayrollCycles | DROPPED (dead) | orphan register OrphanEntityRatchetTests.cs:98; 0 references |
| 231 | PayrollDeductions | MERGED-INTO | payroll_slip_lines (kind='Deduction'/'EmployerContribution') |
| 232 | PayrollEarnings | MERGED-INTO | payroll_slip_lines (kind='Earning') |
| 233 | PayrollEmployeeReceivables | MERGED-INTO | payroll_inputs (kind='Receivable') |
| 234 | PayrollExceptions | DROPPED (dead) | orphan register OrphanEntityRatchetTests.cs:99; 0 references |
| 235 | PayrollGroups | REPLACED-BY-JSON-in | tenant_settings.payroll.pay_groups + employee_assignments.pay_group. LIVE: PayrollController.cs:6604,6614 |
| 236 | PayrollOpeningBalances | MERGED-INTO | payroll_slips + payroll_slip_lines of a run_type='Opening' run. LIVE: MigrationImportController.cs:444,596,598,626; OpeningBalanceCutoverTests.cs:245,489 |
| 237 | PayrollPaymentBatches | MERGED-INTO | wps_batches |
| 238 | PayrollPaymentRecords | MERGED-INTO | wps_lines |
| 239 | PayrollRunConsumptions | MERGED-INTO | payroll_inputs (consumed_run_id, consumed_line_id) |
| 240 | PayrollRunEmployees | MERGED-INTO | payroll_slips |
| 241 | PayrollRunEmployeeSelections | MERGED-INTO | payroll_slips (inclusion_status) + payroll_runs.selection |
| 242 | PayrollRuns | KEPT-AS | payroll_runs |
| 243 | PayrollSlips | KEPT-AS | payroll_slips |
| 244 | PayrollValidationOverrides | MERGED-INTO | payroll_issues (override_by, override_reason) |
| 245 | PayrollValidationResults | MERGED-INTO | payroll_issues |
| 246 | PayslipComponents | MERGED-INTO | payroll_slip_lines |
| 247 | Payslips | MERGED-INTO | payroll_slips (payslip_number, published_at, language) |
| 248 | PayslipTemplates | MERGED-INTO | document_templates (kind='Payslip') |
| 249 | PerformanceAuditLogs | DROPPED (module out of scope) | performance |
| 250 | PerformanceCycleEmployees | DROPPED (module out of scope) | performance |
| 251 | PerformanceCycles | DROPPED (module out of scope) | performance |
| 252 | PerformanceImprovementPlans | DROPPED (module out of scope) | performance |
| 253 | PerformanceRatingOptions | DROPPED (module out of scope) | performance |
| 254 | PerformanceRatingScales | DROPPED (module out of scope) | performance |
| 255 | PerformanceScorecardTemplates | DROPPED (module out of scope) | performance |
| 256 | PermissionGrantorRecords | KEPT-AS | permission_grantor_records (decision F5: delegated granting authority is a distinct fact — scope, sub-delegation, expiry, reason — that `user_roles.granted_by` cannot express). LIVE: AccessController.cs:464,472,484; AccessManagementService.cs:418,1337,1373,1392 |
| 257 | Permissions | KEPT-AS | permissions |
| 258 | PIPCheckIns | DROPPED (module out of scope) | performance |
| 259 | PlatformAnnouncements | DROPPED (module out of scope) | platform CRM/comms |
| 260 | PlatformComplianceControls | DROPPED (module out of scope) | platform compliance register |
| 261 | PlatformConfigEntries | DROPPED (module out of scope) | platform pricing config. LIVE but out of scope: PlatformController.cs:2364,2388,3441 |
| 262 | PlatformLeads | DROPPED (module out of scope) | platform CRM |
| 263 | PlatformSecurityIncidents | DROPPED (module out of scope) | platform incident register |
| 264 | PlatformSupportSessions | DROPPED (module out of scope) | support impersonation; see risk 5 |
| 265 | PlatformUsers | KEPT-AS | platform_users (owner decision Q2: platform operators keep their own table so no nullable-tenant row sits in a client-data table) |
| 266 | PolicyDocuments | DROPPED (module out of scope) | policy library (1 ref) |
| 267 | Positions | DROPPED (module out of scope) | position/seat management (workforce planning) |
| 268 | PricingConfigs | DROPPED (module out of scope) | pricing |
| 269 | PricingModuleConfigs | DROPPED (module out of scope) | pricing |
| 270 | PricingQuotes | DROPPED (module out of scope) | pricing |
| 271 | ProbationReviews | DROPPED (module out of scope) | performance (probation review; probation end date stays on employee_contracts) |
| 272 | PromotionRecommendations | DROPPED (module out of scope) | promotion planning |
| 273 | PublicHolidayCalendars | MERGED-INTO | public_holidays (calendar_code) |
| 274 | PublicHolidays | KEPT-AS | public_holidays |
| 275 | QiwaApiCredentials | DROPPED (module out of scope) | Qiwa sync |
| 276 | QiwaSyncLogs | DROPPED (module out of scope) | Qiwa sync |
| 277 | QiwaTenantConnections | DROPPED (module out of scope) | Qiwa sync |
| 278 | RecruitmentAuditLogs | DROPPED (module out of scope) | recruitment |
| 279 | RefreshTokens | MERGED-INTO | auth_sessions |
| 280 | ReportExecutionLogs | DROPPED (module out of scope) | report execution log |
| 281 | ReportingLines | MERGED-INTO | employee_assignments.manager_employee_id |
| 282 | ReportSchedules | DROPPED (module out of scope) | scheduled report e-mails |
| 283 | ResumeParseResults | DROPPED (module out of scope) | recruitment |
| 284 | RetentionPurgeAudits | KEPT-AS | retention_purge_audits |
| 285 | RoleCompetencies | DROPPED (module out of scope) | performance |
| 286 | RolePermissions | KEPT-AS | role_permissions |
| 287 | Roles | KEPT-AS | roles |
| 288 | SalaryAdvances | MERGED-INTO | loans (kind='Advance') |
| 289 | SalaryComponents | MERGED-INTO | pay_components |
| 290 | SalaryStructures | REPLACED-BY-JSON-in | grades.pay_scale (template) / employee_salaries.components |
| 291 | SavedReports | DROPPED (module out of scope) | saved report builder |
| 292 | SecuritySettings | REPLACED-BY-JSON-in | tenant_settings.security |
| 293 | ShiftAssignments | KEPT-AS | shift_assignments |
| 294 | ShiftDefinitions | KEPT-AS | shifts |
| 295 | ShiftPolicies | REPLACED-BY-JSON-in | shifts.rules |
| 296 | SIFFileRecords | MERGED-INTO | wps_lines |
| 297 | StaffingLevels | DROPPED (module out of scope) | workforce planning |
| 298 | StatutoryRules | KEPT-AS | statutory_rules |
| 299 | SystemSettings | REPLACED-BY-JSON-in | tenant_settings.general |
| 300 | TenantAiUsages | DROPPED (module out of scope) | AI |
| 301 | TenantBrandings | REPLACED-BY-JSON-in | tenant_settings.branding |
| 302 | TenantFeatureFlags | REPLACED-BY-JSON-in | tenants.enabled_modules |
| 303 | TenantFieldHelpTexts | REPLACED-BY-JSON-in | tenant_settings.help_texts |
| 304 | TenantHrConfigs | REPLACED-BY-JSON-in | tenant_settings.hr |
| 305 | TenantIdentityProviderSettings | DROPPED (module out of scope) | enterprise SSO/SCIM, see open question 4 |
| 306 | TenantInvoiceLines | DROPPED (module out of scope) | billing |
| 307 | TenantInvoices | DROPPED (module out of scope) | billing |
| 308 | TenantLocalizationSettings | REPLACED-BY-JSON-in | tenant_settings.localization |
| 309 | TenantPayments | DROPPED (module out of scope) | billing |
| 310 | Tenants | KEPT-AS | tenants |
| 311 | TenantSubscriptions | MERGED-INTO | tenants (plan_code, plan_limits, plan_expires_at: plan gating only) |
| 312 | TimesheetDayReconciliations | KEPT-AS | timesheet_day_reconciliations |
| 313 | TimesheetEntries | KEPT-AS | timesheet_entries (relational, project_code + cost_center_id) |
| 314 | Timesheets | KEPT-AS | timesheets (owner decision: timesheets stay) |
| 315 | UserEntityAccesses | MERGED-INTO | user_roles (scope_company_id / scope_branch_id / scope_department_id) |
| 316 | UserPermissionOverrides | MERGED-INTO | roles / user_roles (a per-user grant becomes a scoped, expiring role). LIVE: AccessManagementService.cs:415,477,848; AuthSeeder.cs:499. See open question 5 |
| 317 | UserRoles | KEPT-AS | user_roles |
| 318 | Users | KEPT-AS | users |
| 319 | VisaRecords | MERGED-INTO | employee_documents (doc_type='Visa') |
| 320 | WorkerHeartbeats | MERGED-INTO | background_jobs (lease_owner, heartbeat_at) |
| 321 | WorkforcePlans | DROPPED (module out of scope) | workforce planning |
| 322 | WorkPermitRecords | MERGED-INTO | employee_documents (doc_type='WorkPermit') |
| 323 | WPSFileBatches | MERGED-INTO | wps_batches |

## 4. Features removed by this scope (for the owner to check)

Each item names the screen (frontend route) and the API that disappear.

1. **Performance and appraisals**: `/performance` (cycles, reviews, calibration, goals, PIPs, probation reviews, competencies, scorecards, 360 and continuous feedback). API `Controllers/Performance/*`. The probation *end date* stays on the contract.
2. **Recruitment / ATS and onboarding**: `/recruitment` (requisitions, openings, candidates, interviews, assessments, offers, onboarding checklists, workforce planning). API `Controllers/Recruitment/*`, `/api/jobs`, `/api/planning`. New hires are entered directly in `/people`.
3. **Benefits**: `/benefits`, the ESS `/ess/benefits` ("My Benefits"), `/api/compensation/benefits`, `EssBenefitsController`. Note: `MyBenefitsPage.tsx` has uncommitted edits on the current branch.
4. **Bonus, increment and promotion planning**: bonus batches, types and recommendations (`Controllers/Finance/BonusesController`). A one-off bonus is still payable as a `payroll_inputs` row. Salary increases and promotions become an approved `SalaryChange` / `Transfer` request, with no planning screen.
5. **AI features**: `/ai-assistant` and `/api/ai`, the leave, attendance and compliance "AI insights", payroll AI validation, churn/burnout/sentiment scores, and the document RAG index.
6. **Qiwa integration**: connection, credentials and sync (`/api/qiwa`, the Qiwa panel in Saudi compliance). The Qiwa contract number stays as a text field.
7. **Platform CRM, pricing and billing**: pricing quotes and module pricing (`/api/pricing`, `PlatformConfigEntries`), tenant invoices and payments, platform leads, announcements, the security-incident and compliance-control registers, and support-session impersonation. Tenant admin keeps: create or suspend tenants, set plan and limits, manage users.
8. **Statutory override surface (Surface B)**: `RatesController`'s `CompanyStatutoryOverride` CRUD. Statutory values become platform reference data. Above-floor contractual enhancements stay, in company pay policy (decision 6).
9. **Income-tax policies**: `/tax-policies` (KSA has no employee income tax). Compliance *profiles* (`/compliance-profiles`) fold into company settings, so that screen becomes a company-settings tab.
10. **HR request ticket centre**: `/hr-requests` (categories, SLAs, comments, attachments). Letter requests survive as an approval request that issues a letter from `/hr-letters`.
11. **Reports builder**: saved reports, scheduled report e-mails and execution logs. The fixed statutory and operational reports in `/reports` remain, computed live.
12. **Per-user permission overrides**: `UserPermissionOverrides` becomes a scoped, expiring role grant on `user_roles` (decision 5). An admin can still say "this person may approve leave for Riyadh until 30 June", but not "this person has exactly this one extra permission key". **Delegated granting authority is *not* removed**: `permission_grantor_records` and its three endpoints survive intact (decision F5).
13. **Smaller removals**: the policy document library and acknowledgements, employee announcements, positions/seat management and staffing budgets (the org chart stays, from manager lines), overtime budgets, enterprise SSO/SCIM provisioning (decision 4), and client billing rates (`/api/finance/rates` client surface, `ClientRateDefinitions`) until `projects` and `project_rates` land.

14. **Dependant records**: `EmployeeDependents` is dropped rather than carried as a JSON column. It has no consumer anywhere in `Controllers/`, `Infrastructure/` or `Application/` (it is in the orphan register at `OrphanEntityRatchetTests.cs:108`), medical cover is out of scope, and nothing in the KSA GOSI path reads dependants. If counsel says GOSI dependant reporting is required, it returns as a table and takes a slot in the budget.
15. **Per-row currency**: every money column is now in the company's currency of record (§13.2). A tenant running payroll in two currencies is out of scope for this baseline.

**Kept, contrary to revision 1**: mid-year go-live and opening balances (`/opening-balances`, now the `Opening` run), timesheets (`/timesheets`, `/api/timesheets`, `/api/ess/timesheets`), cost centres as a first-class dimension, GL period close, and attendance locking.
Screens kept: dashboard, `/group`, `/people`, `/org-chart`, `/payroll` and templates, `/gosi-filing`, `/loans`, `/leave`, `/attendance`, `/shifts`, `/overtime`, `/timesheets`, `/offboarding`, `/approvals`, `/hr-letters`, `/compliance`, `/saudi-compliance`, `/opening-balances`, `/ess`, `/user-management`, `/tenant-admin`, `/setup`, `/reports` (fixed reports), and public letter verification.
`/payroll/variance` (parallel run) needs no schema of its own: decision 7 supports go-live through the `Opening` run, and a variance screen is a report comparing an `Opening` run with the first `Regular` run, or two runs of any type. It can be built whenever the owner wants it, without a migration.

## 5. Decisions taken (settled)

These were open questions in revisions 1-3. The owner has answered them; they are recorded here as decisions and are applied throughout the document.

1. **Employee key: `uuid`.** One key form, `employees.id uuid`, generated as UUIDv7. The `int Id` / `Guid PublicId` split ends, and roughly 105 `int EmployeeId` properties change during the port. People see `employee_number`.
2. **Platform operators keep their own table.** `platform_users` (§A) rather than a `user_kind` column on `users`, so no nullable-tenant row sits in a client-data table. `users.tenant_id` is therefore NOT NULL. `auth_sessions` and `auth_tokens` serve both subject kinds through an XOR CHECK, because duplicating rotation, reuse-detection and lockout logic would breach the no-duplicate-tables principle; they are the only two tables where `tenant_id` is nullable, and they are authentication infrastructure, not client data.
3. **An unknown GOSI cohort blocks payroll.** `employees.gosi_first_registered_on` NULL, or a GCC national whose home-scheme rates are not seeded, raises a `payroll_issues` **Block** (never overridable, §F). Payroll refuses the employee rather than guessing a cohort; the run proceeds for everyone else.
4. **Enterprise SSO and SCIM are dropped for now.** No `sso_connections` table and no provisioning-event table. It returns as a paid item when an enterprise client asks, and at that point it is a table plus a settings section, not a redesign.
5. **Per-user permission overrides are replaced by custom roles.** A per-user grant becomes a role with a data scope and an expiry on `user_roles`. What does *not* fold away is delegated granting authority, which keeps its own table (below).
6. **Tenants cannot override the law.** `statutory_rules` and `statutory_rule_bands` are platform reference data with no tenant rows. A tenant may only *exceed* a statutory minimum, through `company_pay_policies` and `leave_types.policy`, and the engine applies `max(statutory, contractual)`. The `RatesController` statutory-override surface goes. This also settles the revision-3 dispute about `leave_types.policy`: the tenant's contractual ladder stays tenant data, the floor stays reference data.
7. **Mid-year go-live is supported**, through the `Opening` run type (§F): a frozen, non-payable, WPS-excluded run whose slips carry the YTD figures and whose lines carry per-component provenance. Zero extra tables.
8. **Timesheets are in scope**, with `cost_center_id` as a real FK and `project_code` as text until `projects` exists.

**Decision F5 (from the second adversarial review, taken by the CTO on the owner's authorization-depth standard): delegated granting authority keeps its own table.** `permission_grantor_records` — grantor, scope, `can_sub_delegate`, expiry, reason — is live at `Controllers/AccessController.cs:464,472,484` and in User Management, and is read before any grant is allowed (`AccessManagementService.cs:418,1337,1373,1392`). `user_roles.granted_by` records who made one grant; it cannot express who *may* grant, over what scope, with what sub-delegation right, until when. Revisions 2-3 described this as a merge. It is not a merge, and the document no longer says it is.

**Decision 9 (owner, on the platform audit): the hard table cap is dropped.** The rule is the principle — no duplicate tables, nothing kept that nothing uses, every table traceable to a capability. 76 stands, no cut from §7 is taken, and §7 is retained as a record of what those cuts would have cost. `projects` and `project_rates` (→ 78) remain a stated future claim.
**Decision 11 (data architect, revision 7): object naming is settled and no longer open.** `ck_<table>__<assertion>` for checks, with `pk_` / `fk_<table>__<column>` / `uq_` / `ex_` / `ix_` / `trg_` alongside it, and §9's 36 `chk_<table>_<column>` names kept as a **closed legacy set** because C# constants classes and CI assert against those exact strings. §9.0 states it in full. Standard column order (CONVENTIONS §10) is settled with it. This was CONVENTIONS §16 decision 4, and it could not stay open past the first `CREATE TABLE`.

**Decision 10 (owner): the platform audit's P0 spine is approved** — hand-written SQL DDL baseline, Postgres RLS, monthly partitioning of five tables, the index inventory and the transaction model. §19 specifies them; none is built yet.

### Still open
- **`[COUNSEL]` items**: the GOSI rate ladder and wage bounds (§E) and the retention periods (§12.4) are engineering defaults carrying the product's published commitments, and need legal confirmation before go-live.
- **The platform audit's P0s** (SQL DDL baseline, RLS, partitioning, index inventory, transaction model) are approved (decision 10) and **specified in §19**. None is built yet: §19 is a specification, and the build starts with the baseline DDL slice.
- **The region decision** (platform audit P2-19): app in Oregon, database in `us-east-1`, ~70 ms per round trip. Moving Render to Virginia or Neon to `us-west-2` is the cheapest performance work available and needs an owner call; §19.6 carries it and the request-latency SLO that makes the cost visible.

## 6. Risks (6)

1. **Table growth is now governed by a principle rather than a number** (decision 9), which is the right rule and a weaker brake. *Mitigation:* every addition must name the capability it serves and pass the no-duplicate test in review; §7 keeps the record of what was considered and rejected so the bar stays visible; the generated data dictionary and ERD (§19.1) make an unused table obvious rather than invisible.
2. **Wrong GOSI cohort or registered wage.** The cohort depends on `gosi_first_registered_on`, keyed by HR from the GOSI record, and GOSI invoices against the wage it holds, not the wage payroll computes. *Mitigation:* NULL blocks the slip, the rule and band are frozen on every line, `employee_gosi_registrations` holds the registered wage, and `gosi_filings.variance_amount` forces every difference from the GOSI invoice to be explained.
3. **Tenant leakage through JSON and the five nullable-tenant tables.** After decision 2, `users.tenant_id` is NOT NULL and operators live in `platform_users`; the tables where `tenant_id` can still be NULL are exactly **seven** — `audit_logs`, `background_jobs`, `background_job_items`, `public_holidays`, `retention_policies`, `auth_sessions` and `auth_tokens` (revision 7 added the last three to this count; §19.2 explains why each is a *silent* failure under the wrong shape rather than a leak). Composite FKs also cannot guard JSON payloads such as `approval_requests.payload` or `nitaqat_snapshots.employee_breakdown`. *Mitigation:* the shape-(b) and `p_auth` RLS policies of §19.2 aimed at those five tables specifically, CHECK constraints tying `subject_kind` to its subject columns, and a tenant-isolation test per table in the baseline PR.
4. **Checkpointed audit is weaker than a per-row chain, between checkpoints.** A tamper inside the current uncheckpointed window is not provable. *Mitigation:* checkpoint every N rows or M seconds (both small), keep the payroll log row-chained, and put checkpoint lag on the platform health view. A second, subtler failure is handled in §Q: `seq` comes from a Postgres sequence, which is non-transactional, so every rolled-back audited transaction burns a value and leaves a permanent gap. Alerting on gaps would therefore mute itself within a week — and a muted alert is how tamper detection actually dies — so gaps are recorded as data in the checkpoint and the alert fires only on a changed root or a row missing from an already-checkpointed range.
5. **`payroll_inputs` is now the single spine for every variable pay item.** A defect in the claim statement double-pays or silently drops pay. *Mitigation:* the single-statement claim above, a partial index, run-type targeting, cancellation by revision, plus concurrency tests that run two runs against one input set and a crash-and-retry test that must produce identical slips.
6. **Big-bang code rewrite behind the schema.** 76 tables replace 323 across roughly 100 controllers and services; the heaviest rewrites are `payroll_inputs` (8 tables), approvals (every module) and the uuid employee key. *Mitigation:* land the baseline migration and seeders first, then port by domain: identity, org and employee, payroll/WPS/GOSI filing/GL, leave and attendance and timesheets, EOS and Nitaqat. Each step needs tenant and payroll golden-file tests. Do not deploy to the Evostel pilot until the payroll golden files match.

## 7. The table count, and the cuts considered and rejected

**76 tables.** The owner has since dropped the hard cap in favour of the principle (decision 9), so **none of the cuts below is taken**. This section is kept as the record of what was considered and what each would have cost, so the question does not get re-opened from memory.

Added above the 72 of revision 2, each on a stated decision:

| Table | Added by | Why it is not convenience |
|---|---|---|
| `company_pay_policies` | modelling audit P1 #8 | Contractual above-floor money was the one effective-dated structure living in a JSON blob, unable to use the `daterange` EXCLUDE that protects every other dated table |
| `retention_policies` | modelling audit P0 #6 / P1 #9 | The PDPL retention matrix was `appsettings`: not tenant-visible, not audited, not testable as data |
| `platform_users` | owner decision Q2 | Keeps nullable-tenant rows out of the client-data tier |
| `permission_grantor_records` | decision F5 | A live authorization fact with three endpoints that `user_roles.granted_by` cannot express |

**Considered and rejected.** Had the cap been enforced, these were the cuts in priority order. Each line says what would have been lost.

1. `timesheet_day_reconciliations` (→ 75). Fold the three compared values into `timesheet_entries` columns and compute the variance in a view. *Lost:* the per-day Open/Explained/Accepted/Rejected workflow on a variance, which becomes a report rather than a worklist.
2. `attendance_devices` (→ 74). Move the terminal registry into `tenant_settings.attendance.devices`. *Lost:* a hashed per-device API key with rotation and a real FK from punches; device identity becomes a string in a blob. Only acceptable if biometric terminals are not in the first release.
3. `gl_period_closes` (→ 73). Express the close as a status on the last journal of the period. *Lost:* closing a period that has no journal in it, and the reopen-with-reason audit finance will be asked about.
4. `notification_deliveries` (→ 72). Collapse attempts onto `notifications`. *Lost:* multi-channel retry history and per-channel failure diagnosis; one row can then record only the last attempt.
5. `timesheet_day_reconciliations`' sibling cut, `timesheets` itself, only if timesheets leave scope again — which decision 8 has settled the other way.

**Not on the cut list at any price**, because removing them re-creates the defects this design exists to fix: `payroll_slip_lines`, `payroll_inputs`, `payroll_issues`, `wps_lines`, `gosi_filings`, `employee_gosi_registrations`, `statutory_rule_bands`, `leave_ledger`, the four dated employee tables, `approval_*`, the three audit tables, `retention_policies`, `company_pay_policies`, `number_sequences`, `files`, `permission_grantor_records`, `platform_users`.

**Future claims** already known: `projects` + `project_rates` for full timesheet costing (→ 78), and `integration_messages` if a Qiwa or Mudad API replaces file exchange (§18). Both are deliberately unbuilt. Under decision 9 they are judged on the principle, not on a number: each must name its capability and duplicate nothing.

---

# Part II — behaviour (revision 3)

Part I says what exists. Part II says how it behaves. The audit's verdict was that the first was done and the second was not; everything below is the second.

## 8. Relationship register: every foreign key

### 8.1 The rules these follow
- **RESTRICT** is the default. Anything financial, filed, audited or historical refuses to let its parent disappear underneath it.
- **CASCADE** only where the child is a *part* of its parent and has no independent meaning: a slip's lines, a batch's lines, a journal's lines, a job's items, a notification's deliveries, a rule's bands, a timesheet's entries, a loan's schedule. Every CASCADE parent also has a `BEFORE DELETE` guard that raises once the parent leaves its editable state, so CASCADE can only ever fire on a draft.
- **SET NULL** only where the column is genuinely optional and the fact survives the loss (a device, a shift, an uploader, a lock).
- **A composite `ON DELETE SET NULL` must name its column** — `ON DELETE SET NULL (uploaded_by)`, never a bare `SET NULL`. This is the second defect the implementation found (revision 7 decision 9b) and it is a **runtime** failure, not a DDL one: every FK here is `(tenant_id, x_id) REFERENCES x (tenant_id, id)`, and a bare `SET NULL` nulls *both* referencing columns, including `tenant_id`, which is `NOT NULL` on every tenant table. The DDL is accepted; the first parent delete that fires the rule raises `23502` in production, on a purge path, at the exact moment nobody wants a surprise. The column-list form fixes it, and **it requires PostgreSQL 15** — see the version floor in §19.1. Every `SET NULL` row in the register below is to be read as naming its own column.
- **ON UPDATE RESTRICT** everywhere: keys are `uuid`, never updated.
- Composite everywhere it matters: `(tenant_id, x_id) REFERENCES x (tenant_id, id)`.
- **NOT NULL is declared per row below**, under *Opt.* — R = required, O = optional.

### 8.2 Register

| # | Child.column | → Parent | Card. | Opt. | ON DELETE | Why |
|---|---|---|---|---|---|---|
| 1 | tenant_settings.tenant_id | tenants | 1:1 | R | CASCADE | Settings are part of the tenant |
| 2 | number_sequences.tenant_id | tenants | N:1 | R | CASCADE | Counters are part of the tenant |
| 3 | number_sequences.company_id | companies | N:1 | O | RESTRICT | A company with allocated numbers is not removable |
| 4 | files.tenant_id | tenants | N:1 | R | RESTRICT | Blobs are purged by the retention job, never by a parent delete |
| 5 | files.uploaded_by | users | N:1 | O | SET NULL | The file outlives an anonymised uploader |
| 6 | retention_policies.tenant_id | tenants | N:1 | O | CASCADE | A tenant override dies with the tenant; the platform row has NULL |
| 7 | users.tenant_id | tenants | N:1 | **R** | RESTRICT | No nullable tenant in the client tier any more (decision 2); tenant purge deletes users explicitly, in order |
| 8 | users.employee_id | employees | 1:1 | O | RESTRICT | An employee row is never hard-deleted while a login exists; erasure anonymises the employee and disables the login (§12.3) |
| 9 | roles.tenant_id | tenants | N:1 | R | RESTRICT | Purged in the declared order |
| 10 | role_permissions.(tenant_id, role_id) | roles | N:1 | R | CASCADE | Grants are part of the role |
| 11 | role_permissions.permission_code | permissions | N:1 | R | RESTRICT | Retiring a permission is a migration, not a delete |
| 12 | user_roles.(tenant_id, user_id) | users | N:1 | R | CASCADE | A grant is part of the user |
| 13 | user_roles.(tenant_id, role_id) | roles | N:1 | R | RESTRICT | A role in use cannot vanish; deactivate it |
| 14 | user_roles.scope_company_id | companies | N:1 | O | RESTRICT | Scope must not silently widen to the whole tenant |
| 15 | user_roles.scope_branch_id | branches | N:1 | O | RESTRICT | Same |
| 16 | user_roles.scope_department_id | departments | N:1 | O | RESTRICT | Same |
| 17 | user_roles.granted_by | users | N:1 | O | SET NULL | Who granted it is also in `audit_logs` |
| 18 | auth_sessions.user_id | users | N:1 | O (XOR) | CASCADE | Sessions are part of the user; exactly one of the two subject columns is set |
| 18b | auth_sessions.platform_user_id | platform_users | N:1 | O (XOR) | CASCADE | Same, for a platform operator |
| 19 | auth_tokens.user_id | users | N:1 | O (XOR) | CASCADE | Tokens are part of the user |
| 19b | auth_tokens.platform_user_id | platform_users | N:1 | O (XOR) | CASCADE | Same, for a platform operator |
| 20 | companies.tenant_id | tenants | N:1 | R | RESTRICT | Payroll history must never disappear with a mis-clicked tenant delete |
| 21 | company_pay_policies.(tenant_id, company_id) | companies | N:1 | R | RESTRICT | Contractual money |
| 22 | company_pay_policies.pay_component_code | pay_components | N:1 | O | RESTRICT | A component in a policy cannot be deleted |
| 23 | branches.(tenant_id, company_id) | companies | N:1 | R | RESTRICT | Referenced by attendance and assignments |
| 24 | departments.(tenant_id, company_id) | companies | N:1 | R | RESTRICT | Same |
| 25 | departments.parent_id | departments | N:1 | O | RESTRICT | A parent with children stays |
| 26 | departments.cost_center_id | cost_centers | N:1 | O | SET NULL | The department survives a retired cost centre |
| 27 | cost_centers.(tenant_id, company_id) | companies | N:1 | R | RESTRICT | GL dimension |
| 28 | cost_centers.parent_id | cost_centers | N:1 | O | RESTRICT | Hierarchy integrity |
| 29 | designations.tenant_id | tenants | N:1 | R | RESTRICT | Referenced by assignments |
| 30 | grades.tenant_id | tenants | N:1 | R | RESTRICT | Same |
| 31 | public_holidays.tenant_id | tenants | N:1 | O | CASCADE | A tenant calendar is part of the tenant; NULL is the platform calendar |
| 32 | employees.tenant_id | tenants | N:1 | R | RESTRICT | Purge deletes explicitly, in order, with evidence |
| 33 | employee_assignments.(tenant_id, employee_id) | employees | N:1 | R | RESTRICT | History outlives edits; employees are anonymised, not deleted |
| 34 | employee_assignments.company_id | companies | N:1 | R | RESTRICT | The employing entity |
| 35 | employee_assignments.branch_id | branches | N:1 | R | RESTRICT | Required: attendance and WPS both need it |
| 36 | employee_assignments.department_id | departments | N:1 | R | RESTRICT | Required for GL and reporting |
| 37 | employee_assignments.designation_id | designations | N:1 | R | RESTRICT | Required for Nitaqat occupation |
| 38 | employee_assignments.grade_id | grades | N:1 | O | RESTRICT | Optional by product |
| 39 | employee_assignments.manager_employee_id | employees | N:1 | O | RESTRICT | The org chart must not silently break |
| 40 | employee_assignments.cost_center_id | cost_centers | N:1 | O | RESTRICT | Required at calculation when the company posts GL by cost centre; enforced as a `payroll_issues` Block, not a NOT NULL, so HR can still save an employee |
| 41 | employee_assignments.approval_request_id | approval_requests | N:1 | O | RESTRICT | The decision behind the change |
| 42 | employee_salaries.(tenant_id, employee_id) | employees | N:1 | R | RESTRICT | Money history |
| 43 | employee_salaries.approval_request_id | approval_requests | N:1 | O | RESTRICT | Same |
| 44 | employee_contracts.(tenant_id, employee_id) | employees | N:1 | R | RESTRICT | Legal document |
| 45 | employee_contracts.document_id | employee_documents | N:1 | O | SET NULL | The terms survive the scan |
| 46 | employee_bank_accounts.(tenant_id, employee_id) | employees | N:1 | R | RESTRICT | Payment history |
| 47 | employee_documents.(tenant_id, employee_id) | employees | 1:N | R | CASCADE | Documents are part of the person; only the purge job can reach this |
| 48 | employee_documents.file_id | files | N:1 | O | RESTRICT | The `files` row owns the purge state |
| 49 | employee_documents.template_id | document_templates | N:1 | O | RESTRICT | An issued letter must keep its template |
| 50 | employee_documents.supersedes_id | employee_documents | N:1 | O | SET NULL | A renewal chain survives a deleted predecessor |
| 51 | employee_documents.leave_request_id | leave_requests | N:1 | O | SET NULL | A sick note outlives a cancelled request |
| 52 | document_templates.company_id | companies | N:1 | O | RESTRICT | NULL = tenant-wide |
| 53 | statutory_rule_bands.statutory_rule_id | statutory_rules | 1:N | R | CASCADE | Bands are parts of the rule |
| 54 | pay_components.tenant_id | tenants | N:1 | R | RESTRICT | Referenced by every line |
| 55 | payroll_runs.(tenant_id, company_id) | companies | N:1 | R | RESTRICT | Filed money |
| 56 | payroll_runs.parent_run_id | payroll_runs | N:1 | O | RESTRICT | Correction chains stay intact |
| 57 | payroll_runs.source_import_job_id | background_jobs | N:1 | O | SET NULL | Opening-run provenance survives job pruning |
| 58 | payroll_runs.approval_request_id | approval_requests | N:1 | O | RESTRICT | The approval of a run |
| 59 | payroll_slips.(tenant_id, run_id) | payroll_runs | 1:N | R | CASCADE + guard | Slips are parts of a run; the guard raises unless `status='Draft'` |
| 60 | payroll_slips.(tenant_id, employee_id) | employees | N:1 | R | RESTRICT | Paid money |
| 61 | payroll_slips.payslip_file_id | files | N:1 | O | RESTRICT | The issued document |
| 62 | payroll_slips.template_id | document_templates | N:1 | O | RESTRICT | Reprint fidelity |
| 63 | payroll_slip_lines.(tenant_id, slip_id) | payroll_slips | 1:N | R | CASCADE + guard | Lines are parts of a slip; guard raises unless the run is Draft |
| 64 | payroll_slip_lines.(tenant_id, pay_component_code) | pay_components | N:1 | R | RESTRICT | Component meaning must survive |
| 65 | payroll_slip_lines.statutory_rule_id | statutory_rules | N:1 | O | RESTRICT | The frozen rule |
| 66 | payroll_slip_lines.statutory_rule_band_id | statutory_rule_bands | N:1 | O | RESTRICT | The frozen band |
| 67 | payroll_slip_lines.payroll_input_id | payroll_inputs | N:1 | O | RESTRICT | Provenance of variable pay |
| 68 | payroll_slip_lines.loan_installment_id | loan_installments | N:1 | O | RESTRICT | Recovery evidence |
| 69 | payroll_slip_lines.cost_center_id | cost_centers | N:1 | O | RESTRICT | GL dimension; NOT NULL in effect via the same Block rule as row 40 |
| 70 | payroll_inputs.(tenant_id, employee_id) | employees | N:1 | R | RESTRICT | Money owed |
| 71 | payroll_inputs.company_id | companies | N:1 | R | RESTRICT | Paying entity |
| 72 | payroll_inputs.pay_component_code | pay_components | N:1 | R | RESTRICT | Component meaning |
| 73 | payroll_inputs.cost_center_id | cost_centers | N:1 | O | RESTRICT | GL dimension |
| 74 | payroll_inputs.claimed_by_run_id | payroll_runs | N:1 | O | SET NULL | Deleting a draft run releases the claim |
| 75 | payroll_inputs.consumed_run_id | payroll_runs | N:1 | O | RESTRICT | A consumed input pins its run |
| 76 | payroll_issues.run_id | payroll_runs | N:1 | O | CASCADE | Run findings die with a draft run; NULL = standing gap |
| 77 | payroll_issues.(tenant_id, employee_id) | employees | N:1 | O | RESTRICT | Standing gaps belong to the person |
| 78 | payroll_issues.override_by | users | N:1 | O | RESTRICT | Who waived a warning is evidence |
| 79 | wps_batches.run_id | payroll_runs | N:1 | R | RESTRICT | Filed evidence |
| 80 | wps_batches.company_id | companies | N:1 | R | RESTRICT | Filed evidence |
| 81 | wps_batches.file_id | files | N:1 | O | RESTRICT | The SIF as filed |
| 82 | wps_batches.resubmission_of_id | wps_batches | N:1 | O | RESTRICT | Resubmission chain |
| 83 | wps_batches.generated_by | users | N:1 | O | SET NULL | Also in the audit log |
| 84 | wps_lines.batch_id | wps_batches | 1:N | R | CASCADE + guard | Lines are parts of a batch; guard raises unless `status='Generated'` |
| 85 | wps_lines.slip_id | payroll_slips | N:1 | R | RESTRICT | The slip it filed |
| 86 | wps_lines.(tenant_id, employee_id) | employees | N:1 | R | RESTRICT | Paid money |
| 87 | wps_lines.confirmation_job_id | background_jobs | N:1 | O | SET NULL | Bank confirmation provenance |
| 88 | gl_mappings.company_id | companies | N:1 | O | RESTRICT | NULL = tenant default |
| 89 | gl_mappings.cost_center_id | cost_centers | N:1 | O | RESTRICT | Mapping dimension |
| 90 | gl_journals.company_id | companies | N:1 | R | RESTRICT | Posted evidence |
| 91 | gl_journals.file_id | files | N:1 | O | RESTRICT | The export as sent |
| 92 | gl_journals.reversal_of_id | gl_journals | N:1 | O | RESTRICT | Reversal chain |
| 93 | gl_journal_lines.journal_id | gl_journals | 1:N | R | CASCADE + guard | Parts of a journal; guard raises unless `status='Draft'` |
| 94 | gl_journal_lines.cost_center_id | cost_centers | N:1 | O | RESTRICT | Required when the company posts by cost centre (same Block rule) |
| 95 | gl_period_closes.company_id | companies | N:1 | R | RESTRICT | Finance evidence |
| 96a | gl_period_closes.closed_by | users | N:1 | O | RESTRICT | Who closed a period is evidence |
| 96b | gl_period_closes.reopened_by | users | N:1 | O | RESTRICT | Who reopened it is the one finance will be asked about |
| 97 | loans.(tenant_id, employee_id) | employees | N:1 | R | RESTRICT | Money owed |
| 98 | loans.approval_request_id | approval_requests | N:1 | O | RESTRICT | The approval |
| 99 | loan_installments.loan_id | loans | 1:N | R | CASCADE + guard | Schedule is part of the loan; guard raises once any installment is Recovered |
| 100 | leave_types.tenant_id | tenants | N:1 | R | RESTRICT | Referenced by the ledger |
| 101 | leave_requests.(tenant_id, employee_id) | employees | N:1 | R | RESTRICT | Entitlement history |
| 102 | leave_requests.leave_type_id | leave_types | N:1 | R | RESTRICT | Type meaning |
| 103 | leave_requests.approval_request_id | approval_requests | N:1 | O | RESTRICT | The decision |
| 104 | leave_ledger.(tenant_id, employee_id) | employees | N:1 | R | RESTRICT | Append-only balance |
| 105 | leave_ledger.leave_type_id | leave_types | N:1 | R | RESTRICT | Type meaning |
| 106 | shifts.tenant_id | tenants | N:1 | R | RESTRICT | Referenced by assignments |
| 107 | shift_assignments.(tenant_id, employee_id) | employees | N:1 | R | RESTRICT | Roster history |
| 108 | shift_assignments.shift_id | shifts | N:1 | R | RESTRICT | Shift meaning |
| 109 | attendance_devices.branch_id | branches | N:1 | R | RESTRICT | Device location |
| 110 | attendance_punches.(tenant_id, employee_id) | employees | N:1 | R | RESTRICT | Raw evidence |
| 111 | attendance_punches.device_id | attendance_devices | N:1 | O | SET NULL | The punch outlives the terminal |
| 112 | attendance_punches.approval_request_id | approval_requests | N:1 | O | RESTRICT | Correction provenance |
| 113 | attendance_days.(tenant_id, employee_id) | employees | N:1 | R | RESTRICT | Payroll input |
| 114 | attendance_days.shift_id | shifts | N:1 | O | SET NULL | A retired shift does not erase the day |
| 115 | attendance_days.locked_run_id | payroll_runs | N:1 | O | SET NULL | Voiding or deleting a draft run unlocks the days |
| 116 | overtime_requests.(tenant_id, employee_id) | employees | N:1 | R | RESTRICT | Money |
| 117 | overtime_requests.statutory_rule_id | statutory_rules | N:1 | O | RESTRICT | The frozen multiplier |
| 118 | overtime_requests.approval_request_id | approval_requests | N:1 | O | RESTRICT | The decision |
| 119 | timesheets.(tenant_id, employee_id) | employees | N:1 | R | RESTRICT | Hours record |
| 120 | timesheets.company_id | companies | N:1 | R | RESTRICT | Employing entity |
| 121 | timesheets.approval_request_id | approval_requests | N:1 | O | RESTRICT | The decision |
| 122 | timesheets.locked_run_id | payroll_runs | N:1 | O | SET NULL | Same unlock rule as attendance |
| 123 | timesheet_entries.timesheet_id | timesheets | 1:N | R | CASCADE + guard | Parts of a timesheet; guard raises unless Draft or Rejected |
| 124 | timesheet_entries.cost_center_id | cost_centers | N:1 | O | RESTRICT | Costing dimension |
| 125 | timesheet_day_reconciliations.timesheet_id | timesheets | 1:N | R | CASCADE | Parts of a timesheet |
| 126 | timesheet_day_reconciliations.(tenant_id, attendance_day_id, work_date) | attendance_days (tenant_id, id, work_date) | N:1 | O | RESTRICT | The compared day. The partition key is part of the key because `attendance_days` is partitioned (§19.3); it is the only FK targeting a partitioned table |
| 127 | eos_calculations.(tenant_id, employee_id) | employees | N:1 | R | RESTRICT | Statutory money |
| 128 | eos_calculations.settlement_id | final_settlements | N:1 | O | SET NULL | An estimate has no settlement yet |
| 129 | final_settlements.(tenant_id, employee_id) | employees | N:1 | R | RESTRICT | Statutory money |
| 130 | final_settlements.company_id | companies | N:1 | R | RESTRICT | Paying entity |
| 131 | final_settlements.paid_via_run_id | payroll_runs | N:1 | O | RESTRICT | Payment evidence |
| 132 | final_settlements.approval_request_id | approval_requests | N:1 | O | RESTRICT | The decision |
| 133 | final_settlement_lines.settlement_id | final_settlements | 1:N | R | CASCADE + guard | Parts; guard raises unless `status='Draft'` |
| 134 | final_settlement_lines.loan_installment_id | loan_installments | N:1 | O | RESTRICT | Recovery evidence |
| 135 | nitaqat_snapshots.company_id | companies | N:1 | R | RESTRICT | Compliance evidence |
| 136 | employee_gosi_registrations.(tenant_id, employee_id) | employees | N:1 | R | RESTRICT | Statutory registration |
| 137 | employee_gosi_registrations.company_id | companies | N:1 | R | RESTRICT | Establishment |
| 138 | employee_gosi_registrations.(tenant_id, gosi_registration_no) | companies (tenant_id, gosi_registration_no) | N:1 | R | RESTRICT | One owner for the filing identifier (§11.5) |
| 139 | gosi_filings.company_id | companies | N:1 | R | RESTRICT | Filed return |
| 140 | gosi_filings.(tenant_id, gosi_registration_no) | companies (tenant_id, gosi_registration_no) | N:1 | R | RESTRICT | Same |
| 141 | gosi_filings.file_id | files | N:1 | O | RESTRICT | The return as filed |
| 142 | gosi_filings.filed_by | users | N:1 | O | RESTRICT | Who filed is evidence |
| 143 | approval_workflows.company_id | companies | N:1 | O | RESTRICT | NULL = tenant-wide |
| 144 | approval_requests.workflow_id | approval_workflows | N:1 | R | RESTRICT | The definition behind the instance (a snapshot is also frozen on the row) |
| 145 | approval_requests.requester_user_id | users | N:1 | R | RESTRICT | Who asked |
| 146 | approval_requests.(tenant_id, employee_id) | employees | N:1 | O | RESTRICT | Who it is about |
| 147 | approval_actions.request_id | approval_requests | N:1 | R | RESTRICT | Decisions are evidence; requests are never deleted |
| 148 | approval_actions.actor_user_id | users | N:1 | R | RESTRICT | Who decided |
| 149 | approval_actions.on_behalf_of_user_id | users | N:1 | O | SET NULL | Delegation subject |
| 150a | approval_delegations.delegator_user_id | users | N:1 | R | CASCADE | A delegation is meaningless without both parties |
| 150b | approval_delegations.delegate_user_id | users | N:1 | R | CASCADE | Same |
| 151 | notifications.user_id | users | N:1 | R | CASCADE | Inbox is part of the user |
| 152 | notification_deliveries.notification_id | notifications | 1:N | R | CASCADE | Attempts are parts of the notification |
| 153 | background_jobs.tenant_id | tenants | N:1 | O | RESTRICT | NULL = platform job |
| 154 | background_jobs.source_file_id | files | N:1 | O | SET NULL | The job outlives the import file |
| 155 | background_job_items.job_id | background_jobs | 1:N | R | CASCADE | Items are parts of the job |
| 156 | permission_grantor_records.(tenant_id, grantor_user_id) | users | N:1 | R | CASCADE | The authority is part of the user it is granted to |
| 157 | permission_grantor_records.granted_by_user_id | users | N:1 | O | RESTRICT | Who conferred the authority is an authorization fact, not a convenience |
| 158 | permission_grantor_records.revoked_by | users | N:1 | O | RESTRICT | Same |
| **159** | **gl_mappings.tenant_id** | tenants | N:1 | R | RESTRICT | **New in revision 7.** `gl_mappings` is tier T and its only other parent is a **nullable** `company_id`, so a default mapping (`company_id IS NULL`) had no tenant FK at all — an unvalidated `tenant_id` on exactly the rows every company falls back to |
| **160** | **approval_workflows.tenant_id** | tenants | N:1 | R | RESTRICT | **New in revision 7.** Same defect: a tenant-wide workflow (`company_id IS NULL`) is the normal case, and it was the case with no tenant FK |
| **161** | **document_templates.tenant_id** | tenants | N:1 | R | RESTRICT | **New in revision 7.** The third table with the same shape, found by applying the same test; a tenant-wide template had no tenant FK |
| — | audit_logs, payroll_audit_logs, retention_purge_audits | (none) | — | — | — | **Deliberately FK-free** so evidence outlives every purge. `tenant_id`, `entity_id`, `job_id` and `rule_key` are plain columns, with orphan detection in §16.3 |

**The arithmetic, reconciled (revision 7 decision 12).** The register above is **165 rows and 165 foreign-key constraints — one constraint per row, with no row standing for two.** Revision 6's closing line said "160 foreign keys", and three separate things were wrong with it:

| | Count | |
|---|---|---|
| Register rows in revision 6 (1–158, plus 18b and 19b) | 160 | but rows 96 and 150 each named **two** columns, so they were 162 constraints, not 160 |
| Rows 96 and 150 split into 96a/96b and 150a/150b | **+2 rows** | the register is now one row per constraint, which is what the ratchet can actually count |
| Tenant-FK gaps closed (rows 159–161) | **+3** | §8.2 rows 159, 160, 161 above |
| **Total** | **165** | |

Where they live in the baseline DDL, which is the other half of the reconciliation:

| Where | Count | Which |
|---|---|---|
| `020_constraints_a_f.sql` | 77 | every register row whose **child** is a domain A–F table and whose parent is also A–F |
| `021_constraints_g_r.sql` | 79 | every register row whose child is a domain G–R table |
| **`022_constraints_cross.sql`** | **6** | rows **41, 43, 51, 57, 58, 68** — an A–F child with a G–R parent, so neither earlier file can declare them: `employee_assignments.approval_request_id`, `employee_salaries.approval_request_id`, `employee_documents.leave_request_id`, `payroll_runs.source_import_job_id`, `payroll_runs.approval_request_id`, `payroll_slip_lines.loan_installment_id`. All six columns already exist with the nullability §8 requires; only the constraints were missing, and 020 footnoted them rather than dropping them |
| Not yet written | 3 | rows 159–161, added by this revision — `gl_mappings` and `approval_workflows` belong in `021`, `document_templates` in `020` |
| **Total** | **165** | |

Nothing is lost between the files: the 6 cross-domain rows are the ones 020 explicitly parked and 021 could not reach, and `022` exists so that "every register row is declared somewhere" is a countable CI assertion rather than a reading exercise. Note that **row 57 cannot be composite**: `background_jobs.tenant_id` is nullable, so `(tenant_id, id)` would not be enforced under `MATCH SIMPLE` for a platform job; rows 57, 87 and 154/155 are single-column by the same reasoning, stated here so the composite-key ratchet has its four named exemptions.

The nine tables with **no outbound FK** are, by design: `tenants` (the tenancy root), `platform_users` (deliberately outside tenancy), `data_protection_keys` (framework-owned), `permissions`, `statutory_rules` and `nitaqat_grid` (platform reference), and the three audit tables (FK-free so evidence outlives every purge).

### 8.3 Tenant purge order
Because tenant-owned business tables are RESTRICT, a tenant purge is an explicit, ordered job (`background_jobs` kind `retention.purge-tenant`), writing `retention_purge_audits` per step: `wps_lines` → `wps_batches` → `gl_journal_lines` → `gl_journals` → `gl_period_closes` → `gosi_filings` → `employee_gosi_registrations` → `payroll_slip_lines` → `payroll_slips` → `payroll_issues` → `payroll_inputs` → `payroll_runs` → `final_settlement_lines` → `final_settlements` → `eos_calculations` → `loan_installments` → `loans` → `timesheet_day_reconciliations` → `timesheet_entries` → `timesheets` → `attendance_days` → `attendance_punches` → `attendance_devices` → `overtime_requests` → `leave_ledger` → `leave_requests` → `leave_types` → `shift_assignments` → `shifts` → `employee_documents` → `files` → the employee dated tables → `employees` → `approval_actions` → `approval_requests` → `approval_workflows` → `company_pay_policies` → org tables → identity tables (`permission_grantor_records`, `user_roles`, `auth_tokens`, `auth_sessions`, `users`, `roles`) → `tenants`. `platform_users` is never touched by a tenant purge. The audit tables are never deleted; they are redacted in place (§12.3).

### 8.4 The two cycles, broken rather than deferred
Revision 2 had two row-level cycles. Both are removed by keeping the pointer on one side only, so the baseline has **no foreign-key cycle at all** and no deferrable constraint is needed:
- **`payroll_slip_lines` ⇄ `payroll_inputs`**: `payroll_inputs.consumed_line_id` is deleted. The line points at the input (`payroll_slip_lines.payroll_input_id`), the input records only `consumed_run_id`, and "which line consumed this input" is one indexed lookup.
- **`payroll_slip_lines` ⇄ `loan_installments`**: `loan_installments.recovered_slip_line_id` and `.settlement_line_id` are deleted, replaced by `recovered_at` plus the status. Recovery is found from `payroll_slip_lines.loan_installment_id` and `final_settlement_lines.loan_installment_id`, both indexed.
- Insertion order is therefore forced and simple: input or installment first, slip line second. Any future cycle must be declared `DEFERRABLE INITIALLY DEFERRED` **and** state its insert order in this section.

## 9. Status domains: every enumerated column

One mechanism for all of them: `text` + a named `CHECK`, mirrored by a C# constants class, seeded nowhere and changed only by migration. The live database already does this for two columns (`Migrations/20260624000003_AddStatusCheckConstraints.cs:37,40`); revision 3 extended it to 36 and **does not regress the nine `payroll_runs` values already constrained there**. Revision 7 adds eight more (rows 37–44), closing every closed set that had no domain.

### 9.0 Constraint naming: one rule, and one closed legacy set (revision 7 decision 2)

CONVENTIONS §16 carried constraint naming as decision 4, "genuinely undecided". It is decided here, because the baseline DDL cannot be written without a name on every line and 36 of those names are already load-bearing.

- **The rule is `ck_<table>__<what it asserts>`** — double underscore between table and assertion, the assertion in words, not a column list: `ck_gl_journal_lines__debit_xor_credit`, `ck_audit_logs__erasure_is_complete`. It applies to every CHECK constraint written from revision 7 onward and to every CHECK in the baseline that §9 does not name. The sibling patterns are `pk_<table>`, `fk_<table>__<column>`, `uq_<table>__<cols>`, `ex_<table>__<subject>_no_overlap`, `ix_<table>__<cols>`, `trg_<table>__<what it enforces>`.
- **The 36 names in the table below are a closed, frozen legacy set and keep their `chk_<table>_<column>` spelling.** They are not a style lapse to be tidied later: each is **mirrored by a C# constants class of the same name**, the name is what CI compares the constants class against, and the live database already ships two of them (`chk_payroll_runs_status` among them). Renaming them would silently break the assertion that is the entire reason the design named constraints in the first place. They are therefore exempt from the `ck_` rule, the exemption is closed (no thirty-seventh `chk_` name is ever added), and every enumeration added from revision 7 onward — including rows 37–44 below — takes a `ck_` name.
- **A name is per table, so one row may not name one constraint on two tables.** Revision 6's row 3b gave a single name, `chk_auth_subject_kind`, to a CHECK on *both* `auth_sessions.subject_kind` and `auth_tokens.subject_kind`. Postgres stores a constraint against exactly one relation, so that name can only ever exist twice as two different objects, and a failure message naming `chk_auth_subject_kind` would not say which table raised it — which is the whole rationale for encoding the table in the name. Row 3b is therefore **split into 3b and 3c**, one constraint per table, below.

| # | Column | Allowed values | Constraint |
|---|---|---|---|
| 1 | tenants.status | Active, Suspended, SoftDeleted, Purged | chk_tenants_status |
| 2 | users.status | Invited, Active, Suspended, Locked, Disabled | chk_users_status |
| 3 | platform_users.status | Active, Suspended, Disabled | chk_platform_users_status |
| 3b | auth_sessions.subject_kind | Tenant, Platform | chk_auth_sessions_subject_kind (plus `ck_auth_sessions__subject_xor` on the two subject columns) |
| 3c | auth_tokens.subject_kind | Tenant, Platform | chk_auth_tokens_subject_kind (plus `ck_auth_tokens__subject_xor`) |
| 4 | auth_tokens.purpose | PasswordReset, MfaChallenge, Invitation, EmailConfirm | chk_auth_tokens_purpose |
| 5 | employees.status | Draft, Invited, Active, Suspended, Offboarded, Archived | chk_employees_status |
| 6 | employees.privacy_status | Normal, PendingErasure, Anonymised, MergedDuplicate | chk_employees_privacy |
| 7 | employee_documents.status | Active, Expired, Superseded, Revoked | chk_employee_documents_status |
| 8 | employee_documents.doc_type | Iqama, Passport, Visa, WorkPermit, Contract, Letter, Medical, Other | chk_employee_documents_type |
| 9 | payroll_runs.status | **Draft, Processing, Processed, PendingFinanceReview, Approved, Completed, Locked, Paid, Voided** (the nine already in the live CHECK) | chk_payroll_runs_status |
| 10 | payroll_runs.run_type | Regular, OffCycle, FinalSettlement, Correction, Opening | chk_payroll_runs_type |
| 11 | payroll_slips.inclusion_status | Included, ExcludedByFilter, ExcludedByHold, ExcludedByBlock | chk_payroll_slips_inclusion |
| 12 | payroll_slip_lines.kind | Earning, Deduction, EmployerContribution, Info | chk_payroll_slip_lines_kind |
| 13 | payroll_inputs.status | Pending, Claimed, Consumed, Cancelled | chk_payroll_inputs_status |
| 14 | payroll_issues.severity | Block, Warn | chk_payroll_issues_severity |
| 15 | wps_batches.status | Generated, Submitted, Accepted, PartiallyRejected, Rejected, Superseded | chk_wps_batches_status |
| 16 | wps_lines.bank_status | Pending, Paid, Rejected, Returned, OnHold | chk_wps_lines_bank_status |
| 17 | gl_journals.status | Draft, Exported, Posted, Rejected, Reversed | chk_gl_journals_status |
| 18 | gl_period_closes.status | Open, Closed, Reopened | chk_gl_period_closes_status |
| 19 | loans.status | PendingApproval, Active, Settled, Cancelled, Rejected | chk_loans_status |
| 20 | loan_installments.status | Due, Recovered, Waived, Cancelled | chk_loan_installments_status |
| 21 | leave_requests.status | Draft, PendingApproval, Approved, Rejected, Cancelled, Taken | chk_leave_requests_status |
| 22 | overtime_requests.status | Draft, PendingApproval, Approved, Rejected, Cancelled, Paid, ConvertedToCompOff | chk_overtime_requests_status |
| 23 | attendance_days.status | Present, Absent, Leave, Holiday, WeeklyOff, Incomplete | chk_attendance_days_status |
| 24 | timesheets.status | Draft, Submitted, Approved, Rejected, Locked | chk_timesheets_status |
| 25 | timesheet_day_reconciliations.status | Open, Explained, Accepted, Rejected | chk_tsdr_status |
| 26 | eos_calculations.status | Estimate, Final, Superseded | chk_eos_calculations_status |
| 27 | final_settlements.status | Draft, PendingApproval, Approved, Paid, Cancelled | chk_final_settlements_status |
| 28 | gosi_filings.status | Draft, Filed, Reconciled, Disputed, Superseded | chk_gosi_filings_status |
| 29 | employee_gosi_registrations.status | Registered, Suspended, Deregistered | chk_egr_status |
| 30 | approval_requests.status | Draft, Pending, Approved, Rejected, Returned, Cancelled, Expired | chk_approval_requests_status |
| 31 | approval_actions.action | Approve, Reject, Return, Comment, Escalate | chk_approval_actions_action |
| 32 | notification_deliveries.status | Queued, Sent, Delivered, Failed, Suppressed, **DeadLettered** | chk_notification_deliveries_status |
| 33 | background_jobs.status | Queued, Leased, Running, Succeeded, Failed, Cancelled | chk_background_jobs_status |
| 34 | background_job_items.status | Pending, Succeeded, Failed, Skipped | chk_background_job_items_status |
| 35 | files.purge_state | Active, PendingPurge, Purged | chk_files_purge_state |
| 36 | retention_policies.disposition | Anonymise, Purge, Keep | chk_retention_policies_disposition |

**Rows 37–44, added by revision 7 (decision 3).** Each of these is a closed set the design relied on and never enumerated, which §7 forbids outright: "if only a release can add a value, it is a CHECK". The implementation reported all eight and proposed the values in the DDL rather than inventing a domain silently; the values below are those proposals, ruled on. They take `ck_` names under §9.0, not `chk_`.

| # | Column | Allowed values | Constraint |
|---|---|---|---|
| 37 | statutory_rules.gosi_branch, payroll_slip_lines.gosi_branch | Annuities, SANED, OccupationalHazards | ck_statutory_rules__gosi_branch, ck_payroll_slip_lines__gosi_branch |
| 38 | statutory_rules.payer, payroll_slip_lines.gosi_payer | Employee, Employer | ck_statutory_rules__payer, ck_payroll_slip_lines__gosi_payer |
| 39 | overtime_requests.ot_type | Normal, WeeklyOff, PublicHoliday, Ramadan | ck_overtime_requests__ot_type |
| 40 | attendance_punches.direction | In, Out | ck_attendance_punches__direction |
| 41 | final_settlements.separation_type | Resignation, Termination, EndOfContract, Retirement, Death, Abscond | ck_final_settlements__separation_type |
| 42 | notification_deliveries.channel | Email, Sms, Push | ck_notification_deliveries__channel |
| 43 | retention_purge_audits.outcome | Applied, Skipped, Failed | ck_retention_purge_audits__outcome |
| 44 | eos_calculations.separation_reason | Art84, Art85, Art87, Art77 | ck_eos_calculations__separation_reason |

**Rows 37 and 38 are the sharpest of the eight, and both instances of each are in scope** — `statutory_rules` *and* `payroll_slip_lines`. They are the GOSI freeze: branch and payer are copied onto every slip line precisely so a slip can be recomputed years later, and `gosi_filings` reports seven totals that are exactly branch × payer (`annuities_employee`, `annuities_employer`, `saned_employee`, `saned_employer`, `occupational_hazards_employer`, plus the wage and amount totals). **`trg_gosi_filing_totals` pivots on those two columns, and §11.2 makes it a `WARNING` rather than a block** — deliberately, because a filed return may legitimately differ from the computed figure and the variance is the point. Put together, an ungoverned branch string is a **silent wrong number**: a typo in any writer produces a value that matches no pivot arm, so the amount contributes to **no** total, the filing is short by exactly that line, and the only signal is a warning that says a variance exists without saying a value was unroutable. That is worse than a hard failure and is why these two are constrained rather than trusted. The value sets are fixed by the scheme, not by a tenant.

**`SANED` keeps its capitals.** It is the Arabic acronym the scheme is published under, not a word, and `trg_gosi_filing_totals` already pivots on that spelling; §9's PascalCase habit does not extend to an acronym that a GOSI circular writes in capitals. `OccupationalHazards` and `Annuities` are ordinary names and stay PascalCase.

**Row 44** is the Labour Law article that decides the award, and `Art84`/`Art85`/`Art87`/`Art77` are written without punctuation so the value is a code rather than prose to be re-parsed. **Row 41**'s `Abscond` is kept as a distinct value from `Termination` because KSA practice treats absconding as its own separation with its own EOS consequence; folding it into `Termination` would lose the reason at the moment it decides the money.

The remaining closed sets — `pay_components.kind`, `payroll_inputs.kind`, `loan_installments.kind`, `final_settlement_lines.kind`, `leave_ledger.entry_type`, `attendance_punches.source`, `gl_journals.source_type`, `document_templates.kind`, `statutory_rules.family`, `statutory_rule_bands.unit`, `audit_logs.category` and `audit_logs.record_kind` — follow the same rule, one named CHECK each, under the `ck_` pattern of §9.0. `statutory_rules.family` was free text in revision 2; it is now constrained to GOSI, EOS, Overtime, Leave, Nitaqat, WPS. Revision 7 names the rest of that list explicitly, so "the remaining closed sets" stops being an unaudited phrase: `loans.kind`, `leave_requests.request_kind`, `overtime_requests.payout`, `gl_journals.source_type`, `retention_purge_audits.disposition`, `nitaqat_grid.band` and `nitaqat_snapshots.band` (the five colour bands), and `approval_workflows.request_type`, `approval_requests.request_type` and `approval_requests.subject_type` — all three of which take the same thirteen values as §9 row 30's workflow set, which is also what §16.1 makes the subject dictionary.

## 10. State machines

Format: **state → legal next states** [who] . Terminal states are marked ✦. Enforcement column says where the rule lives.

### 10.1 payroll_runs — enforcement: DB trigger `trg_payroll_run_transition` (the money path gets a database guarantee, not a service promise)
| From | To | Who |
|---|---|---|
| Draft | Processing [payroll officer], Voided ✦ [payroll officer] | |
| Processing | Processed [system, when `slips + excluded = selected_employee_count`], Draft (on failure, on cancel, or by the stale-heartbeat watchdog) [system] — **the `Processing → Draft` transition releases every `payroll_inputs` row this run holds as `Claimed` back to `Pending` in the same transaction**, which is the only path that unsticks a crashed run | |
| Processed | PendingFinanceReview [system, when the company requires finance review], Approved [approver via approval engine], Draft (recalculate) [payroll officer] | |
| PendingFinanceReview | Approved [finance], Draft [finance rejects] | |
| Approved | Locked [payroll officer], Draft [payroll manager, only while no WPS batch exists] | |
| Locked | Paid [system, on WPS acceptance], Voided ✦ [payroll manager, only while no accepted WPS batch and no posted GL journal exist] | |
| Paid | Completed ✦ [system, when every `wps_lines.bank_status` is terminal] | |
| Completed ✦, Voided ✦ | — | |

Invariants the trigger also enforces: slips and lines are immutable from Approved onward; `attendance_locked_range` is set on Approved and cleared on Void.

**The `Opening` run's transition set, corrected (revision 7 decision 14).** Revision 6 wrote it as "an `Opening` run may only be Draft → Locked ✦ and never Paid", and the trigger author implemented exactly that — which, read literally, also refuses **Draft → Voided**, leaving a wrong go-live import with no escape but deleting the run while it is still Draft. That is the wrong shape for the one run type that exists to be redone: an Opening import is reconciled against a legacy system by a human, it is normal for the first attempt to be wrong, and "delete the row" destroys the evidence of what was imported and why it was rejected — on a table whose whole purpose is provenance (`source_system`, `source_import_job_id`, and per-component `source_record_id` on every line). **Decided: an `Opening` run is Draft → Locked ✦ *or* Draft → Voided ✦, with `void_reason` mandatory, and Locked → Voided ✦ under the same guard as any other run** (no accepted WPS batch, no posted GL journal — neither of which an Opening run can have, so in practice the guard is "no later run has consumed its YTD figures", which the trigger checks by refusing to void an Opening run once a `Regular` run exists for a later period in the same company). It never reaches Processing, Processed, PendingFinanceReview, Approved, Paid or Completed. Deleting a Draft run remains possible and is the right move for a run created by mistake seconds ago; voiding is the right move for one that was imported, reviewed and rejected, and it is the path that leaves a record.

The row above is therefore read with one exception: for `run_type='Opening'`, the legal set is `Draft → {Locked ✦, Voided ✦}` and `Locked → Voided ✦`.

### 10.2 approval_requests — enforcement: `ApprovalWorkflowService.DecideAsync` only, plus CHECK on status
Draft → Pending [requester] · Pending → Approved ✦ / Rejected ✦ / Returned / Cancelled ✦ [approver of the current step; Cancel also by requester] · Returned → Pending [requester resubmits] · Pending → Expired ✦ [system at `due_at` when the workflow says expire]. Multi-step approval stays Pending and advances `current_step`; only the final step may write Approved.

### 10.3 leave_requests — service, inside the approval engine's SaveChanges
Draft → PendingApproval [employee] · PendingApproval → Approved / Rejected ✦ / Cancelled ✦ · Approved → Cancelled ✦ [approver, before start date; reverses the ledger debit] / Taken ✦ [system, day after the end date]. The ledger debit is written on Approved and reversed (never deleted) on Cancel.

### 10.4 timesheets — service `TimesheetApprovalSync` (the live pattern, `Infrastructure/Timesheets/TimesheetApprovalSync.cs`)
Draft → Submitted [employee] · Submitted → Approved / Rejected [approver] · Rejected → Draft [employee] · Approved → Locked ✦ [system, when a payroll run locks the period]. Entries are editable only in Draft and Rejected.

### 10.5 wps_batches — service + frozen-row trigger
Generated → Submitted [payroll officer] / Superseded ✦ [system, when a resubmission is generated] · Submitted → Accepted ✦ / PartiallyRejected / Rejected [bank confirmation import] · PartiallyRejected, Rejected → Superseded ✦ [on resubmission]. Lines freeze at Submitted; only `bank_status` and the confirmation columns may change afterwards, and only by the confirmation importer.

### 10.6 gosi_filings — service
Draft → Filed [payroll officer] · Filed → Reconciled ✦ [finance, when `variance_amount = 0` or a reason is recorded] / Disputed [finance] · Disputed → Reconciled ✦ / Superseded ✦ [a corrected filing revision is created].

### 10.7 employees — service `EmployeeLifecycleService`, CHECK on status
Draft → Invited [HR sends the ESS invitation] / Active [HR activates without ESS] · Invited → Active [employee accepts, or HR activates] · Active → Suspended [HR, disciplinary hold: access off, pay decision explicit] / Offboarded [HR starts separation] · Suspended → Active / Offboarded · Offboarded → Archived ✦ [system, when the final settlement is Paid]. Archived is the only state a retention rule may anonymise. `Suspended` and `Invited` are restored here after revision 2 dropped them (`Models/Employee.cs:5-23`).

### 10.8 tenants — platform service
Active → Suspended [platform admin, non-payment or breach] · Suspended → Active · Active, Suspended → SoftDeleted [platform admin; stamps `soft_deleted_at`, marks the slug] · SoftDeleted → Active [restore inside the window] / Purged ✦ [retention job after `retention_policies`' period; stamps `purged_at`].

### 10.9 loans
PendingApproval → Active [approval] / Rejected ✦ · Active → Settled ✦ [when `outstanding = 0`] / Cancelled ✦ [before the first recovery only]. Installments: Due → Recovered ✦ [payroll or settlement] / Waived ✦ [approver] / Cancelled ✦ [loan cancelled].

### 10.10 payroll_inputs, gl_period_closes, final_settlements, eos_calculations, sessions and invitations
- `payroll_inputs`: Pending → Claimed [the single-statement claim, §F] → Consumed ✦ [on slip-line write] ; Claimed → Pending [void or crash release] ; Pending → Cancelled ✦ [creator; a replacement is inserted with `revision + 1`].
- `gl_period_closes`: Open → Closed [finance] → Reopened [finance with a reason, only while no later period is Closed] → Closed.
- `final_settlements`: Draft → PendingApproval → Approved → Paid ✦ ; any non-Paid state → Cancelled ✦. Lines freeze at Approved. A required clearance item that is not done blocks Draft → PendingApproval.
- `eos_calculations`: Estimate → Final [on settlement approval] ; Final → Superseded ✦ [only by a recalculation that writes a new Final row].
- `auth_sessions`: Active → Revoked ✦ [logout, admin revoke, or refresh-token reuse detection]. `auth_tokens`: Issued → Consumed ✦ / Expired ✦; an Invitation token consumed moves `employees` Invited → Active and `users` Invited → Active in the same transaction.

### 10.11 platform_users and delegated granting authority
- `platform_users`: Active → Suspended [another platform admin] → Active ; Active, Suspended → Disabled ✦ [platform admin; stamps `deleted_at`]. A platform operator is never deleted while any audit row names them.
- `permission_grantor_records`: Active → Revoked ✦ [an admin, or a grantor with `can_sub_delegate` revoking what they conferred] / Expired ✦ [system at `expires_at`]. A revoked or expired authority never silently widens: grants already made stand and are visible in `audit_logs`, but no new grant may be made under it. Sub-delegation may only narrow the parent scope, checked in `AccessManagementService` and asserted by tests.

**No status column is ever written outside these paths.** The ported controllers set status only by calling the owning service; a code-review rule and a test that greps for direct status assignment outside the service layer enforce it, and the two DB triggers (payroll run, frozen rows) make the money path fail loudly if that rule is ever broken.

## 11. Authoritative facts, projections and invariants

The rule of this section: **every fact has exactly one writer.** Where a second copy exists it is named a *projection*, its writer is named, and an invariant says how it is checked.

### 11.1 Approval status vs subject status — one rule for all nine entities
Nine subjects carry both a `status` and an `approval_request_id`: `leave_requests`, `loans`, `overtime_requests`, `timesheets`, `final_settlements`, `payroll_runs`, `employee_assignments`, `employee_salaries`, `attendance_punches`.

- **`approval_requests.status` + `current_step` is authoritative for the decision.** Nothing else may record whether something was approved.
- **The subject's own `status` is a maintained projection of that decision**, written by the approval engine in the *same* `SaveChangesAsync` as the decision — never by the subject's own controller, and never by a read-path reconciler. This is exactly what the live code already does for timesheets and employee change requests (`Infrastructure/Timesheets/TimesheetApprovalSync.cs`, whose own comment explains why a read-path projection is the step that always gets skipped), generalised to all nine.
- The subject status is **richer** than the decision (`Taken`, `Paid`, `Locked`, `Settled` have no approval meaning), which is why it is not simply dropped.
- **Invariant**: for every subject with a non-null `approval_request_id`, if the approval is Approved the subject is not in a pending state, and if the approval is Rejected the subject is Rejected or Cancelled. Enforced by (a) a test per subject type, and (b) a nightly `background_jobs` consistency sweep that raises a `payroll_issues` Block for payroll subjects and a notification to the tenant admin for the rest. Divergence is visible within a day, not a quarter.
- `attendance_punches`, `employee_assignments` and `employee_salaries` have no status of their own: the approval is their only state, and the row exists only once the change is applied.
- `permission_grantor_records` is deliberately **not** in this set. Conferring granting authority is an administrative act recorded with its own reason and expiry, not an approval workflow; routing it through the engine would make every permission grant a two-step inbox item.

### 11.2 Stored totals vs their lines
Lines are always authoritative. Every stored total is a cache written in the same transaction as the lines, and every one has a named invariant enforced by a **deferred constraint trigger** on the parent (checked at COMMIT, so partial writes inside a transaction are legal):

| Total | Invariant | Trigger |
|---|---|---|
| payroll_slips.gross / deductions / net / employee_statutory_total / employer_statutory_total / loan_deductions / arrears_amount | = the signed SUM of `payroll_slip_lines` of the matching kinds | trg_slip_totals |
| payroll_runs.total_* , employee_count | = SUM / COUNT over `payroll_slips` with `inclusion_status='Included'` — **enforced only in the transaction that moves the run `Processing → Processed`**, and not at all while `Processing` (§19.5 commits the run in ~25 chunks; a trigger that fired on every chunk would either fail all of them or pass on a partial total). While a run is `Processing` its stored totals are **undefined and must not be displayed as authoritative**; the UI shows progress, not money | trg_run_totals, conditioned on `NEW.status` |
| wps_batches.total_amount, employee_count | = SUM / COUNT over `wps_lines` | trg_wps_totals |
| loans.outstanding | = `principal + opening_outstanding − SUM(loan_installments WHERE status='Recovered')` | trg_loan_outstanding |
| timesheets.total_minutes | = SUM(`timesheet_entries.minutes`) — an integer sum of integers, which is why the unit rule of §2.K matters here: a `numeric` sum of hours could not be compared exactly with the attendance side | trg_timesheet_minutes |
| final_settlements.gross / deductions / net | = the signed SUM of `final_settlement_lines` | trg_settlement_totals |
| gosi_filings branch × payer totals | = SUM over the `payroll_slip_lines` of the runs in that period and establishment, or a recorded `variance_reason`. **The pivot is `payroll_slip_lines.gosi_branch × gosi_payer`, which is why both must be enumerated (§9 rows 37–38): a value matching no pivot arm contributes to no total, and because this trigger warns rather than blocks, the shortfall is silent** | trg_gosi_filing_totals (WARN, not block: a filed return may legitimately differ, and the variance is the point) |
| timesheet_day_reconciliations.variance_minutes | = `timesheet_minutes − attendance_minutes`. **Both sides are minutes** (§2.K), so the invariant is a subtraction, not the unit conversion `timesheet_hours × 60 − attendance_minutes` that revision 6 wrote — which was unenforceable as an exact CHECK the moment an entry held a fractional hour | CHECK `ck_timesheet_day_reconciliations__variance` |
| payroll_inputs.amount | = `entitled_amount − previously_settled_amount` | CHECK |
| gl_journals | SUM(debit) = SUM(credit) per journal | trg_gl_balance (already stated in §H) |

`loans.outstanding` is the one the audit called sharpest, and it is now the one with the tightest guard: three writers (payroll recovery, early settlement, final settlement), one invariant, checked at every commit.

### 11.3 Employment state
- **`employee_assignments` is authoritative for "was this person employed on date D"**, and for their company, branch, department, designation, grade, manager and cost centre on that date. Every as-of query and every payroll run reads the assignment, never `employees`.
- **`employees.status` is the current-state projection**, maintained by `EmployeeLifecycleService` (§10.7) and used by list screens and access checks. The view `v_employee_current` joins the two so no screen has to choose.
- **`employees.separation_date` is the projection of `final_settlements.last_working_day`**, written when the settlement is approved. The settlement is authoritative.
- Employees are deliberately **not** effective-dated as a whole: name, nationality and national ID changes are recorded by new `employee_documents` rows and in `audit_logs.before/after`, not by a new employee version. Payroll never depends on those fields as-of, because every slip freezes its own identity snapshot.

### 11.4 Contributory wage, at two grains, deliberately
- `payroll_slips.contributory_wage` is **the period's computed contributory wage** for the employee.
- `payroll_slip_lines.applied_contributory_wage` (renamed in revision 3 to end the ambiguity) is **the wage actually applied to that line's GOSI branch**, which differs whenever a branch has its own floor or cap.
- `employee_gosi_registrations.registered_contributory_wage` is **what GOSI holds on file**, a third and genuinely different fact, and the reason `gosi_filings.variance_amount` can be explained.
- Invariant: for branches with no branch-specific bound, `applied_contributory_wage = payroll_slips.contributory_wage`; a test asserts it, and a run-level `payroll_issues` Warn fires when a registered wage differs from the computed wage by more than a tolerance.

### 11.5 The GOSI registration number has one owner
`companies.gosi_registration_no` is the only writable copy, with `UNIQUE (tenant_id, gosi_registration_no)`. `employee_gosi_registrations` and `gosi_filings` reach it by composite FK (§8 rows 138, 140). `payroll_slips.employer_gosi_registration_no` is a deliberate frozen snapshot, like every other slip witness, and is the only copy not bound by FK. A typo can therefore exist in exactly one place, and it cannot propagate into a filing that will not reconcile.

### 11.6 Other single-writer declarations
- **Leave balance**: `leave_ledger` is authoritative; `v_leave_balances` is the only way to read a balance; no table stores one.
- **Statutory values**: `statutory_rules` + `statutory_rule_bands` are the only source; `company_pay_policies` may only *exceed* a floor, checked at write against the rule in force.
- **Attendance lock**: `payroll_runs.attendance_locked_range` is authoritative; `attendance_days.locked_run_id` is the per-row projection written in the same transaction.
- **Plan gating**: `tenants.plan_limits` is authoritative; nothing caches a seat count.
- **Settings**: `tenant_settings.sections` is authoritative per section, with `section_versions` as the optimistic lock (§A).

## 12. Lifecycle, soft delete and retention

### 12.1 Soft delete exists on exactly four tables
`tenants` (`soft_deleted_at`, `purged_at`), `employees` (`deleted_at`, `retention_until`, `privacy_status`, `redacted_at`), `users` (`deleted_at`) and `companies` (`soft_deleted_at`) — plus `platform_users.deleted_at`, which is platform-tier and outside every tenant purge. Everywhere else, delete means delete, and a ported controller that adds an `IsDeleted` filter is wrong. The live codebase carries `IsDeleted`/`DeletedAt` on 74 model properties and `IsActive` on 70; the baseline keeps soft delete only where a legal trace outlives the record, and keeps `is_active` only where it means "usable now" on a catalogue (`pay_components`, `leave_types`, `cost_centers`, `designations`, `grades`, `approval_workflows`, `attendance_devices`).

### 12.2 The retention engine keeps its inputs
Revision 2 deleted the columns the live engine reads, which left `retention_purge_audits` with no writer. Restored, by name, so the three live rules port without redesign:
- `ExpiredEmployeeRecordRule` (`Infrastructure/Retention/Rules/ExpiredEmployeeRecordRule.cs:74`) reads `employees.deleted_at`, `retention_until`, `redacted_at`, `privacy_status` — all four now exist.
- `SoftDeletedTenantRule` (`.../SoftDeletedTenantRule.cs:94`) reads `tenants.soft_deleted_at`, `purged_at`, plus the slug marker and active flag — all now exist (`status='SoftDeleted'` replaces `IsActive=false`).
- `RefreshTokenExpired` reads `auth_sessions.expires_at` and `revoked_at`, plus the grace period, which now comes from `retention_policies` rather than `appsettings`.

### 12.3 Anonymise, purge, or keep — per entity
Erasure of a person is **anonymisation in place**, never a row delete, because payroll rows are statutory evidence: the employee's identifying columns are cleared, `privacy_status='Anonymised'`, `redacted_at` stamped, `employee_number` retained, and every dependent row keeps its FK. Audit rows are redacted in place too — `personal_data` (including the `ip` and `user_agent` it carries), `before` and `after` are nulled and `personal_data_erased_at` stamped, through the single permitted UPDATE shape of the conventions. `envelope_hash` is untouched, and because the row keeps `personal_data_hash`, `before_hash` and `after_hash`, a verifier can **recompute** the envelope hash from the surviving columns plus those digests rather than trusting the stored value. That distinction is the point: comparing a stored hash with itself proves nothing, because anyone able to UPDATE the row could rewrite row, hash and root together — recomputation binds the row to a Merkle root published before the erasure (§14). A blob is purged by flipping `files.purge_state` and deleting the object, leaving the `sha256` behind as evidence the document existed. Every one of these writes a `retention_purge_audits` row with the `rule_key`, the disposition and the correlation id.

### 12.4 The retention matrix, as data
`retention_policies` holds one row per entity, and the retention job reads it — not `appsettings`. Seeded from what the product already publishes and already implements, each row carrying its `source_reference`:

| entity_name | legal_basis | minimum | trigger_event | disposition | owner_role |
|---|---|---|---|---|---|
| employees | KSA labour + PDPL; privacy policy publishes 7 years for payroll (`DataRetentionOptions.cs:61`) | 84 months | Separation | Anonymise | HR |
| payroll_slips, payroll_slip_lines, wps_batches, wps_lines, gosi_filings, gl_journals | statutory payroll retention | 84 months | RecordDate | Keep | Finance |
| eos_calculations, final_settlements + lines | statutory | 84 months | RecordDate | Keep | Finance |
| leave_ledger, leave_requests | employment record | 84 months | RecordDate | Keep | HR |
| attendance_punches, attendance_days, timesheets + entries | Working inputs, not the statutory wage record: the payslip and its lines are that, and they are retained 84 months. 24 months covers the KSA labour-claim limitation window (one year from the end of the relationship) with a margin. `[COUNSEL]` | 24 months | RecordDate | Purge | HR |
| employee_documents + files | PDPL; expiry-driven | 84 months from issue, or the employee rule if sooner | Expiry | Purge | HR |
| tenants | privacy policy: "anonymised within 90 days of account closure" (`DataRetentionOptions.cs:46`) | 3 months | SoftDelete | Purge | Platform |
| platform_users | PDPL: an operator is a data subject too; employment record of the operator's employer | 12 months | SoftDelete | Anonymise | Platform |
| auth_sessions, auth_tokens | replay forensics (`DataRetentionOptions.cs` refresh-token grace) | 1 month after expiry | Expiry | Purge | Platform |
| notifications, notification_deliveries | operational | 12 months | RecordDate | Purge | Platform |
| background_jobs, background_job_items | operational | 6 months | RecordDate | Purge | Platform |
| audit_logs, payroll_audit_logs, retention_purge_audits | evidential | indefinite | — | Keep (personal payload redactable) | Compliance |

A tenant row may lengthen a platform period, never shorten it, enforced by a CHECK against the platform row. The periods above are engineering defaults carrying the product's published commitments; the legal minima remain a `[COUNSEL]` item, exactly as `DataRetentionOptions.cs:61` already flags.

## 13. Types, enumerations, currency, time and calendar

### 13.1 Enumerations
One mechanism, §9. `text` + named CHECK + a C# constants class. Not PostgreSQL enums, because adding a value would then be a migration with a table lock on the largest tables in the product, and this schema expects new pay components, new document types and new job kinds. Not lookup tables, because a closed set with behaviour attached to each value is code, not data — the open catalogues that customers really do edit are already tables.

### 13.2 Currency
SAR is the currency of record. `companies.currency_code char(3) NOT NULL DEFAULT 'SAR'` states it per legal entity, and every money column in the design is denominated in that company's currency. The per-row `currency` columns of revision 2 are removed (there were 26 `Currency` properties in `Models/` and they disagreed). Multi-currency payroll is explicitly **not** in this baseline: adding it means a currency plus a rate-at-freeze on `payroll_slip_lines`, `gl_journal_lines` and `wps_lines`, and it is a product decision, not a schema default.

### 13.3 Bounded text
Identifier and format-bearing columns are length-bounded, so a paste accident cannot become a filing rejection: `tenants.slug` 63, `employee_number` 32, `iban` 34 (with a `CHECK` on the SA IBAN pattern and the mod-97 check enforced in the service), `bank_code` 16, `gosi_registration_no` 20, `gosi_employee_no` 20, `mol_establishment_no` 20, `cr_number` 15, `national_id`/`iqama_no` 10 (`CHECK (col ~ '^[12][0-9]{9}$')`), `border_no` 12, `occupation_code` 16, `nationality_code` 2 (ISO-3166-1 alpha-2), `currency_code` 3, `timezone_id` 64, all status and kind columns 40, `pay_components.code` 32, `letter_number`/`payslip_number`/`batch_number` 40, `correlation_id` uuid. Names, addresses, reasons, comments and messages stay unbounded `text`.

### 13.4 Time, business day and Hijri
- `tenants.timezone_id` (IANA, validated against the runtime's timezone database at write) with an optional `companies.timezone_id` override. It is a typed column, not a JSON key, precisely because it anchors the business day. Today this value lives in `TenantLocalizationSettings.DefaultTimezone` and is read with a UTC fallback (`Infrastructure/Attendance/AttendanceService.cs:48-55`); revision 2 folded that table into a JSON blob and lost the anchor, which this restores.
- **Local date is the anchor.** `attendance_days.work_date`, `overtime_requests.work_date`, `timesheet_entries.work_date`, `leave_requests.start/end`, and the `payroll_runs` (year, month) period are all local dates in the company's timezone. They are computed once, at write, and never re-derived from a UTC instant at read time — which is how "today" drifts between a dashboard and a payroll run.
- `timestamptz` remains for instants (punch time, login, decision, job lease).
- **Hijri is derived, never stored.** `HijriDateService` feeds `KsaWorkingHoursBaselineService` (`AttendanceService.cs:43-45`) for Ramadan hours and statutory working-hours baselines, from the same local date. The only Hijri-shaped data at rest is a Ramadan window in `shifts.rules`, which is a cached derivation refreshed annually by a job, and `public_holidays` rows, which are dated Gregorian with the Hijri occasion named.
- Payroll period boundaries use the company's timezone, so a run for month M covers `[first local day of M, last local day of M]`.

### 13.5 JSON, revisited
Revision 3 removes two JSON columns the audit flagged and keeps the rest with reasons:
- `companies.settings.pay_policy` → **`company_pay_policies`**, relational, effective-dated, with the same gist no-overlap discipline as every other dated table. This was the riskiest blob: the one place contractual money lived.
- `employees.dependents` → **removed** (no consumer; §4.14).
- `leave_types.policy` → **kept, with a boundary**. Entitlement-by-service-year is band-shaped, but `statutory_rule_bands` is platform *reference* data and a leave entitlement is *tenant contractual* data; putting tenant rows in a reference table would break the "no tenant overrides of statutory rules" rule of §E. The statutory floor stays in `statutory_rule_bands`, the tenant's contractual ladder stays in `leave_types.policy` as a schema-validated versioned record, and the engine takes `max(statutory, contractual)`. This was a stated deviation from the audit's suggestion; the owner has since accepted it (decision 6), so it is settled rather than disputed.
- `final_settlements.clearance` → **kept, with a rule**. A clearance checklist is not a multi-step approval: routing it through the approval engine would create one `approval_requests` row per item and turn a checklist into an inbox. It stays a schema-validated array of `{key, required, done_by, done_at}`, and §10.10 makes an undone required item block the settlement leaving Draft.
- Everything else in the audit's JSON table is kept as listed there: configuration, bounded escape hatches, or immutable evidence.

## 14. Auditability: what revision 3 adds

1. **Before and after.** `audit_logs.before jsonb` and `.after jsonb`, inside the hashed envelope, sharing the purge path with `personal_data`. Six per-module logs being merged into `audit_logs` carry old/new values today (`Models/LoansAdvancesBonuses.cs:127-128,219-220,335-336`, `Models/SetupAdmin.cs:197-198`, `Models/Leave.cs:413`, `Models/EmployeeHistory.cs:11-12`); merging them into a log without those columns would have been a regression. `payroll_audit_logs` gets the same pair.
2. **Correlation id.** `correlation_id uuid` on `audit_logs`, `payroll_audit_logs`, `retention_purge_audits`, `background_jobs` and `background_job_items`, set once per HTTP request or job run and propagated. One approval decision that writes an action, a projected status, a ledger row and three notifications is reassembled with one query.
3. **Actor completeness.** `audit_logs.on_behalf_of_user_id`, plus `user_agent` and `hash_algorithm`, which are live columns revision 2 dropped silently. Support impersonation is out of scope today; if it returns, the log can already express it.
4. **What audit does not do.** It is not a second copy of the database: category, entity, entity_id, actor, and the changed fields only. Full-row snapshots stay where they belong — on the frozen artefacts (slips, WPS lines, filings, EOS snapshots).

## 15. Denormalization register

Every denormalized column in the design, with its read-need and its guard. The eight the audit found unjustified are the eight now carrying invariants (§11.2).

| Column group | Table | Read need | Guard |
|---|---|---|---|
| identity snapshot | payroll_slips | The slip must reconstruct without joining live rows | Frozen at Approved |
| gosi_base_policy, full_*, contributory_wage | payroll_slips | A1 reconstruction witnesses | Frozen |
| gross / deductions / net / statutory totals | payroll_slips | Every list, payslip and report reads totals; recomputing from lines per row is the payroll list's whole cost | trg_slip_totals (§11.2) |
| ytd_* (6) | payroll_slips | Payslip YTD block, and the Opening run's carry-forward | trg_run_totals chain plus the Opening-run rule; recomputed on recalculate |
| applied_contributory_wage, gosi_branch, gosi_payer, rules_version | payroll_slip_lines | Per-branch GOSI freeze; a different fact from the slip's (§11.4) | Frozen + test |
| totals, employee_count | payroll_runs | The run list and the approval screen | trg_run_totals |
| SIF snapshot | wps_lines | As filed | Frozen at Submitted |
| employee_count, total_amount | wps_batches | Bank reconciliation header | trg_wps_totals |
| outstanding | loans | Every loan list and the payroll deduction cap | trg_loan_outstanding |
| branch × payer totals | gosi_filings | As filed | Frozen at Filed + WARN trigger |
| saudi_weighted, achieved_pct, employee_breakdown | nitaqat_snapshots | Point-in-time standing and its drill-down | Immutable snapshot |
| rules_snapshot | eos_calculations | Reconstruct a settlement years later | Immutable |
| workflow_snapshot | approval_requests | A workflow edited mid-flight must not change a live request | Immutable |
| current_approver_user_id, current_approver_employee_id | approval_requests | The approvals inbox (H4) filters on "pending and mine"; without them the predicate lives inside `workflow_snapshot` jsonb and no index can serve it | Written only by the approval engine on step advance, return, escalation and delegation; a nightly sweep re-derives them from `workflow_snapshot` + `current_step` and reports any row that disagrees, on the same path as §11.1's projection check |
| total_minutes | timesheets | Timesheet list and payroll input | trg_timesheet_minutes |
| timesheet_minutes, attendance_minutes, variance_minutes | timesheet_day_reconciliations | The variance list is the screen; both sides are needed to explain it, and both are in **minutes** (§2.K) | CHECK on variance; both sides frozen at Accepted |
| scheduled/worked/break/late/early_out/overtime/absent_minutes, first_in, last_out | attendance_days | The daily computed record | Recomputed by the attendance job on any punch change for that day; recompute is blocked once `locked_run_id` is set |
| gross / deductions / net | final_settlements | Settlement list and letter | trg_settlement_totals |
| amount | payroll_inputs | Net payable of a backdated item | CHECK |
| eos_service_start_date, eos_prior_paid_amount | employees | Go-live opening | Stated in §F |
| employer_gosi_registration_no | payroll_slips | Trace a slip to its filing | Frozen; single owner in §11.5 |

## 16. Polymorphic pointers, and how orphans are found

Three `type + id` pairs survive, deliberately, because the alternative is a nullable FK column per subject type.

1. **`approval_requests.subject_type` + `subject_id`.** Allowed `subject_type` values are exactly the `request_type` values of §9 row 30's workflow set, each mapped to one table in a documented dictionary held in code and asserted by a test. A nightly `background_jobs` sweep resolves every open request's subject and reports unresolvable rows; the request is the evidence, so an orphan is reported, never deleted.
2. **`payroll_inputs.source_type` + `source_id`.** Allowed: `Overtime` → overtime_requests, `Leave` → leave_requests, `Attendance` → attendance_days, `Timesheet` → timesheets, `Manual` → NULL, `Opening` → background_jobs, `Bonus` → NULL. The uniqueness key includes both, so the mapping is also what makes consumption idempotent. Same nightly sweep.
3. **`final_settlement_lines.source_type` + `source_id`** and `payroll_slip_lines.source_type`: the one case that needed real integrity — loan recovery — is now a typed FK (`loan_installment_id`), and the remaining values are informational.
4. `audit_logs.entity` + `entity_id` is deliberately unconstrained: an audit row must be able to describe a row that no longer exists.

## 17. Coupling map and porting order

```
Tenancy, Reference ─→ (nothing)              Identity ─→ Tenancy, HR(employees)
Org       ─→ Tenancy, self                   Jobs     ─→ Tenancy, Files
HR        ─→ Org, Identity, Files, Workflow, Leave(employee_documents.leave_request_id)
Leave     ─→ HR, Workflow                    Nitaqat  ─→ Org        GOSI ─→ HR, Org, Files
Attendance─→ HR, Org, Workflow, Payroll (locked_run_id on attendance_days and timesheets)
Payroll   ─→ HR, Org, Reference, Files, Jobs, Workflow, Loans (slip_lines.loan_installment_id)
Loans     ─→ HR, Workflow                    EOS ─→ HR, Org, Payroll, Loans, Workflow
WPS       ─→ Payroll, Org, Files, Jobs       GL  ─→ Org, Files, cost centres
Workflow  ─→ Identity, HR(employees), Org    Audit ─→ nothing (FK-free)
```

Two declarations the audit asked for:
- **Workflow is a hub, and HR → Workflow → HR is a domain cycle at the domain level only** (the back-references are nullable and there is no row-level cycle, §8.4). Consequence, stated rather than discovered: the approval engine cannot be ported after the modules that use it, and no module can be dropped without first orphan-checking its approval requests.
- **Attendance depends on Payroll** through the lock. Consequence: `payroll_runs` must exist before the attendance slice can be built or tested. The porting order below satisfies this by declaration, not by luck.

**Porting order**: (1) tenancy, settings, numbering, files, retention policies; (2) identity and access; (3) org, including cost centres and pay policies; (4) employees and the four dated tables, documents, templates; (5) approval engine; (6) statutory rules and bands, pay components; (7) payroll runs, slips, lines, inputs, issues — with the Opening run; (8) WPS, GOSI registrations and filings, GL; (9) loans; (10) leave; (11) attendance, overtime, timesheets; (12) EOS and settlements; (13) Nitaqat; (14) notifications, audit checkpointer, retention jobs. Each step ships with tenant-isolation tests, its state-machine tests and, from step 7, payroll golden files.

## 18. The integration surface: a stated decision

Every external exchange in scope is a **file plus a job**: the WPS SIF, the GL export, the GOSI return, the bank confirmation import, the device sync and the opening-balance import. `files` holds the artefact and its hash, `background_jobs` holds the attempt, its idempotency key, its lease and its result, and `background_job_items` holds per-row outcomes. There is deliberately **no outbox or integration-message table**, because nothing in the KSA core calls a synchronous external API: Qiwa is out of scope and Mudad is a file today.

If that changes — a Qiwa or Mudad API returns — the decision is already made: an `integration_messages` table (direction, endpoint, request/response file ids, attempt count, status, correlation id) is added then. Under decision 9 there is no budget slot to spend; it must simply name its capability and duplicate nothing.

**What the inbound side needs, and where it lives** (platform audit P1-15). The outbound side is complete; inbound is not, and all three gaps are columns rather than tables:
1. **Nonce and replay window on the device punch API.** `attendance_punches.idempotency_key` dedupes a replay that reuses the sender's key; it does nothing about a captured payload replayed with a *new* key. The device API therefore requires a per-request `nonce` plus a timestamp, rejects anything outside a ±5-minute window, and holds recent nonces per device — in `attendance_devices.recent_nonces jsonb` with the window as its bound. Additionally `(device_id, external_id)` is unique on `attendance_punches`, so the device's own record id can never land twice under two keys.
2. **A sync watermark per device.** `attendance_devices.last_seen_at` is liveness, not a cursor. `attendance_devices.sync_watermark timestamptz` records the highest `occurred_at` accepted from that terminal, so a resumed sync is bounded, a device offline for a week re-imports exactly the gap, and a gap that cannot be closed is detectable rather than silent.
3. **A dead-letter terminal state on `notification_deliveries`.** Its status set (§9 row 32) gains `DeadLettered`, entered after `max_attempts`, surfaced on the operations view and alerted on. Without it a permanently failing channel retries forever and the queue-depth alarm is the only symptom.
**The external-id convention, and what it does *not* replace (revision 7 decision 4).** Revision 6 ended this section by saying the trio `external_system` / `external_id` / `external_synced_at` **replaces** `qiwa_contract_no`, `submission_reference` and `gosi_employee_no`, while §2.D, §2.G and §2.N went on listing all three by name and no section ever added the trio to a single table. The implementation followed §2, flagged the contradiction, and was right to. It is resolved **in favour of §2: the three named columns stay, and the trio is additive.**

The reason is that the three are not opaque foreign keys, which is the only thing the trio is for. Each is a **statutory identifier with its own format, its own bound and its own meaning in a filing**: §13.3 already bounds `gosi_employee_no` and `gosi_registration_no` at 20 characters, `submission_reference` is the acknowledgement a bank or MOL returned for one SIF and is quoted back in a dispute, and `qiwa_contract_no` is the contract number a labour inspector asks for. Collapsing them into a generic `external_id` would lose the type, the length bound and the ability to have two of them on one row at once — `employee_gosi_registrations` would have to choose between holding the GOSI employee number and holding a future integration's key.

So: **a typed statutory identifier is a named column.** The trio is the convention for **opaque keys owned by an external system** — a Qiwa or Mudad record id, a provider message id, a bank's own batch handle — and it is added **per table, when that table first gains an integration**, alongside whatever named identifiers it already holds. No table in the 76 carries the trio today, because no table in the KSA core has an external system that owns a row's identity; the first one to acquire it (`integration_messages`, if the Qiwa or Mudad API returns) adds it then. CONVENTIONS §14's "external-id convention" is corrected to match.

## 19. Platform design (owner-approved, from `AUDIT_PLATFORM.md`)

The owner has approved the platform audit's P0 spine: hand-written SQL DDL as the baseline, Postgres RLS, monthly partitioning of the five high-volume tables, the index inventory, the transaction model, and the operational go-live gates. This section specifies them against the **76-table** design (the audit read revision 2 at 72).

Two of the audit's findings no longer apply, with evidence:
- It names two FK cycles. `payroll_slip_lines ⇄ loan_installments` was real and is broken in §8.4 by deleting the reverse pointers. The second, `eos_calculations.settlement_id ⇄ final_settlements`, **does not exist**: `final_settlements` carries no FK to `eos_calculations` (§8 rows 127-134), so the reference is one-way and `SET NULL`. No constraint in this baseline is deferrable, and none needs to be.
- Its RLS policy shape (b) lists `users` among the nullable-tenant tables. Decision 2 made `users.tenant_id` NOT NULL and moved operators to `platform_users`. **The nullable-tenant set is exactly seven** (revision 7, decision 16): `audit_logs`, `background_jobs`, **`background_job_items`**, `public_holidays`, `retention_policies` under shape (b), and `auth_sessions`, `auth_tokens` under `p_auth`.

### 19.1 The baseline is hand-written SQL DDL

**Why.** An EF `InitialCreate` cannot express what this design depends on: `EXCLUDE USING gist` constraints, partial indexes, `CHECK` sets, partitioned parents, triggers, roles, grants and RLS policies. Worse, EF's snapshot does not know they exist, so a later `migrations add` cannot detect drift in them and the convergence gate would compare two equally-blind EF schemas. The repo already reaches for `migrationBuilder.Sql(` in 28 migrations for this reason.

**Server version floor: PostgreSQL 15, and production is Neon 17.11 (revision 7 decision 9b).** The baseline is not version-neutral and this is the first revision to say so. The binding constraint is the column-list `ON DELETE SET NULL (<column>)` of §8.1, **added in PostgreSQL 15**: every `SET NULL` foreign key in this design is composite on `(tenant_id, x_id)`, so without the column list the delete nulls `tenant_id` and raises `23502` at runtime. There is no workaround that keeps the composite tenant guard, which is the single most important rule in the schema, so **PG 15 is a floor, not a preference**. Two lesser floors sit below it and are satisfied by the same number: `UNIQUE NULLS NOT DISTINCT` (PG 15), used on the nullable-tenant and nullable-company uniques — `retention_policies`, `public_holidays`, `auth_sessions`, `auth_tokens`, `number_sequences`, `document_templates`, `payroll_inputs`, `statutory_rules`; and `ALTER TABLE … DETACH PARTITION … CONCURRENTLY` (PG 14), which §19.3's default-partition recovery depends on. **Production runs Neon 17.11**, comfortably above the floor; CI, the verification containers and every local dev database must be **PG 15 or later**, and a boot assertion records `server_version_num` so a downgrade is loud rather than mysterious. The one thing a *newer* server changes is the dump format — see the drift-gate normalisation below.

**File layout** (`backend-dotnet/Zayra.Api/Db/baseline/`), applied in filename order:

| File | Contents |
|---|---|
| `001_extensions.sql` | `btree_gist`, `pg_trgm`, `pgcrypto`, `pg_stat_statements` — **and `REVOKE ALL ON pg_stat_statements FROM PUBLIC` in the same file**, because the view is world-readable by default and its query texts carry every tenant's literals (§19.2) |
| `002_roles.sql` | the six roles, `REVOKE ALL ON SCHEMA public FROM PUBLIC`, schema `app`, `app.current_tenant()`, `app.is_platform()` (neither `SECURITY DEFINER`; `is_platform` tests `'MEMBER'` — §19.2). **Requires a bootstrap role holding `CREATEROLE` *and* `BYPASSRLS`** — see the deploy preconditions in §19.6 |
| `010_platform.sql`, `011_identity.sql`, `012_org.sql`, `013_employees.sql`, `014_statutory.sql`, `015_payroll.sql`, `016_wps_gl.sql`, `017_leave_attendance.sql`, `018_workflow_audit.sql` | `CREATE TABLE` only — columns, PK and UNIQUE — with explicit types, `NOT NULL`, defaults, and a `COMMENT ON TABLE` carrying `@tier:`, `@owner:` and `@retention:` tags. Written; the names above are the real ones, not revision 6's `0NN_tables_x_y.sql` sketch |
| `020_constraints_a_f.sql` · `021_constraints_g_r.sql` · `022_constraints_cross.sql` | composite `UNIQUE (tenant_id, id)`, all **165** FKs with the `ON DELETE`/`ON UPDATE` of §8 (77 + 79 + 6, plus the 3 added by revision 7 — see the reconciliation at the end of §8.2), the `CHECK` sets of §9, `EXCLUDE USING gist` for every dated table, `numrange` exclusions for bands. Split into three because the FK register crosses the domain files in both directions; `022` exists so that "every register row is declared somewhere" is countable |
| `030_partitions.sql` | the five partitioned parents — **rebuilt, not converted**, because PostgreSQL has no `ALTER TABLE … PARTITION BY` (§19.3) — their initial 15 monthly partitions with **explicitly `+00`-qualified `timestamptz` bounds**, the `DEFAULT` catch-alls (created here and **never** by the maintenance job), and the **9 foreign keys restated verbatim from `021`** because `CREATE TABLE … LIKE` does not copy them. Those nine must be changed in both files in one commit |
| `040_indexes.sql` | the inventory of §19.4, each with `COMMENT ON INDEX` naming the query it serves |
| `050_triggers.sql` | frozen-row guards, append-only guards, state-transition guard on `payroll_runs` (including the `Opening` set of §10.1), deferred total-reconciliation triggers (§11.2), GL balance, attendance-lock guard, `trg_row_stamp`. **A trigger function attached to more than one table must read the row as `to_jsonb(NEW)`** and pull fields out of the jsonb, never as `NEW.<column>` — PL/pgSQL resolves `NEW.<column>` against *every* table the function is attached to, at first call, **including in branches that table can never reach**, so one shared guard that names a column absent from one of its tables fails at runtime on a table whose code path never touches it. This cost real debugging time on the shared frozen-row and append-only guards, which is why it is written here rather than left to be rediscovered |
| `060_policies.sql` | `ENABLE`/`FORCE ROW LEVEL SECURITY`, grants and the **six policy populations** for all 76 tables (62 / 5 / 2 / 4 / 2 / 1 — §19.2), generated from the model, never hand-edited |
| `070_seed_reference.sql` | `permissions`, `statutory_rules` + bands, `nitaqat_grid`, KSA `public_holidays`, `retention_policies`. **Not yet written** — and it is blocked on the two `[COUNSEL]` items, not on engineering |
| `schema.sql` | the canonical `pg_dump --schema-only --no-owner` output, **normalised and committed**, with the pinned `pg_dump` major version recorded beside it. **Not yet written** |
| `schema-normalise.sh` | the normaliser the drift gate runs over **both** sides of its diff, specified below. **Not yet written**, and the drift gate cannot be switched on until it is |

**How the EF model stays in sync.** The baseline ships as *one* EF migration whose `Up` executes the SQL files as embedded resources, so `__EFMigrationsHistory` stays authoritative, `dotnet ef database update` remains the only apply path, and the existing readiness manifest keeps working. The EF model classes are then reverse-checked against the SQL, not the other way round. After the baseline, ordinary EF migrations are used for column and table changes, and `migrationBuilder.Sql` for anything EF cannot model.

**CI gates.** The existing `schema-gates` job is kept **verbatim** — migration-visibility-on-disk, model drift, fresh-deploy-from-zero, every-modelled-column-present, upgrade-from-previous-release-with-data with tenant row counts unchanged, and fresh-vs-upgrade convergence including indexes. Three gates are added, because the existing six cannot see raw-SQL objects:
1. **Schema-drift gate**: `pg_dump --schema-only --no-owner` of a freshly migrated database, **normalised**, byte-diffed against committed `schema.sql`. Any difference fails the build. This is the gate that catches a trigger, policy, partition or EXCLUDE that EF cannot see.
   **"Normalised" has to be specified, or the gate fails every run** (revision 7 decision 10). From **`pg_dump` 16 onward the dump wraps its body in `\restrict <nonce>` … `\unrestrict <nonce>` guard lines, and the nonce is freshly generated on every invocation** — so two dumps of a byte-identical database differ, and a gate that diffs raw output is red from the first green build and is therefore switched off within a week, taking the only drift detection in the baseline with it. The normaliser is a committed script (`schema-normalise.sh`), it runs over **both** sides of the diff, and it does exactly four things, each stated so nobody widens it later: (i) drop any line matching `^\\(un)?restrict\b`; (ii) drop the `-- Dumped from`/`-- Dumped by` version banner; (iii) drop `SET` lines and empty comment lines that carry no schema meaning; (iv) nothing else. In particular it **must not** sort, reformat, or strip anything inside a `CREATE`, `ALTER`, `COMMENT` or `CREATE POLICY` statement — a normaliser that tidies the payload is a normaliser that hides drift, which is the failure this gate exists to prevent. `schema.sql` is committed already normalised, and the pinned `pg_dump` major version is recorded beside it, because a major-version change to the dump format is a deliberate, reviewed re-baseline of `schema.sql` and not a silent diff.
2. **Policy-coverage ratchet**: `SELECT relname FROM pg_class c JOIN pg_namespace n … WHERE relkind IN ('r','p') AND NOT relrowsecurity` must return zero rows. A new table without a policy fails by absence rather than by review.
3. **Comment-coverage gate**: every table and column has a `COMMENT ON`; the data dictionary is generated from `information_schema`, so it cannot drift.

Squash to this single baseline and stop `rm -rf Migrations` in the Dockerfile (audit P1-16), restoring the readiness gate's primary mechanism.

### 19.2 Row-level security

**Roles** (`002_roles.sql`). The application stops connecting as `neondb_owner`, which is superuser-class and holds `BYPASSRLS`:

| Role | Login | Purpose | Notable grants |
|---|---|---|---|
| `kynex_owner` | no | Owns every object | `BYPASSRLS`; the only role with DDL. Cannot log in, so it is reached only through membership |
| `kynex_migrator` | yes | **Applies the baseline and every migration** | No privilege of its own; `GRANT kynex_owner TO kynex_migrator` and nothing else. Credentials exist only in the CI/deploy secret store, never in an application environment |
| `kynex_app` | yes | The API | `NOBYPASSRLS`, DML on tenant tables, `SELECT` only on reference tables |
| `kynex_job` | yes | The seven background services | as `kynex_app`, plus its own policy on `background_jobs`, longer timeouts |
| `kynex_platform` | yes | Platform/tenant-admin surface | may set `app.platform='on'`; full DML on `platform_users`, `tenants` |
| `kynex_ro` | yes | Support and analytics | `SELECT` only; **no** access to the secret columns of §19.2.5 |

`kynex_migrator` exists because nothing else could apply the baseline: `kynex_owner` holds the only DDL privilege and cannot log in. It is deliberately a membership, not a privilege, so revoking one `GRANT` disarms it.

Three CI assertions, and the first is written transitively on purpose:
1. **No login role may *reach* a `BYPASSRLS` role except `kynex_migrator`.** A direct `pg_roles.rolbypassrls` check is not enough — `GRANT kynex_owner TO kynex_app` would defeat it while every row still reads `rolbypassrls = false`. The assertion walks `pg_auth_members` recursively from every `rolcanlogin` role and fails if any closure but `kynex_migrator`'s contains a `BYPASSRLS` role.
2. From `information_schema.role_table_grants`: `kynex_app` holds no write grant on a reference table, no grant at all on `platform_users`, and no DDL.
3. No `SECURITY DEFINER` function is executable by `PUBLIC` or by `kynex_ro`, and every one declares `SET search_path = pg_catalog, app`.

**GUC accessors.** `app.current_tenant()` and `app.is_platform()` are `STABLE PARALLEL SAFE`, use `current_setting(…, true)` so an unset GUC is NULL rather than an error, and are never `SECURITY DEFINER`. `STABLE` matters: the planner folds the call to a constant and still chooses the tenant-leading index, which is what makes RLS nearly free here.

**`app.is_platform()` must check role membership, not just the GUC.** PostgreSQL does not restrict `SET` on a *custom* GUC by role, so any session — including `kynex_app` — can run `SET app.platform='on'` and satisfy shape (b). The function therefore ands the GUC with actual membership, so the GUC can only ever *narrow* an authority the role already holds:
```sql
CREATE FUNCTION app.is_platform() RETURNS boolean LANGUAGE sql STABLE PARALLEL SAFE AS
$$ SELECT coalesce(current_setting('app.platform', true), 'off') = 'on'
        AND pg_has_role(current_user, 'kynex_platform', 'MEMBER') $$;
```
**Two details of that signature are load-bearing, and both fail silently if got wrong (revision 7, decision 21).** First, **it must not be `SECURITY DEFINER`** — inside a definer function `current_user` is the *function's owner*, so `pg_has_role` would answer about `kynex_owner` rather than about the caller, and the membership test would pass for everyone. The function is `STABLE PARALLEL SAFE` and nothing else, which is also what lets the planner fold it. Second, the privilege tested is **`'MEMBER'`, not `'USAGE'`**: `USAGE` is false for a `NOINHERIT` membership, so a platform operator whose grant happens to be `NOINHERIT` would be silently demoted out of the platform tier and would see an empty database instead of an error. Both are asserted in CI, because neither produces a failure that looks like a failure.
`app.current_tenant()` is left self-assertable by design — a session may choose *which* tenant it acts for, and the middleware and interceptor of §19.2 are what bind it to the authenticated JWT.

**Threat model, stated plainly.** GUC-based RLS defends against a *missing predicate* — the forgotten `WHERE tenant_id`, the ambient worker path, the `IgnoreQueryFilters()` that should not have been there. It does **not** defend against an attacker who can execute arbitrary SQL on an authenticated connection, because that attacker can set the tenant GUC to any value. Defence against that is the layer above: authentication, the JWT-bound middleware, parameterised queries, the raw-SQL lint, and least privilege. Anyone reading §19.2 as "RLS makes SQL injection harmless" has read it wrong.

**The policy shapes, and the arithmetic that has to close (revision 7 decision 15).** Revision 6 said "three shapes, and only three" and then counted 66 + 6 + 4 = 76 while also describing `platform_users` as "outside all three" — which makes 77, and quietly hid two tables that fit no shape at all. The real split, as built, is **six populations summing to exactly 76**:

| Shape | Tables | Policy |
|---|---|---|
| **(a)** tenant and company tier | **62** | `USING (tenant_id = app.current_tenant())` and the same `WITH CHECK` |
| **(b)** nullable-tenant tier | **5** | `USING (tenant_id = app.current_tenant() OR (tenant_id IS NULL AND app.is_platform()))` |
| **`p_auth`** — the dual-subject pair | **2** | `auth_sessions`, `auth_tokens`; the hand-written policy below |
| **(c)** reference tier | **4** | `FOR SELECT USING (true)`, protected by withholding the write grant |
| **grant-policed** | **2** | `platform_users`, `data_protection_keys` |
| **self-tenant** | **1** | `tenants` — see below |
| **Total** | **76** | |

- **(a) tenant and company tier — 62 tables.** Company-tier tables need nothing extra: `company_id` is already bound to the tenant by its composite FK.
- **(b) nullable-tenant tier — 5 tables** (`audit_logs`, `background_jobs`, `public_holidays`, **`retention_policies`**, **`background_job_items`**): `background_jobs` additionally carries a `kynex_job` policy so the leased-queue claim (`FOR UPDATE SKIP LOCKED`) can see queued platform work without `is_platform()`.
  **`background_job_items` belongs here and revision 6 left it out (decision 16)** — the same silent failure this section already diagnosed for `retention_policies`, one level down. A platform job (`background_jobs.tenant_id IS NULL`) writes items that inherit its NULL tenant; under shape (a) the worker draining that queue cannot see its own items, so a platform import reports zero rows processed and zero errors while its per-row outcomes sit invisible in the table. Nothing raises. **The nullable-tenant set is therefore seven, not five or six**: the five above plus `auth_sessions` and `auth_tokens` under `p_auth`. §6 Risk 3, CONVENTIONS §3 and the tenancy ratchet all count seven.
- **`tenants` cannot express shape (a) at all.** It has no `tenant_id` — it *is* the tenant — so the predicate is on its own key: `USING (id = app.current_tenant() OR app.is_platform())`, with a **platform-only `WITH CHECK`**, because a tenant session may read its own tenant row and must never create or re-key one. Revision 6 counted it inside shape (a), where its policy would have referenced a column that does not exist.
- **`data_protection_keys` appears in no shape, and that is deliberate.** It is the **deployment-wide** ASP.NET Data Protection key ring, not tenant data and not platform-operator data: every tenant's payloads are protected by the same ring, so there is nothing to filter on and filtering it would break decryption. It is policed by **grant** — `kynex_app` reads and writes it, nothing else holds a grant — plus the `xml` column revoke of the secret list below. RLS is still enabled and forced with a deny-by-default policy, so a role that acquires a stray grant still sees nothing.
  `retention_policies` belongs here, not in shape (a), and the reason is a silent failure rather than a leak: its platform DEFAULT rows carry `tenant_id IS NULL` (§8.2 row 6), so under shape (a) a tenant session would see only its own override rows and the retention engine would run with overrides and **no defaults at all** — under-retaining or skipping entities whose only policy is the platform row. Under shape (b) the defaults are visible to every session, and the CHECK of §12.4 still prevents a tenant row from shortening a platform period.
- **(c) reference tier — 4 tables** (`permissions`, `statutory_rules`, `statutory_rule_bands`, `nitaqat_grid`): protected by **withholding the write grant**, with `FOR SELECT USING (true)`. This is what removes the largest bypass category outright: reading statutory data stops needing a bypass because it stops being filtered.
- **`platform_users` is outside all three.** It has no `tenant_id` at all, so there is nothing to filter on; it is policed by grant — `kynex_platform` only, with `kynex_app` and `kynex_job` holding no grant whatsoever. RLS is still enabled and forced with a `USING (app.is_platform())` policy, so a stolen `kynex_app` connection sees an empty table rather than an operator list.

`ENABLE` **and** `FORCE ROW LEVEL SECURITY` on every table, every partitioned parent **and every partition child** (§19.3 explains why the children cannot be exempted).

**The ratchet asserts the shape, not merely that RLS is on.** A table with RLS enabled and the wrong policy is as wrong as a table with none — shape (a) on `retention_policies` was exactly that defect. The generator emits a manifest of `table → shape` (a / b / c / grant-only), and CI compares it against `pg_policies` + `role_table_grants`, failing when a table's live policy text does not match its declared shape, when a table has no entry, or when an entry has no table. It also covers `relkind IN ('r','p','v')`, so a **view** without a policy story fails by absence too (F1).

**How `auth_sessions` and `auth_tokens` are policed.** These are the only tables carrying both subject kinds, and the only ones where `tenant_id` is nullable in the new design, so they get an explicitly stated policy rather than the generic shape:
```sql
CREATE POLICY p_auth ON auth_sessions
  USING (
        (subject_kind = 'Tenant'   AND tenant_id = app.current_tenant())
     OR (subject_kind = 'Platform' AND tenant_id IS NULL AND app.is_platform())
  ) WITH CHECK (same);
```
plus a table `CHECK` that ties the three columns together — `subject_kind='Tenant'` requires `user_id IS NOT NULL AND platform_user_id IS NULL AND tenant_id IS NOT NULL`, and the mirror for `'Platform'`. A tenant session can therefore never be created without a tenant, and a platform session can never be read by `kynex_app`. `auth_tokens` carries the identical pair. **Login is the one path that must read these before a tenant is known**, and it does so through the named bypass surface below, not through an unpoliced table.

**Views are `security_invoker`, and this is a P0.** A view without `WITH (security_invoker = true)` evaluates its base tables with the privileges and policies of the **view owner**. Every object here is owned by `kynex_owner`, which holds `BYPASSRLS`, so an ordinary view would read *every tenant's* rows and hand them to the caller — and `FORCE ROW LEVEL SECURITY` would not help, because `BYPASSRLS` outranks `FORCE`. Both baseline views (`v_leave_balances`, `v_employee_current`) are therefore created `WITH (security_invoker = true)`, and two CI assertions keep it that way: `pg_class.reloptions` must contain `security_invoker=true` for every view in the baseline, and the policy-coverage ratchet covers `relkind='v'` so a future view fails by absence rather than by review. This matters most on `v_leave_balances`, which §11.6 makes the only sanctioned way to read a balance.

**Fail-closed.** With `app.tenant_id` unset, `app.current_tenant()` is NULL, `tenant_id = NULL` is NULL, no row is visible, and every INSERT raises `42501`. A misconfigured request returns empty or errors; it never returns another tenant's data. That is the property the EF filter cannot offer, because `IgnoreQueryFilters()` today returns *all* tenants and the anonymous path drops the filter entirely (`ZayraDbContext.cs:62-75`).

**Connection and GUC: direct endpoint, connection-scoped.** Accepted as the audit argues, and the deciding evidence is local: `Program.cs:276-281` registers `EnableRetryOnFailure`, and `NpgsqlRetryingExecutionStrategy` throws on any user-initiated `BeginTransaction` outside `strategy.ExecuteAsync` — a trap this repo has hit three times and documented (`Infrastructure/Timesheets/TimesheetService.cs:15`, `IEstablishmentGuard.cs:60`, `TenantDefaultsBackfill.cs:77`). A transaction-scoped GUC on the pooled endpoint would require an explicit transaction around every request and put all ~100 ported controllers on that landmine. Therefore:
1. **The guarantee is the interceptor, not the middleware.** EF opens and closes connections per operation, and Npgsql's `DISCARD ALL` wipes the GUC on every return to the pool, so a GUC set once in middleware is gone by the second query of the same request. The tenant therefore lives in an **`AsyncLocal<TenantContext>` ambient** set by the middleware — not in `HttpContext`, which is absent in hosted services, in retry continuations after a connection reset, and inside `Task.Run` — and a `DbConnectionInterceptor.ConnectionOpenedAsync` re-applies it on **every** open. Background workers set the same ambient per tenant iteration, so one mechanism serves both.
   The extra round trip is then avoidable: a `DbCommandInterceptor` folds `set_config('app.tenant_id', @t, false)` into the first command on a freshly opened connection as one `NpgsqlBatch`, so the steady-state cost is **zero** extra round trips rather than one per request; the standalone `SELECT set_config(...)` remains only as the fallback when a path cannot batch.
   Raw `NpgsqlConnection`, Dapper and `IDbConnection` paths bypass both interceptors entirely, so they are **banned** and a source-scanning test enforces it (extending today's lint, which already misses `SqlQueryRaw`).
2. Npgsql's `DISCARD ALL` on return-to-pool must stay active (`No Reset On Close` must **not** be set), so a GUC never survives into another request. A test asserts it.
3. A `DbConnectionInterceptor.ConnectionOpenedAsync` backstop sets the GUC for any path that opens outside the middleware and **throws** when no tenant is resolvable and the path is not on the allow-list (login, health, platform).
4. Pool pinned: `Maximum Pool Size=<modelled>;Timeout=10;Command Timeout=30;Keepalive=30;Multiplexing=false`. Multiplexing must be off — it interleaves commands across connections and would break connection-scoped session state.
5. Background workers connect as `kynex_job` and set the GUC per tenant iteration; cross-tenant sweeps (audit checkpointer, expiry reminders, retention purge, Nitaqat snapshot) iterate tenant by tenant, which they should anyway for fairness and bounded transactions.

**The four named bypass surfaces** replace the app-level bypasses. The number needs its denominator stated, because three circulate: **346** raw `IgnoreQueryFilters()` sites in `Zayra.Api` production code today (7 of them inside the `ScopedBypass` helper itself), of which the ratchet in `QueryFilterBypassRatchetTests` approves **330** across the 60 production files it pins; tests are excluded from both counts. A repository-wide grep including test projects returns a larger number again. Wherever a bypass count appears in this document it means *production sites in `Zayra.Api`, tests excluded*. Each is one auditable place with its own test:
1. **Login and credential recovery** — a single `SECURITY DEFINER` function, **`app.resolve_login(tenant_slug text, email text)`**, returning only `(user_id, tenant_id, password_hash, status, lockout_end)`, plus a `platform_users` twin keyed on email alone. Three corrections to the audit's sketch, each of which would otherwise be a hole: (i) email is unique **per tenant**, not globally (§B), so a function keyed on email alone is ambiguous the moment two tenants share an address — the tenant is resolved first, from the sign-in host, the workspace slug or an explicit field, and is part of the key; (ii) a `SECURITY DEFINER` function owned by a `BYPASSRLS` role is `EXECUTE`-to-`PUBLIC` by default, so `kynex_ro` could call it and receive `password_hash`, defeating the column revoke of §19.2's secret list — therefore `REVOKE EXECUTE ON FUNCTION app.resolve_login FROM PUBLIC; GRANT EXECUTE TO kynex_app;`; (iii) every `SECURITY DEFINER` function declares `SET search_path = pg_catalog, app` so it cannot be hijacked by a shadowing object. CI asserts (ii) and (iii) for every such function. Nothing else may read `users` or `auth_tokens` before a tenant is known.
   **A fourth correction, and it is the one that matters: the function as specified is a cross-tenant credential read (revision 7 decision 17).** `SECURITY DEFINER` under a `BYPASSRLS` owner, granted to `kynex_app`, keyed on `(tenant_slug, email)` — an already-authenticated tenant A session can simply *call it* with tenant B's slug and receive B's `password_hash`, `status` and `lockout_end`. The corrections above close who may call it; none of them closes **which tenant a caller may ask about**, and the whole point of the function is that it runs before RLS. The specification therefore includes a **confinement guard inside the function**: the call is permitted when either the session has **no tenant bound** — `app.current_tenant()` IS NULL, which is the real login path, before any tenant is known — **or** the requested tenant resolves to the session's own tenant. Anything else raises `42501`. A bound session asking about another tenant is never a legitimate login; it is either a bug or an attack, and it should fail loudly rather than return a hash. The `platform_users` twin, keyed on email alone, is granted only to `kynex_platform` and carries the same "no tenant bound" requirement. This guard is part of the design, not an implementation detail, because a later rewrite of the function that drops it reopens the hole silently — and a CI assertion checks that every `SECURITY DEFINER` function reading credential columns names `app.current_tenant()` in its body.

   **The parameters are `text`, not `citext` — revision 7 decision 8c.** Revision 6 wrote this signature with `citext` while §19.1's extension list omitted `citext` and §2.B named the column `users.normalized_email`, which announces that normalisation is the application's job and already done by the time a value reaches the database. Case-insensitivity is therefore obtained where it already was: the application lower-cases and trims on write, the column stores the normalised form, and the unique `(tenant_id, normalized_email)` enforces it. `citext` is rejected rather than deferred, for stated reasons — it would make the unique index depend on an extension's collation behaviour, it is a per-column type change on two tables plus every C# mapping, and it would leave the column named `normalized_email` while no longer requiring normalisation, which is the worst of both. `tenants.slug` is likewise plain `text`, bounded at 63 and lower-cased on write. If a future audit finds a path that reaches these columns without normalising, the fix is that path, not the type.
2. **Platform administration** — `kynex_platform` with `app.platform='on'`, set only by the platform controller pipeline, and an `audit_logs` row per request.
3. **Background jobs and seeders** — `kynex_job` with an explicit per-tenant GUC loop. Declared, never ambient, which closes today's fail-open worker path.
4. **Migrations and backfills** — `kynex_owner`, `BYPASSRLS`, no LOGIN.

**Secret columns** (audit P1-17): `users.password_hash`, `users.mfa_secret_encrypted`, `users.mfa_recovery_hashes`, `platform_users.*` equivalents, `data_protection_keys.xml`, `attendance_devices.api_key_hash` and `auth_tokens.token_hash` are revoked from `kynex_ro` by column privilege, so support and analytics access cannot read credentials. **Revision 7 adds three the list had missed (decision 20):** `auth_sessions.refresh_token_hash` and `auth_sessions.previous_token_hash` — the same class of bearer secret as the listed `auth_tokens.token_hash`, and the pair that refresh-token **reuse detection** turns on, so a reader of them can both impersonate a device and know whether the theft has been noticed — and `auth_sessions.push_token`, which is a third-party credential: it addresses a real device through APNs or FCM, and anyone holding it can send notifications that appear to come from the product.

**`pg_stat_statements` is revoked from `PUBLIC`, and this is a tenant-isolation control, not a tidiness one (revision 7, decision 20).** The extension's view is **world-readable by default**, and its `query` texts carry the literals of every statement the server has run — employee numbers, IBANs, national IDs, email addresses, slugs, from every tenant. No RLS policy covers it, because it is not a table in this schema; `kynex_ro` reading it is a cross-tenant read that every other control in §19.2 would have prevented. `REVOKE ALL ON pg_stat_statements FROM PUBLIC` and grant it to the operator role only. The same reasoning applies to any future extension view that records query text, and the policy-coverage ratchet is widened to fail on a world-readable relation in `pg_catalog` or an extension schema that was not explicitly allow-listed.

**Retained on top, not replaced**: the interface-driven EF query filters, the boot assertions, the write guards and the source-scanning ratchets stay. RLS is the second layer, not a substitute — but the bypass-count ratchet is retired in favour of the policy-coverage ratchet, because counting 330 call sites stops being the measurement once there are four surfaces.

### 19.3 Partitioning

Five tables are declaratively RANGE-partitioned **by month from the baseline**, because each reaches 6–9 M rows per 5,000-employee tenant per year and a large table cannot be partitioned later without a full rewrite:

| Table | Partition key | Online window | Then |
|---|---|---|---|
| `attendance_punches` | `occurred_at` | 24 months | detach, export to `files`, drop |
| `audit_logs` | `created_at` | per `retention_policies` (indefinite) | detach to cold storage; PDPL erasure nulls `personal_data`/`before`/`after` in place, never drops a partition |
| `background_job_items` | `created_at` | 90 days | detach and drop |
| `attendance_days` | `work_date` | **24 months** | detach, export, drop |
| `timesheet_entries` | `work_date` | **24 months** | detach, export, drop |

**The retention period governs; the partition schedule follows it.** Revision 5 said 36 months here and 24 in §12.4, and the failure mode of that disagreement is silent **over**-retention, which is a PDPL breach rather than the safe direction. Decided: **24 months** for `attendance_punches`, `attendance_days`, `timesheets` and `timesheet_entries`, matching §12.4. The legal basis is that the *statutory wage record* is the payslip and its lines, retained 84 months; attendance and timesheets are the working inputs behind it, whose result is frozen on the slip, and 24 months comfortably covers the KSA labour-claim limitation window (one year from the end of the relationship) with a margin. `[COUNSEL]` confirms the period; if counsel rules that attendance must itself be retained as wage evidence, then **§12.4's period changes and §19.3 follows it** — never the reverse, and never only one of them.

**Consequence 0 — a table cannot be converted to partitioned, so the baseline rebuilds the parent (revision 7, decision 18).** Revision 6's file layout said `030_partitions.sql` creates "the five partitioned parents", and the table files were written as ordinary tables on the assumption that 030 would convert them. **There is no `ALTER TABLE … PARTITION BY` in PostgreSQL, through PG 17.** The baseline therefore rebuilds each of the five: `ALTER TABLE … RENAME`, `CREATE TABLE … (LIKE … INCLUDING ALL) PARTITION BY RANGE (<key>)`, `DROP` the original. This is free at baseline time because the tables are empty, and it is stated here so nobody later reads the rebuild as a workaround for something simpler.

**It has one cost that has to be managed rather than admired: `LIKE` does not copy foreign keys.** `INCLUDING ALL` brings defaults, constraints, indexes and comments; **FKs are not included, in either direction**. So `030_partitions.sql` restates, verbatim, the **8 outbound** FKs of the five partitioned tables and the **1 inbound** FK that targets one of them (`timesheet_day_reconciliations` → `attendance_days`, consequence 2 below) — nine constraints that also exist in `021`. That duplication is a real maintenance hazard and the rule is explicit: **changing the `ON DELETE`, the column list or the target of any of those nine means changing both `021` and `030` in the same commit.** The drift gate of §19.1 is what catches a divergence — a `pg_dump` of the built database shows the constraint as `030` left it, and the byte-diff against `schema.sql` fails if only one file moved. Whoever edits one of the nine adds a comment in both naming the other.

**Consequence 1 — every unique constraint on a partitioned table must contain the partition key.** The blanket `idempotency_key UNIQUE` convention of §Conventions collides with this on exactly the tables that need partitioning, so it is restated here, correctly:

| Was | Is |
|---|---|
| `attendance_punches.idempotency_key UNIQUE` | **`UNIQUE (tenant_id, idempotency_key, occurred_at)`**, and the device-ingest writer becomes one `INSERT … ON CONFLICT (tenant_id, idempotency_key, occurred_at) DO NOTHING` per batch instead of a per-row `AnyAsync` (H9). Because the key is derived from `(device serial, employee, occurred_at)`, a duplicate always carries the same `occurred_at`, so uniqueness is not weakened in practice |
| `audit_logs UNIQUE (chain_key, seq)` | **`UNIQUE (chain_key, seq, created_at)`**. Cross-partition uniqueness of `seq` is instead guaranteed at allocation — `seq` comes from a per-tenant Postgres sequence, which never reuses a value — and the checkpointer already walks contiguous `seq` ranges, so a gap or a duplicate is detected there and raises. Stated plainly because it is a real, if small, weakening |
| `attendance_days UNIQUE (tenant_id, employee_id, work_date)` | unchanged — it already contains the partition key |
| primary keys on all five | become `(id, <partition key>)`, and the composite tenant key becomes `UNIQUE (tenant_id, id, <partition key>)` |

**Consequence 2 — the one composite FK into a partitioned table changes shape.** `timesheet_day_reconciliations.attendance_day_id → attendance_days` is the only FK targeting a partitioned table. It becomes `(tenant_id, attendance_day_id, work_date) REFERENCES attendance_days (tenant_id, id, work_date)`; the reconciliation row already carries `work_date`, so no new column is needed. §8 row 126 is updated accordingly. No other partitioned table is an FK target, which is why the other four can be partitioned freely.

**Consequence 3 — partition children are a security surface, not a detail.** Policies on the parent govern access *through* the parent, but **privileges are not inherited for direct access**: each child has its own ACL, and a child with a grant and no policy is an unfiltered copy of millions of rows that any `kynex_app` session can read by naming it. `PartitionMaintenance` is also the only process that creates tables in production, so one careless `GRANT` in that job is the whole leak. Four rules, all enforced:
1. The job uses **one fixed template** and takes no parameters but the month: `CREATE TABLE … PARTITION OF … FOR VALUES FROM … TO …;` then `ALTER TABLE … ENABLE ROW LEVEL SECURITY; ALTER TABLE … FORCE ROW LEVEL SECURITY;` and **no `GRANT` of any kind**. Access to the data is through the parent, which already carries the grants and the policies.
2. The coverage ratchet **stops excluding `relispartition = true`**. Revision 5's exclusion was the hole. It now asserts, for every child: RLS enabled and forced, and **zero** direct grants to any login role.
3. A `DEFAULT` partition exists on every parent so a missing month degrades rather than erroring, and the alert fires on the **first row** landing in it — not on a threshold. A populated default is a countdown, because the recovery is expensive.
4. **Recovery from a populated `DEFAULT` partition**, written down because it is the classic partitioned outage and the expensive half is not the insert: detach the default (`ALTER TABLE … DETACH PARTITION … CONCURRENTLY`), create the missing month, `INSERT … SELECT` the rows out of the detached table in batches, then attach the month. Attaching a partition over a populated default takes `ACCESS EXCLUSIVE` and a **full validation scan** of the incoming table, which on `attendance_punches` is minutes of blocked writes — so the work is scheduled, not improvised. Revision 5's "a slow insert rather than an outage" was true of the insert and false of the recovery; this states both.

**Consequence 4 — two traps in the bounds themselves, both of which route rows into `DEFAULT` without saying so (revision 7, decision 19).**

1. **A `timestamptz` partition bound is resolved against the server's `TimeZone` at DDL time, not at insert time.** `FOR VALUES FROM ('2026-03-01') TO ('2026-04-01')` means different instants depending on what `TimeZone` the session creating it happened to have, so two partitions written under different settings leave a **gap or an overlap of a few hours** at every month boundary. An overlap is rejected and is therefore harmless; a gap is not — the rows in it land in `DEFAULT`, silently, at the hour of every month change. **Every bound on a `timestamptz`-keyed partition carries an explicit offset: `'2026-03-01 00:00:00+00'`.** This applies to `attendance_punches` (`occurred_at`), `audit_logs` and `background_job_items` (`created_at`); the two `work_date` parents are `date`-keyed and are not affected, which is itself a reason the local-date rule of §13.4 is worth its cost.
2. **`DEFAULT` partitions are created outside the maintenance template, deliberately.** They are created once, with the parent, by `030_partitions.sql`, and `PartitionMaintenance` has no code path that creates one. The reason is the alert: a job that *can* create a `DEFAULT` can also silently repair a missing month by making one, and the first-row alert of rule 3 above — the thing that turns a missing month from an outage into a scheduled job — would then never fire. The maintenance job creates months and nothing else, so a missing month is always visible.

`PartitionMaintenance` pre-creates three months ahead and alerts below two months of headroom.

### 19.4 Index inventory

Five rules, stated once, as was done for effective dating:
1. Every index on a tenant table **leads with `tenant_id`** (company-tier: `tenant_id, company_id`). Under RLS the policy predicate is `tenant_id = app.current_tenant()`, so a tenant-leading index is what makes RLS nearly free rather than a sequential scan.
2. Every FK gets a covering index on its referencing columns **unless it appears in the exclusions below**, with the reason stated in `COMMENT ON`.
3. Every filter + sort + page endpoint declares its index in the same PR as the endpoint.
4. Partial indexes for small hot subsets: `WHERE status='Pending'`, `read_at IS NULL`, `effective_to IS NULL`, `purge_state='PendingPurge'`, `status IN ('Queued','Leased','Running')`.
5. No index ships without an `EXPLAIN (ANALYZE, BUFFERS)` against a seeded 5,000-employee tenant, recorded in the PR.

| Query | Index |
|---|---|
| **H1** employee list/search (`EmployeesController.cs:86`) — five OR'd leading-wildcard `LIKE` | `pg_trgm` GIN on `(employee_number ‖ name_en ‖ name_ar ‖ work_email)` partial `WHERE status <> 'Archived'`; btree `(tenant_id, status, name_en)` for the unsearched default page. Keyset paging replaces OFFSET in the port |
| **H2** dashboard summary (`DashboardController.cs:250`) | `attendance_days (tenant_id, work_date, status)`; FK cover `(tenant_id, employee_id)`; keep the 60 s cache |
| **H3** dashboard KPIs (`:1104,:1150`) | `employee_documents (tenant_id, employee_id, doc_type)` and `(tenant_id, expiry_date)`; partial `leave_requests (tenant_id, status)`. The correlated per-employee `COUNT(DISTINCT lower(doc_type))` is rewritten as one `GROUP BY` in the port — no index fixes it |
| **H4** approvals inbox (`ApprovalWorkflowService.cs:94`) | Needs the two denormalised approver columns added to `approval_requests` above — revision 5 named columns the design did not have, so the inbox had no index at all. With them: partial `approval_requests (tenant_id, current_approver_user_id, due_at, created_at DESC) WHERE status='Pending'` and its `current_approver_employee_id` twin; overdue: partial `(tenant_id, due_at) WHERE status='Pending' AND due_at IS NOT NULL`. Sort changes to `due_at ASC NULLS LAST` — the `COALESCE` is not sargable |
| **H5** approvals N+1 (`:174,:476,:483`) | Not an index problem: memoise the caller's employee id per request and replace the in-memory manager walk with a recursive CTE over `employee_assignments.manager_employee_id`. Supporting index `(tenant_id, manager_employee_id) WHERE effective_to IS NULL` |
| **H6** payroll run (`PayrollController.cs:1379`) | `employee_salaries (tenant_id, employee_id, effective_from DESC)` and the same on `employee_assignments`, beside their gist EXCLUDEs; the designed partial on `payroll_inputs`; partial `loans (tenant_id, employee_id) WHERE status='Active' AND outstanding > 0`; `overtime_requests (tenant_id, work_date, status)`. The 48 serial preloads are batched in the port |
| **H7** payroll YTD (`:1811`) | `payroll_runs (tenant_id, company_id, year, month, status)`; `payroll_slips (tenant_id, run_id, employee_id)`. **The design's YTD columns make this a single-row read from the prior slip** — the employee filter is added and the 60,000-row scan disappears |
| **H8** attendance sweep (`AttendanceService.cs:947`) | per-partition `attendance_punches (tenant_id, employee_id, occurred_at)`; `attendance_days`' unique serves its side; gist `leave_requests (tenant_id, employee_id, daterange(start_date, end_date, '[]')) WHERE status='Approved'`; `public_holidays (calendar_code, date)` loaded once per sweep. Rewritten set-based in the port |
| **H9** device ingest (`:484`) | the partitioned unique above, used as an `ON CONFLICT` target; `attendance_devices (tenant_id, serial)` |
| **H10** ESS dashboard (`EmployeeSelfServiceController.cs:66`) | `payroll_slips (tenant_id, employee_id, run_id DESC)`; partial `notifications (tenant_id, user_id, created_at DESC) WHERE read_at IS NULL`; `employee_documents (tenant_id, employee_id, expiry_date)`. Batched to ≤4 round trips; the `LIKE '%Pending%'` becomes a status set |
| **H11** unpaged lists | No index: `pageSize` is clamped server-side on every list endpoint, and ESS payslip and attendance history are paged |
| **H12** `v_leave_balances` | `leave_ledger (tenant_id, employee_id, leave_type_id) INCLUDE (days)` so the SUM is index-only. If p95 exceeds 50 ms at 270 k rows/tenant, the fallback is a trigger-maintained balance row — decided by measurement, not in advance |
| Workflow and lifecycle | partial `payroll_issues (tenant_id, run_id) WHERE severity='Block'` and `(tenant_id, employee_id) WHERE run_id IS NULL`; partial `background_jobs (status, next_attempt_at) WHERE status IN ('Queued','Leased','Running')`; partial `notification_deliveries (status, next_attempt_at) WHERE status IN ('Queued','Failed')`; partial `files (purge_state) WHERE purge_state='PendingPurge'`; `employee_documents (tenant_id, expiry_date) WHERE status='Active'`; `wps_lines (tenant_id, batch_id, bank_status)`; `gosi_filings (tenant_id, company_id, year, month)`; `audit_logs (chain_key, seq)` per partition and `(tenant_id, correlation_id)` |

**FKs deliberately left unindexed.** The justification is narrower than revision 5 claimed, because §8.3 *does* hard-delete `users` during a tenant purge, so "users are never removed" was wrong:
- FKs to immutable reference data: `statutory_rule_id`, `statutory_rule_band_id`, `permission_code`, `nitaqat_grid`, `document_templates.template_id`. These parents are never deleted outside a migration, and a migration can build an index first.
- `RESTRICT` actor columns whose parent delete is already blocked by the restriction itself and which no query filters on: `payroll_issues.override_by`, `gosi_filings.filed_by`, `gl_period_closes.closed_by` and `.reopened_by`, `permission_grantor_records.granted_by_user_id` and `.revoked_by`. A purge that must remove such a user resolves the business row first, by definition of `RESTRICT`.
- Self-FKs on small hierarchies: `departments.parent_id`, `cost_centers.parent_id` — hundreds of rows at most, where a sequential scan is cheaper than the index.
- FKs whose child is tiny and always read whole: `role_permissions.permission_code` (counted in the reference class above), `tenant_settings.tenant_id` (one row per tenant).
**Now indexed, correcting revision 5:**
- **Every `SET NULL` child of `users`** — `files.uploaded_by`, `approval_actions.on_behalf_of_user_id`, `wps_batches.generated_by`, **`user_roles.granted_by`** — because a `SET NULL` cascade during a tenant purge scans the whole child table per deleted user, and §8.3 *does* hard-delete `users`, so the "parents are never removed" argument does not apply to them.
- `employee_documents.supersedes_id` and `payroll_runs.parent_run_id`, which revision 5 filed as "read only from the child side" and which are in fact read **parent-side**: renewal chains are listed from the superseded document, and correction runs are listed under their parent run.

**`granted_by` was on both lists, and revision 7 resolves it by splitting the name.** Revisions 5 and 6 listed a bare `granted_by` among the unindexed `RESTRICT` actor columns while the correction immediately below said every `SET NULL` child of `users` is indexed — and both sentences were about different columns that happen to share a stem. **`user_roles.granted_by` is `SET NULL` (§8.2 row 17) and is therefore indexed**; **`permission_grantor_records.granted_by_user_id` is `RESTRICT` (row 157) and is therefore not**. Neither list may say `granted_by` unqualified again; every entry in both lists above now names its table.

**The arithmetic.** The exclusion classes above contain **13** foreign keys. With 165 in the register (§8.2), **152 carry a covering index and 13 deliberately do not** — each with its reason in a `COMMENT ON`, as rule 2 requires. Revision 6's "roughly 125 of the 160" matched neither its own exclusion list nor the register; the numbers here are countable from `pg_constraint` and `pg_index`, which is the point of stating them.

### 19.5 Transaction and concurrency model

**Isolation per workflow.**
| Workflow | Level | Why |
|---|---|---|
| Ordinary CRUD, list, ESS reads | `READ COMMITTED` | Default; nothing read-compute-writes |
| Payroll run calculation | `READ COMMITTED` + chunking + idempotent re-entry | Volume, not anomaly, is the risk |
| Leave debit, encashment, comp-off — **every read-compute-INSERT** | **`pg_advisory_xact_lock(tenant, employee, leave_type)` is the rule, not an alternative.** `REPEATABLE READ` does **not** prevent this anomaly: two sessions each SUM the ledger, each see enough balance, each INSERT a *different* row, and both commit — write skew, which only `SERIALIZABLE` detects. Either take the advisory lock (chosen: cheap, local, and the pattern the team already uses at `TenantSessionSecurity.cs:61-64`) or run the unit `SERIALIZABLE` with the `40001` retry below. Not `REPEATABLE READ` |
| Loan recovery and `outstanding` | `READ COMMITTED` + the deferred invariant trigger | **Safe for a different reason, and it is worth being precise about why**: recovery *UPDATEs the single `loans` row*, so the row lock serialises concurrent recoveries and there is no write skew. It is not an instance of the pattern above, and it does not need the advisory lock |
| `payroll_inputs` claim | `READ COMMITTED`, single statement | The `UPDATE … RETURNING` needs nothing stronger |
| WPS generation, GL posting, settlement approval | `REPEATABLE READ` + idempotency key | Must not double-file or double-post |
| Audit checkpointing, retention purge | `READ COMMITTED`, per-tenant, bounded batches | Long transactions are the hazard |

**Three consequences of chunking, stated because they bite.** (i) `trg_run_totals` is conditioned on the status transition, as §11.2 now says, and run totals are undefined while `Processing`. (ii) **A crashed run stays `Processing`, not `Voided`**, so the claim-release rule of §F — which keys on a voided run — would never fire and the run's `Claimed` `payroll_inputs` would be stranded, invisible to the next run. Therefore: a resumed run re-reads *its own* `Claimed` rows (`claimed_by_run_id = this run`) and continues with them, and §10.1's `Processing → Draft` transition releases them back to `Pending` in the same transaction. A watchdog moves a run whose heartbeat is stale to `Draft`, which is what makes the release happen. (iii) The exit condition is **`slips + explicitly excluded = selected_employee_count`**, not `slips = selection`: an employee who becomes ineligible mid-run (separated, salary held, a new Block) is written as a slip with `inclusion_status='ExcludedByBlock'` plus a `payroll_issues` row naming why, so one ineligible employee cannot wedge the run in `Processing` forever.

**The chunked-batch rule.** A 5,000-employee run writes ~5,000 slips and ~75,000 lines; one transaction would hold locks and WAL for minutes on a 512 MB instance, and per-employee transactions would make the run non-atomic. Therefore: the run's **state machine** (§10.1) is the unit of atomicity, not the SQL transaction. Employees are processed in batches of 200 in their own transactions; the writer is `INSERT … ON CONFLICT (tenant_id, run_id, employee_id) DO NOTHING`, which the designed unique already supports; a crashed run resumes from the first employee without a slip; and the run only leaves `Processing` when the slip count matches the selection. The same rule applies to the attendance sweep, the WPS line build and the GL line build.

**Retry.** `EnableRetryOnFailure` covers transient connection faults, not serialization failures or deadlocks. Financial paths wrap their unit in `strategy.ExecuteAsync` and retry `40001` (serialization) and `40P01` (deadlock) up to 3 times with jittered backoff. The retry is provably safe because every financial writer carries an idempotency key or an `ON CONFLICT` target.

**Optimistic concurrency.** `UseXminAsConcurrencyToken()` globally — no column, no migration, supported by Npgsql — so two HR users editing one employee cannot silently last-write-wins. Exempt: append-only tables (`leave_ledger`, the three audit tables), frozen artefacts after lock, and `tenant_settings`, which has its own per-section version.

**Request-level idempotency.** Every financial POST — approve run, generate WPS file, post GL journal, approve settlement, file GOSI return — requires an `Idempotency-Key` header, stored with the resulting entity id, so a double-click or a retry-on-504 returns the first result rather than filing twice.

**Timeouts** (none are set anywhere today):
```sql
ALTER ROLE kynex_app      SET statement_timeout='30s', lock_timeout='5s',  idle_in_transaction_session_timeout='15s';
ALTER ROLE kynex_job      SET statement_timeout='10min', lock_timeout='30s', idle_in_transaction_session_timeout='2min';
ALTER ROLE kynex_platform SET statement_timeout='60s', lock_timeout='10s', idle_in_transaction_session_timeout='30s';
ALTER ROLE kynex_ro       SET statement_timeout='120s', lock_timeout='5s',  idle_in_transaction_session_timeout='60s';
```
Plus `log_lock_waits=on` and `deadlock_timeout=1s` at the database level.

**One repo fix carried in**: `AccessManagementService.cs:1917` uses session-scoped `pg_advisory_lock` on a pooled connection, so a fault before unlock leaks the lock into the next request's connection. It becomes `pg_advisory_xact_lock`, matching the other three call sites.

### 19.6 Operational readiness and recoverability

**Signals, each with an owner and a threshold** (nothing on this list exists today):

| Signal | Source | Alert |
|---|---|---|
| Slow queries | `pg_stat_statements`, top 20 by total exec time, hourly | statement p95 > 1 s |
| Lock waits, deadlocks | `log_lock_waits`, `deadlock_timeout=1s`, `pg_locks` sampling | any deadlock; lock wait > 5 s |
| Connection saturation | `pg_stat_activity` vs `max_connections` | > 70 % |
| Idle in transaction | `pg_stat_activity` | any session > 60 s |
| Failed jobs and deliveries | `background_jobs status='Failed'`, `notification_deliveries` at max attempts | any; queue depth > N |
| Audit checkpoint lag | `audit_logs` max `created_at` vs last checkpoint `covers_created_to` | > 10 min (risk 4) |
| Audit tamper | recomputed root ≠ published root for a checkpointed range, or a row missing from one | **any, immediately**. Sequence gaps are deliberately *not* alerted: `seq` is non-transactional, so rollbacks make gaps routine |
| **RLS violations** | Postgres `42501` counted in app logs | **any, immediately** — a leak attempt or a bug |
| Storage growth | Neon storage metric, `pg_total_relation_size` of the five partitioned tables | > 80 % of budget; partition headroom < 2 months |
| Backup health | Neon PITR window; last restore-drill date | window < 7 days; drill older than 90 days |
| Migration parity | `/health/ready` `pendingMigrations` | ≠ 0 for > 5 min |
| **End-to-end request latency** | OTel server spans per endpoint | p95 > 800 ms, p99 > 2 s, per hot endpoint. This is the signal a statement-level alarm cannot give: a 70 ms cross-region tax repeated 23 times in one ESS load is 1.6 s of user-visible latency while every individual statement stays far under the 1 s slow-query threshold |

Plus OpenTelemetry with Npgsql instrumentation, the `correlation_id` of §14 propagated from the request through `audit_logs` and `background_jobs`, `/health/ready` behind authentication with a thin anonymous variant (it currently leaks tenant counts and raw Npgsql messages), and the `SLO_AND_ALERT_CATALOG.md` that `Program.cs:812` already cites but which does not exist.

**Open owner decision carried here from the platform audit (P2-19): region co-location.** The app runs in Oregon and the database in `us-east-1`, ~70 ms apart, and that tax is paid on every round trip of every request. Moving Render to Virginia or Neon to `us-west-2` is the cheapest performance work available and removes it wholesale; it is an owner call because it is a migration with a short window, not an engineering preference. The latency SLO above exists so the cost stays visible until the decision is made.

**Recoverability.** Targets: **RPO ≤ 5 min, RTO ≤ 2 h** for logical loss; **RPO = 0, RTO ≤ 15 min** for a bad migration, which is a Neon branch reset rather than a restore. Neon PITR is configured and monitored (≥ 7 days pilot, 30 production). Every deploy that migrates first creates a restore point (`neon branches create --name premigrate-<sha>`), so rollback is a connection-string change; the branch is deleted after a successful soak. Every migration PR rehearses on a branch cut from production and reports apply time and lock duration.

**Go-live gates.** No go-live without all of these:
1. A **restore drill** to a branch at T−1 h, with six recorded proofs: (a) a closed-period payroll run reproduces byte-identical slips; (b) the `payroll_audit_logs` hash chain verifies end to end; (c) an `audit_logs` checkpoint Merkle root verifies; (d) a `files` blob referenced by a restored row still exists in B2 and its `sha256` matches; (e) a DataProtection-encrypted MFA secret still decrypts after the restore; (f) `background_jobs` leases are re-claimable. Achieved RPO and RTO recorded.
2. The **authorization test suite** of the audit's §4 — nine classes, table-driven so a new table without a policy fails by absence — run against **real Postgres**, not InMemory, which cannot execute a policy.
3. A **load test** on a seeded 5,000-employee tenant: concurrent payroll run, ESS traffic and attendance sweep, with p50/p95/p99 per hot endpoint recorded as the baseline.
4. `PROD_DATABASE_URL` and `RENDER_DEPLOY_HOOK_URL` moved from repository secrets into the `production` environment, so the approval gate becomes a control rather than, in the repo's own words, "an intent".
5. The **connection model written down**: `pool size × instances + workers ≤ Neon max_connections`, with the seven `BackgroundService`s moved off the web instance.
6. B2 object-storage versioning and lifecycle configured, with blob recovery proven in the same drill — a DB restore that recovers rows pointing at deleted payslip blobs is not a recovery.

**Deploy preconditions, which are not gates but will stop the deploy dead if they are not met (revision 7, decision 22).** §19.2 describes the role graph the baseline creates and never says what is needed to *create* it:

1. **The bootstrap role that runs `002_roles.sql` must itself hold `CREATEROLE` **and** `BYPASSRLS`.** Postgres will not let a role grant an attribute it does not hold, so a bootstrap role with `CREATEROLE` alone creates `kynex_owner` successfully and then fails on `ALTER ROLE kynex_owner BYPASSRLS` — after four roles already exist, which is the worst place to fail. On Neon this is the project's own owner role, used once, from the deploy pipeline, and never again.
2. **The application is handed `kynex_app` and nothing else.** The cluster's own superuser — `neondb_owner` on Neon — sits **outside** this design: it holds `BYPASSRLS` by virtue of being superuser-class, so an application still connecting as it has every policy in §19.2 written, enabled, forced, and doing nothing whatsoever. This is the single change that makes the rest of §19.2 real, and it is a connection-string change, so it is also the single easiest thing to forget and the hardest to notice, because everything works. The boot assertion that records `current_user` and fails on anything but `kynex_app` (and `kynex_job` in the workers) is the control; the recursive `BYPASSRLS` reachability test of §19.2 is the backstop.

**Scope note.** §19 is a specification, and it is now partly built. Files `001` through `060` — extensions, roles, the nine domain table files, the three constraint files, partitions, indexes, triggers and policies — are written and apply clean from an empty database, with the behavioural and in-database assertions passing. **`070_seed_reference.sql`, `schema.sql` and `schema-normalise.sh` are not written**, and the first is blocked on the two `[COUNSEL]` items rather than on engineering. The operational signals of §19.6, the three new CI gates and the go-live drill are specified and unbuilt. The audit's own estimate for the RLS half is ≈3–4 engineer-weeks, of which ~1.5 are genuinely additive because the ~100-controller port happens regardless.

## 20. What the implementation had to decide, and what this revision decides instead

Writing 76 tables as DDL forces a decision on every column the design left implicit. The implementers took those decisions **in the file, with the reasoning written down and the gap reported**, which is the right behaviour and the reason this section can be short. Each is ruled on below: **adopted** means the design now says what the SQL says, **adopted with a change** means the intent is right and the spelling moves, **rejected** means the SQL changes.

### 20.1 Six implied unique constraints (all adopted)

Each is a uniqueness the design assumed in prose and never declared. A key column with no unique is not a key, and in every one of these six the absence had a named consequence, which is why all six are adopted rather than argued.

| Constraint | Why the design needed it and did not say so |
|---|---|
| `roles` `UNIQUE (tenant_id, code)` | §2.B calls `code` a key column. Two roles with one code cannot be resolved by code, and provisioning seeds roles by code |
| `grades` `UNIQUE (tenant_id, code)` | Same, §2.C |
| `role_permissions` `UNIQUE (tenant_id, role_id, permission_code)` | A join row has no meaning twice; without it, revoking a permission leaves a duplicate granting it |
| `statutory_rule_bands` `UNIQUE (statutory_rule_id, band_key)` | §2.E names `band_key`; a key that repeats inside one rule cannot be resolved by key, and `payroll_slip_lines.statutory_rule_band_id` freezes the band that was resolved |
| `document_templates` `UNIQUE NULLS NOT DISTINCT (tenant_id, company_id, kind, code, version)` | §2.D says `version` is "immutable once used". If "version 3 of OFFER_LETTER" is not one row, the `template_version` snapshots on `payroll_slips` and `employee_documents` do not resolve to one body, and a reprint years later is not reproducible |
| `public_holidays` `UNIQUE NULLS NOT DISTINCT (tenant_id, calendar_code, holiday_date)` | §19.4 H8 loads `(calendar_code, date)` once per attendance sweep; a duplicated holiday double-counts a day for every employee in the tenant |

### 20.2 Columns introduced by the implementation

| Column | Ruling |
|---|---|
| `employees.work_email` | **Adopted** (§2.D). §19.4 H1 already indexed it; revision 6 simply never defined it |
| `timesheets.timesheet_number` | **Adopted** (§2.K). §2.A already lists `'timesheet_no'` as a `number_sequences` scope key, so the counter existed and the column it feeds did not |
| `audit_logs.chain_key` | **Adopted** (§Q). §19.3 already made it half of `UNIQUE (chain_key, seq, created_at)` |
| `loan_installments.installment_number` | **Adopted** (§2.I) with `UNIQUE (tenant_id, loan_id, installment_number)`. A schedule whose rows have no ordinal cannot be displayed in order, regenerated idempotently, or matched to a recovery |
| `background_jobs.progress_current` + `progress_total` | **Adopted** (§2.R), replacing a single `progress`. "How far through what" is the question an observable job exists to answer; `progress_total` is nullable because a streaming import does not know its denominator until it ends |
| `attendance_days.scheduled_minutes`, `break_minutes`, `absent_minutes`, `computed_at` | **Adopted** (§2.K). Absence and lateness are payroll inputs and cannot be derived from `worked_minutes` alone; `computed_at` is what makes a stale recompute detectable |
| `public_holidays.holiday_date` (was `date`), `employee_contracts.end_date` (was `end`) | **Adopted** (§2.C, §2.D). Both original spellings are Postgres reserved words, which CONVENTIONS §1 forbids and quoting only defers |
| `timesheet_day_reconciliations.explanation` | **Adopted** (§2.K). The status set contains `Explained`, and a status that records an explanation with nowhere to put it is a status nobody can use |
| `leave_types.is_paid`, `leave_requests.return_date` / `contact_during_leave`, and the `submitted_at` / `decided_at` / `cancelled_at` lifecycle stamps on `leave_requests`, `timesheets` and `overtime_requests` | **Adopted.** All are ordinary §10 lifecycle columns that §2's summary rows compressed away; none is a new fact and none duplicates one |
| `payroll_slips.employee_name` / `department_name` / `designation_name` (§2.F wrote "name, department, designation") | **Adopted with the `_name` suffix**, so a frozen witness cannot be misread as a live join |

### 20.3 Rejected

| Proposal | Ruling |
|---|---|
| `citext` for `users.normalized_email`, `platform_users.email`, `tenants.slug` | **Rejected** — §19.2 bypass surface 1. Normalisation is the application's job and the column name says so |
| A `company_id` on `payroll_slips`, `payroll_slip_lines`, `payroll_issues` to justify the tier letter | **Rejected** — the letter is corrected to **T** instead (§2.F) |
| Replacing `qiwa_contract_no`, `submission_reference`, `gosi_employee_no` with the external-id trio | **Rejected** — §18. The three are typed statutory identifiers, not opaque foreign keys |

### 20.4 Still open after this revision

Nothing in the twelve is left to the owner. The items that remain open are the ones that were already open and are not schema decisions: the two `[COUNSEL]` items (the GOSI ladder and wage bounds; the retention periods), and the owner's region call (§5, "Still open"). One item is **flagged rather than open**: the four period-column renames of §2.F's period rule are mechanical follow-up work on the written SQL, listed there, and must land before `schema.sql` is committed.
