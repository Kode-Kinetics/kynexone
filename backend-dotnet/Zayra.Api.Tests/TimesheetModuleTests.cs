using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Timesheets;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Timesheets;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Timesheets, proven on the production provider (PostgreSQL, with the retrying execution
/// strategy <c>Program.cs</c> configures).
///
/// <para><b>What these tests are actually for.</b> This codebase's own post-mortem says the read
/// is the only step that gets skipped: models, migrations, controller writes and forms all get
/// built because they are demo-visible, and the thing that CONSUMES the data never does. So the
/// weight here is not on "a timesheet can be saved" — it is on the two places the hours reach
/// attendance and change an outcome:</para>
/// <list type="number">
/// <item><b>The submit gate.</b> A day claiming materially more time than attendance recorded is
/// refused at submission (<c>Submit_IsRefused_WhenADayClaimsMoreThanAttendanceRecorded</c>).</item>
/// <item><b>The persisted reconciliation.</b> Approval writes one
/// <see cref="TimesheetDayReconciliation"/> row per day, and the HR variance report reads those
/// rows (<c>Approval_WritesTheAttendanceReconciliation_AndTheVarianceReportServesIt</c>).</item>
/// </list>
///
/// <para>Every assertion below that counts rows also asserts a specific value, because an
/// assertion that would still hold on an empty result set is not a test.</para>
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class TimesheetModuleTests
{
    private readonly PostgresFixture _fx;
    public TimesheetModuleTests(PostgresFixture fx) => _fx = fx;

    // ── Period arithmetic (pure) ─────────────────────────────────────────────────────────────

    [Theory]
    // 2026-09-20 is a Sunday; the whole week collapses onto it.
    [InlineData("2026-09-20", "2026-09-20")] // Sunday itself
    [InlineData("2026-09-21", "2026-09-20")] // Monday
    [InlineData("2026-09-26", "2026-09-20")] // Saturday
    [InlineData("2026-09-27", "2026-09-27")] // next Sunday
    public void Period_StartsOnTheSundayOnOrBeforeTheDate(string date, string expectedStart)
    {
        var start = TimesheetPeriod.StartFor(DateOnly.Parse(date));
        start.Should().Be(DateOnly.Parse(expectedStart));
        start.DayOfWeek.Should().Be(DayOfWeek.Sunday);
        TimesheetPeriod.EndFor(DateOnly.Parse(date)).Should().Be(start.AddDays(6));
        TimesheetPeriod.Days(start).Should().HaveCount(7).And.OnlyHaveUniqueItems();
    }

    // ── Entry ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Entries_AreReplacedWholesale_AndTheHeaderTotalFollows()
    {
        var s = await SeedAsync();
        await using var db = _fx.CreateRetryingDb();
        var service = NewService(db);

        var sheet = await service.GetOrCreateOwnAsync(s.TenantId, s.EmployeeId, s.PeriodStart, default);
        sheet.Status.Should().Be(TimesheetStatuses.Draft);
        sheet.TotalMinutes.Should().Be(0);
        sheet.Days.Should().HaveCount(7);

        var saved = await service.SaveOwnEntriesAsync(s.TenantId, s.EmployeeId, sheet.Id, new SaveTimesheetEntriesRequest(new[]
        {
            new TimesheetEntryRequest(s.PeriodStart, s.CostCentreId, 300, "Discovery"),
            new TimesheetEntryRequest(s.PeriodStart, null, 120, "Internal"),
            new TimesheetEntryRequest(s.PeriodStart.AddDays(1), s.CostCentreId, 480, null),
            // A zero-minute cell is an empty cell, not a row.
            new TimesheetEntryRequest(s.PeriodStart.AddDays(2), s.CostCentreId, 0, "ignored"),
        }), s.EmployeeUserId, default);

        saved.Entries.Should().HaveCount(3);
        saved.TotalMinutes.Should().Be(900);
        saved.Entries.Single(e => e.Notes == "Discovery").CostCenterCode.Should().Be(s.CostCentreCode);
        saved.Version.Should().BeGreaterThan(sheet.Version);

        // Replacing, not appending.
        var replaced = await service.SaveOwnEntriesAsync(s.TenantId, s.EmployeeId, sheet.Id, new SaveTimesheetEntriesRequest(new[]
        {
            new TimesheetEntryRequest(s.PeriodStart, s.CostCentreId, 60, "One thing only"),
        }), s.EmployeeUserId, default);

        replaced.Entries.Should().ContainSingle().Which.Minutes.Should().Be(60);
        replaced.TotalMinutes.Should().Be(60);
        (await db.TimesheetEntries.CountAsync(e => e.TimesheetId == sheet.Id)).Should().Be(1);
    }

    [Fact]
    public async Task Entries_OutsideThePeriod_AreRefusedWithTheOffendingDate()
    {
        var s = await SeedAsync();
        await using var db = _fx.CreateRetryingDb();
        var service = NewService(db);
        var sheet = await service.GetOrCreateOwnAsync(s.TenantId, s.EmployeeId, s.PeriodStart, default);

        var outside = s.PeriodStart.AddDays(9);
        var ex = await Assert.ThrowsAsync<TimesheetValidationException>(() =>
            service.SaveOwnEntriesAsync(s.TenantId, s.EmployeeId, sheet.Id, new SaveTimesheetEntriesRequest(new[]
            {
                new TimesheetEntryRequest(outside, null, 60, null),
            }), s.EmployeeUserId, default));

        ex.Violations.Should().ContainSingle()
            .Which.Should().Match<TimesheetViolation>(v => v.Code == "outside_period" && v.Date == outside);
        (await db.TimesheetEntries.CountAsync(e => e.TimesheetId == sheet.Id)).Should().Be(0);
    }

    [Fact]
    public async Task OpeningTheSameWeekTwice_ReturnsTheSameTimesheet()
    {
        var s = await SeedAsync();
        await using var db = _fx.CreateRetryingDb();
        var service = NewService(db);

        var first = await service.GetOrCreateOwnAsync(s.TenantId, s.EmployeeId, s.PeriodStart, default);
        var again = await service.GetOrCreateOwnAsync(s.TenantId, s.EmployeeId, s.PeriodStart.AddDays(3), default);

        again.Id.Should().Be(first.Id, "a week is one timesheet however you navigate into it");
        (await db.Timesheets.CountAsync(t => t.TenantId == s.TenantId && t.EmployeeId == s.EmployeeId)).Should().Be(1);
    }

    // ── CONSUMER 1: the submit gate ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Submit_IsRefused_WhenADayClaimsMoreThanAttendanceRecorded()
    {
        var s = await SeedAsync();
        await using var db = _fx.CreateRetryingDb();
        var service = NewService(db);

        // Attendance says the person was on site for 8h on Monday.
        await AddAttendanceAsync(db, s, s.PeriodStart.AddDays(1), workedMinutes: 480, status: "Present");

        var sheet = await service.GetOrCreateOwnAsync(s.TenantId, s.EmployeeId, s.PeriodStart, default);
        await service.SaveOwnEntriesAsync(s.TenantId, s.EmployeeId, sheet.Id, new SaveTimesheetEntriesRequest(new[]
        {
            // 11h logged against 8h recorded: 180 minutes over, well past the 60-minute tolerance.
            new TimesheetEntryRequest(s.PeriodStart.AddDays(1), s.CostCentreId, 660, "Long day"),
        }), s.EmployeeUserId, default);

        var ex = await Assert.ThrowsAsync<TimesheetValidationException>(() =>
            service.SubmitOwnAsync(s.TenantId, s.EmployeeId, sheet.Id, Context(s), default));

        ex.Violations.Should().ContainSingle()
            .Which.Should().Match<TimesheetViolation>(v =>
                v.Code == "over_allocated" && v.Date == s.PeriodStart.AddDays(1));
        ex.Violations[0].Message.Should().Contain("8h 00m", "the message must name the attendance figure it is measured against");

        // The refusal is total: no status change and, crucially, no approval started.
        var after = await db.Timesheets.AsNoTracking().FirstAsync(t => t.Id == sheet.Id);
        after.Status.Should().Be(TimesheetStatuses.Draft);
        after.ApprovalRequestId.Should().BeNull();
        (await db.ApprovalRequests.CountAsync(a =>
            a.TenantId == s.TenantId && a.EntityId == sheet.Id.ToString())).Should().Be(0);
    }

    [Fact]
    public async Task Submit_IsAllowed_WhenTheOverrunIsInsideTolerance()
    {
        var s = await SeedAsync();
        await using var db = _fx.CreateRetryingDb();
        var service = NewService(db);

        await AddAttendanceAsync(db, s, s.PeriodStart.AddDays(1), workedMinutes: 480, status: "Present");

        var sheet = await service.GetOrCreateOwnAsync(s.TenantId, s.EmployeeId, s.PeriodStart, default);
        await service.SaveOwnEntriesAsync(s.TenantId, s.EmployeeId, sheet.Id, new SaveTimesheetEntriesRequest(new[]
        {
            // 8h30 against 8h recorded: 30 minutes over, inside the 60-minute tolerance.
            new TimesheetEntryRequest(s.PeriodStart.AddDays(1), s.CostCentreId, 510, "Ran over a little"),
        }), s.EmployeeUserId, default);

        var submitted = await service.SubmitOwnAsync(s.TenantId, s.EmployeeId, sheet.Id, Context(s), default);

        submitted.Status.Should().Be(TimesheetStatuses.Submitted);
        submitted.IsEditable.Should().BeFalse();
        submitted.ApprovalRequestId.Should().NotBeNull();
        submitted.Days.Single(d => d.Date == s.PeriodStart.AddDays(1))
            .Should().Match<TimesheetDayDto>(d => d.VarianceMinutes == 30 && !d.IsOverAllocated);
    }

    [Fact]
    public async Task Submit_IsAllowed_OnADayWithNoAttendanceRecordAtAll()
    {
        // A tenant with no biometric coverage must still be able to run timesheets; the gate only
        // fires where there is an attendance figure to disagree with.
        var s = await SeedAsync();
        await using var db = _fx.CreateRetryingDb();
        var service = NewService(db);

        var sheet = await service.GetOrCreateOwnAsync(s.TenantId, s.EmployeeId, s.PeriodStart, default);
        await service.SaveOwnEntriesAsync(s.TenantId, s.EmployeeId, sheet.Id, new SaveTimesheetEntriesRequest(new[]
        {
            new TimesheetEntryRequest(s.PeriodStart.AddDays(2), s.CostCentreId, 720, "Client site, no device"),
        }), s.EmployeeUserId, default);

        var submitted = await service.SubmitOwnAsync(s.TenantId, s.EmployeeId, sheet.Id, Context(s), default);

        submitted.Status.Should().Be(TimesheetStatuses.Submitted);
        var day = submitted.Days.Single(d => d.Date == s.PeriodStart.AddDays(2));
        day.AttendanceMinutes.Should().BeNull();
        day.AttendanceStatus.Should().Be(TimesheetReconciliationStatuses.NoRecord);
        day.IsOverAllocated.Should().BeFalse();
    }

    [Fact]
    public async Task Submit_IsRefused_WhenThereAreNoHours()
    {
        var s = await SeedAsync();
        await using var db = _fx.CreateRetryingDb();
        var service = NewService(db);
        var sheet = await service.GetOrCreateOwnAsync(s.TenantId, s.EmployeeId, s.PeriodStart, default);

        var ex = await Assert.ThrowsAsync<TimesheetValidationException>(() =>
            service.SubmitOwnAsync(s.TenantId, s.EmployeeId, sheet.Id, Context(s), default));
        ex.Violations.Should().ContainSingle().Which.Code.Should().Be("empty");
    }

    [Fact]
    public async Task Submit_WithNoTimesheetWorkflowConfigured_SaysSo_AndDoesNotInventAnApprover()
    {
        var s = await SeedAsync(installTimesheetWorkflow: false);
        await using var db = _fx.CreateRetryingDb();
        var service = NewService(db);

        var sheet = await service.GetOrCreateOwnAsync(s.TenantId, s.EmployeeId, s.PeriodStart, default);
        await service.SaveOwnEntriesAsync(s.TenantId, s.EmployeeId, sheet.Id, new SaveTimesheetEntriesRequest(new[]
        {
            new TimesheetEntryRequest(s.PeriodStart, s.CostCentreId, 300, null),
        }), s.EmployeeUserId, default);

        var ex = await Assert.ThrowsAsync<TimesheetValidationException>(() =>
            service.SubmitOwnAsync(s.TenantId, s.EmployeeId, sheet.Id, Context(s), default));
        ex.Violations.Should().ContainSingle().Which.Code.Should().Be("no_approval_route");

        (await db.Timesheets.AsNoTracking().FirstAsync(t => t.Id == sheet.Id)).Status
            .Should().Be(TimesheetStatuses.Draft);
    }

    // ── CONSUMER 2: approval writes the reconciliation, and the report reads it ──────────────

    [Fact]
    public async Task Approval_WritesTheAttendanceReconciliation_AndTheVarianceReportServesIt()
    {
        var s = await SeedAsync();
        await using var db = _fx.CreateRetryingDb();
        var service = NewService(db);

        var monday = s.PeriodStart.AddDays(1);
        var tuesday = s.PeriodStart.AddDays(2);
        var wednesday = s.PeriodStart.AddDays(3);

        await AddAttendanceAsync(db, s, monday, workedMinutes: 480, status: "Present");
        await AddAttendanceAsync(db, s, tuesday, workedMinutes: 420, status: "Present");
        // Wednesday deliberately has NO attendance record.

        var sheet = await service.GetOrCreateOwnAsync(s.TenantId, s.EmployeeId, s.PeriodStart, default);
        await service.SaveOwnEntriesAsync(s.TenantId, s.EmployeeId, sheet.Id, new SaveTimesheetEntriesRequest(new[]
        {
            new TimesheetEntryRequest(monday, s.CostCentreId, 480, "On the nose"),
            new TimesheetEntryRequest(tuesday, s.CostCentreId, 360, "Left early"),   // 60 under
            new TimesheetEntryRequest(wednesday, s.CostCentreId, 300, "Client site"),
        }), s.EmployeeUserId, default);
        var submitted = await service.SubmitOwnAsync(s.TenantId, s.EmployeeId, sheet.Id, Context(s), default);

        // Nothing is consumed until the decision.
        (await db.TimesheetDayReconciliations.CountAsync(r => r.TimesheetId == sheet.Id))
            .Should().Be(0, "an unapproved week must not appear in HR's variance report");

        await ApproveAsync(db, s, submitted.ApprovalRequestId!.Value, "Looks right.");

        // ── The consumer ran. ──
        var rows = await db.TimesheetDayReconciliations.AsNoTracking()
            .Where(r => r.TimesheetId == sheet.Id).OrderBy(r => r.WorkDate).ToListAsync();
        rows.Should().HaveCount(7, "the whole period is reconciled, including its empty days");

        var mon = rows.Single(r => r.WorkDate == monday);
        mon.LoggedMinutes.Should().Be(480);
        mon.AttendanceMinutes.Should().Be(480);
        mon.VarianceMinutes.Should().Be(0);
        mon.AttendanceStatus.Should().Be("Present");

        var tue = rows.Single(r => r.WorkDate == tuesday);
        tue.LoggedMinutes.Should().Be(360);
        tue.AttendanceMinutes.Should().Be(420);
        tue.VarianceMinutes.Should().Be(-60, "logged minus attendance, signed");

        var wed = rows.Single(r => r.WorkDate == wednesday);
        wed.LoggedMinutes.Should().Be(300);
        wed.AttendanceMinutes.Should().BeNull();
        wed.VarianceMinutes.Should().BeNull();
        wed.AttendanceStatus.Should().Be(TimesheetReconciliationStatuses.NoRecord);

        rows.Should().OnlyContain(r => r.TenantId == s.TenantId && r.CompanyId == s.CompanyId,
            "reconciliation rows are company-scoped operational data");
        rows.Should().OnlyContain(r => !r.IsOverAllocated);

        // The timesheet itself now reads Approved, with the approver's words on it.
        var decided = await db.Timesheets.AsNoTracking().FirstAsync(t => t.Id == sheet.Id);
        decided.Status.Should().Be(TimesheetStatuses.Approved);
        decided.DecisionComments.Should().Be("Looks right.");
        decided.DecidedAtUtc.Should().NotBeNull();

        // ── And the report HR actually opens serves those rows. ──
        var report = await service.GetAttendanceVarianceAsync(
            s.TenantId, s.PeriodStart, s.PeriodStart.AddDays(6), s.EmployeeId, overAllocatedOnly: false, default);
        report.Should().HaveCount(7);
        report.Sum(r => r.LoggedMinutes).Should().Be(1140);
        report.Single(r => r.WorkDate == tuesday).VarianceMinutes.Should().Be(-60);
        report.Count(r => r.AttendanceMinutes is null).Should().Be(5, "Wednesday plus the four untouched days");
    }

    [Fact]
    public async Task Rejection_ReturnsTheWeekToTheEmployee_AndConsumesNothing()
    {
        var s = await SeedAsync();
        await using var db = _fx.CreateRetryingDb();
        var service = NewService(db);

        var sheet = await service.GetOrCreateOwnAsync(s.TenantId, s.EmployeeId, s.PeriodStart, default);
        await service.SaveOwnEntriesAsync(s.TenantId, s.EmployeeId, sheet.Id, new SaveTimesheetEntriesRequest(new[]
        {
            new TimesheetEntryRequest(s.PeriodStart.AddDays(1), s.CostCentreId, 480, "Whole day"),
        }), s.EmployeeUserId, default);
        var submitted = await service.SubmitOwnAsync(s.TenantId, s.EmployeeId, sheet.Id, Context(s), default);

        await DecideAsync(db, s, submitted.ApprovalRequestId!.Value, "Reject", "Split this by cost centre, please.");

        var after = await db.Timesheets.AsNoTracking().FirstAsync(t => t.Id == sheet.Id);
        after.Status.Should().Be(TimesheetStatuses.Rejected);
        after.DecisionComments.Should().Be("Split this by cost centre, please.");
        TimesheetStatuses.IsEditable(after.Status).Should().BeTrue("a rejected week must be fixable");

        (await db.TimesheetDayReconciliations.CountAsync(r => r.TimesheetId == sheet.Id))
            .Should().Be(0, "rejected hours must never reach the variance report");

        // …and it can be corrected and resubmitted, starting a fresh approval.
        var reopened = await service.SaveOwnEntriesAsync(s.TenantId, s.EmployeeId, sheet.Id, new SaveTimesheetEntriesRequest(new[]
        {
            new TimesheetEntryRequest(s.PeriodStart.AddDays(1), s.CostCentreId, 240, "Half"),
            new TimesheetEntryRequest(s.PeriodStart.AddDays(1), null, 240, "Other half"),
        }), s.EmployeeUserId, default);
        reopened.TotalMinutes.Should().Be(480);

        var resubmitted = await service.SubmitOwnAsync(s.TenantId, s.EmployeeId, sheet.Id, Context(s), default);
        resubmitted.Status.Should().Be(TimesheetStatuses.Submitted);
        resubmitted.ApprovalRequestId.Should().NotBeNull();
        resubmitted.ApprovalRequestId!.Value.Should().NotBe(submitted.ApprovalRequestId!.Value,
            "a resubmission is a new approval, not a reopened one");
    }

    [Fact]
    public async Task OverAllocatedOnly_NarrowsTheReportToTheDaysWorthChasing()
    {
        // The over-allocation gate refuses a SUBMIT, so an over-allocated day can only reach the
        // report when attendance is corrected downwards after approval — exactly the case HR wants
        // listed. Reproduce it by editing the attendance record after the week is approved.
        var s = await SeedAsync();
        await using var db = _fx.CreateRetryingDb();
        var service = NewService(db);

        var monday = s.PeriodStart.AddDays(1);
        await AddAttendanceAsync(db, s, monday, workedMinutes: 600, status: "Present");

        var sheet = await service.GetOrCreateOwnAsync(s.TenantId, s.EmployeeId, s.PeriodStart, default);
        await service.SaveOwnEntriesAsync(s.TenantId, s.EmployeeId, sheet.Id, new SaveTimesheetEntriesRequest(new[]
        {
            new TimesheetEntryRequest(monday, s.CostCentreId, 600, "Ten hours"),
        }), s.EmployeeUserId, default);
        var submitted = await service.SubmitOwnAsync(s.TenantId, s.EmployeeId, sheet.Id, Context(s), default);

        // A supervisor corrects the punch pair down to 7h before deciding.
        var record = await db.AttendanceDailyRecords.FirstAsync(a => a.TenantId == s.TenantId && a.WorkDate == monday);
        record.TotalWorkedMinutes = 420;
        await db.SaveChangesAsync();

        await ApproveAsync(db, s, submitted.ApprovalRequestId!.Value, null);

        var all = await service.GetAttendanceVarianceAsync(
            s.TenantId, s.PeriodStart, s.PeriodStart.AddDays(6), s.EmployeeId, overAllocatedOnly: false, default);
        all.Should().HaveCount(7);

        var flagged = await service.GetAttendanceVarianceAsync(
            s.TenantId, s.PeriodStart, s.PeriodStart.AddDays(6), s.EmployeeId, overAllocatedOnly: true, default);
        flagged.Should().ContainSingle().Which.Should().Match<TimesheetVarianceRowDto>(r =>
            r.WorkDate == monday && r.LoggedMinutes == 600 && r.AttendanceMinutes == 420 && r.VarianceMinutes == 180);
    }

    // ── One approval mechanism, not two ──────────────────────────────────────────────────────

    [Fact]
    public async Task ADecisionTakenInTheApprovalCenter_ProjectsOntoTheTimesheetToo()
    {
        // The failure this guards against: a timesheet decided from the generic Approvals screen
        // (which knows nothing about timesheets) staying "Submitted" forever, with the hours never
        // reaching the reconciliation. Nothing in this test touches the timesheet API after submit.
        var s = await SeedAsync();
        await using var db = _fx.CreateRetryingDb();
        var service = NewService(db);

        await AddAttendanceAsync(db, s, s.PeriodStart.AddDays(1), workedMinutes: 480, status: "Present");
        var sheet = await service.GetOrCreateOwnAsync(s.TenantId, s.EmployeeId, s.PeriodStart, default);
        await service.SaveOwnEntriesAsync(s.TenantId, s.EmployeeId, sheet.Id, new SaveTimesheetEntriesRequest(new[]
        {
            new TimesheetEntryRequest(s.PeriodStart.AddDays(1), s.CostCentreId, 450, null),
        }), s.EmployeeUserId, default);
        var submitted = await service.SubmitOwnAsync(s.TenantId, s.EmployeeId, sheet.Id, Context(s), default);

        // Exactly the call ApprovalRequestsController.Decide makes — the generic engine, nothing else.
        var approvals = NewApprovalService(db);
        var decided = await approvals.DecideAsync(
            s.TenantId, submitted.ApprovalRequestId!.Value,
            new ApprovalDecisionRequest("Approve", "Approved from the Approval Center."), ApproverContext(s), default);
        decided!.Status.Should().Be("Approved");

        var after = await db.Timesheets.AsNoTracking().FirstAsync(t => t.Id == sheet.Id);
        after.Status.Should().Be(TimesheetStatuses.Approved);
        after.DecisionComments.Should().Be("Approved from the Approval Center.");
        (await db.TimesheetDayReconciliations.CountAsync(r => r.TimesheetId == sheet.Id)).Should().Be(7);
        (await db.TimesheetDayReconciliations.AsNoTracking()
            .Where(r => r.TimesheetId == sheet.Id && r.WorkDate == s.PeriodStart.AddDays(1))
            .Select(r => r.VarianceMinutes).FirstAsync()).Should().Be(-30);
    }

    [Fact]
    public async Task TheSubmitter_CannotApproveTheirOwnTimesheet()
    {
        // Maker-checker is the engine's, not a copy of it. Asserting it here proves the timesheet
        // path really does go through the engine rather than round it.
        var s = await SeedAsync();
        await using var db = _fx.CreateRetryingDb();
        var service = NewService(db);

        var sheet = await service.GetOrCreateOwnAsync(s.TenantId, s.EmployeeId, s.PeriodStart, default);
        await service.SaveOwnEntriesAsync(s.TenantId, s.EmployeeId, sheet.Id, new SaveTimesheetEntriesRequest(new[]
        {
            new TimesheetEntryRequest(s.PeriodStart.AddDays(1), s.CostCentreId, 300, null),
        }), s.EmployeeUserId, default);
        var submitted = await service.SubmitOwnAsync(s.TenantId, s.EmployeeId, sheet.Id, Context(s), default);

        var approvals = NewApprovalService(db);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => approvals.DecideAsync(
            s.TenantId, submitted.ApprovalRequestId!.Value,
            new ApprovalDecisionRequest("Approve", "me again"),
            // The employee's OWN context — the same user who submitted.
            Context(s), default));
        ex.Message.Should().Contain("Maker-checker");

        (await db.Timesheets.AsNoTracking().FirstAsync(t => t.Id == sheet.Id)).Status
            .Should().Be(TimesheetStatuses.Submitted);
        (await db.TimesheetDayReconciliations.CountAsync(r => r.TimesheetId == sheet.Id)).Should().Be(0);
    }

    // ── Multi-tenancy ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ACompanyScopedUser_SeesNeitherAnotherCompanysTimesheetNorItsReconciliation()
    {
        var a = await SeedAsync();
        var b = await SeedAsync(tenantId: a.TenantId);   // same tenant, different company

        await using var seedDb = _fx.CreateRetryingDb();
        var service = NewService(seedDb);
        foreach (var s in new[] { a, b })
        {
            var sheet = await service.GetOrCreateOwnAsync(s.TenantId, s.EmployeeId, s.PeriodStart, default);
            await service.SaveOwnEntriesAsync(s.TenantId, s.EmployeeId, sheet.Id, new SaveTimesheetEntriesRequest(new[]
            {
                new TimesheetEntryRequest(s.PeriodStart.AddDays(1), s.CostCentreId, 300, null),
            }), s.EmployeeUserId, default);
            var submitted = await service.SubmitOwnAsync(s.TenantId, s.EmployeeId, sheet.Id, Context(s), default);
            await ApproveAsync(seedDb, s, submitted.ApprovalRequestId!.Value, null);
        }

        // Sanity: with no ambient request scope both weeks exist.
        (await seedDb.Timesheets.CountAsync(t => t.TenantId == a.TenantId)).Should().Be(2);

        // Now read as a user scoped to company A only. The global filter is the one applied by
        // reflection to every ICompanyScopedOperational entity; nothing here filters by hand.
        var accessor = new SwitchableAccessor { HttpContext = ScopedContext(a.TenantId, a.CompanyId) };
        await using var scoped = _fx.CreateDbWithAccessor(accessor);

        var visibleSheets = await scoped.Timesheets.ToListAsync();
        visibleSheets.Should().ContainSingle("company A's user sees company A's week and no other");
        visibleSheets[0].CompanyId.Should().Be(a.CompanyId);

        var visibleEntries = await scoped.TimesheetEntries.ToListAsync();
        visibleEntries.Should().ContainSingle().Which.CompanyId.Should().Be(a.CompanyId);

        var visibleRecon = await scoped.TimesheetDayReconciliations.ToListAsync();
        visibleRecon.Should().HaveCount(7).And.OnlyContain(r => r.CompanyId == a.CompanyId);

        // Flip to company B: the other week, and only the other week.
        accessor.HttpContext = ScopedContext(a.TenantId, b.CompanyId);
        (await scoped.Timesheets.Select(t => t.CompanyId).ToListAsync())
            .Should().ContainSingle().Which.Should().Be(b.CompanyId);
        (await scoped.TimesheetDayReconciliations.CountAsync(r => r.CompanyId == a.CompanyId))
            .Should().Be(0, "company A's reconciliation must be invisible to company B");
    }

    [Fact]
    public async Task TheVarianceReport_NeverCrossesATenantBoundary()
    {
        var a = await SeedAsync();
        var other = await SeedAsync();   // a different tenant entirely

        await using var db = _fx.CreateRetryingDb();
        var service = NewService(db);
        foreach (var s in new[] { a, other })
        {
            var sheet = await service.GetOrCreateOwnAsync(s.TenantId, s.EmployeeId, s.PeriodStart, default);
            await service.SaveOwnEntriesAsync(s.TenantId, s.EmployeeId, sheet.Id, new SaveTimesheetEntriesRequest(new[]
            {
                new TimesheetEntryRequest(s.PeriodStart.AddDays(1), s.CostCentreId, 240, null),
            }), s.EmployeeUserId, default);
            var submitted = await service.SubmitOwnAsync(s.TenantId, s.EmployeeId, sheet.Id, Context(s), default);
            await ApproveAsync(db, s, submitted.ApprovalRequestId!.Value, null);
        }

        var report = await service.GetAttendanceVarianceAsync(
            a.TenantId, a.PeriodStart, a.PeriodStart.AddDays(6), employeeId: null, overAllocatedOnly: false, default);

        report.Should().NotBeEmpty("an empty report would make every assertion below vacuous");
        report.Should().OnlyContain(r => r.EmployeeId == a.EmployeeId);
        report.Should().NotContain(r => r.EmployeeId == other.EmployeeId);
        report.Sum(r => r.LoggedMinutes).Should().Be(240);
    }

    // ══ Harness ═════════════════════════════════════════════════════════════════════════════

    private sealed record Scenario(
        Guid TenantId, Guid CompanyId, int EmployeeId, Guid EmployeeUserId,
        int ApproverEmployeeId, Guid ApproverUserId, Guid CostCentreId, string CostCentreCode,
        DateOnly PeriodStart);

    private static TimesheetService NewService(ZayraDbContext db) => new(db, NewApprovalService(db));

    private static ApprovalWorkflowService NewApprovalService(ZayraDbContext db) =>
        new(db, new AuditService(db));

    /// <summary>
    /// A tenant (or another company inside one), an employee with a login, an HR Manager to
    /// approve, a cost centre, and — unless told otherwise — the TIMESHEET-DEFAULT workflow every
    /// provisioned tenant gets from <c>TenantProvisioningBundle</c>.
    /// </summary>
    private async Task<Scenario> SeedAsync(Guid? tenantId = null, bool installTimesheetWorkflow = true)
    {
        await using var db = _fx.CreateRetryingDb();
        var tid = tenantId ?? await PostgresFixture.SeedMinimalTenant(db);
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var company = new Company
        {
            Id = Guid.NewGuid(), TenantId = tid, LegalNameEn = $"TS Co {suffix}",
            CountryCode = "SAU", Jurisdiction = "KSA-mainland", RegistrationNumber = $"REG-{suffix}",
            DefaultCurrency = "SAR", IsActive = true,
            CreatedAtUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        var costCentre = new CostCenter
        {
            TenantId = tid, CompanyId = company.Id, Code = $"CC-{suffix}", Name = "Delivery", IsActive = true
        };
        var approverUserId = Guid.NewGuid();
        var approver = new Employee
        {
            TenantId = tid, CompanyId = company.Id, UserAccountId = approverUserId,
            EmployeeCode = $"HR-{suffix}", FullName = "Timesheet HR Manager",
            Department = "HR", Designation = "HR Manager", Status = "Active",
            JoiningDate = DateTime.UtcNow.AddYears(-4)
        };
        db.Companies.Add(company);
        db.CostCenters.Add(costCentre);
        db.Employees.Add(approver);
        await db.SaveChangesAsync();

        var employeeUserId = Guid.NewGuid();
        var employee = new Employee
        {
            TenantId = tid, CompanyId = company.Id, UserAccountId = employeeUserId,
            EmployeeCode = $"EMP-{suffix}", FullName = $"Timesheet Employee {suffix}",
            Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-2),
            ManagerEmployeeId = approver.Id, CostCenterId = costCentre.Id
        };
        db.Employees.Add(employee);

        if (installTimesheetWorkflow
            && !await db.ApprovalWorkflows.AnyAsync(w => w.TenantId == tid
                && w.EntityName == TimesheetConstants.ApprovalEntityName && w.IsActive))
        {
            var workflow = new ApprovalWorkflow
            {
                TenantId = tid, Code = $"TIMESHEET-DEFAULT-{suffix}", Name = "Default Timesheet Approval",
                EntityName = TimesheetConstants.ApprovalEntityName, IsDefault = true, IsActive = true
            };
            workflow.Steps.Add(new ApprovalWorkflowStep
            {
                TenantId = tid, WorkflowId = workflow.Id, StepOrder = 1, StepName = "HR Approval",
                ApproverType = "HR", ApproverRole = "HR Manager", IsFinalStep = true
            });
            db.ApprovalWorkflows.Add(workflow);
        }
        await db.SaveChangesAsync();

        // A fixed Sunday well clear of "today", so a test can never straddle a real week boundary.
        return new Scenario(tid, company.Id, employee.Id, employeeUserId,
            approver.Id, approverUserId, costCentre.Id, costCentre.Code,
            new DateOnly(2026, 3, 1));
    }

    private static async Task AddAttendanceAsync(ZayraDbContext db, Scenario s, DateOnly date, int workedMinutes, string status)
    {
        db.AttendanceDailyRecords.Add(new AttendanceDailyRecord
        {
            TenantId = s.TenantId,
            EmployeeId = s.EmployeeId,
            EmployeeName = "Timesheet Employee",
            WorkDate = date,
            TotalWorkedMinutes = workedMinutes,
            Status = status
        });
        await db.SaveChangesAsync();
    }

    private static Task ApproveAsync(ZayraDbContext db, Scenario s, Guid approvalId, string? comments)
        => DecideAsync(db, s, approvalId, "Approve", comments);

    private static async Task DecideAsync(ZayraDbContext db, Scenario s, Guid approvalId, string decision, string? comments)
    {
        var result = await NewApprovalService(db).DecideAsync(
            s.TenantId, approvalId, new ApprovalDecisionRequest(decision, comments), ApproverContext(s), default);
        result.Should().NotBeNull();
    }

    private static RequestContext Context(Scenario s) => new(
        "127.0.0.1", "tests", s.EmployeeUserId, s.TenantId,
        new[] { "Employee" }, new[] { "ess.read", "ess.write" });

    /// <summary>The HR Manager who owns the "HR Manager" role queue the default workflow routes to.</summary>
    private static RequestContext ApproverContext(Scenario s) => new(
        "127.0.0.1", "tests", s.ApproverUserId, s.TenantId,
        new[] { "HR Manager" }, new[] { "approvals.read", "approvals.decide" });

    private static HttpContext ScopedContext(Guid tenantId, Guid companyId)
    {
        var ctx = new DefaultHttpContext();
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim("entity_access", JsonSerializer.Serialize(new { c = companyId, r = "Viewer" })),
        }, "Test"));
        return ctx;
    }

    private sealed class SwitchableAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }
}
