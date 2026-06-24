using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Controllers.Leave;
using Zayra.Api.Controllers.Recruitment;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Leave;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// End-to-end business logic continuity tests.
/// Each test covers a full domain chain (create → act → verify DB state)
/// to confirm that no feature link is a dead stub.
/// Uses InMemory EF — no Postgres/Docker required.
/// </summary>
public class BusinessLogicContinuityTests
{
    // ─────────────────────────────────────────────────────────────────────────
    // 1. LEAVE LIFECYCLE: Submit → Approve → Balance moved Pending → Used
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LeaveRequest_WhenApproved_MovesBalanceFromPendingToUsed()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();

        var lt = new LeaveType { TenantId = tenantId, Code = "AL", NameEn = "Annual Leave", IsActive = true };
        var emp = new Employee
        {
            TenantId = tenantId, EmployeeCode = "EMP-001", FullName = "Sara Al-Mutairi",
            Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-2)
        };
        db.LeaveTypes.Add(lt);
        db.Employees.Add(emp);
        await db.SaveChangesAsync();

        // Balance must have enough Available days (Entitled - Used - Pending ≥ requestedDays)
        var balance = new EmployeeLeaveBalance
        {
            TenantId = tenantId, EmployeeId = emp.Id, LeaveTypeId = lt.Id,
            LeaveTypeName = lt.NameEn, EmployeeName = emp.FullName,
            Year = DateTime.UtcNow.Year, Entitled = 21, Accrued = 10, Used = 0, Pending = 0
        };
        db.EmployeeLeaveBalances.Add(balance);
        await db.SaveChangesAsync();

        // Submit 3-day leave
        var svc = new LeaveService(db, new NullApprovalPolicyService());
        var request = new LeaveRequest
        {
            TenantId = tenantId, EmployeeId = emp.Id, EmployeeName = emp.FullName,
            LeaveTypeId = lt.Id,
            StartDate = DateOnly.FromDateTime(DateTime.Today.AddDays(7)),
            EndDate = DateOnly.FromDateTime(DateTime.Today.AddDays(9)),
            DayType = "Full", Reason = "Family vacation",
        };
        var submitted = await svc.SubmitRequestAsync(tenantId, request, CancellationToken.None);

        // Balance: Pending > 0, Used = 0
        var afterSubmit = await db.EmployeeLeaveBalances.FindAsync(balance.Id);
        afterSubmit!.Pending.Should().BeGreaterThan(0, "submitting must move days to Pending");
        afterSubmit.Used.Should().Be(0);

        // Approve using a different userId than the employee's UserAccountId (null here)
        await svc.ApproveRequestAsync(tenantId, submitted.Id, Guid.NewGuid(), "Manager", null, CancellationToken.None);

        // Balance: Pending → 0, Used > 0
        var afterApprove = await db.EmployeeLeaveBalances.FindAsync(balance.Id);
        afterApprove!.Used.Should().BeGreaterThan(0, "approval must move days from Pending to Used");
        afterApprove.Pending.Should().Be(0, "Pending must clear on approval");

        var req = await db.LeaveRequests.FindAsync(submitted.Id);
        req!.Status.Should().Be("Approved");

        // Audit log emitted
        var audited = await db.LeaveAuditLogs.AnyAsync(a =>
            a.EntityId == submitted.Id.ToString() && a.Action == "Approved");
        audited.Should().BeTrue("approval must emit a LeaveAuditLog entry");
    }

    [Fact]
    public async Task LeaveRequest_WhenRejected_BalanceIsFullyRestored()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();

        var lt = new LeaveType { TenantId = tenantId, Code = "SL", NameEn = "Sick Leave", IsActive = true };
        var emp = new Employee
        {
            TenantId = tenantId, EmployeeCode = "EMP-002", FullName = "Khalid Al-Dosari",
            Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-1)
        };
        db.LeaveTypes.Add(lt); db.Employees.Add(emp);
        await db.SaveChangesAsync();

        db.EmployeeLeaveBalances.Add(new EmployeeLeaveBalance
        {
            TenantId = tenantId, EmployeeId = emp.Id, LeaveTypeId = lt.Id,
            LeaveTypeName = lt.NameEn, EmployeeName = emp.FullName,
            Year = DateTime.UtcNow.Year, Entitled = 15, Accrued = 5
        });
        await db.SaveChangesAsync();

        var svc = new LeaveService(db, new NullApprovalPolicyService());
        var request = new LeaveRequest
        {
            TenantId = tenantId, EmployeeId = emp.Id, EmployeeName = emp.FullName,
            LeaveTypeId = lt.Id,
            StartDate = DateOnly.FromDateTime(DateTime.Today.AddDays(14)),
            EndDate = DateOnly.FromDateTime(DateTime.Today.AddDays(14)),
            DayType = "Full",
        };
        var submitted = await svc.SubmitRequestAsync(tenantId, request, CancellationToken.None);

        await svc.RejectRequestAsync(tenantId, submitted.Id, Guid.NewGuid(), "HR", "Insufficient proof", CancellationToken.None);

        var bal = await db.EmployeeLeaveBalances.FirstAsync(b => b.EmployeeId == emp.Id);
        bal.Used.Should().Be(0, "rejection must not consume any balance");
        bal.Pending.Should().Be(0, "rejection must release Pending back to zero");

        var req = await db.LeaveRequests.FindAsync(submitted.Id);
        req!.Status.Should().Be("Rejected");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 2. LEAVE MONTHLY ACCRUAL: Active employees get 1.75 days/month
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LeaveAccrual_Monthly_IncreasesBalanceForActiveEmployees_NotTerminated()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();

        var lt = new LeaveType { TenantId = tenantId, Code = "AL", NameEn = "Annual Leave", IsActive = true };
        var active = new Employee
        {
            TenantId = tenantId, EmployeeCode = "EMP-ACR-001", FullName = "Tariq Al-Omari",
            Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-1)
        };
        var terminated = new Employee
        {
            TenantId = tenantId, EmployeeCode = "EMP-ACR-002", FullName = "Reem Al-Mutlaq",
            Status = "Terminated", JoiningDate = DateTime.UtcNow.AddYears(-2)
        };
        db.LeaveTypes.Add(lt);
        db.Employees.AddRange(active, terminated);
        await db.SaveChangesAsync();

        // Policy: 21 days/year → 1.75 days/month
        db.LeavePolicies.Add(new LeavePolicy
        {
            TenantId = tenantId, Name = "Standard AL", LeaveTypeId = lt.Id,
            AnnualEntitlementDays = 21, AccrualMethod = "Monthly", Status = "Active"
        });
        await db.SaveChangesAsync();

        var svc = new LeaveService(db, new NullApprovalPolicyService());
        await svc.AccrueMonthlyAsync(tenantId, CancellationToken.None);

        var activeBal = await db.EmployeeLeaveBalances
            .FirstOrDefaultAsync(b => b.TenantId == tenantId && b.EmployeeId == active.Id);
        activeBal.Should().NotBeNull("accrual must create a balance record for active employee");
        activeBal!.Accrued.Should().Be(Math.Round(21m / 12, 4), "21 days / 12 months = 1.75");

        var terminatedBal = await db.EmployeeLeaveBalances
            .FirstOrDefaultAsync(b => b.TenantId == tenantId && b.EmployeeId == terminated.Id);
        terminatedBal.Should().BeNull("terminated employees must not receive accruals");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 3. LOAN LIFECYCLE: Apply (no-approval type) → Installments Generated
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Loan_AutoApproved_GeneratesCorrectInstallmentSchedule()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();

        var lt = new LoanType
        {
            TenantId = tenantId, Code = "PERSONAL", NameEn = "Personal Loan",
            MaxAmount = 50000, MaxInstallments = 24, RequiresApproval = false, IsActive = true
        };
        db.LoanTypes.Add(lt);
        await db.SaveChangesAsync();

        var ctrl = CreateLoansController(db, tenantId);
        var empGuid = Guid.NewGuid();
        var result = await ctrl.CreateLoan(
            new CreateLoanRequest(empGuid, "Noura Al-Ghamdi", lt.Id, 12000, 6, "Emergency", null),
            CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>("auto-approved loan must return 200");

        var loans = await db.EmployeeLoans.Where(l => l.TenantId == tenantId).ToListAsync();
        loans.Should().HaveCount(1);
        loans[0].Status.Should().Be("Active", "no-approval type activates immediately");
        loans[0].OutstandingBalance.Should().Be(12000);

        var installments = await db.LoanInstallments
            .Where(i => i.LoanId == loans[0].Id)
            .OrderBy(i => i.InstallmentNumber)
            .ToListAsync();
        installments.Should().HaveCount(6, "6 installments for 6-month repayment");
        installments.Should().AllSatisfy(i => i.AmountDue.Should().Be(2000m), "12000 / 6 = 2000 each");
        installments[0].InstallmentNumber.Should().Be(1);
        installments[5].InstallmentNumber.Should().Be(6);
        installments.Should().AllSatisfy(i => i.Status.Should().Be("Pending"));
    }

    [Fact]
    public async Task Loan_MarkInstallmentPaid_DecreasesOutstandingBalance()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();

        var lt = new LoanType
        {
            TenantId = tenantId, Code = "EMER", NameEn = "Emergency Loan",
            MaxAmount = 20000, MaxInstallments = 12, RequiresApproval = false, IsActive = true
        };
        db.LoanTypes.Add(lt);
        await db.SaveChangesAsync();

        var ctrl = CreateLoansController(db, tenantId);
        await ctrl.CreateLoan(
            new CreateLoanRequest(Guid.NewGuid(), "Mohammed Al-Harbi", lt.Id, 6000, 3, null, null),
            CancellationToken.None);

        var loan = await db.EmployeeLoans.FirstAsync(l => l.TenantId == tenantId);
        var inst = await db.LoanInstallments.FirstAsync(i => i.LoanId == loan.Id && i.InstallmentNumber == 1);

        var payResult = await ctrl.MarkInstallmentPaid(
            loan.Id, inst.Id,
            new PayInstallmentRequest(2000, DateOnly.FromDateTime(DateTime.Today), null),
            CancellationToken.None);

        payResult.Should().BeOfType<OkObjectResult>();

        var updatedLoan = await db.EmployeeLoans.FindAsync(loan.Id);
        updatedLoan!.OutstandingBalance.Should().Be(4000, "6000 - 2000 = 4000 remaining");
        updatedLoan.TotalRepaid.Should().Be(2000);

        var paidInst = await db.LoanInstallments.FindAsync(inst.Id);
        paidInst!.Status.Should().Be("Paid");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 4. OVERTIME LIFECYCLE: Submit → Approve → OvertimePayrollImpact Created
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OvertimeRequest_WhenApproved_CreatesPayrollImpact()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();

        var emp = new Employee
        {
            TenantId = tenantId, EmployeeCode = "EMP-OT-001", FullName = "Ahmed Al-Rashidi",
            Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-2), Salary = 9000
        };
        var policy = new OvertimePolicy
        {
            TenantId = tenantId, Code = "OT-STD", Name = "Standard OT",
            HourlyRateBasis = "BasicSalary", StandardMonthlyHours = 240,
            MinimumMinutes = 30, MaximumMinutesPerDay = 240, MonthlyCapMinutes = 3600,
            RequiresApproval = true
        };
        db.Employees.Add(emp);
        db.OvertimePolicies.Add(policy);
        await db.SaveChangesAsync();

        db.OvertimeMultipliers.AddRange(
            new OvertimeMultiplier { TenantId = tenantId, OvertimePolicyId = policy.Id, DayCategory = "RegularDay", Multiplier = 1.25m },
            new OvertimeMultiplier { TenantId = tenantId, OvertimePolicyId = policy.Id, DayCategory = "Weekend", Multiplier = 1.5m },
            new OvertimeMultiplier { TenantId = tenantId, OvertimePolicyId = policy.Id, DayCategory = "PublicHoliday", Multiplier = 2.0m }
        );

        // Use a Wednesday (guaranteed RegularDay — not Friday/Saturday/public holiday)
        var wednesday = DateOnly.FromDateTime(GetNextWeekday(DayOfWeek.Wednesday));
        var otRequest = new OvertimeRequest
        {
            TenantId = tenantId, EmployeeId = emp.Id, EmployeeName = emp.FullName,
            OvertimePolicyId = policy.Id, WorkDate = wednesday,
            StartTimeUtc = DateTime.UtcNow.Date.AddHours(18),
            EndTimeUtc = DateTime.UtcNow.Date.AddHours(20),
            RequestedMinutes = 120, Reason = "Quarterly close",
            Status = "PendingManager"
        };
        db.OvertimeRequests.Add(otRequest);
        await db.SaveChangesAsync();

        var ctrl = CreateOvertimeController(db, tenantId);
        var result = await ctrl.Approve(otRequest.Id,
            new OvertimeDecisionRequest(120, "OK"), CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>("approve must return 200");

        var impacts = await db.OvertimePayrollImpacts
            .Where(i => i.OvertimeRequestId == otRequest.Id).ToListAsync();
        impacts.Should().HaveCount(1, "exactly one payroll impact per approved OT");
        impacts[0].Hours.Should().Be(2.0m, "120 minutes = 2 hours");
        impacts[0].ApprovedMultiplier.Should().Be(1.25m, "Wednesday = RegularDay → 1.25x");
        impacts[0].EmployeeId.Should().Be(emp.Id);
        impacts[0].Status.Should().Be("PendingPayroll", "impact awaits next payroll run");

        var updated = await db.OvertimeRequests.FindAsync(otRequest.Id);
        updated!.Status.Should().Be("Approved");
        updated.ApprovedMinutes.Should().Be(120);
    }

    [Fact]
    public async Task OvertimeRequest_WhenRejected_NoPayrollImpact()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();

        var emp = new Employee
        {
            TenantId = tenantId, EmployeeCode = "EMP-OT-002", FullName = "Fatima Al-Zahrani",
            Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-1)
        };
        var policy = new OvertimePolicy
        {
            TenantId = tenantId, Code = "OT-STD2", Name = "Standard OT 2",
            HourlyRateBasis = "BasicSalary", StandardMonthlyHours = 240,
            MinimumMinutes = 60, MaximumMinutesPerDay = 480, MonthlyCapMinutes = 7200,
            RequiresApproval = true
        };
        db.Employees.Add(emp); db.OvertimePolicies.Add(policy);
        await db.SaveChangesAsync();

        db.OvertimeMultipliers.Add(new OvertimeMultiplier
        {
            TenantId = tenantId, OvertimePolicyId = policy.Id, DayCategory = "RegularDay", Multiplier = 1.25m
        });

        var otRequest = new OvertimeRequest
        {
            TenantId = tenantId, EmployeeId = emp.Id, EmployeeName = emp.FullName,
            OvertimePolicyId = policy.Id,
            WorkDate = DateOnly.FromDateTime(GetNextWeekday(DayOfWeek.Tuesday)),
            StartTimeUtc = DateTime.UtcNow.Date.AddHours(18), EndTimeUtc = DateTime.UtcNow.Date.AddHours(21),
            RequestedMinutes = 180, Reason = "Delivery", Status = "PendingManager"
        };
        db.OvertimeRequests.Add(otRequest);
        await db.SaveChangesAsync();

        var ctrl = CreateOvertimeController(db, tenantId);
        await ctrl.Reject(otRequest.Id, new OvertimeDecisionRequest(0, "Not enough evidence"), CancellationToken.None);

        var impacts = await db.OvertimePayrollImpacts
            .Where(i => i.OvertimeRequestId == otRequest.Id).ToListAsync();
        impacts.Should().BeEmpty("rejection must not create any payroll impact");

        var req = await db.OvertimeRequests.FindAsync(otRequest.Id);
        req!.Status.Should().Be("Rejected");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 5. HR REQUEST LIFECYCLE: Create → Comment → Status → Dashboard
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task HrRequest_FullLifecycle_CommentStatusDashboard()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();

        var emp = new Employee
        {
            TenantId = tenantId, EmployeeCode = "EMP-HR-001", FullName = "Omar Abdullah",
            Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-1)
        };
        db.Employees.Add(emp);
        await db.SaveChangesAsync();

        var category = new HRRequestCategory
        {
            TenantId = tenantId, Name = "Payslip Copy", Code = "PAYSLIP",
            DefaultSlaHours = 24, IsActive = true
        };
        db.HRRequestCategories.Add(category);
        await db.SaveChangesAsync();

        var ctrl = CreateHrRequestController(db, tenantId, Guid.NewGuid());

        // Create
        var createResult = await ctrl.Create(new CreateHRRequestBody(
            emp.Id, category.Id, null, "Need Jan 2026 payslip", "For bank loan purposes", "Normal"),
            CancellationToken.None);
        createResult.Should().BeOfType<CreatedResult>("creation must return 201");
        var hr = ((CreatedResult)createResult).Value as HRRequest;
        hr.Should().NotBeNull();
        hr!.Status.Should().Be("Open");

        // Comment
        var commentResult = await ctrl.AddComment(hr.Id,
            new AddCommentRequest(emp.Id, "Please find attached payslip copy."),
            CancellationToken.None);
        commentResult.Should().BeOfType<CreatedResult>("comment must return 201");
        var comments = await db.HRRequestComments.Where(c => c.HRRequestId == hr.Id).ToListAsync();
        comments.Should().HaveCount(1);
        comments[0].Comment.Should().Contain("payslip");

        // Status: Open → InProgress
        await ctrl.UpdateStatus(hr.Id, new UpdateHRStatusRequest("InProgress"), CancellationToken.None);
        var inProg = await db.HRRequests.FindAsync(hr.Id);
        inProg!.Status.Should().Be("InProgress");

        // Status: → Resolved
        await ctrl.UpdateStatus(hr.Id, new UpdateHRStatusRequest("Resolved"), CancellationToken.None);

        // Dashboard aggregation
        var dashResult = await ctrl.Dashboard(CancellationToken.None);
        dashResult.Should().BeOfType<OkObjectResult>();
        var json = System.Text.Json.JsonSerializer.Serialize(((OkObjectResult)dashResult).Value);
        json.Should().Contain("\"resolved\":1", "dashboard must reflect 1 resolved request");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 6. RECRUITMENT: Offer Accept → Application Stage = Hired
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Offer_WhenAccepted_AdvancesApplicationToHiredStage()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();

        var candidate = new Candidate
        {
            TenantId = tenantId, FirstName = "Hana", LastName = "Al-Farsi", Email = "hana@test.com", Status = "Active"
        };
        var jobOpening = new JobOpening
        {
            TenantId = tenantId, Title = "Finance Manager", Status = "Open",
            DepartmentName = "Finance", HeadCount = 1,
        };
        db.Candidates.Add(candidate); db.JobOpenings.Add(jobOpening);
        await db.SaveChangesAsync();

        var application = new JobApplication
        {
            TenantId = tenantId, CandidateId = candidate.Id, JobOpeningId = jobOpening.Id,
            Stage = "OfferExtended", StageOrder = 5, Status = "Active",
        };
        db.JobApplications.Add(application);
        await db.SaveChangesAsync();

        var offer = new OfferLetter
        {
            TenantId = tenantId, ApplicationId = application.Id,
            CandidateName = "Hana Al-Farsi", OfferedJobTitle = "Finance Manager", Status = "Sent",
        };
        db.OfferLetters.Add(offer);
        await db.SaveChangesAsync();

        var ctrl = CreateOffersController(db, tenantId);
        var result = await ctrl.Accept(offer.Id, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>("accepting offer must return 200");

        var updatedOffer = await db.OfferLetters.FindAsync(offer.Id);
        updatedOffer!.Status.Should().Be("Accepted");
        updatedOffer.AcceptedAtUtc.Should().NotBeNull();

        var updatedApp = await db.JobApplications.FindAsync(application.Id);
        updatedApp!.Stage.Should().Be("Hired");
        updatedApp.HiredAtUtc.Should().NotBeNull();

        var timeline = await db.ApplicationEvents
            .Where(t => t.ApplicationId == application.Id && t.EventType == "OfferAccepted")
            .ToListAsync();
        timeline.Should().HaveCount(1, "OfferAccepted event must be recorded");
    }

    [Fact]
    public async Task Offer_WhenDeclined_ApplicationDoesNotAdvance()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();

        var candidate = new Candidate
        {
            TenantId = tenantId, FirstName = "Sami", LastName = "Al-Qahtani", Email = "sami@test.com", Status = "Active"
        };
        var jobOpening = new JobOpening
        {
            TenantId = tenantId, Title = "Senior Engineer", Status = "Open",
            DepartmentName = "Engineering", HeadCount = 1,
        };
        db.Candidates.Add(candidate); db.JobOpenings.Add(jobOpening);
        await db.SaveChangesAsync();

        var application = new JobApplication
        {
            TenantId = tenantId, CandidateId = candidate.Id, JobOpeningId = jobOpening.Id,
            Stage = "OfferExtended", StageOrder = 5, Status = "Active",
        };
        db.JobApplications.Add(application);
        await db.SaveChangesAsync();

        var offer = new OfferLetter
        {
            TenantId = tenantId, ApplicationId = application.Id,
            CandidateName = "Sami Al-Qahtani", OfferedJobTitle = "Senior Engineer", Status = "Sent",
        };
        db.OfferLetters.Add(offer);
        await db.SaveChangesAsync();

        var ctrl = CreateOffersController(db, tenantId);
        var result = await ctrl.Decline(offer.Id, new DeclineOfferRequest("Accepted counter offer"), CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>();

        var updatedOffer = await db.OfferLetters.FindAsync(offer.Id);
        updatedOffer!.Status.Should().Be("Declined");
        updatedOffer.DeclinedAtUtc.Should().NotBeNull();

        var updatedApp = await db.JobApplications.FindAsync(application.Id);
        updatedApp!.Stage.Should().NotBe("Hired", "declined offer must not hire the candidate");
        updatedApp.HiredAtUtc.Should().BeNull();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 7. TENANT ISOLATION: Tenant B cannot see Tenant A's HR requests
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task HrRequests_TenantIsolation_TenantBCannotSeeTenantAData()
    {
        await using var db = CreateDb();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        db.HRRequests.Add(new HRRequest
        {
            TenantId = tenantA, Subject = "Tenant A Confidential",
            Description = "Secret HR request", Status = "Open"
        });
        await db.SaveChangesAsync();

        var ctrlB = CreateHrRequestController(db, tenantB, Guid.NewGuid());
        var result = await ctrlB.List(null, null, null, 1, 50, CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>();
        var json = System.Text.Json.JsonSerializer.Serialize(((OkObjectResult)result).Value);
        json.Should().NotContain("Tenant A Confidential",
            "tenant B must never see tenant A's HR requests");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static ZayraDbContext CreateDb()
    {
        var opts = new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ZayraDbContext(opts);
    }

    private static ClaimsPrincipal MakePrincipal(Guid tenantId, Guid userId, string role = "Admin")
        => new(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Name, "Test User"),
            new Claim(ClaimTypes.Role, role),
            new Claim("permission", "employees.write"),
        }, "Test"));

    private static LoansController CreateLoansController(ZayraDbContext db, Guid tenantId)
    {
        var ctrl = new LoansController(db, new FakeDataScopeService());
        ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = MakePrincipal(tenantId, Guid.NewGuid()) }
        };
        return ctrl;
    }

    private static OvertimeController CreateOvertimeController(ZayraDbContext db, Guid tenantId, string role = "Admin")
    {
        var ctrl = new OvertimeController(db, new FakeDataScopeService());
        ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = MakePrincipal(tenantId, Guid.NewGuid(), role) }
        };
        return ctrl;
    }

    private static HRRequestCenterController CreateHrRequestController(ZayraDbContext db, Guid tenantId, Guid userId)
    {
        var ctrl = new HRRequestCenterController(db, new FakeDataScopeService());
        ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = MakePrincipal(tenantId, userId) }
        };
        return ctrl;
    }

    private static OffersController CreateOffersController(ZayraDbContext db, Guid tenantId)
    {
        var ctrl = new OffersController(db, new NullLetterService());
        ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = MakePrincipal(tenantId, Guid.NewGuid()) }
        };
        return ctrl;
    }

    private static DateTime GetNextWeekday(DayOfWeek day)
    {
        var date = DateTime.Today.AddDays(7);
        while (date.DayOfWeek != day)
            date = date.AddDays(1);
        return date;
    }
}

// ── Fakes ─────────────────────────────────────────────────────────────────────

file sealed class FakeDataScopeService : IDataScopeService
{
    public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
        => Task.FromResult(new DataScope { Level = DataScopeLevel.Organization });
}

file sealed class NullLetterService : ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(PayslipData d, CancellationToken ct) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(LetterData d, CancellationToken ct) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(LetterData d, CancellationToken ct) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData d, CancellationToken ct) => Task.FromResult(Array.Empty<byte>());
}

file sealed class NullApprovalPolicyService : IApprovalPolicyService
{
    public Task<ResolvedApprovalPolicy?> ResolveAsync(Guid tenantId, int employeeId, string workflowType, CancellationToken ct)
        => Task.FromResult<ResolvedApprovalPolicy?>(null);
}
