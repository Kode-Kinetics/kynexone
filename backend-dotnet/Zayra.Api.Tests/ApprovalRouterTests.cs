using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Leave;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// F1 — the ONE approval router: workflow selection by org specificity, deterministic tie-breaks,
/// the explicit no-configuration error, and approver resolution per approver type. Replaces the
/// ApprovalPolicyService tests (that service and its model are retired).
/// </summary>
public class ApprovalRouterTests
{
    private const string Leave = nameof(LeaveRequest);

    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static ApprovalRouter Router(ZayraDbContext db) => new(db);

    private static async Task<Employee> AddEmp(ZayraDbContext db, Guid tenantId, string code,
        int? managerId = null, int? supervisorId = null, Guid? deptId = null, string? designation = null,
        Guid? gradeId = null, Guid? userId = null)
    {
        var emp = new Employee
        {
            TenantId = tenantId, EmployeeCode = code, FullName = $"Emp {code}",
            Status = "Active", JoiningDate = DateTime.UtcNow.AddDays(-30),
            ManagerEmployeeId = managerId, SupervisorEmployeeId = supervisorId,
            DepartmentId = deptId, GradeId = gradeId, Designation = designation ?? string.Empty,
            UserAccountId = userId,
        };
        db.Employees.Add(emp);
        await db.SaveChangesAsync();
        return emp;
    }

    private static async Task<Department> AddDept(ZayraDbContext db, Guid tenantId, string name, int? headId = null)
    {
        var dept = new Department { TenantId = tenantId, Code = name.ToUpperInvariant()[..3], NameEn = name, ManagerEmployeeId = headId, IsActive = true };
        db.Departments.Add(dept);
        await db.SaveChangesAsync();
        return dept;
    }

    private static async Task<ApprovalWorkflow> AddWorkflow(
        ZayraDbContext db, Guid tenantId, string code, string approverType = "Manager",
        string entity = Leave, Guid? deptId = null, Guid? gradeId = null, bool isDefault = false,
        bool isActive = true, int? specificEmployeeId = null, DateTime? createdAt = null, bool finalStep = true)
    {
        var wf = new ApprovalWorkflow
        {
            TenantId = tenantId, Code = code, Name = code, EntityName = entity,
            DepartmentId = deptId, GradeId = gradeId, IsDefault = isDefault, IsActive = isActive,
            CreatedAtUtc = createdAt ?? DateTime.UtcNow,
        };
        wf.Steps.Add(new ApprovalWorkflowStep
        {
            TenantId = tenantId, WorkflowId = wf.Id, StepOrder = 1, StepName = "Step 1",
            ApproverType = approverType, ApproverRole = approverType == "Role" ? "Payroll Manager" : string.Empty,
            SpecificEmployeeId = specificEmployeeId, IsFinalStep = finalStep,
        });
        db.ApprovalWorkflows.Add(wf);
        await db.SaveChangesAsync();
        return wf;
    }

    private static async Task<ResolvedApprover> FirstApprover(ZayraDbContext db, Guid tid, int employeeId, string entity = Leave)
    {
        var router = Router(db);
        var route = await router.ResolveAsync(tid, employeeId, entity, default);
        return await router.ResolveApproverAsync(tid, employeeId, route.FirstStep, default);
    }

    // ── Workflow selection: specificity ──────────────────────────────────────

