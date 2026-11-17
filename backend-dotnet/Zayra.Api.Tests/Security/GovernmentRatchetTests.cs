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

    /// <summary>
    /// The folders whose code talks, or will talk, to a government or bank body: the government and Qiwa/Jawazat folders, and the
    /// bank API client and channel folders (<c>Infrastructure/Payroll/SaudiBankExports/Api</c> and <c>.../Channels</c>), which do not
    /// exist on this branch yet: the classifier names them so the bank client is caught the day it merges.
    /// </summary>
    internal static readonly string[] GovernmentFolders =
    [
        "Infrastructure/Government/", "Infrastructure/Qiwa/", "Infrastructure/Jawazat/",
        "Infrastructure/Payroll/SaudiBankExports/Api/", "Infrastructure/Payroll/SaudiBankExports/Channels/",
    ];

    internal static bool IsGovernmentFolder(string relative) =>
        GovernmentFolders.Any(folder => relative.StartsWith(folder, StringComparison.Ordinal));

    [Theory]
    [InlineData("Infrastructure/Payroll/SaudiBankExports/Api/AnbPayrollApiClient.cs", true)]
    [InlineData("Infrastructure/Payroll/SaudiBankExports/Channels/BankPayrollChannel.cs", true)]
    [InlineData("Infrastructure/Government/Credentials/GovernmentCredentialStore.cs", true)]
    [InlineData("Infrastructure/Qiwa/LiveQiwaApiAdapter.cs", true)]
    [InlineData("Infrastructure/Payroll/SaudiBankExports/SaudiBankExportService.cs", false)]
    public void TheGovernmentFolders_IncludeTheBankClientFolders(string relative, bool expected) =>
        IsGovernmentFolder(relative).Should().Be(expected);

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

    /// <summary>
    /// The ONE file that may hold its own HttpClient (CTO decision, wave 2 plan section 11): the bank payroll client. It builds that
    /// client only around the guarded handler (the bank handler factory starts from the foundation's guarded socket factory, as
    /// GovernmentHttp.CreateHandler does) and refuses a handler that follows redirects. May only shrink; never add a second file.
    /// </summary>
    private const string OwnHttpClientAllowed = "Infrastructure/Payroll/SaudiBankExports/Api/AnbPayrollApiClient.cs";
    private const string BankHandlerFactoryFile = "Infrastructure/Payroll/SaudiBankExports/Api/AnbHttpHandlerFactory.cs";

    [Fact]
    public void A_GovernmentHttp_IsTheOnlyWayToABody()
    {
        var offenders = new List<string>();
        var allowedSeen = new HashSet<string>(StringComparer.Ordinal);
        var scanned = 0;
        string? ownClientCode = null;
        foreach (var (relative, code) in Source())
        {
            if (relative == GovernmentHttpFile) continue;
            if (relative == OwnHttpClientAllowed) ownClientCode = code;
            else if (IsGovernmentFolder(relative))
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
        // The one own-HttpClient file: still present (else remove the allowance), one client only, built around the injected handler
        // after the redirect refusal, and that handler's factory starts from the guarded socket factory with redirects off.
        ownClientCode.Should().NotBeNull("an allowance for a file that no longer exists must be removed");
        Regex.Matches(ownClientCode!, @"\bnew\s+HttpClient\s*\(").Should().ContainSingle("the bank client builds exactly one HttpClient");
        Regex.IsMatch(ownClientCode!, @"\bnew\s+HttpClient\s*\(\s*handler\s*,").Should().BeTrue("around the injected guarded handler only");
        ownClientCode!.IndexOf("FollowsRedirects(handler)", StringComparison.Ordinal).Should()
            .BeInRange(0, ownClientCode.IndexOf("new HttpClient(", StringComparison.Ordinal), "a redirect-following handler is refused first");
        Regex.IsMatch(ownClientCode, @"\bCreateClient\s*\(").Should().BeFalse("no other client is made there");
        var factory = Source().Single(f => f.Relative == BankHandlerFactoryFile).Code;
        factory.Should().MatchRegex(@"(?:SsrfGuard\.CreateGuardedClientHandler|GovernmentHttp\.CreateHandler)\s*\(\s*\)", "the bank handler starts from the guarded factory");
        factory.Should().Contain("AllowAutoRedirect = false");
        // The bank client as it is written on its own branch would be caught here at integration.
        var sibling = "var client = new HttpClient(handler); using var resp = await client.SendAsync(req, ct);";
        (IsGovernmentFolder("Infrastructure/Payroll/SaudiBankExports/Api/AnbPayrollApiClient.cs") && RawHttpCall().IsMatch(sibling)).Should().BeTrue();
        // And the registration uses the guarded handler.
        Source().Single(f => f.Relative == GovernmentHttpFile).Code.Should().Contain(".ConfigurePrimaryHttpMessageHandler(CreateHandler)");
        RawHttpCall().IsMatch("var c = _factory.CreateClient(\"qiwa\"); await c.PostAsync(path, form, ct);").Should().BeTrue();
        RawHttpCall().IsMatch("await store.IsUsableAsync(t, null, body, env, ct);").Should().BeFalse();
    }

    // ── (b) no "synced", "connected" or "accepted" outside a live path ───────────────────────────────

    [GeneratedRegex(@"(?:(?<![=!<>])=(?!=)|\?|:)\s*(?<state>QiwaSyncStatuses\.Synced|QiwaSyncLogStatuses\.Success|QiwaConnectionStatuses\.Connected|GovernmentExchangeStates\.(?:Accepted|Initiated|Validated|InProgress|Completed|Rejected))\b")]
    private static partial Regex LiveClaimWrite();

    /// <summary>
    /// A claim is guarded only when it is the TRUE branch of a ternary whose condition is the adapter's live flag
    /// (<c>x.IsLiveIntegration</c>, <c>IsLive</c>) or a local assigned from it in the same file. The word "live" nearby is not a
    /// guard (comments are stripped before this runs, and a string or an unrelated name proves nothing).
    /// </summary>
    internal static List<string> UnguardedLiveClaims(string code)
    {
        var liveLocals = Regex.Matches(code, @"\bvar\s+(?<name>\w+)\s*=\s*[\w.?]*\b(?:IsLiveIntegration|IsLive)\b\s*;")
            .Select(m => m.Groups["name"].Value).ToHashSet(StringComparer.Ordinal);
        var offences = new List<string>();
        foreach (Match m in LiveClaimWrite().Matches(code))
        {
            var before = code[..m.Groups["state"].Index].TrimEnd();
            var condition = Regex.Match(before, @"(?<cond>[\w.?!]+)\s*\?$");
            var guarded = before.EndsWith('?') && condition.Success
                && (Regex.IsMatch(condition.Groups["cond"].Value, @"\b(?:IsLiveIntegration|IsLive)$") || liveLocals.Contains(condition.Groups["cond"].Value));
            if (!guarded) offences.Add($"{SourceScan.LineOf(code, m.Index)}: {m.Groups["state"].Value}");
        }
        return offences;
    }

    [Fact]
    public void B_NothingClaimsSyncedOrAccepted_OutsideALivePath()
    {
        var offenders = new List<string>();
        var guarded = 0;
        foreach (var (relative, code) in Source())
        {
            if (relative is "Infrastructure/Government/GovernmentExchangeRecorder.cs") continue; // writes only what the caller asked, under RequireHonestState
            var claims = LiveClaimWrite().Matches(code).Count;
            var unguarded = UnguardedLiveClaims(code);
            guarded += claims - unguarded.Count;
            offenders.AddRange(unguarded.Select(o => $"{relative}:{o}"));
        }

        guarded.Should().BeGreaterThan(0, "the Qiwa worker's live-only writes must be seen, or this guard is inert");
        offenders.Should().BeEmpty("only a live adapter may make a synced, connected or accepted claim; write it as the true branch of a "
            + "condition on the adapter's IsLiveIntegration (or record the attempt through GovernmentExchangeRecorder, which enforces it).\n  "
            + string.Join("\n  ", offenders));

        UnguardedLiveClaims("var live = _adapter.IsLiveIntegration; employee.QiwaSyncStatus = live ? QiwaSyncStatuses.Synced : QiwaSyncStatuses.Simulated;")
            .Should().BeEmpty();
        UnguardedLiveClaims("var deliver = true; /* live */ employee.QiwaSyncStatus = deliver ? QiwaSyncStatuses.Synced : QiwaSyncStatuses.Simulated;")
            .Should().ContainSingle("a condition that is not the live flag is no guard");
        UnguardedLiveClaims("var label = \"live\"; employee.QiwaSyncStatus = QiwaSyncStatuses.Synced;").Should().ContainSingle();
        UnguardedLiveClaims("log.Status = live ? QiwaSyncLogStatuses.Simulated : QiwaSyncLogStatuses.Success;").Should().ContainSingle("the FALSE branch is not live");
        UnguardedLiveClaims("if (x.Status == QiwaSyncLogStatuses.Success) {}").Should().BeEmpty();
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
        // A request that carries a secret prints none of it (a logged action argument cannot leak it).
        var secretRequests = Api.GetTypes().Where(t => t.Name.StartsWith("Government", StringComparison.Ordinal) && t.Name.EndsWith("Request", StringComparison.Ordinal)
            && t.GetProperties().Any(p => SecretShapedName().IsMatch(p.Name))).ToList();
        secretRequests.Should().Contain(typeof(Zayra.Api.Controllers.GovernmentCredentialSaveRequest));
        foreach (var type in secretRequests)
            type.GetMethod(nameof(ToString), Type.EmptyTypes)!.DeclaringType.Should().Be(type, $"{type.Name} must override ToString to withhold its secret");

        controllers.Where(f => f.Code.Contains("UnsealedGovernmentCredential", StringComparison.Ordinal) || f.Code.Contains(".Ciphertext", StringComparison.Ordinal)
                || Regex.IsMatch(f.Code, @"\bOpenAsync\s*\(") && f.Code.Contains("GovernmentCredentialStore", StringComparison.Ordinal))
            .Select(f => f.Relative).Should().BeEmpty("no controller may open a government credential or touch its ciphertext");
    }

    // ── (d) the ledger is written only by the recorder, and only with a staged audit ──────────────────

    [GeneratedRegex(@"GovernmentExchanges\s*\.\s*(?:Add|AddRange|Update|UpdateRange|Remove|RemoveRange|Attach|ExecuteUpdate\w*|ExecuteDelete\w*)\b|Set\s*<\s*GovernmentExchange\s*>|government_exchanges|new\s+GovernmentExchange\s*[({]|\.(?:Add|AddAsync|AddRange|Update|Attach|Remove|Entry)\s*\(\s*(?:\(\s*GovernmentExchange\s*\)|\w*[Ee]xchange\w*\s*[),])")]
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
        foreach (var sample in new[] { "db.Add(new GovernmentExchange { State = x });", "db.Update(exchange);", "db.Entry(exchange).State = EntityState.Modified;",
                     "db.Set<GovernmentExchange>().Add(row);", "db.GovernmentExchanges.ExecuteUpdateAsync(s => s);" })
            LedgerWrite().IsMatch(sample).Should().BeTrue(sample);
        LedgerWrite().IsMatch("var rows = await db.GovernmentExchanges.AsNoTracking().ToListAsync(ct);").Should().BeFalse();

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
            .Where(f => IsGovernmentFolder(f.Relative) && !f.Relative.StartsWith("Infrastructure/Qiwa/", StringComparison.Ordinal)
                && !f.Relative.StartsWith("Infrastructure/Jawazat/", StringComparison.Ordinal))
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

    // ── (i) decisions are made only by the policy; every write goes through the write-checked path ─────────

    [GeneratedRegex(@"new\s+GovernmentLiveDecision\s*\(")]
    private static partial Regex DecisionConstruction();

    [GeneratedRegex(@"HttpMethod\.(?:Post|Put|Delete|Patch)\b|new\s+HttpMethod\s*\(")]
    private static partial Regex WriteMethod();

    /// <summary>Write-method uses that are not an argument of an <c>ExchangeAsync(</c> call (GovernmentHttp refuses those without a write decision).</summary>
    internal static List<string> WritesOutsideTheCheckedPath(string code)
    {
        var offences = new List<string>();
        foreach (Match m in WriteMethod().Matches(code))
        {
            var start = Math.Max(0, m.Index - 400);
            var window = code[start..m.Index];
            var call = window.LastIndexOf("ExchangeAsync(", StringComparison.Ordinal);
            if (call < 0 || window[call..].Contains(';')) offences.Add($"{SourceScan.LineOf(code, m.Index)}: {m.Value}");
        }
        return offences;
    }

    /// <summary>
    /// The one file that may send its own writes (the bank client's payroll and token POSTs), and the one file that may build that
    /// client and its channel: the gated factory, which wraps them in <c>GatedBankPayrollChannel</c> (live-write decision plus a
    /// committed intent before any call). The allowance holds only while nothing else in the app builds the client or the channel.
    /// </summary>
    private const string BankClientWritesAllowed = OwnHttpClientAllowed;
    private const string GatedBankFactoryFile = "Infrastructure/Payroll/SaudiBankExports/Api/AnbGatedChannelFactory.cs";
    private const string GatedBankChannelFile = "Infrastructure/Payroll/SaudiBankExports/Channels/GatedBankPayrollChannel.cs";

    [GeneratedRegex(@"new\s+(?:[\w.]+\.)?(?:AnbPayrollApiClient|AnbPayrollChannel)\s*\("
        + @"|\b(?:AnbPayrollApiClient|AnbPayrollChannel)\s+\w+\s*=\s*new\s*\("
        + @"|typeof\s*\(\s*(?:[\w.]+\.)?(?:AnbPayrollApiClient|AnbPayrollChannel)\s*\)"
        + @"|\b(?:Add(?:Singleton|Scoped|Transient|HttpClient)|CreateInstance|GetService|GetRequiredService|GetServices)\s*<[^>;]*\b(?:AnbPayrollApiClient|AnbPayrollChannel)\b")]
    private static partial Regex BankClientConstruction();

    /// <summary>Places in <paramref name="code"/> that build, register or resolve the ANB client or channel.</summary>
    internal static List<string> BankClientConstructions(string code) =>
        BankClientConstruction().Matches(code).Select(m => $"{SourceScan.LineOf(code, m.Index)}: {m.Value.Trim()}").ToList();

    [Theory]
    [InlineData("var c = new AnbPayrollApiClient(handler, cfg, creds, log);", true)]
    [InlineData("var ch = new Zayra.Api.Infrastructure.Payroll.SaudiBankExports.Api.AnbPayrollChannel(c);", true)]
    [InlineData("AnbPayrollApiClient c = new(handler, cfg, creds, log);", true)]
    [InlineData("services.AddSingleton<AnbPayrollChannel>();", true)]
    [InlineData("services.AddScoped<IBankPayrollChannel, AnbPayrollChannel>();", true)]
    [InlineData("ActivatorUtilities.CreateInstance<AnbPayrollApiClient>(sp, handler);", true)]
    [InlineData("var t = typeof(AnbPayrollChannel);", true)]
    [InlineData("public AnbPayrollChannel(AnbPayrollApiClient client) => _client = client;", false)]
    [InlineData("ILogger<AnbPayrollApiClient> log", false)]
    public void TheBankClientConstructionScan_FindsEveryWayToBuildOne(string code, bool found) =>
        BankClientConstructions(code).Should().HaveCount(found ? 1 : 0);

    [Fact]
    public void I_OnlyThePolicyMakesDecisions_AndEveryGovernmentWriteGoesThroughTheWriteCheck()
    {
        var made = Source().Where(f => f.Relative != "Infrastructure/Government/GovernmentLivePolicy.cs")
            .SelectMany(f => DecisionConstruction().Matches(f.Code).Select(m => $"{f.Relative}:{SourceScan.LineOf(f.Code, m.Index)}")).ToList();
        made.Should().BeEmpty("a GovernmentLiveDecision is made only by GovernmentLivePolicy (its constructor is internal for that reason)");

        var writes = Source().Where(f => IsGovernmentFolder(f.Relative) && f.Relative != GovernmentHttpFile && f.Relative != BankClientWritesAllowed)
            .SelectMany(f => WritesOutsideTheCheckedPath(f.Code).Select(o => $"{f.Relative}:{o}")).ToList();
        writes.Should().BeEmpty("every POST, PUT, PATCH or DELETE to a body goes through GovernmentHttp.ExchangeAsync, which refuses it without "
            + "a live-write decision (four gates plus the legal clearance).\n  " + string.Join("\n  ", writes));

        // The bank client's own writes are allowed only behind the gated channel: the allowed file still sends them (else drop the
        // allowance), and NOTHING but the gated factory builds, registers or resolves the client or its channel.
        var files = Source().ToList();
        WritesOutsideTheCheckedPath(files.Single(f => f.Relative == BankClientWritesAllowed).Code).Should().NotBeEmpty(
            "an allowance for a file that no longer writes must be removed");
        var stray = files.Where(f => f.Relative != GatedBankFactoryFile)
            .SelectMany(f => BankClientConstructions(f.Code).Select(o => $"{f.Relative}:{o}")).ToList();
        stray.Should().BeEmpty("the ANB client and channel are built only by the gated factory, so every call passes the live-write "
            + "decision and the committed intent first.\n  " + string.Join("\n  ", stray));
        var factory = files.Single(f => f.Relative == GatedBankFactoryFile).Code;
        BankClientConstructions(factory).Should().NotBeEmpty("the factory is where the client is built");
        factory.Should().Contain("new GatedBankPayrollChannel(", "and it hands out only the gated channel");
        Regex.IsMatch(factory, @"public\s+static\s+(?:GatedBankPayrollChannel|IBankPayrollChannel)\s+Create\s*\(").Should().BeTrue();
        Regex.Matches(factory, @"\bpublic\b").Count.Should().Be(2, "one public factory type with one public method: no other way out");
        var gated = files.Single(f => f.Relative == GatedBankChannelFile).Code;
        gated.Should().Contain("decision.AllowsWrites", "the gated channel asks for the write grade before a submit");
        gated.Should().Contain("Intent", "and for the committed intent");
        var http = Source().Single(f => f.Relative == GovernmentHttpFile).Code;
        http.Should().Contain("!IsRead(method) && (!decision.AllowsWrites || decision.Environment is null)", "the transport itself refuses a write without the write grade");
        http.Should().Contain("requireIntent: !IsRead(method)", "and a write without a committed intent");

        var receipts = Source().Where(f => f.Relative != "Infrastructure/Government/GovernmentExchangeRecorder.cs")
            .SelectMany(f => Regex.Matches(f.Code, @"new\s+GovernmentExchangeIntentReceipt\s*\(").Select(m => $"{f.Relative}:{SourceScan.LineOf(f.Code, m.Index)}")).ToList();
        receipts.Should().BeEmpty("only the recorder, after its own commit, makes an intent receipt");
        typeof(GovernmentExchangeIntentReceipt).GetConstructors().Should().BeEmpty();

        WritesOutsideTheCheckedPath("using var resp = await _http.ExchangeAsync(decision, env, HttpMethod.Post, \"/x\", body, null, ct);").Should().BeEmpty();
        WritesOutsideTheCheckedPath("var req = new HttpRequestMessage(HttpMethod.Post, uri); await client.SendAsync(req);").Should().ContainSingle();
        WritesOutsideTheCheckedPath("await _http.ExchangeAsync(d, e, HttpMethod.Get, \"/x\", null, null, ct); var m = HttpMethod.Delete;").Should().ContainSingle();
    }
}
