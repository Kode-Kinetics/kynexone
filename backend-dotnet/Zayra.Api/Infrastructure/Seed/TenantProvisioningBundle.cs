using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Data;
using Zayra.Api.Models;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Recruitment;

namespace Zayra.Api.Infrastructure.Seed;

/// <summary>
/// FIX 1 (program C1 / register row S3): the single idempotent per-tenant provisioning bundle,
/// invoked on EVERY new tenant so no tenant is born without its statutory/reference/config
/// foundation. Before this, country rules seeded only for the bootstrap tenant and MasterData /
/// HR categories / templates / default policies seeded only in the demo path — so every
/// post-bootstrap tenant came up broken.
///
/// Idempotency contract (gold standard = <see cref="GlDriverSeeder"/>, security review B1):
/// STRICTLY insert-if-absent, keyed on the natural key, using <see cref="EntityFrameworkQueryableExtensions.IgnoreQueryFilters"/>
/// so the ambient tenant filter never hides the target tenant's rows. It NEVER updates or resets
/// an existing row (weekend days, tax %, a leave policy, a template a client has edited), so it is
/// safe to re-run and cannot cause the "data reverts on deploy" incident class.
///
/// SCOPE (build review B2): GOSI/statutory rules are PLATFORM-GLOBAL defaults (TenantId =
/// Guid.Empty / null) seeded once at startup and inherited by every tenant through the resolver
/// chain — this bundle deliberately does NOT duplicate them per-tenant. The genuinely per-tenant
/// "country rules" surface is <see cref="CountryPayrollRule"/> (ITenantOwned), seeded here.
///
/// Everything installed is a configurable DEFAULT the client can edit — no claim of statutory
/// correctness (matching the StatutoryRuleSeeder / CompanyTaxPolicy "configurable foundation"
/// framing). Country packs are tier-tagged per OD-4 (KSA/UAE certified; QA/KW/OM/BH fail-loud;
/// the rest HR-only, payroll hard-blocked by the existing statutory-pack guard at run time).
/// </summary>
public static class TenantProvisioningBundle
{
    public readonly record struct ProvisionResult(
        int CountryRules, int MasterDataTypes, int MasterDataValues, int HrCategories,
        int AttendancePolicies, int LeaveTypes, int LeavePolicies, int ApprovalWorkflows, int NotificationTemplates,
        int ComplianceProfiles = 0, int PayComponents = 0, int LetterTemplates = 0);

    /// <param name="homeCountryCode">
    /// The tenant's HOME JURISDICTION, stated by the platform administrator at tenant creation.
    /// REQUIRED — statutory seeding runs inside this method, and a country that is blank, guessed
    /// from a currency or inferred from a slug seeds the wrong labour law in silence. An unstated or
    /// unrecognized value throws (<see cref="HomeJurisdiction.Require"/>) rather than defaulting, so
    /// no tenant can be born without a country again.
    /// </param>
    public static async Task<ProvisionResult> ProvisionAsync(
        ZayraDbContext db, Guid tenantId, string homeCountryCode, CancellationToken ct)
    {
        // Validate BEFORE the tenantId short-circuit: a caller that passes no country is wrong
        // whichever tenant it names, and the failure must be loud at the call site.
        var home = HomeJurisdiction.Require(homeCountryCode, "tenant home country");
        if (tenantId == Guid.Empty) return default;

        var countryRules  = await InstallCountryPayrollRulesAsync(db, tenantId, ct);
        var (mdTypes, mdValues) = await InstallMasterDataAsync(db, tenantId, ct);
        var hrCategories  = await InstallHrRequestCategoriesAsync(db, tenantId, ct);
        var attnPolicies  = await InstallDefaultAttendancePolicyAsync(db, tenantId, ct);
        var (leaveTypes, leavePolicies) = await InstallDefaultLeaveAsync(db, tenantId, ct, home);
        var apPolicies    = await InstallDefaultApprovalWorkflowsAsync(db, tenantId, ct);
        var notifs        = await InstallNotificationTemplatesAsync(db, tenantId, ct);
        var compliance    = await InstallComplianceProfilesAsync(db, tenantId, ct);
        // F2 — the system pay-component catalog as REAL rows, on every provisioning path (the bootstrap tenant
        // included, which never reached PayComponentSeeder before), so the compiled fallback in
        // PayComponentEngine.ResolveInEffect is a genuine last resort rather than the normal path. Same
        // insert-if-absent contract as everything else in this bundle.
        var payComponents = (await PayComponentSeeder.SeedTenantDefaultsAsync(db, tenantId, ct)).Components;
        // The bilingual HR letter catalogue. Without it a brand-new tenant's "Issue a Letter" tab
        // has nothing to issue from and ESS offers an empty document-request dropdown — the module
        // renders its shell and cannot be used. Installed here as well as by TenantDefaultsBackfill
        // so a tenant created between deploys is usable immediately, not at the next restart.
        var letterTemplates = await InstallDefaultLetterTemplatesAsync(db, tenantId, ct);

        await db.SaveChangesAsync(ct);
        return new ProvisionResult(countryRules, mdTypes, mdValues, hrCategories,
            attnPolicies, leaveTypes, leavePolicies, apPolicies, notifs, compliance, payComponents,
            letterTemplates);
    }

