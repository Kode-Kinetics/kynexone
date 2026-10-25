using Microsoft.EntityFrameworkCore;

namespace Zayra.Api.Models;

/// <summary>
/// EF configuration for cost-centre accounting migration M1 (<c>CostAccountingFoundation</c>): column shapes, defaults, the
/// CHECKs and the indexes EF can express. Kept beside the models so <c>ZayraDbContext</c> stays append-only, as
/// <see cref="ReleaseAModelConfiguration"/> does.
///
/// <para><b>Deliberately NOT modelled.</b> Every foreign key to <c>cost_centers</c> — the composite
/// <c>(tenant_id, cost_center_id)</c> keys, the company-scoped <c>(tenant_id, company_id, cost_center_id)</c> key on
/// <c>finance_gl_entries</c>, <c>companies.default_cost_center_id</c> and <c>cost_centers.parent_id</c> — and the
/// two unique keys they point at. Most are created <c>NOT VALID</c> (existing rows may hold dangling ids; they are
/// validated by a later migration), which EF cannot emit, and EF would add an index beside each one where the contract
/// lists them as deliberately unindexed (v1-spec 1.6). The migration creates them in raw SQL and the migration test asserts
/// them on a real database.</para>
///
/// <para><b>Value-set literals.</b> The SQL strings below are frozen into the migration; never edit the migration — write
/// a new one.</para>
/// </summary>
internal static class CostAccountingModelConfiguration
{
    internal const string FunctionalClasses = "('COGS', 'SELLING', 'G_AND_A', 'R_AND_D', 'OPERATIONS', 'CAPITALISABLE')";
    internal const string CostCenterSources = "('Employee', 'Position', 'Department', 'ParentDepartment', 'CompanyDefault', 'MappingFixed', 'RolledUp')";
    internal const string CodePattern = "'^[A-Za-z0-9][A-Za-z0-9._-]{0,29}$'";

