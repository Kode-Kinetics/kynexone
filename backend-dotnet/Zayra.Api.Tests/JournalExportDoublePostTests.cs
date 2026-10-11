using System.Security.Claims;
using System.Text;
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
/// GL → ERP hand-off: the double-post guard, per-company balancing and codes, per-journal ERP evidence, option
/// freezing and byte-stable regeneration — driven through the real controller with seeded role bundles (maker =
/// Payroll Manager, checker = Finance Approver).
///
/// <para>THE P0. July is exported and the ERP confirms it. A loan disbursement then posts into July. Before
/// the fix, re-exporting July selected EVERY July row again, so the second file re-sent the whole payroll the
/// ERP had already booked — a double post that balances perfectly and is invisible until reconciliation.
/// Now the default selection leaves out rows any ERP-confirmed export holds and labels the file Supplementary;
/// re-sending confirmed rows needs an explicit Replacement naming the confirmed export.</para>
/// </summary>
public class JournalExportDoublePostTests
{
    private const string Period = "2026-07";
    private static readonly Guid CompanyA = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a");
    private static readonly Guid CompanyB = Guid.Parse("bbbbbbbb-0000-0000-0000-00000000000b");

    // ── P0: no double post ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReExportAfterConfirmation_CarriesOnlyTheLateRows_AndIsSupplementary()
    {
        var (db, tenantId) = await TenantAsync();
        SeedJuly(db, tenantId);
        var maker = await CallerAsync(db, tenantId, "Payroll Manager");
        var checker = await CallerAsync(db, tenantId, "Finance Approver");

        var full = await CreateAsync(db, maker);
        Kind(full).Should().Be("Full");
        var fullId = IdOf(full);
        StatusOf(await Ctrl(db, checker).Confirm(fullId, new ErpImportConfirmationRequest("SAP-1900001"), default)).Should().Be(200);

        var lateLoan = AddLoan(db, tenantId, CompanyA, 3_000m, "Late loan disbursement");

        var second = await CreateAsync(db, maker);
        StatusOf(second).Should().Be(201);
        Kind(second).Should().Be("Supplementary", "it follows an ERP-confirmed export of the same rows' scope");
        Body(second).GetProperty("previouslyConfirmedExcluded").GetProperty("count").GetInt32().Should().Be(3);

        db.ChangeTracker.Clear();
        var secondLines = await db.GlJournalExportLines.AsNoTracking().Where(l => l.GlJournalExportId == IdOf(second)).ToListAsync();
        secondLines.Select(l => l.FinanceGlEntryId).Distinct().Should().Equal(new[] { lateLoan },
            "only the row the ERP has not booked may be sent — the confirmed July payroll must never be re-sent");
        (await db.GlJournalExports.AsNoTracking().SingleAsync(x => x.Id == fullId)).Status
            .Should().Be(GlJournalExportStatuses.Confirmed, "a confirmed export is the ERP's evidence and is never superseded");
    }

    [Fact]
    public async Task ExportAndConfirmation_RecordTheSignedInPersonsName_FromTheTokensNameClaim()
    {
        // Found live: every export read "Exported by Unknown". Real tokens carry the display name as
        // "name"; only test principals carried ClaimTypes.Name, so the controller never found it.
        static ClaimsPrincipal AsIssued(ClaimsPrincipal seeded, string displayName) => new(new ClaimsIdentity(
            seeded.Claims.Where(c => c.Type != ClaimTypes.Name && c.Type != "FullName").Append(new Claim("name", displayName)), "Test"));
        var (db, tenantId) = await TenantAsync();
        SeedJuly(db, tenantId);
        var maker = AsIssued(await CallerAsync(db, tenantId, "Payroll Manager"), "Huda Maker");
        var checker = AsIssued(await CallerAsync(db, tenantId, "Finance Approver"), "Faisal Checker");

        var id = IdOf(await CreateAsync(db, maker));
        StatusOf(await Ctrl(db, checker).Confirm(id, new ErpImportConfirmationRequest("SAP-77"), default)).Should().Be(200);

        var saved = await db.GlJournalExports.AsNoTracking().SingleAsync(x => x.Id == id);
        saved.ExportedByName.Should().Be("Huda Maker");
        saved.ConfirmedByName.Should().Be("Faisal Checker");
    }

