using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Infrastructure.Payroll.ManpowerRecon;

namespace Zayra.Api.Tests.Recon;

/// <summary>A sign-off or a withdrawal always names a person: with no user id the maker-checker rule cannot be applied, so the request is refused.</summary>
public class ReconSignerIdentityTests
{
    // ── A sign-off names a person ────────────────────────────────────────────────────────────────

    private static ReconCaller NoUser(ManpowerReconServiceTests.Kit k) =>
        new(null, "system", true, EntityScopeContext.GroupLevel, new DataScope { Level = DataScopeLevel.Organization, AllowedEmployeeIds = null });

    [Fact]
    public async Task ACallerWithNoUserId_CannotSign_SoMakerCheckerIsNeverSkipped()
    {
        var k = await ReconWithdrawAndSignerTests.SeedAsync();
        var req = await ReconWithdrawAndSignerTests.ValidAsync(k);

        var res = await new ReconSignoffLifecycleService(k.Db).SignAsync(k.TenantId, k.Current.Id, NoUser(k), req, null, default);

        res.Status.Should().Be(403);
        PayrollReconAccessTests.Error(new ObjectResult(res.Body)).Should().Be("signoff_requires_user");
        (await k.Db.PayrollRunReconSignoffs.AsNoTracking().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ACallerWithNoUserId_CannotWithdraw()
    {
        var k = await ReconWithdrawAndSignerTests.SeedAsync();
        await ReconWithdrawAndSignerTests.SignAsync(k);

        var res = await new ReconSignoffLifecycleService(k.Db).WithdrawAsync(k.TenantId, k.Current.Id, NoUser(k), new ReconWithdrawRequest(ReconWithdrawAndSignerTests.Reason), null, default);

        res.Status.Should().Be(403);
        (await k.Db.PayrollRunReconSignoffs.AsNoTracking().SingleAsync()).Status.Should().Be(ReconSignoffStatuses.Active);
    }
}
