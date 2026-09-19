using System.Data;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Seed;

/// <summary>
/// Creates the disposable, deterministic KSA fixture used by the 20-Sep-2026 client-demo gate.
/// This is deliberately a one-off command, never part of normal application startup.
/// </summary>
public static class SundayKsaDemoFixtureSeeder
{
    public const string FixtureId = "KX-SUN-KSA-20260920-v1";
    public const string TenantSlug = "kx-sun-ksa-20260920-v1";
    public const string Confirmation = "CREATE-KX-SUN-KSA-20260920-v1";
    public const string ConfirmationVariable = "SUNDAY_DEMO_FIXTURE_CONFIRMATION";
    public const string DisposableVariable = "SUNDAY_DEMO_FIXTURE_DISPOSABLE";
    public const string PasswordVariable = "SUNDAY_DEMO_FIXTURE_PASSWORD";

    public const int ExpectedEmployees = 30;
    public const int ExpectedBranches = 2;
    public const int ExpectedDepartments = 5;
    public const int ExpectedPersonas = 10;
    public const int ExpectedAttendanceDays = 8;
    public const int ExpectedLeaveRequests = 4;
    public const int ExpectedPayrollEmployees = 3;
    public const decimal ExpectedGross = 42_780m;
    public const decimal ExpectedDeductions = 3_142.92m;
    public const decimal ExpectedNet = 39_637.08m;
    public const decimal ExpectedEmployerStatutory = 3_666.25m;

