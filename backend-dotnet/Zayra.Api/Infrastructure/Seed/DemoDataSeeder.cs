using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Seed;

/// <summary>
/// Seeds two demo tenants (IntelliFlow Systems and Evostel LLC) on startup.
/// Fully idempotent — the outer tenant guard skips the whole block when the slug exists.
/// Each tenant gets a complete set of named demo users:
///   admin, hrdirector, hrmanager, finance, manager, supervisor, employee1, employee2, auditor
/// All passwords are Demo@1234.
/// </summary>
public static class DemoDataSeeder
{
    private const string DemoPassword = "Demo@1234";

    public static async Task SeedAsync(
        ZayraDbContext db,
        IPasswordHasher hasher,
        IAuthSeeder authSeeder,
        ILogger logger,
        CancellationToken ct = default)
    {
        await SeedPlatformOwnerAsync(db, hasher, logger, ct);
        await SeedPricingConfigAsync(db, logger, ct);
        await DeactivateDemoTenantsAsync(db, logger, ct);

        await SeedTenantAsync(db, hasher, authSeeder, logger, ct, new DemoTenantSpec
        {
            Name         = "IntelliFlow Systems",
            Slug         = "intelliflow",
            Plan         = "Enterprise",
            Status       = "Active",
            MaxEmployees = 500,
            MaxUsers     = 100,
            MonthlyAmount = 2500m,
            CurrencyCode  = "USD",
            BillingEmail  = "billing@intelliflow.com",
            ExpiresAtUtc  = null,
            SeedOrgStructure = true,
            Features = new Dictionary<string, bool>
            {
                [FeatureKeys.Recruitment]     = true,
                [FeatureKeys.Performance]     = true,
                [FeatureKeys.Compliance]      = true,
                [FeatureKeys.AiAssistant]     = true,
                [FeatureKeys.Finance]         = true,
                [FeatureKeys.Payroll]         = true,
                [FeatureKeys.Shifts]          = true,
                [FeatureKeys.Overtime]        = true,
                [FeatureKeys.MobileApp]       = true,
                [FeatureKeys.WpsExport]       = true,
                [FeatureKeys.QiwaIntegration] = true,
            },
            Users = new[]
            {
                new DemoUserSpec("Admin",           "IntelliFlow Administrator", "admin@intelliflow.com"),
                new DemoUserSpec("HR Director",     "Sarah Mitchell",            "hrdirector@intelliflow.com"),
                new DemoUserSpec("HR Manager",      "Omar Al-Farsi",             "hrmanager@intelliflow.com"),
                new DemoUserSpec("Finance Approver","Chen Wei",                  "finance@intelliflow.com"),
                new DemoUserSpec("Manager",         "Priya Sharma",              "manager@intelliflow.com"),
                new DemoUserSpec("Supervisor",      "Khalid Al-Rashid",          "supervisor@intelliflow.com"),
                new DemoUserSpec("Employee",        "Fatima Al-Zahra",           "employee1@intelliflow.com"),
                new DemoUserSpec("Employee",        "James O'Brien",             "employee2@intelliflow.com"),
                new DemoUserSpec("Auditor",         "Maya Johnson",              "auditor@intelliflow.com"),
            }
        });

        await KsaDemoTenantSeeder.SeedAsync(db, hasher, authSeeder, logger, ct);

        await SeedTenantAsync(db, hasher, authSeeder, logger, ct, new DemoTenantSpec
        {
            Name         = "Evostel LLC",
            Slug         = "evostel",
            Plan         = "Starter",
            Status       = "PastDue",
            MaxEmployees = 50,
            MaxUsers     = 10,
            MonthlyAmount = 299m,
            CurrencyCode  = "USD",
            BillingEmail  = "billing@evostel.com",
            ExpiresAtUtc  = DateTime.UtcNow.AddDays(7),
            SeedOrgStructure = true,
            Features = new Dictionary<string, bool>
            {
                [FeatureKeys.Recruitment]     = false,
                [FeatureKeys.Performance]     = false,
                [FeatureKeys.Compliance]      = false,
                [FeatureKeys.AiAssistant]     = false,
                [FeatureKeys.Finance]         = true,
                [FeatureKeys.Payroll]         = true,
                [FeatureKeys.Shifts]          = false,
                [FeatureKeys.Overtime]        = false,
                [FeatureKeys.MobileApp]       = false,
                [FeatureKeys.WpsExport]       = false,
                [FeatureKeys.QiwaIntegration] = false,
            },
            Users = new[]
            {
                new DemoUserSpec("Admin",           "Evostel Administrator",   "admin@evostel.com"),
                new DemoUserSpec("HR Manager",      "Dana Wilkins",            "hrmanager@evostel.com"),
                new DemoUserSpec("Finance Approver","Tom Reyes",               "finance@evostel.com"),
                new DemoUserSpec("Manager",         "Lena Müller",             "manager@evostel.com"),
                new DemoUserSpec("Supervisor",      "Raj Patel",               "supervisor@evostel.com"),
                new DemoUserSpec("Employee",        "Nina Costa",              "employee1@evostel.com"),
                new DemoUserSpec("Employee",        "David Kim",               "employee2@evostel.com"),
                new DemoUserSpec("Auditor",         "Ama Owusu",               "auditor@evostel.com"),
            }
        });
    }

    // ── Platform owner ────────────────────────────────────────────────────────

