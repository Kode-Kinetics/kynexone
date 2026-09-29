using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers.Reports;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// The analytics tab of the Reports page: its windows are bounded, its vocabulary matches the
/// attendance module, and each figure is authorized against the data behind it.
/// </summary>
public sealed class ReportsAnalyticsConsistencyTests
{
    /// <summary>What an HR director's token carries for this tab.</summary>
    private static readonly string[] FullReader =
    [
        "reports.read", "employees.read", "attendance.read", "leave.read", "overtime.read", "payroll.read",
    ];

    [Fact]
    public async Task Kpis_CountOnlyThisMonth_AndTreatLateAndHalfDayAsAttended()
    {
        await using var db = Db();
        var tid = await SeedTenantAsync(db);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var month = new DateOnly(today.Year, today.Month, 1);
        var next = month.AddMonths(1);
        var currentJoin = Emp(tid, "NOW", "Active", Utc(month));
        var futureJoin = Emp(tid, "FUT", "Active", Utc(next));             // joins next month: not "new this month"
        var currentExit = Emp(tid, "EXIT1", "Resigned", DateTime.UtcNow.AddYears(-1), today);
        var futureExit = Emp(tid, "EXIT2", "Resigned", DateTime.UtcNow.AddYears(-1), next);   // leaves next month
        db.Employees.AddRange(currentJoin, futureJoin, currentExit, futureExit);
        await db.SaveChangesAsync();
        db.AttendanceDailyRecords.AddRange(
            Day(tid, currentJoin.Id, today, AttendanceStatuses.Present),
            Day(tid, futureJoin.Id, today, AttendanceStatuses.Late, lateMinutes: 5),
            Day(tid, currentExit.Id, today, AttendanceStatuses.HalfDay));
        await db.SaveChangesAsync();

        var json = Ok(await Analytics(db, tid, FullReader).GetKPIs(default));

        Assert.Equal(1, json.GetProperty("headcount").GetProperty("newThisMonth").GetInt32());
        Assert.Equal(1, json.GetProperty("headcount").GetProperty("exitsThisMonth").GetInt32());
        Assert.Equal(3, json.GetProperty("attendance").GetProperty("presentToday").GetInt32());
        Assert.Equal(1, json.GetProperty("attendance").GetProperty("lateToday").GetInt32());
    }

    [Fact]
    public async Task AttendanceTrend_DaysMeansExactlyThatManyCalendarDaysEndingToday()
    {
        await using var db = Db();
        var tid = await SeedTenantAsync(db);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        for (var i = -1; i <= 30; i++)   // tomorrow, today, and 30 days back
            db.AttendanceDailyRecords.Add(Day(tid, 1000 + i, today.AddDays(-i), AttendanceStatuses.Present));
        await db.SaveChangesAsync();

        var json = Ok(await Analytics(db, tid, FullReader).AttendanceTrend(30, default));

        Assert.Equal(30, json.GetArrayLength());
        Assert.Equal(today.AddDays(-29).ToString("yyyy-MM-dd"), json[0].GetProperty("date").GetString());
        Assert.Equal(today.ToString("yyyy-MM-dd"), json[29].GetProperty("date").GetString());
    }

    [Theory]
    [InlineData(nameof(AnalyticsController.PayrollTrend))]
    [InlineData(nameof(AnalyticsController.DepartmentComparison))]
    public void PayrollAnalytics_AreGatedOnPermissions_NotOnAStaleRoleList(string methodName)
    {
        // The list named a "Finance" role that no tenant has, and left out Payroll Manager, Payroll
        // Officer, HR Director and Auditor — every role that actually holds payroll.read.
        var attrs = typeof(AnalyticsController).GetMethod(methodName)!
            .GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>();

        Assert.DoesNotContain(attrs, a => !string.IsNullOrWhiteSpace(a.Roles));
    }

