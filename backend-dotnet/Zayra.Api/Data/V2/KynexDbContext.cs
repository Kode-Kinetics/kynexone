using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data.V2.Entities;

namespace Zayra.Api.Data.V2;

public partial class KynexDbContext : DbContext
{
    public KynexDbContext(DbContextOptions<KynexDbContext> options)
        : base(options)
    {
    }

    public virtual DbSet<ApprovalAction> ApprovalActions { get; set; }

    public virtual DbSet<ApprovalDelegation> ApprovalDelegations { get; set; }

    public virtual DbSet<ApprovalRequest> ApprovalRequests { get; set; }

    public virtual DbSet<ApprovalWorkflow> ApprovalWorkflows { get; set; }

    public virtual DbSet<AttendanceDevice> AttendanceDevices { get; set; }

    public virtual DbSet<AuthSession> AuthSessions { get; set; }

    public virtual DbSet<AuthToken> AuthTokens { get; set; }

    public virtual DbSet<BackgroundJob> BackgroundJobs { get; set; }

    public virtual DbSet<Branch> Branches { get; set; }

    public virtual DbSet<Company> Companies { get; set; }

    public virtual DbSet<CompanyPayPolicy> CompanyPayPolicies { get; set; }

    public virtual DbSet<CostCenter> CostCenters { get; set; }

    public virtual DbSet<DataProtectionKey> DataProtectionKeys { get; set; }

    public virtual DbSet<Department> Departments { get; set; }

    public virtual DbSet<Designation> Designations { get; set; }

    public virtual DbSet<DocumentTemplate> DocumentTemplates { get; set; }

    public virtual DbSet<Employee> Employees { get; set; }

    public virtual DbSet<EmployeeAssignment> EmployeeAssignments { get; set; }

    public virtual DbSet<EmployeeBankAccount> EmployeeBankAccounts { get; set; }

    public virtual DbSet<EmployeeContract> EmployeeContracts { get; set; }

    public virtual DbSet<EmployeeDocument> EmployeeDocuments { get; set; }

    public virtual DbSet<EmployeeGosiRegistration> EmployeeGosiRegistrations { get; set; }

    public virtual DbSet<EmployeeSalary> EmployeeSalaries { get; set; }

    public virtual DbSet<EosCalculation> EosCalculations { get; set; }

    public virtual DbSet<File> Files { get; set; }

    public virtual DbSet<FinalSettlement> FinalSettlements { get; set; }

    public virtual DbSet<FinalSettlementLine> FinalSettlementLines { get; set; }

    public virtual DbSet<GlJournal> GlJournals { get; set; }

    public virtual DbSet<GlJournalLine> GlJournalLines { get; set; }

    public virtual DbSet<GlMapping> GlMappings { get; set; }

    public virtual DbSet<GlPeriodClose> GlPeriodCloses { get; set; }

    public virtual DbSet<GosiFiling> GosiFilings { get; set; }

    public virtual DbSet<Grade> Grades { get; set; }

    public virtual DbSet<LeaveLedger> LeaveLedgers { get; set; }

    public virtual DbSet<LeaveRequest> LeaveRequests { get; set; }

    public virtual DbSet<LeaveType> LeaveTypes { get; set; }

    public virtual DbSet<Loan> Loans { get; set; }

    public virtual DbSet<LoanInstallment> LoanInstallments { get; set; }

    public virtual DbSet<NitaqatGrid> NitaqatGrids { get; set; }

    public virtual DbSet<NitaqatSnapshot> NitaqatSnapshots { get; set; }

    public virtual DbSet<Notification> Notifications { get; set; }

    public virtual DbSet<NotificationDelivery> NotificationDeliveries { get; set; }

    public virtual DbSet<NumberSequence> NumberSequences { get; set; }

    public virtual DbSet<OvertimeRequest> OvertimeRequests { get; set; }

    public virtual DbSet<PayComponent> PayComponents { get; set; }

    public virtual DbSet<PayrollAuditLog> PayrollAuditLogs { get; set; }

    public virtual DbSet<PayrollInput> PayrollInputs { get; set; }

    public virtual DbSet<PayrollIssue> PayrollIssues { get; set; }

    public virtual DbSet<PayrollRun> PayrollRuns { get; set; }

    public virtual DbSet<PayrollSlip> PayrollSlips { get; set; }

    public virtual DbSet<PayrollSlipLine> PayrollSlipLines { get; set; }

    public virtual DbSet<Permission> Permissions { get; set; }

    public virtual DbSet<PermissionGrantorRecord> PermissionGrantorRecords { get; set; }

    public virtual DbSet<PlatformUser> PlatformUsers { get; set; }

    public virtual DbSet<PublicHoliday> PublicHolidays { get; set; }

    public virtual DbSet<RetentionPolicy> RetentionPolicies { get; set; }

    public virtual DbSet<RetentionPurgeAudit> RetentionPurgeAudits { get; set; }

    public virtual DbSet<Role> Roles { get; set; }

    public virtual DbSet<RolePermission> RolePermissions { get; set; }

    public virtual DbSet<Shift> Shifts { get; set; }

    public virtual DbSet<ShiftAssignment> ShiftAssignments { get; set; }

    public virtual DbSet<StatutoryRule> StatutoryRules { get; set; }

    public virtual DbSet<StatutoryRuleBand> StatutoryRuleBands { get; set; }

    public virtual DbSet<Tenant> Tenants { get; set; }

    public virtual DbSet<TenantSetting> TenantSettings { get; set; }

    public virtual DbSet<Timesheet> Timesheets { get; set; }

    public virtual DbSet<TimesheetDayReconciliation> TimesheetDayReconciliations { get; set; }

    public virtual DbSet<User> Users { get; set; }

    public virtual DbSet<UserRole> UserRoles { get; set; }

    public virtual DbSet<VEmployeeCurrent> VEmployeeCurrents { get; set; }

    public virtual DbSet<VLeaveBalance> VLeaveBalances { get; set; }

    public virtual DbSet<WpsBatch> WpsBatches { get; set; }

    public virtual DbSet<WpsLine> WpsLines { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder
            .HasPostgresExtension("btree_gist")
            .HasPostgresExtension("pg_stat_statements")
            .HasPostgresExtension("pg_trgm")
            .HasPostgresExtension("pgcrypto");

        modelBuilder.Entity<ApprovalAction>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pk_approval_actions");

            entity.ToTable("approval_actions", tb => tb.HasComment("Records each approve, reject, return, comment or escalate decision taken on an approval request, including the person it was taken on behalf of. @tier:T @owner:HR @retention:84m-keep"));

            entity.HasIndex(e => new { e.TenantId, e.ActorUserId }, "ix_approval_actions__actor_user_id");

            entity.HasIndex(e => new { e.TenantId, e.OnBehalfOfUserId }, "ix_approval_actions__on_behalf_of_user_id");

            entity.HasIndex(e => new { e.TenantId, e.RequestId }, "ix_approval_actions__request_id");

            entity.HasIndex(e => new { e.TenantId, e.Id }, "uq_approval_actions__tenant_id").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.ActedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("acted_at");
            entity.Property(e => e.Action)
                .HasMaxLength(40)
                .HasColumnName("action");
            entity.Property(e => e.ActorUserId).HasColumnName("actor_user_id");
            entity.Property(e => e.Comment).HasColumnName("comment");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.OnBehalfOfUserId).HasColumnName("on_behalf_of_user_id");
            entity.Property(e => e.RequestId).HasColumnName("request_id");
            entity.Property(e => e.Step).HasColumnName("step");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");

            entity.HasOne(d => d.User).WithMany(p => p.ApprovalActionUsers)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.ActorUserId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_approval_actions__actor_user_id");

