using System.Text.RegularExpressions;
using FluentAssertions;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// No personal data in log calls. Logs leave the database's access controls, retention rules and
/// tenant isolation behind (console, Render's log store, and an OTLP collector once one is set), so an
/// email, phone, IBAN, iqama/national id, passport number, name or message body written to a log is a
/// copy nobody governs. Log the record's id instead (UserId, EmployeeId, DeliveryId).
///
/// <para>Scans every <c>Log{Level}(…)</c>, <c>Log(LogLevel, …)</c> and <c>BeginScope(…)</c> call and
/// every <c>[LoggerMessage]</c> method in Zayra.Api, multi-line calls included:</para>
/// <list type="bullet">
/// <item>the message template must be a plain literal — an interpolated <c>$"…"</c> template bakes the
/// value into the message text, where no structured-logging redaction can reach it and every distinct
/// value becomes a distinct template;</item>
/// <item>no argument (and no interpolation hole) may name personal data. Identifiers are matched
/// case-insensitively as SUBSTRINGS, so <c>userEmail</c>, <c>NationalIdNumber</c> and
/// <c>mobileNo</c> are caught. Literal text is blanked first, so a template may still say "email".</item>
/// </list>
///
/// <para>Offences that predate the tightening are grandfathered in <see cref="Baseline"/> with a reason.
/// The baseline only shrinks: each entry must still be an offence.</para>
/// </summary>
public sealed partial class PersonalDataLoggingRatchetTests
{
    /// <summary>
    /// "file: offence" for log calls that predate the tightened scan (2026-10-05). Never add to this list.
    /// </summary>
    /// <remarks>Empty: when the scan was tightened, Zayra.Api had no offence left to grandfather.</remarks>
    private static readonly Dictionary<string, string> Baseline = new(StringComparer.Ordinal);

    /// <summary>Case-insensitive substrings of an identifier that make it personal data.</summary>
    private static readonly string[] PersonalDataSubstrings =
    [
        "email", "iban", "phone", "mobile", "nationalid", "iqama", "passport",
        "fullname", "firstname", "lastname", "accountnumber", "dateofbirth", "birthdate", "htmlbody", "toaddress",
    ];

    /// <summary>Whole identifiers (case-insensitive) that are personal data but too short to match as substrings.</summary>
    private static readonly HashSet<string> PersonalDataWords = new(StringComparer.OrdinalIgnoreCase) { "body", "msisdn" };

    [Fact]
    public void NoLogCallPassesPersonalDataOrAnInterpolatedTemplate()
    {
        var found = new List<(string Key, string Display)>();
        foreach (var (relative, code) in SourceScan.Files(SourceScan.ResolveApiRoot(), "*.cs"))
        {
            if (relative.StartsWith("Migrations/", StringComparison.Ordinal)) continue;
            foreach (var (line, offence) in Offences(code))
                found.Add(($"{relative}: {offence}", $"{relative}:{line}: {offence}"));
        }

        var offenders = found.Where(f => !Baseline.ContainsKey(f.Key)).Select(f => f.Display).ToList();
        offenders.Should().BeEmpty(
            "log the record's id, never the person's email, phone, IBAN, national id, name or a message body, "
            + "and pass values as structured arguments to a literal template, never as $\"…\".\n  "
            + string.Join("\n  ", offenders));

        var stale = Baseline.Keys.Except(found.Select(f => f.Key)).ToList();
        stale.Should().BeEmpty("the grandfather list may only shrink; remove entries that no longer apply");
        Baseline.Where(b => string.IsNullOrWhiteSpace(b.Value)).Should().BeEmpty("every grandfathered entry records a reason");
    }

    [Fact]
    public void TheScanActuallyReachesTheLogCalls()
    {
        var calls = SourceScan.Files(SourceScan.ResolveApiRoot(), "*.cs")
            .Where(f => !f.Relative.StartsWith("Migrations/", StringComparison.Ordinal))
            .Sum(f => LogCall().Matches(f.Code).Count);
        calls.Should().BeGreaterThan(100, "the ratchet must be reading the real Zayra.Api log calls");
    }

