using Microsoft.EntityFrameworkCore;

namespace Zayra.Api.Models;

/// <summary>
/// EF configuration for the SUC module (succession_*, talent_*, development_plan* tables). Kept next to the models so
/// <c>ZayraDbContext</c> stays append-only, as <see cref="LearningModelConfiguration"/> does.
///
/// <para>Every status column has a named CHECK built from its C# vocabulary, every FK states its ON DELETE, and every
/// index names the query it serves. The "one live row" guarantees are partial unique indexes (<c>HasFilter</c>), so the
/// migration and <c>EnsureCreated</c> (the Postgres test fixture) emit the same DDL. The 9-box cell is a stored generated
/// column, written in portable SQL (CASE, COALESCE, ||) so the SQLite schemas the unit suite builds stay valid. The two
/// immutability triggers are PostgreSQL-only and live in the AddSuccession migration (PostgresOnlyDdl), mirrored into
/// the Postgres test fixture.</para>
/// </summary>
internal static class SuccessionModelConfiguration
{
    private static string In(IEnumerable<string> values) => LearningModelConfiguration.In(values);

    /// <summary>"P{potential}-{band}" from the calibrated potential when present, else the proposed one. NULL when Unrated.</summary>
    internal const string NineBoxCellSql =
        "CASE WHEN performance_band IN ('Low','Moderate','High') THEN "
        + "(CASE COALESCE(calibrated_potential, proposed_potential) WHEN 3 THEN 'P3' WHEN 2 THEN 'P2' ELSE 'P1' END) || '-' || performance_band "
        + "ELSE NULL END";

    internal static readonly string LiveNominationFilter = "status NOT IN " + In(NominationStatuses.Closed);

    internal static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SuccessionCriticalPosition>(entity =>
        {
            entity.ToTable("succession_critical_positions", t =>
            {
                t.HasCheckConstraint("ck_succession_critical_positions__status", "status IN " + In(CriticalPositionStatuses.All));
                t.HasCheckConstraint("ck_succession_critical_positions__risk", "risk_of_loss IN " + In(SuccessionRiskLevels.All));
                t.HasCheckConstraint("ck_succession_critical_positions__impact", "impact_of_vacancy IN " + In(SuccessionImpactLevels.All));
                t.HasCheckConstraint("ck_succession_critical_positions__rationale", "length(trim(rationale)) > 0");
                t.HasCheckConstraint("ck_succession_critical_positions__retired_stamped",
                    "status <> 'Retired' OR (retired_at_utc IS NOT NULL AND retired_reason IS NOT NULL)");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.CompanyId).IsRequired();
            entity.Property(x => x.RiskOfLoss).HasMaxLength(16).IsRequired();
            entity.Property(x => x.ImpactOfVacancy).HasMaxLength(16).IsRequired();
            entity.Property(x => x.Rationale).HasMaxLength(2000).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(16).IsRequired();
            entity.Property(x => x.RetiredReason).HasMaxLength(500);
            entity.Property(x => x.Version).IsConcurrencyToken();
            // A position that is designated is never deleted underneath its succession history.
            entity.HasOne<Position>().WithMany().HasForeignKey(x => x.PositionId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Employee>().WithMany().HasForeignKey(x => x.AccountableEmployeeId).OnDelete(DeleteBehavior.Restrict);
            // SUC-01: one active designation per position. A retired one stays as history; designating again adds a row.
            entity.HasIndex(x => new { x.TenantId, x.PositionId }).IsUnique()
                .HasFilter("status = 'Active'").HasDatabaseName("ux_succession_critical_positions_one_active");
            // The workspace list: a company's active (or retired) critical positions.
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.Status });
            // "Positions I own" for the accountable executive.
            entity.HasIndex(x => x.AccountableEmployeeId);
        });

