using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Controllers;
using Zayra.Api.Controllers.Finance;
using static Zayra.Api.Tests.Security.SeededRoleBundles;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Roles named on a legacy role gate that could never pass it, because the key the resolver inferred was one
/// they do not hold. Each gate now names its key explicitly; the body checks are unchanged. Run through the
/// real authorization pipeline with the seeded bundles.
/// </summary>
public sealed class NamedButLockedGateFixTests
{
    public static TheoryData<Type, string, string[], string[]> Gates => new()
    {
        // Regularization decisions resolved to attendance.lock: the named line Manager and Supervisor were refused.
        { typeof(AttendanceController), nameof(AttendanceController.ApproveRegularization), new[] { "Employee", "Recruiter", "Payroll Officer", "HR Officer" }, new[] { "Manager", "Supervisor", "HR Manager", "HR Director", "Admin" } },
        { typeof(AttendanceController), nameof(AttendanceController.RejectRegularization), new[] { "Employee", "Recruiter", "Payroll Officer" }, new[] { "Manager", "Supervisor", "HR Manager" } },
        // "...Lifecycle" resolved to loans.policy_manage ("cycle"): HR Director, Finance and Finance Approver were refused.
        { typeof(LoansController), nameof(LoansController.RefreshLoanLifecycle), new[] { "Employee", "Manager", "Recruiter", "Payroll Officer" }, new[] { "HR Director", "Finance", "Finance Approver", "HR Manager", "Admin" } },
        { typeof(LoansController), nameof(LoansController.ReviewLoanLifecycle), new[] { "Finance", "Finance Approver", "Manager", "Payroll Manager" }, new[] { "HR Director", "HR Manager", "Admin" } },
        // The whole-tenant people export (owner decision): employees.write only. Compliance Officer, which reached it
        // through the inferred employees.documents, is closed; Payroll Officer and Auditor are dropped from the list.
        { typeof(EmployeesController), nameof(EmployeesController.Export), new[] { "Compliance Officer", "Payroll Officer", "Auditor", "Manager", "Recruiter" }, new[] { "HR Officer", "HR Manager", "HR Director", "Admin" } },
        // Compliance Officer keeps People Search.
        { typeof(EmployeesController), nameof(EmployeesController.Search), new[] { "Employee" }, new[] { "Compliance Officer", "HR Officer" } },
    };

    [Theory]
    [MemberData(nameof(Gates))]
    public async Task TheNamedAudiencePasses_AndUnintendedRolesStillGet403(Type controller, string action, string[] denied, string[] allowed)
    {
        var (db, tenantId) = await NewTenantAsync("named-locked");
        var gate = typeof(ProductionAuthorizationGate).GetMethod(nameof(ProductionAuthorizationGate.StatusAsync))!.MakeGenericMethod(controller);
        async Task<int> StatusFor(string role) =>
            await (Task<int>)gate.Invoke(null, new object[]
            {
                await CallerAsync(db, tenantId, role), action, (Func<Task<IActionResult>>)(() => Task.FromResult<IActionResult>(new OkResult())),
            })!;

        foreach (var role in denied)
            (await StatusFor(role)).Should().Be(StatusCodes.Status403Forbidden, $"{role} must not reach {controller.Name}.{action}");
        foreach (var role in allowed)
            (await StatusFor(role)).Should().Be(StatusCodes.Status200OK, $"{role} is the intended audience of {controller.Name}.{action}");
    }
}