    [Fact]
    public async Task PayrollTrend_WithoutPayrollRead_IsRefusedWithAReason()
    {
        await using var db = Db();
        var tid = await SeedTenantAsync(db);
        db.PayrollRuns.Add(new PayrollRun { TenantId = tid, Year = 2026, Month = 8, Status = "Locked", TotalNetSalary = 250000 });
        await db.SaveChangesAsync();
        var compliance = Analytics(db, tid, "reports.read", "employees.read", "compliance.read");

        ReportDataAuthorizationTests.AssertForbiddenWithReason(await compliance.PayrollTrend(6, default), "payroll.read");
        ReportDataAuthorizationTests.AssertForbiddenWithReason(await compliance.DepartmentComparison(default), "payroll.read");
    }

    [Fact]
    public async Task Kpis_WithholdPayrollFigures_FromACallerWithoutPayrollRead()
    {
        await using var db = Db();
        var tid = await SeedTenantAsync(db);
        db.PayrollRuns.Add(new PayrollRun { TenantId = tid, Year = 2026, Month = 8, Status = "Locked", TotalNetSalary = 250000 });
        await db.SaveChangesAsync();

        var withheld = JsonSerializer.Serialize(Ok(await Analytics(db, tid, "reports.read", "employees.read", "compliance.read").GetKPIs(default)));
        Assert.DoesNotContain("250000", withheld);
        Assert.Contains("payroll.read", withheld);   // says what was held back, rather than showing nothing

        var shown = JsonSerializer.Serialize(Ok(await Analytics(db, tid, FullReader).GetKPIs(default)));
        Assert.Contains("250000", shown);
    }

    /// <summary>
    /// Each gated KPI section, the fields it carries, and a data permission that reveals it. Leave,
    /// attendance and overtime are not here: their modules' lists have no permission gate (only the
    /// employee scope), so the KPI shows them to every organisation-wide reader, as the modules do.
    /// </summary>
    public static TheoryData<string, string[], string> GatedSections => new()
    {
        { "payroll", ["lastRunYear", "lastRunMonth", "lastRunStatus", "totalNetSalary", "currencyCode", "runCount", "byCurrency"], "payroll.read" },
        { "compliance", ["visasExpiring", "passportsExpiring"], "compliance.read" },
        { "recruitment", ["openPositions", "pendingApplications"], "recruitment.read" },
        { "financial", ["activeLoans", "outstandingLoanBalance"], "loans.read" },
    };

    [Fact]
    public async Task Kpis_ShowLeaveAttendanceAndOvertime_ToEveryOrganisationReader_AsTheirModulesDo()
    {
        await using var db = Db();
        var tid = await SeedEverySectionAsync(db);

        var json = Ok(await Analytics(db, tid, "reports.read", "employees.read").GetKPIs(default));

        Assert.Equal(1, json.GetProperty("leave").GetProperty("onLeaveToday").GetInt32());
        Assert.Equal(1, json.GetProperty("attendance").GetProperty("presentToday").GetInt32());
        Assert.Equal(1, json.GetProperty("overtime").GetProperty("pendingOT").GetInt32());
    }

    [Fact]
    public async Task Kpis_AdmitTheModulesRoleList_NotOnlyItsPermission()
    {
        // HR Manager holds neither recruitment.read nor compliance.read, but the recruitment reports and
        // visa-tracking modules admit the role by name, so the KPI does too.
        await using var db = Db();
        var tid = await SeedEverySectionAsync(db);

        var json = Ok(await AnalyticsAs(db, tid, "HR Manager", "reports.read", "employees.read").GetKPIs(default));

        Assert.Equal(1, json.GetProperty("recruitment").GetProperty("openPositions").GetInt32());
        Assert.Equal(1, json.GetProperty("compliance").GetProperty("visasExpiring").GetInt32());
    }

    // ── Payroll figures: by period, across companies, never across currencies ─────────────

