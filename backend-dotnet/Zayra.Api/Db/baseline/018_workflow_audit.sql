-- =============================================================================
-- 018_workflow_audit.sql
-- KynexOne baseline schema — TARGET_SCHEMA.md revision 6.
--
-- Domain O  Approvals ................. approval_workflows, approval_requests,
--                                       approval_actions, approval_delegations
-- Domain P  Notifications ............. notifications,
--                                       notification_deliveries
-- Domain Q  Audit ..................... audit_logs, payroll_audit_logs,
--                                       retention_purge_audits
-- Domain R  Jobs ...................... background_jobs, background_job_items
--
-- Tables only. Foreign keys, CHECK sets (§9) and the §11 invariants live in
-- 021_constraints_g_r.sql.
--
-- The three audit tables are deliberately FOREIGN-KEY FREE (§8.2, §Q) so the
-- evidence outlives every purge: tenant_id, entity_id, job_id, actor ids and
-- rule_key are plain columns, and §16.3 owns orphan detection. They are also
-- append-only (§6): BEFORE UPDATE/DELETE triggers raise, with the single
-- exception of the PDPL redaction UPDATE shape described on audit_logs.
-- =============================================================================

-- -----------------------------------------------------------------------------
-- O. Approvals
-- -----------------------------------------------------------------------------

CREATE TABLE approval_workflows (
    id                      uuid           NOT NULL,
    tenant_id               uuid           NOT NULL,
    company_id              uuid,
    request_type            varchar(40)    NOT NULL,
    name                    text           NOT NULL,
    -- [{order, approver_rule, amount_threshold, sla_hours}] as one versioned
    -- unit written and read by the approval engine
    steps                   jsonb          NOT NULL DEFAULT '[]'::jsonb,
    is_active               boolean        NOT NULL DEFAULT true,
    created_at              timestamptz    NOT NULL DEFAULT now(),
    created_by              uuid,
    updated_at              timestamptz,
    updated_by              uuid,
    CONSTRAINT pk_approval_workflows            PRIMARY KEY (id),
    CONSTRAINT uq_approval_workflows__tenant_id UNIQUE (tenant_id, id),
    -- NULLS NOT DISTINCT: the tenant-wide definition (company_id IS NULL) is
    -- itself a single row per request type.
    CONSTRAINT uq_approval_workflows__request_type
        UNIQUE NULLS NOT DISTINCT (tenant_id, company_id, request_type)
);

COMMENT ON TABLE approval_workflows IS
    'Defines the ordered approval steps, approver rules, amount thresholds and service levels for one request type, optionally narrowed to a company. @tier:T @owner:HR @retention:tenant-lifecycle';

CREATE TABLE approval_requests (
    id                          uuid           NOT NULL,
    tenant_id                   uuid           NOT NULL,
    workflow_id                 uuid           NOT NULL,
    requester_user_id           uuid           NOT NULL,
    employee_id                 uuid,
    request_type                varchar(40)    NOT NULL,
    -- polymorphic subject (§16.1): subject_type is drawn from the request_type
    -- set and mapped to one table by a dictionary asserted in code
    subject_type                varchar(40)    NOT NULL,
    subject_id                  uuid           NOT NULL,
    status                      varchar(40)    NOT NULL DEFAULT 'Draft',
    current_step                integer        NOT NULL DEFAULT 0,
    -- denormalised from the current step of workflow_snapshot so the inbox has
    -- something to index (§15). Written ONLY by the approval engine, on step
    -- advance, return, escalation and delegation, and re-derived nightly.
    -- Plain uuids: §8 registers no FK for either column.
    current_approver_user_id    uuid,
    current_approver_employee_id uuid,
    payload                     jsonb,
    -- frozen at creation: a workflow edited mid-flight must not change a live
    -- request (§15)
    workflow_snapshot           jsonb          NOT NULL,
    due_at                      timestamptz,
    submitted_at                timestamptz,
    decided_at                  timestamptz,
    created_at                  timestamptz    NOT NULL DEFAULT now(),
    created_by                  uuid,
    updated_at                  timestamptz,
    updated_by                  uuid,
    CONSTRAINT pk_approval_requests            PRIMARY KEY (id),
    CONSTRAINT uq_approval_requests__tenant_id UNIQUE (tenant_id, id)
);

COMMENT ON TABLE approval_requests IS
    'Is the single authoritative record of whether something was approved, carrying the frozen workflow it runs against, the current step and the current approver the inbox filters on. @tier:T @owner:HR @retention:84m-keep';

