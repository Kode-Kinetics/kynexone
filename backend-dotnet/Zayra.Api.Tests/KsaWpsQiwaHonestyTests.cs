using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports;
using Zayra.Api.Infrastructure.Qiwa;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// WS3 — Saudi WPS/Mudad and Qiwa honesty. Fixtures live in Fixtures/Wps: one golden file every rule
/// passes, and one negative file per stable error code (a patch over the golden file). A reference
/// validator, written here independently of the product code, re-checks the generated bank instruction.
/// </summary>
public class KsaWpsQiwaHonestyTests
{
    private static readonly string FixtureDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Wps");

    // ── Fixture plumbing ─────────────────────────────────────────────────────────────────────────

    private static JsonObject Golden() => JsonNode.Parse(File.ReadAllText(Path.Combine(FixtureDir, "golden.json")))!.AsObject();

    public static IEnumerable<object[]> NegativeFixtures() =>
        Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Wps", "negative"), "*.json")
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => new object[] { Path.GetFileNameWithoutExtension(f) });

    private static (KsaWageFileHeader Header, List<KsaWageFileRow> Rows, List<string> Used) Load(JsonObject doc)
    {
        var h = doc["header"]!.AsObject();
        var header = new KsaWageFileHeader(
            (string?)h["molEstablishmentId"], (string?)h["currency"], (string?)h["fileReference"],
            (bool)h["autoWpsUpload"]!, (string?)h["nationalUnifiedNo"]);
        var rows = doc["rows"]!.AsArray().Select(n => n!.AsObject()).Select(r => new KsaWageFileRow(
            (int)r["employeeRef"]!, (string)r["label"]!, (string?)r["nationality"], (string?)r["molId"], (string?)r["iban"],
            (string?)r["bankBic"], (string?)r["name"], (decimal)r["gross"]!, (decimal)r["basic"]!, (decimal)r["housing"]!,
            (decimal)r["net"]!, (decimal)r["slipDeductions"]!)).ToList();
        var used = doc["usedReferences"]?.AsArray().Select(x => (string)x!).ToList() ?? new List<string>();
        return (header, rows, used);
    }

    private static JsonObject Apply(JsonObject golden, JsonObject patch)
    {
        if (patch["header"] is JsonObject hp)
            foreach (var kv in hp) golden["header"]![kv.Key] = kv.Value?.DeepClone();
        if (patch["rows"] is JsonObject rp)
            foreach (var kv in rp)
                foreach (var field in kv.Value!.AsObject())
                    golden["rows"]![int.Parse(kv.Key, CultureInfo.InvariantCulture)]![field.Key] = field.Value?.DeepClone();
        if (patch["clearRows"] is JsonValue cr && (bool)cr) golden["rows"] = new JsonArray();
        return golden;
    }

    private static string[] AllCodes() => typeof(KsaWageFileRules.Codes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).ToArray();

    // ── 1. Golden + negative fixtures, one per stable error code ────────────────────────────────

    [Fact]
    public void Golden_fixture_passes_every_rule()
    {
        var (header, rows, used) = Load(Golden());
        KsaWageFileRules.Validate(header, rows, used).Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(NegativeFixtures))]
    public void Negative_fixture_is_blocked_with_its_stable_code_and_a_plain_message(string fixture)
    {
        var doc = JsonNode.Parse(File.ReadAllText(Path.Combine(FixtureDir, "negative", fixture + ".json")))!.AsObject();
        var expected = (string)doc["expectedCode"]!;
        expected.Should().Be(fixture, "each negative fixture is named after the one code it proves");

        var (header, rows, used) = Load(Apply(Golden(), doc["patch"]!.AsObject()));
        var errors = KsaWageFileRules.Validate(header, rows, used);

        errors.Should().Contain(e => e.Code == expected, $"fixture {fixture} must be refused as {expected}");
        var issue = errors.First(e => e.Code == expected);
        issue.Message.Should().NotBeNullOrWhiteSpace();
        issue.Message.Should().NotContain(expected, "the message is plain English, not the code repeated");
    }

    [Fact]
    public void Every_error_code_has_a_negative_fixture()
    {
        var fixtures = NegativeFixtures().Select(x => (string)x[0]).ToHashSet(StringComparer.Ordinal);
        AllCodes().Should().OnlyContain(code => fixtures.Contains(code), "a rule without a failing fixture is untested");
        fixtures.Should().OnlyContain(f => AllCodes().Contains(f), "a fixture must name a real code");
    }

    [Fact]
    public void Aed_is_blocked_and_sar_passes()
    {
        var (header, rows, used) = Load(Golden());
        KsaWageFileRules.Validate(header with { Currency = "AED" }, rows, used)
            .Should().ContainSingle(e => e.Code == KsaWageFileRules.Codes.CurrencyNotSar)
            .Which.Message.Should().Contain("SAR only").And.Contain("AED");
        KsaWageFileRules.Validate(header with { Currency = "SAR" }, rows, used).Should().BeEmpty();
    }

    [Theory]
    [InlineData("0000000000", KsaWageFileRules.Codes.EstablishmentPlaceholder)]
    [InlineData("0-0000000", KsaWageFileRules.Codes.EstablishmentPlaceholder)]
    [InlineData("", KsaWageFileRules.Codes.EstablishmentMissing)]
    [InlineData(null, KsaWageFileRules.Codes.EstablishmentMissing)]
    [InlineData("7-1234567-1", KsaWageFileRules.Codes.EstablishmentInvalid)]
    [InlineData("1234567890123456", KsaWageFileRules.Codes.EstablishmentInvalid)]
    public void Establishment_id_never_accepts_a_placeholder(string? value, string code)
    {
        var errors = new List<SaudiBankExportIssueDto>();
        KsaWageFileRules.ValidateEstablishmentId(value, errors);
        errors.Should().ContainSingle().Which.Code.Should().Be(code);
    }

    [Fact]
    public void Every_known_iban_bank_code_maps_to_a_four_letter_sarie_id()
    {
        KsaWageFileRules.SarieIdByIbanBankCode.Should().NotBeEmpty();
        KsaWageFileRules.SarieIdByIbanBankCode.Should().OnlyContain(kv =>
            kv.Key.Length == 2 && kv.Key.All(char.IsAsciiDigit) && kv.Value.Length == 4 && kv.Value.All(char.IsAsciiLetterUpper));
        KsaWageFileRules.SarieIdByIbanBankCode["80"].Should().Be("RJHI");
        KsaWageFileRules.SarieIdByIbanBankCode["30"].Should().Be("ARNB");
    }

    // ── 2. The ANB bank instruction, re-checked by an independent reference validator ───────────

    private static ZayraDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<(ZayraDbContext Db, Guid Tenant, Guid Company, Guid Batch, SaudiBankExportService Svc)> ArrangeAnb(
        bool autoWps = false, int month = 9)
    {
        var db = NewDb();
        var tenant = Guid.NewGuid();
        var company = await SaudiBankExportTestData.SeedCompanyAsync(db, tenant);
        var batch = await SaudiBankExportTestData.SeedBatchAsync(db, tenant, company, month);
        var svc = new SaudiBankExportService(db, new SaudiBankExportTestData.FakeAddresses());
        var settings = SaudiBankExportTestData.Settings();
        settings.AutoWpsUpload = autoWps;
        settings.NationalUnifiedNo = autoWps ? "7001234567" : string.Empty;
        (await svc.SaveSettingsAsync(tenant, company, Guid.NewGuid(), settings, default)).Outcome.Should().Be(SaudiBankExportOutcome.Ok);
        return (db, tenant, company, batch, svc);
    }

    private static async Task<(string Header, string Body)> GenerateAndUnzip(SaudiBankExportService svc, Guid tenant, Guid batch,
        SaudiBankExportBatchRequest request)
    {
        var gen = await svc.GenerateAsync(tenant, Guid.NewGuid(), batch, request, default);
        gen.Outcome.Should().Be(SaudiBankExportOutcome.Ok, string.Join("; ", gen.Validation?.Errors.Select(e => e.Code) ?? Array.Empty<string>()));
        var dl = await svc.DownloadAsync(tenant, Guid.NewGuid(), batch, default);
        dl.Outcome.Should().Be(SaudiBankExportOutcome.Ok);
        using var zip = new ZipArchive(new MemoryStream(dl.Value!.ZipBytes));
        string Read(string name) { using var r = new StreamReader(zip.GetEntry(name)!.Open(), Encoding.UTF8); return r.ReadToEnd(); }
        return (Read("header.csv"), Read("body.csv"));
    }

    [Fact]
    public async Task Generated_anb_instruction_passes_the_reference_validator()
    {
        var (_, tenant, _, batch, svc) = await ArrangeAnb();
        var (header, body) = await GenerateAndUnzip(svc, tenant, batch, SaudiBankExportTestData.Request());

        ReferenceBankInstructionValidator.Check(header, body, expectAutoWps: false).Should().BeEmpty();
        (header + body).Should().NotContain("0000000000");
    }

    [Fact]
    public async Task Auto_wps_upload_puts_the_switch_and_national_unified_number_in_the_header_only_when_enabled()
    {
        var (_, tenant, _, batch, svc) = await ArrangeAnb(autoWps: true);
        var (header, body) = await GenerateAndUnzip(svc, tenant, batch, SaudiBankExportTestData.Request());

        var lines = header.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines[0].Should().EndWith(",autoWpsFileUpload,nationalUnifiedNo");
        lines[1].Should().EndWith(",YES,7001234567");
        ReferenceBankInstructionValidator.Check(header, body, expectAutoWps: true).Should().BeEmpty();

        var (_, tenant2, _, batch2, svc2) = await ArrangeAnb(autoWps: false);
        var (header2, _) = await GenerateAndUnzip(svc2, tenant2, batch2, SaudiBankExportTestData.Request());
        header2.Should().NotContain("autoWpsFileUpload", "a file without the option stays byte-identical to before");
    }

    [Fact]
    public void Reference_validator_is_not_vacuous()
    {
        var header = "batchNumber,batchType,molEstablishmentId,mainAccountNumber,creditValueDate,organizationName,organizationAddress1,organizationAddress2,organizationAddress3,paymentCount,totalPayrollAmount,narrative,companyName\r\n"
                   + "2026092601,PAYROLL,0000000000,0108061198800026,261001,Org,A,B,C,1,6300,N,Co\r\n";
        var body = "employeeId,employeeAccountNumber,salaryAmount,basicSalary,housingAllowance,otherEarnings,salaryDeductions,bicCode,employeeName,employeeAddress1,employeeAddress2,employeeAddress3\r\n"
                 + "3012345678,SA0480000000608010167519,6300,5000,1250,500,999,RIBLSARI,=Omar,R,O,K\r\n";
        var problems = ReferenceBankInstructionValidator.Check(header, body, expectAutoWps: false);
        problems.Should().Contain(p => p.Contains("placeholder establishment"));
        problems.Should().Contain(p => p.Contains("id prefix"));
        problems.Should().Contain(p => p.Contains("mod-97"));
        problems.Should().Contain(p => p.Contains("net"));
        problems.Should().Contain(p => p.Contains("name"));
    }

    [Fact]
    public async Task Anb_export_settings_refuse_auto_wps_without_a_ten_digit_national_unified_number()
    {
        var (_, tenant, company, _, svc) = await ArrangeAnb();
        var settings = SaudiBankExportTestData.Settings();
        settings.AutoWpsUpload = true;
        settings.NationalUnifiedNo = "";
        var missing = await svc.SaveSettingsAsync(tenant, company, Guid.NewGuid(), settings, default);
        missing.Outcome.Should().Be(SaudiBankExportOutcome.Invalid);
        missing.Validation!.Errors.Should().Contain(e => e.Code == KsaWageFileRules.Codes.NationalUnifiedNoMissing);

        settings.NationalUnifiedNo = "70012";
        (await svc.SaveSettingsAsync(tenant, company, Guid.NewGuid(), settings, default))
            .Validation!.Errors.Should().Contain(e => e.Code == KsaWageFileRules.Codes.NationalUnifiedNoInvalid);

        settings.MolEstablishmentId = "0000000000";
        settings.NationalUnifiedNo = "7001234567";
        (await svc.SaveSettingsAsync(tenant, company, Guid.NewGuid(), settings, default))
            .Validation!.Errors.Should().Contain(e => e.Code == KsaWageFileRules.Codes.EstablishmentPlaceholder);
    }

    [Fact]
    public async Task Anb_export_blocks_an_aed_batch()
    {
        var (db, tenant, _, batch, svc) = await ArrangeAnb();
        var b = await db.PayrollPaymentBatches.SingleAsync(x => x.Id == batch);
        b.Currency = "AED";
        await db.SaveChangesAsync();

        var v = (await svc.ValidateAsync(tenant, batch, SaudiBankExportTestData.Request(), default)).Value!;
        v.CanExport.Should().BeFalse();
        v.Errors.Should().ContainSingle(e => e.Code == KsaWageFileRules.Codes.CurrencyNotSar);
    }

    [Fact]
    public async Task Anb_export_blocks_a_file_reference_longer_than_sixteen()
    {
        var (_, tenant, _, batch, svc) = await ArrangeAnb();
        var v = (await svc.ValidateAsync(tenant, batch, SaudiBankExportTestData.Request("12345678901234567"), default)).Value!;
        v.Errors.Should().Contain(e => e.Code == KsaWageFileRules.Codes.FileReferenceInvalid);
    }

    [Fact]
    public async Task Anb_export_blocks_an_iban_whose_bank_code_contradicts_the_recorded_bic()
    {
        var (db, tenant, _, batch, svc) = await ArrangeAnb();
        // E1 is paid into an Al Rajhi IBAN (bank code 80); record ANB's BIC against them.
        var e1 = await db.Employees.SingleAsync(e => e.TenantId == tenant && e.IdNumber == "1012345678");
        e1.WpsBankDetails = JsonSerializer.Serialize(new { schema = ApprovedSaudiBeneficiaryDetails.Schema, bicCode = "ARNBSARI" });
        await db.SaveChangesAsync();

        var v = (await svc.ValidateAsync(tenant, batch, SaudiBankExportTestData.Request(), default)).Value!;
        v.Errors.Should().Contain(e => e.Code == KsaWageFileRules.Codes.BankCodeMismatch && e.EmployeeId == e1.Id);
    }

    [Fact]
    public async Task File_reference_is_unique_per_establishment_across_history_including_voided_runs()
    {
        var (db, tenant, company, batch, svc) = await ArrangeAnb();
        await GenerateAndUnzip(svc, tenant, batch, SaudiBankExportTestData.Request("2026100501"));

        // Void the run the reference was spent on. Its artifact stays, and so does the reference.
        var b = await db.PayrollPaymentBatches.SingleAsync(x => x.Id == batch);
        var run = await db.PayrollRuns.SingleAsync(r => r.Id == b.PayrollRunId);
        run.Status = "Voided";
        b.WpsStatus = WpsStatuses.Voided;
        await db.SaveChangesAsync();

        // A second legal entity, same MOL establishment, different employer account.
        var company2 = await SaudiBankExportTestData.SeedCompanyAsync(db, tenant);
        var batch2 = await SaudiBankExportTestData.SeedBatchAsync(db, tenant, company2, 10);
        var settings2 = SaudiBankExportTestData.Settings();
        settings2.MainAccountNumber = "0108061198800099";
        (await svc.SaveSettingsAsync(tenant, company2, Guid.NewGuid(), settings2, default)).Outcome.Should().Be(SaudiBankExportOutcome.Ok);

        var v = (await svc.ValidateAsync(tenant, batch2, SaudiBankExportTestData.Request("2026100501"), default)).Value!;
        v.Errors.Should().Contain(e => e.Code == KsaWageFileRules.Codes.FileReferenceReused);
        var fresh = (await svc.ValidateAsync(tenant, batch2, SaudiBankExportTestData.Request("2026100502"), default)).Value!;
        fresh.Errors.Should().NotContain(e => e.Code == KsaWageFileRules.Codes.FileReferenceReused);
        _ = company;
    }

    // ── 3. The legacy KSA export is an internal register; no placeholder anywhere ───────────────

    private static PayrollController Payroll(ZayraDbContext db, Guid tenantId, Zayra.Api.Infrastructure.Documents.IDocumentStorage? storage = null)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new("permission", "payroll.export"),
            new("is_group_scope", "true"),
        };
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) };
        var rules = new StubRuleReader();
        var ctrl = new PayrollController(db, new _WqScope(), new _WqHttp(http), new _WqNotifications(), new _WqKsaPackResolver(),
            rules, new _WqLetters(), storage ?? new MemoryDocumentStorage(), new Zayra.Api.Infrastructure.Documents.PdfRenderGate(1));
        ctrl.ControllerContext = new ControllerContext { HttpContext = http };
        return ctrl;
    }

    private static async Task<(Guid Tenant, Guid Company, Guid Batch)> SeedKsaRegisterBatchAsync(ZayraDbContext db, string currency = "SAR")
    {
        var tenant = Guid.NewGuid();
        var company = new Company { TenantId = tenant, LegalNameEn = "Test Establishment", CountryCode = "SA", DefaultCurrency = "SAR" };
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
            TenantId = tenant, EmployeeId = emp.Id, Iban = SaudiBankExportTestData.IbanA, SalaryCurrency = currency,
            BankRoutingCode = "RJHISARI", MolId = "FREE-TEXT-MOL", WpsEligible = true,
        });
        var batch = new PayrollPaymentBatch
        {
            TenantId = tenant, PayrollRunId = run.Id, BatchNumber = "PB-1", PaymentMethod = "WPS", TotalAmount = 6300m,
            Currency = currency, Status = "Pending", WpsStatus = WpsStatuses.Draft,
        };
        db.PayrollPaymentBatches.Add(batch);
        db.PayrollPaymentRecords.Add(new PayrollPaymentRecord
        {
            TenantId = tenant, PaymentBatchId = batch.Id, EmployeeId = emp.Id, Amount = 6300m, Iban = SaudiBankExportTestData.IbanA, Status = "Pending",
        });
        await db.SaveChangesAsync();
        return (tenant, company.Id, batch.Id);
    }

    private static string Json(IActionResult r) => JsonSerializer.Serialize((r as ObjectResult)?.Value);

    [Fact]
    public async Task Ksa_register_refuses_without_an_establishment_id_instead_of_printing_zeros()
    {
        using var db = NewDb();
        var (tenant, _, batch) = await SeedKsaRegisterBatchAsync(db);

        var res = await Payroll(db, tenant).GenerateWps(batch, true, false, default);

        res.Should().BeOfType<UnprocessableEntityObjectResult>();
        Json(res).Should().Contain(KsaWageFileRules.Codes.EstablishmentMissing);
        db.WPSFileBatches.Should().BeEmpty();
    }

    [Fact]
    public async Task Ksa_register_refuses_a_stored_all_zero_establishment_id()
    {
        using var db = NewDb();
        var (tenant, company, batch) = await SeedKsaRegisterBatchAsync(db);
        db.GCCComplianceSettings.Add(new GCCComplianceSetting { TenantId = tenant, CompanyId = company, CountryCode = "SA", WpsAgentId = "0000000000" });
        await db.SaveChangesAsync();

        var res = await Payroll(db, tenant).GenerateWps(batch, true, false, default);
        Json(res).Should().Contain(KsaWageFileRules.Codes.EstablishmentPlaceholder);
    }

    [Fact]
    public async Task Ksa_register_blocks_aed()
    {
        using var db = NewDb();
        var (tenant, company, batch) = await SeedKsaRegisterBatchAsync(db, currency: "AED");
        db.GCCComplianceSettings.Add(new GCCComplianceSetting { TenantId = tenant, CompanyId = company, CountryCode = "SA", WpsAgentId = "7-1234567" });
        await db.SaveChangesAsync();

        var res = await Payroll(db, tenant).GenerateWps(batch, true, false, default);
        Json(res).Should().Contain(KsaWageFileRules.Codes.CurrencyNotSar);
    }

    [Fact]
    public async Task Ksa_register_is_labelled_internal_everywhere_and_carries_the_employee_id_not_the_profile_mol_id()
    {
        using var db = NewDb();
        var (tenant, company, batch) = await SeedKsaRegisterBatchAsync(db);
        db.GCCComplianceSettings.Add(new GCCComplianceSetting { TenantId = tenant, CompanyId = company, CountryCode = "SA", WpsAgentId = "7-1234567" });
        await db.SaveChangesAsync();
        var ctrl = Payroll(db, tenant);

        var gen = await ctrl.GenerateWps(batch, true, false, default);
        gen.Should().BeOfType<OkObjectResult>(Json(gen));
        Json(gen).Should().Contain("Payroll register (internal").And.Contain(WpsConformance.KsaPayrollRegisterFormat);

        var dl = await ctrl.DownloadWpsFile(batch, default);
        var file = dl.Should().BeOfType<FileContentResult>().Subject;
        var xml = Encoding.UTF8.GetString(file.FileContents);
        file.FileDownloadName.Should().StartWith("payroll-register_INTERNAL-not-a-bank-or-WPS-file_");
        xml.Should().Contain("<PayrollRegister").And.Contain("NotABankOrWpsFile=\"true\"").And.NotContain("<MudadWPS");
        xml.Should().Contain("<MolEstablishmentId>7-1234567</MolEstablishmentId>");
        xml.Should().Contain("<GovernmentId>1012345678</GovernmentId>").And.NotContain("FREE-TEXT-MOL");
        xml.Should().NotContain("0000000000");
        ctrl.Response.Headers[WpsConformance.DownloadHeader].ToString()
            .Should().Contain("kind=internal-payroll-register").And.Contain("not-a-bank-or-wps-file=true");
        db.SIFFileRecords.Single().MolId.Should().Be("1012345678");
    }

    [Fact]
    public void Conformance_labels_legacy_ksa_formats_as_the_internal_register()
    {
        foreach (var f in new[] { "mudad-xml", "SIF_SA_V1", WpsConformance.KsaPayrollRegisterFormat })
        {
            WpsConformance.LabelFor(f).Should().Be(WpsConformance.KsaPayrollRegisterLabel);
            WpsConformance.For(f).FormatLabel.Should().NotContain("Mudad").And.StartWith("Payroll register (internal");
        }
        new KsaDescriptor().GetDescriptor().WpsFormatLabel.Should().NotContain("Mudad XML");
    }

    // ── 4. "Accepted" only with stored evidence ──────────────────────────────────────────────────

    private static IFormFile FormFile(byte[] bytes, string name, string contentType) =>
        new Microsoft.AspNetCore.Http.FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", name)
            { Headers = new HeaderDictionary(), ContentType = contentType };

    private static async Task<(ZayraDbContext Db, Guid Tenant, Guid Batch, PayrollController Ctrl, MemoryDocumentStorage Storage)> SubmittedBatchAsync()
    {
        var db = NewDb();
        var (tenant, company, batch) = await SeedKsaRegisterBatchAsync(db);
        db.GCCComplianceSettings.Add(new GCCComplianceSetting { TenantId = tenant, CompanyId = company, CountryCode = "SA", WpsAgentId = "7-1234567" });
        await db.SaveChangesAsync();
        var storage = new MemoryDocumentStorage();
        var ctrl = Payroll(db, tenant, storage);
        (await ctrl.GenerateWps(batch, true, false, default)).Should().BeOfType<OkObjectResult>();
        (await ctrl.UpdateWpsStatus(batch, new WpsStatusRequest(WpsStatuses.Submitted, null, "SUB-1"), default)).Should().BeOfType<OkObjectResult>();
        return (db, tenant, batch, ctrl, storage);
    }

    private static Guid EvidenceIdOf(IActionResult upload)
    {
        var value = upload.Should().BeOfType<OkObjectResult>().Subject.Value!;
        return (Guid)value.GetType().GetProperty("EvidenceId")!.GetValue(value)!;
    }

    [Fact]
    public async Task Accepted_from_the_dropdown_alone_is_a_400()
    {
        var (db, _, batch, ctrl, _) = await SubmittedBatchAsync();
        var res = await ctrl.UpdateWpsStatus(batch, new WpsStatusRequest(WpsStatuses.Accepted, null, "ACK-1"), default);
        res.Should().BeOfType<BadRequestObjectResult>();
        Json(res).Should().Contain("acceptance_evidence_required");
        (await db.PayrollPaymentBatches.AsNoTracking().SingleAsync(b => b.Id == batch)).WpsStatus.Should().Be(WpsStatuses.Submitted);
    }

    [Fact]
    public async Task Accepted_with_a_stored_bank_output_file_records_its_server_side_sha256()
    {
        var (db, _, batch, ctrl, storage) = await SubmittedBatchAsync();
        var bankFile = Encoding.UTF8.GetBytes("[DEST-ID]\tRJHI\n[FILE-REF]\t2026090101\n-----SIGNATURE-----\n");

        var upload = await ctrl.UploadWpsEvidence(batch, new WpsEvidenceUploadForm
        {
            Kind = WpsEvidenceKinds.BankOutputFile, File = FormFile(bankFile, "WPS_OUT.txt", "text/plain"),
        }, default);
        var evidenceId = EvidenceIdOf(upload);
        Json(upload).Should().Contain(WpsAcceptanceEvidenceService.Sha256Hex(bankFile));
        storage.Objects.Values.Should().ContainSingle().Which.Should().Equal(bankFile, "the bank's bytes are preserved exactly");

        (await ctrl.UpdateWpsStatus(batch, new WpsStatusRequest(WpsStatuses.Accepted, null, "ACK-1", evidenceId), default))
            .Should().BeOfType<OkObjectResult>();
        (await db.PayrollPaymentBatches.AsNoTracking().SingleAsync(b => b.Id == batch)).WpsStatus.Should().Be(WpsStatuses.Accepted);
        db.PayrollAuditLogs.Should().Contain(a => a.Action == "payroll.wps.status_changed" && a.MetadataJson.Contains(evidenceId.ToString()));
        db.PayrollAuditLogs.Should().Contain(a => a.Action == "payroll.wps.evidence_uploaded");

        var dl = await ctrl.DownloadWpsEvidence(batch, evidenceId, default);
        dl.Should().BeOfType<FileContentResult>().Subject.FileContents.Should().Equal(bankFile);
    }

    [Fact]
    public async Task A_mudad_screenshot_must_really_be_an_image_or_pdf()
    {
        var (_, _, batch, ctrl, _) = await SubmittedBatchAsync();
        var notAPng = Encoding.UTF8.GetBytes("this is not a png");
        var bad = await ctrl.UploadWpsEvidence(batch, new WpsEvidenceUploadForm
        {
            Kind = WpsEvidenceKinds.MudadComplianceScreenshot, File = FormFile(notAPng, "mudad.png", "image/png"),
        }, default);
        bad.Should().BeOfType<BadRequestObjectResult>();
        Json(bad).Should().Contain("evidence_file_type_invalid");

        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52, 1, 2, 3 };
        var ok = await ctrl.UploadWpsEvidence(batch, new WpsEvidenceUploadForm
        {
            Kind = WpsEvidenceKinds.MudadComplianceScreenshot, File = FormFile(png, "mudad.png", "image/png"),
        }, default);
        (await ctrl.UpdateWpsStatus(batch, new WpsStatusRequest(WpsStatuses.Accepted, null, "ACK-2", EvidenceIdOf(ok)), default))
            .Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Tampered_evidence_cannot_be_used_to_accept()
    {
        var (_, _, batch, ctrl, storage) = await SubmittedBatchAsync();
        var upload = await ctrl.UploadWpsEvidence(batch, new WpsEvidenceUploadForm
        {
            Kind = WpsEvidenceKinds.BankOutputFile, File = FormFile(Encoding.UTF8.GetBytes("bank-signed-output"), "out.txt", "text/plain"),
        }, default);
        var key = storage.Objects.Keys.Single();
        storage.Objects[key] = Encoding.UTF8.GetBytes("edited after upload");

        var res = await ctrl.UpdateWpsStatus(batch, new WpsStatusRequest(WpsStatuses.Accepted, null, "ACK-3", EvidenceIdOf(upload)), default);
        res.Should().BeOfType<ConflictObjectResult>();
        Json(res).Should().Contain("acceptance_evidence_integrity_failed");
    }

    [Fact]
    public async Task Evidence_for_one_batch_cannot_prove_another()
    {
        var (db, tenant, batch, ctrl, _) = await SubmittedBatchAsync();
        var bytes = Encoding.UTF8.GetBytes("one month's bank file");
        EvidenceIdOf(await ctrl.UploadWpsEvidence(batch, new WpsEvidenceUploadForm
        {
            Kind = WpsEvidenceKinds.BankOutputFile, File = FormFile(bytes, "a.txt", "text/plain"),
        }, default));

        // A second batch in the same tenant re-using the exact same file.
        var other = await db.PayrollPaymentBatches.AsNoTracking().SingleAsync(b => b.Id == batch);
        var clone = new PayrollPaymentBatch
        {
            TenantId = tenant, PayrollRunId = other.PayrollRunId, BatchNumber = "PB-2", PaymentMethod = "WPS",
            TotalAmount = 1m, Currency = "SAR", Status = "Pending", WpsStatus = WpsStatuses.Draft,
        };
        db.PayrollPaymentBatches.Add(clone);
        await db.SaveChangesAsync();
        var reuse = await ctrl.UploadWpsEvidence(clone.Id, new WpsEvidenceUploadForm
        {
            Kind = WpsEvidenceKinds.BankOutputFile, File = FormFile(bytes, "a.txt", "text/plain"),
        }, default);
        reuse.Should().BeOfType<ConflictObjectResult>();
        Json(reuse).Should().Contain("evidence_already_used");
    }

    [Fact]
    public async Task A_frozen_anb_instruction_counts_as_the_generated_file_for_the_status_lifecycle()
    {
        var (db, tenant, _, batch, svc) = await ArrangeAnb();
        await GenerateAndUnzip(svc, tenant, batch, SaudiBankExportTestData.Request());
        var ctrl = Payroll(db, tenant);

        (await ctrl.UpdateWpsStatus(batch, new WpsStatusRequest(WpsStatuses.Submitted, null, "ANB-SUB-1"), default))
            .Should().BeOfType<OkObjectResult>();
        (await ctrl.UpdateWpsStatus(batch, new WpsStatusRequest(WpsStatuses.Accepted, null, "ACK"), default))
            .Should().BeOfType<BadRequestObjectResult>("Accepted still needs evidence on the ANB path");
    }

    // ── 5. Qiwa: the live adapter is refused without a partner agreement ─────────────────────────

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    [Fact]
    public void Live_switch_without_a_partner_agreement_is_refused()
    {
        QiwaLiveAdapterPolicy.Decide(liveRequested: false, hasPartnerAgreement: false).Should().Be(QiwaLiveAdapterPolicy.Mode.Sandbox);
        QiwaLiveAdapterPolicy.Decide(liveRequested: false, hasPartnerAgreement: true).Should().Be(QiwaLiveAdapterPolicy.Mode.Sandbox);
        QiwaLiveAdapterPolicy.Decide(liveRequested: true, hasPartnerAgreement: false).Should().Be(QiwaLiveAdapterPolicy.Mode.RefusedLive);
        QiwaLiveAdapterPolicy.Decide(liveRequested: true, hasPartnerAgreement: true).Should().Be(QiwaLiveAdapterPolicy.Mode.Live);

        QiwaLiveAdapterPolicy.Decide(Config((QiwaLiveAdapterPolicy.LiveSwitchEnvVar, "true")))
            .Should().Be(QiwaLiveAdapterPolicy.Mode.RefusedLive);
        QiwaLiveAdapterPolicy.Decide(Config((QiwaLiveAdapterPolicy.LiveSwitchEnvVar, "true"), (QiwaLiveAdapterPolicy.PartnerAgreementKey, "MHRSD-QIWA-2026-001")))
            .Should().Be(QiwaLiveAdapterPolicy.Mode.Live);
        QiwaLiveAdapterPolicy.CredentialFormEnabled(Config()).Should().BeFalse("the credential form is off by default");
    }

    [Fact]
    public void The_live_adapter_refuses_to_exist_without_a_partner_agreement()
    {
        var act = () => new LiveQiwaApiAdapter(new _WqHttpFactory(), NullLogger<LiveQiwaApiAdapter>.Instance, Config());
        act.Should().Throw<InvalidOperationException>().WithMessage("*partner agreement*");
    }

    [Fact]
    public async Task The_refused_adapter_never_reports_live_and_every_qiwa_write_is_501()
    {
        var adapter = new RefusedLiveQiwaApiAdapter();
        adapter.IsLiveIntegration.Should().BeFalse();
        (await adapter.AcquireAccessTokenAsync("id", "secret", "production", default)).Should().BeNull();
        (await adapter.PushEmployeeAsync("t", new QiwaEmployeePayload("E", "1", "NationalId", "SA", "Saudi", "1", "7-1", "W", "C"), Guid.NewGuid(), default))
            .ErrorCode.Should().Be(QiwaLiveAdapterPolicy.RefusedCode);

        var ctrl = Qiwa(adapter, Config((QiwaLiveAdapterPolicy.CredentialFormKey, "true")));
        foreach (var res in new[]
                 {
                     await ctrl.EnqueueSync(1, "Push", default),
                     await ctrl.EnqueueBulkSync(default),
                     await ctrl.RetryDeadLetter(Guid.NewGuid(), default),
                     await ctrl.SaveCredentials(new QiwaCredentialRequest("id", "secret", "sandbox"), default),
                 })
        {
            res.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status501NotImplemented);
            Json(res).Should().Contain(QiwaLiveAdapterPolicy.RefusedCode);
        }
    }

    [Fact]
    public async Task Credential_form_is_off_by_default_and_the_api_refuses_secrets()
    {
        var ctrl = Qiwa(new SandboxQiwaApiAdapter(NullLogger<SandboxQiwaApiAdapter>.Instance), Config());
        var res = await ctrl.SaveCredentials(new QiwaCredentialRequest("id", "secret", "sandbox"), default);
        res.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status501NotImplemented);
        Json(res).Should().Contain(QiwaController.CredentialsDisabledCode);

        var conn = Json(await ctrl.GetConnection(default));
        conn.Should().Contain("\"credentialFormEnabled\":false");
        conn.Should().Contain(QiwaSyncLogStatuses.SimulatedLabel);
    }

    [Fact]
    public void No_qiwa_label_a_screen_can_show_says_synced_connected_live_or_filed()
    {
        var labels = new List<string>
        {
            QiwaSyncLogStatuses.ModeLabel(false), QiwaSyncLogStatuses.ModeLabel(true), QiwaSyncLogStatuses.SimulatedLabel,
            QiwaController.DataCheckNotice, QiwaController.LiveNotConfiguredMessage, QiwaLiveAdapterPolicy.RefusedMessage,
        };
        labels.AddRange(new[]
            {
                QiwaSyncLogStatuses.Pending, QiwaSyncLogStatuses.Processing, QiwaSyncLogStatuses.Success, QiwaSyncLogStatuses.Failed,
                QiwaSyncLogStatuses.Skipped, QiwaSyncLogStatuses.DeadLetter, QiwaSyncLogStatuses.Simulated,
            }.Select(s => QiwaSyncLogStatuses.Describe(s, null)));

        foreach (var label in labels)
        {
            label.Should().NotContainEquivalentOf("synced");
            label.Should().NotContainEquivalentOf("connected");
            label.Should().NotMatchRegex(@"\bLive\b");
            label.Should().NotContainEquivalentOf("filed with qiwa");
        }
    }

    private static QiwaController Qiwa(IQiwaApiAdapter adapter, IConfiguration config)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", Guid.NewGuid().ToString()), new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new("permission", "qiwa.read"), new("permission", "qiwa.sync"), new("permission", "qiwa.configure"),
        };
        var ctrl = new QiwaController(new _WqQiwaService(), adapter, config)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) },
            },
        };
        return ctrl;
    }
}