    // ── 7b. The bilingual HR letter catalogue ──
    /// <summary>
    /// Plants the bilingual default for every letter type this tenant has no tenant-wide template
    /// for. Shared by <see cref="ProvisionAsync"/> (new tenants) and
    /// <c>TenantDefaultsBackfill</c> (tenants that predate the module), so the two paths cannot
    /// drift and <see cref="HrLetterTemplateDefaults"/> stays the single source of the wording.
    /// </summary>
    internal static async Task<int> InstallDefaultLetterTemplatesAsync(
        ZayraDbContext db, Guid tenantId, CancellationToken ct)
    {
        const string why =
            "Seeding/backfill runs with no HTTP principal, so the company read filter resolves to an "
            + "EMPTY company scope and would hide every template the tenant already has. The gap "
            + "check would then re-insert existing rows and trip ux_hr_letter_templates_scope_type. "
            + "The tenant filter is re-applied by the helper; no other tenant is observable.";

        // Deliberately NOT filtered on IsDeleted. A type whose template has been removed is a
        // DECISION; an unattended pass that resurrected it on the next deploy would be exactly the
        // "data reverts on deploy" incident class this bundle's idempotency contract forbids. An
        // administrator who wants it back still has POST /api/hr-letters/templates/seed-defaults,
        // which is an explicit, audited restore.
        var covered = await ScopedBypass.TenantWide(db.HrLetterTemplates, tenantId, why)
            .Where(x => x.CompanyId == null)
            .Select(x => x.LetterType)
            .ToListAsync(ct);

        var added = 0;
        foreach (var template in HrLetterTemplateDefaults.Build())
        {
            if (covered.Contains(template.LetterType, StringComparer.Ordinal)) continue;
            template.Id = Guid.NewGuid();
            template.TenantId = tenantId;
            // HrLetterTemplate is ICompanyScoped: a null CompanyId is the tenant default that every
            // company-scoped user inherits, which is the scope the admin action writes too.
            template.CompanyId = null;
            db.HrLetterTemplates.Add(template);
            added++;
        }

        return added;
    }

    // ── 8. Tenant-default compliance profiles per GCC state (§3.5) ──
    // An EDITABLE starting point mirroring the code readiness floor — never forced (opt-out by editing),
    // insert-if-absent by (tenant, CompanyId==null, country). The CODE floor (GccReadinessFloor) remains
    // the GUARANTEE regardless of whether this seed ran, so a fresh/mis-provisioned tenant still gates.
    // Keys are jurisdiction readiness vocabulary (EmployeeFieldRegistry), not tenant business data.
    /// <summary>Exposed to tests (InternalsVisibleTo) so the readiness-resolver jurisdiction guard can
    /// assert against the REAL seeded set — six tenant-default rows, one per GCC state — rather than a
    /// copy that could drift away from what provisioning actually writes.</summary>
    internal static readonly (string Country, string RequiredFieldsJson)[] ComplianceSeeds =
    {
        ("SA", """[{"key":"GosiReference","category":"identity","failClosed":true},{"key":"IqamaNumber","category":"identity","failClosed":true,"appliesWhen":{"nationalityNot":"SA"}},{"key":"doc:Contract","category":"contract","failClosed":false}]"""),
        ("AE", """[{"key":"EmiratesId","category":"identity","failClosed":true},{"key":"WorkPermitNumber","category":"identity","failClosed":true,"appliesWhen":{"nationalityNot":"AE"}},{"key":"doc:Contract","category":"contract","failClosed":false}]"""),
        ("QA", """[{"key":"Qid","category":"identity","failClosed":true,"appliesWhen":{"nationalityNot":"QA"}},{"key":"doc:Contract","category":"contract","failClosed":false}]"""),
        ("KW", """[{"key":"CivilId","category":"identity","failClosed":true,"appliesWhen":{"nationalityNot":"KW"}},{"key":"doc:Contract","category":"contract","failClosed":false}]"""),
        ("OM", """[{"key":"CivilId","category":"identity","failClosed":true,"appliesWhen":{"nationalityNot":"OM"}},{"key":"doc:Contract","category":"contract","failClosed":false}]"""),
        ("BH", """[{"key":"CivilId","category":"identity","failClosed":true,"appliesWhen":{"nationalityNot":"BH"}},{"key":"SocialInsuranceReference","category":"identity","failClosed":true},{"key":"doc:Contract","category":"contract","failClosed":false}]"""),
    };

