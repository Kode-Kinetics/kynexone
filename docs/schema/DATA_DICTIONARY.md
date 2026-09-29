# Data dictionary — 76 tables

Derived strictly from `../../TARGET_SCHEMA.md` §2, **pinned to revision 7** (the approved design of record). **No column here is invented.** Where the target
schema names a column but not its type, the type is inferred from `CONVENTIONS.md` §4 and marked `~`.

**How to read an entry**

- **Tier** — `P` platform (no tenant) · `R` reference (baseline-seeded, read-only to tenants) · `T` tenant · `C` company.
- **Keys** — only the columns that carry meaning. The full column list lives in the EF model; this
  is the map, not the territory.
- **Rel** — `col → table` with cardinality. **`TARGET_SCHEMA.md` §8.2 is the authoritative per-FK
  register** (delete rule, cardinality, required/optional for every FK); a mark here summarises it.
  The rules: RESTRICT by default for anything financial, filed, audited or historical; CASCADE only
  where the child is a *part* of its parent, and every CASCADE parent has a `BEFORE DELETE` guard
  so it can only fire on a draft; SET NULL only where the fact survives the loss;
  `ON UPDATE RESTRICT` everywhere.
- **Enum values** — where a value set is shown, `TARGET_SCHEMA.md` §9 is authoritative and §10 holds
  the legal transitions between them.
- **Indexes** — shown only where §19.4 names one for a specific hot query. The five index *rules*
  (tenant-leading, FK-covering, declared-with-the-endpoint, partial for hot subsets, EXPLAIN in the
  PR) are in `CONVENTIONS.md` §14 and are not repeated per table.
- **Partitioned** — five tables are monthly RANGE-partitioned from the baseline. Each says so, and
  each carries a composite PK `(id, <partition key>)` and `UNIQUE (tenant_id, id, <partition key>)`.
- **Row stamping** — every mutable table carries `created_at`/`created_by`/`updated_at`/`updated_by`
  from one `trg_row_stamp` trigger; the exempt list is in `CONVENTIONS.md` §9. Not repeated per table.
- **Retention** — class letter. Defined in `OWNERSHIP_AND_RETENTION.md` §2.
  `R` reference · `C` configuration · `P` personal · `O` operational · `S` statutory · `E` evidence · `T` transient.
- Every `T`/`C` table also has `tenant_id uuid NOT NULL`, `UNIQUE (tenant_id, id)` and composite FKs.
  Not repeated per table.

---

## A. Platform, tenancy and shared infrastructure

### `tenants`
- **Purpose** — one customer account, with its plan and the modules it may use.
- **Tier** P · **Domain** Platform
- **Keys** — `slug text UNIQUE`, `name`, `status` (§9), **`timezone_id`** (IANA, validated, default `Asia/Riyadh`), `plan_code`, `plan_limits jsonb` (max_employees, max_users, max_companies), `plan_expires_at ~timestamptz`, `enabled_modules text[]`; **lifecycle: `soft_deleted_at`, `purged_at`**
- **Rel** — none outbound. Parent of every `T` table (1:N).
- **Lifecycle** — created by platform admin provisioning; `status` suspends; **soft-deleted** by `soft_deleted_at` and purged by `purged_at`, both read by `Infrastructure/Retention/Rules/SoftDeletedTenantRule.cs:94`. One of only four soft-delete tables.
- **Retention** `C`
- **RLS** — its own population, **not shape (a)**. `tenants` has no `tenant_id` — it *is* the tenant — so the predicate is on its own key: `USING (id = app.current_tenant() OR app.is_platform())`, with a **platform-only `WITH CHECK`**. A tenant session may read its own row and must never create or re-key one. Revision 6 counted it inside shape (a), where the policy would have referenced a column that does not exist.
- **Note** — replaces `Tenants` + `TenantSubscriptions` + `TenantFeatureFlags`; invoices, payments and pricing are out of scope. `timezone_id` anchors every business day in the product.

### `tenant_settings`
- **Purpose** — the **one** settings row per tenant, written one section at a time.
- **Tier** T · **Domain** Platform
- **Keys** — `id uuid` **PK** with `UNIQUE (tenant_id)` carrying the 1:1 (revision 7: `tenant_id` is *not* a natural PK — `CONVENTIONS.md` §2 forbids one and §3 needs `UNIQUE (tenant_id, id)`), `sections jsonb` keyed `general, hr, payroll, localization, branding, security, lookups, leave, loans, overtime, notification_templates, document_requirements, help_texts`; `section_versions jsonb` (one version per key)
- **Rel** — `tenant_id → tenants` (1:1, RESTRICT)
- **Lifecycle** — created at tenant provisioning with seeded defaults; written by `jsonb_set` on a single key guarded by that key's version. **A whole-row PUT is not an API the service exposes.**
- **Retention** `C`
- **Note** — replaces 13+ per-feature settings tables. No counter may ever live here (`CONVENTIONS.md` §8).

### `number_sequences`
- **Purpose** — allocates every human-facing number in the product.
- **Tier** T · **Domain** Platform
- **Keys** — `company_id` NULL = tenant-wide, `scope_key` (`employee_no`, `letter_no`, `run_no`, `wps_batch_no`, `settlement_no`, `gosi_filing_no`, `timesheet_no`), `prefix`, `pattern`, `next_value bigint`, `reset_period` (`None`/`Year`/`Month`), `period_key`; `UNIQUE (tenant_id, company_id, scope_key, period_key)`
- **Rel** — `company_id → companies` (N:1, RESTRICT)
- **Lifecycle** — seeded at tenant/company provisioning; allocated by `UPDATE … RETURNING` in one statement; never deleted.
- **Retention** `C`

### `files`
- **Purpose** — every stored blob plus the purge state PDPL erasure needs.
- **Tier** T · **Domain** Platform
- **Keys** — `storage_key UNIQUE`, `bucket`, `mime`, `size_bytes`, `sha256`, `purpose` (`EmployeeDocument`/`Payslip`/`WpsSif`/`GlExport`/`BankConfirmation`/`Import`/`LetterPdf`), `uploaded_by`, `retention_until ~date`, `purge_state` (`Active`/`PendingPurge`/`Purged`), `purged_at`
- **Rel** — `uploaded_by → users` (N:1, SET NULL). Referenced by `employee_documents`, `payroll_slips`, `wps_batches`, `gl_journals`, `gosi_filings`, `background_jobs`.
- **Lifecycle** — created on upload or generation; the purge job flips `purge_state`, deletes the blob, writes `retention_purge_audits`; **the row survives**, keeping `sha256` as evidence the document existed.
- **Retention** `P` (blob) / `E` (row)

### `retention_policies`
- **Purpose** — **the PDPL retention matrix as data**: one row per entity saying how long it is kept, what starts the clock, and what happens at the end.
- **Tier** R/T · **Domain** Platform
- **Keys** — `entity_name`, `legal_basis`, `minimum_retention_months`, `trigger_event` (`SoftDelete`/`Separation`/`RecordDate`/`Expiry`), `disposition` (`Anonymise`/`Purge`/`Keep`), `owner_role`, `rule_key` (matches `RetentionRuleKeys`), `source_reference`, `effective_from`/`effective_to`; `tenant_id` NULL = platform default
- **Rel** — `tenant_id → tenants` (nullable, N:1, CASCADE); matched FK-free from `retention_purge_audits.rule_key`.
- **RLS** — **policy shape (b), not (a)** (§19.2). Its platform DEFAULT rows carry `tenant_id IS NULL`; under shape (a) a tenant session would see only its own *override* rows, so **the retention engine would run with overrides and no defaults at all** — under-retaining or skipping every entity whose only policy is the platform row. A silent failure, not a leak, which is why the ratchet now asserts the *shape* per table and not merely that RLS is on.
- **Lifecycle** — platform rows **seeded with real values** by the baseline (§12.4: employees 84 months / Separation / Anonymise; payroll, WPS, GOSI, GL, EOS 84 / RecordDate / Keep; attendance and timesheets 24 / Purge; tenants 3 from SoftDelete / Purge; auth 1 month after expiry; notifications 12; jobs 6; audit indefinite / Keep). A tenant row **may only lengthen** a platform period, enforced by a CHECK against the platform row.
- **Retention** `R`
- **Note** — new in revision 3. Replaces the day counts hardcoded in `Infrastructure/Retention/DataRetentionOptions.cs:46,61`. **This table, not a document, is the source of truth for retention** — `OWNERSHIP_AND_RETENTION.md` §3 is a rendering of it. The seeded periods carry the product's *published* commitments (7 years payroll, 90 days after account closure); the **legal minima remain `[COUNSEL]`**.

### `platform_users`
- **Purpose** — **platform operators, in their own table**, so no nullable-tenant row ever sits in a client-data table.
- **Tier** P · **Domain** Platform
- **Keys** — `email` (globally unique), `full_name`, `password_hash`, `status` (§9), `platform_role`, `mfa_enabled`, `mfa_secret_encrypted`, `mfa_recovery_hashes`, `failed_login_count`, `lockout_end`, `last_login_at`, `deleted_at`
- **Rel** — none outbound. Shares `auth_sessions` and `auth_tokens` with `users` through an XOR CHECK on the subject columns.
- **Lifecycle** — created by an existing platform operator; `status` disables; `deleted_at` soft-deletes. **Platform-tier, so outside every tenant purge.**
- **Retention** `P` at platform scope
- **Note** — owner decision Q2 (§5). `users.user_kind='Platform'` with a nullable `tenant_id` was rejected because it puts a tenant-less row in the client-data tier. Auth *infrastructure* is still shared, deliberately: duplicating rotation, reuse detection and lockout would breach the no-duplicate-tables principle.

### `data_protection_keys`
- **Purpose** — the ASP.NET Data Protection key ring that encrypts MFA secrets and tokens.
- **Tier** P · **Domain** Platform
- **Keys** — `friendly_name`, `xml`
- **Rel** — none.
- **Lifecycle** — written by the framework; rotated by the framework; **never touched by application code or a migration**.
- **RLS** — **in no policy shape at all, deliberately.** The key ring is **deployment-wide**: every tenant's payloads are protected by the same ring, so there is nothing to filter on and filtering it would break decryption. It is policed by **grant** — `kynex_app` only — plus the `xml` column revoke from `kynex_ro`. RLS is still enabled and forced with a deny-by-default policy, so a stray grant still sees nothing. Revision 6 described `platform_users` as "outside all three shapes" and never mentioned this table, which is how 66+6+4 came to be called 76.
- **Retention** `C` — deleting a key makes every secret encrypted under it unrecoverable.

---

## B. Identity and access

