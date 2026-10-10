using System.Data.Common;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Review minor[1]: withdrawing a pending statutory request used to load the row, check
/// <c>Status == PendingApproval</c> in memory and save <c>Withdrawn</c>. An approval that committed
/// between that read and that write was overwritten: payroll applied the value while the request
/// showed Withdrawn. The race is forced deterministically here — a command interceptor runs a real
/// approval on another connection the moment the withdraw issues its UPDATE — so the old code fails
/// every time, and the conditional claim (UPDATE … WHERE status = 'PendingApproval') returns 409.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class StatutoryWithdrawRacePostgresTests
{
    private readonly PostgresFixture _fx;
    public StatutoryWithdrawRacePostgresTests(PostgresFixture fx) => _fx = fx;

    private static RatesController Rates(ZayraDbContext db, Guid tid, Guid uid)
    {
        var reader = new StatutoryRuleReader(db);
        var claims = new[]
        {
            new Claim("tenant_id", tid.ToString()), new Claim(ClaimTypes.NameIdentifier, uid.ToString()),
            new Claim("permission", "payroll.rates.statutory_override"),
        };
        return new RatesController(db, reader, new StatutoryRateResolver(db, reader))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) },
            },
        };
    }

    /// <summary>Runs <paramref name="onFirstUpdate"/> once, just before the first UPDATE of the
    /// override table goes to the server.</summary>
    private sealed class ApproveFirst(Func<Task> onFirstUpdate) : DbCommandInterceptor
    {
        private int _fired;
        private async Task Maybe(DbCommand command)
        {
            // A real UPDATE statement — not a SELECT of the updated_at/updated_by columns.
            if (System.Text.RegularExpressions.Regex.IsMatch(command.CommandText, @"\bUPDATE\s+""?company_statutory_overrides\b")
                && Interlocked.Exchange(ref _fired, 1) == 0)
                await onFirstUpdate();
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        { await Maybe(command); return result; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        { await Maybe(command); return result; }
    }

    [Fact]
    public async Task WithdrawRacingAnApproval_LosesWith409_AndTheApprovalStands()
    {
        Guid tid, company, maker = Guid.NewGuid(), requestId;
        var jur = $"WDR-{Guid.NewGuid():N}"[..20];
        await using (var db = _fx.CreateDb())
        {
            tid = await PostgresFixture.SeedMinimalTenant(db);
            company = Guid.NewGuid();
            db.StatutoryRules.Add(new StatutoryRule
            {
                TenantId = null, CountryCode = "SAU", Jurisdiction = jur, RuleKey = "ot.standard_multiplier", RuleValue = "1.5",
                DataType = "decimal", Description = "test platform value", EffectiveFrom = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            await db.SaveChangesAsync();
            var created = (OkObjectResult)await Rates(db, tid, maker).CreateStatutoryOverride(new StatutoryOverrideRequest(
                company, "SAU", jur, "ot.standard_multiplier", "1.75", new DateOnly(2026, 1, 1), "CBA", new DateOnly(2027, 1, 1)), default);
            requestId = (Guid)created.Value!.GetType().GetProperty("Id")!.GetValue(created.Value)!;
        }

        IActionResult? approval = null;
        var hook = new ApproveFirst(async () =>
        {
            await using var checkerDb = _fx.CreateDb();
            approval = await Rates(checkerDb, tid, Guid.NewGuid()).ApproveStatutoryOverride(requestId, default);
        });
        await using (var makerDb = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>()
                         .UseNpgsql(_fx.ConnectionString, PostgresFixture.ProductionProviderOptions)
                         .AddInterceptors(hook).Options))
        {
            var withdraw = await Rates(makerDb, tid, maker).RevertStatutoryOverride(requestId, default);
            Assert.IsType<OkObjectResult>(approval);
            var conflict = Assert.IsType<ConflictObjectResult>(withdraw);
            Assert.Equal("STATUTORY_REQUEST_NOT_PENDING", conflict.Value!.GetType().GetProperty("code")!.GetValue(conflict.Value));
        }

        await using var verify = _fx.CreateDb();
        var row = await verify.CompanyStatutoryOverrides.IgnoreQueryFilters().SingleAsync(o => o.Id == requestId);
        Assert.Equal(CompanyPolicyStatuses.Active, row.Status);
        Assert.Equal(0, await verify.AuditLogs.IgnoreQueryFilters()
            .CountAsync(a => a.TenantId == tid && a.Action == "payroll.rate.statutory_override.withdrawn"));
        Assert.Equal(1.75m, await new StatutoryRuleReader(verify).GetDecimalAsync("SAU", jur, "ot.standard_multiplier", new DateOnly(2026, 6, 1), tid, default, company));
    }
}
