using System.Text.RegularExpressions;
using FluentAssertions;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// No personal data in log calls. Logs leave the database's access controls, retention rules and
/// tenant isolation behind (console, Render's log store, and an OTLP collector once one is set), so an
/// email, phone, IBAN, iqama/national id, passport number, name or message body written to a log is a
/// copy nobody governs. Log the record's id instead (UserId, EmployeeId, DeliveryId).
///
/// <para>Scans the ARGUMENTS of every <c>Log{Level}(…)</c> call in Zayra.Api (string literals blanked,
/// so a template may still say "email"). Catches multi-line calls.</para>
/// </summary>
public sealed partial class PersonalDataLoggingRatchetTests
{
    [Fact]
    public void NoLogCallPassesPersonalData()
    {
        var offenders = new List<string>();
        foreach (var (relative, code) in SourceScan.Files(SourceScan.ResolveApiRoot(), "*.cs"))
        {
            if (relative.StartsWith("Migrations/", StringComparison.Ordinal)) continue;
            foreach (var (line, hits) in Offences(code))
                offenders.Add($"{relative}:{line}: {string.Join(", ", hits)}");
        }

        offenders.Should().BeEmpty(
            "log the record's id, never the person's email, phone, IBAN, national id, name or a message body.\n  "
            + string.Join("\n  ", offenders));
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
    }

    private static IEnumerable<(int Line, List<string> Hits)> Offences(string code)
    {
        foreach (Match m in LogCall().Matches(code))
        {
            var open = m.Index + m.Length - 1;
            var args = StringLiteral().Replace(BalancedArguments(code, open), "\"\"");
            // A value passed through a sanitiser (OAuthErrorCode, Mask*, Redact*) is what gets logged.
            args = SanitiserCall().Replace(args, "_");
            var hits = PersonalData().Matches(args).Select(h => h.Value).Distinct().ToList();
            if (hits.Count > 0) yield return (SourceScan.LineOf(code, m.Index), hits);
        }
    }

    private static string BalancedArguments(string text, int open)
    {
        var depth = 0;
        var inString = false;
        for (var i = open; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"' && (i == 0 || text[i - 1] != '\\')) inString = !inString;
            if (inString) continue;
            if (c == '(') depth++;
            else if (c == ')' && --depth == 0) return text[(open + 1)..i];
        }
        return text[(open + 1)..];
    }

    [GeneratedRegex(@"\b(?:OAuthErrorCode|Mask\w*|Redact\w*)\s*\([^()]*\)")]
    private static partial Regex SanitiserCall();

    [GeneratedRegex(@"\.Log(Trace|Debug|Information|Warning|Error|Critical)\s*\(")]
    private static partial Regex LogCall();

    [GeneratedRegex(@"@?\$?""(?:\\.|[^""\\])*""")]
    private static partial Regex StringLiteral();

    /// <summary>A member or local whose name is personal data. Ids (EmployeeId, UserId) never match.</summary>
    [GeneratedRegex(@"\b(?:[A-Za-z_]\w*\.)*(?:Email|WorkEmail|PersonalEmail|Iban|IBAN|Phone|PhoneNumber|Mobile|NationalId|IqamaNumber|Iqama|PassportNumber|FullName|FirstName|LastName|BankAccountNumber|AccountNumber|DateOfBirth|HtmlBody|toAddress|htmlBody|body|email|phone|iban|mobile|nationalId|iqama|fullName)\b(?!\s*\()")]
    private static partial Regex PersonalData();
}
