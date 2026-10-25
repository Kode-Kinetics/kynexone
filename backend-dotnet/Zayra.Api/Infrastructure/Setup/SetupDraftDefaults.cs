using System.Globalization;
using System.Text.RegularExpressions;
using Zayra.Api.Application.Setup;

namespace Zayra.Api.Infrastructure.Setup;

/// <summary>
/// The model-free defaults the create-company wizard starts from, and the validation the policy-intake
/// family (behind the policy_intake opt-in flag) applies to a proposed configuration. Everything here is
/// static and deterministic: no model client can sit behind it and nothing is written anywhere.
/// (The model-backed "starter draft" generator and its api/setup-assistant/preview and /apply endpoints
/// were retired with the old setup studio.)
/// </summary>
public static class SetupDraftDefaults
{
    public static List<string> ValidateConfiguration(CompanyProfile p)
    {
        var errors = new List<string>();
        if (!string.IsNullOrWhiteSpace(p.LeaveYearBasis) && !string.Equals(p.LeaveYearBasis, "Calendar", StringComparison.OrdinalIgnoreCase))
            errors.Add("Only calendar leave years are supported. Anniversary and fiscal leave-year engines are not configured by setup.");
        if (!string.IsNullOrWhiteSpace(p.PayCycle) && !string.Equals(p.PayCycle, "Monthly", StringComparison.OrdinalIgnoreCase))
            errors.Add("Only monthly salary amounts are supported by setup. Other pay cycles require a payroll calendar and salary conversion that this flow cannot activate.");
        if (p.Configuration is not { } c) return errors;
        if (c.PolicyDocumentId.HasValue && !string.IsNullOrWhiteSpace(c.PolicySourceText))
            errors.Add("Choose either a saved policy document or pasted policy text as the active source, not both.");
        if (c.PolicyDocumentId.HasValue != !string.IsNullOrWhiteSpace(c.PolicySourceHash))
            errors.Add("A policy document reference requires both its identifier and content fingerprint.");
        if ((c.PolicyFieldSources?.Count ?? 0) > 500)
            errors.Add("A setup draft may reference at most 500 source excerpts.");
        if (c.PolicySources is { } sources && (sources.Count > 50 || sources.Any(s => s is null)
            || sources.Select(s => s.DocumentId).Distinct().Count() != sources.Count))
            errors.Add("A policy binder may contain at most 50 distinct documents.");
        if ((c.PolicySourceText?.Length ?? 0) > 12000)
            errors.Add("Company policy source must be at most 12,000 characters.");
        void Choices(List<string>? values, string label, params string[] allowed)
        {
            if (values is null) return;
            if (values.Any(v => v is null || !allowed.Contains(v, StringComparer.OrdinalIgnoreCase))
                || values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != values.Count)
                errors.Add($"{label} contains an unsupported or duplicate choice.");
        }
        Choices(c.AttendanceMethods, "Attendance methods", "BiometricDevice", "MobileGeofence", "WebCheckIn", "Manual");
        Choices(c.OvertimeModes, "Overtime modes", "PaidOvertime", "CompensatoryOff", "NotApplicable");
        if (c.AttendanceMethods is { Count: 0 }) errors.Add("Select at least one attendance method, or omit the selection to use the default.");
        if (c.OvertimeModes is { Count: > 1 } modes && modes.Contains("NotApplicable", StringComparer.OrdinalIgnoreCase))
            errors.Add("Overtime not applicable cannot be combined with paid overtime or compensatory time off.");
        if (c.OvertimePolicy is not null && c.OvertimeModes is { } selected
            && (selected.Count == 0 || selected.Contains("NotApplicable", StringComparer.OrdinalIgnoreCase)))
            errors.Add("Remove the custom overtime policy or select an overtime mode.");
        errors.AddRange(ValidateDraftValues(SetupDraft.Empty() with
        {
            Grades = p.Sections.Org ? c.Grades ?? [] : [],
            LeavePolicies = p.Sections.LeavePolicies ? c.LeavePolicies ?? [] : [],
            AttendancePolicy = p.Sections.Attendance ? c.AttendancePolicy : null,
            OvertimePolicy = p.Sections.Attendance ? c.OvertimePolicy : null,
            BenefitPlans = p.Sections.Benefits ? c.BenefitPlans : null,
        }, p.CurrencyCode));
        return errors;
    }

