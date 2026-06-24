using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Seed;

/// <summary>
/// Creates ONE clean IntelliFlow Systems tenant in the KSA jurisdiction.
/// Mirrors the pattern of CleanDemoKsaSeeder: single tenant_id, one company,
/// admin user with is_group_scope=true, 12 employees (6 Saudi + 6 expat),
/// KSA GOSI statutory rules, locked historical payroll run, GL entries balanced.
///
/// Admin login: admin@intelliflow.com / IntelliFlow@2026!
/// Idempotent: no-op if slug "intelliflow" already exists as an active tenant.
/// </summary>
public static class IntelliFlowDemoSeeder
{
    public const string Slug          = "intelliflow";
    public const string AdminEmail    = "admin@intelliflow.com";
    public const string AdminPassword = "IntelliFlow@2026!";
    private const string DemoPassword = "IntelliFlow@2026!";

    public static async Task SeedAsync(
        ZayraDbContext  db,
        IPasswordHasher hasher,
        IAuthSeeder     authSeeder,
        ILogger         logger,
        CancellationToken ct = default)
    {
        // Skip if an active "intelliflow" tenant already exists (idempotency).
        if (await db.Tenants.AnyAsync(t => t.Slug == Slug && t.IsActive, ct))
        {
            logger.LogInformation("IntelliFlowDemoSeeder: active intelliflow tenant exists — skipping.");
            return;
        }

        // Fragment cleanup must have run first; verify the slug is free.
        if (await db.Tenants.AnyAsync(t => t.Slug == Slug, ct))
        {
            logger.LogWarning(
                "IntelliFlowDemoSeeder: slug '{Slug}' exists but tenant is inactive — " +
                "IntelliFlowFragmentCleanup should have renamed it. Skipping to avoid conflict.", Slug);
            return;
        }

        logger.LogInformation("IntelliFlowDemoSeeder: seeding IntelliFlow Systems (KSA)...");

        var now       = DateTime.UtcNow;
        var prevMonth = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-1);
        var year      = prevMonth.Year;
        var month     = prevMonth.Month;
        var period    = $"{year}-{month:D2}";
        var periodDate = new DateOnly(year, month, 1);

        var gosiRules = await db.GosiContributionRules
            .AsNoTracking()
            .Where(r => r.TenantId == Guid.Empty)
            .ToListAsync(ct);

