using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Infrastructure.Payroll.ManpowerRecon;

namespace Zayra.Api.Tests.Recon;

/// <summary>An explanation is filed against something the sign-off actually asks about; anything else is refused by name.</summary>
public class ReconExplanationKeyTests
{
    /// <summary>Employee 1: +20% on basic (flagged). Employee 2: held out (needs a person-level reason). Housing does not move.</summary>
    private static async Task<ManpowerReconServiceTests.Kit> SeedAsync()
    {
        var k = await ManpowerReconServiceTests.Kit.CreateAsync();
        k.AddEmployee(1); k.AddSlip(k.Prior, 1, 1000, 250); k.AddSlip(k.Current, 1, 1200, 250);
        k.AddEmployee(2); k.AddSlip(k.Prior, 2, 800);
        k.Db.PayrollRunEmployeeSelections.Add(new Zayra.Api.Models.PayrollRunEmployeeSelection
        { TenantId = k.TenantId, CompanyId = k.CompanyId, PayrollRunId = k.Current.Id, EmployeeId = 2, Mode = "Exclude", Reason = "hold", Outcome = Zayra.Api.Models.PayrollRunSelectionOutcomes.Excluded });
        await k.Db.SaveChangesAsync();
        return k;
    }

    [Theory]
    [InlineData("Item", "Earning:NOT_A_CODE")]   // no such item
    [InlineData("Item", "Earning:HOUSING")]       // a real item that did not move, so nothing is asked about it
    [InlineData("Employee", "99")]                // no such employee
    [InlineData("Employee", "1")]                 // a continuing employee, not one the sign-off asks about
    public async Task AnExplanationForSomethingNotAskedAbout_IsRefused_NamingTheKey(string scope, string key)
    {
        var k = await SeedAsync();
        var ctrl = ReconHarness.Recon(k.Db, k.TenantId, "Finance Approver", ReconHarness.SignPermissions);
        var req = await ReconHarness.ValidRequestAsync(ctrl, k.Current.Id);
        var withExtra = req with { Explanations = req.Explanations!.Append(new ReconExplanationRequest(scope, key, "A perfectly good explanation text.")).ToList() };

        var res = await ctrl.Signoff(k.Current.Id, withExtra, default);

        PayrollReconAccessTests.Status(res).Should().Be(422);
        PayrollReconAccessTests.Error(res).Should().Be("unknown_explanation_key");
        ReconHarness.Json(((ObjectResult)res).Value).GetProperty("keys").EnumerateArray().Select(e => e.GetString()).Should().Equal(key);
        (await k.Db.PayrollRunReconSignoffs.AsNoTracking().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task EveryRequiredExplanationKey_IsAccepted()
    {
        var k = await SeedAsync();
        var ctrl = ReconHarness.Recon(k.Db, k.TenantId, "Finance Approver", ReconHarness.SignPermissions);
        var req = await ReconHarness.ValidRequestAsync(ctrl, k.Current.Id);
        req.Explanations!.Select(e => $"{e.Scope}:{e.Key}").Should().BeEquivalentTo(new[] { "Item:Earning:BASIC", "Employee:2" });
        PayrollReconAccessTests.Status(await ctrl.Signoff(k.Current.Id, req, default)).Should().Be(201);
    }
}