### `users`
- **Purpose** — every **tenant** login: tenant staff and employees on ESS.
- **Tier** T · **Domain** Identity
- **Keys** — **`tenant_id NOT NULL`** (platform operators live in `platform_users`), `normalized_email` (unique per tenant), `password_hash`, `status` (§9), `lockout_end`, `failed_login_count`, `mfa_enabled`, `mfa_secret_encrypted`, `mfa_recovery_hashes jsonb`, `employee_id` (nullable, unique), `notification_prefs jsonb`, **`deleted_at`**
- **Rel** — `(tenant_id, employee_id) → employees` (1:1, SET NULL); parent of `user_roles`, `auth_sessions`, `auth_tokens`, `notifications`.
- **Lifecycle** — created by admin invitation or ESS enrolment; `status` disables; `deleted_at` soft-deletes; PDPL purge anonymises rather than removes, because audit rows reference the id.
- **Retention** `P`
- **Note** — revision 4 (§5 decision 2) made `tenant_id` **NOT NULL** and moved platform operators to `platform_users`. **`TARGET_SCHEMA.md` §6 Risk 3 still lists `users` as nullable-tenant and is stale** — gap 1 below.

### `roles`
- **Purpose** — a named bundle of permissions, seeded per tenant at provisioning.
- **Tier** T · **Domain** Identity
- **Keys** — `code` with `UNIQUE (tenant_id, code)`, `name`, `is_system`
- **Rel** — `tenant_id → tenants`; parent of `role_permissions`, `user_roles`.
- **Lifecycle** — seeded at provisioning; tenant admins add custom roles; `is_system` roles cannot be deleted.
- **Retention** `C`

### `permissions`
- **Purpose** — the platform-wide catalogue of permission keys, including `access.grant.*`.
- **Tier** R · **Domain** Identity
- **Keys** — `code UNIQUE`, `module`, `description`
- **Rel** — referenced by `role_permissions.permission_code` (1:N, RESTRICT).
- **Lifecycle** — delivered by the baseline seeder; changed only by a release that adds the feature the permission gates.
- **Retention** `R`

### `role_permissions`
- **Purpose** — which permission codes a role carries.
- **Tier** T · **Domain** Identity
- **Keys** — `role_id`, `permission_code`, `UNIQUE (tenant_id, role_id, permission_code)` — a join row has no meaning twice
- **Rel** — `role_id → roles` (N:1, CASCADE), `permission_code → permissions.code` (N:1, RESTRICT)
- **Lifecycle** — seeded per tenant; edited in role administration; deleted with the role.
- **Retention** `C`

### `user_roles`
- **Purpose** — a role grant, narrowed by **data scope** and bounded by **expiry**.
- **Tier** T · **Domain** Identity
- **Keys** — `user_id`, `role_id`, `scope_company_id`, `scope_branch_id`, `scope_department_id` (all NULL = whole tenant), `granted_by`, `granted_at`, `expires_at`
- **Rel** — `user_id → users` (N:1, CASCADE), `role_id → roles` (N:1, RESTRICT), `scope_* → companies`/`branches`/`departments` (N:1, RESTRICT)
- **Lifecycle** — created by an admin holding `access.grant.*`; expires by `expires_at` (no job needed — the query filters it); revoked by delete, with the event in `audit_logs`.
- **Retention** `C`
- **Note** — replaces `UserEntityAccesses` and `UserPermissionOverrides`; a per-user grant becomes a **custom role** with a scope and an expiry (§5 decision 5). `PermissionGrantorRecords` does **not** fold in — it keeps its own table (decision F5).

### `permission_grantor_records`
- **Purpose** — **delegated granting authority**: who may grant what, whether they may sub-delegate, until when, and why.
- **Tier** T · **Domain** Identity
- **Keys** — `grantor_user_id`, `permission_scope` (`all`, a module prefix, or an explicit key list), `can_sub_delegate`, `granted_by_user_id`, `expires_at`, `reason`, `is_active`, `revoked_at`/`revoked_by`
- **Rel** — `grantor_user_id`, `granted_by_user_id`, `revoked_by → users` (N:1, RESTRICT)
- **Lifecycle** — created by someone who already holds granting authority; expires by `expires_at`; revoked by `revoked_at`/`revoked_by`; **read before any grant is allowed** (`AccessManagementService.cs:418,1337,1373,1392`).
- **Retention** `E` — it explains why a grant was permitted.
- **Note** — decision F5. Revisions 2–3 called this a merge into `user_roles`; **it is not.** `user_roles.granted_by` records who made *one* grant; it cannot express who *may* grant, over what scope, with what sub-delegation right, until when. Live at `Controllers/AccessController.cs:464,472,484`.

### `auth_sessions`
- **Purpose** — **one row per device**, not one per login.
- **Tier** T/P · **Domain** Identity
- **Keys** — `user_id`, `device_id` with `UNIQUE (tenant_id, user_id, device_id)`, `refresh_token_hash` (rotated in place), `previous_token_hash` (reuse detection), `expires_at`, `revoked_at`, `push_token`, `push_platform`, `last_seen_at`, `ip`, `user_agent`
- **Rel** — `user_id → users` **or** a `platform_users` subject, resolved by an **XOR CHECK** (N:1, CASCADE). `tenant_id` is nullable here and on `auth_tokens` — the only two in the **client-data** tier (decision 2), and two of the **seven** nullable-tenant tables overall. These two are policed by `p_auth`, the one hand-written policy, rather than by shape (b).
- **Lifecycle** — created on first sign-in from a device; the token hash rotates in place on refresh; ends by `revoked_at` (sign-out) or expiry; hard-deleted after expiry by the cleanup job.
- **Retention** `T`
- **Secret columns** — `refresh_token_hash`, `previous_token_hash` and `push_token` are revoked from `kynex_ro` by column privilege (added in revision 7). The two hashes are bearer secrets and are **the pair reuse detection turns on**, so a reader of both can impersonate a device *and* know whether the theft has been noticed; `push_token` is a third-party credential that addresses a real handset through APNs or FCM.
- **Note** — the push token lives on the device row, so refresh rotation never loses a push registration and revoking one session cannot silence another device.

### `auth_tokens`
- **Purpose** — single-use tokens for password reset, MFA challenge, invitation and e-mail confirmation.
- **Tier** T/P · **Domain** Identity
- **Keys** — `user_id`, `purpose` (`PasswordReset`/`MfaChallenge`/`Invitation`/`EmailConfirm`), `token_hash UNIQUE`, `expires_at`, `consumed_at`, `attempts`
- **Rel** — `user_id → users` **or** a `platform_users` subject, via the same XOR CHECK (N:1, CASCADE).
- **Lifecycle** — issued by the flow that needs it; ended by `consumed_at` or expiry; hard-deleted after expiry.
- **Retention** `T`

---

## C. Organisation

### `companies`
- **Purpose** — a legal entity and its MOL establishment; the unit payroll, WPS and GOSI all file on.
- **Tier** T · **Domain** Organisation
- **Keys** — `name_en`/`name_ar`, `cr_number`, `mol_establishment_no`, **`gosi_registration_no` with `UNIQUE (tenant_id, gosi_registration_no)`** so registrations and filings can FK to it, `nitaqat_activity_code`, `wps_bank_code`, `wps_mol_id`, **`currency_code char(3)`**, **`timezone_id`** (optional override), **`go_live_year smallint` + `go_live_month smallint`** (replacing the untyped `go_live_period`), **`soft_deleted_at`**, `settings jsonb` (**non-money overrides only — pay policy has moved out**)
- **Rel** — `tenant_id → tenants` (N:1, RESTRICT); parent of every `C`-tier table.
- **Lifecycle** — created in setup; `go_live_year`/`go_live_month` set at cutover; **soft-deleted only** (`soft_deleted_at`) — never hard-deleted while payroll history exists.
- **Retention** `C` (but `S` in effect — it is the counterparty on every statutory filing)
- **Note** — above-floor contractual enhancements moved to the `company_pay_policies` **table** in revision 3, out of `settings jsonb`, so they get the same `EXCLUDE … gist` no-overlap discipline as every other dated structure. The engine still applies `max(statutory, contractual)`.

### `company_pay_policies`
- **Purpose** — **contractual, above-floor pay parameters**, effective-dated and out of JSON.
- **Tier** C · **Domain** Organisation
- **Keys** — `company_id`, `policy_key` (from the seeded allow-list that `ClientRateDefinition` used to hold), `pay_component_code` (nullable), `rate numeric(9,6)`, `amount numeric(18,2)`, **`value_json jsonb` bounded by `CHECK (pg_column_size(value_json) <= 8192)`** plus a schema validated on write, `effective_from`/`effective_to`, `approved_by`, `source_reference`
- **Rel** — `company_id → companies` (N:1, RESTRICT), `pay_component_code → pay_components.code` (N:1, RESTRICT)
- **Lifecycle** — created when a contractual enhancement is agreed; ended by the next row's start; never deleted, because it is the basis of a paid amount.
- **Retention** `S`
- **Constraint** — the standard `EXCLUDE … gist` no-overlap per company and `policy_key`.
- **Note** — new in revision 3, replacing `CompanyRatePolicies` and absorbing `CompanyStatutoryOverrides` (above-floor only). **Statutory rows themselves are still not overridable** (owner question Q6).

### `branches`
- **Purpose** — a physical site, with its geofence and holiday calendar.
- **Tier** C · **Domain** Organisation
- **Keys** — `name`, `city`, `address`, `lat`/`lng`, `geofence_radius_m`, `holiday_calendar_code`
- **Rel** — `company_id → companies` (N:1, RESTRICT); referenced by `attendance_devices`, `employee_assignments`, `user_roles.scope_branch_id`.
- **Lifecycle** — created in setup; `is_active`-style retirement via assignment history, not deletion.
- **Retention** `C`
- **Note** — absorbs the live `Location.GeofenceRadiusMeters`. The unused `AttendanceGeofences` model is dropped.

### `departments`
- **Purpose** — an org unit in a hierarchy, carrying its default cost centre.
- **Tier** C · **Domain** Organisation
- **Keys** — `name`, `parent_id`, `cost_center_id` (**optional**)
- **Rel** — `company_id → companies`, `parent_id → departments` (self, N:1, RESTRICT), `cost_center_id → cost_centers` (N:1, SET NULL — optional)
- **Lifecycle** — created in setup; restructured by editing `parent_id`; historical placement lives in `employee_assignments`, not here.
- **Retention** `C`

### `cost_centers`
- **Purpose** — the costing dimension GL, timesheets and payroll all key on.
- **Tier** C · **Domain** Organisation
- **Keys** — `code` (unique per company), `name`, `parent_id`, `gl_segment`, `is_active`
- **Rel** — `company_id → companies`, `parent_id → cost_centers` (self); referenced by `departments`, `employee_assignments`, `timesheet_entries`, `payroll_inputs`, `payroll_slip_lines`, `gl_journal_lines`, `gl_mappings`.
- **Lifecycle** — created in setup; retired by `is_active=false`, never deleted (GL history points at it).
- **Retention** `S` — a posted journal line references it forever.
- **Note** — deliberately a real table, not a text code on `departments`: a text code cannot carry a GL segment and breaks the first time one employee is costed to two centres.

### `designations`
- **Purpose** — a job title, with the MHRSD/GOSI occupation code that Saudization-restricted jobs need.
- **Tier** T · **Domain** Organisation
- **Keys** — `title_en`/`title_ar`, `occupation_code`
- **Rel** — `tenant_id → tenants`; referenced by `employee_assignments`.
- **Lifecycle** — created in setup; retired rather than deleted.
- **Retention** `C`

