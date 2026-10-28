using Zayra.Api.Application.Compliance;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Leave;

/// <summary>
/// The Saudi statutory floors for a leave policy, as structured checks. A configured value below a floor is a
/// WARNING the admin acknowledges (owner decision 2026-10-25), never a refusal: <see cref="LeavePoliciesController"/>
/// saves with the acknowledgement and audits it. The figures still come from the platform statutory rules (tenant
/// null, so a tenant row cannot move them) with the compiled fallbacks; nothing is hard-coded here.
///
/// <para>Covers special leave (Arts. 113/114/151/160: days, per-request cap, unpaid, working-day counting), annual
/// leave (Art. 109, every accrual method) and sick leave (Art. 117 scale and its service requirement). Every check
/// is returned, below or not, so the policy screen can show the Saudi default beside what is typed.</para>
/// </summary>
public static class KsaLeaveFloorChecks
{
    public const string SpecialEntitlement = "leave_special_entitlement_below_minimum";
    public const string SpecialCap = "leave_special_cap_below_minimum";
    public const string SpecialWorkingDays = "leave_special_working_days_below_minimum";
    public const string SpecialUnpaid = "leave_special_unpaid";
    public const string Annual = "leave_annual_below_minimum";
    public const string Sick = "leave_sick_below_minimum";
    public const string SickService = "leave_sick_service_requirement";