    private const long AdvisoryLockKey = 0x4B_58_53_55_4E; // "KXSUN"
    private static readonly DateTime AnchorUtc = new(2026, 9, 20, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly AnchorDate = new(2026, 9, 20);

    public sealed record GuardInput(
        string EnvironmentName,
        bool DedicatedDeployment,
        bool ClientDeployment,
        string? ConfirmationValue,
        bool DisposableAcknowledged,
        string? Password);

    public sealed record FixtureShape(
        int Employees,
        int Branches,
        int Departments,
        string[] EmployeeCodes,
        string[] PersonaSignatures,
        string[] AttendanceSignatures,
        string[] LeaveSignatures,
        string[] PayrollSignatures,
        int PayrollEmployees,
        decimal Gross,
        decimal Deductions,
        decimal Net,
        decimal EmployerStatutory);

    public sealed record SeedResult(Guid TenantId, bool Created, string FixtureId);

    public static GuardInput ReadGuard(IHostEnvironment environment) => new(
        environment.EnvironmentName,
        string.Equals(Environment.GetEnvironmentVariable("DEDICATED_DEPLOYMENT"), "true", StringComparison.OrdinalIgnoreCase),
        string.Equals(Environment.GetEnvironmentVariable("CLIENT_DEPLOYMENT"), "true", StringComparison.OrdinalIgnoreCase),
        Environment.GetEnvironmentVariable(ConfirmationVariable),
        string.Equals(Environment.GetEnvironmentVariable(DisposableVariable), "true", StringComparison.OrdinalIgnoreCase),
        Environment.GetEnvironmentVariable(PasswordVariable));

    public static void ValidateGuard(GuardInput input)
    {
        if (string.Equals(input.EnvironmentName, "Production", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{FixtureId}: refused in Production.");
        if (input.DedicatedDeployment)
            throw new InvalidOperationException($"{FixtureId}: refused on a dedicated deployment.");
        if (input.ClientDeployment)
            throw new InvalidOperationException($"{FixtureId}: refused on a client deployment.");
        if (!string.Equals(input.ConfirmationValue, Confirmation, StringComparison.Ordinal))
            throw new InvalidOperationException($"{FixtureId}: exact {ConfirmationVariable}={Confirmation} is required.");
        if (!input.DisposableAcknowledged)
            throw new InvalidOperationException($"{FixtureId}: {DisposableVariable}=true is required.");
        if (string.IsNullOrWhiteSpace(input.Password) || input.Password.Length < 12)
            throw new InvalidOperationException($"{FixtureId}: {PasswordVariable} must contain at least 12 characters.");
    }

    public static IReadOnlyList<string> ValidateShape(FixtureShape shape)
    {
        var errors = new List<string>();
        if (shape.Employees != ExpectedEmployees) errors.Add($"employees={shape.Employees}, expected {ExpectedEmployees}");
        if (shape.Branches != ExpectedBranches) errors.Add($"branches={shape.Branches}, expected {ExpectedBranches}");
        if (shape.Departments != ExpectedDepartments) errors.Add($"departments={shape.Departments}, expected {ExpectedDepartments}");
        CompareSet("employee codes", shape.EmployeeCodes, ExpectedEmployeeCodes(), errors);
        CompareSet("personas", shape.PersonaSignatures, ExpectedPersonaSignatures(), errors);
        CompareSet("attendance", shape.AttendanceSignatures, ExpectedAttendanceSignatures(), errors);
        CompareSet("leave", shape.LeaveSignatures, ExpectedLeaveSignatures(), errors);
        CompareSet("payroll", shape.PayrollSignatures, ExpectedPayrollSignatures(), errors);
        if (shape.PayrollEmployees != ExpectedPayrollEmployees) errors.Add($"payroll employees={shape.PayrollEmployees}, expected {ExpectedPayrollEmployees}");
        if (shape.Gross != ExpectedGross) errors.Add($"payroll gross={shape.Gross}, expected {ExpectedGross}");
        if (shape.Deductions != ExpectedDeductions) errors.Add($"payroll deductions={shape.Deductions}, expected {ExpectedDeductions}");
        if (shape.Net != ExpectedNet) errors.Add($"payroll net={shape.Net}, expected {ExpectedNet}");
        if (shape.EmployerStatutory != ExpectedEmployerStatutory) errors.Add($"employer statutory={shape.EmployerStatutory}, expected {ExpectedEmployerStatutory}");
        if (shape.Gross - shape.Deductions != shape.Net) errors.Add("payroll equation gross - deductions = net failed");
        return errors;
    }

    public static async Task<SeedResult> RunAsync(
        ZayraDbContext db,
        IPasswordHasher hasher,
        IAuthSeeder authSeeder,
        IHostEnvironment environment,
        ILogger logger,
        CancellationToken ct = default)
    {
        var guard = ReadGuard(environment);
        ValidateGuard(guard);
        var strategy = db.Database.CreateExecutionStrategy();
        SeedResult? result = null;

        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({AdvisoryLockKey})", ct);

            var existing = await db.Tenants.SingleOrDefaultAsync(t => t.Slug == TenantSlug, ct);
            if (existing is not null)
            {
                var shape = await ReadShapeAsync(db, existing.Id, ct);
                var errors = ValidateShape(shape);
                if (errors.Count != 0)
                    throw new InvalidOperationException($"{FixtureId}: existing fixture drift detected; no repair was attempted: {string.Join("; ", errors)}");

                await transaction.CommitAsync(ct);
                result = new SeedResult(existing.Id, false, FixtureId);
                logger.LogInformation("{FixtureId}: existing canonical fixture validated; no changes made.", FixtureId);
                return;
            }

            var tenant = new Tenant
            {
                Name = "KynexOne Sunday KSA Demo Company",
                Slug = TenantSlug,
                IsActive = true,
                AccountType = TenantAccountTypes.SingleCompany,
                CreatedAtUtc = AnchorUtc,
            };
            db.Tenants.Add(tenant);
            await db.SaveChangesAsync(ct);

            await authSeeder.EnsureTenantRolesAsync(tenant.Id, ct);
            await SeedCanonicalAsync(db, hasher, tenant, guard.Password!, ct);

            var createdShape = await ReadShapeAsync(db, tenant.Id, ct);
            var createdErrors = ValidateShape(createdShape);
            if (createdErrors.Count != 0)
                throw new InvalidOperationException($"{FixtureId}: generated fixture failed its control totals: {string.Join("; ", createdErrors)}");

            await transaction.CommitAsync(ct);
            result = new SeedResult(tenant.Id, true, FixtureId);
            logger.LogInformation("{FixtureId}: canonical disposable fixture created for tenant {TenantId}.", FixtureId, tenant.Id);
        });

        return result ?? throw new InvalidOperationException($"{FixtureId}: transaction completed without a result.");
    }

    private static async Task SeedCanonicalAsync(
        ZayraDbContext db, IPasswordHasher hasher, Tenant tenant, string password, CancellationToken ct)
    {
        var tenantId = tenant.Id;
        var roles = await db.Roles.Where(r => r.TenantId == tenantId)
            .ToDictionaryAsync(r => r.Name, StringComparer.OrdinalIgnoreCase, ct);

        db.TenantSubscriptions.Add(new TenantSubscription
        {
            TenantId = tenantId, Plan = "Enterprise", Status = "Active", MaxEmployees = 100,
            MaxUsers = 25, MaxCompanies = 1, BillingEmail = "finance@kx-sunday.demo",
            BillingCycle = "Annually", CurrencyCode = "SAR", StartedAtUtc = AnchorUtc,
        });
        foreach (var feature in new[]
        {
            FeatureKeys.Payroll, FeatureKeys.Recruitment, FeatureKeys.Performance, FeatureKeys.Compliance,
            FeatureKeys.Finance, FeatureKeys.Shifts, FeatureKeys.Overtime, FeatureKeys.AiAssistant,
            FeatureKeys.WpsExport, FeatureKeys.EosbCalc, FeatureKeys.QiwaIntegration, FeatureKeys.MobileApp,
        })
            db.TenantFeatureFlags.Add(new TenantFeatureFlag { TenantId = tenantId, FeatureKey = feature, IsEnabled = true, UpdatedAtUtc = AnchorUtc });

        var company = new Company
        {
            TenantId = tenantId, LegalNameEn = "KynexOne Sunday KSA Demo Company LLC",
            LegalNameAr = "شركة كاينكس ون التجريبية", TradeName = "KX Sunday Demo", CountryCode = "SAU",
            Jurisdiction = "KSA-mainland", RegistrationNumber = "1010999206", TaxNumber = "310999200600003",
            WpsEmployerId = "WPS-KX-920", GosiEmployerId = "3000999206", QiwaEstablishmentId = "7000999206",
            DefaultCurrency = "SAR", EmailDomain = "kx-sunday.demo", IsActive = true, CreatedAtUtc = AnchorUtc,
        };
        db.Companies.Add(company);
        await db.SaveChangesAsync(ct);

        var branches = new[]
        {
            new Branch { TenantId=tenantId, CompanyId=company.Id, Code="RUH-HQ", NameEn="Riyadh Head Office", NameAr="المقر الرئيسي بالرياض", CountryCode="SAU", City="Riyadh", TimeZoneId="Arab Standard Time", IsHeadOffice=true, CreatedAtUtc=AnchorUtc },
            new Branch { TenantId=tenantId, CompanyId=company.Id, Code="JED-OPS", NameEn="Jeddah Operations", NameAr="عمليات جدة", CountryCode="SAU", City="Jeddah", TimeZoneId="Arab Standard Time", IsHeadOffice=false, CreatedAtUtc=AnchorUtc },
        };
        db.Branches.AddRange(branches);
        await db.SaveChangesAsync(ct);

        var departmentSpecs = new[]
        {
            ("HR", "Human Resources", "الموارد البشرية", branches[0]),
            ("FIN", "Finance", "المالية", branches[0]),
            ("IT", "Information Technology", "تقنية المعلومات", branches[0]),
            ("OPS", "Operations", "العمليات", branches[1]),
            ("SALES", "Sales", "المبيعات", branches[1]),
        };
        var departments = departmentSpecs.Select((d, i) => new Department
        {
            TenantId=tenantId, BranchId=d.Item4.Id, Code=d.Item1, NameEn=d.Item2, NameAr=d.Item3,
            SortOrder=i, ApprovedHeadcount=12, MonthlyBudgetAmount=250_000m, CreatedAtUtc=AnchorUtc,
        }).ToArray();
        db.Departments.AddRange(departments);

        var grade = new Grade { TenantId=tenantId, Code="KSA-DEMO", Name="KSA Demo Professional", Band="P1-P5", Level=3, MinSalary=6_000m, MidSalary=14_000m, MaxSalary=30_000m, Currency="SAR", CreatedAtUtc=AnchorUtc };
        db.Grades.Add(grade);
        await db.SaveChangesAsync(ct);

        var designations = departments.Select((d, i) => new Designation
        {
            TenantId=tenantId, DepartmentId=d.Id, Code=$"{d.Code}-PRO", TitleEn=$"{d.NameEn} Professional",
            TitleAr=d.NameAr, GradeId=grade.Id, JobGrade=grade.Code, JobLevel="Professional", LevelRank=i+1,
            IsManagerRole=i < 2, IsActive=true, CreatedAtUtc=AnchorUtc,
        }).ToArray();
        db.Designations.AddRange(designations);
        await db.SaveChangesAsync(ct);

        var employeeNames = new[]
        {
            "Noura Al-Ghamdi", "Faisal Al-Harbi", "Reem Al-Qahtani", "Khalid Al-Dosari", "Hessa Al-Otaibi",
            "Mohammed Al-Shammari", "Sara Al-Anzi", "Tariq Al-Zahrani", "Luluwah Al-Mutairi", "Abdullah Al-Malki",
            "Ahmed Hassan", "Priya Krishnamurthy", "Omar Abdelnabi", "James Okafor", "Siti Rahayu",
            "Michael Fernandez", "Nadia Boukhari", "David Chen", "Fatima Al-Shehri", "Yousef Al-Amri",
            "Amina Rahman", "Bilal Khan", "Mariam Nasser", "George Mensah", "Rania Farouk",
            "Waleed Al-Salem", "Noor Ibrahim", "Samir Patel", "Layla Haddad", "Zainab Al-Rashid",
        };
        var nationalities = new[] { "Saudi", "Saudi", "Saudi", "Saudi", "Saudi", "Saudi", "Saudi", "Saudi", "Saudi", "Saudi", "Egyptian", "Indian", "Sudanese", "Nigerian", "Indonesian", "Filipino", "Moroccan", "Chinese", "Saudi", "Saudi", "Pakistani", "Pakistani", "Jordanian", "Ghanaian", "Egyptian", "Saudi", "Jordanian", "Indian", "Lebanese", "Saudi" };
        var employees = new List<Employee>(ExpectedEmployees);
        for (var i = 0; i < ExpectedEmployees; i++)
        {
            var department = departments[i % departments.Length];
            var designation = designations[i % designations.Length];
            var saudi = nationalities[i] == "Saudi";
            var status = i switch { 27 => EmployeeStatuses.Suspended, 28 => EmployeeStatuses.Offboarded, 29 => EmployeeStatuses.Invited, _ => EmployeeStatuses.Active };
            var readiness = i switch { 26 => "NeedsAttention", 27 => "Blocked", _ => "Ready" };
            var employee = new Employee
            {
                TenantId=tenantId, CompanyId=company.Id, BranchId=branches[i % branches.Length].Id,
                DepartmentId=department.Id, DesignationId=designation.Id, GradeId=grade.Id,
                EmployeeCode=$"KXS-{i+1:D3}", FullName=employeeNames[i], EnglishName=employeeNames[i],
                PreferredName=employeeNames[i].Split(' ')[0], Department=department.NameEn, Designation=designation.TitleEn,
                JobTitle=designation.TitleEn, Grade=grade.Code, Branch=branches[i % branches.Length].NameEn,
                WorkLocation=branches[i % branches.Length].City, WorkEmail=$"employee{i+1:D2}@kx-sunday.demo",
                PersonalEmail=$"kx.sunday.person{i+1:D2}@example.test", Phone=$"+96655000{i+1:D4}", Gender=i%2==0?"Female":"Male",
                Nationality=nationalities[i], CountryCode="SAU", SaudiOrNonSaudi=saudi?"Saudi":"NonSaudi",
                IdType=saudi?"NationalId":"Iqama", IdNumber=$"{(saudi?1:2)}099920{i+1:D3}",
                IqamaNumber=saudi?string.Empty:$"2099920{i+1:D3}", IqamaExpiryDate=saudi?null:AnchorDate.AddYears(1).AddDays(i),
                GosiReference=company.GosiEmployerId, EstablishmentId=company.QiwaEstablishmentId,
                JoiningDate=AnchorUtc.AddYears(-1).AddDays(-i*7), ContractStartDate=AnchorDate.AddYears(-1).AddDays(-i*7),
                ContractEndDate=AnchorDate.AddYears(1).AddDays(i), ContractType="FixedTerm", EmploymentType="FullTime",
                Status=status, Salary=7_500m+(i%10)*750m, PayrollProfileCode="KSA-STANDARD",
                BankName=i%2==0?"Al Rajhi Bank":"Riyad Bank", BankIban=BuildSaudiIban(i),
                ReadinessState=readiness, ActivationBlockersCount=readiness=="Ready"?0:readiness=="Blocked"?2:1,
                ReadinessEvaluatedAtUtc=AnchorUtc, ProfileCompletenessScore=readiness=="Ready"?100m:85m,
                CreatedAtUtc=AnchorUtc,
            };
            employees.Add(employee);
        }
        db.Employees.AddRange(employees);
        await db.SaveChangesAsync(ct);

        foreach (var (department, index) in departments.Select((d, i) => (d, i)))
            department.ManagerEmployeeId = employees[index].Id;
        for (var i = 5; i < employees.Count; i++) employees[i].ManagerEmployeeId = employees[i % 5].Id;

        foreach (var (employee, i) in employees.Select((e, i) => (e, i)))
            db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile
            {
                TenantId=tenantId, EmployeeId=employee.Id, BankName=employee.BankName, Iban=employee.BankIban,
                AccountNumber=$"920{i+1:D7}", BankRoutingCode="RJHISARI", PaymentMethod="BankTransfer",
                SalaryCurrency="SAR", PayrollGroup="Main", SalaryStructureReference="KSA-STANDARD",
                WpsEligible=true, EosbEligible=true, MolId=employee.IdNumber,
                SocialInsuranceReference=$"{company.GosiEmployerId}-{i+1:D3}", CreatedAtUtc=AnchorUtc,
            });

        var personaSpecs = new[]
        {
            ("Admin", "Sunday Demo Admin", "admin@kx-sunday.demo", true),
            ("HR Director", "Noura HR Director", "hr.director@kx-sunday.demo", false),
            ("HR Manager", "Faisal HR Manager", "hr.manager@kx-sunday.demo", false),
            ("Payroll Manager", "Reem Payroll Manager", "payroll.manager@kx-sunday.demo", false),
            ("Payroll Officer", "Khalid Payroll Officer", "payroll.officer@kx-sunday.demo", false),
            ("Finance Approver", "Hessa Finance Approver", "finance.approver@kx-sunday.demo", false),
            ("Compliance Officer", "Mohammed Compliance", "compliance@kx-sunday.demo", false),
            ("Manager", "Sara People Manager", "manager@kx-sunday.demo", false),
            ("Auditor", "Tariq Auditor", "auditor@kx-sunday.demo", false),
            ("Employee", "Luluwah Employee", "employee@kx-sunday.demo", false),
        };
        foreach (var (roleName, fullName, email, groupScope) in personaSpecs)
        {
            if (!roles.TryGetValue(roleName, out var role)) throw new InvalidOperationException($"{FixtureId}: required role '{roleName}' is missing.");
            var user = new User
            {
                TenantId=tenantId, Email=email, NormalizedEmail=AuthService.Normalize(email), FullName=fullName,
                PasswordHash=hasher.Hash(password), AccessMode=roleName=="Employee"?"ReadOnly":"FullPortal",
                Status="Active", IsActive=true, IsEmailConfirmed=true, IsGroupScope=groupScope,
                MustChangePassword=false, PreferredLanguage="en", Timezone="Asia/Riyadh", CreatedAtUtc=AnchorUtc,
            };
            user.UserRoles.Add(new UserRole { User=user, RoleId=role.Id });
            db.Users.Add(user);
            if (!groupScope)
                db.UserEntityAccesses.Add(new UserEntityAccess
                {
                    TenantId=tenantId, User=user, CompanyId=company.Id, GrantMode=EntityGrantModes.SelectedCompanies,
                    Role=roleName, IsActive=true, GrantedAt=AnchorUtc,
                });
        }
        await db.SaveChangesAsync(ct);

        SeedAttendance(db, tenantId, employees);
        await SeedLeaveAsync(db, tenantId, company.Id, employees, ct);
        SeedOvertimeLoanBonus(db, tenantId, company.Id, employees);
        SeedPayroll(db, tenantId, company.Id, employees);
        await db.SaveChangesAsync(ct);
    }

