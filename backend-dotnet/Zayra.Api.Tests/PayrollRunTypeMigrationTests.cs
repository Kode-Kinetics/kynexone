using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Testcontainers.PostgreSql;
using Zayra.Api.Data;

namespace Zayra.Api.Tests;

/// <summary>
/// POD-B2 (M10) — the FIRST test in this repo that actually EXECUTES migrations.
///
/// Every other integration test builds the schema with EnsureCreatedAsync, so the migration files
/// themselves have never been run by CI — a real gap, given a payroll_runs row that fails to backfill
/// would take out all ~55 live tenants on deploy. This spins a fresh Postgres, migrates to the LAST
/// pre-B2 migration, writes a pre-B2-shaped payroll run with raw SQL, then applies B2 and asserts:
///
///   a) every pre-existing row backfilled to run_type = 'Regular' / includes_recurring_pay = true;
///   b) BOTH partial unique indexes carry their full predicate (asserted from pg_indexes.indexdef —
///      a behavioural test alone would pass for the wrong reason if the WHERE clause went missing);
///   c) two Regular runs are rejected while N off-cycle runs coexist, including for the null-company
///      rows the seeders create, which the company-scoped index cannot constrain.
/// </summary>
public sealed class PayrollRunTypeMigrationTests : IAsyncLifetime
{
    /// <summary>The migration immediately BEFORE POD-B2 — the "production as it stands today" point.</summary>
    private const string PreB2Migration = "20260804044517_AddGlEntryCompanyDimension";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();

    private string _cs = string.Empty;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        _cs = _container.GetConnectionString();
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    private ZayraDbContext CreateDb() => new(
        new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql(_cs).Options);

    private async Task<string?> IndexDefAsync(ZayraDbContext db, string indexName)
    {
        await using var cmd = db.Database.GetDbConnection().CreateCommand();
        await db.Database.OpenConnectionAsync();
        cmd.CommandText = $"SELECT indexdef FROM pg_indexes WHERE indexname = '{indexName}'";
        return (await cmd.ExecuteScalarAsync()) as string;
    }