    [Fact]
    public void ScannerCatchesPersonalDataArguments_AndIgnoresTemplatesAndIds()
    {
        Offences("""_log.LogWarning("Login failed for {Email}", request.Email);""").Should().ContainSingle();
        Offences("""
            _log.LogInformation(
                "Sent to {To}",
                employee.WorkEmail);
            """).Should().ContainSingle();
        Offences("""log.LogError("Qiwa failed: {Body}", body);""").Should().ContainSingle();
        Offences("""logger.LogInformation("seeded owner {Email}", email);""").Should().ContainSingle();
        Offences("""log.LogError(ex, "bank {Iban}", profile.Iban);""").Should().ContainSingle();

        Offences("""_log.LogWarning("Email not sent for user {UserId}", user.Id);""").Should().BeEmpty();
        Offences("""_log.LogInformation("Email {MessageId} accepted", messageId);""").Should().BeEmpty();
        Offences("""_log.LogError("Qiwa failed: {Error}", OAuthErrorCode(body));""").Should().BeEmpty();
        Offences("""_log.LogInformation("Mask applied {Masked}", MaskEmail(user.Email));""").Should().BeEmpty();
        Offences("""_log.LogDebug("Rendering {Field}", nameof(Employee.PersonalEmail));""").Should().BeEmpty();
    }

    /// <summary>The independent review of PR #172 found each of these passing the first scanner.</summary>
    [Fact]
    public void ScannerCatchesEveryPatternTheReviewFound()
    {
        // An interpolated template, at every call shape.
        Offences("""_log.LogWarning($"Login failed for {request.Email}");""").Select(o => o.Offence)
            .Should().Contain(o => o.Contains("interpolated")).And.Contain(o => o.Contains("Email"));
        Offences("""_log.LogWarning($"Retry {attempt} of {max}");""").Should().ContainSingle(o => o.Offence.Contains("interpolated"),
            "an interpolated template is an offence even with no personal data in it");
        Offences("""_log.LogError(ex, $"Sync {runId} failed");""").Should().ContainSingle(o => o.Offence.Contains("interpolated"));
        Offences("""_log.Log(LogLevel.Warning, $"Sync {runId} failed");""").Should().ContainSingle(o => o.Offence.Contains("interpolated"));
        Offences("""_log.Log(LogLevel.Warning, new EventId(4), $@"Sync {runId} failed");""").Should().ContainSingle(o => o.Offence.Contains("interpolated"));
        Offences("""using var scope = _log.BeginScope($"Import {batchId}");""").Should().ContainSingle(o => o.Offence.Contains("interpolated"));

        // Personal-data names matched case-insensitively as substrings.
        Offences("""_log.LogInformation("Invited {User}", userEmail);""").Should().ContainSingle(o => o.Offence.Contains("userEmail"));
        Offences("""_log.LogInformation("Verified {Id}", employee.NationalIdNumber);""").Should().ContainSingle(o => o.Offence.Contains("NationalIdNumber"));
        Offences("""_log.LogInformation("OTP to {To}", mobileNo);""").Should().ContainSingle();
        Offences("""_log.LogInformation("Scan {P}", PASSPORT_NO);""").Should().ContainSingle();
        Offences("""_log.LogInformation("Iqama {I}", req.iqamaNumber);""").Should().ContainSingle();
        Offences("""_log.Log(LogLevel.Information, "Sent {To}", recipientPhone);""").Should().ContainSingle();
        Offences("""using (_log.BeginScope(new Dictionary<string, object> { ["Email"] = user.Email })) { }""").Should().ContainSingle();
        Offences("""_log.LogInformation("Done {Count}", $"{user.Email}");""").Should().ContainSingle(o => o.Offence.Contains("Email"),
            "a value smuggled through an interpolated ARGUMENT is still read");

        // [LoggerMessage] source-generated methods: parameter names and template placeholders.
        Offences("""
            [LoggerMessage(EventId = 7, Level = LogLevel.Warning, Message = "Login failed for {UserEmail}")]
            private static partial void LoginFailed(ILogger logger, string userEmail);
            """).Should().HaveCount(2);
        Offences("""
            [LoggerMessage(LogLevel.Information, "Invite sent to user {UserId}")]
            static partial void InviteSent(ILogger logger, Guid userId);
            """).Should().BeEmpty();

        // Not log calls, and ids, stay clean.
        Offences("""_log.LogInformation("Sent {DeliveryId} via {Channel}", delivery.Id, "Email");""").Should().BeEmpty();
        Offences("""var x = Math.Log(phoneCount);""").Should().BeEmpty("Math.Log is not a logger");
    }

