namespace Zayra.Api.Infrastructure.Payroll;

// All field keys, section definitions, compliance locks, and validation rules for payslip templates.
// The controller validates every incoming template config against this registry.
// Unknown fields or sections are rejected — binding to the registry is what makes output auditable.
public static class PayslipTemplateRegistry
{
    // Allowed font families (subset of Google Fonts available as system fonts in most deployments)
    public static readonly IReadOnlySet<string> AllowedFonts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "NotoSans", "Roboto", "SourceSansPro", "Helvetica" };

    // Allowed locale codes
    public static readonly IReadOnlySet<string> AllowedLocales = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "en", "ar", "bilingual" };

    // Sections that can never be disabled — disabling them would drop compliance-required fields.
    // Header and net_pay are always rendered by the renderer regardless of layout config.
    public static readonly IReadOnlySet<string> NonDisablableSections = new HashSet<string>
        { "earnings", "deductions" };

    // All section definitions keyed by section key
    public static readonly IReadOnlyDictionary<string, SectionDef> Sections =
        new Dictionary<string, SectionDef>
        {
            ["earnings"] = new(
                Key: "earnings",
                LabelEn: "Earnings",
                LabelAr: "المستحقات",
                CanDisable: false,
                Fields: new[]
                {
                    new FieldDef("basic_salary",        "Basic Salary",         "الراتب الأساسي",      IsComplianceLocked: true),
                    new FieldDef("housing_allowance",   "Housing Allowance",    "بدل السكن",           IsComplianceLocked: false),
                    new FieldDef("transport_allowance", "Transport Allowance",  "بدل النقل",           IsComplianceLocked: false),
                    new FieldDef("food_allowance",      "Food Allowance",       "بدل الطعام",          IsComplianceLocked: false),
                    new FieldDef("mobile_allowance",    "Mobile Allowance",     "بدل الاتصالات",       IsComplianceLocked: false),
                    new FieldDef("other_allowances",    "Other Allowances",     "بدلات أخرى",          IsComplianceLocked: false),
                    new FieldDef("overtime_pay",        "Overtime Pay",         "أجر العمل الإضافي",  IsComplianceLocked: false),
                    new FieldDef("bonus",               "Bonus",                "المكافأة",            IsComplianceLocked: false),
                }
            ),
            ["deductions"] = new(
                Key: "deductions",
                LabelEn: "Deductions",
                LabelAr: "الاستقطاعات",
                CanDisable: false,
                Fields: new[]
                {
                    // GOSI EE lines are compliance-locked: must appear when KSA jurisdiction
                    new FieldDef("gosi_annuities_ee",  "GOSI Annuities (EE)",  "تأمين المعاشات (الموظف)", IsComplianceLocked: true),
                    new FieldDef("gosi_saned_ee",      "GOSI SANED (EE)",      "تأمين ضد البطالة (الموظف)", IsComplianceLocked: true),
                    new FieldDef("loan_repayment",     "Loan Repayment",       "سداد القرض",           IsComplianceLocked: false),
                    new FieldDef("advance_deduction",  "Advance Deduction",    "استقطاع السلفة",       IsComplianceLocked: false),
                    new FieldDef("other_deductions",   "Other Deductions",     "استقطاعات أخرى",      IsComplianceLocked: false),
                }
            ),
            ["employer_contributions"] = new(
                Key: "employer_contributions",
                LabelEn: "Employer Contributions",
                LabelAr: "اشتراكات صاحب العمل",
                CanDisable: true,
                Fields: new[]
                {
                    new FieldDef("gosi_annuities_er",  "GOSI Annuities (ER)",  "تأمين المعاشات (صاحب العمل)", IsComplianceLocked: false),
                    new FieldDef("gosi_saned_er",      "GOSI SANED (ER)",      "تأمين ضد البطالة (صاحب العمل)", IsComplianceLocked: false),
                    new FieldDef("gosi_oh_er",         "GOSI OH (ER)",         "تأمين الأخطار المهنية",      IsComplianceLocked: false),
                }
            ),
            ["leave_balance"] = new(
                Key: "leave_balance",
                LabelEn: "Leave Balance",
                LabelAr: "رصيد الإجازات",
                CanDisable: true,
                Fields: new[]
                {
                    new FieldDef("annual_leave_balance", "Annual Leave Balance", "رصيد الإجازة السنوية", IsComplianceLocked: false),
                    new FieldDef("sick_leave_balance",   "Sick Leave Balance",   "رصيد الإجازة المرضية", IsComplianceLocked: false),
                }
            ),
            ["loan_balance"] = new(
                Key: "loan_balance",
                LabelEn: "Loan Balance",
                LabelAr: "رصيد القروض",
                CanDisable: true,
                Fields: new[]
                {
                    new FieldDef("outstanding_loan_balance", "Outstanding Loan Balance", "الرصيد المتبقي للقرض", IsComplianceLocked: false),
                }
            ),
            ["ytd"] = new(
                Key: "ytd",
                LabelEn: "Year-to-Date",
                LabelAr: "منذ بداية العام",
                CanDisable: true,
                Fields: new[]
                {
                    new FieldDef("ytd_gross",       "YTD Gross",       "إجمالي الراتب (منذ بداية العام)",   IsComplianceLocked: false),
                    new FieldDef("ytd_deductions",  "YTD Deductions",  "إجمالي الاستقطاعات (منذ بداية العام)", IsComplianceLocked: false),
                    new FieldDef("ytd_net",         "YTD Net",         "صافي الراتب (منذ بداية العام)",     IsComplianceLocked: false),
                }
            ),
            ["bank_wps"] = new(
                Key: "bank_wps",
                LabelEn: "Bank & WPS",
                LabelAr: "بيانات البنك والرواتب",
                CanDisable: true,
                Fields: new[]
                {
                    // WPS compliance: IBAN and bank_name are compliance-locked when section is enabled
                    new FieldDef("iban",       "IBAN",      "رقم الحساب المصرفي الدولي", IsComplianceLocked: true),
                    new FieldDef("bank_name",  "Bank Name", "اسم البنك",                 IsComplianceLocked: true),
                    new FieldDef("mol_id",     "MoL ID",   "رقم وزارة العمل",           IsComplianceLocked: false),
                }
            ),
            ["signatory"] = new(
                Key: "signatory",
                LabelEn: "Signatory",
                LabelAr: "المفوض بالتوقيع",
                CanDisable: true,
                Fields: new[]
                {
                    new FieldDef("signatory_name",  "Signatory Name",  "اسم المفوض بالتوقيع",   IsComplianceLocked: false),
                    new FieldDef("signatory_title", "Signatory Title", "مسمى المفوض بالتوقيع",  IsComplianceLocked: false),
                }
            ),
        };

    // Validate a layout config against the registry. Returns error messages; empty = valid.
    public static IReadOnlyList<string> ValidateLayout(PayslipLayoutConfig layout)
    {
        var errors = new List<string>();

        if (!AllowedLocales.Contains(layout.Locale))
            errors.Add($"locale '{layout.Locale}' is not allowed. Use: {string.Join(", ", AllowedLocales)}");

        var seenSections = new HashSet<string>();
        foreach (var sec in layout.Sections)
        {
            if (!Sections.TryGetValue(sec.Key, out var def))
            {
                errors.Add($"Unknown section '{sec.Key}'.");
                continue;
            }
            if (!seenSections.Add(sec.Key))
            {
                errors.Add($"Duplicate section '{sec.Key}'.");
                continue;
            }
            if (!sec.Enabled && !def.CanDisable)
                errors.Add($"Section '{sec.Key}' is compliance-locked and cannot be disabled.");

            if (!sec.Enabled) continue;

            var allowedFields = def.Fields.ToDictionary(f => f.Key, StringComparer.OrdinalIgnoreCase);
            foreach (var fieldKey in sec.Fields)
            {
                if (!allowedFields.ContainsKey(fieldKey))
                    errors.Add($"Field '{fieldKey}' is not allowed in section '{sec.Key}'.");
            }
            // Locked fields that are missing from the section
            foreach (var locked in def.Fields.Where(f => f.IsComplianceLocked))
            {
                if (!sec.Fields.Contains(locked.Key, StringComparer.OrdinalIgnoreCase))
                    errors.Add($"Compliance-locked field '{locked.Key}' cannot be removed from section '{sec.Key}'.");
            }
        }

        // Ensure all non-disablable sections are present
        foreach (var required in NonDisablableSections)
        {
            if (!seenSections.Contains(required))
                errors.Add($"Section '{required}' is compliance-required and must be included.");
        }

        return errors;
    }

    // Validate a branding config. Returns error messages; empty = valid.
    public static IReadOnlyList<string> ValidateBranding(PayslipBrandingConfig branding)
    {
        var errors = new List<string>();

        if (!System.Text.RegularExpressions.Regex.IsMatch(branding.PrimaryColor, "^#[0-9A-Fa-f]{6}$"))
            errors.Add($"primaryColor '{branding.PrimaryColor}' must be a 6-digit hex color (#RRGGBB).");
        if (!System.Text.RegularExpressions.Regex.IsMatch(branding.AccentColor, "^#[0-9A-Fa-f]{6}$"))
            errors.Add($"accentColor '{branding.AccentColor}' must be a 6-digit hex color (#RRGGBB).");
        if (!AllowedFonts.Contains(branding.FontFamily))
            errors.Add($"fontFamily '{branding.FontFamily}' is not in the allowed list: {string.Join(", ", AllowedFonts)}.");
        if (branding.HeaderTextEn?.Length > 200)
            errors.Add("headerTextEn exceeds 200 characters.");
        if (branding.HeaderTextAr?.Length > 200)
            errors.Add("headerTextAr exceeds 200 characters.");
        if (branding.FooterTextEn?.Length > 200)
            errors.Add("footerTextEn exceeds 200 characters.");
        if (branding.FooterTextAr?.Length > 200)
            errors.Add("footerTextAr exceeds 200 characters.");
        if (!string.IsNullOrEmpty(branding.LogoStorageUrl) && !branding.LogoStorageUrl.StartsWith("storage/", StringComparison.OrdinalIgnoreCase))
            errors.Add("logoStorageUrl must start with 'storage/' (tenant-scoped storage path).");

        return errors;
    }
}

