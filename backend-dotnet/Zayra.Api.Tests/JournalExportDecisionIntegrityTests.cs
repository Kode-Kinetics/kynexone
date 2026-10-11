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
/// ERP evidence integrity across exports, driven through the real controller with seeded role bundles (maker = Payroll
/// Manager, checker = Finance Approver). Each test names the defect it pins:
/// <list type="bullet">
/// <item>a new export silently superseded an export whose journals were only PARTLY recorded — stranding the ERP
/// evidence and re-sending journals the ERP may already hold;</item>
/// <item>confirming an export never checked whether ANOTHER export had already confirmed the same rows, and overwrote
/// the first ERP document number;</item>
/// <item>the period reconciler summed export headers, double-counting the Supplementary / Replacement / partly
/// confirmed flows so "landed" could never become true;</item>
/// <item>the per-company gate grouped by a code string, single-entity formats accepted multi-company files, Oracle
/// exports were persisted with no LEDGER_ID, and a superseded export did not say why.</item>
/// </list>
/// The concurrency half (two simultaneous per-journal confirmations) is proven on Postgres by
/// JournalExportDecisionConcurrencyPostgresTests.
/// </summary>
public class JournalExportDecisionIntegrityTests
{
    private const string Period = "2026-07";
    private static readonly Guid CompanyA = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a");
    private static readonly Guid CompanyB = Guid.Parse("bbbbbbbb-0000-0000-0000-00000000000b");

    // ── Item 1: a partly recorded export is evidence, never superseded ───────────────────────────

    [Fact]
    public async Task APartlyRecordedExport_IsNotSuperseded_AndANewExportWaitsUntilTheRestIsRecorded()
    {
        var (db, tenantId) = await TenantAsync();
        SeedJuly(db, tenantId);
        var maker = await CallerAsync(db, tenantId, "Payroll Manager");
        var checker = await CallerAsync(db, tenantId, "Finance Approver");
        var first = IdOf(await CreateAsync(db, maker));
        var journals = await JournalRefsAsync(db, checker, first);
        var payroll = journals.Single(j => j.Contains("Payroll"));
        var loan = journals.Single(j => j.Contains("Loan"));
        StatusOf(await Ctrl(db, checker).ConfirmJournals(first, new ErpJournalConfirmationRequest(new() { new(payroll, "SAP-100") }), default))
            .Should().Be(200);
        AddLoan(db, tenantId, CompanyA, 3_000m);

        var blocked = await CreateAsync(db, maker);

        StatusOf(blocked).Should().Be(409);
        Body(blocked).GetProperty("error").GetString().Should().Be(JournalExportErrors.PriorPartlyRecorded);
        Body(blocked).GetProperty("message").GetString().Should().Contain("Record whether your ERP posted or refused the remaining journals");
        db.ChangeTracker.Clear();
        (await db.GlJournalExports.AsNoTracking().SingleAsync()).Status
            .Should().Be(GlJournalExportStatuses.Exported, "its confirmed journal is ERP evidence: it must not be superseded");

        // Recording the remaining journal (here: the ERP refused it) settles the file; the new export then carries
        // only what the ERP does not hold — the refused loan journal and the late loan — never the confirmed payroll.
        StatusOf(await Ctrl(db, checker).RejectJournals(first, new ErpJournalRejectionRequest(new() { new(loan, "Missing cost centre") }), default))
            .Should().Be(200);
        var next = await CreateAsync(db, maker);
        StatusOf(next).Should().Be(201);
        Kind(next).Should().Be("Supplementary");
        db.ChangeTracker.Clear();
        (await db.GlJournalExportLines.AsNoTracking().Where(l => l.GlJournalExportId == IdOf(next)).Select(l => l.SourceModule).Distinct().ToListAsync())
            .Should().Equal(new[] { "Loan" });
        (await db.GlJournalExports.AsNoTracking().SingleAsync(x => x.Id == first)).Status.Should().Be(GlJournalExportStatuses.PartiallyConfirmed);
    }

    [Fact]
    public async Task ASupersededExport_SaysByWhichExportAndWhy()
    {
        var (db, tenantId) = await TenantAsync();
        SeedJuly(db, tenantId);
        var maker = await CallerAsync(db, tenantId, "Payroll Manager");
        var first = IdOf(await CreateAsync(db, maker));
        var second = IdOf(await CreateAsync(db, maker));

        db.ChangeTracker.Clear();
        var prior = await db.GlJournalExports.AsNoTracking().SingleAsync(x => x.Id == first);
        prior.Status.Should().Be(GlJournalExportStatuses.Superseded);
        prior.SupersededByExportId.Should().Be(second);
        prior.SupersedeReason.Should().Contain("Replaced by a newer full file").And.Contain("before your ERP's result was recorded");
    }

