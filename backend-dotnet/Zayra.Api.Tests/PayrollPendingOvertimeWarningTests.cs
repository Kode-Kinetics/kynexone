using FluentAssertions;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>OVT-04: overtime still awaiting approval for a period being closed is surfaced (warning, never a block).</summary>
public class PayrollPendingOvertimeWarningTests
{
    private static PayrollValidationContext Ctx(IReadOnlyDictionary<int, int>? pending)
    {
        var run = new PayrollRun { TenantId = Guid.NewGuid(), Year = 2026, Month = 6 };
        var emp = new Employee { Id = 1, EmployeeCode = "E1", FullName = "A" };
        var ctx = new PayrollValidationContext(run, [], [emp], [], [], [], [], null);
        return pending is null ? ctx : ctx with { PendingOvertimeRequestsByEmployee = pending };
    }

    [Fact]
    public void PendingOvertime_RaisesAWarning_NeverAnError()
    {
        var hits = PayrollValidationEngine.Run(Ctx(new Dictionary<int, int> { [1] = 2 }))
            .Where(r => r.Code == PayrollValidationEngine.OvertimePendingApproval).ToList();
        var hit = hits.Should().ContainSingle().Subject;
        hit.Severity.Should().Be("Warning");
        hit.EmployeeId.Should().Be(1);
        hit.Message.Should().Contain("2").And.Contain("2026-06");
    }

    [Fact]
    public void NoPendingOvertime_RaisesNothing()
    {
        PayrollValidationEngine.Run(Ctx(null)).Should().NotContain(r => r.Code == PayrollValidationEngine.OvertimePendingApproval);
        PayrollValidationEngine.Run(Ctx(new Dictionary<int, int>())).Should().NotContain(r => r.Code == PayrollValidationEngine.OvertimePendingApproval);
    }
}
