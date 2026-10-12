using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Controllers;
using static Zayra.Api.Tests.Security.SeededRoleBundles;

namespace Zayra.Api.Tests.Security;

public sealed class GradeBenefitPermissionTests
{
    [Theory]
    [InlineData("Employee", false, false)]
    [InlineData("Manager", false, false)]
    [InlineData("HR Officer", true, false)]
    [InlineData("HR Manager", true, true)]
    [InlineData("HR Director", true, true)]
    [InlineData("Admin", true, true)]
    [InlineData("Payroll Manager", false, false)]
    [InlineData("Auditor", false, false)]
    public async Task GradeDefaultsAndExceptions_UseTheSeededCreationAndApprovalAuthority(string role, bool canPreview, bool canExcept)
    {
        var (db, tenant) = await NewTenantAsync("benefit-authority");
        await using var owned = db;
        var caller = await CallerAsync(db, tenant, role);
        Task<IActionResult> Reached() => Task.FromResult<IActionResult>(new OkResult());
        Assert.Equal(canPreview ? 200 : 403, await ProductionAuthorizationGate.StatusAsync<BenefitsController>(
            caller, nameof(BenefitsController.GradeDefaults), Reached));
        Assert.Equal(canExcept ? 200 : 403, await ProductionAuthorizationGate.StatusAsync<BenefitsController>(
            caller, nameof(BenefitsController.ApplyException), Reached));
    }
}
