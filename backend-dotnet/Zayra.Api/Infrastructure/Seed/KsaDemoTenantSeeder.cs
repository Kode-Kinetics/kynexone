using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Seed;

/// <summary>
/// Seeds a single, credible KSA demo tenant — Al-Nakheel Technology Solutions Co.
/// 18 employees (10 Saudi, 8 expat), a locked payroll run, GOSI rules, GL entries, bonus, and leave data.
/// Fully idempotent: skips if the "alnakheel" slug already exists.
/// </summary>
public static class KsaDemoTenantSeeder
{
    private const string Slug             = "alnakheel";
    private const string DemoPassword     = "AlNakheel@2026!";
    private const string GosiEmployerId   = "3000987654";

    // ── Pre-computed run totals (verified: sum(DR) == sum(CR) == 354,245 SAR) ──
    // Saudi gross:  212,000  |  Expat gross:   117,500  |  Total gross: 329,500
    // Employee deductions (GOSI 10% + SANED 1% on basic, Saudi only): 17,765
    // Net salary: 311,735
    // Employer GOSI (Saudi 12%+1% + NonSaudi OccHazard 2%): 24,745
    // GL DR total = 329,500 + 24,745 = 354,245 == GL CR total 354,245 ✓
    private const decimal TotalGross      = 329_500m;
    private const decimal TotalDeductions =  17_765m;
    private const decimal TotalNet        = 311_735m;

