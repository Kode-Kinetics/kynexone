using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Controllers;
using Zayra.Api.Controllers.Leave;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Leave;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class HrTransactionalInvariantPostgresTests
{
    private readonly PostgresFixture _fixture;

    public HrTransactionalInvariantPostgresTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task GenericApproval_TwoContextsDecideSameStep_ExactlyOneDecisionWins()
    {
        Guid tenantId;
        Guid requestId;
        var requesterId = Guid.NewGuid();
        await using (var seed = _fixture.CreateDb())
        {
            tenantId = await PostgresFixture.SeedMinimalTenant(seed);
            var workflow = new ApprovalWorkflow
            {
                TenantId = tenantId,
                Code = $"RACE-{Guid.NewGuid():N}"[..16],
                Name = "Approval race",
                EntityName = "RaceEntity"
            };
            workflow.Steps.Add(new ApprovalWorkflowStep
            {
                TenantId = tenantId,
                StepOrder = 1,
                StepName = "Manager",
                ApproverRole = "Manager",
                IsFinalStep = true
            });
            seed.ApprovalWorkflows.Add(workflow);
            await seed.SaveChangesAsync();
            var created = await new ApprovalWorkflowService(seed, new AuditService(seed)).CreateRequestAsync(
                tenantId,
                new CreateApprovalRequest(workflow.Id, "RaceEntity", Guid.NewGuid().ToString(), "Race"),
                new RequestContext("127.0.0.1", "race", requesterId, tenantId, ["Employee"], []),
                CancellationToken.None);
            requestId = created.Id;
        }

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<Exception?> Decide(Guid approverId)
        {
            await using var db = _fixture.CreateDb();
            var service = new ApprovalWorkflowService(db, new AuditService(db));
            await gate.Task;
            try
            {
                await service.DecideAsync(tenantId, requestId,
                    new ApprovalDecisionRequest("Approve", "concurrent"),
                    new RequestContext("127.0.0.1", "race", approverId, tenantId, ["Manager"], []),
                    CancellationToken.None);
                return null;
            }
            catch (Exception ex) { return ex; }
        }

        var contenders = new[] { Decide(Guid.NewGuid()), Decide(Guid.NewGuid()) };
        gate.SetResult();
        var results = await Task.WhenAll(contenders);
        results.Count(x => x is null).Should().Be(1);
        results.Count(x => x is InvalidOperationException).Should().Be(1);

        await using var verify = _fixture.CreateDb();
        (await verify.ApprovalDecisions.CountAsync(x => x.TenantId == tenantId
            && x.ApprovalRequestId == requestId && x.StepOrder == 1)).Should().Be(1);
        var approval = await verify.ApprovalRequests.SingleAsync(x => x.Id == requestId);
        approval.Status.Should().Be("Approved");
        approval.DecisionVersion.Should().Be(1);
    }

    [Fact]
    public async Task OvertimeFinalApproval_TwoContexts_CreateOneCalculationAndPayrollImpact()
    {
        var seeded = await SeedApprovedCandidateOvertimeAsync(status: "PendingHR");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<IActionResult?> Approve()
        {
            await using var db = _fixture.CreateDb();
            var controller = OvertimeController(db, seeded.TenantId, Guid.NewGuid());
            await gate.Task;
            var response = await controller.Approve(seeded.RequestId,
                new OvertimeDecisionRequest(120, "final race"), CancellationToken.None);
            return response;
        }

        var contenders = new[] { Approve(), Approve() };
        gate.SetResult();
        var results = await Task.WhenAll(contenders);
        results.Count(x => x is OkObjectResult).Should().Be(1);
        results.Count(x => x is ConflictObjectResult or BadRequestObjectResult).Should().Be(1);

        await using var verify = _fixture.CreateDb();
        (await verify.OvertimeApprovals.CountAsync(x => x.OvertimeRequestId == seeded.RequestId
            && x.ApprovalLevel == "Final")).Should().Be(1);
        (await verify.OvertimeCalculations.CountAsync(x => x.OvertimeRequestId == seeded.RequestId)).Should().Be(1);
        (await verify.OvertimePayrollImpacts.CountAsync(x => x.OvertimeRequestId == seeded.RequestId)).Should().Be(1);
        var request = await verify.OvertimeRequests.SingleAsync(x => x.Id == seeded.RequestId);
        request.Status.Should().Be("Approved");
        request.DecisionVersion.Should().Be(1);
    }

    [Fact]
    public async Task MonthlyAccrual_TwoContextsReplaySameTenantMonth_CreditsOnce()
    {
        Guid tenantId;
        int employeeId;
        Guid leaveTypeId;
        await using (var seed = _fixture.CreateDb())
        {
            tenantId = await PostgresFixture.SeedMinimalTenant(seed);
            var company = new Company
            {
                TenantId = tenantId,
                LegalNameEn = "Accrual Race Co",
                CountryCode = "SAU"
            };
            var leaveType = new LeaveType
            {
                TenantId = tenantId,
                Code = $"AL-{Guid.NewGuid():N}"[..16],
                NameEn = "Annual",
                IsActive = true
            };
            seed.AddRange(company, leaveType);
            await seed.SaveChangesAsync();
            var employee = new Employee
            {
                TenantId = tenantId,
                CompanyId = company.Id,
                EmployeeCode = $"ACC-{Guid.NewGuid():N}"[..18],
                FullName = "Concurrent Accrual",
                Status = "Active",
                JoiningDate = DateTime.UtcNow.AddYears(-2)
            };
            seed.Employees.Add(employee);
            seed.LeavePolicies.Add(new LeavePolicy
            {
                TenantId = tenantId,
                CompanyId = company.Id,
                LeaveTypeId = leaveType.Id,
                Name = "Monthly annual",
                Status = "Active",
                AccrualMethod = "Monthly",
                AnnualEntitlementDays = 24m
            });
            await seed.SaveChangesAsync();
            employeeId = employee.Id;
            leaveTypeId = leaveType.Id;
        }

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Accrue()
        {
            await using var db = _fixture.CreateDb();
            await gate.Task;
            await new LeaveService(db, new ApprovalPolicyService(db))
                .AccrueMonthlyAsync(tenantId, CancellationToken.None);
        }

        var contenders = new[] { Accrue(), Accrue() };
        gate.SetResult();
        await Task.WhenAll(contenders);

        await using var verify = _fixture.CreateDb();
        (await verify.LeaveBalanceTransactions.CountAsync(x => x.TenantId == tenantId
            && x.EmployeeId == employeeId && x.LeaveTypeId == leaveTypeId
            && x.TransactionType == "Accrual")).Should().Be(1);
        (await verify.EmployeeLeaveBalances.SingleAsync(x => x.TenantId == tenantId
            && x.EmployeeId == employeeId && x.LeaveTypeId == leaveTypeId))
            .Accrued.Should().Be(2m);
    }

    [Fact]
    public async Task OvertimeCompOffConversion_TwoContexts_CreateOneConversionAndCredit()
    {
        var seeded = await SeedApprovedCandidateOvertimeAsync(status: "Approved", allowCompOff: true);
        await using (var update = _fixture.CreateDb())
        {
            var request = await update.OvertimeRequests.SingleAsync(x => x.Id == seeded.RequestId);
            request.ApprovedMinutes = 120;
            await update.SaveChangesAsync();
        }
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<IActionResult?> Convert()
        {
            await using var db = _fixture.CreateDb();
            var controller = OvertimeController(db, seeded.TenantId, Guid.NewGuid());
            await gate.Task;
            var response = await controller.CreateCompOffConversion(
                new CompOffConversionRequest(seeded.RequestId, 1m), CancellationToken.None);
            return response.Result;
        }

        var contenders = new[] { Convert(), Convert() };
        gate.SetResult();
        var results = await Task.WhenAll(contenders);
        results.Count(x => x is CreatedResult or CreatedAtActionResult).Should().Be(1);
        results.Count(x => x is ConflictObjectResult).Should().Be(1);

        await using var verify = _fixture.CreateDb();
        var conversion = await verify.OvertimeCompOffConversions.SingleAsync(x =>
            x.OvertimeRequestId == seeded.RequestId);
        var credit = await verify.CompOffCredits.SingleAsync(x =>
            x.OvertimeCompOffConversionId == conversion.Id);
        credit.DaysEarned.Should().Be(1m);
    }

    [Fact]
    public async Task CompOffUse_TwoContextsCannotOverspendObservedRemainder()
    {
        Guid tenantId;
        Guid creditId;
        await using (var seed = _fixture.CreateDb())
        {
            tenantId = await PostgresFixture.SeedMinimalTenant(seed);
            var employee = new Employee
            {
                TenantId = tenantId,
                EmployeeCode = $"CO-{Guid.NewGuid():N}"[..16],
                FullName = "Comp Off Race",
                Status = "Active",
                JoiningDate = DateTime.UtcNow.AddYears(-1)
            };
            seed.Employees.Add(employee);
            await seed.SaveChangesAsync();
            var credit = new CompOffCredit
            {
                TenantId = tenantId,
                EmployeeId = employee.Id,
                EmployeeName = employee.FullName,
                WorkedDate = DateOnly.FromDateTime(DateTime.UtcNow),
                HoursWorked = 8m,
                DaysEarned = 1m,
                Status = "Approved"
            };
            seed.CompOffCredits.Add(credit);
            await seed.SaveChangesAsync();
            creditId = credit.Id;
        }

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<IActionResult> Use()
        {
            await using var db = _fixture.CreateDb();
            var controller = CompOffController(db, tenantId, Guid.NewGuid());
            await gate.Task;
            return await controller.Use(creditId,
                new UseCompOffRequest(0.75m, null, Guid.NewGuid()), CancellationToken.None);
        }

        var contenders = new[] { Use(), Use() };
        gate.SetResult();
        var results = await Task.WhenAll(contenders);
        results.Count(x => x is OkObjectResult).Should().Be(1);
        results.Count(x => x is ConflictObjectResult or BadRequestObjectResult).Should().Be(1);

        await using var verify = _fixture.CreateDb();
        (await verify.CompOffUsages.Where(x => x.CompOffCreditId == creditId)
            .SumAsync(x => x.DaysUsed)).Should().Be(0.75m);
        (await verify.CompOffCredits.SingleAsync(x => x.Id == creditId))
            .UsageVersion.Should().Be(1);
    }

    [Fact]
    public async Task LeaveEncashmentPayrollApproval_TwoContextsCreateOneConsumableArtifact()
    {
        var seeded = await SeedHrApprovedEncashmentAsync();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<IActionResult> Approve()
        {
            await using var db = _fixture.CreateDb();
            var controller = EncashmentController(db, seeded.TenantId, Guid.NewGuid());
            await gate.Task;
            return await controller.PayrollApprove(seeded.RequestId,
                new EncashmentDecisionRequest("concurrent payroll approval", seeded.RunId),
                CancellationToken.None);
        }

        var contenders = new[] { Approve(), Approve() };
        gate.SetResult();
        var results = await Task.WhenAll(contenders);
        results.Count(x => x is OkObjectResult).Should().Be(1);
        results.Count(x => x is ConflictObjectResult or BadRequestObjectResult).Should().Be(1);

        await using var verify = _fixture.CreateDb();
        var request = await verify.LeaveEncashmentRequests.SingleAsync(x => x.Id == seeded.RequestId);
        request.Status.Should().Be(LeaveEncashmentStatuses.PayrollApproved);
        request.PayrollRunId.Should().Be(seeded.RunId);
        request.DecisionVersion.Should().Be(1);
        var adjustment = await verify.PayrollAdjustments.SingleAsync(x =>
            x.TenantId == seeded.TenantId && x.SourceType == PayrollAdjustmentSources.LeaveEncashment
            && x.SourceId == seeded.RequestId);
        request.PayrollAdjustmentId.Should().Be(adjustment.Id);
        adjustment.PayrollRunId.Should().Be(seeded.RunId);
        adjustment.Status.Should().Be("Approved");
        (await verify.LeaveBalanceTransactions.CountAsync(x => x.TenantId == seeded.TenantId
            && x.Reference == seeded.RequestId.ToString() && x.TransactionType == "Encashed")).Should().Be(1);
        var balance = await verify.EmployeeLeaveBalances.SingleAsync(x => x.TenantId == seeded.TenantId
            && x.EmployeeId == seeded.EmployeeId && x.LeaveTypeId == seeded.LeaveTypeId);
        balance.Pending.Should().Be(0m);
        balance.Encashed.Should().Be(2m);

        await using var replayDb = _fixture.CreateDb();
        var replay = await EncashmentController(replayDb, seeded.TenantId, Guid.NewGuid()).PayrollApprove(
            seeded.RequestId, new EncashmentDecisionRequest("replay", seeded.RunId), CancellationToken.None);
        replay.Should().BeOfType<BadRequestObjectResult>();
        (await replayDb.PayrollAdjustments.CountAsync(x => x.SourceId == seeded.RequestId)).Should().Be(1);
    }

    [Fact]
    public async Task LeaveEncashmentPayrollApproval_RejectsWrongCompanyAndLockedRun()
    {
        var wrongCompany = await SeedHrApprovedEncashmentAsync();
        Guid wrongCompanyRunId;
        await using (var db = _fixture.CreateDb())
        {
            var otherCompany = new Company
            {
                TenantId = wrongCompany.TenantId,
                LegalNameEn = "Wrong legal entity",
                CountryCode = "SAU",
                DefaultCurrency = "SAR",
                IsActive = true
            };
            db.Companies.Add(otherCompany);
            await db.SaveChangesAsync();
            var run = new PayrollRun
            {
                TenantId = wrongCompany.TenantId,
                CompanyId = otherCompany.Id,
                Year = 2026,
                Month = 9,
                Status = "Draft",
                RunType = PayrollRunTypes.Regular
            };
            db.PayrollRuns.Add(run);
            await db.SaveChangesAsync();
            wrongCompanyRunId = run.Id;
        }

        await using (var db = _fixture.CreateDb())
        {
            var response = await EncashmentController(db, wrongCompany.TenantId, Guid.NewGuid())
                .PayrollApprove(wrongCompany.RequestId,
                    new EncashmentDecisionRequest("wrong company", wrongCompanyRunId), CancellationToken.None);
            response.Should().BeOfType<BadRequestObjectResult>();
        }

        var locked = await SeedHrApprovedEncashmentAsync(runLocked: true);
        await using (var db = _fixture.CreateDb())
        {
            var response = await EncashmentController(db, locked.TenantId, Guid.NewGuid())
                .PayrollApprove(locked.RequestId,
                    new EncashmentDecisionRequest("locked", locked.RunId), CancellationToken.None);
            response.Should().BeOfType<ConflictObjectResult>();
        }

        await using var verify = _fixture.CreateDb();
        (await verify.PayrollAdjustments.CountAsync(x =>
            x.SourceId == wrongCompany.RequestId || x.SourceId == locked.RequestId)).Should().Be(0);
    }

    [Fact]
    public async Task LeaveEncashmentVoid_PreservesArtifactAndAppendsReversalWitness()
    {
        var seeded = await SeedHrApprovedEncashmentAsync();
        await using (var approveDb = _fixture.CreateDb())
        {
            var approved = await EncashmentController(approveDb, seeded.TenantId, Guid.NewGuid())
                .PayrollApprove(seeded.RequestId,
                    new EncashmentDecisionRequest("approve", seeded.RunId), CancellationToken.None);
            approved.Should().BeOfType<OkObjectResult>();
        }

        await using (var voidDb = _fixture.CreateDb())
        {
            var voided = await EncashmentController(voidDb, seeded.TenantId, Guid.NewGuid())
                .Void(seeded.RequestId, new EncashmentVoidRequest("Payroll approver correction"),
                    CancellationToken.None);
            voided.Should().BeOfType<OkObjectResult>();
        }

        await using var verify = _fixture.CreateDb();
        var request = await verify.LeaveEncashmentRequests.SingleAsync(x => x.Id == seeded.RequestId);
        request.Status.Should().Be(LeaveEncashmentStatuses.Voided);
        request.VoidReason.Should().Be("Payroll approver correction");
        request.PayrollAdjustmentId.Should().NotBeNull();
        request.PayrollRunId.Should().Be(seeded.RunId);
        (await verify.PayrollAdjustments.SingleAsync(x => x.Id == request.PayrollAdjustmentId)).Status
            .Should().Be("Voided");
        (await verify.LeaveBalanceTransactions.CountAsync(x => x.Reference == seeded.RequestId.ToString()
            && x.TransactionType == "Encashed")).Should().Be(1);
        (await verify.LeaveBalanceTransactions.CountAsync(x => x.Reference == seeded.RequestId.ToString()
            && x.TransactionType == "EncashmentVoided")).Should().Be(1);
        var balance = await verify.EmployeeLeaveBalances.SingleAsync(x => x.EmployeeId == seeded.EmployeeId
            && x.LeaveTypeId == seeded.LeaveTypeId && x.Year == 2026);
        balance.Pending.Should().Be(0m);
        balance.Encashed.Should().Be(0m);
        balance.Available.Should().Be(12m);

        await using var replayDb = _fixture.CreateDb();
        var replay = await EncashmentController(replayDb, seeded.TenantId, Guid.NewGuid()).Void(
            seeded.RequestId, new EncashmentVoidRequest("repeat correction"), CancellationToken.None);
        replay.Should().BeOfType<ConflictObjectResult>();
        (await replayDb.LeaveBalanceTransactions.CountAsync(x => x.Reference == seeded.RequestId.ToString()
            && x.TransactionType == "EncashmentVoided")).Should().Be(1);
    }

    private async Task<(Guid TenantId, Guid RequestId)> SeedApprovedCandidateOvertimeAsync(
        string status, bool allowCompOff = false)
    {
        await using var seed = _fixture.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(seed);
        var employee = new Employee
        {
            TenantId = tenantId,
            EmployeeCode = $"OT-{Guid.NewGuid():N}"[..16],
            FullName = "Overtime Race",
            Status = "Active",
            JoiningDate = DateTime.UtcNow.AddYears(-1),
            Salary = 12_000m
        };
        var policy = new OvertimePolicy
        {
            TenantId = tenantId,
            Code = $"OTP-{Guid.NewGuid():N}"[..16],
            Name = "Race policy",
            HourlyRateBasis = "BasicSalary",
            StandardMonthlyHours = 240,
            AllowCompOffConversion = allowCompOff
        };
        seed.AddRange(employee, policy);
        await seed.SaveChangesAsync();
        var request = new OvertimeRequest
        {
            TenantId = tenantId,
            EmployeeId = employee.Id,
            EmployeeName = employee.FullName,
            OvertimePolicyId = policy.Id,
            WorkDate = new DateOnly(2026, 8, 12),
            StartTimeUtc = new DateTime(2026, 8, 12, 18, 0, 0, DateTimeKind.Utc),
            EndTimeUtc = new DateTime(2026, 8, 12, 20, 0, 0, DateTimeKind.Utc),
            RequestedMinutes = 120,
            Status = status
        };
        seed.OvertimeRequests.Add(request);
        await seed.SaveChangesAsync();
        return (tenantId, request.Id);
    }

    private async Task<(Guid TenantId, Guid CompanyId, int EmployeeId, Guid LeaveTypeId,
        Guid RequestId, Guid RunId)> SeedHrApprovedEncashmentAsync(bool runLocked = false)
    {
        await using var db = _fixture.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var company = new Company
        {
            TenantId = tenantId,
            LegalNameEn = "Encashment legal entity",
            CountryCode = "SAU",
            DefaultCurrency = "SAR",
            IsActive = true
        };
        var leaveType = new LeaveType
        {
            TenantId = tenantId,
            Code = $"ENC-{Guid.NewGuid():N}"[..16],
            NameEn = "Annual leave",
            IsActive = true
        };
        db.AddRange(company, leaveType);
        await db.SaveChangesAsync();
        var employee = new Employee
        {
            TenantId = tenantId,
            CompanyId = company.Id,
            EmployeeCode = $"ENC-{Guid.NewGuid():N}"[..18],
            FullName = "Encashment Race",
            Status = "Active",
            JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Salary = 12_000m
        };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        var run = new PayrollRun
        {
            TenantId = tenantId,
            CompanyId = company.Id,
            Year = 2026,
            Month = 8,
            Status = "Draft",
            RunType = PayrollRunTypes.Regular,
            LockedAtUtc = runLocked ? DateTime.UtcNow : null
        };
        var request = new LeaveEncashmentRequest
        {
            TenantId = tenantId,
            CompanyId = company.Id,
            EmployeeId = employee.Id,
            EmployeeName = employee.FullName,
            LeaveTypeId = leaveType.Id,
            LeaveTypeName = leaveType.NameEn,
            Year = 2026,
            DaysToEncash = 2m,
            AmountPerDay = 400m,
            TotalAmount = 800m,
            Currency = "SAR",
            Reason = "approved annual leave cash-out",
            Status = LeaveEncashmentStatuses.HRApproved
        };
        var balance = new EmployeeLeaveBalance
        {
            TenantId = tenantId,
            EmployeeId = employee.Id,
            EmployeeName = employee.FullName,
            LeaveTypeId = leaveType.Id,
            LeaveTypeName = leaveType.NameEn,
            Year = 2026,
            Entitled = 12m,
            Pending = 2m
        };
        db.AddRange(run, request, balance);
        await db.SaveChangesAsync();
        return (tenantId, company.Id, employee.Id, leaveType.Id, request.Id, run.Id);
    }

    private static OvertimeController OvertimeController(ZayraDbContext db, Guid tenantId, Guid userId)
    {
        var controller = new OvertimeController(
            db,
            new DataScopeService(db),
            new HrmHierarchyService(db, new AuditService(db)));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = HttpContext(tenantId, userId, "Admin")
        };
        return controller;
    }

    private static CompOffController CompOffController(ZayraDbContext db, Guid tenantId, Guid userId)
    {
        var controller = new CompOffController(db, new DataScopeService(db));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = HttpContext(tenantId, userId, "Admin")
        };
        return controller;
    }

    private static EncashmentController EncashmentController(ZayraDbContext db, Guid tenantId, Guid userId)
    {
        var controller = new EncashmentController(db, new UnrestrictedScope(), new NullStatutoryRules());
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = HttpContext(tenantId, userId, "Admin")
        };
        return controller;
    }

    private sealed class NullStatutoryRules : IStatutoryRuleReader
    {
        public Task<decimal?> GetDecimalAsync(string countryCode, string jurisdiction, string ruleKey,
            DateOnly effectiveDate, Guid? tenantId = null, CancellationToken ct = default) =>
            Task.FromResult<decimal?>(null);

        public Task<string?> GetStringAsync(string countryCode, string jurisdiction, string ruleKey,
            DateOnly effectiveDate, Guid? tenantId = null, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);
    }

    private sealed class UnrestrictedScope : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) =>
            Task.FromResult(new DataScope { Level = DataScopeLevel.Organization, AllowedEmployeeIds = null });
    }

    private static DefaultHttpContext HttpContext(Guid tenantId, Guid userId, string role)
    {
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Name, "Concurrency tester"),
            new Claim(ClaimTypes.Role, role)
        ], "Test"));
        return context;
    }
}
