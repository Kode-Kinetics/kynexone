using System.IO.Compression;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Infrastructure.Operations;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports;
using Zayra.Api.Infrastructure.Qiwa;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// The independent review's findings on the Saudi WPS/Qiwa branch, one block per finding. Every rule here
/// is exercised through the code path the pilot will run: the pure wage-file rules, the pre-lock payroll
/// validation engine, the bank-export service, and the payroll / Qiwa controllers.
/// </summary>
public class KsaWpsReviewFixesTests
{
    private static ZayraDbContext NewDb(params IInterceptor[] interceptors)
    {
        var b = new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString());
        if (interceptors.Length > 0) b.AddInterceptors(interceptors);
        return new ZayraDbContext(b.Options);
    }

    private static readonly KsaWageFileHeader Header = new("7-1234567", "SAR", "2026100101", false, null);

    private static KsaWageFileRow Row(
        string nationality = "Saudi", string molId = "1012345678", string? iban = null, string? bic = "RJHISARI",
        decimal gross = 6750m, decimal basic = 5000m, decimal housing = 1250m, decimal net = 6300m, decimal slipDed = 450m,
        decimal debt = 0m, bool overridden = false) =>
        new(1, "E1", nationality, molId, iban ?? IbanValidator.WithValidCheckDigits("SA0080000000608010167519"), bic, "Omar Test",
            gross, basic, housing, net, slipDed, debt, overridden);

    private static Company KsaCompany(Guid tenant) =>
        new() { Id = Guid.NewGuid(), TenantId = tenant, LegalNameEn = "KSA Co", CountryCode = "SA", DefaultCurrency = "SAR" };

    private static List<PayrollValidationResult> RunEngine(PayrollSlip slip, params PayrollDeduction[] lines)
    {
        var tenant = slip.TenantId;
        var run = new PayrollRun { Id = slip.RunId, TenantId = tenant, Year = 2026, Month = 9, Status = "Processed" };
        var emp = new Employee { Id = slip.EmployeeId, TenantId = tenant, EmployeeCode = slip.EmployeeCode, Nationality = "Saudi" };
        var profile = new EmployeePayrollProfile
        {
            TenantId = tenant, EmployeeId = slip.EmployeeId, Iban = IbanValidator.WithValidCheckDigits("SA0080000000608010167519"),
            SalaryCurrency = "SAR", MolId = "1012345678",
        };
        foreach (var l in lines) { l.TenantId = tenant; l.PayrollRunId = run.Id; l.EmployeeId = slip.EmployeeId; }
        return PayrollValidationEngine.Run(new PayrollValidationContext(
            run, new[] { slip }, new[] { emp }, Array.Empty<EmployeeSalaryStructure>(), new[] { profile }, lines,
            Array.Empty<PayrollEarning>(), KsaCompany(tenant)));
    }

    private static PayrollSlip Slip(decimal gross, decimal deductions) => new()
    {
        TenantId = Guid.NewGuid(), RunId = Guid.NewGuid(), EmployeeId = 7, EmployeeCode = "E7", EmployeeName = "Joiner",
        BasicSalary = Math.Round(gross * 0.8m, 2), HousingAllowance = gross - Math.Round(gross * 0.8m, 2),
        GrossSalary = gross, Deductions = deductions, NetSalary = gross - deductions,
    };

    private static PayrollDeduction Line(string code, string source, decimal amount) =>
        new() { ComponentCode = code, ComponentName = code, Source = source, Amount = amount };

    // ── 1. P1: the 50% cap applies to DEBT-type deductions only ─────────────────────────────────

    [Fact]
    public void Five_paid_day_saudi_joiner_whose_gosi_exceeds_half_the_wage_passes()
    {
        // 5/30 of a 6,250 package, with GOSI on the FULL monthly base (KSA default proration_gosi_base = FullMonth).
        const decimal gross = 1041.67m, gosi = 609.38m;
        KsaWageFileRules.Validate(Header, new[] { Row(gross: gross, basic: 833.33m, housing: 208.34m, net: gross - gosi, slipDed: gosi) })
            .Should().BeEmpty("GOSI is a statutory contribution, not a debt owed to the employer");

        RunEngine(Slip(gross, gosi), Line("GOSI-ANN-EE", "Statutory", gosi))
            .Should().NotContain(r => r.Code == WageDeductionClassification.DeductionsExceedHalfWageCode);
    }

    [Fact]
    public void Sixteen_day_unpaid_absence_passes()
    {
        const decimal gross = 6250m, lop = 3333.33m, gosi = 609.38m;
        KsaWageFileRules.Validate(Header, new[] { Row(gross: gross, basic: 5000m, housing: 1250m, net: gross - lop - gosi, slipDed: lop + gosi) })
            .Should().BeEmpty();

        RunEngine(Slip(gross, lop + gosi), Line("LOP_DEDUCTION", "Attendance", lop), Line("GOSI-ANN-EE", "Statutory", gosi))
            .Should().NotContain(r => r.Code == WageDeductionClassification.DeductionsExceedHalfWageCode);
    }

    [Fact]
    public void Loan_plus_penalty_above_half_the_wage_blocks_before_lock()
    {
        // 2,000 loan instalment + 1,500 penalty (a negative adjustment) on a 6,750 wage: 3,500 > 3,375.
        var results = RunEngine(Slip(6750m, 3950m),
            Line("LOAN_EMI", "Loan", 2000m), Line("ADJ_PENALTY", "Adjustment", 1500m), Line("GOSI-ANN-EE", "Statutory", 450m));

        var error = results.Should().ContainSingle(r => r.Code == WageDeductionClassification.DeductionsExceedHalfWageCode).Subject;
        error.Severity.Should().Be("Error", "an Error blocks Approve and Lock");
        error.EmployeeId.Should().Be(7);
        error.Message.Should().Contain("3,500.00").And.Contain("Art. 92/93");
        PayrollValidationOverridePolicy.IsOverridable(error.Code)
            .Should().BeTrue("Art. 93 itself admits a court order or the worker's request");

        // The wage file applies the same rule, and honours a recorded override instead of re-blocking.
        KsaWageFileRules.Validate(Header, new[] { Row(net: 2800m, slipDed: 3950m, debt: 3500m) })
            .Should().ContainSingle(e => e.Code == KsaWageFileRules.Codes.DeductionsOverHalf);
        KsaWageFileRules.Validate(Header, new[] { Row(net: 2800m, slipDed: 3950m, debt: 3500m, overridden: true) })
            .Should().BeEmpty();
    }

    [Fact]
    public void Debt_classification_reads_the_line_source_not_its_name()
    {
        WageDeductionClassification.IsDebtType(Line("ANYTHING", "Loan", 1m)).Should().BeTrue();
        WageDeductionClassification.IsDebtType(Line("ADJ_PENALTY", "Adjustment", 1m)).Should().BeTrue();
        WageDeductionClassification.IsDebtType(Line("ADJ_DAMAGES", "Adjustment", 1m)).Should().BeTrue();
        WageDeductionClassification.IsDebtType(Line("ADJ_CORRECTION", "Adjustment", 1m))
            .Should().BeFalse("only debt-like adjustment types count toward the Art. 92/93 cap");
        WageDeductionClassification.IsDebtType(new PayrollDeduction { ComponentCode = "CUSTOM", Source = "Salary", GlDriverKey = "DED:LOAN" }).Should().BeTrue();
        WageDeductionClassification.IsDebtType(Line("LOAN_EMI_LOOKALIKE", "Attendance", 1m)).Should().BeFalse("names are never matched");
        WageDeductionClassification.IsDebtType(Line(PayrollRecoveryComponents.ReceivableRecovery, "Recovery", 1m)).Should().BeFalse();
        WageDeductionClassification.IsDebtType(new PayrollDeduction { Source = "Loan", IsEmployerContribution = true }).Should().BeFalse();
    }

    // ── 3. P2: bank-code table — legacy identifiers, unknown codes warn ─────────────────────────

    [Theory]
    [InlineData("40", "SAMBSARI")]
    [InlineData("40", "NCBKSAJE")]
    [InlineData("50", "AAALSARI")]
    [InlineData("50", "SABBSARI")]
    public void Legacy_bank_codes_accept_the_retired_and_the_current_bank_identifier(string code, string bic)
    {
        var iban = IbanValidator.WithValidCheckDigits($"SA00{code}000000608010167519");
        KsaWageFileRules.Validate(Header, new[] { Row(iban: iban, bic: bic) }).Should().BeEmpty();
    }

    [Fact]
    public void A_known_bank_code_that_contradicts_the_bic_still_blocks()
    {
        var iban = IbanValidator.WithValidCheckDigits("SA0040000000608010167519");
        KsaWageFileRules.Validate(Header, new[] { Row(iban: iban, bic: "RJHISARI") })
            .Should().ContainSingle(e => e.Code == KsaWageFileRules.Codes.BankCodeMismatch);
    }

    [Fact]
    public void An_unlisted_bank_code_is_a_warning_not_a_block()
    {
        var iban = IbanValidator.WithValidCheckDigits("SA0099000000608010167519");
        var v = KsaWageFileRules.ValidateAll(Header, new[] { Row(iban: iban, bic: null) });
        v.Errors.Should().BeEmpty();
        v.Warnings.Should().ContainSingle(w => w.Code == KsaWageFileRules.WarningCodes.IbanBankCodeUnverified)
            .Which.Message.Should().Contain("confirm with your bank");
    }

    // ── 4. P2: nationality spellings; an ID starting with 1 blames the nationality field ────────

    [Theory]
    [InlineData("KSA")]
    [InlineData(" saudi ")]
    [InlineData("SAUDI ARABIA")]
    [InlineData("Saudi Arabian")]
    [InlineData("sa")]
    [InlineData("SAU")]
    [InlineData("سعودي")]
    [InlineData("سعودية")]
    [InlineData(" السعودية")]
    public void Every_recognised_spelling_of_saudi_passes_with_a_national_id(string nationality)
    {
        KsaWageFileRules.IsSaudiNationality(nationality).Should().BeTrue();
        KsaWageFileRules.Validate(Header, new[] { Row(nationality: nationality) }).Should().BeEmpty();
    }

    [Fact]
    public void An_unrecognised_nationality_with_a_saudi_id_is_a_nationality_error_not_an_id_error()
    {
        var errors = KsaWageFileRules.Validate(Header, new[] { Row(nationality: "Saudia") });
        var e = errors.Should().ContainSingle().Subject;
        e.Code.Should().Be(KsaWageFileRules.Codes.NationalityNotSaudi);
        e.Field.Should().Be("nationality");
        e.Message.Should().Contain("Saudia");
        errors.Should().NotContain(x => x.Field == "employeeId");

        // An expatriate with an Iqama is unaffected; an expatriate with neither prefix still blames the ID.
        KsaWageFileRules.Validate(Header, new[] { Row(nationality: "Egyptian", molId: "2012345678") }).Should().BeEmpty();
        KsaWageFileRules.Validate(Header, new[] { Row(nationality: "Egyptian", molId: "3012345678") })
            .Should().ContainSingle(x => x.Code == KsaWageFileRules.Codes.MolIdPrefixMismatch && x.Field == "employeeId");
    }

    // ── 2 & 5. ANB internal accounts export end to end; one net fault, one message ──────────────

    private static async Task<(ZayraDbContext Db, Guid Tenant, Guid Company, Guid Batch, SaudiBankExportService Svc)> ArrangeAnb()
    {
        var db = NewDb();
        var tenant = Guid.NewGuid();
        var company = await SaudiBankExportTestData.SeedCompanyAsync(db, tenant);
        var batch = await SaudiBankExportTestData.SeedBatchAsync(db, tenant, company, 9);
        var svc = new SaudiBankExportService(db, new SaudiBankExportTestData.FakeAddresses());
        (await svc.SaveSettingsAsync(tenant, company, Guid.NewGuid(), SaudiBankExportTestData.Settings(), default))
            .Outcome.Should().Be(SaudiBankExportOutcome.Ok);
        return (db, tenant, company, batch, svc);
    }

    private static async Task<string> GenerateAndReadBody(SaudiBankExportService svc, Guid tenant, Guid batch, Guid? actor = null)
    {
        var gen = await svc.GenerateAsync(tenant, actor ?? Guid.NewGuid(), batch, SaudiBankExportTestData.Request(), default);
        gen.Outcome.Should().Be(SaudiBankExportOutcome.Ok, string.Join("; ", gen.Validation?.Errors.Select(e => $"{e.Code}: {e.Message}") ?? Array.Empty<string>()));
        var dl = await svc.DownloadAsync(tenant, Guid.NewGuid(), batch, default);
        dl.Outcome.Should().Be(SaudiBankExportOutcome.Ok);
        using var zip = new ZipArchive(new MemoryStream(dl.Value!.ZipBytes));
        using var r = new StreamReader(zip.GetEntry("body.csv")!.Open(), Encoding.UTF8);
        return await r.ReadToEndAsync();
    }

    [Fact]
    public async Task A_sixteen_digit_anb_account_with_the_anb_bic_exports_end_to_end()
    {
        var (_, tenant, _, batch, svc) = await ArrangeAnb();
        var v = (await svc.ValidateAsync(tenant, batch, SaudiBankExportTestData.Request(), default)).Value!;
        v.Errors.Should().NotContain(e => e.Code == KsaWageFileRules.Codes.IbanNotSaudi);
        v.CanExport.Should().BeTrue(string.Join("; ", v.Errors.Select(e => e.Code)));

        var body = await GenerateAndReadBody(svc, tenant, batch);
        body.Should().Contain($"2012345678,{SaudiBankExportTestData.AnbInternal},5250.5,").And.Contain(",ARNBSARI,");
    }

    [Fact]
    public async Task A_sixteen_digit_account_with_another_banks_bic_is_still_refused()
    {
        var (db, tenant, _, batch, svc) = await ArrangeAnb();
        var e2 = await db.Employees.SingleAsync(e => e.TenantId == tenant && e.IqamaNumber == "2012345678");
        e2.WpsBankDetails = JsonSerializer.Serialize(new { schema = ApprovedSaudiBeneficiaryDetails.Schema, bicCode = "RJHISARI" });
        await db.SaveChangesAsync();
        var v = (await svc.ValidateAsync(tenant, batch, SaudiBankExportTestData.Request(), default)).Value!;
        v.CanExport.Should().BeFalse();
        v.Errors.Should().Contain(e => e.EmployeeId == e2.Id && e.Field == "employeeAccountNumber");
    }

    [Fact]
    public async Task A_payslip_whose_net_does_not_reconcile_shows_one_message()
    {
        var (db, tenant, _, batch, svc) = await ArrangeAnb();
        var e1 = await db.Employees.SingleAsync(e => e.TenantId == tenant && e.IdNumber == "1012345678");
        var slip = await db.PayrollSlips.SingleAsync(s => s.TenantId == tenant && s.EmployeeId == e1.Id);
        slip.NetSalary += 1m; // gross − deductions ≠ net
        var rec = await db.PayrollPaymentRecords.SingleAsync(r => r.TenantId == tenant && r.EmployeeId == e1.Id);
        rec.Amount += 1m;
        var b = await db.PayrollPaymentBatches.SingleAsync(x => x.Id == batch);
        b.TotalAmount += 1m;
        var run = await db.PayrollRuns.SingleAsync(r => r.Id == b.PayrollRunId);
        run.TotalNetSalary += 1m;
        await db.SaveChangesAsync();

        var v = (await svc.ValidateAsync(tenant, batch, SaudiBankExportTestData.Request(), default)).Value!;
        v.Errors.Where(e => e.EmployeeId == e1.Id).Should().ContainSingle()
            .Which.Code.Should().Be(KsaWageFileRules.Codes.DeductionsUnreconciled);
    }

    // ── 6 & 7. Server-owned lifecycle, honest label, maker-checker ──────────────────────────────

    private static PayrollController Payroll(ZayraDbContext db, Guid tenantId, Guid userId,
        Zayra.Api.Infrastructure.Documents.IDocumentStorage? storage = null)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()), new(ClaimTypes.NameIdentifier, userId.ToString()),
            new("permission", "payroll.export"), new("is_group_scope", "true"),
        };
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) };
        return new PayrollController(db, new _RfScope(), new _RfHttp(http), new _RfNotifications(), new _RfKsaPackResolver(),
            new StubRuleReader(), new _RfLetters(), storage ?? new MemoryDocumentStorage(), new Zayra.Api.Infrastructure.Documents.PdfRenderGate(1))
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    private static IFormFile FormFile(byte[] bytes, string name = "WPS_OUT.txt", string contentType = "text/plain") =>
        new Microsoft.AspNetCore.Http.FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", name)
            { Headers = new HeaderDictionary(), ContentType = contentType };

    private static async Task<JsonElement> BatchView(PayrollController ctrl, Guid batchId)
    {
        var list = await ctrl.ListPaymentBatches(null, default);
        var json = JsonSerializer.Serialize(((ObjectResult)list).Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return JsonDocument.Parse(json).RootElement.EnumerateArray().Single(e => e.GetProperty("id").GetGuid() == batchId);
    }

    private static string[] Next(JsonElement view) =>
        view.GetProperty("allowedNextStatuses").EnumerateArray().Select(x => x.GetString()!).ToArray();

    private static Guid EvidenceIdOf(IActionResult upload)
    {
        var value = upload.Should().BeOfType<OkObjectResult>().Subject.Value!;
        return (Guid)value.GetType().GetProperty("EvidenceId")!.GetValue(value)!;
    }

    [Fact]
    public async Task An_anb_only_batch_reaches_accepted_through_the_transitions_the_api_offers()
    {
        var (db, tenant, _, batch, svc) = await ArrangeAnb();
        var generator = Guid.NewGuid();
        var storage = new MemoryDocumentStorage();
        await GenerateAndReadBody(svc, tenant, batch, generator);

        var maker = Payroll(db, tenant, generator, storage);
        var uploader = Payroll(db, tenant, Guid.NewGuid(), storage);
        var checker = Payroll(db, tenant, Guid.NewGuid(), storage);

        var view = await BatchView(checker, batch);
        view.GetProperty("wpsStatus").GetString().Should().Be(WpsStatuses.Draft, "the export service never writes WpsStatus");
        view.GetProperty("effectiveWpsStatus").GetString().Should().Be(WpsStatuses.Generated);
        Next(view).Should().Equal(WpsStatuses.Submitted);

        (await checker.UpdateWpsStatus(batch, new WpsStatusRequest(WpsStatuses.Submitted, null, "ANB-SUB-1"), default))
            .Should().BeOfType<OkObjectResult>();
        Next(await BatchView(checker, batch)).Should().BeEquivalentTo(new[] { WpsStatuses.Accepted, WpsStatuses.Rejected });

        var evidenceId = EvidenceIdOf(await uploader.UploadWpsEvidence(batch, new WpsEvidenceUploadForm
        {
            Kind = WpsEvidenceKinds.BankOutputFile, File = FormFile(Encoding.UTF8.GetBytes("bank-signed wps output")),
        }, default));

        // The person who generated the bank file and the person who uploaded the evidence are both makers.
        foreach (var refused in new[] { maker, uploader })
        {
            var res = await refused.UpdateWpsStatus(batch, new WpsStatusRequest(WpsStatuses.Accepted, null, "ACK", evidenceId), default);
            res.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
            JsonSerializer.Serialize(((ObjectResult)res).Value).Should().Contain("acceptance_needs_second_person");
            (await BatchView(refused, batch)).GetProperty("acceptBlockedReason").GetString().Should().Contain("second person");
        }
        (await BatchView(checker, batch)).GetProperty("acceptBlockedReason").ValueKind.Should().Be(JsonValueKind.Null);

        (await checker.UpdateWpsStatus(batch, new WpsStatusRequest(WpsStatuses.Accepted, null, "ACK", evidenceId), default))
            .Should().BeOfType<OkObjectResult>();
        var accepted = await BatchView(checker, batch);
        accepted.GetProperty("effectiveWpsStatus").GetString().Should().Be(WpsStatuses.Accepted);
        accepted.GetProperty("wpsStatusLabel").GetString().Should().Be("Accepted — evidence attached (not verified by Mudad)");
        Next(accepted).Should().NotContain(WpsStatuses.Paid, "Paid is reached by settling, not by picking it");
    }

    [Fact]
    public async Task A_caller_with_no_user_id_cannot_record_accepted()
    {
        var (db, tenant, _, batch, svc) = await ArrangeAnb();
        var storage = new MemoryDocumentStorage();
        await GenerateAndReadBody(svc, tenant, batch);
        var ops = Payroll(db, tenant, Guid.NewGuid(), storage);
        (await ops.UpdateWpsStatus(batch, new WpsStatusRequest(WpsStatuses.Submitted, null, "S"), default)).Should().BeOfType<OkObjectResult>();
        var evidenceId = EvidenceIdOf(await ops.UploadWpsEvidence(batch, new WpsEvidenceUploadForm
        {
            Kind = WpsEvidenceKinds.BankOutputFile, File = FormFile(Encoding.UTF8.GetBytes("x-bank-output")),
        }, default));

        var anonymous = Payroll(db, tenant, Guid.NewGuid(), storage);
        anonymous.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenant.ToString()), new Claim("permission", "payroll.export"), new Claim("is_group_scope", "true"),
        }, "test"));
        (await anonymous.UpdateWpsStatus(batch, new WpsStatusRequest(WpsStatuses.Accepted, null, "ACK", evidenceId), default))
            .Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    private sealed class FailEvidenceSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<BankTransferFile>()
                .Any(e => e.State == EntityState.Added && e.Entity.FileName.StartsWith(WpsAcceptanceEvidenceService.Prefix)))
                throw new DbUpdateException("simulated database failure");
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task When_the_database_save_fails_the_stored_evidence_object_is_deleted()
    {
        var db = NewDb(new FailEvidenceSave());
        var tenant = Guid.NewGuid();
        var company = await SaudiBankExportTestData.SeedCompanyAsync(db, tenant);
        var batch = await SaudiBankExportTestData.SeedBatchAsync(db, tenant, company, 9);
        var storage = new MemoryDocumentStorage();

        var act = () => Payroll(db, tenant, Guid.NewGuid(), storage).UploadWpsEvidence(batch, new WpsEvidenceUploadForm
        {
            Kind = WpsEvidenceKinds.BankOutputFile, File = FormFile(Encoding.UTF8.GetBytes("bank output")),
        }, default);

        await act.Should().ThrowAsync<DbUpdateException>();
        storage.Objects.Should().BeEmpty("an object whose row never committed is an orphan");
        (await db.BankTransferFiles.CountAsync(f => f.FileName.StartsWith(WpsAcceptanceEvidenceService.Prefix))).Should().Be(0);
    }

    [Fact]
    public async Task Evidence_hash_reuse_is_checked_within_the_legal_entity_not_across_the_tenant()
    {
        var db = NewDb();
        var tenant = Guid.NewGuid();
        var companyA = await SaudiBankExportTestData.SeedCompanyAsync(db, tenant);
        var companyB = await SaudiBankExportTestData.SeedCompanyAsync(db, tenant);
        var batchA1 = await SaudiBankExportTestData.SeedBatchAsync(db, tenant, companyA, 9);
        var batchA2 = await SaudiBankExportTestData.SeedBatchAsync(db, tenant, companyA, 10);
        var batchB = await SaudiBankExportTestData.SeedBatchAsync(db, tenant, companyB, 9);
        var ctrl = Payroll(db, tenant, Guid.NewGuid(), new MemoryDocumentStorage());
        var bytes = Encoding.UTF8.GetBytes("a screenshot-free bank output file");
        WpsEvidenceUploadForm Form() => new() { Kind = WpsEvidenceKinds.BankOutputFile, File = FormFile(bytes) };

        (await ctrl.UploadWpsEvidence(batchA1, Form(), default)).Should().BeOfType<OkObjectResult>();
        (await ctrl.UploadWpsEvidence(batchB, Form(), default))
            .Should().BeOfType<OkObjectResult>("another legal entity's evidence is not visible to this check");
        (await ctrl.UploadWpsEvidence(batchA2, Form(), default))
            .Should().BeOfType<ConflictObjectResult>("one file cannot prove two months of the same legal entity");
        (await ctrl.UploadWpsEvidence(batchA1, Form(), default))
            .Should().BeOfType<OkObjectResult>("re-uploading the same file for the same batch is idempotent");
        (await db.BankTransferFiles.CountAsync(f => f.FileName.StartsWith(WpsAcceptanceEvidenceService.Prefix))).Should().Be(2);
    }

    // ── 8. Qiwa: no queue that can only dead-letter ─────────────────────────────────────────────

    private static QiwaController Qiwa(IQiwaApiAdapter adapter) => new(new _RfQiwaService(), adapter, new ConfigurationBuilder().Build())
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("tenant_id", Guid.NewGuid().ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                    new Claim("permission", "qiwa.sync"),
                }, "test")),
            },
        },
    };

    [Fact]
    public async Task Without_the_live_adapter_sync_bulk_sync_and_retry_are_refused_before_anything_is_queued()
    {
        var ctrl = Qiwa(new SandboxQiwaApiAdapter(NullLogger<SandboxQiwaApiAdapter>.Instance));
        foreach (var res in new[]
                 {
                     await ctrl.EnqueueSync(1, "Push", default),
                     await ctrl.EnqueueBulkSync(default),
                     await ctrl.RetryDeadLetter(Guid.NewGuid(), default),
                 })
        {
            res.Should().BeOfType<ConflictObjectResult>();
            JsonSerializer.Serialize(((ObjectResult)res).Value).Should().Contain(QiwaController.SyncUnavailableCode);
        }
    }

    [Fact]
    public async Task Credential_only_dead_letters_are_not_counted_as_qiwa_checks_needing_attention()
    {
        await using var db = NewDb();
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Zayra.Api.Domain.Entities.Tenant { Id = tenantId, Name = "T", Slug = $"t-{Guid.NewGuid():N}" });
        db.QiwaSyncLogs.AddRange(
            new QiwaSyncLog { TenantId = tenantId, EmployeeId = 1, Direction = "Push", Status = QiwaSyncLogStatuses.DeadLetter, DeadLetterReason = QiwaSyncLogStatuses.MissingClientIdReason },
            new QiwaSyncLog { TenantId = tenantId, EmployeeId = 2, Direction = "Push", Status = QiwaSyncLogStatuses.DeadLetter, DeadLetterReason = QiwaSyncLogStatuses.MissingSecretReason },
            new QiwaSyncLog { TenantId = tenantId, EmployeeId = 3, Direction = "Push", Status = QiwaSyncLogStatuses.DeadLetter, DeadLetterReason = "Employee not Qiwa-ready — missing required fields: OccupationCode" });
        await db.SaveChangesAsync();

        var summary = await new QiwaIntegrationService(db, NullLogger<QiwaIntegrationService>.Instance, DataProtectionProvider.Create("t"))
            .GetComplianceSummaryAsync(tenantId);
        summary.FailedSyncCount.Should().Be(1, "only the not-ready employee is something a person can fix");

        var queues = await ProductionReadinessEvidence.BuildQueueHealthAsync(db, default);
        queues.QiwaDeadLetter.Should().Be(1);
        QiwaSyncLogStatuses.IsCredentialsDeadLetter(QiwaSyncLogStatuses.DeadLetter, QiwaSyncLogStatuses.MissingClientIdReason).Should().BeTrue();
    }

    // ── 9. P3: never serve a regenerated file; prefer the MOL establishment id ──────────────────

    private static async Task<(ZayraDbContext Db, Guid Tenant, Guid Company, Guid Batch, int EmployeeId)> SeedKsaRegisterBatchAsync()
    {
        var db = NewDb();
        var tenant = Guid.NewGuid();
        var company = KsaCompany(tenant);
        db.Companies.Add(company);
        var emp = new Employee
        {
            TenantId = tenant, CompanyId = company.Id, EmployeeCode = "E1", FullName = "Omar Test", EnglishName = "Omar Test",
            Status = "Active", ReadinessState = "Ready", JoiningDate = DateTime.UtcNow, IdType = "NationalId",
            IdNumber = "1012345678", Nationality = "Saudi",
        };
        db.Employees.Add(emp);
        await db.SaveChangesAsync();
        var run = new PayrollRun { TenantId = tenant, CompanyId = company.Id, Year = 2026, Month = 9, Status = "Locked", TotalNetSalary = 6300m, EmployeeCount = 1 };
        db.PayrollRuns.Add(run);
        db.PayrollSlips.Add(new PayrollSlip
        {
            TenantId = tenant, CompanyId = company.Id, RunId = run.Id, EmployeeId = emp.Id, EmployeeCode = "E1",
            BasicSalary = 5000m, HousingAllowance = 1250m, GrossSalary = 6750m, Deductions = 450m, NetSalary = 6300m, Status = "Locked",
        });
        db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile
        {
            TenantId = tenant, EmployeeId = emp.Id, Iban = SaudiBankExportTestData.IbanA, SalaryCurrency = "SAR",
            BankRoutingCode = "RJHISARI", WpsEligible = true,
        });
        var batch = new PayrollPaymentBatch
        {
            TenantId = tenant, PayrollRunId = run.Id, BatchNumber = "PB-1", PaymentMethod = "WPS", TotalAmount = 6300m,
            Currency = "SAR", Status = "Pending", WpsStatus = WpsStatuses.Draft,
        };
        db.PayrollPaymentBatches.Add(batch);
        db.PayrollPaymentRecords.Add(new PayrollPaymentRecord
        {
            TenantId = tenant, PaymentBatchId = batch.Id, EmployeeId = emp.Id, Amount = 6300m, Iban = SaudiBankExportTestData.IbanA, Status = "Pending",
        });
        await db.SaveChangesAsync();
        return (db, tenant, company.Id, batch.Id, emp.Id);
    }

    private static async Task SaveBankSettings(ZayraDbContext db, Guid tenant, Guid company, string mol)
    {
        var s = SaudiBankExportTestData.Settings();
        s.MolEstablishmentId = mol;
        (await new SaudiBankExportService(db).SaveSettingsAsync(tenant, company, Guid.NewGuid(), s, default))
            .Outcome.Should().Be(SaudiBankExportOutcome.Ok);
    }

    [Fact]
    public async Task A_legacy_mudad_xml_file_is_refused_on_download_not_regenerated_in_the_new_format()
    {
        var (db, tenant, company, batch, _) = await SeedKsaRegisterBatchAsync();
        await SaveBankSettings(db, tenant, company, "7-1234567");
        var ctrl = Payroll(db, tenant, Guid.NewGuid());
        (await ctrl.GenerateWps(batch, true, false, default)).Should().BeOfType<OkObjectResult>();
        (await ctrl.DownloadWpsFile(batch, default)).Should().BeOfType<FileContentResult>("an unchanged file is reproducible byte for byte");

        // A row generated before bytes were stored: no stored copy, and an older format.
        db.BankTransferFiles.RemoveRange(db.BankTransferFiles.Where(f => f.FileName.StartsWith(GeneratedWpsFileStore.Prefix)));
        var file = await db.WPSFileBatches.SingleAsync(f => f.PaymentBatchId == batch);
        file.FormatVersion = "mudad-xml";
        await db.SaveChangesAsync();

        var res = await Payroll(db, tenant, Guid.NewGuid()).DownloadWpsFile(batch, default);
        res.Should().BeOfType<ConflictObjectResult>();
        JsonSerializer.Serialize(((ObjectResult)res).Value).Should().Contain("wps_file_not_reproducible").And.Contain("older file format");
    }

    [Fact]
    public async Task The_stored_bytes_are_served_even_after_the_inputs_change()
    {
        var (db, tenant, company, batch, empId) = await SeedKsaRegisterBatchAsync();
        await SaveBankSettings(db, tenant, company, "7-1234567");
        (await Payroll(db, tenant, Guid.NewGuid()).GenerateWps(batch, true, false, default)).Should().BeOfType<OkObjectResult>();
        var first = ((FileContentResult)await Payroll(db, tenant, Guid.NewGuid()).DownloadWpsFile(batch, default)).FileContents;

        var emp = await db.Employees.SingleAsync(e => e.Id == empId);
        emp.FullName = "Omar Renamed";
        await db.SaveChangesAsync();

        var again = await Payroll(db, tenant, Guid.NewGuid()).DownloadWpsFile(batch, default);
        again.Should().BeOfType<FileContentResult>().Which.FileContents.Should().Equal(first, "the generated file is stored, not re-created");
        GeneratedWpsFileStore.Sha256Hex(first).Should().Be((await db.WPSFileBatches.SingleAsync(f => f.PaymentBatchId == batch)).FileHash);
    }

    [Theory]
    [InlineData(WpsStatuses.Accepted, false)]
    [InlineData(WpsStatuses.Paid, false)]
    [InlineData(WpsStatuses.Submitted, true)]
    public async Task A_legacy_file_whose_inputs_changed_is_refused_with_advice_that_fits_the_batch_status(string status, bool adviseRejected)
    {
        var (db, tenant, company, batch, empId) = await SeedKsaRegisterBatchAsync();
        await SaveBankSettings(db, tenant, company, "7-1234567");
        (await Payroll(db, tenant, Guid.NewGuid()).GenerateWps(batch, true, false, default)).Should().BeOfType<OkObjectResult>();
        db.BankTransferFiles.RemoveRange(db.BankTransferFiles.Where(f => f.FileName.StartsWith(GeneratedWpsFileStore.Prefix)));
        (await db.Employees.SingleAsync(e => e.Id == empId)).FullName = "Omar Renamed";
        (await db.PayrollPaymentBatches.SingleAsync(b => b.Id == batch)).WpsStatus = status;
        await db.SaveChangesAsync();

        var res = await Payroll(db, tenant, Guid.NewGuid()).DownloadWpsFile(batch, default);
        var json = JsonSerializer.Serialize(res.Should().BeOfType<ConflictObjectResult>().Subject.Value);
        json.Should().Contain("SHA-256");
        if (adviseRejected) json.Should().Contain("mark the batch Rejected");
        else json.Should().NotContain("Rejected").And.Contain("already been accepted");
    }

    [Fact]
    public async Task Ksa_prefers_the_mol_establishment_id_from_the_bank_file_settings()
    {
        var (db, tenant, company, batch, _) = await SeedKsaRegisterBatchAsync();
        await SaveBankSettings(db, tenant, company, "7-7654321");
        var ctrl = Payroll(db, tenant, Guid.NewGuid());
        (await ctrl.GenerateWps(batch, true, false, default)).Should().BeOfType<OkObjectResult>();
        var xml = Encoding.UTF8.GetString(((FileContentResult)await ctrl.DownloadWpsFile(batch, default)).FileContents);
        xml.Should().Contain("<MolEstablishmentId>7-7654321</MolEstablishmentId>");
    }

    [Fact]
    public async Task Ksa_refuses_when_the_bank_settings_and_the_wps_agent_id_name_different_establishments()
    {
        var (db, tenant, company, batch, _) = await SeedKsaRegisterBatchAsync();
        await SaveBankSettings(db, tenant, company, "7-7654321");
        db.GCCComplianceSettings.Add(new GCCComplianceSetting { TenantId = tenant, CompanyId = company, CountryCode = "SA", WpsAgentId = "7-1234567" });
        await db.SaveChangesAsync();

        var res = await Payroll(db, tenant, Guid.NewGuid()).GenerateWps(batch, true, false, default);
        res.Should().BeOfType<UnprocessableEntityObjectResult>();
        JsonSerializer.Serialize(((ObjectResult)res).Value).Should().Contain("mol_establishment_id_conflict");
        db.WPSFileBatches.Should().BeEmpty();
    }

    // ── 10. Cash/cheque and zero-net employees are left out of the bank batch, by name ──────────

    /// <summary>The sealed Lock audit entry, as Lock writes it, freezing who is paid by cash/cheque.</summary>
    private static void SeedLockAudit(ZayraDbContext db, Guid tenant, Guid runId, params (int EmployeeId, string Method)[] outside) =>
        db.PayrollAuditLogs.Add(new PayrollAuditLog
        {
            TenantId = tenant, Action = PaymentBatchExclusions.LockAuditAction, EntityName = "PayrollRun", EntityId = runId.ToString(),
            MetadataJson = JsonSerializer.Serialize(new
            {
                ip = "test", userId = (string?)null,
                data = new { paidOutsideBankFile = outside.Select(o => new { employeeId = o.EmployeeId, method = o.Method }).ToList() },
            }),
        });

    [Fact]
    public async Task One_cash_and_one_zero_net_employee_are_excluded_and_the_bank_file_reconciles()
    {
        var db = NewDb();
        var tenant = Guid.NewGuid();
        var company = await SaudiBankExportTestData.SeedCompanyAsync(db, tenant);
        // The standard two bank-paid employees (Al Rajhi IBAN + ANB internal account), then turn the
        // seeded batch into a plain Locked run so the endpoint builds the batch itself.
        var seeded = await SaudiBankExportTestData.SeedBatchAsync(db, tenant, company, 9);
        var seededBatch = await db.PayrollPaymentBatches.SingleAsync(b => b.Id == seeded);
        var run = await db.PayrollRuns.SingleAsync(r => r.Id == seededBatch.PayrollRunId);
        db.PayrollPaymentRecords.RemoveRange(db.PayrollPaymentRecords.Where(r => r.PaymentBatchId == seeded));
        db.PayrollPaymentBatches.Remove(seededBatch);

        Employee Extra(string code, string iqama) => new()
        {
            TenantId = tenant, CompanyId = company, EmployeeCode = code, FullName = code, EnglishName = code, Status = "Active",
            ReadinessState = "Ready", JoiningDate = DateTime.UtcNow, IqamaNumber = iqama, Nationality = "Indian",
        };
        var cash = Extra("CASH-1", "2099999991");
        var zero = Extra("ZERO-1", "2099999992");
        db.Employees.AddRange(cash, zero);
        await db.SaveChangesAsync();
        db.PayrollSlips.AddRange(
            new PayrollSlip { TenantId = tenant, CompanyId = company, RunId = run.Id, EmployeeId = cash.Id, EmployeeCode = cash.EmployeeCode,
                BasicSalary = 2000m, HousingAllowance = 500m, GrossSalary = 2500m, Deductions = 0m, NetSalary = 2500m, Status = "Locked" },
            new PayrollSlip { TenantId = tenant, CompanyId = company, RunId = run.Id, EmployeeId = zero.Id, EmployeeCode = zero.EmployeeCode,
                BasicSalary = 1000m, HousingAllowance = 0m, GrossSalary = 1000m, Deductions = 1000m, NetSalary = 0m, Status = "Locked" });
        db.EmployeePayrollProfiles.AddRange(
            new EmployeePayrollProfile { TenantId = tenant, EmployeeId = cash.Id, PaymentMethod = "Cash", SalaryCurrency = "SAR" },
            new EmployeePayrollProfile { TenantId = tenant, EmployeeId = zero.Id, Iban = IbanValidator.WithValidCheckDigits("SA0020000000608010167519"), SalaryCurrency = "SAR" });
        run.TotalNetSalary = 11550.50m + 2500m;
        await db.SaveChangesAsync();
        // The cash method as FROZEN at Lock (the live profile is not what the batch reads).
        SeedLockAudit(db, tenant, run.Id, (cash.Id, "Cash"));
        await db.SaveChangesAsync();

        var created = await Payroll(db, tenant, Guid.NewGuid()).CreatePaymentBatch(run.Id, new PayrollPaymentBatchRequest("WPS", "SAR"), default);
        var body = JsonDocument.Parse(JsonSerializer.Serialize(created.Should().BeOfType<CreatedResult>().Subject.Value,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))).RootElement;
        body.GetProperty("totalAmount").GetDecimal().Should().Be(11550.50m);
        body.GetProperty("runNetTotal").GetDecimal().Should().Be(14050.50m);
        body.GetProperty("excludedTotal").GetDecimal().Should().Be(2500m);
        var exclusions = body.GetProperty("paymentExclusions").EnumerateArray().ToList();
        exclusions.Should().HaveCount(2);
        exclusions.Should().Contain(x => x.GetProperty("employeeId").GetInt32() == cash.Id
                                         && x.GetProperty("reasonCode").GetString() == PaymentBatchExclusions.PaidOutsideBankFileCode);
        exclusions.Should().Contain(x => x.GetProperty("employeeId").GetInt32() == zero.Id
                                         && x.GetProperty("reasonCode").GetString() == PaymentBatchExclusions.ZeroNetCode);

        var batchId = body.GetProperty("id").GetGuid();
        (await db.PayrollPaymentRecords.CountAsync(r => r.PaymentBatchId == batchId)).Should().Be(2);

        var svc = new SaudiBankExportService(db, new SaudiBankExportTestData.FakeAddresses());
        (await svc.SaveSettingsAsync(tenant, company, Guid.NewGuid(), SaudiBankExportTestData.Settings(), default)).Outcome.Should().Be(SaudiBankExportOutcome.Ok);
        var v = (await svc.ValidateAsync(tenant, batchId, SaudiBankExportTestData.Request(), default)).Value!;
        v.CanExport.Should().BeTrue(string.Join("; ", v.Errors.Select(e => $"{e.Code}: {e.Message}")));
        v.EmployeeCount.Should().Be(2);
        v.TotalAmount.Should().Be(v.RunNetTotal - v.ExcludedTotal, "the bank file reconciles to the run net minus the named exclusions");
        v.Exclusions.Select(x => x.EmployeeId).Should().BeEquivalentTo(new[] { cash.Id, zero.Id });

        var csv = await GenerateAndReadBody(svc, tenant, batchId);
        csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(1 + 2, "a column row plus the two bank-paid employees");
        csv.Should().NotContain("2099999991").And.NotContain("2099999992");
    }

    // ══ Round 3 ══════════════════════════════════════════════════════════════════════════════════

    private const string SalariesPayable = "2100 - Salaries Payable";

    /// <summary>A Locked KSA run: the two standard bank-paid employees plus one paid in cash (frozen at
    /// Lock) who is also a leaver mid-settlement, with its net-pay accrual on the ledger.</summary>
    private static async Task<(ZayraDbContext Db, Guid Tenant, Guid RunId, Employee Cash, EmployeeFinalSettlement CashSettlement)> ArrangeCashRunAsync()
    {
        var db = NewDb();
        var tenant = Guid.NewGuid();
        var company = await SaudiBankExportTestData.SeedCompanyAsync(db, tenant);
        var seeded = await SaudiBankExportTestData.SeedBatchAsync(db, tenant, company, 9);
        var seededBatch = await db.PayrollPaymentBatches.SingleAsync(b => b.Id == seeded);
        var run = await db.PayrollRuns.SingleAsync(r => r.Id == seededBatch.PayrollRunId);
        db.PayrollPaymentRecords.RemoveRange(db.PayrollPaymentRecords.Where(r => r.PaymentBatchId == seeded));
        db.PayrollPaymentBatches.Remove(seededBatch);
        var cash = new Employee
        {
            TenantId = tenant, CompanyId = company, EmployeeCode = "CASH-1", FullName = "Cash Paid", EnglishName = "Cash Paid",
            Status = "Active", ReadinessState = "Ready", JoiningDate = DateTime.UtcNow, IqamaNumber = "2099999991", Nationality = "Indian",
        };
        db.Employees.Add(cash);
        await db.SaveChangesAsync();
        db.PayrollSlips.Add(new PayrollSlip
        {
            TenantId = tenant, CompanyId = company, RunId = run.Id, EmployeeId = cash.Id, EmployeeCode = cash.EmployeeCode,
            BasicSalary = 2000m, HousingAllowance = 500m, GrossSalary = 2500m, Deductions = 0m, NetSalary = 2500m, Status = "Locked",
        });
        db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile { TenantId = tenant, EmployeeId = cash.Id, PaymentMethod = "Cash", SalaryCurrency = "SAR" });
        run.TotalNetSalary = 11550.50m + 2500m;
        SeedLockAudit(db, tenant, run.Id, (cash.Id, "Cash"));
        db.FinanceGlEntries.Add(new FinanceGlEntry
        {
            TenantId = tenant, CompanyId = company, SourceModule = "Payroll", SourceEntityId = run.Id, SourceEntityRef = "RUN",
            EventType = GlEventTypes.Accrual, CreditAccount = SalariesPayable, DebitAccount = string.Empty,
            Amount = run.TotalNetSalary, Currency = "SAR", Description = PayrollGlDescriptions.NetPayable,
            EntryDate = DateOnly.FromDateTime(DateTime.UtcNow), Period = "2026-09",
        });
        var settlement = new EmployeeFinalSettlement
        {
            TenantId = tenant, CompanyId = company, EmployeeId = cash.Id, EmployeeCode = cash.EmployeeCode, PayrollRunId = run.Id,
            OffboardingId = Guid.NewGuid(), Status = FinalSettlementStatuses.Disbursing, Currency = "SAR",
        };
        db.EmployeeFinalSettlements.Add(settlement);
        await db.SaveChangesAsync();
        return (db, tenant, run.Id, cash, settlement);
    }

    private static async Task<decimal> SalariesPayableBalance(ZayraDbContext db, Guid tenant, Guid runId)
    {
        var lines = await db.FinanceGlEntries.AsNoTracking()
            .Where(e => e.TenantId == tenant && e.SourceEntityId == runId && !e.IsReversed).ToListAsync();
        return lines.Where(l => l.CreditAccount == SalariesPayable).Sum(l => l.Amount)
             - lines.Where(l => l.DebitAccount == SalariesPayable).Sum(l => l.Amount);
    }

    [Fact]
    public async Task Salaries_payable_clears_only_after_the_bank_batch_and_every_outside_payment_are_recorded()
    {
        var (db, tenant, runId, cash, settlement) = await ArrangeCashRunAsync();
        var ops = Payroll(db, tenant, Guid.NewGuid());
        var created = await ops.CreatePaymentBatch(runId, new PayrollPaymentBatchRequest("WPS", "SAR"), default);
        var batchId = (Guid)((CreatedResult)created).Value!.GetType().GetProperty("Id")!.GetValue(((CreatedResult)created).Value)!;
        var batch = await db.PayrollPaymentBatches.SingleAsync(b => b.Id == batchId);
        batch.WpsStatus = WpsStatuses.Accepted;
        await db.SaveChangesAsync();
        (await SalariesPayableBalance(db, tenant, runId)).Should().Be(14050.50m);

        (await ops.SettlePaymentBatch(batchId, new SettlePaymentBatchRequest("BANK-REF"), default)).Should().BeOfType<OkObjectResult>();
        (await SalariesPayableBalance(db, tenant, runId))
            .Should().Be(2500m, "the cash wage was not paid by the bank, so its share of 2100 stays open");
        (await db.EmployeeFinalSettlements.AsNoTracking().SingleAsync(s => s.Id == settlement.Id)).Status
            .Should().Be(FinalSettlementStatuses.Disbursing, "a leaver paid in cash is not Paid because the bank batch was");

        // The employee being paid may not record their own payment.
        var subjectUser = Guid.NewGuid();
        (await db.Employees.SingleAsync(e => e.Id == cash.Id)).UserAccountId = subjectUser;
        await db.SaveChangesAsync();
        var request = new OutsidePaymentRequest(cash.Id, "Cash", "RCPT-001", new DateOnly(2026, 9, 28));
        (await Payroll(db, tenant, subjectUser).RecordOutsidePayment(batchId, request, default))
            .Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        (await ops.RecordOutsidePayment(batchId, request with { Method = "Transfer" }, default)).Should().BeOfType<BadRequestObjectResult>();

        (await ops.RecordOutsidePayment(batchId, request, default)).Should().BeOfType<OkObjectResult>();
        (await SalariesPayableBalance(db, tenant, runId)).Should().Be(0m);
        (await db.EmployeeFinalSettlements.AsNoTracking().SingleAsync(s => s.Id == settlement.Id)).Status
            .Should().Be(FinalSettlementStatuses.Paid);
        db.PayrollAuditLogs.Should().Contain(a => a.Action == "payroll.outside_payment.recorded" && a.MetadataJson.Contains("RCPT-001"));
        (await ops.RecordOutsidePayment(batchId, request, default)).Should().BeOfType<ConflictObjectResult>("once per employee");

        var view = await BatchView(ops, batchId);
        view.GetProperty("paymentExclusions").EnumerateArray().Single().GetProperty("outsidePaymentRecorded").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task The_batch_uses_the_payment_method_frozen_at_lock_not_the_live_profile()
    {
        var (db, tenant, runId, cash, _) = await ArrangeCashRunAsync();
        // After Lock someone switches the cash employee to bank transfer: the batch still follows the Lock.
        (await db.EmployeePayrollProfiles.SingleAsync(p => p.EmployeeId == cash.Id)).PaymentMethod = "BankTransfer";
        await db.SaveChangesAsync();
        var created = (CreatedResult)await Payroll(db, tenant, Guid.NewGuid()).CreatePaymentBatch(runId, new PayrollPaymentBatchRequest("WPS", "SAR"), default);
        var json = JsonSerializer.Serialize(created.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        json.Should().Contain($"\"employeeId\":{cash.Id}").And.Contain("\"paymentMethodsFrozenAtLock\":true").And.Contain("Mudad");
    }

    [Fact]
    public async Task Lock_refuses_when_cash_pay_changed_since_it_was_validated_and_approved()
    {
        await using var db = NewDb();
        var tenant = Guid.NewGuid();
        var run = new PayrollRun { TenantId = tenant, Year = 2026, Month = 9, Status = "Approved", TotalNetSalary = 2500m };
        db.PayrollRuns.Add(run);
        db.PayrollSlips.Add(new PayrollSlip { TenantId = tenant, RunId = run.Id, EmployeeId = 41, EmployeeCode = "E41", GrossSalary = 2500m, NetSalary = 2500m });
        db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile { TenantId = tenant, EmployeeId = 41, PaymentMethod = "Cheque" });
        await db.SaveChangesAsync();

        var res = await Payroll(db, tenant, Guid.NewGuid()).Lock(run.Id, default);
        res.Should().BeOfType<ConflictObjectResult>();
        JsonSerializer.Serialize(((ObjectResult)res).Value).Should().Contain("outside_bank_payments_changed");
        (await db.PayrollRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id)).Status.Should().Be("Approved");
    }

    [Fact]
    public async Task Approve_requires_the_cash_and_cheque_count_to_be_acknowledged()
    {
        await using var db = NewDb();
        var tenant = Guid.NewGuid();
        var run = new PayrollRun { TenantId = tenant, Year = 2026, Month = 9, Status = "Processed", CreatedByUserId = Guid.NewGuid(), ProcessedByUserId = Guid.NewGuid() };
        db.PayrollRuns.Add(run);
        db.PayrollValidationResults.Add(new PayrollValidationResult
        {
            TenantId = tenant, PayrollRunId = run.Id, EmployeeId = 41, Severity = "Warning",
            Code = PaymentBatchExclusions.PaidOutsideWithIbanWarning, Message = "paid by cash although an IBAN is on file",
        });
        await db.SaveChangesAsync();
        var approver = Payroll(db, tenant, Guid.NewGuid());

        var refused = await approver.Approve(run.Id, new PayrollDecisionRequest("ok"), default);
        refused.Should().BeOfType<ConflictObjectResult>();
        JsonSerializer.Serialize(((ObjectResult)refused).Value).Should().Contain("outside_bank_payments_not_acknowledged").And.Contain("Mudad");

        var acknowledged = await approver.Approve(run.Id, new PayrollDecisionRequest("ok", ExpectedOutsideBankCount: 1), default);
        JsonSerializer.Serialize((acknowledged as ObjectResult)?.Value).Should().NotContain("outside_bank_payments_not_acknowledged");
    }

    [Fact]
    public void Pre_lock_warns_for_cash_pay_and_warns_harder_when_an_iban_is_on_file()
    {
        var tenant = Guid.NewGuid();
        var run = new PayrollRun { Id = Guid.NewGuid(), TenantId = tenant, Year = 2026, Month = 9, Status = "Processed" };
        var slips = new[]
        {
            new PayrollSlip { TenantId = tenant, RunId = run.Id, EmployeeId = 1, EmployeeCode = "E1", GrossSalary = 3000m, NetSalary = 3000m },
            new PayrollSlip { TenantId = tenant, RunId = run.Id, EmployeeId = 2, EmployeeCode = "E2", GrossSalary = 3000m, NetSalary = 3000m },
        };
        var profiles = new[]
        {
            new EmployeePayrollProfile { TenantId = tenant, EmployeeId = 1, PaymentMethod = "Cash" },
            new EmployeePayrollProfile { TenantId = tenant, EmployeeId = 2, PaymentMethod = "Cheque", Iban = SaudiBankExportTestData.IbanA },
        };
        var results = PayrollValidationEngine.Run(new PayrollValidationContext(run, slips,
            new[] { new Employee { Id = 1, TenantId = tenant, Nationality = "Indian" }, new Employee { Id = 2, TenantId = tenant, Nationality = "Indian" } },
            Array.Empty<EmployeeSalaryStructure>(), profiles, Array.Empty<PayrollDeduction>(), Array.Empty<PayrollEarning>(), KsaCompany(tenant)));
        results.Should().ContainSingle(r => r.Code == PaymentBatchExclusions.PaidOutsideWarning && r.EmployeeId == 1 && r.Severity == "Warning")
            .Which.Message.Should().Contain("Mudad");
        results.Should().ContainSingle(r => r.Code == PaymentBatchExclusions.PaidOutsideWithIbanWarning && r.EmployeeId == 2)
            .Which.Message.Should().Contain("valid IBAN is on file");
        results.Should().NotContain(r => r.Code == "MISSING_IBAN");
    }

    // ── One BIC resolver ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_anb_bic_recorded_only_in_the_approved_bank_details_is_honoured_by_every_stage()
    {
        var (db, tenant, _, batch, svc) = await ArrangeAnb();
        var e2 = await db.Employees.SingleAsync(e => e.TenantId == tenant && e.IqamaNumber == "2012345678");
        e2.WpsBankDetails = JsonSerializer.Serialize(new { schema = ApprovedSaudiBeneficiaryDetails.Schema, bicCode = " arnbsari " });
        var profile = await db.EmployeePayrollProfiles.SingleAsync(p => p.EmployeeId == e2.Id);
        profile.BankRoutingCode = string.Empty;
        await db.SaveChangesAsync();

        SaudiBeneficiaryBic.Resolve(e2, profile).Should().Be("ARNBSARI");

        var v = (await svc.ValidateAsync(tenant, batch, SaudiBankExportTestData.Request(), default)).Value!;
        v.CanExport.Should().BeTrue(string.Join("; ", v.Errors.Select(e => $"{e.Code}: {e.Message}")));

        var b = await db.PayrollPaymentBatches.SingleAsync(x => x.Id == batch);
        var run = await db.PayrollRuns.SingleAsync(r => r.Id == b.PayrollRunId);
        var slips = await db.PayrollSlips.Where(s => s.RunId == run.Id).ToListAsync();
        var employees = await db.Employees.Where(e => e.TenantId == tenant).ToListAsync();
        var profiles = await db.EmployeePayrollProfiles.Where(p => p.TenantId == tenant).ToListAsync();

        var preLock = PayrollValidationEngine.Run(new PayrollValidationContext(run, slips, employees,
            Array.Empty<EmployeeSalaryStructure>(), profiles, Array.Empty<PayrollDeduction>(), Array.Empty<PayrollEarning>(), KsaCompany(tenant)));
        preLock.Should().NotContain(r => r.EmployeeId == e2.Id && (r.Code == "MISSING_IBAN" || r.Code == "INVALID_IBAN"));

        WpsSifValidator.Validate(run, slips, profiles, employees).BlockingErrors
            .Should().NotContain(e => e.EmployeeId == e2.Id && e.Code == "MISSING_IBAN");
    }

    // ── Art. 92/93 cap override: written basis, never the subject, never anonymous ───────────────

    [Fact]
    public async Task The_cap_override_needs_a_document_reference_and_an_identified_approver_who_is_not_the_employee()
    {
        await using var db = NewDb();
        var tenant = Guid.NewGuid();
        var subjectUser = Guid.NewGuid();
        var emp = new Employee { TenantId = tenant, EmployeeCode = "E9", FullName = "Subject", UserAccountId = subjectUser, Status = "Active" };
        db.Employees.Add(emp);
        await db.SaveChangesAsync();
        var run = new PayrollRun { TenantId = tenant, Year = 2026, Month = 9, Status = "Processed", CreatedByUserId = Guid.NewGuid(), ProcessedByUserId = Guid.NewGuid() };
        db.PayrollRuns.Add(run);
        var result = new PayrollValidationResult
        {
            TenantId = tenant, PayrollRunId = run.Id, EmployeeId = emp.Id, Severity = "Error",
            Code = WageDeductionClassification.DeductionsExceedHalfWageCode, Message = "cap",
        };
        db.PayrollValidationResults.Add(result);
        await db.SaveChangesAsync();
        const string reason = "Commission decision orders recovery of the full instalment this month.";

        var noRef = await Payroll(db, tenant, Guid.NewGuid()).ResolveValidationResult(run.Id, result.Id, new PayrollReasonRequest(reason), default);
        JsonSerializer.Serialize(noRef.Should().BeOfType<BadRequestObjectResult>().Subject.Value)
            .Should().Contain("document_reference_required").And.Contain("lawful written basis").And.NotContain("worker");

        var bySubject = await Payroll(db, tenant, subjectUser).ResolveValidationResult(run.Id, result.Id, new PayrollReasonRequest(reason, "LC-2026-17"), default);
        bySubject.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);

        var anonymous = Payroll(db, tenant, Guid.NewGuid());
        anonymous.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("tenant_id", tenant.ToString()) }, "test"));
        (await anonymous.ResolveValidationResult(run.Id, result.Id, new PayrollReasonRequest(reason, "LC-2026-17"), default))
            .Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);

        (await Payroll(db, tenant, Guid.NewGuid()).ResolveValidationResult(run.Id, result.Id, new PayrollReasonRequest(reason, "LC-2026-17"), default))
            .Should().BeOfType<OkObjectResult>();
        (await db.PayrollValidationOverrides.SingleAsync()).Reason.Should().Contain("[ref: LC-2026-17]");
    }
}