/// <summary>
/// An independent re-implementation of the Saudi wage-file rules, applied to the CSV the product
/// actually wrote. It shares no code with KsaWageFileRules or AnbConnectCsvGenerator on purpose: if the
/// product's validator and generator drift together, this still catches it.
/// </summary>
internal static class ReferenceBankInstructionValidator
{
    private static readonly Dictionary<string, string> Sarie = new() { ["80"] = "RJHI", ["30"] = "ARNB", ["20"] = "RIBL", ["10"] = "NCBK" };

    public static List<string> Check(string headerCsv, string bodyCsv, bool expectAutoWps)
    {
        var problems = new List<string>();
        if (headerCsv.Contains("0000000000") || bodyCsv.Contains("0000000000")) problems.Add("placeholder establishment or id (0000000000)");

        var hl = headerCsv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var hCols = hl[0].Split(',');
        var hVals = hl[1].Split(',');
        string H(string c) => hVals[Array.IndexOf(hCols, c)];
        var est = H("molEstablishmentId");
        if (est.Where(char.IsDigit).All(c => c == '0')) problems.Add("placeholder establishment");
        var batchNo = H("batchNumber");
        if (batchNo.Length is < 1 or > 16 || !batchNo.All(char.IsDigit)) problems.Add("file reference not 1..16 digits");
        if (expectAutoWps)
        {
            if (!hCols.Contains("autoWpsFileUpload") || H("autoWpsFileUpload") != "YES") problems.Add("auto-WPS switch missing");
            if (!hCols.Contains("nationalUnifiedNo") || H("nationalUnifiedNo").Length != 10 || !H("nationalUnifiedNo").All(char.IsDigit))
                problems.Add("national unified number not 10 digits");
        }

        var bl = bodyCsv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var bCols = bl[0].Split(',');
        decimal total = 0m;
        var ids = new HashSet<string>();
        var accounts = new HashSet<string>();
        foreach (var line in bl.Skip(1))
        {
            var v = line.Split(',');
            string B(string c) => v[Array.IndexOf(bCols, c)];
            decimal D(string c) => decimal.Parse(B(c), NumberStyles.Number, CultureInfo.InvariantCulture);

            var id = B("employeeId");
            if (id.Length != 10 || !id.All(char.IsDigit) || id[0] is not ('1' or '2')) problems.Add($"id prefix/length invalid: {id}");
            if (!ids.Add(id)) problems.Add($"duplicate id {id}");

            var iban = B("employeeAccountNumber");
            if (iban.Length != 24 || !iban.StartsWith("SA") || iban != iban.ToUpperInvariant() || !Mod97(iban))
                problems.Add($"IBAN not a valid upper-case 24-char SA IBAN (mod-97): {iban}");
            else if (!Sarie.TryGetValue(iban.Substring(4, 2), out var sarie) || !B("bicCode").StartsWith(sarie))
                problems.Add($"bank code / BIC mismatch for {iban}");
            if (!accounts.Add(iban)) problems.Add($"duplicate account {iban}");

            var net = D("salaryAmount");
            var basic = D("basicSalary"); var housing = D("housingAllowance"); var other = D("otherEarnings"); var ded = D("salaryDeductions");
            if (net != basic + housing + other - ded) problems.Add($"net != basic + housing + other - deductions for {id}");
            if (ded > (basic + housing + other) / 2m) problems.Add($"deductions over 50% for {id}");
            total += net;

            var name = B("employeeName");
            if (name.Length is < 1 or > 35 || name.Any(char.IsControl) || name[0] is '=' or '+' or '-' or '@')
                problems.Add($"name invalid for {id}");
        }
        if (int.Parse(H("paymentCount"), CultureInfo.InvariantCulture) != bl.Length - 1) problems.Add("payment count mismatch");
        if (decimal.Parse(H("totalPayrollAmount"), CultureInfo.InvariantCulture) != total) problems.Add("header total != sum of net");
        return problems;
    }

