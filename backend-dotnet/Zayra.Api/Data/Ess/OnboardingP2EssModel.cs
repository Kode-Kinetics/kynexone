using Microsoft.EntityFrameworkCore;
using Zayra.Api.Models;

namespace Zayra.Api.Data.Ess;

/// <summary>
/// P2 (ESS completion) model additions, kept out of <c>ZayraDbContext</c> so the hot file carries one line.
/// Expand-only: every new column is nullable or defaulted, so no existing row changes meaning.
/// Column names are snake_case by the context's convention (<c>ApplySnakeCaseColumns</c>).
/// </summary>
public static class OnboardingP2EssModel
{
    public const string SectionPendingIndex = "ix_epcr_section_pending";
    public const string StepUpJtiIndex = "ux_epcr_step_up_jti";
    public const string InboxIndex = "ix_epcr_inbox";
    public const string SectionCheck = "ck_epcr__section";
    public const string ScanStatusCheck = "ck_employee_documents__scan_status";

    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EmployeeProfileChangeRequest>(entity =>
        {
            entity.ToTable("employee_profile_change_requests", t =>
                t.HasCheckConstraint(SectionCheck,
                    "section IS NULL OR section IN ('person','emergency','arabicName','id','bank','documents')"));
            entity.Property(x => x.Section).HasMaxLength(24);
            entity.Property(x => x.NoticeVersion).HasMaxLength(16);
            entity.Property(x => x.ReviewNote).HasMaxLength(500);
            // One open request per (employee, section): closes the read-then-insert race. A lost race is a 409.
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId, x.Section })
                  .HasDatabaseName(SectionPendingIndex)
                  .IsUnique()
                  .HasFilter("status = 'PendingHR' AND section IS NOT NULL");
            // A step-up token opens one submission: the ledger is the column itself.
            entity.HasIndex(x => x.StepUpJti).HasDatabaseName(StepUpJtiIndex).IsUnique().HasFilter("step_up_jti IS NOT NULL");
            // Serves the HR inbox query.
            entity.HasIndex(x => new { x.TenantId, x.Status, x.CreatedAtUtc })
                  .HasDatabaseName(InboxIndex)
                  .IsDescending(false, false, true)
                  .HasFilter("status = 'PendingHR'");
        });

        modelBuilder.Entity<EmployeeActionItem>(entity =>
            entity.Property(x => x.RequestedSections).HasMaxLength(200));

        modelBuilder.Entity<EmployeeDocument>(entity =>
        {
            entity.ToTable("employee_documents", t =>
                t.HasCheckConstraint(ScanStatusCheck, "scan_status IN ('Clean','PendingScan','Quarantined')"));
            entity.Property(x => x.ScanStatus).HasMaxLength(12).HasDefaultValue("Clean").IsRequired();
            entity.Property(x => x.SourceSection).HasMaxLength(24);
        });
    }
}