        // ── 1. Tenant ─────────────────────────────────────────────────────────
        var tenant = new Tenant
        {
            Name     = "IntelliFlow Systems",
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
            Plan          = "Enterprise",
            Status        = "Active",
            MaxEmployees  = 500,
            MaxUsers      = 100,
            BillingEmail  = "billing@intelliflow.com",
            BillingCycle  = "Annually",
            MonthlyAmount = 2_500m,
            CurrencyCode  = "USD",
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

        // ── 5. Portal users (admin has IsGroupScope=true) ─────────────────────
        var userSpecs = new (string Role, string Name, string Email, bool GroupScope)[]
        {
            ("Admin",            "IntelliFlow Administrator", AdminEmail,                         true),
            ("HR Director",      "Sarah Mitchell",            "hrdirector@intelliflow.com",       false),
            ("HR Manager",       "Omar Al-Farsi",             "hrmanager@intelliflow.com",        false),
            ("Finance Approver", "Chen Wei",                  "finance@intelliflow.com",          false),
            ("Manager",          "Priya Sharma",              "manager@intelliflow.com",          false),
            ("Supervisor",       "Khalid Al-Rashid",          "supervisor@intelliflow.com",       false),
            ("Employee",         "Fatima Al-Zahra",           "employee1@intelliflow.com",        false),
            ("Employee",         "James O'Brien",             "employee2@intelliflow.com",        false),
            ("Auditor",          "Maya Johnson",              "auditor@intelliflow.com",          false),
        };

        foreach (var (roleName, fullName, email, isGroupScope) in userSpecs)
        {
            if (!roleMap.TryGetValue(roleName, out var role))
            {
                logger.LogWarning(
                    "IntelliFlowDemoSeeder: role '{Role}' not found — skipping user {Email}.", roleName, email);
                continue;
            }
            var u = new User
            {
                TenantId         = tenantId,
                Email            = email.Trim().ToLowerInvariant(),
                NormalizedEmail  = AuthService.Normalize(email),
                FullName         = fullName,
                PasswordHash     = hasher.Hash(DemoPassword),
                AccessMode       = "FullPortal",
                Status           = "Active",
                IsActive         = true,
                IsEmailConfirmed = true,
                IsGroupScope     = isGroupScope,
                MustChangePassword = false,
            };
            u.UserRoles.Add(new UserRole { User = u, RoleId = role.Id });
            db.Users.Add(u);
        }
        await db.SaveChangesAsync(ct);

        // ── 6. Company ────────────────────────────────────────────────────────
        var company = new Company
        {
            TenantId           = tenantId,
            LegalNameEn        = "IntelliFlow Systems Ltd",
            LegalNameAr        = "شركة انتليفلو لتقنية المعلومات",
            TradeName          = "IntelliFlow Systems",
            CountryCode        = "SAU",
            Jurisdiction       = "KSA-mainland",
            RegistrationNumber = "1010334455",
            DefaultCurrency    = "SAR",
            IsActive           = true,
            CreatedAtUtc       = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Companies.Add(company);
        await db.SaveChangesAsync(ct);

        // ── 7. Branch ─────────────────────────────────────────────────────────
        var branch = new Branch
        {
            TenantId     = tenantId,
            CompanyId    = company.Id,
            Code         = "RYD-HQ",
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

        // ── 8. Departments ────────────────────────────────────────────────────
        var deptEng = new Department { TenantId = tenantId, BranchId = branch.Id, Code = "ENG",  NameEn = "Engineering",       NameAr = "الهندسة",            SortOrder = 0 };
        var deptPrd = new Department { TenantId = tenantId, BranchId = branch.Id, Code = "PRD",  NameEn = "Product",           NameAr = "المنتج",             SortOrder = 1 };
        var deptHR  = new Department { TenantId = tenantId, BranchId = branch.Id, Code = "HR",   NameEn = "Human Resources",   NameAr = "الموارد البشرية",    SortOrder = 2 };
        var deptFin = new Department { TenantId = tenantId, BranchId = branch.Id, Code = "FIN",  NameEn = "Finance",           NameAr = "المالية",            SortOrder = 3 };
        db.Departments.AddRange(deptEng, deptPrd, deptHR, deptFin);
        await db.SaveChangesAsync(ct);

        // ── 9. Grade + Designations ───────────────────────────────────────────
        var grade = new Grade { TenantId = tenantId, Code = "IFL-STD", Name = "IFL Standard", Level = 1 };
        db.Grades.Add(grade);
        await db.SaveChangesAsync(ct);

        Designation D(string code, string en, string ar, Department dept) => new()
        {
            TenantId = tenantId, Code = code, TitleEn = en, TitleAr = ar,
            GradeId = grade.Id, DepartmentId = dept.Id, LevelRank = 10,
        };

        var desigCTO    = D("CTO",      "Chief Technology Officer", "الرئيس التنفيذي للتقنية",   deptEng);
        var desigSrSWE  = D("SR-SWE",   "Senior Software Engineer", "مهندس برمجيات أول",          deptEng);
        var desigSWE    = D("SWE",      "Software Engineer",        "مهندس برمجيات",              deptEng);
        var desigDevOps = D("DEVOPS",   "DevOps Engineer",          "مهندس DevOps",               deptEng);
        var desigPM     = D("PM",       "Product Manager",          "مدير منتج",                  deptPrd);
        var desigUX     = D("UX",       "UX Designer",              "مصمم تجربة مستخدم",          deptPrd);
        var desigBA     = D("BA",       "Business Analyst",         "محلل أعمال",                  deptPrd);
        var desigHRDir  = D("HR-DIR",   "HR Director",              "مدير الموارد البشرية",       deptHR);
        var desigHRSpec = D("HR-SPEC",  "HR Specialist",            "أخصائي موارد بشرية",         deptHR);
        var desigFinMgr = D("FIN-MGR",  "Finance Manager",          "مدير المالية",               deptFin);
        var desigAcct   = D("ACCT",     "Accountant",               "محاسب",                       deptFin);
        var desigQA     = D("QA",       "QA Engineer",              "مهندس جودة",                 deptEng);

        db.Designations.AddRange(desigCTO, desigSrSWE, desigSWE, desigDevOps, desigPM,
            desigUX, desigBA, desigHRDir, desigHRSpec, desigFinMgr, desigAcct, desigQA);
        await db.SaveChangesAsync(ct);

        // ── 10. Employees ─────────────────────────────────────────────────────
        const string GosiEmployerId  = "3000112233";
        const string EstablishmentId = "7000445566";

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
            JoiningDate     = joining,
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

        // 6 Saudi nationals
        var empYaser  = Emp(tenantId, company, branch, grade, "IFI-001", "Yaser Al-Ghamdi",     "ياسر الغامدي",    deptEng, desigCTO,    20_000m, new DateTime(2021, 1, 15, 0, 0, 0, DateTimeKind.Utc), "Saudi",    "Saudi",    "NationalId", "1010001001");
        var empNadia  = Emp(tenantId, company, branch, grade, "IFI-002", "Nadia Al-Zahrani",    "نادية الزهراني",   deptHR,  desigHRDir,  16_000m, new DateTime(2021, 4,  1, 0, 0, 0, DateTimeKind.Utc), "Saudi",    "Saudi",    "NationalId", "2020002002");
        var empAhmad  = Emp(tenantId, company, branch, grade, "IFI-003", "Ahmad Al-Qahtani",    "أحمد القحطاني",   deptFin, desigFinMgr, 15_000m, new DateTime(2021, 6, 10, 0, 0, 0, DateTimeKind.Utc), "Saudi",    "Saudi",    "NationalId", "1030003003");
        var empSara   = Emp(tenantId, company, branch, grade, "IFI-004", "Sara Al-Otaibi",      "سارة العتيبي",    deptPrd, desigPM,     14_000m, new DateTime(2022, 2, 20, 0, 0, 0, DateTimeKind.Utc), "Saudi",    "Saudi",    "NationalId", "2040004004");
        var empWalid  = Emp(tenantId, company, branch, grade, "IFI-005", "Walid Al-Harbi",      "وليد الحربي",     deptEng, desigSrSWE,  13_000m, new DateTime(2022, 5,  5, 0, 0, 0, DateTimeKind.Utc), "Saudi",    "Saudi",    "NationalId", "1050005005");
        var empAmira  = Emp(tenantId, company, branch, grade, "IFI-006", "Amira Al-Shehri",     "أميرة الشهري",    deptHR,  desigHRSpec,  9_000m, new DateTime(2023, 1, 10, 0, 0, 0, DateTimeKind.Utc), "Saudi",    "Saudi",    "NationalId", "2060006006");

        // 6 expat employees
        var empRaj    = Emp(tenantId, company, branch, grade, "IFI-007", "Raj Krishnamurthy",  "راج كريشنامورثي",   deptEng, desigSrSWE,  13_500m, new DateTime(2021, 9,  1, 0, 0, 0, DateTimeKind.Utc), "Indian",    "NonSaudi", "Iqama", "2530000001");
        var empLiu    = Emp(tenantId, company, branch, grade, "IFI-008", "Liu Wei",            "ليو وي",             deptEng, desigSWE,    11_000m, new DateTime(2022, 3, 15, 0, 0, 0, DateTimeKind.Utc), "Chinese",   "NonSaudi", "Iqama", "2530000002");
        var empCarlos = Emp(tenantId, company, branch, grade, "IFI-009", "Carlos Mendez",      "كارلوس مينديز",     deptEng, desigDevOps, 12_000m, new DateTime(2022, 7, 20, 0, 0, 0, DateTimeKind.Utc), "Mexican",   "NonSaudi", "Iqama", "2530000003");
        var empAmiraE = Emp(tenantId, company, branch, grade, "IFI-010", "Amira Mansour",      "أميرة منصور",       deptEng, desigQA,     10_500m, new DateTime(2023, 2,  1, 0, 0, 0, DateTimeKind.Utc), "Egyptian",  "NonSaudi", "Iqama", "2530000004");
        var empDaniel = Emp(tenantId, company, branch, grade, "IFI-011", "Daniel Osei",        "دانيال أوسي",       deptPrd, desigBA,      9_000m, new DateTime(2023, 4, 12, 0, 0, 0, DateTimeKind.Utc), "Ghanaian",  "NonSaudi", "Iqama", "2530000005");
        var empSunita = Emp(tenantId, company, branch, grade, "IFI-012", "Sunita Patel",       "سونيتا باتيل",      deptPrd, desigUX,      9_000m, new DateTime(2023, 6,  5, 0, 0, 0, DateTimeKind.Utc), "Indian",    "NonSaudi", "Iqama", "2530000006");

        var saudiEmps = new[] { empYaser, empNadia, empAhmad, empSara, empWalid, empAmira };
        var expatEmps = new[] { empRaj,   empLiu,   empCarlos, empAmiraE, empDaniel, empSunita };
        var allEmps   = saudiEmps.Concat(expatEmps).ToArray();

        db.Employees.AddRange(saudiEmps);
        await db.SaveChangesAsync(ct);

        db.Employees.AddRange(expatEmps);
        await db.SaveChangesAsync(ct);

        // Reporting lines
        empNadia.ManagerEmployeeId  = empYaser.Id;
        empAhmad.ManagerEmployeeId  = empYaser.Id;
        empSara.ManagerEmployeeId   = empYaser.Id;
        empWalid.ManagerEmployeeId  = empYaser.Id;
        empAmira.ManagerEmployeeId  = empNadia.Id;
        empRaj.ManagerEmployeeId    = empYaser.Id;
        empLiu.ManagerEmployeeId    = empWalid.Id;
        empCarlos.ManagerEmployeeId = empWalid.Id;
        empAmiraE.ManagerEmployeeId = empWalid.Id;
        empDaniel.ManagerEmployeeId = empSara.Id;
        empSunita.ManagerEmployeeId = empSara.Id;

        deptEng.ManagerEmployeeId = empYaser.Id;
        deptHR.ManagerEmployeeId  = empNadia.Id;
        deptFin.ManagerEmployeeId = empAhmad.Id;
        deptPrd.ManagerEmployeeId = empSara.Id;

        foreach (var (emp, mgr) in new (Employee, Employee)[]
        {
            (empNadia, empYaser), (empAhmad,  empYaser), (empSara,   empYaser), (empWalid, empYaser),
            (empAmira, empNadia), (empRaj,    empYaser), (empLiu,    empWalid), (empCarlos,empWalid),
            (empAmiraE,empWalid), (empDaniel, empSara),  (empSunita, empSara),
        })
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
        var ibans = new[]
        {
            "SA6080000000300000000001", "SA6180000000300000000002", "SA6280000000300000000003",
            "SA6380000000300000000004", "SA6480000000300000000005", "SA6580000000300000000006",
            "SA6605000000400000000001", "SA6705000000400000000002", "SA6805000000400000000003",
            "SA6905000000400000000004", "SA7005000000400000000005", "SA7105000000400000000006",
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
                Iban                     = ibans[i],
                SalaryCurrency           = "SAR",
                PaymentMethod            = "BankTransfer",
                WpsEligible              = true,
                EosbEligible             = true,
                MolId                    = emp.IdNumber,
                SocialInsuranceReference = $"{GosiEmployerId}-{(i + 1):D3}",
                PayrollGroup             = "Main",
                SalaryStructureReference = "IFL-STANDARD",
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

        var today = DateOnly.FromDateTime(now.Date);
        foreach (var (emp, idx) in allEmps.Select((e, i) => (e, i)))
        {
            foreach (var (lt, entitled) in new (LeaveType Lt, decimal Entitled)[]
                { (ltAnnual, 30m), (ltSick, 15m), (ltCasual, 5m) })
            {
                var used = Math.Min(entitled - 1, (idx * 2 + (lt.Code == "SICK" ? 1 : 3)) % (int)entitled);
                db.EmployeeLeaveBalances.Add(new EmployeeLeaveBalance
                {
                    TenantId    = tenantId, EmployeeId = emp.Id, EmployeeName = emp.FullName,
                    LeaveTypeId = lt.Id, LeaveTypeName = lt.NameEn, Year = today.Year,
                    Entitled    = entitled, Accrued = Math.Round(entitled * today.Month / 12m, 1),
                    Used        = used, Pending = 0,
                    CarriedForward = lt.Code == "ANNUAL" && idx % 3 == 0 ? 3 : 0,
                });
            }
        }
        await db.SaveChangesAsync(ct);

        // ── 13. Attendance (last 30 working days) ─────────────────────────────
        var rng     = new Random(tenantId.GetHashCode() & 0x7fffffff);
        var attRecs = new List<AttendanceRecord>();
        for (var d = today.AddDays(-30); d <= today; d = d.AddDays(1))
        {
            if (d.DayOfWeek is DayOfWeek.Friday or DayOfWeek.Saturday) continue;
            foreach (var emp in allEmps)
                attRecs.Add(new AttendanceRecord
                {
                    TenantId   = tenantId, EmployeeId = emp.Id, WorkDate = d,
                    TimeIn     = new TimeOnly(8,  30 + rng.Next(0, 15)),
                    TimeOut    = new TimeOnly(17, 30 + rng.Next(0, 25)),
                    Status     = "Present", Notes = string.Empty,
                });
        }
        db.AttendanceRecords.AddRange(attRecs);
        await db.SaveChangesAsync(ct);

        // ── 14. Locked payroll run ─────────────────────────────────────────────
        var perEmpData = new List<(
            Employee Emp, decimal Basic, decimal Housing, decimal Transport,
            decimal BaseGross, decimal Gross,
            decimal EmpGosiTotal, decimal EmrGosiTotal, GosiContributionResult GosiResult)>();

        foreach (var emp in allEmps)
        {
            var basic     = emp.Salary ?? 0m;
            var housing   = basic * 0.25m;
            var transport = basic * 0.10m;
            var baseGross = basic + housing + transport;
            var gosi      = GosiCalculationService.Calculate(emp.Nationality, basic, gosiRules, periodDate, tenantId);
            perEmpData.Add((emp, basic, housing, transport, baseGross, baseGross, gosi.EmployeeTotal, gosi.EmployerTotal, gosi));
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
            CreatedAtUtc     = prevMonth.AddDays(-5),
            ProcessedAtUtc   = prevMonth,
            LockedAtUtc      = prevMonth.AddDays(3),
        };
        db.PayrollRuns.Add(payrollRun);
        await db.SaveChangesAsync(ct);

        var payslipComponents = new List<PayslipComponent>();
        var glEntries         = new List<FinanceGlEntry>();
        var runEmployees      = new List<PayrollRunEmployee>();
        var payslips          = new List<Payslip>();
        var payrollSlips      = new List<PayrollSlip>();
        var entryDate         = DateOnly.FromDateTime(prevMonth.AddDays(3));

        for (var i = 0; i < perEmpData.Count; i++)
        {
            var (emp, basic, housing, transport, baseGross, gross, empGosiTotal, emrGosiTotal, gosi) = perEmpData[i];
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
                PayslipNumber = $"IFL-{year}{month:D2}-{(i + 1):D3}",
                Language = "en", IsPublishedToEss = true,
                PublishedAtUtc = prevMonth.AddDays(4),
            };
            payslips.Add(payslip);

            payslipComponents.Add(new PayslipComponent { TenantId=tenantId, PayslipId=payslip.Id, ComponentType="Earning", ComponentName="Basic Salary",        Amount=basic });
            payslipComponents.Add(new PayslipComponent { TenantId=tenantId, PayslipId=payslip.Id, ComponentType="Earning", ComponentName="Housing Allowance",   Amount=housing });
            payslipComponents.Add(new PayslipComponent { TenantId=tenantId, PayslipId=payslip.Id, ComponentType="Earning", ComponentName="Transport Allowance", Amount=transport });

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
                TransportAllowance = transport, OtherAllowances = 0m,
                GrossSalary = gross, Deductions = empGosiTotal, NetSalary = netPay,
                Status = "Processed",
            });

            glEntries.Add(new FinanceGlEntry
            {
                TenantId = tenantId, SourceModule = "Payroll", SourceEntityId = payrollRun.Id,
                SourceEntityRef = payrollRun.Id.ToString()[..8], EventType = "PayrollPosting",
                DebitAccount = "5100 - Salaries & Wages", CreditAccount = "2100 - Salaries Payable",
                Amount = baseGross, Currency = "SAR", EntryDate = entryDate, Period = period,
                Description = $"Salary posting {emp.FullName} {period}", PostedByName = "System",
            });

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

        var glSalary = glEntries.Where(e => e.DebitAccount.StartsWith("5100")).Sum(e => e.Amount);
        var glGosi   = glEntries.Where(e => e.DebitAccount.StartsWith("6100")).Sum(e => e.Amount);

        logger.LogInformation(
            "IntelliFlowDemoSeeder: seeded IntelliFlow Systems — {Emp} employees ({Saudi} Saudi, {Expat} expat), " +
            "payroll {Period} gross={Gross:N2} ded={Ded:N2} net={Net:N2} SAR, GL={GL:N2} SAR.",
            allEmps.Length, saudiEmps.Length, expatEmps.Length,
            period, totalGross, totalDed, totalNet, glSalary + glGosi);
    }
}