    internal static string BuildSaudiIban(int zeroBasedEmployeeIndex)
    {
        if (zeroBasedEmployeeIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(zeroBasedEmployeeIndex));

        // Saudi BBAN = 2-digit bank code + 18-character account number (20 chars total).
        // Keep the account deterministic and synthetic; compute only the ISO check digits.
        var bankCode = 80 + zeroBasedEmployeeIndex % 10;
        var account = (zeroBasedEmployeeIndex + 1).ToString("D18", System.Globalization.CultureInfo.InvariantCulture);
        var iban = IbanValidator.WithValidCheckDigits($"SA00{bankCode:D2}{account}");
        if (!IbanValidator.IsSaudiIban(iban))
            throw new InvalidOperationException("The deterministic Sunday fixture generated an invalid Saudi IBAN.");
        return iban;
    }

    private static void SeedAttendance(ZayraDbContext db, Guid tenantId, IReadOnlyList<Employee> employees)
    {
        var cases = new[]
        {
            (0, new DateOnly(2026,9,17), "Present", 0, false, 510, 0, "Normal"),
            (1, new DateOnly(2026,9,17), "Late", 35, false, 445, 0, "Late arrival"),
            (2, new DateOnly(2026,9,17), "Missing Punch", 0, true, 0, 0, "Missing checkout"),
            (3, new DateOnly(2026,9,17), "Present", 0, false, 540, 120, "Overnight shift"),
            (4, new DateOnly(2026,9,18), "Weekend", 0, false, 240, 240, "Weekend work"),
            (5, new DateOnly(2026,9,17), "On Leave", 0, false, 0, 0, "Approved leave"),
            (6, new DateOnly(2026,9,17), "Absent", 0, false, 0, 0, "Unexcused absence"),
            (7, new DateOnly(2026,9,17), "Present", 0, false, 495, 15, "Duplicate punch source"),
        };
        foreach (var c in cases)
        {
            var employee = employees[c.Item1];
            var firstIn = c.Item3 is "Absent" or "On Leave" ? (DateTime?)null : new DateTime(c.Item2.Year,c.Item2.Month,c.Item2.Day,c.Item1==3?20:8,c.Item1==1?35:30,0,DateTimeKind.Utc);
            var lastOut = firstIn is null || c.Item5 ? (DateTime?)null : firstIn.Value.AddMinutes(c.Item6+c.Item7);
            var daily = new AttendanceDailyRecord
            {
                TenantId=tenantId, EmployeeId=employee.Id, EmployeeName=employee.FullName, Department=employee.Department,
                Branch=employee.Branch, WorkDate=c.Item2, FirstInUtc=firstIn, LastOutUtc=lastOut,
                TotalWorkedMinutes=c.Item6, LateMinutes=c.Item4, OvertimeMinutes=c.Item7, MissingPunch=c.Item5,
                Status=c.Item3, WorkMode=c.Item1==3?"Night shift":"Work from site", ProcessedAtUtc=AnchorUtc, CreatedAtUtc=AnchorUtc,
            };
            db.AttendanceDailyRecords.Add(daily);
            if (firstIn is not null)
            {
                db.AttendanceRawEvents.Add(new AttendanceRawEvent
                {
                    TenantId=tenantId, EmployeeId=employee.Id, EmployeeCode=employee.EmployeeCode, Source="Demo fixture",
                    PunchTimestampUtc=firstIn.Value, PunchDirection="In", LocationName=employee.Branch,
                    RawPayloadJson=$"{{\"fixture\":\"{FixtureId}\",\"case\":\"{c.Item8}\"}}", IsProcessed=true, CreatedAtUtc=AnchorUtc,
                });
                if (c.Item1 == 7)
                    db.AttendanceRawEvents.Add(new AttendanceRawEvent
                    {
                        TenantId=tenantId, EmployeeId=employee.Id, EmployeeCode=employee.EmployeeCode, Source="Demo fixture duplicate",
                        PunchTimestampUtc=firstIn.Value, PunchDirection="In", LocationName=employee.Branch,
                        RawPayloadJson=$"{{\"fixture\":\"{FixtureId}\",\"duplicate\":true}}", IsProcessed=false, CreatedAtUtc=AnchorUtc,
                    });
            }
        }
    }

