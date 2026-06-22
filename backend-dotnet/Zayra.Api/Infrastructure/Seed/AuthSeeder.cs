using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Seed;

public class AuthSeeder : IAuthSeeder
{
    private readonly ZayraDbContext _db;
    private readonly IPasswordHasher _passwordHasher;
    private readonly SeedAdminOptions _options;

    public AuthSeeder(ZayraDbContext db, IPasswordHasher passwordHasher, IOptions<SeedAdminOptions> options)
    {
        _db = db;
        _passwordHasher = passwordHasher;
        _options = options.Value;
    }

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        await _db.Database.EnsureCreatedAsync(cancellationToken);

        var tenantSlug = _options.TenantSlug.Trim().ToLowerInvariant();
        var tenant = await _db.Tenants.FirstOrDefaultAsync(x => x.Slug == tenantSlug, cancellationToken);
        if (tenant is null)
        {
            tenant = new Tenant { Name = _options.TenantName.Trim(), Slug = tenantSlug };
            _db.Tenants.Add(tenant);
            await _db.SaveChangesAsync(cancellationToken);
        }

        var adminRole = await EnsureTenantRolesAsync(tenant.Id, cancellationToken);

        var normalizedEmail = AuthService.Normalize(_options.Email);
        var admin = await _db.Users
            .Include(x => x.UserRoles)
            .FirstOrDefaultAsync(x => x.TenantId == tenant.Id && x.NormalizedEmail == normalizedEmail, cancellationToken);
        if (admin is null)
        {
            admin = new User
            {
                TenantId = tenant.Id,
                Email = _options.Email.Trim().ToLowerInvariant(),
                NormalizedEmail = normalizedEmail,
                FullName = _options.FullName.Trim(),
                PasswordHash = _passwordHasher.Hash(_options.Password),
                AccessMode = "FullPortal",
                Status = "Active",
                IsActive = true,
                IsEmailConfirmed = true
            };
            admin.UserRoles.Add(new UserRole { User = admin, Role = adminRole });
            _db.Users.Add(admin);
        }
        else if (!admin.UserRoles.Any(x => x.RoleId == adminRole.Id))
        {
            admin.UserRoles.Add(new UserRole { UserId = admin.Id, RoleId = adminRole.Id });
        }

        await _db.SaveChangesAsync(cancellationToken);

        // Global labour-law / payroll rule packs for major countries — seeded for every
        // tenant as configurable reference data (not demo). Admins can edit, override,
        // or import additional country packs. This keeps the platform global, not UAE-only.
        try { await EnsureGlobalCountryRules(tenant.Id, cancellationToken); }
        catch (Exception ex) { Console.WriteLine($"[Seed] Country rule packs skipped: {ex.Message}"); }

        // Sample organisation/business data is seeded only when explicitly enabled
        // (SeedAdmin:SeedDemoData). Production tenants start clean — tenant, roles,
        // permissions and the admin account above are always seeded; nothing else.
        if (_options.SeedDemoData)
        {
            await EnsureFoundationSeedData(tenant.Id, cancellationToken);
        }

