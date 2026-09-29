using Microsoft.EntityFrameworkCore;
using UglyToad.PdfPig;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// B6 — the salary certificate, the register, and the reference number.
///
/// Every assertion here fails on develop, most of them by not compiling: there was no template
/// entity, no merge engine, no register, no reference allocator and no Arabic font. The two that
/// CAN be expressed against the old code are pulled out into
/// <see cref="HrLetterRegressionTests"/>, which characterises the defect before the fix.
/// </summary>
public class HrLetterIssuanceTests
{
    [Fact]
    public async Task SalaryCertificate_StatesTheRightFigures_AndCarriesReadableArabic()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenantAsync(db);
        var employee = await SeedEmployeeAsync(db, tenantId, basic: 12_000m, housing: 3_000m, transport: 1_500m);
        var issuer = CreateIssuer(db);

        var result = await issuer.IssueAsync(Command(tenantId, employee.Id, HrLetterTypes.SalaryCertificate), default);

        Assert.True(result.Ok, result.ErrorMessage);
        var pdf = result.Pdf!;

        // A real PDF, asserted on the file signature rather than a content-type header we set
        // ourselves. "%PDF-" is the ISO 32000 header.
        Assert.Equal("%PDF-"u8.ToArray(), pdf.Take(5).ToArray());

        using var document = PdfDocument.Open(pdf);
        var page = document.GetPage(1);

        // The figures. Basic 12,000 + housing 3,000 + transport 1,500 = gross 16,500.
        var text = Normalise(page.Text);
        Assert.Contains("12,000.00", text);
        Assert.Contains("16,500.00", text);
        Assert.Contains("SAR", text);
        Assert.Contains(result.Letter!.ReferenceNumber, text);

        // Readable Arabic, not tofu. Two independent checks, because either one alone can be
        // satisfied by an accident:
        //   (a) the embedded Arabic font is OURS. DocumentFonts sets UseEnvironmentFonts = false,
        //       so no host font can supply these glyphs — on develop this assertion fails on
        //       macOS (where Geeza Pro was silently borrowed) and on Linux (where the glyphs were
        //       simply missing and the page showed □□□□).
        var fonts = page.Letters.Select(x => x.FontName ?? string.Empty).Distinct().ToList();
        Assert.Contains(fonts, f => f.Contains("NotoSansArabic", StringComparison.OrdinalIgnoreCase));