    public static async Task<List<FloorCheck>> ChecksAsync(
        ZayraDbContext db, Guid tenantId, LeaveType leaveType, LeavePolicy policy, CancellationToken ct)
    {
        var checks = new List<FloorCheck>();
        if (string.Equals(policy.Status?.Trim(), "Archived", StringComparison.OrdinalIgnoreCase)) return checks;
        var reach = await KsaStatutoryLeavePolicyGuard.ReachAsync(db, tenantId, policy.CountryCode, policy.CompanyId, ct);
        if (reach == KsaPolicyReach.None) return checks;

        var rules = new StatutoryRuleReader(db);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var days = policy.AnnualEntitlementDays;
        var cap = policy.MaximumDaysPerRequest;

        var kind = KsaStatutorySpecialLeave.Classify(leaveType.Code, leaveType.NameEn, leaveType.Category);
        if (kind is { } k)
        {
            var floor = await KsaStatutorySpecialLeave.ResolveFloorAsync(rules, k, today, ct);
            if (floor is { } f)
            {
                var what = KsaStatutorySpecialLeave.Describe(k);
                var whatAr = DescribeAr(k);
                var (cite, citeAr) = Cite(k);
                var calendar = policy.WeekendsIncluded && policy.PublicHolidaysIncluded;
                if (KsaStatutorySpecialLeave.NeedsCalendarCounting(k, calendar))
                {
                    var below = days < f || (cap > 0m && cap < f);
                    checks.Add(Make(SpecialWorkingDays, "annualEntitlementDays", below, days, f, cite,
                        $"{what} is {f:0.##} calendar days by law, and this policy counts working days at {days:0.##}"
                        + (cap > 0m ? $" (cap {cap:0.##})" : string.Empty) + $", below the legal minimum ({cite}). To meet it, count weekends and public holidays and set at least {f:0.##} days.",
                        $"{whatAr} مدتها {f:0.##} يومًا تقويميًا بحكم النظام، وهذه السياسة تحتسب أيام العمل بواقع {days:0.##} يومًا، وهو أقل من الحد الأدنى النظامي ({citeAr}).",
                        "Working-day count vs the legal calendar minimum", "احتساب أيام العمل مقابل الحد الأدنى النظامي بالتقويم"));
                }
                else
                {
                    checks.Add(Make(SpecialEntitlement, "annualEntitlementDays", days < f, days, f, cite,
                        $"{what}: this policy grants {days:0.##} days, below the legal minimum of {f:0.##} days ({cite}).",
                        $"{whatAr}: تمنح هذه السياسة {days:0.##} يومًا، وهو أقل من الحد الأدنى النظامي البالغ {f:0.##} يومًا ({citeAr}).",
                        "Days granted", "عدد الأيام الممنوحة"));
                    if (cap > 0m)
                        checks.Add(Make(SpecialCap, "maximumDaysPerRequest", cap < f, cap, f, cite,
                            $"{what}: a cap of {cap:0.##} days per request is below the legal minimum of {f:0.##} days ({cite}).",
                            $"{whatAr}: الحد الأقصى {cap:0.##} يومًا للطلب الواحد أقل من الحد الأدنى النظامي البالغ {f:0.##} يومًا ({citeAr}).",
                            "Maximum days per request", "الحد الأقصى للأيام في الطلب"));
                }
                checks.Add(Make(SpecialUnpaid, "payrollImpact",
                    string.Equals(policy.PayrollImpact?.Trim(), "Unpaid", StringComparison.OrdinalIgnoreCase), null, null, cite,
                    $"{what} is fully paid by law ({cite}); this policy makes it unpaid.",
                    $"{whatAr} مدفوعة الأجر بالكامل بحكم النظام ({citeAr})، وهذه السياسة تجعلها بدون أجر.",
                    "Pay treatment", "معاملة الأجر"));

                if (reach == KsaPolicyReach.Mixed && checks.Any(c => c.Below))
                {
                    // A neutral policy in a mixed tenant would also govern Saudi staff, unless a compliant Saudi-scoped
                    // sibling outranks it for them.
                    if (await KsaStatutoryLeavePolicyGuard.HasCompliantSaudiPolicyAsync(
                            db, tenantId, leaveType.Id, policy.Id, k, f, ct))
                        return checks.Select(c => c with { Below = false }).ToList();
                    checks = checks.Select(c => c.Below
                        ? c with { Warning = c.Warning with { Message = c.Warning.Message
                            + " This policy has no country or company, so it would also govern Saudi employees." } }
                        : c).ToList();
                }
            }
        }
        else if (reach == KsaPolicyReach.SaudiOnly)
        {
            var protectedKind = KsaStatutorySpecialLeave.ProtectedKind(leaveType.Code, leaveType.NameEn, leaveType.Category);
            if (protectedKind == "Annual")
            {
                var floor = await rules.GetDecimalAsync(CountryCodes.Saudi, Jurisdictions.KsaMainland,
                    KsaLeaveHoursRuleKeys.AnnualLeaveBaseDays, today, null, ct) ?? KsaLeaveHoursDefaults.AnnualLeaveBaseDays;
                var tiered = await rules.GetDecimalAsync(CountryCodes.Saudi, Jurisdictions.KsaMainland,
                    KsaLeaveHoursRuleKeys.AnnualLeaveTieredDays, today, null, ct) ?? KsaLeaveHoursDefaults.AnnualLeaveTieredDays;
                checks.Add(Make(Annual, "annualEntitlementDays", days < floor, days, floor, "Labor Law Art. 109",
                    $"Annual leave of {days:0.##} days is below the legal minimum of {floor:0.##} days ({tiered:0.##} after five years of service) (Labor Law Art. 109).",
                    $"الإجازة السنوية {days:0.##} يومًا أقل من الحد الأدنى النظامي البالغ {floor:0.##} يومًا ({tiered:0.##} يومًا بعد خمس سنوات من الخدمة) (نظام العمل، المادة 109).",
                    "Annual leave days", "أيام الإجازة السنوية"));
            }
            else if (protectedKind == "Sick")
            {
                async Task<decimal> Band(string key, decimal fallback)
                    => await rules.GetDecimalAsync(CountryCodes.Saudi, Jurisdictions.KsaMainland, key, today, null, ct) ?? fallback;
                var floor = await Band(KsaLeaveHoursRuleKeys.SickBand1Days, KsaLeaveHoursDefaults.SickBand1Days)
                    + await Band(KsaLeaveHoursRuleKeys.SickBand2Days, KsaLeaveHoursDefaults.SickBand2Days)
                    + await Band(KsaLeaveHoursRuleKeys.SickBand3Days, KsaLeaveHoursDefaults.SickBand3Days);
                checks.Add(Make(Sick, "annualEntitlementDays", days < floor, days, floor, "Labor Law Art. 117",
                    $"Sick leave of {days:0.##} days a year is below the legal scale of {floor:0.##} days (30 full pay, 60 at three quarters, 30 unpaid) (Labor Law Art. 117).",
                    $"الإجازة المرضية {days:0.##} يومًا في السنة أقل من السلم النظامي البالغ {floor:0.##} يومًا (30 بأجر كامل و60 بثلاثة أرباع الأجر و30 بدون أجر) (نظام العمل، المادة 117).",
                    "Sick leave days per year", "أيام الإجازة المرضية في السنة"));
                checks.Add(Make(SickService, "minimumServiceMonths", policy.MinimumServiceMonths > 0, policy.MinimumServiceMonths, 0m, "Labor Law Art. 117",
                    "Sick leave for employees in Saudi Arabia applies from the first day of service (Labor Law Art. 117); this policy requires "
                    + $"{policy.MinimumServiceMonths} months of service first.",
                    $"الإجازة المرضية للعاملين في المملكة تسري من اليوم الأول للخدمة (نظام العمل، المادة 117)، وهذه السياسة تشترط {policy.MinimumServiceMonths} شهرًا من الخدمة أولًا.",
                    "Service required before use (months)", "مدة الخدمة المطلوبة قبل الاستخدام (بالأشهر)"));
            }
        }
        return checks;
    }