public record SectionDef(
    string Key,
    string LabelEn,
    string LabelAr,
    bool CanDisable,
    IReadOnlyList<FieldDef> Fields
);

public record FieldDef(
    string Key,
    string LabelEn,
    string LabelAr,
    bool IsComplianceLocked
);

// ── Typed DTOs for branding and layout (deserialized from JSON columns) ──────

public record PayslipBrandingConfig(
    string PrimaryColor = "#1E3A5F",
    string AccentColor = "#2563EB",
    string FontFamily = "NotoSans",
    string HeaderTextEn = "",
    string HeaderTextAr = "",
    string FooterTextEn = "",
    string FooterTextAr = "",
    string? LogoStorageUrl = null,
    string Locale = "en"
);

public record PayslipLayoutConfig(
    string Locale = "en",
    IReadOnlyList<PayslipSectionConfig>? Sections = null
)
{
    public IReadOnlyList<PayslipSectionConfig> Sections { get; init; } = Sections ?? Array.Empty<PayslipSectionConfig>();
}

public record PayslipSectionConfig(
    string Key,
    bool Enabled = true,
    int Order = 0,
    IReadOnlyList<string>? Fields = null
)
{
    public IReadOnlyList<string> Fields { get; init; } = Fields ?? Array.Empty<string>();
}
