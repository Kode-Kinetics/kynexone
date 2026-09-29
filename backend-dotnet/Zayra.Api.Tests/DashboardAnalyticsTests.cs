using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Modules;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// /api/dashboard/full, contract v5: approval-item dueAtUtc/department/detail, the 90-day alert
/// window, payrollSummary payDate/employerContributions, and the `analytics` block (attendance
/// heatmap, leave usage, nationality mix, headcount trend).
///
/// <para>In-memory runs in system scope (no company filter); the company half is asserted against
/// Postgres in <see cref="DashboardAnalyticsCompanyScopeTests"/>, which also proves the analytics
/// queries translate on the production provider.</para>
/// </summary>
public class DashboardAnalyticsTests
{
    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static DashboardController Ctrl(ZayraDbContext db, Guid tenantId)
    {
        if (!db.Tenants.Any(t => t.Id == tenantId))
        {
            db.Tenants.Add(new Tenant { Id = tenantId, Name = "Analytics", Slug = $"analytics-{tenantId:N}" });
            db.SaveChanges();
        }
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
        }, "test"));
        return new DashboardController(db,
            new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())),
            new AnalyticsUnrestrictedScope())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principal } },
        };
    }

    private static async Task<DashboardFullDto> Full(ZayraDbContext db, Guid tenantId, int months = 6)
    {
        var result = await Ctrl(db, tenantId).Full(months);
        return Assert.IsType<DashboardFullDto>(Assert.IsType<OkObjectResult>(result).Value);
    }

    private static Employee Emp(ZayraDbContext db, Guid tenantId, string code, string department = "",
        string nationality = "", string status = "Active", DateTime? joined = null)
    {
        var e = new Employee
        {
            TenantId = tenantId, EmployeeCode = code, FullName = $"Person {code}", Status = status,
            Department = department, Nationality = nationality,
            JoiningDate = joined ?? DateTime.UtcNow.AddYears(-2),
        };
        db.Employees.Add(e);
        db.SaveChanges();
        return e;
    }

    private static void Daily(ZayraDbContext db, Guid tenantId, int employeeId, DateOnly day, string status) =>
        db.AttendanceDailyRecords.Add(new AttendanceDailyRecord { TenantId = tenantId, EmployeeId = employeeId, WorkDate = day, Status = status });

    private static DateOnly UtcToday => DateOnly.FromDateTime(DateTime.UtcNow);

    // ── Attendance heatmap ────────────────────────────────────────────────────

    [Fact]
    public async Task Heatmap_RateIsNull_WhenNothingRostered_AndCorrect_WhenRostered()
    {
        using var db = CreateDb();
        var tid = Guid.NewGuid();
        var ops = Enumerable.Range(1, 3).Select(i => Emp(db, tid, $"O{i}", "Operations")).ToList();
        var loner = Emp(db, tid, "U1", department: "  ");
        var today = UtcToday; // no localization row: the tenant-local day is the UTC day
        Daily(db, tid, ops[0].Id, today, AttendanceStatuses.Present);
        Daily(db, tid, ops[1].Id, today, AttendanceStatuses.Late);
        Daily(db, tid, ops[2].Id, today, AttendanceStatuses.Absent);
        Daily(db, tid, ops[0].Id, today.AddDays(-1), AttendanceStatuses.RestDay);
        Daily(db, tid, ops[1].Id, today.AddDays(-1), AttendanceStatuses.OnLeave);
        Daily(db, tid, ops[2].Id, today.AddDays(-1), AttendanceStatuses.PublicHoliday);
        Daily(db, tid, loner.Id, today, AttendanceStatuses.HalfDay);
        // Outside the 15-day window.
        Daily(db, tid, ops[0].Id, today.AddDays(-15), AttendanceStatuses.Present);
        db.SaveChanges();

        var heatmap = (await Full(db, tid)).Analytics!.AttendanceHeatmap;

        heatmap.Days.Should().HaveCount(15);
        heatmap.Days[0].Should().Be(today.AddDays(-14).ToString("yyyy-MM-dd"));
        heatmap.Days[^1].Should().Be(today.ToString("yyyy-MM-dd"));
        heatmap.Days.Should().BeInAscendingOrder();

        heatmap.Departments.Select(d => d.Name).Should().Equal("Operations", "Unassigned");
        var opsRow = heatmap.Departments[0];
        opsRow.Headcount.Should().Be(3);
        opsRow.Cells.Should().HaveCount(15);
        opsRow.Cells.Select(c => c.Date).Should().Equal(heatmap.Days);

        var todayCell = opsRow.Cells[^1];
        todayCell.Rostered.Should().Be(3);
        todayCell.Attended.Should().Be(2, "Late counts as attended, Absent does not");
        todayCell.Rate.Should().Be(66.7m);

        var restCell = opsRow.Cells[^2];
        restCell.Rostered.Should().Be(0, "rest day, leave and public holiday are not rostered");
        restCell.Attended.Should().Be(0);
        restCell.Rate.Should().BeNull();

        opsRow.Cells[0].Rate.Should().BeNull("a day with no records has nothing rostered");

        var unassigned = heatmap.Departments[1];
        unassigned.Headcount.Should().Be(1);
        unassigned.Cells[^1].Rate.Should().Be(100m);
    }

    [Fact]
    public async Task Heatmap_KeepsTheEightLargestDepartments_AndOnlyActiveEmployees()
    {
        using var db = CreateDb();
        var tid = Guid.NewGuid();
        for (var d = 0; d < 9; d++)
            for (var i = 0; i <= d; i++)
                Emp(db, tid, $"D{d}-{i}", $"Dept {d}");
        var leaver = Emp(db, tid, "X1", "Dept 0", status: "Terminated");
        Daily(db, tid, leaver.Id, UtcToday, AttendanceStatuses.Present);
        db.SaveChanges();

        var departments = (await Full(db, tid)).Analytics!.AttendanceHeatmap.Departments;

        departments.Should().HaveCount(8);
        departments.Select(d => d.Name).Should().NotContain("Dept 0", "it is the smallest department");
        departments[0].Name.Should().Be("Dept 8");
        departments[0].Headcount.Should().Be(9);
        departments.SelectMany(d => d.Cells).Sum(c => c.Rostered).Should().Be(0, "the leaver's record is not counted");
    }

    // ── Leave usage ───────────────────────────────────────────────────────────

    [Fact]
    public async Task LeaveUsage_SumsOnlyApprovedCurrentYearLeave_ByType_WithEntitlements()
    {
        using var db = CreateDb();
        var tid = Guid.NewGuid();
        var e = Emp(db, tid, "L1", "HR");
        var year = UtcToday.Year;
        void Leave(string type, string status, DateOnly start, DateOnly end, decimal days) =>
            db.LeaveRequests.Add(new LeaveRequest
            {
                TenantId = tid, EmployeeId = e.Id, LeaveTypeName = type, Status = status,
                StartDate = start, EndDate = end, TotalDays = days,
            });
        Leave("Annual", "Approved", new DateOnly(year, 3, 1), new DateOnly(year, 3, 5), 5m);
        Leave("Sick", "Approved", new DateOnly(year, 4, 10), new DateOnly(year, 4, 11), 2m);
        Leave("Annual", "Pending", new DateOnly(year, 5, 1), new DateOnly(year, 5, 3), 3m);
        Leave("Annual", "Rejected", new DateOnly(year, 6, 1), new DateOnly(year, 6, 3), 3m);
        Leave("Annual", "Approved", new DateOnly(year - 1, 6, 1), new DateOnly(year - 1, 6, 10), 10m);
        // Straddles 1 January: 5 calendar days, 2 of them in this year.
        Leave("Annual", "Approved", new DateOnly(year - 1, 12, 29), new DateOnly(year, 1, 2), 5m);

        db.EmployeeLeaveBalances.AddRange(
            new EmployeeLeaveBalance { TenantId = tid, EmployeeId = e.Id, Year = year, Entitled = 30m, Accrued = 17.5m, CarriedForward = 2m },
            new EmployeeLeaveBalance { TenantId = tid, EmployeeId = e.Id, Year = year, Entitled = 0m, Accrued = 10m },
            new EmployeeLeaveBalance { TenantId = tid, EmployeeId = e.Id, Year = year - 1, Entitled = 30m });
        db.SaveChanges();

        var usage = (await Full(db, tid)).Analytics!.LeaveUsage;

        usage.Year.Should().Be(year);
        usage.TakenDays.Should().Be(9m, "5 + 2 + the 2 in-year days of the straddling request");
        usage.ByType.Select(t => (t.Type, t.Days)).Should().Equal(("Annual", 7m), ("Sick", 2m));
        usage.EntitlementDays.Should().Be(42m, "MAX(30, 17.5) + 2 carried + MAX(0, 10); last year's row excluded");
    }

    [Fact]
    public async Task LeaveUsage_EntitlementIsNull_WhenNoBalancesExist()
    {
        using var db = CreateDb();
        var tid = Guid.NewGuid();
        Emp(db, tid, "N1");

        var usage = (await Full(db, tid)).Analytics!.LeaveUsage;

        usage.TakenDays.Should().Be(0m);
        usage.EntitlementDays.Should().BeNull();
        usage.ByType.Should().BeEmpty();
    }

    // ── Nationality ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Nationality_SplitsSaudiNonSaudiUnknown_AndReportsTheStoredBand()
    {
        using var db = CreateDb();
        var tid = Guid.NewGuid();
        Emp(db, tid, "S1", nationality: "SA");
        Emp(db, tid, "S2", nationality: "sau");
        Emp(db, tid, "S3", nationality: " Saudi Arabia ");
        Emp(db, tid, "S4", nationality: "saudi");
        Emp(db, tid, "F1", nationality: "Indian");
        Emp(db, tid, "F2", nationality: "EG");
        Emp(db, tid, "F3", nationality: "Pakistani");
        Emp(db, tid, "F4", nationality: "IN");
        Emp(db, tid, "U1", nationality: "");
        Emp(db, tid, "X1", nationality: "SA", status: "Terminated");
        var company = Guid.NewGuid();
        db.NitaqatStandingSnapshots.AddRange(
            new NitaqatStandingSnapshot { TenantId = tid, CompanyId = company, AsOfDate = UtcToday.AddDays(-30), Band = NitaqatBands.Red },
            new NitaqatStandingSnapshot { TenantId = tid, CompanyId = company, AsOfDate = UtcToday.AddDays(-1), Band = NitaqatBands.MediumGreen });
        db.SaveChanges();

        var nat = (await Full(db, tid)).Analytics!.Nationality;

        nat.Should().NotBeNull();
        nat!.Saudi.Should().Be(4);
        nat.NonSaudi.Should().Be(4);
        nat.Unknown.Should().Be(1);
        nat.SaudizationPct.Should().Be(50.0m);
        nat.NitaqatBand.Should().Be(NitaqatBands.MediumGreen, "the latest stored snapshot for the one visible establishment");
    }

    [Fact]
    public async Task Nationality_PctIsNull_WithNoKnownNationality_AndBandIsNull_AcrossSeveralEstablishments()
    {
        using var db = CreateDb();
        var tid = Guid.NewGuid();
        Emp(db, tid, "U1");
        db.NitaqatStandingSnapshots.AddRange(
            new NitaqatStandingSnapshot { TenantId = tid, CompanyId = Guid.NewGuid(), AsOfDate = UtcToday, Band = NitaqatBands.Red },
            new NitaqatStandingSnapshot { TenantId = tid, CompanyId = Guid.NewGuid(), AsOfDate = UtcToday, Band = NitaqatBands.Platinum });
        db.SaveChanges();

        var nat = (await Full(db, tid)).Analytics!.Nationality!;

        nat.Saudi.Should().Be(0);
        nat.NonSaudi.Should().Be(0);
        nat.Unknown.Should().Be(1);
        nat.SaudizationPct.Should().BeNull();
        nat.NitaqatBand.Should().BeNull("two establishments have no single band");
    }

    [Fact]
    public async Task Nationality_IsNull_WhenTheSaudizationModuleIsOff()
    {
        using var db = CreateDb();
        var tid = Guid.NewGuid();
        Emp(db, tid, "S1", nationality: "SA");
        // Saudization is statutory (not disableable) in KSA; a UAE tenant may switch it off.
        db.TenantLocalizationSettings.Add(new TenantLocalizationSetting { TenantId = tid, CountryCode = "AE" });
        db.TenantFeatureFlags.Add(new TenantFeatureFlag { TenantId = tid, FeatureKey = ModuleKeys.Saudization, IsEnabled = false });
        db.SaveChanges();

        var analytics = (await Full(db, tid)).Analytics!;

        analytics.Nationality.Should().BeNull();
        analytics.AttendanceHeatmap.Should().NotBeNull("only the Saudization figures are gated");
    }

    // ── Headcount trend ───────────────────────────────────────────────────────

    [Fact]
    public async Task HeadcountTrend_CountsActiveAtEachMonthEnd_FromJoinAndExitDates()
    {
        using var db = CreateDb();
        var tid = Guid.NewGuid();
        var today = UtcToday;
        var thisMonth = new DateOnly(today.Year, today.Month, 1);
        var firstMonth = thisMonth.AddMonths(-2);
        var firstMonthEnd = firstMonth.AddMonths(1).AddDays(-1);

        Emp(db, tid, "A1", joined: DateTime.UtcNow.AddYears(-1));
        Emp(db, tid, "A2", joined: DateTime.UtcNow.AddYears(-1));
        // Joined this month: only in the current point.
        Emp(db, tid, "N1", joined: thisMonth.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        // Left on the last day of the first month (offboarding LWD): in the first point only.
        var leaverA = Emp(db, tid, "L1", status: "Archived", joined: DateTime.UtcNow.AddYears(-1));
        db.EmployeeOffboardings.Add(new EmployeeOffboarding { TenantId = tid, EmployeeId = leaverA.Id, LastWorkingDay = firstMonthEnd, Status = "Completed" });
        // Terminated with only a status-history row, effective in the middle of the first month: nowhere.
        var leaverB = Emp(db, tid, "L2", status: "Terminated", joined: DateTime.UtcNow.AddYears(-1));
        db.EmployeeStatusHistories.Add(new EmployeeStatusHistory { TenantId = tid, EmployeeId = leaverB.Id, OldStatus = "Active", NewStatus = "Terminated", EffectiveDate = firstMonth.AddDays(10) });
        // A leaver whose exit cannot be dated is not guessed into any month.
        Emp(db, tid, "L3", status: "Terminated", joined: DateTime.UtcNow.AddYears(-1));
        // Never active.
        Emp(db, tid, "D1", status: "Draft", joined: DateTime.UtcNow.AddYears(-1));
        db.SaveChanges();

        var trend = (await Full(db, tid, months: 3)).Analytics!.HeadcountTrend;

        trend.Select(p => p.Month).Should().Equal(
            firstMonth.ToString("MMM"), firstMonth.AddMonths(1).ToString("MMM"), thisMonth.ToString("MMM"));
        trend.Select(p => p.Active).Should().Equal(3, 2, 3);
    }

    // ── Overview additions ────────────────────────────────────────────────────

    [Fact]
    public async Task ApprovalItem_CarriesDueAtUtc_Department_AndDetail()
    {
        using var db = CreateDb();
        var tid = Guid.NewGuid();
        var e = Emp(db, tid, "E1", "Finance");
        var now = DateTime.UtcNow;
        var due = new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);

        var rangeLeave = new LeaveRequest { TenantId = tid, EmployeeId = e.Id, LeaveTypeName = "Annual", Status = "Submitted",
            StartDate = new DateOnly(2026, 10, 12), EndDate = new DateOnly(2026, 10, 18), TotalDays = 5 };
        var dayLeave = new LeaveRequest { TenantId = tid, EmployeeId = e.Id, LeaveTypeName = "Sick", Status = "Submitted",
            StartDate = new DateOnly(2026, 9, 29), EndDate = new DateOnly(2026, 9, 29), TotalDays = 1 };
        var change = new EmployeeChangeRequest { TenantId = tid, EmployeeId = e.Id, SensitiveFields = "bankIban,passportNumber" };
        db.LeaveRequests.AddRange(rangeLeave, dayLeave);
        db.EmployeeChangeRequests.Add(change);
        db.ApprovalRequests.AddRange(
            new ApprovalRequest { Id = rangeLeave.Id, TenantId = tid, EntityName = nameof(LeaveRequest), EntityId = rangeLeave.Id.ToString(),
                Title = "Annual — E1", Status = "Pending", RequestedForEmployeeId = e.Id, DueAtUtc = due, CreatedAtUtc = now.AddMinutes(-1) },
            new ApprovalRequest { Id = dayLeave.Id, TenantId = tid, EntityName = nameof(LeaveRequest), EntityId = dayLeave.Id.ToString(),
                Title = "Sick — E1", Status = "Pending", RequestedForEmployeeId = e.Id, CreatedAtUtc = now.AddMinutes(-2) },
            new ApprovalRequest { TenantId = tid, EntityName = nameof(EmployeeChangeRequest), EntityId = change.Id.ToString(),
                Title = "Employee change", Status = "Pending", RequestedForEmployeeId = e.Id, CreatedAtUtc = now.AddMinutes(-3) },
            new ApprovalRequest { TenantId = tid, EntityName = "PayrollRun", EntityId = Guid.NewGuid().ToString(),
                Title = "Payroll", Status = "Pending", CreatedAtUtc = now.AddMinutes(-4) });
        db.SaveChanges();

        var queue = (await Full(db, tid)).Overview.ApprovalQueue;

        queue.Should().HaveCount(4);
        queue[0].DueAtUtc.Should().Be(due);
        queue[0].Department.Should().Be("Finance");
        queue[0].Detail.Should().Be("12 Oct to 18 Oct");
        queue[1].Detail.Should().Be("29 Sep");
        queue[1].DueAtUtc.Should().BeNull();
        queue[2].Detail.Should().Be("IBAN, passport");
        queue[3].Detail.Should().BeNull("no cheap detail for other modules");
        queue[3].Department.Should().BeNull("no subject employee");
    }

    [Fact]
    public async Task ApprovalQueue_ShowsEightRows()
    {
        using var db = CreateDb();
        var tid = Guid.NewGuid();
        for (var i = 0; i < 10; i++)
            db.ApprovalRequests.Add(new ApprovalRequest { TenantId = tid, EntityName = "Loan", Title = $"A{i}", Status = "Pending",
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-i) });
        db.SaveChanges();

        var overview = (await Full(db, tid)).Overview;

        overview.ApprovalQueue.Select(q => q.Title).Should().Equal("A0", "A1", "A2", "A3", "A4", "A5", "A6", "A7");
        overview.PendingApprovals.Should().Be(10);
    }

    [Fact]
    public async Task Alerts_Window_Includes75Days_AndExcludes120Days()
    {
        using var db = CreateDb();
        var tid = Guid.NewGuid();
        var a = Emp(db, tid, "W1");
        var b = Emp(db, tid, "W2");
        db.EmployeeComplianceRecords.AddRange(
            new EmployeeComplianceRecord { TenantId = tid, EmployeeId = a.Id, CountryCode = "SA", FieldKey = "iqama_number",
                FieldLabel = "Iqama", FieldValue = "X", ExpiryDate = UtcToday.AddDays(75) },
            new EmployeeComplianceRecord { TenantId = tid, EmployeeId = b.Id, CountryCode = "SA", FieldKey = "passport_number",
                FieldLabel = "Passport", FieldValue = "X", ExpiryDate = UtcToday.AddDays(120) });
        db.SaveChanges();

        var overview = (await Full(db, tid)).Overview;

        var alert = overview.Alerts.Should().ContainSingle().Subject;
        alert.EmployeeId.Should().Be(a.Id);
        alert.DaysRemaining.Should().Be(75);
        alert.Severity.Should().Be("Info");
        overview.ComplianceAlertsTotal.Should().Be(1, "the total uses the same 90-day window");
    }

    [Fact]
    public async Task PayrollSummary_CarriesEmployerContributions_AndTheBankValueDate()
    {
        using var db = CreateDb();
        var tid = Guid.NewGuid();
        var today = UtcToday;
        var run = new PayrollRun { TenantId = tid, Year = today.Year, Month = today.Month, Status = "Approved",
            TotalGrossSalary = 10_000m, TotalNetSalary = 9_000m, TotalDeductions = 1_000m, TotalEmployerStatutoryCost = 1_175.5m, EmployeeCount = 2 };
        db.PayrollRuns.Add(run);
        db.SaveChanges();

        var before = (await Full(db, tid)).Overview.PayrollSummary!;
        before.EmployerContributions.Should().Be(1_175.5m);
        before.PayDate.Should().BeNull("no bank confirmation has been applied");

        var batch = new PayrollPaymentBatch { TenantId = tid, PayrollRunId = run.Id, WpsStatus = WpsStatuses.Paid };
        db.PayrollPaymentBatches.Add(batch);
        db.BankPaymentConfirmations.AddRange(
            new BankPaymentConfirmation { TenantId = tid, PaymentBatchId = batch.Id, Outcome = BankConfirmationOutcomes.Paid, Applied = true, ValueDate = new DateOnly(2026, 9, 25) },
            new BankPaymentConfirmation { TenantId = tid, PaymentBatchId = batch.Id, Outcome = BankConfirmationOutcomes.Paid, Applied = true, ValueDate = new DateOnly(2026, 9, 27) },
            // Held back: not applied, so not a payment.
            new BankPaymentConfirmation { TenantId = tid, PaymentBatchId = batch.Id, Outcome = BankConfirmationOutcomes.Paid, Applied = false, ValueDate = new DateOnly(2026, 9, 30) });
        db.SaveChanges();

        // A new controller has a fresh cache.
        var after = (await Full(db, tid)).Overview.PayrollSummary!;
        after.PayDate.Should().Be("2026-09-27");
    }

    // ── Wire shape ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Full_SerializesTheNewFields_WithTheExactCamelCaseNames()
    {
        using var db = CreateDb();
        var tid = Guid.NewGuid();
        var e = Emp(db, tid, "J1", "Ops", "SA");
        Daily(db, tid, e.Id, UtcToday, AttendanceStatuses.Present);
        db.ApprovalRequests.Add(new ApprovalRequest { TenantId = tid, EntityName = "Loan", Title = "Loan", Status = "Pending", RequestedForEmployeeId = e.Id });
        db.PayrollRuns.Add(new PayrollRun { TenantId = tid, Year = UtcToday.Year, Month = UtcToday.Month, Status = "Draft" });
        db.SaveChanges();

        var json = JsonSerializer.Serialize(await Full(db, tid), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        static void Has(JsonElement el, params string[] names)
        {
            foreach (var n in names) el.TryGetProperty(n, out _).Should().BeTrue($"'{n}' must be on the wire");
        }

        var item = root.GetProperty("overview").GetProperty("approvalQueue")[0];
        Has(item, "dueAtUtc", "department", "detail", "employeeId", "employeeCode", "employeeName");
        Has(root.GetProperty("overview").GetProperty("payrollSummary"), "payDate", "employerContributions", "status");

        var analytics = root.GetProperty("analytics");
        Has(analytics, "attendanceHeatmap", "leaveUsage", "nationality", "headcountTrend");
        var heat = analytics.GetProperty("attendanceHeatmap");
        Has(heat, "days", "departments");
        heat.GetProperty("days")[0].ValueKind.Should().Be(JsonValueKind.String);
        var dept = heat.GetProperty("departments")[0];
        Has(dept, "name", "headcount", "cells");
        Has(dept.GetProperty("cells")[0], "date", "rostered", "attended", "rate");
        Has(analytics.GetProperty("leaveUsage"), "year", "takenDays", "entitlementDays", "byType");
        Has(analytics.GetProperty("nationality"), "saudi", "nonSaudi", "unknown", "saudizationPct", "nitaqatBand");
        Has(analytics.GetProperty("headcountTrend")[0], "month", "active");

        var byType = JsonSerializer.Serialize(new LeaveUsageByTypeDto("Annual", 3m), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        byType.Should().Be("{\"type\":\"Annual\",\"days\":3}");
    }
}

/// <summary>
/// Company scope of the analytics block, on Postgres: AttendanceDailyRecord and EmployeeLeaveBalance
/// have no CompanyId, so a company-A caller must not see company B's employees in the heatmap, the
/// nationality split or leave usage. Running /full here also proves every new query translates.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class DashboardAnalyticsCompanyScopeTests
{
    private readonly PostgresFixture _fx;
    public DashboardAnalyticsCompanyScopeTests(PostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task CompanyA_Analytics_ExcludeCompanyB_Employees()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var year = today.Year;
        Guid tenantId, companyA;
        await using (var seed = _fx.CreateDb())
        {
            tenantId = await PostgresFixture.SeedMinimalTenant(seed);
            var a = Company(tenantId, "Alpha");
            var b = Company(tenantId, "Beta");
            seed.Companies.AddRange(a, b);
            await seed.SaveChangesAsync();
            companyA = a.Id;

            var ea = Employee(tenantId, a.Id, "Alpha Person", "Ops", "SA");
            var eb = Employee(tenantId, b.Id, "Beta Person", "Ops", "Indian");
            var eb2 = Employee(tenantId, b.Id, "Beta Other", "Sales", "EG");
            seed.Employees.AddRange(ea, eb, eb2);
            await seed.SaveChangesAsync();

            foreach (var (emp, status) in new[] { (ea, AttendanceStatuses.Present), (eb, AttendanceStatuses.Absent), (eb2, AttendanceStatuses.Present) })
                seed.AttendanceDailyRecords.Add(new AttendanceDailyRecord { TenantId = tenantId, EmployeeId = emp.Id, WorkDate = today, Status = status });
            foreach (var (emp, company) in new[] { (ea, a.Id), (eb, b.Id) })
            {
                seed.LeaveRequests.Add(new LeaveRequest
                {
                    TenantId = tenantId, CompanyId = company, EmployeeId = emp.Id, LeaveTypeName = "Annual", Status = "Approved",
                    StartDate = new DateOnly(year, 1, 5), EndDate = new DateOnly(year, 1, 7), TotalDays = 3m,
                });
                seed.EmployeeLeaveBalances.Add(new EmployeeLeaveBalance { TenantId = tenantId, EmployeeId = emp.Id, Year = year, Entitled = 21m });
            }
            seed.NitaqatStandingSnapshots.Add(new NitaqatStandingSnapshot { TenantId = tenantId, CompanyId = a.Id, AsOfDate = today, Band = NitaqatBands.LowGreen });
            seed.NitaqatStandingSnapshots.Add(new NitaqatStandingSnapshot { TenantId = tenantId, CompanyId = b.Id, AsOfDate = today, Band = NitaqatBands.Red });
            await seed.SaveChangesAsync();
        }

        var accessor = new AnalyticsScopeAccessor();
        await using var db = _fx.CreateDbWithAccessor(accessor);
        var http = ScopedContext(tenantId, companyA);
        accessor.HttpContext = http;
        var ctrl = new DashboardController(db,
            new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())), new AnalyticsUnrestrictedScope())
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };

        var payload = (DashboardFullDto)((OkObjectResult)await ctrl.Full(6, CancellationToken.None)).Value!;
        var analytics = payload.Analytics!;

        payload.Summary.ActiveEmployees.Should().Be(1, "fixture sanity: company A has one employee");
        var dept = analytics.AttendanceHeatmap.Departments.Should().ContainSingle().Subject;
        dept.Name.Should().Be("Ops");
        dept.Headcount.Should().Be(1);
        var cell = dept.Cells.Single(c => c.Date == today.ToString("yyyy-MM-dd"));
        cell.Rostered.Should().Be(1, "company B's Absent Ops employee is invisible");
        cell.Attended.Should().Be(1);
        cell.Rate.Should().Be(100m);

        analytics.LeaveUsage.TakenDays.Should().Be(3m);
        analytics.LeaveUsage.EntitlementDays.Should().Be(21m);

        analytics.Nationality!.Saudi.Should().Be(1);
        analytics.Nationality.NonSaudi.Should().Be(0);
        analytics.Nationality.SaudizationPct.Should().Be(100m);
        analytics.Nationality.NitaqatBand.Should().Be(NitaqatBands.LowGreen, "only company A's establishment is visible");

        analytics.HeadcountTrend.Should().HaveCount(6);
        analytics.HeadcountTrend[^1].Active.Should().Be(1);
    }

    private static Company Company(Guid tenantId, string name) => new()
    {
        Id = Guid.NewGuid(), TenantId = tenantId, LegalNameEn = name, CountryCode = "SAU",
        Jurisdiction = "KSA-mainland", RegistrationNumber = $"REG-{Guid.NewGuid():N}", DefaultCurrency = "SAR",
        IsActive = true, CreatedAtUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    private static Employee Employee(Guid tenantId, Guid companyId, string name, string department, string nationality) => new()
    {
        TenantId = tenantId, CompanyId = companyId, EmployeeCode = $"AN-{Guid.NewGuid():N}".Substring(0, 14),
        FullName = name, Status = "Active", Department = department, Nationality = nationality,
        JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    private static HttpContext ScopedContext(Guid tenantId, Guid companyId)
    {
        var accessJson = JsonSerializer.Serialize(new { c = companyId, r = "Viewer" });
        return new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim("tenant_id", tenantId.ToString()),
                new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                new Claim("entity_access", accessJson),
            }, "Test")),
        };
    }

    private sealed class AnalyticsScopeAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }
}

file sealed class AnalyticsUnrestrictedScope : IDataScopeService
{
    public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
        => Task.FromResult(new DataScope { Level = DataScopeLevel.Organization, AllowedEmployeeIds = null });
}