    private static async Task SeedLeaveAsync(ZayraDbContext db, Guid tenantId, Guid companyId, IReadOnlyList<Employee> employees, CancellationToken ct)
    {
        var types = new[]
        {
            new LeaveType { TenantId=tenantId, Code="ANNUAL", NameEn="Annual Leave", NameAr="إجازة سنوية", Category="Annual", IsPaid=true, IsActive=true, SortOrder=1, CreatedAtUtc=AnchorUtc },
            new LeaveType { TenantId=tenantId, Code="SICK", NameEn="Sick Leave", NameAr="إجازة مرضية", Category="Sick", IsPaid=true, IsActive=true, SortOrder=2, CreatedAtUtc=AnchorUtc },
        };
        db.LeaveTypes.AddRange(types);
        await db.SaveChangesAsync(ct);
        var specs = new[]
        {
            (employees[5], types[0], "Submitted", AnchorDate.AddDays(8), AnchorDate.AddDays(10)),
            (employees[6], types[1], "Approved", AnchorDate.AddDays(-3), AnchorDate.AddDays(-2)),
            (employees[7], types[0], "Rejected", AnchorDate.AddDays(15), AnchorDate.AddDays(16)),
            (employees[8], types[0], "Cancelled", AnchorDate.AddDays(22), AnchorDate.AddDays(23)),
        };
        foreach (var (employee, type, status, start, end) in specs)
            db.LeaveRequests.Add(new LeaveRequest
            {
                TenantId=tenantId, CompanyId=companyId, EmployeeId=employee.Id, EmployeeName=employee.FullName,
                DepartmentName=employee.Department, DesignationTitle=employee.Designation, LeaveTypeId=type.Id,
                LeaveTypeName=type.NameEn, StartDate=start, EndDate=end, TotalDays=end.DayNumber-start.DayNumber+1,
                DayType="Full", Reason=$"{FixtureId} {status} scenario", Status=status,
                SubmittedAtUtc=AnchorUtc.AddDays(-5), DecidedAtUtc=status=="Submitted"?null:AnchorUtc.AddDays(-4),
                CancelledAtUtc=status=="Cancelled"?AnchorUtc.AddDays(-3):null, CreatedAtUtc=AnchorUtc.AddDays(-5),
            });
    }

