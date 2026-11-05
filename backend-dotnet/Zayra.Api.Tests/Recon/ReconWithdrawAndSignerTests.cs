using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Infrastructure.Payroll.ManpowerRecon;

namespace Zayra.Api.Tests.Recon;

/// <summary>
/// Who may withdraw a sign-off: someone who can see the whole run, and not the person who created or processed it.
/// </summary>
public class ReconWithdrawAndSignerTests
{
    internal const string Reason = "The housing figure needs another look.";

    internal static async Task<ManpowerReconServiceTests.Kit> SeedAsync()
    {
        var k = await ManpowerReconServiceTests.Kit.CreateAsync();
        k.AddEmployee(1); k.AddSlip(k.Prior, 1, 1000); k.AddSlip(k.Current, 1, 1000);
        await k.Db.SaveChangesAsync();
        return k;
    }

    internal static Zayra.Api.Controllers.PayrollReconController Ctrl(
        ManpowerReconServiceTests.Kit k, Guid? user = null, DataScope? scope = null) =>
        ReconHarness.Recon(k.Db, k.TenantId, "Finance Approver", ReconHarness.SignPermissions, user, dataScope: scope);

    internal static async Task SignAsync(ManpowerReconServiceTests.Kit k)
    {
        var signer = Ctrl(k);
        PayrollReconAccessTests.Status(await signer.Signoff(k.Current.Id, await ReconHarness.ValidRequestAsync(signer, k.Current.Id), default)).Should().Be(201);
    }

    internal static Task<ReconSignoffRequest> ValidAsync(ManpowerReconServiceTests.Kit k) =>
        ReconHarness.ValidRequestAsync(Ctrl(k), k.Current.Id);

    // ── Withdraw: organisation scope ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task ADepartmentScopedCaller_CannotWithdraw_AndTheSignoffStaysActive()
    {
        var k = await SeedAsync();
        await SignAsync(k);
        var scoped = new DataScope { Level = DataScopeLevel.Department, AllowedEmployeeIds = new[] { 1 } };

        var res = await Ctrl(k, scope: scoped).WithdrawSignoff(k.Current.Id, new ReconWithdrawRequest(Reason), default);

        PayrollReconAccessTests.Status(res).Should().Be(403);
        PayrollReconAccessTests.Error(res).Should().Be("signoff_requires_organization_scope");
        (await k.Db.PayrollRunReconSignoffs.AsNoTracking().SingleAsync()).Status.Should().Be(ReconSignoffStatuses.Active);
    }

    // ── Withdraw: maker-checker ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheRunsCreatorAndProcessor_CannotWithdraw()
    {
        var k = await SeedAsync();
        await SignAsync(k);

        var asCreator = await Ctrl(k, k.MakerId).WithdrawSignoff(k.Current.Id, new ReconWithdrawRequest(Reason), default);
        PayrollReconAccessTests.Status(asCreator).Should().Be(403);
        PayrollReconAccessTests.Error(asCreator).Should().Be("maker_checker_violation");

        var processor = Guid.NewGuid();
        k.Current.ProcessedByUserId = processor;
        await k.Db.SaveChangesAsync();
        PayrollReconAccessTests.Error(await Ctrl(k, processor).WithdrawSignoff(k.Current.Id, new ReconWithdrawRequest(Reason), default))
            .Should().Be("maker_checker_violation");
        (await k.Db.PayrollRunReconSignoffs.AsNoTracking().SingleAsync()).Status.Should().Be(ReconSignoffStatuses.Active);
    }

    [Fact]
    public async Task ADifferentReviewer_CanWithdraw()
    {
        var k = await SeedAsync();
        await SignAsync(k);
        PayrollReconAccessTests.Status(await Ctrl(k).WithdrawSignoff(k.Current.Id, new ReconWithdrawRequest(Reason), default)).Should().Be(200);
    }
}