    [Fact]
    public async Task Resolve_Specificity_DepartmentAndGrade_Then_Department_Then_Grade_Then_Default()
    {
        var db = CreateDb();
        var tid = Guid.NewGuid();
        var dept = await AddDept(db, tid, "Engineering");
        var otherDept = await AddDept(db, tid, "Finance");
        var grade = Guid.NewGuid();
        var otherGrade = Guid.NewGuid();

        var wDefault = await AddWorkflow(db, tid, "DEFAULT", isDefault: true);
        var wGrade = await AddWorkflow(db, tid, "GRADE", gradeId: grade);
        var wDept = await AddWorkflow(db, tid, "DEPT", deptId: dept.Id);
        var wBoth = await AddWorkflow(db, tid, "DEPT-GRADE", deptId: dept.Id, gradeId: grade);

        var both = await AddEmp(db, tid, "BOTH", deptId: dept.Id, gradeId: grade);
        var deptOnly = await AddEmp(db, tid, "DEPTONLY", deptId: dept.Id, gradeId: otherGrade);
        var gradeOnly = await AddEmp(db, tid, "GRADEONLY", deptId: otherDept.Id, gradeId: grade);
        var neither = await AddEmp(db, tid, "NEITHER", deptId: otherDept.Id, gradeId: otherGrade);
        var unassigned = await AddEmp(db, tid, "UNASSIGNED");

        var router = Router(db);
        (await router.ResolveAsync(tid, both.Id, Leave, default)).Should().Match<ApprovalRoute>(r => r.WorkflowId == wBoth.Id && r.MatchedOn == "DepartmentAndGrade");
        (await router.ResolveAsync(tid, deptOnly.Id, Leave, default)).Should().Match<ApprovalRoute>(r => r.WorkflowId == wDept.Id && r.MatchedOn == "Department");
        (await router.ResolveAsync(tid, gradeOnly.Id, Leave, default)).Should().Match<ApprovalRoute>(r => r.WorkflowId == wGrade.Id && r.MatchedOn == "Grade");
        (await router.ResolveAsync(tid, neither.Id, Leave, default)).Should().Match<ApprovalRoute>(r => r.WorkflowId == wDefault.Id && r.MatchedOn == "Default");
        (await router.ResolveAsync(tid, unassigned.Id, Leave, default)).WorkflowId.Should().Be(wDefault.Id,
            "an employee with no department/grade must not match a scoped workflow");
    }

    [Fact]
    public async Task Resolve_DepartmentAndGradeWorkflow_RequiresBothToMatch()
    {
        var db = CreateDb();
        var tid = Guid.NewGuid();
        var dept = await AddDept(db, tid, "Engineering");
        var grade = Guid.NewGuid();
        var wDefault = await AddWorkflow(db, tid, "DEFAULT", isDefault: true);
        await AddWorkflow(db, tid, "DEPT-GRADE", deptId: dept.Id, gradeId: grade);
        var deptNoGrade = await AddEmp(db, tid, "E1", deptId: dept.Id);

        (await Router(db).ResolveAsync(tid, deptNoGrade.Id, Leave, default)).WorkflowId.Should().Be(wDefault.Id);
    }

    [Fact]
    public async Task Resolve_NullEmployee_OnlyConsidersTenantWideWorkflows()
    {
        var db = CreateDb();
        var tid = Guid.NewGuid();
        var dept = await AddDept(db, tid, "Engineering");
        await AddWorkflow(db, tid, "DEPT", entity: "ManpowerRequisition", deptId: dept.Id);
        (await Router(db).TryResolveAsync(tid, null, "ManpowerRequisition", default)).Should().BeNull();
        var wide = await AddWorkflow(db, tid, "WIDE", entity: "ManpowerRequisition");
        (await Router(db).ResolveAsync(tid, null, "ManpowerRequisition", default)).WorkflowId.Should().Be(wide.Id);
    }

    // ── Workflow selection: determinism ──────────────────────────────────────

    [Fact]
    public async Task Resolve_TieBreak_IsDefaultThenOldestThenId_AndIsStable()
    {
        var db = CreateDb();
        var tid = Guid.NewGuid();
        var emp = await AddEmp(db, tid, "EMP");
        var t0 = DateTime.UtcNow.AddDays(-10);
        var older = await AddWorkflow(db, tid, "OLDER", createdAt: t0);
        await AddWorkflow(db, tid, "NEWER", createdAt: t0.AddDays(1));

        var router = Router(db);
        for (var i = 0; i < 5; i++)
            (await router.ResolveAsync(tid, emp.Id, Leave, default)).WorkflowId.Should().Be(older.Id, "the oldest wins among non-default ties, every time");

        var flagged = await AddWorkflow(db, tid, "FLAGGED", isDefault: true, createdAt: t0.AddDays(5));
        (await router.ResolveAsync(tid, emp.Id, Leave, default)).WorkflowId.Should().Be(flagged.Id, "IsDefault wins among unscoped ties");
    }

    // ── No configuration is an explicit, typed error ──────────────────────────

