# Ownership and retention

Two questions this document answers, for every one of the 76 tables:

1. **Who approves a schema change here?**
2. **How long must the data live, and when it goes, does it get anonymised or purged?**

> **Read this first.** `TARGET_SCHEMA.md` (pinned: **revision 7, 76 tables** — the approved design of record) makes the retention
> matrix **data**, not a document: the `retention_policies` table holds one row per entity with
> `legal_basis`, `minimum_retention_months`, `trigger_event`, `disposition`, `owner_role` and
> `rule_key`, and **§12.4 seeds it with real values**.
>
> **That table is the source of truth. This document is a rendering of it**, and the class letters
> in §2 are a shorthand that maps onto its columns (§2.1). If the two ever disagree, the table is
> right and this document is stale — which is exactly the failure `KEEPING_DOCS_HONEST.md` §3
> assertion 4 is there to catch.
>
> **Role vocabulary.** §12.4 uses four `owner_role` values — **HR, Finance, Platform,
> Compliance** — and this document's engineering-team names map onto them (§1). `TARGET_SCHEMA.md`
> still defines no team structure, so the mapping to humans is the owner's to make.
>
> **Durations.** §12.4 now seeds real periods. They are **engineering defaults carrying the
> product's published commitments** — 7 years for payroll and 90 days after account closure, both
> taken from `Infrastructure/Retention/DataRetentionOptions.cs:61,46` — **not confirmed legal
> minima**. The legal minimum remains a `[COUNSEL]` item, exactly as the code already flags.

---

## 1. Owning roles

| Role | `owner_role` in §12.4 | Owns | Why it is a distinct approver |
|---|---|---|---|
| **Platform Engineering** | `Platform` | tenancy, identity, files, jobs | A change here is a change to tenant isolation. Blast radius is every tenant. |
| **Payroll Engineering** | `Finance` | payroll, WPS, GL, loans, EOS | Every change is potentially a change to a number a regulator sees. |
| **HR Core Engineering** | `HR` | employees, organisation, leave, attendance, timesheets, approvals, notifications | Highest change rate; lowest statutory risk. |
| **Compliance (KSA)** | `Compliance` | statutory reference, GOSI, Nitaqat, audit | Owns the *values*, not the code. A wrong rate is a legal exposure, not a bug. |
| **Data Protection Officer (DPO)** | — | every `retention_policies` row, and the purge job's rules | PDPL accountability sits with a named person, not a team. |
| **Data Architect** | — | `CONVENTIONS.md`, the table cap, every cross-domain FK | The one role that sees all 76 tables. |

**Two standing rules:**

- **The hard table cap is gone** (decision 9). The rule is the principle: *no duplicate tables,
  nothing kept that nothing uses, every table traceable to a capability.* 76 stands, no cut from
  `TARGET_SCHEMA.md` §7 is taken, and §7 is kept as the record of what each cut would have cost so
  the question is not re-opened from memory. **A principle is a weaker brake than a number** — §6
  Risk 1 says so — so every addition must **name the capability it serves** in the PR and pass the
  no-duplicate test in review, and the doc-sync gate enforces a `@owner:`/`@retention:` comment plus
  a purpose sentence on every table (`KEEPING_DOCS_HONEST.md` assertion 8).
- **Every cross-domain FK needs the Data Architect.** Adding an FK from your domain into someone
  else's couples two release trains. `approval_requests` alone is referenced by ten tables in seven
  domains; that is the cost of a good abstraction and it must stay deliberate.

---

## 2. Retention classes

