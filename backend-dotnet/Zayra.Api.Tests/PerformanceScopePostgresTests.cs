using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Performance;
using Zayra.Api.Controllers.Performance;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Performance;
using Zayra.Api.Models;
using PerformanceAnalyticsController = Zayra.Api.Controllers.Performance.AnalyticsController;
using ReportsAnalyticsController = Zayra.Api.Controllers.Reports.AnalyticsController;

namespace Zayra.Api.Tests;

/// <summary>
/// Data scope and permission gates across the Performance area, which the Succession module builds on.
///
/// <para>Every scope test is NON-VACUOUS: the in-scope company's own rows must be present, so an
/// endpoint that returns nothing cannot pass. Two companies share one tenant and a second tenant's
/// review deliberately reuses the first tenant's cycle id, so neither boundary can pass by accident.</para>
///
/// <para>Controllers are built through <see cref="ActivatorUtilities"/> so constructor changes do not
/// break the tests.</para>
/// </summary>
[Collection("Integration")]
[Trait("Category", "Integration")]
public sealed class PerformanceScopePostgresTests
{
    private readonly PostgresFixture _fx;
    public PerformanceScopePostgresTests(PostgresFixture fx) => _fx = fx;

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // Calibration board and analytics stay inside the caller's companies
    // ═══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CalibrationBoard_ForCompanyScopedHrManager_NeverIncludesAnotherCompanysEmployees()
    {
        var s = await SeedTwoCompaniesAsync();
        var hr = CompanyHrManager(s.TenantId, s.CompanyA);
        await using var db = _fx.CreateDbWithAccessor(Accessor(hr));
        var controller = Build<CalibrationController>(db, hr);

        var payload = JsonPayload(await controller.GetBoard(s.CycleId, null, default));

        var ids = payload.GetProperty("reviews").EnumerateArray()
            .Select(r => r.GetProperty("employeeId").GetInt32()).ToList();
        ids.Should().BeEquivalentTo(new[] { s.AlphaTop, s.AlphaLow }, "company A's own reviews are on its board");
        payload.GetProperty("totalReviews").GetInt32().Should().Be(2);
        AssertNoForeignNames(payload.GetRawText());
    }

    [Fact]
    public async Task CalibrationAdjust_OnAnotherCompanysReview_IsRefusedAndWritesNothing()
    {
        var s = await SeedTwoCompaniesAsync();
        var hr = CompanyHrManager(s.TenantId, s.CompanyA);
        await using var db = _fx.CreateDbWithAccessor(Accessor(hr));
        var controller = Build<CalibrationController>(db, hr);

        IActionResult result;
        try
        {
            result = await controller.AdjustScore(s.CycleId,
                new CalibrationAdjustRequest(s.BetaTopReview, -20m, "Cross-company calibration attempt"), default);
        }
        catch (Exception ex)
        {
            throw new Xunit.Sdk.XunitException($"cross-company adjust must be refused, not attempted: {ex.GetType().Name}: {ex.Message}");
        }

        result.Should().BeOfType<NotFoundResult>("a review outside the caller's companies does not exist for them");
        await using var verify = _fx.CreateDb();
        (await verify.AppraisalCalibrations.CountAsync(c => c.ReviewId == s.BetaTopReview && c.AdjustmentReason == "Cross-company calibration attempt"))
            .Should().Be(0);
        (await verify.AppraisalReviews.AsNoTracking().SingleAsync(r => r.Id == s.BetaTopReview))
            .CalibrationAdjustment.Should().Be(0m);
    }

    [Fact]
    public async Task CalibrationBoard_AfterAReviewIsAdjustedTwice_StillLoadsAndShowsTheLatestRecord()
    {
        var s = await SeedTwoCompaniesAsync();
        await using (var seed = _fx.CreateDb())
        {
            var review = await seed.AppraisalReviews.SingleAsync(r => r.TenantId == s.TenantId && r.EmployeeId == s.AlphaTop);
            seed.AppraisalCalibrations.AddRange(
                new AppraisalCalibration { TenantId = s.TenantId, ReviewId = review.Id, CycleId = s.CycleId, EmployeeName = "Alpha Topper", OriginalScore = 80m, AdjustedScore = 85m, AdjustmentReason = "first", CalibratedAtUtc = DateTime.UtcNow.AddHours(-2) },
                new AppraisalCalibration { TenantId = s.TenantId, ReviewId = review.Id, CycleId = s.CycleId, EmployeeName = "Alpha Topper", OriginalScore = 85m, AdjustedScore = 92m, AdjustmentReason = "second", CalibratedAtUtc = DateTime.UtcNow.AddHours(-1) });
            await seed.SaveChangesAsync();
        }

        var hr = CompanyHrManager(s.TenantId, s.CompanyA);
        await using var db = _fx.CreateDbWithAccessor(Accessor(hr));
        var payload = JsonPayload(await Build<CalibrationController>(db, hr).GetBoard(s.CycleId, null, default));

        payload.GetProperty("reviews").EnumerateArray()
            .Single(r => r.GetProperty("employeeId").GetInt32() == s.AlphaTop)
            .GetProperty("calibrationRecord").GetProperty("adjustmentReason").GetString()
            .Should().Be("second", "every adjustment appends a record; the board shows the latest");
    }

    [Fact]
    public async Task CycleAnalytics_ForCompanyScopedHrManager_NamesOnlyItsOwnCompanysPerformers()
    {
        var s = await SeedTwoCompaniesAsync();
        var hr = CompanyHrManager(s.TenantId, s.CompanyA);
        await using var db = _fx.CreateDbWithAccessor(Accessor(hr));
        var controller = Build<PerformanceAnalyticsController>(db, hr);

        var payload = JsonPayload(await controller.CycleAnalytics(s.CycleId, default));

        payload.GetProperty("topPerformers").EnumerateArray().Select(x => x.GetProperty("employeeId").GetInt32())
            .Should().Equal(s.AlphaTop);
        payload.GetProperty("lowPerformers").EnumerateArray().Select(x => x.GetProperty("employeeId").GetInt32())
            .Should().Equal(s.AlphaLow);
        payload.GetProperty("summary").GetProperty("totalEnrolled").GetInt32().Should().Be(2);
        payload.GetProperty("calibrations").GetArrayLength().Should().Be(0, "the only calibration is company B's");
        AssertNoForeignNames(payload.GetRawText());
    }