    private static async Task SeedPlatformOwnerAsync(
        ZayraDbContext db,
        IPasswordHasher hasher,
        ILogger logger,
        CancellationToken ct)
    {
        if (await db.PlatformUsers.AnyAsync(ct))
            return;

        var email    = Environment.GetEnvironmentVariable("PLATFORM_ADMIN_EMAIL") ?? "platform@kynex.one";
        var password = Environment.GetEnvironmentVariable("PLATFORM_ADMIN_PASSWORD");

        if (string.IsNullOrWhiteSpace(password))
        {
            logger.LogWarning("DemoDataSeeder: PLATFORM_ADMIN_PASSWORD not set — skipping platform owner seed.");
            return;
        }

        db.PlatformUsers.Add(new PlatformUser
        {
            Email        = email.Trim().ToLowerInvariant(),
            FullName     = "Platform Owner",
            PasswordHash = hasher.Hash(password),
            Role         = PlatformRoles.Owner,
            IsActive     = true,
            CreatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync(ct);
        logger.LogInformation("DemoDataSeeder: seeded platform owner {Email}", email);
    }

    // ── Tenant helper ─────────────────────────────────────────────────────────

    private static async Task SeedTenantAsync(
        ZayraDbContext db,
        IPasswordHasher hasher,
        IAuthSeeder authSeeder,
        ILogger logger,
        CancellationToken ct,
        DemoTenantSpec spec)
    {
        if (await db.Tenants.AsNoTracking().AnyAsync(t => t.Slug == spec.Slug, ct))
        {
            var existingTenant = await db.Tenants.FirstAsync(t => t.Slug == spec.Slug, ct);

            // If a platform admin soft-deleted this demo tenant, respect that decision —
            // do NOT restore it. Skip all idempotent seeder work for deactivated tenants.
            if (!existingTenant.IsActive)
            {
                logger.LogInformation("DemoDataSeeder: tenant '{Slug}' is deactivated — skipping re-seed.", spec.Slug);
                return;
            }

            // Ensure any new permissions added since initial seed are propagated to existing roles.
            await authSeeder.EnsureTenantRolesAsync(existingTenant.Id, ct);

            // Update subscription if status/plan drifted (only for active tenants)
            var existingSub = await db.TenantSubscriptions.FirstOrDefaultAsync(s => s.TenantId == existingTenant.Id, ct);
            if (existingSub is not null && existingSub.Status != "Cancelled"
                && (existingSub.Status != spec.Status || existingSub.Plan != spec.Plan))
            {
                existingSub.Status = spec.Status;
                existingSub.Plan   = spec.Plan;
                existingSub.ExpiresAtUtc = spec.ExpiresAtUtc;
                await db.SaveChangesAsync(ct);
                logger.LogInformation("DemoDataSeeder: updated subscription for '{Slug}' to {Plan}/{Status}.", spec.Slug, spec.Plan, spec.Status);
            }

            // Add any missing demo users (idempotent: skip emails that already exist in this tenant)
            var existingEmailList = await db.Users
                .AsNoTracking()
                .Where(u => u.TenantId == existingTenant.Id)
                .Select(u => u.NormalizedEmail)
                .ToListAsync(ct);
            var existingEmails = new HashSet<string>(existingEmailList, StringComparer.OrdinalIgnoreCase);

            var existingRoleMap = await db.Roles.AsNoTracking()
                .Where(r => r.TenantId == existingTenant.Id)
                .ToDictionaryAsync(r => r.Name, StringComparer.OrdinalIgnoreCase, ct);

            bool addedUsers = false;
            foreach (var userSpec in spec.Users)
            {
                var normalized = AuthService.Normalize(userSpec.Email);
                if (existingEmails.Contains(normalized)) continue;
                if (!existingRoleMap.TryGetValue(userSpec.RoleName, out var role))
                {
                    logger.LogWarning("DemoDataSeeder: role '{Role}' not found for tenant '{Slug}' — skipping user {Email}.", userSpec.RoleName, spec.Slug, userSpec.Email);
                    continue;
                }
                var user = new User
                {
                    TenantId         = existingTenant.Id,
                    Email            = userSpec.Email.Trim().ToLowerInvariant(),
                    NormalizedEmail  = normalized,
                    FullName         = userSpec.FullName,
                    PasswordHash     = hasher.Hash(DemoPassword),
                    AccessMode       = "FullPortal",
                    Status           = "Active",
                    IsActive         = true,
                    IsEmailConfirmed = true,
                };
                user.UserRoles.Add(new UserRole { User = user, RoleId = role.Id });
                db.Users.Add(user);
                addedUsers = true;
                logger.LogInformation("DemoDataSeeder: added missing user {Email} to tenant '{Slug}'.", userSpec.Email, spec.Slug);
            }
            if (addedUsers) await db.SaveChangesAsync(ct);

            // Backfill operational data (leave types, balances, payroll, attendance)
            // for existing tenants that were seeded before this data was added.
            if (spec.SeedOrgStructure)
                await SeedOperationalDataAsync(db, existingTenant.Id, logger, ct);

            return;
        }

        // 1. Create tenant
        var tenant = new Tenant { Name = spec.Name, Slug = spec.Slug, IsActive = true };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync(ct);

        // 2. Seed full RBAC (creates all roles + permissions)
        await authSeeder.EnsureTenantRolesAsync(tenant.Id, ct);

        // 3. Load roles for assignment
        var roleMap = await db.Roles.AsNoTracking()
            .Where(r => r.TenantId == tenant.Id)
            .ToDictionaryAsync(r => r.Name, StringComparer.OrdinalIgnoreCase, ct);

        // 4. Create users
        foreach (var userSpec in spec.Users)
        {
            if (!roleMap.TryGetValue(userSpec.RoleName, out var role))
            {
                logger.LogWarning("DemoDataSeeder: role '{Role}' not found for tenant '{Slug}' — skipping user {Email}.",
                    userSpec.RoleName, spec.Slug, userSpec.Email);
                continue;
            }

            var user = new User
            {
                TenantId         = tenant.Id,
                Email            = userSpec.Email.Trim().ToLowerInvariant(),
                NormalizedEmail  = AuthService.Normalize(userSpec.Email),
                FullName         = userSpec.FullName,
                PasswordHash     = hasher.Hash(DemoPassword),
                AccessMode       = "FullPortal",
                Status           = "Active",
                IsActive         = true,
                IsEmailConfirmed = true,
            };
            user.UserRoles.Add(new UserRole { User = user, RoleId = role.Id });
            db.Users.Add(user);
        }

        // 5. Subscription
        db.TenantSubscriptions.Add(new TenantSubscription
        {
            TenantId     = tenant.Id,
            Plan         = spec.Plan,
            Status       = spec.Status,
            MaxEmployees = spec.MaxEmployees,
            MaxUsers     = spec.MaxUsers,
            BillingEmail = spec.BillingEmail,
            BillingCycle = "Monthly",
            MonthlyAmount = spec.MonthlyAmount,
            CurrencyCode  = spec.CurrencyCode,
            ExpiresAtUtc  = spec.ExpiresAtUtc,
        });

        // 6. Feature flags
        foreach (var (key, enabled) in spec.Features)
        {
            db.TenantFeatureFlags.Add(new TenantFeatureFlag
            {
                TenantId    = tenant.Id,
                FeatureKey  = key,
                IsEnabled   = enabled,
                UpdatedAtUtc = DateTime.UtcNow,
            });
        }

        await db.SaveChangesAsync(ct);

        // 7. Seed org structure (company, grades, departments, designations, employees)
        if (spec.SeedOrgStructure)
            await SeedOrgStructureAsync(db, tenant.Id, spec.Slug, logger, ct);

        logger.LogInformation(
            "DemoDataSeeder: seeded tenant '{Slug}' ({Plan}/{Status}) with {Count} users.",
            spec.Slug, spec.Plan, spec.Status, spec.Users.Length);
    }

    // ── Org structure seed ────────────────────────────────────────────────────

    private static async Task SeedOrgStructureAsync(
        ZayraDbContext db,
        Guid tenantId,
        string slug,
        ILogger logger,
        CancellationToken ct)
    {
        // Idempotent: skip if employees already exist
        if (await db.Employees.AnyAsync(e => e.TenantId == tenantId, ct))
            return;

        // ── Company ─────────────────────────────────────────────────────────
        // IntelliFlow → KSA (SAU / KSA-mainland)
        // Evostel     → UAE-DIFC (ARE / UAE-DIFC) — demonstrates jurisdiction fallback
        bool isKsa = slug == "intelliflow";
        var company = new Company
        {
            TenantId    = tenantId,
            LegalNameEn = isKsa ? "IntelliFlow Systems Ltd"      : "Evostel DIFC Ltd",
            LegalNameAr = isKsa ? "إنتلي فلو سيستمز"            : "إيفوستيل دايف سنتر",
            TradeName   = isKsa ? "IntelliFlow"                   : "Evostel",
            CountryCode = isKsa ? "SAU"                           : "ARE",
            Jurisdiction = isKsa ? "KSA-mainland"                 : "UAE-DIFC",
            RegistrationNumber = isKsa ? "IFL-2019-001"           : "EVS-2021-001",
            DefaultCurrency    = isKsa ? "SAR"                    : "AED",
            IsActive = true,
        };
        db.Companies.Add(company);
        await db.SaveChangesAsync(ct);

        var branch = new Branch
        {
            TenantId    = tenantId,
            CompanyId   = company.Id,
            Code        = "HQ",
            NameEn      = "Head Office",
            NameAr      = "المقر الرئيسي",
            CountryCode = isKsa ? "SAU" : "ARE",
            City        = isKsa ? "Riyadh" : "Dubai",
            TimeZoneId  = isKsa ? "Arab Standard Time" : "Arabian Standard Time",
            IsHeadOffice = true,
            IsActive     = true,
        };
        db.Branches.Add(branch);
        await db.SaveChangesAsync(ct);

        if (slug != "intelliflow")
        {
            // Evostel: minimal org structure only
            await SeedMinimalOrgAsync(db, tenantId, company, branch, ct);
            await SeedOperationalDataAsync(db, tenantId, logger, ct);
            logger.LogInformation("DemoDataSeeder: seeded minimal org for '{Slug}'.", slug);
            return;
        }

        // ── Grades (IntelliFlow) ─────────────────────────────────────────────
        var grades = new[]
        {
            new Grade { TenantId = tenantId, Code = "L1", Name = "Level 1 – Associate",      Band = "Individual Contributor", Level = 1 },
            new Grade { TenantId = tenantId, Code = "L2", Name = "Level 2 – Professional",   Band = "Individual Contributor", Level = 2 },
            new Grade { TenantId = tenantId, Code = "L3", Name = "Level 3 – Senior",         Band = "Individual Contributor", Level = 3 },
            new Grade { TenantId = tenantId, Code = "L4", Name = "Level 4 – Lead",           Band = "Management",            Level = 4 },
            new Grade { TenantId = tenantId, Code = "L5", Name = "Level 5 – Manager",        Band = "Management",            Level = 5 },
            new Grade { TenantId = tenantId, Code = "L6", Name = "Level 6 – Director",       Band = "Executive",             Level = 6 },
            new Grade { TenantId = tenantId, Code = "L7", Name = "Level 7 – VP / C-Suite",   Band = "Executive",             Level = 7 },
        };
        db.Grades.AddRange(grades);
        await db.SaveChangesAsync(ct);
        var gradeMap = grades.ToDictionary(g => g.Code);

        // ── Departments (IntelliFlow) ────────────────────────────────────────
        var deptExec = new Department { TenantId = tenantId, BranchId = branch.Id, Code = "EXEC",     NameEn = "Executive",                     SortOrder = 0 };
        var deptHR   = new Department { TenantId = tenantId, BranchId = branch.Id, Code = "HR",       NameEn = "Human Resources",               SortOrder = 1 };
        var deptEng  = new Department { TenantId = tenantId, BranchId = branch.Id, Code = "ENG",      NameEn = "Engineering",                   SortOrder = 2 };
        var deptFin  = new Department { TenantId = tenantId, BranchId = branch.Id, Code = "FIN",      NameEn = "Finance",                       SortOrder = 3 };
        var deptSales= new Department { TenantId = tenantId, BranchId = branch.Id, Code = "SALES",    NameEn = "Sales & Business Development",  SortOrder = 4 };
        var deptOps  = new Department { TenantId = tenantId, BranchId = branch.Id, Code = "OPS",      NameEn = "Operations",                    SortOrder = 5 };
        db.Departments.AddRange(deptExec, deptHR, deptEng, deptFin, deptSales, deptOps);
        await db.SaveChangesAsync(ct);

        // Sub-departments
        var deptEngFE = new Department { TenantId = tenantId, BranchId = branch.Id, Code = "ENG-FE", NameEn = "Frontend Engineering", ParentDepartmentId = deptEng.Id, SortOrder = 0 };
        var deptEngBE = new Department { TenantId = tenantId, BranchId = branch.Id, Code = "ENG-BE", NameEn = "Backend Engineering",  ParentDepartmentId = deptEng.Id, SortOrder = 1 };
        db.Departments.AddRange(deptEngFE, deptEngBE);
        await db.SaveChangesAsync(ct);

        // ── Designations (IntelliFlow) ───────────────────────────────────────
        var desigCTO     = new Designation { TenantId = tenantId, Code = "CTO",    TitleEn = "Chief Technology Officer",   GradeId = gradeMap["L7"].Id, IsManagerRole = true,  LevelRank = 70, IsSystemDefault = true };
        var desigVPEng   = new Designation { TenantId = tenantId, Code = "VPENG",  TitleEn = "VP Engineering",             GradeId = gradeMap["L6"].Id, IsManagerRole = true,  LevelRank = 60 };
        var desigDirHR   = new Designation { TenantId = tenantId, Code = "DIRHR",  TitleEn = "HR Director",                GradeId = gradeMap["L6"].Id, IsManagerRole = true,  LevelRank = 60, DepartmentId = deptHR.Id };
        var desigHRMgr   = new Designation { TenantId = tenantId, Code = "HRMGR",  TitleEn = "HR Manager",                 GradeId = gradeMap["L5"].Id, IsManagerRole = true,  LevelRank = 50, DepartmentId = deptHR.Id };
        var desigHROff   = new Designation { TenantId = tenantId, Code = "HROFF",  TitleEn = "HR Officer",                 GradeId = gradeMap["L3"].Id, IsManagerRole = false, LevelRank = 30, DepartmentId = deptHR.Id };
        var desigEngMgr  = new Designation { TenantId = tenantId, Code = "ENGMGR", TitleEn = "Engineering Manager",        GradeId = gradeMap["L5"].Id, IsManagerRole = true,  LevelRank = 50, DepartmentId = deptEng.Id };
        var desigSrEng   = new Designation { TenantId = tenantId, Code = "SRENG",  TitleEn = "Senior Software Engineer",   GradeId = gradeMap["L3"].Id, IsManagerRole = false, LevelRank = 30, DepartmentId = deptEng.Id };
        var desigEng     = new Designation { TenantId = tenantId, Code = "ENG",    TitleEn = "Software Engineer",          GradeId = gradeMap["L2"].Id, IsManagerRole = false, LevelRank = 20, DepartmentId = deptEng.Id };
        var desigFinDir  = new Designation { TenantId = tenantId, Code = "FINDIR", TitleEn = "Finance Director",           GradeId = gradeMap["L6"].Id, IsManagerRole = true,  LevelRank = 60, DepartmentId = deptFin.Id };
        var desigFinAcc  = new Designation { TenantId = tenantId, Code = "FINACC", TitleEn = "Finance Accountant",         GradeId = gradeMap["L2"].Id, IsManagerRole = false, LevelRank = 20, DepartmentId = deptFin.Id };
        var desigSalesMgr= new Designation { TenantId = tenantId, Code = "SALESMGR",TitleEn = "Sales Manager",            GradeId = gradeMap["L5"].Id, IsManagerRole = true,  LevelRank = 50, DepartmentId = deptSales.Id };
        var desigSalesExec=new Designation { TenantId = tenantId, Code = "SALESEXC",TitleEn = "Sales Executive",          GradeId = gradeMap["L2"].Id, IsManagerRole = false, LevelRank = 20, DepartmentId = deptSales.Id };
        var desigOpsMgr  = new Designation { TenantId = tenantId, Code = "OPSMGR", TitleEn = "Operations Manager",        GradeId = gradeMap["L5"].Id, IsManagerRole = true,  LevelRank = 50, DepartmentId = deptOps.Id };
        db.Designations.AddRange(desigCTO, desigVPEng, desigDirHR, desigHRMgr, desigHROff,
            desigEngMgr, desigSrEng, desigEng, desigFinDir, desigFinAcc,
            desigSalesMgr, desigSalesExec, desigOpsMgr);
        await db.SaveChangesAsync(ct);

        // ── Employees — Level 7 (C-Suite) ───────────────────────────────────
        var cto = AddEmployee(db, tenantId, company, branch, "IFL-001", "Arjun Sharma",       deptExec, desigCTO,    gradeMap["L7"], "arjun.sharma@intelliflow.com",   "1980-03-15");

        await db.SaveChangesAsync(ct);

        // ── Level 6 (Directors) ──────────────────────────────────────────────
        var vpEng   = AddEmployee(db, tenantId, company, branch, "IFL-002", "Sarah Mitchell",  deptEng,  desigVPEng,  gradeMap["L6"], "sarah.mitchell@intelliflow.com",  "1985-07-22", cto.Id);
        var dirHR   = AddEmployee(db, tenantId, company, branch, "IFL-003", "Omar Al-Farsi",   deptHR,   desigDirHR,  gradeMap["L6"], "omar.alfarsi@intelliflow.com",    "1983-11-05", cto.Id);
        var finDir  = AddEmployee(db, tenantId, company, branch, "IFL-004", "Chen Wei",        deptFin,  desigFinDir, gradeMap["L6"], "chen.wei@intelliflow.com",        "1979-04-30", cto.Id);
        await db.SaveChangesAsync(ct);

        // Department heads
        deptEng.ManagerEmployeeId  = vpEng.Id;
        deptHR.ManagerEmployeeId   = dirHR.Id;
        deptFin.ManagerEmployeeId  = finDir.Id;

        // ── Level 5 (Managers) ───────────────────────────────────────────────
        var hrMgr    = AddEmployee(db, tenantId, company, branch, "IFL-005", "Priya Sharma",   deptHR,   desigHRMgr,  gradeMap["L5"], "priya.sharma@intelliflow.com",    "1990-02-14", dirHR.Id);
        var engMgr   = AddEmployee(db, tenantId, company, branch, "IFL-006", "Khalid Al-Rashid",deptEng, desigEngMgr, gradeMap["L5"], "khalid.rashid@intelliflow.com",   "1988-09-18", vpEng.Id);
        var salesMgr = AddEmployee(db, tenantId, company, branch, "IFL-007", "Lena Mueller",   deptSales,desigSalesMgr,gradeMap["L5"],"lena.mueller@intelliflow.com",    "1986-06-01", cto.Id);
        var opsMgr   = AddEmployee(db, tenantId, company, branch, "IFL-008", "Raj Patel",      deptOps,  desigOpsMgr, gradeMap["L5"], "raj.patel@intelliflow.com",       "1987-12-20", cto.Id);
        await db.SaveChangesAsync(ct);

        deptSales.ManagerEmployeeId = salesMgr.Id;
        deptOps.ManagerEmployeeId   = opsMgr.Id;

        // ── Level 3 (Seniors) ────────────────────────────────────────────────
        var hrOff    = AddEmployee(db, tenantId, company, branch, "IFL-009", "Fatima Al-Zahra",deptHR,   desigHROff, gradeMap["L3"], "fatima.alzahra@intelliflow.com",  "1994-08-10", hrMgr.Id);
        var srEng1   = AddEmployee(db, tenantId, company, branch, "IFL-010", "James O'Brien",  deptEngBE,desigSrEng, gradeMap["L3"], "james.obrien@intelliflow.com",    "1992-01-25", engMgr.Id);
        var srEng2   = AddEmployee(db, tenantId, company, branch, "IFL-011", "Maya Johnson",   deptEngFE,desigSrEng, gradeMap["L3"], "maya.johnson@intelliflow.com",    "1993-05-03", engMgr.Id);
        await db.SaveChangesAsync(ct);

        // ── Level 2 (Individual Contributors) ───────────────────────────────
        var eng1     = AddEmployee(db, tenantId, company, branch, "IFL-012", "Yuki Tanaka",    deptEngBE,desigEng,   gradeMap["L2"], "yuki.tanaka@intelliflow.com",     "1997-03-11", srEng1.Id);
        var eng2     = AddEmployee(db, tenantId, company, branch, "IFL-013", "Dana Wilkins",   deptEngFE,desigEng,   gradeMap["L2"], "dana.wilkins@intelliflow.com",    "1998-07-29", srEng2.Id);
        var salesExec= AddEmployee(db, tenantId, company, branch, "IFL-014", "Nina Costa",     deptSales,desigSalesExec,gradeMap["L2"],"nina.costa@intelliflow.com",    "1996-11-15", salesMgr.Id);
        var finAcc   = AddEmployee(db, tenantId, company, branch, "IFL-015", "David Kim",      deptFin,  desigFinAcc, gradeMap["L2"], "david.kim@intelliflow.com",       "1995-04-22", finDir.Id);
        await db.SaveChangesAsync(ct);

        // Update department head on sub-departments
        deptEngBE.ManagerEmployeeId = srEng1.Id;
        deptEngFE.ManagerEmployeeId = srEng2.Id;
        await db.SaveChangesAsync(ct);

        // ── ReportingLines ───────────────────────────────────────────────────
        var hierarchyPairs = new (Employee Emp, Employee Mgr)[]
        {
            (vpEng,    cto),
            (dirHR,    cto),
            (finDir,   cto),
            (salesMgr, cto),
            (opsMgr,   cto),
            (hrMgr,    dirHR),
            (engMgr,   vpEng),
            (hrOff,    hrMgr),
            (srEng1,   engMgr),
            (srEng2,   engMgr),
            (eng1,     srEng1),
            (eng2,     srEng2),
            (salesExec,salesMgr),
            (finAcc,   finDir),
        };
        foreach (var (emp, mgr) in hierarchyPairs)
        {
            db.ReportingLines.Add(new ReportingLine
            {
                TenantId = tenantId,
                EmployeeId = emp.Id,
                ManagerEmployeeId = mgr.Id,
                RelationshipType = "SolidLine",
                EffectiveFrom = emp.JoiningDate,
                IsPrimary = true,
                IsActive = true,
            });
        }
        // Add a dotted-line: HR Officer also reports to VP Eng for engineering HR matters
        db.ReportingLines.Add(new ReportingLine
        {
            TenantId = tenantId,
            EmployeeId = hrOff.Id,
            ManagerEmployeeId = vpEng.Id,
            RelationshipType = "DottedLine",
            EffectiveFrom = hrOff.JoiningDate,
            IsPrimary = false,
            IsActive = true,
        });
        await db.SaveChangesAsync(ct);

        // ── Approval policies (Leave: Manager → HR Director) ─────────────────
        var leavePolicy = new ApprovalPolicy
        {
            TenantId = tenantId,
            WorkflowType = "Leave",
            Name = "Standard Leave Approval",
            IsDefault = true,
            IsActive = true,
        };
        db.ApprovalPolicies.Add(leavePolicy);
        await db.SaveChangesAsync(ct);
        db.ApprovalPolicySteps.AddRange(
            new ApprovalPolicyStep { TenantId = tenantId, PolicyId = leavePolicy.Id, StepOrder = 1, StepName = "Manager Approval", ApproverType = "Manager", IsFinalStep = false },
            new ApprovalPolicyStep { TenantId = tenantId, PolicyId = leavePolicy.Id, StepOrder = 2, StepName = "HR Director Sign-Off", ApproverType = "HR", IsFinalStep = true }
        );
        await db.SaveChangesAsync(ct);

        // ── QA scenario employees: probation, contract, Saudi, terminated ────────
        // Adds diversity of statuses and nationalities needed for real workflow testing.

        // 16 — Saudi engineer on probation (Leave/Overtime approval flows differently during probation)
        var saudiEng = new Employee
        {
            TenantId = tenantId, CompanyId = company.Id, BranchId = branch.Id,
            EmployeeCode = "IFL-016", FullName = "Abdullah Al-Otaibi",
            WorkEmail = "abdullah.alotaibi@intelliflow.com",
            DepartmentId = deptEngBE.Id, Department = deptEngBE.NameEn,
            DesignationId = desigEng.Id, Designation = desigEng.TitleEn,
            GradeId = gradeMap["L2"].Id, Grade = gradeMap["L2"].Code,
            JobTitle = desigEng.TitleEn, ManagerEmployeeId = srEng1.Id,
            Status = "Probation", JoiningDate = DateTime.UtcNow.AddDays(-60),
            EmploymentType = "Full-time", ContractType = "Unlimited",
            Nationality = "Saudi", IqamaNumber = string.Empty, // local national
        };
        db.Employees.Add(saudiEng);

        // 17 — Contract employee (fixed-term, Sales dept)
        var contractEmp = new Employee
        {
            TenantId = tenantId, CompanyId = company.Id, BranchId = branch.Id,
            EmployeeCode = "IFL-017", FullName = "Marco Rossi",
            WorkEmail = "marco.rossi@intelliflow.com",
            DepartmentId = deptSales.Id, Department = deptSales.NameEn,
            DesignationId = desigSalesExec.Id, Designation = desigSalesExec.TitleEn,
            GradeId = gradeMap["L2"].Id, Grade = gradeMap["L2"].Code,
            JobTitle = desigSalesExec.TitleEn, ManagerEmployeeId = salesMgr.Id,
            Status = "Active", JoiningDate = DateTime.UtcNow.AddDays(-180),
            EmploymentType = "Contract", ContractType = "Fixed-term",
            Nationality = "Italian",
        };
        db.Employees.Add(contractEmp);

        // 18 — Resigned employee (no longer in active headcount, but workflows may reference history)
        var resignedEmp = new Employee
        {
            TenantId = tenantId, CompanyId = company.Id, BranchId = branch.Id,
            EmployeeCode = "IFL-018", FullName = "Sophie Laurent",
            WorkEmail = "sophie.laurent@intelliflow.com",
            DepartmentId = deptFin.Id, Department = deptFin.NameEn,
            DesignationId = desigFinAcc.Id, Designation = desigFinAcc.TitleEn,
            GradeId = gradeMap["L2"].Id, Grade = gradeMap["L2"].Code,
            JobTitle = desigFinAcc.TitleEn, ManagerEmployeeId = finDir.Id,
            Status = "Resigned", JoiningDate = DateTime.UtcNow.AddDays(-730),
            EmploymentType = "Full-time", ContractType = "Unlimited",
            Nationality = "French",
        };
        db.Employees.Add(resignedEmp);

        // 19 — Saudi female HR officer on confirmed status (for GOSI/Qiwa readiness scenarios)
        var saudiHR = new Employee
        {
            TenantId = tenantId, CompanyId = company.Id, BranchId = branch.Id,
            EmployeeCode = "IFL-019", FullName = "Noura Al-Ghamdi",
            WorkEmail = "noura.alghamdi@intelliflow.com",
            DepartmentId = deptHR.Id, Department = deptHR.NameEn,
            DesignationId = desigHROff.Id, Designation = desigHROff.TitleEn,
            GradeId = gradeMap["L3"].Id, Grade = gradeMap["L3"].Code,
            JobTitle = desigHROff.TitleEn, ManagerEmployeeId = hrMgr.Id,
            Status = "Confirmed", JoiningDate = DateTime.UtcNow.AddDays(-400),
            EmploymentType = "Full-time", ContractType = "Unlimited",
            Nationality = "Saudi", IqamaNumber = string.Empty, Gender = "Female",
        };
        db.Employees.Add(saudiHR);

        await db.SaveChangesAsync(ct);

        // Overtime approval policy (all departments, all grades — default)
        var overtimePolicy = new ApprovalPolicy
        {
            TenantId = tenantId,
            WorkflowType = "Overtime",
            Name = "Standard Overtime Approval",
            IsDefault = true,
            IsActive = true,
        };
        db.ApprovalPolicies.Add(overtimePolicy);
        await db.SaveChangesAsync(ct);
        db.ApprovalPolicySteps.AddRange(
            new ApprovalPolicyStep { TenantId = tenantId, PolicyId = overtimePolicy.Id, StepOrder = 1, StepName = "Manager Approval", ApproverType = "Manager", IsFinalStep = true }
        );
        await db.SaveChangesAsync(ct);

        await SeedOperationalDataAsync(db, tenantId, logger, ct);
        logger.LogInformation("DemoDataSeeder: seeded full org structure for IntelliFlow ({Count} employees, including QA scenarios).", 19);
    }

    private static async Task SeedMinimalOrgAsync(
        ZayraDbContext db, Guid tenantId, Company company, Branch branch, CancellationToken ct)
    {
        // Evostel gets a small flat structure: 3 grades + 2 departments + 4 employees
        var grades = new[]
        {
            new Grade { TenantId = tenantId, Code = "S1", Name = "Level 1 – Staff",   Level = 1 },
            new Grade { TenantId = tenantId, Code = "S2", Name = "Level 2 – Senior",  Level = 2 },
            new Grade { TenantId = tenantId, Code = "M1", Name = "Level 3 – Manager", Level = 3 },
        };
        db.Grades.AddRange(grades);
        await db.SaveChangesAsync(ct);
        var gMap = grades.ToDictionary(g => g.Code);

        var deptMgmt = new Department { TenantId = tenantId, BranchId = branch.Id, Code = "MGT",  NameEn = "Management",  SortOrder = 0 };
        var deptOps  = new Department { TenantId = tenantId, BranchId = branch.Id, Code = "OPS",  NameEn = "Operations",  SortOrder = 1 };
        db.Departments.AddRange(deptMgmt, deptOps);
        await db.SaveChangesAsync(ct);

        var desigMgr = new Designation { TenantId = tenantId, Code = "MGR",  TitleEn = "Manager",  GradeId = gMap["M1"].Id, IsManagerRole = true,  LevelRank = 30 };
        var desigStf = new Designation { TenantId = tenantId, Code = "STAFF", TitleEn = "Staff",   GradeId = gMap["S1"].Id, IsManagerRole = false, LevelRank = 10 };
        db.Designations.AddRange(desigMgr, desigStf);
        await db.SaveChangesAsync(ct);

        var ceo = AddEmployee(db, tenantId, company, branch, "EVS-001", "Tom Reyes",   deptMgmt, desigMgr, gMap["M1"], "tom.reyes@evostel.com",   "1982-05-10");
        await db.SaveChangesAsync(ct);

        var emp1 = AddEmployee(db, tenantId, company, branch, "EVS-002", "Lena Müller", deptOps, desigStf, gMap["S1"], "lena.mueller@evostel.com", "1994-03-20", ceo.Id);
        var emp2 = AddEmployee(db, tenantId, company, branch, "EVS-003", "Raj Patel",   deptOps, desigStf, gMap["S1"], "raj.patel@evostel.com",    "1995-08-15", ceo.Id);
        await db.SaveChangesAsync(ct);

        db.ReportingLines.Add(new ReportingLine { TenantId = tenantId, EmployeeId = emp1.Id, ManagerEmployeeId = ceo.Id, RelationshipType = "SolidLine", EffectiveFrom = emp1.JoiningDate, IsPrimary = true, IsActive = true });
        db.ReportingLines.Add(new ReportingLine { TenantId = tenantId, EmployeeId = emp2.Id, ManagerEmployeeId = ceo.Id, RelationshipType = "SolidLine", EffectiveFrom = emp2.JoiningDate, IsPrimary = true, IsActive = true });
        await db.SaveChangesAsync(ct);
    }

    // ── Shared operational data seed (leave + payroll + attendance) ──────────
    // Called for both IntelliFlow and Evostel after org structure is seeded.
    private static async Task SeedOperationalDataAsync(ZayraDbContext db, Guid tenantId, ILogger logger, CancellationToken ct)
    {
        var employees = await db.Employees
            .Where(e => e.TenantId == tenantId && !e.IsDeleted && (e.Status == "Active" || e.Status == "Confirmed" || e.Status == "Probation"))
            .ToListAsync(ct);
        if (employees.Count == 0) return;

        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);

        // ── Leave types ──────────────────────────────────────────────────────
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

        var annual = leaveTypes.First(x => x.Code == "ANNUAL");
        var sick   = leaveTypes.First(x => x.Code == "SICK");
        var casual = leaveTypes.First(x => x.Code == "CASUAL");
        var unpaid = leaveTypes.First(x => x.Code == "UNPAID");

        // ── Attendance (last 90 days, working days only) ─────────────────────
        var seededEmpIds = await db.AttendanceRecords
            .Where(x => x.TenantId == tenantId)
            .Select(x => x.EmployeeId).Distinct().ToListAsync(ct);
        var attendTargets = employees.Where(e => !seededEmpIds.Contains(e.Id)).ToList();
        if (attendTargets.Count > 0)
        {
            var rng = new Random(tenantId.GetHashCode() & 0x7fffffff);
            var records = new List<AttendanceRecord>();
            for (var d = today.AddDays(-90); d <= today; d = d.AddDays(1))
            {
                if (d.DayOfWeek is DayOfWeek.Friday or DayOfWeek.Saturday) continue;
                foreach (var emp in attendTargets)
                {
                    var r = rng.Next(100);
                    var status = r < 83 ? "Present" : r < 91 ? "Late" : r < 95 ? "Leave" : "Absent";
                    records.Add(new AttendanceRecord
                    {
                        TenantId = tenantId, EmployeeId = emp.Id, WorkDate = d, Status = status,
                        TimeIn  = status is "Present" ? new TimeOnly(8, 30) : status is "Late" ? new TimeOnly(9, 15) : null,
                        TimeOut = status is "Present" or "Late" ? new TimeOnly(17, 30) : null,
                        OvertimeHours = status == "Present" && rng.Next(100) < 18 ? rng.Next(1, 4) : 0,
                        Notes = string.Empty,
                    });
                }
            }
            db.AttendanceRecords.AddRange(records);
            await db.SaveChangesAsync(ct);
        }

        // ── Leave requests (sample data) ─────────────────────────────────────
        if (!await db.LeaveRequests.AnyAsync(x => x.TenantId == tenantId, ct) && employees.Count >= 2)
        {
            var requests = new List<LeaveRequest>
            {
                new() { TenantId=tenantId, EmployeeId=employees[0].Id, EmployeeName=employees[0].FullName, DepartmentName=employees[0].Department, LeaveTypeId=annual.Id, LeaveTypeName=annual.NameEn, StartDate=today.AddDays(5),   EndDate=today.AddDays(9),   DayType="Full", Reason="Family vacation",   Status="Submitted",  SubmittedAtUtc=DateTime.UtcNow.AddDays(-1) },
                new() { TenantId=tenantId, EmployeeId=employees[1].Id, EmployeeName=employees[1].FullName, DepartmentName=employees[1].Department, LeaveTypeId=sick.Id,   LeaveTypeName=sick.NameEn,   StartDate=today.AddDays(-2),  EndDate=today.AddDays(-1), DayType="Full", Reason="Flu",              Status="Approved",   SubmittedAtUtc=DateTime.UtcNow.AddDays(-3),  DecidedAtUtc=DateTime.UtcNow.AddDays(-2) },
            };
            if (employees.Count >= 5)
            {
                requests.AddRange(new[]
                {
                    new LeaveRequest { TenantId=tenantId, EmployeeId=employees[2].Id, EmployeeName=employees[2].FullName, DepartmentName=employees[2].Department, LeaveTypeId=annual.Id,  LeaveTypeName=annual.NameEn,  StartDate=today.AddDays(12),  EndDate=today.AddDays(14),  DayType="Full", Reason="Personal",         Status="Submitted",  SubmittedAtUtc=DateTime.UtcNow.AddHours(-6) },
                    new LeaveRequest { TenantId=tenantId, EmployeeId=employees[3].Id, EmployeeName=employees[3].FullName, DepartmentName=employees[3].Department, LeaveTypeId=casual.Id, LeaveTypeName=casual.NameEn,  StartDate=today.AddDays(2),   EndDate=today.AddDays(2),   DayType="Full", Reason="Bank errand",      Status="Approved",   SubmittedAtUtc=DateTime.UtcNow.AddDays(-2),  DecidedAtUtc=DateTime.UtcNow.AddDays(-1) },
                    new LeaveRequest { TenantId=tenantId, EmployeeId=employees[4].Id, EmployeeName=employees[4].FullName, DepartmentName=employees[4].Department, LeaveTypeId=sick.Id,   LeaveTypeName=sick.NameEn,    StartDate=today.AddDays(-5),  EndDate=today.AddDays(-4),  DayType="Full", Reason="Medical checkup",  Status="Approved",   SubmittedAtUtc=DateTime.UtcNow.AddDays(-6),  DecidedAtUtc=DateTime.UtcNow.AddDays(-5) },
                });
            }
            if (employees.Count >= 10)
            {
                requests.AddRange(new[]
                {
                    new LeaveRequest { TenantId=tenantId, EmployeeId=employees[6].Id,  EmployeeName=employees[6].FullName,  DepartmentName=employees[6].Department,  LeaveTypeId=annual.Id,  LeaveTypeName=annual.NameEn,  StartDate=today.AddDays(20),  EndDate=today.AddDays(30),  DayType="Full", Reason="Summer holiday",   Status="Submitted",  SubmittedAtUtc=DateTime.UtcNow.AddHours(-3) },
                    new LeaveRequest { TenantId=tenantId, EmployeeId=employees[8].Id,  EmployeeName=employees[8].FullName,  DepartmentName=employees[8].Department,  LeaveTypeId=sick.Id,   LeaveTypeName=sick.NameEn,    StartDate=today.AddDays(-1),  EndDate=today.AddDays(-1),  DayType="Full", Reason="Headache",         Status="Submitted",  SubmittedAtUtc=DateTime.UtcNow.AddHours(-10) },
                    new LeaveRequest { TenantId=tenantId, EmployeeId=employees[9].Id,  EmployeeName=employees[9].FullName,  DepartmentName=employees[9].Department,  LeaveTypeId=unpaid.Id, LeaveTypeName=unpaid.NameEn,  StartDate=today.AddDays(-15), EndDate=today.AddDays(-11), DayType="Full", Reason="Emergency travel",  Status="Approved",   SubmittedAtUtc=DateTime.UtcNow.AddDays(-18), DecidedAtUtc=DateTime.UtcNow.AddDays(-17) },
                });
            }
            db.LeaveRequests.AddRange(requests);
            await db.SaveChangesAsync(ct);
        }

        // ── Leave balances (current year, for all employees) ─────────────────
        if (!await db.EmployeeLeaveBalances.AnyAsync(x => x.TenantId == tenantId, ct))
        {
            var balTypes = new (LeaveType Type, decimal Entitled)[] { (annual, 30m), (sick, 15m), (casual, 5m) };
            foreach (var (emp, i) in employees.Select((e, i) => (e, i)))
            {
                foreach (var (lt, entitled) in balTypes)
                {
                    var used = Math.Min(entitled, (i * 3 + (lt.Code == "SICK" ? 1 : 4)) % (int)entitled);
                    db.EmployeeLeaveBalances.Add(new EmployeeLeaveBalance
                    {
                        TenantId = tenantId, EmployeeId = emp.Id, EmployeeName = emp.FullName,
                        LeaveTypeId = lt.Id, LeaveTypeName = lt.NameEn, Year = today.Year,
                        Entitled = entitled, Accrued = Math.Round(entitled * today.Month / 12m, 1),
                        Used = used, Pending = i % 6 == 0 ? 2 : 0,
                        CarriedForward = lt.Code == "ANNUAL" && i % 4 == 0 ? 5 : 0,
                    });
                }
            }
            await db.SaveChangesAsync(ct);
        }

        // ── Payroll runs: 6 completed months + 1 draft current month ────────
        var rng2 = new Random(42);
        for (var m = 6; m >= 0; m--)
        {
            var period = new DateOnly(today.Year, today.Month, 1).AddMonths(-m);
            if (await db.PayrollRuns.AnyAsync(x => x.TenantId == tenantId && x.Year == period.Year && x.Month == period.Month, ct)) continue;
            var isPending = m == 0;
            var run = new PayrollRun
            {
                TenantId = tenantId, Year = period.Year, Month = period.Month,
                Status = isPending ? "Draft" : "Completed",
                CreatedAtUtc = DateTime.UtcNow.AddMonths(-m).AddDays(-8),
                ProcessedAtUtc = isPending ? null : (DateTime?)DateTime.UtcNow.AddMonths(-m).AddDays(-5),
            };
            decimal tg = 0, td = 0, tn = 0;
            var slips = new List<PayrollSlip>();
            foreach (var emp in employees)
            {
                var basic = GetSalaryForEmployee(emp) * (1 + (rng2.Next(3) == 0 ? 0.05m : 0m));
                var housing    = Math.Round(basic * 0.25m);
                var transport  = 1_000m;
                var gross      = basic + housing + transport;
                var deductions = Math.Round(gross * 0.05m);
                var net        = gross - deductions;
                tg += gross; td += deductions; tn += net;
                slips.Add(new PayrollSlip
                {
                    TenantId=tenantId, RunId=run.Id, EmployeeId=emp.Id, EmployeeCode=emp.EmployeeCode,
                    EmployeeName=emp.FullName, Department=emp.Department ?? "",
                    BasicSalary=Math.Round(basic), HousingAllowance=housing, TransportAllowance=transport, OtherAllowances=0,
                    GrossSalary=gross, Deductions=deductions, NetSalary=net,
                    Status=isPending ? "Draft" : "Paid",
                });
            }
            run.TotalGrossSalary=tg; run.TotalDeductions=td; run.TotalNetSalary=tn; run.EmployeeCount=slips.Count;
            db.PayrollRuns.Add(run);
            db.PayrollSlips.AddRange(slips);
            await db.SaveChangesAsync(ct);
        }

        // ── Payroll profiles (IBAN, MolId, bank details) ────────────────────────
        await SeedPayrollProfilesAsync(db, tenantId, employees, logger, ct);

        logger.LogInformation("DemoDataSeeder: seeded operational data (leave + payroll + attendance) for tenant {TenantId}.", tenantId);
    }

    // ── Payroll profiles ──────────────────────────────────────────────────────
    // Seeds EmployeePayrollProfile for every active demo employee so the
    // People → Payroll tab shows real data and WPS SIF exports have populated
    // IBAN / MolId / BankCode identifiers (not blank strings).
    // Idempotent — only inserts rows that don't already exist.
    private static async Task SeedPayrollProfilesAsync(
        ZayraDbContext db, Guid tenantId, IReadOnlyList<Employee> employees,
        ILogger logger, CancellationToken ct)
    {
        // Detect country from company record to pick correct IBAN format.
        var company = await db.Companies.AsNoTracking()
            .Where(c => c.TenantId == tenantId && !c.IsDeleted)
            .FirstOrDefaultAsync(ct);
        var isKsa = (company?.CountryCode ?? "SAU") == "SAU";

        // Load existing profile employee IDs to skip already-seeded profiles.
        var seededIds = await db.EmployeePayrollProfiles
            .AsNoTracking()
            .Where(p => p.TenantId == tenantId && !p.IsDeleted)
            .Select(p => p.EmployeeId)
            .ToListAsync(ct);
        var seededSet = new HashSet<int>(seededIds);

        var added = 0;
        foreach (var emp in employees)
        {
            if (seededSet.Contains(emp.Id)) continue;

            var isSaudiNational = (emp.Nationality ?? "").StartsWith("Saudi", StringComparison.OrdinalIgnoreCase);
            // Derive a deterministic 16-digit account number from the employee's
            // integer ID so each profile is unique but reproducible across re-seeds.
            var account16 = emp.Id.ToString().PadLeft(16, '0');

            string iban, bankName, bankCode, currency;
            if (isKsa)
            {
                // Saudi Mudad WPS — Al Rajhi Bank IBAN (BBAN: 4-digit bank + 16-digit acct)
                iban      = BuildIban("SA", "8000" + account16);
                bankName  = "Al Rajhi Bank";
                bankCode  = "RJHISARI";
                currency  = "SAR";
            }
            else
            {
                // UAE — Emirates NBD (BBAN: 3-digit bank + 16-digit acct = 19 chars)
                iban      = BuildIban("AE", "033" + account16);
                bankName  = "Emirates NBD";
                bankCode  = "EBILAEAD";
                currency  = "AED";
            }

            // MolId: Saudi national 10-digit ID starting "1", expat starting "2".
            // Digits 2-10 are the employee's int ID padded to 9 characters.
            var molId = (isSaudiNational ? "1" : "2") + emp.Id.ToString().PadLeft(9, '0');

            db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile
            {
                TenantId               = tenantId,
                EmployeeId             = emp.Id,
                BankName               = bankName,
                Iban                   = iban,
                AccountNumber          = account16,
                PaymentMethod          = isKsa ? "WPS" : "BankTransfer",
                SalaryCurrency         = currency,
                PayrollGroup           = "Default",
                WpsEligible            = true,
                EosbEligible           = true,
                MolId                  = molId,
                BankRoutingCode        = bankCode,
                SocialInsuranceReference = molId,  // GOSI reference mirrors MolId for demo
            });
            added++;
        }

        if (added > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "DemoDataSeeder: seeded {Count} payroll profiles for tenant {TenantId} ({Country}).",
                added, tenantId, isKsa ? "KSA/SAR" : "UAE/AED");
        }
    }

    // Computes a standards-compliant IBAN check digit (MOD97) for any country+BBAN.
    // BBAN must contain only digits (letters not needed for SA/AE demo IBANs).
    private static string BuildIban(string country, string bban)
    {
        // Rearrange to: BBAN + country-in-digits + "00", then MOD 97.
        // Letter-to-digit: A=10, B=11 … Z=35 → S=28, A=10, E=14.
        static string LetterToDigit(char c) =>
            char.IsLetter(c) ? (c - 'A' + 10).ToString() : c.ToString();
        var numeric = string.Concat((bban + country + "00").Select(LetterToDigit));
        var rem = 0;
        foreach (var ch in numeric) rem = (rem * 10 + (ch - '0')) % 97;
        return country + (98 - rem).ToString("D2") + bban;
    }

    private static decimal GetSalaryForEmployee(Employee emp) => emp.Grade switch
    {
        "L7" => 35_000m, "L6" => 25_000m, "L5" => 18_000m,
        "L3" => 14_000m, "L2" => 10_000m,
        "M1" => 12_000m, "S2" =>  8_000m, "S1" =>  6_000m,
        _    => 10_000m,
    };

    private static Employee AddEmployee(
        ZayraDbContext db,
        Guid tenantId,
        Company company,
        Branch branch,
        string code,
        string name,
        Department dept,
        Designation desig,
        Grade grade,
        string email,
        string dob,
        int? managerId = null)
    {
        var joiningDate = new DateTime(2022, 1, 1).AddDays(new Random(code.GetHashCode()).Next(0, 730));
        var emp = new Employee
        {
            TenantId          = tenantId,
            CompanyId         = company.Id,
            BranchId          = branch.Id,
            EmployeeCode      = code,
            FullName          = name,
            EnglishName       = name,
            WorkEmail         = email,
            DepartmentId      = dept.Id,
            Department        = dept.NameEn,
            DesignationId     = desig.Id,
            Designation       = desig.TitleEn,
            GradeId           = grade.Id,
            Grade             = grade.Code,
            JobTitle          = desig.TitleEn,
            ManagerEmployeeId = managerId,
            Status            = EmployeeStatuses.Active,
            JoiningDate       = joiningDate,
            EmploymentType    = "Full-time",
            ContractType      = "Unlimited",
        };
        db.Employees.Add(emp);
        return emp;
    }

    // ── Spec records ──────────────────────────────────────────────────────────

    private sealed class DemoTenantSpec
    {
        public string Name { get; init; } = null!;
        public string Slug { get; init; } = null!;
        public string Plan { get; init; } = null!;
        public string Status { get; init; } = null!;
        public int MaxEmployees { get; init; }
        public int MaxUsers { get; init; }
        public decimal MonthlyAmount { get; init; }
        public string CurrencyCode { get; init; } = "USD";
        public string BillingEmail { get; init; } = null!;
        public DateTime? ExpiresAtUtc { get; init; }
        public Dictionary<string, bool> Features { get; init; } = new();
        public DemoUserSpec[] Users { get; init; } = Array.Empty<DemoUserSpec>();
        public bool SeedOrgStructure { get; init; }
    }

    private sealed record DemoUserSpec(string RoleName, string FullName, string Email);

    // ── Deactivate demo tenants ───────────────────────────────────────────────
    // One-time idempotent cleanup: soft-deletes demo tenants that were seeded
    // during development and should no longer appear in production.
    // Respects any tenant whose slug is on the DEACTIVATE_DEMO_SLUGS list.
    // Set env var DEACTIVATE_DEMO_SLUGS=intelliflow,evostel to trigger.
    // If the var is absent this method is a no-op.

    private static readonly string[] _demoSlugsToDeactivate =
        (Environment.GetEnvironmentVariable("DEACTIVATE_DEMO_SLUGS") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    internal static async Task DeactivateDemoTenantsAsync(
        ZayraDbContext db,
        ILogger logger,
        CancellationToken ct)
    {
        if (_demoSlugsToDeactivate.Length == 0) return;

        var slugSet = new HashSet<string>(_demoSlugsToDeactivate, StringComparer.OrdinalIgnoreCase);

        var tenants = await db.Tenants
            .Where(t => slugSet.Contains(t.Slug) && t.IsActive)
            .ToListAsync(ct);

        if (tenants.Count == 0)
        {
            logger.LogInformation("DemoDataSeeder.DeactivateDemoTenantsAsync: all target slugs already deactivated or not found.");
            return;
        }

        foreach (var tenant in tenants)
        {
            logger.LogInformation("DemoDataSeeder: deactivating demo tenant '{Slug}' ({Id}).", tenant.Slug, tenant.Id);
            tenant.IsActive = false;

            // Deactivate users
            var usersDeactivated = await db.Users
                .Where(u => u.TenantId == tenant.Id && !u.IsDeleted)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(u => u.IsActive, false)
                    .SetProperty(u => u.Status, "Deactivated")
                    .SetProperty(u => u.UpdatedAtUtc, DateTime.UtcNow), ct);

            // Revoke refresh tokens
            var userIds = await db.Users
                .Where(u => u.TenantId == tenant.Id)
                .Select(u => u.Id)
                .ToListAsync(ct);
            if (userIds.Count > 0)
            {
                await db.RefreshTokens
                    .Where(t => t.RevokedAtUtc == null && userIds.Contains(t.UserId))
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAtUtc, DateTime.UtcNow), ct);
            }

            // Cancel subscription
            var sub = await db.TenantSubscriptions.FirstOrDefaultAsync(s => s.TenantId == tenant.Id, ct);
            if (sub is not null) sub.Status = "Cancelled";

            await db.SaveChangesAsync(ct);

            logger.LogInformation(
                "DemoDataSeeder: demo tenant '{Slug}' deactivated — {Users} users deactivated, subscription cancelled.",
                tenant.Slug, usersDeactivated);
        }
    }

    // ── Pricing config seed ───────────────────────────────────────────────────

    private static async Task SeedPricingConfigAsync(ZayraDbContext db, ILogger logger, CancellationToken ct)
    {
        if (await db.PricingModuleConfigs.AnyAsync(ct))
        {
            logger.LogInformation("DemoDataSeeder: pricing config already seeded — skipping.");
            return;
        }

        // ── Scalar pricing parameters ─────────────────────────────────────────
        var configs = new[]
        {
            // Base plan prices (monthly)
            new PricingConfig { Key = "base_starter",    Label = "Starter Base Price",    Group = "base",          Plan = "starter",    Value = 299 },
            new PricingConfig { Key = "base_growth",     Label = "Growth Base Price",     Group = "base",          Plan = "growth",     Value = 799 },
            new PricingConfig { Key = "base_enterprise", Label = "Enterprise Base Price", Group = "base",          Plan = "enterprise", Value = 2000 },

            // Per-employee overages
            new PricingConfig { Key = "per_employee_starter",  Label = "Per Extra Employee (Starter)",  Group = "per_employee", Plan = "starter",  Value = 5 },
            new PricingConfig { Key = "per_employee_growth",   Label = "Per Extra Employee (Growth)",   Group = "per_employee", Plan = "growth",   Value = 3 },

            // Per-company overages
            new PricingConfig { Key = "per_company_starter",  Label = "Per Extra Company (Starter)",  Group = "per_company", Plan = "starter",  Value = 100 },
            new PricingConfig { Key = "per_company_growth",   Label = "Per Extra Company (Growth)",   Group = "per_company", Plan = "growth",   Value = 75 },

            // Per-admin-user overages
            new PricingConfig { Key = "per_admin_user_starter",  Label = "Per Extra Admin User (Starter)",  Group = "per_admin_user", Plan = "starter",  Value = 25 },
            new PricingConfig { Key = "per_admin_user_growth",   Label = "Per Extra Admin User (Growth)",   Group = "per_admin_user", Plan = "growth",   Value = 15 },

            // Supplements
            new PricingConfig { Key = "supplement_arabic",    Label = "Arabic/Bilingual Interface Supplement", Group = "supplement", Plan = "all", Value = 50 },
            new PricingConfig { Key = "per_extra_country",    Label = "Per Additional Country",                Group = "supplement", Plan = "all", Value = 100 },

            // Annual discount %
            new PricingConfig { Key = "annual_discount_pct",  Label = "Annual Billing Discount (%)", Group = "discount", Plan = "all", Value = 10 },

            // Implementation estimates
            new PricingConfig { Key = "impl_starter",    Label = "Implementation Estimate (Starter)",    Group = "implementation", Plan = "starter",    Value = 3500 },
            new PricingConfig { Key = "impl_growth",     Label = "Implementation Estimate (Growth)",     Group = "implementation", Plan = "growth",     Value = 7500 },
            new PricingConfig { Key = "impl_enterprise", Label = "Implementation Estimate (Enterprise)", Group = "implementation", Plan = "enterprise", Value = 25000 },
        };

        db.PricingConfigs.AddRange(configs);

        // ── Module configs ────────────────────────────────────────────────────
        var modules = new[]
        {
            new PricingModuleConfig
            {
                ModuleKey = PricingModuleKeys.CoreHr, ModuleName = "Core HR",
                IncludedInTrial = true, IncludedInStarter = true, IncludedInGrowth = true, IncludedInEnterprise = true,
                IsEnterpriseOnly = false, AddonPriceMonthly = 0, SortOrder = 1
            },
            new PricingModuleConfig
            {
                ModuleKey = PricingModuleKeys.LeaveAttendance, ModuleName = "Leave & Attendance",
                IncludedInTrial = true, IncludedInStarter = true, IncludedInGrowth = true, IncludedInEnterprise = true,
                IsEnterpriseOnly = false, AddonPriceMonthly = 0, SortOrder = 2
            },
            new PricingModuleConfig
            {
                ModuleKey = PricingModuleKeys.Payroll, ModuleName = "Payroll",
                IncludedInTrial = false, IncludedInStarter = false, IncludedInGrowth = true, IncludedInEnterprise = true,
                IsEnterpriseOnly = false, AddonPriceMonthly = 150, SortOrder = 3
            },
            new PricingModuleConfig
            {
                ModuleKey = PricingModuleKeys.Performance, ModuleName = "Performance & Appraisals",
                IncludedInTrial = false, IncludedInStarter = false, IncludedInGrowth = false, IncludedInEnterprise = true,
                IsEnterpriseOnly = false, AddonPriceMonthly = 150, SortOrder = 4
            },
            new PricingModuleConfig
            {
                ModuleKey = PricingModuleKeys.Recruitment, ModuleName = "Recruitment",
                IncludedInTrial = false, IncludedInStarter = false, IncludedInGrowth = false, IncludedInEnterprise = true,
                IsEnterpriseOnly = false, AddonPriceMonthly = 150, SortOrder = 5
            },
            new PricingModuleConfig
            {
                ModuleKey = PricingModuleKeys.Documents, ModuleName = "Document Management",
                IncludedInTrial = false, IncludedInStarter = false, IncludedInGrowth = true, IncludedInEnterprise = true,
                IsEnterpriseOnly = false, AddonPriceMonthly = 75, SortOrder = 6
            },
            new PricingModuleConfig
            {
                ModuleKey = PricingModuleKeys.Compliance, ModuleName = "Compliance",
                IncludedInTrial = false, IncludedInStarter = false, IncludedInGrowth = false, IncludedInEnterprise = true,
                IsEnterpriseOnly = false, AddonPriceMonthly = 200, SortOrder = 7
            },
            new PricingModuleConfig
            {
                ModuleKey = PricingModuleKeys.KsaCompliance, ModuleName = "KSA Compliance (GOSI, Qiwa, WPS)",
                IncludedInTrial = false, IncludedInStarter = false, IncludedInGrowth = false, IncludedInEnterprise = true,
                IsEnterpriseOnly = false, AddonPriceMonthly = 200, SortOrder = 8
            },
            new PricingModuleConfig
            {
                ModuleKey = PricingModuleKeys.AiAssistant, ModuleName = "AI Assistant",
                IncludedInTrial = false, IncludedInStarter = false, IncludedInGrowth = false, IncludedInEnterprise = true,
                IsEnterpriseOnly = false, AddonPriceMonthly = 200, SortOrder = 9
            },
            new PricingModuleConfig
            {
                ModuleKey = PricingModuleKeys.AdvancedAnalytics, ModuleName = "Advanced Analytics",
                IncludedInTrial = false, IncludedInStarter = false, IncludedInGrowth = false, IncludedInEnterprise = true,
                IsEnterpriseOnly = false, AddonPriceMonthly = 150, SortOrder = 10
            },
            new PricingModuleConfig
            {
                ModuleKey = PricingModuleKeys.MobileApp, ModuleName = "Mobile App",
                IncludedInTrial = false, IncludedInStarter = false, IncludedInGrowth = false, IncludedInEnterprise = true,
                IsEnterpriseOnly = false, AddonPriceMonthly = 100, SortOrder = 11
            },
            new PricingModuleConfig
            {
                ModuleKey = PricingModuleKeys.SsoMfa, ModuleName = "SSO / MFA",
                IncludedInTrial = false, IncludedInStarter = false, IncludedInGrowth = false, IncludedInEnterprise = true,
                IsEnterpriseOnly = true, AddonPriceMonthly = 75, SortOrder = 12
            },
        };

        db.PricingModuleConfigs.AddRange(modules);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("DemoDataSeeder: seeded {Count} pricing config entries and {ModuleCount} module configs.", configs.Length, modules.Length);
    }
}