    /// <summary>Reviewed customer values fail closed. Unlike generated suggestions, these must
    /// never be clamped or dropped. Run on both preview input and the independently editable apply payload.</summary>
    public static List<string> ValidateDraftValues(SetupDraft d, string currency)
    {
        var errors = new List<string>();
        static bool Code(string? value) => value is not null && Regex.IsMatch(value, @"^[A-Za-z0-9][A-Za-z0-9_-]{0,49}$");
        static bool Text(string? value, int max = 160) => !string.IsNullOrWhiteSpace(value) && value.Length <= max;
        static bool Range(decimal value, decimal max = 365) => value >= 0 && value <= max;
        static bool Precision(decimal value, int places = 2) => decimal.Round(value, places) == value;
        static bool Choice(string? value, params string[] allowed) => allowed.Contains(value, StringComparer.OrdinalIgnoreCase);
        static bool Date(string? value, out DateOnly date) => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
        void Unique(IEnumerable<string> keys, string label)
        {
            var array = keys.ToArray();
            if (array.Distinct(StringComparer.OrdinalIgnoreCase).Count() != array.Length)
                errors.Add($"{label} must have unique codes or scopes.");
        }
        if (d.Branches is null || d.Departments is null || d.CostCenters is null || d.Designations is null
            || d.Grades is null || d.GradePayComponents is null || d.LeaveTypes is null || d.LeavePolicies is null
            || d.Shifts is null || d.PayComponents is null || d.StatutoryRules is null)
        {
            errors.Add("Draft collections must be arrays, not null.");
            return errors;
        }
        if (d.Grades.Count > 100 || d.LeavePolicies.Count > 100 || (d.BenefitPlans?.Count ?? 0) > 100)
            errors.Add("A setup draft may contain at most 100 grades, leave policies or benefit plans in each section.");
        if (d.Grades.Any(g => g is null) || d.LeavePolicies.Any(p => p is null) || d.BenefitPlans?.Any(b => b is null) == true
            || d.GradePayComponents.Any(c => c is null) || d.PayComponents.Any(c => c is null))
        {
            errors.Add("Draft rows cannot be null.");
            return errors;
        }
        Unique(d.Grades.Select(g => g.Code), "Grades");
        foreach (var g in d.Grades)
        {
            if (!Code(g.Code) || !Text(g.Name) || g.Level is < 1 or > 1000
                || !Range(g.MinSalary, 1000000000m) || !Range(g.MidSalary, 1000000000m) || !Range(g.MaxSalary, 1000000000m)
                || !Precision(g.MinSalary) || !Precision(g.MidSalary) || !Precision(g.MaxSalary)
                || g.MinSalary > g.MidSalary || g.MidSalary > g.MaxSalary)
                errors.Add($"Grade '{g.Code}' requires a code, name, level 1–1000 and ordered nonnegative minimum, midpoint and maximum salaries with at most two decimal places.");
            if (!string.Equals(g.Currency, currency, StringComparison.OrdinalIgnoreCase))
                errors.Add($"Grade '{g.Code}' must use the reviewed company currency '{currency}'.");
        }
        Unique(d.GradePayComponents.Select(c => $"{c.GradeCode}|{c.ComponentCode}"), "Grade salary components");
        foreach (var component in d.GradePayComponents)
        {
            if (component.Frequency != "Monthly")
                errors.Add($"Salary component '{component.ComponentCode}' must use monthly amounts and Monthly frequency. The salary writer does not convert other frequencies.");
            if (!Code(component.GradeCode) || !Code(component.ComponentCode) || !Text(component.ComponentName)
                || !new[] { "Earning", "Benefit", "Deduction" }.Contains(component.ComponentType)
                || !new[] { "Fixed", "PercentOfBasic" }.Contains(component.CalculationType)
                || !Range(component.Amount, 1000000000m) || !Precision(component.Amount)
                || !Range(component.Percentage, 100) || !Precision(component.Percentage, 4))
                errors.Add($"Salary component '{component.ComponentCode}' has invalid codes, type, calculation or amount. Amounts must be nonnegative with at most two decimals; percentages must be 0–100 with at most four decimals.");
        }
        Unique(d.PayComponents.Select(c => c.Code), "Salary structure components");
        foreach (var component in d.PayComponents)
            if (!Code(component.Code) || !Text(component.Name) || !new[] { "Earning", "Deduction" }.Contains(component.ComponentType)
                || !new[] { "Fixed", "Percentage" }.Contains(component.CalculationType)
                || !Range(component.Amount, 1000000000m) || !Precision(component.Amount)
                || !Range(component.Percentage, 100) || !Precision(component.Percentage, 3))
                errors.Add($"Salary component '{component.Code}' has invalid values. Use a supported earning/deduction calculation, nonnegative two-decimal amounts, and 0–100 percentages with at most three decimals.");
        Unique(d.LeavePolicies.Select(p => $"{p.LeaveTypeCode}|{p.GradeCode}|{p.DepartmentCode}|{Leave.LeavePolicyScope.EmploymentTypesKey(p.EmploymentType)}"), "Leave policies");
        for (var i = 0; i < d.LeavePolicies.Count; i++)
            for (var j = i + 1; j < d.LeavePolicies.Count; j++)
            {
                var policyA = d.LeavePolicies[i]; var policyB = d.LeavePolicies[j];
                if (string.Equals(policyA.LeaveTypeCode, policyB.LeaveTypeCode, StringComparison.OrdinalIgnoreCase)
                    && LeaveScopesConflict(policyA.GradeCode, policyA.DepartmentCode, policyA.EmploymentType, policyB.GradeCode, policyB.DepartmentCode, policyB.EmploymentType))
                    errors.Add($"Leave policies '{policyA.Name}' and '{policyB.Name}' overlap without a clear broader/narrower scope. Make one population a strict subset of the other or separate their populations.");
            }
        foreach (var p in d.LeavePolicies)
        {
            if (!Text(p.Name) || !Code(p.LeaveTypeCode)
                || !Range(p.AnnualEntitlementDays) || !Range(p.EncashmentMaxDays)
                || p.MinimumDaysPerRequest <= 0 || !Range(p.MinimumDaysPerRequest) || !Range(p.MaximumDaysPerRequest)
                || !Precision(p.AnnualEntitlementDays) || !Precision(p.EncashmentMaxDays) || !Precision(p.MinimumDaysPerRequest) || !Precision(p.MaximumDaysPerRequest)
                || p.MaximumDaysPerRequest > 0 && p.MinimumDaysPerRequest > p.MaximumDaysPerRequest
                || p.NoticeRequiredDays is < 0 or > 365 || !Choice(p.AccrualMethod, "Monthly", "Yearly")
                || !Choice(p.PayrollImpact, "Full", "Unpaid")
                || p.ProratePartialMonths && !Choice(p.AccrualMethod, "Monthly")
                || !string.IsNullOrWhiteSpace(p.GradeCode) && !Code(p.GradeCode)
                || !string.IsNullOrWhiteSpace(p.DepartmentCode) && !Code(p.DepartmentCode)
                || (p.EmploymentType?.Length ?? 0) > 80 || p.EmploymentType is not null && p.EmploymentType != p.EmploymentType.Trim())
                errors.Add($"Leave policy '{p.Name}' has invalid values. Days allow at most two decimal places and minimum days must be positive. Partial-month proration requires monthly accrual; scope references must use valid codes.");
        }
        if (d.AttendancePolicy is { } a && (!Code(a.Code) || !Text(a.Name)
            || a.GraceMinutes is < 0 or > 120
            || a.EarlyExitThresholdMinutes is < 0 or > 480 || a.StandardWorkMinutes is < 60 or > 960
            || a.HalfDayThresholdMinutes < 0 || a.HalfDayThresholdMinutes > a.StandardWorkMinutes || a.BreakMinutes is < 0 or > 240))
            errors.Add("Attendance policy thresholds are invalid; half-day must not exceed the standard working minutes.");
        if (d.OvertimePolicy is { } o)
        {
            if (!Code(o.Code) || !Text(o.Name) || !Choice(o.HourlyRateBasis, "BasicSalary", "GrossSalary")
                || o.StandardMonthlyHours is < 1 or > 400 || o.MinimumMinutes is < 0 or > 480
                || o.MaximumMinutesPerDay < o.MinimumMinutes || o.MaximumMinutesPerDay > 960
                || o.MonthlyCapMinutes is < 0 or > 30000 || !Choice(o.RoundingRule, "NearestMinute", "Nearest15")
                || o.Multipliers is null || o.Multipliers.Any(m => m is null || !Choice(m.DayCategory, "RegularDay", "Weekend", "PublicHoliday") || m.Multiplier is < 1 or > 5 || !Precision(m.Multiplier, 3)))
                errors.Add("Overtime policy has invalid limits, pay basis or multipliers. Fixed hourly rates are not supported by setup.");
            if (o.Multipliers is not null) Unique(o.Multipliers.Where(m => m is not null).Select(m => m.DayCategory), "Overtime multipliers");
        }
        Unique((d.BenefitPlans ?? []).Select(b => b.Code), "Benefit plans");
        foreach (var b in d.BenefitPlans ?? [])
        {
            if (!Code(b.Code) || !Text(b.Name) || !Text(b.PlanType, 80)
                || !Regex.IsMatch(b.Currency ?? "", "^[A-Za-z]{3}$")
                || !string.Equals(b.Currency, currency, StringComparison.OrdinalIgnoreCase)
                || !Date(b.EffectiveFrom, out var start)
                || !string.IsNullOrEmpty(b.EffectiveTo) && (!Date(b.EffectiveTo, out var end) || end < start)
                || b.GradeCodes?.Any(g => !Code(g)) == true)
                errors.Add($"Benefit plan '{b.Code}' requires a code, name, type, company currency, valid effective dates and valid grade codes.");
            Unique(b.GradeCodes ?? [], $"Benefit plan '{b.Code}' grade eligibility");
        }
        return errors;
    }