        //   (b) glyphs were actually drawn from it. A registered-but-unused font would satisfy (a).
        var arabicGlyphs = page.Letters
            .Count(x => (x.FontName ?? string.Empty).Contains("NotoSansArabic", StringComparison.OrdinalIgnoreCase));
        Assert.True(arabicGlyphs > 50, $"expected an Arabic paragraph, drew {arabicGlyphs} glyphs from the Arabic font");
    }

    [Fact]
    public async Task TwoLettersOfTheSameTypeInTheSameMonth_GetDifferentReferenceNumbers()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenantAsync(db);
        var employee = await SeedEmployeeAsync(db, tenantId);
        var issuer = CreateIssuer(db);

        var first = await issuer.IssueAsync(Command(tenantId, employee.Id, HrLetterTypes.ExperienceCertificate), default);
        var second = await issuer.IssueAsync(Command(tenantId, employee.Id, HrLetterTypes.ExperienceCertificate), default);

        Assert.True(first.Ok, first.ErrorMessage);
        Assert.True(second.Ok, second.ErrorMessage);

        // The defect this replaces: LetterService printed "Ref: EXP-{EmployeeCode}-{yyyyMM}",
        // recomputed inline and stored nowhere, so these two were the same string.
        Assert.NotEqual(first.Letter!.ReferenceNumber, second.Letter!.ReferenceNumber);
        Assert.Equal(first.Letter.SequenceNumber + 1, second.Letter.SequenceNumber);
        Assert.StartsWith("EXP-", second.Letter.ReferenceNumber);

        // And the reference on the page is the one in the register, not a second computation.
        using var document = PdfDocument.Open(second.Pdf!);
        Assert.Contains(second.Letter.ReferenceNumber, Normalise(document.GetPage(1).Text));
    }

    [Fact]
    public async Task ReferenceSeries_IsPerLetterType_NotGlobal()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenantAsync(db);
        var employee = await SeedEmployeeAsync(db, tenantId);
        var issuer = CreateIssuer(db);

        var salary = await issuer.IssueAsync(Command(tenantId, employee.Id, HrLetterTypes.SalaryCertificate), default);
        var verification = await issuer.IssueAsync(Command(tenantId, employee.Id, HrLetterTypes.EmploymentVerification), default);

        Assert.True(salary.Ok, salary.ErrorMessage);
        Assert.True(verification.Ok, verification.ErrorMessage);
        Assert.Equal(1, salary.Letter!.SequenceNumber);
        Assert.Equal(1, verification.Letter!.SequenceNumber);
        Assert.StartsWith($"SAL-CERT-{DateTime.UtcNow.Year}-", salary.Letter.ReferenceNumber);
        Assert.StartsWith($"EMP-VER-{DateTime.UtcNow.Year}-", verification.Letter.ReferenceNumber);
    }

    [Fact]
    public async Task EveryIssuance_LandsInTheRegister_WithAnIssuerAndAHash()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenantAsync(db);
        var employee = await SeedEmployeeAsync(db, tenantId);
        var issuer = CreateIssuer(db);

        await issuer.IssueAsync(Command(tenantId, employee.Id, HrLetterTypes.SalaryCertificate), default);
        await issuer.IssueAsync(Command(tenantId, employee.Id, HrLetterTypes.SalaryTransferLetter), default);
        await issuer.IssueAsync(Command(tenantId, employee.Id, HrLetterTypes.EmploymentVerification), default);

        var register = await db.IssuedLetters.AsNoTracking().OrderBy(x => x.IssuedAtUtc).ToListAsync();

        Assert.Equal(3, register.Count);
        Assert.Equal(3, register.Select(x => x.ReferenceNumber).Distinct().Count());
        Assert.All(register, row =>
        {
            Assert.Equal(employee.Id, row.EmployeeId);
            Assert.Equal("EMP-0001", row.EmployeeCode);
            // Named issuer, not the hard-coded "HR Department" the old letters signed off with.
            Assert.Equal("Layla Al-Otaibi", row.IssuedByName);
            Assert.Equal("HR Manager", row.IssuedByTitle);
            Assert.Equal(64, row.FileHash.Length);           // SHA-256, hex
            Assert.True(row.FileSizeBytes > 1000);
            Assert.NotEqual("{}", row.MergedValuesJson);
        });
    }

    [Fact]
    public async Task Reprint_ReturnsTheSameDocumentUnderTheSameReference()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenantAsync(db);
        var employee = await SeedEmployeeAsync(db, tenantId);
        var issuer = CreateIssuer(db);

        var issued = await issuer.IssueAsync(Command(tenantId, employee.Id, HrLetterTypes.SalaryCertificate), default);
        Assert.True(issued.Ok, issued.ErrorMessage);

        var reprint = await issuer.ReprintAsync(tenantId, issued.Letter!.Id, default);

        Assert.NotNull(reprint);
        using var original = PdfDocument.Open(issued.Pdf!);
        using var copy = PdfDocument.Open(reprint!);
        Assert.Equal(Normalise(original.GetPage(1).Text), Normalise(copy.GetPage(1).Text));

        // A reprint is a copy, not a new document: no second register row, no second reference.
        Assert.Equal(1, await db.IssuedLetters.CountAsync());
    }

    [Fact]
    public async Task Issue_RefusesRatherThanPrintingABlankWhereASalaryBelongs()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenantAsync(db);
        var employee = await SeedEmployeeAsync(db, tenantId);

        // Wipe the one field the salary-certificate template needs and the employee record has
        // no fallback for.
        var tracked = await db.Employees.FirstAsync(x => x.Id == employee.Id);
        tracked.IqamaNumber = string.Empty;
        tracked.IdNumber = string.Empty;
        await db.SaveChangesAsync();

        var result = await CreateIssuer(db).IssueAsync(
            Command(tenantId, employee.Id, HrLetterTypes.SalaryCertificate), default);

        Assert.False(result.Ok);
        Assert.Equal("unresolved_merge_fields", result.ErrorCode);
        Assert.Contains("national_id", result.UnresolvedTokens!);
        // Nothing was issued, so nothing was registered and no reference was burned.
        Assert.Empty(await db.IssuedLetters.ToListAsync());
    }

    [Fact]
    public async Task Issue_RefusesALetterTypeTheProductDoesNotHave()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenantAsync(db);
        var employee = await SeedEmployeeAsync(db, tenantId);

        var result = await CreateIssuer(db).IssueAsync(
            Command(tenantId, employee.Id, "Salary Cert"), default);

        Assert.False(result.Ok);
        Assert.Equal("unknown_letter_type", result.ErrorCode);
    }

    [Fact]
    public async Task Issue_RefusesWhenTheTenantHasNoTemplate()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenantAsync(db, seedTemplates: false);
        var employee = await SeedEmployeeAsync(db, tenantId);

        var result = await CreateIssuer(db).IssueAsync(
            Command(tenantId, employee.Id, HrLetterTypes.SalaryCertificate), default);

        Assert.False(result.Ok);
        Assert.Equal("template_not_configured", result.ErrorCode);
    }

    [Fact]
    public async Task ATenantTemplateEdit_ChangesTheIssuedDocument()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenantAsync(db);
        var employee = await SeedEmployeeAsync(db, tenantId);

        var template = await db.HrLetterTemplates.FirstAsync(x => x.LetterType == HrLetterTypes.SalaryCertificate);
        template.BodyEn = "Our reference {{reference_number}}. {{employee_name}} earns {{gross_salary}} {{currency}} a month. Signed on behalf of the board.";
        await db.SaveChangesAsync();

        var result = await CreateIssuer(db).IssueAsync(
            Command(tenantId, employee.Id, HrLetterTypes.SalaryCertificate), default);

        Assert.True(result.Ok, result.ErrorMessage);
        using var document = PdfDocument.Open(result.Pdf!);
        Assert.Contains(Normalise("Signed on behalf of the board"), Normalise(document.GetPage(1).Text));
    }

    [Fact]
    public async Task EnsureDefaultTemplates_IsIdempotent_AndNeverOverwritesAnEdit()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenantAsync(db, seedTemplates: false);
        var issuer = CreateIssuer(db);

        var firstRun = await issuer.EnsureDefaultTemplatesAsync(tenantId, default);
        var edited = await db.HrLetterTemplates.FirstAsync(x => x.LetterType == HrLetterTypes.SalaryCertificate);
        edited.BodyEn = "Tenant wording.";
        await db.SaveChangesAsync();

        var secondRun = await issuer.EnsureDefaultTemplatesAsync(tenantId, default);

        Assert.Equal(HrLetterTemplateDefaults.Build().Count, firstRun);
        Assert.Equal(0, secondRun);
        Assert.Equal("Tenant wording.",
            (await db.HrLetterTemplates.AsNoTracking().FirstAsync(x => x.LetterType == HrLetterTypes.SalaryCertificate)).BodyEn);
    }

    [Fact]
    public void EveryDefaultTemplate_OnlyUsesTokensTheRendererCanSupply()
    {
        // A default that references a token nothing resolves would refuse every issuance for
        // every new tenant, and it would do so only in production.
        foreach (var template in HrLetterTemplateDefaults.Build())
        {
            var used = LetterTemplateRenderer.ExtractTokens(string.Join("\n",
                template.TitleEn, template.TitleAr, template.BodyEn, template.BodyAr,
                template.ClosingEn, template.ClosingAr));
            var unknown = used.Where(t => !HrLetterTemplateDefaults.KnownTokens.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList();
            Assert.True(unknown.Count == 0, $"{template.LetterType} uses unknown token(s): {string.Join(", ", unknown)}");
            Assert.NotEmpty(template.BodyEn);
            Assert.NotEmpty(template.BodyAr);
        }
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────

    private static ZayraDbContext CreateDb() => new(
        new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static HrLetterIssuer CreateIssuer(ZayraDbContext db) =>
        new(db, new LetterService(), new NullDocumentStorage());

    private static IssueLetterCommand Command(Guid tenantId, int employeeId, string letterType) =>
        new(tenantId, employeeId, letterType, HrLetterLanguages.Bilingual,
            "a bank loan application", "Riyad Bank",
            Guid.NewGuid(), "Layla Al-Otaibi", "HR Manager");

    private static async Task<Guid> SeedTenantAsync(ZayraDbContext db, bool seedTemplates = true)
    {
        var tenant = new Tenant { Name = "Letter Test Tenant", Slug = $"letters-{Guid.NewGuid():N}" };
        db.Tenants.Add(tenant);
        db.Companies.Add(new Company
        {
            Id = CompanyId,
            TenantId = tenant.Id,
            LegalNameEn = "Najd Industrial Services Company",
            LegalNameAr = "شركة نجد للخدمات الصناعية",
            RegistrationNumber = "1010123456",
            DefaultCurrency = "SAR",
            IsActive = true,
        });

        if (seedTemplates)
        {
            foreach (var template in HrLetterTemplateDefaults.Build())
            {
                template.TenantId = tenant.Id;
                db.HrLetterTemplates.Add(template);
            }
        }

        await db.SaveChangesAsync();
        return tenant.Id;
    }

    private static readonly Guid CompanyId = Guid.NewGuid();

    private static async Task<Employee> SeedEmployeeAsync(
        ZayraDbContext db, Guid tenantId,
        decimal basic = 10_000m, decimal housing = 2_500m, decimal transport = 1_000m)
    {
        var employee = new Employee
        {
            TenantId = tenantId,
            CompanyId = CompanyId,
            EmployeeCode = "EMP-0001",
            FullName = "Mohammed Abdullah Al-Harbi",
            EnglishName = "Mohammed Abdullah Al-Harbi",
            ArabicName = "محمد عبدالله الحربي",
            Department = "Operations",
            Designation = "Site Supervisor",
            Nationality = "Saudi",
            IqamaNumber = "1098765432",
            PassportNumber = "A12345678",
            BankName = "Riyad Bank",
            BankIban = "SA0380000000608010167519",
            Status = "Active",
            JoiningDate = new DateTime(2021, 3, 14, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();

        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = tenantId,
            EmployeeId = employee.Id,
            BasicSalary = basic,
            HousingAllowance = housing,
            TransportAllowance = transport,
            Currency = "SAR",
            IsActive = true,
            EffectiveDate = new DateOnly(2021, 3, 14),
        });
        await db.SaveChangesAsync();
        return employee;
    }

    /// <summary>
    /// PdfPig reconstructs text from glyph positions, so a line break inside a wrapped paragraph
    /// comes back as "Signed onbehalf" with no separator at all. Strip whitespace from both the
    /// haystack and the needle rather than pretend the extractor preserves it. NUL appears where
    /// PdfPig cannot reverse-map a glyph to a codepoint, which is common with subsetted fonts —
    /// it says nothing about what the page looks like, so it goes too.
    /// </summary>
    private static string Normalise(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text.Replace("\0", string.Empty), @"\s+", string.Empty);
}