    private static async Task<int> InstallComplianceProfilesAsync(ZayraDbContext db, Guid tenantId, CancellationToken ct)
    {
        // IgnoreQueryFilters is intentional: seeder read scoped by explicit tenantId; insert-if-absent, never touches another tenant.
        var existing = (await db.CompanyComplianceProfiles.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.TenantId == tenantId && p.CompanyId == null)
            .Select(p => p.CountryCode).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var (country, json) in ComplianceSeeds)
        {
            if (!existing.Add(country)) continue; // insert-if-absent by (tenant, default, country)
            db.CompanyComplianceProfiles.Add(new CompanyComplianceProfile
            {
                TenantId = tenantId, CompanyId = null, CountryCode = country,
                Jurisdiction = string.Empty, CompliancePack = string.Empty,
                EffectiveFrom = new DateOnly(2020, 1, 1), Status = CompanyPolicyStatuses.Active,
                RequiredFieldsJson = json,
                Notes = "Seeded tenant-default readiness profile (editable). The code floor remains the guarantee.",
            });
            added++;
        }
        return added;
    }

    // ── 1. Country payroll rules (per-rule idempotent; UAE weekend corrected; tier-tagged) ──

    // (CountryCode, Currency, Weekend, AnnualLeaveDays, SickLeaveDays, ProbationMonths,
    //  NoticeDays, OtNormal, OtHoliday, EndOfServiceNote). Weekend uses the canonical hyphen
    //  pair format; WorkWeekService normalises it. GCC rest days: KSA/QA/KW/OM/BH = Fri-Sat;
    //  UAE = Sat-Sun (post 1-Jan-2022 reform — the old Fri-Sat was a defect).
    private static readonly (string Country, string Currency, string Weekend, int Annual, int Sick, int Probation, int Notice, decimal OtNormal, decimal OtHoliday, string Eosb)[] Packs =
    {
        // GCC
        ("AE", "AED", "Sat-Sun", 30, 90, 6, 30, 1.25m, 1.50m, "Gratuity: 21 days/yr for first 5 yrs, 30 days/yr thereafter (UAE Labour Law)"),
        ("SA", "SAR", "Fri-Sat", 21, 120, 3, 60, 1.50m, 2.00m, "End-of-service: 0.5 month/yr first 5 yrs, 1 month/yr after"),
        ("QA", "QAR", "Fri-Sat", 21, 84, 6, 30, 1.25m, 1.50m, "End-of-service gratuity: min 3 weeks basic/yr (Labour Law No. 14/2004)"),
        ("KW", "KWD", "Fri-Sat", 30, 75, 3, 90, 1.25m, 1.50m, "Indemnity: 15 days/yr first 5 yrs, 1 month/yr thereafter"),
        ("OM", "OMR", "Fri-Sat", 30, 70, 3, 30, 1.25m, 2.00m, "Gratuity per Omani Labour Law for non-citizens"),
        ("BH", "BHD", "Fri-Sat", 30, 55, 3, 30, 1.25m, 1.50m, "Leaving indemnity: 15 days/yr first 3 yrs, 1 month/yr after"),
        // Middle East / Africa
        ("EG", "EGP", "Fri-Sat", 21, 180, 3, 60, 1.35m, 2.00m, "End-of-service per Egyptian Labour Law No. 12/2003"),
        ("ZA", "ZAR", "Sat-Sun", 21, 30, 3, 30, 1.50m, 2.00m, "Severance 1 week/yr (BCEA); sick 30 days per 36-month cycle"),
        ("NG", "NGN", "Sat-Sun", 6, 12, 3, 30, 1.50m, 2.00m, "Per Nigerian Labour Act; redundancy by agreement"),
        // Asia
        ("IN", "INR", "Sat-Sun", 18, 12, 6, 30, 2.00m, 2.00m, "Gratuity (Payment of Gratuity Act): 15 days wages/yr after 5 yrs"),
        ("PK", "PKR", "Sat-Sun", 14, 16, 3, 30, 2.00m, 2.00m, "Gratuity 30 days/yr or provident fund"),
        ("PH", "PHP", "Sat-Sun", 5, 0, 6, 30, 1.25m, 2.00m, "13th-month pay mandatory; separation pay per Labor Code"),
        ("SG", "SGD", "Sat-Sun", 14, 14, 3, 30, 1.50m, 2.00m, "No statutory gratuity; OT under Employment Act for covered staff"),
        // Europe
        ("GB", "GBP", "Sat-Sun", 28, 28, 3, 30, 1.50m, 2.00m, "Statutory: no gratuity; redundancy pay per service length"),
        ("DE", "EUR", "Sat-Sun", 20, 42, 6, 28, 1.25m, 1.50m, "No statutory severance; 6 weeks continued sick pay; notice per BGB §622"),
        ("FR", "EUR", "Sat-Sun", 25, 90, 2, 30, 1.25m, 1.50m, "35-hr week; severance per Code du Travail; OT +25% then +50%"),
        // North America
        ("US", "USD", "Sat-Sun", 15, 5, 3, 14, 1.50m, 1.50m, "At-will; FLSA overtime 1.5x over 40 hrs/week; no statutory gratuity"),
        ("CA", "CAD", "Sat-Sun", 10, 10, 3, 14, 1.50m, 1.50m, "Vacation pay 4%+; severance per ESA; no gratuity (province-specific)"),
        // Oceania
        ("AU", "AUD", "Sat-Sun", 20, 10, 6, 28, 1.50m, 2.00m, "4 weeks annual leave; redundancy & long-service leave per NES"),
    };

    private static async Task<int> InstallCountryPayrollRulesAsync(ZayraDbContext db, Guid tenantId, CancellationToken ct)
    {
        // IgnoreQueryFilters is intentional: seeder read scoped by explicit tenantId; insert-if-absent, never touches another tenant.
        var existing = (await db.CountryPayrollRules.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.TenantId == tenantId)
            .Select(r => new { r.CountryCode, r.RuleKey })
            .ToListAsync(ct))
            .Select(x => (x.CountryCode, x.RuleKey)).ToHashSet();

        var added = 0;
        void Add(string country, string key, string val, string type, string desc)
        {
            if (!existing.Add((country, key))) return; // insert-if-absent by natural key
            db.CountryPayrollRules.Add(new CountryPayrollRule
            {
                TenantId = tenantId, CountryCode = country, RuleKey = key,
                RuleValue = val, DataType = type, Description = desc,
            });
            added++;
        }

        foreach (var p in Packs)
        {
            Add(p.Country, "default_currency", p.Currency, "string", "Default payroll currency (configurable default)");
            Add(p.Country, "weekend_days", p.Weekend, "string", "Configurable weekend/rest days (canonical day-pair) — consumed by WorkWeekService");
            Add(p.Country, "annual_leave_days", p.Annual.ToString(), "int", "Configurable annual leave entitlement default (days)");
            Add(p.Country, "sick_leave_days", p.Sick.ToString(), "int", "Configurable sick leave entitlement default (days)");
            Add(p.Country, "probation_months", p.Probation.ToString(), "int", "Configurable maximum probation period default (months)");
            Add(p.Country, "notice_period_days", p.Notice.ToString(), "int", "Configurable notice period default (days)");
            Add(p.Country, "overtime_multiplier_normal", p.OtNormal.ToString(System.Globalization.CultureInfo.InvariantCulture), "decimal", "Configurable overtime multiplier — normal day");
            Add(p.Country, "overtime_multiplier_holiday", p.OtHoliday.ToString(System.Globalization.CultureInfo.InvariantCulture), "decimal", "Configurable overtime multiplier — public holiday/rest day");
            Add(p.Country, "end_of_service", p.Eosb, "string", "End-of-service / gratuity reference note (configurable)");
            // OD-4 tier metadata on the pack: certified | fail-loud | hr-only.
            Add(p.Country, "payroll_tier", CountryTier.TierLabel(CountryTier.GetTier(p.Country)), "string",
                "OD-4 payroll certification tier (certified=KSA/UAE, fail-loud=QA/KW/OM/BH, hr-only=rest)");
        }
        return added;
    }

    // ── 2. MasterData system types + starter values (Program O1 / row S162; A12 canonical codes) ──

    private sealed record MdType(string Code, string NameEn, string NameAr, (string Code, string En, string Ar)[] Values);

    private static readonly MdType[] MasterTypes =
    {
        new("Gender", "Gender", "الجنس", new[] { ("MALE", "Male", "ذكر"), ("FEMALE", "Female", "أنثى") }),
        new("MaritalStatus", "Marital Status", "الحالة الاجتماعية", new[]
        {
            ("SINGLE", "Single", "أعزب"), ("MARRIED", "Married", "متزوج"),
            ("DIVORCED", "Divorced", "مطلق"), ("WIDOWED", "Widowed", "أرمل"),
        }),
        new("EmploymentType", "Employment Type", "نوع التوظيف", new[]
        {
            ("FULL_TIME", "Full-Time", "دوام كامل"), ("PART_TIME", "Part-Time", "دوام جزئي"),
            ("CONTRACT", "Contract", "عقد"), ("TEMPORARY", "Temporary", "مؤقت"),
        }),
        new("ContractType", "Contract Type", "نوع العقد", new[]
        {
            ("LIMITED", "Limited Term", "محدد المدة"), ("UNLIMITED", "Unlimited Term", "غير محدد المدة"),
        }),
    };

    private static async Task<(int types, int values)> InstallMasterDataAsync(ZayraDbContext db, Guid tenantId, CancellationToken ct)
    {
        // IgnoreQueryFilters is intentional: seeder read scoped by explicit tenantId; insert-if-absent, never touches another tenant.
        var existingTypes = await db.MasterDataTypes.IgnoreQueryFilters().AsNoTracking()
            .Where(t => t.TenantId == tenantId)
            .Select(t => new { t.Id, t.Code })
            .ToListAsync(ct);
        var typeByCode = existingTypes.ToDictionary(t => t.Code, t => t.Id, StringComparer.OrdinalIgnoreCase);
        // IgnoreQueryFilters is intentional: seeder read scoped by explicit tenantId; insert-if-absent, never touches another tenant.
        var existingValues = (await db.MasterDataValues.IgnoreQueryFilters().AsNoTracking()
            .Where(v => v.TenantId == tenantId)
            .Select(v => new { v.TypeId, v.Code })
            .ToListAsync(ct))
            .Select(x => (x.TypeId, x.Code.ToUpperInvariant())).ToHashSet();

        int typesAdded = 0, valuesAdded = 0;
        foreach (var t in MasterTypes)
        {
            if (!typeByCode.TryGetValue(t.Code, out var typeId))
            {
                var type = new MasterDataType
                {
                    TenantId = tenantId, Code = t.Code, NameEn = t.NameEn, NameAr = t.NameAr,
                    IsSystemDefined = true, AllowCustomValues = true, IsActive = true,
                };
                db.MasterDataTypes.Add(type);
                typeId = type.Id;
                typeByCode[t.Code] = typeId;
                typesAdded++;
            }

            var sort = 0;
            foreach (var (code, en, ar) in t.Values)
            {
                sort++;
                if (!existingValues.Add((typeId, code.ToUpperInvariant()))) continue;
                db.MasterDataValues.Add(new MasterDataValue
                {
                    TenantId = tenantId, TypeId = typeId, Code = code, ValueEn = en, ValueAr = ar,
                    SortOrder = sort, IsSystemDefined = true, IsActive = true, IsDefault = sort == 1,
                });
                valuesAdded++;
            }
        }
        return (typesAdded, valuesAdded);
    }

    // ── 3. HR request categories (Program L10 — moved out of the demo path) ──

    private static readonly (string Code, string Name, int Sla)[] HrCategories =
    {
        ("SAL-CERT", "Salary Certificate", 24),
        ("NOC", "NOC Letter", 48),
        ("PAY-INQ", "Payroll Inquiry", 48),
        ("DOC-REQ", "Document Request", 72),
        ("GEN", "General HR Query", 72),
    };

    private static async Task<int> InstallHrRequestCategoriesAsync(ZayraDbContext db, Guid tenantId, CancellationToken ct)
    {
        // IgnoreQueryFilters is intentional: seeder read scoped by explicit tenantId; insert-if-absent, never touches another tenant.
        var existing = (await db.HRRequestCategories.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.TenantId == tenantId).Select(c => c.Code).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var (code, name, sla) in HrCategories)
        {
            if (!existing.Add(code)) continue;
            db.HRRequestCategories.Add(new HRRequestCategory { TenantId = tenantId, Code = code, Name = name, DefaultSlaHours = sla, IsActive = true });
            added++;
        }
        return added;
    }

    // ── 4. Default attendance policy (Program row 58 — always-on, not demo-gated) ──

    private static async Task<int> InstallDefaultAttendancePolicyAsync(ZayraDbContext db, Guid tenantId, CancellationToken ct)
    {
        // IgnoreQueryFilters is intentional: seeder read scoped by explicit tenantId; insert-if-absent, never touches another tenant.
        var exists = await db.AttendancePolicies.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(p => p.TenantId == tenantId && p.Code == "DEFAULT", ct);
        if (exists) return 0;
        db.AttendancePolicies.Add(new AttendancePolicy
        {
            TenantId = tenantId, Code = "DEFAULT", Name = "Default attendance policy",
            GraceMinutes = 10, LateThresholdMinutes = 15, EarlyExitThresholdMinutes = 15,
            HalfDayThresholdMinutes = 240, AbsentThresholdMinutes = 120,
            StandardWorkMinutes = 480, BreakMinutes = 60,
            RequiresOvertimeApproval = true, AllowAbsenceToLeaveConversion = true,
        });
        return 1;
    }

    // ── 5. Default leave types + a statutory-default leave POLICY per type, per country ──
    //
    // WHY A POLICY ROW IS NOT OPTIONAL. LeaveService.CalculateWorkingDaysAsync reads the day-count
    // basis — weekends in or out, public holidays in or out — from the resolved LeavePolicy, and
    // there is exactly one correct answer per LEAVE TYPE, not per tenant:
    //
    //   • Annual leave is counted in WORKING days. A rest day inside an annual-leave span is not a
    //     day of leave, so counting it would charge the employee for a day the employer never owed.
    //   • KSA sick leave is counted in CALENDAR days. Art. 117 grants the entitlement "during a
    //     single year, whether such leaves are continuous or intermittent" — the Friday in the
    //     middle of a sick spell is a sick day, and the 120-day entitlement is 120 calendar days.
    //
    // A tenant with NO policy row therefore cannot be served correctly by any fallback: whichever
    // basis the fallback picks is wrong for the other type. Counting every calendar day
    // over-deducts annual leave; counting only working days bands a 120-day statutory sick
    // entitlement as roughly 86. The policy row is the only thing that distinguishes them, so
    // every tenant is born with one per seeded type.
    //
    // Everything seeded here is an ORDINARY, EDITABLE policy row — the same shape
    // POST /api/leave/policies writes — reachable in Setup, editable, and archivable. Nothing in
    // the product branches on "was this seeded"; the "Default …" name prefix is a label for the
    // administrator, not a behaviour switch. The installer is strictly insert-if-absent on
    // (leave type, tenant-wide scope, country), so an edited row is never rewritten and an
    // ARCHIVED row is never resurrected (DELETE on a leave policy sets Status = "Archived" and
    // leaves the row in place, which is what makes the deletion stick across deploys).

    private static readonly (string Code, string En, string Ar, string Category, bool Paid)[] LeaveTypeSeeds =
    {
        ("ANNUAL", "Annual Leave", "إجازة سنوية", "Annual", true),
        ("SICK", "Sick Leave", "إجازة مرضية", "Sick", true),
    };

    /// <summary>
    /// The day-count basis and accrual shape for each seeded leave type. The ENTITLEMENT is not
    /// here — it is read per country from <see cref="Packs"/>, which is already the single source
    /// the tenant's own <c>CountryPayrollRule.annual_leave_days</c> / <c>sick_leave_days</c> rows
    /// are seeded from, so a legal figure is never written down twice.
    /// </summary>
    private static readonly (string TypeCode, string Label, bool WeekendsIncluded, bool PublicHolidaysIncluded, string AccrualMethod)[] LeavePolicyBases =
    {
        // ANNUAL — working days: weekends and public holidays are NOT leave days.
        // KSA Art. 109(1): "a prepaid annual leave of not less than 21 days, to be increased to a
        // period of not less than 30 days if the worker spends five consecutive years in the
        // service of the employer." The 21 → 30 tier is applied on top of this row by
        // KsaAnnualLeaveScale (LeaveService.ResolveKsaAnnualEntitlementAsync) as a statutory FLOOR,
        // so the row carries the base figure and the tier is never a second copy of the rule.
        // Monthly accrual, matching the tier engine, which only visits AccrualMethod == "Monthly".
        ("ANNUAL", "Annual Leave", false, false, "Monthly"),

        // SICK — CALENDAR days. KSA Art. 117: "a worker whose illness has been proven shall be
        // entitled to a sick leave … for the first thirty days with full pay, for the following
        // sixty days with three quarters of the wage, and for the following thirty days without
        // pay … during a single year, whether such leaves are continuous or intermittent" — 120
        // days, counted on the calendar. UAE Federal Decree-Law 33/2021 Art. 31 is the same shape:
        // 90 days after probation, 15 full / 30 half / 45 unpaid, expressed in calendar days.
        // WeekendsIncluded AND PublicHolidaysIncluded are both true for exactly that reason.
        //
        // AccrualMethod "Yearly" (front-loaded) deliberately keeps sick leave OUT of the monthly
        // accrual sweep: a sick entitlement is a per-illness-year cap, not something an employee
        // earns at 120/12 days a month, and the Art. 117 pay banding is applied at approval by
        // LeaveService.ApplyKsaSickLeaveScaleAsync from the same KsaLeaveHoursDefaults constants.
        ("SICK", "Sick Leave", true, true, "Yearly"),
    };

    /// <summary>
    /// The countries a default policy set is planted for. The same six GCC states
    /// <see cref="ComplianceSeeds"/> already seeds a readiness profile for — the jurisdictions this
    /// product actually operates in — rather than all nineteen <see cref="Packs"/> entries, which
    /// would bury a single-country tenant's Setup screen in 38 rows it will never use. A tenant
    /// that opens an entity elsewhere adds its own policy, or edits the country-neutral one.
    /// </summary>
    internal static readonly string[] DefaultLeavePolicyCountries = { "SA", "AE", "QA", "KW", "OM", "BH" };

    /// <summary>
    /// The country whose figures the COUNTRY-NEUTRAL default carries. That row exists because
    /// <c>LeaveService.IsPolicyEligible</c> rejects a country-scoped policy for an employee whose
    /// company has no country code yet — a brand-new tenant and, historically, the pilot tenant.
    /// Without it such a tenant would have no resolvable policy and would be back in the
    /// wrong-basis fallback this whole section exists to remove. KSA is the product's home
    /// jurisdiction and the one certified pack; the moment the company carries a country code the
    /// country row outranks this one.
    /// </summary>
    internal const string NeutralLeavePolicyCountry = "SA";

    /// <summary>
    /// The statutory entitlement for a seeded default, read from <see cref="Packs"/> — the same
    /// table the tenant's <c>CountryPayrollRule</c> leave-day rules are seeded from. KSA's figures
    /// there (21 annual, 120 sick) are asserted against <c>KsaLeaveHoursDefaults</c> by
    /// <c>DefaultLeavePolicyTests</c>, so the two can never drift into two different legal answers.
    /// </summary>
    internal static decimal DefaultLeaveEntitlementDays(string typeCode, string countryCode)
    {
        var cc = string.IsNullOrWhiteSpace(countryCode) ? NeutralLeavePolicyCountry : countryCode;
        var pack = Packs.FirstOrDefault(p => string.Equals(p.Country, cc, StringComparison.OrdinalIgnoreCase));
        if (pack.Country is null)
            pack = Packs.First(p => p.Country == NeutralLeavePolicyCountry);
        return string.Equals(typeCode, "SICK", StringComparison.OrdinalIgnoreCase)
            ? pack.Sick
            : pack.Annual;
    }

    /// <summary>
    /// internal, not private: <c>TenantDefaultsBackfill</c> installs the same leave types and
    /// default policies on tenants that already existed. Sharing this installer rather than copying
    /// it keeps <see cref="LeavePolicyBases"/> and <see cref="Packs"/> the single source of both
    /// the day-count basis and the entitlement.
    ///
    /// <para><paramref name="homeCountryCode"/> is the tenant's stated home jurisdiction. It is
    /// optional HERE and only here, because the backfill runs over tenants that pre-date the field and
    /// must not invent one for them; the new-tenant path always supplies it. When supplied and outside
    /// <see cref="DefaultLeavePolicyCountries"/>, the tenant's own country gets a default policy set
    /// too — otherwise a customer in, say, Egypt would be provisioned six GCC policy sets and none
    /// for the country it actually operates in.</para>
    /// </summary>
    internal static async Task<(int types, int policies)> InstallDefaultLeaveAsync(
        ZayraDbContext db, Guid tenantId, CancellationToken ct, string? homeCountryCode = null)
    {
        // IgnoreQueryFilters is intentional: seeder read scoped by explicit tenantId; insert-if-absent, never touches another tenant.
        var existingTypes = await db.LeaveTypes.IgnoreQueryFilters().AsNoTracking()
            .Where(t => t.TenantId == tenantId)
            .Select(t => new { t.Id, t.Code })
            .ToListAsync(ct);
        var typeByCode = existingTypes.ToDictionary(t => t.Code, t => t.Id, StringComparer.OrdinalIgnoreCase);

        int typesAdded = 0, sort = 0;
        foreach (var (code, en, ar, category, paid) in LeaveTypeSeeds)
        {
            sort++;
            if (typeByCode.ContainsKey(code)) continue;
            var lt = new LeaveType
            {
                TenantId = tenantId, Code = code, NameEn = en, NameAr = ar, Category = category,
                IsPaid = paid, RequiresReason = true, IsActive = true, SortOrder = sort,
            };
            db.LeaveTypes.Add(lt);
            typeByCode[code] = lt.Id;
            typesAdded++;
        }

        var policiesAdded = await InstallDefaultLeavePoliciesAsync(db, tenantId, typeByCode, ct, homeCountryCode);
        return (typesAdded, policiesAdded);
    }

    /// <summary>
    /// Plants one tenant-wide default policy per seeded leave type, per country, for every
    /// (type, country) pair the tenant has no tenant-wide policy for yet.
    ///
    /// <para><b>Natural key: (LeaveTypeId, CompanyId == null, CountryCode).</b> Coarser than the
    /// old "does this type have ANY tenant-wide policy" check, which would have let a tenant's
    /// pre-existing country-neutral annual policy suppress the country set for ever; finer than
    /// the row id, so re-running adds nothing. Deliberately NOT filtered on Status: a policy an
    /// administrator archived (DELETE sets Status = "Archived") must stay archived through every
    /// later deploy, exactly as a removed letter template does. Nothing here updates an existing
    /// row, so an edited entitlement, basis, notice period or approval pin survives untouched.</para>
    /// </summary>
    private static async Task<int> InstallDefaultLeavePoliciesAsync(
        ZayraDbContext db, Guid tenantId, IReadOnlyDictionary<string, Guid> typeByCode, CancellationToken ct,
        string? homeCountryCode = null)
    {
        const string why =
            "Seeding/backfill runs with no HTTP principal, so the company read filter resolves to an "
            + "EMPTY company scope and would hide every leave policy the tenant already has. The gap "
            + "check would then re-insert rows the tenant already owns — including ones it has edited "
            + "or archived. The tenant filter is re-applied by the helper; no other tenant is observable.";

        var existing = (await ScopedBypass.TenantWide(db.LeavePolicies, tenantId, why)
                .AsNoTracking()
                .Where(p => p.CompanyId == null)
                .Select(p => new { p.LeaveTypeId, p.CountryCode })
                .ToListAsync(ct))
            .Select(x => (x.LeaveTypeId, Country: (x.CountryCode ?? string.Empty).Trim().ToUpperInvariant()))
            .ToHashSet();

        // The country-neutral row first (empty CountryCode = applies to any employee), then one per
        // GCC state. Order matters only for readability in Setup, which sorts by name.
        // The tenant's own home jurisdiction is appended when it is not already one of them, so the
        // country the customer actually operates in is never the one country without a policy set.
        var home = HomeJurisdiction.Normalize(homeCountryCode);
        var countries = new[] { string.Empty }
            .Concat(DefaultLeavePolicyCountries)
            .Concat(home is not null && !DefaultLeavePolicyCountries.Contains(home, StringComparer.OrdinalIgnoreCase)
                ? new[] { home }
                : Array.Empty<string>())
            .ToArray();

        var added = 0;
        foreach (var basis in LeavePolicyBases)
        {
            if (!typeByCode.TryGetValue(basis.TypeCode, out var leaveTypeId)) continue;
            foreach (var country in countries)
            {
                if (!existing.Add((leaveTypeId, country))) continue;
                db.LeavePolicies.Add(new LeavePolicy
                {
                    TenantId = tenantId,
                    Name = country.Length == 0 ? $"Default {basis.Label}" : $"Default {basis.Label} — {country}",
                    LeaveTypeId = leaveTypeId,
                    CountryCode = country,
                    CompanyId = null,
                    AnnualEntitlementDays = DefaultLeaveEntitlementDays(basis.TypeCode, country),
                    AccrualMethod = basis.AccrualMethod,
                    WeekendsIncluded = basis.WeekendsIncluded,
                    PublicHolidaysIncluded = basis.PublicHolidaysIncluded,
                    MinimumDaysPerRequest = 1,
                    // A seeded default must never be the reason a statutory entitlement is refused.
                    // AppliesOnProbation defaults to false, and LeaveService REFUSES a request when
                    // the resolved policy does not apply during probation — so shipping these rows
                    // with the default would have turned "your tenant now has a sick-leave policy"
                    // into "a probationer may not take sick leave", which neither Art. 117 nor UAE
                    // Art. 31 permits. An employer that wants a probation restriction unticks it.
                    AppliesOnProbation = true,
                    PayrollImpact = "Full",
                    Status = "Active",
                });
                added++;
            }
        }
        return added;
    }

    // ── 6. Default approval workflows per core entity (Program A4 — seeded defaults) ──
    // F1: installed as ApprovalWorkflow, the single approval-configuration model the router reads.
    // (Previously ApprovalPolicy rows — which only leave read, and which tenants never saw in the
    // Approvals UI.) Same content as before: one HR-approver step, editable by the tenant.

    // Every entry must name an entity in ApprovalEntities.Producers — ApprovalProducerRegistryTests
    // asserts it. OVERTIME-DEFAULT and PAYROLL-DEFAULT used to sit in this list and routed nothing:
    // overtime is decided on its own aggregate through its own two-stage chain, and no code path has
    // ever created an ApprovalRequest for a payroll run. A tenant could open either workflow, add a
    // second approver, save it and be shown it back, and the next overtime approval or payroll run
    // would ignore it in silence. A seeded default for an entity with no producer is not a helpful
    // starting point; it is a control the client believes they have.
    private static readonly (string EntityName, string Code, string Name)[] ApprovalDefaults =
    {
        (nameof(LeaveRequest), "LEAVE-DEFAULT", "Default Leave Approval"),
        // Without this row the first timesheet a tenant submits 422s with
        // approval_route_not_configured — the module would look shipped and be unusable.
        (TimesheetConstants.ApprovalEntityName, "TIMESHEET-DEFAULT", "Default Timesheet Approval"),
        // ManpowerRequisition was the one entity with a producer and NO seeded workflow. The router
        // returned null, Submit created no shared row, and a requisition's approval then existed
        // nowhere but a status string: no queue entry, no decision ledger, no maker-checker. A
        // headcount commitment approved by nobody on the record is a control failure, not a gap in
        // configuration, so the default belongs here beside the other four.
        (RequisitionApprovalSync.ApprovalEntityName, "REQUISITION-DEFAULT", "Default Manpower Requisition Approval"),
    };

    // internal, not private: TenantDefaultsBackfill installs the same defaults on tenants that
    // already existed when the timesheet module shipped. Sharing this installer rather than copying
    // it keeps ApprovalDefaults the single list — ConfigurationConsumerTests asserts every entry in
    // it names an entity with a producer, and a second copy would escape that guard.
    internal static async Task<int> InstallDefaultApprovalWorkflowsAsync(ZayraDbContext db, Guid tenantId, CancellationToken ct)
    {
        // IgnoreQueryFilters is intentional: seeder read scoped by explicit tenantId; insert-if-absent, never touches another tenant.
        var existing = await db.ApprovalWorkflows.IgnoreQueryFilters().AsNoTracking()
            .Where(w => w.TenantId == tenantId)
            .Select(w => new { w.Code, w.EntityName, w.IsActive, w.DepartmentId, w.GradeId })
            .ToListAsync(ct);
        // Natural key: an active tenant-wide workflow for the entity already exists (the tenant's own
        // or a previous install), or the code is taken. Either way this install is a no-op.
        var coveredEntities = existing.Where(w => w.IsActive && w.DepartmentId == null && w.GradeId == null)
            .Select(w => w.EntityName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var codes = existing.Select(w => w.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var added = 0;
        foreach (var (entityName, code, name) in ApprovalDefaults)
        {
            if (coveredEntities.Contains(entityName) || codes.Contains(code)) continue;
            var workflow = new ApprovalWorkflow
            {
                TenantId = tenantId, Code = code, Name = name, EntityName = entityName,
                IsDefault = true, IsActive = true,
            };
            // Single HR-approver step by default (the HR Manager role queue — configurable).
            workflow.Steps.Add(new ApprovalWorkflowStep
            {
                TenantId = tenantId, WorkflowId = workflow.Id, StepOrder = 1,
                StepName = "HR Approval", ApproverType = "HR", ApproverRole = "HR Manager", IsFinalStep = true,
            });
            db.ApprovalWorkflows.Add(workflow);
            added++;
        }
        return added;
    }

    // ── 7. Bilingual notification-template defaults (Program row 142 — seeded at provisioning) ──

    private static readonly (string Code, string Event, string SubjectEn, string SubjectAr, string BodyEn, string BodyAr, string Vars)[] NotificationSeeds =
    {
        ("LEAVE_APPROVED", "LeaveApproved", "Leave request approved", "تمت الموافقة على طلب الإجازة",
            "Your leave request from {StartDate} to {EndDate} has been approved.",
            "تمت الموافقة على طلب إجازتك من {StartDate} إلى {EndDate}.", "StartDate,EndDate"),
        ("LEAVE_REJECTED", "LeaveRejected", "Leave request rejected", "تم رفض طلب الإجازة",
            "Your leave request from {StartDate} to {EndDate} was not approved. Reason: {Reason}.",
            "لم تتم الموافقة على طلب إجازتك من {StartDate} إلى {EndDate}. السبب: {Reason}.", "StartDate,EndDate,Reason"),
        ("PAYSLIP_READY", "PayslipReady", "Your payslip is ready", "قسيمة راتبك جاهزة",
            "Your payslip for {Period} is now available in the portal.",
            "أصبحت قسيمة راتبك عن {Period} متاحة في البوابة.", "Period"),
        ("HR_REQUEST_UPDATE", "HrRequestUpdate", "Update on your HR request", "تحديث بخصوص طلبك",
            "Your HR request '{Subject}' has been updated to status {Status}.",
            "تم تحديث طلبك '{Subject}' إلى الحالة {Status}.", "Subject,Status"),
    };

    private static async Task<int> InstallNotificationTemplatesAsync(ZayraDbContext db, Guid tenantId, CancellationToken ct)
    {
        const string channel = "InApp";
        // IgnoreQueryFilters is intentional: seeder read scoped by explicit tenantId; insert-if-absent, never touches another tenant.
        var existing = (await db.NotificationTemplates.IgnoreQueryFilters().AsNoTracking()
            .Where(t => t.TenantId == tenantId && t.Channel == channel)
            .Select(t => t.Code).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var added = 0;
        foreach (var s in NotificationSeeds)
        {
            if (!existing.Add(s.Code)) continue;
            db.NotificationTemplates.Add(new NotificationTemplate
            {
                TenantId = tenantId, Code = s.Code, EventType = s.Event, Channel = channel,
                SubjectEn = s.SubjectEn, SubjectAr = s.SubjectAr, BodyEn = s.BodyEn, BodyAr = s.BodyAr,
                Variables = s.Vars, IsActive = true,
            });
            added++;
        }

        added += await InstallShortChannelTemplatesAsync(db, tenantId, ct);
        return added;
    }

    // ── 7b. POD-D5: SMS / WhatsApp / Push templates, seeded INACTIVE ─────────────────────────────
    //
    // A short-channel template row is the tenant's OPT-IN SWITCH: NotificationService only selects
    // SMS/WhatsApp/Push when an ACTIVE template exists for (Code, Channel). Seeding them inactive
    // means (a) no behaviour changes for any existing tenant, and (b) an admin can turn a channel on
    // from the existing notification-template screen without hand-writing a body.
    //
    // The bodies deliberately carry NO payroll figures. "Your payslip is ready" — never what it is
    // worth. Only allow-listed variables ({Period}, {Status}, …) may appear; NotificationBodyPolicy
    // drops anything else and a monetary-token guard refuses the send outright.

    private static readonly (string Code, string Event, string SubjectEn, string SubjectAr, string BodyEn, string BodyAr, string Vars)[] ShortChannelSeeds =
    {
        ("PAYSLIP_READY", "PayslipReady", "Payslip ready", "قسيمة الراتب جاهزة",
            "Your payslip for {Period} is ready. Sign in to view it.",
            "قسيمة راتبك عن {Period} جاهزة. سجّل الدخول للاطلاع عليها.", "Period"),
        ("LEAVE_APPROVED", "LeaveApproved", "Leave approved", "تمت الموافقة على الإجازة",
            "Your leave request was approved. Sign in for details.",
            "تمت الموافقة على طلب إجازتك. سجّل الدخول للتفاصيل.", ""),
        ("LEAVE_REJECTED", "LeaveRejected", "Leave update", "تحديث الإجازة",
            "Your leave request was not approved. Sign in for details.",
            "لم تتم الموافقة على طلب إجازتك. سجّل الدخول للتفاصيل.", ""),
        ("HR_REQUEST_UPDATE", "HrRequestUpdate", "HR request update", "تحديث طلب الموارد البشرية",
            "Your HR request status changed. Sign in for details.",
            "تم تغيير حالة طلبك. سجّل الدخول للتفاصيل.", ""),
    };

    private static readonly string[] ShortChannels = ["SMS", "WhatsApp", "Push"];

    private static async Task<int> InstallShortChannelTemplatesAsync(ZayraDbContext db, Guid tenantId, CancellationToken ct)
    {
        // IgnoreQueryFilters is intentional: seeder read scoped by explicit tenantId; insert-if-absent.
        var existing = (await db.NotificationTemplates.IgnoreQueryFilters().AsNoTracking()
            .Where(t => t.TenantId == tenantId && ShortChannels.Contains(t.Channel))
            .Select(t => new { t.Code, t.Channel }).ToListAsync(ct))
            .Select(x => $"{x.Code}|{x.Channel}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var added = 0;
        foreach (var channel in ShortChannels)
        foreach (var s in ShortChannelSeeds)
        {
            if (!existing.Add($"{s.Code}|{channel}")) continue;
            db.NotificationTemplates.Add(new NotificationTemplate
            {
                TenantId = tenantId, Code = s.Code, EventType = s.Event, Channel = channel,
                SubjectEn = s.SubjectEn, SubjectAr = s.SubjectAr, BodyEn = s.BodyEn, BodyAr = s.BodyAr,
                Variables = s.Vars,
                IsActive = false,   // OFF until the tenant configures a provider and turns it on
            });
            added++;
        }
        return added;
    }
}