    [Fact]
    public async Task ASecondExportOfTheSameRows_InAnotherFormat_CannotAlsoBeConfirmed()
    {
        // Found live: a generic CSV and an SAP file of the same month were both "Waiting for ERP result"
        // (only a same-format export supersedes), both were confirmed with different document numbers,
        // and the ledger rows silently took the second number. That is a recorded double post.
        var (db, tenantId) = await TenantAsync();
        SeedJuly(db, tenantId);
        var maker = await CallerAsync(db, tenantId, "Payroll Manager");
        var checker = await CallerAsync(db, tenantId, "Finance Approver");
        var csv = IdOf(await CreateAsync(db, maker));
        var sapId = IdOf(await Ctrl(db, maker).Create(Period, null, null, "SAR", false, false, "sap-s4-gje", default));
        db.ChangeTracker.Clear();

        StatusOf(await Ctrl(db, checker).Confirm(sapId, new ErpImportConfirmationRequest("SAP-100"), default)).Should().Be(200);
        var second = await Ctrl(db, checker).Confirm(csv, new ErpImportConfirmationRequest("CSV-200"), default);

        StatusOf(second).Should().Be(409);
        Body(second).GetProperty("error").GetString().Should().Be("rows_already_confirmed");
        db.ChangeTracker.Clear();
        (await db.FinanceGlEntries.AsNoTracking().Where(e => e.TenantId == tenantId && e.ErpDocumentNumber != null)
            .Select(e => e.ErpDocumentNumber).Distinct().ToListAsync()).Should().Equal(new[] { "SAP-100" });
        (await db.GlJournalExports.AsNoTracking().SingleAsync(x => x.Id == csv)).Status.Should().NotBe(GlJournalExportStatuses.Confirmed);

        // The per-journal path refuses the same way.
        var journalRef = Body(await Ctrl(db, checker).Get(csv, default)).GetProperty("journals")[0].GetProperty("journalRef").GetString();
        var perJournal = await Ctrl(db, checker).ConfirmJournals(csv,
            new ErpJournalConfirmationRequest(new List<ErpJournalConfirmationItem> { new(journalRef, "CSV-200", null) }), default);
        StatusOf(perJournal).Should().Be(409);
    }

    [Fact]
    public async Task ReExportWithNothingNew_IsRefused_WithTheDoublePostMessage()
    {
        var (db, tenantId) = await TenantAsync();
        SeedJuly(db, tenantId);
        var maker = await CallerAsync(db, tenantId, "Payroll Manager");
        var full = IdOf(await CreateAsync(db, maker));
        await Ctrl(db, await CallerAsync(db, tenantId, "Finance Approver")).Confirm(full, new ErpImportConfirmationRequest("SAP-1"), default);

        var again = await CreateAsync(db, maker);

        StatusOf(again).Should().Be(422);
        Body(again).GetProperty("error").GetString().Should().Be(JournalExportErrors.AlreadyPosted);
        Body(again).GetProperty("message").GetString().Should().Contain("would post them twice");
        (await db.GlJournalExports.CountAsync()).Should().Be(1, "a refusal writes no artifact");
    }

    [Fact]
    public async Task ResendingConfirmedRows_RequiresAReplacementThatNamesTheConfirmedExport()
    {
        var (db, tenantId) = await TenantAsync();
        SeedJuly(db, tenantId);
        var maker = await CallerAsync(db, tenantId, "Payroll Manager");
        var full = IdOf(await CreateAsync(db, maker));
        await Ctrl(db, await CallerAsync(db, tenantId, "Finance Approver")).Confirm(full, new ErpImportConfirmationRequest("SAP-1"), default);

        var unnamed = await Ctrl(db, maker).Create(Period, null, null, "SAR", false, false, "generic-csv", default,
            includePreviouslyConfirmed: true);
        StatusOf(unnamed).Should().Be(422);
        Body(unnamed).GetProperty("error").GetString().Should().Be(JournalExportErrors.ReplacementTargetRequired);

        var replacement = await Ctrl(db, maker).Create(Period, null, null, "SAR", false, false, "generic-csv", default,
            includePreviouslyConfirmed: true, replacesExportId: full);
        StatusOf(replacement).Should().Be(201);
        Kind(replacement).Should().Be("Replacement");
        db.ChangeTracker.Clear();
        var row = await db.GlJournalExports.AsNoTracking().SingleAsync(x => x.Id == IdOf(replacement));
        row.ReplacesExportId.Should().Be(full);
        row.EntryCount.Should().Be(3, "a replacement re-sends the rows of the confirmed export it names");
    }

