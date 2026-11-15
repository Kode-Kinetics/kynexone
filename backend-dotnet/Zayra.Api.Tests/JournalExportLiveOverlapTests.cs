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

    // ── 2. Create: refused while another scope's file waits ───────────────────────────────────────

    [Fact]
    public async Task Create_WhileARunExportIsLive_APeriodExportOfTheSameMonth_IsRefused_AndWritesNothing()
    {
        var (db, tenantId, run) = await JulyAsync();
        var maker = await CallerAsync(db, tenantId, "Payroll Manager");
        var runExport = await CreateRunAsync(db, maker, run);
        var runFile = Body(runExport).GetProperty("fileName").GetString()!;

        var period = await CreatePeriodAsync(db, maker);

        StatusOf(period).Should().Be(409);
        var body = Body(period);
        body.GetProperty("error").GetString().Should().Be("entries_in_live_export");
        body.GetProperty("code").GetString().Should().Be("entries_in_live_export");
        body.GetProperty("exportId").GetGuid().Should().Be(IdOf(runExport));
        body.GetProperty("fileName").GetString().Should().Be(runFile);
        body.GetProperty("rows").GetInt32().Should().Be(2);
        body.GetProperty("message").GetString().Should().Contain("2 of these ledger rows").And.Contain(runFile)
            .And.Contain("waiting for its ERP result").And.Contain("posted or rejected").And.Contain("supersede")
            .And.NotContain("Replacement");
        body.GetProperty("messageAr").GetString().Should().Contain(runFile).And.Contain("بانتظار");
        body.GetProperty("liveExports").EnumerateArray().Should().ContainSingle()
            .Which.GetProperty("exportId").GetGuid().Should().Be(IdOf(runExport));

        (await db.GlJournalExports.AsNoTracking().CountAsync()).Should().Be(1, "a refusal writes no artifact");
        (await db.GlJournalExports.AsNoTracking().SingleAsync()).Status.Should().Be(GlJournalExportStatuses.Exported, "the waiting file is untouched");
        (await db.FinanceGlEntries.AsNoTracking().SingleAsync(e => e.SourceModule == "Loan")).ErpPostingStatus
            .Should().NotBe(ErpPostingStatuses.Exported, "nothing was stamped for the refused file");
    }

    [Fact]
    public async Task Create_WhileACompanyExportIsLive_AGroupExportOfTheSameMonth_IsRefused()
    {
        var (db, tenantId, _) = await JulyAsync();
        var maker = await CallerAsync(db, tenantId, "Payroll Manager");
        var companyExport = await CreatePeriodAsync(db, maker, CompanyA);
        StatusOf(companyExport).Should().Be(201);

        var group = await CreatePeriodAsync(db, maker);

        StatusOf(group).Should().Be(409);
        Body(group).GetProperty("error").GetString().Should().Be("entries_in_live_export");
        Body(group).GetProperty("exportId").GetGuid().Should().Be(IdOf(companyExport));
        Body(group).GetProperty("rows").GetInt32().Should().Be(2, "company A's two payroll rows are in the waiting file");
    }

    [Fact]
    public async Task AcknowledgingAnotherFormat_DoesNotOverrideTheRefusal()
    {
        var (db, tenantId, run) = await JulyAsync();
        var maker = await CallerAsync(db, tenantId, "Payroll Manager");
        await CreateRunAsync(db, maker, run);

        var sap = await CreatePeriodAsync(db, maker, format: "sap-s4-gje", acknowledgeOtherFormatLive: true);

        StatusOf(sap).Should().Be(409, "the acknowledgement covers another format of the SAME scope, never another scope's file");
        Body(sap).GetProperty("error").GetString().Should().Be("entries_in_live_export");
        (await db.GlJournalExports.AsNoTracking().CountAsync()).Should().Be(1);
    }

    // ── 3. A recorded ERP result releases the rows ────────────────────────────────────────────────

    [Fact]
    public async Task AfterTheRunExportIsConfirmed_ThePeriodExportSendsOnlyTheRowsTheErpHasNotPosted()
    {
        var (db, tenantId, run) = await JulyAsync();
        var maker = await CallerAsync(db, tenantId, "Payroll Manager");
        var checker = await CallerAsync(db, tenantId, "Finance Approver");
        var runId = IdOf(await CreateRunAsync(db, maker, run));
        StatusOf(await Ctrl(db, checker).Confirm(runId, new ErpImportConfirmationRequest("SAP-RUN-1"), default)).Should().Be(200);
        db.ChangeTracker.Clear();

        var period = await CreatePeriodAsync(db, maker);

        StatusOf(period).Should().Be(201);
        Body(period).GetProperty("kind").GetString().Should().Be("Supplementary");
        Body(period).GetProperty("previouslyConfirmedExcluded").GetProperty("count").GetInt32().Should().Be(2);
        Body(period).GetProperty("liveExportOverlaps").EnumerateArray().Should().BeEmpty();
        (await db.GlJournalExportLines.AsNoTracking().Where(l => l.GlJournalExportId == IdOf(period)).Select(l => l.SourceModule).Distinct().ToListAsync())
            .Should().Equal(new[] { "Loan" }, "the payroll rows are in the ERP; only the loan is owed");
    }

    [Fact]
    public async Task AfterTheRunExportIsRejected_ThePeriodExportSendsEveryRow()
    {
        var (db, tenantId, run) = await JulyAsync();
        var maker = await CallerAsync(db, tenantId, "Payroll Manager");
        var checker = await CallerAsync(db, tenantId, "Finance Approver");
        var runId = IdOf(await CreateRunAsync(db, maker, run));
        StatusOf(await Ctrl(db, checker).Reject(runId, new ErpImportRejectionRequest("Wrong cost centre"), default)).Should().Be(200);
        db.ChangeTracker.Clear();

        var period = await CreatePeriodAsync(db, maker);

        StatusOf(period).Should().Be(201);
        Body(period).GetProperty("kind").GetString().Should().Be("Full");
        Body(period).GetProperty("entryCount").GetInt32().Should().Be(3);
        Body(period).GetProperty("liveExportOverlaps").EnumerateArray().Should().BeEmpty();
    }

    [Fact]
    public async Task ExportingTheWaitingFilesOwnScopeAgain_SupersedesIt_AndTheNewFileThenHoldsTheRows()
    {
        var (db, tenantId, run) = await JulyAsync();
        var maker = await CallerAsync(db, tenantId, "Payroll Manager");
        var first = IdOf(await CreateRunAsync(db, maker, run));

        var again = await CreateRunAsync(db, maker, run);

        StatusOf(again).Should().Be(201, "the way forward the refusal names: the same scope again, in the same format");
        (await db.GlJournalExports.AsNoTracking().SingleAsync(x => x.Id == first)).Status.Should().Be(GlJournalExportStatuses.Superseded);
        var period = await CreatePeriodAsync(db, maker);
        StatusOf(period).Should().Be(409);
        Body(period).GetProperty("exportId").GetGuid().Should().Be(IdOf(again), "a superseded file holds nothing; the new one does");
    }

    [Fact]
    public async Task ARejectedJournalOfAWaitingFile_IsOwedAgain_ButItsPendingJournalStillBlocks()
    {
        var (db, tenantId, _) = await JulyAsync();
        var maker = await CallerAsync(db, tenantId, "Payroll Manager");
        var checker = await CallerAsync(db, tenantId, "Finance Approver");
        var group = IdOf(await CreatePeriodAsync(db, maker));
        var loanJournal = Body(await Ctrl(db, checker).Get(group, default)).GetProperty("journals").EnumerateArray()
            .Select(j => j.GetProperty("journalRef").GetString()!).Single(r => r.Contains("Loan"));
        var reject = await Ctrl(db, checker).RejectJournals(group, new ErpJournalRejectionRequest(new() { new(loanJournal, "Bank account unknown") }), default);
        Body(reject).GetProperty("status").GetString().Should().Be(GlJournalExportStatuses.Exported, "the payroll journal is still waiting");
        db.ChangeTracker.Clear();

        StatusOf(await CreatePeriodAsync(db, maker, CompanyB)).Should().Be(201, "the ERP refused the loan journal, so its rows are owed again");
        var companyA = await CreatePeriodAsync(db, maker, CompanyA);
        StatusOf(companyA).Should().Be(409);
        Body(companyA).GetProperty("exportId").GetGuid().Should().Be(group);
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

    private static async Task<IActionResult> CreatePeriodAsync(ZayraDbContext db, ClaimsPrincipal maker, Guid? companyId = null,
        string format = "generic-csv", bool acknowledgeOtherFormatLive = false)
    {
        var result = await Ctrl(db, maker).Create(Period, null, companyId, "SAR", false, false, format, default,
            acknowledgeOtherFormatLive: acknowledgeOtherFormatLive);
        db.ChangeTracker.Clear();
        return result;
    }

    private static JsonElement Body(IActionResult r) => JsonSerializer.SerializeToElement(((ObjectResult)r).Value);
    private static Guid IdOf(IActionResult r) => Body(r).GetProperty("exportId").GetGuid();
}
