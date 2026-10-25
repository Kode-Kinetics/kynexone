namespace Zayra.Api.Infrastructure.Finance;

/// <summary>
/// Who is asking to create a journal export, and for what. The question the rule below answers is "did this person (or the
/// person who created this API key) change the cost-accounting set-up this export is built from?".
/// </summary>
/// <param name="ApiKeyCreatorUserId">The user who created the ERP API key the caller is using; null for a person's own session.
/// A key acts for its creator, so the creator's changes bar the key as well (AM-14).</param>
public sealed record JournalExportSodRequest(
    Guid TenantId, Guid? CompanyId, string? Period, Guid? PayrollRunId, Guid ActorUserId, Guid? ApiKeyCreatorUserId);

/// <summary>
/// SEAM for contract AM-14 / S-004. A user who changed a mapping, a functional class, the cost-accounting switch, the company
/// default cost centre, an override or the small-cell minimum for a company in a period may not create a journal export for
/// that company and period. The rule and its data belong to the cost-accounting work (WP-B <c>CostAccountingSod</c>); the
/// export door only asks. When no implementation is registered the controller performs no check, so adding the registration is
/// the entire integration:
/// <code>services.AddScoped&lt;IJournalExportSeparationOfDuties, ...&gt;()</code>
/// </summary>
public interface IJournalExportSeparationOfDuties
{
    /// <summary>True when the actor (or the key's creator) is barred from creating this export. Must not throw for an unknown scope.</summary>
    Task<bool> IsBarredAsync(JournalExportSodRequest request, CancellationToken ct);
}
