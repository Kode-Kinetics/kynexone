using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Government;
using Zayra.Api.Infrastructure.Seed;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// The government-integration ratchets (architecture 5.7). Each scans the compiled assembly or the Zayra.Api source, and each
/// has a detection check so an empty scan can never pass by finding nothing.
/// </summary>
public sealed partial class GovernmentRatchetTests
{
    private static readonly Assembly Api = typeof(GovernmentHttp).Assembly;

    private static IEnumerable<(string Relative, string Code)> Source() =>
        SourceScan.Files(SourceScan.ResolveApiRoot(), "*.cs").Where(f => !f.Relative.StartsWith("Migrations/", StringComparison.Ordinal));

    /// <summary>The folders whose code talks, or will talk, to a government or bank body.</summary>
    private static bool IsGovernmentFolder(string relative) =>
        relative.StartsWith("Infrastructure/Government/", StringComparison.Ordinal)
        || relative.StartsWith("Infrastructure/Qiwa/", StringComparison.Ordinal)
        || relative.StartsWith("Infrastructure/Jawazat/", StringComparison.Ordinal);

    private const string GovernmentHttpFile = "Infrastructure/Government/GovernmentHttp.cs";

    // ── (a) government HTTP only through GovernmentHttp ────────────────────────────────────────────────

    [GeneratedRegex(@"\bCreateClient\s*\(|\bnew\s+HttpClient\s*\(|\.(?:Send|Post|Get|Put|Delete|Patch|GetString|GetByteArray|GetStream)Async\s*\(")]
    private static partial Regex RawHttpCall();

    [GeneratedRegex(@"qiwa\.tech|anb\.com\.sa|\belm\.sa\b|mudad\.com\.sa|takamol\.(?:sa|com)|muqeem\.sa", RegexOptions.IgnoreCase)]
    private static partial Regex GovernmentApiHost();

    /// <summary>Files that may name a body's host without calling it, each with the reason. May only shrink.</summary>
    private static readonly Dictionary<string, string> HostMentionsAllowed = new(StringComparer.Ordinal)
    {
        ["Infrastructure/Payroll/SaudiBankExports/SaudiBankExportContracts.cs"] =
            "The published specification page the ANB export format cites as its source; shown to users, never requested.",
    };

    [Fact]
    public void A_GovernmentHttp_IsTheOnlyWayToABody()
    {
        var offenders = new List<string>();
        var allowedSeen = new HashSet<string>(StringComparer.Ordinal);
        var scanned = 0;
        foreach (var (relative, code) in Source())
        {
            if (relative == GovernmentHttpFile) continue;
            if (IsGovernmentFolder(relative))
            {
                scanned++;
                foreach (Match m in RawHttpCall().Matches(code))
                    offenders.Add($"{relative}:{SourceScan.LineOf(code, m.Index)}: {m.Value.Trim()}");
            }
            // A government API host may be written down in one place only: GovernmentHttp's allow-list.
            foreach (Match m in GovernmentApiHost().Matches(code))
            {
                if (HostMentionsAllowed.ContainsKey(relative)) { allowedSeen.Add(relative); continue; }
                offenders.Add($"{relative}:{SourceScan.LineOf(code, m.Index)}: host literal {m.Value}");
            }
        }

        scanned.Should().BeGreaterThan(10, "the government folders must be found, or this guard is inert");
        offenders.Should().BeEmpty("a request to a government or bank body goes through GovernmentHttp.ExchangeAsync: allow-listed "
            + "base URL per environment, the live gate first, one send, no retry.\n  " + string.Join("\n  ", offenders));
        allowedSeen.Should().BeEquivalentTo(HostMentionsAllowed.Keys, "an allowance for a file that no longer needs it must be removed");
        RawHttpCall().IsMatch("var c = _factory.CreateClient(\"qiwa\"); await c.PostAsync(path, form, ct);").Should().BeTrue();
        RawHttpCall().IsMatch("await store.IsUsableAsync(t, null, body, env, ct);").Should().BeFalse();
    }

    // ── (b) no "synced", "connected" or "accepted" outside a live path ───────────────────────────────

