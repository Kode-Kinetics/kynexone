using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>Maker-checker, scope, generate/download and preview for custom bank templates (EF InMemory).</summary>
public class CustomBankTemplateServiceTests
{
    private static ZayraDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static readonly Guid Maker = Guid.NewGuid();
    private static readonly Guid Checker = Guid.NewGuid();

    private static CustomBankTemplateDefinition Layout(string name = "Bank sample layout", Action<CustomBankTemplateDefinition>? tweak = null)
    {
        var d = CustomBankTemplates.Clone(CustomBankTemplateStarters.Find(CustomBankTemplateStarters.GenericCsv)!.Definition);
        d.Name = name;
        tweak?.Invoke(d);
        return d;
    }

    private sealed record World(ZayraDbContext Db, Guid Tenant, Guid Company, Guid Batch, SaudiBankExportService Svc, CustomBankTemplateService Tpl);

    /// <summary>A Locked run whose two employees both have valid Saudi IBANs (the shared fixture's second
    /// employee is an ANB internal account, which a custom IBAN-only file rightly refuses).</summary>
    private static async Task<World> ArrangeAsync(int month = 9)
    {
        var db = NewDb();
        var tenant = Guid.NewGuid();
        var company = await SaudiBankExportTestData.SeedCompanyAsync(db, tenant);
        var batch = await SaudiBankExportTestData.SeedBatchAsync(db, tenant, company, month);
        await MakeSecondEmployeeIbanAsync(db, tenant, batch);
        var svc = new SaudiBankExportService(db, new SaudiBankExportTestData.FakeAddresses());
        var tpl = new CustomBankTemplateService(db);
        return new World(db, tenant, company, batch, svc, tpl);
    }

    private static async Task MakeSecondEmployeeIbanAsync(ZayraDbContext db, Guid tenant, Guid batch)
    {
        var rec = await db.PayrollPaymentRecords.Where(r => r.PaymentBatchId == batch && r.Amount == 5250.50m).SingleAsync();
        rec.Iban = CustomBankFileGeneratorTests.IbanB;
        var profile = await db.EmployeePayrollProfiles.SingleAsync(p => p.EmployeeId == rec.EmployeeId);
        profile.Iban = CustomBankFileGeneratorTests.IbanB; profile.AccountNumber = string.Empty;
        var emp = await db.Employees.SingleAsync(e => e.Id == rec.EmployeeId);
        emp.Nationality = "Egyptian";
        foreach (var e in db.Employees.Where(e => e.TenantId == tenant)) e.ArabicName = "عمر";
        await db.SaveChangesAsync();
    }

    private static async Task<CustomBankTemplateDto> ApprovedAsync(World w, CustomBankTemplateDefinition? def = null)
    {
        var proposed = await w.Tpl.ProposeAsync(w.Tenant, w.Company, Maker, null, new CustomBankTemplateProposal { Definition = def ?? Layout() }, default);
        proposed.Outcome.Should().Be(CustomBankOutcome.Ok, proposed.Message);
        var t = proposed.Value!;
        var approved = await w.Tpl.DecideAsync(w.Tenant, w.Company, Checker, t.Id, true,
            new CustomBankTemplateDecision { Version = 1, DefinitionHash = t.Versions[0].DefinitionHash }, default);
        approved.Outcome.Should().Be(CustomBankOutcome.Ok, approved.Message);
        return approved.Value!;
    }

    private static SaudiBankExportSettingsDto Settings(string formatId) => new()
    {
        FormatId = formatId, MolEstablishmentId = "7001234567", Narrative = "Payroll Sep 2026",
    };