    // ── Item 3: one ledger row, one ERP posting ──────────────────────────────────────────────────

    [Fact]
    public async Task ConfirmingAnExportWhoseRowsAnotherExportAlreadyConfirmed_Is409_AndKeepsTheFirstDocumentNumber()
    {
        var (db, tenantId) = await TenantAsync();
        SeedJuly(db, tenantId);
        var maker = await CallerAsync(db, tenantId, "Payroll Manager");
        var checker = await CallerAsync(db, tenantId, "Finance Approver");
        var a = IdOf(await CreateAsync(db, maker));
        StatusOf(await Ctrl(db, checker).Reject(a, new ErpImportRejectionRequest("Import failed"), default)).Should().Be(200);
        var b = IdOf(await CreateAsync(db, maker));
        StatusOf(await Ctrl(db, checker).Confirm(b, new ErpImportConfirmationRequest("JE-200"), default)).Should().Be(200);

        var late = await Ctrl(db, checker).Confirm(a, new ErpImportConfirmationRequest("JE-100"), default);

        StatusOf(late).Should().Be(409);
        Body(late).GetProperty("error").GetString().Should().Be(JournalExportErrors.RowsAlreadyConfirmed);
        var held = Body(late).GetProperty("alreadyConfirmed").EnumerateArray().Single();
        held.GetProperty("exportId").GetGuid().Should().Be(b);
        held.GetProperty("erpDocumentNumber").GetString().Should().Be("JE-200");
        Body(late).GetProperty("message").GetString().Should().Contain("would mean your ERP holds them twice");
        db.ChangeTracker.Clear();
        (await db.FinanceGlEntries.AsNoTracking().ToListAsync()).Should().OnlyContain(e => e.ErpDocumentNumber == "JE-200",
            "the first ERP document number is the evidence and is never overwritten");
        (await db.GlJournalExports.AsNoTracking().SingleAsync(x => x.Id == a)).Status.Should().Be(GlJournalExportStatuses.Rejected);
    }

    [Fact]
    public async Task ConfirmingAJournalAnExportOfAnotherFormatAlreadyConfirmed_Is409_NamingThatExportAndJournal()
    {
        var (db, tenantId) = await TenantAsync();
        SeedJuly(db, tenantId);
        var maker = await CallerAsync(db, tenantId, "Payroll Manager");
        var checker = await CallerAsync(db, tenantId, "Finance Approver");
        var csv = IdOf(await CreateAsync(db, maker));
        var sap = IdOf(await CreateAsync(db, maker, "sap-s4-gje"));
        var csvPayroll = (await JournalRefsAsync(db, checker, csv)).Single(j => j.Contains("Payroll"));
        var sapPayroll = (await JournalRefsAsync(db, checker, sap)).Single(j => j.Contains("Payroll"));
        StatusOf(await Ctrl(db, checker).ConfirmJournals(csv, new ErpJournalConfirmationRequest(new() { new(csvPayroll, "SAP-1") }), default))
            .Should().Be(200);

        var second = await Ctrl(db, checker).ConfirmJournals(sap, new ErpJournalConfirmationRequest(new() { new(sapPayroll, "SAP-2") }), default);

        StatusOf(second).Should().Be(409);
        var held = Body(second).GetProperty("alreadyConfirmed").EnumerateArray().Single();
        held.GetProperty("exportId").GetGuid().Should().Be(csv);
        held.GetProperty("otherJournalRef").GetString().Should().Be(csvPayroll);
        held.GetProperty("journalRefs").EnumerateArray().Select(x => x.GetString()).Should().Equal(sapPayroll);
        db.ChangeTracker.Clear();
        (await db.GlJournalExports.AsNoTracking().SingleAsync(x => x.Id == sap)).JournalConfirmationsJson.Should().Be("[]");
    }

    [Fact]
    public async Task AReplacementOfAConfirmedExport_CanBeConfirmed()
    {
        var (db, tenantId) = await TenantAsync();
        SeedJuly(db, tenantId);
        var maker = await CallerAsync(db, tenantId, "Payroll Manager");
        var checker = await CallerAsync(db, tenantId, "Finance Approver");
        var x = IdOf(await CreateAsync(db, maker));
        StatusOf(await Ctrl(db, checker).Confirm(x, new ErpImportConfirmationRequest("JE-1"), default)).Should().Be(200);
        var r = IdOf(await CreateAsync(db, maker, replacesExportId: x));

        StatusOf(await Ctrl(db, checker).Confirm(r, new ErpImportConfirmationRequest("JE-2"), default))
            .Should().Be(200, "the ERP reversed the replaced export, so re-posting its rows is the intended outcome");
    }