### `grades`
- **Purpose** — a grade and its pay band.
- **Tier** T · **Domain** Organisation
- **Keys** — `code` with `UNIQUE (tenant_id, code)`, `name`, `min_basic`, `max_basic`, `pay_scale jsonb`
- **Rel** — `tenant_id → tenants`; referenced by `employee_assignments`.
- **Lifecycle** — created in setup; `pay_scale` revised in place (history of what an employee was actually paid lives in `employee_salaries`).
- **Retention** `C`

### `public_holidays`
- **Purpose** — holiday calendars; the platform ships the KSA default.
- **Tier** R/T · **Domain** Organisation
- **Keys** — `calendar_code`, **`holiday_date`** (not `date`, a reserved word — `CONVENTIONS.md` §1), `name`, `is_paid`; `tenant_id` NULL = platform default; `UNIQUE NULLS NOT DISTINCT (tenant_id, calendar_code, holiday_date)` so the H8 sweep cannot double-count a day
- **Rel** — `tenant_id → tenants` (nullable, N:1, CASCADE); joined by `calendar_code` from `branches`.
- **Lifecycle** — platform rows seeded and updated by release; tenant rows added by admins; superseded rows are deleted (no history obligation).
- **Retention** `R`
- **Note** — nullable `tenant_id`: RLS policy shape (b), and the unique is `NULLS NOT DISTINCT` because a nullable column is otherwise exempt from it (`CONVENTIONS.md` §5).

---

## D. Employees, history, documents and letters

### `employees`
- **Purpose** — the person and their current identity; everything time-varying is in a child table.
- **Tier** T · **Domain** Employee
- **Keys** — `employee_number` (unique per tenant), **`status`: `Draft`/`Invited`/`Active`/`Suspended`/`Offboarded`/`Archived`** (`Invited` and `Suspended` are live in `Models/Employee.cs:5-23`; revision 2 lost them), `name_en`/`name_ar`, `gender`, `dob`, `nationality_code`, `national_id`/`iqama_no`/`border_no`, `joining_date`, `gosi_first_registered_on` (**cohort driver; NULL blocks the slip**), `wps_eligible`, `nitaqat_weight_override` + reason, `eos_service_start_date`, `eos_prior_paid_amount`, `separation_date`, **`work_email`** (revision 7: the fourth term of the H1 index below, which revision 6 specified without ever defining the column); **lifecycle and PDPL: `deleted_at`, `retention_until`, `privacy_status` (`Normal`/`PendingErasure`/`Anonymised`/`MergedDuplicate`), `redacted_at`**
- **Rel** — `tenant_id → tenants`; parent of assignments, salaries, contracts, bank accounts, documents, leave, attendance, loans, slips, GOSI registrations.
- **Index** — `pg_trgm` GIN over `(employee_number, name_en, name_ar, work_email)` partial `WHERE status <> 'Archived'`, plus btree `(tenant_id, status, name_en)` for the unsearched default page (H1). **Keyset paging replaces OFFSET in the port.**
- **Lifecycle** — created as `Draft` (replacing `EmployeeDrafts`), `Invited` for ESS, `Active` on hire, `Suspended`, `Offboarded` on last working day, `Archived` after retention. **Never hard-deleted** — `privacy_status` drives erasure, and all four lifecycle columns are read by `Infrastructure/Retention/Rules/ExpiredEmployeeRecordRule.cs:74`. One of only four soft-delete tables.
- **Retention** `P` — erasure obligation, satisfied by anonymisation, not purge.
- **Note** — one key type, `uuid` (**owner question Q1**). Revision 3 **dropped `EmployeeDependents` outright** rather than keeping it as a `dependents jsonb` column; dependants are out of the baseline, not relocated.

### `employee_assignments`
- **Purpose** — **effective-dated** placement: where the person sits and who they report to.
- **Tier** C · **Domain** Employee
- **Keys** — `employee_id`, `effective_from`/`effective_to`, `company_id`, `branch_id`, `department_id`, `designation_id`, `grade_id`, `manager_employee_id`, `cost_center_id`, `pay_group`, `employment_status`, `change_reason`, `approval_request_id`
- **Rel** — 8 outbound FKs (employees, companies, branches, departments, cost_centers, designations, grades, approval_requests), all N:1 RESTRICT; `manager_employee_id → employees` self.
- **Lifecycle** — created on hire and on every transfer/promotion (normally from an approved `Transfer` request); ended by setting `effective_to` when the next row starts.
- **Retention** `P`
- **Constraint** — `EXCLUDE … gist` on `(tenant_id, employee_id, daterange '[]')`. Replaces `EmployeeHistories`, `EmployeeStatusHistories`, `ReportingLines`, `EmployeeTransferRequests`.

