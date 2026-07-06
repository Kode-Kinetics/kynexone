using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Seed;

/// <summary>
/// TEST/DEMO DATA ONLY — three enterprise GROUP tenants (ALMARAI_TEST, TATA_TEST,
/// EMAAR_TEST) for demos and E2E. Gated by SEED_ENTERPRISE_TEST_DATA=true; never enable
/// in production. Idempotent: a tenant whose slug already exists is skipped whole.
///
/// PII policy: no real people, no real identifiers. Sensitive fields (IBAN, passport,
/// Iqama, medical) are left EMPTY by design — compliance profiles then show real
/// "missing field" readiness gaps, which is exactly what the demo needs to prove.
/// All passwords are the published demo credential (GroupDemo123!x).
/// </summary>
public sealed class EnterpriseGroupSeeder
{
    public const string EnableEnvVar = "SEED_ENTERPRISE_TEST_DATA";
    private const string DemoPassword = "GroupDemo123!x";

    private readonly ZayraDbContext _db;
    private readonly IPasswordHasher _hasher;
    private readonly IAuthSeeder _authSeeder;
    private readonly ILogger<EnterpriseGroupSeeder> _log;

    public EnterpriseGroupSeeder(ZayraDbContext db, IPasswordHasher hasher, IAuthSeeder authSeeder, ILogger<EnterpriseGroupSeeder> log)
    {
        _db = db;
        _hasher = hasher;
        _authSeeder = authSeeder;
        _log = log;
    }

    private sealed record CompanySpec(string Code, string Name, string CountryCode, string Jurisdiction, string Currency, string Pack, string RequiredFieldsJson);

    private static readonly string KsaFields = """[{"field":"IqamaNumber","failClosed":true},{"field":"GosiReference","failClosed":true}]""";
    private static readonly string UaeFields = """[{"field":"EmiratesId","failClosed":true}]""";
    private static readonly string IndFields = """[{"field":"IdNumber","failClosed":false}]""";
    private static readonly string GbrFields = """[{"field":"IdNumber","failClosed":false}]""";

    private static CompanySpec Ksa(string code, string name) => new(code, name, "SA", "KSA-mainland", "SAR", "SAU", KsaFields);
    private static CompanySpec Uae(string code, string name) => new(code, name, "AE", "UAE-mainland", "AED", "ARE", UaeFields);

    private static readonly (string Name, string Slug, string Currency, string DisabledFeature, CompanySpec[] Companies)[] Groups =
    {
        ("ALMARAI_TEST", "almarai-test", "SAR", FeatureKeys.Recruitment, new[]
        {
            Ksa("ALM-DAIRY-KSA", "Almarai Test Dairy"),
            Ksa("ALM-POULTRY-KSA", "Almarai Test Poultry"),
            Ksa("ALM-BAKERY-KSA", "Almarai Test Bakery"),
            Ksa("ALM-DIST-KSA", "Almarai Test Distribution"),
            Uae("ALM-UAE-TRD", "Almarai Test UAE Trading"),
        }),
        ("TATA_TEST", "tata-test", "INR", FeatureKeys.Shifts, new[]
        {
            new CompanySpec("TATA-TCS-IN", "Tata Test Consultancy", "IN", "IN-central", "INR", "IND", IndFields),
            new CompanySpec("TATA-MOTORS-IN", "Tata Test Motors", "IN", "IN-central", "INR", "IND", IndFields),
            new CompanySpec("TATA-STEEL-IN", "Tata Test Steel", "IN", "IN-central", "INR", "IND", IndFields),
            new CompanySpec("TATA-HOTELS-IN", "Tata Test Hotels", "IN", "IN-central", "INR", "IND", IndFields),
            new CompanySpec("TATA-JLR-UK", "Tata Test JLR", "GB", "UK-national", "GBP", "GBR", GbrFields),
        }),
        ("EMAAR_TEST", "emaar-test", "AED", FeatureKeys.Overtime, new[]
        {
            Uae("EMAAR-PROP-UAE", "Emaar Test Properties"),
            Uae("EMAAR-MALLS-UAE", "Emaar Test Malls"),
            Uae("EMAAR-HOSP-UAE", "Emaar Test Hospitality"),
            Uae("EMAAR-LEISURE-UAE", "Emaar Test Leisure"),
            Ksa("EMAAR-KSA-PROP", "Emaar Test KSA Properties"),
        }),
    };

    public async Task SeedAsync(CancellationToken ct = default)
    {
        foreach (var group in Groups)
        {
            if (await _db.Tenants.AnyAsync(t => t.Slug == group.Slug, ct))
            {
                _log.LogInformation("EnterpriseGroupSeeder: tenant '{Slug}' exists — skipping.", group.Slug);
                continue;
            }
            await SeedGroupAsync(group.Name, group.Slug, group.Currency, group.DisabledFeature, group.Companies, ct);
            _log.LogInformation("EnterpriseGroupSeeder: seeded group tenant '{Slug}' with {Count} companies.", group.Slug, group.Companies.Length);
        }
    }