    [Fact]
    public async Task Kpis_LatestPayroll_SkipsAVoidedRun()
    {
        await using var db = Db();
        var tid = await SeedTenantAsync(db);
        db.PayrollRuns.AddRange(
            new PayrollRun { TenantId = tid, Year = 2026, Month = 8, Status = "Locked", TotalNetSalary = 250000 },
            new PayrollRun { TenantId = tid, Year = 2026, Month = 9, Status = "Voided", TotalNetSalary = 999 });
        await db.SaveChangesAsync();

        var payroll = Ok(await Analytics(db, tid, FullReader).GetKPIs(default)).GetProperty("payroll");

        Assert.Equal(8, payroll.GetProperty("lastRunMonth").GetInt32());
        Assert.Equal("Locked", payroll.GetProperty("lastRunStatus").GetString());
        Assert.Equal(250000m, payroll.GetProperty("totalNetSalary").GetDecimal());
    }

    [Fact]
    public async Task Kpis_LatestPayroll_IsEveryCompanysRunForThePeriod_NotOneOfThem()
    {
        await using var db = Db();
        var tid = await SeedTenantAsync(db);
        var a = new Company { TenantId = tid, LegalNameEn = "A", DefaultCurrency = "SAR", IsActive = true };
        var b = new Company { TenantId = tid, LegalNameEn = "B", DefaultCurrency = "SAR", IsActive = true };
        db.Companies.AddRange(a, b);
        db.PayrollRuns.AddRange(
            new PayrollRun { TenantId = tid, CompanyId = a.Id, Year = 2026, Month = 9, Status = "Locked", TotalNetSalary = 100000 },
            new PayrollRun { TenantId = tid, CompanyId = b.Id, Year = 2026, Month = 9, Status = "Locked", TotalNetSalary = 40000 });
        await db.SaveChangesAsync();

        var payroll = Ok(await Analytics(db, tid, FullReader).GetKPIs(default)).GetProperty("payroll");

        Assert.Equal(140000m, payroll.GetProperty("totalNetSalary").GetDecimal());
        Assert.Equal("SAR", payroll.GetProperty("currencyCode").GetString());
    }

    [Fact]
    public async Task Kpis_LatestPayroll_InTwoCurrencies_IsNotAddedUp()
    {
        await using var db = Db();
        var tid = await SeedTenantAsync(db);
        var ksa = new Company { TenantId = tid, LegalNameEn = "KSA", DefaultCurrency = "SAR", IsActive = true };
        var uae = new Company { TenantId = tid, LegalNameEn = "UAE", DefaultCurrency = "AED", IsActive = true };
        db.Companies.AddRange(ksa, uae);
        db.PayrollRuns.AddRange(
            new PayrollRun { TenantId = tid, CompanyId = ksa.Id, Year = 2026, Month = 9, Status = "Locked", TotalNetSalary = 100000 },
            new PayrollRun { TenantId = tid, CompanyId = uae.Id, Year = 2026, Month = 9, Status = "Locked", TotalNetSalary = 40000 });
        await db.SaveChangesAsync();

        var payroll = Ok(await Analytics(db, tid, FullReader).GetKPIs(default)).GetProperty("payroll");

        // No single total exists across SAR and AED; the per-currency totals do.
        Assert.Equal(JsonValueKind.Null, payroll.GetProperty("totalNetSalary").ValueKind);
        var byCurrency = payroll.GetProperty("byCurrency").EnumerateArray()
            .ToDictionary(x => x.GetProperty("currencyCode").GetString()!, x => x.GetProperty("totalNetSalary").GetDecimal());
        Assert.Equal(100000m, byCurrency["SAR"]);
        Assert.Equal(40000m, byCurrency["AED"]);
    }