### `employee_salaries`
- **Purpose** — **effective-dated** pay: what the person is contracted to earn.
- **Tier** T · **Domain** Employee
- **Keys** — `employee_id`, `effective_from`/`effective_to`, `basic`, `housing`, `transport`, `components jsonb` (**amounts only — the currency is the company's**), `housing_in_kind`, `change_reason`, `approval_request_id`
- **Rel** — `employee_id → employees` (N:1, RESTRICT), `approval_request_id → approval_requests` (N:1, SET NULL)
- **Lifecycle** — created on hire and on every approved `SalaryChange`; ended by the next row's start. A backdated change also produces `payroll_inputs` arrears rows.
- **Retention** `S` — it is the basis of a statutory payroll figure.
- **Constraint** — standard gist no-overlap per employee.

### `employee_contracts`
- **Purpose** — **effective-dated** contract terms.
- **Tier** T · **Domain** Employee
- **Keys** — `employee_id`, `effective_from`/`effective_to`, `contract_type`, `start_date`/`end_date` (never `end`, a reserved word), `probation_end`, `weekly_hours` (`numeric` — a contractual term, not a measured duration, so it is not subject to the minutes rule), `notice_days`, `qiwa_contract_no text`, `document_id`
- **Rel** — `employee_id → employees` (N:1, RESTRICT), `document_id → employee_documents` (N:1, SET NULL)
- **Lifecycle** — created on hire; renewed by a new row; ends at separation.
- **Retention** `S`
- **Note** — `qiwa_contract_no` is plain text and is **kept**: revision 7 settles §18 against §2 by making the `external_system`/`external_id`/`external_synced_at` trio **additive**, not a rename (`CONVENTIONS.md` §14). The Qiwa integration itself is out of scope.

### `employee_bank_accounts`
- **Purpose** — **effective-dated** IBAN, so a WPS file can always be traced to the account of the day.
- **Tier** T · **Domain** Employee
- **Keys** — `employee_id`, `effective_from`/`effective_to`, `iban` (checksum-validated), `bank_code`, `account_holder_name`, `payment_method`
- **Rel** — `employee_id → employees` (N:1, RESTRICT)
- **Lifecycle** — created on onboarding; a change inserts a new row; a missing current row raises `IBAN_MISSING` in `payroll_issues`.
- **Retention** `S` (financial instruction) with `P` sensitivity.
- **Constraint** — standard gist no-overlap per employee.

### `employee_documents`
- **Purpose** — every employee file, expiring ID document and issued letter, in one versioned chain.
- **Tier** T · **Domain** Employee
- **Keys** — `employee_id`, `doc_type` (`Iqama`/`Passport`/`Visa`/`WorkPermit`/`Contract`/`Letter`/`Medical`/`Other`), `document_number`, `issue_date`/`expiry_date`, `issuing_country`, `file_id`, `version`, `supersedes_id`, `status`; letters add `template_id`, `template_version`, `letter_number`, `verification_code`; `leave_request_id` for sick notes
- **Rel** — `employee_id → employees` (N:1, CASCADE), `file_id → files` (N:1, SET NULL), `template_id → document_templates` (N:1, RESTRICT), `leave_request_id → leave_requests` (N:1, SET NULL), `supersedes_id → employee_documents` (self)
- **Lifecycle** — created on upload or letter issue; superseded by a renewal that points back via `supersedes_id`; the blob is purged through `files`, the row remains as evidence.
- **Retention** `P` (most) / `S` (contracts, letters with a `verification_code`)
- **Note** — replaces 8 tables including `ComplianceRenewals`. **Expiry reminders are a job reading `expiry_date` into `notifications` — there is no reminder table.**

### `document_templates`
- **Purpose** — letter, payslip and contract templates in English and Arabic.
- **Tier** T · **Domain** Employee
- **Keys** — `kind` (`Letter`/`Payslip`/`Contract`), `code`, `version int` (**immutable once used**), `body_en`/`body_ar`, `merge_fields jsonb`, `company_id` NULL = tenant-wide; `UNIQUE NULLS NOT DISTINCT (tenant_id, company_id, kind, code, version)` — without it "version 3 of OFFER_LETTER" is not one row and the `template_version` snapshots do not resolve to one body
- **Rel** — **`tenant_id → tenants` (N:1, RESTRICT — §8.2 row 161, added in revision 7: `company_id` is nullable, so a tenant-wide template had no tenant FK at all)**, `company_id → companies` (N:1, RESTRICT); referenced by `employee_documents`, `payroll_slips`.
- **Lifecycle** — created in setup; an edit after first use creates a new `version`; old versions are retained forever because issued documents name the version that produced them.
- **Retention** `C` — but versions referenced by an issued document are effectively `S`.

---

## E. Statutory reference

### `statutory_rules`
- **Purpose** — the **one** effective-dated statutory table: GOSI, EOS, overtime, leave pay, Nitaqat weights, WPS parameters.
- **Tier** R · **Domain** Statutory
- **Keys** — `country_code`, `family`, `rule_key`; dimensions `nationality_class` (`Saudi`/`GCC`/`NonSaudi`/`Any`), `cohort` (`Legacy`/`Entrant2024`/`Any`), `gosi_branch` (`Annuities`/`SANED`/`OccupationalHazards` — §9 row 37, enumerated in revision 7), `payer` (`Employee`/`Employer` — §9 row 38); `rate`, `value_json`, `wage_floor`, `wage_cap`, `effective_from`/`effective_to`, `rules_version` (e.g. `SA-GOSI-2025.07`), `source_reference`, `verified_by`/`verified_at`
- **Rel** — no outbound FK; referenced by `payroll_slip_lines`, `overtime_requests`, `statutory_rule_bands`.
- **Lifecycle** — seeded by the baseline; a regulation change is a **new effective-dated row**, never an edit. `verified_by`/`verified_at` record who checked it against the circular.
- **Retention** `R` — never deleted; payroll lines from 2025 must still resolve their rule.
- **Constraint** — `UNIQUE NULLS NOT DISTINCT (country_code, family, rule_key, nationality_class, cohort, gosi_branch, payer, effective_from)` + gist no-overlap. Both wrap the **nullable** dimensions: `gosi_branch` and `payer` are NULL on the families that have no branch or payer (EOS, Overtime, Leave), and a NULL operand is otherwise exempt from the constraint entirely (`CONVENTIONS.md` §5).
- **Note** — **there are no tenant overrides.** Above-floor enhancements live in `companies.settings.pay_policy` and the engine applies `max(statutory, contractual)` (**owner question Q6**).

### `statutory_rule_bands`
- **Purpose** — band-shaped rules that a single `rate` column cannot express.
- **Tier** R · **Domain** Statutory
- **Keys** — `statutory_rule_id`, `band_key` with `UNIQUE (statutory_rule_id, band_key)` — a key that repeats inside one rule cannot be resolved by key, `unit` (`ServiceYears`/`SickDays`/`ContributoryWage`), `lower_bound numeric`, `upper_bound numeric` NULL = open (**inclusive lower, exclusive upper**), `rate`, `amount`, `value_json`, `band_order`
- **Rel** — `statutory_rule_id → statutory_rules` (N:1, CASCADE); referenced by `payroll_slip_lines.statutory_rule_band_id`.
- **Lifecycle** — seeded with its parent rule; changed only by a new parent rule version.
- **Retention** `R`
- **Constraint** — `EXCLUDE USING gist (statutory_rule_id WITH =, numrange(lower_bound, COALESCE(upper_bound,'infinity'), '[)') WITH &&)`
- **Carries** — EOS Art. 84 (½ month/yr for years 0–5, full thereafter), Art. 85 resignation fractions, Art. 117 sick pay (30 days @100%, 60 @75%, 30 unpaid), wage-banded GOSI steps.

---

## F. Payroll

### `pay_components`
- **Purpose** — the catalogue of earnings, deductions and employer contributions, with their payroll behaviour.
- **Tier** T · **Domain** Payroll
- **Keys** — `code`, `name_en`/`name_ar`, `kind` (`Earning`/`Deduction`/`EmployerContribution`/`Info`), `gosi_contributory`, `eos_eligible`, `prorate`, `gl_driver`, `is_system`
- **Rel** — `tenant_id → tenants`; referenced by `payroll_slip_lines.pay_component_code`, `payroll_inputs`.
- **Lifecycle** — system components seeded at provisioning (BASIC, HOUSING, TRANSPORT, OT, GOSI_ANN_EE/ER, SANED_EE/ER, OH_ER, LOAN, ADVANCE, UNPAID_LEAVE, ABSENCE…); tenants add custom ones; `is_system` rows cannot be deleted or have their flags changed.
- **Retention** `C` — effectively `S`, since slip lines name the code forever.

### `payroll_runs`
- **Purpose** — the header of one payroll execution for one company and period.
- **Tier** C · **Domain** Payroll
- **Keys** — `company_id`, `year`, `month`, `run_type` (`Regular`/`OffCycle`/`FinalSettlement`/`Correction`/`Opening`), `parent_run_id`, `status`, `selection jsonb`, totals, `employee_count`, `rules_version`, `attendance_locked_range daterange` (+ CHECK it lies inside the run period), `source_system`, `source_import_job_id`, `calculated_at`, `approved_at`, `locked_at`, `void_reason`, `approval_request_id`, `idempotency_key`
- **Rel** — `company_id → companies`, `parent_run_id → payroll_runs` (self), `source_import_job_id → background_jobs`, `approval_request_id → approval_requests`; parent of `payroll_slips` (1:N).
- **Lifecycle** — created on "start run"; `calculated_at` → approval → `locked_at`; ends as `Locked`, `Paid` or `Voided` with a `void_reason`. Voiding clears the attendance lock in the same transaction.
- **Retention** `S`
- **Constraint** — `UNIQUE (tenant_id, company_id, year, month, run_type) WHERE run_type IN ('Regular','Opening') AND status <> 'Voided'`.
- **Note** — mid-year go-live is `run_type='Opening'`: non-payable (a CHECK forbids `status='Paid'`), WPS-excluded, does not post to GL, and supplies the first real run's YTD carry-forward. **Its transition set is `Draft → {Locked, Voided}` and `Locked → Voided`** (§10.1, corrected in revision 7): a go-live import is precisely the thing that gets redone, and voiding with a `void_reason` keeps the provenance — `source_system`, `source_import_job_id` and every line's `source_record_id` — that deleting the row would destroy. Deleting a Draft run remains right for one created by mistake seconds ago.

### `payroll_slips`
- **Purpose** — **one frozen slip per employee per run**, carrying everything needed to reconstruct it years later.
- **Tier** **T** · **Domain** Payroll — corrected in revision 7: the table has no `company_id` and §8 registers no FK for one, so it is tenant-tier and reaches the legal entity through `run_id → payroll_runs.company_id`
- **Keys** — `run_id`, `employee_id`; identity snapshot: `employee_number`, `employee_name`, `department_name`, `designation_name`, `nationality_class`, `gosi_cohort`, `employer_gosi_registration_no`, `iban`, `bank_code`; period: `paid_from`/`paid_to`, `paid_days`, `period_days`, `proration_denominator_days`, `proration_basis`, `proration_factor`; **witnesses**: `gosi_base_policy`, `full_basic`, `full_housing`, `full_transport`, `contributory_wage`; totals: `gross`, `deductions`, `net`, `employee_statutory_total`, `employer_statutory_total`, `loan_deductions`, `arrears_amount`, `is_final_wage_month`; **YTD**: `ytd_gross`, `ytd_deductions`, `ytd_net`, `ytd_employee_statutory`, `ytd_employer_statutory`, `ytd_contributory_wage`; document: `payslip_number`, `payslip_file_id`, `payslip_sha256`, `template_id`, `template_version`, `published_at`, `language`; `inclusion_status`
- **Rel** — `run_id → payroll_runs` (N:1, RESTRICT), `employee_id → employees`, `payslip_file_id → files`, `template_id → document_templates`.
- **Lifecycle** — created by calculation; **frozen by trigger once the run is `Locked`**; only a `Correction` or `OffCycle` run can change the outcome, by producing new slips.
- **Retention** `S` — no erasure; anonymisation only after the statutory minimum.
- **Constraint** — `UNIQUE (tenant_id, run_id, employee_id)`.

### `payroll_slip_lines`
- **Purpose** — the **one** line table: every earning, deduction and employer contribution on a slip.
- **Tier** **T** · **Domain** Payroll — corrected in revision 7, with the rest of the slip chain
- **Keys** — `slip_id`, `pay_component_code`, `kind`, `amount`, `quantity`, `rate`; **GOSI freeze**: `gosi_branch` (`Annuities`/`SANED`/`OccupationalHazards` — §9 row 37) and `gosi_payer` (`Employee`/`Employer` — §9 row 38), **both enumerated in revision 7 because `trg_gosi_filing_totals` pivots on them and only *warns*: a value matching no pivot arm contributes to no total, so the filed return is silently short by that line**, `applied_contributory_wage` (renamed in revision 3 — a different fact from the slip's period figure, §11.4), `statutory_rule_id`, `statutory_rule_band_id`, `rules_version`; `source_type`, `payroll_input_id`, `loan_installment_id`; `source_system`, `source_record_id` (Opening runs); `cost_center_id`, `gl_driver`
- **Rel** — `slip_id → payroll_slips` (N:1, RESTRICT), plus 6 provenance FKs (pay_components, statutory_rules, statutory_rule_bands, payroll_inputs, loan_installments, cost_centers).
- **Lifecycle** — created with the slip; frozen with the slip; never edited.
- **Retention** `S`
- **Note** — replaces `PayrollEarnings`, `PayrollDeductions`, `PayslipComponents`, `OpeningBalanceOrigins`. The frozen `statutory_rule_id` + band is what lets an auditor reproduce a 2025 GOSI figure in 2030.

### `payroll_inputs`
- **Purpose** — the single spine for every variable pay item, carrying **the period it belongs to** separately from the period it is paid in.
- **Tier** C · **Domain** Payroll
- **Keys** — `employee_id`, `company_id`, `run_year`, `run_month` (when paid), **`covered_year`, `covered_month`** (the period it belongs to), `kind` (`Adjustment`/`Arrears`/`Receivable`/`Overtime`/`UnpaidLeave`/`Absence`/`LeaveEncashment`/`Bonus`), `pay_component_code`, `entitled_amount`, `previously_settled_amount`, `amount` (= entitled − previously settled), **`gosi_basis_delta`**, `cost_center_id`, `source_type`, `source_id`, `revision int NOT NULL DEFAULT 1`, `status` (`Pending`/`Claimed`/`Consumed`/`Cancelled`), `target_run_type`, `claimed_by_run_id`, `claimed_at`, `consumed_run_id` (**not** `consumed_line_id` — deleted in revision 3 to break the FK cycle, §8.4: the line points at the input, never both ways)
- **Rel** — employees, companies, pay_components, cost_centers, payroll_runs (N:1 each).
- **Lifecycle** — created by leave/attendance/overtime/adjustment sources; **claimed by one statement** (`UPDATE … WHERE status='Pending' … RETURNING`); consumed into a slip line; cancelled by **bumping `revision`** and inserting a replacement.
- **Retention** `S`
- **Constraint** — `UNIQUE (tenant_id, source_type, source_id, covered_year, covered_month, pay_component_code, revision)`. **Deliberately not** global on `(source_type, source_id)`: two backdated increments for the same covered period, settled in different months, are legal. Partial index `(tenant_id, company_id, covered_year, covered_month) WHERE status='Pending'`.
- **Note** — replaces 8 tables and is `TARGET_SCHEMA.md` §6 Risk 5: a defect in the claim statement double-pays or silently drops pay. `source_type` → table mapping (§16.2): `Overtime` → `overtime_requests`, `Leave` → `leave_requests`, `Attendance` → `attendance_days`, `Timesheet` → `timesheets`, `Opening` → `background_jobs`, `Manual`/`Bonus` → NULL. The uniqueness key includes both, so the mapping is also what makes consumption idempotent.

### `payroll_issues`
- **Purpose** — validation findings for a run **and** standing readiness gaps that block any run.
- **Tier** **T** · **Domain** Payroll — corrected in revision 7. `run_id` is nullable by design, so a standing gap has no run and therefore no company; a `company_id` here would be a nullable column with an unenforced FK exactly where it mattered
- **Keys** — `run_id` **nullable** (NULL = standing gap), `employee_id`, `severity` (`Block`/`Warn`), `code` (`GOSI_COHORT_UNKNOWN`, `IBAN_MISSING`, `ORG_ESTABLISHMENT_MISSING`, `SALARY_HELD`…), `gap_type`, `message`, `evidence jsonb`, `detected_at`, `resolved_at`, `override_by`, `override_reason`, `override_at`
- **Rel** — `run_id → payroll_runs` (N:1, CASCADE), `employee_id → employees`, `override_by → users`.
- **Lifecycle** — written by validation or by employee edits; closed by `resolved_at`, or by an override — **`Warn` only; `Block` is never overridable**.
- **Retention** `E`
- **Note** — replaces `PayrollValidationResults`, `PayrollValidationOverrides` and the live `EmployeeImportGaps`.

---

## G. WPS

### `wps_batches`
- **Purpose** — one SIF file exactly as filed, plus its resubmissions.
- **Tier** C · **Domain** WPS
- **Keys** — `run_id`, `company_id`, `batch_number` (from `number_sequences`), `format_version`, `status` (`Generated`/`Submitted`/`Accepted`/`PartiallyRejected`/`Rejected`), `file_id`, `file_sha256`, `employee_count`, `total_amount`, `submission_reference` (**kept** — §18's external-id trio is additive, not a rename), `submitted_at`/`acknowledged_at`/`rejected_at`, `resubmission_of_id`, `generated_by`
- **Rel** — `run_id → payroll_runs` (N:1, RESTRICT), `company_id → companies`, `file_id → files`, `resubmission_of_id → wps_batches` (self).
- **Lifecycle** — generated from a locked run (**rejected if `run_type='Opening'`**); ends `Accepted`, or spawns a resubmission that points back.
- **Retention** `S`

### `wps_lines`
- **Purpose** — one SIF line frozen as filed, plus the bank's answer.
- **Tier** C · **Domain** WPS
- **Keys** — `batch_id`, `slip_id`, `employee_id`; snapshot: `id_number`, `employee_number`, `iban`, `bank_code`, `basic`, `housing`, `other_earnings`, `deductions`, `net`, `mol_id`; bank result: `bank_status`, `bank_reference`, `confirmed_amount`, `reason_code`, `value_date`, `confirmation_job_id`
- **Rel** — `batch_id → wps_batches` (N:1, RESTRICT), `slip_id → payroll_slips`, `employee_id → employees`, `confirmation_job_id → background_jobs`.
- **Lifecycle** — written with the batch and frozen; the bank-result columns are the **only** ones a confirmation import may update, and only while the batch is not `Accepted`.
- **Retention** `S`
- **Note** — absorbs `BankPaymentConfirmations`; the import's per-row errors go to `background_job_items`.

---

## H. GL export

### `gl_mappings`
- **Purpose** — maps a payroll GL driver to debit/credit account codes; the ERP owns the chart.
- **Tier** T · **Domain** GL
- **Keys** — `company_id` NULL = tenant default, `gl_driver`, `cost_center_id` NULL, `debit_account`, `credit_account`
- **Rel** — **`tenant_id → tenants` (N:1, RESTRICT — §8.2 row 159, added in revision 7: `company_id` is nullable and NULL is the *default* mapping, so those rows had no tenant FK at all)**, `company_id → companies` (N:1, RESTRICT), `cost_center_id → cost_centers` (N:1, RESTRICT)
- **Lifecycle** — seeded with defaults at provisioning; edited by finance; a mapping referenced by a posted journal must not be deleted.
- **Retention** `C`

### `gl_journals`
- **Purpose** — one journal per source event, with the exported file and the ERP's reference.
- **Tier** C · **Domain** GL
- **Keys** — `company_id`, `source_type` (`PayrollRun`/`FinalSettlement`/`LoanDisbursement`/`Reversal`), `source_id`, `year` + `month` (two `smallint` columns — never a bare `period`, `CONVENTIONS.md` §4), `status`, `file_id`, `export_file_sha256`, `erp_reference`, `reversal_of_id`, `idempotency_key`
- **Rel** — `company_id → companies`, `file_id → files`, `reversal_of_id → gl_journals` (self); parent of `gl_journal_lines`.
- **Lifecycle** — created on export; a correction is a **reversal journal** pointing back, never an edit.
- **Retention** `S`
- **Constraint** — `UNIQUE (tenant_id, source_type, source_id, reversal_of_id)`.

### `gl_journal_lines`
- **Purpose** — the balanced lines of a journal, carrying the cost-centre segment.
- **Tier** C · **Domain** GL
- **Keys** — `journal_id`, `account`, `cost_center_id`, `project_code text`, `debit`, `credit`, `description`
- **Rel** — `journal_id → gl_journals` (N:1, CASCADE), `cost_center_id → cost_centers` (N:1, RESTRICT)
- **Lifecycle** — written with the journal; never edited.
- **Retention** `S`
- **Constraint** — `CHECK (debit * credit = 0)`; the journal balance is enforced by a **deferred** trigger.
- **Note** — `project_code` stays `text` until `projects` exists (**owner question Q8**).

### `gl_period_closes`
- **Purpose** — the auditable record that a finance period was closed, and by whom it was reopened.
- **Tier** C · **Domain** GL
- **Keys** — `company_id`, `year` + `month`, with `UNIQUE (tenant_id, company_id, year, month)`, `status` (`Open`/`Closed`/`Reopened`), `closed_at`/`closed_by`, `reopened_at`/`reopened_by`, `reopen_reason`
- **Rel** — `company_id → companies`, `closed_by`/`reopened_by → users`.
- **Lifecycle** — created on close; `Reopened` with a mandatory reason; never deleted.
- **Retention** `E`
- **Note** — a table, not a status on a journal: closing a period with no journal in it is still a statement about the period, and reopening one is a question finance will be asked.

---

## I. Loans and advances

### `loans`
- **Purpose** — a loan **or** a salary advance; one shape, distinguished by `kind`.
- **Tier** T · **Domain** Loans
- **Keys** — `employee_id`, `kind` (`Loan`/`Advance`), `type_code`, `principal`, `installment_count`, `start_year` + `start_month`, `status`, `outstanding`, `opening_outstanding`, `approval_request_id`
- **Rel** — `employee_id → employees` (N:1, RESTRICT), `approval_request_id → approval_requests`; parent of `loan_installments`.
- **Lifecycle** — created from an approved `Loan`/`Advance` request; `opening_outstanding` set at mid-year go-live; ends when `outstanding` reaches zero, at final settlement, or by waiver.
- **Retention** `S` (it is a deduction on a statutory payslip)

### `loan_installments`
- **Purpose** — the repayment schedule and what was actually recovered against each line.
- **Tier** T · **Domain** Loans
- **Keys** — `loan_id`, **`installment_number int`** with `UNIQUE (tenant_id, loan_id, installment_number)` (adopted in revision 7 — a schedule whose rows have no ordinal cannot be ordered, regenerated idempotently, or matched to a recovery), `due_year` + `due_month`, `amount`, `kind` (`Scheduled`/`EarlySettlement`/`FinalSettlement`/`Waiver`), `status` (`Due`/`Recovered`/`Waived`/`Cancelled`), `recovered_at`
- **Rel** — `loan_id → loans` (N:1, CASCADE). **No pointer back to the slip line or the settlement line**: `recovered_slip_line_id` and `settlement_line_id` were deleted in revision 3 to break the FK cycle (§8.4). Recovery is found from `payroll_slip_lines.loan_installment_id` and `final_settlement_lines.loan_installment_id`, both indexed.
- **Lifecycle** — generated on loan approval; a recovery stamps `recovered_at` and the status; unrecovered balance closes at final settlement.
- **Retention** `S`

---

## J. Leave

### `leave_types`
- **Purpose** — a leave type and the whole policy that governs it.
- **Tier** T · **Domain** Leave
- **Keys** — `code`, `name_en`/`name_ar`, `is_statutory`, `pay_rule_key` (resolves to `statutory_rules` + bands for tiered sick pay), `policy jsonb` (entitlement by service years, accrual, carry-forward, eligibility, gender, once-in-service)
- **Rel** — `tenant_id → tenants`; referenced by `leave_requests`, `leave_ledger`.
- **Lifecycle** — KSA set seeded at provisioning (Annual 21/30, Sick, Maternity, Paternity, Marriage, Bereavement, Hajj, Exam, Iddah, Unpaid, CompOff); tenants add types; statutory types cannot be deleted.
- **Note** — **`policy` stays JSON deliberately** (§13.5): the statutory floor is `statutory_rule_bands` (platform reference), the tenant's contractual ladder is this column (tenant data), and the engine takes `max(statutory, contractual)`. Tenant rows in a reference table would break the no-tenant-overrides rule.
- **Retention** `C`

### `leave_requests`
- **Purpose** — a request to take leave, or to encash it.
- **Tier** T · **Domain** Leave
- **Keys** — `employee_id`, `leave_type_id`, `request_kind` (`Leave`/`Encashment`), `start`/`end`, `days`, `day_breakdown jsonb`, `status`, `approval_request_id`
- **Rel** — `employee_id → employees`, `leave_type_id → leave_types` (RESTRICT), `approval_request_id → approval_requests`; referenced by `employee_documents.leave_request_id` (sick notes).
- **Lifecycle** — created by the employee; approved through the one approval engine; approval writes a `Debit` to `leave_ledger` and, if unpaid, a `payroll_inputs` row; **a cancellation is a new request**, not an edit.
- **Retention** `O`

### `leave_ledger`
- **Purpose** — the **append-only** ledger from which every leave balance is derived.
- **Tier** T · **Domain** Leave
- **Keys** — `employee_id`, `leave_type_id`, `entry_date`, `entry_type` (`Opening`/`Accrual`/`Debit`/`Reversal`/`CarryForward`/`Expiry`/`Encashment`/`Adjustment`), `days (±)`, `source_type` + `source_id`, `reason`, `created_by`, `idempotency_key UNIQUE`
- **Rel** — `employee_id → employees`, `leave_type_id → leave_types` (both RESTRICT).
- **Index** — `(tenant_id, employee_id, leave_type_id) INCLUDE (days)` so `v_leave_balances`' SUM is index-only (H12). If p95 exceeds 50 ms at 270 k rows/tenant the fallback is a trigger-maintained balance row — **decided by measurement, not in advance**.
- **Concurrency** — the leave debit is a read-compute-write: `REPEATABLE READ` + retry, or `pg_advisory_xact_lock(tenant, employee, leave_type)` (§19.5).
- **Lifecycle** — written by accrual/carry-forward/expiry jobs, by leave approval, and at go-live (`Opening`); **never updated or deleted** — a `BEFORE UPDATE/DELETE` trigger raises. A correction is a `Reversal` row.
- **Retention** `S` — leave balance is an EOS and final-settlement input.
- **Note** — balances are the view `v_leave_balances` (`SUM`). There is no balance table. Replaces `CompOffCredits`/`Usages` and `OvertimeCompOffConversions`.

---

## K. Attendance, overtime and timesheets

### `shifts`
- **Purpose** — a shift definition and the attendance rules that apply to it.
- **Tier** T · **Domain** Attendance
- **Keys** — `code`, `start`/`end`, `break`, `weekly_off_days`, `rules jsonb` (grace, late/early thresholds, Ramadan hours)
- **Rel** — `tenant_id → tenants`; referenced by `shift_assignments`, `attendance_days`.
- **Lifecycle** — created in setup; rules edited in place (a historical attendance day keeps its computed outcome, not the rule).
- **Retention** `C`
- **Note** — absorbs `AttendancePolicies` and `ShiftPolicies`. The unused generic `AttendanceRules` "rule engine" is dropped.

### `shift_assignments`
- **Purpose** — **effective-dated** roster: which shift an employee is on.
- **Tier** T · **Domain** Attendance
- **Keys** — `employee_id`, `shift_id`, `effective_from`/`effective_to`
- **Rel** — `employee_id → employees` (CASCADE), `shift_id → shifts` (RESTRICT)
- **Lifecycle** — created on hire or roster change; ended by the next row's start.
- **Retention** `O`
- **Constraint** — standard gist no-overlap per employee.

### `attendance_devices`
- **Purpose** — a registered biometric or terminal device at a branch.
- **Tier** C · **Domain** Attendance
- **Keys** — `branch_id`, `serial`, `name`, `api_key_hash`, `last_seen_at`, **`sync_watermark`** (the highest accepted `occurred_at`, so a resumed sync is bounded and gap-detectable), **`recent_nonces jsonb`** (the replay window)
- **Rel** — `branch_id → branches` (N:1, RESTRICT); referenced by `attendance_punches`.
- **Lifecycle** — registered in setup; `last_seen_at` updated by the sync job; deregistered by delete once no punch references it.
- **Retention** `C`

### `attendance_punches`
- **Purpose** — raw, immutable clock events from every source. **Partitioned monthly by `occurred_at`** (24-month online window, then detach → export to `files` → drop).
- **Tier** T · **Domain** Attendance
- **Keys** — `employee_id`, `occurred_at` (**partition key**), `direction` (`In`/`Out` — §9 row 40, enumerated in revision 7), `source` (`Device`/`Mobile`/`Import`/`Correction`), `device_id`, **`external_id`** (the device's own record id), `lat`/`lng`, `approval_request_id`, **two uniques: `(tenant_id, idempotency_key, occurred_at)` and `(tenant_id, device_id, external_id, occurred_at)`**
- **Rel** — `employee_id → employees`, `device_id → attendance_devices` (SET NULL), `approval_request_id → approval_requests`.
- **Lifecycle** — written once by device sync, mobile, import or an approved correction; **never edited** — a correction is a new row with `source='Correction'`. Rejected if its date falls in a locked payroll range.
- **Retention** `O` with `P` sensitivity (`lat`/`lng` is location data). 24 months, `Purge` (§12.4).
- **Constraint / index** — the partitioned unique is the `ON CONFLICT` target for batch device ingest (H9), replacing a per-row `AnyAsync`. Safe because the key derives from `(device serial, employee, occurred_at)`, so a duplicate always carries the same `occurred_at`. Per-partition index `(tenant_id, employee_id, occurred_at)` (H8); `attendance_devices (tenant_id, serial)`.

### `attendance_days`
- **Purpose** — the computed outcome for one employee on one calendar day. **Partitioned monthly by `work_date`** (24-month online window).
- **Tier** T · **Domain** Attendance
- **Keys** — `employee_id`, **`work_date`** (a **local** date in the company's `timezone_id`, never derived from UTC at read time), `shift_id`, `first_in`, `last_out`, and the whole-minute set `scheduled_minutes`, `worked_minutes`, `break_minutes`, `late_minutes`, `early_out_minutes`, `overtime_minutes`, `absent_minutes` (all `integer`, all `>= 0`), `computed_at`, `status`, `exceptions jsonb`, **`locked_run_id`**
- **Rel** — `employee_id → employees`, `shift_id → shifts`, `locked_run_id → payroll_runs` (SET NULL)
- **Lifecycle** — recomputed from punches until the day is locked; `locked_run_id` names the run that froze it; voiding that run clears the lock in the same transaction.
- **Retention** `O`
- **Constraint** — `UNIQUE (tenant_id, employee_id, work_date)` — unchanged by partitioning, because it already contains the partition key. PK becomes `(id, work_date)`.
- **Index** — `(tenant_id, work_date, status)` (H2 dashboard); FK cover `(tenant_id, employee_id)`. **The only partitioned table that is an FK target** — see `timesheet_day_reconciliations`.
- **Note** — replaces `AttendanceDailyRecords`, `AttendanceRecords`, `AbsenceRecords`, `AttendanceExceptions`, `AttendanceLockPeriods`.

### `overtime_requests`
- **Purpose** — an overtime claim with its calculation frozen at approval.
- **Tier** T · **Domain** Attendance
- **Keys** — `employee_id`, `work_date`, **`overtime_minutes int`** (not `hours` — `CONVENTIONS.md` §4), `ot_type` (`Normal`/`WeeklyOff`/`PublicHoliday`/`Ramadan` — §9 row 39), `basic_hourly_rate`, `multiplier`, `amount`, `rules_version`, `statutory_rule_id`, `payout` (`Pay`/`CompOff`), `status`, `approval_request_id`
- **Rel** — `employee_id → employees`, `statutory_rule_id → statutory_rules` (RESTRICT), `approval_request_id → approval_requests`.
- **Lifecycle** — raised by employee or manager; on approval, `payout='Pay'` creates a `payroll_inputs` row and `payout='CompOff'` credits `leave_ledger`; the frozen rate and multiplier are never recomputed.
- **Retention** `S` (it becomes a statutory payslip line)

### `timesheets`
- **Purpose** — one period timesheet per employee, the header its lines hang from.
- **Tier** C · **Domain** Timesheets
- **Keys** — `employee_id`, `company_id`, **`timesheet_number`** (from `number_sequences` scope key `'timesheet_no'`, `UNIQUE (tenant_id, timesheet_number)`), `period_start`/`period_end`, `UNIQUE (tenant_id, employee_id, period_start)`, `status` (`Draft`/`Submitted`/`Approved`/`Rejected`/`Locked`), **`total_minutes int`** (not `total_hours`), `submitted_at`, `decided_at`, `approval_request_id`, `locked_run_id`
- **Rel** — `employee_id → employees`, `company_id → companies`, `approval_request_id → approval_requests`, `locked_run_id → payroll_runs` (SET NULL)
- **Lifecycle** — opened for a period, submitted, approved through the one approval engine (`request_type='Timesheet'`), then locked by a payroll run.
- **Retention** `O`

### `timesheet_entries`
- **Purpose** — one relational line of worked time; carries the project/client dimension. **Partitioned monthly by `work_date`** (24-month online window).
- **Tier** C · **Domain** Timesheets
- **Keys** — `timesheet_id`, `work_date`, **`minutes int`** (not `hours`), `cost_center_id`, `project_code text`, `task`, `billable`, `rate_source`, `notes`
- **Rel** — `timesheet_id → timesheets` (N:1, CASCADE), `cost_center_id → cost_centers` (RESTRICT)
- **Lifecycle** — entered by the employee while `Draft`; immutable once the timesheet is `Approved`; rejected if the date falls in a locked payroll range.
- **Retention** `O`
- **Note** — relational, not JSON, precisely because cost centre must be an FK. `project_code` becomes a FK when `projects` lands (**Q8**).

### `timesheet_day_reconciliations`
- **Purpose** — the variance between what a timesheet claims for a day and what attendance recorded.
- **Tier** C · **Domain** Timesheets
- **Keys** — `timesheet_id`, `attendance_day_id`, `work_date`, `UNIQUE (tenant_id, timesheet_id, work_date)`, and the three durations **all in minutes** — `timesheet_minutes`, `attendance_minutes`, `variance_minutes` — so the invariant `variance_minutes = timesheet_minutes − attendance_minutes` is a `CHECK`able subtraction and not the unit conversion revision 6 wrote; `explanation`, `status`, `resolved_by`/`resolved_at`
- **Rel** — `timesheet_id → timesheets` (CASCADE); **`(tenant_id, attendance_day_id, work_date) → attendance_days (tenant_id, id, work_date)`** (N:1, RESTRICT, optional). §8 row 126.
- **Lifecycle** — computed on timesheet submission; closed by a manager resolving the variance; recomputed if the timesheet is returned and resubmitted.
- **Retention** `O`

---

## L. End of service and final settlement

### `eos_calculations`
- **Purpose** — an end-of-service computation with the statutory bands it used copied onto it.
- **Tier** T · **Domain** EOS
- **Keys** — `employee_id`, `calculation_date`, `separation_reason` (`Art84`/`Art85`/`Art87`/`Art77` — §9 row 44, enumerated in revision 7), `service_start`, `service_days`, `excluded_unpaid_days`, `last_wage_basis`, `eligible_wage`, `amount`, `prior_paid_deducted`, `rules_version`, **`rules_snapshot jsonb`** (the resolved bands, copied), `status` (`Estimate`/`Final`), `settlement_id`
- **Rel** — `employee_id → employees`, `settlement_id → final_settlements` (SET NULL)
- **Lifecycle** — any number of `Estimate` rows; exactly one becomes `Final` and attaches to a settlement; frozen by trigger once `Final`.
- **Retention** `S`
- **Note** — `rules_snapshot` is what lets an employee dispute an EOS figure in 2031 against the bands as they stood on the separation date.

### `final_settlements`
- **Purpose** — the separation case and the header of what is owed on exit.
- **Tier** C · **Domain** EOS
- **Keys** — `employee_id`, `company_id`, `separation_type` (`Resignation`/`Termination`/`EndOfContract`/`Retirement`/`Death`/`Abscond` — §9 row 41, enumerated in revision 7; `Abscond` stays distinct from `Termination` because KSA practice gives it its own EOS consequence), `notice_given`/`notice_served`, `last_working_day`, `clearance jsonb`, `status`, `gross`, `deductions`, `net`, `paid_via_run_id`, `settlement_number`, `approval_request_id`
- **Rel** — `employee_id → employees`, `company_id → companies`, `paid_via_run_id → payroll_runs` (SET NULL), `approval_request_id → approval_requests`; parent of `final_settlement_lines`.
- **Lifecycle** — opened at resignation/termination; approved; paid via a `FinalSettlement` run; ends `Paid`. Sets `employees.status='Offboarded'` and `separation_date`.
- **Note** — **`clearance` stays JSON deliberately** (§13.5): a checklist is not a multi-step approval, and routing it through the approval engine would create one `approval_requests` row per item. It is a validated array of `{key, required, done_by, done_at}`, and an undone required item **blocks the settlement leaving Draft** (§10.10).
- **Retention** `S`

### `final_settlement_lines`
- **Purpose** — the components of a settlement, each traced to what produced it.
- **Tier** C · **Domain** EOS
- **Keys** — `settlement_id`, `kind` (`EOS`/`LeaveEncashment`/`UnpaidSalary`/`NoticePay`/`Art77Compensation`/`LoanRecovery`/`OtherDeduction`), `amount`, `source_type` + `source_id`, `rules_version`
- **Rel** — `settlement_id → final_settlements` (N:1, CASCADE), `loan_installment_id → loan_installments` (N:1, RESTRICT — the typed FK that replaced the polymorphic pointer). **Nothing points back from `loan_installments`**: that reverse pointer was deleted in revision 3 (§8.4).
- **Lifecycle** — written with the settlement; frozen once the settlement is `Approved`.
- **Retention** `S`

---

## M. Nitaqat

### `nitaqat_grid`
- **Purpose** — the MHRSD band thresholds by activity and size tier.
- **Tier** R · **Domain** Nitaqat
- **Keys** — `activity_code`, `activity_name_en`/`activity_name_ar`, `size_tier` with `headcount_min`/`headcount_max`, `band` (`Platinum`/`HighGreen`/`MidGreen`/`LowGreen`/`Red`), `min_saudization_pct`, `max_saudization_pct`, `effective_from`/`effective_to`, `grid_version`
- **Rel** — none; joined from `companies.nitaqat_activity_code`.
- **Lifecycle** — seeded by the baseline; a grid revision is a new `grid_version` with new effective dates, never an edit.
- **Retention** `R`
- **Constraint** — `EXCLUDE` on the percentage `numrange` per activity, size tier and version.
- **Note** — deliberately **not** folded into `statutory_rules`: the key is activity × size tier, which the `statutory_rules` dimension set does not carry, and `value_json` would lose the FK and the query shape.

### `nitaqat_snapshots`
- **Purpose** — an establishment's Saudization standing at a point in time, with the drill-down behind it.
- **Tier** C · **Domain** Nitaqat
- **Keys** — `company_id`, `as_of_date`, `activity_code`, `size_tier`, `saudi_weighted`, `total_weighted`, `achieved_pct`, `band`, `grid_version`, `rules_version`, **`employee_breakdown jsonb`** (employee_id, weight, reason)
- **Rel** — `company_id → companies` (N:1, CASCADE)
- **Lifecycle** — written by a scheduled `background_jobs` run; never edited; retained as the evidence behind a KPI a user clicked into.
- **Retention** `E` with `P` content — `employee_breakdown` holds employee ids and must be covered by the purge job (`TARGET_SCHEMA.md` §6 Risk 3: a composite FK cannot guard inside JSON).

---

## N. GOSI registration and filing

### `employee_gosi_registrations`
- **Purpose** — **effective-dated** GOSI registration at the grain GOSI actually files on.
- **Tier** C · **Domain** GOSI
- **Keys** — `employee_id`, `company_id`, `gosi_registration_no` (the establishment), `gosi_employee_no`, `effective_from`/`effective_to`, **`registered_contributory_wage`** (what GOSI holds, which is not always what payroll computes), `occupation_code`, `registered_on`, `deregistered_on`, `status`
- **Rel** — `employee_id → employees`, `company_id → companies` (both RESTRICT)
- **Lifecycle** — created on GOSI registration; a wage or establishment change is a new row; `deregistered_on` closes it at separation.
- **Retention** `S`
- **Constraint** — standard gist no-overlap per employee.
- **Note** — cross-checks `employees.gosi_first_registered_on` for cohort resolution; a NULL raises a **blocking** `payroll_issues` row. The code never defaults to a cohort.

### `gosi_filings`
- **Purpose** — the monthly GOSI return as filed, per establishment, and its variance against the GOSI invoice.
- **Tier** C · **Domain** GOSI
- **Keys** — `company_id`, `gosi_registration_no`, `year`, `month`, `rules_version`, `status` (`Draft`/`Filed`/`Reconciled`/`Disputed`), `employee_count`, totals per branch × payer, as seven explicit columns (`annuities_employee`, `annuities_employer`, `saned_employee`, `saned_employer`, `occupational_hazards_employer`) — the same branch × payer dimensions §9 rows 37–38 now enumerate, `revision int`, `total_contributory_wage`, `total_amount`, `file_id` + `file_sha256`, `filed_at`/`filed_by`, `gosi_invoice_amount`, `variance_amount`, `variance_reason`
- **Rel** — `company_id → companies`, `file_id → files`.
- **Lifecycle** — built from locked runs as `Draft`; **frozen on `Filed`**; `Reconciled` when the GOSI invoice matches, `Disputed` when it does not — and `variance_reason` is mandatory for any non-zero `variance_amount`.
- **Retention** `S`
- **Constraint** — `UNIQUE (tenant_id, company_id, gosi_registration_no, year, month, revision)`.
- **Note** — new table. GOSI invoices against the wage **it** holds, not the wage payroll computes; without this grain a variance cannot be explained. `payroll_slips.employer_gosi_registration_no` ties every slip to the return that carried it.

---

## O. Approvals

### `approval_workflows`
- **Purpose** — one workflow definition per request type; the whole approvals engine for every module.
- **Tier** T · **Domain** Approvals
- **Keys** — `request_type` (`Leave`, `LeaveCancel`, `Overtime`, `Loan`, `Advance`, `PayrollRun`, `FinalSettlement`, `ProfileChange`, `Transfer`, `SalaryChange`, `LetterRequest`, `AttendanceCorrection`, `Timesheet`), `company_id` NULL = tenant-wide, `steps jsonb` `[{order, approver_rule, amount_threshold, sla_hours}]`, `is_active`
- **Rel** — **`tenant_id → tenants` (N:1, RESTRICT — §8.2 row 160, added in revision 7: `company_id` is nullable and a tenant-wide workflow is the normal case, so those rows had no tenant FK at all)**, `company_id → companies` (RESTRICT); referenced by `approval_requests`.
- **Lifecycle** — seeded with defaults at provisioning; edited by admins; an edit never changes an in-flight request, because the request snapshots the workflow.
- **Retention** `C`
- **Note** — replaces 5 tables. Adding a module means adding a `request_type` value, **not a table**.

### `approval_requests`
- **Purpose** — one instance of something awaiting a decision.
- **Tier** T · **Domain** Approvals
- **Keys** — `request_type`, `subject_type` + `subject_id`, `requester_user_id`, `employee_id`, `payload jsonb`, `workflow_id` + **`workflow_snapshot jsonb`**, `current_step`, `status`, `due_at`, **`current_approver_user_id` and `current_approver_employee_id`**
- **Rel** — `workflow_id → approval_workflows` (RESTRICT), `requester_user_id → users`, `employee_id → employees`; referenced as `approval_request_id` by 10+ tables.
- **Denormalised, with a maintenance rule** — `current_approver_*` are derived from the current step of `workflow_snapshot` and exist **only so the inbox has something to index** (§15). Maintained by the approval engine on **every step advance, return and delegation**, and re-derived nightly. H4 is indexable because of them; without them the inbox has no sargable predicate.
- **Index** — partial `(tenant_id, current_approver_user_id, due_at, created_at DESC) WHERE status='Pending'` and its `current_approver_employee_id` twin; overdue: partial `(tenant_id, due_at) WHERE status='Pending' AND due_at IS NOT NULL` (H4). **The inbox sort becomes `due_at ASC NULLS LAST` — the `COALESCE` is not sargable.**
- **Lifecycle** — created by the module raising the request; advances by `approval_actions`; ends `Approved`/`Rejected`/`Returned`/`Cancelled`; the approving module then does its own write.
- **Retention** `E`
- **Note** — `subject_type` + `subject_id` is a deliberate polymorphic pointer (§16.1). Allowed `subject_type` values are exactly the `request_type` values of §9, each mapped to one table in a **dictionary held in code and asserted by a test**; a nightly `background_jobs` sweep resolves every open request's subject and **reports** unresolvable rows — the request is the evidence, so an orphan is reported, never deleted. `payload` is not reachable by a composite FK and needs a CHECK plus RLS.

### `approval_actions`
- **Purpose** — every decision and comment on a request, in order.
- **Tier** T · **Domain** Approvals
- **Keys** — `request_id`, `step`, `actor_user_id`, `on_behalf_of_user_id`, `action` (`Approve`/`Reject`/`Return`/`Comment`/`Escalate`), `comment`
- **Rel** — `request_id → approval_requests` (N:1, CASCADE), `actor_user_id`/`on_behalf_of_user_id → users`.
- **Lifecycle** — appended as decisions are taken; never edited. `on_behalf_of_user_id` records a delegated decision.
- **Retention** `E`

### `approval_delegations`
- **Purpose** — a time-boxed handover of approval authority.
- **Tier** T · **Domain** Approvals
- **Keys** — `delegator_user_id`, `delegate_user_id`, `from`/`to`, `request_types text[]`
- **Rel** — both user columns → `users` (CASCADE)
- **Lifecycle** — created by the delegator; expires by date, no job required; revoked by delete.
- **Retention** `E` — it explains why someone else approved.

---

## P. Notifications

### `notifications`
- **Purpose** — one in-app inbox item for one user.
- **Tier** T · **Domain** Notifications
- **Keys** — `user_id`, `category`, `title`, `body`, `link`, `read_at`, `source_type` + `source_id`, `idempotency_key UNIQUE`
- **Rel** — `user_id → users` (N:1, CASCADE); parent of `notification_deliveries`.
- **Lifecycle** — created by jobs (document expiry, approvals due) and by events; read by the user; hard-deleted by a TTL job.
- **Retention** `T`
- **Note** — replaces `ComplianceReminders`, `EmployeeNotifications` and `EmployeeActionItems`; the ESS to-do list is derived from `notifications` + `approval_requests`.

### `notification_deliveries`
- **Purpose** — one outbound attempt on one channel, with its retry state.
- **Tier** T · **Domain** Notifications
- **Keys** — `notification_id`, `channel` (`Email`/`Sms`/`Push` — §9 row 42, enumerated in revision 7; the three were named in prose and governed nowhere), `destination` (masked), `status` (§9 row 32 — `Queued`/`Sent`/`Delivered`/`Failed`/`Suppressed`/**`DeadLettered`**), `attempts`, `next_attempt_at`, `provider_message_id`, `last_error`
- **Rel** — `notification_id → notifications` (N:1, CASCADE)
- **Lifecycle** — created when a channel is selected; retried until delivered or **`DeadLettered`** after `max_attempts` — a terminal state surfaced on the operations view and alerted on, so an exhausted delivery is visible rather than silently stuck; hard-deleted with its notification.
- **Retention** `T`
- **Note** — `destination` is masked precisely so this table is not a copy of the tenant's contact database.

---

## Q. Audit

### `audit_logs`
- **Purpose** — one log for everything except payroll money movement, with tamper evidence by periodic Merkle checkpoints. **Partitioned monthly by `created_at`**.
- **Tier** T/P · **Domain** Audit
- **Keys** — `record_kind` (`Event`/`Checkpoint`), `tenant_id` NULL = platform, `company_id`, `category` (`Auth`/`Admin`/`Employee`/`Leave`/`Attendance`/`Loan`/`Document`/`ESS`), **`chain_key`** (the Merkle chain a row belongs to — one per tenant, `'platform'` for tenant-less rows; adopted in revision 7, because §19.3 already made it half of `UNIQUE (chain_key, seq, created_at)` while §Q's column list never defined it), `seq bigint` (per-tenant sequence), `action`, `entity`, `entity_id`, `actor_user_id`, `ip`, `envelope_hash`, `hash_algorithm`, **`before jsonb`, `after jsonb`**, `personal_data jsonb` (all three **purgeable**, all three inside the hashed envelope), **`personal_data_hash`, `before_hash`, `after_hash`** (so the envelope hash can be **recomputed** after erasure, not merely compared), `personal_data_erased_at`, **`correlation_id uuid`**, `on_behalf_of_user_id`; **`ip` and `user_agent` now live *inside* `personal_data`**, not as standalone columns, so erasure reaches them; checkpoint rows add `covers_seq_from`/`covers_seq_to`, **`covers_created_from`/`covers_created_to`** (the checkpointer is bounded by time as well as `seq` — a `seq`-only walk prunes no partitions and scans an ever-growing range), `root_hash`, `prev_checkpoint_hash`, **`observed_gaps int8[]`**
- **Rel** — **none, by design**, so rows outlive the purges of what they describe.
- **Lifecycle** — appended by every audited action; a checkpointer job writes `Checkpoint` rows every N rows or M seconds; **never updated or deleted** except that the purge job nulls `personal_data`, `before` and `after` and sets `personal_data_erased_at` — which leaves `envelope_hash` and every checkpoint still verifying. `entity`/`entity_id` is deliberately unconstrained (§16.4): an audit row must be able to describe a row that no longer exists.
- **Retention** `E` — envelope kept for the statutory minimum; personal payload purgeable on request. **A sequence gap is recorded in `observed_gaps`, not alerted**: a per-tenant sequence legitimately skips on rollback, so a gap is evidence to retain, not an incident to page on.
- **Note** — deliberately unchained per row: a `prev_hash` would serialise every audited write in a tenant behind one hot row, and this product audits every login. Replaces 14 per-module log tables.

### `payroll_audit_logs`
- **Purpose** — the separate, trigger-protected, **row-chained** log of payroll state changes.
- **Tier** T · **Domain** Audit
- **Keys** — `run_id`, `entity`, `entity_id`, `action`, `metadata jsonb`, **`before jsonb`, `after jsonb`**, `user_id`, `seq`, `prev_hash`, `entry_hash`, **`correlation_id uuid`**
- **Rel** — none, by design.
- **Lifecycle** — appended on every payroll state change; protected by `trg_payroll_audit_logs_append_only` (**exists today and is carried into the baseline unchanged**).
- **Retention** `E`/`S` — highest evidentiary bar in the product; never purged while the payroll it describes is retained.
- **Note** — stays row-chained because its write rate is low and its evidentiary bar is highest.

### `retention_purge_audits`
- **Purpose** — the evidence that a PDPL retention or erasure action was executed, or was a dry run.
- **Tier** T · **Domain** Audit
- **Keys** — `job_id` (**plain uuid, FK-free like the other audit tables**), `rule_key` (FK-free match to `retention_policies.rule_key`), `entity`, `entity_id`, `disposition` (the §9 row 36 set), `outcome` (`Applied`/`Skipped`/`Failed` — §9 row 43, enumerated in revision 7), `dry_run`, `retention_until`, **`correlation_id uuid`**, `details jsonb`
- **Rel** — **none.** `job_id` and `rule_key` match by value, deliberately, so evidence outlives the job and the policy row.
- **Lifecycle** — appended by the purge job, including for dry runs; append-only.
- **Retention** `E` — this is the table that proves the others were purged; it outlives them.

---

## R. Jobs

### `background_jobs`
- **Purpose** — one asynchronous, resumable, leased unit of work.
- **Tier** T/P · **Domain** Jobs
- **Keys** — `kind`, `status` (§9), `idempotency_key UNIQUE`, `payload jsonb`, **`progress_current int` + `progress_total int NULL`** (adopted in revision 7, replacing a single `progress`: "how far through what" is the question an observable job exists to answer, and `progress_total` is nullable because a streaming import does not know its denominator until it ends), `attempts`, `lease_owner`, `heartbeat_at`, `source_file_id`, `source_file_sha256`, `result jsonb`, **`correlation_id uuid`**
- **Rel** — `tenant_id → tenants` (nullable), `source_file_id → files` (SET NULL); parent of `background_job_items`; referenced by `payroll_runs.source_import_job_id`, `wps_lines.confirmation_job_id`, `retention_purge_audits.job_id`.
- **Lifecycle** — enqueued with an `idempotency_key`; leased by a worker holding `lease_owner` + `heartbeat_at`; a dead lease is reclaimed by heartbeat age; ends `Succeeded`/`Failed`; hard-deleted by TTL once no audit row references it.
- **Retention** `T` (job row) / `E` (rows referenced by `retention_purge_audits`)
- **Kinds** — imports, opening-balance import, bank confirmations, device sync, Nitaqat snapshot, leave accrual/carry-forward/expiry, expiry reminders, PDPL purge, audit checkpointing.
- **Note** — nullable `tenant_id` (NULL = a platform job), so **RLS policy shape (b)**, not (a). Absorbs `WorkerHeartbeats` and three import-batch tables.

### `background_job_items`
- **Purpose** — the per-row outcome of a job, so a 5,000-row import reports which 11 rows failed and why. **Partitioned monthly by `created_at`** (90-day online window, then detach and drop).
- **Tier** T/P · **Domain** Jobs
- **Keys** — `job_id`, **`created_at`** (partition key, now explicit), `row_ref`, `status` (§9), `error_code`, `error_message`, `entity_id`, **`correlation_id uuid`**
- **Rel** — `job_id → background_jobs` (N:1, CASCADE)
- **RLS** — **policy shape (b), not (a)** (revision 7, and revision 6 had it wrong). An item inherits its job's `tenant_id`, which is NULL for a platform job — so under shape (a) the worker draining a platform queue **cannot see its own items**: the import reports zero rows processed and zero errors while every per-row outcome sits invisible in the table. The same silent failure as `retention_policies`, one level down. With `background_job_items`, the nullable-tenant set is **seven**.
- **Lifecycle** — written as the job processes; deleted with the job.
- **Retention** `T`

---

## Views (not tables)

| View | Definition | Replaces |
|---|---|---|
| `v_leave_balances` | `SUM(days)` over `leave_ledger` per employee and leave type | `EmployeeLeaveBalances` |
| `v_employee_current` | `employees` joined to the current `employee_assignments`, `employee_salaries` and `employee_bank_accounts` row | the "current state" query written ad hoc in ~40 places today |

**Both are created `WITH (security_invoker = true)`, and this is a P0.** A view without it evaluates
its base tables as the **view owner** — `kynex_owner`, which holds `BYPASSRLS` — so it would return
every tenant's rows, and `FORCE ROW LEVEL SECURITY` would not help because **`BYPASSRLS` outranks
`FORCE`**. `v_leave_balances` is the only sanctioned way to read a balance (§11.6), so the mandated
read path was a cross-tenant leak by default. Two CI assertions hold the line: `pg_class.reloptions`
must contain `security_invoker=true` for every baseline view, and the coverage ratchet spans
`relkind IN ('r','p','v')`. See `ANTI_PATTERNS.md` §12.

---

## Gaps in this dictionary

Revision 7 is the approved design of record, and the baseline DDL slice is written. **Every defect
this document reported against revisions 4 and 5 is closed**, with nothing disputed:

| Reported | Outcome |
|---|---|
| Retention (24 mo) vs partition window (36 mo) on `attendance_days` and `timesheet_entries` | **Resolved at 24 months in both**, the direction that does not over-retain, with the legal basis now on the page: "the KSA labour-claim limitation window (one year from the end of the relationship) with a margin", flagged `[COUNSEL]`. |
| `payroll_runs.totals` named as a group | **Four named `numeric(18,2)` columns** + `employee_count` + `selected_employee_count`. |
| `gosi_filings` totals named as a group | **Seven named columns.** |
| `gosi_filings` unique cited an undeclared `revision` | `revision` is now a column; the unique is restated. |
| `companies.go_live_period` untyped | **`go_live_year smallint` + `go_live_month smallint`.** |
| `company_pay_policies.value_json` "bounded" with no bound | **`CHECK (pg_column_size(value_json) <= 8192)`** + a schema validated on write. |
| §5 "Still open" contradicted §19 | Fixed. |
| Bypass figure 330 vs 654 | Denominator stated: **346** production sites, **330** ratchet-approved across 60 files, tests excluded. |
| `TARGET_SCHEMA.md` line 3 understated the document's status | Fixed: it now reads revision 7, approved design of record, baseline DDL slice written. |

### What revision 7 changed in this dictionary

Writing the baseline as SQL surfaced twelve contradictions in the design. All twelve are settled in
`TARGET_SCHEMA.md` (the summary table is in `CONVENTIONS.md` §16); the eight that touch column lists
here are:

| Change | Tables affected |
|---|---|
| **Durations are whole minutes**, leave stays days | `attendance_days`, `overtime_requests`, `timesheets`, `timesheet_entries`, `timesheet_day_reconciliations`. There is no `hours` column left in the dictionary |
| **Periods are `year` + `month`**, never a bare `period` | `gl_journals`, `gl_period_closes`, `loans`, `loan_installments` (and unchanged on `payroll_runs`, `gosi_filings`) |
| **Eight closed sets enumerated** (§9 rows 37–44) | `statutory_rules`, `payroll_slip_lines`, `overtime_requests`, `attendance_punches`, `final_settlements`, `notification_deliveries`, `retention_purge_audits`, `eos_calculations` |
| **Tier letter corrected to T** | `payroll_slips`, `payroll_slip_lines`, `payroll_issues` — none has a `company_id`, and §8 registers no FK for one |
| **Direct tenant FK added** | `gl_mappings`, `approval_workflows`, `document_templates` — each reached tenancy only through a nullable `company_id` |
| **Six implied uniques declared** | `roles`, `grades`, `role_permissions`, `statutory_rule_bands`, `document_templates`, `public_holidays` |
| **Columns adopted** | `employees.work_email`, `timesheets.timesheet_number`, `audit_logs.chain_key`, `loan_installments.installment_number`, `background_jobs.progress_current`/`progress_total`, `public_holidays.holiday_date`, `employee_contracts.end_date` |
| **`Opening` run gains an abandon path** | `payroll_runs` — `Draft → Voided` and `Locked → Voided`, `void_reason` mandatory |
| **RLS populations restated so they sum to 76** | `tenants` (self-tenant, not shape (a)), `data_protection_keys` (grant-policed, previously in no shape and uncounted), `background_job_items` (shape (b), previously (a)) |
| **Nullable-tenant set is seven** | `audit_logs`, `background_jobs`, `background_job_items`, `public_holidays`, `retention_policies`, `auth_sessions`, `auth_tokens` |
| **Three secret columns added** | `auth_sessions.refresh_token_hash`, `.previous_token_hash`, `.push_token` |
| **Stale entries corrected while syncing** | `payroll_inputs.consumed_line_id`, `loan_installments.recovered_slip_line_id` and `.settlement_line_id` were removed by revision 3's cycle-breaking (§8.4) and were still listed here; `payroll_slip_lines.contributory_wage` is `applied_contributory_wage` |

### What remains

| # | Item | Nature |
|---|---|---|
| 1 | **`[COUNSEL]` twice:** the GOSI rate ladder and wage bounds (§E), and the retention periods (§12.4). The *reasoning* for 24 months is on the page, which makes it reviewable — it is still not legal advice, and the 84-month and 90-day figures remain published commitments rather than confirmed minima. | External dependency, not a design defect |
| 2 | **The region decision** (P2-19): app in Oregon, database in `us-east-1`, ~70 ms per round trip. Carried explicitly as an owner call with the latency SLO to price it. | Open owner decision, stated |
| 3 | `branches.holiday_calendar_code → public_holidays.calendar_code` and `companies.nitaqat_activity_code → nitaqat_grid.activity_code` are still text-code joins with no FK. Deliberate or not is still unstated — the smallest surviving ambiguity, and arguably correct, since both targets are versioned reference data a company points at by code. | Minor, unstated |
| 4 | **§19 is now partly built.** The extensions file, the nine domain table files and the constraint files exist; RLS, partitions, indexes, triggers and seeds do not. Everything in this dictionary marked partitioned or policy-governed describes what the remaining passes will create. | Status, not a gap |
| 5 | **Four period-column renames are outstanding in the written SQL** — `gl_journals` and `gl_period_closes` spell the period `period_year`/`period_month`, and the two loan columns carry a redundant `period_` infix. Mechanical, and must land before `schema.sql` is committed. | Follow-up on built code |