    private static void SeedOvertimeLoanBonus(ZayraDbContext db, Guid tenantId, Guid companyId, IReadOnlyList<Employee> employees)
    {
        db.OvertimeRequests.Add(new OvertimeRequest
        {
            TenantId=tenantId, CompanyId=companyId, EmployeeId=employees[3].Id, EmployeeName=employees[3].FullName,
            WorkDate=new DateOnly(2026,9,17), StartTimeUtc=new DateTime(2026,9,17,17,30,0,DateTimeKind.Utc),
            EndTimeUtc=new DateTime(2026,9,17,19,30,0,DateTimeKind.Utc), RequestedMinutes=120, ApprovedMinutes=120,
            Source="Attendance", Reason="Month-end customer delivery", Status="Approved", DecisionVersion=1,
            CreatedAtUtc=AnchorUtc.AddDays(-3), DecidedAtUtc=AnchorUtc.AddDays(-2),
        });
        var loanType = new LoanType { TenantId=tenantId, Code="EMERGENCY", NameEn="Emergency Loan", NameAr="قرض طارئ", MaxAmount=20_000m, MaxInstallments=12, IsInterestFree=true, IsActive=true, CreatedAtUtc=AnchorUtc };
        db.LoanTypes.Add(loanType);
        db.EmployeeLoans.Add(new EmployeeLoan
        {
            TenantId=tenantId, CompanyId=companyId, EmployeeId=employees[4].PublicId, EmployeeIntId=employees[4].Id,
            EmployeeName=employees[4].FullName, LoanTypeId=loanType.Id, LoanTypeName=loanType.NameEn,
            LoanNumber="LN-2026-00920", RequestedAmount=12_000m, ApprovedAmount=12_000m,
            RequestedInstallments=12, ApprovedInstallments=12, InstallmentAmount=1_000m,
            DisbursementDate=new DateOnly(2026,7,1), RepaymentStartDate=new DateOnly(2026,8,1),
            TotalRepaid=2_000m, OutstandingBalance=10_000m, Status="Active", Notes=FixtureId, CreatedAtUtc=AnchorUtc.AddMonths(-2),
        });
        var bonusType = new BonusType { TenantId=tenantId, Code="DEMO-PERF", NameEn="Demo Performance Bonus", NameAr="مكافأة أداء", CalculationMethod="Fixed", Frequency="OneTime", IsIncludedInWps=true, TaxRegion="GCC", IsActive=true, CreatedAtUtc=AnchorUtc };
        var batch = new BonusBatch { TenantId=tenantId, BonusTypeId=bonusType.Id, BonusTypeName=bonusType.NameEn, BatchNumber="BON-2026-00920", BatchName="Sunday demo performance bonus", PaymentPeriod="2026-08", PaymentDate=new DateOnly(2026,8,31), TotalAmount=2_500m, EmployeeCount=1, Status="Approved", Notes=FixtureId, CreatedAtUtc=AnchorUtc };
        db.BonusTypes.Add(bonusType);
        db.BonusBatches.Add(batch);
        db.EmployeeBonuses.Add(new EmployeeBonus
        {
            TenantId=tenantId, CompanyId=companyId, BonusBatchId=batch.Id, EmployeeId=employees[2].PublicId,
            EmployeeIntId=employees[2].Id, EmployeeName=employees[2].FullName, Department=employees[2].Department,
            BonusTypeId=bonusType.Id, BonusTypeName=bonusType.NameEn, BasicSalary=employees[2].Salary??0,
            CalculationMethod="Fixed", CalculationValue=2_500m, GrossBonusAmount=2_500m, BonusAmount=2_500m,
            TaxRegion="GCC", PaymentPeriod="2026-08", Status="Approved", Notes=FixtureId, CreatedAtUtc=AnchorUtc,
        });
    }