        modelBuilder.Entity<TalentPool>(entity =>
        {
            entity.ToTable("talent_pools", t =>
            {
                t.HasCheckConstraint("ck_talent_pools__status", "status IN " + In(TalentPoolStatuses.All));
                t.HasCheckConstraint("ck_talent_pools__code", "length(trim(code)) > 0");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.CompanyId).IsRequired();
            entity.Property(x => x.Code).HasMaxLength(40).IsRequired();
            entity.Property(x => x.NameEn).HasMaxLength(200).IsRequired();
            entity.Property(x => x.NameAr).HasMaxLength(200);
            entity.Property(x => x.Purpose).HasMaxLength(2000).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(16).IsRequired();
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasOne<Employee>().WithMany().HasForeignKey(x => x.OwnerEmployeeId).OnDelete(DeleteBehavior.Restrict);
            // Pool code lookup and uniqueness per company.
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.Code }).IsUnique();
        });

        modelBuilder.Entity<SuccessionNomination>(entity =>
        {
            entity.ToTable("succession_nominations", t =>
            {
                t.HasCheckConstraint("ck_succession_nominations__one_target", "(critical_position_id IS NULL) <> (talent_pool_id IS NULL)");
                t.HasCheckConstraint("ck_succession_nominations__status", "status IN " + In(NominationStatuses.All));
                t.HasCheckConstraint("ck_succession_nominations__readiness", "readiness IS NULL OR readiness IN " + In(ReadinessLevels.All));
                t.HasCheckConstraint("ck_succession_nominations__readiness_previous",
                    "readiness_previous IS NULL OR readiness_previous IN " + In(ReadinessLevels.All));
                // A candidate calibrated for, or on the slate of, a position always has a readiness.
                t.HasCheckConstraint("ck_succession_nominations__readiness_required",
                    "critical_position_id IS NULL OR status NOT IN ('Calibrated','OnSlate') OR readiness IS NOT NULL");
                t.HasCheckConstraint("ck_succession_nominations__on_slate_position_only", "status <> 'OnSlate' OR critical_position_id IS NOT NULL");
                t.HasCheckConstraint("ck_succession_nominations__exit_reason", "exit_reason IS NULL OR exit_reason IN " + In(NominationExitReasons.All));
                // Declined or Withdrawn (by a person or the system) always says why, and when.
                t.HasCheckConstraint("ck_succession_nominations__closed_has_reason",
                    "status NOT IN ('Declined','Withdrawn') OR (exit_reason IS NOT NULL AND exit_note IS NOT NULL AND length(trim(exit_note)) > 0 AND exited_at_utc IS NOT NULL)");
                t.HasCheckConstraint("ck_succession_nominations__readiness_change_reason",
                    "readiness_changed_at_utc IS NULL OR (readiness_reason IS NOT NULL AND length(trim(readiness_reason)) > 0)");
                t.HasCheckConstraint("ck_succession_nominations__evidence", "length(trim(evidence)) > 0");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.CompanyId).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(16).IsRequired();
            entity.Property(x => x.Readiness).HasMaxLength(16);
            entity.Property(x => x.ReadinessPrevious).HasMaxLength(16);
            entity.Property(x => x.Evidence).HasMaxLength(4000).IsRequired();
            entity.Property(x => x.CompetencyEvidenceJson).HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.ReadinessReason).HasMaxLength(1000);
            entity.Property(x => x.ReviewReason).HasMaxLength(500);
            entity.Property(x => x.ExitReason).HasMaxLength(24);
            entity.Property(x => x.ExitNote).HasMaxLength(1000);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasOne<Employee>().WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<SuccessionCriticalPosition>().WithMany().HasForeignKey(x => x.CriticalPositionId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<TalentPool>().WithMany().HasForeignKey(x => x.TalentPoolId).OnDelete(DeleteBehavior.Restrict);
            // SUC-02: a person is nominated once per target while the nomination is live; a declined or withdrawn one
            // may be followed by a fresh nomination. A concurrent double submit loses here (409).
            entity.HasIndex(x => new { x.TenantId, x.CriticalPositionId, x.EmployeeId }).IsUnique()
                .HasFilter("critical_position_id IS NOT NULL AND " + LiveNominationFilter)
                .HasDatabaseName("ux_succession_nominations_one_live_per_position");
            entity.HasIndex(x => new { x.TenantId, x.TalentPoolId, x.EmployeeId }).IsUnique()
                .HasFilter("talent_pool_id IS NOT NULL AND " + LiveNominationFilter)
                .HasDatabaseName("ux_succession_nominations_one_live_per_pool");
            // A position's candidates by status (position detail, bench, slate builder).
            entity.HasIndex(x => new { x.CriticalPositionId, x.Status });
            // A pool's members by status.
            entity.HasIndex(x => new { x.TalentPoolId, x.Status });
            // A person's nominations (sweep, ESS visibility, conflict checks).
            entity.HasIndex(x => x.EmployeeId);
        });

        modelBuilder.Entity<TalentAssessment>(entity =>
        {
            entity.ToTable("talent_assessments", t =>
            {
                t.HasCheckConstraint("ck_talent_assessments__status", "status IN " + In(TalentAssessmentStatuses.All));
                t.HasCheckConstraint("ck_talent_assessments__band", "performance_band IN " + In(PerformanceBandValues.All));
                t.HasCheckConstraint("ck_talent_assessments__proposed_potential", "proposed_potential BETWEEN 1 AND 3");
                t.HasCheckConstraint("ck_talent_assessments__calibrated_potential", "calibrated_potential IS NULL OR calibrated_potential BETWEEN 1 AND 3");
                t.HasCheckConstraint("ck_talent_assessments__evidence", "length(trim(potential_evidence)) > 0");
                // SUC-03: moving a potential in calibration needs a written reason of at least 20 characters.
                t.HasCheckConstraint("ck_talent_assessments__calibration_reason",
                    "calibrated_potential IS NULL OR calibrated_potential = proposed_potential OR (calibration_reason IS NOT NULL AND length(trim(calibration_reason)) >= 20)");
                t.HasCheckConstraint("ck_talent_assessments__calibrated_stamped",
                    "status <> 'Calibrated' OR (calibrated_potential IS NOT NULL AND calibrated_at_utc IS NOT NULL)");
                // Unrated has no score; a band has its score. No placement is ever guessed.
                t.HasCheckConstraint("ck_talent_assessments__unrated_has_no_score", "(performance_band = 'Unrated') = (performance_score IS NULL)");
                t.HasCheckConstraint("ck_talent_assessments__score", "performance_score IS NULL OR (performance_score >= 0 AND performance_score <= 100)");
                t.HasCheckConstraint("ck_talent_assessments__superseded_stamped", "status <> 'Superseded' OR superseded_at_utc IS NOT NULL");
                t.HasCheckConstraint("ck_talent_assessments__not_own_predecessor", "supersedes_id IS NULL OR supersedes_id <> id");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.CompanyId).IsRequired();
            entity.Property(x => x.ReviewPeriod).HasMaxLength(16).IsRequired();
            entity.Property(x => x.PotentialEvidence).HasMaxLength(4000).IsRequired();
            entity.Property(x => x.PerformanceScore).HasPrecision(5, 2);
            entity.Property(x => x.PerformanceBand).HasMaxLength(16).IsRequired();
            entity.Property(x => x.BandRuleVersion).HasMaxLength(80).IsRequired();
            entity.Property(x => x.CalibrationReason).HasMaxLength(2000);
            entity.Property(x => x.Status).HasMaxLength(16).IsRequired();
            entity.Property(x => x.NineBoxCell).HasMaxLength(16).HasComputedColumnSql(NineBoxCellSql, stored: true);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasOne<Employee>().WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<AppraisalReview>().WithMany().HasForeignKey(x => x.AppraisalReviewId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<TalentAssessment>().WithMany().HasForeignKey(x => x.SupersedesId).OnDelete(DeleteBehavior.Restrict);
            // SUC-03: one current assessment per person per period; a re-assessment supersedes, never forks.
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId, x.ReviewPeriod }).IsUnique()
                .HasFilter("status <> 'Superseded'").HasDatabaseName("ux_talent_assessments_one_current_per_period");
            // The calibration session and the 9-box: a period's assessments by status.
            entity.HasIndex(x => new { x.TenantId, x.ReviewPeriod, x.Status });
        });

        modelBuilder.Entity<SuccessionSlateVersion>(entity =>
        {
            entity.ToTable("succession_slate_versions", t =>
            {
                t.HasCheckConstraint("ck_succession_slate_versions__status", "status IN " + In(SlateStatuses.All));
                t.HasCheckConstraint("ck_succession_slate_versions__version_no", "version_no >= 1");
                t.HasCheckConstraint("ck_succession_slate_versions__submitted_stamped",
                    "status = 'Draft' OR (submitted_at_utc IS NOT NULL AND approval_request_id IS NOT NULL) OR status = 'Cancelled'");
                t.HasCheckConstraint("ck_succession_slate_versions__approved_stamped",
                    "status NOT IN ('Approved','Superseded') OR (approved_at_utc IS NOT NULL AND effective_from IS NOT NULL)");
                t.HasCheckConstraint("ck_succession_slate_versions__superseded_stamped", "status <> 'Superseded' OR superseded_at_utc IS NOT NULL");
                t.HasCheckConstraint("ck_succession_slate_versions__returned_reason",
                    "status <> 'ReturnedForRevision' OR (returned_at_utc IS NOT NULL AND return_reason IS NOT NULL AND length(trim(return_reason)) > 0)");
                t.HasCheckConstraint("ck_succession_slate_versions__cancel_reason",
                    "status <> 'Cancelled' OR (cancelled_at_utc IS NOT NULL AND cancel_reason IS NOT NULL)");
                t.HasCheckConstraint("ck_succession_slate_versions__not_own_predecessor", "revised_from_id IS NULL OR revised_from_id <> id");
                t.HasCheckConstraint("ck_succession_slate_versions__anonymised_hash", "(anonymised_sha256 IS NULL) = (anonymised_at_utc IS NULL)");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.CompanyId).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(24).IsRequired();
            entity.Property(x => x.SnapshotJson).HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.SnapshotSha256).HasMaxLength(64).IsRequired();
            entity.Property(x => x.AnonymisedSha256).HasMaxLength(64);
            entity.Property(x => x.ReturnReason).HasMaxLength(1000);
            entity.Property(x => x.CancelReason).HasMaxLength(500);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasOne<SuccessionCriticalPosition>().WithMany().HasForeignKey(x => x.CriticalPositionId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ApprovalRequest>().WithMany().HasForeignKey(x => x.ApprovalRequestId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<SuccessionSlateVersion>().WithMany().HasForeignKey(x => x.RevisedFromId).OnDelete(DeleteBehavior.Restrict);
            // Version numbering per position; a race to create version N twice loses here.
            entity.HasIndex(x => new { x.CriticalPositionId, x.VersionNo }).IsUnique();
            // One open (Draft/Submitted) version per position: revise and a double-click cannot fork the slate.
            entity.HasIndex(x => x.CriticalPositionId).IsUnique()
                .HasFilter("status IN ('Draft','Submitted')").HasDatabaseName("ux_succession_slate_versions_one_open");
            // One Approved version per position is a DEFERRABLE exclusion constraint (ex_succession_slate_versions__one_approved,
            // AddSuccession.PostgresOnlyDdl), not a partial unique index: approving supersedes the previous version in the
            // same statement batch, and an immediate index would refuse whichever row EF happened to update first.
            // Slate lookup from the approval engine (SuccessionSlateApprovalSync).
            entity.HasIndex(x => x.ApprovalRequestId);
        });

        modelBuilder.Entity<DevelopmentPlan>(entity =>
        {
            entity.ToTable("development_plans", t =>
            {
                t.HasCheckConstraint("ck_development_plans__status", "status IN " + In(DevelopmentPlanStatuses.All));
                t.HasCheckConstraint("ck_development_plans__purpose", "length(trim(purpose)) > 0");
                t.HasCheckConstraint("ck_development_plans__completed_stamped", "status <> 'Completed' OR completed_at_utc IS NOT NULL");
                t.HasCheckConstraint("ck_development_plans__cancel_reason",
                    "status <> 'Cancelled' OR (cancelled_at_utc IS NOT NULL AND cancel_reason IS NOT NULL)");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.CompanyId).IsRequired();
            entity.Property(x => x.Purpose).HasMaxLength(2000).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(16).IsRequired();
            entity.Property(x => x.LastReviewNote).HasMaxLength(2000);
            entity.Property(x => x.CancelReason).HasMaxLength(500);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasOne<Employee>().WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Employee>().WithMany().HasForeignKey(x => x.OwnerEmployeeId).OnDelete(DeleteBehavior.Restrict);
            // The nomination link is HR context only; if the nomination row ever went, the plan stays.
            entity.HasOne<SuccessionNomination>().WithMany().HasForeignKey(x => x.NominationId).OnDelete(DeleteBehavior.SetNull);
            // SUC-04: one current plan per employee (Active or OnHold).
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId }).IsUnique()
                .HasFilter("status IN ('Active','OnHold')").HasDatabaseName("ux_development_plans_one_current_per_employee");
            // Review-due sweep and the progress report.
            entity.HasIndex(x => new { x.TenantId, x.Status, x.NextReviewOn });
            // A nomination's plans (HR drill-down).
            entity.HasIndex(x => x.NominationId);
        });

        modelBuilder.Entity<DevelopmentPlanAction>(entity =>
        {
            entity.ToTable("development_plan_actions", t =>
            {
                t.HasCheckConstraint("ck_development_plan_actions__type", "action_type IN " + In(DevelopmentActionTypes.All));
                t.HasCheckConstraint("ck_development_plan_actions__status", "status IN " + In(DevelopmentActionStatuses.All));
                t.HasCheckConstraint("ck_development_plan_actions__method", "completion_method IS NULL OR completion_method IN " + In(DevelopmentCompletionMethods.All));
                // The link column matches the action type: a Learning action names its assignment, a Goal action its goal,
                // a Mentoring action its mentor, and no other type carries those links.
                t.HasCheckConstraint("ck_development_plan_actions__link_matches_type",
                    "((action_type = 'Learning') = (learning_assignment_id IS NOT NULL))"
                    + " AND ((action_type = 'Goal') = (employee_goal_id IS NOT NULL))"
                    + " AND (action_type <> 'Mentoring' OR mentor_employee_id IS NOT NULL)");
                t.HasCheckConstraint("ck_development_plan_actions__completed_stamped",
                    "(status = 'Completed') = (completed_at_utc IS NOT NULL AND completion_method IS NOT NULL)");
                t.HasCheckConstraint("ck_development_plan_actions__linked_method_fits",
                    "(completion_method IS NULL OR completion_method <> 'LinkedLearningCompleted' OR action_type = 'Learning')"
                    + " AND (completion_method IS NULL OR completion_method <> 'LinkedGoalCompleted' OR action_type = 'Goal')");
                t.HasCheckConstraint("ck_development_plan_actions__title", "length(trim(title)) > 0");
                // Only a manual (unlinked) action can be proposed as done by the employee.
                t.HasCheckConstraint("ck_development_plan_actions__proposal_manual_only",
                    "completion_proposed_at_utc IS NULL OR action_type NOT IN ('Learning','Goal')");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.CompanyId).IsRequired();
            entity.Property(x => x.ActionType).HasMaxLength(16).IsRequired();
            entity.Property(x => x.Title).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Description).HasMaxLength(2000);
            entity.Property(x => x.Status).HasMaxLength(16).IsRequired();
            entity.Property(x => x.ProgressNote).HasMaxLength(2000);
            entity.Property(x => x.CompletionMethod).HasMaxLength(32);
            entity.Property(x => x.Version).IsConcurrencyToken();
            // Actions are part of the plan aggregate; a plan is never deleted (cancel is a status), so Restrict.
            entity.HasOne<DevelopmentPlan>().WithMany().HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<LearningAssignment>().WithMany().HasForeignKey(x => x.LearningAssignmentId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<EmployeeGoal>().WithMany().HasForeignKey(x => x.EmployeeGoalId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Employee>().WithMany().HasForeignKey(x => x.MentorEmployeeId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Employee>().WithMany().HasForeignKey(x => x.OwnerEmployeeId).OnDelete(DeleteBehavior.Restrict);
            // A plan's actions in order.
            entity.HasIndex(x => new { x.PlanId, x.SortOrder });
            // Linked-completion sync: "which action does this learning assignment / goal drive?"
            entity.HasIndex(x => x.LearningAssignmentId);
            entity.HasIndex(x => x.EmployeeGoalId);
            // Overdue sweep and report: open actions by due date.
            entity.HasIndex(x => new { x.TenantId, x.Status, x.DueOn });
        });
    }
}
