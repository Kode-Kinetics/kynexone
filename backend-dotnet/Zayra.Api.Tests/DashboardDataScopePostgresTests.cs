using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// F10 against REAL PostgreSQL with the REAL <see cref="DataScopeService"/>: a manager (manager.read,
/// resolved to their reporting tree from the employee_id claim), a plain employee and HR share one
/// dashboard cache. Proves the scope reaches every slice and that the scoped queries translate — the id
/// lists (<c>= ANY</c>), the leave-activity match on the leave request's id as text, and the per-run
/// grouping of a scoped caller's slips.
/// </summary>
[Collection("Integration")]
[Trait("Category", "Integration")]
public sealed class DashboardDataScopePostgresTests
{
    private readonly PostgresFixture _fx;
    public DashboardDataScopePostgresTests(PostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task ManagerAndEmployee_SeeTheirOwnPopulation_HrSeesTheOrganization()
    {
        Guid tenantId;
        int managerId, employeeId;
        int[] team;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await using (var seed = _fx.CreateDb())
        {
            tenantId = await PostgresFixture.SeedMinimalTenant(seed);
            Employee Emp(string code, string department) => new()
            {
                TenantId = tenantId, EmployeeCode = $"{code}-{Guid.NewGuid():N}"[..14], FullName = code, Status = "Active",
                Department = department, EmploymentType = "Full Time", Nationality = "Saudi",
                JoiningDate = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            };
            var manager = Emp("MGR", "Engineering");
            seed.Employees.Add(manager);
            await seed.SaveChangesAsync();
            var reports = Enumerable.Range(1, 3).Select(i => Emp($"TEAM{i}", "Engineering")).ToArray();
            foreach (var r in reports) r.ManagerEmployeeId = manager.Id;
            var others = Enumerable.Range(1, 4).Select(i => Emp($"OTHER{i}", "Sales")).ToArray();
            seed.Employees.AddRange(reports);
            seed.Employees.AddRange(others);
            await seed.SaveChangesAsync();
            managerId = manager.Id;
            employeeId = others[0].Id;
            team = reports.Select(r => r.Id).ToArray();

            var run = new PayrollRun
            {
                TenantId = tenantId, Year = today.Year, Month = today.Month, Status = "Locked",
                TotalGrossSalary = 9_600m, TotalNetSalary = 8_000m, TotalDeductions = 1_600m, EmployeeCount = 8,
            };
            seed.PayrollRuns.Add(run);
            foreach (var e in new[] { manager }.Concat(reports).Concat(others))
            {
                var leave = new LeaveRequest
                {
                    TenantId = tenantId, EmployeeId = e.Id, Status = "Submitted", LeaveTypeName = "Annual",
                    StartDate = today.AddDays(3), EndDate = today.AddDays(4),
                };
                seed.LeaveRequests.Add(leave);
                seed.ApprovalRequests.Add(new ApprovalRequest
                {
                    TenantId = tenantId, Status = "Pending", EntityName = nameof(LeaveRequest),
                    EntityId = leave.Id.ToString(), Title = $"Leave {e.FullName}", RequestedForEmployeeId = e.Id,
                });
                seed.LeaveAuditLogs.Add(new LeaveAuditLog
                {
                    TenantId = tenantId, EntityType = nameof(LeaveRequest), EntityId = leave.Id.ToString(),
                    Action = $"Submitted {e.FullName}", PerformedByName = e.FullName,
                });
                seed.EmployeeComplianceRecords.Add(new EmployeeComplianceRecord
                {
                    TenantId = tenantId, EmployeeId = e.Id, FieldKey = "iqama_number", FieldLabel = "Iqama",
                    ExpiryDate = today.AddDays(20),
                });
                seed.PayrollSlips.Add(new PayrollSlip
                {
                    TenantId = tenantId, RunId = run.Id, EmployeeId = e.Id, EmployeeCode = e.EmployeeCode,
                    EmployeeName = e.FullName, Department = e.Department ?? "",
                    GrossSalary = 1_200m, Deductions = 200m, NetSalary = 1_000m,
                });
            }
            seed.PayrollAuditLogs.Add(new PayrollAuditLog
            {
                TenantId = tenantId, Action = "payroll.processed", EntityName = "PayrollRun", EntityId = run.Id.ToString(),
            });
            await seed.SaveChangesAsync();
        }

        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        async Task<DashboardFullDto> FullAs(int? callerEmployeeId, params string[] permissions)
        {
            await using var db = _fx.CreateDb();
            var claims = new List<Claim>
            {
                new("tenant_id", tenantId.ToString()),
                new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            };
            if (callerEmployeeId is int id) claims.Add(new Claim("employee_id", id.ToString()));
            claims.AddRange(permissions.Select(p => new Claim("permission", p)));
            var ctrl = new DashboardController(db, cache, new DataScopeService(db))
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) },
                },
            };
            return Assert.IsType<DashboardFullDto>(Assert.IsType<OkObjectResult>(await ctrl.Full(6)).Value);
        }

        var hr = await FullAs(null, "employees.write", "payroll.read", "dashboard.read");
        var mgr = await FullAs(managerId, "employees.read", "manager.read", "dashboard.read");
        var managerWithPayroll = await FullAs(managerId, "employees.read", "manager.read", "payroll.read");
        var employee = await FullAs(employeeId, "dashboard.read", "ess.read");

        hr.Summary.TotalEmployees.Should().Be(8);
        hr.Overview.PendingApprovals.Should().Be(8);
        hr.Overview.PayrollSummary!.TotalNet.Should().Be(8_000m);

        mgr.Summary.TotalEmployees.Should().Be(3, "the reporting tree the real scope service resolves, without the manager");
        mgr.Overview.PendingApprovals.Should().Be(3);
        mgr.Overview.ApprovalQueue.Should().OnlyContain(a => a.EmployeeId != null && team.Contains(a.EmployeeId.Value));
        mgr.Overview.ComplianceAlertsTotal.Should().Be(3);
        mgr.Overview.OpenLeaveRequests.Should().Be(3);
        mgr.ActivityFeed.Should().HaveCount(3).And.OnlyContain(a => a.Module == "Leave" && a.Action.Contains("TEAM"));
        mgr.Overview.PayrollSummary.Should().BeNull();
        mgr.PayrollTrends.Should().BeEmpty();

        managerWithPayroll.Overview.PayrollSummary!.TotalNet.Should().Be(3_000m);
        managerWithPayroll.Overview.PayrollSummary.EmployeeCount.Should().Be(3);
        managerWithPayroll.PayrollTrends.Should().Contain(p => p.TotalNet == 3_000m && p.EmployeeCount == 3);

        employee.Summary.TotalEmployees.Should().Be(1);
        employee.Overview.PendingApprovals.Should().Be(1);
        employee.Overview.ApprovalQueue.Should().ContainSingle().Which.EmployeeId.Should().Be(employeeId);
        employee.ActivityFeed.Should().ContainSingle();
        employee.Overview.PayrollSummary.Should().BeNull();
    }
}