    private static void SeedPayroll(ZayraDbContext db, Guid tenantId, Guid companyId, IReadOnlyList<Employee> employees)
    {
        var run = new PayrollRun
        {
            TenantId=tenantId, CompanyId=companyId, Year=2026, Month=8, Status="PendingFinanceReview",
            RunType=PayrollRunTypes.Regular, IncludesRecurringPay=true, EmployeeCount=ExpectedPayrollEmployees,
            TotalGrossSalary=ExpectedGross, TotalDeductions=ExpectedDeductions, TotalNetSalary=ExpectedNet,
            TotalEmployerStatutoryCost=ExpectedEmployerStatutory, CreatedAtUtc=AnchorUtc.AddDays(-10), ProcessedAtUtc=AnchorUtc.AddDays(-2),
        };
        db.PayrollRuns.Add(run);
        var amounts = new[]
        {
            (employees[0], 14_260m, 1_047.64m, 13_212.36m, 1_222.08m),
            (employees[1], 14_820m, 1_086.28m, 13_733.72m, 1_268.17m),
            (employees[2], 13_700m, 1_009.00m, 12_691.00m, 1_176.00m),
        };
        foreach (var (employee, gross, deduction, net, employer) in amounts)
        {
            db.PayrollRunEmployees.Add(new PayrollRunEmployee { TenantId=tenantId, PayrollRunId=run.Id, EmployeeId=employee.Id, GrossEarnings=gross, TotalDeductions=deduction, NetPay=net, Status="PendingFinanceReview" });
            db.PayrollEarnings.Add(new PayrollEarning { TenantId=tenantId, PayrollRunId=run.Id, EmployeeId=employee.Id, ComponentCode="GROSS-DEMO", ComponentName="Demo Gross Earnings", Amount=gross, Source=FixtureId });
            db.PayrollDeductions.Add(new PayrollDeduction { TenantId=tenantId, CompanyId=companyId, PayrollRunId=run.Id, EmployeeId=employee.Id, ComponentCode="EE-STAT-DEMO", ComponentName="Employee Statutory", Amount=deduction, Source=FixtureId, IsEmployerContribution=false });
            db.PayrollDeductions.Add(new PayrollDeduction { TenantId=tenantId, CompanyId=companyId, PayrollRunId=run.Id, EmployeeId=employee.Id, ComponentCode="ER-STAT-DEMO", ComponentName="Employer Statutory", Amount=employer, Source=FixtureId, IsEmployerContribution=true });
        }
    }

