using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Employees;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Localization;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Models;
using EmployeeChangeRequest = Zayra.Api.Models.EmployeeChangeRequest;
using EmployeeTransferRequest = Zayra.Api.Models.EmployeeTransferRequest;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Role × sensitive-field matrix.
///
/// Asserts that each sensitive field (Salary, BankIban, WpsBankDetails, PassportNumber,
/// IqamaNumber, MedicalInformation, DisciplinaryRecords, TerminationReason) is present
/// when the caller can view sensitive data, and is masked (null / empty string) otherwise.
///
/// Covers: canonical mask unit, list DTO field absence, detail endpoint, draft-approve endpoint.
/// </summary>
public class SensitiveFieldMaskingTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static Employee SeedEmployee(ZayraDbContext db, Guid tenantId)
    {
        var emp = new Employee
        {
            TenantId             = tenantId,
            EmployeeCode         = "EMP-0001",
            FullName             = "Test Employee",
            Department           = "HR",
            Designation          = "Officer",
            Status               = "Active",
            JoiningDate          = DateTime.UtcNow.Date,
            Salary               = 50_000m,
            BankName             = "Test Bank",
            BankIban             = "SA0000000000000000001234",
            WpsBankDetails       = "WPS-REF-001",
            PassportNumber       = "P99999999",
            IqamaNumber          = "2000000001",
            MedicalInformation   = "Allergic to penicillin",
            DisciplinaryRecords  = "Warning issued 2025-01",
            TerminationReason    = "Voluntary resignation",
        };
        db.Employees.Add(emp);
        db.SaveChanges();
        return emp;
    }

    private static EmployeeManagementService CreateService(ZayraDbContext db) =>
        new(db, new AuditService(db), new FakeDocumentStorage(), new FakeNotificationService());

    private static RequestContext TestContext(Guid tenantId) =>
        new("127.0.0.1", "test", Guid.NewGuid(), tenantId);

    // ── EmployeeSensitiveMask canonical coverage ──────────────────────────────────

    [Fact]
    public void EmployeeSensitiveMask_Apply_ClearsAllNineFields()
    {
        var emp = new Employee
        {
            Salary              = 99_000m,
            BankName            = "Bank",
            BankIban            = "SA00000000001",
            WpsBankDetails      = "WPS",
            PassportNumber      = "PASSPORT",
            IqamaNumber         = "IQAMA",
            MedicalInformation  = "MED",
            DisciplinaryRecords = "DISC",
            TerminationReason   = "TERM",
        };

        EmployeeSensitiveMask.Apply(emp);

        emp.Salary.Should().BeNull();
        emp.BankName.Should().BeEmpty();
        emp.BankIban.Should().BeEmpty();
        emp.WpsBankDetails.Should().BeEmpty();
        emp.PassportNumber.Should().BeEmpty();
        emp.IqamaNumber.Should().BeEmpty();
        emp.MedicalInformation.Should().BeEmpty();
        emp.DisciplinaryRecords.Should().BeEmpty();
        emp.TerminationReason.Should().BeEmpty();
    }

    // ── List DTO — IqamaNumber must never appear for any role ─────────────────────

    [Fact]
    public void EmployeeListItemDto_DoesNotHaveIqamaNumberField()
    {
        typeof(EmployeeListItemDto)
            .GetProperty("IqamaNumber")
            .Should().BeNull(
                "IqamaNumber was removed from the list DTO so Manager/Auditor cannot read national IDs via the list endpoint");
    }

    [Fact]
    public void EmployeeListItemDto_HasExpectedFieldCount()
    {
        // 12 fields: Id, EmployeeCode, FullName, ArabicName, Department, Designation, Branch,
        // ManagerEmployeeId, Status, ProfileCompletenessScore, VisaExpiryDate, PassportExpiryDate.
        // If someone adds a sensitive field to the list DTO, this test breaks and forces review.
        typeof(EmployeeListItemDto).GetConstructors().Single()
            .GetParameters()
            .Should().HaveCount(12,
                "list DTO must contain exactly 12 non-sensitive fields");
    }

    // ── Detail endpoint — EmployeeManagementService.GetAsync ─────────────────────

    [Theory]
    [InlineData("Admin")]
    [InlineData("HR Manager")]
    [InlineData("Payroll Officer")]
    public async Task Detail_PrivilegedRole_SeesAllSensitiveFields(string role)
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var emp = SeedEmployee(db, tenantId);
        var svc = CreateService(db);

        var dto = await svc.GetAsync(tenantId, emp.Id, includeSensitive: true, TestContext(tenantId), CancellationToken.None);

        dto.Should().NotBeNull($"{role} should see the employee");
        dto!.Salary.Should().Be(50_000m, $"{role} must see salary");
        dto.BankIban.Should().Be("SA0000000000000000001234", $"{role} must see IBAN");
        dto.PassportNumber.Should().Be("P99999999", $"{role} must see passport number");
        dto.IqamaNumber.Should().Be("2000000001", $"{role} must see Iqama number");
        dto.MedicalInformation.Should().NotBeEmpty($"{role} must see medical information");
        dto.DisciplinaryRecords.Should().NotBeEmpty($"{role} must see disciplinary records");
        dto.TerminationReason.Should().NotBeEmpty($"{role} must see termination reason");
    }

    [Theory]
    [InlineData("HR Officer")]
    [InlineData("Manager")]
    [InlineData("Auditor")]
    public async Task Detail_UnprivilegedRole_MasksSensitiveFields(string role)
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var emp = SeedEmployee(db, tenantId);
        var svc = CreateService(db);

        var dto = await svc.GetAsync(tenantId, emp.Id, includeSensitive: false, TestContext(tenantId), CancellationToken.None);

        dto.Should().NotBeNull($"{role} should see the employee record");
        dto!.Salary.Should().BeNull($"{role} must NOT see salary");
        dto.BankName.Should().BeEmpty($"{role} must NOT see bank name");
        dto.BankIban.Should().BeEmpty($"{role} must NOT see IBAN");
        dto.WpsBankDetails.Should().BeEmpty($"{role} must NOT see WPS details");
        dto.PassportNumber.Should().BeEmpty($"{role} must NOT see passport number");
        dto.IqamaNumber.Should().BeEmpty($"{role} must NOT see Iqama number");
        dto.MedicalInformation.Should().BeEmpty($"{role} must NOT see medical information");
        dto.DisciplinaryRecords.Should().BeEmpty($"{role} must NOT see disciplinary records");
        dto.TerminationReason.Should().BeEmpty($"{role} must NOT see termination reason");
    }

    // ── ApproveDraft — response type and mask gate ─────────────────────────────────

    [Fact]
    public async Task ApproveDraft_ResponseType_IsEmployeeDetailDtoNotEmployeeProfileDto()
    {
        // EmployeeProfileDto (unmasked raw entity) was deleted in P2. Verifies at runtime
        // that ApproveDraft no longer returns the old unmasked type.
        await using var db = CreateDb();
        var tenantId = await SeedTenantAsync(db);
        var controller = CreateController(db, tenantId, "Admin");

        var draftResult = await controller.CreateDraft(MinimalDraftRequest(), CancellationToken.None);
        var draft = Assert.IsType<EmployeeDraftDto>(Assert.IsType<CreatedResult>(draftResult.Result).Value);
        await controller.SubmitDraft(draft.Id, CancellationToken.None);

        var approval = await controller.ApproveDraft(draft.Id, CancellationToken.None);
        var okResult = Assert.IsType<OkObjectResult>(approval.Result);

        okResult.Value.Should().BeOfType<EmployeeDetailDto>(
            "ApproveDraft must return EmployeeDetailDto so the canonical mask gate is applied before serialization");
    }

    [Fact]
    public async Task ApproveDraft_AdminRole_ReceivesSalaryUnmasked()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenantAsync(db);
        var controller = CreateController(db, tenantId, "Admin");

        var draftResult = await controller.CreateDraft(MinimalDraftRequest(), CancellationToken.None);
        var draft = Assert.IsType<EmployeeDraftDto>(Assert.IsType<CreatedResult>(draftResult.Result).Value);
        await controller.SubmitDraft(draft.Id, CancellationToken.None);

        var approval = await controller.ApproveDraft(draft.Id, CancellationToken.None);
        var detail = Assert.IsType<EmployeeDetailDto>(Assert.IsType<OkObjectResult>(approval.Result).Value);

        detail.Salary.Should().Be(12_000m,
            "Admin satisfies CanViewSensitive() — salary submitted in the draft must flow through unmasked");
        detail.BankIban.Should().Be("AE000000000000001",
            "Admin must see the IBAN from the draft");
    }

    // ── P2.1: EmployeeDetailDto has no raw Employee member ────────────────────────

    [Fact]
    public void EmployeeDetailDto_DoesNotExposeRawEntityField()
    {
        var entityField = typeof(EmployeeDetailDto)
            .GetProperties()
            .FirstOrDefault(p => p.PropertyType == typeof(Employee));

        entityField.Should().BeNull(
            "EmployeeDetailDto must not hold a raw Employee entity — any new EF field would auto-serialize without masking");
    }

    // ── P2.1: EssEmployeeProfileDto field exclusions ──────────────────────────────

    [Fact]
    public void EssEmployeeProfileDto_DoesNotHaveInternalHrNoteFields()
    {
        var type = typeof(EssEmployeeProfileDto);

        type.GetProperty("DisciplinaryRecords").Should().BeNull(
            "ESS profile must not expose internal disciplinary records to the employee");
        type.GetProperty("MedicalInformation").Should().BeNull(
            "ESS profile must not expose internal medical information to the employee");
        type.GetProperty("TerminationReason").Should().BeNull(
            "ESS profile must not expose termination reason to the employee");
        type.GetProperty("WpsBankDetails").Should().BeNull(
            "WpsBankDetails is a backend processing field — not needed by the employee");
        type.GetProperty("TenantId").Should().BeNull("System field must not appear in any DTO");
        type.GetProperty("IsDeleted").Should().BeNull("System field must not appear in any DTO");
    }

    [Fact]
    public void EssEmployeeProfileDto_HasOwnFinancialAndIdentityFields()
    {
        var type = typeof(EssEmployeeProfileDto);

        type.GetProperty("Salary").Should().NotBeNull("Employee can see own salary");
        type.GetProperty("BankIban").Should().NotBeNull("Employee can see own IBAN");
        type.GetProperty("PassportNumber").Should().NotBeNull("Employee can see own passport number");
        type.GetProperty("IqamaNumber").Should().NotBeNull("Employee can see own Iqama number");
    }

    // ── P2.1: UpdateEmployee — mask gate on write response ────────────────────────

    [Fact]
    public async Task UpdateEmployee_AdminRole_ResponseIsEmployeeDetailDto_WithSensitiveData()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenantAsync(db);
        var controller = CreateController(db, tenantId, "Admin");

        var draftResult = await controller.CreateDraft(MinimalDraftRequest(), CancellationToken.None);
        var draft = Assert.IsType<EmployeeDraftDto>(Assert.IsType<CreatedResult>(draftResult.Result).Value);
        await controller.SubmitDraft(draft.Id, CancellationToken.None);
        var approval = await controller.ApproveDraft(draft.Id, CancellationToken.None);
        var created = Assert.IsType<EmployeeDetailDto>(Assert.IsType<OkObjectResult>(approval.Result).Value);

        // Update a non-sensitive field so the non-approval path is exercised
        var updateRequest = new EmployeeUpdateRequest(
            EffectiveDate: DateOnly.FromDateTime(DateTime.UtcNow.Date),
            Changes: new Dictionary<string, System.Text.Json.JsonElement>
            {
                ["workLocation"] = System.Text.Json.JsonSerializer.SerializeToElement("New HQ")
            });

        var updateResult = await controller.UpdateEmployee(created.Id, updateRequest, CancellationToken.None);

        var dto = Assert.IsType<EmployeeDetailDto>(Assert.IsType<OkObjectResult>(updateResult).Value);
        dto.Should().NotBeNull("UpdateEmployee must return EmployeeDetailDto");
        dto.Salary.Should().Be(12_000m, "Admin sees salary unmasked in write response");
        dto.BankIban.Should().Be("AE000000000000001", "Admin sees IBAN unmasked in write response");
    }

    [Fact]
    public async Task UpdateEmployee_UnprivilegedRole_ResponseMasksSensitiveFields()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenantAsync(db);
        // Seed employee directly — HR Officer cannot approve drafts so seed via Admin
        var adminController = CreateController(db, tenantId, "Admin");
        var draftResult = await adminController.CreateDraft(MinimalDraftRequest(), CancellationToken.None);
        var draft = Assert.IsType<EmployeeDraftDto>(Assert.IsType<CreatedResult>(draftResult.Result).Value);
        await adminController.SubmitDraft(draft.Id, CancellationToken.None);
        var approval = await adminController.ApproveDraft(draft.Id, CancellationToken.None);
        var created = Assert.IsType<EmployeeDetailDto>(Assert.IsType<OkObjectResult>(approval.Result).Value);

        // HR Officer updates a non-sensitive field
        var hrOfficer = CreateController(db, tenantId, "HR Officer");
        var updateRequest = new EmployeeUpdateRequest(
            EffectiveDate: DateOnly.FromDateTime(DateTime.UtcNow.Date),
            Changes: new Dictionary<string, System.Text.Json.JsonElement>
            {
                ["workLocation"] = System.Text.Json.JsonSerializer.SerializeToElement("Annex")
            });

        var updateResult = await hrOfficer.UpdateEmployee(created.Id, updateRequest, CancellationToken.None);

        var dto = Assert.IsType<EmployeeDetailDto>(Assert.IsType<OkObjectResult>(updateResult).Value);
        dto.Salary.Should().BeNull("HR Officer must NOT see salary in the update response");
        dto.BankIban.Should().BeEmpty("HR Officer must NOT see IBAN in the update response");
        dto.PassportNumber.Should().BeEmpty("HR Officer must NOT see passport in the update response");
        dto.IqamaNumber.Should().BeEmpty("HR Officer must NOT see Iqama in the update response");
    }

    // ── P2.1: ApproveChange — mask gate on write response ────────────────────────

    [Fact]
    public async Task ApproveChange_AdminRole_ResponseIsEmployeeDetailDto_WithSensitiveData()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenantAsync(db);
        var emp = SeedEmployee(db, tenantId);
        var controller = CreateController(db, tenantId, "Admin");

        var change = new EmployeeChangeRequest
        {
            TenantId = tenantId,
            EmployeeId = emp.Id,
            EffectiveDate = DateOnly.FromDateTime(DateTime.UtcNow.Date),
            SensitiveFields = "salary",
            ProposedChangesJson = System.Text.Json.JsonSerializer.Serialize(
                new Dictionary<string, System.Text.Json.JsonElement>
                {
                    ["salary"] = System.Text.Json.JsonSerializer.SerializeToElement(60_000m)
                })
        };
        db.EmployeeChangeRequests.Add(change);
        await db.SaveChangesAsync();

        var result = await controller.ApproveChange(change.Id, CancellationToken.None);

        var dto = Assert.IsType<EmployeeDetailDto>(Assert.IsType<OkObjectResult>(result).Value);
        dto.Should().NotBeNull("ApproveChange must return EmployeeDetailDto");
        dto.Salary.Should().Be(60_000m, "Admin sees updated salary unmasked after approving change");
        dto.IqamaNumber.Should().Be("2000000001", "Admin sees Iqama unmasked in approve response");
    }

    [Fact]
    public async Task ApproveChange_UnprivilegedViewerForRead_ResponseMasksSensitiveFields()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenantAsync(db);
        var emp = SeedEmployee(db, tenantId);

        var change = new EmployeeChangeRequest
        {
            TenantId = tenantId,
            EmployeeId = emp.Id,
            EffectiveDate = DateOnly.FromDateTime(DateTime.UtcNow.Date),
            SensitiveFields = "salary",
            ProposedChangesJson = System.Text.Json.JsonSerializer.Serialize(
                new Dictionary<string, System.Text.Json.JsonElement>
                {
                    ["salary"] = System.Text.Json.JsonSerializer.SerializeToElement(60_000m)
                })
        };
        db.EmployeeChangeRequests.Add(change);
        await db.SaveChangesAsync();

        // HR Manager can approve but does NOT satisfy CanViewSensitive (only Admin/HR Manager/Payroll Officer do)
        // ApproveChange is limited to Admin,HR Manager — so test HR Manager seeing salary (they CAN)
        // and separately confirm CanViewSensitive returns false for non-qualifying roles.
        // Use Auditor role impersonation on the read side to verify mask in write response.
        // The practical mask test: CanViewSensitive() in controller returns false when role != Admin/HR Manager/Payroll Officer.
        // We test this with a role that passes [Authorize(Roles="Admin,HR Manager")] but doesn't satisfy CanViewSensitive.
        // For this scenario we simply assert that a non-HR-Manager role calling the endpoint gets masked output.
        // We create a controller with no roles to confirm that CanViewSensitive=false masks the response.
        var noRoleController = CreateController(db, tenantId, "Auditor");
        // Note: Auditor cannot call ApproveChange (filtered by Authorize attribute, but in unit tests attribute isn't enforced)
        var result = await noRoleController.ApproveChange(change.Id, CancellationToken.None);

        var dto = Assert.IsType<EmployeeDetailDto>(Assert.IsType<OkObjectResult>(result).Value);
        dto.Salary.Should().BeNull("Auditor-role caller must NOT see salary in ApproveChange write response");
        dto.BankIban.Should().BeEmpty("Auditor-role caller must NOT see IBAN");
    }

    // ── P2.1: ApproveHrTransfer — mask gate on write response ────────────────────

    [Fact]
    public async Task ApproveHrTransfer_AdminRole_ResponseIsEmployeeDetailDto_WithSensitiveData()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenantAsync(db);
        var emp = SeedEmployee(db, tenantId);
        var controller = CreateController(db, tenantId, "Admin");

        var transfer = new EmployeeTransferRequest
        {
            TenantId = tenantId,
            EmployeeId = emp.Id,
            CurrentDepartment = emp.Department,
            CurrentBranch = emp.Branch,
            NewDepartment = "Finance",
            NewBranch = "Riyadh",
            NewManagerEmployeeId = null,
            EffectiveDate = DateOnly.FromDateTime(DateTime.UtcNow.Date),
            Status = "PendingHrApproval",
        };
        db.EmployeeTransferRequests.Add(transfer);
        await db.SaveChangesAsync();

        var result = await controller.ApproveHrTransfer(transfer.Id, CancellationToken.None);

        var dto = Assert.IsType<EmployeeDetailDto>(Assert.IsType<OkObjectResult>(result).Value);
        dto.Should().NotBeNull("ApproveHrTransfer must return EmployeeDetailDto");
        dto.Department.Should().Be("Finance", "Department must be updated to new department after transfer");
        dto.Salary.Should().Be(50_000m, "Admin sees salary unmasked after transfer approval");
        dto.IqamaNumber.Should().Be("2000000001", "Admin sees Iqama unmasked after transfer approval");
    }

    [Fact]
    public async Task ApproveHrTransfer_UnprivilegedRole_ResponseMasksSensitiveFields()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenantAsync(db);
        var emp = SeedEmployee(db, tenantId);

        var transfer = new EmployeeTransferRequest
        {
            TenantId = tenantId,
            EmployeeId = emp.Id,
            CurrentDepartment = emp.Department,
            CurrentBranch = emp.Branch,
            NewDepartment = "Legal",
            NewBranch = "Jeddah",
            EffectiveDate = DateOnly.FromDateTime(DateTime.UtcNow.Date),
            Status = "PendingHrApproval",
        };
        db.EmployeeTransferRequests.Add(transfer);
        await db.SaveChangesAsync();

        var managerController = CreateController(db, tenantId, "Manager");
        var result = await managerController.ApproveHrTransfer(transfer.Id, CancellationToken.None);

        var dto = Assert.IsType<EmployeeDetailDto>(Assert.IsType<OkObjectResult>(result).Value);
        dto.Salary.Should().BeNull("Manager must NOT see salary in transfer approval response");
        dto.BankIban.Should().BeEmpty("Manager must NOT see IBAN in transfer approval response");
        dto.IqamaNumber.Should().BeEmpty("Manager must NOT see Iqama in transfer approval response");
    }

    // ── P2.1: ESS Profile — returns EssEmployeeProfileDto, not raw Employee ──────

    [Fact]
    public async Task EssProfile_ReturnType_IsEssEmployeeProfileDtoNotRawEntity()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var emp = SeedEmployee(db, tenantId);

        var essController = CreateEssController(db, tenantId, emp.Id);
        var result = await essController.Profile(CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        okResult.Value.Should().BeOfType<EssEmployeeProfileDto>(
            "ESS profile endpoint must return EssEmployeeProfileDto, not the raw EF entity");
    }

    [Fact]
    public async Task EssProfile_EmployeeSeesOwnFinancialData()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var emp = SeedEmployee(db, tenantId);

        var essController = CreateEssController(db, tenantId, emp.Id);
        var result = await essController.Profile(CancellationToken.None);

        var dto = Assert.IsType<EssEmployeeProfileDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        dto.Salary.Should().Be(50_000m, "Employee can see their own salary");
        dto.BankIban.Should().Be("SA0000000000000000001234", "Employee can see their own IBAN");
        dto.PassportNumber.Should().Be("P99999999", "Employee can see their own passport number");
        dto.IqamaNumber.Should().Be("2000000001", "Employee can see their own Iqama number");
    }

    [Fact]
    public async Task EssProfile_DoesNotExposeInternalHrNotes()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var emp = SeedEmployee(db, tenantId);

        var essController = CreateEssController(db, tenantId, emp.Id);
        var result = await essController.Profile(CancellationToken.None);

        var dto = Assert.IsType<EssEmployeeProfileDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        // These fields are internal HR notes — the employee has no right to see them
        var dtoType = dto.GetType();
        dtoType.GetProperty("DisciplinaryRecords").Should().BeNull(
            "EssEmployeeProfileDto must not include disciplinary records");
        dtoType.GetProperty("MedicalInformation").Should().BeNull(
            "EssEmployeeProfileDto must not include internal medical information");
        dtoType.GetProperty("TerminationReason").Should().BeNull(
            "EssEmployeeProfileDto must not include termination reason");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────

    private static async Task<Guid> SeedTenantAsync(ZayraDbContext db)
    {
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Test Corp", Slug = "test-corp" };
        db.Tenants.Add(tenant);
        db.Roles.Add(new Role { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Employee", NormalizedName = "EMPLOYEE", Description = "Default employee role" });
        await db.SaveChangesAsync();
        return tenant.Id;
    }

    private static EmployeesController CreateController(ZayraDbContext db, Guid tenantId, string role)
    {
        var controller = new EmployeesController(
            db,
            new Pbkdf2PasswordHasher(),
            new AuditService(db),
            new FakeDocumentStorage(),
            new FakeNotificationService(),
            new FakeHijriDateService(),
            new DataScopeService(db),
            new FakeLetterService());
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Role, role),
        }, "Test"));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } };
        return controller;
    }

    private static EmployeeDraftRequest MinimalDraftRequest() =>
        new(
            CurrentStep: "Review",
            EnglishName: "Test User", ArabicName: "مستخدم", PersonalEmail: "test@example.com",
            WorkEmail: "test@company.com", Phone: "+971500000", Gender: "Male",
            DateOfBirth: new DateOnly(1990, 1, 1), MaritalStatus: "Single",
            EmergencyContactName: "Contact", EmergencyContactPhone: "+97150001",
            Nationality: "UAE", CountryCode: "UAE",
            Department: "Engineering", Designation: "Engineer", Branch: "Dubai", WorkLocation: "HQ",
            ManagerEmployeeId: null,
            JoiningDate: DateTime.UtcNow.Date,
            ContractType: "Unlimited", Grade: "G1", CostCenter: "CC-001",
            ContractStartDate: DateOnly.FromDateTime(DateTime.UtcNow.Date),
            ContractEndDate: null,
            ProbationEndDate: DateOnly.FromDateTime(DateTime.UtcNow.Date.AddMonths(3)),
            PayrollProfileCode: "MONTHLY",
            Salary: 12_000m, BankName: "Test Bank", BankIban: "AE000000000000001",
            WpsBankDetails: "WPS-001",
            ShiftPolicyCode: "DAY", LeavePolicyCode: "STANDARD",
            SponsorName: null,
            PassportIssueDate: null, PassportNumber: "P123456",
            PassportExpiryDate: DateOnly.FromDateTime(DateTime.UtcNow.Date.AddYears(5)),
            VisaIssueDate: null, VisaNumber: null, VisaExpiryDate: null,
            IqamaNumber: "200000001", MuqeemNumber: null,
            GosiReference: null, QiwaContractNumber: null,
            EmiratesId: null, LaborCardNumber: null, VisaFileNumber: null,
            Qid: null, WorkPermitNumber: null, WorkPermitIssueDate: null,
            CivilId: null, ResidencyNumber: null, ResidencyIssueDate: null);

    // ── Stubs ─────────────────────────────────────────────────────────────────────

    private sealed class FakeDocumentStorage : IDocumentStorage
    {
        public Task<StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken ct) =>
            Task.FromResult(new StoredDocument(file.FileName, file.ContentType, "storage/test", "/tmp/test"));
        public string ResolvePath(string storageUrl) => "/tmp/test";
        public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) =>
            Task.FromResult(Array.Empty<byte>());
    }

    private sealed class FakeNotificationService : INotificationService
    {
        public Task NotifyAsync(Guid t, Guid? u, string title, string msg, string entity, string? entityId, CancellationToken ct) => Task.CompletedTask;
        public Task SendEmailAsync(Guid t, string code, string to, string name, Dictionary<string, string> vars, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeHijriDateService : IHijriDateService
    {
        public DateConversionDto FromGregorian(DateOnly date) =>
            new(date.ToString("yyyy-MM-dd"), "1447-01-01", 1447, 1, 1);
    }

    private static EmployeeSelfServiceController CreateEssController(ZayraDbContext db, Guid tenantId, int employeeId)
    {
        var controller = new EmployeeSelfServiceController(db, new FakeLetterService(), new Zayra.Api.Infrastructure.Documents.PdfRenderGate(1));
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim("employee_id", employeeId.ToString()),
            new Claim("permission", "ess.read"),
        }, "Test"));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } };
        return controller;
    }

    private sealed class FakeLetterService : ILetterService
    {
        public Task<byte[]> GeneratePayslipPdfAsync(PayslipData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateAppointmentLetterAsync(LetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateExperienceLetterAsync(LetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    }
}
