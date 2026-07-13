using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Controllers.Reports;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Operations;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

public class SyntheticWorkforcePlanningSimulationTests
{
    [Fact]
    public async Task PlanningAndReportingSimulation_ProducesGovernedTelemetry()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var user = Principal(tenantId, "organization.read", "organization.write", "reports.read", "employees.read");
        var department = new Department
        {
            TenantId = tenantId,
            Code = "OPS",
            NameEn = "Operations",
            ApprovedHeadcount = 2,
            MonthlyBudgetAmount = 10_000m,
            IsActive = true
        };
        db.Departments.Add(department);
        db.Employees.Add(new Employee
        {
            TenantId = tenantId,
            DepartmentId = department.Id,
            Department = department.NameEn,
            EmployeeCode = "OPS-001",
            FullName = "Operations One",
            Status = "Active",
            Salary = 4_500m,
            JoiningDate = DateTime.UtcNow.AddMonths(-3)
        });
        db.ManpowerRequisitions.Add(new ManpowerRequisition
        {
            TenantId = tenantId,
            DepartmentId = department.Id,
            DepartmentName = department.NameEn,
            RequisitionNumber = "MRQ-SYN-001",
            HeadCount = 1,
            Status = "Approved"
        });
        await db.SaveChangesAsync();

        var planning = new PlanningController(db)
        {
            ControllerContext = Context(user)
        };

        var check = await planning.HeadcountCheck(department.Id, null, 1, CancellationToken.None);
        var checkJson = JsonSerializer.Serialize(((OkObjectResult)check).Value);
        checkJson.Should().Contain("\"WithinBudget\":false");
        checkJson.Should().Contain("would exceed");

        var summary = await planning.WorkforceSummary(CancellationToken.None);
        var summaryJson = JsonSerializer.Serialize(((OkObjectResult)summary).Value);
        summaryJson.Should().Contain("\"totalApprovedHeadcount\":2");
        summaryJson.Should().Contain("\"totalProjectedHeadcount\":2");

        var reports = new ReportsController(db, new DataScopeService(db))
        {
            ControllerContext = Context(user)
        };
        var report = await reports.RunReport(new RunReportRequest("hr.headcount", new ReportFilters { Department = department.NameEn }), CancellationToken.None);
        var reportJson = JsonSerializer.Serialize(((OkObjectResult)report).Value);
        reportJson.Should().Contain("Operations");
        reportJson.Should().Contain("\"rowCount\":1");

        var log = await db.ReportExecutionLogs.SingleAsync();
        log.TenantId.Should().Be(tenantId);
        log.ReportKey.Should().Be("hr.headcount");
        log.Status.Should().Be("Success");
        log.RowCount.Should().Be(1);
        log.DurationMs.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task ReportsRun_WithoutReportsReadPermission_IsForbiddenAndDoesNotLog()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var reports = new ReportsController(db, new DataScopeService(db))
        {
            ControllerContext = Context(Principal(tenantId, "organization.read"))
        };

        var result = await reports.RunReport(new RunReportRequest("hr.headcount", null), CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();
        (await db.ReportExecutionLogs.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ProductionTelemetryEvidence_SummarizesDependenciesReportingAndPlanning()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        db.SystemSettings.Add(new SystemSetting
        {
            TenantId = tenantId,
            Category = "Email",
            SettingKey = "Smtp.Host",
            SettingValue = "smtp.enterprise.test"
        });
        db.ReportExecutionLogs.AddRange(
            new ReportExecutionLog { TenantId = tenantId, ReportKey = "hr.headcount", Status = "Success", DurationMs = 120, CreatedAtUtc = DateTime.UtcNow.AddMinutes(-15) },
            new ReportExecutionLog { TenantId = tenantId, ReportKey = "payroll.summary", Status = "Failed", DurationMs = 640, CreatedAtUtc = DateTime.UtcNow.AddMinutes(-10), ErrorMessage = "synthetic failure" },
            new ReportExecutionLog { TenantId = tenantId, ReportKey = "attendance.daily", Status = "Success", DurationMs = 220, CreatedAtUtc = DateTime.UtcNow.AddHours(-30) });
        db.ReportSchedules.AddRange(
            new ReportSchedule { TenantId = tenantId, ReportKey = "hr.headcount", ReportName = "Headcount", IsActive = true, NextRunAtUtc = DateTime.UtcNow.AddMinutes(-5) },
            new ReportSchedule { TenantId = tenantId, ReportKey = "payroll.summary", ReportName = "Payroll", IsActive = true, NextRunAtUtc = DateTime.UtcNow.AddDays(1) });
        db.Positions.AddRange(
            new Position { TenantId = tenantId, Code = "OPS-001", Title = "Operations Lead", Status = PositionStatuses.Open, BudgetedMonthlyCost = 12_000m },
            new Position { TenantId = tenantId, Code = "OPS-002", Title = "Operations Analyst", Status = PositionStatuses.Frozen, BudgetedMonthlyCost = 8_000m },
            new Position { TenantId = tenantId, Code = "OPS-003", Title = "Closed Role", Status = PositionStatuses.Closed, BudgetedMonthlyCost = 5_000m });
        db.ManpowerRequisitions.AddRange(
            new ManpowerRequisition { TenantId = tenantId, RequisitionNumber = "MRQ-001", HeadCount = 2, Status = "Approved" },
            new ManpowerRequisition { TenantId = tenantId, RequisitionNumber = "MRQ-002", HeadCount = 1, Status = "Submitted" },
            new ManpowerRequisition { TenantId = tenantId, RequisitionNumber = "MRQ-003", HeadCount = 4, Status = "Rejected" });
        db.AuditLogs.Add(new Zayra.Api.Domain.Entities.AuditLog
        {
            TenantId = tenantId,
            Action = "governance.controlled_override.report_schedule_created",
            EntityName = "ReportSchedule",
            EntityId = Guid.NewGuid().ToString(),
            CreatedAtUtc = DateTime.UtcNow.AddMinutes(-3)
        });
        await db.SaveChangesAsync();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["REDIS_URL"] = "localhost:6379",
                ["QIWA_USE_LIVE_ADAPTER"] = "true"
            })
            .Build();