    private async Task SeedGroupAsync(string name, string slug, string currency, string disabledFeature, CompanySpec[] specs, CancellationToken ct)
    {
        var tenant = new Tenant { Name = name, Slug = slug, AccountType = TenantAccountTypes.Group };
        _db.Tenants.Add(tenant);
        _db.TenantSubscriptions.Add(new TenantSubscription
        {
            TenantId = tenant.Id, Plan = "Enterprise", Status = "ManualContract",
            MaxEmployees = 0, MaxUsers = 0, MaxCompanies = 0, MaxAdminUsers = 0,
            BillingEmail = $"billing@{slug}.local", CurrencyCode = currency,
        });
        _db.TenantFeatureFlags.Add(new TenantFeatureFlag { TenantId = tenant.Id, FeatureKey = disabledFeature, IsEnabled = false });
        await _db.SaveChangesAsync(ct);

        await _authSeeder.EnsureTenantRolesAsync(tenant.Id, ct);
        var roles = await _db.Roles.Where(r => r.TenantId == tenant.Id).ToDictionaryAsync(r => r.Name, ct);

        // Companies
        var companies = new List<Company>();
        foreach (var spec in specs)
        {
            var company = new Company
            {
                TenantId = tenant.Id, LegalNameEn = spec.Code, TradeName = spec.Name,
                CountryCode = spec.CountryCode, Jurisdiction = spec.Jurisdiction,
                RegistrationNumber = $"REG-{spec.Code}", DefaultCurrency = spec.Currency,
                IsActive = true, ApprovalStatus = CompanyApprovalStatuses.Active,
            };
            companies.Add(company);
            _db.Companies.Add(company);
        }
        await _db.SaveChangesAsync(ct);

        // Group-level users (explicit group scope)
        AddUser(tenant.Id, $"owner@{slug}.local", $"{name} Group Owner", roles["Admin"], isGroupScope: true);
        AddUser(tenant.Id, $"admin@{slug}.local", $"{name} Group Admin", roles["Admin"], isGroupScope: true);
        AddUser(tenant.Id, $"hr@{slug}.local", $"{name} Group HR Head", roles["HR Director"], isGroupScope: true);
        AddUser(tenant.Id, $"finance@{slug}.local", $"{name} Group Finance Head", roles["Finance Approver"], isGroupScope: true);
        AddUser(tenant.Id, $"compliance@{slug}.local", $"{name} Group Compliance Officer", roles["Compliance Officer"], isGroupScope: true);
        AddUser(tenant.Id, $"auditor@{slug}.local", $"{name} Group Auditor", roles["Auditor"], isGroupScope: true);

        // Selected-companies demonstration user: first two companies only.
        var scoped = AddUser(tenant.Id, $"scoped.admin@{slug}.local", $"{name} Scoped Admin", roles["HR Manager"], isGroupScope: false);
        Grant(tenant.Id, scoped, companies[0].Id);
        Grant(tenant.Id, scoped, companies[1].Id);

        // Per-company users
        for (var i = 0; i < companies.Count; i++)
        {
            var company = companies[i];
            var codeLower = company.LegalNameEn.ToLowerInvariant();
            var admin = AddUser(tenant.Id, $"admin@{codeLower}.{slug}.local", $"{company.TradeName} Admin", roles["Admin"], isGroupScope: false);
            Grant(tenant.Id, admin, company.Id);
            var hr = AddUser(tenant.Id, $"hr@{codeLower}.{slug}.local", $"{company.TradeName} HR Manager", roles["HR Manager"], isGroupScope: false);
            Grant(tenant.Id, hr, company.Id);
            if (i < 2)
            {
                var payroll = AddUser(tenant.Id, $"payroll@{codeLower}.{slug}.local", $"{company.TradeName} Payroll Officer", roles["Payroll Officer"], isGroupScope: false);
                Grant(tenant.Id, payroll, company.Id);
            }
        }
        await _db.SaveChangesAsync(ct);

        // Org structure + workforce per company
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        foreach (var (company, spec) in companies.Zip(specs))
        {
            var hq = new Branch { TenantId = tenant.Id, CompanyId = company.Id, Code = $"{spec.Code}-HQ", NameEn = $"{spec.Name} HQ", CountryCode = spec.CountryCode, IsHeadOffice = true, IsActive = true };
            var ops = new Branch { TenantId = tenant.Id, CompanyId = company.Id, Code = $"{spec.Code}-OPS", NameEn = $"{spec.Name} Operations Site", CountryCode = spec.CountryCode, IsActive = true };
            _db.Branches.AddRange(hq, ops);
            await _db.SaveChangesAsync(ct);

            var departments = new[] { "Operations", "HR", "Finance" }.Select((d, ix) => new Department
            {
                TenantId = tenant.Id, BranchId = hq.Id, Code = $"{spec.Code}-D{ix + 1}", NameEn = d, IsActive = true,
            }).ToList();
            _db.Departments.AddRange(departments);
            await _db.SaveChangesAsync(ct);

            var employees = new List<Employee>();
            for (var n = 1; n <= 3; n++)
            {
                employees.Add(new Employee
                {
                    TenantId = tenant.Id, CompanyId = company.Id, BranchId = hq.Id,
                    DepartmentId = departments[n - 1].Id, Department = departments[n - 1].NameEn,
                    EmployeeCode = $"{spec.Code}-E{n:D3}",
                    FullName = $"Test Employee {spec.Code} {n}", EnglishName = $"Test Employee {spec.Code} {n}",
                    Gender = n % 2 == 0 ? "Female" : "Male",
                    Nationality = spec.CountryCode switch { "SA" => "Saudi", "AE" => "Emirati", "IN" => "Indian", _ => "British" },
                    CountryCode = spec.CountryCode,
                    Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-1).AddMonths(-n),
                    Salary = 6000m + n * 2000m,
                    // PII deliberately EMPTY (no real/synthetic identifiers) — produces
                    // honest "missing required field" compliance readiness gaps.
                });
            }
            _db.Employees.AddRange(employees);
            await _db.SaveChangesAsync(ct); // int identity ids needed below

            foreach (var employee in employees)
            {
                for (var d = 1; d <= 5; d++)
                {
                    _db.AttendanceRecords.Add(new AttendanceRecord
                    {
                        TenantId = tenant.Id, CompanyId = company.Id, EmployeeId = employee.Id,
                        WorkDate = today.AddDays(-d), Status = d == 3 ? "Absent" : "Present",
                    });
                }
            }
            _db.LeaveRequests.Add(new LeaveRequest
            {
                TenantId = tenant.Id, CompanyId = company.Id, EmployeeId = employees[0].Id,
                EmployeeName = employees[0].FullName, LeaveTypeId = Guid.NewGuid(), LeaveTypeName = "Annual",
                StartDate = today.AddMonths(1), EndDate = today.AddMonths(1).AddDays(3), Status = "PendingHRApproval",
            });
            _db.LeaveRequests.Add(new LeaveRequest
            {
                TenantId = tenant.Id, CompanyId = company.Id, EmployeeId = employees[1].Id,
                EmployeeName = employees[1].FullName, LeaveTypeId = Guid.NewGuid(), LeaveTypeName = "Annual",
                StartDate = today.AddMonths(-2), EndDate = today.AddMonths(-2).AddDays(2), Status = "Approved",
            });

            _db.CompanyComplianceProfiles.Add(new CompanyComplianceProfile
            {
                TenantId = tenant.Id, CompanyId = company.Id, CountryCode = spec.CountryCode,
                Jurisdiction = spec.Jurisdiction, CompliancePack = spec.Pack,
                EffectiveFrom = new DateOnly(2026, 1, 1), Status = CompanyPolicyStatuses.Active,
                RequiredFieldsJson = spec.RequiredFieldsJson,
                Notes = "TEST DATA — configurable readiness profile, not legal certification.",
            });
            await _db.SaveChangesAsync(ct);
        }