    // ── Item 4: the reconciler counts each ledger row once ───────────────────────────────────────

    [Fact]
    public async Task Reconciler_SupplementaryAfterAConfirmedFile_ExportedEqualsPosted()
    {
        var (db, tenantId) = await TenantAsync();
        SeedJuly(db, tenantId);
        var maker = await CallerAsync(db, tenantId, "Payroll Manager");
        var checker = await CallerAsync(db, tenantId, "Finance Approver");
        var x = IdOf(await CreateAsync(db, maker));
        await Ctrl(db, checker).Confirm(x, new ErpImportConfirmationRequest("JE-1"), default);
        AddLoan(db, tenantId, CompanyA, 3_000m);
        StatusOf(await CreateAsync(db, maker)).Should().Be(201);

        var rec = await ReconcileAsync(db, maker);

        rec.GetProperty("deltas").GetProperty("postedMinusExported").GetDecimal().Should().Be(0m);
        rec.GetProperty("exported").GetProperty("newRowsInScopeSinceExport").GetProperty("count").GetInt32().Should().Be(0);
        Blockers(rec).Should().NotContain(new[] { "posted_export_delta", "gl_rows_posted_since_last_export" });
    }

    [Fact]
    public async Task Reconciler_AReplacementSupersedesTheRowsOfTheExportItReplaces()
    {
        var (db, tenantId) = await TenantAsync();
        SeedJuly(db, tenantId);
        var maker = await CallerAsync(db, tenantId, "Payroll Manager");
        var checker = await CallerAsync(db, tenantId, "Finance Approver");
        var x = IdOf(await CreateAsync(db, maker));
        await Ctrl(db, checker).Confirm(x, new ErpImportConfirmationRequest("JE-1"), default);
        var r = IdOf(await CreateAsync(db, maker, replacesExportId: x));
        await Ctrl(db, checker).Confirm(r, new ErpImportConfirmationRequest("JE-2"), default);

        var rec = await ReconcileAsync(db, maker);

        rec.GetProperty("deltas").GetProperty("postedMinusExported").GetDecimal()
            .Should().Be(0m, "the replaced file and its replacement hold the same rows once, not twice");
        rec.GetProperty("exported").GetProperty("exportedTotalDebits").GetDecimal().Should().Be(rec.GetProperty("posted").GetProperty("totalDebits").GetDecimal());
        rec.GetProperty("exported").GetProperty("replacedExportIds").EnumerateArray().Select(e => e.GetGuid()).Should().Equal(x);
        Blockers(rec).Should().NotContain("posted_export_delta");
    }

    [Fact]
    public async Task Reconciler_APartlyConfirmedExportsRejectedJournal_IsOwed_ThenCountedOnceWhenResent()
    {
        var (db, tenantId) = await TenantAsync();
        SeedJuly(db, tenantId);
        var maker = await CallerAsync(db, tenantId, "Payroll Manager");
        var checker = await CallerAsync(db, tenantId, "Finance Approver");
        var x = IdOf(await CreateAsync(db, maker));
        var journals = await JournalRefsAsync(db, checker, x);
        await Ctrl(db, checker).ConfirmJournals(x, new ErpJournalConfirmationRequest(new() { new(journals.Single(j => j.Contains("Payroll")), "JE-1") }), default);
        await Ctrl(db, checker).RejectJournals(x, new ErpJournalRejectionRequest(new() { new(journals.Single(j => j.Contains("Loan")), "Refused") }), default);

        var owed = await ReconcileAsync(db, maker);
        owed.GetProperty("exported").GetProperty("newRowsInScopeSinceExport").GetProperty("count").GetInt32()
            .Should().Be(1, "the refused loan journal's row is owed again, not handed over");
        owed.GetProperty("deltas").GetProperty("postedMinusExported").GetDecimal().Should().Be(2_500m);

        StatusOf(await CreateAsync(db, maker)).Should().Be(201);
        var resent = await ReconcileAsync(db, maker);

        resent.GetProperty("deltas").GetProperty("postedMinusExported").GetDecimal()
            .Should().Be(0m, "the re-sent loan journal is counted once, through the Supplementary file only");
        Blockers(resent).Should().NotContain(new[] { "posted_export_delta", "gl_rows_posted_since_last_export" });
    }

    // ── Item 7: legal entities, single-entity formats, required ERP settings ─────────────────────