    private static async Task<FixtureShape> ReadShapeAsync(ZayraDbContext db, Guid tenantId, CancellationToken ct)
    {
        // This one-off executes without an authenticated request principal, so ZayraDbContext is
        // already in its documented startup/system read scope. Keep every canonical read on the
        // normal global-filter path; the explicit TenantId predicates below are defense in depth.
        var employeeCodes = await db.Employees.Where(e => e.TenantId == tenantId && !e.IsDeleted).Select(e => e.EmployeeCode).OrderBy(x => x).ToArrayAsync(ct);
        var personaRows = await (from u in db.Users
                                 join ur in db.UserRoles on u.Id equals ur.UserId
                                 join r in db.Roles on ur.RoleId equals r.Id
                                 where u.TenantId == tenantId && !u.IsDeleted
                                 select u.Email.ToLower() + "|" + r.Name).OrderBy(x => x).ToArrayAsync(ct);
        var attendance = await db.AttendanceDailyRecords.Where(a => a.TenantId == tenantId && !a.IsDeleted)
            .Join(db.Employees, a => a.EmployeeId, e => e.Id, (a,e) => e.EmployeeCode + "|" + a.WorkDate.ToString("yyyy-MM-dd") + "|" + a.Status)
            .OrderBy(x => x).ToArrayAsync(ct);
        var leave = await db.LeaveRequests.Where(l => l.TenantId == tenantId)
            .Join(db.Employees, l => l.EmployeeId, e => e.Id, (l,e) => e.EmployeeCode + "|" + l.Status)
            .OrderBy(x => x).ToArrayAsync(ct);
        var run = await db.PayrollRuns.SingleOrDefaultAsync(r => r.TenantId == tenantId && r.Year == 2026 && r.Month == 8 && r.Status == "PendingFinanceReview", ct);
        var payrollRows = run is null
            ? []
            : await db.PayrollRunEmployees
                .Where(x => x.TenantId == tenantId && x.PayrollRunId == run.Id)
                .Join(db.Employees, p => p.EmployeeId, e => e.Id,
                    (p,e) => new { e.EmployeeCode, p.GrossEarnings, p.TotalDeductions, p.NetPay, p.Status })
                .OrderBy(x => x.EmployeeCode).ToArrayAsync(ct);
        var payrollSignatures = payrollRows.Select(x => string.Join('|',
            x.EmployeeCode,
            x.GrossEarnings.ToString("0.00", CultureInfo.InvariantCulture),
            x.TotalDeductions.ToString("0.00", CultureInfo.InvariantCulture),
            x.NetPay.ToString("0.00", CultureInfo.InvariantCulture),
            x.Status)).ToArray();
        return new FixtureShape(
            employeeCodes.Length,
            await db.Branches.CountAsync(x => x.TenantId == tenantId && !x.IsDeleted, ct),
            await db.Departments.CountAsync(x => x.TenantId == tenantId && !x.IsDeleted, ct),
            employeeCodes, personaRows, attendance, leave, payrollSignatures, payrollRows.Length,
            run?.TotalGrossSalary ?? 0m, run?.TotalDeductions ?? 0m, run?.TotalNetSalary ?? 0m,
            run?.TotalEmployerStatutoryCost ?? 0m);
    }