| Class | Name | Legal minimum | Erasure obligation | Disposal method |
|---|---|---|---|---|
| **R** | Reference | Indefinite | None — no personal data | **Never disposed.** A 2025 payslip must still resolve its 2025 rule. |
| **C** | Configuration | Life of the tenant; **3 months** after `soft_deleted_at` (§12.4) | None | Purge with the tenant. |
| **P** | Personal | **84 months** from Separation (§12.4) | **Yes** — PDPL right to erasure | **Anonymise**, not purge: clear the identifying columns, set `privacy_status='Anonymised'` and `redacted_at`, **retain `employee_number`**, and every dependent row keeps its FK. |
| **O** | Operational | **24 months** (attendance, timesheets) / 12 (notifications) / 6 (jobs) | **Yes**, once no P/S row depends on it | `Purge`. The 24-month basis is now stated: attendance and timesheets are **working inputs, not the statutory wage record** — the payslip and its lines are that, and they are kept 84 months — and 24 months covers the KSA labour-claim limitation window (one year from the end of the relationship) with a margin. `[COUNSEL]` |
| **S** | Statutory | **84 months** from RecordDate, `disposition='Keep'` (§12.4) | **Overridden.** An erasure request does **not** reach an S row. | Kept. Amounts and totals must survive so historical reports still reconcile; the person is removed via `employees`. |
| **E** | Evidence | **indefinite**, `disposition='Keep'` (§12.4) | **Partial** — the personal payload is redactable, the envelope is not | Null `personal_data`, `before` and `after`, set `personal_data_erased_at`, keep `envelope_hash`. Checkpoints still verify. |
| **T** | Transient | **1 month after expiry** for auth (§12.4) | N/A — expires faster than any request | **Hard delete** by TTL job. |

### 2.1 How a class maps onto a `retention_policies` row

| Class | `trigger_event` | `disposition` | Notes |
|---|---|---|---|
| **R** | — | `Keep` | No row needed; reference data is never disposed. |
| **C** | `SoftDelete` (of the tenant) | `Purge` | |
| **P** | `Separation` | `Anonymise` | |
| **O** | `RecordDate` | `Purge` after the minimum; `Anonymise` while a P/S row depends on it | |
| **S** | `RecordDate` | `Anonymise` | An erasure request **cannot** shorten `minimum_retention_months`. |
| **E** | `RecordDate` | `Anonymise` (personal payload only) | The envelope is `Keep`. |
| **T** | `Expiry` | `Purge` | Short TTL; usually no policy row, a TTL job instead. |

A class letter is a summary. **The row is the rule**, and the purge job reads the row.

### 2.2 Partitioning changes how disposal happens

Five tables are monthly RANGE-partitioned (§19.3), so for them **disposal is a partition operation,
not a `DELETE`** — which is the difference between an instant `DETACH` and a multi-hour scan that
bloats the table.

| Table | Partition key | Online window | Disposal |
|---|---|---|---|
| `attendance_punches` | `occurred_at` | 24 months | detach → **export to `files`** → drop |
| `attendance_days` | `work_date` | **24 months** | detach → export → drop |
| `timesheet_entries` | `work_date` | **24 months** | detach → export → drop |
| `background_job_items` | `created_at` | 90 days | detach and drop |
| `audit_logs` | `created_at` | indefinite | detach to **cold storage**; **PDPL erasure nulls `personal_data`/`before`/`after` in place and never drops a partition** |

**The retention window and the partition window now agree at 24 months** on all three attendance and
timesheet tables. Revision 5 had 24 in §12.4 and 36 in §19.3; revision 6 resolved it **downward**,
which is the direction that does not over-retain.

**Three consequences for whoever runs retention:**

1. **`audit_logs` is never dropped, only redacted.** Erasure nulls the personal payload in place and
   leaves `envelope_hash` intact so every Merkle checkpoint still verifies. A dropped audit
   partition would break the chain and destroy the evidence that the purge itself happened.
2. **An exported partition is still retained data.** "Detach and export to `files`" moves it, it
   does not dispose of it — the `files` row inherits the same `retention_policies` obligation, and
   the blob sits in object storage that is currently in a **US region** (§4 item 5).

3. **Partition children are a security surface.** Privileges are **not** inherited for direct
   access: each child has its own ACL, and a child with a grant and no policy is an unfiltered copy
   of millions of rows. `PartitionMaintenance` is the only process that creates tables in
   production, so one careless `GRANT` in that job is the whole leak. The job therefore issues
   **no `GRANT` of any kind**, and the ratchet asserts RLS forced and zero direct grants on every
   child.