    [Fact]
    public async Task Resolve_NoWorkflow_ThrowsTypedNotConfigured_AndTryResolveReturnsNull()
    {
        var db = CreateDb();
        var tid = Guid.NewGuid();
        var emp = await AddEmp(db, tid, "EMP");
        await AddWorkflow(db, tid, "INACTIVE", isActive: false);
        await AddWorkflow(db, tid, "OTHER-ENTITY", entity: "OvertimeRequest");
        await AddWorkflow(db, Guid.NewGuid(), "OTHER-TENANT");

        var act = () => Router(db).ResolveAsync(tid, emp.Id, Leave, default);
        var ex = (await act.Should().ThrowAsync<ApprovalRouteNotConfiguredException>()).Which;
        ex.Code.Should().Be("approval_route_not_configured");
        ex.EntityName.Should().Be(Leave);
        ex.EmployeeId.Should().Be(emp.Id);
        ex.Should().BeAssignableTo<InvalidOperationException>("existing catch sites must keep turning it into a 4xx");
        (await Router(db).TryResolveAsync(tid, emp.Id, Leave, default)).Should().BeNull();
    }

    [Fact]
    public async Task Resolve_WorkflowWithNoFinalStep_ThrowsTypedInvalid()
    {
        var db = CreateDb();
        var tid = Guid.NewGuid();
        var emp = await AddEmp(db, tid, "EMP");
        await AddWorkflow(db, tid, "BROKEN", finalStep: false);

        var act = () => Router(db).ResolveAsync(tid, emp.Id, Leave, default);
        (await act.Should().ThrowAsync<ApprovalRouteInvalidException>()).Which.Code.Should().Be("approval_route_invalid");
    }

    [Fact]
    public async Task LeaveSubmit_WithNoWorkflow_IsRefusedWithTypedError_NothingPersisted()
    {
        var db = CreateDb();
        var tid = Guid.NewGuid();
        var lt = new LeaveType { TenantId = tid, Code = "AL", NameEn = "Annual", IsActive = true };
        db.LeaveTypes.Add(lt);
        var emp = await AddEmp(db, tid, "EMP");
        var start = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(10));
        db.EmployeeLeaveBalances.Add(new EmployeeLeaveBalance
        {
            TenantId = tid, EmployeeId = emp.Id, LeaveTypeId = lt.Id, LeaveTypeName = "Annual", Year = start.Year, Entitled = 21
        });
        await db.SaveChangesAsync();

        var submit = () => new LeaveService(db, Router(db)).SubmitRequestAsync(tid, new LeaveRequest
        {
            EmployeeId = emp.Id, LeaveTypeId = lt.Id, StartDate = start, EndDate = start, DayType = "Full"
        });