    public static string[] ExpectedEmployeeCodes() => Enumerable.Range(1, ExpectedEmployees).Select(i => $"KXS-{i:D3}").OrderBy(x => x).ToArray();
    public static string[] ExpectedPersonaSignatures() => new[]
    {
        "admin@kx-sunday.demo|Admin", "hr.director@kx-sunday.demo|HR Director",
        "hr.manager@kx-sunday.demo|HR Manager", "payroll.manager@kx-sunday.demo|Payroll Manager",
        "payroll.officer@kx-sunday.demo|Payroll Officer", "finance.approver@kx-sunday.demo|Finance Approver",
        "compliance@kx-sunday.demo|Compliance Officer", "manager@kx-sunday.demo|Manager",
        "auditor@kx-sunday.demo|Auditor", "employee@kx-sunday.demo|Employee",
    }.OrderBy(x => x).ToArray();
    public static string[] ExpectedAttendanceSignatures() => new[]
    {
        "KXS-001|2026-09-17|Present", "KXS-002|2026-09-17|Late", "KXS-003|2026-09-17|Missing Punch",
        "KXS-004|2026-09-17|Present", "KXS-005|2026-09-18|Weekend", "KXS-006|2026-09-17|On Leave",
        "KXS-007|2026-09-17|Absent", "KXS-008|2026-09-17|Present",
    }.OrderBy(x => x).ToArray();
    public static string[] ExpectedLeaveSignatures() => new[]
    {
        "KXS-006|Submitted", "KXS-007|Approved", "KXS-008|Rejected", "KXS-009|Cancelled",
    }.OrderBy(x => x).ToArray();
    public static string[] ExpectedPayrollSignatures() => new[]
    {
        "KXS-001|14260.00|1047.64|13212.36|PendingFinanceReview",
        "KXS-002|14820.00|1086.28|13733.72|PendingFinanceReview",
        "KXS-003|13700.00|1009.00|12691.00|PendingFinanceReview",
    }.OrderBy(x => x).ToArray();

    private static void CompareSet(string label, string[] actual, string[] expected, ICollection<string> errors)
    {
        var normalizedActual = actual.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var normalizedExpected = expected.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        if (!normalizedActual.SequenceEqual(normalizedExpected, StringComparer.Ordinal))
            errors.Add($"{label} drift (same counts are not accepted): actual=[{string.Join(',', normalizedActual)}]");
    }
}