    [GeneratedRegex(@"(?:(?<![=!<>])=(?!=)|\?)\s*(?<state>QiwaSyncStatuses\.Synced|QiwaSyncLogStatuses\.Success|QiwaConnectionStatuses\.Connected|GovernmentExchangeStates\.(?:Accepted|Validated|InProgress|Completed))\b")]
    private static partial Regex LiveClaimWrite();

    [GeneratedRegex(@"\blive\b|IsLiveIntegration|\bIsLive\b")]
    private static partial Regex LiveCondition();

    [Fact]
    public void B_NothingClaimsSyncedOrAccepted_OutsideALivePath()
    {
        var offenders = new List<string>();
        var guarded = 0;
        foreach (var (relative, code) in Source())
            foreach (Match m in LiveClaimWrite().Matches(code))
            {
                var window = code[Math.Max(0, m.Index - 160)..m.Index];
                if (LiveCondition().IsMatch(window)) { guarded++; continue; }
                offenders.Add($"{relative}:{SourceScan.LineOf(code, m.Index)}: {m.Groups["state"].Value}");
            }

        guarded.Should().BeGreaterThan(0, "the Qiwa worker's live-only writes must be seen, or this guard is inert");
        offenders.Should().BeEmpty("only a live adapter may make a synced, connected or accepted claim; write it under a condition on "
            + "the adapter's IsLiveIntegration (or record the attempt through GovernmentExchangeRecorder, which enforces it).\n  "
            + string.Join("\n  ", offenders));
        LiveClaimWrite().IsMatch("employee.QiwaSyncStatus = QiwaSyncStatuses.Synced;").Should().BeTrue();
        LiveClaimWrite().IsMatch("if (x.Status == QiwaSyncLogStatuses.Success)").Should().BeFalse();
    }

    // ── (c) no response type exposes a secret ──────────────────────────────────────────────────────────

    [GeneratedRegex("secret|privatekey|token|ciphertext|password", RegexOptions.IgnoreCase)]
    private static partial Regex SecretShapedName();

