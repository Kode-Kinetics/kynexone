using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Tax policy create/edit/retire/remove: company scope on every write (the tenant-wide default is
/// group-only), history protection once a finalized payroll run falls in the policy window, and an
/// audit entry per change.
/// </summary>
public class SetupRemovalSafetyTaxPolicyTests
{
    private static ZayraDbContext Db() => new(new DbContextOptionsBuilder<ZayraDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed record World(Guid Tenant, Company A, Company B);

    private static async Task<World> Seed(ZayraDbContext db)
    {
        var t = Guid.NewGuid();
        var a = new Company { TenantId = t, LegalNameEn = "Alpha", RegistrationNumber = "A", IsActive = true };
        var b = new Company { TenantId = t, LegalNameEn = "Beta", RegistrationNumber = "B", IsActive = true };
        db.AddRange(a, b); await db.SaveChangesAsync();
        return new World(t, a, b);
    }

    private static CompanyTaxPoliciesController Ctl(ZayraDbContext db, Guid tenant, params Guid[] companies)
    {
        var claims = new List<Claim> { new("tenant_id", tenant.ToString()), new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new(ClaimTypes.Role, "Admin") };
        if (companies.Length == 0) claims.Add(new Claim("is_group_scope", "true"));
        else claims.AddRange(companies.Select(c => new Claim("entity_access", JsonSerializer.Serialize(new { c, r = "Payroll Manager" }))));
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) };
        return new CompanyTaxPoliciesController(db) { ControllerContext = new ControllerContext { HttpContext = http } };
    }

    private static CompanyTaxPolicy Policy(Guid t, Guid? company, DateOnly from, DateOnly? to = null, decimal rate = 0m) => new()
    {
        TenantId = t, CompanyId = company, CountryCode = "SA", EffectiveFrom = from, EffectiveTo = to, IncomeTaxRatePercent = rate,
    };

    private static CompanyTaxPolicyRequest Req(Guid? company, DateOnly from, DateOnly? to = null, decimal rate = 0m, string? notes = null) =>
        new(company, "SA", from, to, "Active", rate, true, notes, AcknowledgeNonZeroGccTax: true);

    private static string? Code(IActionResult r) => r is ConflictObjectResult c
        ? c.Value!.GetType().GetProperty("code")!.GetValue(c.Value)?.ToString() : null;

    private static PayrollRun Run(Guid t, Guid? company, int y, int m, string status) =>
        new() { TenantId = t, CompanyId = company, Year = y, Month = m, Status = status };

    // ── scope ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CompanyScopedAdmin_CannotDelete_AnotherCompanysPolicy_OrTheTenantDefault()
    {
        await using var db = Db(); var w = await Seed(db);
        var other = Policy(w.Tenant, w.B.Id, new DateOnly(2026, 1, 1));
        var tenantWide = Policy(w.Tenant, null, new DateOnly(2026, 1, 1));
        db.CompanyTaxPolicies.AddRange(other, tenantWide); await db.SaveChangesAsync();
        var c = Ctl(db, w.Tenant, w.A.Id);

        (await c.Delete(other.Id, default)).Should().BeOfType<ForbidResult>();
        (await c.Delete(tenantWide.Id, default)).Should().BeOfType<ForbidResult>();
        (await db.CompanyTaxPolicies.CountAsync(p => !p.IsDeleted)).Should().Be(2);
    }

    [Fact]
    public async Task CompanyScopedAdmin_CannotEdit_TheTenantDefault_OrCreateOne()
    {
        await using var db = Db(); var w = await Seed(db);
        var tenantWide = Policy(w.Tenant, null, new DateOnly(2026, 1, 1));
        db.CompanyTaxPolicies.Add(tenantWide); await db.SaveChangesAsync();
        var c = Ctl(db, w.Tenant, w.A.Id);

        (await c.Update(tenantWide.Id, Req(null, new DateOnly(2026, 1, 1), notes: "x"), default)).Should().BeOfType<ForbidResult>();
        (await c.Create(Req(null, new DateOnly(2027, 1, 1)), default)).Should().BeOfType<ForbidResult>();
        (await db.CompanyTaxPolicies.SingleAsync()).Notes.Should().BeEmpty();
    }

    [Fact]
    public async Task GroupAdmin_CanManageTenantDefault_AndEveryChangeIsAudited()
    {
        await using var db = Db(); var w = await Seed(db);
        var c = Ctl(db, w.Tenant);

        var created = (OkObjectResult)await c.Create(Req(null, new DateOnly(2026, 1, 1)), default);
        var id = (Guid)created.Value!.GetType().GetProperty("id")!.GetValue(created.Value)!;
        (await c.Update(id, Req(null, new DateOnly(2026, 1, 1), notes: "memo"), default)).Should().BeOfType<OkObjectResult>();
        (await c.Delete(id, default)).Should().BeOfType<NoContentResult>();

        (await db.AdminAuditLogs.OrderBy(x => x.CreatedAtUtc).Select(x => x.Action).ToListAsync())
            .Should().BeEquivalentTo(new[] { "Created", "Updated", "Deleted" }, o => o.WithStrictOrdering());
        (await db.AdminAuditLogs.AllAsync(x => x.EntityType == "CompanyTaxPolicy")).Should().BeTrue();
    }

    // ── payroll history protection ───────────────────────────────────────────

    [Fact]
    public async Task Delete_PolicyWithFinalizedRunInWindow_IsRefused()
    {
        await using var db = Db(); var w = await Seed(db);
        var p = Policy(w.Tenant, w.A.Id, new DateOnly(2026, 1, 1));
        db.CompanyTaxPolicies.Add(p);
        db.PayrollRuns.Add(Run(w.Tenant, w.A.Id, 2026, 3, "Locked"));
        await db.SaveChangesAsync();

        var r = await Ctl(db, w.Tenant).Delete(p.Id, default);

        Code(r).Should().Be("TAX_POLICY_USED_BY_PAYROLL");
        (await db.CompanyTaxPolicies.SingleAsync()).IsDeleted.Should().BeFalse();
    }

    [Fact]
    public async Task Delete_PolicyWithOnlyDraftOrVoidedRuns_IsAllowed()
    {
        await using var db = Db(); var w = await Seed(db);
        var p = Policy(w.Tenant, w.A.Id, new DateOnly(2026, 1, 1));
        db.CompanyTaxPolicies.Add(p);
        db.PayrollRuns.Add(Run(w.Tenant, w.A.Id, 2026, 3, "Draft"));
        db.PayrollRuns.Add(Run(w.Tenant, w.A.Id, 2026, 4, "Voided"));
        await db.SaveChangesAsync();

        (await Ctl(db, w.Tenant).Delete(p.Id, default)).Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task RunOutsideWindow_OrOfAnotherCompany_DoesNotBlock()
    {
        await using var db = Db(); var w = await Seed(db);
        var p = Policy(w.Tenant, w.A.Id, new DateOnly(2026, 1, 1), new DateOnly(2026, 6, 30));
        db.CompanyTaxPolicies.Add(p);
        db.PayrollRuns.Add(Run(w.Tenant, w.A.Id, 2025, 12, "Locked"));
        db.PayrollRuns.Add(Run(w.Tenant, w.A.Id, 2026, 8, "Locked"));
        db.PayrollRuns.Add(Run(w.Tenant, w.B.Id, 2026, 3, "Locked"));
        await db.SaveChangesAsync();

        (await Ctl(db, w.Tenant).Delete(p.Id, default)).Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task LegacyRunWithNoCompany_CountsAsUsingACompanyPolicy()
    {
        await using var db = Db(); var w = await Seed(db);
        var p = Policy(w.Tenant, w.A.Id, new DateOnly(2026, 1, 1));
        db.CompanyTaxPolicies.Add(p);
        db.PayrollRuns.Add(Run(w.Tenant, null, 2026, 3, "Locked"));
        await db.SaveChangesAsync();

        Code(await Ctl(db, w.Tenant).Delete(p.Id, default)).Should().Be("TAX_POLICY_USED_BY_PAYROLL");
    }

    [Fact]
    public async Task Retire_CannotExtendAnEndDate()
    {
        await using var db = Db(); var w = await Seed(db);
        var p = Policy(w.Tenant, w.A.Id, new DateOnly(2026, 1, 1), new DateOnly(2026, 6, 30));
        db.CompanyTaxPolicies.Add(p); await db.SaveChangesAsync();

        (await Ctl(db, w.Tenant).Retire(p.Id, new RetireTaxPolicyRequest(new DateOnly(2026, 12, 31)), default))
            .Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Update_RateOrStartDate_OfUsedPolicy_IsRefused_ButNotesAreAllowed()
    {
        await using var db = Db(); var w = await Seed(db);
        var p = Policy(w.Tenant, w.A.Id, new DateOnly(2026, 1, 1), rate: 0m);
        db.CompanyTaxPolicies.Add(p);
        db.PayrollRuns.Add(Run(w.Tenant, w.A.Id, 2026, 3, "Approved"));
        await db.SaveChangesAsync();
        var c = Ctl(db, w.Tenant);

        Code(await c.Update(p.Id, Req(w.A.Id, new DateOnly(2026, 1, 1), rate: 5m), default)).Should().Be("TAX_POLICY_USED_BY_PAYROLL");
        Code(await c.Update(p.Id, Req(w.A.Id, new DateOnly(2026, 2, 1)), default)).Should().Be("TAX_POLICY_USED_BY_PAYROLL");
        (await c.Update(p.Id, Req(w.A.Id, new DateOnly(2026, 1, 1), notes: "approved by board"), default)).Should().BeOfType<OkObjectResult>();
        var saved = await db.CompanyTaxPolicies.SingleAsync();
        saved.IncomeTaxRatePercent.Should().Be(0m); saved.Notes.Should().Be("approved by board");
    }

    [Fact]
    public async Task Retire_UsedPolicy_EndDatesAfterLastFinalizedMonth_AndAudits()
    {
        await using var db = Db(); var w = await Seed(db);
        var p = Policy(w.Tenant, w.A.Id, new DateOnly(2026, 1, 1));
        db.CompanyTaxPolicies.Add(p);
        db.PayrollRuns.Add(Run(w.Tenant, w.A.Id, 2026, 3, "Locked"));
        await db.SaveChangesAsync();
        var c = Ctl(db, w.Tenant);

        // Cannot retire from a date inside a month that is already paid.
        Code(await c.Retire(p.Id, new RetireTaxPolicyRequest(new DateOnly(2026, 3, 15)), default)).Should().Be("TAX_POLICY_USED_BY_PAYROLL");

        (await c.Retire(p.Id, new RetireTaxPolicyRequest(new DateOnly(2026, 3, 31)), default)).Should().BeOfType<OkObjectResult>();

        var saved = await db.CompanyTaxPolicies.SingleAsync();
        saved.EffectiveTo.Should().Be(new DateOnly(2026, 3, 31));
        saved.IsDeleted.Should().BeFalse("retiring keeps the policy for the record");
        (await db.AdminAuditLogs.SingleAsync()).Action.Should().Be("Retired");
    }

    [Fact]
    public async Task Retire_BeforeStartDate_IsRejected()
    {
        await using var db = Db(); var w = await Seed(db);
        var p = Policy(w.Tenant, w.A.Id, new DateOnly(2026, 6, 1));
        db.CompanyTaxPolicies.Add(p); await db.SaveChangesAsync();

        (await Ctl(db, w.Tenant).Retire(p.Id, new RetireTaxPolicyRequest(new DateOnly(2026, 5, 1)), default))
            .Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Retire_AnotherCompanysPolicy_IsForbidden()
    {
        await using var db = Db(); var w = await Seed(db);
        var p = Policy(w.Tenant, w.B.Id, new DateOnly(2026, 1, 1));
        db.CompanyTaxPolicies.Add(p); await db.SaveChangesAsync();

        (await Ctl(db, w.Tenant, w.A.Id).Retire(p.Id, new RetireTaxPolicyRequest(new DateOnly(2026, 12, 31)), default))
            .Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task List_FlagsPoliciesLockedByPayroll()
    {
        await using var db = Db(); var w = await Seed(db);
        var used = Policy(w.Tenant, w.A.Id, new DateOnly(2026, 1, 1), new DateOnly(2026, 6, 30));
        var fresh = Policy(w.Tenant, w.B.Id, new DateOnly(2026, 1, 1));
        db.CompanyTaxPolicies.AddRange(used, fresh);
        db.PayrollRuns.Add(Run(w.Tenant, w.A.Id, 2026, 2, "Locked"));
        await db.SaveChangesAsync();

        var ok = (OkObjectResult)await Ctl(db, w.Tenant).List(null, default);
        var rows = ((System.Collections.IEnumerable)ok.Value!).Cast<object>().ToList();
        bool Locked(Guid id) => (bool)rows.Single(r => (Guid)r.GetType().GetProperty("id")!.GetValue(r)! == id)
            .GetType().GetProperty("usedByPayroll")!.GetValue(rows.Single(r => (Guid)r.GetType().GetProperty("id")!.GetValue(r)! == id))!;
        Locked(used.Id).Should().BeTrue();
        Locked(fresh.Id).Should().BeFalse();
    }
}
