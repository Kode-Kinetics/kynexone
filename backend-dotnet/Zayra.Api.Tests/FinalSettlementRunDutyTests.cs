using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// G-006 (owner decision): the Payroll Manager owns the off-cycle run that pays an approved final settlement. Approving a
/// settlement tells the Payroll Managers of that company to create it, with the legal due date; a daily sweep reminds them two
/// days before and on the due date while no run holds the employee; one action creates the run pre-filled; and the settlement
/// list links the run back so the offboarding page can show it. None of it changes an amount or who approves.
/// </summary>
public class FinalSettlementRunDutyTests
{
    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed record Notice(Guid TenantId, Guid? UserId, string Title, string Message, string EntityName, string? EntityId);

    private sealed class RecordingNotifications : INotificationService
    {
        public List<Notice> Sent { get; } = [];
        public Task NotifyAsync(Guid t, Guid? u, string title, string msg, string en, string? eid, CancellationToken ct)
        { Sent.Add(new Notice(t, u, title, msg, en, eid)); return Task.CompletedTask; }
        public Task SendEmailAsync(Guid t, string code, string to, string name, Dictionary<string, string> vars, CancellationToken ct) => Task.CompletedTask;
        // The reminder sweep uses the multi-channel entry point; record it the same way.
        public Task<IReadOnlyList<NotificationDelivery>> EnqueueAsync(NotificationRequest r, CancellationToken ct)
        { Sent.Add(new Notice(r.TenantId, r.UserId, r.Title, r.Message, r.EntityName, r.EntityId)); return Task.FromResult<IReadOnlyList<NotificationDelivery>>([]); }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }

    private sealed record People(Guid PayrollManagerInScope, Guid PayrollManagerOtherCompany, Guid HrManagerInScope, Guid GroupPayrollManager);

    /// <summary>Four logins: a Payroll Manager with access to the settlement's company, one with access only to a sister company, an
    /// HR Manager with access to the settlement's company, and a group-scope Payroll Manager.</summary>
    private static async Task<People> SeedPeopleAsync(ZayraDbContext db, FinalSettlementMakerCheckerTests.SettlementFixture fx)
    {
        var sister = new Company { TenantId = fx.TenantId, LegalNameEn = "Sister Co", RegistrationNumber = $"S-{Guid.NewGuid():N}", CountryCode = "SA", DefaultCurrency = "SAR", IsActive = true };
        var pmRole = new Role { TenantId = fx.TenantId, Name = "Payroll Manager", NormalizedName = "PAYROLL MANAGER", IsActive = true };
        var hrRole = new Role { TenantId = fx.TenantId, Name = "HR Manager", NormalizedName = "HR MANAGER", IsActive = true };
        User NewUser(string name, bool group = false) => new() { TenantId = fx.TenantId, Email = $"{name}@x.test", NormalizedEmail = $"{name}@X.TEST", FullName = name, IsActive = true, IsGroupScope = group };
        var inScope = NewUser("pm-in"); var other = NewUser("pm-other"); var hr = NewUser("hr-in"); var group = NewUser("pm-group", group: true);
        db.AddRange(sister, pmRole, hrRole, inScope, other, hr, group);
        db.UserRoles.AddRange(
            new UserRole { UserId = inScope.Id, RoleId = pmRole.Id }, new UserRole { UserId = other.Id, RoleId = pmRole.Id },
            new UserRole { UserId = group.Id, RoleId = pmRole.Id }, new UserRole { UserId = hr.Id, RoleId = hrRole.Id });
        db.UserEntityAccesses.AddRange(
            new UserEntityAccess { TenantId = fx.TenantId, UserId = inScope.Id, CompanyId = fx.CompanyId },
            new UserEntityAccess { TenantId = fx.TenantId, UserId = other.Id, CompanyId = sister.Id },
            new UserEntityAccess { TenantId = fx.TenantId, UserId = hr.Id, CompanyId = fx.CompanyId });
        await db.SaveChangesAsync();
        return new People(inScope.Id, other.Id, hr.Id, group.Id);
    }

