using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Fast (SQLite in-memory) unit coverage for the pay-component engine, catalog, seeder and guard —
/// the pieces that make the golden-master's byte-identical claim credible: emission ORDER (which the
/// persistence layer does not itself guarantee), the statutory carve-out, and idempotent seeding.
/// </summary>
public class PayComponentEngineTests
{
    private static (ZayraDbContext db, SqliteConnection conn) NewDb()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (db, conn);
    }

    // ── Engine emits the current fixed sequence, in order, for the seeded catalog ────────────────
    [Fact]
    public void Compute_SeededCatalog_ReproducesCurrentEmissionOrder()
    {
        var comps = PayComponentCatalog.SystemComponentSeeds(Guid.NewGuid());
        var ctx = new PayComponentContext
        {
            Basic = 10_000m, Housing = 3_000m, Transport = 1_000m, OtherAllowances = 900m,
            FixedDeduction = 250m, Gross = 14_900m,
            OvertimePay = 750m, OtHours = 10m, HourlyRate = 41.6667m, OtMultiplier = 1.5m,
            TaxDeduction = 500m, IncomeTaxRate = 5m,
            AttendanceDeduction = 75m, LopDeduction = 300m, LopDays = 1m, LopDayRate = 300m,
            LeaveDeduction = 150m, LoanEmi = 1_000m, AdvEmi = 400m,
            BonusLines = new[] { new PayComponentLine("BONUS_ANNUAL", "Annual", 2_000m, "Bonus", false) },
            AdjustmentEarningLines = new[] { new PayComponentLine("ADJ_TOPUP", "Payroll adjustment - Topup", 800m, "Adjustment", false) },
            AdjustmentDeductionLines = new[] { new PayComponentLine("ADJ_CLAWBACK", "Payroll adjustment - Clawback", 300m, "Adjustment", false) },
            StatutoryLines = new[]
            {
                new StatutoryDeductionLine("GOSI-ANN-EE", "GOSI Annuities (Employee)", 1_170m, 0m),
                new StatutoryDeductionLine("GOSI-ANN-ER", "GOSI Annuities (Employer)", 0m, 1_170m),
                new StatutoryDeductionLine("GOSI-OH-ER", "Occupational Hazard (Employer)", 0m, 260m),
            },
        };

        var result = PayComponentEngine.Compute(comps, ctx);

        result.Earnings.Select(l => l.Code).Should().Equal(
            "BONUS_ANNUAL", "ADJ_TOPUP", "BASIC", "HOUSING", "TRANSPORT", "OTHER_ALLOWANCES", "OVERTIME");
        result.Deductions.Select(l => l.Code).Should().Equal(
            "FIXED_DEDUCTION", "INCOME_TAX", "ATTENDANCE", "LOP_DEDUCTION", "LEAVE", "LOAN_EMI", "ADVANCE_EMI",
            "ADJ_CLAWBACK", "GOSI-ANN-EE", "GOSI-ANN-ER", "GOSI-OH-ER");

        // Employer statutory lines carry the employer flag; nothing else does.
        result.Deductions.Where(l => l.IsEmployerContribution).Select(l => l.Code)
            .Should().Equal("GOSI-ANN-ER", "GOSI-OH-ER");
        // Dynamic labels are byte-identical to the current engine.
        result.Earnings.Single(l => l.Code == "OVERTIME").Name.Should().Be("Overtime (10.00 h × 41.67/h × 1.50)");
        result.Deductions.Single(l => l.Code == "INCOME_TAX").Name.Should().Be("Income tax (5%)");
        result.Deductions.Single(l => l.Code == "LOP_DEDUCTION").Name.Should().Be("Loss of Pay (1.00 d × 300.00/d)");
    }

    // ── BASIC emits even at 0; every other structure line suppresses at 0 ────────────────────────
    [Fact]
    public void Compute_ZeroValues_OnlyBasicEmits()
    {
        var comps = PayComponentCatalog.SystemComponentSeeds(Guid.NewGuid());
        var ctx = new PayComponentContext { Basic = 0m, Housing = 0m, Transport = 0m, OtherAllowances = 0m };
        var result = PayComponentEngine.Compute(comps, ctx);

        result.Earnings.Should().ContainSingle();
        result.Earnings[0].Code.Should().Be("BASIC");
        result.Earnings[0].Amount.Should().Be(0m);
        result.Deductions.Should().BeEmpty();
    }

    // ── Statutory carve-out: config can NEVER drive a statutory amount (always the pack) ─────────
    [Fact]
    public void Compute_StatutoryComponentWithTamperedValue_IgnoresConfigUsesPack()
    {
        // A malicious/edited STATUTORY_EE row with Fixed 99,999 — the engine must ignore it and use the pack.
        var tampered = PayComponentCatalog.SystemComponentSeeds(Guid.NewGuid())
            .Where(c => c.Code is "STATUTORY_EE")
            .Select(c => { c.CalcMethod = PayComponentCalcMethods.Fixed; c.Value = 99_999m; return c; })
            .ToList();
        var ctx = new PayComponentContext
        {
            StatutoryLines = new[] { new StatutoryDeductionLine("GOSI-ANN-EE", "GOSI Annuities (Employee)", 500m, 0m) },
        };

        var result = PayComponentEngine.Compute(tampered, ctx);

        result.Deductions.Should().ContainSingle();
        result.Deductions[0].Code.Should().Be("GOSI-ANN-EE");
        result.Deductions[0].Amount.Should().Be(500m); // pack value, NOT the tampered 99,999
    }

    // ── The catalog + guard classify statutory codes so the boundary is enforceable in one place ─
    [Fact]
    public void Catalog_StatutoryRows_AreFlaggedAndGuardClassifiesThem()
    {
        var comps = PayComponentCatalog.SystemComponentSeeds(Guid.NewGuid());
        comps.Where(c => c.Code is "STATUTORY_EE" or "STATUTORY_ER").Should().OnlyContain(c => c.IsStatutory);
        comps.Where(c => c.Code is "BASIC" or "HOUSING" or "OVERTIME").Should().OnlyContain(c => !c.IsStatutory);

        PayComponentGuard.IsStatutoryComponentCode("GOSI-ANN-EE").Should().BeTrue();
        PayComponentGuard.IsStatutoryComponentCode("GPSSA-EE").Should().BeTrue();
        PayComponentGuard.IsStatutoryComponentCode("GRSIA-ER").Should().BeTrue();
        PayComponentGuard.IsStatutoryComponentCode("STATUTORY_EE").Should().BeTrue();
        PayComponentGuard.IsStatutoryComponentCode("BASIC").Should().BeFalse();
        PayComponentGuard.IsStatutoryComponentCode("HOUSING").Should().BeFalse();
        PayComponentGuard.IsStatutoryComponentCode("OVERTIME").Should().BeFalse();
    }

    // ── Seeder is idempotent and reproduces the 17-row default set (7 earning + 10 deduction) ────
    [Fact]
    public async Task Seeder_IsIdempotent_And_Seeds17Rows()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();

        var first = await PayComponentSeeder.SeedTenantDefaultsAsync(db, tid, CancellationToken.None);
        await db.SaveChangesAsync();
        first.Components.Should().Be(17);

        // Second run adds nothing (up-serts absent rows only).
        var second = await PayComponentSeeder.SeedTenantDefaultsAsync(db, tid, CancellationToken.None);
        await db.SaveChangesAsync();
        second.Components.Should().Be(0);

        var rows = await db.PayComponents.IgnoreQueryFilters().Where(c => c.TenantId == tid).ToListAsync();
        rows.Should().HaveCount(17);
        rows.Count(c => c.ComponentType == PayComponentTypes.Earning).Should().Be(7);
        rows.Count(c => c.ComponentType == PayComponentTypes.Deduction).Should().Be(9);
        rows.Count(c => c.ComponentType == PayComponentTypes.EmployerContribution).Should().Be(1);
        // "ADJ" legitimately exists as BOTH an earning family and a deduction family.
        rows.Where(c => c.Code == "ADJ").Select(c => c.ComponentType)
            .Should().BeEquivalentTo(new[] { PayComponentTypes.Earning, PayComponentTypes.Deduction });
    }

    // ── Company row wins over tenant default (precedence parity with the GL driver store) ────────
    [Fact]
    public async Task Load_CompanyRow_WinsOverTenantDefault()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        await PayComponentSeeder.SeedTenantDefaultsAsync(db, tid, CancellationToken.None);
        await db.SaveChangesAsync();

        // Company override for BASIC that re-labels + re-orders it.
        db.PayComponents.Add(new PayComponent
        {
            TenantId = tid, CompanyId = companyId, Code = "BASIC", ComponentType = PayComponentTypes.Earning,
            NameEn = "Base pay (company)", CalcMethod = PayComponentCalcMethods.StructureField,
            StructureField = PayComponentStructureFields.BasicSalary, EmitWhenZero = true, DisplayOrder = 30, IsActive = true,
        });
        await db.SaveChangesAsync();

        var rows = await db.PayComponents.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.TenantId == tid && (c.CompanyId == companyId || c.CompanyId == null))
            .ToListAsync();
        var resolved = rows
            .GroupBy(c => (c.Code, c.ComponentType))
            .Select(g => g.OrderByDescending(c => c.CompanyId != null).First())
            .ToList();

        resolved.Single(c => c.Code == "BASIC" && c.ComponentType == PayComponentTypes.Earning)
            .NameEn.Should().Be("Base pay (company)");
        // The tenant default (unchanged) still resolves for every other code.
        resolved.Single(c => c.Code == "HOUSING").NameEn.Should().Be("Housing allowance");
    }
}
