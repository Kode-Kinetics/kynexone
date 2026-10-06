using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers.Entitlements;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Entitlements;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.ReleaseA;

/// <summary>
/// Slice R1 on real PostgreSQL 16, with the migration's DDL: publishing under the no-overlap EXCLUDE and the
/// serializer's transaction, the floor CHECK on pay_components behind the service's refusal, the skip marker against
/// the NULLS NOT DISTINCT unique key, and the legacy import committing in one transaction.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class EntitlementMatrixPostgresTests(PostgresFixture fixture)
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);
    private static readonly DateOnly NextMonth = new DateOnly(Today.Year, Today.Month, 1).AddMonths(1);
    private static readonly DateOnly MonthAfter = NextMonth.AddMonths(1);

    [Fact]
    public async Task Publish_ClosesAndOpensUnderTheRealExclusion_AndTheCompanyCellSitsBesideTheGroupCell()
    {
        var seed = await SeedAsync();
        await using (var db = fixture.CreateDb())
        {
            Ok(await Controller(db, seed).Publish(Service(db), new PublishMatrixRequest(null, NextMonth,
                [Pct(seed.GradeId, "HOUSING", 0.25m), Amount(seed.GradeId, "PER_DIEM", 150m)]), false, default));
        }
        await using (var db = fixture.CreateDb())
        {
            Ok(await Controller(db, seed).Publish(Service(db), new PublishMatrixRequest(null, MonthAfter, [Pct(seed.GradeId, "HOUSING", 0.30m)]), false, default));
            Ok(await Controller(db, seed).Publish(Service(db), new PublishMatrixRequest(seed.CompanyId, NextMonth, [Amount(seed.GradeId, "PER_DIEM", 200m)]), false, default));
        }

        await using var verify = fixture.CreateDb();
        var housing = await verify.GradeEntitlements.Where(x => x.TenantId == seed.TenantId && x.PayComponentCode == "HOUSING")
            .OrderBy(x => x.EffectiveFrom).ToListAsync();
        housing.Select(x => (x.Rate, x.EffectiveTo)).Should().Equal((0.25m, (DateOnly?)MonthAfter.AddDays(-1)), (0.30m, (DateOnly?)null));
        var perDiem = await verify.GradeEntitlements.Where(x => x.TenantId == seed.TenantId && x.PayComponentCode == "PER_DIEM").ToListAsync();
        perDiem.Select(x => x.CompanyKey).Should().BeEquivalentTo([Guid.Empty, seed.CompanyId]);
        (await verify.AuditLogs.CountAsync(a => a.TenantId == seed.TenantId && a.Action == EntitlementMatrixService.AuditPublished)).Should().Be(4);
        var lines = await new EntitlementMatrixService(verify, new FixedClock(Today))
            .GradeStandardLinesAsync(seed.TenantId, seed.GradeId, seed.CompanyId, NextMonth, default);
        lines.Single(l => l.ComponentCode == "PER_DIEM").Amount.Should().Be(200m);
    }

    [Fact]
    public async Task TheFloorCheck_BacksTheServiceRefusal_AndASkipMarkerReusesItsRowAndNeverReachesPayroll()
    {
        var seed = await SeedAsync();
        await using (var db = fixture.CreateDb())
        {
            var housing = await Controller(db, seed).SetOffering(Service(db), new SetOfferingRequest(seed.CompanyId, "HOUSING", false, NextMonth), default);
            ((ObjectResult)housing).StatusCode.Should().Be(409);
            // Skip, withdraw, skip again on the same month: the unique key (tenant, company, code, type, effective_from)
            // NULLS NOT DISTINCT counts the withdrawn row, so the service reuses it rather than inserting a second.
            foreach (var offered in new[] { false, true, false })
                Ok(await Controller(db, seed).SetOffering(Service(db), new SetOfferingRequest(seed.CompanyId, "EDUCATION", offered, NextMonth), default));
        }

        await using var verify = fixture.CreateDb();
        var companyRows = await verify.PayComponents.IgnoreQueryFilters().Where(p => p.TenantId == seed.TenantId && p.CompanyId == seed.CompanyId).ToListAsync();
        companyRows.Should().ContainSingle().Which.Should().Match<PayComponent>(p => p.Code == "EDUCATION" && !p.IsOffered && !p.IsDeleted);

        // The database refuses a skipped floor even if a writer bypassed the service.
        var template = await verify.PayComponents.AsNoTracking().SingleAsync(p => p.TenantId == seed.TenantId && p.CompanyId == null && p.Code == "HOUSING");
        verify.PayComponents.Add(new PayComponent
        {
            TenantId = seed.TenantId, CompanyId = seed.CompanyId, Code = "HOUSING", NameEn = "Housing", NameAr = "سكن",
            ComponentType = template.ComponentType, CalcMethod = template.CalcMethod, EntitlementClass = template.EntitlementClass,
            StatutoryFloor = template.StatutoryFloor, IsOffered = false, EffectiveFrom = NextMonth,
        });
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => verify.SaveChangesAsync());
        var pg = Assert.IsType<PostgresException>(error.InnerException);
        (pg.SqlState, pg.ConstraintName).Should().Be((PostgresErrorCodes.CheckViolation, "ck_pay_components__floor_always_offered"));

        // The payroll engine never sees a benefit row or its company switch.
        await using var read = fixture.CreateDb();
        var all = await read.PayComponents.IgnoreQueryFilters().AsNoTracking().Where(p => p.TenantId == seed.TenantId && !p.IsDeleted).ToListAsync();
        PayComponentEngine.ResolveInEffect(all.Where(p => p.CompanyId == null || p.CompanyId == seed.CompanyId), seed.TenantId, NextMonth)
            .Select(p => p.Code).Should().NotContain(["EDUCATION", "AIR_TICKET", "MEDICAL", "PER_DIEM"]);
    }

    [Fact]
    public async Task LegacyImport_CommitsInOneTransaction_AndIsIdempotent()
    {
        var seed = await SeedAsync();
        await using (var db = fixture.CreateDb())
        {
            db.GradePayScaleComponents.Add(new GradePayScaleComponent { TenantId = seed.TenantId, GradeId = seed.GradeId, ComponentCode = "HOUSING",
                ComponentName = "Housing", CalculationType = "PercentOfBasic", Percentage = 25m });
            db.GradePayScaleComponents.Add(new GradePayScaleComponent { TenantId = seed.TenantId, GradeId = seed.GradeId, ComponentCode = "TRANSPORT",
                ComponentName = "Transport", Amount = 800m });
            await db.SaveChangesAsync();
        }
        foreach (var expected in new[] { 2, 0 })
        {
            await using var db = fixture.CreateDb();
            var result = (LegacyImportResult)((ObjectResult)await Controller(db, seed).ImportLegacy(Service(db), commit: true, effectiveFrom: NextMonth, default)).Value!;
            result.Imported.Should().Be(expected);
        }
        await using var verify = fixture.CreateDb();
        (await verify.GradeEntitlements.CountAsync(x => x.TenantId == seed.TenantId && x.SourceRule == EntitlementMatrixService.SourceRuleImport)).Should().Be(2);
        (await verify.GradePayScaleComponents.CountAsync(x => x.TenantId == seed.TenantId)).Should().Be(2, "nothing legacy is dropped");
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────

    private sealed record Seed(Guid TenantId, Guid CompanyId, Guid GradeId);

    private async Task<Seed> SeedAsync()
    {
        await using var db = fixture.CreateDb();
        var tid = await PostgresFixture.SeedMinimalTenant(db);
        var company = new Company { TenantId = tid, LegalNameEn = $"Masar Logistics {Guid.NewGuid():N}", CountryCode = "SAU", DefaultCurrency = "SAR", IsActive = true };
        var grade = new Grade { TenantId = tid, Code = "G3", Name = "Supervisor", Level = 30 };
        db.AddRange(company, grade, new TenantFeatureFlag { TenantId = tid, FeatureKey = FeatureKeys.ReleaseA, IsEnabled = true });
        await db.SaveChangesAsync();
        return new Seed(tid, company.Id, grade.Id);
    }

    private static EntitlementMatrixService Service(ZayraDbContext db) => new(db, new FixedClock(Today));

    private static EntitlementMatrixController Controller(ZayraDbContext db, Seed seed) => new()
    {
        ControllerContext = new()
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("tenant_id", seed.TenantId.ToString()),
                    new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                    new Claim(ClaimTypes.Role, "HR Director"),
                }, "test")),
            },
        },
    };

    private static void Ok(IActionResult result) =>
        ((ObjectResult)result).StatusCode.Should().Be(200, System.Text.Json.JsonSerializer.Serialize(((ObjectResult)result).Value));

    private static MatrixCellInput Amount(Guid gradeId, string code, decimal amount) =>
        new() { GradeId = gradeId, ComponentCode = code, Eligible = true, ValueType = "Amount", Amount = amount };

    private static MatrixCellInput Pct(Guid gradeId, string code, decimal rate) =>
        new() { GradeId = gradeId, ComponentCode = code, Eligible = true, ValueType = "PercentOfBasic", Rate = rate };

    private sealed class FixedClock(DateOnly today) : ITenantClock
    {
        public Task<DateOnly> TodayAsync(Guid tenantId, CancellationToken ct) => Task.FromResult(today);
    }
}