    internal static bool LeaveScopesConflict(string? gradeA, string? departmentA, string? employmentA,
        string? gradeB, string? departmentB, string? employmentB)
    {
        static bool Blank(string? value) => string.IsNullOrWhiteSpace(value);
        static bool Same(string? a, string? b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
        static bool Covers(string? a, string? b) => Blank(a) || Same(a, b);
        static bool Overlap(string? a, string? b) => Blank(a) || Blank(b) || Same(a, b);
        // Employment type is a list (Full-Time,Part-Time): overlap is a shared type, cover is a superset.
        return Overlap(gradeA, gradeB) && Overlap(departmentA, departmentB) && Leave.LeavePolicyScope.EmploymentTypesOverlap(employmentA, employmentB)
            && !(Covers(gradeA, gradeB) && Covers(departmentA, departmentB) && Leave.LeavePolicyScope.CoversEmploymentTypes(employmentA, employmentB))
            && !(Covers(gradeB, gradeA) && Covers(departmentB, departmentA) && Leave.LeavePolicyScope.CoversEmploymentTypes(employmentB, employmentA));
    }

    private static string NormalizeCountry(string raw)
    {
        var c = (raw ?? "").Trim().ToUpperInvariant();
        return c switch
        {
            "SA" or "KSA" or "SAU" => "SAU",
            "AE" or "UAE" or "ARE" => "ARE",
            "QA" or "QAT" => "QAT",
            "KW" or "KWT" => "KWT",
            "BH" or "BHR" => "BHR",
            "OM" or "OMN" => "OMN",
            _ => c.Length >= 3 ? c[..3] : c,
        };
    }

    /// <summary>The country's usual working week, used when the customer leaves the weekend on
    /// "country default".</summary>
    /// <summary>Currency as a 3-letter code, or empty. Empty is a real answer: it means the tenant
    /// has not stated one, and an empty band is visibly unset where a plausible-but-wrong code is
    /// not.</summary>
    private static string NormalizeCurrency(string? raw)
    {
        var c = (raw ?? string.Empty).Trim().ToUpperInvariant();
        return c.Length == 3 && c.All(char.IsAsciiLetter) ? c : string.Empty;
    }

    private static DraftWorkingWeek WorkingWeekFor(string iso3) => iso3 switch
    {
        "SAU" or "QAT" or "KWT" or "BHR" or "OMN" => new DraftWorkingWeek("Sun-Thu", "Sunday"),
        "ARE" => new DraftWorkingWeek("Mon-Fri", "Monday"),
        _ => new DraftWorkingWeek("Mon-Fri", "Monday"),
    };

    /// <summary>Case-insensitive pick from a closed list. Anything the client invents falls back
    /// to <paramref name="fallback"/> rather than reaching a column.</summary>
    private static string Choice(string? raw, string fallback, params string[] allowed)
        => allowed.FirstOrDefault(a => string.Equals(a, (raw ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase))
           ?? fallback;

    /// <summary>The shift pattern. An explicit choice wins; otherwise this is the inference the
    /// service has always made — industry, then a regex over the free-text note.</summary>
    private static string ResolveWorkPattern(CompanyProfile p)
    {
        var chosen = Choice(p.WorkPattern, string.Empty,
            "SingleDayShift", "TwoShifts", "ContinuousThreeShifts", "FieldRoster");
        if (chosen.Length > 0) return chosen;
        return PackFor(p.Industry).ContinuousOperations || MentionsContinuousOps(p.Notes)
            ? "ContinuousThreeShifts"
            : "SingleDayShift";
    }

    private static string ResolveWorkforceMix(CompanyProfile p)
        => Choice(p.WorkforceMix, "Mixed", "MostlyNational", "Mixed", "MostlyExpat");

    private static List<DraftShift> ShiftsFor(string pattern) => pattern switch
    {
        "ContinuousThreeShifts" => new()
        {
            new("MORNING", "Morning Shift", "06:00", "14:00", 60, "#00C896"),
            new("EVENING", "Evening Shift", "14:00", "22:00", 60, "#8b5cf6"),
            new("NIGHT", "Night Shift", "22:00", "06:00", 60, "#2F6BFF"),
        },
        "TwoShifts" => new()
        {
            new("DAY", "Day Shift", "08:00", "16:00", 60, "#2F6BFF"),
            new("EVENING", "Evening Shift", "16:00", "00:00", 60, "#8b5cf6"),
        },
        "FieldRoster" => new()
        {
            new("FIELD", "Field Shift", "06:00", "18:00", 90, "#f59e0b"),
            new("OFFICE", "Office Shift", "08:00", "17:00", 60, "#2F6BFF"),
        },
        _ => new()
        {
            new("DAY", "Day Shift", "08:00", "17:00", 60, "#2F6BFF"),
        },
    };

    // ── Attendance & overtime ───────────────────────────────────────────────

    private static DateOnly NthWeekday(int year, int month, DayOfWeek day, int nth)
    {
        var d = new DateOnly(year, month, 1);
        while (d.DayOfWeek != day) d = d.AddDays(1);
        return d.AddDays(7 * (nth - 1));
    }

    /// <summary>
    /// The current year's public holidays that have a fixed Gregorian date.
    ///
    /// <para>Eid al-Fitr, Eid al-Adha, the Islamic New Year and the Prophet's Birthday move with
    /// the lunar calendar and are announced locally, often by moon sighting days beforehand. A
    /// drafted date for them would be wrong most years, and wrong by a day is enough to misprice
    /// every overtime hour worked over the holiday and to deduct leave that was never taken. They
    /// are named in a note instead, so the gap is visible rather than filled.</para>
    /// </summary>
    private static DraftHolidayCalendar? HolidayCalendarFor(string iso3, List<string> notes)
    {
        var year = DateTime.UtcNow.Year;
        string D(int month, int day) => new DateOnly(year, month, day).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        DraftHoliday H(string en, string ar, string date, string type = "National", bool optional = false)
            => new(en, ar, date, IsRecurring: true, IsOptional: optional, HolidayType: type, Notes: string.Empty);

        var newYear = H("New Year's Day", "رأس السنة الميلادية", D(1, 1));

        var holidays = iso3 switch
        {
            "SAU" => new List<DraftHoliday>
            {
                H("Founding Day", "يوم التأسيس", D(2, 22)),
                H("Saudi National Day", "اليوم الوطني السعودي", D(9, 23)),
            },
            "ARE" => new List<DraftHoliday>
            {
                newYear,
                H("Commemoration Day", "يوم الشهيد", D(12, 1)),
                H("UAE National Day", "اليوم الوطني للإمارات", D(12, 2)),
                H("UAE National Day Holiday", "عطلة اليوم الوطني", D(12, 3)),
            },
            "QAT" => new List<DraftHoliday>
            {
                H("National Sports Day", "اليوم الرياضي للدولة",
                  NthWeekday(year, 2, DayOfWeek.Tuesday, 2).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                H("Qatar National Day", "اليوم الوطني لقطر", D(12, 18)),
            },
            "KWT" => new List<DraftHoliday>
            {
                newYear,
                H("Kuwait National Day", "العيد الوطني الكويتي", D(2, 25)),
                H("Liberation Day", "يوم التحرير", D(2, 26)),
            },
            "BHR" => new List<DraftHoliday>
            {
                newYear,
                H("Bahrain National Day", "العيد الوطني البحريني", D(12, 16)),
                H("Bahrain National Day Holiday", "عطلة العيد الوطني", D(12, 17)),
            },
            "OMN" => new List<DraftHoliday>
            {
                newYear,
                H("Oman National Day", "العيد الوطني العماني", D(11, 18)),
                H("Oman National Day Holiday", "عطلة العيد الوطني", D(11, 19)),
            },
            _ => new List<DraftHoliday>(),
        };

        if (holidays.Count == 0)
        {
            notes.Add($"No public holiday calendar was drafted: this build has no built-in holiday list for {iso3}. Add the year's holidays before running attendance or payroll over them — an unlisted holiday is treated as an ordinary working day.");
            return null;
        }

        notes.Add($"The {year} holiday calendar contains only the fixed-date holidays. Eid al-Fitr, Eid al-Adha, the Islamic New Year and the Prophet's Birthday follow the lunar calendar and are confirmed locally close to the date, so they are deliberately not drafted — add them once announced.");
        return new DraftHolidayCalendar($"{iso3} Public Holidays {year}", year, holidays);
    }

    // ── Localization ────────────────────────────────────────────────────────

    // Industry packs. Each adds departments and roles on top of the core four, and declares
    // whether the industry normally runs around the clock (which decides the shift pattern).
    private enum GradeSlot { Low, Mid, Upper, Top }

    private sealed record IndustryPack(
        string Label,
        List<DraftDepartment> Departments,
        List<(string Code, string Title, string Dept, GradeSlot Slot, bool IsManager, int Rank)> Designations,
        bool ContinuousOperations);

    private static IndustryPack PackFor(string? industry)
    {
        var key = (industry ?? string.Empty).ToLowerInvariant();
        bool Has(params string[] needles) => needles.Any(key.Contains);

        if (Has("health", "hospital", "clinic", "medical", "pharma"))
            return new("Healthcare",
                new() { new("CLIN", "Clinical"), new("NURS", "Nursing"), new("PHARM", "Pharmacy") },
                new() { ("PHYS", "Physician", "CLIN", GradeSlot.Upper, false, 3),
                        ("NURSE", "Staff Nurse", "NURS", GradeSlot.Low, false, 5),
                        ("PHARMACIST", "Pharmacist", "PHARM", GradeSlot.Mid, false, 5) },
                true);

        if (Has("construct", "contract", "civil", "infrastructure"))
            return new("Construction",
                new() { new("PROJ", "Projects"), new("HSE", "Health & Safety"), new("SITE", "Site Operations") },
                new() { ("PROJ_MGR", "Project Manager", "PROJ", GradeSlot.Upper, true, 3),
                        ("SITE_ENG", "Site Engineer", "SITE", GradeSlot.Mid, false, 5),
                        ("HSE_OFF", "HSE Officer", "HSE", GradeSlot.Low, false, 5) },
                false);

        if (Has("logistic", "transport", "shipping", "freight", "courier", "supply chain"))
            return new("Logistics",
                new() { new("FLEET", "Fleet"), new("WHSE", "Warehouse"), new("DISP", "Dispatch") },
                new() { ("FLEET_SUP", "Fleet Supervisor", "FLEET", GradeSlot.Mid, true, 4),
                        ("WH_KEEPER", "Warehouse Keeper", "WHSE", GradeSlot.Low, false, 5),
                        ("DRIVER", "Driver", "FLEET", GradeSlot.Low, false, 6) },
                true);

        if (Has("retail", "trading", "wholesale", "ecommerce", "e-commerce"))
            return new("Retail",
                new() { new("STORE", "Stores"), new("MERCH", "Merchandising"), new("SALES", "Sales") },
                new() { ("STORE_MGR", "Store Manager", "STORE", GradeSlot.Upper, true, 3),
                        ("MERCHANDISER", "Merchandiser", "MERCH", GradeSlot.Low, false, 5),
                        ("CASHIER", "Cashier", "STORE", GradeSlot.Low, false, 6) },
                false);

        if (Has("hospitality", "hotel", "restaurant", "catering", "f&b", "tourism"))
            return new("Hospitality",
                new() { new("FNB", "Food & Beverage"), new("HK", "Housekeeping"), new("FO", "Front Office") },
                new() { ("HK_SUP", "Housekeeping Supervisor", "HK", GradeSlot.Mid, true, 4),
                        ("CHEF", "Chef", "FNB", GradeSlot.Mid, false, 5),
                        ("FO_AGENT", "Front Office Agent", "FO", GradeSlot.Low, false, 5) },
                true);

        if (Has("manufact", "factory", "industrial", "production", "plant"))
            return new("Manufacturing",
                new() { new("PROD", "Production"), new("QA", "Quality Assurance"), new("MAINT", "Maintenance") },
                new() { ("PROD_SUP", "Production Supervisor", "PROD", GradeSlot.Mid, true, 4),
                        ("QA_INSP", "Quality Inspector", "QA", GradeSlot.Low, false, 5),
                        ("MAINT_TECH", "Maintenance Technician", "MAINT", GradeSlot.Low, false, 5) },
                true);

        if (Has("tech", "software", "saas", "digital", "fintech"))
            return new("Technology",
                new() { new("ENG", "Engineering"), new("PRODUCT", "Product"), new("SUPPORT", "Customer Support") },
                new() { ("PROD_MGR", "Product Manager", "PRODUCT", GradeSlot.Upper, true, 3),
                        ("SW_ENG", "Software Engineer", "ENG", GradeSlot.Mid, false, 5),
                        ("SUP_AGENT", "Support Agent", "SUPPORT", GradeSlot.Low, false, 5) },
                false);

        if (Has("education", "school", "university", "training", "academy"))
            return new("Education",
                new() { new("ACAD", "Academics"), new("ADMISSIONS", "Admissions"), new("STUDENT", "Student Affairs") },
                new() { ("REGISTRAR", "Registrar", "ADMISSIONS", GradeSlot.Upper, true, 3),
                        ("TEACHER", "Teacher", "ACAD", GradeSlot.Mid, false, 5),
                        ("COUNSELLOR", "Student Counsellor", "STUDENT", GradeSlot.Low, false, 5) },
                false);

        return new("General",
            new() { new("OPS", "Operations"), new("SALES", "Sales") },
            new() { ("OPS_OFF", "Operations Officer", "OPS", GradeSlot.Low, false, 5),
                    ("SALES_EXEC", "Sales Executive", "SALES", GradeSlot.Low, false, 5) },
            false);
    }

    /// <summary>Headcount decides how many rungs the ladder has. A 30-person company does not
    /// need seven grades, and a 2,000-person one cannot run on five.</summary>
    private static int GradeCountFor(string? size)
    {
        var digits = Regex.Matches(size ?? string.Empty, @"\d+").Select(m => int.Parse(m.Value)).ToList();
        var headcount = digits.Count > 0 ? digits.Max() : 0;
        if (headcount > 0 && (size ?? string.Empty).Contains('+')) headcount = Math.Max(headcount, 201);
        return headcount switch { 0 => 5, <= 50 => 3, <= 200 => 5, _ => 7 };
    }

    private static readonly (string Tier, string Band, decimal Min, decimal Mid, decimal Max)[] SarLadder =
    {
        ("Entry",            "Staff",         3000m,   4500m,   6000m),
        ("Junior",           "Staff",         6000m,   8000m,  10000m),
        ("Senior",           "Professional", 10000m,  13500m,  17000m),
        ("Lead",             "Management",   17000m,  22500m,  28000m),
        ("Executive",        "Executive",    28000m,  39000m,  50000m),
        ("Director",         "Executive",    50000m,  65000m,  80000m),
        ("Senior Executive", "Executive",    80000m, 105000m, 130000m),
    };

    // The ladder above is denominated in SAR, which holds a FIXED peg at 3.75/USD. The other
    // currencies here hold fixed USD pegs of their own, so converting between them is arithmetic
    // rather than a forecast. Everything absent from this table -- KWD (basket peg, not fixed)
    // and every non-GCC currency -- gets no bands at all and a note saying so. Inventing a number
    // and stamping a currency code on it is what produced "SAR 28000-50000" for USD tenants.
    private static readonly Dictionary<string, (decimal Factor, decimal Step)> SarPegs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SAR"] = (1m, 100m),
        ["USD"] = (1m / 3.75m, 50m),
        ["AED"] = (3.6725m / 3.75m, 100m),
        ["QAR"] = (3.64m / 3.75m, 100m),
        ["OMR"] = (0.3845m / 3.75m, 25m),
        ["BHD"] = (0.376m / 3.75m, 25m),
    };

    private static decimal RoundTo(decimal value, decimal step)
        => step <= 0 ? value : Math.Round(value / step, MidpointRounding.AwayFromZero) * step;

    /// <summary>The employee-side statutory deduction, where one is defined. Previously the Saudi
    /// GOSI line was appended for EVERY country, so a UAE or Kuwaiti tenant applying the draft
    /// wrote a 9.75% Saudi deduction into their own pay components.</summary>
    private static DraftPayComponent? StatutoryDeductionFor(string iso3) => iso3 switch
    {
        "SAU" => new("GOSI_DED", "GOSI Deduction", "Deduction", "Percentage", 0, 9.75m, false),
        "ARE" => new("GPSSA_DED", "GPSSA Pension Deduction", "Deduction", "Percentage", 0, 5m, false),
        _ => null,
    };

    private static bool MentionsContinuousOps(string? notes)
        => !string.IsNullOrWhiteSpace(notes)
           && Regex.IsMatch(notes, @"24\s*[/x-]?\s*7|round[- ]the[- ]clock|night\s*shift|rotating\s*shift|shift\s*work|field\s*crew",
                            RegexOptions.IgnoreCase);

    //
    // This is what ships whenever no model answers, so it has to stand on its own: keyed to the
    // inputs the form actually collects, and silent about nothing. Anything it cannot derive is
    // flagged and left empty rather than guessed.
    /// <summary>
    /// The deterministic company defaults the create-company wizard starts from (design contract Decision 1):
    /// the built-in template (organisation, grades, branch, employee-ID rule) plus the country's working week
    /// and fixed-date holiday calendar. <b>Static and model-free by construction</b> — it is reachable without an
    /// instance of this service, so no <c>ILlmClient</c> can sit behind it. Nothing here is written anywhere.
    /// </summary>
    public static SetupDraft DeterministicCompanyDefaults(string countryCode, string currencyCode, string? legalEntityName)
    {
        var iso3 = NormalizeCountry(countryCode);
        var notes = new List<string>();
        var profile = new CompanyProfile(countryCode, Industry: "General", CompanySize: "1-50", CurrencyCode: currencyCode,
            Notes: null, LegalEntityName: legalEntityName, BranchCity: null, OperatingModel: null, PayrollModel: null,
            Sections: new SetupSections(Org: true, Leave: false, Shifts: true, Payroll: false, Entity: true, Governance: true));
        var draft = DeterministicTemplate(profile, iso3, notes);
        return draft with { WorkingWeek = WorkingWeekFor(iso3), HolidayCalendar = HolidayCalendarFor(iso3, notes) };
    }

    private static SetupDraft DeterministicTemplate(CompanyProfile p, string iso3, List<string> notes)
    {
        var pack = PackFor(p.Industry);

        // Grades — rung count from headcount, money from the currency peg table.
        var count = GradeCountFor(p.CompanySize);
        var picks = count switch
        {
            3 => new[] { 0, 2, 4 },            // entry / senior / executive — a compressed ladder
            7 => new[] { 0, 1, 2, 3, 4, 5, 6 },
            _ => new[] { 0, 1, 2, 3, 4 },
        };
        // No "SAR" fallback. Assuming riyals for a tenant that never said so prices its whole ladder
        // in a currency nobody chose, and a salary band does not look wrong in the wrong currency.
        var currency = NormalizeCurrency(p.CurrencyCode);
        // Declared up front: the short-circuit below means the compiler cannot prove `out` ran.
        (decimal Factor, decimal Step) peg = default;
        var pegged = currency.Length > 0 && SarPegs.TryGetValue(currency, out peg);
        if (currency.Length == 0)
            notes.Add("Salary bands were left at zero because this workspace has not stated a currency. Set the currency in Setup - Localization, or pick one above, and regenerate.");
        else if (!pegged)
            notes.Add($"Salary bands were left at zero. The built-in ladder is denominated in SAR and {currency} has no fixed relationship to it, so any figure here would be invented. Set the bands before applying.");

        var grades = new List<DraftGrade>();
        for (var i = 0; i < picks.Length; i++)
        {
            var rung = SarLadder[picks[i]];
            var code = $"G{i + 1}";
            var name = $"Grade {i + 1} - {rung.Tier}";
            grades.Add(pegged
                ? new DraftGrade(code, name, rung.Band, i + 1,
                    RoundTo(rung.Min * peg.Factor, peg.Step),
                    RoundTo(rung.Mid * peg.Factor, peg.Step),
                    RoundTo(rung.Max * peg.Factor, peg.Step), currency)
                : new DraftGrade(code, name, rung.Band, i + 1, 0m, 0m, 0m, currency));
        }

        // Roles attach to a position on the ladder, not a hard-coded "G4" that may not exist.
        string Slot(GradeSlot slot)
        {
            var n = grades.Count;
            var idx = slot switch
            {
                GradeSlot.Top => n - 1,
                GradeSlot.Upper => n - 2,
                GradeSlot.Mid => n / 2,
                _ => n >= 5 ? 1 : 0,
            };
            return grades[Math.Clamp(idx, 0, n - 1)].Code;
        }

        var depts = new List<DraftDepartment>
        {
            new("HR", "Human Resources"), new("FIN", "Finance"),
            new("IT", "Information Technology"), new("ADMIN", "Administration"),
        };
        depts.AddRange(pack.Departments);

        var desigs = new List<DraftDesignation>
        {
            new("CEO", "Chief Executive Officer", "ADMIN", Slot(GradeSlot.Top), "Executive", true, 1),
            new("HR_MGR", "HR Manager", "HR", Slot(GradeSlot.Upper), "Management", true, 3),
            new("HR_OFF", "HR Officer", "HR", Slot(GradeSlot.Low), "Staff", false, 5),
            new("ACCOUNTANT", "Accountant", "FIN", Slot(GradeSlot.Low), "Staff", false, 5),
            new("IT_ENG", "IT Engineer", "IT", Slot(GradeSlot.Mid), "Staff", false, 5),
        };
        desigs.AddRange(pack.Designations.Select(d => new DraftDesignation(
            d.Code, d.Title, d.Dept, Slot(d.Slot), d.IsManager ? "Management" : "Staff", d.IsManager, d.Rank)));

        // The hire-time salary writer copies these amounts into monthly salary components and
        // does not convert a frequency. Never divide a monthly salary for a proposed pay schedule.
        const string frequency = "Monthly";
        decimal PerRun(decimal monthly) => Math.Round(monthly, 2);

        var gradePay = grades.SelectMany(g => new[]
        {
            new DraftGradePayComponent(g.Code, "BASIC", "Basic Salary", "Earning", "Fixed", PerRun(g.MidSalary * 0.60m), 0, false, frequency),
            new DraftGradePayComponent(g.Code, "HOUSING", "Housing Allowance", "Earning", "PercentOfBasic", 0, 25, false, frequency),
            new DraftGradePayComponent(g.Code, "TRANSPORT", "Transport Allowance", "Earning", "Fixed", PerRun(g.MidSalary * 0.10m), 0, false, frequency),
            new DraftGradePayComponent(g.Code, "OTHER", "Other Allowance", "Earning", "Fixed", PerRun(g.MidSalary * 0.05m), 0, false, frequency),
        }).ToList();

        var branches = new List<DraftBranch>
        {
            new("HQ", "Head Office", string.IsNullOrWhiteSpace(p.BranchCity) ? DefaultCityFor(iso3) : p.BranchCity!, true),
        };
        var costCenters = depts.Select(d => new DraftCostCenter($"CC_{d.Code}", $"{d.NameEn} Cost Center", d.Code)).ToList();

        var leaves = new List<DraftLeaveType>
        {
            new("ANNUAL", "Annual Leave", "Standard", true, 30, false, "#00C896"),
            new("SICK", "Sick Leave", "Medical", true, 30, true, "#f59e0b"),
            new("PAT", "Paternity Leave", "Parental", true, 3, false, "#8b5cf6"),
            new("UNPAID", "Unpaid Leave", "Unpaid", false, 30, false, "#64748b"),
        };
        // Saudi maternity is not drafted here: GenerateAsync's WithKsaStatutoryLeave adds it, and the
        // other Saudi statutory leaves, from the Art. 151 statutory rule (84 days from 19 Feb 2025).
        // For every other country this 70 has not been checked against local law.
        if (iso3 != "SAU") leaves.Insert(2, new("MAT", "Maternity Leave", "Parental", true, 70, true, "#ec4899"));
        if (iso3 == "SAU") leaves.Add(new("HAJJ", "Hajj Leave", "Religious", true, 10, false, "#2F6BFF"));

        // A three-shift rotation is a real operational commitment, not a default. The customer now
        // states the pattern directly; where they left it unset, ResolveWorkPattern falls back to
        // the old inference (industry, then a regex over the free-text note).
        var pattern = ResolveWorkPattern(p);
        var continuous = pattern == "ContinuousThreeShifts";
        var shifts = ShiftsFor(pattern);

        var pay = new List<DraftPayComponent>
        {
            new("BASIC", "Basic Salary", "Earning", "Fixed", 0, 0, false),
            new("HRA", "Housing Allowance", "Earning", "Percentage", 0, 25, false),
            new("TRA", "Transport Allowance", "Earning", "Fixed", 0, 0, false),
        };
        // An expatriate workforce carries contractual entitlements a national one does not. The
        // amount is left at zero deliberately: an annual ticket accrual is a route and a class,
        // not a figure this form can know.
        var mix = ResolveWorkforceMix(p);
        if (mix is "Mixed" or "MostlyExpat")
        {
            pay.Add(new("AIR_TICKET", "Annual Air Ticket Accrual", "Earning", "Fixed", 0, 0, false));
            notes.Add("An annual air-ticket accrual was added for your expatriate staff with no amount — set the monthly accrual from the routes and cabin class your contracts promise.");
        }
        var statutory = StatutoryDeductionFor(iso3);
        if (statutory is not null) pay.Add(statutory);
        else if (p.Sections.Payroll)
            notes.Add($"No statutory payroll deduction was added — there is no built-in contribution rule for {iso3}. Add the local one before running payroll.");
        if (mix == "MostlyExpat" && iso3 is "SAU" or "ARE")
            notes.Add($"The {(iso3 == "SAU" ? "GOSI" : "GPSSA")} deduction in this draft is the rate for nationals. It does not apply to expatriate staff, who are on a different (usually smaller) contribution — check which of your employees it should be attached to before running payroll.");

        if (!string.IsNullOrWhiteSpace(p.Notes) && !continuous)
            notes.Add("Your free-text note was not applied. Without AI the template only recognises shift-pattern hints such as \"24/7\" or \"field crews\"; everything else in that box needs the AI provider.");

        // The five trailing members are filled by GenerateAsync from country data, never here:
        // entitlements, holidays, attendance, overtime and localization are not industry guesses.
        return new SetupDraft(branches, depts, costCenters, desigs, grades, gradePay, leaves, shifts, null, pay, new(),
            EmployeeIdRuleFor(p, iso3), new(), null, null, null, null);
    }

    /// <summary>"Riyadh" was the hard-coded head-office city for every country on earth.</summary>
    private static string DefaultCityFor(string iso3) => iso3 switch
    {
        "SAU" => "Riyadh",
        "ARE" => "Dubai",
        "QAT" => "Doha",
        "KWT" => "Kuwait City",
        "BHR" => "Manama",
        "OMN" => "Muscat",
        _ => "Head Office",
    };

    private static DraftEmployeeIdRule EmployeeIdRuleFor(CompanyProfile profile, string iso3)
    {
        var prefix = string.IsNullOrWhiteSpace(profile.LegalEntityName)
            ? "EMP"
            : new string(profile.LegalEntityName.Where(char.IsLetterOrDigit).Take(3).ToArray()).ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(prefix)) prefix = "EMP";
        return new DraftEmployeeIdRule(prefix, true, true, true, true, 4, 1, false);
    }

    // ── Provider/model resolution (mirrors AiAdvisoryService) ────────────────

}
