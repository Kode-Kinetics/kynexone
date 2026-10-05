using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.WorkWeek;
using Zayra.Api.Controllers;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Controllers.Leave;
using Zayra.Api.Controllers.Recruitment;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Leave;
using Zayra.Api.Infrastructure.Recruitment;
using Zayra.Api.Models;
using ApprovalDecisionRequest = Zayra.Api.Application.Approvals.ApprovalDecisionRequest;

namespace Zayra.Api.Tests;

/// <summary>
/// The gaps an independent review found in the first separation-of-duties change: the subject check
/// failed open across legal entities and for an employee change carrying no subject; a one-click
/// endpoint applied employee changes around the Approval Center; a barred sole Admin was not told that
/// nobody else could decide; and Loans and Offers still let one person approve every step.
/// </summary>
public class ApprovalSeparationOfDutiesGapTests
{
    // ── (1) The subject check is tenant-wide ─────────────────────────────────────

    [Fact]
    public async Task TheSubject_IsBarred_WhenTheirOwnEmployeeRowIsInAnotherCompany()
    {
        // Transferred to company B after the request was raised under company A. The caller is scoped to
        // A, so a company-filtered "which employee am I?" lookup finds nothing and the bar failed open.
        var options = Options();
        var tenantId = Guid.NewGuid();
        var subjectUserId = Guid.NewGuid();
        var companyA = MakeCompany(tenantId, "Alpha");
        var companyB = MakeCompany(tenantId, "Beta");
        var subject = new Employee
        {
            TenantId = tenantId, CompanyId = companyB.Id, UserAccountId = subjectUserId, EmployeeCode = "SOD-X1",
            FullName = "Transferred Approver", Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-2),
        };
        var workflow = new ApprovalWorkflow { TenantId = tenantId, Code = "TRANSFER", Name = "Transfer", EntityName = "EmployeeTransferRequest" };
        workflow.Steps.Add(new ApprovalWorkflowStep { TenantId = tenantId, StepOrder = 1, StepName = "HR", ApproverRole = "HR Manager", IsFinalStep = true });
        await using (var seed = new ZayraDbContext(options))
        {
            seed.AddRange(companyA, companyB, subject, workflow);
            await seed.SaveChangesAsync();
            seed.ApprovalRequests.Add(new ApprovalRequest
            {
                TenantId = tenantId, CompanyId = companyA.Id, WorkflowId = workflow.Id, EntityName = "EmployeeTransferRequest",
                EntityId = "TR-X1", Title = "Transfer", Status = "Pending", CurrentStepOrder = 1, RequestedByUserId = Guid.NewGuid(),
                RequestedForEmployeeId = subject.Id, CurrentApproverRole = "HR Manager", CurrentApproverType = "Role",
            });
            await seed.SaveChangesAsync();
        }

        var accessor = new SwitchableAccessor { HttpContext = ScopedTo(tenantId, subjectUserId, companyA.Id) };
        await using var db = new ZayraDbContext(options, accessor);
        (await db.Employees.AnyAsync(e => e.Id == subject.Id)).Should().BeFalse("precondition: the caller's own row is outside their company scope");
        var approval = await db.ApprovalRequests.SingleAsync();
        var caller = new RequestContext("127.0.0.1", "xunit", subjectUserId, tenantId, ["HR Manager"], ["approvals.decide", "approvals.override"]);

        var act = () => new ApprovalWorkflowService(db, new AuditService(db)).DecideAsync(tenantId, approval.Id,
            new ApprovalDecisionRequest("Approve", "mine"), caller, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Segregation of duties: this request is about you*");
    }

