using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
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
    public const string DemoCompanyRegistrationNumber = "1010334455";
    private const string DemoPassword = "IntelliFlow@2026!";
    private const string DemoGosiEmployerId = "3000112233";
    private const string DemoEstablishmentId = "7000445566";
    private const string DemoWorkLocationId = "QIWA-RYD-HQ-01";

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

        // Subscription currency is the SaaS billing contract; payroll/reporting currency is the tenant's
        // localization + legal-entity currency. Seed it explicitly so a clean KSA pilot does not inherit
        // the US/USD model defaults while every salary and ledger row is denominated in SAR.
        db.TenantLocalizationSettings.Add(new TenantLocalizationSetting
        {
            TenantId = tenantId,
            DefaultLanguage = "en",
            RtlEnabled = false,
            CalendarSystem = "Gregorian",
            DefaultTimezone = "Asia/Riyadh",
            DateFormat = "DD/MM/YYYY",
            CurrencyCode = "SAR",
            CountryCode = "SAU",
            WeekStartDay = "Sunday",
            WorkWeek = "Sun-Thu",
            HijriDatesEnabled = true,
            UpdatedAtUtc = now,
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
        var seededUsers = new List<(User User, string Role, bool GroupScope)>();

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
            seededUsers.Add((u, roleName, isGroupScope));
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
            RegistrationNumber = DemoCompanyRegistrationNumber,
            WpsEmployerId      = DemoEstablishmentId,
            GosiEmployerId     = DemoGosiEmployerId,
            QiwaEstablishmentId = DemoEstablishmentId,
            DefaultCurrency    = "SAR",
            IsActive           = true,
            CreatedAtUtc       = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Companies.Add(company);
        db.QiwaTenantConnections.Add(new QiwaTenantConnection
        {
            TenantId = tenantId,
            EstablishmentId = DemoEstablishmentId,
            EstablishmentName = company.TradeName,
            Status = QiwaConnectionStatuses.Disconnected,
            Environment = "sandbox",
            UnifiedOrganisationNumber = company.RegistrationNumber,
            CreatedAtUtc = now,
        });
        await db.SaveChangesAsync(ct);

        // Entity scope is default-deny. Every non-group pilot persona therefore needs an
        // explicit legal-entity grant before its JWT can see operational records (including
        // role-owned Approval Center work). Without this, HR Manager authenticates with the
        // correct role but receives entity_scope=none, so the ambient company query filter
        // removes the request before the role-owner predicate can match it.
        foreach (var (user, role, isGroupScope) in seededUsers.Where(x => !x.GroupScope))
        {
            db.UserEntityAccesses.Add(new UserEntityAccess
            {
                TenantId = tenantId,
                UserId = user.Id,
                CompanyId = company.Id,
                GrantMode = EntityGrantModes.SelectedCompanies,
                Role = role,
                IsActive = true,
                GrantedAt = now,
            });
        }
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
        static Employee Emp(
            Guid tid, Company co, Branch br, Grade g,
            string code, string en, string ar,
            Department dept, Designation desig,
            decimal basic, DateTime joining,
            string nationality, string saudiFlag, string idType, string idNum,
            DateOnly? iqamaExpiry = null) => new()
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
            // GccReadinessFloor makes IqamaExpiry a fail-closed PAY gate for non-GCC expats, so an
            // Iqama number without its expiry leaves every expat unpayable and the Compliance module
            // with nothing to show. A Saudi national never holds one (EmployeeFieldRegistry hides it).
            IqamaExpiryDate = idType == "Iqama" ? iqamaExpiry : null,
            GosiReference   = $"GOSI-{code}",
            EstablishmentId = DemoEstablishmentId,
            OccupationCode  = desig.Code switch
            {
                "CTO" => "1120", "FIN-MGR" => "1211", "HR-DIR" => "1212",
                "PM" or "BA" => "2421", "HR-SPEC" => "2423", "ACCT" => "2411",
                "DEVOPS" => "2522", "UX" => "2166", "QA" => "2519",
                _ => "2512",
            },
            WorkLocationId  = DemoWorkLocationId,
            ContractReference = $"IFL-QIWA-{code}-2026",
            QiwaContractNumber = $"IFL-QIWA-{code}-2026",
            WorkPermitReference = saudiFlag == "NonSaudi" ? $"WP-{idNum}" : string.Empty,
            IsDeleted       = false,
        };

        // 6 Saudi nationals
        var empYaser  = Emp(tenantId, company, branch, grade, "IFI-001", "Yaser Al-Ghamdi",     "ياسر الغامدي",    deptEng, desigCTO,    20_000m, new DateTime(2021, 1, 15, 0, 0, 0, DateTimeKind.Utc), "Saudi",    "Saudi",    "NationalId", "1010001001");
        var empNadia  = Emp(tenantId, company, branch, grade, "IFI-002", "Nadia Al-Zahrani",    "نادية الزهراني",   deptHR,  desigHRDir,  16_000m, new DateTime(2021, 4,  1, 0, 0, 0, DateTimeKind.Utc), "Saudi",    "Saudi",    "NationalId", "2020002002");
        var empAhmad  = Emp(tenantId, company, branch, grade, "IFI-003", "Ahmad Al-Qahtani",    "أحمد القحطاني",   deptFin, desigFinMgr, 15_000m, new DateTime(2021, 6, 10, 0, 0, 0, DateTimeKind.Utc), "Saudi",    "Saudi",    "NationalId", "1030003003");
        var empSara   = Emp(tenantId, company, branch, grade, "IFI-004", "Sara Al-Otaibi",      "سارة العتيبي",    deptPrd, desigPM,     14_000m, new DateTime(2022, 2, 20, 0, 0, 0, DateTimeKind.Utc), "Saudi",    "Saudi",    "NationalId", "2040004004");
        var empWalid  = Emp(tenantId, company, branch, grade, "IFI-005", "Walid Al-Harbi",      "وليد الحربي",     deptEng, desigSrSWE,  13_000m, new DateTime(2022, 5,  5, 0, 0, 0, DateTimeKind.Utc), "Saudi",    "Saudi",    "NationalId", "1050005005");
        var empAmira  = Emp(tenantId, company, branch, grade, "IFI-006", "Amira Al-Shehri",     "أميرة الشهري",    deptHR,  desigHRSpec,  9_000m, new DateTime(2023, 1, 10, 0, 0, 0, DateTimeKind.Utc), "Saudi",    "Saudi",    "NationalId", "2060006006");

        // 6 expat employees.
        // Iqama expiry is deliberately a SPREAD, anchored on seed day so the demo never goes stale:
        // four comfortably valid, two inside the 60-day alert window (EmployeeReadinessEvaluator's
        // DefaultAlertDays) so Compliance has real amber to show. None is back-dated: an expired
        // Iqama IS handled — FieldPresence.Expired never blocks activation — but GccReadinessFloor
        // makes it a fail-closed PAY blocker, which would hand the flagship pilot tenant a payroll
        // run that cannot pay that employee. That is a defect to demo on request, not a default.
        var iqamaFrom = DateOnly.FromDateTime(now.Date);
        var empRaj    = Emp(tenantId, company, branch, grade, "IFI-007", "Raj Krishnamurthy",  "راج كريشنامورثي",   deptEng, desigSrSWE,  13_500m, new DateTime(2021, 9,  1, 0, 0, 0, DateTimeKind.Utc), "Indian",    "NonSaudi", "Iqama", "2530000001", iqamaFrom.AddDays(410));
        var empLiu    = Emp(tenantId, company, branch, grade, "IFI-008", "Liu Wei",            "ليو وي",             deptEng, desigSWE,    11_000m, new DateTime(2022, 3, 15, 0, 0, 0, DateTimeKind.Utc), "Chinese",   "NonSaudi", "Iqama", "2530000002", iqamaFrom.AddDays(38));  // expiring soon (amber)
        var empCarlos = Emp(tenantId, company, branch, grade, "IFI-009", "Carlos Mendez",      "كارلوس مينديز",     deptEng, desigDevOps, 12_000m, new DateTime(2022, 7, 20, 0, 0, 0, DateTimeKind.Utc), "Mexican",   "NonSaudi", "Iqama", "2530000003", iqamaFrom.AddDays(250));
        var empAmiraE = Emp(tenantId, company, branch, grade, "IFI-010", "Amira Mansour",      "أميرة منصور",       deptEng, desigQA,     10_500m, new DateTime(2023, 2,  1, 0, 0, 0, DateTimeKind.Utc), "Egyptian",  "NonSaudi", "Iqama", "2530000004", iqamaFrom.AddDays(54));  // expiring soon (amber)
        var empDaniel = Emp(tenantId, company, branch, grade, "IFI-011", "Daniel Osei",        "دانيال أوسي",       deptPrd, desigBA,      9_000m, new DateTime(2023, 4, 12, 0, 0, 0, DateTimeKind.Utc), "Ghanaian",  "NonSaudi", "Iqama", "2530000005", iqamaFrom.AddDays(620));
        var empSunita = Emp(tenantId, company, branch, grade, "IFI-012", "Sunita Patel",       "سونيتا باتيل",      deptPrd, desigUX,      9_000m, new DateTime(2023, 6,  5, 0, 0, 0, DateTimeKind.Utc), "Indian",    "NonSaudi", "Iqama", "2530000006", iqamaFrom.AddDays(165));

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
        // Employee.Salary remains a legacy/search projection, but payroll and statutory workflows resolve
        // EmployeeSalaryStructure. A pilot seed must follow that same contract rather than creating a
        // hand-crafted paid slip that has no corresponding effective salary assignment.
        var salaryEffectiveDate = new DateOnly(year, 1, 1);
        var salaryStructure = new SalaryStructure
        {
            TenantId = tenantId,
            CompanyId = company.Id,
            Code = "IFL-STANDARD",
            Name = "IntelliFlow Standard Package",
            Currency = "SAR",
            EffectiveDate = salaryEffectiveDate,
            MinBasicSalary = allEmps.Min(e => e.Salary ?? 0m),
            MaxBasicSalary = allEmps.Max(e => e.Salary ?? 0m),
            MinGrossSalary = allEmps.Min(e => (e.Salary ?? 0m) * 1.35m),
            MaxGrossSalary = allEmps.Max(e => (e.Salary ?? 0m) * 1.35m),
            EligibleGradeIdsJson = System.Text.Json.JsonSerializer.Serialize(new[] { grade.Id }),
            IsActive = true,
        };
        db.SalaryStructures.Add(salaryStructure);
        db.SalaryComponents.AddRange(
            new SalaryComponent
            {
                TenantId = tenantId, SalaryStructureId = salaryStructure.Id, Code = "BASIC",
                Name = "Basic Salary", ComponentType = "Earning", CalculationType = "Fixed", IsActive = true,
            },
            new SalaryComponent
            {
                TenantId = tenantId, SalaryStructureId = salaryStructure.Id, Code = "HOUSING",
                Name = "Housing Allowance", ComponentType = "Earning", CalculationType = "Percentage",
                Percentage = 25m, IsActive = true,
            },
            new SalaryComponent
            {
                TenantId = tenantId, SalaryStructureId = salaryStructure.Id, Code = "TRANSPORT",
                Name = "Transport Allowance", ComponentType = "Earning", CalculationType = "Percentage",
                Percentage = 10m, IsActive = true,
            });

        var salaryAssignments = allEmps.Select(emp =>
        {
            var basic = emp.Salary ?? 0m;
            return new EmployeeSalaryStructure
            {
                TenantId = tenantId,
                EmployeeId = emp.Id,
                SalaryStructureId = salaryStructure.Id,
                BasicSalary = basic,
                HousingAllowance = basic * 0.25m,
                TransportAllowance = basic * 0.10m,
                EffectiveDate = salaryEffectiveDate,
                Currency = "SAR",
                IsActive = true,
            };
        }).ToArray();
        db.EmployeeSalaryStructures.AddRange(salaryAssignments);
        await db.SaveChangesAsync(ct);

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
                Iban                     = Zayra.Api.Infrastructure.Payroll.IbanValidator.WithValidCheckDigits(ibans[i]),
                SalaryCurrency           = "SAR",
                PaymentMethod            = "BankTransfer",
                WpsEligible              = true,
                EosbEligible             = true,
                MolId                    = emp.IdNumber,
                SocialInsuranceReference = $"{DemoGosiEmployerId}-{(i + 1):D3}",
                PayrollGroup             = "Main",
                SalaryStructureReference = salaryStructure.Code,
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

        // ── 12b. Leave requests ───────────────────────────────────────────────
        // The flagship pilot tenant previously seeded leave TYPES and BALANCES but not a single
        // REQUEST, so /api/leave/requests returned total:0 and the Leave module opened empty for the
        // customer. Every row is built through DemoLeaveSeed so it carries the employee's CompanyId
        // (LeaveRequest is ICompanyScopedOperational — see that type's remarks).
        var leaveWorkflow = new ApprovalWorkflow
        {
            TenantId   = tenantId,
            Code       = "LEAVE-APPROVAL",
            Name       = "Leave Approval",
            EntityName = nameof(LeaveRequest),
            IsActive   = true,
        };
        leaveWorkflow.Steps.Add(new ApprovalWorkflowStep
        {
            TenantId = tenantId, WorkflowId = leaveWorkflow.Id, StepOrder = 1,
            StepName = "Line Manager Approval", ApproverRole = "Manager",
        });
        leaveWorkflow.Steps.Add(new ApprovalWorkflowStep
        {
            TenantId = tenantId, WorkflowId = leaveWorkflow.Id, StepOrder = 2,
            StepName = "HR Approval", ApproverRole = "HR Manager", IsFinalStep = true,
        });
        db.ApprovalWorkflows.Add(leaveWorkflow);
        await db.SaveChangesAsync(ct);

        // A believable queue: work waiting on two different roles, settled history either way.
        var leaveRows = new (LeaveRequest Request, string Role)[]
        {
            // ── Awaiting a decision — this is what makes Approvals non-empty ──
            (DemoLeaveSeed.Request(tenantId, empRaj, ltAnnual, today.AddDays(21), today.AddDays(32),
                DemoLeaveSeed.PendingManager, "Annual home-country visit", now.AddDays(-2)), "Manager"),
            (DemoLeaveSeed.Request(tenantId, empSunita, ltCasual, today.AddDays(3), today.AddDays(4),
                DemoLeaveSeed.PendingManager, "Family commitment", now.AddHours(-5)), "Manager"),
            (DemoLeaveSeed.Request(tenantId, empAmiraE, ltAnnual, today.AddDays(10), today.AddDays(16),
                DemoLeaveSeed.PendingHr, "Eid break extension", now.AddDays(-1)), "HR Manager"),

            // ── Approved ──
            (DemoLeaveSeed.Request(tenantId, empLiu, ltSick, today.AddDays(-6), today.AddDays(-5),
                DemoLeaveSeed.Approved, "Medical appointment", now.AddDays(-7), now.AddDays(-6),
                managerApprovalNotes: "Approved — medical note on file."), "Manager"),
            (DemoLeaveSeed.Request(tenantId, empNadia, ltAnnual, today.AddDays(-20), today.AddDays(-14),
                DemoLeaveSeed.Approved, "Annual vacation", now.AddDays(-28), now.AddDays(-25),
                managerApprovalNotes: "Approved — cover arranged."), "Manager"),
            (DemoLeaveSeed.Request(tenantId, empWalid, ltCasual, today.AddDays(-2), today.AddDays(-2),
                DemoLeaveSeed.Approved, "Government paperwork", now.AddDays(-4), now.AddDays(-3)), "Manager"),

            // ── Rejected ──
            (DemoLeaveSeed.Request(tenantId, empCarlos, ltAnnual, today.AddDays(6), today.AddDays(20),
                DemoLeaveSeed.Rejected, "Extended personal travel", now.AddDays(-9), now.AddDays(-8),
                rejectionReason: "Clashes with the release freeze — please re-submit for after the 30th."), "Manager"),
        };

        // Role-routed steps (ApproverId null): these demo employees have no portal user account, and
        // LeaveService routes to the role in exactly that case. Admin and the matching role holder can
        // both decide, so the queue is genuinely actionable for hrmanager@ / manager@intelliflow.com.
        foreach (var (request, role) in leaveRows)
        {
            db.LeaveRequests.Add(request);
            DemoLeaveSeed.AddApprovalTrail(db, request, role,
                approverUserId: null, approverName: string.Empty, approverEmployeeId: null,
                workflowId: leaveWorkflow.Id);
        }
        await db.SaveChangesAsync(ct);

        // ── 13. Attendance (last 30 working days) ─────────────────────────────
        // Seeds AttendanceDailyRecord (what GET /api/attendance and /monthly read), the punches
        // behind it, and the legacy projection. This tenant sets DefaultTimezone=Asia/Riyadh, so
        // the local wall-clock punches below are converted to UTC before any late/worked arithmetic
        // — the same conversion ProcessEmployeeDay performs.
        var rng       = new Random(tenantId.GetHashCode() & 0x7fffffff);
        var attPolicy = await AttendanceDemoSeed.ResolvePolicyAsync(db, tenantId, ct);
        var attTz     = await AttendanceDemoSeed.ResolveTimeZoneAsync(db, tenantId, ct);
        var approvedLeave = await db.LeaveRequests.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Status == "Approved")
            .Select(x => new { x.EmployeeId, x.StartDate, x.EndDate })
            .ToListAsync(ct);
        for (var d = today.AddDays(-30); d <= today; d = d.AddDays(1))
        {
            if (d.DayOfWeek is DayOfWeek.Friday or DayOfWeek.Saturday) continue;
            foreach (var emp in allEmps)
            {
                var inLocal  = new TimeOnly(8,  30 + rng.Next(0, 15));
                var outLocal = new TimeOnly(17, 30 + rng.Next(0, 25));
                var onLeave  = approvedLeave.Any(l => l.EmployeeId == emp.Id && l.StartDate <= d && l.EndDate >= d);
                AttendanceDemoSeed.AddDay(db, tenantId, AttendanceDemoSeed.EmployeeFacts.From(emp), d,
                    onLeave ? null : inLocal,
                    onLeave ? null : outLocal,
                    attPolicy, attTz,
                    onLeave ? AttendanceDemoSeed.DayContext.ApprovedLeave : AttendanceDemoSeed.DayContext.WorkingDay);
            }
        }
        await db.SaveChangesAsync(ct);

        // ── 14. Locked payroll run ─────────────────────────────────────────────
        var perEmpData = new List<(
            Employee Emp, decimal Basic, decimal Housing, decimal Transport,
            decimal BaseGross, decimal Gross,
            decimal EmpGosiTotal, decimal EmrGosiTotal, StatutoryDeductionResult GosiResult)>();
        var statutoryCalculator = new KsaDeductionCalculator(new StatutoryRuleReader(db));

        foreach (var emp in allEmps)
        {
            var salary = salaryAssignments.Single(s => s.EmployeeId == emp.Id && s.EffectiveDate <= periodDate);
            var basic     = salary.BasicSalary;
            var housing   = salary.HousingAllowance;
            var transport = salary.TransportAllowance;
            var baseGross = basic + housing + transport;
            var gosi = await statutoryCalculator.CalculateAsync(new StatutoryDeductionInput(
                EmployeeId: Guid.Empty,
                CompanyId: company.Id,
                Salary: new SalaryBreakdown(basic, housing, transport, 0m),
                Nationality: emp.Nationality,
                ContractType: emp.ContractType,
                PeriodYear: year,
                PeriodMonth: month), ct);
            perEmpData.Add((emp, basic, housing, transport, baseGross, baseGross,
                gosi.TotalEmployeeDeduction, gosi.TotalEmployerContribution, gosi));
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
            TotalEmployerStatutoryCost = perEmpData.Sum(x => x.EmrGosiTotal),
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
        var payrollDeductions = new List<PayrollDeduction>();
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

            foreach (var line in gosi.Lines)
            {
                if (line.EmployeeAmount > 0m)
                {
                    payslipComponents.Add(new PayslipComponent
                    {
                        TenantId = tenantId, PayslipId = payslip.Id, ComponentType = "Deduction",
                        ComponentName = line.Label, Amount = line.EmployeeAmount,
                    });
                    payrollDeductions.Add(new PayrollDeduction
                    {
                        TenantId = tenantId, CompanyId = company.Id, PayrollRunId = payrollRun.Id,
                        EmployeeId = emp.Id, ComponentCode = line.Code, ComponentName = line.Label,
                        Amount = line.EmployeeAmount, Source = "Statutory", IsEmployerContribution = false,
                    });
                }
                if (line.EmployerAmount > 0m)
                    payrollDeductions.Add(new PayrollDeduction
                    {
                        TenantId = tenantId, CompanyId = company.Id, PayrollRunId = payrollRun.Id,
                        EmployeeId = emp.Id, ComponentCode = line.Code, ComponentName = line.Label,
                        Amount = line.EmployerAmount, Source = "Statutory", IsEmployerContribution = true,
                    });
            }

            payrollSlips.Add(new PayrollSlip
            {
                TenantId = tenantId, CompanyId = company.Id, RunId = payrollRun.Id, EmployeeId = emp.Id,
                EmployeeCode = emp.EmployeeCode, EmployeeName = emp.FullName,
                Department = emp.Department ?? string.Empty,
                BasicSalary = basic, HousingAllowance = housing,
                TransportAllowance = transport, OtherAllowances = 0m,
                GrossSalary = gross, Deductions = empGosiTotal, NetSalary = netPay,
                EmployeeStatutoryTotal = empGosiTotal,
                EmployerStatutoryTotal = emrGosiTotal,
                // The demo's first locked run of the year follows Process's current-period-inclusive YTD.
                YtdGross = gross, YtdDeductions = empGosiTotal, YtdNet = netPay,
                FullBasicSalary = basic, FullHousingAllowance = housing, FullTransportAllowance = transport,
                Status = "Final",
            });

            // Persist the same single-sided accrual journal shape produced by PayrollController.Lock:
            // earnings DR + statutory liabilities CR + employer statutory expense DR + net payable CR.
            // This makes the seeded locked run a real reconciliation witness, not a hand-crafted total.
            glEntries.Add(new FinanceGlEntry
            {
                TenantId = tenantId, CompanyId = company.Id,
                SourceModule = "Payroll", SourceEntityId = payrollRun.Id,
                SourceEntityRef = period, EventType = GlEventTypes.Accrual,
                DebitAccount = "5100 - Salaries & Wages", CreditAccount = string.Empty,
                Amount = baseGross, Currency = "SAR", EntryDate = entryDate, Period = period,
                Description = $"Payroll earning: {emp.EmployeeCode}", PostedByName = "System",
            });

            foreach (var line in gosi.Lines)
            {
                if (line.EmployeeAmount > 0m)
                    glEntries.Add(new FinanceGlEntry
                    {
                        TenantId = tenantId, CompanyId = company.Id,
                        SourceModule = "Payroll", SourceEntityId = payrollRun.Id,
                        SourceEntityRef = period, EventType = GlEventTypes.Accrual,
                        DebitAccount = string.Empty, CreditAccount = "2101 - GOSI Employee Payable",
                        Amount = line.EmployeeAmount, Currency = "SAR", EntryDate = entryDate, Period = period,
                        Description = $"{PayrollGlDescriptions.DeductionPrefix}{line.Code}", PostedByName = "System",
                    });
                if (line.EmployerAmount > 0m)
                    glEntries.Add(new FinanceGlEntry
                    {
                        TenantId = tenantId, CompanyId = company.Id,
                        SourceModule = "Payroll", SourceEntityId = payrollRun.Id,
                        SourceEntityRef = period, EventType = GlEventTypes.Accrual,
                        DebitAccount = string.Empty, CreditAccount = "2106 - GOSI Employer Payable",
                        Amount = line.EmployerAmount, Currency = "SAR", EntryDate = entryDate, Period = period,
                        Description = $"{PayrollGlDescriptions.DeductionPrefix}{line.Code}", PostedByName = "System",
                    });
            }

            if (emrGosiTotal > 0m)
                glEntries.Add(new FinanceGlEntry
                {
                    TenantId = tenantId, CompanyId = company.Id,
                    SourceModule = "Payroll", SourceEntityId = payrollRun.Id,
                    SourceEntityRef = period, EventType = GlEventTypes.Accrual,
                    DebitAccount = "5101 - Employer Statutory Expense", CreditAccount = string.Empty,
                    Amount = emrGosiTotal, Currency = "SAR", EntryDate = entryDate, Period = period,
                    Description = "Employer statutory contributions (social insurance)", PostedByName = "System",
                });

            glEntries.Add(new FinanceGlEntry
            {
                TenantId = tenantId, CompanyId = company.Id,
                SourceModule = "Payroll", SourceEntityId = payrollRun.Id,
                SourceEntityRef = period, EventType = GlEventTypes.Accrual,
                DebitAccount = string.Empty, CreditAccount = "2100 - Net Salary Payable",
                Amount = netPay, Currency = "SAR", EntryDate = entryDate, Period = period,
                Description = PayrollGlDescriptions.NetPayable, PostedByName = "System",
            });
        }

        db.PayrollRunEmployees.AddRange(runEmployees);
        db.Payslips.AddRange(payslips);
        await db.SaveChangesAsync(ct);

        db.PayslipComponents.AddRange(payslipComponents);
        db.PayrollDeductions.AddRange(payrollDeductions);
        db.PayrollSlips.AddRange(payrollSlips);
        db.FinanceGlEntries.AddRange(glEntries);
        await db.SaveChangesAsync(ct);

        var glDebits = glEntries.Where(e => !string.IsNullOrEmpty(e.DebitAccount)).Sum(e => e.Amount);
        var glCredits = glEntries.Where(e => !string.IsNullOrEmpty(e.CreditAccount)).Sum(e => e.Amount);
        if (glDebits != glCredits)
            throw new InvalidOperationException($"IntelliFlow seed GL is not balanced: DR={glDebits:N2}, CR={glCredits:N2}.");

        logger.LogInformation(
            "IntelliFlowDemoSeeder: seeded IntelliFlow Systems — {Emp} employees ({Saudi} Saudi, {Expat} expat), " +
            "payroll {Period} gross={Gross:N2} ded={Ded:N2} net={Net:N2} SAR, GL={GL:N2} SAR.",
            allEmps.Length, saudiEmps.Length, expatEmps.Length,
            period, totalGross, totalDed, totalNet, glDebits);
    }
}