    // ── Scanner ───────────────────────────────────────────────────────────────

    private static List<(int Line, string Offence)> Offences(string code)
    {
        var offences = new List<(int, string)>();
        foreach (Match m in LogCall().Matches(code))
        {
            var open = m.Index + m.Length - 1;
            var close = SourceScan.MatchingClose(code, open, '(', ')');
            var args = SourceScan.SplitTopLevel(code[(open + 1)..close]);
            var line = SourceScan.LineOf(code, m.Index);
            var kind = m.Groups["kind"].Value;

            // .Log(...) only counts when its first argument is a log level; Math.Log(x) is not a logger.
            if (kind == "Log" && (args.Count < 2 || !args[0].Contains("level", StringComparison.OrdinalIgnoreCase))) continue;

            var template = args.FirstOrDefault(IsStringLiteral);
            if (template is not null && IsInterpolated(template))
                offences.Add((line, $"{kind}: interpolated template"));

            foreach (var hit in PersonalDataIn(string.Join(",", args)))
                offences.Add((line, $"{kind}: {hit}"));
        }

        foreach (Match m in LoggerMessageMethod().Matches(code))
        {
            var line = SourceScan.LineOf(code, m.Index);
            foreach (Match placeholder in Placeholder().Matches(m.Groups["attr"].Value))
                if (IsPersonalData(placeholder.Groups["name"].Value))
                    offences.Add((line, $"[LoggerMessage] {m.Groups["method"].Value}: placeholder {{{placeholder.Groups["name"].Value}}}"));
            foreach (var hit in PersonalDataIn(m.Groups["params"].Value))
                offences.Add((line, $"[LoggerMessage] {m.Groups["method"].Value}: parameter {hit}"));
        }
        return offences;
    }

    private static bool IsStringLiteral(string arg) => arg.TrimStart('$', '@').StartsWith('"');

    private static bool IsInterpolated(string literal) => literal[..literal.IndexOf('"')].Contains('$');

    private static IEnumerable<string> PersonalDataIn(string args)
    {
        var bare = SourceScan.BlankLiteralsKeepHoles(args);
        // A value passed through a sanitiser (OAuthErrorCode, Mask*, Redact*, nameof) is what gets logged.
        bare = SanitiserCall().Replace(bare, "_");
        return Identifier().Matches(bare)
            .Select(id => id.Value)
            .Where(IsPersonalData)
            .Distinct(StringComparer.Ordinal);
    }

    private static bool IsPersonalData(string identifier)
    {
        if (PersonalDataWords.Contains(identifier)) return true;
        var lower = identifier.ToLowerInvariant();
        return PersonalDataSubstrings.Any(lower.Contains);
    }

    [GeneratedRegex(@"\b(?:OAuthErrorCode|Mask\w*|Redact\w*|nameof)\s*\([^()]*\)")]
    private static partial Regex SanitiserCall();

    [GeneratedRegex(@"\.(?<kind>Log(?:Trace|Debug|Information|Warning|Error|Critical)?|BeginScope)\s*(?:<[^>()]*>)?\s*\(")]
    private static partial Regex LogCall();

    /// <summary>An identifier that is not a method being called (Mask*/nameof are replaced first).</summary>
    [GeneratedRegex(@"(?<!\w)[A-Za-z_]\w*\b(?!\s*\()")]
    private static partial Regex Identifier();

    [GeneratedRegex(@"\[\s*LoggerMessage(?:Attribute)?\s*\((?<attr>(?:[^()""]|""(?:\\.|[^""\\])*""|\([^()]*\))*)\)\s*\]\s*(?:(?:public|private|internal|protected|static|partial)\s+)*\w+\s+(?<method>\w+)\s*\((?<params>[^)]*)\)")]
    private static partial Regex LoggerMessageMethod();

    [GeneratedRegex(@"\{(?<name>[A-Za-z_@]\w*)(?:[,:][^}]*)?\}")]
    private static partial Regex Placeholder();
}