    // ── Maker-checker ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NewTemplate_IsPending_AndCannotBeSelectedOrGenerated()
    {
        var w = await ArrangeAsync();
        var r = await w.Tpl.ProposeAsync(w.Tenant, w.Company, Maker, null, new CustomBankTemplateProposal { Definition = Layout() }, default);
        r.Outcome.Should().Be(CustomBankOutcome.Ok);
        r.Value!.State.Should().Be("PendingApproval");
        r.Value.ActiveVersion.Should().BeNull();

        // It cannot be chosen as the company's format ...
        var save = await w.Svc.SaveSettingsAsync(w.Tenant, w.Company, Maker, Settings(r.Value.FormatId), default);
        save.Outcome.Should().Be(SaudiBankExportOutcome.Invalid);
        save.Validation!.Errors.Should().Contain(e => e.Code == "template_not_approved");

        // ... and even if a stale selection exists, nothing is generated.
        w.Db.SystemSettings.Add(new SystemSetting
        {
            TenantId = w.Tenant, Category = SaudiBankExportService.SettingsCategory, SettingKey = SaudiBankExportService.SettingsKey(w.Company),
            DataType = "json", SettingValue = JsonSerializer.Serialize(Settings(r.Value.FormatId), new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        });
        await w.Db.SaveChangesAsync();
        var gen = await w.Svc.GenerateAsync(w.Tenant, Maker, w.Batch, SaudiBankExportTestData.Request(), default);
        gen.Outcome.Should().Be(SaudiBankExportOutcome.Invalid);
        gen.Validation!.Errors.Should().Contain(e => e.Code == "template_not_approved");
        w.Db.BankTransferFiles.Count().Should().Be(0);
    }

    [Fact]
    public async Task Proposer_CannotApproveOrReject_OwnTemplate()
    {
        var w = await ArrangeAsync();
        var t = (await w.Tpl.ProposeAsync(w.Tenant, w.Company, Maker, null, new CustomBankTemplateProposal { Definition = Layout() }, default)).Value!;
        var body = new CustomBankTemplateDecision { Version = 1, DefinitionHash = t.Versions[0].DefinitionHash, Note = "no" };

        var approve = await w.Tpl.DecideAsync(w.Tenant, w.Company, Maker, t.Id, true, body, default);
        var reject = await w.Tpl.DecideAsync(w.Tenant, w.Company, Maker, t.Id, false, body, default);

        approve.Outcome.Should().Be(CustomBankOutcome.Forbidden);
        approve.Error.Should().Be("proposer_cannot_decide");
        reject.Outcome.Should().Be(CustomBankOutcome.Forbidden);
        (await w.Tpl.GetAsync(w.Tenant, w.Company, t.Id, Maker, default))!.State.Should().Be("PendingApproval");
    }

    [Fact]
    public async Task DifferentPerson_Approves_AndTheTemplateBecomesUsable()
    {
        var w = await ArrangeAsync();
        var t = await ApprovedAsync(w);
        t.State.Should().Be("Active");
        t.ActiveVersion.Should().Be(1);
        t.AcceptanceStatus.Should().Be("customer-configured-layout; bank-acceptance-not-verified");

        (await w.Svc.SaveSettingsAsync(w.Tenant, w.Company, Maker, Settings(t.FormatId), default)).Outcome.Should().Be(SaudiBankExportOutcome.Ok);
        w.Db.PayrollAuditLogs.Select(a => a.Action).Should().Contain(new[] { "payroll.bank_template.proposed", "payroll.bank_template.approved" });
    }

    [Fact]
    public async Task Approval_IsBoundToTheHashTheCheckerReviewed()
    {
        var w = await ArrangeAsync();
        var t = (await w.Tpl.ProposeAsync(w.Tenant, w.Company, Maker, null, new CustomBankTemplateProposal { Definition = Layout() }, default)).Value!;
        // The maker edits after the checker opened version 1: the pending version is now 2.
        var edited = (await w.Tpl.ProposeAsync(w.Tenant, w.Company, Maker, t.Id, new CustomBankTemplateProposal { Definition = Layout(tweak: d => d.Delimiter = ";") }, default)).Value!;

        var stale = await w.Tpl.DecideAsync(w.Tenant, w.Company, Checker, t.Id, true,
            new CustomBankTemplateDecision { Version = 1, DefinitionHash = t.Versions[0].DefinitionHash }, default);
        stale.Outcome.Should().Be(CustomBankOutcome.Conflict);
        stale.Error.Should().Be("not_pending", "version 1 was superseded");

        var wrongHash = await w.Tpl.DecideAsync(w.Tenant, w.Company, Checker, t.Id, true,
            new CustomBankTemplateDecision { Version = 2, DefinitionHash = t.Versions[0].DefinitionHash }, default);
        wrongHash.Error.Should().Be("definition_changed");

        (await w.Tpl.DecideAsync(w.Tenant, w.Company, Checker, t.Id, true,
            new CustomBankTemplateDecision { Version = 2, DefinitionHash = edited.Versions[0].DefinitionHash }, default))
            .Outcome.Should().Be(CustomBankOutcome.Ok);
    }

    [Fact]
    public async Task EditingAnApprovedTemplate_AddsAPendingVersion_AndThePreviousApprovedOneStaysInUseUntilApproved()
    {
        var w = await ArrangeAsync();
        var t = await ApprovedAsync(w);
        await w.Svc.SaveSettingsAsync(w.Tenant, w.Company, Maker, Settings(t.FormatId), default);

        var edit = await w.Tpl.ProposeAsync(w.Tenant, w.Company, Maker, t.Id,
            new CustomBankTemplateProposal { Definition = Layout(tweak: d => d.Delimiter = "|") }, default);

        edit.Value!.State.Should().Be("ActivePendingChange");
        edit.Value.ActiveVersion.Should().Be(1);
        edit.Value.PendingVersion.Should().Be(2);
        edit.Value.Versions.Single(v => v.Version == 1).Status.Should().Be("Approved", "an approved version is never edited");
        edit.Value.Versions.Single(v => v.Version == 1).Definition.Delimiter.Should().Be(",");

        // Generation keeps using the approved version 1 (comma), never the unapproved version 2.
        var gen = await w.Svc.GenerateAsync(w.Tenant, Maker, w.Batch, SaudiBankExportTestData.Request(), default);
        gen.Outcome.Should().Be(SaudiBankExportOutcome.Ok, string.Join("; ", gen.Validation?.Errors.Select(e => e.Code) ?? Array.Empty<string>()));
        var dl = await w.Svc.DownloadAsync(w.Tenant, Maker, w.Batch, default);
        Encoding.UTF8.GetString(dl.Value!.ZipBytes).Split("\r\n")[1].Should().Contain(",").And.NotContain("|");

        // Approval retires version 1 and activates version 2.
        var approved = await w.Tpl.DecideAsync(w.Tenant, w.Company, Checker, t.Id, true,
            new CustomBankTemplateDecision { Version = 2, DefinitionHash = edit.Value.Versions[0].DefinitionHash }, default);
        approved.Value!.ActiveVersion.Should().Be(2);
        approved.Value.Versions.Single(v => v.Version == 1).Status.Should().Be("Retired");
    }

    [Fact]
    public async Task Reject_NeedsAReason_AndLeavesNothingActive()
    {
        var w = await ArrangeAsync();
        var t = (await w.Tpl.ProposeAsync(w.Tenant, w.Company, Maker, null, new CustomBankTemplateProposal { Definition = Layout() }, default)).Value!;
        var hash = t.Versions[0].DefinitionHash;

        (await w.Tpl.DecideAsync(w.Tenant, w.Company, Checker, t.Id, false, new CustomBankTemplateDecision { Version = 1, DefinitionHash = hash }, default))
            .Error.Should().Be("reason_required");
        var rejected = await w.Tpl.DecideAsync(w.Tenant, w.Company, Checker, t.Id, false,
            new CustomBankTemplateDecision { Version = 1, DefinitionHash = hash, Note = "Header does not match the bank sample" }, default);

        rejected.Value!.State.Should().Be("Rejected");
        rejected.Value.ActiveVersion.Should().BeNull();
        (await w.Tpl.DecideAsync(w.Tenant, w.Company, Checker, t.Id, true, new CustomBankTemplateDecision { Version = 1, DefinitionHash = hash }, default))
            .Error.Should().Be("not_pending", "a decided version cannot be decided again");
    }

    [Fact]
    public async Task InvalidLayout_IsRefused_AndIdenticalResubmissionIsANoOp()
    {
        var w = await ArrangeAsync();
        var bad = await w.Tpl.ProposeAsync(w.Tenant, w.Company, Maker, null,
            new CustomBankTemplateProposal { Definition = Layout(tweak: d => d.Columns.RemoveAll(c => c.Field == CustomBankFields.BeneficiaryIban)) }, default);
        bad.Outcome.Should().Be(CustomBankOutcome.Invalid);
        bad.Errors.Should().Contain(e => e.Code == "required_column_missing");

        var t = await ApprovedAsync(w);
        var same = await w.Tpl.ProposeAsync(w.Tenant, w.Company, Maker, t.Id, new CustomBankTemplateProposal { Definition = Layout() }, default);
        same.Outcome.Should().Be(CustomBankOutcome.Conflict);
        same.Error.Should().Be("no_change");
    }

    [Fact]
    public async Task Starter_CanBeCloned_ButIsOnlyEverPending_NotCertified()
    {
        var w = await ArrangeAsync();
        var r = await w.Tpl.ProposeAsync(w.Tenant, w.Company, Maker, null, new CustomBankTemplateProposal { StarterId = CustomBankTemplateStarters.GenericCsv }, default);
        r.Value!.State.Should().Be("PendingApproval");
        r.Value.Name.Should().Be("Mudad/bank generic CSV (verify with your bank)");
        r.Value.AcceptanceStatus.Should().Contain("not-verified");
        CustomBankTemplateStarters.All.Should().OnlyContain(s => s.VerifyWithBank && s.AcceptanceStatus.Contains("not-verified"));
        CustomBankTemplateStarters.All.Select(s => s.Id).Should().Equal(CustomBankTemplateStarters.GenericCsv);   // no invented per-bank formats
        (await w.Tpl.ProposeAsync(w.Tenant, w.Company, Maker, null, new CustomBankTemplateProposal { StarterId = "al-rajhi" }, default)).Outcome
            .Should().Be(CustomBankOutcome.Invalid);
    }

    // ── Scope ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Template_OfAnotherCompanyOrTenant_IsNotFound_ForReadEditAndDecide()
    {
        var w = await ArrangeAsync();
        var t = (await w.Tpl.ProposeAsync(w.Tenant, w.Company, Maker, null, new CustomBankTemplateProposal { Definition = Layout() }, default)).Value!;
        var otherCompany = await SaudiBankExportTestData.SeedCompanyAsync(w.Db, w.Tenant);
        var otherTenant = Guid.NewGuid();
        var body = new CustomBankTemplateDecision { Version = 1, DefinitionHash = t.Versions[0].DefinitionHash };

        foreach (var (tenant, company) in new[] { (w.Tenant, otherCompany), (otherTenant, w.Company), (otherTenant, otherCompany) })
        {
            (await w.Tpl.GetAsync(tenant, company, t.Id, Checker, default)).Should().BeNull();
            (await w.Tpl.ListAsync(tenant, company, Checker, default)).Should().BeEmpty();
            (await w.Tpl.ProposeAsync(tenant, company, Maker, t.Id, new CustomBankTemplateProposal { Definition = Layout(tweak: d => d.Delimiter = ";") }, default))
                .Outcome.Should().Be(CustomBankOutcome.NotFound);
            (await w.Tpl.DecideAsync(tenant, company, Checker, t.Id, true, body, default)).Outcome.Should().Be(CustomBankOutcome.NotFound);
        }
        (await w.Tpl.GetAsync(w.Tenant, w.Company, t.Id, Checker, default)).Should().NotBeNull();
    }

    [Fact]
    public async Task AnotherCompanysApprovedTemplate_CannotBeSelectedOrUsed()
    {
        var w = await ArrangeAsync();
        var otherCompany = await SaudiBankExportTestData.SeedCompanyAsync(w.Db, w.Tenant);
        var foreign = new CustomBankTemplateService(w.Db);
        var proposed = (await foreign.ProposeAsync(w.Tenant, otherCompany, Maker, null, new CustomBankTemplateProposal { Definition = Layout() }, default)).Value!;
        await foreign.DecideAsync(w.Tenant, otherCompany, Checker, proposed.Id, true,
            new CustomBankTemplateDecision { Version = 1, DefinitionHash = proposed.Versions[0].DefinitionHash }, default);

        var save = await w.Svc.SaveSettingsAsync(w.Tenant, w.Company, Maker, Settings(proposed.FormatId), default);
        save.Outcome.Should().Be(SaudiBankExportOutcome.Invalid);
        save.Validation!.Errors.Should().Contain(e => e.Code == "format_unsupported");
    }

    // ── Generate / download / ledger / preview ─────────────────────────────────────────────────────

    private static async Task<(World W, CustomBankTemplateDto T)> ReadyAsync(int month = 9)
    {
        var w = await ArrangeAsync(month);
        var t = await ApprovedAsync(w);
        (await w.Svc.SaveSettingsAsync(w.Tenant, w.Company, Maker, Settings(t.FormatId), default)).Outcome.Should().Be(SaudiBankExportOutcome.Ok);
        return (w, t);
    }

    [Fact]
    public async Task Generate_StoresOneHashedFileThroughTheWpsFileStore_AndDownloadReturnsTheExactBytes()
    {
        var (w, t) = await ReadyAsync();

        var v = (await w.Svc.ValidateAsync(w.Tenant, w.Batch, SaudiBankExportTestData.Request(), default)).Value!;
        v.CanExport.Should().BeTrue(string.Join("; ", v.Errors.Select(e => e.Code + ":" + e.Message)));
        v.FormatId.Should().Be(t.FormatId);

        var gen = await w.Svc.GenerateAsync(w.Tenant, Maker, w.Batch, SaudiBankExportTestData.Request(), default);
        gen.Outcome.Should().Be(SaudiBankExportOutcome.Ok);
        var g = gen.Value!;
        g.FormatId.Should().Be(t.FormatId);
        g.Files.Should().ContainSingle().Which.Name.Should().Be("salary-2026092601.csv");
        g.EmployeeCount.Should().Be(2);
        g.TotalAmount.Should().Be(11550.50m);

        w.Db.BankTransferFiles.Count(f => f.FileName.StartsWith(GeneratedWpsFileStore.Prefix)).Should().Be(1);
        var dl = await w.Svc.DownloadAsync(w.Tenant, Maker, w.Batch, default);
        dl.Outcome.Should().Be(SaudiBankExportOutcome.Ok);
        dl.Value!.ContentType.Should().Be("application/octet-stream");
        GeneratedWpsFileStore.Sha256Hex(dl.Value.ZipBytes).Should().Be(g.Files[0].Sha256);
        var text = Encoding.UTF8.GetString(dl.Value.ZipBytes);
        text.Should().StartWith("Employee ID,Employee name,Bank code,IBAN,Basic salary,Housing allowance,Other earnings,Deductions,Net pay,Currency\r\n");
        text.Should().Contain($"1012345678,Omar Test,RJHI,{CustomBankFileGeneratorTests.IbanA},5000.00,1250.00,500.00,450.00,6300.00,SAR");
        text.Should().Contain($"2012345678,Sara Test,ARNB,{CustomBankFileGeneratorTests.IbanB},4000.50,1000.00,250.00,0.00,5250.50,SAR");

        // Which template version produced it is recorded beside the hash, in the audit and in the stored row.
        var audit = w.Db.PayrollAuditLogs.Where(a => a.Action == "payroll.bank_export.generated").Select(a => a.MetadataJson).Single();
        audit.Should().Contain("\"templateVersion\":1").And.Contain(g.Files[0].Sha256).And.Contain(t.Versions[0].DefinitionHash);
        w.Db.PayrollAuditLogs.Select(a => a.Action).Should().Contain("payroll.bank_export.downloaded");
    }

    [Fact]
    public async Task Generate_Twice_IsIdempotent_AndAChangedInputOrDifferentReferenceIsRefused()
    {
        var (w, _) = await ReadyAsync();
        var first = (await w.Svc.GenerateAsync(w.Tenant, Maker, w.Batch, SaudiBankExportTestData.Request(), default)).Value!;
        var again = await w.Svc.GenerateAsync(w.Tenant, Maker, w.Batch, SaudiBankExportTestData.Request(), default);
        again.Value!.Id.Should().Be(first.Id);
        w.Db.BankTransferFiles.Count(f => f.FileName.StartsWith(GeneratedWpsFileStore.Prefix)).Should().Be(1);

        var other = await w.Svc.GenerateAsync(w.Tenant, Maker, w.Batch, SaudiBankExportTestData.Request("2026092602"), default);
        other.Outcome.Should().Be(SaudiBankExportOutcome.Conflict);
        other.Error.Should().Be("batch_already_exported");
    }

    [Fact]
    public async Task Generate_WithABadIban_IsBlocked_RegardlessOfTheApprovedTemplate()
    {
        var (w, _) = await ReadyAsync();
        var rec = await w.Db.PayrollPaymentRecords.FirstAsync(r => r.PaymentBatchId == w.Batch && r.Amount == 6300m);
        rec.Iban = rec.Iban![..^1] + (rec.Iban[^1] == '0' ? '1' : '0');
        var profile = await w.Db.EmployeePayrollProfiles.FirstAsync(p => p.EmployeeId == rec.EmployeeId);
        profile.Iban = rec.Iban;
        await w.Db.SaveChangesAsync();

        var gen = await w.Svc.GenerateAsync(w.Tenant, Maker, w.Batch, SaudiBankExportTestData.Request(), default);
        gen.Outcome.Should().Be(SaudiBankExportOutcome.Invalid);
        gen.Validation!.Errors.Should().Contain(e => e.Code == "iban_checksum_failed");
        w.Db.BankTransferFiles.Count().Should().Be(0);
    }

    [Fact]
    public async Task CustomFile_ReferenceIsSpentForTheEstablishment_AcrossBatches()
    {
        var (w, _) = await ReadyAsync();
        (await w.Svc.GenerateAsync(w.Tenant, Maker, w.Batch, SaudiBankExportTestData.Request(), default)).Outcome.Should().Be(SaudiBankExportOutcome.Ok);
        var second = await SaudiBankExportTestData.SeedBatchAsync(w.Db, w.Tenant, w.Company, 10);
        await MakeSecondEmployeeIbanAsync(w.Db, w.Tenant, second);

        var v = (await w.Svc.ValidateAsync(w.Tenant, second, SaudiBankExportTestData.Request(), default)).Value!;

        v.CanExport.Should().BeFalse();
        v.Errors.Select(e => e.Code).Should().Contain("batch_reference_in_use", "the custom export spent the reference for this establishment and account");
    }

    [Fact]
    public async Task TamperedStoredFile_IsDetectedOnDownload_NeverServed()
    {
        var (w, _) = await ReadyAsync();
        await w.Svc.GenerateAsync(w.Tenant, Maker, w.Batch, SaudiBankExportTestData.Request(), default);
        var row = await w.Db.BankTransferFiles.SingleAsync(f => f.FileName.StartsWith(GeneratedWpsFileStore.Prefix));
        var env = JsonSerializer.Deserialize<GeneratedWpsFileStore.Envelope>(row.FileContent, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        row.FileContent = JsonSerializer.Serialize(env with { ContentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("tampered")) },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await w.Db.SaveChangesAsync();

        await FluentActions.Awaiting(() => w.Svc.DownloadAsync(w.Tenant, Maker, w.Batch, default))
            .Should().ThrowAsync<SaudiBankExportIntegrityException>();
    }

    [Fact]
    public async Task Preview_RedactsIbanAndId_ShowsLayout_AndWritesNothing()
    {
        var w = await ArrangeAsync();
        var t = (await w.Tpl.ProposeAsync(w.Tenant, w.Company, Maker, null, new CustomBankTemplateProposal { Definition = Layout() }, default)).Value!;
        await w.Svc.SaveSettingsAsync(w.Tenant, w.Company, Maker, new SaudiBankExportSettingsDto { MolEstablishmentId = "7001234567" }, default);
        var before = (w.Db.BankTransferFiles.Count(), w.Db.PayrollAuditLogs.Count(), w.Db.SystemSettings.Count());

        var p = (await w.Svc.PreviewAsync(w.Tenant, w.Batch,
            new CustomBankPreviewRequest { TemplateId = t.Id, BatchReference = "2026092601", PaymentDate = SaudiBankExportTestData.Request().PaymentDate }, default)).Value!;

        p.Redacted.Should().BeTrue();
        p.ApprovedVersion.Should().BeFalse("only a pending version exists");
        p.Version.Should().Be(1);
        p.Purpose.Should().Contain("not the Mudad file");
        p.Lines.Should().HaveCount(3, "title row + two employees");
        string.Concat(p.Lines).Should().NotContain(CustomBankFileGeneratorTests.IbanA).And.NotContain(CustomBankFileGeneratorTests.IbanB)
            .And.NotContain("1012345678").And.NotContain("2012345678");
        p.Lines[1].Should().Be($"XXXXXXXXXX,Omar Test,RJHI,{CustomBankFileGeneratorTests.IbanA[..6]}{new string('X', 18)},5000.00,1250.00,500.00,450.00,6300.00,SAR");
        p.CanExport.Should().BeTrue(string.Join("; ", p.Errors.Select(e => e.Code)));
        (w.Db.BankTransferFiles.Count(), w.Db.PayrollAuditLogs.Count(), w.Db.SystemSettings.Count()).Should().Be(before);
    }

    [Fact]
    public async Task Preview_OfAnUnsavedDraft_ReportsValidationErrors_AndStillNeverReturnsAFile()
    {
        var w = await ArrangeAsync();
        await w.Svc.SaveSettingsAsync(w.Tenant, w.Company, Maker, new SaudiBankExportSettingsDto { MolEstablishmentId = "7001234567" }, default);
        var draft = Layout(tweak: d => d.Columns.First(c => c.Field == CustomBankFields.NameEn).MaxLength = 3);

        var p = (await w.Svc.PreviewAsync(w.Tenant, w.Batch,
            new CustomBankPreviewRequest { Definition = draft, BatchReference = "2026092601", PaymentDate = SaudiBankExportTestData.Request().PaymentDate }, default)).Value!;

        p.CanExport.Should().BeFalse();
        p.Errors.Should().Contain(e => e.Code == "value_too_long");
        p.Version.Should().BeNull();
        w.Db.BankTransferFiles.Count().Should().Be(0);
    }

    [Fact]
    public async Task Preview_OfAnotherTenantsBatch_IsNotFound()
    {
        var w = await ArrangeAsync();
        var r = await w.Svc.PreviewAsync(Guid.NewGuid(), w.Batch, new CustomBankPreviewRequest { Definition = Layout() }, default);
        r.Outcome.Should().Be(SaudiBankExportOutcome.NotFound);
    }

    // ── Controller: permissions, maker-checker over HTTP shapes, 404s ──────────────────────────────

    private static SaudiBankExportsController Ctrl(ZayraDbContext db, Guid tenant, Guid user, Guid? scopedCompany, Guid activeCompany, params string[] permissions)
    {
        var claims = new List<Claim> { new("tenant_id", tenant.ToString()), new(ClaimTypes.NameIdentifier, user.ToString()) };
        claims.AddRange(permissions.Select(p => new Claim("permission", p)));
        claims.Add(scopedCompany is Guid c
            ? new Claim(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new { v = 2, m = "companies", c = new[] { c.ToString() } }))
            : new Claim("is_group_scope", "true"));
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) };
        return new SaudiBankExportsController(db, new SaudiBankExportTestData.FakeAddresses(),
            SaudiBankExportTests.EnabledActivationConfig(tenant, activeCompany))
        { ControllerContext = new ControllerContext { HttpContext = http } };
    }

    [Fact]
    public async Task Controller_CreateNeedsStructureManage_DecideNeedsApproveOrExport_ProposerBlocked()
    {
        var w = await ArrangeAsync();
        var body = new CustomBankTemplateProposal { Definition = Layout() };

        (await Ctrl(w.Db, w.Tenant, Maker, w.Company, w.Company, "payroll.export").CreateTemplate(w.Company, body, default))
            .Should().BeOfType<ForbidResult>("payroll.export alone cannot change a template");
        var created = await Ctrl(w.Db, w.Tenant, Maker, w.Company, w.Company, "payroll.export", "payroll.structure_manage").CreateTemplate(w.Company, body, default);
        created.Should().BeOfType<OkObjectResult>();
        var t = (await w.Tpl.ListAsync(w.Tenant, w.Company, Maker, default)).Single();
        var decision = new CustomBankTemplateDecision { Version = 1, DefinitionHash = t.Versions[0].DefinitionHash };

        // The proposer, even holding payroll.approve, is refused.
        var self = await Ctrl(w.Db, w.Tenant, Maker, w.Company, w.Company, "payroll.export", "payroll.structure_manage", "payroll.approve").ApproveTemplate(w.Company, t.Id, decision, default);
        self.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(403);
        // A user with neither permission is refused.
        (await Ctrl(w.Db, w.Tenant, Checker, w.Company, w.Company, "payroll.read").ApproveTemplate(w.Company, t.Id, decision, default))
            .Should().BeOfType<ForbidResult>();
        // Finance approves: payroll.export alone is not enough to be the checker.
        (await Ctrl(w.Db, w.Tenant, Checker, w.Company, w.Company, "payroll.export").ApproveTemplate(w.Company, t.Id, decision, default))
            .Should().BeOfType<ForbidResult>();
        // A different user holding only payroll.approve (not payroll.export) approves.
        var ok = await Ctrl(w.Db, w.Tenant, Checker, w.Company, w.Company, "payroll.approve").ApproveTemplate(w.Company, t.Id, decision, default);
        ok.Should().BeOfType<OkObjectResult>();
        (await w.Tpl.GetAsync(w.Tenant, w.Company, t.Id, Checker, default))!.State.Should().Be("Active");
    }

    [Fact]
    public async Task Controller_CrossTenantAndCrossCompany_Template_Is404()
    {
        var w = await ArrangeAsync();
        var t = (await w.Tpl.ProposeAsync(w.Tenant, w.Company, Maker, null, new CustomBankTemplateProposal { Definition = Layout() }, default)).Value!;
        var otherCompany = await SaudiBankExportTestData.SeedCompanyAsync(w.Db, w.Tenant);
        var decision = new CustomBankTemplateDecision { Version = 1, DefinitionHash = t.Versions[0].DefinitionHash };
        var edit = new CustomBankTemplateProposal { Definition = Layout(tweak: d => d.Delimiter = ";") };

        // Caller scoped to a different company of the same tenant.
        var scoped = Ctrl(w.Db, w.Tenant, Checker, otherCompany, otherCompany, "payroll.export", "payroll.approve", "payroll.structure_manage");
        (await scoped.GetTemplate(w.Company, t.Id, default)).Should().BeOfType<NotFoundResult>();
        (await scoped.ListTemplates(w.Company, default)).Should().BeOfType<NotFoundResult>();
        (await scoped.UpdateTemplate(w.Company, t.Id, edit, default)).Should().BeOfType<NotFoundResult>();
        (await scoped.ApproveTemplate(w.Company, t.Id, decision, default)).Should().BeOfType<NotFoundResult>();
        // The template id under the wrong company of the SAME caller scope.
        var group = Ctrl(w.Db, w.Tenant, Checker, null, otherCompany, "payroll.export", "payroll.approve");
        (await group.GetTemplate(otherCompany, t.Id, default)).Should().BeOfType<NotFoundResult>();
        // Another tenant entirely.
        var foreign = Ctrl(w.Db, Guid.NewGuid(), Checker, null, w.Company, "payroll.export", "payroll.approve", "payroll.structure_manage");
        (await foreign.GetTemplate(w.Company, t.Id, default)).Should().BeOfType<NotFoundResult>();
        (await foreign.ApproveTemplate(w.Company, t.Id, decision, default)).Should().BeOfType<NotFoundResult>();
        (await foreign.Preview(w.Batch, new CustomBankPreviewRequest { Definition = Layout() }, default)).Should().BeOfType<NotFoundResult>();
        (await w.Tpl.GetAsync(w.Tenant, w.Company, t.Id, Checker, default))!.State.Should().Be("PendingApproval");
    }

    [Fact]
    public async Task Controller_Preview_RequiresExport_AndTheBatchScope()
    {
        var w = await ArrangeAsync();
        var req = new CustomBankPreviewRequest { Definition = Layout() };
        (await Ctrl(w.Db, w.Tenant, Maker, w.Company, w.Company, "payroll.read").Preview(w.Batch, req, default)).Should().BeOfType<ForbidResult>();
        // The shared batch-scope helper answers 403 for a company the caller cannot access (unchanged behaviour).
        (await Ctrl(w.Db, w.Tenant, Maker, Guid.NewGuid(), w.Company, "payroll.export").Preview(w.Batch, req, default)).Should().BeOfType<ForbidResult>();
        (await Ctrl(w.Db, w.Tenant, Maker, w.Company, w.Company, "payroll.export").Preview(w.Batch, req, default)).Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Controller_TemplateRoutes_AreOffUnlessTheCompanyIsActivated()
    {
        var w = await ArrangeAsync();
        var claims = new List<Claim> { new("tenant_id", w.Tenant.ToString()), new(ClaimTypes.NameIdentifier, Maker.ToString()), new("permission", "payroll.export"), new("is_group_scope", "true") };
        var ctrl = new SaudiBankExportsController(w.Db, null, configuration: null)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) } } };

        var r = await ctrl.ListTemplates(w.Company, default);

        r.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(403);
    }

    [Fact]
    public void Download_ForTheAnbFormat_StillReturnsAZip()
    {
        new SaudiBankExportDownload(new byte[] { 1 }, "x.zip").ContentType.Should().Be("application/zip");
    }

    [Fact]
    public async Task Settings_DebitAccount_MustBeASaudiIban_AndAuditsFieldNamesOnly()
    {
        var w = await ArrangeAsync();
        var bad = await w.Svc.SaveSettingsAsync(w.Tenant, w.Company, Maker, new SaudiBankExportSettingsDto { DebitAccountIban = "SA0380000000608010167518" }, default);
        bad.Outcome.Should().Be(SaudiBankExportOutcome.Invalid);

        var good = await w.Svc.SaveSettingsAsync(w.Tenant, w.Company, Maker,
            new SaudiBankExportSettingsDto { DebitAccountIban = CustomBankFileGeneratorTests.IbanA }, default);
        good.Outcome.Should().Be(SaudiBankExportOutcome.Ok);
        var audit = w.Db.PayrollAuditLogs.Where(a => a.Action == "payroll.bank_export.settings_saved").Select(a => a.MetadataJson).Single();
        audit.Should().Contain("debitAccountIban").And.NotContain(CustomBankFileGeneratorTests.IbanA);
    }

    [Fact]
    public async Task OnlyTheLastFiveRetiredVersions_AreKept()
    {
        var w = await ArrangeAsync();
        var t = await ApprovedAsync(w);
        for (var v = 2; v <= 9; v++)
        {
            var edit = (await w.Tpl.ProposeAsync(w.Tenant, w.Company, Maker, t.Id,
                new CustomBankTemplateProposal { Definition = Layout(tweak: d => d.SampleReference = $"rev {v}") }, default)).Value!;
            await w.Tpl.DecideAsync(w.Tenant, w.Company, Checker, t.Id, true,
                new CustomBankTemplateDecision { Version = v, DefinitionHash = edit.Versions[0].DefinitionHash }, default);
        }
        var final = (await w.Tpl.GetAsync(w.Tenant, w.Company, t.Id, Maker, default))!;
        final.ActiveVersion.Should().Be(9);
        final.Versions.Count(v => v.Status == "Retired").Should().Be(5);
        final.Versions.Count(v => v.Status == "Approved").Should().Be(1);
    }

    [Fact]
    public async Task StoredDefinitionThatNoLongerMatchesItsApprovedHash_FailsClosedAtGenerate()
    {
        var (w, t) = await ReadyAsync();
        var row = await w.Db.SystemSettings.SingleAsync(s => s.SettingKey == CustomBankTemplateService.Key(w.Company, t.Id));
        row.SettingValue = row.SettingValue.Replace("\"delimiter\":\",\"", "\"delimiter\":\";\"");
        row.SettingValue.Should().Contain("\"delimiter\":\";\"");
        await w.Db.SaveChangesAsync();

        var gen = await w.Svc.GenerateAsync(w.Tenant, Maker, w.Batch, SaudiBankExportTestData.Request(), default);

        gen.Outcome.Should().Be(SaudiBankExportOutcome.Invalid);
        gen.Validation!.Errors.Should().Contain(e => e.Code == "template_integrity_failed");
        w.Db.BankTransferFiles.Count().Should().Be(0);
    }

    [Fact]
    public async Task FileMeta_TakesTheTemplateNameFromTheActiveVersion_NotALaterProposal()
    {
        var (w, t) = await ReadyAsync();
        await w.Tpl.ProposeAsync(w.Tenant, w.Company, Maker, t.Id,
            new CustomBankTemplateProposal { Definition = Layout(name: "Renamed in a pending proposal") }, default);

        (await w.Svc.GenerateAsync(w.Tenant, Maker, w.Batch, SaudiBankExportTestData.Request(), default)).Outcome.Should().Be(SaudiBankExportOutcome.Ok);

        var stored = await w.Db.BankTransferFiles.SingleAsync(f => f.FileName.StartsWith(GeneratedWpsFileStore.Prefix));
        stored.FileContent.Should().Contain("Bank sample layout").And.NotContain("Renamed in a pending proposal");
    }

    [Fact]
    public async Task Preview_OfThePendingVersion_ShowsItsRenderedMaskedSample_ForTheChecker()
    {
        var (w, t) = await ReadyAsync();
        var edit = (await w.Tpl.ProposeAsync(w.Tenant, w.Company, Maker, t.Id,
            new CustomBankTemplateProposal { Definition = Layout(tweak: d => d.Delimiter = ";") }, default)).Value!;

        var p = (await w.Svc.PreviewAsync(w.Tenant, w.Batch,
            new CustomBankPreviewRequest { TemplateId = t.Id, Version = edit.PendingVersion, BatchReference = "2026092601", PaymentDate = SaudiBankExportTestData.Request().PaymentDate }, default)).Value!;

        p.Version.Should().Be(2);
        p.ApprovedVersion.Should().BeFalse();
        p.Lines[1].Should().Contain(";").And.StartWith("XXXXXXXXXX;");
        string.Concat(p.Lines).Should().NotContain(CustomBankFileGeneratorTests.IbanA);
    }
}