    // ── Per company ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GroupExport_StampsEachLineWithItsCompanysErpCode_AndExternalAccountCode()
    {
        var (db, tenantId) = await TenantAsync();
        SeedJuly(db, tenantId);
        db.GlAccounts.Add(new GlAccount { TenantId = tenantId, CompanyId = CompanyA, Code = "5000", Name = "Salaries", ExternalCode = "6100-SAL" });
        await db.SaveChangesAsync();

        var created = await CreateAsync(db, await CallerAsync(db, tenantId, "Payroll Manager"));
        StatusOf(created).Should().Be(201);
        var byCompany = Body(created).GetProperty("byCompany").EnumerateArray().ToList();
        byCompany.Select(c => c.GetProperty("companyCode").GetString()).Should().BeEquivalentTo(new[] { "SA01", "1010-555" });
        byCompany.Should().OnlyContain(c => c.GetProperty("balanced").GetBoolean());

        db.ChangeTracker.Clear();
        var lines = await db.GlJournalExportLines.AsNoTracking().Where(l => l.GlJournalExportId == IdOf(created)).ToListAsync();
        lines.Where(l => l.SourceCompanyId == CompanyA).Should().OnlyContain(l => l.CompanyCode == "SA01", "erp_company_code wins");
        lines.Where(l => l.SourceCompanyId == CompanyB).Should().OnlyContain(l => l.CompanyCode == "1010-555", "CR number is the fallback");
        lines.Single(l => l.AccountCode == "5000").ExternalAccountCode.Should().Be("6100-SAL");

        var csv = await DownloadTextAsync(db, tenantId, IdOf(created));
        csv.Should().Contain(",6100-SAL,").And.NotContain(",GROUP,");
    }

    [Fact]
    public async Task AFileThatBalancesOnlyInTotal_IsRefused_WithAPerCompanyBreakdown()
    {
        var (db, tenantId) = await TenantAsync();
        // Company A carries only the debit of a journal whose credit landed on company B: in total DR == CR,
        // but each company's ERP ledger would receive an unbalanced journal.
        var run = Guid.NewGuid();
        db.FinanceGlEntries.AddRange(
            Row(tenantId, CompanyA, "Payroll", run, "Accrual", "5000 - Salaries", "", 800m, "Salaries"),
            Row(tenantId, CompanyB, "Payroll", run, "Accrual", "", "2100 - Salaries Payable", 800m, "Payable"));
        await db.SaveChangesAsync();

        var result = await CreateAsync(db, await CallerAsync(db, tenantId, "Payroll Manager"));

        StatusOf(result).Should().Be(422);
        Body(result).GetProperty("error").GetString().Should().Be(JournalExportErrors.CompanyUnbalanced);
        var byCompany = Body(result).GetProperty("byCompany").EnumerateArray().ToList();
        byCompany.Should().HaveCount(2).And.OnlyContain(c => !c.GetProperty("balanced").GetBoolean());
        (await db.GlJournalExports.CountAsync()).Should().Be(0);
    }

