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
        int? serviceYears = 3, bool allowsHajjBeyond = false)
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var company = new Company { TenantId = tenantId, LegalNameEn = "KSA Co", CountryCode = "SA" };
        db.Companies.Add(company);
        var employee = new Employee
        {
            TenantId = tenantId, EmployeeCode = $"E-{Guid.NewGuid():N}", FullName = "Employee", EnglishName = "Employee",
            Status = "Active", CompanyId = company.Id,
            JoiningDate = serviceYears is { } y ? DateTime.UtcNow.AddYears(-y) : default,
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

    private static Task<LeaveRequest> Submit(Fixture f, DateOnly start, int calendarDays,
        DateOnly? eventDate = null, string? separateEventReason = null)
        => f.Service.SubmitRequestAsync(f.TenantId, new LeaveRequest
        {
            EmployeeId = f.Employee.Id, EmployeeName = f.Employee.FullName, LeaveTypeId = f.Type.Id,
            StartDate = start, EndDate = start.AddDays(calendarDays - 1), DayType = "Full", Reason = "Statutory",
            StatutoryEventDate = eventDate, SeparateEventReason = separateEventReason,
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

        (await Submit(f, Base.AddDays(6), 5)).TotalDays.Should().Be(5m,
            "after the first leave's window, with a day back at work between, it is a new event; the approver sees the history (below)");
    }

    [Fact]
    public async Task TheApproverSeesTheEarlierLeaveOfTheSameKind()
    {
        var f = await SeedAsync("BEREAVEMENT", "Bereavement Leave", "Bereavement");
        var earlier = await Submit(f, Base, 5);
        var later = await Submit(f, Base.AddDays(30), 5);

        var history = (await f.Service.GetKsaStatutoryLeaveHistoryAsync(f.TenantId, new[] { later.Id }))[later.Id].History;

        history.Should().ContainSingle().Which.RequestId.Should().Be(earlier.Id);
        history[0].StatutoryKind.Should().Contain("Bereavement");
        history[0].SameEvent.Should().BeFalse("a month apart is a separate event");
    }

    [Fact]
    public async Task TheApproverSeesWhenEarlierLeaveIsTheSameEvent()
    {
        var f = await SeedAsync("BEREAVEMENT", "Bereavement Leave", "Bereavement");
        var earlier = await Submit(f, Base, 2);
        var later = await Submit(f, Base.AddDays(2), 3);

        var history = (await f.Service.GetKsaStatutoryLeaveHistoryAsync(f.TenantId, new[] { later.Id }))[later.Id].History;

        history.Should().ContainSingle(h => h.RequestId == earlier.Id).Which.SameEvent.Should().BeTrue();
    }

    // ── Calendar counting for maternity and iddah ───────────────────────────────────────────

    [Fact]
    public async Task WorkingDayMaternityPolicyOfSeventy_EightyFourWorkingDays_IsRefused_EightyFourCalendarDays_IsAllowed()
    {
        // 84 working days is ~117 calendar days — about 17 weeks. The statute's 84 is 12 weeks on the
        // calendar, and a 70-working-day policy does not reach 84 working days either.
        var f = await SeedAsync("MAT", "Maternity Leave", "Parental", calendarPolicy: false, policyDays: 70m);

        var seventeenWeeks = () => Submit(f, Base, 117);
        (await seventeenWeeks.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("84 calendar day(s) per event").And.Contain("117 calendar day(s)");

        var twelveWeeks = await Submit(f, Base.AddDays(200), 84);
        twelveWeeks.EndDate.DayNumber.Should().Be(twelveWeeks.StartDate.DayNumber + 83);
    }

    [Fact]
    public async Task WorkingDayMaternityPolicyOfEightyFour_IsHonouredInItsOwnUnit()
    {
        // The law is a floor: a company that grants 84 WORKING days gives more than 12 weeks, and the
        // request is honoured as the policy counts it.
        var f = await SeedAsync("MAT", "Maternity Leave", "Parental", calendarPolicy: false, policyDays: 84m);

        var submitted = await Submit(f, Base, 117);

        submitted.TotalDays.Should().BeLessThanOrEqualTo(84m);
    }

    // ── Same event: backdated splits, and a more generous company policy ──────────────────

    [Fact]
    public async Task ABackdatedSplit_IsTheSameEvent()
    {
        // 10–12 then 7–9: the second starts BEFORE the first, and still shares its event.
        var f = await SeedAsync("MARRIAGE", "Marriage Leave", "Marriage");
        await Submit(f, Base.AddDays(10), 3);

        var backdated = () => Submit(f, Base.AddDays(7), 3);
        (await backdated.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("per event");

        (await Submit(f, Base.AddDays(8), 2)).TotalDays.Should().Be(2m, "3 + 2 is the 5 days the statute gives");
    }

    [Fact]
    public async Task FullLengthLeavesBackToBack_AreSeparateEvents_AndTheApproverSeesBoth()
    {
        // 10–14 first, then 5–9 submitted later. Each is the full statutory five days and their windows
        // do not overlap, so they are two events — two marriages, or two deaths, can be that close.
        // Nothing is hidden: the approver sees the other leave beside the request.
        var f = await SeedAsync("BEREAVEMENT", "Bereavement Leave", "Bereavement");
        var first = await Submit(f, Base.AddDays(10), 5);

        var second = await Submit(f, Base.AddDays(5), 5);

        var context = (await f.Service.GetKsaStatutoryLeaveHistoryAsync(f.TenantId, new[] { second.Id }))[second.Id];
        context.History.Should().ContainSingle(h => h.RequestId == first.Id).Which.SameEvent.Should().BeFalse();
    }

    // ── Declared separate events (bereavement, sibling bereavement, birth, marriage) ────────

    [Fact]
    public async Task ANextDaySecondBereavement_WithoutADeclaration_IsRefused()
    {
        // 3 days from the 10th; a 5-day request from the 13th starts inside that event's window.
        var f = await SeedAsync("BEREAVEMENT", "Bereavement Leave", "Bereavement");
        await Submit(f, Base.AddDays(10), 3);

        var act = () => Submit(f, Base.AddDays(13), 5);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("per event").And.Contain("giving the event date and the reason")
            .And.Contain("ask HR or use the web app to declare a separate event");
    }

    [Fact]
    public async Task ANextDaySecondBereavement_DeclaredSeparate_IsAccepted_StoredAuditedAndFlagged()
    {
        var f = await SeedAsync("BEREAVEMENT", "Bereavement Leave", "Bereavement");
        await Submit(f, Base.AddDays(10), 3, eventDate: Base.AddDays(9));

        var second = await Submit(f, Base.AddDays(13), 5, eventDate: Base.AddDays(12), separateEventReason: "Grandmother died two days after my father");

        second.TotalDays.Should().Be(5m);
        var stored = await f.Db.LeaveRequests.AsNoTracking().SingleAsync(r => r.Id == second.Id);
        (stored.StatutoryEventDate, stored.SeparateEventReason).Should().Be((Base.AddDays(12), "Grandmother died two days after my father"));
        (await f.Db.LeaveAuditLogs.SingleAsync(a => a.EntityId == second.Id.ToString() && a.Action == "StatutoryEventDeclaredSeparate"))
            .NewValue.Should().Contain(Base.AddDays(12).ToString("yyyy-MM-dd"));
        var context = (await f.Service.GetKsaStatutoryLeaveHistoryAsync(f.TenantId, new[] { second.Id }))[second.Id];
        context.SeparateEventReason.Should().Be("Grandmother died two days after my father");
        context.StatutoryKind.Should().Be("Bereavement");
        context.History.Should().ContainSingle().Which.SameEvent.Should().BeFalse();
    }

    [Fact]
    public async Task DifferentEventDatesWithoutAReason_DoNotSplitTheEvent()
    {
        // A new date alone is not a declaration: without a reason (audited, flagged) the window rule
        // decides, and the overrun is refused with the way to declare.
        var f = await SeedAsync("BEREAVEMENT", "Bereavement Leave", "Bereavement");
        await Submit(f, Base.AddDays(10), 3, eventDate: Base.AddDays(9));

        var act = () => Submit(f, Base.AddDays(13), 5, eventDate: Base.AddDays(12));

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("per event").And.Contain("declare it by giving the event date and the reason");
    }

    [Fact]
    public async Task AnEventDateBeyondTheLimit_IsRefused_AndOneAtTheLimit_IsAllowed()
    {
        var f = await SeedAsync("MARRIAGE", "Marriage Leave", "Marriage");

        var tooEarly = () => Submit(f, Base, 3, eventDate: Base.AddDays(-31));
        (await tooEarly.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("more than 30 days before the leave starts");

        (await Submit(f, Base, 3, eventDate: Base.AddDays(-30))).StatutoryEventDate.Should().Be(Base.AddDays(-30));
    }

    [Fact]
    public async Task TheEventDateLimit_IsReadFromTheStatutoryRule()
    {
        var f = await SeedAsync("MARRIAGE", "Marriage Leave", "Marriage");
        f.Db.StatutoryRules.Add(new StatutoryRule
        {
            Id = Guid.NewGuid(), TenantId = null, CountryCode = CountryCodes.Saudi, Jurisdiction = Jurisdictions.KsaMainland,
            RuleKey = KsaSpecialLeaveRuleKeys.EventDateMaxLeadDays, RuleValue = "10", DataType = "decimal",
            EffectiveFrom = new DateTime(2005, 9, 27, 0, 0, 0, DateTimeKind.Utc), Description = "test",
        });
        await f.Db.SaveChangesAsync();

        var act = () => Submit(f, Base, 3, eventDate: Base.AddDays(-11));

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("more than 10 days before the leave starts");
        (await Submit(f, Base.AddDays(40), 3, eventDate: Base.AddDays(30))).TotalDays.Should().Be(3m);
    }

    [Fact]
    public async Task ARequestSharingADeclaredEventDate_JoinsThatEvent_NotAnEarlierOne()
    {
        // A (1 day) is an earlier, approved event. B is declared separate on date Y (2 days). C names the
        // same date Y with no reason (3 days) and starts inside A's window: it joins B's event — 2 + 3 =
        // 5 days — and is not chained back to A, which would make 6.
        var f = await SeedAsync("BEREAVEMENT", "Bereavement Leave", "Bereavement");
        var a = await Submit(f, Base, 1);
        await f.Service.ApproveRequestAsync(f.TenantId, a.Id, Guid.NewGuid(), "HR", null);
        var y = Base.AddDays(1);
        var b = await Submit(f, Base.AddDays(1), 2, eventDate: y, separateEventReason: "A second death in the family");

        var c = await Submit(f, Base.AddDays(3), 3, eventDate: y);

        c.TotalDays.Should().Be(3m);
        var history = (await f.Service.GetKsaStatutoryLeaveHistoryAsync(f.TenantId, new[] { c.Id }))[c.Id].History;
        history.Single(h => h.RequestId == b.Id).SameEvent.Should().BeTrue();
        history.Single(h => h.RequestId == a.Id).SameEvent.Should().BeFalse();
    }

    [Fact]
    public async Task BirthLeave_HasItsOwnSevenDayEventDateLimit()
    {
        // Art. 113: the three days are taken within seven days of the birth.
        var f = await SeedAsync("PAT", "Paternity Leave", "Parental");

        var eightDays = () => Submit(f, Base, 3, eventDate: Base.AddDays(-8));
        (await eightDays.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("more than 7 days before the leave starts");

        (await Submit(f, Base, 3, eventDate: Base.AddDays(-7))).StatutoryEventDate.Should().Be(Base.AddDays(-7));
    }

    [Fact]
    public async Task BereavementAndMarriage_KeepTheGeneralThirtyDayLimit()
    {
        var f = await SeedAsync("BEREAVEMENT", "Bereavement Leave", "Bereavement");

        (await Submit(f, Base, 3, eventDate: Base.AddDays(-20))).TotalDays.Should().Be(3m, "20 days is within the general 30");
    }

    [Fact]
    public async Task ADeclarationNeedsAnEventDate()
    {
        var f = await SeedAsync("BEREAVEMENT", "Bereavement Leave", "Bereavement");

        var act = () => Submit(f, Base, 3, separateEventReason: "Second death");

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("give the date of the event");
    }

    [Fact]
    public async Task Maternity_WithADeclaration_IsStillRefused()
    {
        // Maternity, iddah and Hajj cannot be split into "separate events".
        var f = await SeedAsync("MAT", "Maternity Leave", "Parental");
        await Submit(f, Base, 50, eventDate: Base);

        var act = () => Submit(f, Base.AddDays(50), 50, eventDate: Base.AddDays(49), separateEventReason: "Second birth");

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("per event");
    }

    [Fact]
    public async Task TheApprovalReCheck_HonoursTheDeclaration()
    {
        var f = await SeedAsync("BEREAVEMENT", "Bereavement Leave", "Bereavement");
        var first = await Submit(f, Base.AddDays(10), 3);
        await f.Service.ApproveRequestAsync(f.TenantId, first.Id, Guid.NewGuid(), "HR", null);
        var second = await Submit(f, Base.AddDays(13), 5, eventDate: Base.AddDays(12), separateEventReason: "A second death");

        var approved = await f.Service.ApproveRequestAsync(f.TenantId, second.Id, Guid.NewGuid(), "HR", null);

        approved.Status.Should().Be("Approved");
    }

    [Fact]
    public async Task ADeclaredRequest_CannotBeApprovedByTheRequester()
    {
        // #168: the subject never decides their own request, declaration or not.
        var f = await SeedAsync("BEREAVEMENT", "Bereavement Leave", "Bereavement");
        await Submit(f, Base.AddDays(10), 3);
        var second = await Submit(f, Base.AddDays(13), 5, eventDate: Base.AddDays(12), separateEventReason: "A second death");

        var self = () => f.Service.ApproveRequestAsync(f.TenantId, second.Id, f.Employee.UserAccountId!.Value, "Employee", null);

        await self.Should().ThrowAsync<Exception>();
        (await f.Db.LeaveRequests.AsNoTracking().SingleAsync(r => r.Id == second.Id)).Status.Should().NotBe("Approved");
    }

    [Fact]
    public async Task RequestsWithTheSameEventDate_AreOneEvent_WhateverTheirDates()
    {
        // With event dates given, events group by the date rather than the window heuristic.
        var f = await SeedAsync("MARRIAGE", "Marriage Leave", "Marriage");
        await Submit(f, Base, 3, eventDate: Base);

        var act = () => Submit(f, Base.AddDays(25), 3, eventDate: Base);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("per event");
    }

    [Fact]
    public async Task AChainOfLinkedLeave_IsCountedWhole_EvenBeyondTheFirstLookup()
    {
        // A–B–C–D–E, each linked to the next, with A further from E than one lookup reaches. Under a
        // 10-day policy: 2 + 2 + 2 + 2 = 8 already, so a 3-day E makes 11 and must be refused.
        var f = await SeedAsync("BEREAVEMENT", "Bereavement Leave", "Bereavement", policyDays: 10m);
        foreach (var offset in new[] { 0, 9, 18, 27 })
            await Submit(f, Base.AddDays(offset), 2);

        var act = () => Submit(f, Base.AddDays(36), 3);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("per event");
    }

    [Fact]
    public async Task AMoreGenerousBereavementPolicy_AllowsASplitAboveTheStatute_UpToItsOwnFigure()
    {
        var f = await SeedAsync("BEREAVEMENT", "Bereavement Leave", "Bereavement", policyDays: 10m);
        await Submit(f, Base, 3);
        (await Submit(f, Base.AddDays(3), 4)).TotalDays.Should().Be(4m, "3 + 4 = 7 is above the statutory 5 but within the policy's 10");

        var over = () => Submit(f, Base.AddDays(7), 4);
        (await over.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("10 day(s) under your company policy");
    }

    [Fact]
    public async Task AMoreGenerousMaternityPolicy_AllowsFiftyPlusFifty_AndRefusesMore()
    {
        var f = await SeedAsync("MAT", "Maternity Leave", "Parental", policyDays: 100m);
        await Submit(f, Base, 50);
        (await Submit(f, Base.AddDays(50), 50)).TotalDays.Should().Be(50m, "50 + 50 is within the policy's 100 calendar days");

        var over = () => Submit(f, Base.AddDays(100), 1);
        (await over.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("per event");
    }

    // ── Hajj: missing joining date; re-checked at final approval ───────────────────────────

    [Fact]
    public async Task Hajj_WithNoJoiningDate_IsRefused_NotCountedAsZeroYears()
    {
        var f = await SeedAsync("HAJJ", "Hajj Leave", "Religious", serviceYears: null);

        var act = () => Submit(f, Base, 10);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("Joining date missing — cannot verify Hajj eligibility (Art. 114)");
    }

    [Fact]
    public async Task Hajj_IsReCheckedAtFinalApproval_AgainstApprovedHajj()
    {
        // A second Hajj request that got past submission — here because the waiver was on at the time
        // and has since been withdrawn — must still be refused at the final approval.
        var f = await SeedAsync("HAJJ", "Hajj Leave", "Religious");
        var first = await Submit(f, Base, 10);
        await f.Service.ApproveRequestAsync(f.TenantId, first.Id, Guid.NewGuid(), "HR", null);
        var policy = await f.Db.LeavePolicies.SingleAsync(p => p.Id == f.Policy.Id);
        policy.AllowsHajjBeyondStatutoryEligibility = true;
        await f.Db.SaveChangesAsync();
        var second = await Submit(f, Base.AddDays(400), 10);
        policy.AllowsHajjBeyondStatutoryEligibility = false;
        await f.Db.SaveChangesAsync();

        var approve = () => f.Service.ApproveRequestAsync(f.TenantId, second.Id, Guid.NewGuid(), "HR", null);

        (await approve.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("once in an employee's service");
    }

    [Fact]
    public async Task Approval_UsesTheKindStampedAtSubmission_NotTheEmployeesCompanyToday()
    {
        var f = await SeedAsync("MARRIAGE", "Marriage Leave", "Marriage");
        var request = await Submit(f, Base, 5);
        request.StatutoryLeaveKind.Should().Be("Marriage");
        // The employee moves to a UAE entity before the decision.
        var uae = new Company { TenantId = f.TenantId, LegalNameEn = "UAE Co", CountryCode = "AE" };
        f.Db.Companies.Add(uae);
        var tracked = await f.Db.Employees.SingleAsync(e => e.Id == f.Employee.Id);
        tracked.CompanyId = uae.Id;
        await f.Db.SaveChangesAsync();

        await f.Service.ApproveRequestAsync(f.TenantId, request.Id, Guid.NewGuid(), "HR", null);

        (await BalanceAsync(f)).Available.Should().Be(0m, "the statutory grant was booked as stamped at submission");
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
    public async Task AnAccruingBalance_TheGrantLiftsGranted_AndCancellationRestoresItExactly()
    {
        // Available is built on Granted = MAX(Entitled, Accrued). With 3 days accrued, the statutory
        // grant must lift GRANTED to cover the 5 used — and a cancellation must give back exactly that.
        var f = await SeedAsync("MARRIAGE", "Marriage Leave", "Marriage");
        f.Db.EmployeeLeaveBalances.Add(new EmployeeLeaveBalance
        {
            TenantId = f.TenantId, EmployeeId = f.Employee.Id, EmployeeName = f.Employee.FullName,
            LeaveTypeId = f.Type.Id, LeaveTypeName = f.Type.NameEn, Year = Base.Year, Accrued = 3m,
        });
        await f.Db.SaveChangesAsync();
        var request = await Submit(f, Base, 5);

        await f.Service.ApproveRequestAsync(f.TenantId, request.Id, Guid.NewGuid(), "HR", null);
        var approved = await BalanceAsync(f);
        (approved.Granted, approved.Used, approved.Available).Should().Be((5m, 5m, 0m));

        await f.Service.CancelRequestAsync(f.TenantId, request.Id, "HR", "Postponed");
        var cancelled = await BalanceAsync(f);
        (cancelled.Entitled, cancelled.Accrued, cancelled.Used, cancelled.Available).Should().Be((0m, 3m, 0m, 3m),
            "the accrued 3 days are untouched and nothing granted for the request remains");
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