**Partition headroom is an operational alarm, not a retention concern**, but it fails the same way.
`PartitionMaintenance` pre-creates three months ahead and alerts under two. Each parent has a
`DEFAULT` partition, and **the alert fires on the first row landing in it, not on a threshold** — a
populated default is a countdown, because recovery means `DETACH … CONCURRENTLY`, create the month,
`INSERT … SELECT` in batches, then attach, and **attaching over a populated default takes
`ACCESS EXCLUSIVE` and a full validation scan** — minutes of blocked writes on
`attendance_punches`. Scheduled work, not improvised.

**The three rules that make this work**

1. **`S` beats an erasure request.** When a data subject asks for erasure, the purge job satisfies
   `P` and `O`, defers `S` to its minimum, and partially satisfies `E`. The refusal and its reason
   are written to `retention_purge_audits` — a refusal you cannot evidence is a breach.
2. **Anonymise by default, purge by exception.** Deleting an `employees` row would orphan payroll
   slips, GOSI filings and GL lines that must reconcile for years. Anonymisation keeps the graph
   intact and removes the person.
3. **A blob and its row are disposed separately.** `files.purge_state` goes `Active → PendingPurge
   → Purged`: the blob is deleted, the row keeps `sha256` as proof the document existed, and
   `retention_purge_audits` records the act (`TARGET_SCHEMA.md` §A).

**Where the purge job must reach inside JSON.** Composite FKs cannot guard JSON, so these are
listed explicitly and each needs its own `retention_policies` row
(`TARGET_SCHEMA.md` §6 Risk 3):
`nitaqat_snapshots.employee_breakdown` · `approval_requests.payload` · `payroll_issues.evidence` ·
`audit_logs.personal_data` · `background_jobs.payload` / `result` · `users.notification_prefs`.
(`employees.dependents` is no longer on this list: revision 3 dropped `EmployeeDependents` outright
rather than folding it into JSON.)

---

## 3. Per-domain ownership and retention

### A. Platform, tenancy and shared infrastructure

**Owner:** Platform Engineering · **Schema change approved by:** Data Architect + Platform lead
(a change to `tenants` or `files` also needs the **DPO**)

| Table | Class | Note |
|---|---|---|
| `tenants` | C | Soft-deleted by `soft_deleted_at`, purged by `purged_at`. Purging cascades to every `T` row: destructive and irreversible — owner authorisation required. |
| `tenant_settings` | C | No personal data. If any section ever holds one, this class changes. |
| `number_sequences` | C | Never reset; a reused number breaks an audit trail. |
| `files` | P (blob) / E (row) | The single chokepoint for PDPL blob erasure. |
| `platform_users` | P (platform scope) | Platform operators. **Outside every tenant purge** — a tenant deletion must never touch it. Its own rule: **12 months from `deleted_at`, Anonymise, Platform owner** (§12.4) — an operator is a data subject too. |
| `retention_policies` | R | **The rule table itself.** Platform rows are reference; a tenant row may only **lengthen** a platform period. Changing a row changes what the purge job does — **DPO approval, always**. |
| `data_protection_keys` | C | **Never delete a key.** Every secret encrypted under it becomes unrecoverable. |

### B. Identity and access

**Owner:** Platform Engineering · **Approved by:** Platform lead + **Security review mandatory**
(any change to `users`, `user_roles`, `auth_sessions` or `auth_tokens`)

