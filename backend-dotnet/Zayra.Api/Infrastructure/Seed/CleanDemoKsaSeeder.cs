using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Seed;

/// <summary>
/// Replaces the three garbage demo tenants (intelliflow, evostel, alnakheel) with one
/// credible KSA tenant: Ras Al-Manar Consulting Company LLC.
/// 15 employees (10 Saudi, 5 expat), locked payroll run computed by GosiCalculationService,
/// GL entries balanced. Admin login: admin@rasalmanar.com / RasAlManar@2026!
/// </summary>
public static class CleanDemoKsaSeeder
{
    public const string Slug          = "rasalmanar";
    public const string AdminEmail    = "admin@rasalmanar.com";
    public const string AdminPassword = "RasAlManar@2026!";

    // Slugs NOT renamed — idempotency guards in DemoDataSeeder + KsaDemoTenantSeeder
    // find the existing row (even with IsActive=false) and skip re-creation.
    private static readonly string[] GarbageSlugs = ["intelliflow", "evostel", "alnakheel"];

    // ── Cleanup ────────────────────────────────────────────────────────────────

    public static async Task DeactivateGarbageDemoTenantsAsync(
        ZayraDbContext db, ILogger logger, CancellationToken ct = default)
    {
        var slugSet = new HashSet<string>(GarbageSlugs, StringComparer.OrdinalIgnoreCase);
        var active  = await db.Tenants
            .Where(t => slugSet.Contains(t.Slug) && t.IsActive)
            .ToListAsync(ct);

        if (active.Count == 0)
        {
            logger.LogInformation("CleanDemoKsaSeeder: garbage demo tenants already deactivated or absent.");
            return;
        }

        foreach (var tenant in active)
        {
            logger.LogInformation("CleanDemoKsaSeeder: deactivating '{Slug}' ({Id}).", tenant.Slug, tenant.Id);
            tenant.IsActive = false;

            await db.Users
                .Where(u => u.TenantId == tenant.Id && !u.IsDeleted)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(u => u.IsActive,     false)
                    .SetProperty(u => u.Status,       "Deactivated")
                    .SetProperty(u => u.UpdatedAtUtc, DateTime.UtcNow), ct);

            var userIds = await db.Users
                .Where(u => u.TenantId == tenant.Id)
                .Select(u => u.Id)
                .ToListAsync(ct);
            if (userIds.Count > 0)
                await db.RefreshTokens
                    .Where(r => r.RevokedAtUtc == null && userIds.Contains(r.UserId))
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.RevokedAtUtc, DateTime.UtcNow), ct);

            var sub = await db.TenantSubscriptions.FirstOrDefaultAsync(s => s.TenantId == tenant.Id, ct);
            if (sub is not null) sub.Status = "Cancelled";

            await db.SaveChangesAsync(ct);
            logger.LogInformation("CleanDemoKsaSeeder: '{Slug}' deactivated.", tenant.Slug);
        }
    }

    // ── Seed ──────────────────────────────────────────────────────────────────

    public static async Task SeedAsync(
        ZayraDbContext  db,
        IPasswordHasher hasher,
        IAuthSeeder     authSeeder,
        ILogger         logger,
        CancellationToken ct = default)
    {
        if (await db.Tenants.AnyAsync(t => t.Slug == Slug, ct))
        {
            logger.LogInformation("CleanDemoKsaSeeder: rasalmanar already exists — skipping.");
            return;
        }

        logger.LogInformation("CleanDemoKsaSeeder: seeding Ras Al-Manar Consulting Company LLC...");

        var now       = DateTime.UtcNow;
        var prevMonth = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-1);
        var year      = prevMonth.Year;
        var month     = prevMonth.Month;
        var period    = $"{year}-{month:D2}";
        var periodDate = new DateOnly(year, month, 1);

        // Load platform-default GOSI rules for the payroll calc.
        // GosiRuleSeeder runs before this seeder; empty list means zero GOSI (acceptable in tests).
        var gosiRules = await db.GosiContributionRules
            .AsNoTracking()
            .Where(r => r.TenantId == Guid.Empty)
            .ToListAsync(ct);

        // ── 1. Tenant ─────────────────────────────────────────────────────────
        var tenant = new Tenant
        {
            Name     = "Ras Al-Manar Consulting Company LLC",
            Slug     = Slug,
            IsActive = true,
        };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync(ct);
        var tenantId = tenant.Id;

        // ── 2. RBAC ───────────────────────────────────────────────────────────
        await authSeeder.EnsureTenantRolesAsync(tenantId, ct);
        var roleMap = await db.Roles.AsNoTracking()
            .Where(r => r.TenantId == tenantId)
            .ToDictionaryAsync(r => r.Name, StringComparer.OrdinalIgnoreCase, ct);

        // ── 3. Subscription ───────────────────────────────────────────────────
        db.TenantSubscriptions.Add(new TenantSubscription
        {
            TenantId      = tenantId,
            Plan          = "Growth",
            Status        = "Active",
            MaxEmployees  = 50,
            MaxUsers      = 20,
            BillingEmail  = "finance@rasalmanar.com.sa",
            BillingCycle  = "Annually",
            MonthlyAmount = 4_500m,
            CurrencyCode  = "SAR",
        });

        // ── 4. Feature flags ──────────────────────────────────────────────────
        foreach (var key in new[]
        {
            FeatureKeys.Payroll,           FeatureKeys.Recruitment,    FeatureKeys.Performance,
            FeatureKeys.Compliance,        FeatureKeys.Finance,        FeatureKeys.Shifts,
            FeatureKeys.Overtime,          FeatureKeys.AiAssistant,    FeatureKeys.ResumeScreening,
            FeatureKeys.PayrollAiValidation, FeatureKeys.RiskScores,   FeatureKeys.WpsExport,
            FeatureKeys.EosbCalc,          FeatureKeys.QiwaIntegration, FeatureKeys.HijriCalendar,
            FeatureKeys.MobileApp,
        })
            db.TenantFeatureFlags.Add(new TenantFeatureFlag
                { TenantId = tenantId, FeatureKey = key, IsEnabled = true, UpdatedAtUtc = now });

        await db.SaveChangesAsync(ct);

        // ── 5. Portal users ───────────────────────────────────────────────────
        var userSpecs = new (string Role, string Name, string Email)[]
        {
            ("Admin",             "Ras Al-Manar Administrator", AdminEmail),
            ("HR Director",       "Noura Al-Ghamdi",            "hr@rasalmanar.com.sa"),
            ("HR Manager",        "Mohammed Al-Shammari",       "hrm@rasalmanar.com.sa"),
            ("Finance Approver",  "Khalid Al-Dosari",           "finance@rasalmanar.com.sa"),
            ("Manager",           "Mohammed Al-Zahrani",        "ops.mgr@rasalmanar.com.sa"),
            ("Supervisor",        "Hessa Al-Mutairi",           "supervisor@rasalmanar.com.sa"),
            ("Auditor",           "Tariq Al-Zahrani",           "audit@rasalmanar.com.sa"),
        };

        foreach (var (roleName, fullName, email) in userSpecs)
        {
            if (!roleMap.TryGetValue(roleName, out var role))
            {
                logger.LogWarning("CleanDemoKsaSeeder: role '{Role}' not found — skipping user {Email}.", roleName, email);
                continue;
            }
            var u = new User
            {
                TenantId         = tenantId,
                Email            = email.Trim().ToLowerInvariant(),
                NormalizedEmail  = AuthService.Normalize(email),
                FullName         = fullName,
                PasswordHash     = hasher.Hash(AdminPassword),
                AccessMode       = "FullPortal",
                Status           = "Active",
                IsActive         = true,
                IsEmailConfirmed = true,
            };
            u.UserRoles.Add(new UserRole { User = u, RoleId = role.Id });
            db.Users.Add(u);
        }
        await db.SaveChangesAsync(ct);

        // ── 6. Company + Branch ───────────────────────────────────────────────
        var company = new Company
        {
            TenantId           = tenantId,
            LegalNameEn        = "Ras Al-Manar Consulting Company LLC",
            LegalNameAr        = "شركة رأس المنار للاستشارات",
            TradeName          = "Ras Al-Manar",
            CountryCode        = "SAU",
            Jurisdiction       = "KSA-mainland",
            RegistrationNumber = "1010567890",
            DefaultCurrency    = "SAR",
            IsActive           = true,
        };
        db.Companies.Add(company);
        await db.SaveChangesAsync(ct);

        var branch = new Branch
        {
            TenantId     = tenantId,
            CompanyId    = company.Id,
            Code         = "RUH-HQ",
            NameEn       = "Riyadh Head Office",
            NameAr       = "المقر الرئيسي — الرياض",
            CountryCode  = "SAU",
            City         = "Riyadh",
            TimeZoneId   = "Arab Standard Time",
            IsHeadOffice = true,
            IsActive     = true,
        };
        db.Branches.Add(branch);
        await db.SaveChangesAsync(ct);

        // ── 7. Departments ────────────────────────────────────────────────────
        var deptIT  = new Department { TenantId = tenantId, BranchId = branch.Id, Code = "IT",  NameEn = "Information Technology", NameAr = "تقنية المعلومات",  SortOrder = 0 };
        var deptFin = new Department { TenantId = tenantId, BranchId = branch.Id, Code = "FIN", NameEn = "Finance",                NameAr = "المالية",            SortOrder = 1 };
        var deptHR  = new Department { TenantId = tenantId, BranchId = branch.Id, Code = "HR",  NameEn = "Human Resources",        NameAr = "الموارد البشرية",    SortOrder = 2 };
        var deptOps = new Department { TenantId = tenantId, BranchId = branch.Id, Code = "OPS", NameEn = "Operations",             NameAr = "العمليات",           SortOrder = 3 };
        db.Departments.AddRange(deptIT, deptFin, deptHR, deptOps);
        await db.SaveChangesAsync(ct);

        // ── 8. Grade + Designations ───────────────────────────────────────────
        var grade = new Grade { TenantId = tenantId, Code = "KSA-STD", Name = "KSA Standard", Level = 1 };
        db.Grades.Add(grade);
        await db.SaveChangesAsync(ct);

        Designation D(string code, string en, string ar, Department dept) => new()
        {
            TenantId = tenantId, Code = code, TitleEn = en, TitleAr = ar,
            GradeId = grade.Id, DepartmentId = dept.Id, LevelRank = 10,
        };

        var desigGM       = D("GM",        "General Manager",          "المدير العام",            deptIT);
        var desigHRDir    = D("HR-DIR",    "HR Director",              "مدير الموارد البشرية",    deptHR);
        var desigFinMgr   = D("FIN-MGR",   "Finance Manager",          "مدير المالية",            deptFin);
        var desigSrConsl  = D("SR-CNSL",   "Senior Consultant",        "استشاري أول",              deptIT);
        var desigOpsMgr   = D("OPS-MGR",   "Operations Manager",       "مدير العمليات",           deptOps);
        var desigHRSpec   = D("HR-SPEC",   "HR Specialist",            "أخصائي موارد بشرية",      deptHR);
        var desigSrAcct   = D("SR-ACCT",   "Senior Accountant",        "محاسب أول",                deptFin);
        var desigITAnal   = D("IT-ANAL",   "IT Analyst",               "محلل تقنية المعلومات",    deptIT);
        var desigOpsSpec  = D("OPS-SPEC",  "Operations Specialist",    "أخصائي عمليات",           deptOps);
        var desigAcct     = D("ACCT",      "Accountant",               "محاسب",                    deptFin);
        var desigSrSWE    = D("SR-SWE",    "Lead Software Engineer",   "مهندس برمجيات أول",        deptIT);
        var desigBA       = D("BA",        "Business Analyst",         "محلل أعمال",               deptIT);
        var desigPrjCoord = D("PRJ-COORD", "Project Coordinator",      "منسق مشاريع",              deptOps);
        var desigSWE      = D("SWE",       "Software Developer",       "مطور برمجيات",             deptIT);
        var desigAdminAst = D("ADMIN-AST", "Administrative Assistant", "مساعد إداري",              deptHR);

        db.Designations.AddRange(
            desigGM, desigHRDir, desigFinMgr, desigSrConsl, desigOpsMgr,
            desigHRSpec, desigSrAcct, desigITAnal, desigOpsSpec, desigAcct,
            desigSrSWE, desigBA, desigPrjCoord, desigSWE, desigAdminAst);
        await db.SaveChangesAsync(ct);

        // ── 9. Employees ──────────────────────────────────────────────────────
        const string GosiEmployerId  = "3000654321";
        const string EstablishmentId = "7000123456";

        static Employee Emp(
            Guid tid, Company co, Branch br, Grade g,
            string code, string en, string ar,
            Department dept, Designation desig,
            decimal basic, DateTime joining,
            string nationality, string saudiFlag, string idType, string idNum) => new()
        {
            TenantId        = tid,
            CompanyId       = co.Id,
            BranchId        = br.Id,
            GradeId         = g.Id,
            EmployeeCode    = code,
            FullName        = en,
            EnglishName     = en,
            ArabicName      = ar,
            DepartmentId    = dept.Id,
            Department      = dept.NameEn,
            DesignationId   = desig.Id,
            Designation     = desig.TitleEn,
            JobTitle        = desig.TitleEn,
            Salary          = basic,
            // Force UTC — the literal new DateTime(...) joining dates are Kind=Unspecified, which
            // Npgsql rejects for the timestamptz column (fails only on a fresh DB, where this seeds).
            JoiningDate     = DateTime.SpecifyKind(joining, DateTimeKind.Utc),
            Status          = "Active",
            ContractType    = "FixedTerm",
            EmploymentType  = "FullTime",
            Nationality     = nationality,
            CountryCode     = "SAU",
            SaudiOrNonSaudi = saudiFlag,
            IdType          = idType,
            IdNumber        = idNum,
            IqamaNumber     = idType == "Iqama" ? idNum : string.Empty,
            GosiReference   = GosiEmployerId,
            EstablishmentId = EstablishmentId,
            IsDeleted       = false,
        };

        // 10 Saudi nationals
        var empAbdulrahman = Emp(tenantId, company, branch, grade, "RAM-001", "Abdulrahman Al-Qahtani", "عبدالرحمن القحطاني", deptIT,  desigGM,       22_000m, new DateTime(2021, 3,  1), "Saudi",    "Saudi",    "NationalId", "1020987654");
        var empNoura       = Emp(tenantId, company, branch, grade, "RAM-002", "Noura Al-Ghamdi",         "نورة الغامدي",       deptHR,  desigHRDir,    18_000m, new DateTime(2021, 4, 15), "Saudi",    "Saudi",    "NationalId", "2030876543");
        var empKhalid      = Emp(tenantId, company, branch, grade, "RAM-003", "Khalid Al-Dosari",        "خالد الدوسري",       deptFin, desigFinMgr,   16_000m, new DateTime(2021, 5, 10), "Saudi",    "Saudi",    "NationalId", "1040765432");
        var empFatimah     = Emp(tenantId, company, branch, grade, "RAM-004", "Fatimah Al-Shammari",     "فاطمة الشمري",       deptIT,  desigSrConsl,  14_000m, new DateTime(2022, 1, 16), "Saudi",    "Saudi",    "NationalId", "2050654321");
        var empMohammed    = Emp(tenantId, company, branch, grade, "RAM-005", "Mohammed Al-Zahrani",     "محمد الزهراني",      deptOps, desigOpsMgr,   15_000m, new DateTime(2021, 9,  1), "Saudi",    "Saudi",    "NationalId", "1060543210");
        var empHessa       = Emp(tenantId, company, branch, grade, "RAM-006", "Hessa Al-Mutairi",        "حصة المطيري",        deptHR,  desigHRSpec,    9_500m, new DateTime(2022, 6,  5), "Saudi",    "Saudi",    "NationalId", "2070432109");
        var empFaisal      = Emp(tenantId, company, branch, grade, "RAM-007", "Faisal Al-Harbi",         "فيصل الحربي",        deptFin, desigSrAcct,   11_000m, new DateTime(2022, 3, 20), "Saudi",    "Saudi",    "NationalId", "1080321098");
        var empReem        = Emp(tenantId, company, branch, grade, "RAM-008", "Reem Al-Anazi",           "ريم العنزي",          deptIT,  desigITAnal,   10_000m, new DateTime(2023, 1,  8), "Saudi",    "Saudi",    "NationalId", "2090210987");
        var empTurki       = Emp(tenantId, company, branch, grade, "RAM-009", "Turki Al-Otaibi",         "تركي العتيبي",       deptOps, desigOpsSpec,   9_000m, new DateTime(2023, 4, 15), "Saudi",    "Saudi",    "NationalId", "1091209876");
        var empLina        = Emp(tenantId, company, branch, grade, "RAM-010", "Lina Al-Sulami",          "لينا السلمي",        deptFin, desigAcct,      8_500m, new DateTime(2023, 7, 10), "Saudi",    "Saudi",    "NationalId", "2012098765");

        // 5 expat employees
        var empAhmed  = Emp(tenantId, company, branch, grade, "RAM-011", "Ahmed Hassan",   "أحمد حسن",      deptIT,  desigSrSWE,    13_000m, new DateTime(2022, 2,  1), "Egyptian",  "NonSaudi", "Iqama", "2430000001");
        var empPriya  = Emp(tenantId, company, branch, grade, "RAM-012", "Priya Sharma",   "بريا شارما",     deptIT,  desigBA,       10_500m, new DateTime(2022, 5, 15), "Indian",    "NonSaudi", "Iqama", "2430000002");
        var empOmar   = Emp(tenantId, company, branch, grade, "RAM-013", "Omar Abdallah",  "عمر عبدالله",    deptOps, desigPrjCoord,  9_000m, new DateTime(2023, 1, 20), "Jordanian", "NonSaudi", "Iqama", "2430000003");
        var empDavid  = Emp(tenantId, company, branch, grade, "RAM-014", "David Mensah",   "ديفيد مينساه",   deptIT,  desigSWE,       9_500m, new DateTime(2023, 3, 12), "Ghanaian",  "NonSaudi", "Iqama", "2430000004");
        var empMaria  = Emp(tenantId, company, branch, grade, "RAM-015", "Maria Santos",   "ماريا سانتوس",   deptHR,  desigAdminAst,  7_500m, new DateTime(2023, 8,  1), "Filipino",  "NonSaudi", "Iqama", "2430000005");

        db.Employees.AddRange(empAbdulrahman, empNoura, empKhalid, empFatimah, empMohammed,
                               empHessa, empFaisal, empReem, empTurki, empLina);
        await db.SaveChangesAsync(ct);

        db.Employees.AddRange(empAhmed, empPriya, empOmar, empDavid, empMaria);
        await db.SaveChangesAsync(ct);

        // Set department managers and reporting relationships
        deptIT.ManagerEmployeeId  = empAbdulrahman.Id;
        deptHR.ManagerEmployeeId  = empNoura.Id;
        deptFin.ManagerEmployeeId = empKhalid.Id;
        deptOps.ManagerEmployeeId = empMohammed.Id;

        empNoura.ManagerEmployeeId    = empAbdulrahman.Id;
        empKhalid.ManagerEmployeeId   = empAbdulrahman.Id;
        empFatimah.ManagerEmployeeId  = empAbdulrahman.Id;
        empMohammed.ManagerEmployeeId = empAbdulrahman.Id;
        empHessa.ManagerEmployeeId    = empNoura.Id;
        empFaisal.ManagerEmployeeId   = empKhalid.Id;
        empReem.ManagerEmployeeId     = empAbdulrahman.Id;
        empTurki.ManagerEmployeeId    = empMohammed.Id;
        empLina.ManagerEmployeeId     = empKhalid.Id;
        empAhmed.ManagerEmployeeId    = empAbdulrahman.Id;
        empPriya.ManagerEmployeeId    = empAbdulrahman.Id;
        empOmar.ManagerEmployeeId     = empMohammed.Id;
        empDavid.ManagerEmployeeId    = empAbdulrahman.Id;
        empMaria.ManagerEmployeeId    = empNoura.Id;

        await db.SaveChangesAsync(ct);

        // ── 10. Reporting lines ───────────────────────────────────────────────
        var reportingPairs = new (Employee Emp, Employee Mgr)[]
        {
            (empNoura,    empAbdulrahman), (empKhalid,   empAbdulrahman),
            (empFatimah,  empAbdulrahman), (empMohammed, empAbdulrahman),
            (empHessa,    empNoura),       (empFaisal,   empKhalid),
            (empReem,     empAbdulrahman), (empTurki,    empMohammed),
            (empLina,     empKhalid),      (empAhmed,    empAbdulrahman),
            (empPriya,    empAbdulrahman), (empOmar,     empMohammed),
            (empDavid,    empAbdulrahman), (empMaria,    empNoura),
        };
        foreach (var (emp, mgr) in reportingPairs)
            db.ReportingLines.Add(new ReportingLine
            {
                TenantId          = tenantId,
                EmployeeId        = emp.Id,
                ManagerEmployeeId = mgr.Id,
                RelationshipType  = "SolidLine",
                EffectiveFrom     = emp.JoiningDate,
                IsPrimary         = true,
                IsActive          = true,
            });
        await db.SaveChangesAsync(ct);

        // ── 11. Payroll profiles ──────────────────────────────────────────────
        var allEmps = new[]
        {
            empAbdulrahman, empNoura, empKhalid, empFatimah, empMohammed,
            empHessa, empFaisal, empReem, empTurki, empLina,
            empAhmed, empPriya, empOmar, empDavid, empMaria,
        };

        // SA + 22 digits (24 chars total). Al Rajhi (80) for Saudi; Riyad Bank (05) for expat.
        var ibans = new[]
        {
            "SA4480000000100000000001", "SA4580000000100000000002", "SA4680000000100000000003",
            "SA4780000000100000000004", "SA4880000000100000000005", "SA4980000000100000000006",
            "SA5080000000100000000007", "SA5180000000100000000008", "SA5280000000100000000009",
            "SA5380000000100000000010", "SA4405000000200000000001", "SA4505000000200000000002",
            "SA4605000000200000000003", "SA4705000000200000000004", "SA4805000000200000000005",
        };

        for (var i = 0; i < allEmps.Length; i++)
        {
            var emp     = allEmps[i];
            var isSaudi = emp.SaudiOrNonSaudi == "Saudi";
            db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile
            {
                TenantId                 = tenantId,
                EmployeeId               = emp.Id,
                BankName                 = isSaudi ? "Al Rajhi Bank" : "Riyad Bank",
                Iban                     = Zayra.Api.Infrastructure.Payroll.IbanValidator.WithValidCheckDigits(ibans[i]),
                SalaryCurrency           = "SAR",
                PaymentMethod            = "BankTransfer",
                WpsEligible              = true,
                EosbEligible             = true,
                MolId                    = emp.IdNumber,
                SocialInsuranceReference = $"{GosiEmployerId}-{(i + 1):D3}",
                PayrollGroup             = "Main",
                SalaryStructureReference = "KSA-STANDARD",
            });
        }
        await db.SaveChangesAsync(ct);

        // ── 12. Leave types ───────────────────────────────────────────────────
        var ltDefs = new (string Code, string En, string Ar, string Cat, bool Paid)[]
        {
            ("ANNUAL",    "Annual Leave",    "إجازة سنوية",     "Annual",    true),
            ("SICK",      "Sick Leave",      "إجازة مرضية",     "Sick",      true),
            ("CASUAL",    "Casual Leave",    "إجازة عارضة",     "Casual",    true),
            ("MATERNITY", "Maternity Leave", "إجازة أمومة",     "Maternity", true),
            ("HAJJ",      "Hajj Leave",      "إجازة الحج",      "Religious", true),
            ("UNPAID",    "Unpaid Leave",    "إجازة بدون راتب", "Unpaid",    false),
        };
        var leaveTypes = new List<LeaveType>();
        var ltSort = 0;
        foreach (var lt in ltDefs)
        {
            var lv = new LeaveType
            {
                TenantId = tenantId, Code = lt.Code, NameEn = lt.En, NameAr = lt.Ar,
                Category = lt.Cat, IsPaid = lt.Paid, IsActive = true, SortOrder = ltSort++,
            };
            db.LeaveTypes.Add(lv);
            leaveTypes.Add(lv);
        }
        await db.SaveChangesAsync(ct);

        var ltAnnual = leaveTypes.First(x => x.Code == "ANNUAL");
        var ltSick   = leaveTypes.First(x => x.Code == "SICK");
        var ltCasual = leaveTypes.First(x => x.Code == "CASUAL");

        // ── 13. Leave requests ────────────────────────────────────────────────
        var today = DateOnly.FromDateTime(now.Date);
        db.LeaveRequests.AddRange(
            new LeaveRequest { TenantId=tenantId, EmployeeId=empNoura.Id,  EmployeeName=empNoura.FullName,  DepartmentName=empNoura.Department,  LeaveTypeId=ltAnnual.Id, LeaveTypeName=ltAnnual.NameEn, StartDate=today.AddDays(30), EndDate=today.AddDays(43), DayType="Full", Reason="Annual vacation",    Status="Submitted", SubmittedAtUtc=now.AddHours(-2) },
            new LeaveRequest { TenantId=tenantId, EmployeeId=empFaisal.Id, EmployeeName=empFaisal.FullName, DepartmentName=empFaisal.Department, LeaveTypeId=ltSick.Id,   LeaveTypeName=ltSick.NameEn,   StartDate=today.AddDays(-5), EndDate=today.AddDays(-4), DayType="Full", Reason="Illness",            Status="Approved",  SubmittedAtUtc=now.AddDays(-6), DecidedAtUtc=now.AddDays(-5) },
            new LeaveRequest { TenantId=tenantId, EmployeeId=empHessa.Id,  EmployeeName=empHessa.FullName,  DepartmentName=empHessa.Department,  LeaveTypeId=ltAnnual.Id, LeaveTypeName=ltAnnual.NameEn, StartDate=today.AddDays(14), EndDate=today.AddDays(20), DayType="Full", Reason="Personal leave",     Status="Approved",  SubmittedAtUtc=now.AddDays(-3), DecidedAtUtc=now.AddDays(-2) },
            new LeaveRequest { TenantId=tenantId, EmployeeId=empAhmed.Id,  EmployeeName=empAhmed.FullName,  DepartmentName=empAhmed.Department,  LeaveTypeId=ltAnnual.Id, LeaveTypeName=ltAnnual.NameEn, StartDate=today.AddDays(20), EndDate=today.AddDays(29), DayType="Full", Reason="Home country visit", Status="Submitted", SubmittedAtUtc=now.AddDays(-1) },
            new LeaveRequest { TenantId=tenantId, EmployeeId=empPriya.Id,  EmployeeName=empPriya.FullName,  DepartmentName=empPriya.Department,  LeaveTypeId=ltSick.Id,   LeaveTypeName=ltSick.NameEn,   StartDate=today.AddDays(-1), EndDate=today.AddDays(-1), DayType="Full", Reason="Migraine",           Status="Approved",  SubmittedAtUtc=now.AddDays(-1), DecidedAtUtc=now.AddHours(-6) },
            new LeaveRequest { TenantId=tenantId, EmployeeId=empTurki.Id,  EmployeeName=empTurki.FullName,  DepartmentName=empTurki.Department,  LeaveTypeId=ltCasual.Id, LeaveTypeName=ltCasual.NameEn, StartDate=today.AddDays(2),  EndDate=today.AddDays(2),  DayType="Full", Reason="Family commitment",  Status="Submitted", SubmittedAtUtc=now.AddHours(-4) }
        );

        foreach (var (emp, idx) in allEmps.Select((e, i) => (e, i)))
        {
            foreach (var (lt, entitled) in new (LeaveType Lt, decimal Entitled)[] { (ltAnnual, 30m), (ltSick, 15m), (ltCasual, 5m) })
            {
                var used = Math.Min(entitled - 1, (idx * 3 + (lt.Code == "SICK" ? 1 : 4)) % (int)entitled);
                db.EmployeeLeaveBalances.Add(new EmployeeLeaveBalance
                {
                    TenantId    = tenantId, EmployeeId = emp.Id, EmployeeName = emp.FullName,
                    LeaveTypeId = lt.Id,    LeaveTypeName = lt.NameEn, Year = today.Year,
                    Entitled    = entitled, Accrued = Math.Round(entitled * today.Month / 12m, 1),
                    Used        = used,     Pending = 0,
                    CarriedForward = lt.Code == "ANNUAL" && idx % 4 == 0 ? 5 : 0,
                });
            }
        }
        await db.SaveChangesAsync(ct);

        // ── 14. Attendance (last 45 working days, all 15 employees) ──────────
        var rng     = new Random(tenantId.GetHashCode() & 0x7fffffff);
        var attRecs = new List<AttendanceRecord>();
        for (var d = today.AddDays(-45); d <= today; d = d.AddDays(1))
        {
            if (d.DayOfWeek is DayOfWeek.Friday or DayOfWeek.Saturday) continue;
            foreach (var emp in allEmps)
                attRecs.Add(new AttendanceRecord
                {
                    TenantId   = tenantId, EmployeeId = emp.Id, WorkDate = d,
                    TimeIn     = new TimeOnly(8,  30 + rng.Next(0, 20)),
                    TimeOut    = new TimeOnly(17, 30 + rng.Next(0, 29)),
                    Status     = "Present", Notes = string.Empty,
                });
        }
        db.AttendanceRecords.AddRange(attRecs);
        await db.SaveChangesAsync(ct);

        // ── 15. Bonus type + batch (paid in locked payroll run) ───────────────
        const decimal BonusAmount = 10_000m;
        var bonusType = new BonusType
        {
            TenantId = tenantId, Code = "PERF", NameEn = "Performance Bonus",
            NameAr = "مكافأة الأداء", CalculationMethod = "Fixed",
            Frequency = "OneTime", IsIncludedInWps = true, TaxRegion = "GCC", IsActive = true,
        };
        db.BonusTypes.Add(bonusType);
        await db.SaveChangesAsync(ct);

        var bonusBatch = new BonusBatch
        {
            TenantId = tenantId, BonusTypeId = bonusType.Id, BonusTypeName = "Performance Bonus",
            BatchNumber = $"BON-{year}-001", BatchName = $"H1 {year} Performance Bonus",
            PaymentPeriod = period, PaymentDate = DateOnly.FromDateTime(prevMonth),
            TotalAmount = BonusAmount, EmployeeCount = 1, Status = "Approved",
        };
        db.BonusBatches.Add(bonusBatch);
        await db.SaveChangesAsync(ct);

        // ── 16. Locked payroll run — real GOSI calc ───────────────────────────
        // Pass 1: compute per-employee figures via GosiCalculationService
        var perEmpData = new List<(
            Employee Emp, decimal Basic, decimal Housing, decimal Transport,
            decimal Bonus, decimal BaseGross, decimal Gross,
            decimal EmpGosiTotal, decimal EmrGosiTotal, GosiContributionResult GosiResult)>();

        foreach (var emp in allEmps)
        {
            var basic     = emp.Salary ?? 0m;
            var housing   = basic * 0.25m;
            var transport = basic * 0.10m;
            var bonus     = emp == empAbdulrahman ? BonusAmount : 0m;
            var baseGross = basic + housing + transport;
            var gross     = baseGross + bonus;
            var gosi      = GosiCalculationService.Calculate(emp.Nationality, basic, gosiRules, periodDate, tenantId);
            perEmpData.Add((emp, basic, housing, transport, bonus, baseGross, gross, gosi.EmployeeTotal, gosi.EmployerTotal, gosi));
        }

        var totalGross = perEmpData.Sum(x => x.Gross);
        var totalDed   = perEmpData.Sum(x => x.EmpGosiTotal);
        var totalNet   = totalGross - totalDed;

        var payrollRun = new PayrollRun
        {
            TenantId         = tenantId,
            CompanyId        = company.Id,
            Year             = year,
            Month            = month,
            Status           = "Locked",
            EmployeeCount    = allEmps.Length,
            TotalGrossSalary = totalGross,
            TotalDeductions  = totalDed,
            TotalNetSalary   = totalNet,
            CreatedAtUtc     = prevMonth.AddDays(-8),
            ProcessedAtUtc   = prevMonth,
            LockedAtUtc      = prevMonth.AddDays(4),
        };
        db.PayrollRuns.Add(payrollRun);
        await db.SaveChangesAsync(ct);

        // Pass 2: build payslips, run-employees, GL entries
        var payslipComponents = new List<PayslipComponent>();
        var glEntries         = new List<FinanceGlEntry>();
        var runEmployees      = new List<PayrollRunEmployee>();
        var payslips          = new List<Payslip>();
        var payrollSlips      = new List<PayrollSlip>();
        var entryDate         = DateOnly.FromDateTime(prevMonth.AddDays(4));

        for (var i = 0; i < perEmpData.Count; i++)
        {
            var (emp, basic, housing, transport, bonus, baseGross, gross, empGosiTotal, emrGosiTotal, gosi) = perEmpData[i];
            var netPay = gross - empGosiTotal;

            runEmployees.Add(new PayrollRunEmployee
            {
                TenantId = tenantId, PayrollRunId = payrollRun.Id, EmployeeId = emp.Id,
                GrossEarnings = gross, TotalDeductions = empGosiTotal, NetPay = netPay,
                Status = "Processed",
            });

            var payslip = new Payslip
            {
                TenantId = tenantId, PayrollRunId = payrollRun.Id, EmployeeId = emp.Id,
                PayslipNumber = $"PS-{year}{month:D2}-{(i + 1):D3}",
                Language = "en", IsPublishedToEss = true,
                PublishedAtUtc = prevMonth.AddDays(5),
            };
            payslips.Add(payslip);

            payslipComponents.Add(new PayslipComponent { TenantId=tenantId, PayslipId=payslip.Id, ComponentType="Earning",   ComponentName="Basic Salary",        Amount=basic });
            payslipComponents.Add(new PayslipComponent { TenantId=tenantId, PayslipId=payslip.Id, ComponentType="Earning",   ComponentName="Housing Allowance",   Amount=housing });
            payslipComponents.Add(new PayslipComponent { TenantId=tenantId, PayslipId=payslip.Id, ComponentType="Earning",   ComponentName="Transport Allowance", Amount=transport });
            if (bonus > 0m)
                payslipComponents.Add(new PayslipComponent { TenantId=tenantId, PayslipId=payslip.Id, ComponentType="Earning", ComponentName="Performance Bonus", Amount=bonus });

            // GOSI employee deductions from real calc result
            foreach (var line in gosi.Lines.Where(l => l.Payer == GosiPayers.Employee))
                payslipComponents.Add(new PayslipComponent
                {
                    TenantId = tenantId, PayslipId = payslip.Id, ComponentType = "Deduction",
                    ComponentName = GosiCalculationService.ToComponentName(line.Branch, line.Payer, line.Rate),
                    Amount = line.Amount,
                });

            payrollSlips.Add(new PayrollSlip
            {
                TenantId = tenantId, RunId = payrollRun.Id, EmployeeId = emp.Id,
                EmployeeCode = emp.EmployeeCode, EmployeeName = emp.FullName,
                Department = emp.Department ?? string.Empty,
                BasicSalary = basic, HousingAllowance = housing,
                TransportAllowance = transport, OtherAllowances = bonus,
                GrossSalary = gross, Deductions = empGosiTotal, NetSalary = netPay,
                Status = "Processed",
            });

            // GL: salary posting DR 5100 / CR 2100 (base gross, bonus GL posted separately)
            glEntries.Add(new FinanceGlEntry
            {
                TenantId = tenantId, SourceModule = "Payroll", SourceEntityId = payrollRun.Id,
                SourceEntityRef = payrollRun.Id.ToString()[..8], EventType = "PayrollPosting",
                DebitAccount = "5100 - Salaries & Wages", CreditAccount = "2100 - Salaries Payable",
                Amount = baseGross, Currency = "SAR", EntryDate = entryDate, Period = period,
                Description = $"Salary posting {emp.FullName} {period}", PostedByName = "System",
            });

            // GL: employer GOSI DR 6100 / CR 2300
            if (emrGosiTotal > 0m)
                glEntries.Add(new FinanceGlEntry
                {
                    TenantId = tenantId, SourceModule = "Payroll", SourceEntityId = payrollRun.Id,
                    SourceEntityRef = payrollRun.Id.ToString()[..8], EventType = "PayrollPosting",
                    DebitAccount = "6100 - GOSI Employer Expense", CreditAccount = "2300 - GOSI Payable",
                    Amount = emrGosiTotal, Currency = "SAR", EntryDate = entryDate, Period = period,
                    Description = $"GOSI employer {emp.FullName} {period}", PostedByName = "System",
                });
        }

        db.PayrollRunEmployees.AddRange(runEmployees);
        db.Payslips.AddRange(payslips);
        await db.SaveChangesAsync(ct);

        db.PayslipComponents.AddRange(payslipComponents);
        db.PayrollSlips.AddRange(payrollSlips);
        db.FinanceGlEntries.AddRange(glEntries);
        await db.SaveChangesAsync(ct);

        // Bonus GL DR 6200 / CR 2100 + EmployeeBonus record
        db.FinanceGlEntries.Add(new FinanceGlEntry
        {
            TenantId = tenantId, SourceModule = "Payroll", SourceEntityId = payrollRun.Id,
            SourceEntityRef = payrollRun.Id.ToString()[..8], EventType = "BonusPayment",
            DebitAccount = "6200 - Bonus Expense", CreditAccount = "2100 - Salaries Payable",
            Amount = BonusAmount, Currency = "SAR", EntryDate = entryDate, Period = period,
            Description = $"Performance bonus {empAbdulrahman.FullName} {period}",
            PostedByName = "System",
        });

        db.EmployeeBonuses.Add(new EmployeeBonus
        {
            TenantId = tenantId, BonusBatchId = bonusBatch.Id,
            EmployeeId = Guid.NewGuid(),          // placeholder Guid per model design
            EmployeeIntId = empAbdulrahman.Id,
            EmployeeName = empAbdulrahman.FullName,
            Department = empAbdulrahman.Department ?? "Information Technology",
            BonusTypeId = bonusType.Id, BonusTypeName = "Performance Bonus",
            BasicSalary = empAbdulrahman.Salary ?? 22_000m,
            CalculationMethod = "Fixed", CalculationValue = BonusAmount,
            GrossBonusAmount = BonusAmount, TaxWithheld = 0m, BonusAmount = BonusAmount,
            TaxRegion = "GCC", PaymentPeriod = period,
            Status = "PaidInPayroll", PayrollRunId = payrollRun.Id,
        });
        await db.SaveChangesAsync(ct);

        var glSalary = glEntries.Where(e => e.DebitAccount.StartsWith("5100")).Sum(e => e.Amount);
        var glGosi   = glEntries.Where(e => e.DebitAccount.StartsWith("6100")).Sum(e => e.Amount);
        var glTotal  = glSalary + BonusAmount + glGosi;

        logger.LogInformation(
            "CleanDemoKsaSeeder: seeded Ras Al-Manar — {Emp} employees ({Saudi} Saudi, {Expat} expat), " +
            "payroll {Period} gross={Gross:N2} ded={Ded:N2} net={Net:N2} SAR, GL DR=CR={GL:N2} SAR.",
            allEmps.Length, 10, 5, period, totalGross, totalDed, totalNet, glTotal);
    }
}