CREATE TABLE approval_actions (
    id                      uuid           NOT NULL,
    tenant_id               uuid           NOT NULL,
    request_id              uuid           NOT NULL,
    actor_user_id           uuid           NOT NULL,
    on_behalf_of_user_id    uuid,
    action                  varchar(40)    NOT NULL,
    step                    integer        NOT NULL,
    comment                 text,
    acted_at                timestamptz    NOT NULL DEFAULT now(),
    created_at              timestamptz    NOT NULL DEFAULT now(),
    created_by              uuid,
    updated_at              timestamptz,
    updated_by              uuid,
    CONSTRAINT pk_approval_actions            PRIMARY KEY (id),
    CONSTRAINT uq_approval_actions__tenant_id UNIQUE (tenant_id, id)
);

COMMENT ON TABLE approval_actions IS
    'Records each approve, reject, return, comment or escalate decision taken on an approval request, including the person it was taken on behalf of. @tier:T @owner:HR @retention:84m-keep';

-- Not effective-dated in the §5 sense: two delegations covering different
-- request_types may legitimately overlap in time, so there is no EXCLUDE.
CREATE TABLE approval_delegations (
    id                      uuid           NOT NULL,
    tenant_id               uuid           NOT NULL,
    delegator_user_id       uuid           NOT NULL,
    delegate_user_id        uuid           NOT NULL,
    effective_from          date           NOT NULL,
    effective_to            date,
    request_types           text[]         NOT NULL DEFAULT '{}'::text[],
    reason                  text,
    is_active               boolean        NOT NULL DEFAULT true,
    revoked_at              timestamptz,
    created_at              timestamptz    NOT NULL DEFAULT now(),
    created_by              uuid,
    updated_at              timestamptz,
    updated_by              uuid,
    CONSTRAINT pk_approval_delegations            PRIMARY KEY (id),
    CONSTRAINT uq_approval_delegations__tenant_id UNIQUE (tenant_id, id)
);

COMMENT ON TABLE approval_delegations IS
    'Time-boxes the handover of one user approval authority to another for a named set of request types. @tier:T @owner:HR @retention:84m-keep';

-- -----------------------------------------------------------------------------
-- P. Notifications
-- -----------------------------------------------------------------------------

CREATE TABLE notifications (
    id                      uuid           NOT NULL,
    tenant_id               uuid           NOT NULL,
    user_id                 uuid           NOT NULL,
    category                varchar(40)    NOT NULL,
    title                   text           NOT NULL,
    body                    text,
    link                    text,
    source_type             varchar(40),
    source_id               uuid,
    idempotency_key         text           NOT NULL,
    read_at                 timestamptz,
    created_at              timestamptz    NOT NULL DEFAULT now(),
    created_by              uuid,
    updated_at              timestamptz,
    updated_by              uuid,
    CONSTRAINT pk_notifications            PRIMARY KEY (id),
    CONSTRAINT uq_notifications__tenant_id UNIQUE (tenant_id, id),
    CONSTRAINT uq_notifications__idempotency_key
        UNIQUE (tenant_id, idempotency_key)
);

COMMENT ON TABLE notifications IS
    'Is one in-app inbox item for a user, deduplicated by idempotency key so a retried producer cannot notify twice. @tier:T @owner:Platform @retention:12m-purge';

CREATE TABLE notification_deliveries (
    id                      uuid           NOT NULL,
    tenant_id               uuid           NOT NULL,
    notification_id         uuid           NOT NULL,
    channel                 varchar(40)    NOT NULL,
    status                  varchar(40)    NOT NULL DEFAULT 'Queued',
    -- masked at write; the full address is never stored here
    destination             text,
    attempts                integer        NOT NULL DEFAULT 0,
    next_attempt_at         timestamptz,
    provider_message_id     text,
    last_error              text,
    sent_at                 timestamptz,
    delivered_at            timestamptz,
    dead_lettered_at        timestamptz,
    created_at              timestamptz    NOT NULL DEFAULT now(),
    created_by              uuid,
    updated_at              timestamptz,
    updated_by              uuid,
    CONSTRAINT pk_notification_deliveries            PRIMARY KEY (id),
    CONSTRAINT uq_notification_deliveries__tenant_id UNIQUE (tenant_id, id)
);

COMMENT ON TABLE notification_deliveries IS
    'Tracks each outbound email, SMS or push attempt for a notification through its retries to delivery, suppression or the dead-letter terminal state. @tier:T @owner:Platform @retention:12m-purge';

-- -----------------------------------------------------------------------------
-- Q. Audit
-- -----------------------------------------------------------------------------