| Table | Class | Note |
|---|---|---|
| `users` | P | `deleted_at` soft-deletes; anonymise, never hard-delete — `audit_logs.actor_user_id` and `approval_actions.actor_user_id` must still resolve. **`tenant_id` is `NOT NULL`** as of revision 4. |
| `roles` | C | |
| `permissions` | R | Platform catalogue; changes ship with the feature they gate. |
| `role_permissions` | C | |
| `user_roles` | C | Keep expired grants for the evidence class window — "who could approve this, then". |
| `auth_sessions` | T | Delete 1 month after expiry (§12.4). Holds `ip`, `user_agent` and a push token. One of only two nullable-`tenant_id` tables, because it serves `platform_users` too. |
| `auth_tokens` | T | Delete after expiry or consumption. Nullable `tenant_id` — see `auth_sessions`. |
| `permission_grantor_records` | E | Delegated granting authority. Keep expired and revoked rows: they explain why a past grant was permitted. **Changing this table is a privilege-escalation surface — Security review, always.** |

### C. Organisation

**Owner:** HR Core Engineering · **Approved by:** HR Core lead (`cost_centers` also needs
**Payroll Engineering**, because GL lines reference it forever)

| Table | Class | Note |
|---|---|---|
| `companies` | C, treated as S | The counterparty on every statutory filing. Retain for the S minimum. Soft-deleted only (`soft_deleted_at`). |
| `company_pay_policies` | S | Contractual above-floor pay; it is the basis of a paid amount, so it lives as long as the payslip does. |
| `branches` | C | |
| `departments` | C | |
| `cost_centers` | S | Referenced by posted GL lines. Retire with `is_active`; never delete. |
| `designations` | C | |
| `grades` | C | |
| `public_holidays` | R | Platform rows are reference; tenant rows are configuration. |

### D. Employees, history, documents and letters