/// <summary>Real-Postgres proof that the evidence hash-reuse check and the insert are one serialized unit.</summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class WpsEvidencePostgresTests
{
    private readonly PostgresFixture _fx;
    public WpsEvidencePostgresTests(PostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task Two_concurrent_uploads_of_one_file_to_two_batches_of_one_company_record_it_once()
    {
        Guid tenant, batchA, batchB, company;
        await using (var db = _fx.CreateDb())
        {
            tenant = await PostgresFixture.SeedMinimalTenant(db);
            company = await SaudiBankExportTestData.SeedCompanyAsync(db, tenant);
            batchA = await SaudiBankExportTestData.SeedBatchAsync(db, tenant, company, 9);
            batchB = await SaudiBankExportTestData.SeedBatchAsync(db, tenant, company, 10);
        }
        var storage = new LockedMemoryStorage();
        var bytes = Encoding.UTF8.GetBytes($"bank output {Guid.NewGuid()}");

        async Task<WpsEvidenceUploadResult> Upload(Guid batch)
        {
            await using var db = _fx.CreateDb();
            return await new WpsAcceptanceEvidenceService(db, storage).RecordAsync(
                tenant, company, batch, Guid.NewGuid(), WpsEvidenceKinds.BankOutputFile, "out.txt", "text/plain", bytes, null,
                _ => Task.CompletedTask, default);
        }

        var results = await Task.WhenAll(Upload(batchA), Upload(batchB));
        results.Count(r => r.Evidence is not null).Should().Be(1);
        results.Single(r => r.Evidence is null).Error.Should().Be("evidence_already_used");

        await using var check = _fx.CreateDb();
        (await check.BankTransferFiles.CountAsync(f => f.TenantId == tenant && f.FileName.StartsWith(WpsAcceptanceEvidenceService.Prefix)))
            .Should().Be(1);
        storage.Count.Should().Be(1, "the loser stored nothing");
    }

    private sealed class LockedMemoryStorage : Zayra.Api.Infrastructure.Documents.IDocumentStorage
    {
        private readonly MemoryDocumentStorage _inner = new();
        public int Count { get { lock (_inner) return _inner.Objects.Count; } }
        public Task<Zayra.Api.Infrastructure.Documents.StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken ct)
        { lock (_inner) return Task.FromResult(_inner.SaveAsync(tenantId, file, ct).GetAwaiter().GetResult()); }
        public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default)
        { lock (_inner) return _inner.GetBytesAsync(tenantId, storageUrl, ct); }
        public Task<bool> TryDeleteAsync(Guid tenantId, string storageUrl, CancellationToken ct = default)
        { lock (_inner) return _inner.TryDeleteAsync(tenantId, storageUrl, ct); }
        public string ResolvePath(string storageUrl) => storageUrl;
    }
}

