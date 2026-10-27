namespace Zayra.Api.Infrastructure.Finance;

/// <summary>
/// A ledger line broke a cost-accounting rule (Required / NotAllowed / P&amp;L side / cost-centre validity), a posting
/// integrity rule (new rows need a company) or the reversal rule (a contra must mirror an existing original).
/// Thrown from inside <c>ZayraDbContext.SaveChanges*</c>, so no writer can skip it. <see cref="Code"/> is the stable
/// machine-readable error code the API layer maps to a 422.
/// </summary>
public sealed class GlCostCentreRuleException : InvalidOperationException
{
    public string Code { get; }
    public GlCostCentreRuleException(string code, string message) : base(message) => Code = code;
}
