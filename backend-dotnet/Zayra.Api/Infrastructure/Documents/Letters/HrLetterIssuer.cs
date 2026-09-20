using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Documents.Letters;

/// <summary>What the caller asked for. Everything else the issuer resolves from the database.</summary>
public record IssueLetterCommand(
    Guid TenantId,
    int EmployeeId,
    string LetterType,
    string Language,
    string Purpose,
    string AddresseeName,
    Guid? IssuedByUserId,
    string IssuerName,
    string IssuerTitle,
    Guid? DocumentRequestId = null,
    Guid? HrRequestId = null
);

/// <summary>
/// Outcome of an issuance. A refusal carries a machine-readable code so the controller maps it to
/// a status without string-matching, and <see cref="UnresolvedTokens"/> so HR is told which merge
/// field is empty rather than "something went wrong".
/// </summary>
public record LetterIssueResult(
    bool Ok,
    IssuedLetter? Letter = null,
    byte[]? Pdf = null,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    IReadOnlyList<string>? UnresolvedTokens = null
)
{
    public static LetterIssueResult Fail(string code, string message, IReadOnlyList<string>? tokens = null) =>
        new(false, null, null, code, message, tokens);
}

/// <summary>
/// The rendered document, frozen at issuance, so a reprint is the same document.
/// Logo bytes are intentionally NOT stored (they live in document storage and are re-fetched);
/// everything that carries meaning is.
/// </summary>
public record StoredLetterContent(
    LetterSection? English,
    LetterSection? Arabic,
    string CompanyNameEn,
    string CompanyNameAr,
    string RegistrationNumber,
    string PrimaryColorHex
);

public interface IHrLetterIssuer
{
    /// <summary>
    /// Allocates a reference, merges the tenant's template, renders the PDF and writes the
    /// register row — in that order, in one SaveChanges. Either all of it happened or none of it
    /// did: a PDF that leaves the building without a register row is the defect this replaces.
    /// </summary>
    Task<LetterIssueResult> IssueAsync(IssueLetterCommand command, CancellationToken cancellationToken);

    /// <summary>
    /// Seeds or repairs the tenant-default templates. Idempotent; never overwrites a tenant's edits.
    /// </summary>
    Task<int> EnsureDefaultTemplatesAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>
    /// Re-renders an already-issued letter from its frozen content. Same reference, same words,
    /// current logo. Does NOT allocate a new reference and does NOT write a new register row.
    /// </summary>
    Task<byte[]?> ReprintAsync(Guid tenantId, Guid issuedLetterId, CancellationToken cancellationToken);
}

public class HrLetterIssuer : IHrLetterIssuer
{
    private readonly ZayraDbContext _db;
    private readonly ILetterService _letters;
    private readonly IDocumentStorage _storage;
    private readonly ILogger<HrLetterIssuer>? _log;

    public HrLetterIssuer(
        ZayraDbContext db,
        ILetterService letters,
        IDocumentStorage storage,
        ILogger<HrLetterIssuer>? log = null)
    {
        _db = db;
        _letters = letters;
        _storage = storage;
        _log = log;
    }

    public async Task<int> EnsureDefaultTemplatesAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        // Seeding must see templates belonging to EVERY company in the tenant, not only the ones
        // the caller's entity scope covers, or a company-scoped admin re-seeds duplicates that
        // then trip ux_hr_letter_templates_scope_type.
        var existing = await ScopedBypass.TenantWide(_db.HrLetterTemplates, tenantId,
                "Template seeding must observe every company's templates inside its own tenant.")
            .Where(x => x.CompanyId == null && !x.IsDeleted)
            .Select(x => x.LetterType)
            .ToListAsync(cancellationToken);