    private static bool Mod97(string iban)
    {
        var r = iban[4..] + iban[..4];
        var rem = 0;
        foreach (var c in r)
        {
            var s = char.IsLetter(c) ? (c - 'A' + 10).ToString(CultureInfo.InvariantCulture) : c.ToString();
            foreach (var d in s) rem = (rem * 10 + (d - '0')) % 97;
        }
        return rem == 1;
    }
}

// ── File-scoped stubs ───────────────────────────────────────────────────────────────────────────

file sealed class _WqScope : Zayra.Api.Application.Common.IDataScopeService
{
    public Task<Zayra.Api.Application.Common.DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
        => Task.FromResult(new Zayra.Api.Application.Common.DataScope { Level = Zayra.Api.Application.Common.DataScopeLevel.Organization, AllowedEmployeeIds = null });
}

file sealed class _WqHttp : IHttpContextAccessor
{
    public _WqHttp(HttpContext ctx) => HttpContext = ctx;
    public HttpContext? HttpContext { get; set; }
}

file sealed class _WqNotifications : Zayra.Api.Infrastructure.Notifications.INotificationService
{
    public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken ct) => Task.CompletedTask;
    public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken ct) => Task.CompletedTask;
}

file sealed class _WqLetters : Zayra.Api.Infrastructure.Documents.Letters.ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(Zayra.Api.Infrastructure.Documents.Letters.PayslipData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.OfferLetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}

file sealed class _WqKsaPackResolver : Zayra.Api.Application.CountryPack.ICountryPackResolver
{
    public Zayra.Api.Application.CountryPack.IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j) => new Zayra.Api.Infrastructure.CountryPack.DefaultStatutoryDeductionCalculator();
    public Zayra.Api.Application.CountryPack.IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j) => new Zayra.Api.Infrastructure.CountryPack.DefaultEndOfServiceCalculator();
    public Zayra.Api.Application.CountryPack.IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j) => new KsaWageProtectionExporter();
    public Zayra.Api.Application.CountryPack.INationalizationTracker ResolveNationalizationTracker(string cc, string j) => new Zayra.Api.Infrastructure.CountryPack.DefaultNationalizationTracker();
    public Zayra.Api.Application.CountryPack.ILocalizationProfile ResolveLocalizationProfile(string cc, string j) => new Zayra.Api.Infrastructure.CountryPack.DefaultLocalizationProfile();
    public Zayra.Api.Application.CountryPack.ICountryPackDescriptor ResolveDescriptor(string cc, string j) => new KsaDescriptor();
}

file sealed class _WqHttpFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => throw new InvalidOperationException("No network in tests.");
}

file sealed class _WqQiwaService : IQiwaIntegrationService
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
