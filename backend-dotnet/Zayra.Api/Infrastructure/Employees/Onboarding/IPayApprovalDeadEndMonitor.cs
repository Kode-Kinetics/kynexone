namespace Zayra.Api.Infrastructure.Employees.Onboarding;

/// <summary>
/// Re-evaluates one company's pending pay packages after anything that can change who may review them
/// (a pay decision, a role or company-access change, a login being switched off). It only reads reviewer
/// eligibility and keeps one admin notification in step with the result. It never approves, rejects or
/// otherwise resolves a package, and it never throws into the caller: a failure is logged and swallowed.
/// </summary>
public interface IPayApprovalDeadEndMonitor
{
    /// <param name="tenantId">The tenant the company belongs to.</param>
    /// <param name="companyId">The company to re-evaluate.</param>
    /// <param name="trigger">Short caller label recorded in the audit row (for example "decision" or "access_change").</param>
    Task SyncCompanyAsync(Guid tenantId, Guid companyId, string trigger, CancellationToken ct);
}