        var added = 0;
        foreach (var template in HrLetterTemplateDefaults.Build())
        {
            if (existing.Contains(template.LetterType, StringComparer.Ordinal)) continue;
            template.Id = Guid.NewGuid();
            template.TenantId = tenantId;
            template.CompanyId = null;
            _db.HrLetterTemplates.Add(template);
            added++;
        }
        if (added > 0) await _db.SaveChangesAsync(cancellationToken);
        return added;
    }

    public async Task<byte[]?> ReprintAsync(Guid tenantId, Guid issuedLetterId, CancellationToken cancellationToken)
    {
        var record = await _db.IssuedLetters.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == issuedLetterId && !x.IsDeleted, cancellationToken);
        if (record is null) return null;

        StoredLetterContent? content;
        try { content = JsonSerializer.Deserialize<StoredLetterContent>(record.RenderedContentJson); }
        catch (JsonException) { content = null; }
        if (content is null || (content.English is null && content.Arabic is null)) return null;

        byte[]? logo = null;
        var branding = await _db.TenantBrandings.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId, cancellationToken);
        if (!string.IsNullOrWhiteSpace(branding?.LogoUrl))
        {
            try { logo = await _storage.GetBytesAsync(tenantId, branding!.LogoUrl, cancellationToken); }
            catch (Exception ex) { _log?.LogWarning(ex, "Reprint logo unavailable for tenant {TenantId}.", tenantId); }
        }

        return await _letters.GenerateTemplateLetterAsync(new TemplateLetterData(
            LetterType: record.LetterType,
            ReferenceNumber: record.ReferenceNumber,
            Language: record.Language,
            English: content.English,
            Arabic: content.Arabic,
            Letterhead: new LetterheadData(
                CompanyNameEn: content.CompanyNameEn,
                CompanyNameAr: content.CompanyNameAr,
                RegistrationNumber: content.RegistrationNumber,
                LogoBytes: logo,
                PrimaryColorHex: content.PrimaryColorHex),
            IssuerName: record.IssuedByName,
            IssuerTitle: record.IssuedByTitle,
            // The original issue date, not today: a reprint is a copy, not a new document.
            IssuedOn: record.IssuedAtUtc), cancellationToken);
    }

    public async Task<LetterIssueResult> IssueAsync(IssueLetterCommand command, CancellationToken cancellationToken)
    {
        var letterType = HrLetterTypes.Normalize(command.LetterType);
        if (letterType is null)
            return LetterIssueResult.Fail("unknown_letter_type",
                $"'{command.LetterType}' is not a letter this product issues. Known types: {string.Join(", ", HrLetterTypes.All)}.");

        var language = HrLetterLanguages.IsKnown(command.Language)
            ? command.Language!.ToLowerInvariant()
            : HrLetterLanguages.Bilingual;

        var employee = await _db.Employees.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == command.TenantId && x.Id == command.EmployeeId && !x.IsDeleted, cancellationToken);
        if (employee is null)
            return LetterIssueResult.Fail("employee_not_found", "Employee not found.");

        var template = await ResolveTemplateAsync(command.TenantId, employee.CompanyId, letterType, cancellationToken);
        if (template is null)
            return LetterIssueResult.Fail("template_not_configured",
                $"No active {letterType} template is configured for this tenant. Configure one under Setup → HR Letter Templates.");

        // A template may be English-only or Arabic-only. Asking it for a language it does not
        // carry must refuse, not silently issue a half-blank page.
        if (language is HrLetterLanguages.Arabic or HrLetterLanguages.Bilingual
            && template.Language == HrLetterLanguages.English)
            return LetterIssueResult.Fail("language_not_available",
                $"The configured {letterType} template is English-only. Add Arabic wording to it before issuing in Arabic.");
        if (language is HrLetterLanguages.English or HrLetterLanguages.Bilingual
            && template.Language == HrLetterLanguages.Arabic)
            return LetterIssueResult.Fail("language_not_available",
                $"The configured {letterType} template is Arabic-only. Add English wording to it before issuing in English.");

        var values = await BuildMergeValuesAsync(command, employee, letterType, cancellationToken);
        var letterhead = await BuildLetterheadAsync(command.TenantId, employee.CompanyId, values, cancellationToken);

        // Reference first: it is one of the merge values, it is printed on the page, and it is
        // the register key. Allocating it after rendering would mean rendering twice.
        var (reference, sequence, year) = await AllocateReferenceAsync(command.TenantId, letterType, cancellationToken);
        values["reference_number"] = reference;

        var unresolved = new List<string>();
        LetterSection? english = null;
        LetterSection? arabic = null;

        if (language is HrLetterLanguages.English or HrLetterLanguages.Bilingual)
            english = RenderSection(template.TitleEn, template.BodyEn, template.ClosingEn, values, unresolved);
        if (language is HrLetterLanguages.Arabic or HrLetterLanguages.Bilingual)
            arabic = RenderSection(template.TitleAr, template.BodyAr, template.ClosingAr, values, unresolved);

        if (unresolved.Count > 0)
            return LetterIssueResult.Fail("unresolved_merge_fields",
                "The letter cannot be issued because the employee record is missing data the template needs: "
                + string.Join(", ", unresolved.Distinct(StringComparer.OrdinalIgnoreCase))
                + ". Complete the record, or edit the template to drop the field.",
                [.. unresolved.Distinct(StringComparer.OrdinalIgnoreCase)]);

        var issuedOn = DateTime.UtcNow;
        var pdf = await _letters.GenerateTemplateLetterAsync(new TemplateLetterData(
            LetterType: letterType,
            ReferenceNumber: reference,
            Language: language,
            English: english,
            Arabic: arabic,
            Letterhead: letterhead,
            IssuerName: command.IssuerName,
            IssuerTitle: command.IssuerTitle,
            IssuedOn: issuedOn), cancellationToken);

        var record = new IssuedLetter
        {
            TenantId = command.TenantId,
            CompanyId = employee.CompanyId,
            EmployeeId = employee.Id,
            EmployeeCode = employee.EmployeeCode,
            EmployeeName = FirstNonEmpty(employee.EnglishName, employee.FullName),
            LetterType = letterType,
            ReferenceNumber = reference,
            SequenceNumber = sequence,
            SequenceYear = year,
            TemplateId = template.Id,
            Language = language,
            Purpose = Truncate(command.Purpose, 500),
            AddresseeName = Truncate(values.GetValueOrDefault("addressee", string.Empty), 200),
            IssuedByUserId = command.IssuedByUserId,
            IssuedByName = Truncate(command.IssuerName, 200),
            IssuedByTitle = Truncate(command.IssuerTitle, 200),
            IssuedAtUtc = issuedOn,
            FileHash = Convert.ToHexString(SHA256.HashData(pdf)).ToLowerInvariant(),
            FileSizeBytes = pdf.Length,
            MergedValuesJson = JsonSerializer.Serialize(values),
            RenderedContentJson = JsonSerializer.Serialize(new StoredLetterContent(english, arabic, letterhead.CompanyNameEn, letterhead.CompanyNameAr, letterhead.RegistrationNumber, letterhead.PrimaryColorHex)),
            DocumentRequestId = command.DocumentRequestId,
            HrRequestId = command.HrRequestId,
        };

        _db.IssuedLetters.Add(record);
        await _db.SaveChangesAsync(cancellationToken);

        _log?.LogInformation(
            "Issued {LetterType} {Reference} for employee {EmployeeId} in tenant {TenantId}.",
            letterType, reference, employee.Id, command.TenantId);

        return new LetterIssueResult(true, record, pdf);
    }

    private static LetterSection RenderSection(
        string title, string body, string closing,
        IReadOnlyDictionary<string, string> values, List<string> unresolved)
    {
        var renderedTitle = LetterTemplateRenderer.Render(title, values, out var t1);
        var renderedBody = LetterTemplateRenderer.Render(body, values, out var t2);
        var renderedClosing = LetterTemplateRenderer.Render(closing, values, out var t3);
        unresolved.AddRange(t1);
        unresolved.AddRange(t2);
        unresolved.AddRange(t3);
        return new LetterSection(renderedTitle, LetterTemplateRenderer.Paragraphs(renderedBody), renderedClosing);
    }

    /// <summary>
    /// Company override first, tenant default second. A group tenant can give one legal entity its
    /// own wording without forking the other eleven.
    /// </summary>
    private async Task<HrLetterTemplate?> ResolveTemplateAsync(
        Guid tenantId, Guid? companyId, string letterType, CancellationToken cancellationToken)
    {
        var candidates = await _db.HrLetterTemplates.AsNoTracking()
            .Where(x => x.TenantId == tenantId
                        && x.LetterType == letterType
                        && x.IsActive
                        && !x.IsDeleted
                        && (x.CompanyId == null || x.CompanyId == companyId))
            .ToListAsync(cancellationToken);

        return candidates.FirstOrDefault(x => x.CompanyId != null)
               ?? candidates.FirstOrDefault();
    }

    /// <summary>
    /// Allocates the next ordinal in the (tenant, letter type, calendar year) series and formats
    /// it as e.g. <c>SAL-CERT-2026-00042</c>.
    ///
    /// <para>Concurrency: the ordinal is read, incremented and inserted, and the unique index
    /// <c>ux_issued_letters_series_ordinal</c> is what makes a lost update impossible — two
    /// simultaneous issuances race, one insert violates the index, and the retry re-reads the
    /// now-higher maximum. This is deliberately not a counter table: a counter row is a per-tenant
    /// write hotspot and, worse, a counter that is incremented before the insert commits leaves
    /// gaps in a series an auditor will ask about.</para>
    /// </summary>
    private async Task<(string Reference, int Sequence, int Year)> AllocateReferenceAsync(
        Guid tenantId, string letterType, CancellationToken cancellationToken)
    {
        var year = DateTime.UtcNow.Year;
        var prefix = HrLetterTypes.Prefixes[letterType];

        // Load-bearing bypass: the reference series is per TENANT, so the highest ordinal must be
        // read across every company. Through the company-scope filter the series would restart at
        // 1 for each legal entity and hand two employees the same reference number — the exact
        // defect this table exists to prevent. TenantWide re-applies the tenant predicate.
        var highest = await ScopedBypass.TenantWide(_db.IssuedLetters, tenantId,
                "The letter reference series is allocated per tenant, across all its companies.")
            .Where(x => x.LetterType == letterType && x.SequenceYear == year)
            .Select(x => (int?)x.SequenceNumber)
            .MaxAsync(cancellationToken) ?? 0;

        var next = highest + 1;
        return ($"{prefix}-{year}-{next:D5}", next, year);
    }

    private async Task<Dictionary<string, string>> BuildMergeValuesAsync(
        IssueLetterCommand command, Employee employee, string letterType, CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var salary = await _db.EmployeeSalaryStructures.AsNoTracking()
            .Where(x => x.TenantId == command.TenantId && x.EmployeeId == employee.Id
                        && x.IsActive && x.EffectiveDate <= today)
            .OrderByDescending(x => x.EffectiveDate)
            .FirstOrDefaultAsync(cancellationToken);

        var currency = !string.IsNullOrWhiteSpace(salary?.Currency)
            ? salary!.Currency
            : await _db.ResolveTenantCurrencyAsync(command.TenantId, cancellationToken);

        var basic = salary?.BasicSalary ?? employee.Salary ?? 0m;
        var allowances = salary is null
            ? 0m
            : salary.HousingAllowance + salary.TransportAllowance + salary.FoodAllowance
              + salary.MobileAllowance + salary.OtherAllowance;
        var gross = basic + allowances;
        var net = gross - (salary?.FixedDeduction ?? 0m);

        var leaving = employee.ContractEndDate?.ToDateTime(TimeOnly.MinValue);
        var serviceEnd = leaving ?? DateTime.UtcNow;

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["employee_name"] = FirstNonEmpty(employee.EnglishName, employee.FullName),
            // Falls back to the English name when no Arabic name is recorded. An Arabic letter
            // naming the person in Latin script is correct and routine; a blank name is not.
            ["employee_name_ar"] = FirstNonEmpty(employee.ArabicName, employee.FullName, employee.EnglishName),
            ["employee_code"] = employee.EmployeeCode,
            ["designation"] = employee.Designation,
            ["department"] = employee.Department,
            ["nationality"] = employee.Nationality,
            ["national_id"] = FirstNonEmpty(employee.IqamaNumber, employee.IdNumber),
            ["passport_number"] = employee.PassportNumber,
            ["joining_date"] = employee.JoiningDate.ToString("dd MMMM yyyy", CultureInfo.InvariantCulture),
            ["leaving_date"] = leaving?.ToString("dd MMMM yyyy", CultureInfo.InvariantCulture) ?? string.Empty,
            // Numeric dates for the Arabic body. An English month name is a left-to-right run
            // inside a right-to-left paragraph: the bidi algorithm handles it correctly, and the
            // correct handling is "14 March" on one line and ".2021" on the next, which reads as
            // broken. dd/MM/yyyy is also what GCC Arabic correspondence uses.
            ["joining_date_ar"] = employee.JoiningDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
            ["leaving_date_ar"] = leaving?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) ?? string.Empty,
            ["issue_date_ar"] = DateTime.UtcNow.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
            ["service_duration"] = DescribeService(employee.JoiningDate, serviceEnd),
            ["basic_salary"] = basic.ToString("N2", CultureInfo.InvariantCulture),
            ["allowances"] = allowances.ToString("N2", CultureInfo.InvariantCulture),
            ["gross_salary"] = gross.ToString("N2", CultureInfo.InvariantCulture),
            ["net_salary"] = net.ToString("N2", CultureInfo.InvariantCulture),
            ["currency"] = currency,
            ["currency_ar"] = CurrencyInArabic(currency),
            ["bank_name"] = employee.BankName,
            ["bank_iban"] = employee.BankIban,
            ["issue_date"] = DateTime.UtcNow.ToString("dd MMMM yyyy", CultureInfo.InvariantCulture),
            // Defaults so a requester who left the field blank still gets a grammatical sentence.
            ["purpose"] = string.IsNullOrWhiteSpace(command.Purpose) ? "the employee's personal records" : command.Purpose.Trim(),
            ["addressee"] = string.IsNullOrWhiteSpace(command.AddresseeName) ? "Whom It May Concern" : command.AddresseeName.Trim(),
            ["issuer_name"] = command.IssuerName,
            ["issuer_title"] = command.IssuerTitle,
        };

        // The experience certificate is the one letter that needs an end date. If the employee is
        // still serving, use today — "to date" is what an experience letter for a current employee
        // means, and leaving the token unresolved would refuse an issuance that is perfectly valid.
        if (letterType == HrLetterTypes.ExperienceCertificate && string.IsNullOrWhiteSpace(values["leaving_date"]))
        {
            values["leaving_date"] = DateTime.UtcNow.ToString("dd MMMM yyyy", CultureInfo.InvariantCulture);
            values["leaving_date_ar"] = DateTime.UtcNow.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        }

        return values;
    }

    private async Task<LetterheadData> BuildLetterheadAsync(
        Guid tenantId, Guid? companyId, Dictionary<string, string> values, CancellationToken cancellationToken)
    {
        // The letterhead names the EMPLOYING legal entity, which for a group HR user issuing on
        // another entity's behalf may sit outside their own entity scope. Company is ITenantOwned
        // and not itself company-scoped, so TenantWide re-applies the only boundary that matters.
        var company = companyId is Guid cid
            ? await ScopedBypass.TenantWide(_db.Companies, tenantId,
                    "A letter's letterhead names the employing legal entity inside the same tenant.")
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == cid && !x.IsDeleted, cancellationToken)
            : null;

        var branding = await _db.TenantBrandings.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId, cancellationToken);

        var tenantName = await _db.Tenants.AsNoTracking()
            .Where(x => x.Id == tenantId).Select(x => x.Name).FirstOrDefaultAsync(cancellationToken);

        // Precedence: the employing legal entity's registered name, then tenant branding, then the
        // tenant name. The hard-coded "KynexOne Technologies" fallback the old letters used is gone
        // — a letter that names the vendor instead of the employer is not a letter, it is a bug.
        var nameEn = FirstNonEmpty(
            company?.LegalNameEn, company?.TradeName, branding?.CompanyNameEn, tenantName, "Unnamed Company");
        var nameAr = FirstNonEmpty(company?.LegalNameAr, branding?.CompanyNameAr);

        byte[]? logo = null;
        if (!string.IsNullOrWhiteSpace(branding?.LogoUrl))
        {
            try
            {
                logo = await _storage.GetBytesAsync(tenantId, branding!.LogoUrl, cancellationToken);
            }
            catch (Exception ex)
            {
                // Non-fatal. A missing logo is a cosmetic problem; refusing the salary certificate
                // the employee needs for a loan appointment is not a proportionate response.
                _log?.LogWarning(ex, "Letterhead logo could not be loaded for tenant {TenantId}.", tenantId);
            }
        }

        values["company_name"] = nameEn;
        values["company_name_ar"] = string.IsNullOrWhiteSpace(nameAr) ? nameEn : nameAr;
        values["company_registration_number"] = company?.RegistrationNumber ?? string.Empty;

        return new LetterheadData(
            CompanyNameEn: nameEn,
            CompanyNameAr: nameAr,
            RegistrationNumber: company?.RegistrationNumber ?? string.Empty,
            LogoBytes: logo,
            PrimaryColorHex: string.IsNullOrWhiteSpace(branding?.PrimaryColor) ? "#1E3A5F" : branding!.PrimaryColor);
    }

    private static string DescribeService(DateTime from, DateTime to)
    {
        if (to < from) return "less than one month";
        var months = ((to.Year - from.Year) * 12) + to.Month - from.Month;
        if (to.Day < from.Day) months--;
        if (months < 1) return "less than one month";
        var years = months / 12;
        var remainder = months % 12;
        var parts = new List<string>();
        if (years > 0) parts.Add($"{years} year{(years == 1 ? "" : "s")}");
        if (remainder > 0) parts.Add($"{remainder} month{(remainder == 1 ? "" : "s")}");
        return string.Join(" and ", parts);
    }

    /// <summary>
    /// GCC currency names in Arabic. A salary certificate that prints "SAR" in the middle of an
    /// Arabic sentence is the sort of detail a bank clerk rejects the document over.
    /// </summary>
    private static string CurrencyInArabic(string code) => code.ToUpperInvariant() switch
    {
        "SAR" => "ريال سعودي",
        "AED" => "درهم إماراتي",
        "KWD" => "دينار كويتي",
        "BHD" => "دينار بحريني",
        "QAR" => "ريال قطري",
        "OMR" => "ريال عماني",
        "EGP" => "جنيه مصري",
        "JOD" => "دينار أردني",
        "USD" => "دولار أمريكي",
        "EUR" => "يورو",
        "GBP" => "جنيه إسترليني",
        _ => code,
    };

    private static string FirstNonEmpty(params string?[] candidates) =>
        candidates.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim() ?? string.Empty;

    private static string Truncate(string? value, int max)
    {
        var trimmed = (value ?? string.Empty).Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}
