using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Models;
using static Zayra.Api.Tests.CostExportHarness;
using static Zayra.Api.Tests.Security.SeededRoleBundles;

namespace Zayra.Api.Tests;

/// <summary>
/// WP-D slice 2, through the real controller and service: the cost-centre snapshot is frozen on the export line and
/// regenerates byte-identically (T-058, T-062), file names and company codes are safe (T-060), Replacement and
/// Supplementary exports still work with costed lines (T-059), a rule that became Required after posting is reported
/// at export (S-039), and a line in another currency than the company's is refused (S-040).
/// EF in-memory, like the rest of the export suite; the Postgres-only behaviours belong to WP-E.
/// </summary>
public class JournalExportCostCentreServiceTests
{
    private static void SeedCostedPayroll(Microsoft.EntityFrameworkCore.DbContext db, Guid tenantId, decimal amount = 10_000m)
    {
        db.Add(Row(tenantId, "Accrual", "5000 - Salaries", "", amount, "July payroll", ccCode: "CC100", ccName: "Operations", ccSegment: "6100-OPS"));
        db.Add(Row(tenantId, "Accrual", "", "2100 - Salaries Payable", amount, "July payroll"));
        db.SaveChanges();
    }

    // ── Freeze, regenerate, safe names (T-058, T-060, T-062) ──────────────────────────────────────

    [Fact]
    public async Task ACostedExport_FreezesTheSnapshot_OnTheProfitAndLossLineOnly_AndCarriesItInEveryFormat()
    {
        var (db, tenantId) = await TenantAsync(costAccounting: true);
        SeedChart(db, tenantId);
        SeedCostedPayroll(db, tenantId);
        var maker = await CallerAsync(db, tenantId, "Payroll Manager");

        var generic = IdOf(await CreateAsync(db, maker, "generic-csv"));
        var sap = IdOf(await CreateAsync(db, maker, "sap-s4-gje"));
        var d365 = IdOf(await CreateAsync(db, maker, "d365-fo-general-journal"));

        var frozen = await db.GlJournalExportLines.AsNoTracking().Where(l => l.GlJournalExportId == generic).OrderBy(l => l.LineNo).ToListAsync();
        frozen.Single(l => l.CostCenterCode != null).Should().Match<GlJournalExportLine>(l =>
            l.AccountCode == "5000" && l.Side == "DR" && l.CostCenterName == "Operations" && l.CostCenterGlSegment == "6100-OPS");
        frozen.Where(l => l.AccountCode == "2100").Should().OnlyContain(l => l.CostCenterCode == null && l.CostCenterName == null);

        var g = ParseCsv(Utf8(await DownloadAsync(db, tenantId, generic)));
        g[1][g[0].IndexOf("CostCentre")].Should().Be("CC100");
        g[1][g[0].IndexOf("CostCentreErpCode")].Should().Be("6100-OPS");
        g[2][g[0].IndexOf("CostCentre")].Should().BeEmpty();

        var s = ParseCsv(Utf8(await DownloadAsync(db, tenantId, sap)));
        s.Select(r => r[s[0].IndexOf("CostCenter")]).Skip(1).Should().Equal("6100-OPS", "");

        var d = ParseCsv(Utf8(await DownloadAsync(db, tenantId, d365)));
        d.Select(r => r[d[0].IndexOf("COSTCENTRE")]).Skip(1).Should().Equal("6100-OPS", "");
    }