-- PARTITIONED: RANGE (created_at), monthly, retained per retention_policies
-- (indefinite) — partitions are detached to cold storage, never dropped, and
-- PDPL erasure nulls personal_data/before/after in place (§19.3).
--
-- Unique constraints must carry the partition key, so the §Conventions
-- UNIQUE (chain_key, seq) becomes UNIQUE (chain_key, seq, created_at). Stated
-- plainly in §19.3 as a real if small weakening: cross-partition uniqueness of
-- seq is guaranteed at ALLOCATION instead, by a per-tenant Postgres sequence
-- that never reuses a value, and the checkpointer walks contiguous seq ranges
-- and raises on a gap or a duplicate, recording what it saw in observed_gaps.
--
-- FK-free by design. Append-only, with exactly one permitted UPDATE shape:
-- the PDPL redaction that nulls personal_data, before and after and stamps
-- personal_data_erased_at. envelope_hash is NEVER rewritten -- the row keeps
-- personal_data_hash, before_hash and after_hash so a verifier can RECOMPUTE
-- the envelope hash from the surviving columns plus those digests and bind it
-- to a Merkle root published before the erasure. Comparing a stored hash with
-- itself would prove nothing (§12.3, §14).
CREATE TABLE audit_logs (
    id                      uuid           NOT NULL,
    -- NULL = a platform-scope event; policy shape (b) in §19.2
    tenant_id               uuid,
    company_id              uuid,
    record_kind             varchar(40)    NOT NULL DEFAULT 'Event',
    category                varchar(40),
    -- the Merkle chain this row belongs to (one per tenant, 'platform' for
    -- tenant-less rows); part of the uniqueness key with seq
    chain_key               varchar(64)    NOT NULL,
    seq                     bigint         NOT NULL,
    -- the monthly partition key
    created_at              timestamptz    NOT NULL DEFAULT now(),
    action                  varchar(64),
    entity                  varchar(64),
    -- deliberately unconstrained: an audit row must be able to describe a row
    -- that no longer exists (§16.4)
    entity_id               uuid,
    actor_user_id           uuid,
    on_behalf_of_user_id    uuid,
    correlation_id          uuid,
    hash_algorithm          varchar(40)    NOT NULL DEFAULT 'sha256',
    -- the changed fields only; audit is not a second copy of the database
    before                  jsonb,
    after                   jsonb,
    -- purgeable, and it carries ip and user_agent, which are personal data
    personal_data           jsonb,
    personal_data_hash      text,
    before_hash             text,
    after_hash              text,
    -- H(immutable envelope || personal_data_hash || before_hash || after_hash)
    envelope_hash           text,
    personal_data_erased_at timestamptz,
    -- checkpoint rows only (record_kind = 'Checkpoint')
    covers_seq_from         bigint,
    covers_seq_to           bigint,
    -- the checkpointer is bounded by time as well as by seq: a seq-only walk
    -- prunes no partitions and scans an ever-growing range
    covers_created_from     timestamptz,
    covers_created_to       timestamptz,
    root_hash               text,
    prev_checkpoint_hash    text,
    -- seq values the checkpointer expected in the range and did not find
    observed_gaps           bigint[],
    CONSTRAINT pk_audit_logs PRIMARY KEY (id, created_at),
    CONSTRAINT uq_audit_logs__tenant_id UNIQUE (tenant_id, id, created_at),
    CONSTRAINT uq_audit_logs__chain_seq UNIQUE (chain_key, seq, created_at)
);

COMMENT ON TABLE audit_logs IS
    'Is the single append-only log of every audited event outside payroll money movement, made tamper-evident by periodic Merkle checkpoint rows and verifiable after a PDPL erasure because each row keeps the digests of the payload it no longer holds. @tier:T/P @owner:Compliance @retention:indefinite-keep';

-- Row-chained rather than checkpointed: the write rate is low and the
-- evidentiary bar is highest (§Q). Not partitioned. FK-free. Append-only,
-- enforced by trg_payroll_audit_logs_append_only, which exists today and is
-- carried into the baseline.
CREATE TABLE payroll_audit_logs (
    id                      uuid           NOT NULL,
    tenant_id               uuid           NOT NULL,
    run_id                  uuid,
    entity                  varchar(64)    NOT NULL,
    entity_id               uuid,
    action                  varchar(64)    NOT NULL,
    seq                     bigint         NOT NULL,
    user_id                 uuid,
    correlation_id          uuid,
    hash_algorithm          varchar(40)    NOT NULL DEFAULT 'sha256',
    before                  jsonb,
    after                   jsonb,
    metadata                jsonb,
    prev_hash               text,
    entry_hash              text           NOT NULL,
    created_at              timestamptz    NOT NULL DEFAULT now(),
    CONSTRAINT pk_payroll_audit_logs            PRIMARY KEY (id),
    CONSTRAINT uq_payroll_audit_logs__tenant_id UNIQUE (tenant_id, id),
    CONSTRAINT uq_payroll_audit_logs__seq       UNIQUE (tenant_id, seq),
    CONSTRAINT uq_payroll_audit_logs__entry_hash UNIQUE (tenant_id, entry_hash)
);

