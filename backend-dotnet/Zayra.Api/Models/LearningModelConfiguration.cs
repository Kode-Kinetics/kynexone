using Microsoft.EntityFrameworkCore;

namespace Zayra.Api.Models;

/// <summary>
/// EF configuration for the LRN module (learning_* tables). Kept next to the models so <c>ZayraDbContext</c> stays
/// append-only, as <see cref="TimesheetModelConfiguration"/> and <see cref="ReleaseAModelConfiguration"/> do.
///
/// <para>Every status column has a named CHECK built from its C# vocabulary (<see cref="In"/>), every FK states its
/// ON DELETE, and every index names the query it serves. The two "one live row" guarantees (one live assignment per
/// employee and path; one Draft and one Published version per course code) are partial unique indexes EF can express
/// with <c>HasFilter</c>, so the migration and <c>EnsureCreated</c> (the Postgres test fixture) emit the same DDL —
/// no raw SQL to mirror. All CHECK and filter SQL is portable (no regex, no Postgres-only functions) so the SQLite
/// schemas the unit suite builds from this model stay valid.</para>
/// </summary>
internal static class LearningModelConfiguration
{
    internal static string In(IEnumerable<string> values) => "(" + string.Join(",", values.Select(v => $"'{v}'")) + ")";

    internal static readonly string ActiveAssignmentFilter = "status IN " + In(LearningAssignmentStatuses.Active);