**Owner:** HR Core Engineering · **Approved by:** HR Core lead + **DPO** (this domain is the bulk
of the tenant's personal data)

| Table | Class | Note |
|---|---|---|
| `employees` | P | **Anonymise, never delete.** `privacy_status` drives it: `Normal → PendingErasure → Anonymised`. Keep `id`, `employee_number` and dates; null names, `national_id`/`iqama_no`, `dob` and contact, then set `redacted_at`. |
| `employee_assignments` | P | Anonymise with the employee; the org history itself may be retained. |
| `employee_salaries` | S | It is the basis of a statutory figure. Anonymise only after the S minimum. |
| `employee_contracts` | S | Labour-law evidence. |
| `employee_bank_accounts` | S, high sensitivity | A financial instruction. The IBAN is a purge target the moment the S minimum passes. |
| `employee_documents` | P, or S for contracts and verifiable letters | The blob goes through `files`; the row survives with its `sha256`. |
| `document_templates` | C, S for used versions | A version named by an issued document is never deleted. |

### E. Statutory reference

**Owner:** **Compliance (KSA)** · **Approved by:** Compliance + Payroll Engineering.
**A value change needs a named verifier** recorded in `statutory_rules.verified_by` / `verified_at`.

| Table | Class | Note |
|---|---|---|
| `statutory_rules` | R | **Never deleted, never edited.** A change is a new effective-dated row. Editing one silently changes history. |
| `statutory_rule_bands` | R | Same. |

**Outstanding compliance obligation (`TARGET_SCHEMA.md` §E, Q3):** GCC-national rates and the
Entrant2024 annuities ladder are unconfirmed. Until counsel confirms them, the run **blocks**
rather than guesses. Do not let anyone "unblock" this with a default.

### F. Payroll

**Owner:** Payroll Engineering · **Approved by:** Payroll lead + Data Architect.
**Any change touching `payroll_inputs`, `payroll_slips` or `payroll_slip_lines` additionally needs
a passing payroll golden-file test** (`TARGET_SCHEMA.md` §6 Risk 6).

| Table | Class | Note |
|---|---|---|
| `pay_components` | C, treated as S | Slip lines name the code forever. `is_system` rows are immutable. |
| `payroll_runs` | S | |
| `payroll_slips` | S | Frozen on lock. Anonymise after the minimum; **keep every amount**, including YTD. |
| `payroll_slip_lines` | S | Frozen. The `statutory_rule_id` freeze is the audit trail; never null it. |
| `payroll_inputs` | S | Cancellation bumps `revision`; rows are never deleted. |
| `payroll_issues` | E | `evidence jsonb` may contain personal data — purge target. |

### G. WPS · H. GL export

**Owner:** Payroll Engineering · **Approved by:** Payroll lead + **Finance sign-off for GL**

| Table | Class | Note |
|---|---|---|
| `wps_batches` | S | The filed SIF and its hash. |
| `wps_lines` | S, high sensitivity | Holds IBAN and national id. Anonymise after the S minimum; keep amounts. |
| `gl_mappings` | C | A mapping used by a posted journal is not deletable. |
| `gl_journals` | S | Corrections are reversals. |
| `gl_journal_lines` | S | |
| `gl_period_closes` | E | The record that finance closed and who reopened. |

### I. Loans and advances

**Owner:** Payroll Engineering · **Approved by:** Payroll lead

| Table | Class | Note |
|---|---|---|
| `loans` | S | It produces a deduction on a statutory payslip. |
| `loan_installments` | S | |

### J. Leave

**Owner:** HR Core Engineering · **Approved by:** HR Core lead (any change to `leave_ledger` also
needs **Payroll Engineering** — leave balance is an EOS input)

| Table | Class | Note |
|---|---|---|
| `leave_types` | C | Statutory types are not deletable. |
| `leave_requests` | O, P where a medical reason is recorded | |
| `leave_ledger` | S | Append-only. A balance feeds EOS and final settlement, so it lives as long as the settlement does. |

### K. Attendance, overtime and timesheets

**Owner:** HR Core Engineering · **Approved by:** HR Core lead

| Table | Class | Note |
|---|---|---|
| `shifts` | C | |
| `shift_assignments` | O | |
| `attendance_devices` | C | `api_key_hash` is a credential — rotate, do not archive. |
| `attendance_punches` | O, **high sensitivity** | `lat`/`lng` is location data about a person. Shortest defensible retention; purge aggressively once the day is locked. |
| `attendance_days` | O | The locked computed day; a payroll input once `locked_run_id` is set. |
| `overtime_requests` | S | It becomes a statutory payslip line. |
| `timesheets` | O | |
| `timesheet_entries` | O | Billable time may have a contractual retention of its own — `[COUNSEL]`. |
| `timesheet_day_reconciliations` | O | |

### L. End of service and final settlement

**Owner:** Payroll Engineering · **Approved by:** Payroll lead + **Compliance (KSA)** — the
calculation is Art. 84/85/87/77

| Table | Class | Note |
|---|---|---|
| `eos_calculations` | S | `rules_snapshot` is what lets a disputed figure be reproduced years later. Never strip it. |
| `final_settlements` | S | |
| `final_settlement_lines` | S | |

### M. Nitaqat

**Owner:** Compliance (KSA) · **Approved by:** Compliance + HR Core lead

| Table | Class | Note |
|---|---|---|
| `nitaqat_grid` | R | Grid revisions are new versions, never edits. |
| `nitaqat_snapshots` | E, **with P content** | `employee_breakdown` holds employee ids inside JSON. **Explicit purge rule required.** |

### N. GOSI registration and filing

**Owner:** Compliance (KSA) · **Approved by:** Compliance + Payroll lead

| Table | Class | Note |
|---|---|---|
| `employee_gosi_registrations` | S | `registered_contributory_wage` is the GOSI-side truth; do not reconcile it away. |
| `gosi_filings` | S | Frozen on `Filed`. A non-zero `variance_amount` without a `variance_reason` is a defect, not a data state. |

### O. Approvals · P. Notifications

**Owner:** HR Core Engineering · **Approved by:** HR Core lead. **Adding a `request_type` value
does not need a table and does not need the Data Architect** — that is the point of the design.

| Table | Class | Note |
|---|---|---|
| `approval_workflows` | C | |
| `approval_requests` | E | `payload jsonb` may hold salary and personal data — purge target. |
| `approval_actions` | E | Who decided what, and on whose behalf. |
| `approval_delegations` | E | Explains why someone else approved. |
| `notifications` | T | TTL delete. |
| `notification_deliveries` | T | `destination` is masked precisely so this is not a shadow contact database. |

### Q. Audit

**Owner:** Platform Engineering · **Approved by:** Platform lead + **DPO** + **Security**.
**`payroll_audit_logs` additionally needs Payroll Engineering.**

| Table | Class | Note |
|---|---|---|
| `audit_logs` | E | Envelope permanent; `personal_data` purgeable, which is exactly why it is a separate column. |
| `payroll_audit_logs` | E / S | Row-chained, trigger-protected. **Never purged while the payroll it describes is retained.** |
| `retention_purge_audits` | E | The table that proves the others were purged. It outlives them. |

**Standing rule: nobody deletes from an audit table.** The append-only triggers make it an error,
not a judgement call. A `DELETE` here is an incident.

### R. Jobs

**Owner:** Platform Engineering · **Approved by:** Platform lead

| Table | Class | Note |
|---|---|---|
| `background_jobs` | T, or E when referenced by `retention_purge_audits` | The TTL job must check the reference before deleting. |
| `background_job_items` | T | May hold row-level personal data from a failed import — TTL aggressively. |

---

## 4. What the DPO must still decide

All of these become **rows in `retention_policies`**, not paragraphs here.

| # | Open item | Blocks |
|---|---|---|
| 0 | ~~Retention vs partition window~~ — **closed in revision 6**, resolved at 24 months in both, with the legal basis stated. | — |
| 0b | ~~`retention_policies` overlapping platform defaults~~ — **closed in revision 7.** The `EXCLUDE` that was meant to stop two policies for one entity covering the same dates **skipped the platform rows entirely**, because their `tenant_id` is NULL and a NULL operand exempts a row from the constraint. Those are precisely the rows the purge job falls back to when a tenant has no override, so two overlapping defaults would have been accepted, resolved non-deterministically, and silently changed how long personal data was kept. The subject is now `COALESCE(tenant_id, <nil uuid>)` (`CONVENTIONS.md` §5). **No DPO action — this was a defect, not a policy change** — but it is worth knowing that the guarantee this table's integrity rested on did not exist until revision 7. | — |
| 1 | **Legal confirmation of the seeded periods.** Revision 6 states the *reasoning* for each — which makes them reviewable, and is the real improvement — but reasoning is not legal advice. The 84-month payroll and 90-day tenant figures remain the product's **published commitments**, already in front of customers, rather than confirmed minima. | Nothing technically; a **compliance exposure that exists today**. |
| 2 | Whether **`attendance_punches` location data** needs a shorter, separate period than the rest of class O. | One policy row. |
| 3 | The **anonymisation recipe per table**: which columns are nulled, which are hashed, which survive. §12.3 gives the shape (clear identifying columns, set `privacy_status` and `redacted_at`, retain `employee_number`, keep every FK) but not the per-table column list. | The anonymisation path of the purge job. |
| 4 | Whether a **tenant offboarding** purges or exports. Purging a tenant destroys `S` rows a regulator may still ask the tenant for. | Tenant deletion, currently unimplementable safely. |
| 5 | Retention of the **`files` blob store** in its current region. Object storage sits in a US region — a data-residency question for KSA personal data, and a DPO decision, not an engineering one. | Storage configuration. |
| 6 | Mapping §12.4's four `owner_role` values (HR, Finance, Platform, Compliance) to actual humans. | Accountability for every purge decision. |
| 7 | ~~`platform_users` retention~~ — **closed in revision 5**: 12 months from SoftDelete, Anonymise, Platform owner. | — |
| 8 | **Who runs the restore drill, and how often.** §19.6 makes a restore drill a go-live gate with six recorded proofs, and alerts when a drill is older than 90 days — but names no owner for it. Backup health without a named owner is the gate that quietly lapses. | Recoverability |

Items 1–4 and 6 are prerequisites for the retention job. Item 5 is a live exposure, not a future one.
