namespace Zayra.Api.Infrastructure.Employees;

/// <summary>
/// Thrown when an employee cannot be activated because required readiness items are missing.
///
/// STANDALONE exception — deliberately NOT a subclass of InvalidOperationException. The employee
/// controllers catch InvalidOperationException → 400/422-generic at several sites; if this derived
/// from it, the structured body would be swallowed into a generic message. Its catch block must be
/// placed BEFORE the InvalidOperationException catch at each site so `this.NotActivatable(ex)` renders
/// the structured 422 employee_not_activatable contract.
/// </summary>
public sealed class EmployeeActivationBlockedException : System.Exception
{
    public int EmployeeId { get; }
    public EmployeeReadiness Readiness { get; }
    public ResolvedReadinessPolicy Policy { get; }

    public EmployeeActivationBlockedException(int employeeId, EmployeeReadiness readiness, ResolvedReadinessPolicy policy)
        : base($"Cannot activate this employee — {readiness.Blocking.Count} required detail(s) missing.")
    {
        EmployeeId = employeeId;
        Readiness = readiness;
        Policy = policy;
    }
}
