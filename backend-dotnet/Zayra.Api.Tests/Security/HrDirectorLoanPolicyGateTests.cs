using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;
using static Zayra.Api.Tests.Security.SeededRoleBundles;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Owner decision: HR Director manages loan policies. The four policy gates take loans.policy_manage (Admin, HR
/// Manager) OR employees.approve (Admin, HR Manager, HR Director); Finance and Finance Approver hold neither. Every
/// call runs the seeded role bundles through the real authorization pipeline (<see cref="ProductionAuthorizationGate"/>),
/// and the end-to-end cases run the real action body behind it, including its hr_policy_owner_required check.
/// </summary>
public sealed class HrDirectorLoanPolicyGateTests
{
    private static readonly string[] PolicyActions =
    {
        nameof(LoansController.CreateLoanPolicy),
        nameof(LoansController.PublishGradeLimits),
        nameof(LoansController.SetLoanTypeGradeLimited),
        nameof(LoansController.SetLoanTypeOffering),
    };

    public static TheoryData<string> Actions()
    {
        var data = new TheoryData<string>();
        foreach (var action in PolicyActions) data.Add(action);
        return data;
    }

    [Theory]
    [MemberData(nameof(Actions))]
    public async Task PolicyGate_AdmitsHrPolicyOwners_AndRefusesFinance(string action)
    {
        var (db, tenantId) = await NewTenantAsync("loan-policy-gate");
        async Task<int> StatusFor(string role) =>
            await ProductionAuthorizationGate.StatusAsync<LoansController>(await CallerAsync(db, tenantId, role), action,
                () => Task.FromResult<IActionResult>(new OkResult()));

        foreach (var role in new[] { "HR Director", "HR Manager", "Admin" })
            (await StatusFor(role)).Should().Be(StatusCodes.Status200OK, $"{role} owns loan policy configuration ({action})");
        foreach (var role in new[] { "Finance", "Finance Approver", "Payroll Manager", "HR Officer", "Manager", "Employee" })
            (await StatusFor(role)).Should().Be(StatusCodes.Status403Forbidden, $"{role} must not reach {action}");
    }

    [Fact]
    public async Task TheSeededBundles_AreUnchanged_HrDirectorPassesOnEmployeesApprove()
    {
        var (db, tenantId) = await NewTenantAsync("loan-policy-keys");
        var hrDirector = await PermissionsOfAsync(db, tenantId, "HR Director");
        hrDirector.Should().Contain("employees.approve").And.NotContain("loans.policy_manage").And.NotContain("loans.write");
        foreach (var finance in new[] { "Finance", "Finance Approver" })
            (await PermissionsOfAsync(db, tenantId, finance)).Should().NotContain(new[] { "employees.approve", "loans.policy_manage" });
    }

    [Fact]
    public async Task HrDirector_Gets200_OnAllFourPolicyEndpoints_EndToEnd()
    {
        var f = await Fixture.CreateAsync();
        var caller = await CallerAsync(f.Db, f.TenantId, "HR Director");
        var controller = f.Controller(caller);

        (await Run(caller, nameof(LoansController.CreateLoanPolicy), () => controller.CreateLoanPolicy(
            new LoanPolicyRequest(f.Company.Id, f.Type.Id, "Director terms", MaxAmount: 8_000m,
                AllowedEmploymentStatuses: ["Active"], AllowedRepaymentMethods: ["BankTransfer"]), default)))
            .Should().Be(StatusCodes.Status200OK);
        (await Run(caller, nameof(LoansController.PublishGradeLimits), () => controller.PublishGradeLimits(
            new PublishGradeLimitsRequest(f.Type.Id, null, f.Today,
                [new(f.Grade.Id, true, GradeEntitlementValueTypes.Amount, 5_000m)]), default)))
            .Should().Be(StatusCodes.Status200OK);
        (await Run(caller, nameof(LoansController.SetLoanTypeGradeLimited), () => controller.SetLoanTypeGradeLimited(
            f.Type.Id, new SetGradeLimitedRequest(true), default)))
            .Should().Be(StatusCodes.Status200OK);
        (await Run(caller, nameof(LoansController.SetLoanTypeOffering), () => controller.SetLoanTypeOffering(
            new SetLoanOfferingRequest(f.Company.Id, f.Type.Id, false), default)))
            .Should().Be(StatusCodes.Status200OK);

        var policies = await f.Db.LoanPolicies.AsNoTracking().Where(x => x.TenantId == f.TenantId).ToListAsync();
        policies.Should().Contain(x => x.PolicyName == "Director terms");
        policies.Should().ContainSingle(x => x.IsActive && !x.IsOffered);
        (await f.Db.GradeEntitlements.AsNoTracking().CountAsync(x => x.TenantId == f.TenantId)).Should().Be(1);
        (await f.Db.LoanTypes.AsNoTracking().SingleAsync(x => x.Id == f.Type.Id)).GradeLimited.Should().BeTrue();
    }

    [Theory]
    [InlineData("Finance")]
    [InlineData("Finance Approver")]
    public async Task FinanceRoles_Get403_OnAllFourPolicyEndpoints_AndChangeNothing(string role)
    {
        var f = await Fixture.CreateAsync();
        var caller = await CallerAsync(f.Db, f.TenantId, role);
        var controller = f.Controller(caller);

        (await Run(caller, nameof(LoansController.CreateLoanPolicy), () => controller.CreateLoanPolicy(
            new LoanPolicyRequest(f.Company.Id, f.Type.Id, "Finance terms",
                AllowedEmploymentStatuses: ["Active"], AllowedRepaymentMethods: ["BankTransfer"]), default)))
            .Should().Be(StatusCodes.Status403Forbidden);
        (await Run(caller, nameof(LoansController.PublishGradeLimits), () => controller.PublishGradeLimits(
            new PublishGradeLimitsRequest(f.Type.Id, null, f.Today,
                [new(f.Grade.Id, true, GradeEntitlementValueTypes.Amount, 5_000m)]), default)))
            .Should().Be(StatusCodes.Status403Forbidden);
        (await Run(caller, nameof(LoansController.SetLoanTypeGradeLimited), () => controller.SetLoanTypeGradeLimited(
            f.Type.Id, new SetGradeLimitedRequest(true, ConfirmStopOffering: true), default)))
            .Should().Be(StatusCodes.Status403Forbidden);
        (await Run(caller, nameof(LoansController.SetLoanTypeOffering), () => controller.SetLoanTypeOffering(
            new SetLoanOfferingRequest(f.Company.Id, f.Type.Id, false), default)))
            .Should().Be(StatusCodes.Status403Forbidden);

        (await f.Db.LoanPolicies.AsNoTracking().AnyAsync(x => x.TenantId == f.TenantId)).Should().BeFalse();
        (await f.Db.GradeEntitlements.AsNoTracking().AnyAsync(x => x.TenantId == f.TenantId)).Should().BeFalse();
        (await f.Db.LoanTypes.AsNoTracking().SingleAsync(x => x.Id == f.Type.Id)).GradeLimited.Should().BeFalse();
    }

    private static Task<int> Run(ClaimsPrincipal caller, string action, Func<Task<IActionResult>> invoke) =>
        ProductionAuthorizationGate.StatusAsync<LoansController>(caller, action, invoke);

    private sealed record Fixture(ZayraDbContext Db, Guid TenantId, Company Company, LoanType Type, Grade Grade)
    {
        public DateOnly Today { get; } = DateOnly.FromDateTime(DateTime.UtcNow);

        public static async Task<Fixture> CreateAsync()
        {
            var (db, tenantId) = await NewTenantAsync("loan-policy-e2e");
            var company = new Company { TenantId = tenantId, LegalNameEn = "Policy Co", DefaultCurrency = "SAR" };
            var grade = new Grade { TenantId = tenantId, Code = "G1", Name = "Grade 1", Level = 1 };
            var type = new LoanType { TenantId = tenantId, Code = "PERSONAL", NameEn = "Personal", MaxInstallments = 24 };
            db.AddRange(company, grade, type);
            await db.SaveChangesAsync();
            return new Fixture(db, tenantId, company, type, grade);
        }

        public LoansController Controller(ClaimsPrincipal caller) => Bind(new LoansController(Db, new OrgScope()), caller);
    }

    private sealed class OrgScope : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) =>
            Task.FromResult(new DataScope { Level = DataScopeLevel.Organization });
    }
}