    [Fact]
    public async Task PayrollTrend_MonthsMeansPayPeriods_NotRuns()
    {
        await using var db = Db();
        var tid = await SeedTenantAsync(db);
        var companies = Enumerable.Range(0, 3).Select(i => new Company { TenantId = tid, LegalNameEn = $"C{i}", DefaultCurrency = "SAR", IsActive = true }).ToList();
        db.Companies.AddRange(companies);
        foreach (var month in new[] { 7, 8, 9 })
            foreach (var (company, net) in companies.Zip(new[] { 1000m, 2000m, 3000m }))
                db.PayrollRuns.Add(new PayrollRun { TenantId = tid, CompanyId = company.Id, Year = 2026, Month = month, Status = "Locked", TotalNetSalary = net });
        db.PayrollRuns.Add(new PayrollRun { TenantId = tid, CompanyId = companies[0].Id, Year = 2026, Month = 9, Status = "Voided", TotalNetSalary = 50000 });
        await db.SaveChangesAsync();

        var rows = Ok(await Analytics(db, tid, FullReader).PayrollTrend(2, default)).EnumerateArray().ToList();

        Assert.Equal(["2026-08", "2026-09"], rows.Select(r => r.GetProperty("period").GetString()).OrderBy(p => p));
        Assert.All(rows, r => Assert.Equal(6000m, r.GetProperty("TotalNetSalary").GetDecimal()));
    }

    [Fact]
    public async Task PayrollTrend_InTwoCurrencies_GivesOnePointPerCurrency()
    {
        await using var db = Db();
        var tid = await SeedTenantAsync(db);
        var ksa = new Company { TenantId = tid, LegalNameEn = "KSA", DefaultCurrency = "SAR", IsActive = true };
        var uae = new Company { TenantId = tid, LegalNameEn = "UAE", DefaultCurrency = "AED", IsActive = true };
        db.Companies.AddRange(ksa, uae);
        db.PayrollRuns.AddRange(
            new PayrollRun { TenantId = tid, CompanyId = ksa.Id, Year = 2026, Month = 9, Status = "Locked", TotalNetSalary = 100000 },
            new PayrollRun { TenantId = tid, CompanyId = uae.Id, Year = 2026, Month = 9, Status = "Locked", TotalNetSalary = 40000 });
        await db.SaveChangesAsync();

        var rows = Ok(await Analytics(db, tid, FullReader).PayrollTrend(6, default)).EnumerateArray().ToList();

        Assert.Equal(2, rows.Count);
        Assert.Equal(100000m, rows.Single(r => r.GetProperty("currencyCode").GetString() == "SAR").GetProperty("TotalNetSalary").GetDecimal());
    }

    [Theory]
    [MemberData(nameof(GatedSections))]
    public async Task Kpis_WithholdEachSection_FromACallerWithoutItsPermission_KeepingTheFieldsAsNull(
        string section, string[] fields, string permission)
    {
        await using var db = Db();
        var tid = await SeedEverySectionAsync(db);

        // Only the base employee reader: every gated section is withheld.
        var bare = Ok(await Analytics(db, tid, "reports.read", "employees.read").GetKPIs(default));
        foreach (var field in fields)
        {
            // Present (the shape is unchanged for the page), and null — not zero, which is a claim.
            Assert.True(bare.GetProperty(section).TryGetProperty(field, out var value), $"{section}.{field} must stay in the response");
            Assert.Equal(JsonValueKind.Null, value.ValueKind);
        }
        var withheld = bare.GetProperty("withheld").EnumerateArray()
            .Single(w => w.GetProperty("section").GetString() == section);
        Assert.Contains(permission, withheld.GetProperty("requiredPermissions").EnumerateArray().Select(p => p.GetString()));
        Assert.Equal(fields, withheld.GetProperty("fields").EnumerateArray().Select(f => f.GetString()));
        // Headcount is the endpoint's own permission, so it is always shown.
        Assert.Equal(JsonValueKind.Number, bare.GetProperty("headcount").GetProperty("totalActive").ValueKind);

        // With the permission, the section is shown and not listed as withheld.
        var granted = Ok(await Analytics(db, tid, "reports.read", "employees.read", permission).GetKPIs(default));
        Assert.NotEqual(JsonValueKind.Null, granted.GetProperty(section).GetProperty(fields[0]).ValueKind);
        Assert.DoesNotContain(granted.GetProperty("withheld").EnumerateArray(), w => w.GetProperty("section").GetString() == section);
    }

