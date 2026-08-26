using FluentAssertions;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// G3 precondition — PII must not reach a log sink.
///
/// <para>D5: <c>SmtpEmailService</c> logged the full recipient address and the message subject at
/// Information level on every send, while the same wave was masking that destination in the delivery
/// ledger. The database was scrubbed; the log was not. That is the worst place for it to survive:
/// centralised logs are retained, indexed, and shipped to third parties, and are far harder to purge
/// than a table.</para>
///
/// <para>This lint exists because G3 is about to add real log sinks. Instrumenting a system that
/// leaks PII into logs means the first thing observability does is centralise the leak. The scan is
/// deliberately narrow — a raw recipient/destination identifier interpolated into a log call —
/// rather than a general PII heuristic, so it stays actionable instead of noisy.</para>
/// </summary>
public class PiiLoggingLintTests
{
    /// <summary>Log-call arguments that name a raw recipient identifier rather than a masked one.</summary>
    private static readonly string[] ForbiddenLogArguments =
    [
        "toAddress", "destinationRaw", "DestinationRaw", "recipientEmail", "phoneNumber", "iban", "Iban",
    ];

    private static string ResolveApiRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 6; i++)
        {
            if (dir?.Parent is null) break;
            dir = dir.Parent;
            var candidate = Path.Combine(dir.FullName, "Zayra.Api");
            if (Directory.Exists(candidate)) return candidate;
        }
        // Same rule as the other guards: an unresolvable root means this check verifies NOTHING,
        // which must be a build failure rather than a silent pass.
        throw new InvalidOperationException(
            "The Zayra.Api source root could not be resolved, so this guard would check NOTHING. " +
            "Treat an unresolvable path as a build failure, never a skip.");
    }

    [Fact]
    public void NoLogCallMayPassARawRecipientOrBankIdentifier()
    {
        var apiRoot = ResolveApiRoot();

        var violations = new List<string>();

        foreach (var file in Directory.EnumerateFiles(apiRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                if (!line.Contains(".Log", StringComparison.Ordinal)) continue;
                // A masked value is the approved form and may legitimately mention the raw variable
                // as the argument being masked.
                if (line.Contains("Mask", StringComparison.OrdinalIgnoreCase)) continue;

                foreach (var arg in ForbiddenLogArguments)
                {
                    // Match the identifier as a log ARGUMENT (", toAddress" / "(toAddress"), not as a
                    // substring of some longer name.
                    if (line.Contains($", {arg}", StringComparison.Ordinal) ||
                        line.Contains($"({arg}", StringComparison.Ordinal))
                    {
                        violations.Add($"  {Path.GetRelativePath(apiRoot, file)}:{i + 1} — logs '{arg}'");
                        break;
                    }
                }
            }
        }

        violations.Should().BeEmpty(
            "a recipient address, phone number or IBAN in a log is PII in the hardest place to purge it. " +
            "Mask it (NotificationBodyPolicy.MaskEmail / MaskTail) or log an id instead.\n\n" +
            string.Join("\n", violations));
    }
}