            entity.HasOne(d => d.UserNavigation).WithMany(p => p.ApprovalActionUserNavigations)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.OnBehalfOfUserId })
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("fk_approval_actions__on_behalf_of_user_id");

            entity.HasOne(d => d.ApprovalRequest).WithMany(p => p.ApprovalActions)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.RequestId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_approval_actions__request_id");
        });

        modelBuilder.Entity<ApprovalDelegation>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pk_approval_delegations");

            entity.ToTable("approval_delegations", tb => tb.HasComment("Time-boxes the handover of one user approval authority to another for a named set of request types. @tier:T @owner:HR @retention:84m-keep"));

            entity.HasIndex(e => new { e.TenantId, e.DelegateUserId }, "ix_approval_delegations__delegate_user_id");

            entity.HasIndex(e => new { e.TenantId, e.DelegatorUserId }, "ix_approval_delegations__delegator_user_id");

            entity.HasIndex(e => new { e.TenantId, e.Id }, "uq_approval_delegations__tenant_id").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DelegateUserId).HasColumnName("delegate_user_id");
            entity.Property(e => e.DelegatorUserId).HasColumnName("delegator_user_id");
            entity.Property(e => e.EffectiveFrom).HasColumnName("effective_from");
            entity.Property(e => e.EffectiveTo).HasColumnName("effective_to");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true)
                .HasColumnName("is_active");
            entity.Property(e => e.Reason).HasColumnName("reason");
            entity.Property(e => e.RequestTypes)
                .HasDefaultValueSql("'{}'::text[]")
                .HasColumnName("request_types");
            entity.Property(e => e.RevokedAt).HasColumnName("revoked_at");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");

            entity.HasOne(d => d.User).WithMany(p => p.ApprovalDelegationUsers)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.DelegateUserId })
                .HasConstraintName("fk_approval_delegations__delegate_user_id");

            entity.HasOne(d => d.UserNavigation).WithMany(p => p.ApprovalDelegationUserNavigations)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.DelegatorUserId })
                .HasConstraintName("fk_approval_delegations__delegator_user_id");
        });

        modelBuilder.Entity<ApprovalRequest>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pk_approval_requests");

            entity.ToTable("approval_requests", tb => tb.HasComment("Is the single authoritative record of whether something was approved, carrying the frozen workflow it runs against, the current step and the current approver the inbox filters on. @tier:T @owner:HR @retention:84m-keep"));

            entity.HasIndex(e => new { e.TenantId, e.EmployeeId }, "ix_approval_requests__employee_id");

            entity.HasIndex(e => new { e.TenantId, e.CurrentApproverEmployeeId, e.DueAt, e.CreatedAt }, "ix_approval_requests__inbox_employee")
                .IsDescending(false, false, false, true)
                .HasFilter("((status)::text = 'Pending'::text)");

            entity.HasIndex(e => new { e.TenantId, e.CurrentApproverUserId, e.DueAt, e.CreatedAt }, "ix_approval_requests__inbox_user")
                .IsDescending(false, false, false, true)
                .HasFilter("((status)::text = 'Pending'::text)");

            entity.HasIndex(e => new { e.TenantId, e.DueAt }, "ix_approval_requests__overdue").HasFilter("(((status)::text = 'Pending'::text) AND (due_at IS NOT NULL))");

            entity.HasIndex(e => new { e.TenantId, e.RequesterUserId }, "ix_approval_requests__requester_user_id");

            entity.HasIndex(e => new { e.TenantId, e.WorkflowId }, "ix_approval_requests__workflow_id");

            entity.HasIndex(e => new { e.TenantId, e.Id }, "uq_approval_requests__tenant_id").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.CurrentApproverEmployeeId).HasColumnName("current_approver_employee_id");
            entity.Property(e => e.CurrentApproverUserId).HasColumnName("current_approver_user_id");
            entity.Property(e => e.CurrentStep)
                .HasDefaultValue(0)
                .HasColumnName("current_step");
            entity.Property(e => e.DecidedAt).HasColumnName("decided_at");
            entity.Property(e => e.DueAt).HasColumnName("due_at");
            entity.Property(e => e.EmployeeId).HasColumnName("employee_id");
            entity.Property(e => e.Payload)
                .HasColumnType("jsonb")
                .HasColumnName("payload");
            entity.Property(e => e.RequestType)
                .HasMaxLength(40)
                .HasColumnName("request_type");
            entity.Property(e => e.RequesterUserId).HasColumnName("requester_user_id");
            entity.Property(e => e.Status)
                .HasMaxLength(40)
                .HasDefaultValueSql("'Draft'::character varying")
                .HasColumnName("status");
            entity.Property(e => e.SubjectId).HasColumnName("subject_id");
            entity.Property(e => e.SubjectType)
                .HasMaxLength(40)
                .HasColumnName("subject_type");
            entity.Property(e => e.SubmittedAt).HasColumnName("submitted_at");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
            entity.Property(e => e.WorkflowId).HasColumnName("workflow_id");
            entity.Property(e => e.WorkflowSnapshot)
                .HasColumnType("jsonb")
                .HasColumnName("workflow_snapshot");

            entity.HasOne(d => d.Employee).WithMany(p => p.ApprovalRequests)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.EmployeeId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_approval_requests__employee_id");

            entity.HasOne(d => d.User).WithMany(p => p.ApprovalRequests)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.RequesterUserId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_approval_requests__requester_user_id");

            entity.HasOne(d => d.ApprovalWorkflow).WithMany(p => p.ApprovalRequests)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.WorkflowId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_approval_requests__workflow_id");
        });

        modelBuilder.Entity<ApprovalWorkflow>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pk_approval_workflows");

            entity.ToTable("approval_workflows", tb => tb.HasComment("Defines the ordered approval steps, approver rules, amount thresholds and service levels for one request type, optionally narrowed to a company. @tier:T @owner:HR @retention:tenant-lifecycle"));

            entity.HasIndex(e => new { e.TenantId, e.CompanyId, e.RequestType }, "uq_approval_workflows__request_type").IsUnique();

            entity.HasIndex(e => new { e.TenantId, e.Id }, "uq_approval_workflows__tenant_id").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.CompanyId).HasColumnName("company_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true)
                .HasColumnName("is_active");
            entity.Property(e => e.Name).HasColumnName("name");
            entity.Property(e => e.RequestType)
                .HasMaxLength(40)
                .HasColumnName("request_type");
            entity.Property(e => e.Steps)
                .HasDefaultValueSql("'[]'::jsonb")
                .HasColumnType("jsonb")
                .HasColumnName("steps");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");

            entity.HasOne(d => d.Company).WithMany(p => p.ApprovalWorkflows)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.CompanyId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_approval_workflows__company_id");
        });

        modelBuilder.Entity<AttendanceDevice>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pk_attendance_devices");

            entity.ToTable("attendance_devices", tb => tb.HasComment("Registers a biometric or terminal device at a branch with its API credential hash, sync watermark and replay-nonce window. @tier:C @owner:HR @retention:tenant-lifecycle"));

            entity.HasIndex(e => new { e.TenantId, e.BranchId }, "ix_attendance_devices__branch_id");

            entity.HasIndex(e => new { e.TenantId, e.Serial }, "uq_attendance_devices__serial").IsUnique();

            entity.HasIndex(e => new { e.TenantId, e.Id }, "uq_attendance_devices__tenant_id").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.ApiKeyHash).HasColumnName("api_key_hash");
            entity.Property(e => e.BranchId).HasColumnName("branch_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true)
                .HasColumnName("is_active");
            entity.Property(e => e.LastSeenAt).HasColumnName("last_seen_at");
            entity.Property(e => e.Model)
                .HasMaxLength(64)
                .HasColumnName("model");
            entity.Property(e => e.Name).HasColumnName("name");
            entity.Property(e => e.RecentNonces)
                .HasDefaultValueSql("'[]'::jsonb")
                .HasColumnType("jsonb")
                .HasColumnName("recent_nonces");
            entity.Property(e => e.Serial)
                .HasMaxLength(64)
                .HasColumnName("serial");
            entity.Property(e => e.SyncWatermark).HasColumnName("sync_watermark");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");

            entity.HasOne(d => d.Branch).WithMany(p => p.AttendanceDevices)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.BranchId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_attendance_devices__branch_id");
        });

        modelBuilder.Entity<AuthSession>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pk_auth_sessions");

            entity.ToTable("auth_sessions", tb => tb.HasComment("One row per signed-in device for either subject kind, carrying the rotated refresh token, the previous hash for reuse detection and the push registration, so re-login never loses a device. @tier:T/P @owner:Platform @retention:1-month-after-Expiry-then-Purge"));

            entity.HasIndex(e => e.PlatformUserId, "ix_auth_sessions__platform_user_id");

            entity.HasIndex(e => new { e.TenantId, e.UserId }, "ix_auth_sessions__user_id");

            entity.HasIndex(e => new { e.TenantId, e.Id }, "uq_auth_sessions__tenant_id_id").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DeviceId)
                .HasMaxLength(128)
                .HasColumnName("device_id");
            entity.Property(e => e.ExpiresAt).HasColumnName("expires_at");
            entity.Property(e => e.Ip).HasColumnName("ip");
            entity.Property(e => e.LastSeenAt).HasColumnName("last_seen_at");
            entity.Property(e => e.PlatformUserId).HasColumnName("platform_user_id");
            entity.Property(e => e.PreviousTokenHash)
                .HasComment("Presented-again detection: a refresh with this hash means the token was replayed, and the whole session is revoked.")
                .HasColumnName("previous_token_hash");
            entity.Property(e => e.PushPlatform)
                .HasMaxLength(40)
                .HasColumnName("push_platform");
            entity.Property(e => e.PushToken).HasColumnName("push_token");
            entity.Property(e => e.RefreshTokenHash).HasColumnName("refresh_token_hash");
            entity.Property(e => e.RevokedAt).HasColumnName("revoked_at");
            entity.Property(e => e.SubjectKind)
                .HasMaxLength(40)
                .HasColumnName("subject_kind");
            entity.Property(e => e.TenantId)
                .HasComment("NULL only for subject_kind='Platform'. One of exactly two nullable-tenant client-adjacent tables; policed by the hand-written p_auth policy (§19.2).")
                .HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
            entity.Property(e => e.UserAgent).HasColumnName("user_agent");
            entity.Property(e => e.UserId).HasColumnName("user_id");

            entity.HasOne(d => d.PlatformUser).WithMany(p => p.AuthSessions)
                .HasForeignKey(d => d.PlatformUserId)
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("fk_auth_sessions__platform_user_id");

            entity.HasOne(d => d.User).WithMany(p => p.AuthSessions)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.UserId })
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("fk_auth_sessions__user_id");
        });

        modelBuilder.Entity<AuthToken>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pk_auth_tokens");

            entity.ToTable("auth_tokens", tb => tb.HasComment("Single-use, expiring tokens for password reset, MFA challenge, invitation and email confirmation, for either subject kind, with an attempt counter that supports lockout. @tier:T/P @owner:Platform @retention:1-month-after-Expiry-then-Purge"));

            entity.HasIndex(e => e.PlatformUserId, "ix_auth_tokens__platform_user_id");

            entity.HasIndex(e => new { e.TenantId, e.UserId }, "ix_auth_tokens__user_id");

            entity.HasIndex(e => new { e.TenantId, e.Id }, "uq_auth_tokens__tenant_id_id").IsUnique();

            entity.HasIndex(e => e.TokenHash, "uq_auth_tokens__token_hash").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.Attempts)
                .HasDefaultValue(0)
                .HasColumnName("attempts");
            entity.Property(e => e.ConsumedAt).HasColumnName("consumed_at");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.ExpiresAt).HasColumnName("expires_at");
            entity.Property(e => e.PlatformUserId).HasColumnName("platform_user_id");
            entity.Property(e => e.Purpose)
                .HasMaxLength(40)
                .HasColumnName("purpose");
            entity.Property(e => e.SubjectKind)
                .HasMaxLength(40)
                .HasColumnName("subject_kind");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.TokenHash)
                .HasComment("Secret column: REVOKE from kynex_ro by column privilege (§19.2). Lowercase hex; the plaintext token is never stored.")
                .HasColumnName("token_hash");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
            entity.Property(e => e.UserId).HasColumnName("user_id");

            entity.HasOne(d => d.PlatformUser).WithMany(p => p.AuthTokens)
                .HasForeignKey(d => d.PlatformUserId)
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("fk_auth_tokens__platform_user_id");

            entity.HasOne(d => d.User).WithMany(p => p.AuthTokens)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.UserId })
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("fk_auth_tokens__user_id");
        });

        modelBuilder.Entity<BackgroundJob>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pk_background_jobs");

            entity.ToTable("background_jobs", tb => tb.HasComment("Is one asynchronous, resumable and idempotent unit of work with its payload, lease, heartbeat, attempt count and result, covering every import, export, sync and retention run in the product. @tier:T/P @owner:Platform @retention:6m-purge"));

            entity.HasIndex(e => new { e.Status, e.ScheduledAt }, "ix_background_jobs__queue").HasFilter("((status)::text = ANY ((ARRAY['Queued'::character varying, 'Leased'::character varying, 'Running'::character varying])::text[]))");

            entity.HasIndex(e => e.SourceFileId, "ix_background_jobs__source_file_id");

            entity.HasIndex(e => new { e.TenantId, e.IdempotencyKey }, "uq_background_jobs__idempotency_key").IsUnique();

            entity.HasIndex(e => new { e.TenantId, e.Id }, "uq_background_jobs__tenant_id").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.Attempts)
                .HasDefaultValue(0)
                .HasColumnName("attempts");
            entity.Property(e => e.CompletedAt).HasColumnName("completed_at");
            entity.Property(e => e.CorrelationId).HasColumnName("correlation_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.HeartbeatAt).HasColumnName("heartbeat_at");
            entity.Property(e => e.IdempotencyKey).HasColumnName("idempotency_key");
            entity.Property(e => e.Kind)
                .HasMaxLength(64)
                .HasColumnName("kind");
            entity.Property(e => e.LastError).HasColumnName("last_error");
            entity.Property(e => e.LeaseExpiresAt).HasColumnName("lease_expires_at");
            entity.Property(e => e.LeaseOwner)
                .HasMaxLength(128)
                .HasColumnName("lease_owner");
            entity.Property(e => e.Payload)
                .HasColumnType("jsonb")
                .HasColumnName("payload");
            entity.Property(e => e.ProgressCurrent)
                .HasDefaultValue(0)
                .HasColumnName("progress_current");
            entity.Property(e => e.ProgressTotal).HasColumnName("progress_total");
            entity.Property(e => e.Result)
                .HasColumnType("jsonb")
                .HasColumnName("result");
            entity.Property(e => e.ScheduledAt).HasColumnName("scheduled_at");
            entity.Property(e => e.SourceFileId).HasColumnName("source_file_id");
            entity.Property(e => e.SourceFileSha256).HasColumnName("source_file_sha256");
            entity.Property(e => e.StartedAt).HasColumnName("started_at");
            entity.Property(e => e.Status)
                .HasMaxLength(40)
                .HasDefaultValueSql("'Queued'::character varying")
                .HasColumnName("status");
            entity.Property(e => e.TenantId)
                .IsRequired()
                .HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");

            entity.HasOne(d => d.SourceFile).WithMany(p => p.BackgroundJobs)
                .HasForeignKey(d => d.SourceFileId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("fk_background_jobs__source_file_id");

            entity.HasOne(d => d.Tenant).WithMany(p => p.BackgroundJobs)
                .HasForeignKey(d => d.TenantId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_background_jobs__tenant_id");
        });

        modelBuilder.Entity<Branch>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pk_branches");

            entity.ToTable("branches", tb => tb.HasComment("A physical site of a legal entity, carrying the geofence that validates a mobile punch and the holiday calendar that shapes its working days. @tier:C @owner:HR"));

            entity.HasIndex(e => new { e.TenantId, e.CompanyId }, "ix_branches__company_id");

            entity.HasIndex(e => new { e.TenantId, e.Id }, "uq_branches__tenant_id_id").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.Address).HasColumnName("address");
            entity.Property(e => e.City).HasColumnName("city");
            entity.Property(e => e.CompanyId).HasColumnName("company_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.GeofenceRadiusM).HasColumnName("geofence_radius_m");
            entity.Property(e => e.HolidayCalendarCode)
                .HasMaxLength(40)
                .HasColumnName("holiday_calendar_code");
            entity.Property(e => e.Lat)
                .HasPrecision(9, 6)
                .HasColumnName("lat");
            entity.Property(e => e.Lng)
                .HasPrecision(9, 6)
                .HasColumnName("lng");
            entity.Property(e => e.Name).HasColumnName("name");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");

            entity.HasOne(d => d.Company).WithMany(p => p.Branches)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.CompanyId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_branches__company_id");
        });

        modelBuilder.Entity<Company>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pk_companies");

            entity.ToTable("companies", tb => tb.HasComment("The legal entity and its MOL establishment: the registration identifiers every statutory filing is made under, the currency of record, an optional timezone override and the mid-year go-live period. @tier:T @owner:Finance @retention:Keep"));

            entity.HasIndex(e => new { e.TenantId, e.GosiRegistrationNo }, "uq_companies__tenant_id_gosi_registration_no").IsUnique();

            entity.HasIndex(e => new { e.TenantId, e.Id }, "uq_companies__tenant_id_id").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.CrNumber)
                .HasMaxLength(15)
                .HasColumnName("cr_number");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.CurrencyCode)
                .HasMaxLength(3)
                .HasDefaultValueSql("'SAR'::bpchar")
                .IsFixedLength()
                .HasComment("SAR is the currency of record. Every money column in the design is denominated in THIS company's currency; there is no per-row currency column anywhere (§13.2).")
                .HasColumnName("currency_code");
            entity.Property(e => e.GoLiveMonth).HasColumnName("go_live_month");
            entity.Property(e => e.GoLiveYear)
                .HasComment("The go-live period as two typed columns, not a named concept (§2.C, revision 6 correction).")
                .HasColumnName("go_live_year");
            entity.Property(e => e.GosiRegistrationNo)
                .HasMaxLength(20)
                .HasComment("The single writable copy of the GOSI establishment number (§11.5). A typo can exist in exactly one place and cannot propagate into a filing.")
                .HasColumnName("gosi_registration_no");
            entity.Property(e => e.MolEstablishmentNo)
                .HasMaxLength(20)
                .HasColumnName("mol_establishment_no");
            entity.Property(e => e.NameAr).HasColumnName("name_ar");
            entity.Property(e => e.NameEn).HasColumnName("name_en");
            entity.Property(e => e.NitaqatActivityCode)
                .HasMaxLength(16)
                .HasColumnName("nitaqat_activity_code");
            entity.Property(e => e.Settings)
                .HasDefaultValueSql("'{}'::jsonb")
                .HasComment("Non-money company overrides only. Contractual pay parameters moved OUT to company_pay_policies in revision 3 precisely so they get the dated EXCLUDE discipline (§13.5).")
                .HasColumnType("jsonb")
                .HasColumnName("settings");
            entity.Property(e => e.SoftDeletedAt).HasColumnName("soft_deleted_at");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.TimezoneId)
                .HasMaxLength(64)
                .HasColumnName("timezone_id");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
            entity.Property(e => e.WpsBankCode)
                .HasMaxLength(16)
                .HasColumnName("wps_bank_code");
            entity.Property(e => e.WpsMolId)
                .HasMaxLength(20)
                .HasColumnName("wps_mol_id");

            entity.HasOne(d => d.Tenant).WithMany(p => p.Companies)
                .HasForeignKey(d => d.TenantId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_companies__tenant_id");
        });

        modelBuilder.Entity<CompanyPayPolicy>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pk_company_pay_policies");

            entity.ToTable("company_pay_policies", tb => tb.HasComment("Contractual, above-statutory-floor pay parameters per legal entity, effective-dated so the rate in force on any day is a single row rather than a JSON lookup. @tier:C @owner:Finance @retention:Keep"));

            entity.HasIndex(e => new { e.TenantId, e.PayComponentCode }, "ix_company_pay_policies__pay_component_code");

            entity.HasIndex(e => new { e.TenantId, e.Id }, "uq_company_pay_policies__tenant_id_id").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.Amount)
                .HasPrecision(18, 2)
                .HasColumnName("amount");
            entity.Property(e => e.ApprovedBy)
                .HasComment("Plain uuid, not an FK: §8.2 registers no FK for this column and a purged approver must not block a contractual row (same rule as created_by).")
                .HasColumnName("approved_by");
            entity.Property(e => e.CompanyId).HasColumnName("company_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.EffectiveFrom).HasColumnName("effective_from");
            entity.Property(e => e.EffectiveTo).HasColumnName("effective_to");
            entity.Property(e => e.PayComponentCode)
                .HasMaxLength(32)
                .HasColumnName("pay_component_code");
            entity.Property(e => e.PolicyKey)
                .HasMaxLength(64)
                .HasColumnName("policy_key");
            entity.Property(e => e.Rate)
                .HasPrecision(9, 6)
                .HasColumnName("rate");
            entity.Property(e => e.SourceReference).HasColumnName("source_reference");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
            entity.Property(e => e.ValueJson)
                .HasComment("Bounded to 8 KiB by CHECK and schema-validated on write. May only EXCEED a statutory floor, checked against the rule in force (§11.6).")
                .HasColumnType("jsonb")
                .HasColumnName("value_json");

            entity.HasOne(d => d.Company).WithMany(p => p.CompanyPayPolicies)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.CompanyId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_company_pay_policies__company_id");

            entity.HasOne(d => d.PayComponent).WithMany(p => p.CompanyPayPolicies)
                .HasPrincipalKey(p => new { p.TenantId, p.Code })
                .HasForeignKey(d => new { d.TenantId, d.PayComponentCode })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_company_pay_policies__pay_component_code");
        });

        modelBuilder.Entity<CostCenter>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pk_cost_centers");

            entity.ToTable("cost_centers", tb => tb.HasComment("The costing dimension shared by GL export, timesheets, payroll inputs and slip lines, carrying the segment the ERP's chart of accounts expects. @tier:C @owner:Finance"));

            entity.HasIndex(e => new { e.TenantId, e.CompanyId, e.Code }, "uq_cost_centers__company_id_code").IsUnique();

            entity.HasIndex(e => new { e.TenantId, e.Id }, "uq_cost_centers__tenant_id_id").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.Code)
                .HasMaxLength(40)
                .HasColumnName("code");
            entity.Property(e => e.CompanyId).HasColumnName("company_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.GlSegment)
                .HasMaxLength(64)
                .HasColumnName("gl_segment");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true)
                .HasColumnName("is_active");
            entity.Property(e => e.Name).HasColumnName("name");
            entity.Property(e => e.ParentId).HasColumnName("parent_id");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");

            entity.HasOne(d => d.Company).WithMany(p => p.CostCenters)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.CompanyId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_cost_centers__company_id");

            entity.HasOne(d => d.CostCenterNavigation).WithMany(p => p.InverseCostCenterNavigation)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.ParentId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_cost_centers__parent_id");
        });

        modelBuilder.Entity<DataProtectionKey>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pk_data_protection_keys");

            entity.ToTable("data_protection_keys", tb => tb.HasComment("The ASP.NET Data Protection key ring whose keys encrypt MFA secrets and single-use tokens at rest. @tier:P @owner:Platform @retention:Keep"));

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.FriendlyName).HasColumnName("friendly_name");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
            entity.Property(e => e.Xml)
                .HasComment("Secret column: REVOKE from kynex_ro by column privilege (§19.2).")
                .HasColumnName("xml");
        });

        modelBuilder.Entity<Department>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pk_departments");

            entity.ToTable("departments", tb => tb.HasComment("The org-unit hierarchy within a legal entity, optionally pointing at the cost centre its salary cost posts to. @tier:C @owner:HR"));

            entity.HasIndex(e => new { e.TenantId, e.CompanyId }, "ix_departments__company_id");

            entity.HasIndex(e => new { e.TenantId, e.CostCenterId }, "ix_departments__cost_center_id");

            entity.HasIndex(e => new { e.TenantId, e.Id }, "uq_departments__tenant_id_id").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.CompanyId).HasColumnName("company_id");
            entity.Property(e => e.CostCenterId).HasColumnName("cost_center_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.Name).HasColumnName("name");
            entity.Property(e => e.ParentId).HasColumnName("parent_id");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");

            entity.HasOne(d => d.Company).WithMany(p => p.Departments)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.CompanyId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_departments__company_id");

            entity.HasOne(d => d.CostCenter).WithMany(p => p.Departments)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.CostCenterId })
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("fk_departments__cost_center_id");

            entity.HasOne(d => d.DepartmentNavigation).WithMany(p => p.InverseDepartmentNavigation)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.ParentId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_departments__parent_id");
        });

        modelBuilder.Entity<Designation>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pk_designations");

            entity.ToTable("designations", tb => tb.HasComment("Job titles in English and Arabic with the MHRSD/GOSI occupation code that Saudization-restricted jobs and GOSI registration require. @tier:T @owner:HR"));

            entity.HasIndex(e => new { e.TenantId, e.Id }, "uq_designations__tenant_id_id").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true)
                .HasColumnName("is_active");
            entity.Property(e => e.OccupationCode)
                .HasMaxLength(16)
                .HasColumnName("occupation_code");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.TitleAr).HasColumnName("title_ar");
            entity.Property(e => e.TitleEn).HasColumnName("title_en");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");

            entity.HasOne(d => d.Tenant).WithMany(p => p.Designations)
                .HasForeignKey(d => d.TenantId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_designations__tenant_id");
        });

        modelBuilder.Entity<DocumentTemplate>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pk_document_templates");

            entity.ToTable("document_templates", tb => tb.HasComment("Versioned letter, payslip and contract bodies in English and Arabic with their merge-field contract, scoped to one company or to the whole tenant. @tier:T @owner:HR"));

            entity.HasIndex(e => new { e.TenantId, e.CompanyId, e.Kind, e.Code, e.Version }, "uq_document_templates__code_version").IsUnique();

            entity.HasIndex(e => new { e.TenantId, e.Id }, "uq_document_templates__tenant_id_id").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.BodyAr).HasColumnName("body_ar");
            entity.Property(e => e.BodyEn).HasColumnName("body_en");
            entity.Property(e => e.Code)
                .HasMaxLength(64)
                .HasColumnName("code");
            entity.Property(e => e.CompanyId).HasColumnName("company_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.Kind)
                .HasMaxLength(40)
                .HasColumnName("kind");
            entity.Property(e => e.MergeFields)
                .HasColumnType("jsonb")
                .HasColumnName("merge_fields");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
            entity.Property(e => e.Version)
                .HasDefaultValue(1)
                .HasComment("Immutable once a document or slip references it; a change is a NEW row with the next version (§6).")
                .HasColumnName("version");

            entity.HasOne(d => d.Company).WithMany(p => p.DocumentTemplates)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.CompanyId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_document_templates__company_id");
        });

        modelBuilder.Entity<Employee>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pk_employees");

            entity.ToTable("employees", tb => tb.HasComment("The person: current identity, statutory identifiers, employment anchors and the PDPL lifecycle that lets an erasure anonymise the record in place while every dependent payroll row keeps its foreign key. @tier:T @owner:HR @retention:84-months-from-Separation-then-Anonymise"));

            entity.HasIndex(e => new { e.TenantId, e.Status, e.NameEn }, "ix_employees__tenant_status_name_en");

            entity.HasIndex(e => new { e.TenantId, e.EmployeeNumber }, "uq_employees__tenant_id_employee_number").IsUnique();

            entity.HasIndex(e => new { e.TenantId, e.Id }, "uq_employees__tenant_id_id").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.BorderNo)
                .HasMaxLength(12)
                .HasColumnName("border_no");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.Dob).HasColumnName("dob");
            entity.Property(e => e.EmployeeNumber)
                .HasMaxLength(32)
                .HasComment("Allocated from number_sequences. Retained through anonymisation so statutory payroll rows stay traceable (§12.3).")
                .HasColumnName("employee_number");
            entity.Property(e => e.EosPriorPaidAmount)
                .HasPrecision(18, 2)
                .HasColumnName("eos_prior_paid_amount");
            entity.Property(e => e.EosServiceStartDate).HasColumnName("eos_service_start_date");
            entity.Property(e => e.Gender)
                .HasMaxLength(40)
                .HasColumnName("gender");
            entity.Property(e => e.GosiFirstRegisteredOn)
                .HasComment("Drives the GOSI cohort (Legacy vs Entrant2024). NULL raises a BLOCKING payroll_issues row; the code never defaults to a cohort (§2.E).")
                .HasColumnName("gosi_first_registered_on");
            entity.Property(e => e.IqamaNo)
                .HasMaxLength(10)
                .HasColumnName("iqama_no");
            entity.Property(e => e.JoiningDate).HasColumnName("joining_date");
            entity.Property(e => e.NameAr).HasColumnName("name_ar");
            entity.Property(e => e.NameEn).HasColumnName("name_en");
            entity.Property(e => e.NationalId)
                .HasMaxLength(10)
                .HasColumnName("national_id");
            entity.Property(e => e.NationalityCode)
                .HasMaxLength(2)
                .IsFixedLength()
                .HasColumnName("nationality_code");
            entity.Property(e => e.NitaqatWeightOverride)
                .HasPrecision(9, 6)
                .HasColumnName("nitaqat_weight_override");
            entity.Property(e => e.NitaqatWeightOverrideReason).HasColumnName("nitaqat_weight_override_reason");
            entity.Property(e => e.PrivacyStatus)
                .HasMaxLength(40)
                .HasDefaultValueSql("'Normal'::character varying")
                .HasColumnName("privacy_status");
            entity.Property(e => e.RedactedAt).HasColumnName("redacted_at");
            entity.Property(e => e.RetentionUntil).HasColumnName("retention_until");
            entity.Property(e => e.SeparationDate)
                .HasComment("Projection of final_settlements.last_working_day, written when the settlement is approved; the settlement is authoritative (§11.3).")
                .HasColumnName("separation_date");
            entity.Property(e => e.Status)
                .HasMaxLength(40)
                .HasDefaultValueSql("'Draft'::character varying")
                .HasComment("Current-state PROJECTION maintained by EmployeeLifecycleService (§10.7). employee_assignments is authoritative for \"was this person employed on date D\" (§11.3).")
                .HasColumnName("status");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
            entity.Property(e => e.WorkEmail)
                .HasComment("Added to satisfy §19.4 H1, which indexes it; §2.D does not list it. See the note above this comment.")
                .HasColumnName("work_email");
            entity.Property(e => e.WpsEligible)
                .HasDefaultValue(true)
                .HasColumnName("wps_eligible");

            entity.HasOne(d => d.Tenant).WithMany(p => p.Employees)
                .HasForeignKey(d => d.TenantId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_employees__tenant_id");
        });

        modelBuilder.Entity<EmployeeAssignment>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pk_employee_assignments");

            entity.ToTable("employee_assignments", tb => tb.HasComment("The effective-dated record of where a person sat — company, branch, department, designation, grade, manager and cost centre — and the single authority for whether they were employed on any given date. @tier:C @owner:HR @retention:Keep"));

            entity.HasIndex(e => new { e.TenantId, e.ApprovalRequestId }, "ix_employee_assignments__approval_request_id");

            entity.HasIndex(e => new { e.TenantId, e.EmployeeId, e.EffectiveFrom }, "ix_employee_assignments__as_of").IsDescending(false, false, true);

            entity.HasIndex(e => new { e.TenantId, e.BranchId }, "ix_employee_assignments__branch_id");

            entity.HasIndex(e => new { e.TenantId, e.CompanyId }, "ix_employee_assignments__company_id");

            entity.HasIndex(e => new { e.TenantId, e.CostCenterId }, "ix_employee_assignments__cost_center_id");

            entity.HasIndex(e => new { e.TenantId, e.DepartmentId }, "ix_employee_assignments__department_id");

            entity.HasIndex(e => new { e.TenantId, e.DesignationId }, "ix_employee_assignments__designation_id");

            entity.HasIndex(e => new { e.TenantId, e.GradeId }, "ix_employee_assignments__grade_id");

            entity.HasIndex(e => new { e.TenantId, e.ManagerEmployeeId }, "ix_employee_assignments__manager_employee_id").HasFilter("(effective_to IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.Id }, "uq_employee_assignments__tenant_id_id").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.ApprovalRequestId)
                .HasComment("The decision behind the change. The row has no status of its own: the approval is its only state (§11.1).")
                .HasColumnName("approval_request_id");
            entity.Property(e => e.BranchId).HasColumnName("branch_id");
            entity.Property(e => e.ChangeReason).HasColumnName("change_reason");
            entity.Property(e => e.CompanyId).HasColumnName("company_id");
            entity.Property(e => e.CostCenterId)
                .HasComment("Optional in the schema on purpose: a company that posts GL by cost centre gets a payroll_issues BLOCK at calculation instead of a NOT NULL that stops HR saving an employee (§8 row 40).")
                .HasColumnName("cost_center_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DepartmentId).HasColumnName("department_id");
            entity.Property(e => e.DesignationId).HasColumnName("designation_id");
            entity.Property(e => e.EffectiveFrom).HasColumnName("effective_from");
            entity.Property(e => e.EffectiveTo).HasColumnName("effective_to");
            entity.Property(e => e.EmployeeId).HasColumnName("employee_id");
            entity.Property(e => e.EmploymentStatus)
                .HasMaxLength(40)
                .HasColumnName("employment_status");
            entity.Property(e => e.GradeId).HasColumnName("grade_id");
            entity.Property(e => e.ManagerEmployeeId).HasColumnName("manager_employee_id");
            entity.Property(e => e.PayGroup)
                .HasMaxLength(40)
                .HasColumnName("pay_group");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");

            entity.HasOne(d => d.ApprovalRequest).WithMany(p => p.EmployeeAssignments)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.ApprovalRequestId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_employee_assignments__approval_request_id");

            entity.HasOne(d => d.Branch).WithMany(p => p.EmployeeAssignments)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.BranchId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_employee_assignments__branch_id");

            entity.HasOne(d => d.Company).WithMany(p => p.EmployeeAssignments)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.CompanyId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_employee_assignments__company_id");

            entity.HasOne(d => d.CostCenter).WithMany(p => p.EmployeeAssignments)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.CostCenterId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_employee_assignments__cost_center_id");

            entity.HasOne(d => d.Department).WithMany(p => p.EmployeeAssignments)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.DepartmentId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_employee_assignments__department_id");

            entity.HasOne(d => d.Designation).WithMany(p => p.EmployeeAssignments)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.DesignationId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_employee_assignments__designation_id");

            entity.HasOne(d => d.Employee).WithMany(p => p.EmployeeAssignmentEmployees)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.EmployeeId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_employee_assignments__employee_id");

            entity.HasOne(d => d.Grade).WithMany(p => p.EmployeeAssignments)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.GradeId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_employee_assignments__grade_id");

            entity.HasOne(d => d.EmployeeNavigation).WithMany(p => p.EmployeeAssignmentEmployeeNavigations)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.ManagerEmployeeId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_employee_assignments__manager_employee_id");
        });

        modelBuilder.Entity<EmployeeBankAccount>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pk_employee_bank_accounts");

            entity.ToTable("employee_bank_accounts", tb => tb.HasComment("The effective-dated payment instruction an employee is paid to, so a WPS file filed last March can still be explained by the IBAN that was current then. @tier:T @owner:Finance @retention:Keep"));

            entity.HasIndex(e => new { e.TenantId, e.Id }, "uq_employee_bank_accounts__tenant_id_id").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.AccountHolderName).HasColumnName("account_holder_name");
            entity.Property(e => e.BankCode)
                .HasMaxLength(16)
                .HasColumnName("bank_code");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.EffectiveFrom).HasColumnName("effective_from");
            entity.Property(e => e.EffectiveTo).HasColumnName("effective_to");
            entity.Property(e => e.EmployeeId).HasColumnName("employee_id");
            entity.Property(e => e.Iban)
                .HasMaxLength(34)
                .HasComment("Bounded to 34 and pattern-checked for SA IBANs by CHECK; the mod-97 checksum is enforced in the service (§13.3).")
                .HasColumnName("iban");
            entity.Property(e => e.PaymentMethod)
                .HasMaxLength(40)
                .HasColumnName("payment_method");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");

            entity.HasOne(d => d.Employee).WithMany(p => p.EmployeeBankAccounts)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.EmployeeId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_employee_bank_accounts__employee_id");
        });

        modelBuilder.Entity<EmployeeContract>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pk_employee_contracts");

            entity.ToTable("employee_contracts", tb => tb.HasComment("The effective-dated employment contract — type, term, probation, contracted hours and notice period — with an optional pointer to the signed scan. @tier:T @owner:HR @retention:Keep"));

            entity.HasIndex(e => new { e.TenantId, e.DocumentId }, "ix_employee_contracts__document_id");

            entity.HasIndex(e => new { e.TenantId, e.Id }, "uq_employee_contracts__tenant_id_id").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.ContractType)
                .HasMaxLength(40)
                .HasColumnName("contract_type");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DocumentId).HasColumnName("document_id");
            entity.Property(e => e.EffectiveFrom).HasColumnName("effective_from");
            entity.Property(e => e.EffectiveTo).HasColumnName("effective_to");
            entity.Property(e => e.EmployeeId).HasColumnName("employee_id");
            entity.Property(e => e.EndDate)
                .HasComment("Named end_date, not `end`: `end` is a reserved word (CONVENTIONS.md §1).")
                .HasColumnName("end_date");
            entity.Property(e => e.NoticeDays).HasColumnName("notice_days");
            entity.Property(e => e.ProbationEnd).HasColumnName("probation_end");
            entity.Property(e => e.QiwaContractNo)
                .HasComment("Kept per §2.D. §18 says the external_system/external_id/external_synced_at trio should replace it — unreconciled in revision 6.")
                .HasColumnName("qiwa_contract_no");
            entity.Property(e => e.StartDate).HasColumnName("start_date");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
            entity.Property(e => e.WeeklyHours)
                .HasPrecision(6, 2)
                .HasColumnName("weekly_hours");

            entity.HasOne(d => d.EmployeeDocument).WithMany(p => p.EmployeeContracts)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.DocumentId })
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("fk_employee_contracts__document_id");

            entity.HasOne(d => d.Employee).WithMany(p => p.EmployeeContracts)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.EmployeeId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_employee_contracts__employee_id");
        });

        modelBuilder.Entity<EmployeeDocument>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pk_employee_documents");

            entity.ToTable("employee_documents", tb => tb.HasComment("Every document attached to a person — iqama, passport, visa, work permit, contract scan, issued letter, sick note — with its expiry, its version chain and the file that holds the blob. @tier:T @owner:HR @retention:84-months-from-Expiry-then-Purge"));

            entity.HasIndex(e => new { e.TenantId, e.ExpiryDate }, "ix_employee_documents__active_expiry").HasFilter("((status)::text = 'Active'::text)");

            entity.HasIndex(e => new { e.TenantId, e.EmployeeId, e.DocType }, "ix_employee_documents__employee_doc_type");

            entity.HasIndex(e => new { e.TenantId, e.EmployeeId, e.ExpiryDate }, "ix_employee_documents__employee_expiry");

            entity.HasIndex(e => new { e.TenantId, e.EmployeeId }, "ix_employee_documents__employee_id");

            entity.HasIndex(e => new { e.TenantId, e.ExpiryDate }, "ix_employee_documents__expiry_date");

            entity.HasIndex(e => new { e.TenantId, e.FileId }, "ix_employee_documents__file_id");

            entity.HasIndex(e => new { e.TenantId, e.LeaveRequestId }, "ix_employee_documents__leave_request_id");

            entity.HasIndex(e => new { e.TenantId, e.SupersedesId }, "ix_employee_documents__supersedes_id");

            entity.HasIndex(e => new { e.TenantId, e.Id }, "uq_employee_documents__tenant_id_id").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DocType)
                .HasMaxLength(40)
                .HasColumnName("doc_type");
            entity.Property(e => e.DocumentNumber)
                .HasMaxLength(64)
                .HasColumnName("document_number");
            entity.Property(e => e.EmployeeId).HasColumnName("employee_id");
            entity.Property(e => e.ExpiryDate)
                .HasComment("Read by the expiry-reminder job straight into notifications. There is deliberately no reminder table (§2.D).")
                .HasColumnName("expiry_date");
            entity.Property(e => e.FileId).HasColumnName("file_id");
            entity.Property(e => e.IssueDate).HasColumnName("issue_date");
            entity.Property(e => e.IssuingCountry)
                .HasMaxLength(2)
                .IsFixedLength()
                .HasColumnName("issuing_country");
            entity.Property(e => e.LeaveRequestId)
                .HasComment("Present only for sick notes. FK deferred to the cross-domain constraints pass: leave_requests is domain J.")
                .HasColumnName("leave_request_id");
            entity.Property(e => e.LetterNumber)
                .HasMaxLength(40)
                .HasColumnName("letter_number");
            entity.Property(e => e.Status)
                .HasMaxLength(40)
                .HasDefaultValueSql("'Active'::character varying")
                .HasColumnName("status");
            entity.Property(e => e.SupersedesId)
                .HasComment("Renewal chain. SET NULL on delete so a chain survives a removed predecessor (§8 row 50); indexed parent-side in revision 6 (§19.4).")
                .HasColumnName("supersedes_id");
            entity.Property(e => e.TemplateId).HasColumnName("template_id");
            entity.Property(e => e.TemplateVersion).HasColumnName("template_version");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
            entity.Property(e => e.VerificationCode)
                .HasMaxLength(64)
                .HasColumnName("verification_code");
            entity.Property(e => e.Version)
                .HasDefaultValue(1)
                .HasColumnName("version");

            entity.HasOne(d => d.Employee).WithMany(p => p.EmployeeDocuments)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.EmployeeId })
                .HasConstraintName("fk_employee_documents__employee_id");

            entity.HasOne(d => d.File).WithMany(p => p.EmployeeDocuments)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.FileId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_employee_documents__file_id");

            entity.HasOne(d => d.LeaveRequest).WithMany(p => p.EmployeeDocuments)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.LeaveRequestId })
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("fk_employee_documents__leave_request_id");

            entity.HasOne(d => d.EmployeeDocumentNavigation).WithMany(p => p.InverseEmployeeDocumentNavigation)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.SupersedesId })
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("fk_employee_documents__supersedes_id");

            entity.HasOne(d => d.DocumentTemplate).WithMany(p => p.EmployeeDocuments)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.TemplateId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_employee_documents__template_id");
        });

        modelBuilder.Entity<EmployeeGosiRegistration>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pk_employee_gosi_registrations");

            entity.ToTable("employee_gosi_registrations", tb => tb.HasComment("Tracks the effective-dated GOSI registration of an employee against an establishment, including the contributory wage GOSI itself holds on file, which is what a filing variance is explained against. @tier:C @owner:Finance @retention:84m-keep"));

            entity.HasIndex(e => new { e.TenantId, e.CompanyId }, "ix_employee_gosi_registrations__company_id");

            entity.HasIndex(e => new { e.TenantId, e.GosiRegistrationNo }, "ix_employee_gosi_registrations__gosi_registration_no");

            entity.HasIndex(e => new { e.TenantId, e.Id }, "uq_employee_gosi_registrations__tenant_id").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.CompanyId).HasColumnName("company_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DeregisteredOn).HasColumnName("deregistered_on");
            entity.Property(e => e.EffectiveFrom).HasColumnName("effective_from");
            entity.Property(e => e.EffectiveTo).HasColumnName("effective_to");
            entity.Property(e => e.EmployeeId).HasColumnName("employee_id");
            entity.Property(e => e.GosiEmployeeNo)
                .HasMaxLength(20)
                .HasColumnName("gosi_employee_no");
            entity.Property(e => e.GosiRegistrationNo)
                .HasMaxLength(20)
                .HasColumnName("gosi_registration_no");
            entity.Property(e => e.OccupationCode)
                .HasMaxLength(16)
                .HasColumnName("occupation_code");
            entity.Property(e => e.RegisteredContributoryWage)
                .HasPrecision(18, 2)
                .HasColumnName("registered_contributory_wage");
            entity.Property(e => e.RegisteredOn).HasColumnName("registered_on");
            entity.Property(e => e.Status)
                .HasMaxLength(40)
                .HasDefaultValueSql("'Registered'::character varying")
                .HasColumnName("status");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");

            entity.HasOne(d => d.Company).WithMany(p => p.EmployeeGosiRegistrationCompanies)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.CompanyId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_employee_gosi_registrations__company_id");

            entity.HasOne(d => d.Employee).WithMany(p => p.EmployeeGosiRegistrations)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.EmployeeId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_employee_gosi_registrations__employee_id");

            entity.HasOne(d => d.CompanyNavigation).WithMany(p => p.EmployeeGosiRegistrationCompanyNavigations)
                .HasPrincipalKey(p => new { p.TenantId, p.GosiRegistrationNo })
                .HasForeignKey(d => new { d.TenantId, d.GosiRegistrationNo })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_employee_gosi_registrations__gosi_registration_no");
        });

        modelBuilder.Entity<EmployeeSalary>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pk_employee_salaries");

            entity.ToTable("employee_salaries", tb => tb.HasComment("The effective-dated salary structure — basic, housing, transport and any further components — that a payroll run resolves as of the period, in the employing company's currency. @tier:T @owner:Finance @retention:Keep"));

            entity.HasIndex(e => new { e.TenantId, e.ApprovalRequestId }, "ix_employee_salaries__approval_request_id");

            entity.HasIndex(e => new { e.TenantId, e.EmployeeId, e.EffectiveFrom }, "ix_employee_salaries__as_of").IsDescending(false, false, true);

            entity.HasIndex(e => new { e.TenantId, e.Id }, "uq_employee_salaries__tenant_id_id").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.ApprovalRequestId).HasColumnName("approval_request_id");
            entity.Property(e => e.Basic)
                .HasPrecision(18, 2)
                .HasColumnName("basic");
            entity.Property(e => e.ChangeReason).HasColumnName("change_reason");
            entity.Property(e => e.Components)
                .HasComment("Amounts only. There is no per-row currency column anywhere; the currency is companies.currency_code (§13.2).")
                .HasColumnType("jsonb")
                .HasColumnName("components");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.EffectiveFrom).HasColumnName("effective_from");
            entity.Property(e => e.EffectiveTo).HasColumnName("effective_to");
            entity.Property(e => e.EmployeeId).HasColumnName("employee_id");
            entity.Property(e => e.Housing)
                .HasPrecision(18, 2)
                .HasColumnName("housing");
            entity.Property(e => e.HousingInKind)
                .HasDefaultValue(false)
                .HasComment("When true, housing is deemed at the statutory percentage of basic for the contributory wage rather than paid in cash (§2.E).")
                .HasColumnName("housing_in_kind");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.Transport)
                .HasPrecision(18, 2)
                .HasColumnName("transport");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");

            entity.HasOne(d => d.ApprovalRequest).WithMany(p => p.EmployeeSalaries)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.ApprovalRequestId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_employee_salaries__approval_request_id");

            entity.HasOne(d => d.Employee).WithMany(p => p.EmployeeSalaries)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.EmployeeId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_employee_salaries__employee_id");
        });

        modelBuilder.Entity<EosCalculation>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pk_eos_calculations");

            entity.ToTable("eos_calculations", tb => tb.HasComment("Computes an end-of-service award under the Labour Law articles that apply to the separation, freezing the resolved statutory bands so the figure can be reconstructed years later. @tier:T @owner:Finance @retention:84m-keep"));

            entity.HasIndex(e => new { e.TenantId, e.EmployeeId }, "ix_eos_calculations__employee_id");

            entity.HasIndex(e => new { e.TenantId, e.SettlementId }, "ix_eos_calculations__settlement_id");

            entity.HasIndex(e => new { e.TenantId, e.Id }, "uq_eos_calculations__tenant_id").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.Amount)
                .HasPrecision(18, 2)
                .HasColumnName("amount");
            entity.Property(e => e.CalculationDate).HasColumnName("calculation_date");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.EligibleWage)
                .HasPrecision(18, 2)
                .HasColumnName("eligible_wage");
            entity.Property(e => e.EmployeeId).HasColumnName("employee_id");
            entity.Property(e => e.ExcludedUnpaidDays)
                .HasDefaultValue(0)
                .HasColumnName("excluded_unpaid_days");
            entity.Property(e => e.LastWageBasis)
                .HasMaxLength(40)
                .HasColumnName("last_wage_basis");
            entity.Property(e => e.PriorPaidDeducted)
                .HasPrecision(18, 2)
                .HasColumnName("prior_paid_deducted");
            entity.Property(e => e.RulesSnapshot)
                .HasDefaultValueSql("'{}'::jsonb")
                .HasColumnType("jsonb")
                .HasColumnName("rules_snapshot");
            entity.Property(e => e.RulesVersion)
                .HasMaxLength(40)
                .HasColumnName("rules_version");
            entity.Property(e => e.SeparationReason)
                .HasMaxLength(40)
                .HasColumnName("separation_reason");
            entity.Property(e => e.ServiceDays).HasColumnName("service_days");
            entity.Property(e => e.ServiceEndDate).HasColumnName("service_end_date");
            entity.Property(e => e.ServiceStartDate).HasColumnName("service_start_date");
            entity.Property(e => e.SettlementId).HasColumnName("settlement_id");
            entity.Property(e => e.Status)
                .HasMaxLength(40)
                .HasDefaultValueSql("'Estimate'::character varying")
                .HasColumnName("status");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");

            entity.HasOne(d => d.Employee).WithMany(p => p.EosCalculations)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.EmployeeId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_eos_calculations__employee_id");

            entity.HasOne(d => d.FinalSettlement).WithMany(p => p.EosCalculations)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.SettlementId })
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("fk_eos_calculations__settlement_id");
        });

        modelBuilder.Entity<File>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pk_files");

            entity.ToTable("files", tb => tb.HasComment("Every stored blob with its hash and purge state, so PDPL erasure can delete the object while the referencing row keeps the sha256 as evidence the document existed. @tier:T @owner:HR @retention:84-months-from-Expiry-then-Purge"));

            entity.HasIndex(e => e.PurgeState, "ix_files__pending_purge").HasFilter("((purge_state)::text = 'PendingPurge'::text)");

            entity.HasIndex(e => new { e.TenantId, e.UploadedBy }, "ix_files__uploaded_by");

            entity.HasIndex(e => e.StorageKey, "uq_files__storage_key").IsUnique();

            entity.HasIndex(e => new { e.TenantId, e.Id }, "uq_files__tenant_id_id").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.Bucket)
                .HasMaxLength(63)
                .HasColumnName("bucket");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.Mime)
                .HasMaxLength(255)
                .HasColumnName("mime");
            entity.Property(e => e.PurgeState)
                .HasMaxLength(40)
                .HasDefaultValueSql("'Active'::character varying")
                .HasColumnName("purge_state");
            entity.Property(e => e.PurgedAt).HasColumnName("purged_at");
            entity.Property(e => e.Purpose)
                .HasMaxLength(40)
                .HasColumnName("purpose");
            entity.Property(e => e.RetentionUntil).HasColumnName("retention_until");
            entity.Property(e => e.Sha256)
                .HasComment("Lowercase hex digest. Survives a purge as evidence (§12.3).")
                .HasColumnName("sha256");
            entity.Property(e => e.SizeBytes).HasColumnName("size_bytes");
            entity.Property(e => e.StorageKey).HasColumnName("storage_key");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
            entity.Property(e => e.UploadedBy).HasColumnName("uploaded_by");

            entity.HasOne(d => d.Tenant).WithMany(p => p.Files)
                .HasForeignKey(d => d.TenantId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_files__tenant_id");

            entity.HasOne(d => d.User).WithMany(p => p.Files)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.UploadedBy })
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("fk_files__uploaded_by");
        });

        modelBuilder.Entity<FinalSettlement>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pk_final_settlements");

            entity.ToTable("final_settlements", tb => tb.HasComment("Is the separation case and settlement header for one employee, carrying the clearance checklist, the approved totals and the payroll run the settlement was paid through. @tier:C @owner:Finance @retention:84m-keep"));

            entity.HasIndex(e => new { e.TenantId, e.ApprovalRequestId }, "ix_final_settlements__approval_request_id");

            entity.HasIndex(e => new { e.TenantId, e.CompanyId }, "ix_final_settlements__company_id");

            entity.HasIndex(e => new { e.TenantId, e.EmployeeId }, "ix_final_settlements__employee_id");

            entity.HasIndex(e => new { e.TenantId, e.PaidViaRunId }, "ix_final_settlements__paid_via_run_id");

            entity.HasIndex(e => new { e.TenantId, e.SettlementNumber }, "uq_final_settlements__settlement_number").IsUnique();

            entity.HasIndex(e => new { e.TenantId, e.Id }, "uq_final_settlements__tenant_id").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.ApprovalRequestId).HasColumnName("approval_request_id");
            entity.Property(e => e.ApprovedAt).HasColumnName("approved_at");
            entity.Property(e => e.CancelledAt).HasColumnName("cancelled_at");
            entity.Property(e => e.Clearance)
                .HasDefaultValueSql("'[]'::jsonb")
                .HasColumnType("jsonb")
                .HasColumnName("clearance");
            entity.Property(e => e.CompanyId).HasColumnName("company_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.Deductions)
                .HasPrecision(18, 2)
                .HasColumnName("deductions");
            entity.Property(e => e.EmployeeId).HasColumnName("employee_id");
            entity.Property(e => e.Gross)
                .HasPrecision(18, 2)
                .HasColumnName("gross");
            entity.Property(e => e.LastWorkingDay).HasColumnName("last_working_day");
            entity.Property(e => e.Net)
                .HasPrecision(18, 2)
                .HasColumnName("net");
            entity.Property(e => e.NoticeGivenOn).HasColumnName("notice_given_on");
            entity.Property(e => e.NoticeServedDays).HasColumnName("notice_served_days");
            entity.Property(e => e.PaidAt).HasColumnName("paid_at");
            entity.Property(e => e.PaidViaRunId).HasColumnName("paid_via_run_id");
            entity.Property(e => e.SeparationType)
                .HasMaxLength(40)
                .HasColumnName("separation_type");
            entity.Property(e => e.SettlementNumber)
                .HasMaxLength(40)
                .HasColumnName("settlement_number");
            entity.Property(e => e.Status)
                .HasMaxLength(40)
                .HasDefaultValueSql("'Draft'::character varying")
                .HasColumnName("status");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");

            entity.HasOne(d => d.ApprovalRequest).WithMany(p => p.FinalSettlements)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.ApprovalRequestId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_final_settlements__approval_request_id");

            entity.HasOne(d => d.Company).WithMany(p => p.FinalSettlements)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.CompanyId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_final_settlements__company_id");

            entity.HasOne(d => d.Employee).WithMany(p => p.FinalSettlements)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.EmployeeId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_final_settlements__employee_id");

            entity.HasOne(d => d.PayrollRun).WithMany(p => p.FinalSettlements)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.PaidViaRunId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_final_settlements__paid_via_run_id");
        });

        modelBuilder.Entity<FinalSettlementLine>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pk_final_settlement_lines");

            entity.ToTable("final_settlement_lines", tb => tb.HasComment("Itemises a final settlement into its end-of-service, encashment, notice-pay and recovery components, frozen once the settlement is approved. @tier:C @owner:Finance @retention:84m-keep"));

            entity.HasIndex(e => new { e.TenantId, e.LoanInstallmentId }, "ix_final_settlement_lines__loan_installment_id");

            entity.HasIndex(e => new { e.TenantId, e.SettlementId }, "ix_final_settlement_lines__settlement_id");

            entity.HasIndex(e => new { e.TenantId, e.Id }, "uq_final_settlement_lines__tenant_id").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.Amount)
                .HasPrecision(18, 2)
                .HasColumnName("amount");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.Description).HasColumnName("description");
            entity.Property(e => e.Kind)
                .HasMaxLength(40)
                .HasColumnName("kind");
            entity.Property(e => e.LoanInstallmentId).HasColumnName("loan_installment_id");
            entity.Property(e => e.RulesVersion)
                .HasMaxLength(40)
                .HasColumnName("rules_version");
            entity.Property(e => e.SettlementId).HasColumnName("settlement_id");
            entity.Property(e => e.SourceId).HasColumnName("source_id");
            entity.Property(e => e.SourceType)
                .HasMaxLength(40)
                .HasColumnName("source_type");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");

            entity.HasOne(d => d.LoanInstallment).WithMany(p => p.FinalSettlementLines)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.LoanInstallmentId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_final_settlement_lines__loan_installment_id");

            entity.HasOne(d => d.FinalSettlement).WithMany(p => p.FinalSettlementLines)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.SettlementId })
                .HasConstraintName("fk_final_settlement_lines__settlement_id");
        });

        modelBuilder.Entity<GlJournal>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pk_gl_journals");

            entity.ToTable("gl_journals", tb => tb.HasComment("Represents one general-ledger journal per source event, carrying the exported file, its hash, the ERP acknowledgement and the reversal chain. @tier:C @owner:Finance @retention:84m-keep"));

            entity.HasIndex(e => new { e.TenantId, e.CompanyId }, "ix_gl_journals__company_id");

            entity.HasIndex(e => new { e.TenantId, e.FileId }, "ix_gl_journals__file_id");

            entity.HasIndex(e => new { e.TenantId, e.ReversalOfId }, "ix_gl_journals__reversal_of_id");

            entity.HasIndex(e => new { e.TenantId, e.IdempotencyKey }, "uq_gl_journals__idempotency_key").IsUnique();

            entity.HasIndex(e => new { e.TenantId, e.SourceType, e.SourceId, e.ReversalOfId }, "uq_gl_journals__source").IsUnique();

            entity.HasIndex(e => new { e.TenantId, e.Id }, "uq_gl_journals__tenant_id").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.CompanyId).HasColumnName("company_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.ErpReference).HasColumnName("erp_reference");
            entity.Property(e => e.ExportFileSha256).HasColumnName("export_file_sha256");
            entity.Property(e => e.ExportedAt).HasColumnName("exported_at");
            entity.Property(e => e.FileId).HasColumnName("file_id");
            entity.Property(e => e.IdempotencyKey).HasColumnName("idempotency_key");
            entity.Property(e => e.Month).HasColumnName("month");
            entity.Property(e => e.PostedAt).HasColumnName("posted_at");
            entity.Property(e => e.ReversalOfId).HasColumnName("reversal_of_id");
            entity.Property(e => e.SourceId).HasColumnName("source_id");
            entity.Property(e => e.SourceType)
                .HasMaxLength(40)
                .HasColumnName("source_type");
            entity.Property(e => e.Status)
                .HasMaxLength(40)
                .HasDefaultValueSql("'Draft'::character varying")
                .HasColumnName("status");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
            entity.Property(e => e.Year).HasColumnName("year");

            entity.HasOne(d => d.Company).WithMany(p => p.GlJournals)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.CompanyId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_gl_journals__company_id");

            entity.HasOne(d => d.File).WithMany(p => p.GlJournals)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.FileId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_gl_journals__file_id");

            entity.HasOne(d => d.GlJournalNavigation).WithMany(p => p.InverseGlJournalNavigation)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.ReversalOfId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_gl_journals__reversal_of_id");
        });

        modelBuilder.Entity<GlJournalLine>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pk_gl_journal_lines");

            entity.ToTable("gl_journal_lines", tb => tb.HasComment("Carries the balanced debit and credit lines of a GL journal together with their cost-centre and project segments. @tier:C @owner:Finance @retention:84m-keep"));

            entity.HasIndex(e => new { e.TenantId, e.CostCenterId }, "ix_gl_journal_lines__cost_center_id");

            entity.HasIndex(e => new { e.TenantId, e.JournalId }, "ix_gl_journal_lines__journal_id");

            entity.HasIndex(e => new { e.TenantId, e.Id }, "uq_gl_journal_lines__tenant_id").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.Account)
                .HasMaxLength(64)
                .HasColumnName("account");
            entity.Property(e => e.CostCenterId).HasColumnName("cost_center_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.Credit)
                .HasPrecision(18, 2)
                .HasColumnName("credit");
            entity.Property(e => e.Debit)
                .HasPrecision(18, 2)
                .HasColumnName("debit");
            entity.Property(e => e.Description).HasColumnName("description");
            entity.Property(e => e.JournalId).HasColumnName("journal_id");
            entity.Property(e => e.LineOrder)
                .HasDefaultValue(0)
                .HasColumnName("line_order");
            entity.Property(e => e.ProjectCode)
                .HasMaxLength(64)
                .HasColumnName("project_code");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");

            entity.HasOne(d => d.CostCenter).WithMany(p => p.GlJournalLines)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.CostCenterId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_gl_journal_lines__cost_center_id");

            entity.HasOne(d => d.GlJournal).WithMany(p => p.GlJournalLines)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.JournalId })
                .HasConstraintName("fk_gl_journal_lines__journal_id");
        });

        modelBuilder.Entity<GlMapping>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pk_gl_mappings");

            entity.ToTable("gl_mappings", tb => tb.HasComment("Maps each payroll GL driver, optionally narrowed by company and cost centre, onto the debit and credit account codes owned by the customer ERP chart of accounts. @tier:T @owner:Finance @retention:tenant-lifecycle"));

            entity.HasIndex(e => new { e.TenantId, e.CostCenterId }, "ix_gl_mappings__cost_center_id");

            entity.HasIndex(e => new { e.TenantId, e.CompanyId, e.GlDriver, e.CostCenterId }, "uq_gl_mappings__driver_scope").IsUnique();

            entity.HasIndex(e => new { e.TenantId, e.Id }, "uq_gl_mappings__tenant_id").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.CompanyId).HasColumnName("company_id");
            entity.Property(e => e.CostCenterId).HasColumnName("cost_center_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.CreditAccount)
                .HasMaxLength(64)
                .HasColumnName("credit_account");
            entity.Property(e => e.DebitAccount)
                .HasMaxLength(64)
                .HasColumnName("debit_account");
            entity.Property(e => e.GlDriver)
                .HasMaxLength(40)
                .HasColumnName("gl_driver");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true)
                .HasColumnName("is_active");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");

            entity.HasOne(d => d.Company).WithMany(p => p.GlMappings)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.CompanyId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_gl_mappings__company_id");

            entity.HasOne(d => d.CostCenter).WithMany(p => p.GlMappings)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.CostCenterId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_gl_mappings__cost_center_id");
        });

        modelBuilder.Entity<GlPeriodClose>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pk_gl_period_closes");

            entity.ToTable("gl_period_closes", tb => tb.HasComment("Records the finance lock on one accounting period per company, including who closed it and the reason any reopening was granted. @tier:C @owner:Finance @retention:84m-keep"));

            entity.HasIndex(e => new { e.TenantId, e.CompanyId, e.Year, e.Month }, "uq_gl_period_closes__period").IsUnique();

            entity.HasIndex(e => new { e.TenantId, e.Id }, "uq_gl_period_closes__tenant_id").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.ClosedAt).HasColumnName("closed_at");
            entity.Property(e => e.ClosedBy).HasColumnName("closed_by");
            entity.Property(e => e.CompanyId).HasColumnName("company_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.Month).HasColumnName("month");
            entity.Property(e => e.ReopenReason).HasColumnName("reopen_reason");
            entity.Property(e => e.ReopenedAt).HasColumnName("reopened_at");
            entity.Property(e => e.ReopenedBy).HasColumnName("reopened_by");
            entity.Property(e => e.Status)
                .HasMaxLength(40)
                .HasDefaultValueSql("'Open'::character varying")
                .HasColumnName("status");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
            entity.Property(e => e.Year).HasColumnName("year");

            entity.HasOne(d => d.User).WithMany(p => p.GlPeriodCloseUsers)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.ClosedBy })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_gl_period_closes__closed_by");

            entity.HasOne(d => d.Company).WithMany(p => p.GlPeriodCloses)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.CompanyId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_gl_period_closes__company_id");

            entity.HasOne(d => d.UserNavigation).WithMany(p => p.GlPeriodCloseUserNavigations)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.ReopenedBy })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_gl_period_closes__reopened_by");
        });

        modelBuilder.Entity<GosiFiling>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pk_gosi_filings");

            entity.ToTable("gosi_filings", tb => tb.HasComment("Holds the monthly GOSI return per establishment exactly as filed, with its seven branch-by-payer totals, the invoice it is reconciled against and the revision that supersedes a correction. @tier:C @owner:Finance @retention:84m-keep"));

            entity.HasIndex(e => new { e.TenantId, e.FileId }, "ix_gosi_filings__file_id");

            entity.HasIndex(e => new { e.TenantId, e.GosiRegistrationNo }, "ix_gosi_filings__gosi_registration_no");

            entity.HasIndex(e => new { e.TenantId, e.CompanyId, e.Year, e.Month }, "ix_gosi_filings__period");

            entity.HasIndex(e => new { e.TenantId, e.CompanyId, e.GosiRegistrationNo, e.Year, e.Month, e.Revision }, "uq_gosi_filings__period").IsUnique();

            entity.HasIndex(e => new { e.TenantId, e.Id }, "uq_gosi_filings__tenant_id").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.AnnuitiesEmployee)
                .HasPrecision(18, 2)
                .HasColumnName("annuities_employee");
            entity.Property(e => e.AnnuitiesEmployer)
                .HasPrecision(18, 2)
                .HasColumnName("annuities_employer");
            entity.Property(e => e.CompanyId).HasColumnName("company_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.EmployeeCount)
                .HasDefaultValue(0)
                .HasColumnName("employee_count");
            entity.Property(e => e.FileId).HasColumnName("file_id");
            entity.Property(e => e.FileSha256).HasColumnName("file_sha256");
            entity.Property(e => e.FiledAt).HasColumnName("filed_at");
            entity.Property(e => e.FiledBy).HasColumnName("filed_by");
            entity.Property(e => e.GosiInvoiceAmount)
                .HasPrecision(18, 2)
                .HasColumnName("gosi_invoice_amount");
            entity.Property(e => e.GosiRegistrationNo)
                .HasMaxLength(20)
                .HasColumnName("gosi_registration_no");
            entity.Property(e => e.Month).HasColumnName("month");
            entity.Property(e => e.OccupationalHazardsEmployer)
                .HasPrecision(18, 2)
                .HasColumnName("occupational_hazards_employer");
            entity.Property(e => e.ReconciledAt).HasColumnName("reconciled_at");
            entity.Property(e => e.Revision)
                .HasDefaultValue(1)
                .HasColumnName("revision");
            entity.Property(e => e.RulesVersion)
                .HasMaxLength(40)
                .HasColumnName("rules_version");
            entity.Property(e => e.SanedEmployee)
                .HasPrecision(18, 2)
                .HasColumnName("saned_employee");
            entity.Property(e => e.SanedEmployer)
                .HasPrecision(18, 2)
                .HasColumnName("saned_employer");
            entity.Property(e => e.Status)
                .HasMaxLength(40)
                .HasDefaultValueSql("'Draft'::character varying")
                .HasColumnName("status");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.TotalAmount)
                .HasPrecision(18, 2)
                .HasColumnName("total_amount");
            entity.Property(e => e.TotalContributoryWage)
                .HasPrecision(18, 2)
                .HasColumnName("total_contributory_wage");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
            entity.Property(e => e.VarianceAmount)
                .HasPrecision(18, 2)
                .HasColumnName("variance_amount");
            entity.Property(e => e.VarianceReason).HasColumnName("variance_reason");
            entity.Property(e => e.Year).HasColumnName("year");

            entity.HasOne(d => d.Company).WithMany(p => p.GosiFilingCompanies)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.CompanyId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_gosi_filings__company_id");

            entity.HasOne(d => d.File).WithMany(p => p.GosiFilings)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.FileId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_gosi_filings__file_id");

            entity.HasOne(d => d.User).WithMany(p => p.GosiFilings)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.FiledBy })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_gosi_filings__filed_by");

            entity.HasOne(d => d.CompanyNavigation).WithMany(p => p.GosiFilingCompanyNavigations)
                .HasPrincipalKey(p => new { p.TenantId, p.GosiRegistrationNo })
                .HasForeignKey(d => new { d.TenantId, d.GosiRegistrationNo })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_gosi_filings__gosi_registration_no");
        });

        modelBuilder.Entity<Grade>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pk_grades");

            entity.ToTable("grades", tb => tb.HasComment("Salary grades with their basic-pay band and an optional component pay scale, used to validate and default an employee's salary structure. @tier:T @owner:HR"));

            entity.HasIndex(e => new { e.TenantId, e.Code }, "uq_grades__tenant_id_code").IsUnique();

            entity.HasIndex(e => new { e.TenantId, e.Id }, "uq_grades__tenant_id_id").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.Code)
                .HasMaxLength(40)
                .HasColumnName("code");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true)
                .HasColumnName("is_active");
            entity.Property(e => e.MaxBasic)
                .HasPrecision(18, 2)
                .HasColumnName("max_basic");
            entity.Property(e => e.MinBasic)
                .HasPrecision(18, 2)
                .HasColumnName("min_basic");
            entity.Property(e => e.Name).HasColumnName("name");
            entity.Property(e => e.PayScale)
                .HasColumnType("jsonb")
                .HasColumnName("pay_scale");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");

            entity.HasOne(d => d.Tenant).WithMany(p => p.Grades)
                .HasForeignKey(d => d.TenantId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_grades__tenant_id");
        });

        modelBuilder.Entity<LeaveLedger>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pk_leave_ledger");

            entity.ToTable("leave_ledger", tb => tb.HasComment("Is the append-only record of every movement in an employee leave balance, where a correction is a reversing row and the balance itself is only ever read through v_leave_balances. @tier:T @owner:HR @retention:84m-keep"));

            entity.HasIndex(e => new { e.TenantId, e.EmployeeId, e.LeaveTypeId }, "ix_leave_ledger__balance");

            entity.HasIndex(e => new { e.TenantId, e.EmployeeId }, "ix_leave_ledger__employee_id");

            entity.HasIndex(e => new { e.TenantId, e.LeaveTypeId }, "ix_leave_ledger__leave_type_id");

            entity.HasIndex(e => new { e.TenantId, e.IdempotencyKey }, "uq_leave_ledger__idempotency_key").IsUnique();

            entity.HasIndex(e => new { e.TenantId, e.Id }, "uq_leave_ledger__tenant_id").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.Days)
                .HasPrecision(9, 2)
                .HasColumnName("days");
            entity.Property(e => e.EmployeeId).HasColumnName("employee_id");
            entity.Property(e => e.EntryDate).HasColumnName("entry_date");
            entity.Property(e => e.EntryType)
                .HasMaxLength(40)
                .HasColumnName("entry_type");
            entity.Property(e => e.IdempotencyKey).HasColumnName("idempotency_key");
            entity.Property(e => e.LeaveTypeId).HasColumnName("leave_type_id");
            entity.Property(e => e.Reason).HasColumnName("reason");
            entity.Property(e => e.SourceId).HasColumnName("source_id");
            entity.Property(e => e.SourceType)
                .HasMaxLength(40)
                .HasColumnName("source_type");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");

            entity.HasOne(d => d.Employee).WithMany(p => p.LeaveLedgers)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.EmployeeId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_leave_ledger__employee_id");

            entity.HasOne(d => d.LeaveType).WithMany(p => p.LeaveLedgers)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.LeaveTypeId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_leave_ledger__leave_type_id");
        });

        modelBuilder.Entity<LeaveRequest>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pk_leave_requests");

            entity.ToTable("leave_requests", tb => tb.HasComment("Captures an employee request to take leave or to encash it, with the per-day breakdown and the approval decision that turns it into a ledger movement. @tier:T @owner:HR @retention:84m-keep"));

            entity.HasIndex(e => new { e.TenantId, e.ApprovalRequestId }, "ix_leave_requests__approval_request_id");

            entity.HasIndex(e => new { e.TenantId, e.EmployeeId }, "ix_leave_requests__employee_id");

            entity.HasIndex(e => new { e.TenantId, e.LeaveTypeId }, "ix_leave_requests__leave_type_id");

            entity.HasIndex(e => new { e.TenantId, e.Status }, "ix_leave_requests__pending").HasFilter("((status)::text = 'PendingApproval'::text)");

            entity.HasIndex(e => new { e.TenantId, e.Id }, "uq_leave_requests__tenant_id").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.ApprovalRequestId).HasColumnName("approval_request_id");
            entity.Property(e => e.CancelledAt).HasColumnName("cancelled_at");
            entity.Property(e => e.ContactDuringLeave).HasColumnName("contact_during_leave");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DayBreakdown)
                .HasDefaultValueSql("'[]'::jsonb")
                .HasColumnType("jsonb")
                .HasColumnName("day_breakdown");
            entity.Property(e => e.Days)
                .HasPrecision(9, 2)
                .HasColumnName("days");
            entity.Property(e => e.DecidedAt).HasColumnName("decided_at");
            entity.Property(e => e.EmployeeId).HasColumnName("employee_id");
            entity.Property(e => e.EndDate).HasColumnName("end_date");
            entity.Property(e => e.LeaveTypeId).HasColumnName("leave_type_id");
            entity.Property(e => e.Reason).HasColumnName("reason");
            entity.Property(e => e.RequestKind)
                .HasMaxLength(40)
                .HasDefaultValueSql("'Leave'::character varying")
                .HasColumnName("request_kind");
            entity.Property(e => e.ReturnDate).HasColumnName("return_date");
            entity.Property(e => e.StartDate).HasColumnName("start_date");
            entity.Property(e => e.Status)
                .HasMaxLength(40)
                .HasDefaultValueSql("'Draft'::character varying")
                .HasColumnName("status");
            entity.Property(e => e.SubmittedAt).HasColumnName("submitted_at");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");

            entity.HasOne(d => d.ApprovalRequest).WithMany(p => p.LeaveRequests)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.ApprovalRequestId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_leave_requests__approval_request_id");

            entity.HasOne(d => d.Employee).WithMany(p => p.LeaveRequests)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.EmployeeId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_leave_requests__employee_id");

            entity.HasOne(d => d.LeaveType).WithMany(p => p.LeaveRequests)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.LeaveTypeId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_leave_requests__leave_type_id");
        });

        modelBuilder.Entity<LeaveType>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pk_leave_types");

            entity.ToTable("leave_types", tb => tb.HasComment("Defines each leave type a tenant offers together with its contractual entitlement, accrual, carry-forward and eligibility policy above the statutory floor. @tier:T @owner:HR @retention:tenant-lifecycle"));

            entity.HasIndex(e => new { e.TenantId, e.Code }, "uq_leave_types__code").IsUnique();

            entity.HasIndex(e => new { e.TenantId, e.Id }, "uq_leave_types__tenant_id").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.Code)
                .HasMaxLength(32)
                .HasColumnName("code");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true)
                .HasColumnName("is_active");
            entity.Property(e => e.IsPaid)
                .HasDefaultValue(true)
                .HasColumnName("is_paid");
            entity.Property(e => e.IsStatutory)
                .HasDefaultValue(false)
                .HasColumnName("is_statutory");
            entity.Property(e => e.NameAr).HasColumnName("name_ar");
            entity.Property(e => e.NameEn).HasColumnName("name_en");
            entity.Property(e => e.PayRuleKey)
                .HasMaxLength(64)
                .HasColumnName("pay_rule_key");
            entity.Property(e => e.Policy)
                .HasDefaultValueSql("'{}'::jsonb")
                .HasColumnType("jsonb")
                .HasColumnName("policy");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");

            entity.HasOne(d => d.Tenant).WithMany(p => p.LeaveTypes)
                .HasForeignKey(d => d.TenantId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_leave_types__tenant_id");
        });

        modelBuilder.Entity<Loan>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pk_loans");

            entity.ToTable("loans", tb => tb.HasComment("Holds an employee loan or salary advance with its principal, opening balance carried in at go-live, approval and the outstanding amount reconciled against its recoveries. @tier:T @owner:Finance @retention:84m-keep"));

            entity.HasIndex(e => new { e.TenantId, e.EmployeeId }, "ix_loans__active_recoverable").HasFilter("(((status)::text = 'Active'::text) AND (outstanding > (0)::numeric))");

            entity.HasIndex(e => new { e.TenantId, e.ApprovalRequestId }, "ix_loans__approval_request_id");

            entity.HasIndex(e => new { e.TenantId, e.EmployeeId }, "ix_loans__employee_id");

            entity.HasIndex(e => new { e.TenantId, e.Id }, "uq_loans__tenant_id").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.ApprovalRequestId).HasColumnName("approval_request_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DisbursedAt).HasColumnName("disbursed_at");
            entity.Property(e => e.EmployeeId).HasColumnName("employee_id");
            entity.Property(e => e.InstallmentCount).HasColumnName("installment_count");
            entity.Property(e => e.Kind)
                .HasMaxLength(40)
                .HasColumnName("kind");
            entity.Property(e => e.OpeningOutstanding)
                .HasPrecision(18, 2)
                .HasColumnName("opening_outstanding");
            entity.Property(e => e.Outstanding)
                .HasPrecision(18, 2)
                .HasColumnName("outstanding");
            entity.Property(e => e.Principal)
                .HasPrecision(18, 2)
                .HasColumnName("principal");
            entity.Property(e => e.Reason).HasColumnName("reason");
            entity.Property(e => e.SettledAt).HasColumnName("settled_at");
            entity.Property(e => e.StartMonth).HasColumnName("start_month");
            entity.Property(e => e.StartYear).HasColumnName("start_year");
            entity.Property(e => e.Status)
                .HasMaxLength(40)
                .HasDefaultValueSql("'PendingApproval'::character varying")
                .HasColumnName("status");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.TypeCode)
                .HasMaxLength(40)
                .HasColumnName("type_code");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");

            entity.HasOne(d => d.ApprovalRequest).WithMany(p => p.Loans)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.ApprovalRequestId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_loans__approval_request_id");

            entity.HasOne(d => d.Employee).WithMany(p => p.Loans)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.EmployeeId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_loans__employee_id");
        });

        modelBuilder.Entity<LoanInstallment>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pk_loan_installments");

            entity.ToTable("loan_installments", tb => tb.HasComment("Schedules each recovery of a loan or advance by due period and records whether it was recovered, waived or cancelled; the recovering payroll or settlement line points back at this row. @tier:T @owner:Finance @retention:84m-keep"));

            entity.HasIndex(e => new { e.TenantId, e.LoanId, e.InstallmentNumber }, "uq_loan_installments__loan_id_number").IsUnique();

            entity.HasIndex(e => new { e.TenantId, e.Id }, "uq_loan_installments__tenant_id").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.Amount)
                .HasPrecision(18, 2)
                .HasColumnName("amount");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DueMonth).HasColumnName("due_month");
            entity.Property(e => e.DueYear).HasColumnName("due_year");
            entity.Property(e => e.InstallmentNumber).HasColumnName("installment_number");
            entity.Property(e => e.Kind)
                .HasMaxLength(40)
                .HasDefaultValueSql("'Scheduled'::character varying")
                .HasColumnName("kind");
            entity.Property(e => e.LoanId).HasColumnName("loan_id");
            entity.Property(e => e.RecoveredAt).HasColumnName("recovered_at");
            entity.Property(e => e.Status)
                .HasMaxLength(40)
                .HasDefaultValueSql("'Due'::character varying")
                .HasColumnName("status");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");

            entity.HasOne(d => d.Loan).WithMany(p => p.LoanInstallments)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.LoanId })
                .HasConstraintName("fk_loan_installments__loan_id");
        });

        modelBuilder.Entity<NitaqatGrid>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pk_nitaqat_grid");

            entity.ToTable("nitaqat_grid", tb => tb.HasComment("Seeds the MHRSD Nitaqat colour bands as Saudization percentage ranges per economic activity, establishment size tier and published grid version. @tier:R @owner:Compliance @retention:indefinite-keep"));

            entity.HasIndex(e => new { e.ActivityCode, e.SizeTier, e.GridVersion, e.Band, e.EffectiveFrom }, "uq_nitaqat_grid__band").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.ActivityCode)
                .HasMaxLength(32)
                .HasColumnName("activity_code");
            entity.Property(e => e.ActivityNameAr).HasColumnName("activity_name_ar");
            entity.Property(e => e.ActivityNameEn).HasColumnName("activity_name_en");
            entity.Property(e => e.Band)
                .HasMaxLength(40)
                .HasColumnName("band");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.EffectiveFrom).HasColumnName("effective_from");
            entity.Property(e => e.EffectiveTo).HasColumnName("effective_to");
            entity.Property(e => e.GridVersion)
                .HasMaxLength(40)
                .HasColumnName("grid_version");
            entity.Property(e => e.HeadcountMax).HasColumnName("headcount_max");
            entity.Property(e => e.HeadcountMin).HasColumnName("headcount_min");
            entity.Property(e => e.MaxSaudizationPct)
                .HasPrecision(9, 6)
                .HasColumnName("max_saudization_pct");
            entity.Property(e => e.MinSaudizationPct)
                .HasPrecision(9, 6)
                .HasColumnName("min_saudization_pct");
            entity.Property(e => e.SizeTier)
                .HasMaxLength(40)
                .HasColumnName("size_tier");
            entity.Property(e => e.SourceReference).HasColumnName("source_reference");
        });

        modelBuilder.Entity<NitaqatSnapshot>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pk_nitaqat_snapshots");

            entity.ToTable("nitaqat_snapshots", tb => tb.HasComment("Freezes an establishment Saudization standing on one date, with the weighted headcounts, achieved percentage, awarded band and the per-employee breakdown that the figure drills down to. @tier:C @owner:Compliance @retention:84m-keep"));

            entity.HasIndex(e => new { e.TenantId, e.CompanyId, e.AsOfDate }, "uq_nitaqat_snapshots__as_of").IsUnique();

            entity.HasIndex(e => new { e.TenantId, e.Id }, "uq_nitaqat_snapshots__tenant_id").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.AchievedPct)
                .HasPrecision(9, 6)
                .HasColumnName("achieved_pct");
            entity.Property(e => e.ActivityCode)
                .HasMaxLength(32)
                .HasColumnName("activity_code");
            entity.Property(e => e.AsOfDate).HasColumnName("as_of_date");
            entity.Property(e => e.Band)
                .HasMaxLength(40)
                .HasColumnName("band");
            entity.Property(e => e.CompanyId).HasColumnName("company_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.EmployeeBreakdown)
                .HasDefaultValueSql("'[]'::jsonb")
                .HasColumnType("jsonb")
                .HasColumnName("employee_breakdown");
            entity.Property(e => e.GridVersion)
                .HasMaxLength(40)
                .HasColumnName("grid_version");
            entity.Property(e => e.RulesVersion)
                .HasMaxLength(40)
                .HasColumnName("rules_version");
            entity.Property(e => e.SaudiWeighted)
                .HasPrecision(18, 2)
                .HasColumnName("saudi_weighted");
            entity.Property(e => e.SizeTier)
                .HasMaxLength(40)
                .HasColumnName("size_tier");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.TotalHeadcount)
                .HasDefaultValue(0)
                .HasColumnName("total_headcount");
            entity.Property(e => e.TotalWeighted)
                .HasPrecision(18, 2)
                .HasColumnName("total_weighted");

            entity.HasOne(d => d.Company).WithMany(p => p.NitaqatSnapshots)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.CompanyId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_nitaqat_snapshots__company_id");
        });

        modelBuilder.Entity<Notification>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pk_notifications");

            entity.ToTable("notifications", tb => tb.HasComment("Is one in-app inbox item for a user, deduplicated by idempotency key so a retried producer cannot notify twice. @tier:T @owner:Platform @retention:12m-purge"));

            entity.HasIndex(e => new { e.TenantId, e.UserId, e.CreatedAt }, "ix_notifications__unread")
                .IsDescending(false, false, true)
                .HasFilter("(read_at IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.UserId }, "ix_notifications__user_id");

            entity.HasIndex(e => new { e.TenantId, e.IdempotencyKey }, "uq_notifications__idempotency_key").IsUnique();

            entity.HasIndex(e => new { e.TenantId, e.Id }, "uq_notifications__tenant_id").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.Body).HasColumnName("body");
            entity.Property(e => e.Category)
                .HasMaxLength(40)
                .HasColumnName("category");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.IdempotencyKey).HasColumnName("idempotency_key");
            entity.Property(e => e.Link).HasColumnName("link");
            entity.Property(e => e.ReadAt).HasColumnName("read_at");
            entity.Property(e => e.SourceId).HasColumnName("source_id");
            entity.Property(e => e.SourceType)
                .HasMaxLength(40)
                .HasColumnName("source_type");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.Title).HasColumnName("title");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
            entity.Property(e => e.UserId).HasColumnName("user_id");

            entity.HasOne(d => d.User).WithMany(p => p.Notifications)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.UserId })
                .HasConstraintName("fk_notifications__user_id");
        });

        modelBuilder.Entity<NotificationDelivery>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pk_notification_deliveries");

            entity.ToTable("notification_deliveries", tb => tb.HasComment("Tracks each outbound email, SMS or push attempt for a notification through its retries to delivery, suppression or the dead-letter terminal state. @tier:T @owner:Platform @retention:12m-purge"));

            entity.HasIndex(e => new { e.TenantId, e.NotificationId }, "ix_notification_deliveries__notification_id");

            entity.HasIndex(e => new { e.Status, e.NextAttemptAt }, "ix_notification_deliveries__retry").HasFilter("((status)::text = ANY ((ARRAY['Queued'::character varying, 'Failed'::character varying])::text[]))");

            entity.HasIndex(e => new { e.TenantId, e.Id }, "uq_notification_deliveries__tenant_id").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.Attempts)
                .HasDefaultValue(0)
                .HasColumnName("attempts");
            entity.Property(e => e.Channel)
                .HasMaxLength(40)
                .HasColumnName("channel");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DeadLetteredAt).HasColumnName("dead_lettered_at");
            entity.Property(e => e.DeliveredAt).HasColumnName("delivered_at");
            entity.Property(e => e.Destination).HasColumnName("destination");
            entity.Property(e => e.LastError).HasColumnName("last_error");
            entity.Property(e => e.NextAttemptAt).HasColumnName("next_attempt_at");
            entity.Property(e => e.NotificationId).HasColumnName("notification_id");
            entity.Property(e => e.ProviderMessageId).HasColumnName("provider_message_id");
            entity.Property(e => e.SentAt).HasColumnName("sent_at");
            entity.Property(e => e.Status)
                .HasMaxLength(40)
                .HasDefaultValueSql("'Queued'::character varying")
                .HasColumnName("status");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");

            entity.HasOne(d => d.Notification).WithMany(p => p.NotificationDeliveries)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.NotificationId })
                .HasConstraintName("fk_notification_deliveries__notification_id");
        });

        modelBuilder.Entity<NumberSequence>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pk_number_sequences");

            entity.ToTable("number_sequences", tb => tb.HasComment("Allocates every human-facing number (employee, letter, run, WPS batch, settlement, GOSI filing, timesheet) with one UPDATE ... RETURNING, so no counter ever lives in a settings blob. @tier:T @owner:Platform"));

            entity.HasIndex(e => new { e.TenantId, e.CompanyId, e.ScopeKey, e.PeriodKey }, "uq_number_sequences__scope").IsUnique();

            entity.HasIndex(e => new { e.TenantId, e.Id }, "uq_number_sequences__tenant_id_id").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.CompanyId).HasColumnName("company_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.NextValue)
                .HasDefaultValue(1L)
                .HasColumnName("next_value");
            entity.Property(e => e.Pattern)
                .HasMaxLength(64)
                .HasColumnName("pattern");
            entity.Property(e => e.PeriodKey)
                .HasMaxLength(16)
                .HasColumnName("period_key");
            entity.Property(e => e.Prefix)
                .HasMaxLength(16)
                .HasColumnName("prefix");
            entity.Property(e => e.ResetPeriod)
                .HasMaxLength(40)
                .HasDefaultValueSql("'None'::character varying")
                .HasColumnName("reset_period");
            entity.Property(e => e.ScopeKey)
                .HasMaxLength(40)
                .HasColumnName("scope_key");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");

            entity.HasOne(d => d.Tenant).WithMany(p => p.NumberSequences)
                .HasForeignKey(d => d.TenantId)
                .HasConstraintName("fk_number_sequences__tenant_id");

            entity.HasOne(d => d.Company).WithMany(p => p.NumberSequences)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.CompanyId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_number_sequences__company_id");
        });

        modelBuilder.Entity<OvertimeRequest>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pk_overtime_requests");

            entity.ToTable("overtime_requests", tb => tb.HasComment("Records an overtime claim in minutes for one local working day with the hourly rate, statutory multiplier and amount frozen at the moment it was calculated. @tier:T @owner:HR @retention:84m-keep"));

            entity.HasIndex(e => new { e.TenantId, e.ApprovalRequestId }, "ix_overtime_requests__approval_request_id");

            entity.HasIndex(e => new { e.TenantId, e.EmployeeId }, "ix_overtime_requests__employee_id");

            entity.HasIndex(e => new { e.TenantId, e.WorkDate, e.Status }, "ix_overtime_requests__work_date_status");

            entity.HasIndex(e => new { e.TenantId, e.Id }, "uq_overtime_requests__tenant_id").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.Amount)
                .HasPrecision(18, 2)
                .HasColumnName("amount");
            entity.Property(e => e.ApprovalRequestId).HasColumnName("approval_request_id");
            entity.Property(e => e.BasicHourlyRate)
                .HasPrecision(18, 2)
                .HasColumnName("basic_hourly_rate");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DecidedAt).HasColumnName("decided_at");
            entity.Property(e => e.EmployeeId).HasColumnName("employee_id");
            entity.Property(e => e.Multiplier)
                .HasPrecision(9, 6)
                .HasColumnName("multiplier");
            entity.Property(e => e.OtType)
                .HasMaxLength(40)
                .HasColumnName("ot_type");
            entity.Property(e => e.OvertimeMinutes).HasColumnName("overtime_minutes");
            entity.Property(e => e.Payout)
                .HasMaxLength(40)
                .HasDefaultValueSql("'Pay'::character varying")
                .HasColumnName("payout");
            entity.Property(e => e.Reason).HasColumnName("reason");
            entity.Property(e => e.RulesVersion)
                .HasMaxLength(40)
                .HasColumnName("rules_version");
            entity.Property(e => e.Status)
                .HasMaxLength(40)
                .HasDefaultValueSql("'Draft'::character varying")
                .HasColumnName("status");
            entity.Property(e => e.StatutoryRuleId).HasColumnName("statutory_rule_id");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
            entity.Property(e => e.WorkDate).HasColumnName("work_date");

            entity.HasOne(d => d.StatutoryRule).WithMany(p => p.OvertimeRequests)
                .HasForeignKey(d => d.StatutoryRuleId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_overtime_requests__statutory_rule_id");

            entity.HasOne(d => d.ApprovalRequest).WithMany(p => p.OvertimeRequests)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.ApprovalRequestId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_overtime_requests__approval_request_id");

            entity.HasOne(d => d.Employee).WithMany(p => p.OvertimeRequests)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.EmployeeId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_overtime_requests__employee_id");
        });

        modelBuilder.Entity<PayComponent>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pk_pay_components");

            entity.ToTable("pay_components", tb => tb.HasComment("The catalogue of everything that can appear as a line on a payslip, each declaring whether it is GOSI-contributory, EOS-eligible, prorated and which GL driver it posts through. @tier:T @owner:Finance"));

            entity.HasIndex(e => new { e.TenantId, e.Code }, "uq_pay_components__tenant_id_code").IsUnique();

            entity.HasIndex(e => new { e.TenantId, e.Id }, "uq_pay_components__tenant_id_id").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.Code)
                .HasMaxLength(32)
                .HasColumnName("code");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.EosEligible)
                .HasDefaultValue(false)
                .HasColumnName("eos_eligible");
            entity.Property(e => e.GlDriver)
                .HasMaxLength(64)
                .HasColumnName("gl_driver");
            entity.Property(e => e.GosiContributory)
                .HasDefaultValue(false)
                .HasColumnName("gosi_contributory");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true)
                .HasColumnName("is_active");
            entity.Property(e => e.IsSystem)
                .HasDefaultValue(false)
                .HasComment("Seeded per tenant at provisioning: BASIC, HOUSING, TRANSPORT, OT, GOSI_ANN_EE/ER, SANED_EE/ER, OH_ER, LOAN, ADVANCE, UNPAID_LEAVE, ABSENCE. A tenant may add components but not remove these.")
                .HasColumnName("is_system");
            entity.Property(e => e.Kind)
                .HasMaxLength(40)
                .HasColumnName("kind");
            entity.Property(e => e.NameAr).HasColumnName("name_ar");
            entity.Property(e => e.NameEn).HasColumnName("name_en");
            entity.Property(e => e.Prorate)
                .HasDefaultValue(false)
                .HasColumnName("prorate");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");

            entity.HasOne(d => d.Tenant).WithMany(p => p.PayComponents)
                .HasForeignKey(d => d.TenantId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_pay_components__tenant_id");
        });

        modelBuilder.Entity<PayrollAuditLog>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pk_payroll_audit_logs");

            entity.ToTable("payroll_audit_logs", tb => tb.HasComment("Is the separate, trigger-protected, row-chained log of every payroll state change, where each entry hash commits to its predecessor so the money path carries an unbroken chain rather than a checkpoint. @tier:T @owner:Compliance @retention:indefinite-keep"));

            entity.HasIndex(e => new { e.TenantId, e.EntryHash }, "uq_payroll_audit_logs__entry_hash").IsUnique();

            entity.HasIndex(e => new { e.TenantId, e.Seq }, "uq_payroll_audit_logs__seq").IsUnique();

            entity.HasIndex(e => new { e.TenantId, e.Id }, "uq_payroll_audit_logs__tenant_id").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.Action)
                .HasMaxLength(64)
                .HasColumnName("action");
            entity.Property(e => e.After)
                .HasColumnType("jsonb")
                .HasColumnName("after");
            entity.Property(e => e.Before)
                .HasColumnType("jsonb")
                .HasColumnName("before");
            entity.Property(e => e.CorrelationId).HasColumnName("correlation_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.Entity)
                .HasMaxLength(64)
                .HasColumnName("entity");
            entity.Property(e => e.EntityId).HasColumnName("entity_id");
            entity.Property(e => e.EntryHash).HasColumnName("entry_hash");
            entity.Property(e => e.HashAlgorithm)
                .HasMaxLength(40)
                .HasDefaultValueSql("'sha256'::character varying")
                .HasColumnName("hash_algorithm");
            entity.Property(e => e.Metadata)
                .HasColumnType("jsonb")
                .HasColumnName("metadata");
            entity.Property(e => e.PrevHash).HasColumnName("prev_hash");
            entity.Property(e => e.RunId).HasColumnName("run_id");
            entity.Property(e => e.Seq).HasColumnName("seq");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UserId).HasColumnName("user_id");
        });

        modelBuilder.Entity<PayrollInput>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pk_payroll_inputs");

            entity.ToTable("payroll_inputs", tb => tb.HasComment("Variable pay waiting to be paid — adjustments, arrears, overtime, absence, encashment — each carrying the period it BELONGS to as well as the period it is paid in, so a backdated amount recalculates GOSI as data rather than as arithmetic in a service. @tier:C @owner:Finance @retention:Keep"));

            entity.HasIndex(e => new { e.TenantId, e.CompanyId, e.RunYear, e.RunMonth, e.EmployeeId }, "ix_payroll_inputs__claimable").HasFilter("((status)::text = 'Pending'::text)");

            entity.HasIndex(e => new { e.TenantId, e.ClaimedByRunId }, "ix_payroll_inputs__claimed_by_run_id");

            entity.HasIndex(e => new { e.TenantId, e.CompanyId }, "ix_payroll_inputs__company_id");

            entity.HasIndex(e => new { e.TenantId, e.ConsumedRunId }, "ix_payroll_inputs__consumed_run_id");

            entity.HasIndex(e => new { e.TenantId, e.CostCenterId }, "ix_payroll_inputs__cost_center_id");

            entity.HasIndex(e => new { e.TenantId, e.EmployeeId }, "ix_payroll_inputs__employee_id");

            entity.HasIndex(e => new { e.TenantId, e.PayComponentCode }, "ix_payroll_inputs__pay_component_code");

            entity.HasIndex(e => new { e.TenantId, e.SourceType, e.SourceId, e.CoveredYear, e.CoveredMonth, e.PayComponentCode, e.Revision }, "uq_payroll_inputs__source_period_revision").IsUnique();

            entity.HasIndex(e => new { e.TenantId, e.Id }, "uq_payroll_inputs__tenant_id_id").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.Amount)
                .HasPrecision(18, 2)
                .HasColumnName("amount");
            entity.Property(e => e.ClaimedAt).HasColumnName("claimed_at");
            entity.Property(e => e.ClaimedByRunId)
                .HasComment("SET NULL on delete so deleting a draft run releases the claim. A crashed run releases by predicate — status back to Pending where claimed_by_run_id points at a voided run — which is what makes a retry idempotent (§F).")
                .HasColumnName("claimed_by_run_id");
            entity.Property(e => e.CompanyId).HasColumnName("company_id");
            entity.Property(e => e.ConsumedRunId).HasColumnName("consumed_run_id");
            entity.Property(e => e.CostCenterId).HasColumnName("cost_center_id");
            entity.Property(e => e.CoveredMonth).HasColumnName("covered_month");
            entity.Property(e => e.CoveredYear).HasColumnName("covered_year");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.EmployeeId).HasColumnName("employee_id");
            entity.Property(e => e.EntitledAmount)
                .HasPrecision(18, 2)
                .HasColumnName("entitled_amount");
            entity.Property(e => e.GosiBasisDelta)
                .HasPrecision(18, 2)
                .HasComment("The contributory-wage change this backdated amount causes in the COVERED period. Stored so the GOSI recalculation is data, not arithmetic in a service (§2.F).")
                .HasColumnName("gosi_basis_delta");
            entity.Property(e => e.Kind)
                .HasMaxLength(40)
                .HasColumnName("kind");
            entity.Property(e => e.PayComponentCode)
                .HasMaxLength(32)
                .HasColumnName("pay_component_code");
            entity.Property(e => e.PreviouslySettledAmount)
                .HasPrecision(18, 2)
                .HasColumnName("previously_settled_amount");
            entity.Property(e => e.Revision)
                .HasDefaultValue(1)
                .HasComment("Cancelling an input BUMPS this and inserts the replacement; it never reuses the key its replacement needs (§2.F, CONVENTIONS.md §11).")
                .HasColumnName("revision");
            entity.Property(e => e.RunMonth).HasColumnName("run_month");
            entity.Property(e => e.RunYear).HasColumnName("run_year");
            entity.Property(e => e.SourceId).HasColumnName("source_id");
            entity.Property(e => e.SourceType)
                .HasMaxLength(40)
                .HasComment("Polymorphic pointer with source_id (§16): Overtime -> overtime_requests, Leave -> leave_requests, Attendance -> attendance_days, Timesheet -> timesheets, Opening -> background_jobs, Manual/Bonus -> NULL. A nightly sweep reports unresolvable rows.")
                .HasColumnName("source_type");
            entity.Property(e => e.Status)
                .HasMaxLength(40)
                .HasDefaultValueSql("'Pending'::character varying")
                .HasColumnName("status");
            entity.Property(e => e.TargetRunType)
                .HasMaxLength(40)
                .HasColumnName("target_run_type");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");

            entity.HasOne(d => d.PayrollRun).WithMany(p => p.PayrollInputPayrollRuns)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.ClaimedByRunId })
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("fk_payroll_inputs__claimed_by_run_id");

            entity.HasOne(d => d.Company).WithMany(p => p.PayrollInputs)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.CompanyId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_payroll_inputs__company_id");

            entity.HasOne(d => d.PayrollRunNavigation).WithMany(p => p.PayrollInputPayrollRunNavigations)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.ConsumedRunId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_payroll_inputs__consumed_run_id");

            entity.HasOne(d => d.CostCenter).WithMany(p => p.PayrollInputs)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.CostCenterId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_payroll_inputs__cost_center_id");

            entity.HasOne(d => d.Employee).WithMany(p => p.PayrollInputs)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.EmployeeId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_payroll_inputs__employee_id");

            entity.HasOne(d => d.PayComponent).WithMany(p => p.PayrollInputs)
                .HasPrincipalKey(p => new { p.TenantId, p.Code })
                .HasForeignKey(d => new { d.TenantId, d.PayComponentCode })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_payroll_inputs__pay_component_code");
        });

        modelBuilder.Entity<PayrollIssue>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pk_payroll_issues");

            entity.ToTable("payroll_issues", tb => tb.HasComment("Every validation finding and standing readiness gap that blocks or warns a payroll run, with the evidence behind it and — for warnings only — who waived it and why. @tier:C @owner:Finance @retention:Keep"));

            entity.HasIndex(e => new { e.TenantId, e.EmployeeId }, "ix_payroll_issues__employee_id");

            entity.HasIndex(e => new { e.TenantId, e.EmployeeId }, "ix_payroll_issues__employee_standing").HasFilter("(run_id IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.RunId }, "ix_payroll_issues__run_blocks").HasFilter("((severity)::text = 'Block'::text)");

            entity.HasIndex(e => new { e.TenantId, e.RunId }, "ix_payroll_issues__run_id");

            entity.HasIndex(e => new { e.TenantId, e.Id }, "uq_payroll_issues__tenant_id_id").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.Code)
                .HasMaxLength(64)
                .HasColumnName("code");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DetectedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("detected_at");
            entity.Property(e => e.EmployeeId).HasColumnName("employee_id");
            entity.Property(e => e.Evidence)
                .HasColumnType("jsonb")
                .HasColumnName("evidence");
            entity.Property(e => e.GapType)
                .HasMaxLength(40)
                .HasColumnName("gap_type");
            entity.Property(e => e.Message).HasColumnName("message");
            entity.Property(e => e.OverrideAt).HasColumnName("override_at");
            entity.Property(e => e.OverrideBy).HasColumnName("override_by");
            entity.Property(e => e.OverrideReason).HasColumnName("override_reason");
            entity.Property(e => e.ResolvedAt).HasColumnName("resolved_at");
            entity.Property(e => e.RunId)
                .HasComment("NULL = a standing gap that blocks ANY run for this employee (GOSI_COHORT_UNKNOWN, IBAN_MISSING, ORG_ESTABLISHMENT_MISSING, SALARY_HELD). Non-NULL = a finding of one run, which dies with a draft run.")
                .HasColumnName("run_id");
            entity.Property(e => e.Severity)
                .HasMaxLength(40)
                .HasComment("Block is NEVER overridable — enforced by CHECK, not by the UI (§2.F). Warn may be overridden with a recorded reason.")
                .HasColumnName("severity");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");

            entity.HasOne(d => d.Employee).WithMany(p => p.PayrollIssues)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.EmployeeId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_payroll_issues__employee_id");

            entity.HasOne(d => d.User).WithMany(p => p.PayrollIssues)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.OverrideBy })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_payroll_issues__override_by");

            entity.HasOne(d => d.PayrollRun).WithMany(p => p.PayrollIssues)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.RunId })
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("fk_payroll_issues__run_id");
        });

        modelBuilder.Entity<PayrollRun>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pk_payroll_runs");

            entity.ToTable("payroll_runs", tb => tb.HasComment("One payroll execution for a company and period — regular, off-cycle, correction, final settlement or the mid-year Opening import — carrying its selection, its cached totals, the rules version it applied and the attendance range it locked. @tier:C @owner:Finance @retention:Keep"));

            entity.HasIndex(e => new { e.TenantId, e.ApprovalRequestId }, "ix_payroll_runs__approval_request_id");

            entity.HasIndex(e => new { e.TenantId, e.ParentRunId }, "ix_payroll_runs__parent_run_id");

            entity.HasIndex(e => new { e.TenantId, e.CompanyId, e.Year, e.Month, e.Status }, "ix_payroll_runs__period");

            entity.HasIndex(e => new { e.TenantId, e.SourceImportJobId }, "ix_payroll_runs__source_import_job_id");

            entity.HasIndex(e => new { e.TenantId, e.IdempotencyKey }, "uq_payroll_runs__idempotency_key").IsUnique();

            entity.HasIndex(e => new { e.TenantId, e.CompanyId, e.Year, e.Month, e.RunType }, "uq_payroll_runs__period_regular_opening")
                .IsUnique()
                .HasFilter("(((run_type)::text = ANY ((ARRAY['Regular'::character varying, 'Opening'::character varying])::text[])) AND ((status)::text <> 'Voided'::text))");

            entity.HasIndex(e => new { e.TenantId, e.Id }, "uq_payroll_runs__tenant_id_id").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.ApprovalRequestId).HasColumnName("approval_request_id");
            entity.Property(e => e.ApprovedAt).HasColumnName("approved_at");
            entity.Property(e => e.AttendanceLockedRange)
                .HasComment("Authoritative for the attendance lock; attendance_days.locked_run_id is the per-row projection written in the same transaction (§11.6). CHECK-constrained to lie inside the run period. Set on Approved, cleared on Void.")
                .HasColumnName("attendance_locked_range");
            entity.Property(e => e.CalculatedAt).HasColumnName("calculated_at");
            entity.Property(e => e.CompanyId).HasColumnName("company_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.EmployeeCount)
                .HasDefaultValue(0)
                .HasColumnName("employee_count");
            entity.Property(e => e.IdempotencyKey).HasColumnName("idempotency_key");
            entity.Property(e => e.LockedAt).HasColumnName("locked_at");
            entity.Property(e => e.Month).HasColumnName("month");
            entity.Property(e => e.ParentRunId).HasColumnName("parent_run_id");
            entity.Property(e => e.RulesVersion)
                .HasMaxLength(40)
                .HasColumnName("rules_version");
            entity.Property(e => e.RunType)
                .HasMaxLength(40)
                .HasColumnName("run_type");
            entity.Property(e => e.SelectedEmployeeCount)
                .HasDefaultValue(0)
                .HasComment("The run's exit condition: it leaves Processing only when slips + explicitly excluded = this count (§10.1, §19.5).")
                .HasColumnName("selected_employee_count");
            entity.Property(e => e.Selection)
                .HasColumnType("jsonb")
                .HasColumnName("selection");
            entity.Property(e => e.SourceImportJobId)
                .HasComment("Opening-run provenance. FK deferred to the cross-domain constraints pass: background_jobs is domain R.")
                .HasColumnName("source_import_job_id");
            entity.Property(e => e.SourceSystem)
                .HasMaxLength(40)
                .HasColumnName("source_system");
            entity.Property(e => e.Status)
                .HasMaxLength(40)
                .HasDefaultValueSql("'Draft'::character varying")
                .HasColumnName("status");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.TotalDeductions)
                .HasPrecision(18, 2)
                .HasColumnName("total_deductions");
            entity.Property(e => e.TotalEmployerStatutory)
                .HasPrecision(18, 2)
                .HasColumnName("total_employer_statutory");
            entity.Property(e => e.TotalGross)
                .HasPrecision(18, 2)
                .HasComment("CACHE of SUM over included slips, reconciled by trg_run_totals only in the transaction that moves Processing -> Processed. While Processing, every total_* column is UNDEFINED and must not be displayed as authoritative (§11.2).")
                .HasColumnName("total_gross");
            entity.Property(e => e.TotalNet)
                .HasPrecision(18, 2)
                .HasColumnName("total_net");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
            entity.Property(e => e.VoidReason).HasColumnName("void_reason");
            entity.Property(e => e.Year).HasColumnName("year");

            entity.HasOne(d => d.ApprovalRequest).WithMany(p => p.PayrollRuns)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.ApprovalRequestId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_payroll_runs__approval_request_id");

            entity.HasOne(d => d.Company).WithMany(p => p.PayrollRuns)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.CompanyId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_payroll_runs__company_id");

            entity.HasOne(d => d.PayrollRunNavigation).WithMany(p => p.InversePayrollRunNavigation)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.ParentRunId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_payroll_runs__parent_run_id");

            entity.HasOne(d => d.BackgroundJob).WithMany(p => p.PayrollRuns)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.SourceImportJobId })
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("fk_payroll_runs__source_import_job_id");
        });

        modelBuilder.Entity<PayrollSlip>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pk_payroll_slips");

            entity.ToTable("payroll_slips", tb => tb.HasComment("One frozen payslip per employee per run, holding its own identity, proration, GOSI and year-to-date witnesses so it can be reprinted and reconciled years later without joining a single live row. @tier:C @owner:Finance @retention:84-months-from-RecordDate-then-Keep"));

            entity.HasIndex(e => new { e.TenantId, e.EmployeeId, e.RunId }, "ix_payroll_slips__employee_run").IsDescending(false, false, true);

            entity.HasIndex(e => new { e.TenantId, e.PayslipFileId }, "ix_payroll_slips__payslip_file_id");

            entity.HasIndex(e => new { e.TenantId, e.RunId, e.EmployeeId }, "uq_payroll_slips__run_id_employee_id").IsUnique();

            entity.HasIndex(e => new { e.TenantId, e.Id }, "uq_payroll_slips__tenant_id_id").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.ArrearsAmount)
                .HasPrecision(18, 2)
                .HasColumnName("arrears_amount");
            entity.Property(e => e.BankCode)
                .HasMaxLength(16)
                .HasColumnName("bank_code");
            entity.Property(e => e.ContributoryWage)
                .HasPrecision(18, 2)
                .HasComment("The PERIOD's computed contributory wage. Deliberately a different fact from payroll_slip_lines.applied_contributory_wage (per GOSI branch) and from employee_gosi_registrations.registered_contributory_wage (what GOSI holds) — §11.4.")
                .HasColumnName("contributory_wage");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.Deductions)
                .HasPrecision(18, 2)
                .HasColumnName("deductions");
            entity.Property(e => e.DepartmentName).HasColumnName("department_name");
            entity.Property(e => e.DesignationName).HasColumnName("designation_name");
            entity.Property(e => e.EmployeeId).HasColumnName("employee_id");
            entity.Property(e => e.EmployeeName)
                .HasComment("Identity snapshot. §2.F names these columns \"name, department, designation\"; spelled _name here so they cannot be mistaken for live joins.")
                .HasColumnName("employee_name");
            entity.Property(e => e.EmployeeNumber)
                .HasMaxLength(32)
                .HasColumnName("employee_number");
            entity.Property(e => e.EmployeeStatutoryTotal)
                .HasPrecision(18, 2)
                .HasColumnName("employee_statutory_total");
            entity.Property(e => e.EmployerGosiRegistrationNo)
                .HasMaxLength(20)
                .HasComment("Frozen snapshot, and the only copy of the registration number not bound by FK; companies.gosi_registration_no is the single owner (§11.5). It is what traces a slip to the return that carried it.")
                .HasColumnName("employer_gosi_registration_no");
            entity.Property(e => e.EmployerStatutoryTotal)
                .HasPrecision(18, 2)
                .HasColumnName("employer_statutory_total");
            entity.Property(e => e.FullBasic)
                .HasPrecision(18, 2)
                .HasColumnName("full_basic");
            entity.Property(e => e.FullHousing)
                .HasPrecision(18, 2)
                .HasColumnName("full_housing");
            entity.Property(e => e.FullTransport)
                .HasPrecision(18, 2)
                .HasColumnName("full_transport");
            entity.Property(e => e.GosiBasePolicy)
                .HasMaxLength(40)
                .HasColumnName("gosi_base_policy");
            entity.Property(e => e.GosiCohort)
                .HasMaxLength(40)
                .HasColumnName("gosi_cohort");
            entity.Property(e => e.Gross)
                .HasPrecision(18, 2)
                .HasComment("CACHE of the signed SUM of payroll_slip_lines of the matching kinds, reconciled by the deferred trg_slip_totals (§11.2). Lines are authoritative.")
                .HasColumnName("gross");
            entity.Property(e => e.Iban)
                .HasMaxLength(34)
                .HasColumnName("iban");
            entity.Property(e => e.InclusionStatus)
                .HasMaxLength(40)
                .HasDefaultValueSql("'Included'::character varying")
                .HasColumnName("inclusion_status");
            entity.Property(e => e.IsFinalWageMonth)
                .HasDefaultValue(false)
                .HasColumnName("is_final_wage_month");
            entity.Property(e => e.Language)
                .HasMaxLength(40)
                .HasColumnName("language");
            entity.Property(e => e.LoanDeductions)
                .HasPrecision(18, 2)
                .HasColumnName("loan_deductions");
            entity.Property(e => e.NationalityClass)
                .HasMaxLength(40)
                .HasColumnName("nationality_class");
            entity.Property(e => e.Net)
                .HasPrecision(18, 2)
                .HasColumnName("net");
            entity.Property(e => e.PaidDays)
                .HasPrecision(9, 2)
                .HasColumnName("paid_days");
            entity.Property(e => e.PaidFrom).HasColumnName("paid_from");
            entity.Property(e => e.PaidTo).HasColumnName("paid_to");
            entity.Property(e => e.PayslipFileId).HasColumnName("payslip_file_id");
            entity.Property(e => e.PayslipNumber)
                .HasMaxLength(40)
                .HasColumnName("payslip_number");
            entity.Property(e => e.PayslipSha256).HasColumnName("payslip_sha256");
            entity.Property(e => e.PeriodDays)
                .HasPrecision(9, 2)
                .HasColumnName("period_days");
            entity.Property(e => e.ProrationBasis)
                .HasMaxLength(40)
                .HasColumnName("proration_basis");
            entity.Property(e => e.ProrationDenominatorDays)
                .HasPrecision(9, 2)
                .HasColumnName("proration_denominator_days");
            entity.Property(e => e.ProrationFactor)
                .HasPrecision(9, 6)
                .HasColumnName("proration_factor");
            entity.Property(e => e.PublishedAt).HasColumnName("published_at");
            entity.Property(e => e.RunId).HasColumnName("run_id");
            entity.Property(e => e.TemplateId).HasColumnName("template_id");
            entity.Property(e => e.TemplateVersion).HasColumnName("template_version");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
            entity.Property(e => e.YtdContributoryWage)
                .HasPrecision(18, 2)
                .HasColumnName("ytd_contributory_wage");
            entity.Property(e => e.YtdDeductions)
                .HasPrecision(18, 2)
                .HasColumnName("ytd_deductions");
            entity.Property(e => e.YtdEmployeeStatutory)
                .HasPrecision(18, 2)
                .HasColumnName("ytd_employee_statutory");
            entity.Property(e => e.YtdEmployerStatutory)
                .HasPrecision(18, 2)
                .HasColumnName("ytd_employer_statutory");
            entity.Property(e => e.YtdGross)
                .HasPrecision(18, 2)
                .HasComment("Year-to-date set. §19.4 H7 turns the YTD query into a single-row read of the prior slip; the Opening run seeds these as the go-live carry-forward (§2.F).")
                .HasColumnName("ytd_gross");
            entity.Property(e => e.YtdNet)
                .HasPrecision(18, 2)
                .HasColumnName("ytd_net");

            entity.HasOne(d => d.Employee).WithMany(p => p.PayrollSlips)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.EmployeeId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_payroll_slips__employee_id");

            entity.HasOne(d => d.File).WithMany(p => p.PayrollSlips)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.PayslipFileId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_payroll_slips__payslip_file_id");

            entity.HasOne(d => d.PayrollRun).WithMany(p => p.PayrollSlips)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.RunId })
                .HasConstraintName("fk_payroll_slips__run_id");

            entity.HasOne(d => d.DocumentTemplate).WithMany(p => p.PayrollSlips)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.TemplateId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_payroll_slips__template_id");
        });

        modelBuilder.Entity<PayrollSlipLine>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pk_payroll_slip_lines");

            entity.ToTable("payroll_slip_lines", tb => tb.HasComment("The single line table behind every payslip, freezing the component, the GOSI branch, payer, applied wage, rule and band that produced each amount, plus where the amount came from. @tier:C @owner:Finance @retention:84-months-from-RecordDate-then-Keep"));

            entity.HasIndex(e => new { e.TenantId, e.CostCenterId }, "ix_payroll_slip_lines__cost_center_id");

            entity.HasIndex(e => new { e.TenantId, e.LoanInstallmentId }, "ix_payroll_slip_lines__loan_installment_id");

            entity.HasIndex(e => new { e.TenantId, e.PayComponentCode }, "ix_payroll_slip_lines__pay_component_code");

            entity.HasIndex(e => new { e.TenantId, e.PayrollInputId }, "ix_payroll_slip_lines__payroll_input_id");

            entity.HasIndex(e => new { e.TenantId, e.SlipId }, "ix_payroll_slip_lines__slip_id");

            entity.HasIndex(e => new { e.TenantId, e.Id }, "uq_payroll_slip_lines__tenant_id_id").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.Amount)
                .HasPrecision(18, 2)
                .HasColumnName("amount");
            entity.Property(e => e.AppliedContributoryWage)
                .HasPrecision(18, 2)
                .HasComment("The wage actually applied to THIS line's GOSI branch, which differs whenever a branch has its own floor or cap. Renamed in revision 3 to end the ambiguity with the slip-level figure (§11.4).")
                .HasColumnName("applied_contributory_wage");
            entity.Property(e => e.CostCenterId).HasColumnName("cost_center_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.GlDriver)
                .HasMaxLength(64)
                .HasColumnName("gl_driver");
            entity.Property(e => e.GosiBranch)
                .HasMaxLength(40)
                .HasColumnName("gosi_branch");
            entity.Property(e => e.GosiPayer)
                .HasMaxLength(40)
                .HasColumnName("gosi_payer");
            entity.Property(e => e.Kind)
                .HasMaxLength(40)
                .HasColumnName("kind");
            entity.Property(e => e.LoanInstallmentId)
                .HasComment("Recovery evidence, and the surviving half of the broken payroll_slip_lines <-> loan_installments cycle (§8.4). FK deferred to the cross-domain pass: loan_installments is domain I.")
                .HasColumnName("loan_installment_id");
            entity.Property(e => e.PayComponentCode)
                .HasMaxLength(32)
                .HasColumnName("pay_component_code");
            entity.Property(e => e.PayrollInputId)
                .HasComment("The line points at the input; the input does NOT point back. That is how the second cycle was broken (§8.4).")
                .HasColumnName("payroll_input_id");
            entity.Property(e => e.Quantity)
                .HasPrecision(18, 6)
                .HasColumnName("quantity");
            entity.Property(e => e.Rate)
                .HasPrecision(9, 6)
                .HasColumnName("rate");
            entity.Property(e => e.RulesVersion)
                .HasMaxLength(40)
                .HasColumnName("rules_version");
            entity.Property(e => e.SlipId).HasColumnName("slip_id");
            entity.Property(e => e.SourceRecordId)
                .HasComment("Opening-run provenance alongside source_system: which row of which legacy system this opening amount came from (§2.F).")
                .HasColumnName("source_record_id");
            entity.Property(e => e.SourceSystem)
                .HasMaxLength(40)
                .HasColumnName("source_system");
            entity.Property(e => e.SourceType)
                .HasMaxLength(40)
                .HasColumnName("source_type");
            entity.Property(e => e.StatutoryRuleBandId).HasColumnName("statutory_rule_band_id");
            entity.Property(e => e.StatutoryRuleId).HasColumnName("statutory_rule_id");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");

            entity.HasOne(d => d.StatutoryRuleBand).WithMany(p => p.PayrollSlipLines)
                .HasForeignKey(d => d.StatutoryRuleBandId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_payroll_slip_lines__statutory_rule_band_id");

            entity.HasOne(d => d.StatutoryRule).WithMany(p => p.PayrollSlipLines)
                .HasForeignKey(d => d.StatutoryRuleId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_payroll_slip_lines__statutory_rule_id");

            entity.HasOne(d => d.CostCenter).WithMany(p => p.PayrollSlipLines)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.CostCenterId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_payroll_slip_lines__cost_center_id");

            entity.HasOne(d => d.LoanInstallment).WithMany(p => p.PayrollSlipLines)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.LoanInstallmentId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_payroll_slip_lines__loan_installment_id");

            entity.HasOne(d => d.PayComponent).WithMany(p => p.PayrollSlipLines)
                .HasPrincipalKey(p => new { p.TenantId, p.Code })
                .HasForeignKey(d => new { d.TenantId, d.PayComponentCode })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_payroll_slip_lines__pay_component_code");

            entity.HasOne(d => d.PayrollInput).WithMany(p => p.PayrollSlipLines)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.PayrollInputId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_payroll_slip_lines__payroll_input_id");

            entity.HasOne(d => d.PayrollSlip).WithMany(p => p.PayrollSlipLines)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.SlipId })
                .HasConstraintName("fk_payroll_slip_lines__slip_id");
        });

        modelBuilder.Entity<Permission>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pk_permissions");

            entity.ToTable("permissions", tb => tb.HasComment("The platform permission catalogue, including the access.grant.* keys that govern delegated granting authority; retiring a permission is a migration, never a delete. @tier:R @owner:Platform @retention:Keep"));

            entity.HasIndex(e => e.Code, "uq_permissions__code").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.Code)
                .HasMaxLength(64)
                .HasColumnName("code");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.Description).HasColumnName("description");
            entity.Property(e => e.Module)
                .HasMaxLength(40)
                .HasColumnName("module");
        });

        modelBuilder.Entity<PermissionGrantorRecord>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pk_permission_grantor_records");

            entity.ToTable("permission_grantor_records", tb => tb.HasComment("Records that a named user MAY grant permissions over a stated scope, with or without sub-delegation, until a date and for a reason — the authority behind a grant, not the grant itself. @tier:T @owner:Platform"));

            entity.HasIndex(e => new { e.TenantId, e.GrantorUserId }, "ix_permission_grantor_records__grantor_user_id");

            entity.HasIndex(e => new { e.TenantId, e.RevokedBy }, "ix_permission_grantor_records__revoked_by");

            entity.HasIndex(e => new { e.TenantId, e.Id }, "uq_permission_grantor_records__tenant_id_id").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.CanSubDelegate)
                .HasDefaultValue(false)
                .HasColumnName("can_sub_delegate");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.ExpiresAt).HasColumnName("expires_at");
            entity.Property(e => e.GrantedByUserId).HasColumnName("granted_by_user_id");
            entity.Property(e => e.GrantorUserId).HasColumnName("grantor_user_id");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true)
                .HasColumnName("is_active");
            entity.Property(e => e.PermissionScope)
                .HasComment("'all', a module prefix, or an explicit key list. Sub-delegation may only narrow the parent scope (§10.11), checked in AccessManagementService.")
                .HasColumnName("permission_scope");
            entity.Property(e => e.Reason).HasColumnName("reason");
            entity.Property(e => e.RevokedAt).HasColumnName("revoked_at");
            entity.Property(e => e.RevokedBy).HasColumnName("revoked_by");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");

            entity.HasOne(d => d.User).WithMany(p => p.PermissionGrantorRecordUsers)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.GrantedByUserId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_permission_grantor_records__granted_by_user_id");

            entity.HasOne(d => d.UserNavigation).WithMany(p => p.PermissionGrantorRecordUserNavigations)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.GrantorUserId })
                .HasConstraintName("fk_permission_grantor_records__grantor_user_id");

            entity.HasOne(d => d.User1).WithMany(p => p.PermissionGrantorRecordUser1s)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.RevokedBy })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_permission_grantor_records__revoked_by");
        });

        modelBuilder.Entity<PlatformUser>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pk_platform_users");

            entity.ToTable("platform_users", tb => tb.HasComment("Platform operators and their credentials, kept outside tenancy entirely so the operator surface is policed by grant rather than by a tenant filter. @tier:P @owner:Platform @retention:12-months-after-SoftDelete-then-Anonymise"));

            entity.HasIndex(e => e.Email, "uq_platform_users__email").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.Email)
                .HasComment("Globally unique (no tenant to scope it by). Stored normalised by the application; see 001_extensions.sql on the citext question.")
                .HasColumnName("email");
            entity.Property(e => e.FailedLoginCount)
                .HasDefaultValue(0)
                .HasColumnName("failed_login_count");
            entity.Property(e => e.FullName).HasColumnName("full_name");
            entity.Property(e => e.LastLoginAt).HasColumnName("last_login_at");
            entity.Property(e => e.LockoutEnd).HasColumnName("lockout_end");
            entity.Property(e => e.MfaEnabled)
                .HasDefaultValue(false)
                .HasColumnName("mfa_enabled");
            entity.Property(e => e.MfaRecoveryHashes)
                .HasColumnType("jsonb")
                .HasColumnName("mfa_recovery_hashes");
            entity.Property(e => e.MfaSecretEncrypted).HasColumnName("mfa_secret_encrypted");
            entity.Property(e => e.PasswordHash)
                .HasComment("Secret column: REVOKE from kynex_ro by column privilege (§19.2).")
                .HasColumnName("password_hash");
            entity.Property(e => e.PlatformRole)
                .HasMaxLength(40)
                .HasColumnName("platform_role");
            entity.Property(e => e.Status)
                .HasMaxLength(40)
                .HasDefaultValueSql("'Active'::character varying")
                .HasColumnName("status");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
        });

        modelBuilder.Entity<PublicHoliday>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pk_public_holidays");

            entity.ToTable("public_holidays", tb => tb.HasComment("Named non-working days per calendar code, with the platform KSA calendar carried as the tenant_id IS NULL rows that every tenant session can read. @tier:R/T @owner:HR"));

            entity.HasIndex(e => new { e.CalendarCode, e.HolidayDate }, "ix_public_holidays__calendar_date");

            entity.HasIndex(e => new { e.TenantId, e.CalendarCode, e.HolidayDate }, "uq_public_holidays__calendar_date").IsUnique();

            entity.HasIndex(e => new { e.TenantId, e.Id }, "uq_public_holidays__tenant_id_id").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.CalendarCode)
                .HasMaxLength(40)
                .HasColumnName("calendar_code");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.HolidayDate)
                .HasComment("A local Gregorian date. Hijri occasions are named in `name` but never stored as Hijri — Hijri is always derived (§13.4).")
                .HasColumnName("holiday_date");
            entity.Property(e => e.IsPaid)
                .HasDefaultValue(true)
                .HasColumnName("is_paid");
            entity.Property(e => e.Name).HasColumnName("name");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");

            entity.HasOne(d => d.Tenant).WithMany(p => p.PublicHolidays)
                .HasForeignKey(d => d.TenantId)
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("fk_public_holidays__tenant_id");
        });

        modelBuilder.Entity<RetentionPolicy>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pk_retention_policies");

            entity.ToTable("retention_policies", tb => tb.HasComment("The PDPL retention matrix as data — one row per entity giving the legal basis, minimum period, trigger event and disposition the retention job reads instead of appsettings. @tier:R/T @owner:Compliance @retention:Keep"));

            entity.HasIndex(e => new { e.TenantId, e.Id }, "uq_retention_policies__tenant_id_id").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.Disposition)
                .HasMaxLength(40)
                .HasColumnName("disposition");
            entity.Property(e => e.EffectiveFrom).HasColumnName("effective_from");
            entity.Property(e => e.EffectiveTo).HasColumnName("effective_to");
            entity.Property(e => e.EntityName)
                .HasMaxLength(63)
                .HasColumnName("entity_name");
            entity.Property(e => e.LegalBasis).HasColumnName("legal_basis");
            entity.Property(e => e.MinimumRetentionMonths).HasColumnName("minimum_retention_months");
            entity.Property(e => e.OwnerRole)
                .HasMaxLength(40)
                .HasColumnName("owner_role");
            entity.Property(e => e.RuleKey)
                .HasMaxLength(64)
                .HasComment("Matches the C# RetentionRuleKeys constants and retention_purge_audits.rule_key (FK-free match, §Q).")
                .HasColumnName("rule_key");
            entity.Property(e => e.SourceReference).HasColumnName("source_reference");
            entity.Property(e => e.TenantId)
                .HasComment("NULL = the platform default row, visible to every session under RLS shape (b). A tenant override row may only LENGTHEN the platform period (§12.4) — enforced by trigger, not by a table CHECK, because the rule needs a subquery.")
                .HasColumnName("tenant_id");
            entity.Property(e => e.TriggerEvent)
                .HasMaxLength(40)
                .HasColumnName("trigger_event");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");

            entity.HasOne(d => d.Tenant).WithMany(p => p.RetentionPolicies)
                .HasForeignKey(d => d.TenantId)
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("fk_retention_policies__tenant_id");
        });

        modelBuilder.Entity<RetentionPurgeAudit>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pk_retention_purge_audits");

            entity.ToTable("retention_purge_audits", tb => tb.HasComment("Is the append-only evidence that a PDPL retention rule ran, naming the rule, the record, the disposition applied and whether the run was a rehearsal. @tier:T @owner:Compliance @retention:indefinite-keep"));

            entity.HasIndex(e => new { e.TenantId, e.Id }, "uq_retention_purge_audits__tenant_id").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.CorrelationId).HasColumnName("correlation_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.Details)
                .HasColumnType("jsonb")
                .HasColumnName("details");
            entity.Property(e => e.Disposition)
                .HasMaxLength(40)
                .HasColumnName("disposition");
            entity.Property(e => e.DryRun)
                .HasDefaultValue(false)
                .HasColumnName("dry_run");
            entity.Property(e => e.Entity)
                .HasMaxLength(64)
                .HasColumnName("entity");
            entity.Property(e => e.EntityId).HasColumnName("entity_id");
            entity.Property(e => e.JobId).HasColumnName("job_id");
            entity.Property(e => e.Outcome)
                .HasMaxLength(40)
                .HasColumnName("outcome");
            entity.Property(e => e.RetentionUntil).HasColumnName("retention_until");
            entity.Property(e => e.RuleKey)
                .HasMaxLength(64)
                .HasColumnName("rule_key");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
        });

        modelBuilder.Entity<Role>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pk_roles");

            entity.ToTable("roles", tb => tb.HasComment("Tenant-defined and system-seeded roles; a per-user permission override is modelled as a custom role rather than its own table (§5 decision 5). @tier:T @owner:Platform"));

            entity.HasIndex(e => new { e.TenantId, e.Code }, "uq_roles__tenant_id_code").IsUnique();

            entity.HasIndex(e => new { e.TenantId, e.Id }, "uq_roles__tenant_id_id").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.Code)
                .HasMaxLength(64)
                .HasColumnName("code");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.IsSystem)
                .HasDefaultValue(false)
                .HasColumnName("is_system");
            entity.Property(e => e.Name).HasColumnName("name");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");

            entity.HasOne(d => d.Tenant).WithMany(p => p.Roles)
                .HasForeignKey(d => d.TenantId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_roles__tenant_id");
        });

        modelBuilder.Entity<RolePermission>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pk_role_permissions");

            entity.ToTable("role_permissions", tb => tb.HasComment("Grants one catalogue permission to one tenant role; the join that turns a role into an authorisation decision. @tier:T @owner:Platform"));

            entity.HasIndex(e => new { e.TenantId, e.RoleId, e.PermissionCode }, "uq_role_permissions__role_id_permission_code").IsUnique();

            entity.HasIndex(e => new { e.TenantId, e.Id }, "uq_role_permissions__tenant_id_id").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.PermissionCode)
                .HasMaxLength(64)
                .HasColumnName("permission_code");
            entity.Property(e => e.RoleId).HasColumnName("role_id");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");

            entity.HasOne(d => d.PermissionCodeNavigation).WithMany(p => p.RolePermissions)
                .HasPrincipalKey(p => p.Code)
                .HasForeignKey(d => d.PermissionCode)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_role_permissions__permission_code");

            entity.HasOne(d => d.Role).WithMany(p => p.RolePermissions)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.RoleId })
                .HasConstraintName("fk_role_permissions__role_id");
        });

        modelBuilder.Entity<Shift>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pk_shifts");

            entity.ToTable("shifts", tb => tb.HasComment("Defines a working shift with its start and end times, unpaid break, weekly off days and the grace, lateness and Ramadan rules the attendance engine applies to it. @tier:T @owner:HR @retention:tenant-lifecycle"));

            entity.HasIndex(e => new { e.TenantId, e.Code }, "uq_shifts__code").IsUnique();

            entity.HasIndex(e => new { e.TenantId, e.Id }, "uq_shifts__tenant_id").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.BreakMinutes)
                .HasDefaultValue(0)
                .HasColumnName("break_minutes");
            entity.Property(e => e.Code)
                .HasMaxLength(32)
                .HasColumnName("code");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.CrossesMidnight)
                .HasDefaultValue(false)
                .HasColumnName("crosses_midnight");
            entity.Property(e => e.EndTime).HasColumnName("end_time");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true)
                .HasColumnName("is_active");
            entity.Property(e => e.Name).HasColumnName("name");
            entity.Property(e => e.Rules)
                .HasDefaultValueSql("'{}'::jsonb")
                .HasColumnType("jsonb")
                .HasColumnName("rules");
            entity.Property(e => e.StartTime).HasColumnName("start_time");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
            entity.Property(e => e.WeeklyOffDays)
                .HasDefaultValueSql("'{}'::text[]")
                .HasColumnName("weekly_off_days");

            entity.HasOne(d => d.Tenant).WithMany(p => p.Shifts)
                .HasForeignKey(d => d.TenantId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_shifts__tenant_id");
        });

        modelBuilder.Entity<ShiftAssignment>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pk_shift_assignments");

            entity.ToTable("shift_assignments", tb => tb.HasComment("Rosters an employee onto a shift for an inclusive effective-dated period, with the database rejecting any overlapping assignment. @tier:T @owner:HR @retention:24m-purge"));

            entity.HasIndex(e => new { e.TenantId, e.ShiftId }, "ix_shift_assignments__shift_id");

            entity.HasIndex(e => new { e.TenantId, e.Id }, "uq_shift_assignments__tenant_id").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.EffectiveFrom).HasColumnName("effective_from");
            entity.Property(e => e.EffectiveTo).HasColumnName("effective_to");
            entity.Property(e => e.EmployeeId).HasColumnName("employee_id");
            entity.Property(e => e.ShiftId).HasColumnName("shift_id");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");

            entity.HasOne(d => d.Employee).WithMany(p => p.ShiftAssignments)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.EmployeeId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_shift_assignments__employee_id");

            entity.HasOne(d => d.Shift).WithMany(p => p.ShiftAssignments)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.ShiftId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_shift_assignments__shift_id");
        });

        modelBuilder.Entity<StatutoryRule>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pk_statutory_rules");

            entity.ToTable("statutory_rules", tb => tb.HasComment("The single effective-dated source of every statutory rate and bound the payroll engine may apply — GOSI by cohort, branch and payer, EOS, overtime, leave pay, Nitaqat weights and WPS parameters — with the circular it came from and who verified it. @tier:R @owner:Compliance @retention:Keep"));

            entity.HasIndex(e => new { e.CountryCode, e.Family, e.RuleKey, e.NationalityClass, e.Cohort, e.GosiBranch, e.Payer, e.EffectiveFrom }, "uq_statutory_rules__key").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.Cohort)
                .HasMaxLength(40)
                .HasDefaultValueSql("'Any'::character varying")
                .HasComment("'Legacy' (Saudis first insured before 3 July 2024), 'Entrant2024' (on or after), or 'Any'. Resolved from employees.gosi_first_registered_on; an unknown cohort BLOCKS the slip and is never defaulted (§2.E). [COUNSEL] confirms the ladder.")
                .HasColumnName("cohort");
            entity.Property(e => e.CountryCode)
                .HasMaxLength(2)
                .IsFixedLength()
                .HasColumnName("country_code");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.EffectiveFrom).HasColumnName("effective_from");
            entity.Property(e => e.EffectiveTo).HasColumnName("effective_to");
            entity.Property(e => e.Family)
                .HasMaxLength(40)
                .HasColumnName("family");
            entity.Property(e => e.GosiBranch)
                .HasMaxLength(40)
                .HasComment("Closed set with no enumerated domain anywhere in revision 6 — left unconstrained pending a §9 entry. See the note above.")
                .HasColumnName("gosi_branch");
            entity.Property(e => e.NationalityClass)
                .HasMaxLength(40)
                .HasDefaultValueSql("'Any'::character varying")
                .HasColumnName("nationality_class");
            entity.Property(e => e.Payer)
                .HasMaxLength(40)
                .HasComment("Closed set with no enumerated domain anywhere in revision 6 — left unconstrained pending a §9 entry.")
                .HasColumnName("payer");
            entity.Property(e => e.Rate)
                .HasPrecision(9, 6)
                .HasComment("numeric(9,6): the Entrant2024 annuities ladder steps by 0.5 percentage points, so two decimals on a percentage has no headroom (CONVENTIONS.md §4). Stored as 0.090000, not 9.")
                .HasColumnName("rate");
            entity.Property(e => e.RuleKey)
                .HasMaxLength(64)
                .HasColumnName("rule_key");
            entity.Property(e => e.RulesVersion)
                .HasMaxLength(40)
                .HasComment("e.g. SA-GOSI-2025.07. Frozen onto every payroll_slip_line so a slip can be recomputed years later.")
                .HasColumnName("rules_version");
            entity.Property(e => e.SourceReference).HasColumnName("source_reference");
            entity.Property(e => e.ValueJson)
                .HasColumnType("jsonb")
                .HasColumnName("value_json");
            entity.Property(e => e.VerifiedAt).HasColumnName("verified_at");
            entity.Property(e => e.VerifiedBy).HasColumnName("verified_by");
            entity.Property(e => e.WageCap)
                .HasPrecision(18, 2)
                .HasColumnName("wage_cap");
            entity.Property(e => e.WageFloor)
                .HasPrecision(18, 2)
                .HasColumnName("wage_floor");
        });

        modelBuilder.Entity<StatutoryRuleBand>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pk_statutory_rule_bands");

            entity.ToTable("statutory_rule_bands", tb => tb.HasComment("The banded form of a statutory rule — service-year, sick-day or contributory-wage ranges each carrying their own rate or amount — with the database guaranteeing the bands of one rule never overlap. @tier:R @owner:Compliance @retention:Keep"));

            entity.HasIndex(e => new { e.StatutoryRuleId, e.BandKey }, "uq_statutory_rule_bands__rule_band_key").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.Amount)
                .HasPrecision(18, 2)
                .HasColumnName("amount");
            entity.Property(e => e.BandKey)
                .HasMaxLength(64)
                .HasColumnName("band_key");
            entity.Property(e => e.BandOrder)
                .HasDefaultValue(0)
                .HasColumnName("band_order");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.LowerBound).HasColumnName("lower_bound");
            entity.Property(e => e.Rate)
                .HasPrecision(9, 6)
                .HasColumnName("rate");
            entity.Property(e => e.StatutoryRuleId).HasColumnName("statutory_rule_id");
            entity.Property(e => e.Unit)
                .HasMaxLength(40)
                .HasComment("ServiceYears | SickDays | ContributoryWage — what lower_bound and upper_bound are measured in.")
                .HasColumnName("unit");
            entity.Property(e => e.UpperBound)
                .HasComment("NULL = open-ended. The exclusion constraint reads it as numrange(lower, COALESCE(upper, 'infinity'), '[)') — inclusive lower, EXCLUSIVE upper, unlike the inclusive-inclusive date convention (§5).")
                .HasColumnName("upper_bound");
            entity.Property(e => e.ValueJson)
                .HasColumnType("jsonb")
                .HasColumnName("value_json");

            entity.HasOne(d => d.StatutoryRule).WithMany(p => p.StatutoryRuleBands)
                .HasForeignKey(d => d.StatutoryRuleId)
                .HasConstraintName("fk_statutory_rule_bands__statutory_rule_id");
        });

        modelBuilder.Entity<Tenant>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pk_tenants");

            entity.ToTable("tenants", tb => tb.HasComment("Customer account root: identity, plan gating, seat and module limits, timezone anchor and the tenancy soft-delete/purge lifecycle. @tier:P @owner:Platform @retention:3-months-after-SoftDelete-then-Purge"));

            entity.HasIndex(e => e.Slug, "uq_tenants__slug").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.EnabledModules)
                .HasDefaultValueSql("'{}'::text[]")
                .HasColumnName("enabled_modules");
            entity.Property(e => e.Name).HasColumnName("name");
            entity.Property(e => e.PlanCode)
                .HasMaxLength(40)
                .HasColumnName("plan_code");
            entity.Property(e => e.PlanExpiresAt).HasColumnName("plan_expires_at");
            entity.Property(e => e.PlanLimits)
                .HasDefaultValueSql("'{}'::jsonb")
                .HasComment("Authoritative seat/module limits (max_employees, max_users, max_companies). Nothing caches a seat count (§11.6).")
                .HasColumnType("jsonb")
                .HasColumnName("plan_limits");
            entity.Property(e => e.PurgedAt).HasColumnName("purged_at");
            entity.Property(e => e.Slug)
                .HasMaxLength(63)
                .HasColumnName("slug");
            entity.Property(e => e.SoftDeletedAt).HasColumnName("soft_deleted_at");
            entity.Property(e => e.Status)
                .HasMaxLength(40)
                .HasDefaultValueSql("'Active'::character varying")
                .HasColumnName("status");
            entity.Property(e => e.TimezoneId)
                .HasMaxLength(64)
                .HasDefaultValueSql("'Asia/Riyadh'::character varying")
                .HasComment("IANA id, validated against the runtime tz database on write. Anchors every business day (§13.4).")
                .HasColumnName("timezone_id");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
        });

        modelBuilder.Entity<TenantSetting>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pk_tenant_settings");

            entity.ToTable("tenant_settings", tb => tb.HasComment("The single settings row per tenant, holding every configuration section as versioned JSON so two admins editing different sections never clobber each other. @tier:T @owner:Platform"));

            entity.HasIndex(e => e.TenantId, "uq_tenant_settings__tenant_id").IsUnique();

            entity.HasIndex(e => new { e.TenantId, e.Id }, "uq_tenant_settings__tenant_id_id").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.SectionVersions)
                .HasDefaultValueSql("'{}'::jsonb")
                .HasColumnType("jsonb")
                .HasColumnName("section_versions");
            entity.Property(e => e.Sections)
                .HasDefaultValueSql("'{}'::jsonb")
                .HasComment("Keys: general, hr, payroll, localization, branding, security, lookups, leave, loans, overtime, notification_templates, document_requirements, help_texts. Written with jsonb_set on one key, guarded by that key's section_versions entry (§A).")
                .HasColumnType("jsonb")
                .HasColumnName("sections");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");

            entity.HasOne(d => d.Tenant).WithOne(p => p.TenantSetting)
                .HasForeignKey<TenantSetting>(d => d.TenantId)
                .HasConstraintName("fk_tenant_settings__tenant_id");
        });

        modelBuilder.Entity<Timesheet>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pk_timesheets");

            entity.ToTable("timesheets", tb => tb.HasComment("Groups one employee logged working minutes for a period into a single submittable, approvable and lockable record. @tier:C @owner:HR @retention:24m-purge"));

            entity.HasIndex(e => new { e.TenantId, e.ApprovalRequestId }, "ix_timesheets__approval_request_id");

            entity.HasIndex(e => new { e.TenantId, e.CompanyId }, "ix_timesheets__company_id");

            entity.HasIndex(e => new { e.TenantId, e.LockedRunId }, "ix_timesheets__locked_run_id");

            entity.HasIndex(e => new { e.TenantId, e.EmployeeId, e.PeriodStart }, "uq_timesheets__employee_period").IsUnique();

            entity.HasIndex(e => new { e.TenantId, e.Id }, "uq_timesheets__tenant_id").IsUnique();

            entity.HasIndex(e => new { e.TenantId, e.TimesheetNumber }, "uq_timesheets__timesheet_number").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.ApprovalRequestId).HasColumnName("approval_request_id");
            entity.Property(e => e.CompanyId).HasColumnName("company_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DecidedAt).HasColumnName("decided_at");
            entity.Property(e => e.EmployeeId).HasColumnName("employee_id");
            entity.Property(e => e.LockedRunId).HasColumnName("locked_run_id");
            entity.Property(e => e.PeriodEnd).HasColumnName("period_end");
            entity.Property(e => e.PeriodStart).HasColumnName("period_start");
            entity.Property(e => e.Status)
                .HasMaxLength(40)
                .HasDefaultValueSql("'Draft'::character varying")
                .HasColumnName("status");
            entity.Property(e => e.SubmittedAt).HasColumnName("submitted_at");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.TimesheetNumber)
                .HasMaxLength(40)
                .HasColumnName("timesheet_number");
            entity.Property(e => e.TotalMinutes)
                .HasDefaultValue(0)
                .HasColumnName("total_minutes");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");

            entity.HasOne(d => d.ApprovalRequest).WithMany(p => p.Timesheets)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.ApprovalRequestId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_timesheets__approval_request_id");

            entity.HasOne(d => d.Company).WithMany(p => p.Timesheets)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.CompanyId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_timesheets__company_id");

            entity.HasOne(d => d.Employee).WithMany(p => p.Timesheets)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.EmployeeId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_timesheets__employee_id");

            entity.HasOne(d => d.PayrollRun).WithMany(p => p.Timesheets)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.LockedRunId })
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("fk_timesheets__locked_run_id");
        });

        modelBuilder.Entity<TimesheetDayReconciliation>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pk_timesheet_day_reconciliations");

            entity.ToTable("timesheet_day_reconciliations", tb => tb.HasComment("Compares the minutes an employee booked on a timesheet day against the minutes attendance computed for the same day and holds the variance until it is explained or accepted. @tier:C @owner:HR @retention:24m-purge"));

            entity.HasIndex(e => new { e.TenantId, e.AttendanceDayId, e.WorkDate }, "ix_timesheet_day_reconciliations__attendance_day_id_work_date");

            entity.HasIndex(e => new { e.TenantId, e.TimesheetId, e.WorkDate }, "uq_timesheet_day_reconciliations__day").IsUnique();

            entity.HasIndex(e => new { e.TenantId, e.Id }, "uq_timesheet_day_reconciliations__tenant_id").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.AttendanceDayId).HasColumnName("attendance_day_id");
            entity.Property(e => e.AttendanceMinutes)
                .HasDefaultValue(0)
                .HasColumnName("attendance_minutes");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.Explanation).HasColumnName("explanation");
            entity.Property(e => e.ResolvedAt).HasColumnName("resolved_at");
            entity.Property(e => e.ResolvedBy).HasColumnName("resolved_by");
            entity.Property(e => e.Status)
                .HasMaxLength(40)
                .HasDefaultValueSql("'Open'::character varying")
                .HasColumnName("status");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.TimesheetId).HasColumnName("timesheet_id");
            entity.Property(e => e.TimesheetMinutes)
                .HasDefaultValue(0)
                .HasColumnName("timesheet_minutes");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
            entity.Property(e => e.VarianceMinutes)
                .HasDefaultValue(0)
                .HasColumnName("variance_minutes");
            entity.Property(e => e.WorkDate).HasColumnName("work_date");

            entity.HasOne(d => d.Timesheet).WithMany(p => p.TimesheetDayReconciliations)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.TimesheetId })
                .HasConstraintName("fk_timesheet_day_reconciliations__timesheet_id");
        });

        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pk_users");

            entity.ToTable("users", tb => tb.HasComment("Every tenant-side login — staff and employee self-service — with its credential, MFA and lockout state, optionally bound one-to-one to an employee record. @tier:T @owner:Platform @retention:soft-delete-only"));

            entity.HasIndex(e => new { e.TenantId, e.EmployeeId }, "uq_users__tenant_id_employee_id").IsUnique();

            entity.HasIndex(e => new { e.TenantId, e.Id }, "uq_users__tenant_id_id").IsUnique();

            entity.HasIndex(e => new { e.TenantId, e.NormalizedEmail }, "uq_users__tenant_id_normalized_email").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.EmployeeId).HasColumnName("employee_id");
            entity.Property(e => e.FailedLoginCount)
                .HasDefaultValue(0)
                .HasColumnName("failed_login_count");
            entity.Property(e => e.LastLoginAt).HasColumnName("last_login_at");
            entity.Property(e => e.LockoutEnd).HasColumnName("lockout_end");
            entity.Property(e => e.MfaEnabled)
                .HasDefaultValue(false)
                .HasColumnName("mfa_enabled");
            entity.Property(e => e.MfaRecoveryHashes)
                .HasColumnType("jsonb")
                .HasColumnName("mfa_recovery_hashes");
            entity.Property(e => e.MfaSecretEncrypted).HasColumnName("mfa_secret_encrypted");
            entity.Property(e => e.NormalizedEmail)
                .HasComment("Unique per tenant, NOT globally: two tenants may legitimately share an address, which is why app.resolve_login() is keyed on (tenant, email) (§19.2 bypass surface 1).")
                .HasColumnName("normalized_email");
            entity.Property(e => e.NotificationPrefs)
                .HasDefaultValueSql("'{}'::jsonb")
                .HasColumnType("jsonb")
                .HasColumnName("notification_prefs");
            entity.Property(e => e.PasswordHash)
                .HasComment("Secret column: REVOKE from kynex_ro by column privilege (§19.2). NULL until an invitation is consumed.")
                .HasColumnName("password_hash");
            entity.Property(e => e.Status)
                .HasMaxLength(40)
                .HasDefaultValueSql("'Invited'::character varying")
                .HasColumnName("status");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");

            entity.HasOne(d => d.Tenant).WithMany(p => p.Users)
                .HasForeignKey(d => d.TenantId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_users__tenant_id");

            entity.HasOne(d => d.Employee).WithOne(p => p.User)
                .HasPrincipalKey<Employee>(p => new { p.TenantId, p.Id })
                .HasForeignKey<User>(d => new { d.TenantId, d.EmployeeId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_users__employee_id");
        });

        modelBuilder.Entity<UserRole>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pk_user_roles");

            entity.ToTable("user_roles", tb => tb.HasComment("Grants a role to a user, optionally narrowed to one company, branch or department and optionally time-boxed; NULL scope columns mean the whole tenant. @tier:T @owner:Platform"));

            entity.HasIndex(e => new { e.TenantId, e.GrantedBy }, "ix_user_roles__granted_by");

            entity.HasIndex(e => new { e.TenantId, e.RoleId }, "ix_user_roles__role_id");

            entity.HasIndex(e => new { e.TenantId, e.ScopeBranchId }, "ix_user_roles__scope_branch_id");

            entity.HasIndex(e => new { e.TenantId, e.ScopeCompanyId }, "ix_user_roles__scope_company_id");

            entity.HasIndex(e => new { e.TenantId, e.ScopeDepartmentId }, "ix_user_roles__scope_department_id");

            entity.HasIndex(e => new { e.TenantId, e.UserId }, "ix_user_roles__user_id");

            entity.HasIndex(e => new { e.TenantId, e.Id }, "uq_user_roles__tenant_id_id").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.ExpiresAt).HasColumnName("expires_at");
            entity.Property(e => e.GrantedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("granted_at");
            entity.Property(e => e.GrantedBy)
                .HasComment("Who made THIS grant. Who MAY grant is a different fact and lives in permission_grantor_records (§2.B).")
                .HasColumnName("granted_by");
            entity.Property(e => e.RoleId).HasColumnName("role_id");
            entity.Property(e => e.ScopeBranchId).HasColumnName("scope_branch_id");
            entity.Property(e => e.ScopeCompanyId).HasColumnName("scope_company_id");
            entity.Property(e => e.ScopeDepartmentId).HasColumnName("scope_department_id");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
            entity.Property(e => e.UserId).HasColumnName("user_id");

            entity.HasOne(d => d.User).WithMany(p => p.UserRoleUsers)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.GrantedBy })
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("fk_user_roles__granted_by");

            entity.HasOne(d => d.Role).WithMany(p => p.UserRoles)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.RoleId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_user_roles__role_id");

            entity.HasOne(d => d.Branch).WithMany(p => p.UserRoles)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.ScopeBranchId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_user_roles__scope_branch_id");

            entity.HasOne(d => d.Company).WithMany(p => p.UserRoles)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.ScopeCompanyId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_user_roles__scope_company_id");

            entity.HasOne(d => d.Department).WithMany(p => p.UserRoles)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.ScopeDepartmentId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_user_roles__scope_department_id");

            entity.HasOne(d => d.UserNavigation).WithMany(p => p.UserRoleUserNavigations)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.UserId })
                .HasConstraintName("fk_user_roles__user_id");
        });

        modelBuilder.Entity<VEmployeeCurrent>(entity =>
        {
            entity
                .HasNoKey()
                .ToView("v_employee_current")
                .HasAnnotation("Npgsql:StorageParameter:security_invoker", "true");

            entity.Property(e => e.AssignmentEffectiveFrom).HasColumnName("assignment_effective_from");
            entity.Property(e => e.AssignmentId).HasColumnName("assignment_id");
            entity.Property(e => e.BankAccountId).HasColumnName("bank_account_id");
            entity.Property(e => e.BankCode)
                .HasMaxLength(16)
                .HasColumnName("bank_code");
            entity.Property(e => e.Basic)
                .HasPrecision(18, 2)
                .HasColumnName("basic");
            entity.Property(e => e.BranchId).HasColumnName("branch_id");
            entity.Property(e => e.CompanyId).HasColumnName("company_id");
            entity.Property(e => e.CostCenterId).HasColumnName("cost_center_id");
            entity.Property(e => e.DepartmentId).HasColumnName("department_id");
            entity.Property(e => e.DesignationId).HasColumnName("designation_id");
            entity.Property(e => e.EmployeeId).HasColumnName("employee_id");
            entity.Property(e => e.EmployeeNumber)
                .HasMaxLength(32)
                .HasColumnName("employee_number");
            entity.Property(e => e.EmploymentStatus)
                .HasMaxLength(40)
                .HasColumnName("employment_status");
            entity.Property(e => e.GradeId).HasColumnName("grade_id");
            entity.Property(e => e.Housing)
                .HasPrecision(18, 2)
                .HasColumnName("housing");
            entity.Property(e => e.HousingInKind).HasColumnName("housing_in_kind");
            entity.Property(e => e.Iban)
                .HasMaxLength(34)
                .HasColumnName("iban");
            entity.Property(e => e.JoiningDate).HasColumnName("joining_date");
            entity.Property(e => e.ManagerEmployeeId).HasColumnName("manager_employee_id");
            entity.Property(e => e.NameAr).HasColumnName("name_ar");
            entity.Property(e => e.NameEn).HasColumnName("name_en");
            entity.Property(e => e.PayGroup)
                .HasMaxLength(40)
                .HasColumnName("pay_group");
            entity.Property(e => e.PaymentMethod)
                .HasMaxLength(40)
                .HasColumnName("payment_method");
            entity.Property(e => e.SalaryEffectiveFrom).HasColumnName("salary_effective_from");
            entity.Property(e => e.SalaryId).HasColumnName("salary_id");
            entity.Property(e => e.SeparationDate).HasColumnName("separation_date");
            entity.Property(e => e.Status)
                .HasMaxLength(40)
                .HasColumnName("status");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.Transport)
                .HasPrecision(18, 2)
                .HasColumnName("transport");
            entity.Property(e => e.WorkEmail).HasColumnName("work_email");
        });

        modelBuilder.Entity<VLeaveBalance>(entity =>
        {
            entity
                .HasNoKey()
                .ToView("v_leave_balances")
                .HasAnnotation("Npgsql:StorageParameter:security_invoker", "true");

            entity.Property(e => e.AccruedDays).HasColumnName("accrued_days");
            entity.Property(e => e.BalanceDays).HasColumnName("balance_days");
            entity.Property(e => e.DebitedDays).HasColumnName("debited_days");
            entity.Property(e => e.EmployeeId).HasColumnName("employee_id");
            entity.Property(e => e.LastMovementOn).HasColumnName("last_movement_on");
            entity.Property(e => e.LeaveTypeId).HasColumnName("leave_type_id");
            entity.Property(e => e.MovementCount).HasColumnName("movement_count");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
        });

        modelBuilder.Entity<WpsBatch>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pk_wps_batches");

            entity.ToTable("wps_batches", tb => tb.HasComment("Holds one Wage Protection System SIF file per payroll run as generated, submitted and acknowledged by the bank, including its resubmission chain. @tier:C @owner:Finance @retention:84m-keep"));

            entity.HasIndex(e => new { e.TenantId, e.CompanyId }, "ix_wps_batches__company_id");

            entity.HasIndex(e => new { e.TenantId, e.FileId }, "ix_wps_batches__file_id");

            entity.HasIndex(e => new { e.TenantId, e.GeneratedBy }, "ix_wps_batches__generated_by");

            entity.HasIndex(e => new { e.TenantId, e.ResubmissionOfId }, "ix_wps_batches__resubmission_of_id");

            entity.HasIndex(e => new { e.TenantId, e.RunId }, "ix_wps_batches__run_id");

            entity.HasIndex(e => new { e.TenantId, e.BatchNumber }, "uq_wps_batches__batch_number").IsUnique();

            entity.HasIndex(e => new { e.TenantId, e.Id }, "uq_wps_batches__tenant_id").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.AcknowledgedAt).HasColumnName("acknowledged_at");
            entity.Property(e => e.BatchNumber)
                .HasMaxLength(40)
                .HasColumnName("batch_number");
            entity.Property(e => e.CompanyId).HasColumnName("company_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.EmployeeCount)
                .HasDefaultValue(0)
                .HasColumnName("employee_count");
            entity.Property(e => e.FileId).HasColumnName("file_id");
            entity.Property(e => e.FileSha256).HasColumnName("file_sha256");
            entity.Property(e => e.FormatVersion)
                .HasMaxLength(40)
                .HasColumnName("format_version");
            entity.Property(e => e.GeneratedBy).HasColumnName("generated_by");
            entity.Property(e => e.RejectedAt).HasColumnName("rejected_at");
            entity.Property(e => e.ResubmissionOfId).HasColumnName("resubmission_of_id");
            entity.Property(e => e.RunId).HasColumnName("run_id");
            entity.Property(e => e.Status)
                .HasMaxLength(40)
                .HasDefaultValueSql("'Generated'::character varying")
                .HasColumnName("status");
            entity.Property(e => e.SubmissionReference)
                .HasMaxLength(64)
                .HasColumnName("submission_reference");
            entity.Property(e => e.SubmittedAt).HasColumnName("submitted_at");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.TotalAmount)
                .HasPrecision(18, 2)
                .HasColumnName("total_amount");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");

            entity.HasOne(d => d.Company).WithMany(p => p.WpsBatches)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.CompanyId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_wps_batches__company_id");

            entity.HasOne(d => d.File).WithMany(p => p.WpsBatches)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.FileId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_wps_batches__file_id");

            entity.HasOne(d => d.User).WithMany(p => p.WpsBatches)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.GeneratedBy })
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("fk_wps_batches__generated_by");

            entity.HasOne(d => d.WpsBatchNavigation).WithMany(p => p.InverseWpsBatchNavigation)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.ResubmissionOfId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_wps_batches__resubmission_of_id");

            entity.HasOne(d => d.PayrollRun).WithMany(p => p.WpsBatches)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.RunId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_wps_batches__run_id");
        });

        modelBuilder.Entity<WpsLine>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pk_wps_lines");

            entity.ToTable("wps_lines", tb => tb.HasComment("Records each employee payment line exactly as filed in a WPS SIF batch, frozen at submission, alongside the bank confirmation result returned for it. @tier:C @owner:Finance @retention:84m-keep"));

            entity.HasIndex(e => new { e.TenantId, e.BatchId, e.BankStatus }, "ix_wps_lines__batch_bank_status");

            entity.HasIndex(e => e.ConfirmationJobId, "ix_wps_lines__confirmation_job_id");

            entity.HasIndex(e => new { e.TenantId, e.EmployeeId }, "ix_wps_lines__employee_id");

            entity.HasIndex(e => new { e.TenantId, e.SlipId }, "ix_wps_lines__slip_id");

            entity.HasIndex(e => new { e.TenantId, e.BatchId, e.SlipId }, "uq_wps_lines__batch_id_slip_id").IsUnique();

            entity.HasIndex(e => new { e.TenantId, e.Id }, "uq_wps_lines__tenant_id").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.BankCode)
                .HasMaxLength(16)
                .HasColumnName("bank_code");
            entity.Property(e => e.BankReference).HasColumnName("bank_reference");
            entity.Property(e => e.BankStatus)
                .HasMaxLength(40)
                .HasDefaultValueSql("'Pending'::character varying")
                .HasColumnName("bank_status");
            entity.Property(e => e.Basic)
                .HasPrecision(18, 2)
                .HasColumnName("basic");
            entity.Property(e => e.BatchId).HasColumnName("batch_id");
            entity.Property(e => e.ConfirmationJobId).HasColumnName("confirmation_job_id");
            entity.Property(e => e.ConfirmedAmount)
                .HasPrecision(18, 2)
                .HasColumnName("confirmed_amount");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.Deductions)
                .HasPrecision(18, 2)
                .HasColumnName("deductions");
            entity.Property(e => e.EmployeeId).HasColumnName("employee_id");
            entity.Property(e => e.EmployeeNumber)
                .HasMaxLength(32)
                .HasColumnName("employee_number");
            entity.Property(e => e.Housing)
                .HasPrecision(18, 2)
                .HasColumnName("housing");
            entity.Property(e => e.Iban)
                .HasMaxLength(34)
                .HasColumnName("iban");
            entity.Property(e => e.IdNumber)
                .HasMaxLength(20)
                .HasColumnName("id_number");
            entity.Property(e => e.MolId)
                .HasMaxLength(20)
                .HasColumnName("mol_id");
            entity.Property(e => e.Net)
                .HasPrecision(18, 2)
                .HasColumnName("net");
            entity.Property(e => e.OtherEarnings)
                .HasPrecision(18, 2)
                .HasColumnName("other_earnings");
            entity.Property(e => e.ReasonCode)
                .HasMaxLength(40)
                .HasColumnName("reason_code");
            entity.Property(e => e.SlipId).HasColumnName("slip_id");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.ValueDate).HasColumnName("value_date");

            entity.HasOne(d => d.ConfirmationJob).WithMany(p => p.WpsLines)
                .HasForeignKey(d => d.ConfirmationJobId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("fk_wps_lines__confirmation_job_id");

            entity.HasOne(d => d.WpsBatch).WithMany(p => p.WpsLines)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.BatchId })
                .HasConstraintName("fk_wps_lines__batch_id");

            entity.HasOne(d => d.Employee).WithMany(p => p.WpsLines)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.EmployeeId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_wps_lines__employee_id");

            entity.HasOne(d => d.PayrollSlip).WithMany(p => p.WpsLines)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.SlipId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_wps_lines__slip_id");
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