    [Fact]
    public void ThePerCompanyGate_GroupsByLegalEntity_NotByTheCodeString()
    {
        var entryA = Guid.NewGuid();
        var entryB = Guid.NewGuid();
        var lines = new List<JournalExportLine>
        {
            Line(1, entryA, "DR", "5000", CompanyA, "SA01"),
            Line(2, entryB, "CR", "2100", CompanyB, "SA01"),
        };
        var source = new List<FinanceGlEntry>
        {
            new() { Id = entryA, DebitAccount = "5000 - Salaries", CreditAccount = "", Amount = 100m, Currency = "SAR", Period = Period },
            new() { Id = entryB, DebitAccount = "", CreditAccount = "2100 - Payable", Amount = 100m, Currency = "SAR", Period = Period },
        };

        var tie = JournalExportBuilder.Verify(lines, source);

        tie.Ok.Should().BeFalse("company A is short exactly what company B is long; sharing a code must not offset them");
        tie.Failure.Should().Be(JournalExportErrors.CompanyUnbalanced);
        tie.ByCompany!.Select(c => c.CompanyId).Should().BeEquivalentTo(new Guid?[] { CompanyA, CompanyB });
        new JournalExportDocument
        {
            TenantId = Guid.NewGuid(), TenantName = "", CompanyId = null, CompanyCode = "GROUP", CompanyName = "", Period = Period,
            PayrollRunId = null, Currency = "SAR", GeneratedAtUtc = DateTime.UtcNow, Lines = lines,
        }.PostingDocuments().Should().HaveCount(2, "one ERP document per legal entity, even under one code");
    }

