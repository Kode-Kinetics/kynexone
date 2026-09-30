using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Application.Employees;

/// <summary>
/// Resolves the two supported employee identifiers without guessing from mutable attributes such as name.
/// If both identifiers are supplied, they must identify the same employee in the same tenant.
/// </summary>
public static class EmployeeIdentityResolver
{
    public static async Task<EmployeeIdentityResolution> ResolveEmployeeAsync(
        this ZayraDbContext db,
        Guid tenantId,
        Guid? publicId,
        int? internalId,
        CancellationToken cancellationToken)
    {
        var hasPublicId = publicId.HasValue && publicId.Value != Guid.Empty;
        var hasInternalId = internalId.HasValue && internalId.Value > 0;

        if (!hasPublicId && !hasInternalId)
            return EmployeeIdentityResolution.Failure("A valid employeeId or employeeIntId is required.");

        var query = db.Employees.Where(x => x.TenantId == tenantId && !x.IsDeleted);
        if (hasPublicId) query = query.Where(x => x.PublicId == publicId!.Value);
        if (hasInternalId) query = query.Where(x => x.Id == internalId!.Value);

        var employee = await query.SingleOrDefaultAsync(cancellationToken);
        if (employee is null)
        {
            var reason = hasPublicId && hasInternalId
                ? "employeeId and employeeIntId do not identify the same employee in this tenant."
                : "Employee was not found in this tenant.";
            return EmployeeIdentityResolution.Failure(reason);
        }

        return EmployeeIdentityResolution.Success(employee);
    }

    /// <summary>
    /// Bridges pre-joining tasks to the activated employee through the immutable
    /// application -&gt; onboarding draft relationship. No mutable person attribute is consulted.
    /// </summary>
    public static async Task<int> LinkOnboardingTasksForActivatedDraftAsync(
        this ZayraDbContext db,
        Guid tenantId,
        Guid draftId,
        Employee employee,
        CancellationToken cancellationToken)
    {
        if (employee.TenantId != tenantId || employee.PublicId == Guid.Empty)
            throw new InvalidOperationException("The activated employee identity is invalid for this tenant.");

        var applications = await db.JobApplications
            .Where(x => x.TenantId == tenantId && x.OnboardingDraftId == draftId)
            .Select(x => new { x.Id, x.CompanyId })
            .ToListAsync(cancellationToken);

        if (applications.Count == 0) return 0;
        if (applications.Count > 1)
            throw new InvalidOperationException("Multiple applications reference the same onboarding draft.");

        // The hire lands in the legal entity that made the offer. Activation places the employee by
        // resolving the draft's department and branch, and a department of another entity (or a name
        // shared with one) moved the hire there — a different employer, currency and payroll. This
        // runs inside the activation transaction, so refusing here writes nothing.
        if (applications[0].CompanyId is { } offeringEntity
            && employee.CompanyId is { } hireEntity
            && hireEntity != offeringEntity)
            throw new InvalidOperationException(
                "This hire would be activated in a different legal entity from the one that made the offer. " +
                "Change the draft's department or branch to one of the offering entity's, or make the offer from the right entity.");

        var applicationId = applications[0].Id;
        var tasks = await db.OnboardingTasks
            .Where(x => x.TenantId == tenantId && x.ApplicationId == applicationId)
            .ToListAsync(cancellationToken);

        if (tasks.Any(x => x.EmployeeId.HasValue && x.EmployeeId.Value != employee.PublicId))
            throw new InvalidOperationException("An onboarding task is already linked to a different employee identity.");

        foreach (var task in tasks) task.EmployeeId = employee.PublicId;
        return tasks.Count;
    }
}

public sealed record EmployeeIdentityResolution(Employee? Employee, string? Error)
{
    public bool IsSuccess => Employee is not null;

    public static EmployeeIdentityResolution Success(Employee employee) => new(employee, null);
    public static EmployeeIdentityResolution Failure(string error) => new(null, error);
}
