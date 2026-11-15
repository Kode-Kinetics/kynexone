using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Models;
using static Zayra.Api.Tests.Security.SeededRoleBundles;

namespace Zayra.Api.Tests;

/// <summary>
/// C-1: one ledger row, one live file. The live-export guard used to match only the exact scope (period + run + company),
/// and the selection left out only CONFIRMED rows. So while a payroll-run export was waiting for the ERP's result, an
/// export of the whole month (or of the group) selected the same rows again and was created as a Full file: two files in
/// circulation for one journal, both importable, and only the second confirmation noticed.
///
/// <para>Now the check (preview) says which waiting file already holds the rows, and the create is refused until that
/// file's ERP result is recorded or its own scope is exported again in the same format (which supersedes it).</para>
/// </summary>
public class JournalExportLiveOverlapTests
{
    private const string Period = "2026-07";
    private static readonly Guid CompanyA = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a");
    private static readonly Guid CompanyB = Guid.Parse("bbbbbbbb-0000-0000-0000-00000000000b");

    // ── 1. Preview: the overlap is named ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Preview_WhileARunExportIsLive_WarnsWithTheOtherFileAndHowManyRowsItHolds()
    {
        var (db, tenantId, run) = await JulyAsync();
        var maker = await CallerAsync(db, tenantId, "Payroll Manager");
        var runExport = await CreateRunAsync(db, maker, run);
        StatusOf(runExport).Should().Be(201);
        var runFile = Body(runExport).GetProperty("fileName").GetString()!;

        var preview = await Ctrl(db, maker).Preview(Period, null, null, "SAR");

        StatusOf(preview).Should().Be(200);
        var warnings = Body(preview).GetProperty("warnings").EnumerateArray().Select(w => w.GetString()!).ToList();
        warnings.Should().ContainSingle(w => w.Contains(runFile))
            .Which.Should().Contain("2 of these ledger rows").And.Contain("waiting for its ERP result");
        var overlap = Body(preview).GetProperty("liveExportOverlaps").EnumerateArray().Should().ContainSingle().Subject;
        overlap.GetProperty("exportId").GetGuid().Should().Be(IdOf(runExport));
        overlap.GetProperty("fileName").GetString().Should().Be(runFile);
        overlap.GetProperty("rows").GetInt32().Should().Be(2);
        overlap.GetProperty("relation").GetString().Should().Be("other_scope");
        overlap.GetProperty("messageAr").GetString().Should().Contain(runFile).And.Contain("بانتظار");
    }

    [Fact]
    public async Task Preview_WithNoOtherFileWaiting_CarriesNoOverlap()
    {
        var (db, tenantId, _) = await JulyAsync();
        var maker = await CallerAsync(db, tenantId, "Payroll Manager");

        var preview = await Ctrl(db, maker).Preview(Period, null, null, "SAR");

        StatusOf(preview).Should().Be(200);
        Body(preview).GetProperty("liveExportOverlaps").EnumerateArray().Should().BeEmpty();
        Body(preview).GetProperty("warnings").EnumerateArray().Should().NotContain(w => w.GetString()!.Contains("waiting for its ERP result"));
    }

    [Fact]
    public async Task Preview_OfTheLiveFilesOwnScopeAndFormat_SaysTheNewFileSupersedesIt()
    {
        var (db, tenantId, run) = await JulyAsync();
        var maker = await CallerAsync(db, tenantId, "Payroll Manager");
        var runExport = await CreateRunAsync(db, maker, run);

        var preview = await Ctrl(db, maker).Preview(Period, run, null, "SAR");

        StatusOf(preview).Should().Be(200);
        var overlap = Body(preview).GetProperty("liveExportOverlaps").EnumerateArray().Should().ContainSingle().Subject;
        overlap.GetProperty("exportId").GetGuid().Should().Be(IdOf(runExport));
        overlap.GetProperty("relation").GetString().Should().Be("superseded_by_this_export");
        overlap.GetProperty("message").GetString().Should().Contain("supersede");
    }

    // ── Harness ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>July: company A payroll run (two one-sided accrual rows), company B loan disbursement (two-sided).</summary>
    private static async Task<(ZayraDbContext Db, Guid TenantId, Guid Run)> JulyAsync()
    {
        var (db, tenantId) = await NewTenantAsync("gl-live-overlap");
        db.Companies.AddRange(
            new Company { Id = CompanyA, TenantId = tenantId, LegalNameEn = "Alpha KSA", RegistrationNumber = "7001234567", ErpCompanyCode = "SA01", DefaultCurrency = "SAR" },
            new Company { Id = CompanyB, TenantId = tenantId, LegalNameEn = "Beta Services", RegistrationNumber = "1010-555", ErpCompanyCode = "SA02", DefaultCurrency = "SAR" });
        var run = Guid.NewGuid();
        db.FinanceGlEntries.AddRange(
            Row(tenantId, CompanyA, "Payroll", run, "Accrual", "5000 - Salaries", "", 10_000m, "July salaries"),
            Row(tenantId, CompanyA, "Payroll", run, "Accrual", "", "2100 - Salaries Payable", 10_000m, "July payable"),
            Row(tenantId, CompanyB, "Loan", Guid.NewGuid(), "Disbursement", "1400 - Employee Loans", "1000 - Bank", 2_500m, "Loan LN-1"));
        await db.SaveChangesAsync();
        return (db, tenantId, run);
    }

    private static FinanceGlEntry Row(Guid tenantId, Guid company, string module, Guid source, string evt, string dr, string cr, decimal amount, string description) => new()
    {
        TenantId = tenantId, CompanyId = company, SourceModule = module, SourceEntityId = source, SourceEntityRef = module + "-REF",
        EventType = evt, DebitAccount = dr, CreditAccount = cr, Amount = amount, Currency = "SAR",
        EntryDate = new DateOnly(2026, 8, 3), Period = Period, Description = description,
    };

    private static GlJournalExportsController Ctrl(ZayraDbContext db, ClaimsPrincipal caller)
    {
        var exports = new JournalExportService(db, JournalExportFormatterGoldenTests.AllFormatters());
        return Bind(new GlJournalExportsController(db, exports, new PeriodHandoffReconciler(db, exports)), caller);
    }

    /// <summary>A payroll-run export (group scope). The run record is not seeded, so the period is sent with it.</summary>
    private static async Task<IActionResult> CreateRunAsync(ZayraDbContext db, ClaimsPrincipal maker, Guid run, string format = "generic-csv")
    {
        var result = await Ctrl(db, maker).Create(Period, run, null, "SAR", false, false, format, default);
        db.ChangeTracker.Clear();
        return result;
    }

    private static JsonElement Body(IActionResult r) => JsonSerializer.SerializeToElement(((ObjectResult)r).Value);
    private static Guid IdOf(IActionResult r) => Body(r).GetProperty("exportId").GetGuid();
}