    // ── Per-journal ERP evidence ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task JournalsAreConfirmedAndRejectedIndividually_AndOnlyTheRejectedAreOwedAgain()
    {
        var (db, tenantId) = await TenantAsync();
        SeedJuly(db, tenantId);
        var maker = await CallerAsync(db, tenantId, "Payroll Manager");
        var checker = await CallerAsync(db, tenantId, "Finance Approver");
        var exportId = IdOf(await CreateAsync(db, maker));
        var journals = await JournalRefsAsync(db, checker, exportId);
        journals.Should().HaveCount(2, "one payroll journal for company A, one loan journal for company B");
        var payroll = journals.Single(j => j.Contains("Payroll"));
        var loan = journals.Single(j => j.Contains("Loan"));

        StatusOf(await Ctrl(db, maker).ConfirmJournals(exportId, new ErpJournalConfirmationRequest(
            new() { new(payroll, "SAP-100") }), default)).Should().Be(403, "the maker role does not hold the checker key");

        var confirm = await Ctrl(db, checker).ConfirmJournals(exportId, new ErpJournalConfirmationRequest(
            new() { new(payroll, "SAP-100", new DateTime(2026, 8, 4, 0, 0, 0, DateTimeKind.Utc)) }), default);
        StatusOf(confirm).Should().Be(200);
        Body(confirm).GetProperty("status").GetString().Should().Be(GlJournalExportStatuses.Exported, "the loan journal is still pending");

        StatusOf(await Ctrl(db, checker).ConfirmJournals(exportId, new ErpJournalConfirmationRequest(
            new() { new(payroll, "SAP-100") }), default)).Should().Be(200, "same number again is an idempotent no-op");
        StatusOf(await Ctrl(db, checker).ConfirmJournals(exportId, new ErpJournalConfirmationRequest(
            new() { new(payroll, "SAP-999") }), default)).Should().Be(409, "two numbers for one journal means a double import");

        var reject = await Ctrl(db, checker).RejectJournals(exportId, new ErpJournalRejectionRequest(
            new() { new(loan, "Cost centre missing in company 1010") }), default);
        StatusOf(reject).Should().Be(200);
        Body(reject).GetProperty("status").GetString().Should().Be(GlJournalExportStatuses.PartiallyConfirmed);

        StatusOf(await Ctrl(db, checker).Confirm(exportId, new ErpImportConfirmationRequest("SAP-ALL"), default))
            .Should().Be(409, "a whole-file decision cannot overwrite per-journal evidence");

        db.ChangeTracker.Clear();
        var entries = await db.FinanceGlEntries.AsNoTracking().ToListAsync();
        entries.Where(e => e.SourceModule == "Payroll").Should().OnlyContain(e => e.ErpPostingStatus == ErpPostingStatuses.Posted && e.ErpDocumentNumber == "SAP-100");
        entries.Where(e => e.SourceModule == "Loan").Should().OnlyContain(e => e.ErpPostingStatus == ErpPostingStatuses.Rejected);

        var next = await CreateAsync(db, maker);
        StatusOf(next).Should().Be(201);
        Kind(next).Should().Be("Supplementary");
        db.ChangeTracker.Clear();
        (await db.GlJournalExportLines.AsNoTracking().Where(l => l.GlJournalExportId == IdOf(next)).Select(l => l.SourceModule).Distinct().ToListAsync())
            .Should().Equal(new[] { "Loan" }, "the confirmed payroll journal is in the ERP; only the rejected loan journal is owed");
    }

    [Fact]
    public async Task TheAdminWhoExported_CannotConfirmOrRejectItsJournals()
    {
        var (db, tenantId) = await TenantAsync();
        SeedJuly(db, tenantId);
        var admin = await CallerAsync(db, tenantId, "Admin");
        var exportId = IdOf(await CreateAsync(db, admin));
        var journal = (await JournalRefsAsync(db, admin, exportId))[0];

        var confirm = await Ctrl(db, admin).ConfirmJournals(exportId, new ErpJournalConfirmationRequest(new() { new(journal, "SAP-1") }), default);
        var reject = await Ctrl(db, admin).RejectJournals(exportId, new ErpJournalRejectionRequest(new() { new(journal, "no") }), default);

        StatusOf(confirm).Should().Be(409);
        Body(confirm).GetProperty("error").GetString().Should().Be("segregation_of_duties");
        StatusOf(reject).Should().Be(409);
        db.ChangeTracker.Clear();
        (await db.GlJournalExports.AsNoTracking().SingleAsync()).JournalConfirmationsJson.Should().Be("[]");
    }

    // ── Byte-stable regeneration + options ────────────────────────────────────────────────────────

