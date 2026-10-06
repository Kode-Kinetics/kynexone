using Zayra.Api.Application.Entitlements;

namespace Zayra.Api.Infrastructure.Entitlements;

// Release A slice R2 owns this file. R0 created it as a stub (registered in DI by ReleaseAServiceCollectionExtensions)
// so no slice has to edit Program.cs; the owning slice replaces the body. Shared contracts: Application/Entitlements,
// Application/Contracts. Gated per tenant by the release_a feature flag.

/// <summary>Reads the employee package and the grade standard (<see cref="IEntitlementResolver"/>). Slice R2.</summary>
public sealed class EntitlementResolver : IEntitlementResolver
{
    public Task<EmployeePackage> ResolveAsync(Guid tenantId, int employeeId, DateOnly asOf, CancellationToken ct) =>
        throw new NotImplementedException("The package resolver arrives with Release A slice R2.");

    public Task<IReadOnlyList<GradeStandardLine>> GradeStandardAsync(Guid tenantId, Guid gradeId, Guid companyId, DateOnly asOf, CancellationToken ct) =>
        throw new NotImplementedException("The package resolver arrives with Release A slice R2.");
}