        var telemetry = await ProductionReadinessEvidence.BuildTelemetryAsync(db, config, CancellationToken.None);

        telemetry.Status.Should().Be("ok");
        telemetry.Dependencies.Redis.Should().Be(new DependencyMode("configured", true));
        telemetry.Dependencies.Qiwa.Should().Be(new DependencyMode("live_adapter", true));
        telemetry.Dependencies.Smtp.Should().Be(new DependencyMode("configured", true));
        telemetry.Governance.ControlledOverrides24h.Should().Be(1);
        telemetry.Governance.LatestControlledOverrideAtUtc.Should().NotBeNull();
        telemetry.Reporting.ReportRuns24h.Should().Be(2);
        telemetry.Reporting.FailedReportRuns24h.Should().Be(1);
        telemetry.Reporting.FailureRatePercent24h.Should().Be(50);
        telemetry.Reporting.P95DurationMs24h.Should().Be(640);
        telemetry.Reporting.ActiveSchedules.Should().Be(2);
        telemetry.Reporting.StaleSchedules.Should().Be(1);
        telemetry.WorkforcePlanning.ActivePositions.Should().Be(2);
        telemetry.WorkforcePlanning.FrozenPositions.Should().Be(1);
        telemetry.WorkforcePlanning.OpenRequisitionHeadcount.Should().Be(3);
        telemetry.WorkforcePlanning.ApprovedRequisitionHeadcount.Should().Be(2);
        telemetry.WorkforcePlanning.BudgetedMonthlyCost.Should().Be(20_000m);
    }

    [Fact]
    public async Task ReportGovernanceMutations_RequireControlledOverrideAndWriteAuditEvidence()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var principal = Principal(tenantId, "reports.read", "reports.schedule");
        var reports = new ReportsController(db, new DataScopeService(db))
        {
            ControllerContext = Context(principal)
        };

        var blocked = await reports.CreateSchedule(
            new CreateScheduleRequest("payroll.summary", "Payroll Summary", "Payroll", null, "Monthly", "Email", "finance@example.test", "CSV"),
            CancellationToken.None);

        blocked.Should().BeOfType<ConflictObjectResult>();
        (await db.ReportSchedules.CountAsync()).Should().Be(0);
        (await db.AuditLogs.CountAsync()).Should().Be(0);

        var governance = new GovernanceOverrideRequest(
            "GRC-2026-0007",
            "Certification evidence schedule approved by operations lead.",
            true);
        var created = await reports.CreateSchedule(
            new CreateScheduleRequest("payroll.summary", "Payroll Summary", "Payroll", null, "Monthly", "Email", "finance@example.test", "CSV", governance),
            CancellationToken.None);

        created.Should().BeOfType<OkObjectResult>();
        var schedule = await db.ReportSchedules.SingleAsync();
        schedule.ReportKey.Should().Be("payroll.summary");

        var audit = await db.AuditLogs.SingleAsync();
        audit.Action.Should().Be("governance.controlled_override.report_schedule_created");
        audit.TenantId.Should().Be(tenantId);
        audit.UserId.Should().NotBeNull();
        audit.Metadata.Should().Contain("GRC-2026-0007");
        audit.EntryHash.Should().NotBeNullOrWhiteSpace();
    }

    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static ControllerContext Context(ClaimsPrincipal user) =>
        new() { HttpContext = new DefaultHttpContext { User = user } };

    private static ClaimsPrincipal Principal(Guid tenantId, params string[] permissions)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Name, "Synthetic Planner"),
            new(ClaimTypes.Role, "Admin"),
            new("is_group_scope", "true")
        };
        claims.AddRange(permissions.Select(p => new Claim("permission", p)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }
}