    [Fact]
    public async Task EveryFormat_DownloadsTheExactBytesItRecorded()
    {
        var (db, tenantId) = await TenantAsync();
        SeedJuly(db, tenantId);
        var maker = await CallerAsync(db, tenantId, "Payroll Manager");
        foreach (var f in JournalExportFormatterGoldenTests.AllFormatters())
        {
            // One-company formats are exported per company; a required ERP setting (Oracle's ledger id) is supplied.
            var required = f.Options.Where(o => o.Required).ToDictionary(o => o.Key, _ => "300000046975971");
            var created = await Ctrl(db, maker).Create(Period, null, f.SingleEntity ? CompanyA : null, "SAR", false, false, f.Key, default,
                options: required);
            StatusOf(created).Should().Be(201, f.Key);
            var hash = Body(created).GetProperty("fileHash").GetString();
            db.ChangeTracker.Clear();
            var file = (FileContentResult)await Ctrl(db, maker).Download(IdOf(created), default);
            JournalExportService.Sha256(file.FileContents).Should().Be(hash, $"{f.Key} must regenerate byte-for-byte");
        }
    }

    [Fact]
    public async Task FormatOptions_AreValidated_FrozenWithTheExport_AndRegenerated()
    {
        var (db, tenantId) = await TenantAsync();
        SeedJuly(db, tenantId);
        var maker = await CallerAsync(db, tenantId, "Payroll Manager");

        var bad = await Ctrl(db, maker).Create(Period, null, null, "SAR", false, false, "oracle-gl-interface-csv", default,
            options: new() { ["ledgerId"] = "ABC" });
        StatusOf(bad).Should().Be(400);
        var unknown = await Ctrl(db, maker).Create(Period, null, null, "SAR", false, false, "oracle-gl-interface-csv", default,
            options: new() { ["password"] = "x" });
        StatusOf(unknown).Should().Be(400);

        var ok = await Ctrl(db, maker).Create(Period, null, null, "SAR", false, false, "oracle-gl-interface-csv", default,
            options: new() { ["ledgerId"] = "300000046975971", ["segments"] = "{company}|000|{account}" });
        StatusOf(ok).Should().Be(201);
        var text = await DownloadTextAsync(db, tenantId, IdOf(ok));
        text.Should().Contain("NEW,300000046975971,").And.Contain("KynexOne Payroll").And.NotContain("ZAYRA");
        text.Split('\n')[0].Should().Contain("SEGMENT3");
        text.Should().Contain(",SA01,000,5000,");
    }

    [Fact]
    public async Task AVersion1Export_StillRegeneratesToItsRecordedHash()
    {
        var (db, tenantId) = await TenantAsync();
        SeedJuly(db, tenantId);
        // An export taken before this change: v1 formatter, no per-line company code, format_version 0.
        var entries = await db.FinanceGlEntries.AsNoTracking().ToListAsync();
        var lines = JournalExportBuilder.Expand(entries, "GROUP");
        var exportId = Guid.NewGuid();
        var export = new GlJournalExport
        {
            Id = exportId, TenantId = tenantId, CompanyCode = "GROUP", Period = Period, FormatKey = "generic-csv",
            Currency = "SAR", ExportedAtUtc = new DateTime(2026, 8, 3, 9, 0, 0, DateTimeKind.Utc), FormatVersion = 0,
            LineCount = lines.Count, Status = GlJournalExportStatuses.Exported,
        };
        var doc = JournalExportService.ToDocument(export, lines.Select(l => new GlJournalExportLine
        {
            TenantId = tenantId, GlJournalExportId = exportId, FinanceGlEntryId = l.EntryId, LineNo = l.LineNo, Side = l.Side,
            AccountCode = l.AccountCode, AccountName = l.AccountName, Amount = l.Amount, Currency = l.Currency,
            AccountingDate = l.AccountingDate, SystemPostedDate = l.SystemPostedDate, Period = l.Period, JournalRef = l.JournalRef,
            Description = l.Description, SourceModule = l.SourceModule, SourceEntityId = l.SourceEntityId,
            SourceEntityRef = l.SourceEntityRef, EventType = l.EventType, IsReversed = l.IsReversed,
        }).ToList());
        var v1Bytes = new GenericCsvJournalFormatterV1().Format(doc);
        export.FileHash = JournalExportService.Sha256(v1Bytes);
        db.GlJournalExports.Add(export);
        db.GlJournalExportLines.AddRange(doc.Lines.Select(l => new GlJournalExportLine
        {
            TenantId = tenantId, GlJournalExportId = exportId, FinanceGlEntryId = l.EntryId, LineNo = l.LineNo, Side = l.Side,
            AccountCode = l.AccountCode, AccountName = l.AccountName, Amount = l.Amount, Currency = l.Currency,
            AccountingDate = l.AccountingDate, SystemPostedDate = l.SystemPostedDate, Period = l.Period, JournalRef = l.JournalRef,
            Description = l.Description, SourceModule = l.SourceModule, SourceEntityId = l.SourceEntityId,
            SourceEntityRef = l.SourceEntityRef, EventType = l.EventType, IsReversed = l.IsReversed,
        }));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var file = await Ctrl(db, await CallerAsync(db, tenantId, "Payroll Manager")).Download(exportId, default);

        file.Should().BeOfType<FileContentResult>("a v1 artifact must not be reported as drifted after the upgrade");
        ((FileContentResult)file).FileContents.Should().Equal(v1Bytes);
    }