        // Propagate any newly-added permissions to all other existing tenants' Admin roles.
        // Uses raw SQL so it doesn't trigger global tenant query filters on the DbContext.
        // This ensures new permissions are always available for demo/test tenants after restart.
        var permissions2 = await _db.Permissions.AsNoTracking().ToListAsync(cancellationToken);
        var otherTenantIds = await _db.Tenants
            .AsNoTracking()
            .Where(t => t.Id != tenant.Id)
            .Select(t => t.Id)
            .ToListAsync(cancellationToken);
        foreach (var otherTenantId in otherTenantIds)
        {
            try
            {
                var allRoles = await _db.Roles
                    .AsNoTracking()
                    .Where(r => r.TenantId == otherTenantId)
                    .Select(r => new { r.Id, r.Name })
                    .ToListAsync(cancellationToken);

                foreach (var role in allRoles)
                {
                    var existingPermIds = await _db.Database
                        .SqlQuery<Guid>($"SELECT permission_id as Value FROM role_permissions WHERE role_id = {role.Id}")
                        .ToListAsync(cancellationToken);

                    var existingSet = existingPermIds.ToHashSet();
                    var adminAll = role.Name == "Admin";

                    foreach (var perm in permissions2)
                    {
                        if (existingSet.Contains(perm.Id)) continue;
                        if (!adminAll) continue; // only patch Admin role here; other roles stay as seeded
                        await _db.Database.ExecuteSqlRawAsync(
                            "INSERT INTO role_permissions (role_id, permission_id) VALUES ({0}, {1}) ON CONFLICT DO NOTHING",
                            role.Id, perm.Id);
                    }
                }
            }
            catch (Exception ex) { Console.WriteLine($"[Seed] Permission sync skipped for tenant {otherTenantId}: {ex.Message}"); }
        }
    }

    public async Task<Role> EnsureTenantRolesAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var permissions = await EnsurePermissions(cancellationToken);
        var Ps = (string[] keys) => permissions.Where(x => keys.Contains(x.Key)).ToList();

        // Level 1 — Admin: all permissions
        var adminRole = await EnsureRole(tenantId, "Admin", "Tenant system administrator with full access", permissions, 1, false, cancellationToken);

        // Level 2 — HR Director: full HR + payroll visibility + reports + compliance
        await EnsureRole(tenantId, "HR Director", "Senior HR leader with strategic visibility", permissions.Where(x =>
            x.Key.StartsWith("employees.") || x.Key.StartsWith("attendance.") || x.Key.StartsWith("leave.") ||
            x.Key.StartsWith("overtime.") || x.Key.StartsWith("dashboard.") || x.Key.StartsWith("organization.") ||
            x.Key.StartsWith("approvals.") || x.Key.StartsWith("notifications.") || x.Key.StartsWith("localization.") ||
            x.Key.StartsWith("performance.") || x.Key.StartsWith("compliance.") || x.Key.StartsWith("reports.") ||
            x.Key.StartsWith("recruitment.") || x.Key is "payroll.read" or "loans.read" or "audit.read" or
            "roles.manage" or "users.manage" or "manager.read" or "manager.approve" or
            "qiwa.read" or "qiwa.sync"
        ).ToList(), 2, true, cancellationToken);

        // Level 3 — HR Manager: operational HR management
        await EnsureRole(tenantId, "HR Manager", "HR operations manager", permissions.Where(x =>
            x.Key.StartsWith("employees.") || x.Key.StartsWith("attendance.") || x.Key.StartsWith("leave.") ||
            x.Key.StartsWith("overtime.") || x.Key.StartsWith("dashboard.") || x.Key.StartsWith("organization.") ||
            x.Key.StartsWith("approvals.") || x.Key.StartsWith("notifications.") || x.Key.StartsWith("localization.") ||
            x.Key is "audit.read" or "manager.read" or "manager.approve" or "reports.read" or "qiwa.read"
        ).ToList(), 3, true, cancellationToken);

        // Level 4 — Payroll Manager: payroll + finance + employees
        await EnsureRole(tenantId, "Payroll Manager", "Manages payroll processing and WPS submissions", Ps(new[] {
            "dashboard.read", "employees.read", "employees.sensitive", "attendance.read", "leave.read",
            "overtime.read", "payroll.read", "payroll.write", "payroll.approve", "loans.read", "loans.approve",
            "approvals.read", "approvals.decide", "reports.read", "notifications.read"
        }), 4, true, cancellationToken);

        // Level 5 — HR Officer: HR operations specialist
        await EnsureRole(tenantId, "HR Officer", "HR operations specialist", Ps(new[] {
            "dashboard.read", "employees.read", "employees.write", "employees.documents", "employees.templates",
            "organization.read", "approvals.read", "approvals.write", "notifications.read", "localization.read",
            "leave.read", "leave.write", "attendance.read", "overtime.read", "profile.read"
        }), 5, true, cancellationToken);

        // Level 6 — Payroll Officer: payroll processing
        await EnsureRole(tenantId, "Payroll Officer", "Payroll and WPS specialist", Ps(new[] {
            "dashboard.read", "employees.read", "employees.sensitive", "attendance.read",
            "payroll.read", "payroll.write", "loans.read", "notifications.read", "reports.read"
        }), 6, true, cancellationToken);

        // Level 7 — Finance Approver: finance approvals
        await EnsureRole(tenantId, "Finance Approver", "Finance approver for loans, advances and payroll", Ps(new[] {
            "dashboard.read", "employees.read", "payroll.read", "payroll.approve",
            "loans.read", "loans.approve", "approvals.read", "approvals.decide"
        }), 7, true, cancellationToken);

        // Level 8 — Compliance Officer: compliance and contracts
        await EnsureRole(tenantId, "Compliance Officer", "Manages compliance, contracts and regulatory records", Ps(new[] {
            "dashboard.read", "employees.read", "employees.documents", "organization.read",
            "compliance.read", "compliance.write", "approvals.read", "audit.read", "reports.read", "notifications.read"
        }), 8, true, cancellationToken);

        // Level 9 — Manager: team management and approvals
        await EnsureRole(tenantId, "Manager", "People manager with team oversight and approval authority", Ps(new[] {
            "dashboard.read", "employees.read", "approvals.read", "approvals.decide", "notifications.read",
            "manager.read", "manager.approve", "ess.read", "ess.write", "leave.read", "leave.approve",
            "attendance.read", "overtime.read", "overtime.approve", "profile.read"
        }), 9, true, cancellationToken);

        // Level 10 — Supervisor: front-line supervision
        await EnsureRole(tenantId, "Supervisor", "Front-line supervisor for operational staff", Ps(new[] {
            "dashboard.read", "employees.read", "attendance.read", "attendance.write",
            "manager.read", "manager.approve", "leave.read", "overtime.read", "ess.read", "ess.write", "profile.read"
        }), 10, true, cancellationToken);

        // Level 11 — Recruiter: talent acquisition
        await EnsureRole(tenantId, "Recruiter", "Recruitment and hiring specialist", Ps(new[] {
            "dashboard.read", "employees.read", "recruitment.read", "recruitment.write",
            "notifications.read", "organization.read", "profile.read"
        }), 11, true, cancellationToken);

        // Level 12 — HR Assistant: limited HR support
        await EnsureRole(tenantId, "HR Assistant", "Junior HR support with limited write access", Ps(new[] {
            "dashboard.read", "employees.read", "organization.read", "notifications.read",
            "attendance.read", "leave.read", "ess.read", "profile.read", "localization.read"
        }), 12, true, cancellationToken);

        // Level 13 — Auditor: read-only audit
        await EnsureRole(tenantId, "Auditor", "Read-only audit and compliance reviewer", Ps(new[] {
            "dashboard.read", "employees.read", "organization.read", "approvals.read",
            "audit.read", "payroll.read", "attendance.read", "leave.read", "compliance.read", "reports.read",
            "qiwa.read"
        }), 13, true, cancellationToken);

        // Level 14 — Kiosk Operator: attendance kiosk only
        await EnsureRole(tenantId, "Kiosk Operator", "Restricted to kiosk attendance capture only", Ps(new[] {
            "attendance.kiosk"
        }), 14, true, cancellationToken);

        // Level 15 — Employee: self-service only
        await EnsureRole(tenantId, "Employee", "Employee self-service user", Ps(new[] {
            "dashboard.read", "profile.read", "ess.read", "ess.write"
        }), 15, true, cancellationToken);

        return adminRole;
    }

    private async Task EnsureGlobalCountryRules(Guid tenantId, CancellationToken ct)
    {
        if (await _db.CountryPayrollRules.AnyAsync(x => x.TenantId == tenantId, ct)) return;

        // (CountryCode, Currency, Weekend, AnnualLeaveDays, SickLeaveDays, ProbationMonths,
        //  NoticeDays, OtNormal, OtHoliday, EndOfServiceNote)
        var packs = new (string Country, string Currency, string Weekend, int Annual, int Sick, int Probation, int Notice, decimal OtNormal, decimal OtHoliday, string Eosb)[]
        {
            // GCC
            ("AE", "AED", "Fri-Sat", 30, 90, 6, 30, 1.25m, 1.50m, "Gratuity: 21 days/yr for first 5 yrs, 30 days/yr thereafter (UAE Labour Law)"),
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

        var rules = new List<CountryPayrollRule>();
        void Add(string country, string key, string val, string type, string desc) =>
            rules.Add(new CountryPayrollRule { TenantId = tenantId, CountryCode = country, RuleKey = key, RuleValue = val, DataType = type, Description = desc });

        foreach (var p in packs)
        {
            Add(p.Country, "default_currency", p.Currency, "string", "Default payroll currency");
            Add(p.Country, "weekend_days", p.Weekend, "string", "Statutory weekend");
            Add(p.Country, "annual_leave_days", p.Annual.ToString(), "int", "Statutory annual leave entitlement (days)");
            Add(p.Country, "sick_leave_days", p.Sick.ToString(), "int", "Statutory sick leave entitlement (days)");
            Add(p.Country, "probation_months", p.Probation.ToString(), "int", "Maximum probation period (months)");
            Add(p.Country, "notice_period_days", p.Notice.ToString(), "int", "Default notice period (days)");
            Add(p.Country, "overtime_multiplier_normal", p.OtNormal.ToString(System.Globalization.CultureInfo.InvariantCulture), "decimal", "Overtime multiplier — normal day");
            Add(p.Country, "overtime_multiplier_holiday", p.OtHoliday.ToString(System.Globalization.CultureInfo.InvariantCulture), "decimal", "Overtime multiplier — public holiday/rest day");
            Add(p.Country, "end_of_service", p.Eosb, "string", "End-of-service / gratuity rule");
        }
        _db.CountryPayrollRules.AddRange(rules);
        await _db.SaveChangesAsync(ct);
    }

    private async Task<List<Permission>> EnsurePermissions(CancellationToken cancellationToken)
    {
        var definitions = new (string Key, string Module, string Description)[]
        {
            // Dashboard
            ("dashboard.read", "Dashboard", "Read workforce dashboard metrics"),
            ("dashboard.export", "Dashboard", "Export dashboard data"),
            // Employees
            ("employees.read", "Employees", "Read employee records"),
            ("employees.write", "Employees", "Create and update employee records"),
            ("employees.delete", "Employees", "Delete/archive employee records"),
            ("employees.sensitive", "Employees", "View sensitive employee fields (salary, NID, passport)"),
            ("employees.approve", "Employees", "Approve employee drafts, changes, and transfers"),
            ("employees.documents", "Employees", "Upload and download employee documents"),
            ("employees.templates", "Employees", "Generate localized employee document templates"),
            ("employees.bulk_import", "Employees", "Bulk import employee records"),
            // Profile
            ("profile.read", "Profile", "Read own profile"),
            ("profile.write", "Profile", "Update own profile"),
            // Organization
            ("organization.read", "Organization", "Read companies, branches, departments, and designations"),
            ("organization.write", "Organization", "Create and update organization master data"),
            ("organization.delete", "Organization", "Delete organization master data"),
            // Attendance
            ("attendance.read", "Attendance", "Read attendance records"),
            ("attendance.write", "Attendance", "Create and update attendance records"),
            ("attendance.delete", "Attendance", "Delete or cancel attendance records"),
            ("attendance.kiosk", "Attendance", "Use kiosk-only attendance capture"),
            ("attendance.bulk_import", "Attendance", "Bulk import attendance data"),
            ("attendance.lock", "Attendance", "Lock/unlock attendance periods"),
            // Leave
            ("leave.read", "Leave", "Read leave requests and balances"),
            ("leave.write", "Leave", "Submit and manage leave requests"),
            ("leave.approve", "Leave", "Approve or reject leave requests"),
            ("leave.cancel", "Leave", "Cancel approved leave"),
            ("leave.policy_manage", "Leave", "Manage leave types and policies"),
            // Overtime
            ("overtime.read", "Overtime", "Read overtime requests"),
            ("overtime.write", "Overtime", "Submit overtime requests"),
            ("overtime.approve", "Overtime", "Approve or reject overtime requests"),
            ("overtime.policy_manage", "Overtime", "Manage overtime types and policies"),
            // Payroll
            ("payroll.read", "Payroll", "Read payroll runs and slips"),
            ("payroll.write", "Payroll", "Create and process payroll runs"),
            ("payroll.approve", "Payroll", "Approve payroll runs"),
            ("payroll.export", "Payroll", "Export payroll and WPS files"),
            ("payroll.structure_manage", "Payroll", "Manage salary structures and components"),
            // Loans & Advances
            ("loans.read", "Loans", "Read loan and advance records"),
            ("loans.write", "Loans", "Create loan and advance applications"),
            ("loans.approve", "Loans", "Approve or reject loans and advances"),
            ("loans.policy_manage", "Loans", "Manage loan types and policies"),
            // Recruitment
            ("recruitment.read", "Recruitment", "Read job openings and applications"),
            ("recruitment.write", "Recruitment", "Manage recruitment pipeline"),
            ("recruitment.approve", "Recruitment", "Approve requisitions and offers"),
            ("recruitment.delete", "Recruitment", "Delete recruitment records"),
            // Performance
            ("performance.read", "Performance", "Read appraisal and performance data"),
            ("performance.write", "Performance", "Create and update performance reviews"),
            ("performance.approve", "Performance", "Approve performance ratings and recommendations"),
            ("performance.cycle_manage", "Performance", "Manage performance cycles and templates"),
            // Compliance
            ("compliance.read", "Compliance", "Read compliance and contract records"),
            ("compliance.write", "Compliance", "Manage compliance documents"),
            ("compliance.approve", "Compliance", "Approve compliance items"),
            // Manager
            ("manager.read", "Manager", "Read direct and indirect team records"),
            ("manager.approve", "Manager", "Approve assigned team requests"),
            // ESS
            ("ess.read", "ESS", "Read own employee self-service records"),
            ("ess.write", "ESS", "Create employee self-service requests"),
            // Approvals
            ("approvals.read", "Approvals", "Read approval workflows and approval requests"),
            ("approvals.write", "Approvals", "Create approval workflows and start approval requests"),
            ("approvals.decide", "Approvals", "Approve or reject approval requests"),
            ("approvals.manage", "Approvals", "Manage approval workflow definitions"),
            // Reports
            ("reports.read", "Reports", "Run and view reports"),
            ("reports.schedule", "Reports", "Create scheduled reports"),
            ("reports.export", "Reports", "Export reports to Excel/PDF"),
            // Notifications
            ("notifications.read", "Notifications", "Read workflow notifications"),
            ("notifications.manage", "Notifications", "Manage notification templates"),
            // Localization
            ("localization.read", "Localization", "Read localized calendar conversions"),
            ("localization.manage", "Localization", "Manage localization and calendar settings"),
            // Access Control
            ("roles.manage", "Access", "Manage user roles and permissions"),
            ("users.manage", "Access", "Manage users and user accounts"),
            ("security.manage", "Security", "Manage security settings and access policies"),
            // Audit
            ("audit.read", "Audit", "Read audit logs"),
            ("audit.export", "Audit", "Export audit logs"),
            // AI & Intelligence
            ("ai.query", "AI", "Query the AI HR assistant"),
            ("ai.insights_view", "AI", "View AI-generated workforce insights"),
            // Shifts
            ("shifts.read", "Shifts", "Read shift schedules and rosters"),
            ("shifts.write", "Shifts", "Create and update shift schedules"),
            ("shifts.manage", "Shifts", "Manage shift definitions and policies"),
            // QIWA (Saudi workforce platform)
            ("qiwa.configure", "QIWA", "Configure QIWA establishment credentials and connection"),
            ("qiwa.sync", "QIWA", "Trigger and manage QIWA employee sync operations"),
            ("qiwa.read", "QIWA", "View QIWA connection status and sync logs"),
        };

        foreach (var definition in definitions)
        {
            if (!await _db.Permissions.AnyAsync(x => x.Key == definition.Key, cancellationToken))
            {
                _db.Permissions.Add(new Permission { Key = definition.Key, Module = definition.Module, Description = definition.Description });
            }
        }
        await _db.SaveChangesAsync(cancellationToken);
        return await _db.Permissions.ToListAsync(cancellationToken);
    }

    private async Task<Role> EnsureRole(Guid tenantId, string name, string description, IReadOnlyCollection<Permission> permissions, int authorityLevel, bool isEditable, CancellationToken cancellationToken)
    {
        var normalized = AuthService.Normalize(name);
        var role = await _db.Roles
            .Include(x => x.RolePermissions)
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.NormalizedName == normalized, cancellationToken);
        if (role is null)
        {
            role = new Role
            {
                TenantId = tenantId, Name = name, NormalizedName = normalized, Description = description,
                IsSystem = true, AuthorityLevel = authorityLevel, IsActive = true, IsEditable = isEditable
            };
            _db.Roles.Add(role);
            await _db.SaveChangesAsync(cancellationToken);
        }
        else
        {
            role.AuthorityLevel = authorityLevel;
            role.IsEditable = isEditable;
            role.Description = description;
        }

        foreach (var permission in permissions)
        {
            if (!role.RolePermissions.Any(x => x.PermissionId == permission.Id))
            {
                role.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = permission.Id });
            }
        }
        await _db.SaveChangesAsync(cancellationToken);
        return role;
    }

    private async Task EnsureFoundationSeedData(Guid tenantId, CancellationToken cancellationToken)
    {
        var company = await _db.Companies.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.RegistrationNumber == "KNX-DEMO", cancellationToken);
        if (company is null)
        {
            company = new Company
            {
                TenantId = tenantId,
                LegalNameEn = "KynexOne Technologies Inc.",
                LegalNameAr = "كينكس ون للتقنية",
                TradeName = "KynexOne",
                CountryCode = "US",
                RegistrationNumber = "KNX-DEMO",
                DefaultCurrency = "USD"
            };
            _db.Companies.Add(company);
            await _db.SaveChangesAsync(cancellationToken);
        }

        var branch = await _db.Branches.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Code == "NYC-HQ", cancellationToken);
        if (branch is null)
        {
            branch = new Branch
            {
                TenantId = tenantId,
                CompanyId = company.Id,
                Code = "NYC-HQ",
                NameEn = "New York Headquarters",
                NameAr = "المقر الرئيسي نيويورك",
                CountryCode = "US",
                City = "New York",
                AddressLine1 = "350 Fifth Avenue",
                TimeZoneId = "America/New_York",
                IsHeadOffice = true
            };
            _db.Branches.Add(branch);
            await _db.SaveChangesAsync(cancellationToken);
        }

        var department = await _db.Departments.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Code == "HR", cancellationToken);
        if (department is null)
        {
            department = new Department
            {
                TenantId = tenantId,
                BranchId = branch.Id,
                Code = "HR",
                NameEn = "Human Resources",
                NameAr = "الموارد البشرية"
            };
            _db.Departments.Add(department);
            await _db.SaveChangesAsync(cancellationToken);
        }

        var grade = await _db.Grades.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Code == "G5", cancellationToken);
        if (grade is null)
        {
            grade = new Grade
            {
                TenantId = tenantId,
                Code = "G5",
                Name = "Professional Grade 5",
                Band = "Professional",
                Level = 5
            };
            _db.Grades.Add(grade);
            await _db.SaveChangesAsync(cancellationToken);
        }

        var costCenter = await _db.CostCenters.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Code == "HR-OPS", cancellationToken);
        if (costCenter is null)
        {
            costCenter = new CostCenter
            {
                TenantId = tenantId,
                CompanyId = company.Id,
                Code = "HR-OPS",
                Name = "HR Operations"
            };
            _db.CostCenters.Add(costCenter);
            await _db.SaveChangesAsync(cancellationToken);
        }

        if (department.CostCenterId is null)
        {
            department.CostCenterId = costCenter.Id;
            department.UpdatedAtUtc = DateTime.UtcNow;
        }

        if (!await _db.Designations.AnyAsync(x => x.TenantId == tenantId && x.Code == "HR-OFFICER", cancellationToken))
        {
            _db.Designations.Add(new Designation
            {
                TenantId = tenantId,
                DepartmentId = department.Id,
                GradeId = grade.Id,
                Code = "HR-OFFICER",
                TitleEn = "HR Officer",
                TitleAr = "مسؤول الموارد البشرية",
                JobGrade = "G5",
                JobLevel = "Officer",
                JobDescription = "Supports employee lifecycle administration, records, and compliance workflows."
            });
        }

        if (!await _db.EmployeeIdRules.AnyAsync(x => x.TenantId == tenantId && x.CompanyId == company.Id && x.IsActive && !x.IsDeleted, cancellationToken))
        {
            _db.EmployeeIdRules.Add(new EmployeeIdRule
            {
                TenantId = tenantId,
                CompanyId = company.Id,
                Name = "Default GCC employee ID rule",
                CompanyPrefix = "ZAY",
                UseCountryPrefix = true,
                UseBranchPrefix = false,
                UseDepartmentPrefix = true,
                UseYear = true,
                PaddingLength = 4,
                NextSequence = 1,
                AllowManualOverride = false
            });
        }

        if (!await _db.AttendancePolicies.AnyAsync(x => x.TenantId == tenantId && x.Code == "DEFAULT", cancellationToken))
        {
            _db.AttendancePolicies.Add(new AttendancePolicy
            {
                TenantId = tenantId,
                Code = "DEFAULT",
                Name = "Default attendance policy",
                GraceMinutes = 10,
                LateThresholdMinutes = 15,
                EarlyExitThresholdMinutes = 15,
                HalfDayThresholdMinutes = 240,
                AbsentThresholdMinutes = 120,
                StandardWorkMinutes = 480,
                BreakMinutes = 60,
                RequiresOvertimeApproval = true,
                AllowAbsenceToLeaveConversion = true
            });
        }

        if (!await _db.ApprovalWorkflows.AnyAsync(x => x.TenantId == tenantId && x.Code == "EMPLOYEE-ONBOARDING", cancellationToken))
        {
            var onboarding = new ApprovalWorkflow
            {
                TenantId = tenantId,
                Code = "EMPLOYEE-ONBOARDING",
                Name = "Employee Onboarding Approval",
                EntityName = "EmployeeDraft"
            };
            onboarding.Steps.Add(new ApprovalWorkflowStep { TenantId = tenantId, WorkflowId = onboarding.Id, StepOrder = 1, StepName = "HR Review", ApproverRole = "HR Manager", IsFinalStep = true });
            _db.ApprovalWorkflows.Add(onboarding);
        }

        if (!await _db.ApprovalWorkflows.AnyAsync(x => x.TenantId == tenantId && x.Code == "EMPLOYEE-TRANSFER", cancellationToken))
        {
            var transfer = new ApprovalWorkflow
            {
                TenantId = tenantId,
                Code = "EMPLOYEE-TRANSFER",
                Name = "Employee Transfer Approval",
                EntityName = "EmployeeTransferRequest"
            };
            transfer.Steps.Add(new ApprovalWorkflowStep { TenantId = tenantId, WorkflowId = transfer.Id, StepOrder = 1, StepName = "Current Manager Approval", ApproverRole = "Manager" });
            transfer.Steps.Add(new ApprovalWorkflowStep { TenantId = tenantId, WorkflowId = transfer.Id, StepOrder = 2, StepName = "HR Approval", ApproverRole = "HR Manager", IsFinalStep = true });
            _db.ApprovalWorkflows.Add(transfer);
        }

        await _db.SaveChangesAsync(cancellationToken);

        try
        {
            await EnsureDemoOperationalData(tenantId, company.Id, branch.Id, cancellationToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Seed] Demo operational data skipped: {ex.Message}");
        }
    }

    private record DemoPerson(string Code, string Name, string Dept, string Title, string Type, string Gender, string Nationality, decimal Basic, int TenureDays = 0);

    // Bump this when the demo dataset grows: databases seeded with an older version
    // top up the missing sections on next startup (each section guards itself).
    private const int DemoSeedVersion = 3;

    private async Task EnsureDemoOperationalData(Guid tenantId, Guid companyId, Guid branchId, CancellationToken ct)
    {
        var seedFlag = await _db.TenantFeatureFlags.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.FeatureKey == "demo_seed_version", ct);
        if (seedFlag is not null && int.TryParse(seedFlag.ConfigJson, out var seededVersion) && seededVersion >= DemoSeedVersion) return;

        // ── Boston branch ────────────────────────────────────────────────────────
        var abuDhabiBranch = await _db.Branches.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Code == "AUH-BR", ct);
        if (abuDhabiBranch is null)
        {
            abuDhabiBranch = new Branch
            {
                TenantId = tenantId, CompanyId = companyId, Code = "AUH-BR",
                NameEn = "Boston Office", NameAr = "مكتب بوسطن",
                CountryCode = "US", City = "Boston", AddressLine1 = "100 Federal Street",
                TimeZoneId = "America/New_York", IsHeadOffice = false
            };
            _db.Branches.Add(abuDhabiBranch);
            await _db.SaveChangesAsync(ct);
        }

        // ── Departments ──────────────────────────────────────────────────────────
        var deptDefs = new (string Code, string En, string Ar)[]
        {
            ("ENG", "Engineering", "الهندسة"),
            ("FIN", "Finance", "المالية"),
            ("OPS", "Operations", "العمليات"),
            ("SAL", "Sales", "المبيعات"),
            ("IT",  "Information Technology", "تقنية المعلومات"),
            ("MKT", "Marketing", "التسويق"),
            ("LGL", "Legal & Compliance", "الشؤون القانونية"),
        };
        foreach (var d in deptDefs)
        {
            if (!await _db.Departments.AnyAsync(x => x.TenantId == tenantId && x.Code == d.Code, ct))
                _db.Departments.Add(new Department { TenantId = tenantId, BranchId = branchId, Code = d.Code, NameEn = d.En, NameAr = d.Ar });
        }
        await _db.SaveChangesAsync(ct);

        // ── Employees (25) ───────────────────────────────────────────────────────
        var people = new[]
        {
            new DemoPerson("KNX-0001", "Aisha Al Mansoori",   "Human Resources",        "HR Director",               "Full-time", "Female", "Emirati",    24000m, 900),
            new DemoPerson("KNX-0002", "Omar Khalifa",         "Engineering",             "Engineering Manager",        "Full-time", "Male",   "Emirati",    22000m, 780),
            new DemoPerson("KNX-0003", "Priya Nair",           "Finance",                 "Finance Manager",            "Full-time", "Female", "Indian",     19000m, 650),
            new DemoPerson("KNX-0004", "James Carter",         "Engineering",             "Senior Software Engineer",   "Full-time", "Male",   "British",    18000m, 500),
            new DemoPerson("KNX-0005", "Fatima Al Hashimi",    "Human Resources",        "HR Officer",                 "Full-time", "Female", "Emirati",    12000m, 420),
            new DemoPerson("KNX-0006", "Rahul Mehta",          "Operations",              "Operations Lead",            "Full-time", "Male",   "Indian",     14000m, 390),
            new DemoPerson("KNX-0007", "Sara Abdullah",        "Sales",                   "Senior Account Executive",   "Full-time", "Female", "Saudi",      13000m, 350),
            new DemoPerson("KNX-0008", "Daniel Okoro",         "Operations",              "Logistics Coordinator",      "Full-time", "Male",   "Nigerian",    9000m, 310),
            new DemoPerson("KNX-0009", "Ahmed Al Rashid",      "Information Technology", "IT Manager",                 "Full-time", "Male",   "Emirati",    20000m, 720),
            new DemoPerson("KNX-0010", "Nadia Farouq",         "Marketing",               "Marketing Manager",          "Full-time", "Female", "Egyptian",   16000m, 480),
            new DemoPerson("KNX-0011", "Tariq Hassan",         "Engineering",             "Software Engineer",          "Full-time", "Male",   "Pakistani",  14000m, 270),
            new DemoPerson("KNX-0012", "Maryam Yusuf",         "Finance",                 "Senior Accountant",          "Full-time", "Female", "Emirati",    13000m, 440),
            new DemoPerson("KNX-0013", "Ravi Shankar",         "Engineering",             "DevOps Engineer",            "Full-time", "Male",   "Indian",     16000m, 360),
            new DemoPerson("KNX-0014", "Hana Kim",             "Marketing",               "Content & Brand Specialist", "Full-time", "Female", "Korean",     11000m, 200),
            new DemoPerson("KNX-0015", "Abdullah Al Zaabi",    "Sales",                   "Sales Manager",              "Full-time", "Male",   "Emirati",    21000m, 810),
            new DemoPerson("KNX-0016", "Lina Abboud",          "Legal & Compliance",      "Legal Counsel",              "Full-time", "Female", "Lebanese",   18000m, 560),
            new DemoPerson("KNX-0017", "Vikram Singh",         "Information Technology", "Systems Administrator",      "Full-time", "Male",   "Indian",     13000m, 300),
            new DemoPerson("KNX-0018", "Noura Al Suwaidi",     "Human Resources",        "HR Coordinator",             "Full-time", "Female", "Emirati",    10000m, 180),
            new DemoPerson("KNX-0019", "Marcus Johnson",       "Engineering",             "QA Engineer",                "Full-time", "Male",   "American",   15000m, 230),
            new DemoPerson("KNX-0020", "Amira Benali",         "Finance",                 "Financial Analyst",          "Full-time", "Female", "Moroccan",   14000m, 290),
            new DemoPerson("KNX-0021", "Khalid Al Mazrouei",   "Operations",              "Warehouse Manager",          "Full-time", "Male",   "Emirati",    15000m, 670),
            new DemoPerson("KNX-0022", "Deepa Thomas",         "Information Technology", "Business Analyst",           "Full-time", "Female", "Indian",     13000m, 240),
            new DemoPerson("KNX-0023", "Faisal Al Hajri",      "Sales",                   "Business Dev Manager",       "Full-time", "Male",   "Qatari",     19000m, 580),
            new DemoPerson("KNX-0024", "Yuki Tanaka",          "Marketing",               "Digital Marketing Lead",     "Contract",  "Female", "Japanese",   12000m, 150),
            new DemoPerson("KNX-0025", "Carlos Mendez",        "Operations",              "Supply Chain Analyst",       "Full-time", "Male",   "Filipino",   11000m, 120),
        };

        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var employees = new List<Employee>();
        foreach (var (p, i) in people.Select((p, i) => (p, i)))
        {
            var existing = await _db.Employees.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.EmployeeCode == p.Code, ct);
            if (existing is not null) { employees.Add(existing); continue; }
            var loc = i < 20 ? "New York HQ" : "Boston";
            var bId = i < 20 ? branchId : abuDhabiBranch.Id;
            var emp = new Employee
            {
                TenantId = tenantId, CompanyId = companyId, BranchId = bId,
                EmployeeCode = p.Code, FullName = p.Name, EnglishName = p.Name,
                WorkEmail = $"{p.Name.Split(' ')[0].ToLowerInvariant()}.{p.Name.Split(' ')[^1].ToLowerInvariant()}@kynexone.com",
                Phone = $"+9715{(50000000 + i):00000000}",
                Gender = p.Gender, Nationality = p.Nationality, CountryCode = "US",
                Department = p.Dept, Designation = p.Title, JobTitle = p.Title,
                EmploymentType = p.Type, ContractType = p.Type == "Contract" ? "Fixed-term" : "Permanent",
                WorkLocation = loc, Status = "Active",
                JoiningDate = DateTime.UtcNow.AddDays(-p.TenureDays),
            };
            _db.Employees.Add(emp);
            employees.Add(emp);
        }
        await _db.SaveChangesAsync(ct);

        // ── Attendance (last 6 months of working days) ───────────────────────────
        // Per-employee guard: employees that already have attendance keep it; new ones get history.
        var attendanceSeededIds = await _db.AttendanceRecords.Where(x => x.TenantId == tenantId)
            .Select(x => x.EmployeeId).Distinct().ToListAsync(ct);
        var attendanceTargets = employees.Where(e => !attendanceSeededIds.Contains(e.Id)).ToList();
        var rng = new Random(42);
        var attendance = new List<AttendanceRecord>();
        for (var offset = 0; offset <= 180; offset++)
        {
            var date = today.AddDays(-offset);
            if (date.DayOfWeek is DayOfWeek.Friday or DayOfWeek.Saturday) continue;
            foreach (var emp in attendanceTargets)
            {
                var roll = rng.Next(100);
                var status = roll < 83 ? "Present" : roll < 91 ? "Late" : roll < 96 ? "Leave" : "Absent";
                attendance.Add(new AttendanceRecord
                {
                    TenantId = tenantId, EmployeeId = emp.Id, WorkDate = date, Status = status,
                    OvertimeHours = status == "Present" && rng.Next(100) < 18 ? rng.Next(1, 4) : 0,
                    Notes = string.Empty,
                });
            }
        }
        _db.AttendanceRecords.AddRange(attendance);
        await _db.SaveChangesAsync(ct);

        // ── Leave types ──────────────────────────────────────────────────────────
        var leaveTypeDefs = new (string Code, string En, string Ar, string Cat, bool Paid)[]
        {
            ("ANNUAL",    "Annual Leave",    "إجازة سنوية",       "Annual",    true),
            ("SICK",      "Sick Leave",      "إجازة مرضية",       "Sick",      true),
            ("CASUAL",    "Casual Leave",    "إجازة عارضة",       "Casual",    true),
            ("MATERNITY", "Maternity Leave", "إجازة أمومة",       "Maternity", true),
            ("PATERNITY", "Paternity Leave", "إجازة الأبوة",      "Paternity", true),
            ("HAJJ",      "Hajj Leave",      "إجازة الحج",        "Religious", true),
            ("UNPAID",    "Unpaid Leave",    "إجازة بدون راتب",   "Unpaid",    false),
        };
        var leaveTypes = new List<LeaveType>();
        var ltSort = 0;
        foreach (var lt in leaveTypeDefs)
        {
            var existing = await _db.LeaveTypes.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Code == lt.Code, ct);
            if (existing is not null) { leaveTypes.Add(existing); continue; }
            var entity = new LeaveType { TenantId = tenantId, Code = lt.Code, NameEn = lt.En, NameAr = lt.Ar, Category = lt.Cat, IsPaid = lt.Paid, IsActive = true, SortOrder = ltSort++ };
            _db.LeaveTypes.Add(entity);
            leaveTypes.Add(entity);
        }
        await _db.SaveChangesAsync(ct);

        // ── Leave requests ───────────────────────────────────────────────────────
        var annual   = leaveTypes.First(x => x.Code == "ANNUAL");
        var sick     = leaveTypes.First(x => x.Code == "SICK");
        var casual   = leaveTypes.First(x => x.Code == "CASUAL");
        var unpaid   = leaveTypes.First(x => x.Code == "UNPAID");
        if (!await _db.LeaveRequests.AnyAsync(x => x.TenantId == tenantId, ct))
        _db.LeaveRequests.AddRange(
            new LeaveRequest { TenantId=tenantId, EmployeeId=employees[3].Id,  EmployeeName=employees[3].FullName,  DepartmentName=employees[3].Department,  LeaveTypeId=annual.Id,  LeaveTypeName=annual.NameEn,  StartDate=today.AddDays(5),   EndDate=today.AddDays(9),   DayType="Full", Reason="Family vacation",            Status="Submitted", SubmittedAtUtc=DateTime.UtcNow.AddDays(-1) },
            new LeaveRequest { TenantId=tenantId, EmployeeId=employees[5].Id,  EmployeeName=employees[5].FullName,  DepartmentName=employees[5].Department,  LeaveTypeId=sick.Id,    LeaveTypeName=sick.NameEn,    StartDate=today.AddDays(-2),  EndDate=today.AddDays(-1),  DayType="Full", Reason="Flu",                        Status="Approved", SubmittedAtUtc=DateTime.UtcNow.AddDays(-3), DecidedAtUtc=DateTime.UtcNow.AddDays(-2) },
            new LeaveRequest { TenantId=tenantId, EmployeeId=employees[6].Id,  EmployeeName=employees[6].FullName,  DepartmentName=employees[6].Department,  LeaveTypeId=annual.Id,  LeaveTypeName=annual.NameEn,  StartDate=today.AddDays(12),  EndDate=today.AddDays(14),  DayType="Full", Reason="Personal",                   Status="Submitted", SubmittedAtUtc=DateTime.UtcNow.AddHours(-6) },
            new LeaveRequest { TenantId=tenantId, EmployeeId=employees[9].Id,  EmployeeName=employees[9].FullName,  DepartmentName=employees[9].Department,  LeaveTypeId=casual.Id,  LeaveTypeName=casual.NameEn,  StartDate=today.AddDays(2),   EndDate=today.AddDays(2),   DayType="Full", Reason="Bank errand",                Status="Approved", SubmittedAtUtc=DateTime.UtcNow.AddDays(-2), DecidedAtUtc=DateTime.UtcNow.AddDays(-1) },
            new LeaveRequest { TenantId=tenantId, EmployeeId=employees[11].Id, EmployeeName=employees[11].FullName, DepartmentName=employees[11].Department, LeaveTypeId=sick.Id,    LeaveTypeName=sick.NameEn,    StartDate=today.AddDays(-5),  EndDate=today.AddDays(-4),  DayType="Full", Reason="Medical checkup",            Status="Approved", SubmittedAtUtc=DateTime.UtcNow.AddDays(-6), DecidedAtUtc=DateTime.UtcNow.AddDays(-5) },
            new LeaveRequest { TenantId=tenantId, EmployeeId=employees[14].Id, EmployeeName=employees[14].FullName, DepartmentName=employees[14].Department, LeaveTypeId=annual.Id,  LeaveTypeName=annual.NameEn,  StartDate=today.AddDays(20),  EndDate=today.AddDays(30),  DayType="Full", Reason="Summer holiday",             Status="Submitted", SubmittedAtUtc=DateTime.UtcNow.AddHours(-3) },
            new LeaveRequest { TenantId=tenantId, EmployeeId=employees[18].Id, EmployeeName=employees[18].FullName, DepartmentName=employees[18].Department, LeaveTypeId=sick.Id,    LeaveTypeName=sick.NameEn,    StartDate=today.AddDays(-1),  EndDate=today.AddDays(-1),  DayType="Full", Reason="Headache",                   Status="Submitted", SubmittedAtUtc=DateTime.UtcNow.AddHours(-10) },
            new LeaveRequest { TenantId=tenantId, EmployeeId=employees[20].Id, EmployeeName=employees[20].FullName, DepartmentName=employees[20].Department, LeaveTypeId=unpaid.Id,  LeaveTypeName=unpaid.NameEn,  StartDate=today.AddDays(-15), EndDate=today.AddDays(-11), DayType="Full", Reason="Emergency travel",            Status="Approved", SubmittedAtUtc=DateTime.UtcNow.AddDays(-18), DecidedAtUtc=DateTime.UtcNow.AddDays(-17) },
            new LeaveRequest { TenantId=tenantId, EmployeeId=employees[22].Id, EmployeeName=employees[22].FullName, DepartmentName=employees[22].Department, LeaveTypeId=annual.Id,  LeaveTypeName=annual.NameEn,  StartDate=today.AddDays(7),   EndDate=today.AddDays(11),  DayType="Full", Reason="Eid Al-Adha extended break",  Status="Submitted", SubmittedAtUtc=DateTime.UtcNow.AddHours(-1) }
        );
        await _db.SaveChangesAsync(ct);

        // ── Payroll runs: 6 completed months + 1 pending current month ───────────
        var payrollPeople = people.ToDictionary(p => p.Code);
        for (var m = 6; m >= 0; m--)
        {
            var period = new DateOnly(today.Year, today.Month, 1).AddMonths(-m);
            if (await _db.PayrollRuns.AnyAsync(x => x.TenantId == tenantId && x.Year == period.Year && x.Month == period.Month, ct)) continue;
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
                if (!payrollPeople.TryGetValue(emp.EmployeeCode, out var pd)) continue;
                var basic = pd.Basic * (1 + (rng.Next(3) == 0 ? 0.05m : 0m)); // occasional increment
                var housing = Math.Round(basic * 0.25m);
                var transport = 1000m;
                var gross = basic + housing + transport;
                var deductions = Math.Round(gross * 0.05m);
                var net = gross - deductions;
                tg += gross; td += deductions; tn += net;
                slips.Add(new PayrollSlip
                {
                    TenantId=tenantId, RunId=run.Id, EmployeeId=emp.Id, EmployeeCode=emp.EmployeeCode,
                    EmployeeName=emp.FullName, Department=emp.Department, BasicSalary=pd.Basic,
                    HousingAllowance=housing, TransportAllowance=transport, OtherAllowances=0,
                    GrossSalary=gross, Deductions=deductions, NetSalary=net,
                    Status=isPending ? "Draft" : "Paid",
                });
            }
            run.TotalGrossSalary=tg; run.TotalDeductions=td; run.TotalNetSalary=tn; run.EmployeeCount=slips.Count;
            _db.PayrollRuns.Add(run);
            _db.PayrollSlips.AddRange(slips);
            await _db.SaveChangesAsync(ct);
        }

        // ── Compliance records ───────────────────────────────────────────────────
        var complianceData = new (int EmpIdx, string Key, string Label, string Value, int ExpiryDays)[]
        {
            (1,  "passport",    "Passport",          "P1234567", 18),
            (3,  "visa",        "Residence Visa",    "V998877",  45),
            (6,  "emirates_id", "Emirates ID",       "784-1001", -3),
            (8,  "passport",    "Passport",          "P7654321", 30),
            (10, "visa",        "Residence Visa",    "V112233",  60),
            (13, "emirates_id", "Emirates ID",       "784-2002", 12),
            (15, "visa",        "Residence Visa",    "V445566",  -8),
            (17, "passport",    "Passport",          "P9988776", 90),
            (19, "emirates_id", "Emirates ID",       "784-3003", 22),
            (21, "visa",        "Residence Visa",    "V667788",  55),
        };
        if (!await _db.EmployeeComplianceRecords.AnyAsync(x => x.TenantId == tenantId && x.ExpiryDate != null, ct))
        foreach (var c in complianceData)
        {
            _db.EmployeeComplianceRecords.Add(new EmployeeComplianceRecord
            {
                TenantId=tenantId, EmployeeId=employees[c.EmpIdx].Id, CountryCode="US",
                FieldKey=c.Key, FieldLabel=c.Label, FieldValue=c.Value,
                ExpiryDate=today.AddDays(c.ExpiryDays), IsRequired=true
            });
        }
        await _db.SaveChangesAsync(ct);

        // ── Recruitment: job openings + candidates (per-record top-up) ───────────
        var openingDefs = new[]
        {
            new JobOpening { TenantId=tenantId, JobCode="JOB-2026-0001", Title="Senior Software Engineer",    DepartmentName="Engineering",             EmploymentType="Full-Time", HeadCount=2, FilledCount=0, Location="New York HQ",   SalaryFrom=18000, SalaryTo=26000, Status="Open",       Description="Build and scale platform microservices.", PublishedAtUtc=DateTime.UtcNow.AddDays(-12) },
            new JobOpening { TenantId=tenantId, JobCode="JOB-2026-0002", Title="Payroll Specialist",          DepartmentName="Finance",                  EmploymentType="Full-Time", HeadCount=1, FilledCount=0, Location="New York HQ",   SalaryFrom=11000, SalaryTo=15000, Status="Open",       Description="Own monthly WPS payroll processing.",     PublishedAtUtc=DateTime.UtcNow.AddDays(-6) },
            new JobOpening { TenantId=tenantId, JobCode="JOB-2026-0003", Title="Sales Account Executive",     DepartmentName="Sales",                    EmploymentType="Full-Time", HeadCount=3, FilledCount=1, Location="Boston",  SalaryFrom=12000, SalaryTo=18000, Status="InProgress", Description="Drive enterprise SaaS sales in North America.",     PublishedAtUtc=DateTime.UtcNow.AddDays(-20) },
            new JobOpening { TenantId=tenantId, JobCode="JOB-2026-0004", Title="HR Business Partner",         DepartmentName="Human Resources",          EmploymentType="Full-Time", HeadCount=1, FilledCount=0, Location="New York HQ",   SalaryFrom=16000, SalaryTo=21000, Status="Open",       Description="Strategic HR partner for tech divisions.",PublishedAtUtc=DateTime.UtcNow.AddDays(-4) },
            new JobOpening { TenantId=tenantId, JobCode="JOB-2026-0005", Title="Cloud Infrastructure Engineer",DepartmentName="Information Technology",  EmploymentType="Full-Time", HeadCount=2, FilledCount=0, Location="New York HQ",   SalaryFrom=17000, SalaryTo=24000, Status="Open",       Description="Manage Azure/AWS cloud workloads.",       PublishedAtUtc=DateTime.UtcNow.AddDays(-9) },
            new JobOpening { TenantId=tenantId, JobCode="JOB-2026-0006", Title="Operations Supervisor",       DepartmentName="Operations",               EmploymentType="Full-Time", HeadCount=1, FilledCount=0, Location="Sharjah",    SalaryFrom=10000, SalaryTo=14000, Status="Open",       Description="Oversee warehouse operations.",           PublishedAtUtc=DateTime.UtcNow.AddDays(-15) }
        };
        var existingJobCodes = await _db.JobOpenings.Where(x => x.TenantId == tenantId).Select(x => x.JobCode).ToListAsync(ct);
        _db.JobOpenings.AddRange(openingDefs.Where(o => !existingJobCodes.Contains(o.JobCode)));

        var candidateDefs = new[]
        {
            new Candidate { TenantId=tenantId, FirstName="Layla",    LastName="Haddad",   Email="layla.haddad@example.com",  Phone="+971551110001", CurrentJobTitle="Software Engineer",  CurrentCompany="TechCorp",    TotalExperienceYears=6,  EducationLevel="Bachelor", Nationality="Lebanese",   Source="LinkedIn",  Status="Active" },
            new Candidate { TenantId=tenantId, FirstName="Mohammed", LastName="Raza",     Email="m.raza@example.com",        Phone="+971551110002", CurrentJobTitle="Payroll Analyst",    CurrentCompany="Finance Ltd", TotalExperienceYears=4,  EducationLevel="Bachelor", Nationality="Pakistani",  Source="Referral",  Status="Active" },
            new Candidate { TenantId=tenantId, FirstName="Elena",    LastName="Petrova",  Email="elena.p@example.com",       Phone="+971551110003", CurrentJobTitle="Account Executive",  CurrentCompany="SaaS Inc",    TotalExperienceYears=8,  EducationLevel="Master",   Nationality="Russian",    Source="Agency",    Status="Active" },
            new Candidate { TenantId=tenantId, FirstName="Yousef",   LastName="Salem",    Email="yousef.salem@example.com",  Phone="+971551110004", CurrentJobTitle="Backend Engineer",   CurrentCompany="Cloud Co",    TotalExperienceYears=5,  EducationLevel="Bachelor", Nationality="Jordanian",  Source="JobBoard",  Status="Active" },
            new Candidate { TenantId=tenantId, FirstName="Aditya",   LastName="Kumar",    Email="aditya.k@example.com",      Phone="+971551110005", CurrentJobTitle="DevOps Engineer",    CurrentCompany="Infra Ltd",   TotalExperienceYears=7,  EducationLevel="Bachelor", Nationality="Indian",     Source="LinkedIn",  Status="Active" },
            new Candidate { TenantId=tenantId, FirstName="Reem",     LastName="Al Hosani",Email="reem.h@example.com",        Phone="+971551110006", CurrentJobTitle="HR Manager",         CurrentCompany="Corp Group",  TotalExperienceYears=9,  EducationLevel="Master",   Nationality="Emirati",    Source="Referral",  Status="Active" },
            new Candidate { TenantId=tenantId, FirstName="David",    LastName="Nguyen",   Email="d.nguyen@example.com",      Phone="+971551110007", CurrentJobTitle="Full Stack Engineer", CurrentCompany="StartupXY",  TotalExperienceYears=4,  EducationLevel="Bachelor", Nationality="Vietnamese", Source="JobBoard",  Status="Active" },
            new Candidate { TenantId=tenantId, FirstName="Mariam",   LastName="Kassem",   Email="m.kassem@example.com",      Phone="+971551110008", CurrentJobTitle="Sales Executive",    CurrentCompany="Gulf Sales",  TotalExperienceYears=5,  EducationLevel="Bachelor", Nationality="Egyptian",   Source="LinkedIn",  Status="Active" }
        };
        var existingCandidateEmails = await _db.Candidates.Where(x => x.TenantId == tenantId).Select(x => x.Email).ToListAsync(ct);
        _db.Candidates.AddRange(candidateDefs.Where(c => !existingCandidateEmails.Contains(c.Email)));
        await _db.SaveChangesAsync(ct);

        // ── Performance: two cycles ───────────────────────────────────────────────
        var ratings = new[] { "Outstanding", "Exceeds Expectations", "Meets Expectations", "Meets Expectations", "Needs Improvement" };
        foreach (var (cycleName, daysAgo, status) in new[] {
            ("H2 2025 Performance Review", 95, "Published"),
            ("H1 2026 Performance Review",  5, "Published"),
        })
        {
            // Per-cycle guard: full cycle exists → skip; partial (old smaller seed) → replace.
            var cycleCount = await _db.AppraisalReviews.CountAsync(x => x.TenantId == tenantId && x.CycleName == cycleName, ct);
            if (cycleCount >= 20) continue;
            if (cycleCount > 0)
                await _db.AppraisalReviews.Where(x => x.TenantId == tenantId && x.CycleName == cycleName).ExecuteDeleteAsync(ct);
            var cycleId = Guid.NewGuid();
            var reviews = employees.Take(20).Select((emp, i) =>
            {
                var kpi  = 3.0m + (i % 5) * 0.4m;
                var comp = 3.1m + (i % 4) * 0.35m;
                var final = Math.Round((kpi + comp) / 2m, 2);
                return new AppraisalReview
                {
                    TenantId=tenantId, CycleId=cycleId, CycleName=cycleName,
                    ScorecardTemplateId=Guid.NewGuid(), EmployeeId=emp.Id, EmployeeName=emp.FullName,
                    DepartmentName=emp.Department, DesignationTitle=emp.Designation,
                    KpiScore=kpi, CompetencyScore=comp, AttendanceScore=4.0m + (i % 3) * 0.2m, ProductivityScore=3.5m + (i % 4) * 0.25m,
                    FinalScore=final, FinalRating=ratings[i % ratings.Length], Status=status,
                    PublishedAt=DateTime.UtcNow.AddDays(-daysAgo),
                };
            }).ToList();
            _db.AppraisalReviews.AddRange(reviews);
        }
        await _db.SaveChangesAsync(ct);

        // ── Pending approvals ────────────────────────────────────────────────────
        var workflowId = (await _db.ApprovalWorkflows.FirstOrDefaultAsync(x => x.TenantId == tenantId, ct))?.Id ?? Guid.NewGuid();
        var currentPeriod = new DateOnly(today.Year, today.Month, 1);
        var currentRunId = (await _db.PayrollRuns.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Year == currentPeriod.Year && x.Month == currentPeriod.Month, ct))?.Id ?? Guid.NewGuid();
        if (!await _db.ApprovalRequests.AnyAsync(x => x.TenantId == tenantId && x.Status == "Pending", ct))
        _db.ApprovalRequests.AddRange(
            new ApprovalRequest { TenantId=tenantId, WorkflowId=workflowId, EntityName="LeaveRequest",         EntityId=Guid.NewGuid().ToString(), Title=$"Annual leave — {employees[3].FullName}",         Status="Pending", CurrentStepOrder=1, CreatedAtUtc=DateTime.UtcNow.AddDays(-1) },
            new ApprovalRequest { TenantId=tenantId, WorkflowId=workflowId, EntityName="LeaveRequest",         EntityId=Guid.NewGuid().ToString(), Title=$"Annual leave — {employees[14].FullName}",        Status="Pending", CurrentStepOrder=1, CreatedAtUtc=DateTime.UtcNow.AddHours(-3) },
            new ApprovalRequest { TenantId=tenantId, WorkflowId=workflowId, EntityName="LeaveRequest",         EntityId=Guid.NewGuid().ToString(), Title=$"Sick leave — {employees[18].FullName}",          Status="Pending", CurrentStepOrder=1, CreatedAtUtc=DateTime.UtcNow.AddHours(-10) },
            new ApprovalRequest { TenantId=tenantId, WorkflowId=workflowId, EntityName="PayrollRun",           EntityId=currentRunId.ToString(),   Title=$"Payroll approval — {currentPeriod:MMM yyyy}",   Status="Pending", CurrentStepOrder=1, CreatedAtUtc=DateTime.UtcNow.AddHours(-20) },
            new ApprovalRequest { TenantId=tenantId, WorkflowId=workflowId, EntityName="EmployeeTransferRequest", EntityId=Guid.NewGuid().ToString(), Title=$"Transfer request — {employees[5].FullName}", Status="Pending", CurrentStepOrder=1, CreatedAtUtc=DateTime.UtcNow.AddHours(-8) },
            new ApprovalRequest { TenantId=tenantId, WorkflowId=workflowId, EntityName="LeaveRequest",         EntityId=Guid.NewGuid().ToString(), Title=$"Annual leave — {employees[22].FullName}",        Status="Pending", CurrentStepOrder=1, CreatedAtUtc=DateTime.UtcNow.AddHours(-1) }
        );
        await _db.SaveChangesAsync(ct);

        // ── Notifications ────────────────────────────────────────────────────────
        var adminUserId = (await _db.Users.FirstOrDefaultAsync(x => x.TenantId == tenantId, ct))?.Id;
        if (adminUserId is not null && !await _db.Notifications.AnyAsync(x => x.TenantId == tenantId, ct))
        {
            _db.Notifications.AddRange(
                new Notification { TenantId=tenantId, UserId=adminUserId, Title="Payroll ready for approval",   Message=$"{currentPeriod:MMMM yyyy} payroll run is awaiting your approval.",            EntityName="PayrollRun",    Status="Unread" },
                new Notification { TenantId=tenantId, UserId=adminUserId, Title="2 documents expired",          Message="Two residence visas have expired and require immediate renewal.",               EntityName="Compliance",    Status="Unread" },
                new Notification { TenantId=tenantId, UserId=adminUserId, Title="3 documents expiring soon",    Message="Passport and Emirates IDs expiring within 30 days.",                           EntityName="Compliance",    Status="Unread" },
                new Notification { TenantId=tenantId, UserId=adminUserId, Title="New leave requests (3)",       Message="3 employees submitted leave requests awaiting approval.",                      EntityName="LeaveRequest",  Status="Unread" },
                new Notification { TenantId=tenantId, UserId=adminUserId, Title="Performance cycle published",  Message="H1 2026 Performance Review results are now published for 20 employees.",      EntityName="Performance",   Status="Read" },
                new Notification { TenantId=tenantId, UserId=adminUserId, Title="New hire joining tomorrow",    Message=$"{employees[17].FullName} joins as HR Coordinator tomorrow.",                  EntityName="Employee",      Status="Read" }
            );
        }

        // ── AI insights ──────────────────────────────────────────────────────────
        if (!await _db.AIInsights.AnyAsync(x => x.TenantId == tenantId, ct))
        _db.AIInsights.AddRange(
            new AIInsight { TenantId=tenantId, Module="Attendance",   InsightType="AbsenteeismPattern", Severity="Warning",  Title="Rising absenteeism in Operations",              Summary="Operations dept shows 14% higher unplanned absences over the last 3 weeks vs company average. Recommend a manager check-in.", GeneratedBy="System" },
            new AIInsight { TenantId=tenantId, Module="Compliance",   InsightType="DocumentExpiry",     Severity="Critical", Title="2 documents expired, 5 expiring within 30 days",Summary="2 residence visas expired. Passport (KNX-0002) and Emirates IDs (KNX-0013, KNX-0019) expire within 30 days. Initiate renewals.", GeneratedBy="System" },
            new AIInsight { TenantId=tenantId, Module="Payroll",      InsightType="PayrollVariance",    Severity="Info",     Title="Payroll variance within normal range",          Summary="Month-over-month net payroll increased 2.3% — within acceptable variance. No anomalies detected in current draft run.", GeneratedBy="System" },
            new AIInsight { TenantId=tenantId, Module="Recruitment",  InsightType="PipelineHealth",     Severity="Warning",  Title="3 open roles with no interviews scheduled",     Summary="JOB-2026-0001, 0004 and 0005 have active candidates but no interview slots booked. Average time-to-hire risk is rising.", GeneratedBy="System" },
            new AIInsight { TenantId=tenantId, Module="Performance",  InsightType="TopPerformers",      Severity="Info",     Title="4 employees rated Outstanding this half",       Summary="Omar Khalifa, James Carter, Abdullah Al Zaabi, and Faisal Al Hajri scored Outstanding in H1 2026. Consider retention bonuses.", GeneratedBy="System" },
            new AIInsight { TenantId=tenantId, Module="Leave",        InsightType="LeaveUtilisation",   Severity="Info",     Title="Annual leave utilisation below 40%",            Summary="60% of employees have used less than 40% of their annual leave entitlement. Proactive leave planning recommended to avoid year-end pileup.", GeneratedBy="System" }
        );
        await _db.SaveChangesAsync(ct);

        // ══ v2 sections — overtime, leave balances, HR requests, loans, applications, goals ══

        // ── Overtime requests ────────────────────────────────────────────────────
        if (!await _db.OvertimeRequests.AnyAsync(x => x.TenantId == tenantId, ct))
        {
            var otDefs = new (int EmpIdx, int DaysAgo, int Minutes, string Status, string Reason)[]
            {
                (3,  2,  120, "Approved",        "Production deployment support"),
                (10, 3,  90,  "Approved",        "Month-end campaign launch"),
                (12, 5,  150, "Approved",        "Quarter-close reconciliation"),
                (5,  1,  60,  "PendingManager",  "Warehouse stock count"),
                (8,  4,  180, "PendingManager",  "Shipment delay recovery"),
                (16, 6,  120, "Approved",        "Server patching window"),
                (18, 2,  90,  "PendingManager",  "Onboarding documentation"),
                (6,  8,  120, "Rejected",        "Client dinner — not eligible"),
                (13, 7,  60,  "Approved",        "Social media incident response"),
                (20, 9,  150, "Approved",        "Inventory audit support"),
                (1,  10, 120, "Approved",        "Release hotfix"),
                (22, 12, 90,  "PendingManager",  "Pipeline data migration"),
            };
            foreach (var o in otDefs)
            {
                var emp = employees[o.EmpIdx % employees.Count];
                var workDate = today.AddDays(-o.DaysAgo);
                var start = workDate.ToDateTime(new TimeOnly(18, 0));
                _db.OvertimeRequests.Add(new OvertimeRequest
                {
                    TenantId = tenantId, EmployeeId = emp.Id, EmployeeName = emp.FullName,
                    WorkDate = workDate, StartTimeUtc = start, EndTimeUtc = start.AddMinutes(o.Minutes),
                    RequestedMinutes = o.Minutes, ApprovedMinutes = o.Status == "Approved" ? o.Minutes : 0,
                    Source = "Manual", Reason = o.Reason, Status = o.Status,
                    CreatedAtUtc = DateTime.UtcNow.AddDays(-o.DaysAgo),
                    DecidedAtUtc = o.Status is "Approved" or "Rejected" ? DateTime.UtcNow.AddDays(-o.DaysAgo + 1) : null,
                });
            }
            await _db.SaveChangesAsync(ct);
        }

        // ── Leave balances (current year, every employee) ────────────────────────
        if (!await _db.EmployeeLeaveBalances.AnyAsync(x => x.TenantId == tenantId, ct))
        {
            var balanceTypes = new (LeaveType Type, decimal Entitled)[] { (annual, 30m), (sick, 15m), (casual, 5m) };
            foreach (var (emp, i) in employees.Select((e, i) => (e, i)))
            {
                foreach (var (lt, entitled) in balanceTypes)
                {
                    var used = Math.Min(entitled, (i * 3 + (lt.Code == "SICK" ? 1 : 4)) % (int)entitled);
                    _db.EmployeeLeaveBalances.Add(new EmployeeLeaveBalance
                    {
                        TenantId = tenantId, EmployeeId = emp.Id, EmployeeName = emp.FullName,
                        LeaveTypeId = lt.Id, LeaveTypeName = lt.NameEn, Year = today.Year,
                        Entitled = entitled, Accrued = Math.Round(entitled * today.Month / 12m, 1),
                        Used = used, Pending = i % 6 == 0 ? 2 : 0,
                        CarriedForward = lt.Code == "ANNUAL" && i % 4 == 0 ? 5 : 0,
                    });
                }
            }
            await _db.SaveChangesAsync(ct);
        }

        // ── HR Request Center: categories + requests ─────────────────────────────
        if (!await _db.HRRequestCategories.AnyAsync(x => x.TenantId == tenantId, ct))
        {
            var categories = new[]
            {
                new HRRequestCategory { TenantId = tenantId, Code = "SAL-CERT", Name = "Salary Certificate",   DefaultSlaHours = 24 },
                new HRRequestCategory { TenantId = tenantId, Code = "NOC",      Name = "NOC Letter",           DefaultSlaHours = 48 },
                new HRRequestCategory { TenantId = tenantId, Code = "PAY-INQ",  Name = "Payroll Inquiry",      DefaultSlaHours = 48 },
                new HRRequestCategory { TenantId = tenantId, Code = "DOC-REQ",  Name = "Document Request",     DefaultSlaHours = 72 },
                new HRRequestCategory { TenantId = tenantId, Code = "GEN",      Name = "General HR Query",     DefaultSlaHours = 72 },
            };
            _db.HRRequestCategories.AddRange(categories);
            await _db.SaveChangesAsync(ct);

            var hrDefs = new (int EmpIdx, int CatIdx, string Subject, string Priority, string Status, int HoursAgo)[]
            {
                (4,  0, "Salary certificate for bank loan application",      "High",   "Open",       5),
                (7,  1, "NOC letter for visa change",                        "Normal", "InProgress", 30),
                (11, 2, "Overtime missing from May payslip",                 "High",   "InProgress", 50),
                (2,  3, "Copy of signed employment contract",                "Normal", "Open",       8),
                (15, 0, "Salary certificate for embassy",                    "Normal", "Resolved",   120),
                (9,  4, "Question about probation confirmation process",     "Low",    "Open",       12),
                (19, 2, "Bank account update for salary transfer",           "High",   "Resolved",   200),
                (23, 1, "NOC for part-time teaching engagement",             "Low",    "Open",       3),
            };
            foreach (var h in hrDefs)
            {
                var emp = employees[h.EmpIdx % employees.Count];
                var cat = categories[h.CatIdx];
                _db.HRRequests.Add(new HRRequest
                {
                    TenantId = tenantId, EmployeeId = emp.Id, CategoryId = cat.Id, CategoryName = cat.Name,
                    Subject = h.Subject, Description = h.Subject, Priority = h.Priority, Status = h.Status,
                    CreatedAtUtc = DateTime.UtcNow.AddHours(-h.HoursAgo),
                    DueAtUtc = DateTime.UtcNow.AddHours(-h.HoursAgo + cat.DefaultSlaHours),
                });
            }
            await _db.SaveChangesAsync(ct);
        }

        // ── Loans, advances ──────────────────────────────────────────────────────
        if (!await _db.EmployeeLoans.AnyAsync(x => x.TenantId == tenantId, ct))
        {
            var personal  = new LoanType { TenantId = tenantId, Code = "PERSONAL",  NameEn = "Personal Loan",  NameAr = "قرض شخصي",  MaxAmount = 50000, MaxInstallments = 24, MinServiceMonths = 12 };
            var emergency = new LoanType { TenantId = tenantId, Code = "EMERGENCY", NameEn = "Emergency Loan", NameAr = "قرض طارئ",  MaxAmount = 20000, MaxInstallments = 12, MinServiceMonths = 6 };
            var education = new LoanType { TenantId = tenantId, Code = "EDUCATION", NameEn = "Education Loan", NameAr = "قرض تعليمي", MaxAmount = 40000, MaxInstallments = 18, MinServiceMonths = 12 };
            _db.LoanTypes.AddRange(personal, emergency, education);
            await _db.SaveChangesAsync(ct);

            var loanDefs = new (int EmpIdx, LoanType Type, decimal Amount, int Months, string Status, decimal Repaid)[]
            {
                (3,  personal,  30000, 12, "Active",   12500),
                (7,  emergency, 8000,  8,  "Active",   3000),
                (12, education, 24000, 12, "Active",   6000),
                (16, personal,  15000, 10, "Pending",  0),
                (20, emergency, 5000,  5,  "Settled",  5000),
            };
            foreach (var (l, i) in loanDefs.Select((l, i) => (l, i)))
            {
                var emp = employees[l.EmpIdx % employees.Count];
                var approved = l.Status == "Pending" ? 0 : l.Amount;
                _db.EmployeeLoans.Add(new EmployeeLoan
                {
                    TenantId = tenantId, EmployeeId = Guid.NewGuid(), EmployeeName = emp.FullName,
                    LoanTypeId = l.Type.Id, LoanTypeName = l.Type.NameEn,
                    LoanNumber = $"LN-{today.Year}-{i + 1:00000}",
                    RequestedAmount = l.Amount, ApprovedAmount = approved,
                    RequestedInstallments = l.Months, ApprovedInstallments = l.Status == "Pending" ? 0 : l.Months,
                    InstallmentAmount = l.Status == "Pending" ? 0 : Math.Round(l.Amount / l.Months, 2),
                    DisbursementDate = l.Status == "Pending" ? null : today.AddMonths(-(int)(l.Repaid / Math.Max(1, l.Amount / l.Months))),
                    TotalRepaid = l.Repaid, OutstandingBalance = approved - l.Repaid,
                    Status = l.Status, Notes = "Demo data",
                });
            }

            var advDefs = new (int EmpIdx, decimal Amount, string Status, string Reason)[]
            {
                (5,  4000, "Active",  "School fees due before payday"),
                (14, 2500, "Pending", "Medical expense"),
                (21, 3000, "Settled", "Rent deposit"),
            };
            foreach (var (a, i) in advDefs.Select((a, i) => (a, i)))
            {
                var emp = employees[a.EmpIdx % employees.Count];
                _db.SalaryAdvances.Add(new SalaryAdvance
                {
                    TenantId = tenantId, EmployeeId = Guid.NewGuid(), EmployeeName = emp.FullName,
                    AdvanceNumber = $"ADV-{today.Year}-{i + 1:00000}",
                    RequestedAmount = a.Amount, ApprovedAmount = a.Status == "Pending" ? 0 : a.Amount,
                    InstallmentAmount = a.Status == "Pending" ? 0 : a.Amount,
                    TotalRepaid = a.Status == "Settled" ? a.Amount : 0,
                    OutstandingBalance = a.Status == "Active" ? a.Amount : 0,
                    Reason = a.Reason, Status = a.Status,
                });
            }
            await _db.SaveChangesAsync(ct);
        }

        // ── Recruitment pipeline: applications across stages ─────────────────────
        {
            var openings = await _db.JobOpenings.Where(x => x.TenantId == tenantId).OrderBy(x => x.JobCode).ToListAsync(ct);
            var appliedCandidateIds = await _db.JobApplications.Where(x => x.TenantId == tenantId)
                .Select(x => x.CandidateId).Distinct().ToListAsync(ct);
            var candidates = await _db.Candidates
                .Where(x => x.TenantId == tenantId && !appliedCandidateIds.Contains(x.Id))
                .OrderBy(x => x.Email).ToListAsync(ct);
            if (openings.Count > 0 && candidates.Count > 0)
            {
                var stages = new (string Stage, int Order)[] { ("Applied", 1), ("Screening", 2), ("Interview", 3), ("Offer", 4), ("Hired", 5) };
                foreach (var (cand, i) in candidates.Select((c, i) => (c, i)))
                {
                    var opening = openings[i % openings.Count];
                    var (stage, order) = stages[i % stages.Length];
                    var appliedDaysAgo = 6 + i * 3;
                    _db.JobApplications.Add(new JobApplication
                    {
                        TenantId = tenantId, JobOpeningId = opening.Id, JobTitle = opening.Title,
                        CandidateId = cand.Id, CandidateName = $"{cand.FirstName} {cand.LastName}", CandidateEmail = cand.Email,
                        Stage = stage, StageOrder = order,
                        Status = stage == "Hired" ? "Hired" : "Active",
                        OfferedSalary = stage is "Offer" or "Hired" ? 16000 + i * 500 : null,
                        AppliedAtUtc = DateTime.UtcNow.AddDays(-appliedDaysAgo),
                        StageChangedAtUtc = DateTime.UtcNow.AddDays(-(appliedDaysAgo - 4 - (i % 3) * 3)),
                        HiredAtUtc = stage == "Hired" ? DateTime.UtcNow.AddDays(-2) : null,
                    });
                }
                await _db.SaveChangesAsync(ct);
            }
        }

        // ── Performance goals ────────────────────────────────────────────────────
        if (!await _db.EmployeeGoals.AnyAsync(x => x.TenantId == tenantId, ct))
        {
            var goalDefs = new (int EmpIdx, string Title, string Unit, decimal Target, decimal Actual)[]
            {
                (1,  "Ship platform v3 milestones",            "Milestones", 8,  5),
                (3,  "Reduce deployment failures",              "Percent",    50, 35),
                (2,  "Close monthly books within 5 days",       "Days",       5,  6),
                (6,  "New enterprise deals signed",             "Deals",      12, 7),
                (9,  "Grow marketing-qualified leads",          "Leads",      400, 310),
                (10, "Resolve engineering tickets within SLA",  "Percent",    95, 91),
                (12, "Automate 6 finance reports",              "Reports",    6,  4),
                (14, "Increase sales pipeline value (USD m)",   "Million",    10, 6),
                (8,  "Maintain IT uptime",                      "Percent",    99, 99),
                (5,  "Cut order fulfilment time",               "Hours",      24, 30),
            };
            foreach (var g in goalDefs)
            {
                var emp = employees[g.EmpIdx % employees.Count];
                _db.EmployeeGoals.Add(new EmployeeGoal
                {
                    TenantId = tenantId, EmployeeId = emp.Id, EmployeeName = emp.FullName,
                    Title = g.Title, Description = g.Title, Category = "Individual",
                    KpiType = "Quantitative", MeasurementUnit = g.Unit,
                    TargetValue = g.Target, ActualValue = g.Actual,
                    AchievementPct = Math.Round(Math.Min(150, g.Actual / Math.Max(1, g.Target) * 100), 1),
                    DueDate = new DateOnly(today.Year, 12, 31), Status = "Active", ManagerApproved = true,
                });
            }
            await _db.SaveChangesAsync(ct);
        }

        // ── Record seed version so future startups skip instantly ────────────────
        if (seedFlag is null)
        {
            seedFlag = new TenantFeatureFlag { TenantId = tenantId, FeatureKey = "demo_seed_version", IsEnabled = true };
            _db.TenantFeatureFlags.Add(seedFlag);
        }
        seedFlag.ConfigJson = DemoSeedVersion.ToString();
        seedFlag.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }
}
