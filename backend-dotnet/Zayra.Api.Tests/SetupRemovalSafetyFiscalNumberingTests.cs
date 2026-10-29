using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers.Admin;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Removal rules for fiscal years and numbering rules: unused rows are deleted, rows with
/// history are refused with a plain reason, closed years stay closed, and every change is audited.
/// </summary>
public class SetupRemovalSafetyFiscalNumberingTests
{
    private static ZayraDbContext Db() => new(new DbContextOptionsBuilder<ZayraDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static SetupSettingsController Controller(ZayraDbContext db, Guid tenant, bool group = true, Guid? user = null)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenant.ToString()),
            new(ClaimTypes.NameIdentifier, (user ?? Guid.NewGuid()).ToString()),
            new(ClaimTypes.Role, "Admin"),
            new("permission", "security.manage"),
        };
        if (group) claims.Add(new Claim("is_group_scope", "true"));
        else claims.Add(new Claim("entity_access", System.Text.Json.JsonSerializer.Serialize(new { c = Guid.NewGuid(), r = "Admin" })));
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) };
        return new SetupSettingsController(db, new NoAudit()) { ControllerContext = new ControllerContext { HttpContext = http } };
    }

    private sealed class NoAudit : Zayra.Api.Application.Auth.IAuditService
    {
        public Task WriteAsync(string action, string entityName, string? entityId, Zayra.Api.Application.Auth.RequestContext context, string? metadata, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static FiscalYear Fy(Guid tenant, int year, string status = "Open") => new()
    {
        TenantId = tenant, Code = $"FY{year}", Year = year, Status = status,
        StartDate = new DateOnly(year, 1, 1), EndDate = new DateOnly(year, 12, 31),
    };

    private static string Code(IActionResult r) =>
        r.Should().BeOfType<ConflictObjectResult>().Subject.Value!.GetType().GetProperty("code")!.GetValue(
            ((ConflictObjectResult)r).Value)!.ToString()!;

    [Fact]
    public async Task Delete_ClosedFiscalYear_IsRefusedAndKept()
    {
        await using var db = Db(); var t = Guid.NewGuid();
        var fy = Fy(t, 2024, "Closed"); db.FiscalYears.Add(fy); await db.SaveChangesAsync();

        var r = await Controller(db, t).DeleteFiscalYear(fy.Id, default);

        Code(r).Should().Be("FISCAL_YEAR_CLOSED");
        (await db.FiscalYears.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Delete_YearWithGlPeriodInWindow_IsRefused()
    {
        await using var db = Db(); var t = Guid.NewGuid();
        var fy = Fy(t, 2025); db.FiscalYears.Add(fy);
        db.GlPeriodCloses.Add(new GlPeriodClose { TenantId = t, Period = "2025-03" });
        await db.SaveChangesAsync();

        var r = await Controller(db, t).DeleteFiscalYear(fy.Id, default);

        Code(r).Should().Be("FISCAL_YEAR_IN_USE");
        (await db.FiscalYears.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Delete_YearWithPayrollRunInWindow_IsRefused()
    {
        await using var db = Db(); var t = Guid.NewGuid();
        var fy = Fy(t, 2025); db.FiscalYears.Add(fy);
        db.PayrollRuns.Add(new PayrollRun { TenantId = t, Year = 2025, Month = 6 });
        await db.SaveChangesAsync();

        var r = await Controller(db, t).DeleteFiscalYear(fy.Id, default);

        Code(r).Should().Be("FISCAL_YEAR_IN_USE");
    }

    [Fact]
    public async Task Delete_UnusedOpenYear_Deletes_AndAudits()
    {
        await using var db = Db(); var t = Guid.NewGuid();
        var fy = Fy(t, 2031); db.FiscalYears.Add(fy); await db.SaveChangesAsync();

        var r = await Controller(db, t).DeleteFiscalYear(fy.Id, default);

        r.Should().BeOfType<NoContentResult>();
        (await db.FiscalYears.CountAsync()).Should().Be(0);
        var audit = await db.AdminAuditLogs.SingleAsync();
        audit.EntityType.Should().Be("FiscalYear"); audit.Action.Should().Be("Deleted");
    }

    [Fact]
    public async Task Delete_OtherTenantsYear_IsNotFound()
    {
        await using var db = Db(); var t = Guid.NewGuid();
        var fy = Fy(Guid.NewGuid(), 2031); db.FiscalYears.Add(fy); await db.SaveChangesAsync();

        (await Controller(db, t).DeleteFiscalYear(fy.Id, default)).Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Delete_WithoutGroupScope_IsForbidden()
    {
        await using var db = Db(); var t = Guid.NewGuid();
        var fy = Fy(t, 2031); db.FiscalYears.Add(fy); await db.SaveChangesAsync();

        (await Controller(db, t, group: false).DeleteFiscalYear(fy.Id, default)).Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task Close_AlreadyClosed_IsRefused_AndKeepsOriginalCloser()
    {
        await using var db = Db(); var t = Guid.NewGuid();
        var first = Guid.NewGuid(); var firstAt = new DateTime(2025, 1, 5, 0, 0, 0, DateTimeKind.Utc);
        var fy = Fy(t, 2024, "Closed"); fy.ClosedBy = first; fy.ClosedAtUtc = firstAt;
        db.FiscalYears.Add(fy); await db.SaveChangesAsync();

        var r = await Controller(db, t).CloseFiscalYear(fy.Id, default);

        Code(r).Should().Be("FISCAL_YEAR_ALREADY_CLOSED");
        var reloaded = await db.FiscalYears.SingleAsync();
        reloaded.ClosedBy.Should().Be(first); reloaded.ClosedAtUtc.Should().Be(firstAt);
        (await db.AdminAuditLogs.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Close_OpenYear_Closes_AndAudits()
    {
        await using var db = Db(); var t = Guid.NewGuid();
        var fy = Fy(t, 2024); db.FiscalYears.Add(fy); await db.SaveChangesAsync();

        (await Controller(db, t).CloseFiscalYear(fy.Id, default)).Should().BeOfType<OkObjectResult>();

        (await db.FiscalYears.SingleAsync()).Status.Should().Be("Closed");
        (await db.AdminAuditLogs.SingleAsync()).Action.Should().Be("Closed");
    }

    [Fact]
    public async Task Create_Audits()
    {
        await using var db = Db(); var t = Guid.NewGuid();
        var r = await Controller(db, t).CreateFiscalYear(new FiscalYearRequest(2040, new DateOnly(2040, 1, 1), new DateOnly(2040, 12, 31)), default);
        r.Should().BeOfType<OkObjectResult>();
        (await db.AdminAuditLogs.SingleAsync()).Action.Should().Be("Created");
    }

    [Fact]
    public async Task List_ComputesCurrentFromDates_AndExplainsBlockedActions()
    {
        await using var db = Db(); var t = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var cur = Fy(t, today.Year); var closed = Fy(t, today.Year - 3, "Closed");
        db.FiscalYears.AddRange(cur, closed);
        db.PayrollRuns.Add(new PayrollRun { TenantId = t, Year = today.Year, Month = 1 });
        await db.SaveChangesAsync();

        var ok = (OkObjectResult)await Controller(db, t).ListFiscalYears(default);
        var rows = ((System.Collections.IEnumerable)ok.Value!).Cast<object>().ToList();
        string Prop(object o, string n) => o.GetType().GetProperty(n)!.GetValue(o)?.ToString() ?? "";

        var curRow = rows.Single(x => Prop(x, "Year") == today.Year.ToString());
        Prop(curRow, "isCurrent").Should().Be("True");
        Prop(curRow, "canDelete").Should().Be("False");
        Prop(curRow, "deleteBlockedReason").Should().NotBeEmpty();
        var closedRow = rows.Single(x => Prop(x, "Year") == (today.Year - 3).ToString());
        Prop(closedRow, "isCurrent").Should().Be("False");
        Prop(closedRow, "canClose").Should().Be("False");
        Prop(closedRow, "deleteBlockedReason").Should().Contain("closed");
    }

    [Fact]
    public async Task List_ForCallerWithoutManageRights_GetsOneGenericReason_NotUsageDetail()
    {
        await using var db = Db(); var t = Guid.NewGuid();
        db.FiscalYears.Add(Fy(t, 2025));
        db.PayrollRuns.Add(new PayrollRun { TenantId = t, Year = 2025, Month = 6 });
        await db.SaveChangesAsync();

        var ok = (OkObjectResult)await Controller(db, t, group: false).ListFiscalYears(default);
        var row = ((System.Collections.IEnumerable)ok.Value!).Cast<object>().Single();
        var reason = row.GetType().GetProperty("deleteBlockedReason")!.GetValue(row)!.ToString()!;
        reason.Should().NotContain("Payroll").And.NotContain("Accounting");
    }

    // ── Numbering rules ──────────────────────────────────────────────────────

    private static NumberingRule Rule(Guid t, string type = "Employee", int seq = 0) =>
        new() { TenantId = t, EntityType = type, Prefix = "EMP", CurrentSequence = seq };

    [Fact]
    public async Task DeleteNumberingRule_IsAllowed_BecauseNothingConsumesRules_AndIsAudited()
    {
        // No issuing code reads or advances numbering rules, so there is no issued-number history to protect.
        await using var db = Db(); var t = Guid.NewGuid();
        var rule = Rule(t, seq: 42); db.NumberingRules.Add(rule); await db.SaveChangesAsync();

        (await Controller(db, t).DeleteNumberingRule(rule.Id, default)).Should().BeOfType<NoContentResult>();

        (await db.NumberingRules.CountAsync()).Should().Be(0);
        (await db.AdminAuditLogs.SingleAsync()).Action.Should().Be("Deleted");
    }

    [Fact]
    public async Task DeleteNumberingRule_Unused_Deletes_AndAudits()
    {
        await using var db = Db(); var t = Guid.NewGuid();
        var rule = Rule(t); db.NumberingRules.Add(rule); await db.SaveChangesAsync();

        (await Controller(db, t).DeleteNumberingRule(rule.Id, default)).Should().BeOfType<NoContentResult>();

        (await db.NumberingRules.CountAsync()).Should().Be(0);
        (await db.AdminAuditLogs.SingleAsync()).Action.Should().Be("Deleted");
    }

    [Fact]
    public async Task SetNumberingRuleActive_TurnsOffAndOn_Audited()
    {
        await using var db = Db(); var t = Guid.NewGuid();
        var rule = Rule(t, seq: 5); db.NumberingRules.Add(rule); await db.SaveChangesAsync();
        var c = Controller(db, t);

        (await c.SetNumberingRuleActive(rule.Id, false, default)).Should().BeOfType<OkObjectResult>();
        (await db.NumberingRules.SingleAsync()).IsActive.Should().BeFalse();
        (await c.SetNumberingRuleActive(rule.Id, true, default)).Should().BeOfType<OkObjectResult>();
        (await db.NumberingRules.SingleAsync()).IsActive.Should().BeTrue();
        (await db.AdminAuditLogs.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task UpsertNumberingRule_Audits()
    {
        await using var db = Db(); var t = Guid.NewGuid();
        var c = Controller(db, t);
        var req = new NumberingRuleRequest("Employee", "EMP", null, 5, "-", false, false, false);
        await c.UpsertNumberingRule(req, default);
        await c.UpsertNumberingRule(req with { Prefix = "E" }, default);
        (await db.AdminAuditLogs.OrderBy(x => x.CreatedAtUtc).Select(x => x.Action).ToListAsync())
            .Should().BeEquivalentTo(new[] { "Created", "Updated" });
    }
}