    [Fact]
    public async Task Kpis_WithEveryPermission_WithholdNothing()
    {
        await using var db = Db();
        var tid = await SeedEverySectionAsync(db);

        var json = Ok(await Analytics(db, tid, [.. FullReader, "compliance.read", "recruitment.read", "loans.read"]).GetKPIs(default));

        Assert.Empty(json.GetProperty("withheld").EnumerateArray());
        Assert.Equal(1, json.GetProperty("financial").GetProperty("activeLoans").GetInt32());
        Assert.Equal(1500m, json.GetProperty("financial").GetProperty("outstandingLoanBalance").GetDecimal());
        Assert.Equal(1, json.GetProperty("recruitment").GetProperty("openPositions").GetInt32());
    }

    /// <summary>One non-zero figure in every KPI section, so "withheld" can never be confused with "none".</summary>
    private static async Task<Guid> SeedEverySectionAsync(ZayraDbContext db)
    {
        var tid = await SeedTenantAsync(db);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var employee = Emp(tid, "E-ALL", "Active", DateTime.UtcNow.AddYears(-1));
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        db.LeaveRequests.Add(new LeaveRequest { TenantId = tid, EmployeeId = employee.Id, EmployeeName = "E-ALL", LeaveTypeName = "Annual", StartDate = today, EndDate = today, TotalDays = 1, Status = "Approved" });
        db.AttendanceDailyRecords.Add(Day(tid, employee.Id, today, AttendanceStatuses.Present));
        db.OvertimeRequests.Add(new OvertimeRequest { TenantId = tid, EmployeeId = employee.Id, EmployeeName = "E-ALL", WorkDate = today, RequestedMinutes = 60, Status = "Pending" });
        db.PayrollRuns.Add(new PayrollRun { TenantId = tid, Year = 2026, Month = 8, Status = "Locked", TotalNetSalary = 250000 });
        db.VisaRecords.Add(new VisaRecord { TenantId = tid, EmployeeId = employee.PublicId, EmployeeName = "E-ALL", VisaType = "Residence", VisaNumber = "V-1", ExpiryDate = today.AddDays(10), Status = "Active" });
        db.PassportRecords.Add(new PassportRecord { TenantId = tid, EmployeeId = employee.PublicId, EmployeeName = "E-ALL", PassportNumber = "P-1", ExpiryDate = today.AddDays(10), Status = "Active" });
        db.JobOpenings.Add(new JobOpening { TenantId = tid, JobCode = "JOB-1", Title = "Engineer", Status = "Open" });
        db.JobApplications.Add(new JobApplication { TenantId = tid, JobTitle = "Engineer", CandidateName = "Cand", Stage = "Screening", Status = "Active" });
        db.EmployeeLoans.Add(new EmployeeLoan { TenantId = tid, EmployeeName = "E-ALL", LoanNumber = "LN-1", Status = "Active", OutstandingBalance = 1500 });
        await db.SaveChangesAsync();
        return tid;
    }

    [Fact]
    public async Task LeaveTrend_IsServedToAnOrganisationReader_AsTheLeaveModuleIs()
    {
        // A Payroll Officer holds no leave.read, and the leave module's own lists admit them regardless
        // (they have no permission gate, only the employee scope), so the trend does too.
        await using var db = Db();
        var tid = await SeedTenantAsync(db);

        Assert.IsType<OkObjectResult>(
            await Analytics(db, tid, "reports.read", "employees.read", "payroll.read").LeaveTrend(6, default));
    }

