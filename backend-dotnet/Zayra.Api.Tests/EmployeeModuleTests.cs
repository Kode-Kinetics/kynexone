using FluentAssertions;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Employees;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Email;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

public class EmployeeModuleTests
{
    [Fact]
    public async Task ApproveDraft_ActivatesEmployeeCreatesUserAndHistory()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenantAndEmployeeRole(db);
        // Draft approval now resolves org names to IDs (establishment Batch A) — the referenced
        // master data must exist or the approve legitimately 422s.
        db.Departments.Add(new Department { TenantId = tenantId, Code = "PPL", NameEn = "People", IsActive = true });
        db.Designations.Add(new Designation { TenantId = tenantId, Code = "HRO", TitleEn = "HR Officer", IsActive = true });
        db.Branches.Add(new Branch { TenantId = tenantId, Code = "DXB", NameEn = "Dubai", IsActive = true });
        await db.SaveChangesAsync();
        var controller = CreateController(db, tenantId);
        var draftResult = await controller.CreateDraft(new EmployeeDraftRequest("Review", "Sara Ahmed", "سارة أحمد", "sara.personal@example.com", "sara@zayra.local", "+9715000000", "Female", DateOnly.FromDateTime(DateTime.UtcNow.Date.AddYears(-30)), "Married", "Ali Ahmed", "+9715111111", "UAE", "UAE", "People", "HR Officer", "Dubai", "Dubai HQ", null, DateTime.UtcNow.Date, "Unlimited", "G5", "HR-001", DateOnly.FromDateTime(DateTime.UtcNow.Date), DateOnly.FromDateTime(DateTime.UtcNow.Date.AddYears(2)), DateOnly.FromDateTime(DateTime.UtcNow.Date.AddMonths(6)), "MONTHLY", 12000m, "Emirates NBD", "AE000000", "WPS-1", "DAY", "UAE-ANNUAL", "Zayra", DateOnly.FromDateTime(DateTime.UtcNow.Date.AddYears(-1)), "P123", DateOnly.FromDateTime(DateTime.UtcNow.Date.AddYears(5)), DateOnly.FromDateTime(DateTime.UtcNow.Date), "V123", DateOnly.FromDateTime(DateTime.UtcNow.Date.AddYears(2)), null, null, null, null, "784-0000", "LC-1", "VF-1", null, null, null, null, null, null), CancellationToken.None);
        var draft = Assert.IsType<EmployeeDraftDto>(Assert.IsType<CreatedResult>(draftResult.Result).Value);
        await controller.SubmitDraft(draft.Id, CancellationToken.None);

        var approval = await controller.ApproveDraft(draft.Id, CancellationToken.None);

