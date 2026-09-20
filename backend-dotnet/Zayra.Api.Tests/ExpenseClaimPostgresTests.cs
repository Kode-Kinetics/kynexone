using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Application.Expenses;
using Zayra.Api.Controllers;
using Zayra.Api.Controllers.Expenses;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Expenses;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// W2-B — expense claims, proven end to end on PostgreSQL with the production retrying execution
/// strategy: F1 routing (one and two steps), category policy, the payroll money rail (a real
/// Process + Lock through <see cref="PayrollController"/>, which this stream does not modify), the
/// GL journal, and tenant / company / employee isolation.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class ExpenseClaimPostgresTests
{
    private readonly PostgresFixture _fx;
    public ExpenseClaimPostgresTests(PostgresFixture fx) => _fx = fx;

    // ══ Scenario ════════════════════════════════════════════════════════════════════════════════

    private sealed record Scenario(
        Guid TenantId, Guid CompanyId, int EmployeeId, Guid EmployeeUserId, int PeerEmployeeId, Guid PeerUserId,
        int ManagerEmployeeId, Guid ManagerUserId, Guid HrUserId);

    /// <param name="twoStep">Line manager (step 1) → HR Manager (step 2, final) instead of the provisioning default.</param>
    /// <param name="workflow">false ⇒ the tenant has NO ExpenseClaim workflow at all.</param>
    private async Task<Scenario> SeedAsync(bool twoStep = false, bool workflow = true)
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var company = new Company
        {
            TenantId = tenantId, LegalNameEn = $"Expense Co {suffix}", CountryCode = "SAU", Jurisdiction = "KSA-mainland",
            RegistrationNumber = $"EXP-{suffix}", DefaultCurrency = "SAR", IsActive = true,
            CreatedAtUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Companies.Add(company);
        await db.SaveChangesAsync();

        var managerUser = Guid.NewGuid();
        var manager = NewEmployee(tenantId, company.Id, $"MGR-{suffix}", managerUser, "Line Manager");
        db.Employees.Add(manager);
        await db.SaveChangesAsync();
        var employeeUser = Guid.NewGuid();
        var peerUser = Guid.NewGuid();
        var employee = NewEmployee(tenantId, company.Id, $"EE-{suffix}", employeeUser, "Claimant", manager.Id);
        var peer = NewEmployee(tenantId, company.Id, $"PEER-{suffix}", peerUser, "Peer", manager.Id);
        db.Employees.AddRange(employee, peer);
        await db.SaveChangesAsync();

        var structure = new SalaryStructure
        {
            TenantId = tenantId, Code = $"STR-{suffix}", Name = "Base", Currency = "SAR",
            EffectiveDate = new DateOnly(2024, 1, 1), IsActive = true,
        };
        db.SalaryStructures.Add(structure);
        await db.SaveChangesAsync();
        foreach (var e in new[] { manager, employee, peer })
        {
            db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
            {
                TenantId = tenantId, EmployeeId = e.Id, SalaryStructureId = structure.Id,
                BasicSalary = 10_000m, HousingAllowance = 2_000m, TransportAllowance = 1_000m,
                EffectiveDate = new DateOnly(2024, 1, 1), IsActive = true,
            });
            db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile
            {
                TenantId = tenantId, EmployeeId = e.Id, Iban = "SA4420000001234567891234",
                MolId = $"MOL-{e.EmployeeCode}", SalaryCurrency = "SAR",
            });
        }

        if (workflow)
        {
            var wf = new ApprovalWorkflow
            {
                TenantId = tenantId, Code = "EXPENSE-DEFAULT", Name = "Expense approval",
                EntityName = ExpenseClaimConstants.ApprovalEntityName, IsDefault = true, IsActive = true,
            };
            if (twoStep)
            {
                wf.Steps.Add(new ApprovalWorkflowStep { TenantId = tenantId, WorkflowId = wf.Id, StepOrder = 1, StepName = "Line Manager", ApproverType = "Manager", ApproverRole = "Manager" });
                wf.Steps.Add(new ApprovalWorkflowStep { TenantId = tenantId, WorkflowId = wf.Id, StepOrder = 2, StepName = "HR", ApproverType = "Role", ApproverRole = "HR Manager", IsFinalStep = true });
            }
            else
            {
                // Exactly the provisioning default (TenantProvisioningBundle: EXPENSE-DEFAULT, one HR step).
                wf.Steps.Add(new ApprovalWorkflowStep { TenantId = tenantId, WorkflowId = wf.Id, StepOrder = 1, StepName = "HR Approval", ApproverType = "HR", ApproverRole = "HR Manager", IsFinalStep = true });
            }
            db.ApprovalWorkflows.Add(wf);
        }

        // Categories with policy: TRAVEL capped at 1,000 per claim, receipt above 200; MEALS receipt above 50.
        var type = new MasterDataType { TenantId = tenantId, Code = ExpenseClaimConstants.CategoryMasterType, NameEn = "Expense Category", IsSystemDefined = true };
        db.MasterDataTypes.Add(type);
        db.MasterDataValues.AddRange(
            new MasterDataValue { TenantId = tenantId, TypeId = type.Id, Code = "TRAVEL", ValueEn = "Travel", SortOrder = 1, ExtraJson = "{\"maxAmountPerClaim\":1000,\"receiptRequiredAbove\":200}" },
            new MasterDataValue { TenantId = tenantId, TypeId = type.Id, Code = "MEALS", ValueEn = "Meals", SortOrder = 2, ExtraJson = "{\"receiptRequiredAbove\":50}" },
            new MasterDataValue { TenantId = tenantId, TypeId = type.Id, Code = "OFFICE", ValueEn = "Office", SortOrder = 3 },
            new MasterDataValue { TenantId = tenantId, TypeId = type.Id, Code = "RETIRED", ValueEn = "Retired", SortOrder = 4, IsActive = false });
        await db.SaveChangesAsync();

        // The tenant's GL defaults exactly as provisioning seeds them (includes EARN:EXPENSE_REIMBURSEMENT).
        await GlDriverSeeder.SeedTenantDefaultsAsync(db, tenantId, CancellationToken.None);
        await db.SaveChangesAsync();

        return new Scenario(tenantId, company.Id, employee.Id, employeeUser, peer.Id, peerUser, manager.Id, managerUser, Guid.NewGuid());
    }

    private static Employee NewEmployee(Guid tenantId, Guid companyId, string code, Guid userId, string name, int? managerId = null) => new()
    {
        TenantId = tenantId, CompanyId = companyId, EmployeeCode = code, FullName = $"{name} {code}", UserAccountId = userId,
        ManagerEmployeeId = managerId, Status = "Active", Nationality = "Indian", ContractType = "Indefinite",
        JoiningDate = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc), WorkEmail = $"{code.ToLowerInvariant()}@expense.test",
    };

    private static ExpenseClaimService Service(ZayraDbContext db, IDocumentStorage? storage = null)
    {
        var audit = new AuditService(db);
        return new ExpenseClaimService(db, new ApprovalWorkflowService(db, audit), storage ?? new MemoryReceiptStorage(), audit);
    }

    private static RequestContext Ctx(Guid tenantId, Guid userId, params string[] roles)
        => new(null, null, userId, tenantId, roles, new[] { "approvals.decide", "ess.read", "ess.write" });

    private static SaveExpenseClaimRequest Lines(params (string Category, decimal Amount)[] lines)
        => new(null, lines.Select(l => new ExpenseClaimLineRequest(null, DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-3)), l.Category, l.Amount, $"{l.Category} expense")).ToList());

    /// <summary>Draft → submit, as the employee.</summary>
    private async Task<ExpenseClaimDto> SubmitAsync(Scenario s, params (string Category, decimal Amount)[] lines)
    {
        await using var db = _fx.CreateDb();
        var svc = Service(db);
        var draft = await svc.CreateDraftAsync(s.TenantId, s.EmployeeId, Lines(lines), CancellationToken.None);
        return await svc.SubmitAsync(s.TenantId, s.EmployeeId, draft.Id, Ctx(s.TenantId, s.EmployeeUserId, "Employee"), CancellationToken.None);
    }

    private async Task<ExpenseClaimDto> DecideAsync(Scenario s, Guid claimId, string decision, Guid userId, string role, string? comment = null)
    {
        await using var db = _fx.CreateDb();
        return await Service(db).DecideAsync(s.TenantId, claimId, new ExpenseDecisionRequest(decision, comment), Ctx(s.TenantId, userId, role), CancellationToken.None);
    }

    private async Task<ExpensePayoutResult> ScheduleAsync(Scenario s, Guid runId, params Guid[] claimIds)
    {
        await using var db = _fx.CreateDb();
        return await Service(db).ScheduleForPayrollAsync(s.TenantId, new ScheduleExpensesRequest(runId, claimIds.Length == 0 ? null : claimIds), Guid.NewGuid(), CancellationToken.None);
    }

    private async Task<ExpenseClaimDto> ReadAsync(Scenario s, Guid claimId)
    {
        await using var db = _fx.CreateDb();
        return (await Service(db).GetAsync(s.TenantId, claimId, null, CancellationToken.None))!;
    }

    private async Task<List<PayrollAdjustment>> AdjustmentsAsync(Guid tenantId)
    {
        await using var db = _fx.CreateDb();
        return await db.PayrollAdjustments.AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.SourceType == ExpenseClaimConstants.AdjustmentSourceType).ToListAsync();
    }

    // ── payroll through its PUBLIC controller path (PayrollController is not modified by W2-B) ──

    private async Task<Guid> CreateRunAsync(Scenario s, int year, int month)
    {
        await using var db = _fx.CreateDb();
        var result = await Payroll(db, s.TenantId).CreateRun(new CreatePayrollRunRequest(year, month, s.CompanyId), CancellationToken.None);
        var ok = result.Should().BeAssignableTo<ObjectResult>().Subject;
        ok.StatusCode.Should().BeOneOf(200, 201);
        return await db.PayrollRuns.AsNoTracking()
            .Where(r => r.TenantId == s.TenantId && r.Year == year && r.Month == month).Select(r => r.Id).SingleAsync();
    }

    private async Task ProcessAsync(Scenario s, Guid runId)
    {
        await using var db = _fx.CreateDb();
        var result = await Payroll(db, s.TenantId).Process(runId, CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>(because: JsonSerializer.Serialize((result as ObjectResult)?.Value));
    }

    private async Task LockAsync(Scenario s, Guid runId)
    {
        await using (var db = _fx.CreateDb())
        {
            // Run approval is its own maker-checker flow (not under test here); mirror BonusLifecycleGlTests.
            var run = await db.PayrollRuns.SingleAsync(r => r.Id == runId);
            run.Status = "Approved";
            await db.SaveChangesAsync();
        }
        await using var lockDb = _fx.CreateDb();
        var result = await Payroll(lockDb, s.TenantId).Lock(runId, CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>(because: JsonSerializer.Serialize((result as ObjectResult)?.Value));
    }

    private static PayrollController Payroll(ZayraDbContext db, Guid tenantId)
    {
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim("tenant_id", tenantId.ToString()),
                new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                new Claim(ClaimTypes.Name, "w2b-payroll"),
                new Claim(ClaimTypes.Role, "Admin"),
                new Claim("permission", "payroll.write"), new Claim("permission", "payroll.lock"),
            }, "Test")),
        };
        var ctrl = new PayrollController(db, new Zayra.Api.Infrastructure.Common.DataScopeService(db), new _W2bHttp(http), new _W2bNotifications(),
            new _W2bKsaResolver(), _W2bRules.Rules, new _W2bLetters(), new NullDocumentStorage(), new PdfRenderGate(4));
        ctrl.ControllerContext = new ControllerContext { HttpContext = http };
        return ctrl;
    }

    // ══ 1. The money rail: paid exactly once, as an earning, GL balanced ═════════════════════════

    [Fact]
    public async Task ApprovedClaim_IsPaidExactlyOnceInTheNextRun_AsAnEarning_WithBalancedGlOn5120()
    {
        var s = await SeedAsync();
        var claim = await SubmitAsync(s, ("TRAVEL", 150.00m), ("OFFICE", 325.50m));
        claim.Status.Should().Be(ExpenseClaimStatuses.Submitted);
        claim.Currency.Should().Be("SAR");
        claim.TotalAmount.Should().Be(475.50m);

        var approved = await DecideAsync(s, claim.Id, "Approve", s.HrUserId, "HR Manager");
        approved.Status.Should().Be(ExpenseClaimStatuses.Approved);

        var runId = await CreateRunAsync(s, 2026, 6);

        // Schedule, then replay the same request and race two more: still exactly ONE adjustment.
        (await ScheduleAsync(s, runId)).Scheduled.Should().Be(1);
        (await ScheduleAsync(s, runId)).Scheduled.Should().Be(0, "a scheduled claim is not Approved-unscheduled any more");
        var raced = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            try { return (await ScheduleAsync(s, runId, claim.Id)).Scheduled; }
            catch (ExpenseConflictException) { return 0; }
        }));
        raced.Sum().Should().Be(0);
        var adjustments = await AdjustmentsAsync(s.TenantId);
        adjustments.Should().ContainSingle();
        adjustments[0].SourceId.Should().Be(claim.Id);
        adjustments[0].Amount.Should().Be(475.50m);
        adjustments[0].PayrollRunId.Should().Be(runId);
        (await ReadAsync(s, claim.Id)).Status.Should().Be(ExpenseClaimStatuses.Scheduled);

        await ProcessAsync(s, runId);

        await using (var db = _fx.CreateDb())
        {
            var expenseEarnings = await db.PayrollEarnings.AsNoTracking()
                .Where(e => e.TenantId == s.TenantId && e.ComponentCode == ExpenseClaimConstants.EarningComponentCode).ToListAsync();
            expenseEarnings.Should().ContainSingle("the claim is paid once, as ONE earning line");
            expenseEarnings[0].EmployeeId.Should().Be(s.EmployeeId);
            expenseEarnings[0].Amount.Should().Be(475.50m);
            expenseEarnings[0].PayrollRunId.Should().Be(runId);

            // Same package, same nationality: the claimant's net is the peer's net + the claim, to the halala —
            // a reimbursement is not taxed, not GOSI-able and not prorated by this rail.
            var slips = await db.PayrollSlips.AsNoTracking().Where(x => x.TenantId == s.TenantId && x.RunId == runId).ToListAsync();
            var mine = slips.Single(x => x.EmployeeId == s.EmployeeId);
            var peer = slips.Single(x => x.EmployeeId == s.PeerEmployeeId);
            (mine.NetSalary - peer.NetSalary).Should().Be(475.50m);
            (mine.GrossSalary - peer.GrossSalary).Should().Be(475.50m);
        }
        (await ReadAsync(s, claim.Id)).Status.Should().Be(ExpenseClaimStatuses.Paid);
        (await AdjustmentsAsync(s.TenantId)).Single().Status.Should().Be("Processed");

        await LockAsync(s, runId);
        await using (var db = _fx.CreateDb())
        {
            var gl = await db.FinanceGlEntries.AsNoTracking().Where(x => x.TenantId == s.TenantId && x.SourceEntityId == runId).ToListAsync();
            gl.Should().NotBeEmpty();
            var debits = gl.Where(l => !string.IsNullOrEmpty(l.DebitAccount)).Sum(l => l.Amount);
            var credits = gl.Where(l => !string.IsNullOrEmpty(l.CreditAccount)).Sum(l => l.Amount);
            debits.Should().Be(credits, "the run's journal must balance");
            gl.Where(l => l.DebitAccount.StartsWith("5120")).Sum(l => l.Amount).Should().Be(475.50m,
                "the reimbursement books to 5120 Employee Expense Reimbursements via the EARN:EXPENSE_REIMBURSEMENT driver");
            gl.Where(l => l.DebitAccount.StartsWith("5099")).Sum(l => l.Amount).Should().Be(0m,
                "nothing may fall through to the 5099 Other Earnings catch-all");
        }

        // The NEXT payroll run does not pay it again, and a paid claim cannot be rescheduled.
        var july = await CreateRunAsync(s, 2026, 7);
        (await ScheduleAsync(s, july, claim.Id)).Scheduled.Should().Be(0);
        await ProcessAsync(s, july);
        await using (var db = _fx.CreateDb())
        {
            (await db.PayrollEarnings.AsNoTracking().CountAsync(e => e.TenantId == s.TenantId && e.ComponentCode == ExpenseClaimConstants.EarningComponentCode))
                .Should().Be(1, "across both runs the claim was paid exactly once");
        }
        (await AdjustmentsAsync(s.TenantId)).Should().ContainSingle();
    }

    // ══ 2. Rejected and part-approved claims are never paid ══════════════════════════════════════

    [Fact]
    public async Task RejectedClaim_IsNeverPaid()
    {
        var s = await SeedAsync();
        var claim = await SubmitAsync(s, ("OFFICE", 90m));
        var act = () => DecideAsync(s, claim.Id, "Reject", s.HrUserId, "HR Manager");
        await act.Should().ThrowAsync<ExpenseValidationException>().Where(e => e.Violations[0].Code == "reason_required");
        var rejected = await DecideAsync(s, claim.Id, "Reject", s.HrUserId, "HR Manager", "Personal purchase");
        rejected.Status.Should().Be(ExpenseClaimStatuses.Rejected);
        rejected.RejectionReason.Should().Be("Personal purchase");

        var runId = await CreateRunAsync(s, 2026, 6);
        var payout = await ScheduleAsync(s, runId, claim.Id);
        payout.Scheduled.Should().Be(0);
        payout.Items.Should().ContainSingle(i => i.ClaimId == claim.Id && i.Outcome == "Skipped");
        await ProcessAsync(s, runId);

        (await AdjustmentsAsync(s.TenantId)).Should().BeEmpty();
        await using var db = _fx.CreateDb();
        (await db.PayrollEarnings.AsNoTracking().AnyAsync(e => e.TenantId == s.TenantId && e.ComponentCode == ExpenseClaimConstants.EarningComponentCode))
            .Should().BeFalse();
    }

    [Fact]
    public async Task TwoStepWorkflow_Step1ApprovalDoesNotPay_EvenIfTheClaimRowIsForgedApproved_OnlyTheFinalStepDoes()
    {
        var s = await SeedAsync(twoStep: true);
        var claim = await SubmitAsync(s, ("OFFICE", 200m));
        claim.Approval!.CurrentStepOrder.Should().Be(1);

        // HR cannot jump the queue: step 1 belongs to the line manager.
        var hrFirst = () => DecideAsync(s, claim.Id, "Approve", s.HrUserId, "HR Manager");
        await hrFirst.Should().ThrowAsync<InvalidOperationException>();

        var afterStep1 = await DecideAsync(s, claim.Id, "Approve", s.ManagerUserId, "Manager");
        afterStep1.Status.Should().Be(ExpenseClaimStatuses.Submitted, "step 1 of 2 is not the final step");
        afterStep1.Approval!.Status.Should().Be("Pending");
        afterStep1.Approval.CurrentStepOrder.Should().Be(2);

        var runId = await CreateRunAsync(s, 2026, 6);
        (await ScheduleAsync(s, runId, claim.Id)).Scheduled.Should().Be(0);

        // Defence in depth: even a forged/corrupted claim status cannot pay — the payout reads the approval row.
        await using (var db = _fx.CreateDb())
        {
            await db.ExpenseClaims.Where(c => c.Id == claim.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(c => c.Status, ExpenseClaimStatuses.Approved));
        }
        var forged = await ScheduleAsync(s, runId, claim.Id);
        forged.Scheduled.Should().Be(0, "the approval row is still Pending at step 2");
        (await AdjustmentsAsync(s.TenantId)).Should().BeEmpty();
        await using (var db = _fx.CreateDb())
            await db.ExpenseClaims.Where(c => c.Id == claim.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(c => c.Status, ExpenseClaimStatuses.Submitted));

        var final = await DecideAsync(s, claim.Id, "Approve", s.HrUserId, "HR Manager");
        final.Status.Should().Be(ExpenseClaimStatuses.Approved);
        (await ScheduleAsync(s, runId, claim.Id)).Scheduled.Should().Be(1);
        await ProcessAsync(s, runId);
        (await ReadAsync(s, claim.Id)).Status.Should().Be(ExpenseClaimStatuses.Paid);
    }

    [Fact]
    public async Task DecisionTakenInTheApprovalCenter_IsReflectedOnTheClaim()
    {
        var s = await SeedAsync();
        var claim = await SubmitAsync(s, ("OFFICE", 40m));
        await using (var db = _fx.CreateDb())
        {
            // Exactly what ApprovalRequestsController does — no expense code involved.
            var approvals = new ApprovalWorkflowService(db, new AuditService(db));
            await approvals.DecideAsync(s.TenantId, claim.ApprovalRequestId!.Value, new ApprovalDecisionRequest("Approve", "ok"),
                Ctx(s.TenantId, s.HrUserId, "HR Manager"), CancellationToken.None);
        }
        (await ReadAsync(s, claim.Id)).Status.Should().Be(ExpenseClaimStatuses.Approved);
    }

    [Fact]
    public async Task Requester_CannotApproveTheirOwnClaim()
    {
        var s = await SeedAsync();
        var claim = await SubmitAsync(s, ("OFFICE", 40m));
        var self = () => DecideAsync(s, claim.Id, "Approve", s.EmployeeUserId, "HR Manager");
        await self.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Maker-checker*");
        (await ReadAsync(s, claim.Id)).Status.Should().Be(ExpenseClaimStatuses.Submitted);
    }

    [Fact]
    public async Task NoExpenseWorkflowConfigured_SubmitFailsTyped_AndNothingIsWritten()
    {
        var s = await SeedAsync(workflow: false);
        await using var db = _fx.CreateDb();
        var svc = Service(db);
        var draft = await svc.CreateDraftAsync(s.TenantId, s.EmployeeId, Lines(("OFFICE", 10m)), CancellationToken.None);
        var act = () => svc.SubmitAsync(s.TenantId, s.EmployeeId, draft.Id, Ctx(s.TenantId, s.EmployeeUserId, "Employee"), CancellationToken.None);
        await act.Should().ThrowAsync<ApprovalRouteNotConfiguredException>();

        await using var verify = _fx.CreateDb();
        (await verify.ExpenseClaims.AsNoTracking().SingleAsync(c => c.Id == draft.Id)).Status.Should().Be(ExpenseClaimStatuses.Draft);
        (await verify.ApprovalRequests.AsNoTracking().AnyAsync(a => a.TenantId == s.TenantId)).Should().BeFalse();
    }

    // ══ 3. Category policy ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CategoryCap_IsEnforcedPerClaim_IncludingWhenSplitAcrossLines()
    {
        var s = await SeedAsync();
        await using var db = _fx.CreateDb();
        var svc = Service(db);

        var single = () => svc.CreateDraftAsync(s.TenantId, s.EmployeeId, Lines(("TRAVEL", 1000.01m)), CancellationToken.None);
        (await single.Should().ThrowAsync<ExpenseValidationException>())
            .Which.Violations.Should().ContainSingle(v => v.Code == "category_cap_exceeded" && v.Message.Contains("1,000.00"));

        var split = () => svc.CreateDraftAsync(s.TenantId, s.EmployeeId, Lines(("TRAVEL", 600m), ("TRAVEL", 400.01m)), CancellationToken.None);
        (await split.Should().ThrowAsync<ExpenseValidationException>())
            .Which.Violations.Should().Contain(v => v.Code == "category_cap_exceeded");

        var atCap = await svc.CreateDraftAsync(s.TenantId, s.EmployeeId, Lines(("TRAVEL", 600m), ("OFFICE", 5_000m)), CancellationToken.None);
        atCap.TotalAmount.Should().Be(5_600m);

        var bad = () => svc.CreateDraftAsync(s.TenantId, s.EmployeeId, Lines(("RETIRED", 10m), ("NOPE", 10m), ("OFFICE", -1m)), CancellationToken.None);
        (await bad.Should().ThrowAsync<ExpenseValidationException>())
            .Which.Violations.Select(v => v.Code).Should().BeEquivalentTo(new[] { "unknown_category", "unknown_category", "amount_not_positive" });

        (await db.ExpenseClaims.AsNoTracking().CountAsync(c => c.TenantId == s.TenantId)).Should().Be(1, "rejected drafts are not saved");
    }

    [Fact]
    public async Task ReceiptThreshold_BlocksSubmitUntilAReceiptIsAttached()
    {
        var s = await SeedAsync();
        var storage = new MemoryReceiptStorage();
        await using var db = _fx.CreateDb();
        var svc = Service(db, storage);
        // MEALS: receipt above 50. 50.00 needs none; 50.01 does.
        var draft = await svc.CreateDraftAsync(s.TenantId, s.EmployeeId, Lines(("MEALS", 50m), ("MEALS", 80m)), CancellationToken.None);
        var ctx = Ctx(s.TenantId, s.EmployeeUserId, "Employee");

        var submit = () => svc.SubmitAsync(s.TenantId, s.EmployeeId, draft.Id, ctx, CancellationToken.None);
        var ex = (await submit.Should().ThrowAsync<ExpenseValidationException>()).Which;
        ex.Violations.Should().ContainSingle(v => v.Code == "receipt_required" && v.LineNumber == 2);
        (await ReadAsync(s, draft.Id)).Status.Should().Be(ExpenseClaimStatuses.Draft);

        var exe = () => svc.AttachReceiptAsync(s.TenantId, s.EmployeeId, draft.Id, draft.Lines[1].Id, File("malware.exe", "application/octet-stream"), CancellationToken.None);
        (await exe.Should().ThrowAsync<ExpenseValidationException>()).Which.Violations[0].Code.Should().Be("receipt_type");

        var withReceipt = await svc.AttachReceiptAsync(s.TenantId, s.EmployeeId, draft.Id, draft.Lines[1].Id, File("dinner.pdf", "application/pdf"), CancellationToken.None);
        withReceipt.Lines[1].HasReceipt.Should().BeTrue();
        storage.SavedTenants.Should().ContainSingle().Which.Should().Be(s.TenantId, "the storage key is generated server-side under the caller's tenant");

        (await svc.SubmitAsync(s.TenantId, s.EmployeeId, draft.Id, ctx, CancellationToken.None)).Status.Should().Be(ExpenseClaimStatuses.Submitted);
    }

    [Fact]
    public async Task EditingADraft_KeepsReceiptsOnKeptLines_AndRecomputesTheTotal()
    {
        var s = await SeedAsync();
        await using var db = _fx.CreateDb();
        var svc = Service(db);
        var draft = await svc.CreateDraftAsync(s.TenantId, s.EmployeeId, Lines(("MEALS", 80m), ("OFFICE", 20m)), CancellationToken.None);
        await svc.AttachReceiptAsync(s.TenantId, s.EmployeeId, draft.Id, draft.Lines[0].Id, File("r.png", "image/png"), CancellationToken.None);

        await using var db2 = _fx.CreateDb();
        var edited = await Service(db2).UpdateDraftAsync(s.TenantId, s.EmployeeId, draft.Id, new SaveExpenseClaimRequest("Trip", new[]
        {
            new ExpenseClaimLineRequest(draft.Lines[0].Id, draft.Lines[0].ExpenseDate, "MEALS", 85m, "Dinner"),
            new ExpenseClaimLineRequest(null, draft.Lines[0].ExpenseDate, "TRAVEL", 150m, "Taxi"),
        }), CancellationToken.None);
        edited.Lines.Should().HaveCount(2);
        edited.Lines[0].HasReceipt.Should().BeTrue();
        edited.Lines[1].CategoryCode.Should().Be("TRAVEL");
        edited.TotalAmount.Should().Be(235m);
        edited.Title.Should().Be("Trip");
    }

    // ══ 4. Isolation ═════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task AnotherTenantsClaims_AreInvisible()
    {
        var a = await SeedAsync();
        var b = await SeedAsync();
        var claimA = await SubmitAsync(a, ("OFFICE", 10m));

        // Tenant B's principal on a real request-scoped context (global query filters live).
        var accessor = new _W2bAccessor { HttpContext = Principal(b.TenantId, null) };
        await using var db = _fx.CreateDbWithAccessor(accessor);
        var svc = Service(db);
        (await svc.GetAsync(b.TenantId, claimA.Id, null, CancellationToken.None)).Should().BeNull();
        (await svc.ListAsync(b.TenantId, new ExpenseClaimQuery(), null, CancellationToken.None)).Items.Should().BeEmpty();
        // Even a buggy caller passing tenant A's id gets nothing: the tenant filter is bound to the token.
        (await svc.ListAsync(a.TenantId, new ExpenseClaimQuery(), null, CancellationToken.None)).Items.Should().BeEmpty();
        (await db.ExpenseClaimLines.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task CompanyScopedUser_SeesOnlyTheirCompanysClaims()
    {
        var s = await SeedAsync();
        Guid otherCompany;
        int otherEmployee;
        await using (var seed = _fx.CreateDb())
        {
            var co = new Company { TenantId = s.TenantId, LegalNameEn = "Other Co", CountryCode = "SAU", Jurisdiction = "KSA-mainland", DefaultCurrency = "SAR", IsActive = true, RegistrationNumber = $"O-{Guid.NewGuid():N}" };
            seed.Companies.Add(co);
            await seed.SaveChangesAsync();
            var e = NewEmployee(s.TenantId, co.Id, $"OT-{Guid.NewGuid().ToString("N")[..6]}", Guid.NewGuid(), "Other");
            seed.Employees.Add(e);
            await seed.SaveChangesAsync();
            otherCompany = co.Id;
            otherEmployee = e.Id;
        }
        var mine = await SubmitAsync(s, ("OFFICE", 10m));
        ExpenseClaimDto theirs;
        await using (var db = _fx.CreateDb())
            theirs = await Service(db).CreateDraftAsync(s.TenantId, otherEmployee, Lines(("OFFICE", 20m)), CancellationToken.None);
        theirs.CompanyId.Should().Be(otherCompany);

        var accessor = new _W2bAccessor { HttpContext = Principal(s.TenantId, s.CompanyId) };
        await using var scoped = _fx.CreateDbWithAccessor(accessor);
        var svc = Service(scoped);
        var list = await svc.ListAsync(s.TenantId, new ExpenseClaimQuery(), null, CancellationToken.None);
        list.Items.Select(c => c.Id).Should().Equal(mine.Id);
        (await svc.GetAsync(s.TenantId, theirs.Id, null, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Employee_SeesAndSubmitsOnlyTheirOwnClaims_ThroughTheSelfServiceEndpoints()
    {
        var s = await SeedAsync();
        await using var db = _fx.CreateDb();
        var svc = Service(db);
        var peerDraft = await svc.CreateDraftAsync(s.TenantId, s.PeerEmployeeId, Lines(("OFFICE", 70m)), CancellationToken.None);

        var ctrl = new EssExpensesController(svc, db)
        {
            ControllerContext = new ControllerContext { HttpContext = EssPrincipal(s.TenantId, s.EmployeeUserId) },
        };
        // Own draft via the endpoint — the employee is taken from the token, never the body.
        var created = (await ctrl.Create(Lines(("OFFICE", 15m)), CancellationToken.None)).Should().BeOfType<CreatedResult>().Subject.Value as ExpenseClaimDto;
        created!.EmployeeId.Should().Be(s.EmployeeId);

        var list = (await ctrl.List(null, 1, 25, CancellationToken.None)).Should().BeOfType<OkObjectResult>().Subject.Value as PagedResult<ExpenseClaimDto>;
        list!.Items.Select(c => c.Id).Should().Equal(created.Id);

        (await ctrl.Get(peerDraft.Id, CancellationToken.None)).Should().BeOfType<NotFoundResult>();
        (await ctrl.Submit(peerDraft.Id, CancellationToken.None)).Should().BeOfType<NotFoundObjectResult>();
        (await ctrl.Update(peerDraft.Id, Lines(("OFFICE", 1m)), CancellationToken.None)).Should().BeOfType<NotFoundObjectResult>();
        (await ctrl.Cancel(peerDraft.Id, CancellationToken.None)).Should().BeOfType<NotFoundObjectResult>();
        (await db.ExpenseClaims.AsNoTracking().SingleAsync(c => c.Id == peerDraft.Id)).Status.Should().Be(ExpenseClaimStatuses.Draft);

        (await ctrl.Submit(created.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();

        // Without ess.write the endpoints refuse writes.
        var readOnly = new EssExpensesController(svc, db)
        {
            ControllerContext = new ControllerContext { HttpContext = EssPrincipal(s.TenantId, s.EmployeeUserId, write: false) },
        };
        (await readOnly.Create(Lines(("OFFICE", 1m)), CancellationToken.None)).Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(403);
    }

    // ══ 5. Migration data step ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task MigrationSeeds_AreIdempotent_AndGiveExistingTenantsAWorkingSetup()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        await GlDriverSeeder.SeedTenantDefaultsAsync(db, tenantId, CancellationToken.None);
        await db.SaveChangesAsync();
        // Simulate a tenant provisioned BEFORE this migration: no expense driver/account/mapping yet.
        await db.GlAccountMappings.Where(m => m.TenantId == tenantId && m.DriverKey == ExpenseClaimConstants.GlDriverKey).ExecuteDeleteAsync();
        await db.GlDrivers.Where(d => d.TenantId == tenantId && d.Key == ExpenseClaimConstants.GlDriverKey).ExecuteDeleteAsync();
        await db.GlAccounts.Where(a => a.TenantId == tenantId && a.Code == "5120").ExecuteDeleteAsync();

        // The seeds are tenant-wide statements; run them in a transaction that is rolled back so the
        // shared fixture database is left exactly as it was for every other test.
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            foreach (var _ in new[] { 1, 2 })
            {
                await db.Database.ExecuteSqlRawAsync(Zayra.Api.Migrations.AddExpenseClaims.SeedExpenseWorkflowsSql);
                await db.Database.ExecuteSqlRawAsync(Zayra.Api.Migrations.AddExpenseClaims.SeedExpenseCategoriesSql);
                await db.Database.ExecuteSqlRawAsync(Zayra.Api.Migrations.AddExpenseClaims.SeedExpenseGlSql);
            }
            var wf = await db.ApprovalWorkflows.AsNoTracking().Include(w => w.Steps)
                .Where(w => w.TenantId == tenantId && w.EntityName == ExpenseClaimConstants.ApprovalEntityName).ToListAsync();
            wf.Should().ContainSingle();
            wf[0].Steps.Should().ContainSingle(st => st.IsFinalStep && st.ApproverRole == "HR Manager");
            (await db.MasterDataValues.AsNoTracking().CountAsync(v => v.TenantId == tenantId
                && db.MasterDataTypes.Any(t => t.Id == v.TypeId && t.Code == ExpenseClaimConstants.CategoryMasterType))).Should().Be(6);
            (await db.GlDrivers.AsNoTracking().CountAsync(d => d.TenantId == tenantId && d.Key == ExpenseClaimConstants.GlDriverKey)).Should().Be(1);
            (await db.GlAccountMappings.AsNoTracking().CountAsync(m => m.TenantId == tenantId && m.DriverKey == ExpenseClaimConstants.GlDriverKey)).Should().Be(1);

            await db.Database.ExecuteSqlRawAsync(Zayra.Api.Migrations.AddExpenseClaims.RevertSeedsSql);
            (await db.ApprovalWorkflows.AsNoTracking().AnyAsync(w => w.TenantId == tenantId && w.EntityName == ExpenseClaimConstants.ApprovalEntityName)).Should().BeFalse();
            (await db.GlDrivers.AsNoTracking().AnyAsync(d => d.TenantId == tenantId && d.Key == ExpenseClaimConstants.GlDriverKey)).Should().BeFalse();
            await tx.RollbackAsync();
        });
    }

    // ══ helpers ══════════════════════════════════════════════════════════════════════════════════

    private static IFormFile File(string name, string contentType)
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", name) { Headers = new HeaderDictionary(), ContentType = contentType };
    }

    private static HttpContext Principal(Guid tenantId, Guid? companyId)
    {
        var claims = new List<Claim> { new("tenant_id", tenantId.ToString()), new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) };
        if (companyId is { } c) claims.Add(new Claim("entity_access", JsonSerializer.Serialize(new { c, r = "Viewer" })));
        return new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) };
    }

    private static HttpContext EssPrincipal(Guid tenantId, Guid userId, bool write = true)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()), new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Role, "Employee"), new("permission", "ess.read"),
        };
        if (write) claims.Add(new Claim("permission", "ess.write"));
        return new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) };
    }
}

