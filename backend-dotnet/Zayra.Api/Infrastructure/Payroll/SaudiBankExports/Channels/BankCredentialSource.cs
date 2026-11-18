using Zayra.Api.Infrastructure.Government;
using Zayra.Api.Infrastructure.Government.Credentials;

namespace Zayra.Api.Infrastructure.Payroll.SaudiBankExports.Channels;

/// <summary>
/// Where a bank channel gets its credential: the government credential store, nothing else. The opened credential is bound to its
/// body, environment, API host and token host (all in the store's AAD), and <see cref="GovernmentHttp"/> refuses it for any other.
/// A seam only so tests can hand in a synthetic credential.
/// </summary>
public interface IBankCredentialSource
{
    /// <summary>The scope's company credential for <paramref name="body"/> in the scope's environment (else the tenant-wide one, never a
    /// fallback from an unusable company one), or null when none is held.</summary>
    Task<UnsealedGovernmentCredential?> OpenAsync(string body, BankChannelScope scope, CancellationToken ct);
}

/// <summary>The production <see cref="IBankCredentialSource"/>: <see cref="GovernmentCredentialStore.OpenAsync"/> for the scope.</summary>
public sealed class GovernmentStoreBankCredentials(GovernmentCredentialStore store) : IBankCredentialSource
{
    public Task<UnsealedGovernmentCredential?> OpenAsync(string body, BankChannelScope scope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return store.OpenAsync(scope.TenantId, scope.CompanyId, body, scope.Environment, ct);
    }
}