    [Fact]
    public async Task Analytics_ForATeamScopedCaller_AreRefused_NotServedOrganisationWide()
    {
        await using var db = Db();
        var tid = await SeedTenantAsync(db);
        var manager = Analytics(db, tid, "reports.read", "employees.read", "manager.read", "attendance.read");

        ReportDataAuthorizationTests.AssertForbiddenWithReason(await manager.GetKPIs(default), "organisation");
        ReportDataAuthorizationTests.AssertForbiddenWithReason(await manager.AttendanceTrend(30, default), "organisation");
    }

    [Fact]
    public async Task Kpis_ForACompanyScopedCaller_CountOnlyTheirCompanysAttendance()
    {
        await using var db = Db();
        var tid = await SeedTenantAsync(db);
        var companyA = Guid.NewGuid();
        var companyB = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var alpha = Emp(tid, "A-1", "Active", DateTime.UtcNow.AddYears(-1));
        alpha.CompanyId = companyA;
        var beta = Emp(tid, "B-1", "Active", DateTime.UtcNow.AddYears(-1));
        beta.CompanyId = companyB;
        db.Employees.AddRange(alpha, beta);
        await db.SaveChangesAsync();
        // Attendance rows carry no company of their own; only the employee says whose they are.
        db.AttendanceDailyRecords.AddRange(
            Day(tid, alpha.Id, today, AttendanceStatuses.Present),
            Day(tid, beta.Id, today, AttendanceStatuses.Late, lateMinutes: 12));
        await db.SaveChangesAsync();

        var json = Ok(await Analytics(db, tid, companyA, FullReader).GetKPIs(default));

        Assert.Equal(1, json.GetProperty("attendance").GetProperty("presentToday").GetInt32());
        Assert.Equal(0, json.GetProperty("attendance").GetProperty("lateToday").GetInt32());
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────

    private static JsonElement Ok(IActionResult result) =>
        JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value);

    private static ZayraDbContext Db() => new(
        new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<Guid> SeedTenantAsync(ZayraDbContext db)
    {
        var tid = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tid, Name = "Analytics", Slug = $"analytics-{tid:N}" });
        db.TenantLocalizationSettings.Add(new TenantLocalizationSetting { TenantId = tid, DefaultTimezone = "UTC" });
        await db.SaveChangesAsync();
        return tid;
    }

    private static DateTime Utc(DateOnly date) => DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);

    private static Employee Emp(Guid tid, string code, string status, DateTime joined, DateOnly? contractEnd = null) => new()
    {
        TenantId = tid, EmployeeCode = code, FullName = code, Status = status, JoiningDate = joined, ContractEndDate = contractEnd,
    };

    private static AttendanceDailyRecord Day(Guid tid, int employeeId, DateOnly date, string status, int lateMinutes = 0) => new()
    {
        TenantId = tid, EmployeeId = employeeId, WorkDate = date, Status = status, LateMinutes = lateMinutes,
    };

    private static AnalyticsController Analytics(ZayraDbContext db, Guid tid, params string[] permissions) =>
        Analytics(db, tid, null, permissions);

    private static AnalyticsController Analytics(ZayraDbContext db, Guid tid, Guid? companyId, params string[] permissions) =>
        Build(db, tid, companyId, null, permissions);

    private static AnalyticsController AnalyticsAs(ZayraDbContext db, Guid tid, string role, params string[] permissions) =>
        Build(db, tid, null, role, permissions);

    private static AnalyticsController Build(ZayraDbContext db, Guid tid, Guid? companyId, string? role, string[] permissions)
    {
        var claims = new List<Claim> { new("tenant_id", tid.ToString()) };
        if (role is not null) claims.Add(new Claim(ClaimTypes.Role, role));
        if (companyId is Guid company)
            claims.Add(new Claim(EntityScopeContext.V2ClaimType,
                JsonSerializer.Serialize(new { v = 2, m = "companies", c = new[] { company } })));
        claims.AddRange(permissions.Select(p => new Claim("permission", p)));
        return new AnalyticsController(db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) },
            },
        };
    }
}
