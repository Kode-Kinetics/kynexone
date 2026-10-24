using System.Globalization;
using Zayra.Api.Application.CompanySetup;
using Zayra.Api.Application.Organization;
using Zayra.Api.Application.WorkWeek;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.CompanySetup;

/// <summary>
/// Addendum A9 — the three-way comparison of existing mode. The prefill stamps each existing row (and the company,
/// numbering and work-week blocks) with a <c>baseline</c>: the field values it was read from, as normalized strings.
/// At plan time each field is judged on three values — the draft's, the baseline's and the database's now:
/// not changed by the user → the database value stands; changed by the user only → update; changed on both sides →
/// unchanged when they agree, otherwise <see cref="ChangedCode"/>. A row or block with no baseline (a draft saved before
/// A9) is refused with <see cref="RefreshCode"/>; nothing is guessed.
///
/// <para>One projection per entity serves both sides (the database row, and the draft body), so the prefill's baseline
/// and the planner's comparison can never normalize differently.</para>
/// </summary>
public static class CompanySetupBaseline
{
    public const string ChangedCode = "changed_since_draft_started";
    public const string RefreshCode = "draft_needs_refresh";

    public static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    /// <summary>Scale-free: 21.5 and 21.500000 (numeric column) are the same value.</summary>
    public static string? Number(decimal? value) => value?.ToString("G29", CultureInfo.InvariantCulture);
    public static string Flag(bool value) => value ? "true" : "false";
    public static string Whole(int value) => value.ToString(CultureInfo.InvariantCulture);
    public static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.Ordinal);

    // ── Company (step 2) ────────────────────────────────────────────────────────────────────────────

    public static Dictionary<string, string?> Company(Company c, CompanyCutover? cutover) => CompanyFields(
        c.LegalNameEn, c.LegalNameAr, c.TradeName, c.RegistrationNumber, c.WpsEmployerId, c.GosiEmployerId, c.UnifiedNationalNumber,
        c.TaxNumber, c.QiwaEstablishmentId, c.EmailDomain, c.WorkEmailPattern, c.ErpCompanyCode,
        cutover?.CutoverDate.ToString("yyyy-MM", CultureInfo.InvariantCulture));

    public static Dictionary<string, string?> Company(CompanySetupCompany c) => CompanyFields(
        c.LegalNameEn, c.LegalNameAr, c.TradeName, c.RegistrationNumber, c.MolEstablishmentNumber, c.GosiEmployerId, c.UnifiedNationalNumber,
        c.TaxNumber, c.QiwaEstablishmentId, c.EmailDomain, c.WorkEmailPattern, c.ErpCompanyCode, c.FirstPayrollMonth);

    private static Dictionary<string, string?> CompanyFields(string? legalNameEn, string? legalNameAr, string? tradeName, string? registration,
        string? mol, string? gosi, string? unified, string? tax, string? qiwa, string? emailDomain, string? workEmailPattern, string? erp, string? month) =>
        new(StringComparer.Ordinal)
        {
            ["legalNameEn"] = Text(legalNameEn),
            ["legalNameAr"] = Text(legalNameAr),
            ["tradeName"] = Text(tradeName),
            ["registrationNumber"] = Text(registration),
            ["molEstablishmentNumber"] = Text(mol),
            ["gosiEmployerId"] = Text(gosi),
            ["unifiedNationalNumber"] = Text(unified),
            ["taxNumber"] = Text(tax),
            ["qiwaEstablishmentId"] = Text(qiwa),
            ["emailDomain"] = Text(CompanyEmailDomainRules.Normalize(emailDomain)),
            ["workEmailPattern"] = Text(WorkEmailPatterns.Normalize(workEmailPattern)),
            ["erpCompanyCode"] = Text(erp),
            ["firstPayrollMonth"] = Text(month),
        };

    // ── Branches, locations, cost centres (steps 3–4) ───────────────────────────────────────────────

    public static Dictionary<string, string?> Branch(Branch b, string packTimeZone) =>
        BranchFields(b.NameEn, b.NameAr, b.City, b.TimeZoneId, b.IsHeadOffice, packTimeZone);

    public static Dictionary<string, string?> Branch(CompanySetupBranch b, string packTimeZone) =>
        BranchFields(b.NameEn, b.NameAr, b.City, b.TimeZoneId, b.IsHeadOffice, packTimeZone);

    private static Dictionary<string, string?> BranchFields(string? nameEn, string? nameAr, string? city, string? tz, bool head, string packTimeZone) =>
        new(StringComparer.Ordinal)
        {
            ["nameEn"] = Text(nameEn),
            ["nameAr"] = Text(nameAr),
            ["city"] = Text(city),
            ["timeZoneId"] = Text(tz) ?? packTimeZone,
            ["isHeadOffice"] = Flag(head),
        };

    public static Dictionary<string, string?> Location(Location l) => LocationFields(l.NameEn, l.NameAr, l.Latitude, l.Longitude, l.GeofenceRadiusMeters);
    public static Dictionary<string, string?> Location(CompanySetupLocation l) => LocationFields(l.NameEn, l.NameAr, l.Latitude, l.Longitude, l.RadiusMeters);

    private static Dictionary<string, string?> LocationFields(string? nameEn, string? nameAr, decimal? lat, decimal? lng, decimal? radius) =>
        new(StringComparer.Ordinal)
        {
            ["nameEn"] = Text(nameEn),
            ["nameAr"] = Text(nameAr),
            ["latitude"] = Number(lat),
            ["longitude"] = Number(lng),
            ["radiusMeters"] = Number(radius),
        };

    public static Dictionary<string, string?> CostCentre(CostCenter c) => new(StringComparer.Ordinal) { ["nameEn"] = Text(c.Name), ["nameAr"] = Text(c.NameAr) };
    public static Dictionary<string, string?> CostCentre(CompanySetupCostCentre c) => new(StringComparer.Ordinal) { ["nameEn"] = Text(c.NameEn), ["nameAr"] = Text(c.NameAr) };

    /// <summary>Keys a baseline may lack because the value did not exist when it was taken: the cost centre's Arabic name
    /// (stored only since Addendum B — before it the column did not exist, so the value read then was empty).</summary>
    public static bool AbsentMeansEmpty(string area, string key) => area == "costCentre" && key == "nameAr";

    // ── Departments, designations, grades (step 4; Addendum B: owned by one company) ─────────────────

    /// <summary>A link as the baseline holds it: the linked row's normalized code, null when there is none.</summary>
    public static string? Code(string? code) => string.IsNullOrWhiteSpace(code) ? null : OrgCodes.Normalize(code);
    private static string? Code(Guid? id, IReadOnlyDictionary<Guid, string> codes) => id is Guid x && codes.TryGetValue(x, out var c) ? c : null;

    public static Dictionary<string, string?> Department(Department d, IReadOnlyDictionary<Guid, string> departmentCodes,
        IReadOnlyDictionary<Guid, string> branchCodes, IReadOnlyDictionary<Guid, string> costCentreCodes) =>
        DepartmentFields(d.NameEn, d.NameAr, Code(d.ParentDepartmentId, departmentCodes), Code(d.BranchId, branchCodes), Code(d.CostCenterId, costCentreCodes));

    public static Dictionary<string, string?> Department(CompanySetupDepartment d) =>
        DepartmentFields(d.NameEn, d.NameAr, Code(d.ParentCode), Code(d.BranchCode), Code(d.CostCentreCode));

    private static Dictionary<string, string?> DepartmentFields(string? nameEn, string? nameAr, string? parent, string? branch, string? costCentre) =>
        new(StringComparer.Ordinal)
        {
            ["nameEn"] = Text(nameEn),
            ["nameAr"] = Text(nameAr),
            ["parentCode"] = parent,
            ["branchCode"] = branch,
            ["costCentreCode"] = costCentre,
        };

    public static Dictionary<string, string?> Designation(Designation d, IReadOnlyDictionary<Guid, string> departmentCodes, IReadOnlyDictionary<Guid, string> gradeCodes) =>
        DesignationFields(d.TitleEn, d.TitleAr, Code(d.DepartmentId, departmentCodes), Code(d.GradeId, gradeCodes));

    public static Dictionary<string, string?> Designation(CompanySetupDesignation d) =>
        DesignationFields(d.TitleEn, d.TitleAr, Code(d.DepartmentCode), Code(d.GradeCode));

    private static Dictionary<string, string?> DesignationFields(string? titleEn, string? titleAr, string? department, string? grade) =>
        new(StringComparer.Ordinal)
        {
            ["titleEn"] = Text(titleEn),
            ["titleAr"] = Text(titleAr),
            ["departmentCode"] = department,
            ["gradeCode"] = grade,
        };

    public static Dictionary<string, string?> Grade(Grade g) => GradeFields(g.Name, g.NameAr, g.Level, g.MinSalary, g.MidSalary, g.MaxSalary);
    public static Dictionary<string, string?> Grade(CompanySetupGrade g) => GradeFields(g.NameEn, g.NameAr, g.Level, g.MinSalary, g.MidSalary, g.MaxSalary);

    private static Dictionary<string, string?> GradeFields(string? nameEn, string? nameAr, int level, decimal min, decimal mid, decimal max) =>
        new(StringComparer.Ordinal)
        {
            ["nameEn"] = Text(nameEn),
            ["nameAr"] = Text(nameAr),
            ["level"] = Whole(level),
            ["minSalary"] = Number(min),
            ["midSalary"] = Number(mid),
            ["maxSalary"] = Number(max),
        };

    // ── Work and pay rules (step 5) ─────────────────────────────────────────────────────────────────

    /// <summary>The company's numbering rule; every field null when it has none yet (the commit then creates it).</summary>
    public static Dictionary<string, string?> Numbering(EmployeeIdRule? r) => new(StringComparer.Ordinal)
    {
        ["prefix"] = r is null ? null : Text(r.CompanyPrefix)?.ToUpperInvariant(),
        ["useCountryPrefix"] = r is null ? null : Flag(r.UseCountryPrefix),
        ["useBranchPrefix"] = r is null ? null : Flag(r.UseBranchPrefix),
        ["useDepartmentPrefix"] = r is null ? null : Flag(r.UseDepartmentPrefix),
        ["useYear"] = r is null ? null : Flag(r.UseYear),
        ["paddingLength"] = r is null ? null : Whole(r.PaddingLength),
        ["nextSequence"] = r is null ? null : Whole(r.NextSequence),
    };

    public static Dictionary<string, string?> Numbering(CompanySetupEmployeeNumbering n) => new(StringComparer.Ordinal)
    {
        ["prefix"] = Text(n.Prefix)?.ToUpperInvariant(),
        ["useCountryPrefix"] = Flag(n.UseCountryPrefix),
        ["useBranchPrefix"] = Flag(n.UseBranchPrefix),
        ["useDepartmentPrefix"] = Flag(n.UseDepartmentPrefix),
        ["useYear"] = Flag(n.UseYear),
        ["paddingLength"] = Whole(n.PaddingLength),
        ["nextSequence"] = Whole(n.NextSequence),
    };

    /// <summary>The working week as the prefill shows it: the weekend that applies to the company, and whether the
    /// company has its own row.</summary>
    public static Dictionary<string, string?> WorkWeek(string weekendDays, bool own) => new(StringComparer.Ordinal)
    {
        ["weekendDays"] = Weekend(weekendDays),
        ["override"] = Flag(own),
    };

    public static Dictionary<string, string?> WorkWeek(CompanySetupWorkWeek w) => WorkWeek(w.WeekendDays, w.Override);

    private static string? Weekend(string? text)
    {
        var parsed = WorkWeekParser.ParseWeekendDays(text);
        return parsed is null || parsed.Count == 0 ? Text(text) : CompanySetupPack.FormatWeekend(parsed);
    }

    // ── The decision ────────────────────────────────────────────────────────────────────────────────

    public enum Outcome { TakeDatabase, TakeDraft, Conflict }

    /// <summary>One field of the three-way comparison.</summary>
    public static Outcome Decide(string? draft, string? baseline, string? database)
    {
        if (Same(draft, baseline)) return Outcome.TakeDatabase;      // the user did not touch it: what is there now stands
        if (Same(database, baseline)) return Outcome.TakeDraft;      // only the user changed it: update
        if (Same(draft, database)) return Outcome.TakeDatabase;      // both changed it the same way: nothing to write
        return Outcome.Conflict;                                     // both changed it differently: the user decides
    }

    /// <summary>English and Arabic words for a field in the conflict message.</summary>
    public static (string En, string Ar) FieldWords(string key) => key switch
    {
        "legalNameEn" => ("legal name (English)", "الاسم القانوني (بالإنجليزية)"),
        "legalNameAr" => ("legal name (Arabic)", "الاسم القانوني (بالعربية)"),
        "tradeName" => ("trade name", "الاسم التجاري"),
        "registrationNumber" => ("CR number", "رقم السجل التجاري"),
        "molEstablishmentNumber" => ("MOL establishment number", "رقم المنشأة لدى وزارة الموارد البشرية"),
        "gosiEmployerId" => ("GOSI number", "رقم التأمينات الاجتماعية"),
        "unifiedNationalNumber" => ("Unified National Number", "الرقم الوطني الموحد"),
        "taxNumber" => ("VAT number", "الرقم الضريبي"),
        "qiwaEstablishmentId" => ("Qiwa establishment id", "رقم المنشأة في قوى"),
        "emailDomain" => ("email domain", "نطاق البريد الإلكتروني"),
        "workEmailPattern" => ("work-email pattern", "نمط البريد الإلكتروني للعمل"),
        "erpCompanyCode" => ("ERP company code", "رمز الشركة في نظام تخطيط الموارد"),
        "firstPayrollMonth" => ("first payroll month", "أول شهر للرواتب"),
        "nameEn" => ("name (English)", "الاسم (بالإنجليزية)"),
        "nameAr" => ("name (Arabic)", "الاسم (بالعربية)"),
        "city" => ("city", "المدينة"),
        "timeZoneId" => ("time zone", "المنطقة الزمنية"),
        "isHeadOffice" => ("head office", "المقر الرئيسي"),
        "latitude" => ("latitude", "خط العرض"),
        "longitude" => ("longitude", "خط الطول"),
        "radiusMeters" => ("geofence radius", "نطاق السياج الجغرافي"),
        "prefix" => ("employee-number prefix", "بادئة رقم الموظف"),
        "useCountryPrefix" or "useBranchPrefix" or "useDepartmentPrefix" or "useYear" => ("employee-number format", "تنسيق رقم الموظف"),
        "paddingLength" => ("digits in the employee number", "عدد أرقام رقم الموظف"),
        "nextSequence" => ("next employee number", "رقم الموظف التالي"),
        "weekendDays" => ("weekend", "العطلة الأسبوعية"),
        "override" => ("own working week", "أسبوع العمل الخاص"),
        "parentCode" => ("parent department", "القسم الأعلى"),
        "branchCode" => ("branch", "الفرع"),
        "costCentreCode" => ("cost centre", "مركز التكلفة"),
        "titleEn" => ("title (English)", "المسمى (بالإنجليزية)"),
        "titleAr" => ("title (Arabic)", "المسمى (بالعربية)"),
        "departmentCode" => ("department", "القسم"),
        "gradeCode" => ("grade", "الدرجة"),
        "level" => ("level", "المستوى"),
        "minSalary" => ("minimum salary", "الحد الأدنى للراتب"),
        "midSalary" => ("midpoint salary", "منتصف نطاق الراتب"),
        "maxSalary" => ("maximum salary", "الحد الأقصى للراتب"),
        _ => (key, key),
    };
}