        await submit.Should().ThrowAsync<ApprovalRouteNotConfiguredException>();
        (await db.LeaveRequests.CountAsync()).Should().Be(0);
        (await db.LeaveApprovals.CountAsync()).Should().Be(0);
        (await db.ApprovalRequests.CountAsync()).Should().Be(0);
        (await db.EmployeeLeaveBalances.SingleAsync()).Pending.Should().Be(0);
    }

    // ── Approver resolution per type ─────────────────────────────────────────

    [Fact]
    public async Task Approver_Manager_ReturnsDirectManagerWithLogin()
    {
        var db = CreateDb();
        var tid = Guid.NewGuid();
        var mgrUser = Guid.NewGuid();
        var mgr = await AddEmp(db, tid, "MGR", userId: mgrUser);
        var emp = await AddEmp(db, tid, "EMP", managerId: mgr.Id);
        await AddWorkflow(db, tid, "W", "Manager");

        var a = await FirstApprover(db, tid, emp.Id);
        a.EmployeeId.Should().Be(mgr.Id);
        a.UserId.Should().Be(mgrUser);
        a.Escalated.Should().BeFalse();
    }

    [Fact]
    public async Task Approver_Manager_Missing_EscalatesVisiblyToHrManagerQueue()
    {
        var db = CreateDb();
        var tid = Guid.NewGuid();
        var emp = await AddEmp(db, tid, "EMP");
        await AddWorkflow(db, tid, "W", "Manager");

        var a = await FirstApprover(db, tid, emp.Id);
        a.EmployeeId.Should().BeNull();
        a.QueueRole.Should().Be("HR Manager");
        a.Escalated.Should().BeTrue();
    }

    [Fact]
    public async Task Approver_Supervisor_ReturnsSupervisor()
    {
        var db = CreateDb();
        var tid = Guid.NewGuid();
        var sup = await AddEmp(db, tid, "SUP");
        var emp = await AddEmp(db, tid, "EMP", supervisorId: sup.Id);
        await AddWorkflow(db, tid, "W", "Supervisor", entity: "OvertimeRequest");

        (await FirstApprover(db, tid, emp.Id, "OvertimeRequest")).EmployeeId.Should().Be(sup.Id);
    }

    [Fact]
    public async Task Approver_DepartmentHead_ReturnsHeadOfEmployeesDepartment()
    {
        var db = CreateDb();
        var tid = Guid.NewGuid();
        var head = await AddEmp(db, tid, "HEAD");
        var dept = await AddDept(db, tid, "Engineering", head.Id);
        var emp = await AddEmp(db, tid, "EMP", deptId: dept.Id);
        await AddWorkflow(db, tid, "W", "DepartmentHead");

        (await FirstApprover(db, tid, emp.Id)).EmployeeId.Should().Be(head.Id);
    }

    [Fact]
    public async Task Approver_DepartmentHead_None_Escalates()
    {
        var db = CreateDb();
        var tid = Guid.NewGuid();
        var emp = await AddEmp(db, tid, "EMP");
        await AddWorkflow(db, tid, "W", "DepartmentHead");

        (await FirstApprover(db, tid, emp.Id)).Escalated.Should().BeTrue();
    }

    [Fact]
    public async Task Approver_SpecificEmployee_ReturnsNamedEmployee_ButNeverTheRequesterThemself()
    {
        var db = CreateDb();
        var tid = Guid.NewGuid();
        var ceo = await AddEmp(db, tid, "CEO");
        var emp = await AddEmp(db, tid, "EMP");
        await AddWorkflow(db, tid, "W", "SpecificEmployee", entity: "ExpenseClaim", specificEmployeeId: ceo.Id);

        (await FirstApprover(db, tid, emp.Id, "ExpenseClaim")).EmployeeId.Should().Be(ceo.Id);
        var self = await FirstApprover(db, tid, ceo.Id, "ExpenseClaim");
        self.EmployeeId.Should().BeNull("an approver cannot be routed their own request");
        self.Escalated.Should().BeTrue();
    }

    [Fact]
    public async Task Approver_HR_IsTheHrManagerRoleQueue_Deterministically_NotAnArbitraryPerson()
    {
        var db = CreateDb();
        var tid = Guid.NewGuid();
        // Two HR-designated people: the retired resolver picked one with an unordered FirstOrDefault.
        await AddEmp(db, tid, "HR1", designation: "HR Director", managerId: 999);
        await AddEmp(db, tid, "HR2", designation: "HR Manager", managerId: 999);
        var emp = await AddEmp(db, tid, "EMP");
        await AddWorkflow(db, tid, "W", "HR");

        for (var i = 0; i < 3; i++)
        {
            var a = await FirstApprover(db, tid, emp.Id);
            a.EmployeeId.Should().BeNull();
            a.UserId.Should().BeNull();
            a.QueueRole.Should().Be("HR Manager");
            a.Escalated.Should().BeFalse();
        }
    }

    [Fact]
    public async Task Approver_Role_IsTheConfiguredRoleQueue()
    {
        var db = CreateDb();
        var tid = Guid.NewGuid();
        var emp = await AddEmp(db, tid, "EMP");
        await AddWorkflow(db, tid, "W", "Role", entity: "PayrollRun");

        var a = await FirstApprover(db, tid, emp.Id, "PayrollRun");
        a.ApproverType.Should().Be("Role");
        a.QueueRole.Should().Be("Payroll Manager");
        a.EmployeeId.Should().BeNull();
    }

    // ── Configuration guard rails on the single model ────────────────────────────────────────────

    private static readonly Zayra.Api.Application.Auth.RequestContext Admin =
        new("127.0.0.1", "tests", Guid.NewGuid(), null, ["Admin"], ["approvals.manage"]);

    private static ApprovalWorkflowRequest WfRequest(string code, Guid? deptId = null, Guid? gradeId = null, bool isDefault = false) =>
        new(code, code, Leave, true,
            new[] { new ApprovalWorkflowStepRequest(1, "Manager", "Manager", "Manager"), new ApprovalWorkflowStepRequest(2, "HR", "HR Manager", "Role") },
            deptId, gradeId, isDefault);

    [Fact]
    public async Task CreateWorkflow_SecondActiveWorkflowForSameEntityAndScope_IsRefused_ScopedDefaultIsRefused()
    {
        var db = CreateDb();
        var tid = Guid.NewGuid();
        var dept = await AddDept(db, tid, "Engineering");
        var svc = new ApprovalWorkflowService(db, new Zayra.Api.Infrastructure.Audit.AuditService(db));

        var first = await svc.CreateWorkflowAsync(tid, WfRequest("LEAVE-A", isDefault: true), Admin, default);
        first.IsDefault.Should().BeTrue();
        first.Steps.Should().Contain(x => x.StepOrder == 2 && x.IsFinalStep, "the last step is always final");

        var dup = () => svc.CreateWorkflowAsync(tid, WfRequest("LEAVE-B"), Admin, default);
        await dup.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already covers*");

        var scoped = await svc.CreateWorkflowAsync(tid, WfRequest("LEAVE-ENG", deptId: dept.Id), Admin, default);
        scoped.DepartmentId.Should().Be(dept.Id);

        var scopedDefault = () => svc.CreateWorkflowAsync(tid, WfRequest("LEAVE-ENG2", deptId: dept.Id, isDefault: true), Admin, default);
        await scopedDefault.Should().ThrowAsync<InvalidOperationException>().WithMessage("*cannot be the tenant default*");
    }

    [Fact]
    public async Task GenericDecision_OnANonFinalStepWithNothingAfterIt_IsRefused_NotApproved()
    {
        var db = CreateDb();
        var tid = Guid.NewGuid();
        var wf = new ApprovalWorkflow { TenantId = tid, Code = "T", Name = "T", EntityName = "EmployeeTransferRequest" };
        wf.Steps.Add(new ApprovalWorkflowStep { TenantId = tid, WorkflowId = wf.Id, StepOrder = 1, StepName = "Mgr", ApproverRole = "Manager" });
        wf.Steps.Add(new ApprovalWorkflowStep { TenantId = tid, WorkflowId = wf.Id, StepOrder = 2, StepName = "HR", ApproverRole = "HR Manager", IsFinalStep = true });
        db.ApprovalWorkflows.Add(wf);
        await db.SaveChangesAsync();
        var svc = new ApprovalWorkflowService(db, new Zayra.Api.Infrastructure.Audit.AuditService(db));
        var started = await svc.CreateRequestAsync(tid, new CreateApprovalRequest(wf.Id, "EmployeeTransferRequest", "T-1", "Transfer"),
            new("127.0.0.1", "tests", Guid.NewGuid(), tid, ["Employee"], []), default);

        db.ApprovalWorkflowSteps.Remove(await db.ApprovalWorkflowSteps.SingleAsync(x => x.WorkflowId == wf.Id && x.StepOrder == 2));
        await db.SaveChangesAsync();

        var decide = () => svc.DecideAsync(tid, started.Id, new ApprovalDecisionRequest("Approve", "ok"),
            new("127.0.0.1", "tests", Guid.NewGuid(), tid, ["Manager"], []), default);
        await decide.Should().ThrowAsync<ApprovalRouteInvalidException>();
        (await db.ApprovalRequests.SingleAsync(x => x.Id == started.Id)).Status.Should().Be("Pending");
    }

    // ── Import preview ────────────────────────────────────────────────────────

    [Fact]
    public async Task ImportPreview_DetectsCircularHierarchyInBatch()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Test", Slug = "prev-circ" });
        db.TenantSubscriptions.Add(new TenantSubscription { TenantId = tenantId, MaxEmployees = 100, Plan = "Pro", Status = "Active" });
        await db.SaveChangesAsync();

        var controller = HrmHierarchyTests.BuildImportControllerInternal(db, tenantId);

        // A → B → A (circular within the same import batch)
        var csv =
            "EmployeeCode,FullName,ArabicName,WorkEmail,Phone,Gender,Nationality,Department,DepartmentCode,Designation,JobTitle,EmploymentType,ContractType,Status,JoiningDate,ManagerEmployeeCode,SupervisorEmployeeCode\n" +
            "PREV-A,Alice,,,,,,,,,,,Full-time,,2023-01-01,PREV-B,\n" +
            "PREV-B,Bob,,,,,,,,,,,Full-time,,2023-01-01,PREV-A,\n";

        var result = await controller.ImportPreview(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None);

        var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result);
        var data = ok.Value!;
        var wouldCreate = (int)data.GetType().GetProperty("wouldCreate")!.GetValue(data)!;
        var wouldSkip = (int)data.GetType().GetProperty("wouldSkip")!.GetValue(data)!;
        var rows = (System.Collections.IEnumerable)data.GetType().GetProperty("rows")!.GetValue(data)!;
        var rowList = rows.Cast<object>().ToList();
        // ACCEPT-NEVER-BLOCK + preview↔commit parity: circular manager is NO LONGER a preview Error (commit
        // does not drop those rows — it imports both people and skips the link). Both rows WillCreate, and
        // the circular is surfaced as a WARNING, not a row Error.
        Assert.Equal(2, wouldCreate);
        Assert.Equal(0, wouldSkip);
        Assert.DoesNotContain(rowList, r => (string)r.GetType().GetProperty("status")!.GetValue(r)! == "Error");
        Assert.Contains(rowList, r =>
        {
            var warns = (System.Collections.IEnumerable)r.GetType().GetProperty("warnings")!.GetValue(r)!;
            return warns.Cast<string>().Any(w => w.Contains("circular hierarchy"));
        });
    }

    [Fact]
    public async Task ImportPreview_DoesNotCommitToDatabase()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Test", Slug = "prev-nocommit" });
        db.TenantSubscriptions.Add(new TenantSubscription { TenantId = tenantId, MaxEmployees = 100, Plan = "Pro", Status = "Active" });
        await db.SaveChangesAsync();

        var controller = HrmHierarchyTests.BuildImportControllerInternal(db, tenantId);
        var csv =
            "EmployeeCode,FullName,ArabicName,WorkEmail,Phone,Gender,Nationality,Department,DepartmentCode,Designation,JobTitle,EmploymentType,ContractType,Status,JoiningDate,ManagerEmployeeCode,SupervisorEmployeeCode\n" +
            "NOCOMMIT-1,Alice,,,,,,,,,,,Full-time,,2023-01-01,,\n";

        await controller.ImportPreview(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None);

        // No employees should have been created
        Assert.Equal(0, await db.Employees.CountAsync(e => e.TenantId == tenantId));
    }

    [Fact]
    public async Task ImportPreview_FlagsUnknownManagerCode()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Test", Slug = "prev-mgr" });
        db.TenantSubscriptions.Add(new TenantSubscription { TenantId = tenantId, MaxEmployees = 100, Plan = "Pro", Status = "Active" });
        await db.SaveChangesAsync();

        var controller = HrmHierarchyTests.BuildImportControllerInternal(db, tenantId);
        var csv =
            "EmployeeCode,FullName,ArabicName,WorkEmail,Phone,Gender,Nationality,Department,DepartmentCode,Designation,JobTitle,EmploymentType,ContractType,Status,JoiningDate,ManagerEmployeeCode,SupervisorEmployeeCode\n" +
            "MGR-TEST,Alice,,,,,,,,,,,Full-time,,2023-01-01,NONEXISTENT,\n";

        var result = await controller.ImportPreview(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None);

        var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result);
        var data = ok.Value!;
        var rows = (System.Collections.IEnumerable)data.GetType().GetProperty("rows")!.GetValue(data)!;
        var rowList = rows.Cast<object>().ToList();
        var row = rowList[0];
        var warnings = (System.Collections.IEnumerable)row.GetType().GetProperty("warnings")!.GetValue(row)!;
        Assert.NotEmpty(warnings.Cast<string>());
    }
}
