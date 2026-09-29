using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Controllers.Leave;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Controllers;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Defects where A NUMBER A CUSTOMER SEES IS WRONG.
///
///   • Defect 3 — a rejected leave cancellation resurrected a dead leave, spending the same leave
///     balance twice.
///   • Defect 4 — Art. 85 was applied to every resignation, with no Art. 87 or Art. 81 exception,
///     so a settlement was under-paid by a third or two thirds.
///
/// Defect 1 (the GOSI ceiling) is pinned in <c>GosiTests</c>, beside the calculator it belongs to.
/// Defect 2 (the Art. 98 Ramadan gate) is pinned in <c>KsaStatutoryLeaveAndHoursTests</c>.
/// </summary>
public class MoneyFigureDefectTests
{
    // ─────────────────────────────────────────────────────────────────────────
    //  Defect 3 — rejecting a cancellation must not resurrect a dead leave
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// THE DEFECT. <c>RejectCancellation</c> ended with an unconditional
    /// <c>leave.Status = "Approved"</c>. A leave that had since been WITHDRAWN — releasing its days
    /// back into the employee's balance — was raised from the dead as Approved by a stale pending
    /// cancellation row, while the released days stayed released. The employee then held an approved
    /// absence AND the days back in their entitlement: the same balance spent twice.
    /// </summary>
    [Fact]
    public async Task RejectCancellation_OnAWithdrawnLeave_IsRefused_AndDoesNotResurrectIt()
    {
        await using var db = NewDb();
        var f = await SeedLeaveAwaitingCancellationDecisionAsync(db);

        // The leave moves on behind the cancellation request's back: it is withdrawn, and its
        // reservation is released.
        f.Leave.Status = "Withdrawn";
        await db.SaveChangesAsync();

        var result = await ControllerFor(db, f).RejectCancellation(
            f.Leave.Id, new CancellationDecisionRequest("not approving this"), CancellationToken.None);

        result.Should().BeOfType<ConflictObjectResult>(
            "a cancellation decision on a leave that has already moved on is a stale decision");

        var reloaded = await db.LeaveRequests.SingleAsync(x => x.Id == f.Leave.Id);
        reloaded.Status.Should().Be("Withdrawn", "the dead leave must stay dead");

        var cancellation = await db.LeaveCancellationRequests.SingleAsync();
        cancellation.Status.Should().Be("Pending",
            "a refused decision must not half-apply — the cancellation row is untouched too");
    }

    /// <summary>
    /// The same guard for a CANCELLED leave, which is the other way the balance has already been
    /// released before the stale decision arrives.
    /// </summary>
    [Fact]
    public async Task RejectCancellation_OnACancelledLeave_IsRefused()
    {
        await using var db = NewDb();
        var f = await SeedLeaveAwaitingCancellationDecisionAsync(db);
        f.Leave.Status = "Cancelled";
        await db.SaveChangesAsync();

        var result = await ControllerFor(db, f).RejectCancellation(
            f.Leave.Id, new CancellationDecisionRequest(null), CancellationToken.None);

        result.Should().BeOfType<ConflictObjectResult>();
        (await db.LeaveRequests.SingleAsync(x => x.Id == f.Leave.Id)).Status.Should().Be("Cancelled");
    }