    [Fact]
    public async Task TheSubject_IsBarred_ForAnEmployeeChangeWhoseApprovalCarriesNoSubject()
    {
        // Legacy rows: RequestedForEmployeeId is null and the subject is only on the change request.
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var subjectUserId = Guid.NewGuid();
        var subject = await AddEmployeeAsync(db, tenantId, subjectUserId);
        var change = new EmployeeChangeRequest
        {
            TenantId = tenantId, EmployeeId = subject.Id, RequestedByUserId = Guid.NewGuid(), SensitiveFields = "salary",
            EffectiveDate = DateOnly.FromDateTime(DateTime.UtcNow.Date),
            ProposedChangesJson = JsonSerializer.Serialize(new Dictionary<string, object> { ["salary"] = 99_000m }),
        };
        var workflow = new ApprovalWorkflow { TenantId = tenantId, Code = "EMPLOYEE-CHANGE", Name = "Change", EntityName = nameof(EmployeeChangeRequest) };
        workflow.Steps.Add(new ApprovalWorkflowStep { TenantId = tenantId, StepOrder = 1, StepName = "HR", ApproverRole = "HR Manager", IsFinalStep = true });
        var approval = new ApprovalRequest
        {
            TenantId = tenantId, WorkflowId = workflow.Id, EntityName = nameof(EmployeeChangeRequest), EntityId = change.Id.ToString(),
            Title = "Salary", Status = "Pending", CurrentStepOrder = 1, RequestedByUserId = change.RequestedByUserId,
            RequestedForEmployeeId = null, CurrentApproverRole = "HR Manager", CurrentApproverType = "Role",
        };
        db.AddRange(change, workflow, approval);
        await db.SaveChangesAsync();
        var caller = new RequestContext("127.0.0.1", "xunit", subjectUserId, tenantId, ["Admin"], ["approvals.decide", "approvals.override"]);

        var act = () => Service(db).DecideAsync(tenantId, approval.Id, new ApprovalDecisionRequest("Approve", "my raise"), caller, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Segregation of duties: this request is about you*");
        db.ChangeTracker.Clear();
        (await db.Employees.SingleAsync()).Salary.Should().NotBe(99_000m);
        (await db.EmployeeChangeRequests.SingleAsync()).Status.Should().Be("PendingApproval");
    }

    // ── (2) The one-click change approval is retired ─────────────────────────────

    [Fact]
    public async Task TheRetiredDirectChangeApproval_CannotBeUsedByTheSubject()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var subjectUserId = Guid.NewGuid();
        var subject = await AddEmployeeAsync(db, tenantId, subjectUserId);
        var accepted = await EmployeesFor(db, tenantId, Guid.NewGuid()).UpdateEmployee(subject.Id,
            new EmployeeUpdateRequest(DateOnly.FromDateTime(DateTime.UtcNow.Date), new() { ["salary"] = JsonSerializer.SerializeToElement(99_000m) }),
            CancellationToken.None);
        accepted.Should().BeOfType<AcceptedResult>();
        var change = await db.EmployeeChangeRequests.AsNoTracking().SingleAsync();

        var result = await EmployeesFor(db, tenantId, subjectUserId).ApproveChange(change.Id, CancellationToken.None);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status410Gone);
        db.ChangeTracker.Clear();
        (await db.Employees.SingleAsync()).Salary.Should().Be(10_000m, "nothing is applied");
        (await db.EmployeeChangeRequests.SingleAsync()).Status.Should().Be("PendingApproval");
        (await db.ApprovalRequests.SingleAsync()).Status.Should().Be("Pending", "the Approval Center item is not left orphaned");
    }

    // ── (3) A barred sole Admin is told nobody else can decide ───────────────────

    [Fact]
    public async Task ASoleAdminWhoIsTheSubject_IsToldNobodyElseCanDecide_UntilAColleagueHasTheRole()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var adminRole = await SeedAdminRoleAsync(db, tenantId);
        var soleAdmin = await AddUserAsync(db, tenantId, adminRole);
        var subject = await AddEmployeeAsync(db, tenantId, soleAdmin);
        var approvalId = await StartRequestAsync(db, tenantId, subject.Id, steps: 1);
        var caller = AdminContext(tenantId, soleAdmin);

        var alone = await Service(db).GetRequestAsync(tenantId, approvalId, caller, CancellationToken.None);
        alone!.CanDecide.Should().BeFalse();
        alone.DecisionBlockedReason.Should().StartWith("This request is about you")
            .And.Contain("No other active user can decide it yet").And.Contain("User Management");

        await AddUserAsync(db, tenantId, adminRole);
        var withColleague = await Service(db).GetRequestAsync(tenantId, approvalId, caller, CancellationToken.None);
        withColleague!.DecisionBlockedReason.Should().Contain("It is waiting for");
    }

    [Fact]
    public async Task ASoleAdminWhoDecidedStepOne_IsToldNobodyElseCanDecideStepTwo()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var adminRole = await SeedAdminRoleAsync(db, tenantId);
        var soleAdmin = await AddUserAsync(db, tenantId, adminRole);
        var subject = await AddEmployeeAsync(db, tenantId, userAccountId: null);
        var approvalId = await StartRequestAsync(db, tenantId, subject.Id, steps: 2);
        var caller = AdminContext(tenantId, soleAdmin);

        (await Service(db).DecideAsync(tenantId, approvalId, new ApprovalDecisionRequest("Approve", "step 1"), caller, CancellationToken.None))!
            .CurrentStepOrder.Should().Be(2);
        var view = await Service(db).GetRequestAsync(tenantId, approvalId, caller, CancellationToken.None);

        view!.CanDecide.Should().BeFalse();
        view.DecisionBlockedReason.Should().StartWith("You already decided an earlier step")
            .And.Contain("No other active user can decide it yet");
    }

    // ── (5) The leave screen path ────────────────────────────────────────────────

    [Fact]
    public async Task LeaveScreen_TheManagerWhoApprovedStepOne_CannotApproveStepTwo()
    {
        await using var db = CreateDb();
        var (tenantId, managerUserId, hrUserId, leaveId) = await SubmitTwoStepLeaveAsync(db);

        (await LeaveFor(db, tenantId, managerUserId).Approve(leaveId, new ApproveLeaveRequest("ok"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();

        var again = await LeaveFor(db, tenantId, managerUserId).Approve(leaveId, new ApproveLeaveRequest("both steps"), CancellationToken.None);
        var bad = again.Should().BeOfType<BadRequestObjectResult>().Subject;
        JsonSerializer.Serialize(bad.Value).Should().Contain("Segregation of duties");
        (await db.LeaveRequests.AsNoTracking().SingleAsync(r => r.Id == leaveId)).Status.Should().Be("PendingHRApproval");

        (await LeaveFor(db, tenantId, hrUserId).Approve(leaveId, new ApproveLeaveRequest("final"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        (await db.LeaveRequests.AsNoTracking().SingleAsync(r => r.Id == leaveId)).Status.Should().Be("Approved");
    }

    // ── (4) Loans and Offers: one person, one step ───────────────────────────────

    [Fact]
    public async Task Loans_WhoeverApprovedAnEarlierStep_CannotDecideALaterOne()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var firstApprover = Guid.NewGuid();
        var borrower = await AddEmployeeAsync(db, tenantId, userAccountId: null);
        var loan = new EmployeeLoan
        {
            TenantId = tenantId, EmployeeId = borrower.PublicId, EmployeeIntId = borrower.Id, EmployeeName = borrower.FullName,
            LoanTypeId = Guid.NewGuid(), LoanTypeName = "Emergency", LoanNumber = "LN-2026-SOD02",
            RequestedAmount = 6_000m, RequestedInstallments = 3, Status = "Pending", RepaymentMethod = "BankTransfer",
            CreatedBy = Guid.NewGuid(),
        };
        var stepOne = new LoanApproval { TenantId = tenantId, LoanId = loan.Id, StepOrder = 1, ApproverRole = "HR Manager", Status = "Approved", ApprovedBy = firstApprover };
        var stepTwo = new LoanApproval { TenantId = tenantId, LoanId = loan.Id, StepOrder = 2, ApproverRole = "HR Director" };
        db.AddRange(loan, stepOne, stepTwo);
        await db.SaveChangesAsync();

        foreach (var decision in new[] { "Approved", "Rejected" })
        {
            var refused = await WithPrincipal(new LoansController(db, new OrgScope()), tenantId, firstApprover, "Admin")
                .DecideApproval(loan.Id, stepTwo.Id, new Zayra.Api.Controllers.Finance.ApprovalDecisionRequest(decision, "again", null, null, null), CancellationToken.None);
            Assert.Equal("Maker-checker control: you approved an earlier step of this loan, so a different approver must decide this one.",
                Assert.IsType<BadRequestObjectResult>(refused).Value as string);
        }
        Assert.Equal("Pending", (await db.LoanApprovals.SingleAsync(x => x.Id == stepTwo.Id)).Status);

        var bySomeoneElse = await WithPrincipal(new LoansController(db, new OrgScope()), tenantId, Guid.NewGuid(), "Admin")
            .DecideApproval(loan.Id, stepTwo.Id, new Zayra.Api.Controllers.Finance.ApprovalDecisionRequest("Rejected", "no", null, null, null), CancellationToken.None);
        Assert.IsType<OkObjectResult>(bySomeoneElse);
    }

    [Fact]
    public async Task Offers_WhoeverApprovedAnEarlierStep_CannotApproveALaterOne()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var approver = Guid.NewGuid();
        var applicationId = Guid.NewGuid();
        var offer = new OfferLetter
        {
            TenantId = tenantId, ApplicationId = applicationId, CandidateName = "Layla Hassan", OfferedJobTitle = "Engineer",
            OfferedDepartment = "Technology", StartDate = new DateOnly(2026, 11, 1), Status = "PendingApproval",
        };
        var stepOne = new OfferApproval { TenantId = tenantId, OfferLetterId = offer.Id, ApplicationId = applicationId, StepOrder = 1, ApproverName = "Head", ApproverUserId = approver, Status = "Approved" };
        var stepTwo = new OfferApproval { TenantId = tenantId, OfferLetterId = offer.Id, ApplicationId = applicationId, StepOrder = 2, ApproverName = "Head", ApproverUserId = approver };
        db.AddRange(offer, stepOne, stepTwo);
        await db.SaveChangesAsync();

        var result = await WithPrincipal(new OffersController(db, new GapNoLetters(), new RecruitmentService(db)), tenantId, approver, "HR Manager")
            .DecideApproval(offer.Id, stepTwo.Id, new DecideApprovalRequest("Approved", "again"), CancellationToken.None);

        var refused = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, refused.StatusCode);
        JsonSerializer.Serialize(refused.Value).Should().Contain("offer_earlier_step_approver");
        Assert.Equal("Pending", (await db.OfferApprovals.SingleAsync(x => x.Id == stepTwo.Id)).Status);
        Assert.Equal("PendingApproval", (await db.OfferLetters.SingleAsync()).Status);
    }

    // ── fixture ──────────────────────────────────────────────────────────────────

    private static DbContextOptions<ZayraDbContext> Options() =>
        new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

    private static ZayraDbContext CreateDb() => new(Options());

    private static ApprovalWorkflowService Service(ZayraDbContext db) => new(db, new AuditService(db));

    private static RequestContext AdminContext(Guid tenantId, Guid userId) =>
        new("127.0.0.1", "xunit", userId, tenantId, ["Admin"], ["approvals.read", "approvals.decide", "approvals.override"]);

    private static async Task<Guid> StartRequestAsync(ZayraDbContext db, Guid tenantId, int subjectEmployeeId, int steps)
    {
        var workflow = new ApprovalWorkflow { TenantId = tenantId, Code = "TRANSFER", Name = "Transfer", EntityName = "EmployeeTransferRequest" };
        workflow.Steps.Add(new ApprovalWorkflowStep { TenantId = tenantId, StepOrder = 1, StepName = "First", ApproverRole = "HR Manager", IsFinalStep = steps == 1 });
        if (steps > 1)
            workflow.Steps.Add(new ApprovalWorkflowStep { TenantId = tenantId, StepOrder = 2, StepName = "Second", ApproverRole = "HR Manager", IsFinalStep = true });
        db.ApprovalWorkflows.Add(workflow);
        await db.SaveChangesAsync();
        var request = await Service(db).CreateRequestAsync(tenantId,
            new CreateApprovalRequest(workflow.Id, "EmployeeTransferRequest", "TR-SOD-2", "Transfer", RequestedForEmployeeId: subjectEmployeeId),
            new RequestContext("127.0.0.1", "xunit", Guid.NewGuid(), tenantId, ["HR Officer"], []), CancellationToken.None);
        return request.Id;
    }

    private static async Task<Employee> AddEmployeeAsync(ZayraDbContext db, Guid tenantId, Guid? userAccountId)
    {
        var employee = new Employee
        {
            TenantId = tenantId, UserAccountId = userAccountId, EmployeeCode = $"SOD-{Guid.NewGuid():N}"[..12],
            FullName = "Huda Salem", Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-2), Salary = 10_000m,
        };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        return employee;
    }

    private static async Task<Guid> SeedAdminRoleAsync(ZayraDbContext db, Guid tenantId)
    {
        var admin = new Role { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Admin", NormalizedName = "ADMIN", Description = "Admin" };
        var overridePermission = new Permission { Key = "approvals.override", Module = "Approvals", Description = "Override" };
        db.Roles.Add(admin);
        db.Permissions.Add(overridePermission);
        db.RolePermissions.Add(new RolePermission { RoleId = admin.Id, PermissionId = overridePermission.Id });
        await db.SaveChangesAsync();
        return admin.Id;
    }

    private static async Task<Guid> AddUserAsync(ZayraDbContext db, Guid tenantId, Guid roleId)
    {
        var user = new User { TenantId = tenantId, Email = $"{Guid.NewGuid():N}@example.test", FullName = "Admin", IsActive = true };
        user.NormalizedEmail = user.Email.ToUpperInvariant();
        db.Users.Add(user);
        db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = roleId });
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static async Task<(Guid TenantId, Guid ManagerUserId, Guid HrUserId, Guid LeaveId)> SubmitTwoStepLeaveAsync(ZayraDbContext db)
    {
        var tenantId = Guid.NewGuid();
        var managerUserId = Guid.NewGuid();
        var hrUserId = Guid.NewGuid();
        var leaveType = new LeaveType { TenantId = tenantId, Code = "AL", NameEn = "Annual Leave", IsActive = true };
        var manager = new Employee { TenantId = tenantId, UserAccountId = managerUserId, EmployeeCode = "MGR-1", FullName = "Manager One", Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-3) };
        var hr = new Employee { TenantId = tenantId, UserAccountId = hrUserId, EmployeeCode = "HR-1", FullName = "HR One", Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-3) };
        db.LeaveTypes.Add(leaveType);
        db.Employees.AddRange(manager, hr);
        await db.SaveChangesAsync();
        var employee = new Employee { TenantId = tenantId, EmployeeCode = "EMP-1", FullName = "Employee One", ManagerEmployeeId = manager.Id, Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-1) };
        db.Employees.Add(employee);
        var workflow = new ApprovalWorkflow { TenantId = tenantId, Code = "MGR-HR", Name = "Manager then HR", EntityName = nameof(LeaveRequest), IsDefault = true, IsActive = true };
        workflow.Steps.Add(new ApprovalWorkflowStep { TenantId = tenantId, WorkflowId = workflow.Id, StepOrder = 1, StepName = "Manager", ApproverType = "Manager" });
        workflow.Steps.Add(new ApprovalWorkflowStep { TenantId = tenantId, WorkflowId = workflow.Id, StepOrder = 2, StepName = "HR", ApproverType = "HR", IsFinalStep = true });
        db.ApprovalWorkflows.Add(workflow);
        var start = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(7));
        while (WorkWeekConfig.GccDefault.IsWeekend(start.DayOfWeek)) start = start.AddDays(1);
        await db.SaveChangesAsync();
        db.EmployeeLeaveBalances.Add(new EmployeeLeaveBalance
        {
            TenantId = tenantId, EmployeeId = employee.Id, EmployeeName = employee.FullName,
            LeaveTypeId = leaveType.Id, LeaveTypeName = leaveType.NameEn, Year = start.Year, Entitled = 21,
        });
        await db.SaveChangesAsync();
        var submitted = await new LeaveService(db, new ApprovalRouter(db)).SubmitRequestAsync(tenantId, new LeaveRequest
        {
            TenantId = tenantId, EmployeeId = employee.Id, LeaveTypeId = leaveType.Id, StartDate = start, EndDate = start, DayType = "Full",
        });
        return (tenantId, managerUserId, hrUserId, submitted.Id);
    }

    private static LeaveRequestsController LeaveFor(ZayraDbContext db, Guid tenantId, Guid userId) =>
        WithPrincipal(new LeaveRequestsController(db, new LeaveService(db, new ApprovalRouter(db)), new OrgScope(), TestNotifications.For(db)),
            tenantId, userId, "Admin");

    private static EmployeesController EmployeesFor(ZayraDbContext db, Guid tenantId, Guid userId)
    {
        var audit = new AuditService(db);
        var controller = new EmployeesController(db, new Pbkdf2PasswordHasher(), audit, new NullDocumentStorage(), TestNotifications.For(db),
            new GapHijri(), new Zayra.Api.Infrastructure.Common.DataScopeService(db), new GapNoLetters(), new ApprovalWorkflowService(db, audit));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("tenant_id", tenantId.ToString()),
                    new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                    new Claim(ClaimTypes.Role, "Admin"),
                    new Claim("permission", "employees.read"),
                    new Claim("permission", "employees.write"),
                    new Claim("permission", "employees.sensitive"),
                    new Claim("permission", "employees.approve"),
                }, "Test")),
            },
        };
        return controller;
    }

    private static T WithPrincipal<T>(T controller, Guid tenantId, Guid userId, string role) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("tenant_id", tenantId.ToString()),
                    new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                    new Claim(ClaimTypes.Name, "Approver"),
                    new Claim(ClaimTypes.Role, role),
                }, "Test")),
            },
        };
        return controller;
    }

    private static HttpContext ScopedTo(Guid tenantId, Guid userId, Guid companyId) => new DefaultHttpContext
    {
        User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim("entity_access", JsonSerializer.Serialize(new { c = companyId, r = "Viewer" })),
        }, "Test")),
    };

    private static Company MakeCompany(Guid tenantId, string name) => new()
    {
        Id = Guid.NewGuid(), TenantId = tenantId, LegalNameEn = name, CountryCode = "SAU", Jurisdiction = "KSA-mainland",
        RegistrationNumber = $"REG-{Guid.NewGuid():N}", DefaultCurrency = "SAR", IsActive = true,
    };

    private sealed class SwitchableAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    private sealed class OrgScope : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
            => Task.FromResult(new DataScope { Level = DataScopeLevel.Organization });
    }
}

file sealed class GapHijri : Zayra.Api.Infrastructure.Localization.IHijriDateService
{
    public Zayra.Api.Infrastructure.Localization.DateConversionDto FromGregorian(DateOnly date) => new(date.ToString("yyyy-MM-dd"), "1447-01-01", 1447, 1, 1);
}

file sealed class GapNoLetters : Zayra.Api.Infrastructure.Documents.Letters.ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(Zayra.Api.Infrastructure.Documents.Letters.PayslipData data, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData data, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData data, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.OfferLetterData data, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());
}