    [Fact]
    public async Task Migration_BackfillsExistingRuns_AndReKeysUniquenessToRegularRunsOnly()
    {
        var tenantId = Guid.NewGuid();
        var companyId = Guid.NewGuid();

        // ── 1. Migrate to the pre-B2 state and seed a run in the OLD shape ────────────────────────
        await using (var db = CreateDb())
        {
            await db.Database.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync(PreB2Migration);

            // Deliberately written with raw SQL, listing ONLY the pre-B2 columns: this row must survive
            // the migration exactly as a live tenant's would, with no help from the CLR defaults.
            await db.Database.ExecuteSqlRawAsync($@"
                INSERT INTO payroll_runs
                    (id, tenant_id, company_id, year, month, status,
                     total_gross_salary, total_deductions, total_net_salary, total_employer_statutory_cost,
                     employee_count, created_at_utc, erp_posting_status)
                VALUES
                    ('{Guid.NewGuid()}', '{tenantId}', '{companyId}', 2026, 5, 'Locked',
                     13000, 1170, 11830, 1410, 1, now(), 'ReadyForErp'),
                    -- the UNSCOPED shape both seeders create (company_id IS NULL)
                    ('{Guid.NewGuid()}', '{tenantId}', NULL, 2026, 4, 'Locked',
                     13000, 1170, 11830, 1410, 1, now(), 'ReadyForErp');");
        }

        // ── 2. Apply POD-B2 ──────────────────────────────────────────────────────────────────────
        await using (var db = CreateDb())
            await db.Database.MigrateAsync();

        await using (var db = CreateDb())
        {
            // ── a) Backfill ──────────────────────────────────────────────────────────────────────
            var runs = await db.PayrollRuns.IgnoreQueryFilters().AsNoTracking()
                .Where(r => r.TenantId == tenantId).ToListAsync();
            runs.Should().HaveCount(2);
            runs.Should().OnlyContain(r => r.RunType == "Regular",
                "every pre-B2 run is the tenant's monthly run and must stay exactly what it was");
            runs.Should().OnlyContain(r => r.IncludesRecurringPay,
                "pre-B2 runs paid full recurring salary");
            runs.Should().OnlyContain(r => r.ParentRunId == null && r.GlPostingPeriod == null);

            // ── b) Index predicates ──────────────────────────────────────────────────────────────
            var companyScoped = await IndexDefAsync(db, "IX_payroll_runs_tenant_id_company_id_year_month");
            companyScoped.Should().NotBeNull();
            companyScoped!.Should().Contain("UNIQUE");
            companyScoped.Should().Contain("run_type", "uniqueness must apply to Regular runs ONLY");
            companyScoped.Should().Contain("Voided", "a voided run must never block re-running the period");

            var nullCompany = await IndexDefAsync(db, "IX_payroll_runs_tenant_id_year_month");
            nullCompany.Should().NotBeNull("Postgres treats NULL as distinct, so the company-scoped index " +
                                           "constrains nothing for the null-company runs the seeders create");
            nullCompany!.Should().Contain("UNIQUE");
            nullCompany.Should().Contain("company_id IS NULL");
            nullCompany.Should().Contain("run_type");

            // The unfiltered lookup index must exist, or every non-Regular period query seq-scans.
            var lookup = await IndexDefAsync(db, "IX_payroll_runs_period_lookup");
            lookup.Should().NotBeNull();
            lookup!.Should().NotContain("WHERE", "the lookup index must serve NON-Regular runs too");

            // The selector table exists.
            (await IndexDefAsync(db, "IX_payroll_run_employee_selections_tenant_id_company_id"))
                .Should().NotBeNull();
        }

        // ── c) Behaviour under the new indexes ───────────────────────────────────────────────────
        await using (var db = CreateDb())
        {
            // A second REGULAR run for the already-populated (tenant, company, 2026-05) is rejected.
            db.PayrollRuns.Add(new Zayra.Api.Models.PayrollRun
            { TenantId = tenantId, CompanyId = companyId, Year = 2026, Month = 5, Status = "Draft" });
            var act = async () => await db.SaveChangesAsync();
            await act.Should().ThrowAsync<DbUpdateException>();
            db.ChangeTracker.Clear();

            // A second UNSCOPED Regular run for 2026-04 is rejected too — the hole the company-scoped
            // index cannot see, and the reason the null-company companion index exists.
            db.PayrollRuns.Add(new Zayra.Api.Models.PayrollRun
            { TenantId = tenantId, CompanyId = null, Year = 2026, Month = 4, Status = "Draft" });
            var act2 = async () => await db.SaveChangesAsync();
            await act2.Should().ThrowAsync<DbUpdateException>();
            db.ChangeTracker.Clear();

            // Any number of non-Regular runs coexist with the Regular one, in either scope.
            for (var i = 0; i < 5; i++)
            {
                db.PayrollRuns.Add(new Zayra.Api.Models.PayrollRun
                {
                    TenantId = tenantId, CompanyId = companyId, Year = 2026, Month = 5,
                    Status = "Draft", RunType = "OffCycle", IncludesRecurringPay = false,
                });
                db.PayrollRuns.Add(new Zayra.Api.Models.PayrollRun
                {
                    TenantId = tenantId, CompanyId = null, Year = 2026, Month = 4,
                    Status = "Draft", RunType = "Supplementary", IncludesRecurringPay = false,
                });
            }
            await db.SaveChangesAsync();

            (await db.PayrollRuns.IgnoreQueryFilters().CountAsync(r => r.TenantId == tenantId)).Should().Be(12);

            // And voiding the Regular run frees the period — the POD-B3 seam.
            var locked = await db.PayrollRuns.IgnoreQueryFilters()
                .FirstAsync(r => r.TenantId == tenantId && r.Month == 5 && r.RunType == "Regular");
            locked.Status = "Voided";
            await db.SaveChangesAsync();
            db.PayrollRuns.Add(new Zayra.Api.Models.PayrollRun
            { TenantId = tenantId, CompanyId = companyId, Year = 2026, Month = 5, Status = "Draft" });
            await db.SaveChangesAsync();
        }
    }
}