    /// <summary>
    /// The guard must not break the case it exists to allow: a leave genuinely awaiting the decision
    /// reverts to Approved, which is the state POST /{id}/cancel moved it out of.
    /// </summary>
    [Fact]
    public async Task RejectCancellation_OnALeaveAwaitingTheDecision_StillRevertsItToApproved()
    {
        await using var db = NewDb();
        var f = await SeedLeaveAwaitingCancellationDecisionAsync(db);

        var result = await ControllerFor(db, f).RejectCancellation(
            f.Leave.Id, new CancellationDecisionRequest("staffing need stands"), CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();

        var reloaded = await db.LeaveRequests.SingleAsync(x => x.Id == f.Leave.Id);
        reloaded.Status.Should().Be("Approved");

        var cancellation = await db.LeaveCancellationRequests.SingleAsync();
        cancellation.Status.Should().Be("Rejected");
    }

    /// <summary>
    /// The endpoint used to return the raw <c>LeaveRequest</c> entity, serializing TenantId,
    /// CompanyId and every field later added to the model. It now returns a projection.
    /// </summary>
    [Fact]
    public async Task RejectCancellation_ReturnsADto_NotTheRawEntity()
    {
        await using var db = NewDb();
        var f = await SeedLeaveAwaitingCancellationDecisionAsync(db);

        var ok = Assert.IsType<OkObjectResult>(await ControllerFor(db, f).RejectCancellation(
            f.Leave.Id, new CancellationDecisionRequest(null), CancellationToken.None));

        ok.Value.Should().BeOfType<LeaveRequestStateDto>();
        ok.Value.Should().NotBeOfType<LeaveRequest>();
        var dto = (LeaveRequestStateDto)ok.Value!;
        dto.Id.Should().Be(f.Leave.Id);
        dto.Status.Should().Be("Approved");
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Defect 4 — Art. 85 without the Art. 87 / Art. 81 exceptions
    // ─────────────────────────────────────────────────────────────────────────
    //
    // Fixture: SAR 10,000 basic, no other components, four years of service to the day.
    //   Art. 84 tier 1 = 4 yrs x 15 days/yr / 30 x 10,000 = SAR 20,000.
    //   Art. 85 at 2–5 years reduces that to one third = SAR 6,666.67.
    //   Art. 87 and Art. 81 are express exceptions: the FULL SAR 20,000 is due.

    private static readonly DateOnly ServiceStart = new(2021, 1, 1);
    private static readonly DateOnly ServiceEnd = new(2025, 1, 1);
    private const decimal FullArt84Award = 20_000m;

    private static EndOfServiceInput Eosb(string reason) => new(
        Guid.NewGuid(), Guid.NewGuid(),
        new SalaryBreakdown(10_000m, 0m, 0m, 0m),
        ServiceStart, ServiceEnd, reason, "Unlimited", "SA");

    /// <summary>Anchors the arithmetic: this is the undisputed full Art. 84 award for the fixture.</summary>
    [Fact]
    public async Task Eosb_EmployerTermination_PaysTheFullArt84Award()
    {
        var result = await new KsaEndOfServiceCalculator(new StubRuleReader()).CalculateAsync(Eosb("Termination"));
        result.TotalGratuity.Should().Be(FullArt84Award);
    }

    /// <summary>And this is the Art. 85 reduction the exceptions must displace.</summary>
    [Fact]
    public async Task Eosb_OrdinaryResignation_IsStillReducedByArt85()
    {
        var result = await new KsaEndOfServiceCalculator(new StubRuleReader()).CalculateAsync(Eosb("Resignation"));
        result.TotalGratuity.Should().Be(6_666.67m, "2–5 years of service attracts the one-third band");
    }

    /// <summary>
    /// THE DEFECT. Art. 87: "As an exception to Article 85, a worker is entitled to the FULL
    /// end-of-service award if they leave owing to force majeure beyond their control. Likewise a
    /// female worker who terminates the contract within six months of her marriage or within three
    /// months of giving birth." There was no exception path at all, so such a resignation was cut to
    /// SAR 6,666.67 — SAR 13,333.33 short of the SAR 20,000 owed.
    /// </summary>
    [Fact]
    public async Task Eosb_Article87Resignation_PaysTheFullAward_NotTheArt85Third()
    {
        var result = await new KsaEndOfServiceCalculator(new StubRuleReader()).CalculateAsync(Eosb("Article87"));

        result.TotalGratuity.Should().Be(FullArt84Award,
            "Art. 87 is an express exception to Art. 85 — marriage, childbirth or force majeure");
        result.TotalGratuity.Should().BeGreaterThan(6_666.67m);
    }

    /// <summary>
    /// Art. 81: the worker leaves without notice because the EMPLOYER is at fault, and retains full
    /// statutory rights. The Art. 85 reduction does not reach them either.
    /// </summary>
    [Fact]
    public async Task Eosb_Article81Departure_PaysTheFullAward()
    {
        var result = await new KsaEndOfServiceCalculator(new StubRuleReader()).CalculateAsync(Eosb("Article81"));
        result.TotalGratuity.Should().Be(FullArt84Award);
    }

    /// <summary>
    /// The exception moves money towards the employee on the strength of something a human asserted,
    /// so it is announced rather than applied silently — a reviewer must be able to see the claim and
    /// challenge it before the settlement is paid.
    /// </summary>
    [Fact]
    public async Task Eosb_Article87And81_AnnounceWhyNoReductionWasApplied()
    {
        var calc = new KsaEndOfServiceCalculator(new StubRuleReader());

        var art87 = await calc.CalculateAsync(Eosb("Article87"));
        art87.Notices.Should().Contain(n => n.Contains("Art. 87") && n.Contains("[CERT-KSA]"));
        art87.Notices.Should().Contain(n =>
            n.Contains("SIX months", StringComparison.OrdinalIgnoreCase)
            && n.Contains("THREE months", StringComparison.OrdinalIgnoreCase));

        var art81 = await calc.CalculateAsync(Eosb("Article81"));
        art81.Notices.Should().Contain(n => n.Contains("Art. 81") && n.Contains("[CERT-KSA]"));
    }

    /// <summary>
    /// Art. 80 is untouched by the new branches: a dismissal for grave fault still forfeits
    /// everything. The exceptions lift the Art. 85 REDUCTION; they are not a general amnesty.
    /// </summary>
    [Fact]
    public async Task Eosb_Article80Forfeiture_IsUnaffectedByTheNewExceptions()
    {
        var result = await new KsaEndOfServiceCalculator(new StubRuleReader()).CalculateAsync(Eosb("Article80"));
        result.TotalGratuity.Should().Be(0m);
    }

    /// <summary>
    /// The codes must survive the trip from the offboarding record to the calculator. Falling
    /// through <c>NormalizeTerminationReason</c> as "Resignation" would silently reapply the very
    /// haircut Art. 87 and Art. 81 exist to prevent.
    /// </summary>
    [Theory]
    [InlineData("Article87")]
    [InlineData("Art87")]
    [InlineData("article87")]
    public void Art87Aliases_AreRecognisedAsTheException(string raw)
        => KsaEndOfServiceCalculator.IsArticle87Exception(
            Zayra.Api.Controllers.PayrollController.NormalizeTerminationReason(raw)).Should().BeTrue();

    [Theory]
    [InlineData("Article81")]
    [InlineData("Art81")]
    public void Art81Aliases_AreRecognisedAsTheException(string raw)
        => KsaEndOfServiceCalculator.IsArticle81Exception(
            Zayra.Api.Controllers.PayrollController.NormalizeTerminationReason(raw)).Should().BeTrue();

    /// <summary>An ordinary resignation must still normalise to the reduced path.</summary>
    [Fact]
    public void OrdinaryResignation_IsNotMistakenForAnException()
    {
        var normalised = Zayra.Api.Controllers.PayrollController.NormalizeTerminationReason("Resignation");
        KsaEndOfServiceCalculator.IsArticle87Exception(normalised).Should().BeFalse();
        KsaEndOfServiceCalculator.IsArticle81Exception(normalised).Should().BeFalse();
    }

    /// <summary>
    /// THE MONEY, AND WHERE THE DEFECT ACTUALLY BIT. The calculator passes an unrecognised reason
    /// through to the full award, so on develop the Art. 87 case was not lost inside the arithmetic —
    /// it was lost before it got there. The separation vocabulary is CLOSED and fails shut, and it
    /// held no Art. 87 or Art. 81 code, so <c>NormalizeSeparationType</c> THREW on one. The only
    /// thing an operator could record for a woman resigning within six months of her marriage was
    /// "Resignation", and that is what the Art. 85 haircut was then applied to.
    ///
    /// SAR 20,000 owed, SAR 6,666.67 paid, SAR 13,333.33 short — and no way to say otherwise.
    /// </summary>
    [Fact]
    public void SeparationVocabulary_CanRecordTheArt85Exceptions()
    {
        EmployeeManagementService.NormalizeSeparationType("Article87").Should().Be("Article87");
        EmployeeManagementService.NormalizeSeparationType("Article81").Should().Be("Article81");

        // And the screen can offer them, with the consequence spelled out.
        var art87 = SeparationTypeCatalog.All.Should().ContainSingle(t => t.Code == "Article87").Subject;
        art87.ForfeitsEndOfServiceAward.Should().BeFalse();
        art87.RequiresReason.Should().BeTrue("the qualifying ground and date must be recorded");
        art87.Description.Should().Contain("Art. 87");
    }

    /// <summary>
    /// The whole path, as the product walks it: what the offboarding screen records, through the
    /// reason normaliser the payroll run uses, into the award. This is the figure that changes.
    /// </summary>
    [Fact]
    public async Task Eosb_Art87Leaver_FromRecordedSeparationTypeThroughToTheAward()
    {
        var recorded = EmployeeManagementService.NormalizeSeparationType("Article87");
        var reason = Zayra.Api.Controllers.PayrollController.NormalizeTerminationReason(recorded);
        var result = await new KsaEndOfServiceCalculator(new StubRuleReader()).CalculateAsync(Eosb(reason));

        result.TotalGratuity.Should().Be(FullArt84Award);

        // What the same leaver received while "Resignation" was the only recordable code.
        var asResignation = await new KsaEndOfServiceCalculator(new StubRuleReader())
            .CalculateAsync(Eosb("Resignation"));
        (result.TotalGratuity - asResignation.TotalGratuity).Should().Be(13_333.33m);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Fixtures
    // ─────────────────────────────────────────────────────────────────────────

    private static ZayraDbContext NewDb() => new(new DbContextOptionsBuilder<ZayraDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed record LeaveFixture(Guid TenantId, Employee Employee, LeaveRequest Leave, int ApproverEmployeeId);

    /// <summary>
    /// An approved leave for which a cancellation has been requested: the leave sits in
    /// "CancellationRequested" and a Pending LeaveCancellationRequest awaits a decision. This is the
    /// exact state POST /{id}/cancel leaves behind for an Approved leave.
    /// </summary>
    private static async Task<LeaveFixture> SeedLeaveAwaitingCancellationDecisionAsync(ZayraDbContext db)
    {
        var tenantId = Guid.NewGuid();
        var leaveType = new LeaveType { TenantId = tenantId, Code = "AL", NameEn = "Annual", IsActive = true };
        var employee = new Employee
        {
            TenantId = tenantId, EmployeeCode = "LV-1", FullName = "Leave Taker", EnglishName = "Leave Taker",
            Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-3),
        };
        var approver = new Employee
        {
            TenantId = tenantId, EmployeeCode = "LV-MGR", FullName = "Approver", EnglishName = "Approver",
            Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-5),
        };
        db.AddRange(leaveType, employee, approver);
        await db.SaveChangesAsync();

        var leave = new LeaveRequest
        {
            TenantId = tenantId, EmployeeId = employee.Id, EmployeeName = employee.FullName,
            LeaveTypeId = leaveType.Id, LeaveTypeName = leaveType.NameEn,
            StartDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10)),
            EndDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(12)),
            TotalDays = 3m, DayType = "Full", Status = "CancellationRequested",
        };
        db.LeaveRequests.Add(leave);
        db.LeaveCancellationRequests.Add(new LeaveCancellationRequest
        {
            TenantId = tenantId, LeaveRequestId = leave.Id, EmployeeId = employee.Id,
            Reason = "plans changed", Status = "Pending",
        });
        await db.SaveChangesAsync();

        return new LeaveFixture(tenantId, employee, leave, approver.Id);
    }

    private static LeaveRequestsController ControllerFor(ZayraDbContext db, LeaveFixture f) =>
        new(db,
            new Zayra.Api.Infrastructure.Leave.LeaveService(db, new Zayra.Api.Infrastructure.Approvals.ApprovalRouter(db)),
            new ApproverScope(f.ApproverEmployeeId),
            new NullNotifications())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim("tenant_id", f.TenantId.ToString()),
                        new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                        new Claim(ClaimTypes.Role, "HR Manager"),
                    ], "Test")),
                },
            },
        };

    /// <summary>An org-wide approver who is NOT the leave's own employee (self-decision is forbidden).</summary>
    private sealed class ApproverScope(int callerEmployeeId) : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) =>
            Task.FromResult(new DataScope
            {
                Level = DataScopeLevel.Organization,
                CallerEmployeeId = callerEmployeeId,
                AllowedEmployeeIds = null,   // unrestricted
            });
    }

    private sealed class NullNotifications : INotificationService
    {
        public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message,
            string entityName, string? entityId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName,
            Dictionary<string, string> variables, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
