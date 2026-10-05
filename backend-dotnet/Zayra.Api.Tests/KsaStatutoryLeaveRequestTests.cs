using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Controllers.Leave;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Infrastructure.Leave;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// The request path of KSA statutory special leave: the repeat limits (Hajj once and after two
/// years; one event's window for the others), calendar counting for maternity and iddah, and the
/// ledger — the statutory grant is booked at approval with the Used it pays for, so nothing is left
/// behind by a reject, cancel or withdraw.
/// </summary>
public class KsaStatutoryLeaveRequestTests
{
    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static readonly DateOnly Base = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30);

    private sealed record Fixture(ZayraDbContext Db, Guid TenantId, Employee Employee, LeaveType Type, LeavePolicy Policy, LeaveService Service);

    private static async Task<Fixture> SeedAsync(
        string code, string name, string category, bool calendarPolicy = true, decimal policyDays = 0m,
        int serviceYears = 3, bool allowsHajjBeyond = false)
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var company = new Company { TenantId = tenantId, LegalNameEn = "KSA Co", CountryCode = "SA" };
        db.Companies.Add(company);
        var employee = new Employee
        {
            TenantId = tenantId, EmployeeCode = $"E-{Guid.NewGuid():N}", FullName = "Employee", EnglishName = "Employee",
            Status = "Active", CompanyId = company.Id, JoiningDate = DateTime.UtcNow.AddYears(-serviceYears),
            UserAccountId = Guid.NewGuid(), Gender = "Female",
        };
        db.Employees.Add(employee);
        var type = new LeaveType { TenantId = tenantId, Code = code, NameEn = name, Category = category, IsPaid = true, IsActive = true };
        db.LeaveTypes.Add(type);
        var policy = new LeavePolicy
        {
            TenantId = tenantId, Name = name, LeaveTypeId = type.Id, CountryCode = "SA", AnnualEntitlementDays = policyDays,
            MinimumDaysPerRequest = 1m, WeekendsIncluded = calendarPolicy, PublicHolidaysIncluded = calendarPolicy,
            AppliesOnProbation = true, AccrualMethod = "Yearly", Status = "Active",
            AllowsHajjBeyondStatutoryEligibility = allowsHajjBeyond,
        };
        db.LeavePolicies.Add(policy);
        await db.SaveChangesAsync();
        await TestApprovalConfig.EnsureDefaultLeaveWorkflowAsync(db, tenantId);
        return new Fixture(db, tenantId, employee, type, policy, new LeaveService(db, new ApprovalRouter(db)));
    }

    private static Task<LeaveRequest> Submit(Fixture f, DateOnly start, int calendarDays)
        => f.Service.SubmitRequestAsync(f.TenantId, new LeaveRequest
        {
            EmployeeId = f.Employee.Id, EmployeeName = f.Employee.FullName, LeaveTypeId = f.Type.Id,
            StartDate = start, EndDate = start.AddDays(calendarDays - 1), DayType = "Full", Reason = "Statutory",
        }, f.Employee.UserAccountId);

    // ── Hajj (Art. 114): once in the employee's service, after two years ─────────────────────

    [Fact]
    public async Task Hajj_ASecondRequest_IsRefused_WhileTheFirstIsPendingOrApproved()
    {
        var f = await SeedAsync("HAJJ", "Hajj Leave", "Religious");
        await Submit(f, Base, 10);

        var again = () => Submit(f, Base.AddDays(400), 10);

        (await again.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("once in an employee's service");
    }

    [Fact]
    public async Task Hajj_AfterTheFirstWasRejected_IsAllowedAgain()
    {
        var f = await SeedAsync("HAJJ", "Hajj Leave", "Religious");
        var first = await Submit(f, Base, 10);
        await f.Service.RejectRequestAsync(f.TenantId, first.Id, Guid.NewGuid(), "HR", "Not this year");

        (await Submit(f, Base.AddDays(400), 10)).TotalDays.Should().Be(10m);
    }

    [Fact]
    public async Task Hajj_UnderTwoYearsOfService_IsRefused()
    {
        var f = await SeedAsync("HAJJ", "Hajj Leave", "Religious", serviceYears: 1);

        var act = () => Submit(f, Base, 10);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("at least 2 consecutive years");
    }

    [Fact]
    public async Task Hajj_ServiceRequirement_ComesFromTheStatutoryRule()
    {
        var f = await SeedAsync("HAJJ", "Hajj Leave", "Religious", serviceYears: 3);
        f.Db.StatutoryRules.Add(new StatutoryRule
        {
            Id = Guid.NewGuid(), TenantId = null, CountryCode = CountryCodes.Saudi, Jurisdiction = Jurisdictions.KsaMainland,
            RuleKey = KsaSpecialLeaveRuleKeys.HajjMinServiceYears, RuleValue = "4", DataType = "decimal",
            EffectiveFrom = new DateTime(2005, 9, 27, 0, 0, 0, DateTimeKind.Utc), Description = "test",
        });
        await f.Db.SaveChangesAsync();

        var act = () => Submit(f, Base, 10);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("at least 4 consecutive years");
    }

    [Fact]
    public async Task Hajj_ACompanyPolicyThatAllowsMore_Wins()
    {
        // The statute is a floor: a company that chooses to grant Hajj earlier, or again, may.
        var f = await SeedAsync("HAJJ", "Hajj Leave", "Religious", serviceYears: 1, allowsHajjBeyond: true);

        (await Submit(f, Base, 10)).TotalDays.Should().Be(10m);
        (await Submit(f, Base.AddDays(400), 10)).TotalDays.Should().Be(10m);
    }

    // ── One event's window for the others ───────────────────────────────────────────────────

    [Theory]
    [InlineData("MARRIAGE", "Marriage Leave", "Marriage", 5)]
    [InlineData("BEREAVEMENT", "Bereavement Leave", "Bereavement", 5)]
    [InlineData("BRV_SIB", "Death of a brother or sister", "", 3)]
    [InlineData("PAT", "Paternity Leave", "Parental", 3)]
    [InlineData("MAT", "Maternity Leave", "Parental", 84)]
    [InlineData("IDDAH", "Iddah Leave", "Bereavement", 130)]
    public async Task ARequestInsideAnEarlierLeavesWindow_IsTheSameEvent_AndMayNotExceedTheStatute(
        string code, string name, string category, int statutoryDays)
    {
        var f = await SeedAsync(code, name, category);
        await Submit(f, Base, 2);

        // Starts the day after, inside the window the first leave opened: together one day over.
        var over = () => Submit(f, Base.AddDays(2), statutoryDays - 1);
        (await over.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("per event");

        // The rest of the same entitlement is still available — splitting is allowed, drawing twice is not.
        (await Submit(f, Base.AddDays(2), statutoryDays - 2)).Should().NotBeNull();
    }

    [Fact]
    public async Task ARequestAfterTheWindowCloses_IsANewEvent()
    {
        var f = await SeedAsync("MARRIAGE", "Marriage Leave", "Marriage");
        await Submit(f, Base, 5);

        (await Submit(f, Base.AddDays(5), 5)).TotalDays.Should().Be(5m,
            "outside the first leave's window it is a new event; the approver sees the history (below)");
    }

    [Fact]
    public async Task TheApproverSeesTheEarlierLeaveOfTheSameKind()
    {
        var f = await SeedAsync("BEREAVEMENT", "Bereavement Leave", "Bereavement");
        var earlier = await Submit(f, Base, 5);
        var later = await Submit(f, Base.AddDays(30), 5);

        var history = await f.Service.GetKsaStatutoryLeaveHistoryAsync(f.TenantId, later.Id);

        history.Should().ContainSingle().Which.RequestId.Should().Be(earlier.Id);
        history[0].StatutoryKind.Should().Contain("Bereavement");
    }

    // ── Calendar counting for maternity and iddah ───────────────────────────────────────────

    [Fact]
    public async Task WorkingDayMaternityPolicy_EightyFourWorkingDays_IsRefused_EightyFourCalendarDays_IsAllowed()
    {
        // The policy counts working days and even says 84. 84 working days is ~117 calendar days —
        // about 17 weeks — and the statute's 84 is 12 weeks on the calendar.
        var f = await SeedAsync("MAT", "Maternity Leave", "Parental", calendarPolicy: false, policyDays: 84m);

        var seventeenWeeks = () => Submit(f, Base, 117);
        (await seventeenWeeks.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("84 calendar day(s) per event").And.Contain("117 calendar day(s)");

        var twelveWeeks = await Submit(f, Base.AddDays(200), 84);
        twelveWeeks.EndDate.DayNumber.Should().Be(twelveWeeks.StartDate.DayNumber + 83);
    }

    // ── Ledger: no phantom days after reject, cancel or withdraw ────────────────────────────

    private static async Task<EmployeeLeaveBalance> BalanceAsync(Fixture f)
        => await f.Db.EmployeeLeaveBalances.SingleAsync(b => b.EmployeeId == f.Employee.Id && b.LeaveTypeId == f.Type.Id);

    [Fact]
    public async Task Approval_BooksTheStatutoryGrant_PairedWithUsed()
    {
        var f = await SeedAsync("MARRIAGE", "Marriage Leave", "Marriage");
        var request = await Submit(f, Base, 5);
        (await f.Db.LeaveBalanceTransactions.AnyAsync(t => t.TransactionType == "Allocation"))
            .Should().BeFalse("nothing is granted at submission");

        await f.Service.ApproveRequestAsync(f.TenantId, request.Id, Guid.NewGuid(), "HR", null);

        var balance = await BalanceAsync(f);
        balance.Entitled.Should().Be(5m);
        balance.Used.Should().Be(5m);
        balance.Pending.Should().Be(0m);
        balance.Available.Should().Be(0m);
    }

    [Fact]
    public async Task Reject_LeavesNoPhantomDays()
    {
        var f = await SeedAsync("MARRIAGE", "Marriage Leave", "Marriage");
        var request = await Submit(f, Base, 5);

        await f.Service.RejectRequestAsync(f.TenantId, request.Id, Guid.NewGuid(), "HR", "No");

        var balance = await BalanceAsync(f);
        (balance.Entitled, balance.Pending, balance.Used, balance.Available).Should().Be((0m, 0m, 0m, 0m));
    }

    [Fact]
    public async Task CancellingAnApprovedStatutoryLeave_ReversesTheGrant_LeavingNoPhantomDays()
    {
        var f = await SeedAsync("MARRIAGE", "Marriage Leave", "Marriage");
        var request = await Submit(f, Base, 5);
        await f.Service.ApproveRequestAsync(f.TenantId, request.Id, Guid.NewGuid(), "HR", null);

        await f.Service.CancelRequestAsync(f.TenantId, request.Id, "HR", "Wedding postponed");

        var balance = await BalanceAsync(f);
        (balance.Entitled, balance.Pending, balance.Used, balance.Available).Should().Be((0m, 0m, 0m, 0m));
        (await f.Db.LeaveBalanceTransactions.SingleAsync(t => t.TransactionType == "AllocationReversed")).Amount.Should().Be(5m);
    }

    [Fact]
    public async Task CancellingAPendingStatutoryLeave_LeavesNoPhantomDays()
    {
        var f = await SeedAsync("MARRIAGE", "Marriage Leave", "Marriage");
        var request = await Submit(f, Base, 5);

        await f.Service.CancelRequestAsync(f.TenantId, request.Id, "Employee", "Changed plans");

        var balance = await BalanceAsync(f);
        (balance.Entitled, balance.Pending, balance.Used, balance.Available).Should().Be((0m, 0m, 0m, 0m));
    }

    [Fact]
    public async Task WithdrawingAStatutoryLeave_LeavesNoPhantomDays()
    {
        var f = await SeedAsync("MARRIAGE", "Marriage Leave", "Marriage");
        var request = await Submit(f, Base, 5);
        var controller = new LeaveRequestsController(f.Db, f.Service, new OwnScope(f.Employee.Id), new NullNotifications())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim("tenant_id", f.TenantId.ToString()),
                        new Claim(ClaimTypes.NameIdentifier, f.Employee.UserAccountId!.Value.ToString()),
                    }, "Test")),
                },
            },
        };

        (await controller.Withdraw(request.Id, new WithdrawLeaveRequest("changed plans"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();

        var balance = await BalanceAsync(f);
        (balance.Entitled, balance.Pending, balance.Used, balance.Available).Should().Be((0m, 0m, 0m, 0m));
    }

    private sealed class OwnScope(int employeeId) : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) =>
            Task.FromResult(new DataScope
            {
                Level = DataScopeLevel.Own, CallerEmployeeId = employeeId, AllowedEmployeeIds = new[] { employeeId },
            });
    }

    private sealed class NullNotifications : INotificationService
    {
        public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken cancellationToken)
            => Task.CompletedTask;
        public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