    /// <summary>True when the policy carries an acknowledgement, for this rule, of the value it has now.</summary>
    public static bool IsAcknowledgedBelowMinimum(LeavePolicy policy, string code)
        => FloorAckStore.Parse(policy.BelowMinimumAcksJson).Any(a => a.Code == code && a.ConfiguredValue == policy.AnnualEntitlementDays);

    public static IReadOnlyList<FloorWarning> Warnings(IEnumerable<FloorCheck> checks)
        => checks.Where(c => c.Below).Select(c => c.Warning).ToList();

    private static FloorCheck Make(string code, string field, bool below, decimal? configured, decimal? statutory, string citation,
        string messageEn, string messageAr, string label, string labelAr)
        => new(new FloorWarning(code, field, messageEn, false, statutory, configured, citation, messageAr), below, label, labelAr);

    private static (string En, string Ar) Cite(KsaStatutoryLeaveKind kind) => kind switch
    {
        KsaStatutoryLeaveKind.Maternity => ("Labor Law Art. 151", "نظام العمل، المادة 151"),
        KsaStatutoryLeaveKind.Hajj => ("Labor Law Art. 114", "نظام العمل، المادة 114"),
        KsaStatutoryLeaveKind.IddahMuslim or KsaStatutoryLeaveKind.IddahNonMuslim => ("Labor Law Art. 160", "نظام العمل، المادة 160"),
        _ => ("Labor Law Art. 113", "نظام العمل، المادة 113"),
    };

    private static string DescribeAr(KsaStatutoryLeaveKind kind) => kind switch
    {
        KsaStatutoryLeaveKind.Maternity => "إجازة الوضع",
        KsaStatutoryLeaveKind.Paternity => "إجازة المولود",
        KsaStatutoryLeaveKind.Marriage => "إجازة الزواج",
        KsaStatutoryLeaveKind.Bereavement => "إجازة الوفاة (الزوج أو الأصول أو الفروع)",
        KsaStatutoryLeaveKind.BereavementSibling => "إجازة وفاة الأخ أو الأخت",
        KsaStatutoryLeaveKind.Hajj => "إجازة الحج",
        KsaStatutoryLeaveKind.IddahMuslim => "إجازة العدة",
        KsaStatutoryLeaveKind.IddahNonMuslim => "إجازة الأرملة غير المسلمة",
        _ => kind.ToString(),
    };
}