    private static PayrollController ControllerWith(ZayraDbContext db, Guid tenantId, Guid userId, INotificationService notifications, string name = "Approver")
    {
        var rules = PayComponentNetPayDefectTests.KsaRules();
        var ctrl = new PayrollController(
            db, new DataScopeService(db), new HttpContextAccessor(),
            notifications, new KsaTestPackResolver(rules), rules,
            new F2NullLetters(), new NullDocumentStorage(), new Zayra.Api.Infrastructure.Documents.PdfRenderGate(8));
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()), new(ClaimTypes.Name, name), new(ClaimTypes.Role, "Payroll Manager"),
            new("permission", "payroll.read"), new("permission", "payroll.write"), new("permission", "payroll.approve"),
            new(ClaimTypes.NameIdentifier, userId.ToString()),
        };
        ctrl.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) } };
        return ctrl;
    }

    private static async Task<EmployeeFinalSettlement> ApproveAsync(ZayraDbContext db, FinalSettlementMakerCheckerTests.SettlementFixture fx, RecordingNotifications notifications)
    {
        var result = await ControllerWith(db, fx.TenantId, Guid.NewGuid(), notifications)
            .ApproveFinalSettlement(fx.Settlement.Id, FinalSettlementMakerCheckerTests.ApproveBody(), CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        return await db.EmployeeFinalSettlements.AsNoTracking().SingleAsync(s => s.Id == fx.Settlement.Id);
    }

    private static FinalSettlementRunReminderService Sweep(ZayraDbContext db, RecordingNotifications notifications, DateOnly today) =>
        new(db, new TenantClock(db, new FixedClock(new DateTimeOffset(today.ToDateTime(new TimeOnly(9, 0)), TimeSpan.Zero))), notifications, NullLogger<FinalSettlementRunReminderService>.Instance);

    private static async Task<EmployeeFinalSettlement> ApprovedSettlementAsync(ZayraDbContext db)
    {
        var fx = await FinalSettlementMakerCheckerTests.SeedPendingSettlementAsync(db, createdBy: Guid.NewGuid(), submittedBy: Guid.NewGuid());
        var s = await db.EmployeeFinalSettlements.SingleAsync(x => x.Id == fx.Settlement.Id);
        s.Status = FinalSettlementStatuses.Approved;
        s.SettlementDueDate = new DateOnly(2026, 11, 20);
        await db.SaveChangesAsync();
        return s;
    }

    // ── approval → notice to the Payroll Managers in the company's scope ─────────────────────────

    [Fact]
    public async Task Approving_a_settlement_tells_the_Payroll_Managers_of_that_company_to_create_the_run_with_the_due_date()
    {
        await using var db = CreateDb();
        var fx = await FinalSettlementMakerCheckerTests.SeedPendingSettlementAsync(db, createdBy: Guid.NewGuid(), submittedBy: Guid.NewGuid());
        var people = await SeedPeopleAsync(db, fx);
        var notifications = new RecordingNotifications();

        var approved = await ApproveAsync(db, fx, notifications);

        var recipients = notifications.Sent.Select(n => n.UserId).ToList();
        recipients.Should().BeEquivalentTo([people.PayrollManagerInScope, people.GroupPayrollManager], "only Payroll Managers whose access covers the settlement's company");
        recipients.Should().NotContain(people.PayrollManagerOtherCompany).And.NotContain(people.HrManagerInScope);
        var notice = notifications.Sent.First();
        notice.Title.Should().Contain("Create the off-cycle run for Leaving Person");
        notice.Message.Should().Contain(approved.SettlementDueDate.ToString("yyyy-MM-dd"));
        notice.EntityName.Should().Be(nameof(EmployeeFinalSettlement));
        notice.EntityId.Should().Be(approved.Id.ToString());
        approved.Status.Should().Be(FinalSettlementStatuses.Approved);
        approved.NetPayable.Should().Be(12_000m, "the amounts are untouched");
    }

    [Theory]
    [InlineData("FINAL_SETTLEMENT_RUN_ASSIGNED")]
    [InlineData("FINAL_SETTLEMENT_RUN_DUE_SOON")]
    [InlineData("FINAL_SETTLEMENT_RUN_DUE_TODAY")]
    public void The_run_reminder_text_carries_no_amount_and_no_currency(string eventCode)
    {
        var settlement = new EmployeeFinalSettlement
        {
            EmployeeName = "Leaving Person", NetPayable = 12_345.67m, Currency = "SAR", SettlementDueDate = new DateOnly(2026, 11, 20),
        };

        var (title, message) = FinalSettlementRunDuty.Compose(eventCode, settlement);

        foreach (var text in new[] { title, message })
        {
            text.Should().NotContain("12,345.67").And.NotContain("12345").And.NotContain("SAR");
            text.Should().Contain("Leaving Person");
        }
        message.Should().Contain("2026-11-20", "who and when stay; how much does not");
    }

    // ── the reminder sweep ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_sweep_reminds_two_days_before_and_on_the_due_date_and_at_no_other_time()
    {
        await using var db = CreateDb();
        var s = await ApprovedSettlementAsync(db);
        var people = await SeedPeopleAsync(db, new FinalSettlementMakerCheckerTests.SettlementFixture(s.TenantId, s.CompanyId!.Value, s));
        var due = s.SettlementDueDate;

        var early = new RecordingNotifications();
        (await Sweep(db, early, due.AddDays(-3)).RunTenantAsync(s.TenantId, default)).Should().Be(0, "three days out is too early");
        early.Sent.Should().BeEmpty();

        var soon = new RecordingNotifications();
        (await Sweep(db, soon, due.AddDays(-2)).RunTenantAsync(s.TenantId, default)).Should().Be(2);
        soon.Sent.Select(n => n.UserId).Should().BeEquivalentTo([people.PayrollManagerInScope, people.GroupPayrollManager]);
        soon.Sent.Should().OnlyContain(n => n.Title.Contains("Leaving Person") && n.Message.Contains(due.ToString("yyyy-MM-dd")));

        var day = new RecordingNotifications();
        (await Sweep(db, day, due).RunTenantAsync(s.TenantId, default)).Should().Be(2);
        day.Sent.First().Title.Should().Contain("today", "the due-date notice says so");

        var late = new RecordingNotifications();
        (await Sweep(db, late, due.AddDays(1)).RunTenantAsync(s.TenantId, default)).Should().Be(0, "past the due date the overdue badge on the list takes over");
    }

    [Fact]
    public async Task The_notice_text_is_the_same_on_each_day_of_the_soon_window_so_the_notification_pipeline_sends_it_once()
    {
        await using var db = CreateDb();
        var s = await ApprovedSettlementAsync(db);
        await SeedPeopleAsync(db, new FinalSettlementMakerCheckerTests.SettlementFixture(s.TenantId, s.CompanyId!.Value, s));
        var twoOut = new RecordingNotifications(); var oneOut = new RecordingNotifications();

        await Sweep(db, twoOut, s.SettlementDueDate.AddDays(-2)).RunTenantAsync(s.TenantId, default);
        await Sweep(db, oneOut, s.SettlementDueDate.AddDays(-1)).RunTenantAsync(s.TenantId, default);

        twoOut.Sent.Should().NotBeEmpty();
        oneOut.Sent.Select(n => (n.Title, n.Message, n.EntityId)).Should().BeEquivalentTo(twoOut.Sent.Select(n => (n.Title, n.Message, n.EntityId)),
            "the pipeline dedupes on (event, entity, recipient, content), so identical text on D-2 and D-1 is one notice");
    }

    [Fact]
    public async Task The_sweep_stays_silent_once_an_offcycle_run_holds_the_employee_or_the_settlement_is_not_waiting()
    {
        await using var db = CreateDb();
        var s = await ApprovedSettlementAsync(db);
        await SeedPeopleAsync(db, new FinalSettlementMakerCheckerTests.SettlementFixture(s.TenantId, s.CompanyId!.Value, s));
        var today = s.SettlementDueDate.AddDays(-2);

        // A voided run holding the employee does not count: nobody is going to pay from it.
        var voided = new PayrollRun { TenantId = s.TenantId, CompanyId = s.CompanyId, Year = 2026, Month = 11, RunType = PayrollRunTypes.OffCycle, SettlesFinalSettlements = true, IncludesRecurringPay = false, Status = "Voided" };
        db.PayrollRuns.Add(voided);
        db.PayrollRunEmployeeSelections.Add(new PayrollRunEmployeeSelection { TenantId = s.TenantId, CompanyId = s.CompanyId, PayrollRunId = voided.Id, EmployeeId = s.EmployeeId, Mode = PayrollRunSelectionModes.Include, Reason = "x" });
        await db.SaveChangesAsync();
        var withVoided = new RecordingNotifications();
        (await Sweep(db, withVoided, today).RunTenantAsync(s.TenantId, default)).Should().BeGreaterThan(0);

        var live = new PayrollRun { TenantId = s.TenantId, CompanyId = s.CompanyId, Year = 2026, Month = 11, RunType = PayrollRunTypes.OffCycle, SettlesFinalSettlements = true, IncludesRecurringPay = false, Status = "Draft" };
        db.PayrollRuns.Add(live);
        db.PayrollRunEmployeeSelections.Add(new PayrollRunEmployeeSelection { TenantId = s.TenantId, CompanyId = s.CompanyId, PayrollRunId = live.Id, EmployeeId = s.EmployeeId, Mode = PayrollRunSelectionModes.Include, Reason = "x" });
        await db.SaveChangesAsync();
        var withRun = new RecordingNotifications();
        (await Sweep(db, withRun, today).RunTenantAsync(s.TenantId, default)).Should().Be(0);

        db.PayrollRuns.Remove(live);
        db.PayrollRunEmployeeSelections.RemoveRange(db.PayrollRunEmployeeSelections.Where(x => x.PayrollRunId == live.Id));
        foreach (var status in new[] { FinalSettlementStatuses.Draft, FinalSettlementStatuses.PendingApproval, FinalSettlementStatuses.Paid, FinalSettlementStatuses.Cancelled })
        {
            var row = await db.EmployeeFinalSettlements.SingleAsync(x => x.Id == s.Id);
            row.Status = status;
            await db.SaveChangesAsync();
            var quiet = new RecordingNotifications();
            (await Sweep(db, quiet, today).RunTenantAsync(s.TenantId, default)).Should().Be(0, $"a {status} settlement needs no run");
        }
        var disbursing = await db.EmployeeFinalSettlements.SingleAsync(x => x.Id == s.Id);
        disbursing.Status = FinalSettlementStatuses.Approved;
        disbursing.PayrollRunId = Guid.NewGuid();
        await db.SaveChangesAsync();
        (await Sweep(db, new RecordingNotifications(), today).RunTenantAsync(s.TenantId, default)).Should().Be(0, "a settlement already tied to a run needs no reminder");
    }

    // ── one action creates the run, pre-filled; the list links it back ───────────────────────────

    [Fact]
    public async Task One_action_creates_the_prefilled_offcycle_run_once_and_the_list_links_it_back()
    {
        await using var db = CreateDb();
        var s = await ApprovedSettlementAsync(db);
        var ctrl = ControllerWith(db, s.TenantId, Guid.NewGuid(), new RecordingNotifications());

        var first = await ctrl.CreateFinalSettlementRun(s.Id, CancellationToken.None);

        var created = first.Should().BeOfType<OkObjectResult>().Subject;
        var runId = Guid.Parse(JsonSerializer.SerializeToElement(created.Value).GetProperty("runId").GetString()!);
        db.ChangeTracker.Clear();
        var run = await db.PayrollRuns.AsNoTracking().SingleAsync(r => r.Id == runId);
        run.RunType.Should().Be(PayrollRunTypes.OffCycle);
        run.SettlesFinalSettlements.Should().BeTrue();
        run.IncludesRecurringPay.Should().BeFalse("a settlement run never pays recurring salary");
        run.CompanyId.Should().Be(s.CompanyId);
        var selection = await db.PayrollRunEmployeeSelections.AsNoTracking().SingleAsync(x => x.PayrollRunId == runId);
        (selection.EmployeeId, selection.Mode).Should().Be((s.EmployeeId, PayrollRunSelectionModes.Include));

        var second = await ctrl.CreateFinalSettlementRun(s.Id, CancellationToken.None);
        JsonSerializer.SerializeToElement(second.Should().BeOfType<OkObjectResult>().Subject.Value).GetProperty("runId").GetString()
            .Should().Be(runId.ToString(), "asking again returns the run that already holds the employee");
        (await db.PayrollRuns.CountAsync(r => r.TenantId == s.TenantId && r.SettlesFinalSettlements)).Should().Be(1);

        var list = await ctrl.ListFinalSettlements(null, null, CancellationToken.None);
        var rows = JsonSerializer.SerializeToElement(list.Should().BeOfType<OkObjectResult>().Subject.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web)).GetProperty("rows");
        var row = rows.EnumerateArray().Single(r => r.GetProperty("id").GetString() == s.Id.ToString());
        row.GetProperty("linkedRunId").GetString().Should().Be(runId.ToString());
        row.GetProperty("runDutyOpen").GetBoolean().Should().BeFalse("a run now holds the employee");
    }

    [Fact]
    public async Task The_list_flags_an_approved_settlement_with_no_run_as_a_run_still_to_create()
    {
        await using var db = CreateDb();
        var s = await ApprovedSettlementAsync(db);
        var list = await ControllerWith(db, s.TenantId, Guid.NewGuid(), new RecordingNotifications()).ListFinalSettlements(null, null, CancellationToken.None);

        var row = JsonSerializer.SerializeToElement(list.Should().BeOfType<OkObjectResult>().Subject.Value).GetProperty("rows").EnumerateArray().Single();
        row.GetProperty("runDutyOpen").GetBoolean().Should().BeTrue();
        row.GetProperty("linkedRunId").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Only_an_approved_settlement_with_no_run_can_have_a_run_created()
    {
        await using var db = CreateDb();
        var s = await ApprovedSettlementAsync(db);
        s.Status = FinalSettlementStatuses.PendingApproval;
        await db.SaveChangesAsync();

        var result = await ControllerWith(db, s.TenantId, Guid.NewGuid(), new RecordingNotifications()).CreateFinalSettlementRun(s.Id, CancellationToken.None);

        result.Should().BeOfType<ConflictObjectResult>();
        (await db.PayrollRuns.AnyAsync(r => r.TenantId == s.TenantId)).Should().BeFalse();
    }
}
