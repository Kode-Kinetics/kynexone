using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Attendance;
using Zayra.Api.Infrastructure.Compliance;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Verifies that every IgnoreQueryFilters() usage that was flagged in the P1 security
/// audit re-applies explicit tenant isolation. Each test authenticates as Tenant A,
/// seeds one or more Tenant B rows, and asserts zero Tenant B rows appear in the result.
///
/// The DbContext in these tests is created WITHOUT an IHttpContextAccessor so the EF
/// global query filter is inactive — isolating the test to the explicit WHERE predicates
/// in each query. This is the worst-case scenario (no defence-in-depth filter) and
/// proves the first line of isolation is correct.
/// </summary>
public class CrossTenantQueryFilterTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────────

    /// <summary>Creates a no-accessor DbContext (global filter inactive, all tenants visible).</summary>
    private static ZayraDbContext CreateDb()
    {
        var opts = new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ZayraDbContext(opts);
    }

    /// <summary>Wires a GosiController with TenantA's JWT claims on the controller context.</summary>
    private static GosiController GosiControllerFor(ZayraDbContext db, Guid tenantId)
    {
        var controller = new GosiController(db, TestReconciliation.For(db));
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Role, "Admin"),
            new Claim("permission", "payroll.manage"),
        }, "Test"));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } };
        return controller;
    }

    private static string HashDeviceKey(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    // ── Seed helpers ─────────────────────────────────────────────────────────────

    private static GosiContributionRule GosiRule(Guid tenantId, string classification = "Saudi") =>
        new()
        {
            Id             = Guid.NewGuid(),
            TenantId       = tenantId,
            Classification = classification,
            Branch         = "Annuities",
            Payer          = "Employee",
            Rate           = 9.75m,
            EffectiveFrom  = new DateOnly(2016, 6, 1),
            IsActive       = true,
        };

    private static Employee ActiveEmployee(Guid tenantId, string code) =>
        new()
        {
            TenantId    = tenantId,
            EmployeeCode = code,
            FullName    = $"Employee {code}",
            Department  = "HR",
            Designation = "Officer",
            Status      = "Active",
            JoiningDate = DateTime.UtcNow.AddYears(-1).Date,
            Salary      = 10_000m,
            Nationality = "Saudi",
        };

    // ── GosiController: GetContributionRules (:31) ───────────────────────────────

    [Fact]
    public async Task GetContributionRules_AuthenticatedAsTenantA_NeverReturnsTenantBRules()
    {
        await using var db = CreateDb();

        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        // System default (Guid.Empty) + TenantA override + TenantB override
        db.GosiContributionRules.AddRange(
            GosiRule(Guid.Empty),                       // platform default — should appear
            GosiRule(tenantA),                          // own tenant — should appear
            GosiRule(tenantB));                         // other tenant — must NOT appear
        await db.SaveChangesAsync();

        var controller = GosiControllerFor(db, tenantA);
        var result = await controller.GetContributionRules(CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var rows = ok.Value as IEnumerable<object>;
        rows.Should().NotBeNull();

        // Cast to dynamic to inspect TenantId
        var tenantIds = rows!.Select(r =>
        {
            var prop = r.GetType().GetProperty("TenantId");
            return (Guid?)prop?.GetValue(r);
        }).ToList();

        tenantIds.Should().NotContain(tenantB,
            "TenantB's rule must never appear in TenantA's contribution-rule list");
        tenantIds.Should().Contain(Guid.Empty,
            "platform default rules (TenantId==Guid.Empty) must appear");
        tenantIds.Should().Contain(tenantA,
            "own tenant's overrides must appear");
    }

    // ── GosiController: DeactivateContributionRule (:108) ────────────────────────

    [Fact]
    public async Task DeactivateContributionRule_TargetingTenantBRule_Returns404()
    {
        await using var db = CreateDb();

        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        var tenantBRule = GosiRule(tenantB);
        db.GosiContributionRules.Add(tenantBRule);
        await db.SaveChangesAsync();

        // TenantA tries to deactivate TenantB's rule — must be 404
        var controller = GosiControllerFor(db, tenantA);
        var result = await controller.DeactivateContributionRule(tenantBRule.Id, CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>(
            "a tenant-A user must not be able to deactivate rules owned by tenant B");

        // Confirm the TenantB rule was NOT modified
        var rule = await db.GosiContributionRules.FindAsync(tenantBRule.Id);
        rule!.IsActive.Should().BeTrue("the rule must remain active — TenantA cannot touch it");
    }

    [Fact]
    public async Task DeactivateContributionRule_OwnRule_Succeeds()
    {
        await using var db = CreateDb();

        var tenantA = Guid.NewGuid();
        var ownRule = GosiRule(tenantA);
        db.GosiContributionRules.Add(ownRule);
        await db.SaveChangesAsync();

        var controller = GosiControllerFor(db, tenantA);
        var result = await controller.DeactivateContributionRule(ownRule.Id, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>(
            "a tenant can deactivate its own override rules");
        var updated = await db.GosiContributionRules.FindAsync(ownRule.Id);
        updated!.IsActive.Should().BeFalse();
    }

    // ── GosiReadinessReportService.BuildAsync (:35) ──────────────────────────────

    [Fact]
    public async Task GosiReadinessReport_BuildAsync_NeverIncludesTenantBEmployees()
    {
        await using var db = CreateDb();

        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        // Seed 2 TenantA employees and 3 TenantB employees
        db.Employees.AddRange(
            ActiveEmployee(tenantA, "A-001"),
            ActiveEmployee(tenantA, "A-002"),
            ActiveEmployee(tenantB, "B-001"),
            ActiveEmployee(tenantB, "B-002"),
            ActiveEmployee(tenantB, "B-003"));
        // A system default GOSI rule so the engine doesn't error
        db.GosiContributionRules.Add(GosiRule(Guid.Empty));
        await db.SaveChangesAsync();

        var svc    = new GosiReadinessReportService(db);
        var report = await svc.BuildAsync(tenantA, CancellationToken.None);

        report.TotalEmployees.Should().Be(2,
            "BuildAsync(tenantAId) must see exactly TenantA's 2 employees, not TenantB's 3");
        report.Employees.Select(e => e.EmployeeCode).Should().NotContain("B-001")
            .And.NotContain("B-002").And.NotContain("B-003");
    }

    // ── SaudiComplianceDashboardService (GOSI section, :149) ────────────────────

    [Fact]
    public async Task SaudiComplianceDashboard_GosiSection_NeverIncludesTenantBEmployees()
    {
        await using var db = CreateDb();

        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        db.Employees.AddRange(
            ActiveEmployee(tenantA, "A-001"),
            ActiveEmployee(tenantB, "B-001"),
            ActiveEmployee(tenantB, "B-002"));
        db.GosiContributionRules.Add(GosiRule(Guid.Empty));
        await db.SaveChangesAsync();

        var svc       = new SaudiComplianceDashboardService(db, TestReconciliation.For(db));
        var dashboard = await svc.BuildAsync(tenantA, CancellationToken.None);

        // TenantA has 1 employee; TenantB has 2. The GOSI section must reflect only TenantA.
        var gosiTotal = dashboard.Gosi.ReadyCount + dashboard.Gosi.BlockedCount;
        gosiTotal.Should().Be(1,
            "GOSI dashboard must count only TenantA's employees, not TenantB's");
    }

    // ── AttendanceService: IngestByDeviceKeyAsync (:321 device lookup, :338 dup-check) ──

    [Fact]
    public async Task DeviceIngest_RawEventsCreated_AlwaysTaggedWithDeviceTenant()
    {
        await using var db = CreateDb();

        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        const string deviceKeyPlaintext = "test_device_key_for_tenant_a";
        var keyHash = HashDeviceKey(deviceKeyPlaintext);

        // Seed a device belonging to TenantA
        var device = new AttendanceDevice
        {
            TenantId         = tenantA,
            DeviceName       = "Gate A",
            DeviceType       = "Biometric",
            Vendor           = "Test",
            SerialNumber     = "SN-A-001",
            LocationName     = "Main Entrance",
            ApiKeyReference  = keyHash,
            IsActive         = true,
        };
        db.AttendanceDevices.Add(device);

        // Seed a TenantB employee whose code could be used by an attacker
        db.Employees.Add(ActiveEmployee(tenantB, "B-EMP-001"));

        await db.SaveChangesAsync();

        var fakeNotifications = new NullNotificationService();
        var fakeHttpFactory   = new NullHttpClientFactory();
        var svc = new AttendanceService(db, fakeNotifications, fakeHttpFactory);

        // AutoProcess=false avoids the attendance-policy query chain; we only need the raw event.
        var ingestRequest = new DeviceIngestRequest(
            Punches: new[]
            {
                // Use TenantB's employee code in the punch. The service must create a raw event
                // scoped to TenantA (the device's tenant), not TenantB, and mark it "unmatched"
                // (no TenantA employee has code B-EMP-001).
                new DeviceIngestPunch("B-EMP-001", DateTime.UtcNow, "IN", null, null, null, null, null, null)
            },
            AutoProcess: false);

        var result = await svc.IngestByDeviceKeyAsync(deviceKeyPlaintext, ingestRequest, "127.0.0.1", CancellationToken.None);

        result.Should().NotBeNull();
        result!.Accepted.Should().Be(1);
        result.Unmatched.Should().Be(1, "no TenantA employee has code B-EMP-001 — correct isolation");

        // Most important assertion: the raw event must carry TenantA's id, not TenantB's
        var rawEvents = await db.AttendanceRawEvents.IgnoreQueryFilters().ToListAsync();
        rawEvents.Should().HaveCount(1);
        rawEvents[0].TenantId.Should().Be(tenantA,
            "raw events must always be scoped to the authenticating device's tenant, never the looked-up employee's tenant");
        rawEvents[0].TenantId.Should().NotBe(tenantB);
    }

    // ── Null stubs for AttendanceService dependencies ────────────────────────────

    private sealed class NullNotificationService : INotificationService
    {
        public Task NotifyAsync(Guid t, Guid? u, string title, string msg, string entity, string? entityId, CancellationToken ct) => Task.CompletedTask;
        public Task SendEmailAsync(Guid t, string code, string to, string name, Dictionary<string, string> vars, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NullHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