        // Payroll run + tax policy for the first company of the group.
        var first = companies[0];
        var firstEmployees = await _db.Employees.Where(e => e.TenantId == tenant.Id && e.CompanyId == first.Id).ToListAsync(ct);
        var lastMonth = DateTime.UtcNow.AddMonths(-1);
        _db.PayrollRuns.Add(new PayrollRun
        {
            TenantId = tenant.Id, CompanyId = first.Id, Year = lastMonth.Year, Month = lastMonth.Month,
            Status = "Approved",
            TotalGrossSalary = firstEmployees.Sum(e => e.Salary ?? 0m),
            TotalNetSalary = firstEmployees.Sum(e => e.Salary ?? 0m),
            EmployeeCount = firstEmployees.Count,
        });
        _db.CompanyTaxPolicies.Add(new CompanyTaxPolicy
        {
            TenantId = tenant.Id, CompanyId = first.Id, CountryCode = specs[0].CountryCode,
            EffectiveFrom = new DateOnly(2026, 1, 1), Status = CompanyPolicyStatuses.Active,
            IncomeTaxRatePercent = specs[0].CountryCode == "IN" ? 10m : 0m, AppliesToBonus = true,
            Notes = "TEST DATA — configurable policy foundation, not legal tax advice.",
        });
        await _db.SaveChangesAsync(ct);
    }

    private User AddUser(Guid tenantId, string email, string fullName, Role role, bool isGroupScope)
    {
        var user = new User
        {
            TenantId = tenantId,
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            FullName = fullName,
            PasswordHash = _hasher.Hash(DemoPassword),
            IsActive = true,
            IsGroupScope = isGroupScope,
        };
        _db.Users.Add(user);
        _db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
        return user;
    }

    private void Grant(Guid tenantId, User user, Guid companyId) =>
        _db.UserEntityAccesses.Add(new UserEntityAccess
        {
            TenantId = tenantId, UserId = user.Id, CompanyId = companyId,
            GrantMode = EntityGrantModes.SelectedCompanies, Role = "CompanyAdmin", IsActive = true,
        });
}
