using System.Text.RegularExpressions;
using FluentAssertions;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// RATCHET (employee-access review P1). A company's EmailDomain decides who can sign in and where a workspace-less
/// sign-in is routed, so only a security.manage holder may set it. That gate lives in OrganizationSetupService
/// (CompanyEmailDomainRules.EnsureCallerMaySetAsync) — every place that writes the field is enumerated here, so a new
/// writer cannot appear without someone deciding how it is gated.
/// </summary>
public sealed class CompanyEmailDomainWriteRatchetTests
{
    /// <summary>File → number of EmailDomain assignments it may contain, and why each is gated.</summary>
    private static readonly Dictionary<string, int> Approved = new(StringComparer.Ordinal)
    {
        // Apply(company, request): reached only from Create/UpdateCompanyAsync, both of which call EnsureCallerMaySetAsync.
        ["Infrastructure/Organization/OrganizationSetupService.cs"] = 1,
        // POST platform/tenants/{id}/companies: platform Owner/Admin only (RequirePlatformRole), plus EnsureClaimableAsync.
        ["Controllers/PlatformController.cs"] = 1,
        // CompanyRequest / CompanyDto parameter defaults, not writes.
        ["Application/Organization/OrganizationDtos.cs"] = 2,
    };

    private static readonly Regex Assignment = new(@"\bEmailDomain\s*=(?!=)", RegexOptions.Compiled);
    private static readonly Regex RawColumnWrite = new(@"\bemail_domain\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    [Fact]
    public void EveryWriterOfCompanyEmailDomain_IsKnownAndGated()
    {
        var root = SourceScan.ResolveApiRoot();
        var found = new Dictionary<string, int>(StringComparer.Ordinal);
        var rawSql = new List<string>();
        foreach (var (relative, code) in SourceScan.Files(root, "*.cs"))
        {
            if (relative.StartsWith("Migrations/", StringComparison.Ordinal) || relative.StartsWith("Data/V2/", StringComparison.Ordinal)) continue;
            var n = Assignment.Matches(code).Count;
            if (n > 0) found[relative] = n;
            if (RawColumnWrite.IsMatch(code)) rawSql.Add(relative);
        }
        found.Should().BeEquivalentTo(Approved, "a new EmailDomain writer must go through OrganizationSetupService's security.manage gate, or be added here with its reason");
        rawSql.Should().BeEmpty("no raw SQL may write companies.email_domain around the gate");

        var service = File.ReadAllText(Path.Combine(root, "Infrastructure/Organization/OrganizationSetupService.cs"));
        Regex.Matches(service, @"CompanyEmailDomainRules\.EnsureCallerMaySetAsync\(").Count.Should().Be(2, "create and update are both gated");
    }
}
