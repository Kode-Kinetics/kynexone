using FluentAssertions;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// WAVE 1 B1 — the architecture guard behind the authoritative resolver.
///
/// <para>Centralising scope resolution is worth nothing if the next feature quietly parses the claims
/// again. That is exactly how the repository ended up with three divergent resolutions in the first
/// place: each was locally reasonable, and nothing noticed they disagreed.</para>
///
/// <para>This is a ratchet, not a style rule. It fails when entity-scope claims are interpreted outside
/// the small set of files allowed to do so — the resolver itself, the claim value object, and the
/// token-issuance path that WRITES the claims.</para>
/// </summary>
public class EntityScopeResolutionRatchetTests
{
    /// <summary>
    /// Files permitted to interpret scope claims directly, and why. Adding to this list is a
    /// deliberate act that shows up in review — which is the point.
    /// </summary>
    private static readonly Dictionary<string, string> Approved = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Infrastructure/Scope/RequestEntityScopeResolver.cs"] = "THE resolver — the one place this is decided.",
        ["Application/Common/EntityScopeContext.cs"] = "The claim value object and its legacy parser.",
        ["Application/Common/EntityScopeClaims.cs"] = "Token issuance — WRITES the claims.",
        ["Infrastructure/Auth/AuthService.cs"] = "Token issuance — builds the scope descriptor at login.",
        ["Infrastructure/Auth/JwtTokenService.cs"] = "Token issuance — stamps the claims onto the token.",

        // TenantSessionSecurity is the ONE consumer that is deliberately not a consumer of the
        // resolver's answer, and the distinction matters. Every other caller asks "what may this
        // request see?" and must get the narrowed, header-aware decision. This one asks a different
        // question: "does the scope this TOKEN claims still match the grants in the database?" — and
        // it must therefore read the token's own, UN-narrowed scope. Routing it through
        // IRequestEntityScopeResolver would compare the DB grants against a scope already narrowed by
        // X-Company-Id, so a user whose entity-access grants had been REVOKED would still pass
        // revalidation for as long as they sent a header selecting one company they had kept — which
        // is precisely the stale-session escalation this check exists to close. It reads the claims
        // with strictMode:true, compares against EntityScopeClaims.Resolve(...) over live grants, and
        // returns a boolean; it never hands a scope to anything downstream.
        //
        // The narrow shape of this exception is the reason it is safe: if this file ever starts
        // USING the parsed scope to decide what data to read or write, it no longer belongs here and
        // must move onto the resolver.
        ["Infrastructure/Auth/TenantSessionSecurity.cs"] =
            "Session revalidation — compares the TOKEN's un-narrowed scope against live DB grants; "
            + "the resolver's header-narrowed answer would let a revoked grant pass revalidation.",
    };

    /// <summary>Markers that indicate a file is interpreting scope claims rather than being handed a decision.</summary>
    private static readonly string[] ResolutionMarkers =
    {
        "EntityScopeContext.FromClaims",
        "FindFirst(EntityScopeContext.V2ClaimType)",
        "FindAll(\"entity_access\")",
        "HasClaim(\"is_group_scope\"",
        "HasClaim(EntityScopeContext.StrictScopeClaim",
    };

    [Fact]
    public void NoNewCodeParsesEntityScopeClaimsOutsideTheResolver()
    {
        var apiRoot = FindApiRoot();
        var offenders = new List<string>();

        foreach (var file in Directory.GetFiles(apiRoot, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(apiRoot, file).Replace('\\', '/');
            if (relative.StartsWith("Migrations/", StringComparison.OrdinalIgnoreCase)) continue;
            if (Approved.Keys.Any(a => relative.Equals(a, StringComparison.OrdinalIgnoreCase))) continue;

            // Scan CODE, not prose. A doc comment explaining what the old call used to do would
            // otherwise trip this — which teaches the next engineer to reword the comment rather than
            // fix the call, and a guard that is worked around is worse than no guard.
            var text = StripComments(File.ReadAllText(file));
            foreach (var marker in ResolutionMarkers)
                if (text.Contains(marker, StringComparison.Ordinal))
                    offenders.Add($"{relative} → {marker}");
        }

        offenders.Should().BeEmpty(
            "entity scope must be resolved ONLY by IRequestEntityScopeResolver. Three independent "
            + "resolutions is how a controller came to authorize against a wider scope than the "
            + "database would serve. Inject IRequestEntityScopeResolver (or use "
            + "ControllerBase.GetRequestScope()) instead of reading the claims again.\n"
            + string.Join("\n", offenders));
    }

    /// <summary>
    /// The guard is only meaningful if it is actually looking at the source tree. An empty scan would
    /// pass silently, which is the failure mode that made the Wave 0 batch-route ratchet useless.
    /// </summary>
    [Fact]
    public void TheGuardIsActuallyScanningSource()
    {
        var apiRoot = FindApiRoot();
        Directory.GetFiles(apiRoot, "*.cs", SearchOption.AllDirectories)
            .Length.Should().BeGreaterThan(200, "the scan must be reading the real API source tree");

        // ...and the markers it looks for must still exist somewhere approved, or they have been
        // renamed and this guard has silently stopped guarding anything.
        var resolver = File.ReadAllText(Path.Combine(apiRoot, "Infrastructure/Scope/RequestEntityScopeResolver.cs"));
        resolver.Should().Contain("FindFirst(EntityScopeContext.V2ClaimType)",
            "if this marker moved, every other marker in the list is suspect too");
    }

    private static string FindApiRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Zayra.Api", "Controllers")))
            dir = dir.Parent;
        dir.Should().NotBeNull("the test must be able to locate the Zayra.Api source tree");
        return Path.Combine(dir!.FullName, "Zayra.Api");
    }

    /// <summary>
    /// Removes <c>//</c> line comments and <c>/* */</c> blocks. Deliberately simple: it does not
    /// understand string literals, which is safe here because a false NEGATIVE would require a marker
    /// like "EntityScopeContext.FromClaims" to appear inside a string after a comment opener — and if
    /// that ever happens the "guard is actually scanning" test below still holds the line.
    /// </summary>
    private static string StripComments(string source)
    {
        var withoutBlocks = System.Text.RegularExpressions.Regex.Replace(
            source, @"/\*.*?\*/", string.Empty, System.Text.RegularExpressions.RegexOptions.Singleline);
        var lines = withoutBlocks.Split('\n')
            .Select(l =>
            {
                var idx = l.IndexOf("//", StringComparison.Ordinal);
                return idx >= 0 ? l[..idx] : l;
            });
        return string.Join('\n', lines);
    }
}