// ── Test doubles (file-scoped; mirror BonusLifecycleGlTests) ────────────────────────────────────

internal sealed class MemoryReceiptStorage : IDocumentStorage
{
    public List<Guid> SavedTenants { get; } = new();
    private readonly Dictionary<string, byte[]> _files = new();

    public async Task<StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken cancellationToken)
    {
        SavedTenants.Add(tenantId);
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, cancellationToken);
        var key = $"storage/documents/{tenantId:N}/{Guid.NewGuid():N}_{Path.GetFileName(file.FileName)}";
        _files[key] = ms.ToArray();
        return new StoredDocument(Path.GetFileName(file.FileName), file.ContentType, key, key);
    }

    public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default)
    {
        if (!storageUrl.StartsWith($"storage/documents/{tenantId:N}/", StringComparison.Ordinal))
            throw new InvalidOperationException("Cross-tenant storage access denied.");
        return Task.FromResult(_files[storageUrl]);
    }

    public string ResolvePath(string storageUrl) => storageUrl;
}

file sealed class _W2bAccessor : IHttpContextAccessor
{
    public HttpContext? HttpContext { get; set; }
}

file static class _W2bRules
{
    internal static readonly StubRuleReader Rules = new StubRuleReader()
        .Set("gosi.saudi_employee_rate",            0.09m)
        .Set("gosi.saudi_employer_rate",            0.09m)
        .Set("gosi.saned_rate",                     0.0075m)
        .Set("gosi.expat_occupational_hazard_rate", 0.02m)
        .Set("gosi.covered_wage_ceiling_sar",       45_000m)
        .Set("ot.standard_multiplier",              1.5m)
        .Set("ot.standard_monthly_hours",           240m)
        .Set("lop.monthly_day_divisor",             30m)
        .Set("lop.standard_work_minutes_per_day",   480m);
}