// ── File-scoped stubs ───────────────────────────────────────────────────────────────────────────

file sealed class _RfScope : Zayra.Api.Application.Common.IDataScopeService
{
    public Task<Zayra.Api.Application.Common.DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
        => Task.FromResult(new Zayra.Api.Application.Common.DataScope { Level = Zayra.Api.Application.Common.DataScopeLevel.Organization, AllowedEmployeeIds = null });
}

file sealed class _RfHttp : IHttpContextAccessor
{
    public _RfHttp(HttpContext ctx) => HttpContext = ctx;
    public HttpContext? HttpContext { get; set; }
}

file sealed class _RfNotifications : Zayra.Api.Infrastructure.Notifications.INotificationService
{
    public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken ct) => Task.CompletedTask;
    public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken ct) => Task.CompletedTask;
}

file sealed class _RfLetters : Zayra.Api.Infrastructure.Documents.Letters.ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(Zayra.Api.Infrastructure.Documents.Letters.PayslipData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.OfferLetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}

file sealed class _RfKsaPackResolver : Zayra.Api.Application.CountryPack.ICountryPackResolver
{
    public Zayra.Api.Application.CountryPack.IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j) => new Zayra.Api.Infrastructure.CountryPack.DefaultStatutoryDeductionCalculator();
    public Zayra.Api.Application.CountryPack.IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j) => new Zayra.Api.Infrastructure.CountryPack.DefaultEndOfServiceCalculator();
    public Zayra.Api.Application.CountryPack.IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j) => new KsaWageProtectionExporter();
    public Zayra.Api.Application.CountryPack.INationalizationTracker ResolveNationalizationTracker(string cc, string j) => new Zayra.Api.Infrastructure.CountryPack.DefaultNationalizationTracker();
    public Zayra.Api.Application.CountryPack.ILocalizationProfile ResolveLocalizationProfile(string cc, string j) => new Zayra.Api.Infrastructure.CountryPack.DefaultLocalizationProfile();
    public Zayra.Api.Application.CountryPack.ICountryPackDescriptor ResolveDescriptor(string cc, string j) => new KsaDescriptor();
}