    // ── Harness ───────────────────────────────────────────────────────────────────────────────────

    private static async Task<(ZayraDbContext Db, Guid TenantId)> TenantAsync()
    {
        var (db, tenantId) = await NewTenantAsync("gl-erp");
        db.Companies.AddRange(
            new Company { Id = CompanyA, TenantId = tenantId, LegalNameEn = "Alpha KSA", RegistrationNumber = "7001234567", ErpCompanyCode = "SA01", DefaultCurrency = "SAR" },
            new Company { Id = CompanyB, TenantId = tenantId, LegalNameEn = "Beta Services", RegistrationNumber = "1010-555", DefaultCurrency = "SAR" });
        await db.SaveChangesAsync();
        return (db, tenantId);
    }

    /// <summary>July: company A payroll accrual (two one-sided rows), company B loan disbursement (two-sided).</summary>
    private static void SeedJuly(ZayraDbContext db, Guid tenantId)
    {
        var run = Guid.NewGuid();
        db.FinanceGlEntries.AddRange(
            Row(tenantId, CompanyA, "Payroll", run, "Accrual", "5000 - Salaries", "", 10_000m, "July salaries"),
            Row(tenantId, CompanyA, "Payroll", run, "Accrual", "", "2100 - Salaries Payable", 10_000m, "رواتب يوليو"),
            Row(tenantId, CompanyB, "Loan", Guid.NewGuid(), "Disbursement", "1400 - Employee Loans", "1000 - Bank", 2_500m, "Loan LN-1"));
        db.SaveChanges();
    }

    private static Guid AddLoan(ZayraDbContext db, Guid tenantId, Guid company, decimal amount, string description)
    {
        var row = Row(tenantId, company, "Loan", Guid.NewGuid(), "Disbursement", "1400 - Employee Loans", "1000 - Bank", amount, description);
        db.FinanceGlEntries.Add(row);
        db.SaveChanges();
        return row.Id;
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

    private static async Task<IActionResult> CreateAsync(ZayraDbContext db, ClaimsPrincipal maker)
    {
        var result = await Ctrl(db, maker).Create(Period, null, null, "SAR", false, false, "generic-csv", default);
        db.ChangeTracker.Clear();
        return result;
    }

    private static async Task<List<string>> JournalRefsAsync(ZayraDbContext db, ClaimsPrincipal reader, Guid exportId)
    {
        var get = await Ctrl(db, reader).Get(exportId, default);
        return Body(get).GetProperty("journals").EnumerateArray().Select(j => j.GetProperty("journalRef").GetString()!).ToList();
    }

    private static async Task<string> DownloadTextAsync(ZayraDbContext db, Guid tenantId, Guid exportId)
    {
        db.ChangeTracker.Clear();
        var file = (FileContentResult)await Ctrl(db, await CallerAsync(db, tenantId, "Payroll Manager")).Download(exportId, default);
        return Encoding.UTF8.GetString(file.FileContents);
    }

    private static JsonElement Body(IActionResult r) => JsonSerializer.SerializeToElement(((ObjectResult)r).Value);
    private static Guid IdOf(IActionResult r) => Body(r).GetProperty("exportId").GetGuid();
    private static string? Kind(IActionResult r) => Body(r).GetProperty("kind").GetString();
}