    [Fact]
    public async Task TwoCompaniesResolvingToOneCode_AreRefusedBeforeAnythingIsBuilt()
    {
        var (db, tenantId) = await TenantAsync(companyBRegistration: "SA01");
        SeedJuly(db, tenantId);

        var result = await CreateAsync(db, await CallerAsync(db, tenantId, "Payroll Manager"));

        StatusOf(result).Should().Be(422);
        Body(result).GetProperty("error").GetString().Should().Be(JournalExportErrors.CompanyCodeShared);
        Body(result).GetProperty("message").GetString().Should().Contain("Alpha KSA and Beta Services both use the ERP company code 'SA01'");
        (await db.GlJournalExports.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData("xero-manual-journal")]
    [InlineData("zoho-books-journal")]
    [InlineData("quickbooks-iif")]
    [InlineData("quickbooks-online-journal")]
    [InlineData("d365-fo-general-journal")]
    public async Task SingleEntityFormats_RefuseAMultiCompanyFile_ButExportOneCompany(string format)
    {
        var (db, tenantId) = await TenantAsync();
        SeedJuly(db, tenantId);
        var maker = await CallerAsync(db, tenantId, "Payroll Manager");

        var group = await CreateAsync(db, maker, format);
        StatusOf(group).Should().Be(422);
        Body(group).GetProperty("error").GetString().Should().Be(JournalExportErrors.SingleCompanyFormat);
        Body(group).GetProperty("message").GetString().Should().Be("This format holds one company per file; export each company separately.");
        (await db.GlJournalExports.CountAsync()).Should().Be(0);

        var one = await Ctrl(db, maker).Create(Period, null, CompanyA, "SAR", false, false, format, default);
        StatusOf(one).Should().Be(201);
    }

    [Theory]
    [InlineData("oracle-gl-interface-csv")]
    [InlineData("oracle-fusion-fbdi")]
    public async Task OracleFormats_RequireTheLedgerId_BeforeAnythingIsPersisted(string format)
    {
        var (db, tenantId) = await TenantAsync();
        SeedJuly(db, tenantId);
        var maker = await CallerAsync(db, tenantId, "Payroll Manager");

        var missing = await CreateAsync(db, maker, format);

        StatusOf(missing).Should().Be(422);
        Body(missing).GetProperty("error").GetString().Should().Be(JournalExportErrors.OptionRequired);
        Body(missing).GetProperty("option").GetString().Should().Be("ledgerId");
        Body(missing).GetProperty("message").GetString().Should().StartWith("Enter the Ledger ID for Oracle");
        (await db.GlJournalExports.CountAsync()).Should().Be(0, "a file Journal Import refuses must never be stamped Exported");
        (await db.FinanceGlEntries.AsNoTracking().AnyAsync(e => e.ErpPostingStatus == ErpPostingStatuses.Exported)).Should().BeFalse();

        StatusOf(await Ctrl(db, maker).Create(Period, null, null, "SAR", false, false, format, default,
            options: new() { ["ledgerId"] = "300000046975971" })).Should().Be(201);
    }

    // ── Harness ───────────────────────────────────────────────────────────────────────────────────

    private static async Task<(ZayraDbContext Db, Guid TenantId)> TenantAsync(string companyBRegistration = "1010-555")
    {
        var (db, tenantId) = await NewTenantAsync("gl-erp-integrity");
        db.Companies.AddRange(
            new Company { Id = CompanyA, TenantId = tenantId, LegalNameEn = "Alpha KSA", RegistrationNumber = "7001234567", ErpCompanyCode = "SA01", DefaultCurrency = "SAR" },
            new Company { Id = CompanyB, TenantId = tenantId, LegalNameEn = "Beta Services", RegistrationNumber = companyBRegistration, DefaultCurrency = "SAR" });
        await db.SaveChangesAsync();
        return (db, tenantId);
    }

    /// <summary>July: company A payroll accrual (two one-sided rows), company B loan disbursement (two-sided).</summary>
    private static void SeedJuly(ZayraDbContext db, Guid tenantId)
    {
        var run = Guid.NewGuid();
        db.FinanceGlEntries.AddRange(
            Row(tenantId, CompanyA, "Payroll", run, "Accrual", "5000 - Salaries", "", 10_000m, "July salaries"),
            Row(tenantId, CompanyA, "Payroll", run, "Accrual", "", "2100 - Salaries Payable", 10_000m, "July payable"),
            Row(tenantId, CompanyB, "Loan", Guid.NewGuid(), "Disbursement", "1400 - Employee Loans", "1000 - Bank", 2_500m, "Loan LN-1"));
        db.SaveChanges();
    }

    private static void AddLoan(ZayraDbContext db, Guid tenantId, Guid company, decimal amount)
    {
        db.FinanceGlEntries.Add(Row(tenantId, company, "Loan", Guid.NewGuid(), "Disbursement", "1400 - Employee Loans", "1000 - Bank", amount, "Late loan"));
        db.SaveChanges();
    }

    private static FinanceGlEntry Row(Guid tenantId, Guid company, string module, Guid source, string evt, string dr, string cr, decimal amount, string description) => new()
    {
        TenantId = tenantId, CompanyId = company, SourceModule = module, SourceEntityId = source, SourceEntityRef = module + "-REF",
        EventType = evt, DebitAccount = dr, CreditAccount = cr, Amount = amount, Currency = "SAR",
        EntryDate = new DateOnly(2026, 7, 31), Period = Period, Description = description,
    };

    private static JournalExportLine Line(int no, Guid entry, string side, string account, Guid company, string code) =>
        new(no, entry, null, side, account, account, 100m, "SAR", new DateOnly(2026, 7, 31), new DateOnly(2026, 7, 31), Period,
            $"{Period}-Payroll-{entry.ToString("N")[..8]}-Accrual", "x", "Payroll", entry, "ref", "Accrual", company, code, false);

    private static GlJournalExportsController Ctrl(ZayraDbContext db, ClaimsPrincipal caller)
    {
        var exports = new JournalExportService(db, JournalExportFormatterGoldenTests.AllFormatters());
        return Bind(new GlJournalExportsController(db, exports, new PeriodHandoffReconciler(db, exports)), caller);
    }

    private static async Task<IActionResult> CreateAsync(ZayraDbContext db, ClaimsPrincipal maker, string format = "generic-csv", Guid? replacesExportId = null)
    {
        var result = await Ctrl(db, maker).Create(Period, null, null, "SAR", false, false, format, default,
            includePreviouslyConfirmed: replacesExportId is not null, replacesExportId: replacesExportId);
        db.ChangeTracker.Clear();
        return result;
    }

    private static async Task<JsonElement> ReconcileAsync(ZayraDbContext db, ClaimsPrincipal reader)
    {
        db.ChangeTracker.Clear();
        return Body(await Ctrl(db, reader).Reconciliation(Period, null, null, false, default));
    }

    private static List<string?> Blockers(JsonElement rec) =>
        rec.GetProperty("blockers").EnumerateArray().Select(b => b.GetString()).ToList();

    private static async Task<List<string>> JournalRefsAsync(ZayraDbContext db, ClaimsPrincipal reader, Guid exportId)
    {
        db.ChangeTracker.Clear();
        var get = await Ctrl(db, reader).Get(exportId, default);
        return Body(get).GetProperty("journals").EnumerateArray().Select(j => j.GetProperty("journalRef").GetString()!).ToList();
    }

    private static JsonElement Body(IActionResult r) => JsonSerializer.SerializeToElement(((ObjectResult)r).Value);
    private static Guid IdOf(IActionResult r) => Body(r).GetProperty("exportId").GetGuid();
    private static string? Kind(IActionResult r) => Body(r).GetProperty("kind").GetString();
}
