using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Entitlements;
using Zayra.Api.Controllers.Entitlements;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Entitlements;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.ReleaseA;

/// <summary>
/// The HR package view shows an employee's salary figures (basic, housing, transport, and any amount worked out from them) only to a caller who
/// holds employees.sensitive, like every other view of pay. entitlements.read alone (a custom role, or a Finance role that confirms allowances) sees
/// the package's shape: which benefits, the grade's standard, the contract benefits, but no salary amounts.
/// </summary>
public sealed class PackageViewSalaryAmountsTests
{
    private static readonly DateOnly Today = new(2026, 10, 6);

    [Fact]
    public async Task ACustomRoleWithEntitlementsReadOnly_SeesThePackage_WithoutTheSalaryAmounts()
    {
        var (db, s) = await SeededAsync();

        var view = await GetAsync(db, s, "entitlements.read");

        view.Salary.Should().BeNull("the salary record's basic salary is pay data");
        view.SalaryAmountsHidden.Should().BeTrue("the screen says why the amounts are missing");
        var salaryLines = view.Package.Lines.Where(l => l.Source == PackageLineSources.Salary).ToList();
        salaryLines.Should().NotBeEmpty();
        salaryLines.Should().OnlyContain(l => l.Amount == null && l.MonthlyCash == null && l.ResolvedAmount == null);
        view.Package.Lines.Where(l => l.Rate != null).Should().OnlyContain(l => l.ResolvedAmount == null, "an amount worked out from the salary reveals it");
        view.Package.Lines.Select(l => l.ComponentCode).Should().Contain(["HOUSING", "TRANSPORT"], "the package's shape is still shown");
    }

    [Fact]
    public async Task WithEmployeesSensitive_TheSameViewCarriesTheSalaryAmounts()
    {
        var (db, s) = await SeededAsync();

        var view = await GetAsync(db, s, "entitlements.read", "employees.sensitive");

        view.Salary!.BasicSalary.Should().Be(8000m);
        view.SalaryAmountsHidden.Should().BeFalse();
        view.Package.Lines.Single(l => l.ComponentCode == "HOUSING").MonthlyCash.Should().Be(2000m);
        view.Package.Lines.Single(l => l.ComponentCode == "TRANSPORT").MonthlyCash.Should().Be(800m);
    }

    [Fact]
    public async Task WithoutEmployeesSensitive_AFrozenRowsResolvedFigureIsLeftOut_WithItTheFigureIsShown()
    {
        var (db, s) = await SeededAsync();
        var resolver = new EntitlementResolver(db);
        await new EntitlementWriter(db, new FixedTenantClock(PackageSeed.FreezeDay), resolver).FreezeTermAsync(s.TenantId, s.Term.Id, default);
        await db.SaveChangesAsync();
        // A rate-based frozen row, as the writer witnesses one: 25% of basic = SAR 2,000 (the figure gives the salary away beside the rate).
        foreach (var row in await db.EmployeeEntitlements.ToListAsync()) { row.Rate = 0.25m; row.ResolvedAmount = 2000m; }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var hidden = await GetAsync(db, s, "entitlements.read");
        var shown = await GetAsync(db, s, "entitlements.read", "employees.sensitive");

        shown.FrozenRows.Should().NotBeEmpty().And.OnlyContain(r => r.ResolvedAmount == 2000m);
        hidden.FrozenRows.Should().HaveCount(shown.FrozenRows.Count).And.OnlyContain(r => r.ResolvedAmount == null);
    }

    [Fact]
    public async Task WithoutEmployeesSensitive_AProposedRowsResolvedFigureIsLeftOut()
    {
        var (db, s) = await SeededAsync();
        var resolver = new EntitlementResolver(db);
        var proposal = (await new EntitlementWriter(db, new FixedTenantClock(Today), resolver).ProposeAsync(s.TenantId, s.Term.Id, default))!;
        proposal = proposal with { Rows = proposal.Rows.Select(r => r with { Rate = 0.25m, ResolvedAmount = 2000m, ResolvedBasisSalaryId = s.Salary.Id }).ToList() };
        var job = new BackgroundJob { TenantId = s.TenantId, JobType = PackageFreezeJobHandler.JobType, IdempotencyKey = "k", CreatedByUserId = Guid.NewGuid() };
        db.BackgroundJobs.Add(job);
        db.BackgroundJobItems.Add(new BackgroundJobItem { TenantId = s.TenantId, JobId = job.Id, ItemKey = PackageFreezeJobHandler.ItemKey(s.Term.Id),
            ResultJson = System.Text.Json.JsonSerializer.Serialize(proposal) });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var hidden = await GetAsync(db, s, "entitlements.read");
        var shown = await GetAsync(db, s, "entitlements.read", "employees.sensitive");

        shown.Proposal!.Rows.Should().NotBeEmpty().And.OnlyContain(r => r.ResolvedAmount == 2000m);
        hidden.Proposal!.Rows.Should().HaveCount(shown.Proposal.Rows.Count).And.OnlyContain(r => r.ResolvedAmount == null && r.ResolvedBasisSalaryId == null);
    }

    private static async Task<(ZayraDbContext Db, PackageSeed Seed)> SeededAsync()
    {
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase($"pkg-sensitive-{Guid.NewGuid():N}").Options);
        var seed = new PackageSeed();
        await seed.SaveAsync(db);
        return (db, seed);
    }

    private static async Task<EmployeePackageView> GetAsync(ZayraDbContext db, PackageSeed s, params string[] permissions)
    {
        var resolver = new EntitlementResolver(db);
        var clock = new FixedTenantClock(Today);
        var claims = new List<Claim> { new("tenant_id", s.TenantId.ToString()), new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Role, "Benefits Viewer"), new("is_group_scope", "true") };
        claims.AddRange(permissions.Select(p => new Claim("permission", p)));
        var controller = new EmployeePackageController(db, resolver, new EntitlementWriter(db, clock, resolver), clock)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) } },
        };
        return (EmployeePackageView)((OkObjectResult)(await controller.Get(s.Mohammed.Id, null, default)).Result!).Value!;
    }
}
