using Microsoft.EntityFrameworkCore;

namespace Zayra.Api.Models;

/// <summary>
/// EF configuration for the onboarding columns on <c>employees</c> (nationality_code, nationality_mapping_state,
/// original_joining_date). Kept beside the models so <c>ZayraDbContext</c> stays append-only, like
/// <see cref="ReleaseAModelConfiguration"/>: the integrator wires ONE line,
/// <c>OnboardingModelConfiguration.Configure(modelBuilder);</c>, next to the ReleaseA call and generates the single
/// expand-only migration. No migration or snapshot is produced by the work package.
/// </summary>
internal static class OnboardingModelConfiguration
{
    internal static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Employee>(entity =>
        {
            entity.Property(x => x.NationalityCode).HasMaxLength(2);
            entity.Property(x => x.NationalityMappingState).HasMaxLength(12).IsRequired().HasDefaultValue(NationalityMappingStates.Unset);
            entity.ToTable(t => t.HasCheckConstraint("ck_employees__nationality_mapping_state",
                "nationality_mapping_state IN " + ReleaseAModelConfiguration.In(NationalityMappingStates.All)));
            entity.HasIndex(x => x.TenantId).HasDatabaseName("ix_employees_nationality_review")
                .HasFilter("nationality_mapping_state IN ('ProposedAuto','Unresolved') AND NOT is_deleted");
        });
    }
}