    internal static void Configure(ModelBuilder modelBuilder, bool isNpgsql = true)
    {
        // ── cost_centers ──────────────────────────────────────────────────────────────────────────────
        modelBuilder.Entity<CostCenter>(entity =>
        {
            entity.Property(x => x.FunctionalClass).HasMaxLength(24);
            entity.Property(x => x.GlSegment).HasMaxLength(30);
            // Serves: the ERP cost-centre value is unique per company among live rows (journal export column).
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.GlSegment })
                .IsUnique().AreNullsDistinct(false)
                .HasDatabaseName("ux_cost_centers_scope_gl_segment")
                .HasFilter("gl_segment IS NOT NULL AND NOT is_deleted");
            entity.ToTable(t =>
            {
                t.HasCheckConstraint("ck_cost_centers__functional_class", "functional_class IS NULL OR functional_class IN " + FunctionalClasses);
                t.HasCheckConstraint("ck_cost_centers__parent_not_self", "parent_id IS NULL OR parent_id <> id");
                // Regex CHECKs are PostgreSQL-only (the SQLite unit-test databases have no ~ operator). code_format is
                // created NOT VALID by the migration: existing codes are audited and repaired first (v1-spec 1.1).
                if (isNpgsql)
                {
                    t.HasCheckConstraint("ck_cost_centers__gl_segment_format", "gl_segment IS NULL OR gl_segment ~ " + CodePattern);
                    t.HasCheckConstraint("ck_cost_centers__code_format", "code ~ " + CodePattern);
                }
            });
        });

        // ── gl_accounts / gl_account_mappings ─────────────────────────────────────────────────────────
        modelBuilder.Entity<GlAccount>(entity =>
        {
            entity.Property(x => x.CostCenterRule).HasMaxLength(12).HasDefaultValue("Optional");
            entity.ToTable(t => t.HasCheckConstraint("ck_gl_accounts__cost_center_rule", "cost_center_rule IN ('Required', 'Optional', 'NotAllowed')"));
        });
        modelBuilder.Entity<GlAccountMapping>(entity =>
        {
            entity.Property(x => x.FunctionalClass).HasMaxLength(24);
            // Serves: posting-rule lookup by driver and cost-centre class. Replaces ux_gl_account_mappings_scope_driver
            // (same columns without the class; identical while every class is NULL, so uniqueness is never absent).
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.DriverKey, x.FunctionalClass })
                .IsUnique().AreNullsDistinct(false)
                .HasDatabaseName("ux_gl_account_mappings_scope_driver_class");
            entity.ToTable(t => t.HasCheckConstraint("ck_gl_account_mappings__functional_class", "functional_class IS NULL OR functional_class IN " + FunctionalClasses));
        });

        // ── companies / employees ─────────────────────────────────────────────────────────────────────
        modelBuilder.Entity<Company>(entity =>
        {
            entity.Property(x => x.CostAccountingEnabled).HasDefaultValue(false);
            entity.Property(x => x.CostCentreSmallCellMin).HasDefaultValue((short)0);
            entity.ToTable(t => t.HasCheckConstraint("ck_companies__cost_centre_small_cell_min", "cost_centre_small_cell_min >= 0"));
        });
        modelBuilder.Entity<Employee>().Property(x => x.CostCenterIsOverride).HasDefaultValue(false);

        // ── payroll ───────────────────────────────────────────────────────────────────────────────────
        modelBuilder.Entity<PayrollEarning>(entity =>
        {
            entity.Property(x => x.CostCenterSource).HasMaxLength(16);
            entity.ToTable(t => t.HasCheckConstraint("ck_payroll_earnings__cost_center_source", "cost_center_source IS NULL OR cost_center_source IN " + CostCenterSources));
        });
        modelBuilder.Entity<PayrollDeduction>(entity =>
        {
            entity.Property(x => x.CostCenterSource).HasMaxLength(16);
            entity.ToTable(t => t.HasCheckConstraint("ck_payroll_deductions__cost_center_source", "cost_center_source IS NULL OR cost_center_source IN " + CostCenterSources));
        });
        modelBuilder.Entity<PayrollRun>(entity =>
        {
            entity.Property(x => x.CostCenterResolutionHash).HasMaxLength(64);
            entity.Property(x => x.CostAccountingApplied).HasDefaultValue(false);
        });

        // ── finance_gl_entries ────────────────────────────────────────────────────────────────────────
        modelBuilder.Entity<FinanceGlEntry>(entity =>
        {
            entity.Property(x => x.CostCenterCode).HasMaxLength(30);
            entity.Property(x => x.CostCenterName).HasMaxLength(200);
            entity.Property(x => x.CostCenterGlSegment).HasMaxLength(30);
            entity.Property(x => x.PostingKey).HasMaxLength(64);
            // Serves: the cost-centre trial balance (one cost centre over a period) and the in-use guard's lookup.
            entity.HasIndex(x => new { x.TenantId, x.CostCenterId, x.Period })
                .HasDatabaseName("ix_finance_gl_entries_cc_period")
                .HasFilter("cost_center_id IS NOT NULL");
            // Serves: the idempotency probe at Lock / void; NULL keys (loans, advances, history) never conflict.
            entity.HasIndex(x => new { x.TenantId, x.PostingKey })
                .IsUnique()
                .HasDatabaseName("ux_finance_gl_entries_posting_key")
                .HasFilter("posting_key IS NOT NULL");
            entity.ToTable(t =>
            {
                t.HasCheckConstraint("ck_finance_gl_entries__cost_center_pair", "(cost_center_id IS NULL) = (cost_center_code IS NULL)");
                t.HasCheckConstraint("ck_finance_gl_entries__cost_center_needs_company", "cost_center_id IS NULL OR company_id IS NOT NULL");
                // NOT VALID in the migration: they bind new and updated rows, never the legacy ledger at upgrade time.
                // PostgreSQL-only so the SQLite unit-test databases (which never had them) are unchanged.
                if (isNpgsql)
                {
                    t.HasCheckConstraint("ck_finance_gl_entries__amount_nonnegative", "amount >= 0");
                    t.HasCheckConstraint("ck_finance_gl_entries__has_account", "(debit_account <> '' OR credit_account <> '')");
                    t.HasCheckConstraint("ck_finance_gl_entries__currency_format", "currency ~ '^[A-Z]{3}$'");
                }
            });
        });

        // ── exports (frozen artifacts) ────────────────────────────────────────────────────────────────
        modelBuilder.Entity<GlJournalExportLine>(entity =>
        {
            entity.Property(x => x.CostCenterCode).HasMaxLength(30);
            entity.Property(x => x.CostCenterName).HasMaxLength(200);
            entity.Property(x => x.CostCenterGlSegment).HasMaxLength(30);
        });
        // GlJournalExport.AmountScale (smallint NULL) needs no further configuration.
    }
}