COMMENT ON TABLE payroll_audit_logs IS
    'Is the separate, trigger-protected, row-chained log of every payroll state change, where each entry hash commits to its predecessor so the money path carries an unbroken chain rather than a checkpoint. @tier:T @owner:Compliance @retention:indefinite-keep';

-- FK-free: job_id is a plain uuid and rule_key a plain text match to
-- retention_policies.rule_key, so the evidence outlives the job and the policy
-- it describes (§8.2, §12.3).
CREATE TABLE retention_purge_audits (
    id                      uuid           NOT NULL,
    tenant_id               uuid           NOT NULL,
    job_id                  uuid,
    rule_key                varchar(64)    NOT NULL,
    entity                  varchar(64)    NOT NULL,
    entity_id               uuid,
    disposition             varchar(40)    NOT NULL,
    outcome                 varchar(40)    NOT NULL,
    dry_run                 boolean        NOT NULL DEFAULT false,
    retention_until         date,
    correlation_id          uuid,
    details                 jsonb,
    created_at              timestamptz    NOT NULL DEFAULT now(),
    created_by              uuid,
    CONSTRAINT pk_retention_purge_audits            PRIMARY KEY (id),
    CONSTRAINT uq_retention_purge_audits__tenant_id UNIQUE (tenant_id, id)
);

COMMENT ON TABLE retention_purge_audits IS
    'Is the append-only evidence that a PDPL retention rule ran, naming the rule, the record, the disposition applied and whether the run was a rehearsal. @tier:T @owner:Compliance @retention:indefinite-keep';

-- -----------------------------------------------------------------------------
-- R. Jobs
-- -----------------------------------------------------------------------------

CREATE TABLE background_jobs (
    id                      uuid           NOT NULL,
    -- NULL = a platform-scope job; policy shape (b) in §19.2, plus a kynex_job
    -- policy so the leased-queue claim can see queued platform work
    tenant_id               uuid,
    source_file_id          uuid,
    kind                    varchar(64)    NOT NULL,
    status                  varchar(40)    NOT NULL DEFAULT 'Queued',
    correlation_id          uuid,
    idempotency_key         text,
    payload                 jsonb,
    result                  jsonb,
    -- progress as two counters rather than a blob, so a queue screen can read
    -- it without parsing JSON (§8 JSON policy)
    progress_current        integer        NOT NULL DEFAULT 0,
    progress_total          integer,
    attempts                integer        NOT NULL DEFAULT 0,
    last_error              text,
    -- the worker lease lives on the job row; there is no heartbeat table (§R)
    lease_owner             varchar(128),
    heartbeat_at            timestamptz,
    lease_expires_at        timestamptz,
    source_file_sha256      text,
    scheduled_at            timestamptz,
    started_at              timestamptz,
    completed_at            timestamptz,
    created_at              timestamptz    NOT NULL DEFAULT now(),
    created_by              uuid,
    updated_at              timestamptz,
    updated_by              uuid,
    CONSTRAINT pk_background_jobs            PRIMARY KEY (id),
    CONSTRAINT uq_background_jobs__tenant_id UNIQUE (tenant_id, id),
    CONSTRAINT uq_background_jobs__idempotency_key
        UNIQUE NULLS NOT DISTINCT (tenant_id, idempotency_key)
);

COMMENT ON TABLE background_jobs IS
    'Is one asynchronous, resumable and idempotent unit of work with its payload, lease, heartbeat, attempt count and result, covering every import, export, sync and retention run in the product. @tier:T/P @owner:Platform @retention:6m-purge';

-- PARTITIONED: RANGE (created_at), monthly, 90-day online window (§19.3);
-- partitions are detached and dropped. Primary key (id, created_at); tenant
-- key (tenant_id, id, created_at). Exempt from row stamping.
CREATE TABLE background_job_items (
    id                      uuid           NOT NULL,
    tenant_id               uuid,
    job_id                  uuid           NOT NULL,
    status                  varchar(40)    NOT NULL DEFAULT 'Pending',
    -- the monthly partition key
    created_at              timestamptz    NOT NULL DEFAULT now(),
    row_ref                 text,
    entity_id               uuid,
    correlation_id          uuid,
    error_code              varchar(64),
    error_message           text,
    processed_at            timestamptz,
    CONSTRAINT pk_background_job_items PRIMARY KEY (id, created_at),
    CONSTRAINT uq_background_job_items__tenant_id
        UNIQUE (tenant_id, id, created_at)
);

COMMENT ON TABLE background_job_items IS
    'Records the per-row outcome of a background job so a partially failed import names the rows that failed and why, rather than failing as a whole. @tier:T/P @owner:Platform @retention:6m-purge';
