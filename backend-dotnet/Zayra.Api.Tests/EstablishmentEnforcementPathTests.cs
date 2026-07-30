using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Employees;
using Zayra.Api.Application.Organization;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Localization;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Enforcement-path tests: "one guard, many callers — all or none" (spec §5, consultant B1/B2).
/// Covers CSV import (row-level errors + intra-batch counting, AC9), onboarding draft approval
/// (the front-door hire path, consultant B1/R-A), HR transfer approval (AC8), sensitive-change
/// approval at apply (stale approvals, AC1), reactivation/rehire, Suspended reinstatement
/// (never blocked — compliance #3), the offboarding side-door precondition (security R5), and
/// grandfathering on unrelated edits (AC1/AC5).
/// </summary>
public class EstablishmentEnforcementPathTests
{
    private static ZayraDbContext CreateDb() => new(new DbContextOptionsBuilder<ZayraDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static RequestContext Ctx(Guid tenantId) => new("127.0.0.1", "xunit", Guid.NewGuid(), tenantId);

    private sealed record Fixture(Guid TenantId, Department Ops, StaffingLevel Manager, Designation OpsManager, Department Hr, Designation HrOfficer);

    /// <summary>Tenant + "Operations" dept with a Manager-level designation, plus an
    /// uncontrolled "HR" dept/designation, plus the Employee role the account paths need.</summary>
    private static async Task<Fixture> SeedOrg(ZayraDbContext db, int? managerBudget)
    {
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "T", Slug = $"t-{Guid.NewGuid():N}" };
        db.Tenants.Add(tenant);
        db.Roles.Add(new Role { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Employee", NormalizedName = "EMPLOYEE", Description = "Employee" });
        var ops = new Department { TenantId = tenant.Id, Code = "OPS", NameEn = "Operations", IsActive = true };
        var hr = new Department { TenantId = tenant.Id, Code = "HR", NameEn = "Human Resources", IsActive = true };
        var level = new StaffingLevel { TenantId = tenant.Id, Code = "MGR", NameEn = "Manager", NameAr = "مدير", Rank = 2 };
        var opsManager = new Designation { TenantId = tenant.Id, Code = "OPS-MGR", TitleEn = "Operations Manager", IsActive = true, StaffingLevelId = level.Id };
        var hrOfficer = new Designation { TenantId = tenant.Id, Code = "HR-OFF", TitleEn = "HR Officer", IsActive = true };
        db.AddRange(ops, hr, level, opsManager, hrOfficer);
        if (managerBudget is not null)
            db.DepartmentStaffingBudgets.Add(new DepartmentStaffingBudget
            {
                TenantId = tenant.Id, DepartmentId = ops.Id, StaffingLevelId = level.Id, BudgetedHeadcount = managerBudget.Value
            });
        await db.SaveChangesAsync();
        return new Fixture(tenant.Id, ops, level, opsManager, hr, hrOfficer);
    }

    private static Employee AddEmployee(ZayraDbContext db, Fixture fx, Department dept, Designation desig, string status = "Active")
    {
        var emp = new Employee
        {
            TenantId = fx.TenantId,
            EmployeeCode = $"E-{Guid.NewGuid():N}"[..12],
            FullName = "Path Employee",
            Status = status,
            JoiningDate = DateTime.UtcNow,
            DepartmentId = dept.Id,
            Department = dept.NameEn,
            DesignationId = desig.Id,
            Designation = desig.TitleEn
        };
        db.Employees.Add(emp);
        db.SaveChanges();
        return emp;
    }

    private static EmployeesController CreateController(ZayraDbContext db, Guid tenantId)
    {
        var audit = new AuditService(db);
        var controller = new EmployeesController(
            db,
            new Pbkdf2PasswordHasher(),
            audit,
            new FakeDocs(),
            new FakeNotifications(),
            new FakeHijri(),
            new Zayra.Api.Infrastructure.Common.DataScopeService(db),
            new FakeLetters(),
            new ApprovalWorkflowService(db, audit),
            NullLogger<EmployeesController>.Instance,
            new EstablishmentGuardService(db));
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Role, "Admin"),
            new Claim("permission", "organization.establishment.write"),
            new Claim("is_group_scope", "true")
        }, "Test"));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } };
        return controller;
    }

    private static EmployeeManagementService CreateService(ZayraDbContext db) =>
        new(db, new AuditService(db), new FakeDocs(), new FakeNotifications(), new EstablishmentGuardService(db));

    // ── (4) CSV import: row-level errors + cumulative intra-batch counting (AC9) ──

    [Fact]
    public async Task Import_ThreeManagersAgainstTwoSlots_ImportsTwo_FailsRowFour_WithCountsAndAudit()
    {
        await using var db = CreateDb();
        var fx = await SeedOrg(db, managerBudget: 2);
        var controller = CreateController(db, fx.TenantId);

        // Status=Active is explicit: these rows are meant to OCCUPY manager seats so the establishment
        // budget check fires (blank status now lands Draft, which never consumes a seat — §7).
        const string csv =
            "EmployeeCode,FullName,Department,Designation,Status,JoiningDate\n" +
            "M1,Mgr One,Operations,Operations Manager,Active,2026-01-01\n" +
            "M2,Mgr Two,Operations,Operations Manager,Active,2026-01-01\n" +
            "M3,Mgr Three,Operations,Operations Manager,Active,2026-01-01\n";

        var result = await controller.Import(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None);
        var payload = JsonSerializer.Serialize(((OkObjectResult)result).Value);

        payload.Should().Contain("\"created\":2").And.Contain("\"skipped\":1");
        payload.Should().Contain("Row 4").And.Contain("has 2 of 2 budgeted Manager(s)")
            .And.Contain("no Manager slot is available for assignment", "row error names level, budgeted and current (AC9)");
        (await db.Employees.CountAsync(e => e.TenantId == fx.TenantId)).Should().Be(2, "file order wins — first two fit");

        var blockAudit = await db.AuditLogs.SingleAsync(a => a.TenantId == fx.TenantId && a.Action == "establishment.assignment_blocked");
        blockAudit.Metadata.Should().Contain("\"path\":\"import\"").And.Contain("\"rowNumber\":4")
            .And.Contain("\"levelNameEn\":\"Manager\"", "bulk rejections are the highest-volume demand signal — they must audit too");
    }

    [Fact]
    public async Task Import_IsDeterministicOnRerun_AndNonOccupyingRowsNeverConsumeSlots()
    {
        await using var db = CreateDb();
        var fx = await SeedOrg(db, managerBudget: 1);
        var controller = CreateController(db, fx.TenantId);

        const string csv =
            "EmployeeCode,FullName,Department,Designation,Status,JoiningDate\n" +
            "T1,Term Mgr,Operations,Operations Manager,Terminated,2026-01-01\n" + // non-occupying — no slot
            "A1,Active Mgr,Operations,Operations Manager,Active,2026-01-01\n" +
            "A2,Second Mgr,Operations,Operations Manager,Active,2026-01-01\n";

        var first = JsonSerializer.Serialize(((OkObjectResult)await controller.Import(
            new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None)).Value);
        first.Should().Contain("\"created\":2", "Terminated row consumes no seat; A1 takes the single slot; A2 fails");
        first.Should().Contain("Row 4").And.Contain("has 1 of 1 budgeted Manager(s)");

        // Re-run: A1's seat is now genuinely occupied; the same rows fail for the same reason
        // (duplicate codes skip first, then the budget row error is unchanged) — deterministic.
        var second = JsonSerializer.Serialize(((OkObjectResult)await controller.Import(
            new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None)).Value);
        second.Should().Contain("\"created\":0").And.Contain("already exists");
    }

    [Fact]
    public async Task Import_AdvisoryMode_WarnsInsteadOfSkipping()
    {
        await using var db = CreateDb();
        var fx = await SeedOrg(db, managerBudget: 1);
        db.TenantHrConfigs.Add(new TenantHrConfig { TenantId = fx.TenantId, EstablishmentEnforcementMode = "Advisory" });
        await db.SaveChangesAsync();
        var controller = CreateController(db, fx.TenantId);

        const string csv =
            "EmployeeCode,FullName,Department,Designation,Status,JoiningDate\n" +
            "M1,Mgr One,Operations,Operations Manager,Active,2026-01-01\n" +
            "M2,Mgr Two,Operations,Operations Manager,Active,2026-01-01\n";

        var payload = JsonSerializer.Serialize(((OkObjectResult)await controller.Import(
            new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None)).Value);
        payload.Should().Contain("\"created\":2", "Advisory mode imports everything");
        payload.Should().Contain("has 1 of 1 budgeted Manager(s)", "…but surfaces the over-budget rows as warnings");
        (await db.Employees.CountAsync(e => e.TenantId == fx.TenantId)).Should().Be(2);
    }

    // ── (9) Onboarding draft approval (consultant B1 / R-A) ──────────────────

    private static EmployeeDraft AddDraft(ZayraDbContext db, Fixture fx, string department, string designation)
    {
        var draft = new EmployeeDraft
        {
            TenantId = fx.TenantId,
            Status = "PendingHrApproval",
            CurrentStep = "HrApproval",
            EnglishName = "Draft Hire",
            WorkEmail = $"draft-{Guid.NewGuid():N}@test.local",
            Department = department,
            Designation = designation,
            JoiningDate = DateTime.UtcNow.Date
        };
        db.EmployeeDrafts.Add(draft);
        db.SaveChanges();
        return draft;
    }

    [Fact]
    public async Task DraftApprove_ResolvesOrgIds_AndConsumesSeat()
    {
        await using var db = CreateDb();
        var fx = await SeedOrg(db, managerBudget: 1);
        var draft = AddDraft(db, fx, "Operations", "Operations Manager");

        var result = await CreateController(db, fx.TenantId).ApproveDraft(draft.Id, CancellationToken.None);
        result.Result.Should().BeOfType<OkObjectResult>();

        var employee = await db.Employees.SingleAsync(e => e.TenantId == fx.TenantId);
        employee.DepartmentId.Should().Be(fx.Ops.Id, "the onboarding hire path must never create string-only employees (B1)");
        employee.DesignationId.Should().Be(fx.OpsManager.Id);
        employee.Status.Should().Be("Active");
    }

    [Fact]
    public async Task DraftApprove_IntoFullLevel_Returns409_DraftStaysPending_NoEmployeeCreated()
    {
        await using var db = CreateDb();
        var fx = await SeedOrg(db, managerBudget: 1);
        AddEmployee(db, fx, fx.Ops, fx.OpsManager); // seat taken
        var draft = AddDraft(db, fx, "Operations", "Operations Manager");
        var controller = CreateController(db, fx.TenantId);

        var result = await controller.ApproveDraft(draft.Id, CancellationToken.None);
        var conflict = result.Result.Should().BeOfType<ConflictObjectResult>().Subject;
        var payload = JsonSerializer.Serialize(conflict.Value);
        payload.Should().Contain("ESTABLISHMENT_BUDGET_EXCEEDED")
            .And.Contain("\"budgeted\":1").And.Contain("\"current\":1")
            .And.Contain("\"canEditEstablishment\":true");
        using var doc = JsonDocument.Parse(payload);
        doc.RootElement.GetProperty("levelNameAr").GetString().Should().Be("مدير", "AR ships in the 409 payload from day one (AC11)");

        (await db.Employees.CountAsync(e => e.TenantId == fx.TenantId)).Should().Be(1, "the blocked hire must not be persisted");
        (await db.EmployeeDrafts.AsNoTracking().SingleAsync(d => d.Id == draft.Id)).Status
            .Should().Be("PendingHrApproval", "the draft is untouched and can be approved after a budget raise");
    }

    [Fact]
    public async Task DraftApprove_UnknownDepartmentName_Is422_NeverStringOnly()
    {
        await using var db = CreateDb();
        var fx = await SeedOrg(db, managerBudget: null);
        var draft = AddDraft(db, fx, "No Such Department", "Operations Manager");

        var result = await CreateController(db, fx.TenantId).ApproveDraft(draft.Id, CancellationToken.None);
        var unprocessable = result.Result.Should().BeOfType<UnprocessableEntityObjectResult>().Subject;
        JsonSerializer.Serialize(unprocessable.Value).Should().Contain("No Such Department");
        (await db.Employees.CountAsync(e => e.TenantId == fx.TenantId)).Should().Be(0);
    }

    // ── (5) Transfers: request-time resolution + guarded HR apply (AC8) ──────

    [Fact]
    public async Task RequestTransfer_ResolvesAndStoresIds_UnknownDepartmentThrows422Shape()
    {
        await using var db = CreateDb();
        var fx = await SeedOrg(db, managerBudget: null);
        var emp = AddEmployee(db, fx, fx.Hr, fx.HrOfficer);
        var service = CreateService(db);

        var transfer = await service.RequestTransferAsync(fx.TenantId, emp.Id,
            new EmployeeTransferCreateRequest(null, "Operations", "Operations Manager", null,
                DateOnly.FromDateTime(DateTime.UtcNow.Date), "Growth"), Ctx(fx.TenantId), CancellationToken.None);

        transfer!.NewDepartmentId.Should().Be(fx.Ops.Id, "IDs are resolved at REQUEST time");
        transfer.NewDesignationId.Should().Be(fx.OpsManager.Id);
        transfer.CurrentDepartmentId.Should().Be(fx.Hr.Id);
        transfer.NewDepartment.Should().Be("Operations", "legacy display strings are kept alongside");

        var unknown = () => service.RequestTransferAsync(fx.TenantId, emp.Id,
            new EmployeeTransferCreateRequest(null, "Atlantis Division", null, null,
                DateOnly.FromDateTime(DateTime.UtcNow.Date), "typo"), Ctx(fx.TenantId), CancellationToken.None);
        await unknown.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Atlantis Division*", "a transfer can no longer manufacture a string-only department");
    }

    [Fact]
    public async Task HrTransferApprove_IntoFullLevel_409_StaysPending_ThenAppliesAfterBudgetRaise()
    {
        await using var db = CreateDb();
        var fx = await SeedOrg(db, managerBudget: 1);
        AddEmployee(db, fx, fx.Ops, fx.OpsManager); // incumbent fills the only Manager seat
        var emp = AddEmployee(db, fx, fx.Hr, fx.HrOfficer);
        var service = CreateService(db);
        var transfer = await service.RequestTransferAsync(fx.TenantId, emp.Id,
            new EmployeeTransferCreateRequest(null, "Operations", "Operations Manager", null,
                DateOnly.FromDateTime(DateTime.UtcNow.Date), "Promotion"), Ctx(fx.TenantId), CancellationToken.None);
        var tracked = await db.EmployeeTransferRequests.SingleAsync(t => t.Id == transfer!.Id);
        tracked.Status = "PendingHrApproval";
        await db.SaveChangesAsync();

        var controller = CreateController(db, fx.TenantId);
        var hierarchy = new Zayra.Api.Infrastructure.Organization.HrmHierarchyService(db, new AuditService(db));

        var blocked = await controller.ApproveHrTransfer(transfer!.Id, hierarchy, CancellationToken.None);
        JsonSerializer.Serialize(((ConflictObjectResult)blocked).Value).Should().Contain("ESTABLISHMENT_BUDGET_EXCEEDED");
        (await db.EmployeeTransferRequests.AsNoTracking().SingleAsync(t => t.Id == transfer.Id)).Status
            .Should().Be("PendingHrApproval", "a blocked transfer stays pending for re-approval after a raise");
        (await db.Employees.AsNoTracking().SingleAsync(e => e.Id == emp.Id)).DepartmentId
            .Should().Be(fx.Hr.Id, "nothing may be half-applied on the block path");

        // Raise the wall (the audited, permissioned budget edit IS the override) and re-approve.
        var budget = await db.DepartmentStaffingBudgets.SingleAsync(b => b.TenantId == fx.TenantId);
        budget.BudgetedHeadcount = 2;
        await db.SaveChangesAsync();

        var applied = await controller.ApproveHrTransfer(transfer.Id, hierarchy, CancellationToken.None);
        applied.Should().BeOfType<OkObjectResult>();
        var after = await db.Employees.AsNoTracking().SingleAsync(e => e.Id == emp.Id);
        after.DepartmentId.Should().Be(fx.Ops.Id, "AC8: transfer approval sets the resolved DepartmentId");
        after.DesignationId.Should().Be(fx.OpsManager.Id);
    }

    [Fact]
    public async Task HrTransferApprove_LegacyStringOnlyRow_ReResolvesAtApply()
    {
        await using var db = CreateDb();
        var fx = await SeedOrg(db, managerBudget: null);
        var emp = AddEmployee(db, fx, fx.Hr, fx.HrOfficer);
        // Pre-fix row: strings only, no resolved IDs (as produced before the Batch A migration).
        var legacy = new EmployeeTransferRequest
        {
            TenantId = fx.TenantId,
            EmployeeId = emp.Id,
            NewDepartment = "Operations",
            NewDesignation = "Operations Manager",
            Status = "PendingHrApproval",
            EffectiveDate = DateOnly.FromDateTime(DateTime.UtcNow.Date)
        };
        db.EmployeeTransferRequests.Add(legacy);
        await db.SaveChangesAsync();

        var result = await CreateController(db, fx.TenantId).ApproveHrTransfer(legacy.Id,
            new Zayra.Api.Infrastructure.Organization.HrmHierarchyService(db, new AuditService(db)), CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>();
        (await db.Employees.AsNoTracking().SingleAsync(e => e.Id == emp.Id)).DepartmentId
            .Should().Be(fx.Ops.Id, "legacy pre-migration rows re-resolve from their strings at approval");
    }

    // ── (6) Sensitive-change approval: authoritative re-check at apply (AC1) ──

    [Fact]
    public async Task ChangeApprove_SlotConsumedAfterSubmission_409_StaysPending_ReapproveSucceedsAfterRaise()
    {
        await using var db = CreateDb();
        var fx = await SeedOrg(db, managerBudget: 1);
        AddEmployee(db, fx, fx.Ops, fx.OpsManager); // seat consumed after the change was requested
        var emp = AddEmployee(db, fx, fx.Hr, fx.HrOfficer);
        var change = new EmployeeChangeRequest
        {
            TenantId = fx.TenantId,
            EmployeeId = emp.Id,
            RequestedByUserId = Guid.NewGuid(), // different from approver (maker-checker)
            EffectiveDate = DateOnly.FromDateTime(DateTime.UtcNow.Date),
            SensitiveFields = "salary",
            ProposedChangesJson = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["department"] = "Operations",
                ["designation"] = "Operations Manager"
            })
        };
        db.EmployeeChangeRequests.Add(change);
        await db.SaveChangesAsync();
        var controller = CreateController(db, fx.TenantId);

        var blocked = await controller.ApproveChange(change.Id, CancellationToken.None);
        JsonSerializer.Serialize(((ConflictObjectResult)blocked).Value).Should().Contain("ESTABLISHMENT_BUDGET_EXCEEDED");
        (await db.EmployeeChangeRequests.AsNoTracking().SingleAsync(c => c.Id == change.Id)).Status
            .Should().Be("PendingApproval", "a stale approval fails gracefully and stays re-approvable");
        (await db.Employees.AsNoTracking().SingleAsync(e => e.Id == emp.Id)).DepartmentId.Should().Be(fx.Hr.Id);

        var budget = await db.DepartmentStaffingBudgets.SingleAsync(b => b.TenantId == fx.TenantId);
        budget.BudgetedHeadcount = 2;
        await db.SaveChangesAsync();

        (await controller.ApproveChange(change.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        (await db.Employees.AsNoTracking().SingleAsync(e => e.Id == emp.Id)).DepartmentId.Should().Be(fx.Ops.Id);
        (await db.EmployeeChangeRequests.AsNoTracking().SingleAsync(c => c.Id == change.Id)).Status.Should().Be("ApprovedApplied");
    }

    // ── (3) Status transitions: rehire consumes, reinstatement never blocked ──

    [Fact]
    public async Task Reactivation_IntoFullLevel_Blocked_IntoUncontrolledLevel_Passes()
    {
        await using var db = CreateDb();
        var fx = await SeedOrg(db, managerBudget: 1);
        AddEmployee(db, fx, fx.Ops, fx.OpsManager); // fills the seat
        var returning = AddEmployee(db, fx, fx.Ops, fx.OpsManager, status: "Terminated");
        var service = CreateService(db);

        var rehire = () => service.ChangeStatusAsync(fx.TenantId, returning.Id,
            new EmployeeStatusChangeRequest("Active", DateOnly.FromDateTime(DateTime.UtcNow.Date), "Rehire"),
            Ctx(fx.TenantId), CancellationToken.None);
        await rehire.Should().ThrowAsync<EstablishmentBudgetExceededException>("a returning Manager consumes a seat (§5.6)");
        db.ChangeTracker.Clear();
        (await db.Employees.AsNoTracking().SingleAsync(e => e.Id == returning.Id)).Status.Should().Be("Terminated");

        var uncontrolled = AddEmployee(db, fx, fx.Hr, fx.HrOfficer, status: "Terminated");
        var dto = await service.ChangeStatusAsync(fx.TenantId, uncontrolled.Id,
            new EmployeeStatusChangeRequest("Active", DateOnly.FromDateTime(DateTime.UtcNow.Date), "Rehire"),
            Ctx(fx.TenantId), CancellationToken.None);
        dto.Should().NotBeNull("levels without a budget row never block (AC3)");
    }

    [Fact]
    public async Task SuspendedReinstatement_IsOccupyingToOccupying_NeverBlocked_EvenOverBudget()
    {
        await using var db = CreateDb();
        var fx = await SeedOrg(db, managerBudget: 1);
        AddEmployee(db, fx, fx.Ops, fx.OpsManager);                       // 1 Active
        var suspended = AddEmployee(db, fx, fx.Ops, fx.OpsManager, "Suspended"); // 2 of 1 — grandfathered
        var service = CreateService(db);

        var dto = await service.ChangeStatusAsync(fx.TenantId, suspended.Id,
            new EmployeeStatusChangeRequest("Active", DateOnly.FromDateTime(DateTime.UtcNow.Date), "Investigation closed"),
            Ctx(fx.TenantId), CancellationToken.None);
        dto.Should().NotBeNull("KSA labor law: the system must never refuse to record a mandated reinstatement — Suspended already occupies the seat");
    }

    // ── Offboarding side door (security R5) ──────────────────────────────────

    [Fact]
    public async Task OffboardingInitiate_NonOccupyingEmployee_IsRejected()
    {
        await using var db = CreateDb();
        var fx = await SeedOrg(db, managerBudget: null);
        var terminated = AddEmployee(db, fx, fx.Ops, fx.OpsManager, status: "Terminated");
        var controller = new OffboardingController(db, new AuditService(db))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = EstablishmentApiTests.User(fx.TenantId, "organization.write") }
            }
        };

        var result = await controller.Initiate(new InitiateOffboardingRequest(
            terminated.Id, "Resignation", null, null, 30, null), CancellationToken.None);
        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        JsonSerializer.Serialize(bad.Value).Should().Contain("cannot be offboarded",
            "a non-occupying employee must not transition INTO the occupying Offboarded status through this side door unguarded");
    }

    // ── Grandfathering: unrelated edits never trip the guard (AC1/AC5) ───────

    [Fact]
    public async Task UpdateEmployee_PhoneEditInOverBudgetCell_Succeeds_DepartmentChangeIntoFullCell_409()
    {
        await using var db = CreateDb();
        var fx = await SeedOrg(db, managerBudget: 1);
        AddEmployee(db, fx, fx.Ops, fx.OpsManager);
        var grandfathered = AddEmployee(db, fx, fx.Ops, fx.OpsManager); // 2 of 1 — over budget, grandfathered
        var outsider = AddEmployee(db, fx, fx.Hr, fx.HrOfficer);
        var controller = CreateController(db, fx.TenantId);
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        static Dictionary<string, JsonElement> Changes(object o) =>
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(o))!;

        // Editing an existing over-budget employee's phone number must succeed (AC1).
        var phoneEdit = await controller.UpdateEmployee(grandfathered.Id,
            new EmployeeUpdateRequest(today, Changes(new { phone = "+966500000000" })), CancellationToken.None);
        phoneEdit.Should().BeOfType<OkObjectResult>("edits not touching department/designation never trip the guard");

        // Moving a NEW person into the full cell is the net addition that blocks.
        var move = await controller.UpdateEmployee(outsider.Id,
            new EmployeeUpdateRequest(today, Changes(new { department = "Operations", designation = "Operations Manager" })), CancellationToken.None);
        var payload = JsonSerializer.Serialize(((ConflictObjectResult)move).Value);
        payload.Should().Contain("ESTABLISHMENT_BUDGET_EXCEEDED").And.Contain("\"budgeted\":1").And.Contain("\"current\":2");
        (await db.Employees.AsNoTracking().SingleAsync(e => e.Id == outsider.Id)).DepartmentId.Should().Be(fx.Hr.Id);
    }

    // ── Stubs ────────────────────────────────────────────────────────────────

    private sealed class FakeDocs : IDocumentStorage
    {
        public Task<StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken ct) =>
            Task.FromResult(new StoredDocument(file.FileName, file.ContentType ?? "application/octet-stream", "storage/test", "/tmp/test"));
        public string ResolvePath(string storageUrl) => "/tmp/test";
        public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) =>
            Task.FromResult(Array.Empty<byte>());
    }

    private sealed class FakeNotifications : INotificationService
    {
        public Task NotifyAsync(Guid t, Guid? u, string title, string msg, string entity, string? entityId, CancellationToken ct) => Task.CompletedTask;
        public Task SendEmailAsync(Guid t, string code, string to, string name, Dictionary<string, string> vars, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeHijri : IHijriDateService
    {
        public DateConversionDto FromGregorian(DateOnly date) => new(date.ToString("yyyy-MM-dd"), "1447-01-01", 1447, 1, 1);
    }

    private sealed class FakeLetters : ILetterService
    {
        public Task<byte[]> GeneratePayslipPdfAsync(PayslipData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateAppointmentLetterAsync(LetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateExperienceLetterAsync(LetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    }
}