    [Fact]
    public async Task PerformanceDashboard_ForCompanyScopedHrManager_CountsAndAuditRowsStayInsideItsCompanies()
    {
        var s = await SeedTwoCompaniesAsync();
        var hr = CompanyHrManager(s.TenantId, s.CompanyA);
        await using var db = _fx.CreateDbWithAccessor(Accessor(hr));
        var controller = Build<PerformanceAnalyticsController>(db, hr);

        var payload = JsonPayload(await controller.Dashboard(default));

        payload.GetProperty("pendingActions").GetProperty("managerReviewPending").GetInt32()
            .Should().Be(1, "only company A's one review is awaiting its manager; company B's is not this caller's");
        var raw = payload.GetRawText();
        raw.Should().Contain("Alpha audit reason", "company A's own audit trail is still shown");
        raw.Should().NotContain("Beta audit reason");

        var goals = JsonPayload(await controller.GoalsCompletion(s.CycleId, default));
        goals.GetProperty("total").GetInt32().Should().Be(1, "company B's goal is not counted for company A's HR");
    }

    [Fact]
    public async Task GroupLevelHr_StillSeesEveryCompany_ButNeverAnotherTenant()
    {
        var s = await SeedTwoCompaniesAsync();
        var hr = GroupHrAdmin(s.TenantId);
        await using var db = _fx.CreateDbWithAccessor(Accessor(hr));

        var board = JsonPayload(await Build<CalibrationController>(db, hr).GetBoard(s.CycleId, null, default));
        board.GetProperty("reviews").EnumerateArray().Select(r => r.GetProperty("employeeId").GetInt32())
            .Should().BeEquivalentTo(new[] { s.AlphaTop, s.AlphaLow, s.BetaTop, s.BetaLow, s.Nomad },
                "group HR sees every company, and the employee with no company is still discoverable to them");
        board.GetRawText().Should().NotContain("Gamma Outsider", "a second tenant's review must never appear");

        var analytics = JsonPayload(await Build<PerformanceAnalyticsController>(db, hr).CycleAnalytics(s.CycleId, default));
        analytics.GetProperty("summary").GetProperty("totalEnrolled").GetInt32().Should().Be(5);
        analytics.GetRawText().Should().NotContain("Gamma Outsider");
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // Private continuous feedback is its author's alone
    // ═══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ContinuousFeedback_IncludePrivate_NeverRevealsAnotherAuthorsPrivateNote()
    {
        var s = await SeedTwoCompaniesAsync();
        var author = Guid.NewGuid();
        await using (var seed = _fx.CreateDb())
        {
            seed.ContinuousFeedback.AddRange(
                new ContinuousFeedback { TenantId = s.TenantId, EmployeeId = s.AlphaLow, EmployeeName = "Alpha Lowrank", GivenByUserId = author, GivenByName = "Line Manager", FeedbackType = "Concern", Content = "PRIVATE-NOTE-XYZ", IsPrivate = true },
                new ContinuousFeedback { TenantId = s.TenantId, EmployeeId = s.AlphaLow, EmployeeName = "Alpha Lowrank", GivenByUserId = author, GivenByName = "Line Manager", FeedbackType = "Praise", Content = "PUBLIC-NOTE-XYZ", IsPrivate = false });
            await seed.SaveChangesAsync();
        }

        var hr = GroupHrAdmin(s.TenantId);
        await using (var db = _fx.CreateDbWithAccessor(Accessor(hr)))
        {
            var raw = JsonPayload(await Build<FeedbackController>(db, hr).ListContinuous(s.AlphaLow, null, true, default)).GetRawText();
            raw.Should().Contain("PUBLIC-NOTE-XYZ", "non-private feedback is still listed");
            raw.Should().NotContain("PRIVATE-NOTE-XYZ", "a private note is its author's alone, whatever the query string says");
        }

        var self = GroupHrAdmin(s.TenantId, userId: author);
        await using (var db = _fx.CreateDbWithAccessor(Accessor(self)))
        {
            var raw = JsonPayload(await Build<FeedbackController>(db, self).ListContinuous(s.AlphaLow, null, null, default)).GetRawText();
            raw.Should().Contain("PRIVATE-NOTE-XYZ", "the author still sees their own private note");
        }
    }

    [Fact]
    public async Task ContinuousFeedback_IsGivenOnlyToColleaguesInScope_UnderTheEmployeeRecordsName()
    {
        var s = await SeedTwoCompaniesAsync();
        var caller = EmployeeLogin(s.TenantId, s.CompanyA, s.AlphaTop);

        await using (var db = _fx.CreateDbWithAccessor(Accessor(caller)))
        {
            var foreign = await Build<FeedbackController>(db, caller).CreateContinuous(
                new ContinuousFeedbackRequest(s.BetaLow, "Beta Lowrank", "Concern", "cross-company note", false, null), default);
            StatusOf(foreign).Should().Be(404, "an employee in another company is outside a company-A employee's reach");

            var nomad = await Build<FeedbackController>(db, caller).CreateContinuous(
                new ContinuousFeedbackRequest(s.Nomad, "Nomad Nullco", "Concern", "no-company note", false, null), default);
            StatusOf(nomad).Should().Be(404, "an employee with no company is in nobody's company");

            var wrongReview = await Build<FeedbackController>(db, caller).CreateContinuous(
                new ContinuousFeedbackRequest(s.AlphaLow, "Alpha Lowrank", "Praise", "linked to someone else's review", false, s.BetaTopReview), default);
            StatusOf(wrongReview).Should().Be(400, "a linked review must be the subject's own review");

            var peer = await Build<FeedbackController>(db, caller).CreateContinuous(
                new ContinuousFeedbackRequest(s.AlphaLow, "Somebody Else Entirely", "Praise", "PEER-NOTE-XYZ", false, s.AlphaLowReview), default);
            StatusOf(peer).Should().Be(200, "a same-company colleague may give peer feedback");
        }

        await using var verify = _fx.CreateDb();
        var rows = await verify.ContinuousFeedback.Where(f => f.TenantId == s.TenantId).ToListAsync();
        rows.Should().ContainSingle("only the in-scope peer note is written");
        rows[0].EmployeeId.Should().Be(s.AlphaLow);
        rows[0].EmployeeName.Should().Be("Alpha Lowrank", "the subject's name comes from the employee record, not the body");
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // 360 reviewer is the caller; small anonymous role groups are merged
    // ═══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Submit360_NamingSomeoneElseAsReviewer_IsRefusedAndWritesNothing()
    {
        var s = await SeedTwoCompaniesAsync();
        await Open360Async(s);
        var caller = EmployeeLogin(s.TenantId, s.CompanyA, s.AlphaTop);
        await using var db = _fx.CreateDbWithAccessor(Accessor(caller));

        var result = await Build<FeedbackController>(db, caller).Submit360(
            new Feedback360Request(s.AlphaLowReview, s.AlphaHr, "Alpha Hr", "Peer", false, 4m, "spoofed", null, null), default);

        StatusOf(result).Should().Be(403, "the reviewer is the authenticated caller, never a name typed into the body");
        await using var verify = _fx.CreateDb();
        (await verify.Feedback360.CountAsync(f => f.ReviewId == s.AlphaLowReview)).Should().Be(0);
    }

    [Fact]
    public async Task Submit360_RecordsTheCallersOwnEmployeeAndName_NotTheBodys()
    {
        var s = await SeedTwoCompaniesAsync();
        await Open360Async(s);
        var caller = EmployeeLogin(s.TenantId, s.CompanyA, s.AlphaTop);
        await using var db = _fx.CreateDbWithAccessor(Accessor(caller));

        var result = await Build<FeedbackController>(db, caller).Submit360(
            new Feedback360Request(s.AlphaLowReview, s.AlphaTop, "Somebody Important", "Peer", true, 4m, "good", null, null), default);

        StatusOf(result).Should().Be(200);
        await using var verify = _fx.CreateDb();
        var row = await verify.Feedback360.SingleAsync(f => f.ReviewId == s.AlphaLowReview);
        row.ReviewerEmployeeId.Should().Be(s.AlphaTop);
        row.ReviewerName.Should().Be("Alpha Topper", "the name comes from the caller's employee record");
    }

    [Fact]
    public async Task Submit360_WithoutALinkedEmployee_OrClaimingAManagerRoleTheyDoNotHold_IsRefused()
    {
        var s = await SeedTwoCompaniesAsync();
        await Open360Async(s);

        var unlinked = GroupHrAdmin(s.TenantId);
        await using (var db = _fx.CreateDbWithAccessor(Accessor(unlinked)))
        {
            var r = await Build<FeedbackController>(db, unlinked).Submit360(
                new Feedback360Request(s.AlphaLowReview, s.AlphaTop, "Alpha Topper", "Peer", false, 4m, null, null, null), default);
            StatusOf(r).Should().Be(403, "a login with no employee record cannot be anyone's 360 reviewer");
        }

        // AlphaTop is not AlphaLow's manager (AlphaHr is), so cannot file as "Manager".
        var peer = EmployeeLogin(s.TenantId, s.CompanyA, s.AlphaTop);
        await using (var db = _fx.CreateDbWithAccessor(Accessor(peer)))
        {
            var r = await Build<FeedbackController>(db, peer).Submit360(
                new Feedback360Request(s.AlphaLowReview, 0, "", "Manager", true, 4m, null, null, null), default);
            StatusOf(r).Should().Be(400, "the Manager role is a fact about the reporting line, not a claim");
        }

        await using var verify = _fx.CreateDb();
        (await verify.Feedback360.CountAsync(f => f.ReviewId == s.AlphaLowReview)).Should().Be(0);
    }

    [Fact]
    public async Task List360_WhenAnyAnonymousRoleGroupIsSmall_HidesEveryAnonymousRoleAndTimestamp()
    {
        var s = await SeedTwoCompaniesAsync();
        await SeedThreeSixtyAsync(s);

        var hr = GroupHrAdmin(s.TenantId);
        await using var db = _fx.CreateDbWithAccessor(Accessor(hr));
        var items = JsonPayload(await Build<FeedbackController>(db, hr).List360(s.AlphaLowReview, default))
            .EnumerateArray().ToList();

        JsonElement ByComment(string comment) => items.Single(i => i.GetProperty("comments").GetString() == comment);
        // One anonymous Manager is a group of one, so EVERY anonymous role is hidden — hiding only the
        // Manager would let a reader subtract the visible Peer group from the total.
        ByComment("manager-comment").GetProperty("reviewerRole").GetString().Should().Be("Other");
        ByComment("peer-1").GetProperty("reviewerRole").GetString().Should().Be("Other");
        ByComment("named-sub").GetProperty("reviewerRole").GetString().Should().Be("Subordinate", "a named entry's role is no secret");
        ByComment("manager-comment").GetProperty("reviewerName").GetString().Should().Be("Anonymous");
        ByComment("manager-comment").GetProperty("submittedAt").ValueKind.Should().Be(JsonValueKind.Null,
            "the time of an anonymous submission correlates with who was asked when");
        ByComment("named-sub").GetProperty("submittedAt").ValueKind.Should().NotBe(JsonValueKind.Null);
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // Department pay totals respect a minimum group size
    // ═══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task DepartmentComparison_SuppressesPayTotalsForDepartmentsSmallerThanFive()
    {
        await using var db = _fx.CreateDb();
        var tid = await PostgresFixture.SeedMinimalTenant(db);
        var co = new Company { TenantId = tid, LegalNameEn = "Cohort Co", RegistrationNumber = $"R-{Guid.NewGuid():N}", IsActive = true, DefaultCurrency = "SAR" };
        db.Companies.Add(co);
        var run = new PayrollRun { TenantId = tid, CompanyId = co.Id, Year = 2026, Month = 9, Status = "Locked" };
        db.PayrollRuns.Add(run);
        var n = 0;
        PayrollSlip Slip(string dept, decimal net) => new()
        {
            TenantId = tid, CompanyId = co.Id, RunId = run.Id, EmployeeId = 50_000 + ++n,
            EmployeeCode = $"C{n}", EmployeeName = $"Person {n}", Department = dept, NetSalary = net,
        };
        for (var i = 0; i < 6; i++) db.PayrollSlips.Add(Slip("Engineering", 10_000m));
        db.PayrollSlips.Add(Slip("Legal", 77_777m));     // a department of two: its total is two salaries
        db.PayrollSlips.Add(Slip("Legal", 12_345m));
        await db.SaveChangesAsync();

        var json = JsonPayload(await Reports(db, tid).DepartmentComparison(default));
        var raw = json.GetRawText();

        var rows = json.GetProperty("payrollByDept").EnumerateArray().ToList();
        rows.Single(r => r.GetProperty("department").GetString() == "Engineering")
            .GetProperty("totalNet").GetDecimal().Should().Be(60_000m, "a department of six is shown");
        rows.Should().NotContain(r => r.GetProperty("department").GetString() == "Legal");
        raw.Should().NotContain("77777").And.NotContain("90122", "neither the salary nor the department total may surface");
        var other = rows.Single(r => r.GetProperty("department").GetString() == "Other departments (suppressed)");
        other.GetProperty("totalNet").ValueKind.Should().Be(JsonValueKind.Null,
            "the merged group is itself smaller than five, so its total is withheld too");
    }

    [Fact]
    public async Task DepartmentComparison_MergesSmallDepartmentsIntoOneGroupWhenThatGroupIsLargeEnough()
    {
        await using var db = _fx.CreateDb();
        var tid = await PostgresFixture.SeedMinimalTenant(db);
        var co = new Company { TenantId = tid, LegalNameEn = "Cohort Co 2", RegistrationNumber = $"R-{Guid.NewGuid():N}", IsActive = true, DefaultCurrency = "SAR" };
        db.Companies.Add(co);
        var run = new PayrollRun { TenantId = tid, CompanyId = co.Id, Year = 2026, Month = 9, Status = "Locked" };
        db.PayrollRuns.Add(run);
        var n = 0;
        PayrollSlip Slip(string dept, decimal net) => new()
        {
            TenantId = tid, CompanyId = co.Id, RunId = run.Id, EmployeeId = 60_000 + ++n,
            EmployeeCode = $"D{n}", EmployeeName = $"Person {n}", Department = dept, NetSalary = net,
        };
        for (var i = 0; i < 3; i++) db.PayrollSlips.Add(Slip("Legal", 1_000m));
        for (var i = 0; i < 3; i++) db.PayrollSlips.Add(Slip("Audit", 2_000m));
        await db.SaveChangesAsync();

        var rows = JsonPayload(await Reports(db, tid).DepartmentComparison(default))
            .GetProperty("payrollByDept").EnumerateArray().ToList();

        rows.Should().ContainSingle();
        rows[0].GetProperty("department").GetString().Should().Be("Other departments (suppressed)");
        rows[0].GetProperty("totalNet").GetDecimal().Should().Be(9_000m, "six people merged is a large enough group to total");
        rows[0].GetProperty("currencyCode").GetString().Should().Be("SAR");
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // Cycle detail and stats are an HR view, cut to scope
    // ═══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CycleDetailAndStats_ForAnEmployeeRoleCaller_AreRefusedByTheProductionGate()
    {
        var s = await SeedTwoCompaniesAsync();
        var employee = EmployeeLogin(s.TenantId, s.CompanyA, s.AlphaLow);
        await using var db = _fx.CreateDbWithAccessor(Accessor(employee));

        (await Zayra.Api.Tests.Security.ProductionAuthorizationGate.StatusAsync<CyclesController>(employee, nameof(CyclesController.Get),
            () => Build<CyclesController>(db, employee).Get(s.CycleId, default)))
            .Should().Be(403, "a cycle's detail carries every review's score, rating and notes");
        (await Zayra.Api.Tests.Security.ProductionAuthorizationGate.StatusAsync<CyclesController>(employee, nameof(CyclesController.Stats),
            () => Build<CyclesController>(db, employee).Stats(default)))
            .Should().Be(403);
    }

    [Fact]
    public async Task CycleDetail_ForCompanyScopedHrManager_ListsOnlyItsOwnCompanysReviews()
    {
        var s = await SeedTwoCompaniesAsync();
        var hr = CompanyHrManager(s.TenantId, s.CompanyA);
        await using var db = _fx.CreateDbWithAccessor(Accessor(hr));

        var status = await Zayra.Api.Tests.Security.ProductionAuthorizationGate.StatusAsync<CyclesController>(hr, nameof(CyclesController.Get),
            () => Build<CyclesController>(db, hr).Get(s.CycleId, default));
        status.Should().Be(200, "HR's view still opens for HR");

        var payload = JsonPayload(await Build<CyclesController>(db, hr).Get(s.CycleId, default));
        payload.GetProperty("reviews").EnumerateArray().Select(r => r.GetProperty("employeeId").GetInt32())
            .Should().BeEquivalentTo(new[] { s.AlphaTop, s.AlphaLow });
        AssertNoForeignNames(payload.GetRawText());

        var stats = JsonPayload(await Build<CyclesController>(db, hr).Stats(default));
        stats.GetProperty("totalReviews").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task EveryPerformanceAction_NamesARoleOrPermissionGate()
    {
        // Bare [Authorize] is authentication only: LegacyRolePermissionResolver infers a permission only for
        // an endpoint that carries a role gate. So every action must carry Roles= or [HasPermission].
        var ungated = typeof(CyclesController).Assembly.GetTypes()
            .Where(t => t.Namespace == typeof(CyclesController).Namespace && typeof(ControllerBase).IsAssignableFrom(t))
            .SelectMany(t => t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute), true).Length > 0)
                .Where(m => !t.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), true)
                    .Concat(m.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), true))
                    .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>()
                    .Any(a => !string.IsNullOrWhiteSpace(a.Roles) || (a.Policy?.StartsWith("perm:") ?? false)))
                .Select(m => $"{t.Name}.{m.Name}"))
            .ToList();

        ungated.Should().BeEmpty("every Performance action must name who may call it");
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // by-id reads and writes outside the caller's scope are 404
    // ═══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ReviewByIdActions_OnAnotherCompanysReview_Are404AndChangeNothing()
    {
        var s = await SeedTwoCompaniesAsync();
        var hr = CompanyHrManager(s.TenantId, s.CompanyA);
        await using var db = _fx.CreateDbWithAccessor(Accessor(hr));
        var reviews = Build<ReviewsController>(db, hr);

        StatusOf(await reviews.Get(s.BetaTopReview, default)).Should().Be(404);
        StatusOf(await reviews.OverrideScore(s.BetaTopReview,
            new ScoreOverrideRequest(10m, null, null, null, null, null, "cross-company override"), default)).Should().Be(404);
        StatusOf(await reviews.Publish(s.BetaTopReview, default)).Should().Be(404);
        StatusOf(await reviews.ComputeAttendance(s.BetaTopReview, default)).Should().Be(404);

        await using var verify = _fx.CreateDb();
        var b = await verify.AppraisalReviews.AsNoTracking().SingleAsync(r => r.Id == s.BetaTopReview);
        b.KpiScore.Should().Be(0m);
        b.HrNotes.Should().NotContain("cross-company override");
        b.Status.Should().Be("ManagerReviewComplete");

        StatusOf(await reviews.Get(s.AlphaTopReview, default)).Should().Be(200, "company A's own review still opens");
    }

    [Fact]
    public async Task LineManager_ReadsTheirTeamsReview_ButAnotherReviewIs404()
    {
        var s = await SeedTwoCompaniesAsync();
        var manager = LineManager(s.TenantId, s.CompanyA, s.AlphaHr);
        await using var db = _fx.CreateDbWithAccessor(Accessor(manager));
        var reviews = Build<ReviewsController>(db, manager);

        StatusOf(await reviews.Get(s.AlphaLowReview, default)).Should().Be(200, "AlphaLow reports to this manager");
        StatusOf(await reviews.Get(s.AlphaTopReview, default)).Should().Be(404, "same company, but not their team");
    }

    [Fact]
    public async Task RecommendationDecisions_OnAnotherCompanysEmployee_Are404AndStayPending()
    {
        var s = await SeedTwoCompaniesAsync();
        var recommender = Guid.NewGuid();
        Guid inc, pro, bon;
        await using (var seed = _fx.CreateDb())
        {
            var i = new IncrementRecommendation { TenantId = s.TenantId, ReviewId = s.BetaTopReview, EmployeeId = s.BetaTop, EmployeeName = "Beta Topper", CurrentSalary = 1000m, NewSalary = 1100m, Status = "Pending", RecommendedByUserId = recommender, EffectiveDate = new DateOnly(2026, 11, 1) };
            var p = new PromotionRecommendation { TenantId = s.TenantId, ReviewId = s.BetaTopReview, EmployeeId = s.BetaTop, EmployeeName = "Beta Topper", ProposedDesignation = "Lead", Status = "Pending", RecommendedByUserId = recommender, EffectiveDate = new DateOnly(2026, 11, 1) };
            var b = new BonusRecommendation { TenantId = s.TenantId, ReviewId = s.BetaTopReview, EmployeeId = s.BetaTop, EmployeeName = "Beta Topper", BonusAmount = 500m, Status = "Pending", RecommendedByUserId = recommender };
            seed.AddRange(i, p, b);
            await seed.SaveChangesAsync();
            inc = i.Id; pro = p.Id; bon = b.Id;
        }

        var hr = CompanyHrManager(s.TenantId, s.CompanyA);
        await using var db = _fx.CreateDbWithAccessor(Accessor(hr));
        var recs = Build<RecommendationsController>(db, hr);
        var decision = new SimpleDecisionRequest("Approved", "cross-company");
        StatusOf(await recs.ApproveIncrement(inc, decision, default)).Should().Be(404);
        StatusOf(await recs.ApprovePromotion(pro, decision, default)).Should().Be(404);
        StatusOf(await recs.ApproveBonus(bon, decision, default)).Should().Be(404);

        await using var verify = _fx.CreateDb();
        (await verify.IncrementRecommendations.AsNoTracking().SingleAsync(r => r.Id == inc)).Status.Should().Be("Pending");
        (await verify.PromotionRecommendations.AsNoTracking().SingleAsync(r => r.Id == pro)).Status.Should().Be("Pending");
        (await verify.BonusRecommendations.AsNoTracking().SingleAsync(r => r.Id == bon)).Status.Should().Be("Pending");
    }

    [Fact]
    public async Task PipOutcome_OnAnotherCompanysEmployee_Is404AndUnchanged()
    {
        var s = await SeedTwoCompaniesAsync();
        Guid pipId;
        await using (var seed = _fx.CreateDb())
        {
            var pip = new PerformanceImprovementPlan { TenantId = s.TenantId, EmployeeId = s.BetaLow, EmployeeName = "Beta Lowrank", Status = "Active", StartDate = new DateOnly(2026, 8, 1), EndDate = new DateOnly(2026, 11, 1) };
            seed.PerformanceImprovementPlans.Add(pip);
            await seed.SaveChangesAsync();
            seed.PIPCheckIns.Add(new PIPCheckIn { TenantId = s.TenantId, PipId = pip.Id, Outcome = "Improved", CheckInDate = new DateOnly(2026, 9, 1) });
            await seed.SaveChangesAsync();
            pipId = pip.Id;
        }

        var hr = CompanyHrManager(s.TenantId, s.CompanyA);
        await using var db = _fx.CreateDbWithAccessor(Accessor(hr));
        StatusOf(await Build<PIPController>(db, hr).UpdateStatus(pipId, new PIPStatusRequest("Improved", "cross-company close"), default))
            .Should().Be(404);
        await using var verify = _fx.CreateDb();
        (await verify.PerformanceImprovementPlans.AsNoTracking().SingleAsync(p => p.Id == pipId)).Status.Should().Be("Active");
    }

    [Fact]
    public async Task ProbationCreate_ForAnotherCompanysEmployee_Is404_AndTakesTheNameFromTheEmployeeRecord()
    {
        var s = await SeedTwoCompaniesAsync();
        var hr = CompanyHrManager(s.TenantId, s.CompanyA);
        await using var db = _fx.CreateDbWithAccessor(Accessor(hr));
        var probation = Build<ProbationController>(db, hr);

        StatusOf(await probation.Create(new ProbationRequest(s.BetaLow, "Beta Lowrank", "Ops", "Analyst",
            new DateOnly(2026, 7, 1), new DateOnly(2026, 12, 31), null), default)).Should().Be(404);
        StatusOf(await probation.Create(new ProbationRequest(s.Nomad, "Nomad Nullco", "Ops", "Analyst",
            new DateOnly(2026, 7, 1), new DateOnly(2026, 12, 31), null), default)).Should().Be(404, "no company is nobody's company");

        StatusOf(await probation.Create(new ProbationRequest(s.AlphaLow, "Typed Somebody Else", "Typed Dept", "Typed Title",
            new DateOnly(2026, 7, 1), new DateOnly(2026, 12, 31), null), default)).Should().Be(201);

        await using var verify = _fx.CreateDb();
        (await verify.ProbationReviews.CountAsync(p => p.TenantId == s.TenantId && (p.EmployeeId == s.BetaLow || p.EmployeeId == s.Nomad)))
            .Should().Be(0);
        (await verify.ProbationReviews.SingleAsync(p => p.TenantId == s.TenantId && p.EmployeeId == s.AlphaLow))
            .EmployeeName.Should().Be("Alpha Lowrank", "the subject's name comes from their employee record, not the body");
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // a cycle spans every company; only group-level callers move it
    // ═══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CycleTransitions_ByACompanyScopedHrManager_Are403WithAClearCode()
    {
        var s = await SeedTwoCompaniesAsync();
        var hr = CompanyHrManager(s.TenantId, s.CompanyA);
        await using var db = _fx.CreateDbWithAccessor(Accessor(hr));
        var cycles = Build<CyclesController>(db, hr);

        foreach (var result in new[]
                 {
                     await cycles.Advance(s.CycleId, default),
                     await cycles.Launch(s.CycleId, default),
                     await cycles.Close(s.CycleId, default),
                 })
        {
            StatusOf(result).Should().Be(403);
            JsonSerializer.Serialize(((ObjectResult)result).Value).Should().Contain("cycle_action_requires_group_scope");
        }
        await using var verify = _fx.CreateDb();
        (await verify.PerformanceCycles.AsNoTracking().SingleAsync(c => c.Id == s.CycleId)).Status.Should().Be("Calibration");

        // A group-level HR administrator is not refused for scope (the cycle's own rules then apply).
        var group = GroupHrAdmin(s.TenantId);
        await using var gdb = _fx.CreateDbWithAccessor(Accessor(group));
        var advanced = await Build<CyclesController>(gdb, group).Advance(s.CycleId, default);
        JsonSerializer.Serialize((advanced as ObjectResult)?.Value).Should().NotContain("cycle_action_requires_group_scope");
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // 360 only where the cycle collects it, while the review is open, in-company
    // ═══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Submit360_IsRefused_WhenTheCycleDoesNotCollect360_OrTheReviewIsPastManagerReview()
    {
        var s = await SeedTwoCompaniesAsync();   // FY26: Calibration, 360 disabled
        var caller = EmployeeLogin(s.TenantId, s.CompanyA, s.AlphaTop);
        await using (var db = _fx.CreateDbWithAccessor(Accessor(caller)))
            StatusOf(await Build<FeedbackController>(db, caller).Submit360(
                new Feedback360Request(s.AlphaLowReview, 0, null, "Peer", false, 4m, null, null, null), default))
                .Should().Be(409, "this cycle does not collect 360 feedback");

        await Open360Async(s);
        var low = EmployeeLogin(s.TenantId, s.CompanyA, s.AlphaLow);
        await using (var db = _fx.CreateDbWithAccessor(Accessor(low)))
            StatusOf(await Build<FeedbackController>(db, low).Submit360(
                new Feedback360Request(s.AlphaTopReview, 0, null, "Peer", false, 4m, null, null, null), default))
                .Should().Be(409, "AlphaTop's manager review is already complete");

        await using var verify = _fx.CreateDb();
        (await verify.Feedback360.CountAsync(f => f.ReviewId == s.AlphaLowReview || f.ReviewId == s.AlphaTopReview)).Should().Be(0);
    }

    [Fact]
    public async Task Submit360_FromAnotherCompany_OrFromAnEmployeeWithNoCompany_IsRefused()
    {
        var s = await SeedTwoCompaniesAsync();
        await Open360Async(s);

        var beta = EmployeeLogin(s.TenantId, s.CompanyB, s.BetaTop);
        await using (var db = _fx.CreateDbWithAccessor(Accessor(beta)))
            StatusOf(await Build<FeedbackController>(db, beta).Submit360(
                new Feedback360Request(s.AlphaLowReview, 0, null, "Peer", false, 4m, null, null, null), default))
                .Should().Be(403, "company B's employee is not a 360 reviewer for company A without a nomination");

        var nomad = EmployeeLogin(s.TenantId, s.CompanyA, s.Nomad);
        await using (var db = _fx.CreateDbWithAccessor(Accessor(nomad)))
        {
            var r = await Build<FeedbackController>(db, nomad).Submit360(
                new Feedback360Request(s.AlphaLowReview, 0, null, "Peer", false, 4m, null, null, null), default);
            StatusOf(r).Should().BeOneOf(new[] { 403 }, "an employee with no company is in nobody's company");
        }

        await using var verify = _fx.CreateDb();
        (await verify.Feedback360.CountAsync(f => f.ReviewId == s.AlphaLowReview)).Should().Be(0);
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // one anonymous-360 projection for both endpoints
    // ═══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ReviewDetailAndList360_ProjectThe360FeedbackIdentically_ForHrAndForTheLineManager()
    {
        var s = await SeedTwoCompaniesAsync();
        await SeedThreeSixtyAsync(s);

        foreach (var caller in new[] { GroupHrAdmin(s.TenantId), LineManager(s.TenantId, s.CompanyA, s.AlphaHr) })
        {
            await using var db = _fx.CreateDbWithAccessor(Accessor(caller));
            var fromList = JsonPayload(await Build<FeedbackController>(db, caller).List360(s.AlphaLowReview, default));
            var fromReview = JsonPayload(await Build<ReviewsController>(db, caller).Get(s.AlphaLowReview, default))
                .GetProperty("feedback360");
            fromReview.GetRawText().Should().Be(fromList.GetRawText(), "the two endpoints use one projection");
        }

        var manager = LineManager(s.TenantId, s.CompanyA, s.AlphaHr);
        await using var mdb = _fx.CreateDbWithAccessor(Accessor(manager));
        var managerView = JsonPayload(await Build<FeedbackController>(mdb, manager).List360(s.AlphaLowReview, default))
            .EnumerateArray().ToList();
        managerView.Should().ContainSingle("a line manager (not the approver tier) sees named entries only")
            .Which.GetProperty("comments").GetString().Should().Be("named-sub");
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // dashboard active cycles stay inside the caller's companies
    // ═══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task PerformanceDashboard_ListsOnlyActiveCyclesTheCallerHasSomeoneIn()
    {
        var s = await SeedTwoCompaniesAsync();
        await using (var seed = _fx.CreateDb())
        {
            var alpha = new PerformanceCycle { TenantId = s.TenantId, Name = "Alpha Active Cycle", Status = "Active", ReviewPeriodStart = new DateOnly(2026, 1, 1), ReviewPeriodEnd = new DateOnly(2026, 6, 30) };
            var beta = new PerformanceCycle { TenantId = s.TenantId, Name = "Beta Secret Cycle", Status = "Active", ReviewPeriodStart = new DateOnly(2026, 1, 1), ReviewPeriodEnd = new DateOnly(2026, 6, 30) };
            seed.PerformanceCycles.AddRange(alpha, beta);
            seed.AppraisalReviews.AddRange(
                new AppraisalReview { TenantId = s.TenantId, CycleId = alpha.Id, EmployeeId = s.AlphaTop, EmployeeName = "Alpha Topper", Status = "SelfAssessmentDue" },
                new AppraisalReview { TenantId = s.TenantId, CycleId = beta.Id, EmployeeId = s.BetaTop, EmployeeName = "Beta Topper", Status = "SelfAssessmentDue" });
            await seed.SaveChangesAsync();
        }

        var hr = CompanyHrManager(s.TenantId, s.CompanyA);
        await using var db = _fx.CreateDbWithAccessor(Accessor(hr));
        var raw = JsonPayload(await Build<PerformanceAnalyticsController>(db, hr).Dashboard(default)).GetRawText();
        raw.Should().Contain("Alpha Active Cycle");
        raw.Should().NotContain("Beta Secret Cycle");
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // Seed + helpers
    // ═══════════════════════════════════════════════════════════════════════════════════════════

    private sealed record Seeded(
        Guid TenantId, Guid CompanyA, Guid CompanyB, Guid CycleId,
        int AlphaTop, int AlphaLow, int AlphaHr, int BetaTop, int BetaLow, int Nomad,
        Guid AlphaTopReview, Guid AlphaLowReview, Guid BetaTopReview, Guid BetaLowReview);

    /// <summary>Names a company-A caller must never see: company B's people, an employee with NO company
    /// (in nobody's company scope), and another tenant's.</summary>
    private static readonly string[] ForeignNames = ["Beta Topper", "Beta Lowrank", "Beta Manager", "Nomad Nullco", "Gamma Outsider"];

    private static void AssertNoForeignNames(string raw)
    {
        foreach (var name in ForeignNames)
            raw.Should().NotContain(name, $"'{name}' belongs to another company or tenant");
    }

    private async Task<Seeded> SeedTwoCompaniesAsync()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var otherTenant = await PostgresFixture.SeedMinimalTenant(db);
        var a = new Company { TenantId = tenantId, LegalNameEn = "Scope Co A", RegistrationNumber = $"R-{Guid.NewGuid():N}", IsActive = true };
        var b = new Company { TenantId = tenantId, LegalNameEn = "Scope Co B", RegistrationNumber = $"R-{Guid.NewGuid():N}", IsActive = true };
        var c = new Company { TenantId = otherTenant, LegalNameEn = "Other Tenant Co", RegistrationNumber = $"R-{Guid.NewGuid():N}", IsActive = true };
        db.Companies.AddRange(a, b, c);
        await db.SaveChangesAsync();

        Employee Emp(Guid tenant, Guid company, string name) => new()
        {
            TenantId = tenant, CompanyId = company, EmployeeCode = $"PS{Guid.NewGuid():N}"[..12],
            FullName = name, Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-2),
        };
        var alphaHr = Emp(tenantId, a.Id, "Alpha Hr");
        var betaMgr = Emp(tenantId, b.Id, "Beta Manager");
        db.Employees.AddRange(alphaHr, betaMgr);
        await db.SaveChangesAsync();
        var alphaTop = Emp(tenantId, a.Id, "Alpha Topper");
        var alphaLow = Emp(tenantId, a.Id, "Alpha Lowrank");
        alphaLow.ManagerEmployeeId = alphaHr.Id;
        var betaTop = Emp(tenantId, b.Id, "Beta Topper");
        var betaLow = Emp(tenantId, b.Id, "Beta Lowrank");
        var gamma = Emp(otherTenant, c.Id, "Gamma Outsider");
        // An employee with no company: a migration/backfill transient that must not leak into any
        // company-scoped view (DataScopeService excludes CompanyId == null from company scopes).
        var nomad = Emp(tenantId, a.Id, "Nomad Nullco");
        nomad.CompanyId = null;
        db.Employees.AddRange(alphaTop, alphaLow, betaTop, betaLow, gamma, nomad);

        var cycle = new PerformanceCycle
        {
            TenantId = tenantId, Name = "FY26", Status = "Calibration",
            ReviewPeriodStart = new DateOnly(2026, 1, 1), ReviewPeriodEnd = new DateOnly(2026, 12, 31),
        };
        db.PerformanceCycles.Add(cycle);
        await db.SaveChangesAsync();

        AppraisalReview Review(Guid tenant, Employee e, decimal score, string rating, string status, Employee? mgr) => new()
        {
            TenantId = tenant, CycleId = cycle.Id, CycleName = cycle.Name, EmployeeId = e.Id, EmployeeName = e.FullName,
            DepartmentName = "Ops", DesignationTitle = "Analyst", FinalScore = score, FinalRating = rating, Status = status,
            ReviewerManagerId = mgr?.Id, ReviewerManagerName = mgr?.FullName ?? string.Empty,
        };
        var rAlphaTop = Review(tenantId, alphaTop, 92m, "Outstanding", "ManagerReviewComplete", alphaHr);
        var rAlphaLow = Review(tenantId, alphaLow, 50m, "Developing", "ManagerReview", alphaHr);
        var rBetaTop = Review(tenantId, betaTop, 95m, "Outstanding", "ManagerReviewComplete", betaMgr);
        var rBetaLow = Review(tenantId, betaLow, 40m, "Unsatisfactory", "ManagerReview", betaMgr);
        // A second tenant's review that (impossibly, but defensively) carries the first tenant's cycle id.
        var rGamma = Review(otherTenant, gamma, 99m, "Outstanding", "ManagerReview", null);
        var rNomad = Review(tenantId, nomad, 70m, "Meets Expectations", "ManagerReviewComplete", null);
        db.AppraisalReviews.AddRange(rAlphaTop, rAlphaLow, rBetaTop, rBetaLow, rGamma, rNomad);

        db.AppraisalCalibrations.Add(new AppraisalCalibration
        {
            TenantId = tenantId, ReviewId = rBetaTop.Id, CycleId = cycle.Id, EmployeeName = "Beta Topper",
            DepartmentName = "Ops", OriginalScore = 90m, AdjustedScore = 95m, AdjustmentReason = "Beta calibration",
        });
        db.PerformanceAuditLogs.AddRange(
            new PerformanceAuditLog { TenantId = tenantId, EntityType = "AppraisalReview", EntityId = rAlphaTop.Id.ToString(), Action = "Note", Reason = "Alpha audit reason" },
            new PerformanceAuditLog { TenantId = tenantId, EntityType = "AppraisalReview", EntityId = rBetaTop.Id.ToString(), Action = "Note", Reason = "Beta audit reason" });
        db.EmployeeGoals.AddRange(
            new EmployeeGoal { TenantId = tenantId, CycleId = cycle.Id, EmployeeId = alphaTop.Id, EmployeeName = alphaTop.FullName, Title = "A goal", Status = "Active", AchievementPct = 80 },
            new EmployeeGoal { TenantId = tenantId, CycleId = cycle.Id, EmployeeId = betaTop.Id, EmployeeName = betaTop.FullName, Title = "B goal", Status = "Completed", AchievementPct = 100 });
        await db.SaveChangesAsync();

        return new Seeded(tenantId, a.Id, b.Id, cycle.Id, alphaTop.Id, alphaLow.Id, alphaHr.Id, betaTop.Id, betaLow.Id,
            nomad.Id, rAlphaTop.Id, rAlphaLow.Id, rBetaTop.Id, rBetaLow.Id);
    }

    /// <summary>Opens the seeded cycle for 360 feedback: InReview, 360 enabled.</summary>
    private async Task Open360Async(Seeded s)
    {
        await using var db = _fx.CreateDb();
        var cycle = await db.PerformanceCycles.SingleAsync(c => c.Id == s.CycleId);
        cycle.Status = "InReview";
        cycle.Enable360Feedback = true;
        await db.SaveChangesAsync();
    }

    /// <summary>One anonymous Manager, three anonymous Peers and one named Subordinate on AlphaLow's review.</summary>
    private async Task SeedThreeSixtyAsync(Seeded s)
    {
        await using var seed = _fx.CreateDb();
        Feedback360 Fb(int reviewer, string role, bool anon, string text) => new()
        {
            TenantId = s.TenantId, ReviewId = s.AlphaLowReview, ReviewerEmployeeId = reviewer,
            ReviewerName = $"Reviewer {reviewer}", ReviewerRole = role, IsAnonymous = anon,
            Score = 3m, Comments = text, SubmittedAt = DateTime.UtcNow,
        };
        seed.Feedback360.AddRange(
            Fb(s.AlphaHr, "Manager", true, "manager-comment"),
            Fb(9001, "Peer", true, "peer-1"), Fb(9002, "Peer", true, "peer-2"), Fb(9003, "Peer", true, "peer-3"),
            Fb(9004, "Subordinate", false, "named-sub"));
        await seed.SaveChangesAsync();
    }

    /// <summary>A line manager (the seeded Manager bundle's keys) linked to their employee record.</summary>
    private static ClaimsPrincipal LineManager(Guid tenantId, Guid companyId, int employeeId) =>
        new(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Role, "Manager"),
            new Claim("employee_id", employeeId.ToString()),
            new Claim("permission", "employees.read"),
            new Claim("permission", "manager.read"),
            new Claim("permission", "performance.read"),
            new Claim("permission", "performance.write"),
            new Claim(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new { v = 2, m = "companies", c = new[] { companyId } })),
        }, "test"));

    private static T Build<T>(ZayraDbContext db, ClaimsPrincipal principal) where T : ControllerBase
    {
        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton<IPerformanceService>(new PerformanceService(db));
        services.AddSingleton<IDataScopeService>(new DataScopeService(db));
        services.AddSingleton<Zayra.Api.Application.Organization.IHrmHierarchyService>(
            new Zayra.Api.Infrastructure.Organization.HrmHierarchyService(db, new Zayra.Api.Infrastructure.Audit.AuditService(db)));
        using var sp = services.BuildServiceProvider();
        var controller = ActivatorUtilities.CreateInstance<T>(sp);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principal } };
        return controller;
    }

    private static ReportsAnalyticsController Reports(ZayraDbContext db, Guid tenantId)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim("permission", "reports.read"),
            new Claim("permission", "employees.read"),
            new Claim("permission", "payroll.read"),
            new Claim(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new { v = 2, m = "group", c = Array.Empty<Guid>() })),
        }, "test"));
        return new ReportsAnalyticsController(db)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principal } },
        };
    }

    /// <summary>The seeded HR Manager's relevant keys, limited to one company of a multi-company tenant.</summary>
    private static ClaimsPrincipal CompanyHrManager(Guid tenantId, Guid companyId) =>
        new(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Role, "HR Manager"),
            new Claim("permission", "employees.read"),
            new Claim("permission", "employees.write"),
            new Claim("permission", "manager.read"),
            new Claim("permission", "performance.read"),
            new Claim("permission", "performance.approve"),
            new Claim("FullName", "Company A HR"),
            new Claim(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new { v = 2, m = "companies", c = new[] { companyId } })),
        }, "test"));

    private static ClaimsPrincipal GroupHrAdmin(Guid tenantId, Guid? userId = null) =>
        new(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, (userId ?? Guid.NewGuid()).ToString()),
            new Claim(ClaimTypes.Role, "Admin"),
            new Claim("permission", "employees.read"),
            new Claim("permission", "employees.write"),
            new Claim("permission", "performance.read"),
            new Claim("permission", "performance.approve"),
            new Claim("FullName", "Group HR"),
            new Claim(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new { v = 2, m = "group", c = Array.Empty<Guid>() })),
        }, "test"));

    private static ClaimsPrincipal EmployeeLogin(Guid tenantId, Guid companyId, int employeeId) =>
        new(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Role, "Employee"),
            new Claim("employee_id", employeeId.ToString()),
            new Claim("permission", "performance.read"),
            new Claim("FullName", "Typed Display Name"),
            new Claim(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new { v = 2, m = "companies", c = new[] { companyId } })),
        }, "test"));

    private static IHttpContextAccessor Accessor(ClaimsPrincipal principal) =>
        new FixedAccessor { HttpContext = new DefaultHttpContext { User = principal } };

    private sealed class FixedAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    private static int StatusOf(IActionResult result) => result switch
    {
        ObjectResult o => o.StatusCode ?? 200,
        StatusCodeResult s => s.StatusCode,
        ForbidResult => 403,
        _ => throw new InvalidOperationException($"unexpected result {result.GetType().Name}"),
    };

    private static JsonElement JsonPayload(IActionResult result)
    {
        var value = result.Should().BeOfType<OkObjectResult>().Subject.Value;
        return JsonDocument.Parse(JsonSerializer.Serialize(value,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })).RootElement.Clone();
    }
}