file sealed class _W2bHttp : IHttpContextAccessor
{
    public _W2bHttp(HttpContext ctx) => HttpContext = ctx;
    public HttpContext? HttpContext { get; set; }
}

file sealed class _W2bNotifications : Zayra.Api.Infrastructure.Notifications.INotificationService
{
    public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken ct) => Task.CompletedTask;
    public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken ct) => Task.CompletedTask;
}

file sealed class _W2bKsaResolver : ICountryPackResolver
{
    private static readonly KsaDeductionCalculator _calc = new(_W2bRules.Rules);
    public IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j)
        => cc == "SAU" ? _calc : new DefaultStatutoryDeductionCalculator();
    public IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j) => new DefaultEndOfServiceCalculator();
    public IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j) => new DefaultWageProtectionExporter();
    public INationalizationTracker ResolveNationalizationTracker(string cc, string j) => new DefaultNationalizationTracker();
    public ILocalizationProfile ResolveLocalizationProfile(string cc, string j) => new DefaultLocalizationProfile();
    public ICountryPackDescriptor ResolveDescriptor(string cc, string j) => new DefaultCountryPackDescriptor();
}

file sealed class _W2bLetters : Zayra.Api.Infrastructure.Documents.Letters.ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(Zayra.Api.Infrastructure.Documents.Letters.PayslipData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.OfferLetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}