    internal static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LearningCourse>(entity =>
        {
            entity.ToTable("learning_courses", t =>
            {
                t.HasCheckConstraint("ck_learning_courses__status", "status IN " + In(LearningCourseStatuses.All));
                t.HasCheckConstraint("ck_learning_courses__content_kind", "content_kind IN " + In(LearningContentKinds.All));
                t.HasCheckConstraint("ck_learning_courses__completion_rule", "completion_rule IN " + In(LearningCompletionRules.All));
                t.HasCheckConstraint("ck_learning_courses__move_on", "cmi5_move_on IS NULL OR cmi5_move_on IN " + In(Cmi5MoveOnValues.All));
                t.HasCheckConstraint("ck_learning_courses__version_number", "version_number >= 1");
                t.HasCheckConstraint("ck_learning_courses__pass_mark", "pass_mark_percent IS NULL OR (pass_mark_percent >= 0 AND pass_mark_percent <= 100)");
                t.HasCheckConstraint("ck_learning_courses__max_attempts", "max_attempts IS NULL OR (max_attempts >= 1 AND max_attempts <= 20)");
                t.HasCheckConstraint("ck_learning_courses__validity_months", "validity_months IS NULL OR (validity_months >= 1 AND validity_months <= 120)");
                // §3.1 item 3: completion evidence comes from one source, and Acknowledgement only on Lesson/Link with
                // no assessment. A Draft may be incomplete while it is being written; anything assignable may not.
                t.HasCheckConstraint("ck_learning_courses__completion_rule_fits_content",
                    "status = 'Draft'"
                    + " OR (completion_rule = 'Assessment' AND content_kind IN ('Lesson','Link') AND assessment_json IS NOT NULL"
                    + "     AND pass_mark_percent IS NOT NULL AND max_attempts IS NOT NULL)"
                    + " OR (completion_rule = 'Cmi5MoveOn' AND content_kind = 'Cmi5' AND cmi5_move_on IS NOT NULL AND assessment_json IS NULL)"
                    + " OR (completion_rule = 'Acknowledgement' AND content_kind IN ('Lesson','Link') AND assessment_json IS NULL)");
                t.HasCheckConstraint("ck_learning_courses__content_present",
                    "status = 'Draft'"
                    + " OR (content_kind = 'Lesson' AND lesson_html IS NOT NULL)"
                    + " OR (content_kind = 'Link' AND link_url IS NOT NULL)"
                    + " OR (content_kind = 'Cmi5' AND cmi5_au_url IS NOT NULL AND cmi5_activity_id IS NOT NULL AND cmi5_au_origin IS NOT NULL)");
                t.HasCheckConstraint("ck_learning_courses__published_stamped", "status = 'Draft' OR published_at_utc IS NOT NULL");
                t.HasCheckConstraint("ck_learning_courses__hr_verification_cmi5_only", "NOT requires_hr_verification OR content_kind = 'Cmi5'");
                t.HasCheckConstraint("ck_learning_courses__assessment_pass_mark_positive",
                    "status = 'Draft' OR completion_rule <> 'Assessment' OR pass_mark_percent > 0");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Code).HasMaxLength(40).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(16).IsRequired();
            entity.Property(x => x.Title).HasMaxLength(200).IsRequired();
            entity.Property(x => x.TitleAr).HasMaxLength(200);
            entity.Property(x => x.Description).HasMaxLength(2000);
            entity.Property(x => x.DescriptionAr).HasMaxLength(2000);
            entity.Property(x => x.ContentKind).HasMaxLength(16).IsRequired();
            entity.Property(x => x.LinkUrl).HasMaxLength(2000);
            entity.Property(x => x.Cmi5AuUrl).HasMaxLength(2000);
            entity.Property(x => x.Cmi5ActivityId).HasMaxLength(500);
            entity.Property(x => x.Cmi5MoveOn).HasMaxLength(24);
            entity.Property(x => x.Cmi5AuOrigin).HasMaxLength(300);
            entity.Property(x => x.CompletionRule).HasMaxLength(24).IsRequired();
            entity.Property(x => x.AssessmentJson).HasColumnType("jsonb");
            entity.Property(x => x.PassMarkPercent).HasPrecision(5, 2);
            entity.Property(x => x.Version).IsConcurrencyToken();
            // Identity + version: the catalogue list and "the next version number for this code".
            entity.HasIndex(x => new { x.TenantId, x.Code, x.VersionNumber }).IsUnique();
            // One Draft per code (editing a published version creates exactly one next draft; a double click cannot
            // create two) and one Published per code (assignment resolves "the" current version without ambiguity).
            // Two indexes over the same columns: they must be NAMED indexes, or EF folds them into one.
            entity.HasIndex(x => new { x.TenantId, x.Code }, "ux_learning_courses_one_draft_per_code").IsUnique()
                .HasFilter("status = 'Draft'");
            entity.HasIndex(x => new { x.TenantId, x.Code }, "ux_learning_courses_one_published_per_code").IsUnique()
                .HasFilter("status = 'Published'");
        });

        modelBuilder.Entity<LearningPath>(entity =>
        {
            entity.ToTable("learning_paths", t =>
            {
                t.HasCheckConstraint("ck_learning_paths__status", "status IN " + In(LearningPathStatuses.All));
                t.HasCheckConstraint("ck_learning_paths__effective_window", "effective_to IS NULL OR effective_from IS NULL OR effective_to >= effective_from");
                t.HasCheckConstraint("ck_learning_paths__renewal_window", "renewal_window_days >= 0 AND renewal_window_days <= 365");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Code).HasMaxLength(40).IsRequired();
            entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
            entity.Property(x => x.NameAr).HasMaxLength(200);
            entity.Property(x => x.Description).HasMaxLength(2000);
            entity.Property(x => x.Status).HasMaxLength(16).IsRequired();
            entity.Property(x => x.EligibilityJson).HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.Version).IsConcurrencyToken();
            // Path code lookup and uniqueness.
            entity.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
            // The catalogue list filtered by status.
            entity.HasIndex(x => new { x.TenantId, x.Status });
        });

        modelBuilder.Entity<LearningPathItem>(entity =>
        {
            entity.ToTable("learning_path_items", t =>
            {
                t.HasCheckConstraint("ck_learning_path_items__sequence", "sequence >= 1");
                t.HasCheckConstraint("ck_learning_path_items__not_own_prerequisite", "prerequisite_item_id IS NULL OR prerequisite_item_id <> id");
            });
            entity.HasKey(x => x.Id);
            // Items are part of the path aggregate: they go with it.
            entity.HasOne<LearningPath>().WithMany().HasForeignKey(x => x.PathId).OnDelete(DeleteBehavior.Cascade);
            // A course version that a path names is never deleted underneath it.
            entity.HasOne<LearningCourse>().WithMany().HasForeignKey(x => x.CourseId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<LearningPathItem>().WithMany().HasForeignKey(x => x.PrerequisiteItemId).OnDelete(DeleteBehavior.Restrict);
            // Ordered item list of a path; one course appears once.
            entity.HasIndex(x => new { x.PathId, x.Sequence }).IsUnique();
            entity.HasIndex(x => new { x.PathId, x.CourseId }).IsUnique();
            // "Which paths use this course?" (retire warning, catalogue drill-down).
            entity.HasIndex(x => new { x.TenantId, x.CourseId });
        });

        modelBuilder.Entity<LearningAssignmentRule>(entity =>
        {
            entity.ToTable("learning_assignment_rules", t =>
            {
                t.HasCheckConstraint("ck_learning_assignment_rules__target_kind", "target_kind IN " + In(LearningRuleTargets.All));
                t.HasCheckConstraint("ck_learning_assignment_rules__due_in_days", "due_in_days >= 0 AND due_in_days <= 3650");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
            entity.Property(x => x.TargetKind).HasMaxLength(16).IsRequired();
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasOne<LearningPath>().WithMany().HasForeignKey(x => x.PathId).OnDelete(DeleteBehavior.Restrict);
            // One live rule per (path, target): saving the same rule twice cannot double-assign on the next sweep.
            entity.HasIndex(x => new { x.TenantId, x.PathId, x.TargetKind, x.TargetId }).IsUnique()
                .HasFilter("is_active").HasDatabaseName("ux_learning_assignment_rules_one_active_per_target");
            // The sweep: every active rule of a tenant.
            entity.HasIndex(x => new { x.TenantId, x.IsActive });
        });

        modelBuilder.Entity<LearningAssignment>(entity =>
        {
            entity.ToTable("learning_assignments", t =>
            {
                t.HasCheckConstraint("ck_learning_assignments__status", "status IN " + In(LearningAssignmentStatuses.All));
                t.HasCheckConstraint("ck_learning_assignments__source", "source IN " + In(LearningAssignmentSources.All));
                t.HasCheckConstraint("ck_learning_assignments__cycle", "cycle >= 1");
                t.HasCheckConstraint("ck_learning_assignments__completed_stamped", "status <> 'Completed' OR completed_at_utc IS NOT NULL");
                t.HasCheckConstraint("ck_learning_assignments__cancel_reason", "status <> 'Cancelled' OR (cancelled_at_utc IS NOT NULL AND cancel_reason IS NOT NULL)");
                t.HasCheckConstraint("ck_learning_assignments__rule_source", "rule_id IS NULL OR source = 'Rule'");
                t.HasCheckConstraint("ck_learning_assignments__renewal_source", "(renewal_of_credential_id IS NULL) = (source <> 'Renewal')");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Status).HasMaxLength(16).IsRequired();
            entity.Property(x => x.Source).HasMaxLength(16).IsRequired();
            entity.Property(x => x.CancelReason).HasMaxLength(500);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasOne<Employee>().WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<LearningPath>().WithMany().HasForeignKey(x => x.PathId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<LearningAssignmentRule>().WithMany().HasForeignKey(x => x.RuleId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<LearningCredential>().WithMany().HasForeignKey(x => x.RenewalOfCredentialId).OnDelete(DeleteBehavior.Restrict);
            // LRN-03: one live assignment of a path to an employee. A concurrent double submit loses on this index (409).
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId, x.PathId }).IsUnique()
                .HasFilter(ActiveAssignmentFilter).HasDatabaseName("ux_learning_assignments_one_active_per_employee_path");
            // "My learning" and the employee's history.
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId, x.Status });
            // Reminder sweep: live assignments by due date.
            entity.HasIndex(x => new { x.TenantId, x.Status, x.DueDate });
            // Reports per path.
            entity.HasIndex(x => new { x.TenantId, x.PathId, x.Status });
        });

        modelBuilder.Entity<LearningEnrollment>(entity =>
        {
            entity.ToTable("learning_enrollments", t =>
            {
                t.HasCheckConstraint("ck_learning_enrollments__status", "status IN " + In(LearningEnrollmentStatuses.All));
                t.HasCheckConstraint("ck_learning_enrollments__completion_source",
                    "completion_source IS NULL OR completion_source IN " + In(LearningCompletionSources.All));
                // Completion evidence is never implied: a Completed row names its source and evidence, and only it does.
                t.HasCheckConstraint("ck_learning_enrollments__completion_evidence",
                    "(status IN ('Completed','PendingVerification')) = (completion_source IS NOT NULL AND completion_evidence_ref IS NOT NULL AND completed_at_utc IS NOT NULL)");
                t.HasCheckConstraint("ck_learning_enrollments__verification_after_evidence", "verified_at_utc IS NULL OR status = 'Completed'");
                t.HasCheckConstraint("ck_learning_enrollments__progress", "progress_percent IS NULL OR (progress_percent >= 0 AND progress_percent <= 100)");
                t.HasCheckConstraint("ck_learning_enrollments__attempts", "attempts_used >= 0 AND attempt_allowance >= 0 AND attempts_used <= attempt_allowance");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Status).HasMaxLength(24).IsRequired();
            entity.Property(x => x.CompletionSource).HasMaxLength(24);
            entity.Property(x => x.CompletionEvidenceRef).HasMaxLength(64);
            entity.Property(x => x.ProgressPercent).HasPrecision(5, 2);
            entity.Property(x => x.ScorePercent).HasPrecision(5, 2);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasOne<LearningAssignment>().WithMany().HasForeignKey(x => x.AssignmentId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<LearningPathItem>().WithMany().HasForeignKey(x => x.PathItemId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<LearningCourse>().WithMany().HasForeignKey(x => x.CourseId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Employee>().WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
            // One enrollment per item of an assignment.
            entity.HasIndex(x => new { x.AssignmentId, x.PathItemId }).IsUnique();
            // The employee's transcript and the reports' status matrix.
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId, x.Status });
        });

        modelBuilder.Entity<LearningAttempt>(entity =>
        {
            entity.ToTable("learning_attempts", t =>
            {
                t.HasCheckConstraint("ck_learning_attempts__status", "status IN " + In(LearningAttemptStatuses.All));
                t.HasCheckConstraint("ck_learning_attempts__attempt_no", "attempt_no >= 1");
                t.HasCheckConstraint("ck_learning_attempts__score", "score_percent IS NULL OR (score_percent >= 0 AND score_percent <= 100)");
                t.HasCheckConstraint("ck_learning_attempts__scored_when_submitted",
                    "(status = 'InProgress') = (submitted_at_utc IS NULL) AND (status = 'InProgress' OR score_percent IS NOT NULL)");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Status).HasMaxLength(16).IsRequired();
            entity.Property(x => x.QuestionOrderJson).HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.AnswersJson).HasColumnType("jsonb");
            entity.Property(x => x.ScorePercent).HasPrecision(5, 2);
            entity.Property(x => x.PassMarkPercent).HasPrecision(5, 2);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasOne<LearningEnrollment>().WithMany().HasForeignKey(x => x.EnrollmentId).OnDelete(DeleteBehavior.Restrict);
            // Attempt numbering: a race to start attempt N twice loses here.
            entity.HasIndex(x => new { x.EnrollmentId, x.AttemptNo }).IsUnique();
            // At most one open attempt per enrollment.
            entity.HasIndex(x => x.EnrollmentId).IsUnique()
                .HasFilter("status = 'InProgress'").HasDatabaseName("ux_learning_attempts_one_open_per_enrollment");
        });

        modelBuilder.Entity<LearningCredential>(entity =>
        {
            entity.ToTable("learning_credentials", t =>
            {
                t.HasCheckConstraint("ck_learning_credentials__status", "status IN " + In(LearningCredentialStatuses.All));
                t.HasCheckConstraint("ck_learning_credentials__completion_source", "completion_source IN " + In(LearningCompletionSources.All));
                t.HasCheckConstraint("ck_learning_credentials__revoked_stamped",
                    "status <> 'Revoked' OR (revoked_at_utc IS NOT NULL AND revoke_reason IS NOT NULL)");
                t.HasCheckConstraint("ck_learning_credentials__superseded_stamped", "status <> 'Superseded' OR superseded_at_utc IS NOT NULL");
                t.HasCheckConstraint("ck_learning_credentials__expiry_after_completion", "expires_on IS NULL OR expires_on > completed_on");
                t.HasCheckConstraint("ck_learning_credentials__not_own_predecessor", "supersedes_credential_id IS NULL OR supersedes_credential_id <> id");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.CourseCode).HasMaxLength(40).IsRequired();
            entity.Property(x => x.CertificateNumber).HasMaxLength(32).IsRequired();
            entity.Property(x => x.VerificationCode).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(16).IsRequired();
            entity.Property(x => x.CompletionSource).HasMaxLength(24).IsRequired();
            entity.Property(x => x.ScorePercent).HasPrecision(5, 2);
            entity.Property(x => x.RevokeReason).HasMaxLength(500);
            entity.Property(x => x.SupersedeReason).HasMaxLength(500);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasOne<Employee>().WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<LearningEnrollment>().WithMany().HasForeignKey(x => x.EnrollmentId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<LearningCourse>().WithMany().HasForeignKey(x => x.CourseId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<LearningPath>().WithMany().HasForeignKey(x => x.PathId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<LearningCredential>().WithMany().HasForeignKey(x => x.SupersedesCredentialId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<LearningAssignment>().WithMany().HasForeignKey(x => x.RenewalAssignmentId).OnDelete(DeleteBehavior.SetNull);
            // Certificate lookup / verification.
            entity.HasIndex(x => new { x.TenantId, x.CertificateNumber }).IsUnique();
            // A credential is superseded at most once: the history is a chain, never a fork.
            entity.HasIndex(x => x.SupersedesCredentialId).IsUnique()
                .HasFilter("supersedes_credential_id IS NOT NULL").HasDatabaseName("ux_learning_credentials_superseded_once");
            // Expiry/renewal sweep and the "expiring in N days" report.
            entity.HasIndex(x => new { x.TenantId, x.Status, x.ExpiresOn });
            // The employee's certificates.
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId });
            // "Which credential did this enrollment earn?"
            entity.HasIndex(x => x.EnrollmentId);
        });

        modelBuilder.Entity<LearningLaunchSession>(entity =>
        {
            entity.ToTable("learning_launch_sessions", t =>
            {
                t.HasCheckConstraint("ck_learning_launch_sessions__status", "status IN " + In(LearningSessionStatuses.All));
                t.HasCheckConstraint("ck_learning_launch_sessions__move_on", "move_on IN " + In(Cmi5MoveOnValues.All));
                t.HasCheckConstraint("ck_learning_launch_sessions__fetch_then_auth", "(auth_token_hash IS NULL) = (fetch_used_at_utc IS NULL)");
                t.HasCheckConstraint("ck_learning_launch_sessions__auth_expiry", "(auth_token_hash IS NULL) = (auth_expires_at_utc IS NULL)");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ActivityId).HasMaxLength(500).IsRequired();
            entity.Property(x => x.ActorAccountName).HasMaxLength(64).IsRequired();
            entity.Property(x => x.ActorHomePage).HasMaxLength(300).IsRequired();
            entity.Property(x => x.AuOrigin).HasMaxLength(300).IsRequired();
            entity.Property(x => x.MoveOn).HasMaxLength(24).IsRequired();
            entity.Property(x => x.ReturnUrl).HasMaxLength(2000);
            entity.Property(x => x.Status).HasMaxLength(16).IsRequired();
            entity.Property(x => x.FetchTokenHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.AuthTokenHash).HasMaxLength(64);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasOne<LearningEnrollment>().WithMany().HasForeignKey(x => x.EnrollmentId).OnDelete(DeleteBehavior.Restrict);
            // Fetch: WHERE fetch_token_hash = $1 (one row per token, platform-wide).
            entity.HasIndex(x => x.FetchTokenHash).IsUnique();
            // An enrollment's sessions (relaunch abandons the open one; registration reuse).
            entity.HasIndex(x => new { x.TenantId, x.EnrollmentId, x.LaunchedAtUtc });
            // CORS preflight: is this origin an AU with a live session?
            entity.HasIndex(x => new { x.AuOrigin, x.Status });
            // Abandonment sweep: open sessions whose auth window has closed.
            entity.HasIndex(x => new { x.Status, x.AuthExpiresAtUtc });
        });

        modelBuilder.Entity<LearningXapiStatement>(entity =>
        {
            entity.ToTable("learning_xapi_statements", t =>
            {
                t.HasCheckConstraint("ck_learning_xapi_statements__progress", "progress_percent IS NULL OR (progress_percent >= 0 AND progress_percent <= 100)");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Verb).HasMaxLength(300).IsRequired();
            entity.Property(x => x.ScoreScaled).HasPrecision(6, 4);
            entity.Property(x => x.ProgressPercent).HasPrecision(5, 2);
            entity.Property(x => x.StatementJson).HasColumnType("jsonb").IsRequired();
            entity.HasOne<LearningLaunchSession>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<LearningEnrollment>().WithMany().HasForeignKey(x => x.EnrollmentId).OnDelete(DeleteBehavior.Restrict);
            // LRN-13 idempotency: a statement id is stored once; a duplicate callback is a no-op.
            entity.HasIndex(x => new { x.TenantId, x.StatementId }).IsUnique();
            // moveOn evaluation: every statement of a registration.
            entity.HasIndex(x => new { x.TenantId, x.Registration });
        });
    }
}