    [Fact]
    public void C_NoGovernmentResponseType_ExposesASecret_AndNoControllerOpensOne()
    {
        var responseTypes = Api.GetTypes()
            .Where(t => t.IsPublic && t.Namespace is { } ns
                && (ns.StartsWith("Zayra.Api.Infrastructure.Government", StringComparison.Ordinal) || ns == "Zayra.Api.Controllers")
                && Regex.IsMatch(t.Name, "(Dto|Metadata|Response|Result|Capabilities)$")
                && (t.Namespace!.StartsWith("Zayra.Api.Infrastructure.Government", StringComparison.Ordinal) || t.Name.StartsWith("Government", StringComparison.Ordinal)))
            .ToList();
        responseTypes.Should().Contain(t => t.Name == "GovernmentCredentialMetadata").And.Contain(t => t.Name == "GovernmentCapabilityDto");

        var exposed = responseTypes
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => (Type: t, Property: p)))
            .Where(x => SecretShapedName().IsMatch(x.Property.Name))
            .Select(x => $"{x.Type.Name}.{x.Property.Name}")
            .ToList();
        exposed.Should().BeEmpty("a government response carries metadata only; a secret leaves the store only inside a transport");

        var controllers = Source().Where(f => f.Relative.StartsWith("Controllers/", StringComparison.Ordinal)).ToList();
        controllers.Should().HaveCountGreaterThan(50);
        controllers.Where(f => f.Code.Contains("UnsealedGovernmentCredential", StringComparison.Ordinal) || f.Code.Contains(".Ciphertext", StringComparison.Ordinal)
                || Regex.IsMatch(f.Code, @"\bOpenAsync\s*\(") && f.Code.Contains("GovernmentCredentialStore", StringComparison.Ordinal))
            .Select(f => f.Relative).Should().BeEmpty("no controller may open a government credential or touch its ciphertext");
    }

    // ── (d) the ledger is written only by the recorder, and only with a staged audit ──────────────────

    [GeneratedRegex(@"GovernmentExchanges\s*\.\s*(?:Add|AddRange|Update|UpdateRange|Remove|RemoveRange|Attach|ExecuteUpdate\w*|ExecuteDelete\w*)\b|Set\s*<\s*GovernmentExchange\s*>|government_exchanges")]
    private static partial Regex LedgerWrite();

    [Fact]
    public void D_OnlyTheRecorderWritesTheLedger_AndItStagesItsAudit()
    {
        const string recorder = "Infrastructure/Government/GovernmentExchangeRecorder.cs";
        var offenders = Source()
            .Where(f => f.Relative != recorder && f.Relative != "Data/ZayraDbContext.cs")
            .SelectMany(f => LedgerWrite().Matches(f.Code).Select(m => $"{f.Relative}:{SourceScan.LineOf(f.Code, m.Index)}: {m.Value}"))
            .ToList();
        offenders.Should().BeEmpty("government_exchanges is written only through IGovernmentExchangeRecorder.\n  " + string.Join("\n  ", offenders));

        var code = Source().Single(f => f.Relative == recorder).Code;
        LedgerWrite().IsMatch(code).Should().BeTrue("the recorder is the writer");
        code.Should().Contain("audit.Stage(").And.NotContain(".WriteAsync(", "the ledger row and its audit row commit in one save (ratchet 96)");
    }

    // ── (e) no IBAN, ID, secret or payload in a log call under the government folders ────────────────

    [GeneratedRegex(@"\.(?<kind>Log(?:Trace|Debug|Information|Warning|Error|Critical)?|BeginScope)\s*(?:<[^>()]*>)?\s*\(")]
    private static partial Regex LogCall();

    [GeneratedRegex(@"iban|idnumber|nationalid|iqama|passport|account|secret|token|password|payload|response|request|content|preview|raw|message|name|phone|email", RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveArgument();

    internal static List<string> GovernmentLogOffences(string code)
    {
        var offences = new List<string>();
        foreach (Match m in LogCall().Matches(code))
        {
            var open = m.Index + m.Length - 1;
            var close = SourceScan.MatchingClose(code, open, '(', ')');
            var args = SourceScan.SplitTopLevel(code[(open + 1)..close]);
            var line = SourceScan.LineOf(code, m.Index);
            if (args.FirstOrDefault(a => a.TrimStart('$', '@').StartsWith('"')) is { } template && template.TrimStart('@').StartsWith('$'))
                offences.Add($"{line}: interpolated template");
            foreach (var arg in args.Where(a => !a.TrimStart('$', '@').StartsWith('"')))
            {
                var bare = SourceScan.BlankLiterals(arg);
                if (Regex.IsMatch(bare, @"^\s*(ex|e|exception)\s*$", RegexOptions.IgnoreCase)) offences.Add($"{line}: exception object ({arg.Trim()})");
                else if (SensitiveArgument().IsMatch(bare)) offences.Add($"{line}: {arg.Trim()}");
            }
        }
        return offences;
    }

    [Fact]
    public void E_GovernmentLogCalls_CarryNoPersonalDataSecretOrPayload()
    {
        var offenders = Source()
            .Where(f => f.Relative.StartsWith("Infrastructure/Government/", StringComparison.Ordinal))
            .SelectMany(f => GovernmentLogOffences(f.Code).Select(o => $"{f.Relative}:{o}"))
            .ToList();
        offenders.Should().BeEmpty("log the exchange id, body and status code only; an exception message or payload can carry an IBAN or ID.\n  "
            + string.Join("\n  ", offenders));

        GovernmentLogOffences("_log.LogWarning(\"Refused {Body} {Iban}\", body, employee.BankIban);").Should().ContainSingle();
        GovernmentLogOffences("_log.LogError(ex, \"Exchange {ExchangeId} failed\", exchangeId);").Should().ContainSingle();
        GovernmentLogOffences("_log.LogWarning($\"Refused {body}\");").Should().ContainSingle();
        GovernmentLogOffences("_log.LogInformation(\"Exchange {ExchangeId} {Status}\", exchangeId, statusCode);").Should().BeEmpty();
    }

    // ── (f) every adapter has a Disabled or Simulated twin ─────────────────────────────────────────────

    private static bool IsLive(Type concrete) =>
        ((IGovernmentAdapter)RuntimeHelpers.GetUninitializedObject(concrete)).IsLiveIntegration;

    [Fact]
    public void F_EveryGovernmentAdapterInterface_HasANonLiveTwin()
    {
        var concrete = Api.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IGovernmentAdapter).IsAssignableFrom(t)).ToList();
        var families = Api.GetTypes()
            .Where(t => t.IsInterface && typeof(IGovernmentAdapter).IsAssignableFrom(t) && t != typeof(IGovernmentAdapter) && t != typeof(IGovernmentProvider))
            .ToList();
        families.Should().Contain(typeof(Zayra.Api.Infrastructure.Qiwa.IQiwaApiAdapter)).And.Contain(typeof(IGosiProvider))
            .And.Contain(typeof(IMuqeemProvider)).And.Contain(typeof(ITakamolProvider));

        var missing = families
            .Where(f => !concrete.Any(c => f.IsAssignableFrom(c) && !IsLive(c)
                && Regex.IsMatch(c.Name, "^(Disabled|Sandbox|Simulated|Refused)")))
            .Select(f => f.Name).ToList();
        missing.Should().BeEmpty("every adapter family needs a Disabled or Simulated implementation that never claims to be live");

        concrete.Where(c => !families.Any(f => f.IsAssignableFrom(c))).Select(c => c.Name)
            .Should().BeEmpty("an adapter implements a body's family interface, not the bare marker");
        concrete.Where(c => Regex.IsMatch(c.Name, "^(Disabled|Sandbox|Simulated|Refused)") && IsLive(c)).Select(c => c.Name)
            .Should().BeEmpty("a twin never declares itself live");
        concrete.Should().Contain(c => IsLive(c), "the live Qiwa adapter must be seen, or this guard is inert");
    }

    // ── (g) the two tables are tenant-owned, company-scoped and filtered ───────────────────────────────

    [Fact]
    public void G_TheNewTables_AreTenantOwned_CompanyScoped_AndQueryFiltered()
    {
        typeof(ITenantOwned).IsAssignableFrom(typeof(Models.GovernmentCredential)).Should().BeTrue();
        typeof(ICompanyScoped).IsAssignableFrom(typeof(Models.GovernmentCredential)).Should().BeTrue();
        typeof(ITenantOwned).IsAssignableFrom(typeof(Models.GovernmentExchange)).Should().BeTrue();
        typeof(ICompanyScopedOperational).IsAssignableFrom(typeof(Models.GovernmentExchange)).Should().BeTrue();

        using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        foreach (var type in new[] { typeof(Models.GovernmentCredential), typeof(Models.GovernmentExchange) })
        {
            var entity = db.Model.FindEntityType(type)!;
            entity.GetQueryFilter().Should().NotBeNull($"{type.Name} must carry the tenant query filter");
        }
        db.Model.FindEntityType(typeof(Models.GovernmentCredential))!.GetTableName().Should().Be("government_credentials");
        db.Model.FindEntityType(typeof(Models.GovernmentExchange))!.GetTableName().Should().Be("government_exchanges");
    }

    // ── (h) the new permission is catalogued, privileged, and held by no built-in role ─────────────────

    [Fact]
    public async Task H_TheCredentialPermission_IsInTheCatalog_Privileged_AndGrantedToNoRole()
    {
        const string key = Zayra.Api.Controllers.GovernmentCredentialsController.Permission;
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Seeded roles", Slug = $"gov-roles-{tenantId:N}" });
        await db.SaveChangesAsync();
        await new AuthSeeder(db).EnsureTenantRolesAsync(tenantId);

        (await db.Permissions.AnyAsync(p => p.Key == key)).Should().BeTrue("an endpoint gated on an uncatalogued key 403s for everyone");
        var holders = await db.Roles.Where(r => r.RolePermissions.Any(rp => rp.Permission!.Key == key)).Select(r => r.Name).ToListAsync();
        holders.Should().BeEmpty("no built-in role is given government credentials; the tenant decides who, deliberately");
        AuthSeeder.AdminWithheldPermissions.Should().Contain(key);
        PrivilegedMfaPolicy.PrivilegedPermissions.Should().Contain(key);
    }
}
