using Microsoft.EntityFrameworkCore;
using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Models;

/// <summary>EF configuration of <c>benefit_usage_requests</c>, kept next to its model like <see cref="ReleaseAModelConfiguration"/>.</summary>
internal static class BenefitUsageModelConfiguration
{
    internal static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BenefitUsageRequest>(entity =>
        {
            entity.ToTable("benefit_usage_requests", t =>
            {
                t.HasCheckConstraint("ck_benefit_usage_requests__status", "status IN " + ReleaseAModelConfiguration.In(BenefitUsageStatuses.All));
                // A request is a count OR an amount, never both, always positive; money carries its currency.
                t.HasCheckConstraint("ck_benefit_usage_requests__measure",
                    "(units IS NULL) <> (amount IS NULL) AND (units IS NULL OR units > 0) AND (amount IS NULL OR amount > 0)");
                t.HasCheckConstraint("ck_benefit_usage_requests__currency_iff_amount", "(amount IS NULL) = (currency IS NULL)");
                t.HasCheckConstraint("ck_benefit_usage_requests__period", "period_end >= period_start");
                // Decided ⇔ who and when are recorded; a rejection or withdrawal says why.
                t.HasCheckConstraint("ck_benefit_usage_requests__decided",
                    "(status = 'Pending') = (decided_by_user_id IS NULL) AND (decided_by_user_id IS NULL) = (decided_at_utc IS NULL)");
                t.HasCheckConstraint("ck_benefit_usage_requests__reason",
                    "status NOT IN ('Rejected','Cancelled') OR (decision_reason IS NOT NULL AND length(trim(decision_reason)) > 0)");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.CompanyId).IsRequired();
            entity.Property(x => x.BenefitCode).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Amount).HasPrecision(18, 2);
            entity.Property(x => x.Currency).HasMaxLength(3);
            entity.Property(x => x.AcademicYear).HasMaxLength(20);
            entity.Property(x => x.ProviderName).HasMaxLength(200);
            entity.Property(x => x.EvidenceReference).HasMaxLength(200);
            entity.Property(x => x.Note).HasMaxLength(1000);
            entity.Property(x => x.Status).HasMaxLength(12).IsRequired();
            entity.Property(x => x.DecisionReason).HasMaxLength(1000);

            // Utilisation: an employee's requests for one benefit in one period.
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId, x.BenefitCode, x.PeriodStart })
                .HasDatabaseName("ix_benefit_usage_requests__employee_benefit_period");
            // The Finance queue and the register: a company's requests by status, newest first.
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.Status, x.CreatedAtUtc })
                .HasDatabaseName("ix_benefit_usage_requests__company_status");

            entity.HasOne<Company>().WithMany().HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Employee>().WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<EmployeeEntitlement>().WithMany().HasForeignKey(x => new { x.TenantId, x.EntitlementId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<EmployeeDependent>().WithMany().HasForeignKey(x => x.DependantId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