file sealed class _RfQiwaService : IQiwaIntegrationService
{
    private static T Unused<T>() => throw new InvalidOperationException("The refusal must happen before the service is called.");
    public Task<QiwaTenantConnection?> GetConnectionStatusAsync(Guid tenantId, CancellationToken ct = default) => Task.FromResult<QiwaTenantConnection?>(null);
    public Task<QiwaTenantConnection> UpsertConnectionAsync(Guid tenantId, QiwaConnectionRequest request, Guid? userId, string ipAddress, CancellationToken ct = default) => Unused<Task<QiwaTenantConnection>>();
    public Task SaveApiCredentialAsync(Guid tenantId, string clientId, string clientSecret, string environment, Guid updatedBy, string ipAddress, CancellationToken ct = default) => Unused<Task>();
    public Task<QiwaReadinessReport> CheckEmployeeReadinessAsync(Guid tenantId, int employeeId, CancellationToken ct = default) => Unused<Task<QiwaReadinessReport>>();
    public Task<QiwaReadinessSummary> GetReadinessSummaryAsync(Guid tenantId, CancellationToken ct = default) => Unused<Task<QiwaReadinessSummary>>();
    public Task<QiwaComplianceSummary> GetComplianceSummaryAsync(Guid tenantId, CancellationToken ct = default) => Unused<Task<QiwaComplianceSummary>>();
    public Task<QiwaSyncLog> EnqueueEmployeeSyncAsync(Guid tenantId, int employeeId, string direction, string triggerSource, Guid? triggeredBy, CancellationToken ct = default) => Unused<Task<QiwaSyncLog>>();
    public Task<QiwaBulkSyncResult> EnqueueBulkSyncAsync(Guid tenantId, string triggerSource, Guid? triggeredBy, CancellationToken ct = default) => Unused<Task<QiwaBulkSyncResult>>();
    public Task RetryDeadLetterAsync(Guid tenantId, Guid syncLogId, Guid retriedBy, CancellationToken ct = default) => Unused<Task>();
    public Task<IReadOnlyList<QiwaSyncLog>> GetSyncLogsAsync(Guid tenantId, int? employeeId, int page, int pageSize, CancellationToken ct = default) => Unused<Task<IReadOnlyList<QiwaSyncLog>>>();
}