    [Fact]
    public async Task ARenamedCostCentre_DoesNotChangeAnExportAlreadyTaken_AndRegenerationIsStable()
    {
        var (db, tenantId) = await TenantAsync(costAccounting: true);
        SeedChart(db, tenantId);
        SeedCostedPayroll(db, tenantId);
        var cc = new CostCenter { TenantId = tenantId, CompanyId = CompanyA, Code = "CC100", Name = "Operations", GlSegment = "6100-OPS" };
        db.CostCenters.Add(cc);
        db.SaveChanges();
        var maker = await CallerAsync(db, tenantId, "Payroll Manager");
        var id = IdOf(await CreateAsync(db, maker));
        var hash = (await db.GlJournalExports.AsNoTracking().SingleAsync(x => x.Id == id)).FileHash;

        var tracked = await db.CostCenters.SingleAsync(c => c.Id == cc.Id);
        tracked.Name = "Renamed"; tracked.GlSegment = "ZZZ-999";
        await db.SaveChangesAsync();

        var runs = new List<byte[]>();
        for (var i = 0; i < 3; i++) runs.Add(await DownloadAsync(db, tenantId, id));
        runs.Skip(1).Should().OnlyContain(b => b.SequenceEqual(runs[0]), "the snapshot is frozen on the export line");
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(runs[0])).Should().BeEquivalentTo(hash);
        Utf8(runs[0]).Should().Contain("6100-OPS").And.NotContain("ZZZ-999");
    }

    [Fact]
    public async Task TheFileName_NeverCarriesACostCentreCodeOrName()
    {
        var (db, tenantId) = await TenantAsync(costAccounting: true);
        SeedChart(db, tenantId);
        SeedCostedPayroll(db, tenantId);
        var maker = await CallerAsync(db, tenantId, "Payroll Manager");

        var created = await CreateAsync(db, maker);

        var name = Body(created).GetProperty("fileName").GetString()!;
        name.Should().NotContain("CC100").And.NotContain("Operations").And.NotContain("6100-OPS");
        (await db.GlJournalExports.AsNoTracking().SingleAsync()).FileName.Should().Be(name);
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("SA01/..")]
    [InlineData("a\\b")]
    [InlineData("C:evil")]
    public async Task AnUnsafeCompanyCode_IsRefusedBeforeAnythingIsWritten(string erpCode)
    {
        var (db, tenantId) = await TenantAsync(erpCode: erpCode);
        db.Add(Row(tenantId, "Accrual", "5000 - Salaries", "", 5m));
        db.Add(Row(tenantId, "Accrual", "", "2100 - Payable", 5m));
        db.SaveChanges();
        var maker = await CallerAsync(db, tenantId, "Payroll Manager");

        var created = await CreateAsync(db, maker);

        StatusOf(created).Should().Be(422);
        Body(created).GetProperty("error").GetString().Should().Be("unsafe_company_code");
        (await db.GlJournalExports.AnyAsync()).Should().BeFalse();
        (await db.GlJournalExportLines.AnyAsync()).Should().BeFalse();
        (await db.FinanceGlEntries.AsNoTracking().AllAsync(e => e.ErpPostingStatus != ErpPostingStatuses.Exported)).Should().BeTrue();
    }

    // ── Replacement and Supplementary with costed lines (T-059) ──────────────────────────────────

    [Fact]
    public async Task ASupplementaryExport_StillWorks_WithCostedLines()
    {
        var (db, tenantId) = await TenantAsync(costAccounting: true);
        SeedChart(db, tenantId);
        SeedCostedPayroll(db, tenantId);
        var maker = await CallerAsync(db, tenantId, "Payroll Manager");
        var checker = await CallerAsync(db, tenantId, "Finance Approver");
        var full = IdOf(await CreateAsync(db, maker));
        StatusOf(await Ctrl(db, checker).Confirm(full, new ErpImportConfirmationRequest("SAP-1"), default)).Should().Be(200);
        db.Add(Row(tenantId, "Adjustment", "5000 - Salaries", "", 300m, "Late adjustment", module: "Adjustment", source: Guid.NewGuid(),
            ccCode: "CC100", ccName: "Operations", ccSegment: "6100-OPS"));
        db.Add(Row(tenantId, "Adjustment", "", "2100 - Salaries Payable", 300m, "Late adjustment", module: "Adjustment", source: Guid.NewGuid()));
        db.SaveChanges();

        var second = await CreateAsync(db, maker);

        StatusOf(second).Should().Be(201);
        Body(second).GetProperty("kind").GetString().Should().Be("Supplementary");
        var rows = ParseCsv(Utf8(await DownloadAsync(db, tenantId, IdOf(second))));
        rows.Should().HaveCount(3);
        rows[1][rows[0].IndexOf("CostCentre")].Should().Be("CC100");
        rows[1][rows[0].IndexOf("ExportKind")].Should().Be("Supplementary");
    }

    [Fact]
    public async Task AReplacementExport_StillWorks_WithCostedLines()
    {
        var (db, tenantId) = await TenantAsync(costAccounting: true);
        SeedChart(db, tenantId);
        SeedCostedPayroll(db, tenantId);
        var maker = await CallerAsync(db, tenantId, "Payroll Manager");
        var checker = await CallerAsync(db, tenantId, "Finance Approver");
        var full = IdOf(await CreateAsync(db, maker));
        StatusOf(await Ctrl(db, checker).Confirm(full, new ErpImportConfirmationRequest("SAP-1"), default)).Should().Be(200);
        db.ChangeTracker.Clear();

        var replacement = await Ctrl(db, maker).Create(Period, null, null, "SAR", false, false, "generic-csv", default,
            includePreviouslyConfirmed: true, replacesExportId: full);
        db.ChangeTracker.Clear();

        StatusOf(replacement).Should().Be(201);
        Body(replacement).GetProperty("kind").GetString().Should().Be("Replacement");
        var rows = ParseCsv(Utf8(await DownloadAsync(db, tenantId, IdOf(replacement))));
        rows[1][rows[0].IndexOf("CostCentre")].Should().Be("CC100");
        (await db.GlJournalExports.AsNoTracking().SingleAsync(x => x.Id == IdOf(replacement))).ReplacesExportId.Should().Be(full);
    }

    // ── S-039: a rule that became Required after posting ──────────────────────────────────────────

    private static void SeedUncosted(Microsoft.EntityFrameworkCore.DbContext db, Guid tenantId)
    {
        db.Add(Row(tenantId, "Accrual", "5000 - Salaries", "", 10_000m, "Confidential July payroll"));
        db.Add(Row(tenantId, "Accrual", "", "2100 - Salaries Payable", 10_000m, "Confidential July payroll"));
        db.SaveChanges();
    }

    [Fact]
    public async Task ARequiredRule_WithUncostedPostedLines_IsReportedWithCountsOnly_UntilAcknowledged()
    {
        var (db, tenantId) = await TenantAsync(costAccounting: true);
        SeedChart(db, tenantId, rule5000: "Required");
        SeedUncosted(db, tenantId);
        var maker = await CallerAsync(db, tenantId, "Payroll Manager");

        var refused = await CreateAsync(db, maker);

        StatusOf(refused).Should().Be(422);
        var body = Body(refused);
        body.GetProperty("error").GetString().Should().Be("uncosted_required_lines");
        body.GetProperty("count").GetInt32().Should().Be(1);
        body.GetProperty("accounts")[0].GetProperty("accountCode").GetString().Should().Be("5000");
        body.GetRawText().Should().NotContain("Confidential").And.NotContain("10000", "counts only: no description, amount or id");
        (await db.GlJournalExports.AnyAsync()).Should().BeFalse();

        var acknowledged = await Ctrl(db, maker).Create(Period, null, null, "SAR", false, false, "generic-csv", default,
            acknowledgeUncostedRequired: true);
        db.ChangeTracker.Clear();

        StatusOf(acknowledged).Should().Be(201);
        Body(acknowledged).GetProperty("warnings").EnumerateArray().Select(w => w.GetString())
            .Should().Contain(w => w!.Contains("without a cost centre"));
    }

    [Fact]
    public async Task TheRequiredRuleIsNotEnforcedAtExport_WhileCostingIsOff_OrOnceTheLineIsCosted_OrOnAContra()
    {
        // costing OFF: nothing is enforced.
        var (off, offTenant) = await TenantAsync(costAccounting: false);
        SeedChart(off, offTenant, rule5000: "Required");
        SeedUncosted(off, offTenant);
        StatusOf(await CreateAsync(off, await CallerAsync(off, offTenant, "Payroll Manager"))).Should().Be(201);

        // costed line on a Required account: fine.
        var (on, onTenant) = await TenantAsync(costAccounting: true);
        SeedChart(on, onTenant, rule5000: "Required");
        SeedCostedPayroll(on, onTenant);
        StatusOf(await CreateAsync(on, await CallerAsync(on, onTenant, "Payroll Manager"))).Should().Be(201);

        // an uncosted CONTRA is exempt (a rule change after posting must not block a void's export); its original still counts once.
        var (rev, revTenant) = await TenantAsync(costAccounting: true);
        SeedChart(rev, revTenant, rule5000: "Required");
        var original = Row(revTenant, "Accrual", "5000 - Salaries", "2100 - Salaries Payable", 50m, "orig");
        rev.Add(original);
        rev.Add(Row(revTenant, "Reversal", "2100 - Salaries Payable", "5000 - Salaries", 50m, "contra", reversalOf: original.Id));
        rev.SaveChanges();
        var refused = await CreateAsync(rev, await CallerAsync(rev, revTenant, "Payroll Manager"));
        StatusOf(refused).Should().Be(422);
        Body(refused).GetProperty("count").GetInt32().Should().Be(1);
    }

    // ── S-040: currency must match the company ───────────────────────────────────────────────────

    [Fact]
    public async Task ALineInAnotherCurrencyThanTheCompanys_IsRefused_OnceCostingIsOn()
    {
        var (db, tenantId) = await TenantAsync("SAR", costAccounting: true);
        db.Add(Row(tenantId, "Accrual", "5000 - Salaries", "", 5m, currency: "USD"));
        db.Add(Row(tenantId, "Accrual", "", "2100 - Payable", 5m, currency: "USD"));
        db.SaveChanges();
        var maker = await CallerAsync(db, tenantId, "Payroll Manager");

        var refused = await CreateAsync(db, maker, "generic-csv", "USD");

        StatusOf(refused).Should().Be(422);
        Body(refused).GetProperty("error").GetString().Should().Be("mixed_currency_export");
        Body(refused).GetProperty("companyCurrency").GetString().Should().Be("SAR");
        (await db.GlJournalExports.AnyAsync()).Should().BeFalse();

        var (legacy, legacyTenant) = await TenantAsync("SAR", costAccounting: false);
        legacy.Add(Row(legacyTenant, "Accrual", "5000 - Salaries", "", 5m, currency: "USD"));
        legacy.Add(Row(legacyTenant, "Accrual", "", "2100 - Payable", 5m, currency: "USD"));
        legacy.SaveChanges();
        StatusOf(await CreateAsync(legacy, await CallerAsync(legacy, legacyTenant, "Payroll Manager"), "generic-csv", "USD"))
            .Should().Be(201, "tenants that have not switched costing on are unchanged");
    }

    // ── Disclosure at export time ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExportingCostedLines_ToAFormatWithNoDimensionField_Warns()
    {
        var (db, tenantId) = await TenantAsync(costAccounting: true);
        SeedChart(db, tenantId);
        SeedCostedPayroll(db, tenantId);
        var maker = await CallerAsync(db, tenantId, "Payroll Manager");

        var created = await CreateAsync(db, maker, "xero-manual-journal");

        StatusOf(created).Should().Be(201);
        Body(created).GetProperty("warnings").EnumerateArray().Select(w => w.GetString())
            .Should().Contain(w => w!.Contains("cost centre", StringComparison.OrdinalIgnoreCase));
        var plain = await CreateAsync(db, maker, "generic-csv");
        Body(plain).GetProperty("warnings").EnumerateArray().Should().BeEmpty();
    }
}