        var profile = Assert.IsType<EmployeeDetailDto>(Assert.IsType<OkObjectResult>(approval.Result).Value);
        Assert.Equal("Active", profile.Status);
        Assert.StartsWith("EMP-", profile.EmployeeCode);
        Assert.NotNull(profile.UserAccountId);
        Assert.True(await db.EmployeeHistories.AnyAsync(x => x.EmployeeId == profile.Id && x.EventType == "Activated"));
    }

    [Fact]
    public async Task SensitiveUpdate_CreatesApprovalRequestWithoutChangingEmployee()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenantAndEmployeeRole(db);
        var employee = new Employee { TenantId = tenantId, EmployeeCode = "EMP-00001", FullName = "Sara Ahmed", Department = "People", Designation = "HR Officer", Status = "Active", JoiningDate = DateTime.UtcNow.Date, Salary = 10000m };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        var controller = CreateController(db, tenantId);

        var result = await controller.UpdateEmployee(employee.Id, new EmployeeUpdateRequest(DateOnly.FromDateTime(DateTime.UtcNow.Date), new() { ["salary"] = System.Text.Json.JsonDocument.Parse("15000").RootElement }), CancellationToken.None);

        Assert.IsType<AcceptedResult>(result);
        var unchanged = await db.Employees.FindAsync(employee.Id);
        Assert.Equal(10000m, unchanged!.Salary);
        Assert.True(await db.EmployeeChangeRequests.AnyAsync(x => x.EmployeeId == employee.Id && x.SensitiveFields == "salary"));
        Assert.True(await db.ApprovalRequests.AnyAsync(x => x.EntityName == nameof(EmployeeChangeRequest) && x.Status == "Pending"));
    }

    [Fact]
    public async Task ApprovalCenterDecision_AppliesSensitiveEmployeeChange()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenantAndEmployeeRole(db);
        var requesterId = Guid.NewGuid();
        var approverId = Guid.NewGuid();
        var employee = new Employee { TenantId = tenantId, EmployeeCode = "EMP-00002", FullName = "Omar Khan", Department = "People", Designation = "HR Officer", Status = "Active", JoiningDate = DateTime.UtcNow.Date, Salary = 10000m };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        var controller = CreateController(db, tenantId, requesterId);

        var result = await controller.UpdateEmployee(employee.Id, new EmployeeUpdateRequest(DateOnly.FromDateTime(DateTime.UtcNow.Date), new() { ["salary"] = System.Text.Json.JsonDocument.Parse("15000").RootElement }), CancellationToken.None);

        Assert.IsType<AcceptedResult>(result);
        var approval = await db.ApprovalRequests.SingleAsync(x => x.EntityName == nameof(EmployeeChangeRequest));
        var service = new ApprovalWorkflowService(db, new AuditService(db));

        var decided = await service.DecideAsync(
            tenantId,
            approval.Id,
            new Zayra.Api.Application.Approvals.ApprovalDecisionRequest("Approve", "Verified"),
            new Zayra.Api.Application.Auth.RequestContext("127.0.0.1", "tests", approverId, tenantId, ["HR Manager"], []),
            CancellationToken.None);

        decided!.Status.Should().Be("Approved");
        (await db.Employees.FindAsync(employee.Id))!.Salary.Should().Be(15000m);
        (await db.EmployeeChangeRequests.SingleAsync(x => x.EmployeeId == employee.Id)).Status.Should().Be("ApprovedApplied");
    }

    [Fact]
    public async Task CreateEmployee_WithBrowserDate_CreatesDraftWithUtcJoiningDate()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenantAndEmployeeRole(db);
        var controller = CreateController(db, tenantId);

        var browserDate = new DateTime(2026, 7, 14, 0, 0, 0, DateTimeKind.Unspecified);
        var request = new EmployeeCreateRequest(
            EmployeeCode: "EMP-CREATE-001",
            ManualEmployeeCode: true,
            EnglishName: "Create Flow Test",
            ArabicName: null,
            PreferredName: null,
            Gender: "Male",
            DateOfBirth: null,
            Nationality: "Pakistani",
            MaritalStatus: null,
            PersonalEmail: null,
            WorkEmail: null,
            MobileNumber: null,
            ProfilePhotoUrl: null,
            CompanyId: null,
            BranchId: null,
            DepartmentId: null,
            DesignationId: null,
            GradeId: null,
            CostCenterId: null,
            JobTitle: null,
            ReportingManagerEmployeeId: null,
            SecondLevelManagerEmployeeId: null,
            EmploymentType: "Full-Time",
            ContractType: "Unlimited",
            JoiningDate: browserDate,
            ConfirmationDate: null,
            ProbationStartDate: null,
            ProbationEndDate: null,
            NoticePeriodDays: null,
            WorkLocation: null,
            PayrollGroup: null,
            ShiftPolicyCode: null,
            LeavePolicyCode: null,
            AttendancePolicyCode: null,
            PayrollProfile: null,
            SalaryBreakdown: null,
            ComplianceRecords: null);

        var result = await controller.CreateEmployee(request, new Zayra.Api.Infrastructure.Employees.EmployeeManagementService(
            db,
            new AuditService(db),
            new FakeDocumentStorage(),
            new NotificationService(db, new FakeEmailService(), NullLogger<NotificationService>.Instance)),
            CancellationToken.None);

        var created = Assert.IsType<EmployeeDetailDto>(Assert.IsType<CreatedAtActionResult>(result.Result).Value);
        created.Status.Should().Be("Draft");
        created.EmployeeCode.Should().Be("EMP-CREATE-001");

        var stored = await db.Employees.SingleAsync(x => x.TenantId == tenantId && x.EmployeeCode == "EMP-CREATE-001");
        stored.JoiningDate.Kind.Should().Be(DateTimeKind.Utc);
        stored.JoiningDate.Should().Be(new DateTime(2026, 7, 14, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task CreateEmployee_WithDepartmentHead_AutoAssignsLineManagerAndReportingLine()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenantAndEmployeeRole(db);
        var manager = new Employee
        {
            TenantId = tenantId,
            EmployeeCode = "MGR-001",
            FullName = "Department Head",
            EnglishName = "Department Head",
            Gender = "Female",
            Department = "Operations",
            Status = "Active",
            JoiningDate = DateTime.SpecifyKind(new DateTime(2025, 1, 1), DateTimeKind.Utc)
        };
        db.Employees.Add(manager);
        await db.SaveChangesAsync();
        var department = new Department
        {
            TenantId = tenantId,
            Code = "OPS",
            NameEn = "Operations",
            ManagerEmployeeId = manager.Id,
            IsActive = true
        };
        db.Departments.Add(department);
        await db.SaveChangesAsync();

        var controller = CreateController(db, tenantId);
        var request = new EmployeeCreateRequest(
            EmployeeCode: "EMP-DEPT-MGR",
            ManualEmployeeCode: true,
            EnglishName: "Department Joiner",
            ArabicName: null,
            PreferredName: null,
            Gender: "Male",
            DateOfBirth: null,
            Nationality: "Pakistani",
            MaritalStatus: null,
            PersonalEmail: null,
            WorkEmail: null,
            MobileNumber: null,
            ProfilePhotoUrl: null,
            CompanyId: null,
            BranchId: null,
            DepartmentId: department.Id,
            DesignationId: null,
            GradeId: null,
            CostCenterId: null,
            JobTitle: null,
            ReportingManagerEmployeeId: null,
            SecondLevelManagerEmployeeId: null,
            EmploymentType: "Full-Time",
            ContractType: "Unlimited",
            JoiningDate: new DateTime(2026, 7, 14, 0, 0, 0, DateTimeKind.Unspecified),
            ConfirmationDate: null,
            ProbationStartDate: null,
            ProbationEndDate: null,
            NoticePeriodDays: null,
            WorkLocation: null,
            PayrollGroup: null,
            ShiftPolicyCode: null,
            LeavePolicyCode: null,
            AttendancePolicyCode: null,
            PayrollProfile: null,
            SalaryBreakdown: null,
            ComplianceRecords: null);

        var result = await controller.CreateEmployee(request, new Zayra.Api.Infrastructure.Employees.EmployeeManagementService(
            db,
            new AuditService(db),
            new FakeDocumentStorage(),
            new NotificationService(db, new FakeEmailService(), NullLogger<NotificationService>.Instance)),
            CancellationToken.None);

        var created = Assert.IsType<EmployeeDetailDto>(Assert.IsType<CreatedAtActionResult>(result.Result).Value);
        created.Department.Should().Be("Operations");
        created.ManagerEmployeeId.Should().Be(manager.Id);
        (await db.ReportingLines.AnyAsync(x =>
            x.TenantId == tenantId &&
            x.EmployeeId == created.Id &&
            x.ManagerEmployeeId == manager.Id &&
            x.RelationshipType == "SolidLine" &&
            x.IsPrimary &&
            x.IsActive)).Should().BeTrue();
    }

    private static ZayraDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        return new ZayraDbContext(options);
    }

    private static async Task<Guid> SeedTenantAndEmployeeRole(ZayraDbContext db)
    {
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Zayra HQ", Slug = "zayra" };
        var role = new Role { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Employee", NormalizedName = "EMPLOYEE", Description = "Employee" };
        db.Tenants.Add(tenant);
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        return tenant.Id;
    }

    // ── Test: Employee CSV import with payroll columns creates PayrollProfile + SalaryStructure ──

    [Fact]
    public async Task EmployeeImport_WithPayrollColumns_CreatesPayrollProfileAndSalaryStructure()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenantAndEmployeeRole(db);
        await SeedGrade(db, tenantId, "G3", 1_000m, 20_000m);
        var ctrl = CreateController(db, tenantId);

        const string csv =
            "EmployeeCode,FullName,WorkEmail,JoiningDate,Grade,BasicSalary,HousingAllowance,TransportAllowance,OtherAllowance,Currency,IBAN,BankName,MolId\n" +
            "EMP-PAY-001,Ahmad Al-Rashidi,ahmad@test.com,2024-01-15,G3,8000,2000,1000,500,SAR,SA0380000000608010167519,Al Rajhi Bank,MOL-001\n";

        var result = await ctrl.Import(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);

        var employee = await db.Employees.FirstOrDefaultAsync(e => e.TenantId == tenantId && e.EmployeeCode == "EMP-PAY-001");
        Assert.NotNull(employee);
        Assert.Equal(11500m, employee.Salary);
        Assert.Equal("Al Rajhi Bank", employee.BankName);
        Assert.Equal("SA0380000000608010167519", employee.BankIban);

        var profile = await db.EmployeePayrollProfiles.FirstOrDefaultAsync(p => p.TenantId == tenantId && p.EmployeeId == employee.Id);
        Assert.NotNull(profile);
        Assert.Equal("SA0380000000608010167519", profile.Iban);
        Assert.Equal("SAR", profile.SalaryCurrency);
        Assert.Equal("MOL-001", profile.MolId);
        Assert.True(profile.WpsEligible);
        Assert.True(profile.EosbEligible);

        var salary = await db.EmployeeSalaryStructures.FirstOrDefaultAsync(s => s.TenantId == tenantId && s.EmployeeId == employee.Id);
        Assert.NotNull(salary);
        Assert.Equal(8000m, salary.BasicSalary);
        Assert.Equal(2000m, salary.HousingAllowance);
        Assert.Equal(1000m, salary.TransportAllowance);
        Assert.Equal(500m, salary.OtherAllowance);
        Assert.Equal("SAR", salary.Currency);
        Assert.True(salary.IsActive);
    }

    [Fact]
    public void EmployeeImportTemplate_IncludesModalAndStatutoryFields()
    {
        using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var ctrl = CreateController(db, tenantId);

        var result = ctrl.ImportTemplate();

        var file = Assert.IsType<FileContentResult>(result);
        var csv = System.Text.Encoding.UTF8.GetString(file.FileContents);
        foreach (var header in new[]
        {
            "PreferredName", "PersonalEmail", "DateOfBirth", "MaritalStatus",
            "ConfirmationDate", "ProbationStartDate", "ProbationEndDate", "NoticePeriodDays",
            "ShiftPolicyCode", "LeavePolicyCode", "AttendancePolicyCode",
            "BasicSalary", "HousingAllowance", "TransportAllowance", "FixedDeduction",
            "PassportNumber", "PassportExpiryDate", "VisaNumber", "VisaExpiryDate",
            "EmiratesId", "IqamaNumber", "GosiReference", "SaudiOrNonSaudi", "QiwaSyncStatus"
        })
        {
            Assert.Contains(header, csv);
        }
    }

    [Fact]
    public async Task EmployeeImport_WithModalAndStatutoryColumns_PersistsEmployeeMasterFields()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenantAndEmployeeRole(db);
        await SeedGrade(db, tenantId, "G4", 1_000m, 30_000m);
        var ctrl = CreateController(db, tenantId);

        const string csv =
            "EmployeeCode,FullName,ArabicName,PreferredName,PersonalEmail,WorkEmail,Phone,Gender,DateOfBirth,Nationality,MaritalStatus,CountryCode,JoiningDate,ConfirmationDate,ProbationStartDate,ProbationEndDate,NoticePeriodDays,Grade,BasicSalary,Currency,ShiftPolicyCode,LeavePolicyCode,AttendancePolicyCode,PassportNumber,PassportExpiryDate,VisaNumber,VisaExpiryDate,EmiratesId,IqamaNumber,GosiReference,SaudiOrNonSaudi,IdType,IdNumber,QiwaSyncStatus\n" +
            "EMP-FULL-001,Fatima Noor,فاطمة نور,Fati,fatima.personal@test.com,fatima@test.com,+971500000001,Female,1992-03-05,UAE,Married,AE,2024-01-15,2024-07-15,2024-01-15,2024-07-15,30,G4,12000,AED,SHIFT-DAY,UAE-ANNUAL,ATT-STD,P1234567,2030-01-01,V7654321,2027-01-01,784-1992-1234567-1,IQ-123,GOSI-123,NonSaudi,EmiratesId,784-1992-1234567-1,Ready\n";

        var result = await ctrl.Import(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var employee = await db.Employees.SingleAsync(e => e.TenantId == tenantId && e.EmployeeCode == "EMP-FULL-001");
        Assert.Equal("Fati", employee.PreferredName);
        Assert.Equal("fatima.personal@test.com", employee.PersonalEmail);
        Assert.Equal(new DateOnly(1992, 3, 5), employee.DateOfBirth);
        Assert.Equal("Married", employee.MaritalStatus);
        Assert.Equal("AE", employee.CountryCode);
        Assert.Equal(new DateOnly(2024, 7, 15), employee.ConfirmationDate);
        Assert.Equal(new DateOnly(2024, 1, 15), employee.ProbationStartDate);
        Assert.Equal(new DateOnly(2024, 7, 15), employee.ProbationEndDate);
        Assert.Equal(30, employee.NoticePeriodDays);
        Assert.Equal("SHIFT-DAY", employee.ShiftPolicyCode);
        Assert.Equal("UAE-ANNUAL", employee.LeavePolicyCode);
        Assert.Equal("ATT-STD", employee.AttendancePolicyCode);
        Assert.Equal("P1234567", employee.PassportNumber);
        Assert.Equal(new DateOnly(2030, 1, 1), employee.PassportExpiryDate);
        Assert.Equal("V7654321", employee.VisaNumber);
        Assert.Equal(new DateOnly(2027, 1, 1), employee.VisaExpiryDate);
        Assert.Equal("784-1992-1234567-1", employee.EmiratesId);
        Assert.Equal("IQ-123", employee.IqamaNumber);
        Assert.Equal("GOSI-123", employee.GosiReference);
        Assert.Equal("NonSaudi", employee.SaudiOrNonSaudi);
        Assert.Equal("EmiratesId", employee.IdType);
        Assert.Equal("Ready", employee.QiwaSyncStatus);
    }

    // ── Test: Currency defaults to SAR when blank ─────────────────────────────

    [Fact]
    public async Task EmployeeImport_BlankCurrency_DefaultsToSar()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenantAndEmployeeRole(db);
        await SeedGrade(db, tenantId, "G3", 1_000m, 20_000m);
        var ctrl = CreateController(db, tenantId);

        const string csv =
            "EmployeeCode,FullName,JoiningDate,Grade,BasicSalary,Currency\n" +
            "EMP-CUR-001,Test Employee,2024-01-15,G3,5000,\n";

        await ctrl.Import(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None);

        var employee = await db.Employees.FirstOrDefaultAsync(e => e.TenantId == tenantId && e.EmployeeCode == "EMP-CUR-001");
        Assert.NotNull(employee);
        var profile = await db.EmployeePayrollProfiles.FirstOrDefaultAsync(p => p.TenantId == tenantId && p.EmployeeId == employee.Id);
        Assert.NotNull(profile);
        Assert.Equal("SAR", profile.SalaryCurrency);
        var salaryRec = await db.EmployeeSalaryStructures.FirstOrDefaultAsync(s => s.TenantId == tenantId && s.EmployeeId == employee.Id);
        Assert.NotNull(salaryRec);
        Assert.Equal("SAR", salaryRec.Currency);
    }

    // ── Test: Import preview with invalid IBAN produces warning (not error) ───

    [Fact]
    public async Task EmployeeImportPreview_InvalidIban_ProducesWarning()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenantAndEmployeeRole(db);
        var ctrl = CreateController(db, tenantId);

        const string csv =
            "EmployeeCode,FullName,JoiningDate,IBAN\n" +
            "EMP-IBAN-001,Test Employee,2024-01-15,INVALID-IBAN\n";

        var result = await ctrl.ImportPreview(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        // Row is WillCreate (not Error) because IBAN issue is a warning only
        Assert.Contains("WillCreate", json);
        Assert.Contains("IBAN", json);
        Assert.Contains("invalid", json);
        // No DB records created (preview is dry-run)
        Assert.Equal(0, await db.EmployeePayrollProfiles.CountAsync());
    }

    // ── Test: 15-row import on a fresh empty tenant succeeds without pre-setup ─

    [Fact]
    public async Task EmployeeImport_FreshTenant_15Rows_CreatesAll()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenantAndEmployeeRole(db);
        await SeedGrade(db, tenantId, "G3", 1_000m, 20_000m);
        var ctrl = CreateController(db, tenantId);

        var headerLine = "EmployeeCode,FullName,Department,JoiningDate,Grade,BasicSalary,Currency";
        var dataLines = Enumerable.Range(1, 15).Select(i =>
            $"EMP-{i:D3},Employee {i},Engineering,2024-01-01,G3,{5000 + i * 100},SAR");
        var csv = string.Join("\n", new[] { headerLine }.Concat(dataLines)) + "\n";

        var result = await ctrl.Import(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains("\"created\":15", json);
        Assert.Contains("\"skipped\":0", json);

        var empCount = await db.Employees.CountAsync(e => e.TenantId == tenantId && !e.IsDeleted);
        Assert.Equal(15, empCount);
        // All 15 should have payroll profiles (salary provided)
        var profileCount = await db.EmployeePayrollProfiles.CountAsync(p => p.TenantId == tenantId);
        Assert.Equal(15, profileCount);
        // All 15 should have salary structures
        var salaryCount = await db.EmployeeSalaryStructures.CountAsync(s => s.TenantId == tenantId);
        Assert.Equal(15, salaryCount);
    }

    [Fact]
    public async Task EmployeeImport_WithSalaryButNoGrade_SkipsBeforeCreatingEmployee()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenantAndEmployeeRole(db);
        var ctrl = CreateController(db, tenantId);

        const string csv =
            "EmployeeCode,FullName,JoiningDate,BasicSalary,Currency\n" +
            "EMP-NOGRADE-001,No Grade Salary,2024-01-15,5000,SAR\n";

        var result = await ctrl.Import(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains("\"created\":0", json);
        Assert.Contains("requires a valid grade", json);
        Assert.False(await db.Employees.AnyAsync(e => e.TenantId == tenantId && e.EmployeeCode == "EMP-NOGRADE-001"));
    }

    [Fact]
    public async Task EmployeeImport_WithSalaryOutsideGradeRange_SkipsBeforeCreatingEmployee()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenantAndEmployeeRole(db);
        await SeedGrade(db, tenantId, "G1", 1_000m, 4_000m);
        var ctrl = CreateController(db, tenantId);

        const string csv =
            "EmployeeCode,FullName,JoiningDate,Grade,BasicSalary,Currency\n" +
            "EMP-RANGE-001,Range Fail,2024-01-15,G1,5000,SAR\n";

        var result = await ctrl.Import(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains("\"created\":0", json);
        Assert.Contains("outside grade G1 range", json);
        Assert.False(await db.Employees.AnyAsync(e => e.TenantId == tenantId && e.EmployeeCode == "EMP-RANGE-001"));
    }

    // ── Regression: an OPTIONAL org reference (CostCenter/Branch) that doesn't resolve must
    //    NOT skip the whole row. A blank code is already imported as null, so an unrecognized
    //    code degrades to a warning + import-without-the-reference. This is what unblocks bulk
    //    imports into a freshly-created company that has no cost centers seeded yet. ──

    [Fact]
    public async Task EmployeeImport_UnknownCostCenter_ImportsEmployeeWithWarningNotSkipped()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenantAndEmployeeRole(db);
        var company = await SeedCompany(db, tenantId, "Widget Corp");
        var ctrl = CreateController(db, tenantId);

        const string csv =
            "EmployeeCode,FullName,CompanyLegalName,CostCenterCode,JoiningDate\n" +
            "EMP-CC-001,Bilal Ahmed,Widget Corp,HR COST CENTER,2024-01-15\n";

        var result = await ctrl.Import(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains("\"created\":1", json);
        Assert.Contains("\"skipped\":0", json);
        Assert.Contains("imported without a cost center", json);

        var employee = await db.Employees.SingleAsync(e => e.TenantId == tenantId && e.EmployeeCode == "EMP-CC-001");
        Assert.Equal(company.Id, employee.CompanyId);
        Assert.Null(employee.CostCenterId);
    }

    // ── Regression: a blank FullName now records a per-row error instead of skipping silently,
    //    so the user can see WHY nothing was created (previously it returned created:0 with an
    //    empty errors[] — a success-looking no-op). ──

    [Fact]
    public async Task EmployeeImport_BlankFullName_RecordsPerRowErrorInsteadOfSilentSkip()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenantAndEmployeeRole(db);
        var ctrl = CreateController(db, tenantId);

        const string csv =
            "EmployeeCode,FullName,JoiningDate\n" +
            "EMP-BLANK-001,,2024-01-15\n";

        var result = await ctrl.Import(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains("\"created\":0", json);
        Assert.Contains("missing FullName", json);
        Assert.False(await db.Employees.AnyAsync(e => e.TenantId == tenantId && e.EmployeeCode == "EMP-BLANK-001"));
    }

    // ── Regression: two rows sharing an EmployeeCode in the SAME file no longer both slip past
    //    the DB-only duplicate check and blow up the batch SaveChanges — the second is skipped
    //    with a clear error and the first still persists. ──

    [Fact]
    public async Task EmployeeImport_DuplicateEmployeeCodeWithinFile_SkipsSecondAndPersistsFirst()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenantAndEmployeeRole(db);
        var ctrl = CreateController(db, tenantId);

        const string csv =
            "EmployeeCode,FullName,JoiningDate\n" +
            "EMP-DUP-001,First Person,2024-01-15\n" +
            "EMP-DUP-001,Second Person,2024-02-15\n";

        var result = await ctrl.Import(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains("\"created\":1", json);
        Assert.Contains("duplicated within the import file", json);
        Assert.Equal(1, await db.Employees.CountAsync(e => e.TenantId == tenantId && e.EmployeeCode == "EMP-DUP-001"));
    }

    // ── Regression: an IBAN that fails the ISO 13616 mod-97 checksum is now caught at IMPORT time via a
    //    warning (previously only a weak structural check that missed checksum failures, so a bad IBAN
    //    slipped through and only blocked the payroll run weeks later). Import still succeeds; the IBAN
    //    must be corrected before payroll. ──

    [Fact]
    public async Task EmployeeImport_InvalidIbanChecksum_ImportsWithMod97Warning()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenantAndEmployeeRole(db);
        await SeedGrade(db, tenantId, "G3", 1_000m, 20_000m);
        var ctrl = CreateController(db, tenantId);

        // SA4420000009876543219876 is structurally valid (SA + 24 chars) but fails mod-97 (remainder 94).
        const string csv =
            "EmployeeCode,FullName,JoiningDate,Grade,BasicSalary,Currency,IBAN,BankName\n" +
            "EMP-IBAN-BAD,Bad Iban,2024-01-15,G3,5000,SAR,SA4420000009876543219876,Al Rajhi Bank\n";

        var result = await ctrl.Import(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains("\"created\":1", json);
        Assert.Contains("mod-97", json);
        Assert.True(await db.Employees.AnyAsync(e => e.TenantId == tenantId && e.EmployeeCode == "EMP-IBAN-BAD"));
    }

    private static async Task<Company> SeedCompany(ZayraDbContext db, Guid tenantId, string legalName)
    {
        var company = new Company
        {
            TenantId = tenantId,
            LegalNameEn = legalName,
            CountryCode = "SA",
            Jurisdiction = "test",
            RegistrationNumber = $"RC-{Guid.NewGuid():N}",
            DefaultCurrency = "SAR",
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow,
        };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        return company;
    }

    private static async Task SeedGrade(ZayraDbContext db, Guid tenantId, string code, decimal min, decimal max)
    {
        db.Grades.Add(new Grade
        {
            TenantId = tenantId,
            Code = code,
            Name = code,
            Level = 1,
            MinSalary = min,
            MidSalary = (min + max) / 2,
            MaxSalary = max,
            Currency = "SAR",
            IsActive = true
        });
        await db.SaveChangesAsync();
    }

    private static EmployeesController CreateController(ZayraDbContext db, Guid tenantId, Guid? userId = null)
    {
        var audit = new AuditService(db);
        var controller = new EmployeesController(
            db,
            new Pbkdf2PasswordHasher(),
            audit,
            new FakeDocumentStorage(),
            new NotificationService(db, new FakeEmailService(), NullLogger<NotificationService>.Instance),
            new FakeHijriDateService(),
            new Zayra.Api.Infrastructure.Common.DataScopeService(db),
            new FakeLetterService(),
            new ApprovalWorkflowService(db, audit));
        var effectiveUserId = userId ?? Guid.NewGuid();
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, effectiveUserId.ToString()),
            new Claim(ClaimTypes.Role, "Admin")
        }, "Test"));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } };
        return controller;
    }
}