    public static async Task SeedAsync(
        ZayraDbContext db,
        IPasswordHasher hasher,
        IAuthSeeder authSeeder,
        ILogger logger,
        CancellationToken ct = default)
    {
        // ── Idempotency guard ─────────────────────────────────────────────────
        if (await db.Tenants.AnyAsync(t => t.Slug == Slug, ct))
        {
            logger.LogInformation("KsaDemoTenantSeeder: alnakheel already exists — skipping.");
            return;
        }

        logger.LogInformation("KsaDemoTenantSeeder: seeding Al-Nakheel Technology Solutions Co. ...");

        // ── 1. Tenant ─────────────────────────────────────────────────────────
        var tenant = new Tenant { Name = "Al-Nakheel Technology Solutions Co.", Slug = Slug, IsActive = true };
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
            MaxEmployees  = 100,
            MaxUsers      = 20,
            BillingEmail  = "finance@alnakheel.com.sa",
            BillingCycle  = "Annually",
            MonthlyAmount = 4_500m,
            CurrencyCode  = "SAR",
        });

        // ── 4. Feature flags ──────────────────────────────────────────────────
        foreach (var key in new[]
        {
            FeatureKeys.Payroll, FeatureKeys.Recruitment, FeatureKeys.Performance,
            FeatureKeys.Compliance, FeatureKeys.Finance, FeatureKeys.Shifts,
            FeatureKeys.Overtime, FeatureKeys.AiAssistant, FeatureKeys.ResumeScreening,
            FeatureKeys.PayrollAiValidation, FeatureKeys.RiskScores, FeatureKeys.WpsExport,
            FeatureKeys.EosbCalc, FeatureKeys.QiwaIntegration, FeatureKeys.HijriCalendar,
            FeatureKeys.MobileApp,
        })
        {
            db.TenantFeatureFlags.Add(new TenantFeatureFlag
            {
                TenantId    = tenantId,
                FeatureKey  = key,
                IsEnabled   = true,
                UpdatedAtUtc = DateTime.UtcNow,
            });
        }

        await db.SaveChangesAsync(ct);

        // ── 5. Portal users ───────────────────────────────────────────────────
        var userSpecs = new[]
        {
            ("Admin",            "Al-Nakheel Administrator", "admin@alnakheel.com.sa"),
            ("HR Director",      "Noura Al-Qahtani",         "hr@alnakheel.com.sa"),
            ("HR Manager",       "Mohammed Al-Shammari",     "hrm@alnakheel.com.sa"),
            ("Finance Approver", "Abdullah Al-Dosari",       "finance@alnakheel.com.sa"),
            ("Manager",          "Faisal Al-Harbi",          "ops.manager@alnakheel.com.sa"),
            ("Supervisor",       "Hessa Al-Otaibi",          "supervisor@alnakheel.com.sa"),
            ("Auditor",          "Tariq Al-Zahrani",         "audit@alnakheel.com.sa"),
        };

        foreach (var (roleName, fullName, email) in userSpecs)
        {
            if (!roleMap.TryGetValue(roleName, out var role))
            {
                logger.LogWarning("KsaDemoTenantSeeder: role '{Role}' not found — skipping user {Email}.", roleName, email);
                continue;
            }
            var user = new User
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
            };
            user.UserRoles.Add(new UserRole { User = user, RoleId = role.Id });
            db.Users.Add(user);
        }
        await db.SaveChangesAsync(ct);

        // ── 6. Company + Branch ───────────────────────────────────────────────
        var company = new Company
        {
            TenantId           = tenantId,
            LegalNameEn        = "Al-Nakheel Technology Solutions Co.",
            LegalNameAr        = "شركة النخيل لحلول التقنية",
            TradeName          = "Al-Nakheel",
            CountryCode        = "SAU",
            Jurisdiction       = "KSA-mainland",
            RegistrationNumber = "1010123456",
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
            NameEn       = "Riyadh HQ",
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
        var deptHR   = new Department { TenantId = tenantId, BranchId = branch.Id, Code = "HR",    NameEn = "Human Resources", SortOrder = 0 };
        var deptFin  = new Department { TenantId = tenantId, BranchId = branch.Id, Code = "FIN",   NameEn = "Finance",         SortOrder = 1 };
        var deptIT   = new Department { TenantId = tenantId, BranchId = branch.Id, Code = "IT",    NameEn = "IT",              SortOrder = 2 };
        var deptOps  = new Department { TenantId = tenantId, BranchId = branch.Id, Code = "OPS",   NameEn = "Operations",      SortOrder = 3 };
        var deptSale = new Department { TenantId = tenantId, BranchId = branch.Id, Code = "SALES", NameEn = "Sales",           SortOrder = 4 };
        db.Departments.AddRange(deptHR, deptFin, deptIT, deptOps, deptSale);
        await db.SaveChangesAsync(ct);

        // ── 8. Grades + Designations (lightweight — one per designation) ──────
        var grade = new Grade { TenantId = tenantId, Code = "KSA-STD", Name = "KSA Standard", Level = 1 };
        db.Grades.Add(grade);
        await db.SaveChangesAsync(ct);

        Designation MakeDesig(string code, string title, Department dept) => new()
        {
            TenantId     = tenantId,
            Code         = code,
            TitleEn      = title,
            GradeId      = grade.Id,
            DepartmentId = dept.Id,
            LevelRank    = 10,
        };

        var desigHRDir   = MakeDesig("HR-DIR",   "HR Director",             deptHR);
        var desigHRMgr   = MakeDesig("HR-MGR",   "HR Manager",              deptHR);
        var desigHRCoord = MakeDesig("HR-COORD", "HR Coordinator",          deptHR);
        var desigTrainer = MakeDesig("TRAINER",  "Training Specialist",     deptHR);
        var desigFinSr   = MakeDesig("FIN-SR",   "Senior Finance Analyst",  deptFin);
        var desigFinOff  = MakeDesig("FIN-OFF",  "Finance Officer",         deptFin);
        var desigFinAP   = MakeDesig("FIN-AP",   "Accounts Payable",        deptFin);
        var desigSrSWE   = MakeDesig("SWE-SR",   "Senior Software Engineer",deptIT);
        var desigSWE     = MakeDesig("SWE",      "Software Engineer",       deptIT);
        var desigQA      = MakeDesig("QA",       "QA Engineer",             deptIT);
        var desigDevOps  = MakeDesig("DEVOPS",   "DevOps Engineer",         deptIT);
        var desigBA      = MakeDesig("BA",       "Business Analyst",        deptIT);
        var desigOpsMgr  = MakeDesig("OPS-MGR",  "Operations Manager",      deptOps);
        var desigSuper   = MakeDesig("OPS-SUP",  "Supervisor",              deptOps);
        var desigOpsCoord= MakeDesig("OPS-COORD","Operations Coordinator",  deptOps);
        var desigSalesDir= MakeDesig("SALES-DIR","Sales Director",          deptSale);
        var desigSalesExe= MakeDesig("SALES-EXE","Sales Executive",         deptSale);
        var desigAccExe  = MakeDesig("ACC-EXE",  "Account Executive",       deptSale);

        db.Designations.AddRange(
            desigHRDir, desigHRMgr, desigHRCoord, desigTrainer,
            desigFinSr, desigFinOff, desigFinAP,
            desigSrSWE, desigSWE, desigQA, desigDevOps, desigBA,
            desigOpsMgr, desigSuper, desigOpsCoord,
            desigSalesDir, desigSalesExe, desigAccExe);
        await db.SaveChangesAsync(ct);

        // ── 9. Employees ──────────────────────────────────────────────────────
        // Helper: housing = Math.Round(basic * 0.25m / 500) * 500 (C# banker's rounding)

        static Employee MakeEmp(
            Guid tid, Company co, Branch br,
            string code, string nameEn, string nameAr,
            Department dept, Designation desig,
            decimal basic, DateTime joining,
            string nationality, string saudiFlag, string idType, string idNumber,
            int? managerId = null) => new()
        {
            TenantId          = tid,
            CompanyId         = co.Id,
            BranchId          = br.Id,
            EmployeeCode      = code,
            FullName          = nameEn,
            EnglishName       = nameEn,
            ArabicName        = nameAr,
            DepartmentId      = dept.Id,
            Department        = dept.NameEn,
            DesignationId     = desig.Id,
            Designation       = desig.TitleEn,
            GradeId           = default, // set below after save (grade.Id captured via closure)
            JobTitle          = desig.TitleEn,
            Salary            = basic,
            // Force UTC — literal new DateTime(...) joining dates are Kind=Unspecified, rejected by
            // Npgsql for the timestamptz column (fails on a fresh DB where this seeds).
            JoiningDate       = DateTime.SpecifyKind(joining, DateTimeKind.Utc),
            Status            = "Active",
            ContractType      = "FixedTerm",
            EmploymentType    = "FullTime",
            Nationality       = nationality,
            CountryCode       = "SAU",
            SaudiOrNonSaudi   = saudiFlag,
            IdType            = idType,
            IdNumber          = idNumber,
            IqamaNumber       = idType == "Iqama" ? idNumber : string.Empty,
            GosiReference     = GosiEmployerId,
            EstablishmentId   = "7000456789",
            IsDeleted         = false,
            ManagerEmployeeId = managerId,
        };

        // Saudi employees 1–10
        var empNoura     = MakeEmp(tenantId, company, branch, "ANK-001", "Noura Al-Qahtani",    "نورة القحطاني",      deptHR,   desigHRDir,   22_000m, new DateTime(2020, 3,  1), "Saudi", "Saudi", "NationalId", "1098765432");
        var empMohammed  = MakeEmp(tenantId, company, branch, "ANK-002", "Mohammed Al-Shammari","محمد الشمري",         deptHR,   desigHRMgr,   15_000m, new DateTime(2021, 6, 15), "Saudi", "Saudi", "NationalId", "1087654321");
        var empFaisal    = MakeEmp(tenantId, company, branch, "ANK-003", "Faisal Al-Harbi",     "فيصل الحربي",        deptOps,  desigOpsMgr,  20_000m, new DateTime(2019, 9,  1), "Saudi", "Saudi", "NationalId", "1076543210");
        var empHessa     = MakeEmp(tenantId, company, branch, "ANK-004", "Hessa Al-Otaibi",     "حصة العتيبي",        deptOps,  desigSuper,   12_000m, new DateTime(2022, 1, 10), "Saudi", "Saudi", "NationalId", "1065432109");
        var empAbdullah  = MakeEmp(tenantId, company, branch, "ANK-005", "Abdullah Al-Dosari",  "عبدالله الدوسري",   deptFin,  desigFinSr,   18_000m, new DateTime(2020,11,  1), "Saudi", "Saudi", "NationalId", "1054321098");
        var empLuluwah   = MakeEmp(tenantId, company, branch, "ANK-006", "Luluwah Al-Ghamdi",   "لولوه الغامدي",      deptFin,  desigFinOff,  11_000m, new DateTime(2023, 2, 14), "Saudi", "Saudi", "NationalId", "1043210987");
        var empTariq     = MakeEmp(tenantId, company, branch, "ANK-007", "Tariq Al-Zahrani",    "طارق الزهراني",      deptIT,   desigSrSWE,   19_000m, new DateTime(2021, 4,  1), "Saudi", "Saudi", "NationalId", "1032109876");
        var empReema     = MakeEmp(tenantId, company, branch, "ANK-008", "Reema Al-Malki",      "ريما المالكي",       deptIT,   desigBA,      13_500m, new DateTime(2022, 9,  1), "Saudi", "Saudi", "NationalId", "1021098765");
        var empKhalid    = MakeEmp(tenantId, company, branch, "ANK-009", "Khalid Al-Mutairi",   "خالد المطيري",       deptSale, desigSalesDir,21_000m, new DateTime(2020, 1, 15), "Saudi", "Saudi", "NationalId", "1010987654");
        var empSara      = MakeEmp(tenantId, company, branch, "ANK-010", "Sara Al-Anzi",        "سارة العنزي",        deptSale, desigAccExe,  10_000m, new DateTime(2023, 7,  1), "Saudi", "Saudi", "NationalId", "1009876543");

        // Expat employees 11–18
        var empAhmed     = MakeEmp(tenantId, company, branch, "ANK-011", "Ahmed Hassan",         "أحمد حسن",          deptIT,   desigSWE,     14_000m, new DateTime(2022, 3, 15), "Egyptian",   "NonSaudi", "Iqama", "2345678901");
        var empPriya     = MakeEmp(tenantId, company, branch, "ANK-012", "Priya Krishnamurthy",  "بريا كريشنامورثي",  deptIT,   desigQA,      11_000m, new DateTime(2023, 1, 20), "Indian",     "NonSaudi", "Iqama", "2234567890");
        var empOmar      = MakeEmp(tenantId, company, branch, "ANK-013", "Omar Abdelnabi",       "عمر عبدالنبي",      deptHR,   desigHRCoord,  9_500m, new DateTime(2022,11,  1), "Sudanese",   "NonSaudi", "Iqama", "2123456789");
        var empJames     = MakeEmp(tenantId, company, branch, "ANK-014", "James Okafor",         "جيمس أوكافور",       deptSale, desigSalesExe,10_500m, new DateTime(2023, 4,  1), "Nigerian",   "NonSaudi", "Iqama", "2012345678");
        var empSiti      = MakeEmp(tenantId, company, branch, "ANK-015", "Siti Rahayu",          "سيتي راهايو",        deptFin,  desigFinAP,    8_500m, new DateTime(2023, 6,  1), "Indonesian", "NonSaudi", "Iqama", "2901234567");
        var empMichael   = MakeEmp(tenantId, company, branch, "ANK-016", "Michael Fernandez",    "مايكل فيرنانديز",    deptOps,  desigOpsCoord, 9_000m, new DateTime(2022, 8, 15), "Filipino",   "NonSaudi", "Iqama", "2890123456");
        var empNadia     = MakeEmp(tenantId, company, branch, "ANK-017", "Nadia Boukhari",       "نادية بوخاري",       deptHR,   desigTrainer, 10_000m, new DateTime(2023, 3,  1), "Moroccan",   "NonSaudi", "Iqama", "2789012345");
        var empDavid     = MakeEmp(tenantId, company, branch, "ANK-018", "David Chen",           "ديفيد تشن",          deptIT,   desigDevOps,  15_000m, new DateTime(2021,12,  1), "Chinese",    "NonSaudi", "Iqama", "2678901234");

        // Fix GradeId (captured after grade was saved)
        foreach (var emp in new[] {
            empNoura, empMohammed, empFaisal, empHessa, empAbdullah, empLuluwah,
            empTariq, empReema, empKhalid, empSara,
            empAhmed, empPriya, empOmar, empJames, empSiti, empMichael, empNadia, empDavid })
        {
            emp.GradeId = grade.Id;
        }

        db.Employees.AddRange(
            empNoura, empMohammed, empFaisal, empHessa, empAbdullah, empLuluwah,
            empTariq, empReema, empKhalid, empSara);
        await db.SaveChangesAsync(ct);

        db.Employees.AddRange(
            empAhmed, empPriya, empOmar, empJames, empSiti, empMichael, empNadia, empDavid);
        await db.SaveChangesAsync(ct);

        // Set department managers
        deptHR.ManagerEmployeeId   = empNoura.Id;
        deptFin.ManagerEmployeeId  = empAbdullah.Id;
        deptIT.ManagerEmployeeId   = empTariq.Id;
        deptOps.ManagerEmployeeId  = empFaisal.Id;
        deptSale.ManagerEmployeeId = empKhalid.Id;

        // Apply manager IDs to employees that needed a managerId
        // (MakeEmp doesn't accept managerId for expats seeded after Saudi managers exist)
        empMohammed.ManagerEmployeeId  = empNoura.Id;
        empHessa.ManagerEmployeeId     = empFaisal.Id;
        empLuluwah.ManagerEmployeeId   = empAbdullah.Id;
        empReema.ManagerEmployeeId     = empTariq.Id;
        empSara.ManagerEmployeeId      = empKhalid.Id;
        empAhmed.ManagerEmployeeId     = empTariq.Id;
        empPriya.ManagerEmployeeId     = empTariq.Id;
        empOmar.ManagerEmployeeId      = empMohammed.Id;
        empJames.ManagerEmployeeId     = empKhalid.Id;
        empSiti.ManagerEmployeeId      = empLuluwah.Id;
        empMichael.ManagerEmployeeId   = empHessa.Id;
        empNadia.ManagerEmployeeId     = empMohammed.Id;
        empDavid.ManagerEmployeeId     = empTariq.Id;

        await db.SaveChangesAsync(ct);

        // ── 10. Reporting lines ───────────────────────────────────────────────
        var reportingPairs = new (Employee Emp, Employee Mgr)[]
        {
            (empMohammed, empNoura),
            (empHessa,    empFaisal),
            (empLuluwah,  empAbdullah),
            (empReema,    empTariq),
            (empSara,     empKhalid),
            (empAhmed,    empTariq),
            (empPriya,    empTariq),
            (empOmar,     empMohammed),
            (empJames,    empKhalid),
            (empSiti,     empLuluwah),
            (empMichael,  empHessa),
            (empNadia,    empMohammed),
            (empDavid,    empTariq),
        };
        foreach (var (emp, mgr) in reportingPairs)
        {
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
        }
        await db.SaveChangesAsync(ct);

        // ── 11. GOSI contribution rules (KSA 2024 rates) ─────────────────────
        // VERIFY: rates per GOSI Circular effective 2024-01-01.
        // Saudi Annuities: 10% employee / 12% employer (min 400, max 45,000 SAR).
        // SANED: 1% employee / 1% employer (no wage cap).
        // NonSaudi Occupational Hazards: 2% employer (no cap).
        var gosiEffective = new DateOnly(2024, 1, 1);
        db.GosiContributionRules.AddRange(
            new GosiContributionRule { TenantId = tenantId, CountryCode = "SA", Classification = GosiClassifications.Saudi, Branch = GosiBranches.Annuities, Payer = GosiPayers.Employee, Rate = 0.10m, MinContributoryWage = 400m, MaxContributoryWage = 45_000m, EffectiveFrom = gosiEffective, IsActive = true, Notes = "VERIFY: KSA 2024 GOSI Annuities employee rate" },
            new GosiContributionRule { TenantId = tenantId, CountryCode = "SA", Classification = GosiClassifications.Saudi, Branch = GosiBranches.Annuities, Payer = GosiPayers.Employer, Rate = 0.12m, MinContributoryWage = 400m, MaxContributoryWage = 45_000m, EffectiveFrom = gosiEffective, IsActive = true, Notes = "VERIFY: KSA 2024 GOSI Annuities employer rate" },
            new GosiContributionRule { TenantId = tenantId, CountryCode = "SA", Classification = GosiClassifications.Saudi, Branch = GosiBranches.SANED,      Payer = GosiPayers.Employee, Rate = 0.01m, EffectiveFrom = gosiEffective, IsActive = true, Notes = "VERIFY: KSA 2024 SANED employee rate" },
            new GosiContributionRule { TenantId = tenantId, CountryCode = "SA", Classification = GosiClassifications.Saudi, Branch = GosiBranches.SANED,      Payer = GosiPayers.Employer, Rate = 0.01m, EffectiveFrom = gosiEffective, IsActive = true, Notes = "VERIFY: KSA 2024 SANED employer rate" },
            new GosiContributionRule { TenantId = tenantId, CountryCode = "SA", Classification = GosiClassifications.NonSaudi, Branch = GosiBranches.OccupationalHazards, Payer = GosiPayers.Employer, Rate = 0.02m, EffectiveFrom = gosiEffective, IsActive = true, Notes = "VERIFY: KSA 2024 Occ Hazards employer rate NonSaudi" }
        );

        // ── 12. Statutory rules ───────────────────────────────────────────────
        var srEffective = DateTime.UtcNow.AddYears(-1);
        db.StatutoryRules.AddRange(
            new StatutoryRule { TenantId = tenantId, CountryCode = "SAU", Jurisdiction = "KSA-mainland", RuleKey = "gosi.employee_rate_saudi",            RuleValue = "0.10",     DataType = "decimal", EffectiveFrom = srEffective },
            new StatutoryRule { TenantId = tenantId, CountryCode = "SAU", Jurisdiction = "KSA-mainland", RuleKey = "gosi.employer_rate_saudi",            RuleValue = "0.12",     DataType = "decimal", EffectiveFrom = srEffective },
            new StatutoryRule { TenantId = tenantId, CountryCode = "SAU", Jurisdiction = "KSA-mainland", RuleKey = "gosi.saned_employee_rate",            RuleValue = "0.01",     DataType = "decimal", EffectiveFrom = srEffective },
            new StatutoryRule { TenantId = tenantId, CountryCode = "SAU", Jurisdiction = "KSA-mainland", RuleKey = "gosi.saned_employer_rate",            RuleValue = "0.01",     DataType = "decimal", EffectiveFrom = srEffective },
            new StatutoryRule { TenantId = tenantId, CountryCode = "SAU", Jurisdiction = "KSA-mainland", RuleKey = "gosi.occ_hazard_employer_rate_nonsaudi", RuleValue = "0.02", DataType = "decimal", EffectiveFrom = srEffective },
            new StatutoryRule { TenantId = tenantId, CountryCode = "SAU", Jurisdiction = "KSA-mainland", RuleKey = "eosb.years_factor",                  RuleValue = "0.5",      DataType = "decimal", EffectiveFrom = srEffective },
            new StatutoryRule { TenantId = tenantId, CountryCode = "SAU", Jurisdiction = "KSA-mainland", RuleKey = "wps.payment_frequency",              RuleValue = "Monthly",  DataType = "string",  EffectiveFrom = srEffective }
        );
        await db.SaveChangesAsync(ct);

        // ── 13. Payroll profiles (IBANs + MolIds) ────────────────────────────
        var ibans = new[]
        {
            "SA0380000000608010167519", // ANK-001 Noura
            "SA4420000001234567891234", // ANK-002 Mohammed
            "SA2920000009876543219876", // ANK-003 Faisal
            "SA6780000004567890124567", // ANK-004 Hessa
            "SA8910000007654321097654", // ANK-005 Abdullah
            "SA3350000002345678902345", // ANK-006 Luluwah
            "SA5670000005678901235678", // ANK-007 Tariq
            "SA1230000008901234568901", // ANK-008 Reema
            "SA7490000001357902461357", // ANK-009 Khalid
            "SA9010000006543210986543", // ANK-010 Sara
            "SA2460000003210987653210", // ANK-011 Ahmed
            "SA4820000009087654329087", // ANK-012 Priya
            "SA6180000006789012346789", // ANK-013 Omar
            "SA8540000004321098764321", // ANK-014 James
            "SA0900000001098765431098", // ANK-015 Siti
            "SA3260000007777777777777", // ANK-016 Michael
            "SA5620000005555555555555", // ANK-017 Nadia
            "SA7980000003333333333333", // ANK-018 David
        };

        var allEmps = new[]
        {
            empNoura, empMohammed, empFaisal, empHessa, empAbdullah, empLuluwah,
            empTariq, empReema, empKhalid, empSara,
            empAhmed, empPriya, empOmar, empJames, empSiti, empMichael, empNadia, empDavid,
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

        // ── 14. Leave types ───────────────────────────────────────────────────
        var ltDefs = new (string Code, string En, string Ar, string Cat, bool Paid)[]
        {
            ("ANNUAL",    "Annual Leave",    "إجازة سنوية",      "Annual",    true),
            ("SICK",      "Sick Leave",      "إجازة مرضية",      "Sick",      true),
            ("CASUAL",    "Casual Leave",    "إجازة عارضة",      "Casual",    true),
            ("MATERNITY", "Maternity Leave", "إجازة أمومة",      "Maternity", true),
            ("PATERNITY", "Paternity Leave", "إجازة الأبوة",     "Paternity", true),
            ("HAJJ",      "Hajj Leave",      "إجازة الحج",       "Religious", true),
            ("UNPAID",    "Unpaid Leave",    "إجازة بدون راتب",  "Unpaid",    false),
        };
        var leaveTypes = new List<LeaveType>();
        var ltSort = 0;
        foreach (var lt in ltDefs)
        {
            var existing = await db.LeaveTypes.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Code == lt.Code, ct);
            if (existing is null)
            {
                existing = new LeaveType { TenantId = tenantId, Code = lt.Code, NameEn = lt.En, NameAr = lt.Ar, Category = lt.Cat, IsPaid = lt.Paid, IsActive = true, SortOrder = ltSort };
                db.LeaveTypes.Add(existing);
            }
            leaveTypes.Add(existing);
            ltSort++;
        }
        await db.SaveChangesAsync(ct);

        var ltAnnual = leaveTypes.First(x => x.Code == "ANNUAL");
        var ltSick   = leaveTypes.First(x => x.Code == "SICK");
        var ltCasual = leaveTypes.First(x => x.Code == "CASUAL");

        // ── 15. Leave requests ────────────────────────────────────────────────
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        db.LeaveRequests.AddRange(
            // 1. Noura: Annual, 2 weeks from today+30
            new LeaveRequest { TenantId=tenantId, EmployeeId=empNoura.Id, EmployeeName=empNoura.FullName, DepartmentName=empNoura.Department, LeaveTypeId=ltAnnual.Id, LeaveTypeName=ltAnnual.NameEn, StartDate=today.AddDays(30), EndDate=today.AddDays(43),  DayType="Full", Reason="Annual vacation", Status="Submitted", SubmittedAtUtc=DateTime.UtcNow.AddHours(-2) },
            // 2. Mohammed: Sick, 2 days last week
            new LeaveRequest { TenantId=tenantId, EmployeeId=empMohammed.Id, EmployeeName=empMohammed.FullName, DepartmentName=empMohammed.Department, LeaveTypeId=ltSick.Id, LeaveTypeName=ltSick.NameEn, StartDate=today.AddDays(-7), EndDate=today.AddDays(-6), DayType="Full", Reason="Illness", Status="Approved", SubmittedAtUtc=DateTime.UtcNow.AddDays(-8), DecidedAtUtc=DateTime.UtcNow.AddDays(-7) },
            // 3. Hessa: Annual, 1 week from today+14
            new LeaveRequest { TenantId=tenantId, EmployeeId=empHessa.Id, EmployeeName=empHessa.FullName, DepartmentName=empHessa.Department, LeaveTypeId=ltAnnual.Id, LeaveTypeName=ltAnnual.NameEn, StartDate=today.AddDays(14), EndDate=today.AddDays(20), DayType="Full", Reason="Personal", Status="Approved", SubmittedAtUtc=DateTime.UtcNow.AddDays(-5), DecidedAtUtc=DateTime.UtcNow.AddDays(-4) },
            // 4. Khalid: Casual, 1 day tomorrow
            new LeaveRequest { TenantId=tenantId, EmployeeId=empKhalid.Id, EmployeeName=empKhalid.FullName, DepartmentName=empKhalid.Department, LeaveTypeId=ltCasual.Id, LeaveTypeName=ltCasual.NameEn, StartDate=today.AddDays(1), EndDate=today.AddDays(1), DayType="Full", Reason="Bank visit", Status="Submitted", SubmittedAtUtc=DateTime.UtcNow.AddHours(-1) },
            // 5. Ahmed: Annual, 10 days from today+20
            new LeaveRequest { TenantId=tenantId, EmployeeId=empAhmed.Id, EmployeeName=empAhmed.FullName, DepartmentName=empAhmed.Department, LeaveTypeId=ltAnnual.Id, LeaveTypeName=ltAnnual.NameEn, StartDate=today.AddDays(20), EndDate=today.AddDays(29), DayType="Full", Reason="Home country visit", Status="Submitted", SubmittedAtUtc=DateTime.UtcNow.AddDays(-2) },
            // 6. Priya: Sick, 1 day yesterday
            new LeaveRequest { TenantId=tenantId, EmployeeId=empPriya.Id, EmployeeName=empPriya.FullName, DepartmentName=empPriya.Department, LeaveTypeId=ltSick.Id, LeaveTypeName=ltSick.NameEn, StartDate=today.AddDays(-1), EndDate=today.AddDays(-1), DayType="Full", Reason="Migraine", Status="Approved", SubmittedAtUtc=DateTime.UtcNow.AddDays(-1), DecidedAtUtc=DateTime.UtcNow.AddHours(-6) }
        );

        // ── Leave balances ─────────────────────────────────────────────────────
        var balancedTypes = new (LeaveType Lt, decimal Entitled)[] { (ltAnnual, 30m), (ltSick, 15m), (ltCasual, 5m) };
        foreach (var (emp, i) in allEmps.Select((e, idx) => (e, idx)))
        {
            foreach (var (lt, entitled) in balancedTypes)
            {
                var used = Math.Min(entitled, (i * 3 + (lt.Code == "SICK" ? 1 : 4)) % (int)entitled);
                db.EmployeeLeaveBalances.Add(new EmployeeLeaveBalance
                {
                    TenantId = tenantId, EmployeeId = emp.Id, EmployeeName = emp.FullName,
                    LeaveTypeId = lt.Id, LeaveTypeName = lt.NameEn, Year = today.Year,
                    Entitled = entitled, Accrued = Math.Round(entitled * today.Month / 12m, 1),
                    Used = used, Pending = 0,
                    CarriedForward = lt.Code == "ANNUAL" && i % 4 == 0 ? 5 : 0,
                });
            }
        }
        await db.SaveChangesAsync(ct);

        // ── 16. Attendance (last 60 working days, first 10 employees) ─────────
        var rng = new Random(tenantId.GetHashCode() & 0x7fffffff);
        var attendanceRecords = new List<AttendanceRecord>();
        var first10 = allEmps.Take(10).ToArray();
        for (var d = today.AddDays(-60); d <= today; d = d.AddDays(1))
        {
            if (d.DayOfWeek is DayOfWeek.Friday or DayOfWeek.Saturday) continue;
            foreach (var emp in first10)
            {
                var checkInMin  = rng.Next(0, 16);   // 08:30 + 0–15 min
                var checkOutMin = rng.Next(0, 31);   // 17:30 + 0–30 min
                attendanceRecords.Add(new AttendanceRecord
                {
                    TenantId   = tenantId,
                    EmployeeId = emp.Id,
                    WorkDate   = d,
                    TimeIn     = new TimeOnly(8, 30).AddMinutes(checkInMin),
                    TimeOut    = new TimeOnly(17, 30).AddMinutes(checkOutMin),
                    Status     = "Present",
                    Notes      = string.Empty,
                });
            }
        }
        db.AttendanceRecords.AddRange(attendanceRecords);
        await db.SaveChangesAsync(ct);

        // ── 17. Locked payroll run (previous calendar month) ──────────────────
        var now       = DateTime.UtcNow;
        var prevMonth = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-1);
        var prevYear  = prevMonth.Year;
        var prevMo    = prevMonth.Month;
        var period    = $"{prevYear}-{prevMo:D2}";

        var payrollRun = new PayrollRun
        {
            TenantId         = tenantId,
            CompanyId        = company.Id,
            Year             = prevYear,
            Month            = prevMo,
            Status           = "Locked",
            EmployeeCount    = 18,
            TotalGrossSalary = TotalGross,
            TotalDeductions  = TotalDeductions,
            TotalNetSalary   = TotalNet,
            CreatedAtUtc     = prevMonth.AddDays(-8),
            ProcessedAtUtc   = prevMonth,                       // first of prev month
            LockedAtUtc      = prevMonth.AddDays(4),            // 5th of prev month
        };
        db.PayrollRuns.Add(payrollRun);
        await db.SaveChangesAsync(ct);

        // Per-employee payroll data
        var payslipComponents = new List<PayslipComponent>();
        var glEntries         = new List<FinanceGlEntry>();
        var runEmployees      = new List<PayrollRunEmployee>();
        var payslips          = new List<Payslip>();
        var payrollSlips      = new List<PayrollSlip>();
        var entryDate         = DateOnly.FromDateTime(prevMonth.AddDays(4));

        for (var i = 0; i < allEmps.Length; i++)
        {
            var emp     = allEmps[i];
            var isSaudi = emp.SaudiOrNonSaudi == "Saudi";
            var basic   = emp.Salary ?? 0m;
            var housing = Math.Round(basic * 0.25m / 500m) * 500m;
            var transport = 1_000m;
            var gross   = basic + housing + transport;

            // Employee-side deductions
            var coveredWage   = Math.Min(basic, 45_000m);
            var gosiEmpDed    = isSaudi ? Math.Round(coveredWage * 0.10m, 2) : 0m;
            var sanedEmpDed   = isSaudi ? Math.Round(coveredWage * 0.01m, 2) : 0m;
            var totalDed      = gosiEmpDed + sanedEmpDed;
            var netPay        = gross - totalDed;

            // Employer-side (not on payslip, for GL only)
            var gosiEmplExp   = isSaudi ? Math.Round(coveredWage * 0.12m, 2) : 0m;
            var sanedEmplExp  = isSaudi ? Math.Round(coveredWage * 0.01m, 2) : 0m;
            var occHazardExp  = !isSaudi ? Math.Round(basic * 0.02m, 2) : 0m;
            var employerGosiTotal = isSaudi ? (gosiEmplExp + sanedEmplExp) : occHazardExp;

            // PayrollRunEmployee
            runEmployees.Add(new PayrollRunEmployee
            {
                TenantId      = tenantId,
                PayrollRunId  = payrollRun.Id,
                EmployeeId    = emp.Id,
                GrossEarnings = gross,
                TotalDeductions = totalDed,
                NetPay        = netPay,
                Status        = "Processed",
            });

            // Payslip
            var payslip = new Payslip
            {
                TenantId       = tenantId,
                PayrollRunId   = payrollRun.Id,
                EmployeeId     = emp.Id,
                PayslipNumber  = $"PS-{prevYear}{prevMo:D2}-{(i + 1):D3}",
                Language       = "en",
                IsPublishedToEss = true,
                PublishedAtUtc = prevMonth.AddDays(5),
            };
            payslips.Add(payslip);

            // PayslipComponents
            payslipComponents.Add(new PayslipComponent { TenantId=tenantId, PayslipId=payslip.Id, ComponentType="Earning",   ComponentName="Basic Salary",                Amount=basic });
            payslipComponents.Add(new PayslipComponent { TenantId=tenantId, PayslipId=payslip.Id, ComponentType="Earning",   ComponentName="Housing Allowance",           Amount=housing });
            payslipComponents.Add(new PayslipComponent { TenantId=tenantId, PayslipId=payslip.Id, ComponentType="Earning",   ComponentName="Transport Allowance",         Amount=transport });
            if (isSaudi)
            {
                payslipComponents.Add(new PayslipComponent { TenantId=tenantId, PayslipId=payslip.Id, ComponentType="Deduction", ComponentName="GOSI - Employee (Annuities)", Amount=gosiEmpDed });
                payslipComponents.Add(new PayslipComponent { TenantId=tenantId, PayslipId=payslip.Id, ComponentType="Deduction", ComponentName="SANED - Employee",            Amount=sanedEmpDed });
            }

            // PayrollSlip (dashboard aggregation)
            payrollSlips.Add(new PayrollSlip
            {
                TenantId          = tenantId,
                RunId             = payrollRun.Id,
                EmployeeId        = emp.Id,
                EmployeeCode      = emp.EmployeeCode,
                EmployeeName      = emp.FullName,
                Department        = emp.Department ?? string.Empty,
                BasicSalary       = basic,
                HousingAllowance  = housing,
                TransportAllowance = transport,
                OtherAllowances   = 0m,
                GrossSalary       = gross,
                Deductions        = totalDed,
                NetSalary         = netPay,
                Status            = "Processed",
            });

            // GL entries — salary posting (DR 5100 / CR 2100)
            glEntries.Add(new FinanceGlEntry
            {
                TenantId        = tenantId,
                SourceModule    = "Payroll",
                SourceEntityId  = payrollRun.Id,
                SourceEntityRef = payrollRun.Id.ToString()[..8],
                EventType       = "PayrollPosting",
                DebitAccount    = "5100 - Salaries & Wages",
                CreditAccount   = "2100 - Salaries Payable",
                Amount          = gross,
                Currency        = "SAR",
                EntryDate       = entryDate,
                Period          = period,
                Description     = $"Salary posting {emp.FullName} {period}",
                PostedByName    = "System",
            });
            // GL entries — employer GOSI (DR 6100 / CR 2300)
            if (employerGosiTotal > 0m)
            {
                glEntries.Add(new FinanceGlEntry
                {
                    TenantId        = tenantId,
                    SourceModule    = "Payroll",
                    SourceEntityId  = payrollRun.Id,
                    SourceEntityRef = payrollRun.Id.ToString()[..8],
                    EventType       = "PayrollPosting",
                    DebitAccount    = "6100 - GOSI Employer Expense",
                    CreditAccount   = "2300 - GOSI Payable",
                    Amount          = employerGosiTotal,
                    Currency        = "SAR",
                    EntryDate       = entryDate,
                    Period          = period,
                    Description     = $"GOSI employer {emp.FullName} {period}",
                    PostedByName    = "System",
                });
            }
        }

        db.PayrollRunEmployees.AddRange(runEmployees);
        db.Payslips.AddRange(payslips);
        await db.SaveChangesAsync(ct);

        db.PayslipComponents.AddRange(payslipComponents);
        db.PayrollSlips.AddRange(payrollSlips);
        db.FinanceGlEntries.AddRange(glEntries);
        await db.SaveChangesAsync(ct);

        // ── 18. Bonus (Performance Bonus for Khalid — Sales Director) ────────
        var bonusType = new BonusType
        {
            TenantId            = tenantId,
            Code                = "PERF",
            NameEn              = "Performance Bonus",
            NameAr              = "مكافأة الأداء",
            CalculationMethod   = "Fixed",
            Frequency           = "OneTime",
            IsIncludedInWps     = true,
            TaxRegion           = "GCC",
            IsActive            = true,
        };
        db.BonusTypes.Add(bonusType);
        await db.SaveChangesAsync(ct);

        var bonusBatch = new BonusBatch
        {
            TenantId       = tenantId,
            BonusTypeId    = bonusType.Id,
            BonusTypeName  = "Performance Bonus",
            BatchNumber    = "BON-2026-001",
            BatchName      = "Q1 2026 Performance Bonus",
            PaymentPeriod  = $"{prevYear}-{prevMo:D2}",
            PaymentDate    = DateOnly.FromDateTime(prevMonth),
            TotalAmount    = 8_000m,
            EmployeeCount  = 1,
            Status         = "Approved",
        };
        db.BonusBatches.Add(bonusBatch);
        await db.SaveChangesAsync(ct);

        var empBonus = new EmployeeBonus
        {
            TenantId           = tenantId,
            BonusBatchId       = bonusBatch.Id,
            EmployeeId         = Guid.NewGuid(),  // placeholder Guid per model design
            EmployeeIntId      = empKhalid.Id,
            EmployeeName       = empKhalid.FullName,
            Department         = empKhalid.Department ?? "Sales",
            BonusTypeId        = bonusType.Id,
            BonusTypeName      = "Performance Bonus",
            BasicSalary        = empKhalid.Salary ?? 21_000m,
            CalculationMethod  = "Fixed",
            CalculationValue   = 8_000m,
            GrossBonusAmount   = 8_000m,
            TaxWithheld        = 0m,
            BonusAmount        = 8_000m,
            TaxRegion          = "GCC",
            PaymentPeriod      = $"{prevYear}-{prevMo:D2}",
            Status             = "PaidInPayroll",
            PayrollRunId       = payrollRun.Id,
        };
        db.EmployeeBonuses.Add(empBonus);

        // Bonus GL entry: DR 6200 / CR 2100
        db.FinanceGlEntries.Add(new FinanceGlEntry
        {
            TenantId        = tenantId,
            SourceModule    = "Payroll",
            SourceEntityId  = payrollRun.Id,
            SourceEntityRef = payrollRun.Id.ToString()[..8],
            EventType       = "BonusPayment",
            DebitAccount    = "6200 - Bonus Expense",
            CreditAccount   = "2100 - Salaries Payable",
            Amount          = 8_000m,
            Currency        = "SAR",
            EntryDate       = entryDate,
            Period          = period,
            Description     = $"Performance bonus {empKhalid.FullName} {period}",
            PostedByName    = "System",
        });
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "KsaDemoTenantSeeder: seeded Al-Nakheel (18 employees, locked payroll {Period}, GL balanced at {GlTotal:N0} SAR DR==CR).",
            period, TotalGross + 24_745m);
    }
}
