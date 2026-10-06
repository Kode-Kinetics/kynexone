using Zayra.Api.Application.Entitlements;

namespace Zayra.Api.Infrastructure.Payroll;

// Release A slice R3 owns this file. R0 created it as a stub (registered in DI by ReleaseAServiceCollectionExtensions)
// so no slice has to edit Program.cs; the owning slice replaces the body. Shared contracts: Application/Entitlements,
// Application/Contracts. Gated per tenant by the release_a feature flag.

/// <summary>Deductions statement over the existing slip lines: category, cap, basis, balance (<see cref="IDeductionStatementService"/>). Slice R3.</summary>
public sealed class DeductionStatementService : IDeductionStatementService
{
    public Task<DeductionStatement> ForSlipAsync(Guid tenantId, Guid slipId, DeductionAudience who, CancellationToken ct) =>
        throw new NotImplementedException("The deductions statement arrives with Release A slice R3.");
}