file sealed class FakeDocumentStorage : Zayra.Api.Infrastructure.Documents.IDocumentStorage
{
    public Task<Zayra.Api.Infrastructure.Documents.StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken cancellationToken) => Task.FromResult(new Zayra.Api.Infrastructure.Documents.StoredDocument(file.FileName, file.ContentType, "storage/documents/test", "/tmp/test"));
    public string ResolvePath(string storageUrl) => "/tmp/test";
    public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) =>
        Task.FromResult(Array.Empty<byte>());
}

file sealed class FakeHijriDateService : Zayra.Api.Infrastructure.Localization.IHijriDateService
{
    public Zayra.Api.Infrastructure.Localization.DateConversionDto FromGregorian(DateOnly date) => new(date.ToString("yyyy-MM-dd"), "1447-01-01", 1447, 1, 1);
}

file sealed class FakeEmailService : IEmailService
{
    public Task SendAsync(string toAddress, string toName, string subject, string htmlBody,
        IReadOnlyList<EmailAttachment>? attachments = null, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(false);
}

file sealed class FakeLetterService : ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(PayslipData data, CancellationToken cancellationToken = default)
        => Task.FromResult(Array.Empty<byte>());

    public Task<byte[]> GenerateAppointmentLetterAsync(LetterData data, CancellationToken cancellationToken = default)
        => Task.FromResult(Array.Empty<byte>());

    public Task<byte[]> GenerateExperienceLetterAsync(LetterData data, CancellationToken cancellationToken = default)
        => Task.FromResult(Array.Empty<byte>());

    public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData data, CancellationToken cancellationToken = default)
        => Task.FromResult(Array.Empty<byte>());
}
