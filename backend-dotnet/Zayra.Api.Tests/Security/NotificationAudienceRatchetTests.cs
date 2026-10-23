using System.Text;
using FluentAssertions;
using Zayra.Api.Infrastructure.Notifications;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Broadcast RATCHET. <c>NotifyAsync(tenantId, null, ...)</c> writes a notification row with no user, and
/// <c>NotificationsController.Recent</c> shows such a row to every login in the tenant, self-service employees
/// included. That is how a colleague's leave, a candidate's interview and a new hire's activation reached the
/// whole workspace. A notice about a person goes to the people involved (<see cref="NotificationAudience"/>).
///
/// <para>This scans the API source for <c>NotifyAsync</c> calls whose user argument is the literal <c>null</c> and
/// fails when the entity argument is one of <see cref="NotificationAudience.PersonSpecificEntityTypes"/>, or is not a
/// literal at all (a wrapper that hides the entity hides what it broadcasts). A genuine workspace-wide notice
/// names a non-person entity literally (e.g. <c>nameof(ReportSchedule)</c>) and passes.</para>
///
/// <para>Counts may only go DOWN. <see cref="NotificationService"/> also refuses to write a person-specific
/// broadcast at runtime, so an approved site below sends nothing until it is routed.</para>
/// </summary>
public class NotificationAudienceRatchetTests
{
    // file (relative to Zayra.Api) -> approved number of null-user NotifyAsync calls for a person-specific entity.
    private static readonly Dictionary<string, int> Approved = new(StringComparer.Ordinal)
    {
        // Attendance correction submitted / pending HR (AttendanceRegularizationRequest). Owned by the attendance
        // work stream, which routes them to the employee and the deciders; 2 -> 0 when that lands. Until then the
        // NotificationService guard drops them rather than broadcasting.
        ["Infrastructure/Attendance/AttendanceService.cs"] = 2,
    };

    [Fact]
    public void No_new_person_specific_notice_may_be_broadcast()
    {
        var calls = ScanNullUserCalls(ResolveApiRoot(), out var scanned);
        scanned.Should().BeGreaterThan(20, "the scan must actually find the NotifyAsync call sites, or it proves nothing");

        var offenders = calls
            .Where(c => c.Entity is null || NotificationAudience.IsPersonSpecific(c.Entity))
            .GroupBy(c => c.File)
            .Where(g => !Approved.TryGetValue(g.Key, out var allowed) || g.Count() > allowed)
            .SelectMany(g => g.Select(c => $"{c.File}:{c.Line} NotifyAsync(..., null, ...) for {c.Entity ?? "an entity that is not a literal"}"))
            .ToList();

        offenders.Should().BeEmpty(
            "a notification with no user is a broadcast that every login in the tenant reads. Address it to the " +
            "people involved (NotificationAudience.NotifyEachAsync with the employee's logins and the deciders) instead.\n  " +
            string.Join("\n  ", offenders));
    }

    [Fact]
    public void The_person_specific_list_covers_the_known_personal_notices()
    {
        NotificationAudience.PersonSpecificEntityTypes.Should().Contain(
        [
            "LeaveRequest", "AttendanceRegularizationRequest", "JobApplication", "InterviewSchedule", "OfferLetter",
            "Candidate", "Employee", "EmployeeDraft", "EmployeeTransferRequest", "EmployeeDocument", "ApprovalRequest",
        ]);
        NotificationAudience.IsPersonSpecific("ReportSchedule").Should().BeFalse("a genuine workspace notice stays a broadcast");
    }

    [Fact]
    public void The_scanner_reads_the_entity_through_interpolated_messages()
    {
        const string source = """
            await _n.NotifyAsync(tenantId, null, "T", $"{a.Name}, {string.Join(", ", b)} {{x}}", "LeaveRequest", id, ct);
            await _n.NotifyAsync(tenantId, null, "T", "m", nameof(ReportSchedule), id, ct);
            await _n.NotifyAsync(tenantId, userId, "T", "m", "LeaveRequest", id, ct);
            await _n.NotifyAsync(tenantId, null, title, message, entity, entityId, ct);
            """;
        var calls = ParseCalls("X.cs", source, out var total);
        total.Should().Be(4);
        calls.Select(c => c.Entity).Should().Equal("LeaveRequest", "ReportSchedule", null);
    }

    private sealed record NullUserCall(string File, int Line, string? Entity);

    private static List<NullUserCall> ScanNullUserCalls(string apiRoot, out int scanned)
    {
        scanned = 0;
        var result = new List<NullUserCall>();
        foreach (var file in Directory.EnumerateFiles(apiRoot, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(apiRoot, file).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.StartsWith("Migrations/", StringComparison.Ordinal)
                || relative.StartsWith("bin/", StringComparison.Ordinal) || relative.StartsWith("obj/", StringComparison.Ordinal)
                || relative.Contains("/bin/", StringComparison.Ordinal) || relative.Contains("/obj/", StringComparison.Ordinal)) continue;
            result.AddRange(ParseCalls(relative, File.ReadAllText(file), out var count));
            scanned += count;
        }
        return result;
    }

    private static List<NullUserCall> ParseCalls(string file, string source, out int total)
    {
        total = 0;
        var result = new List<NullUserCall>();
        const string marker = "NotifyAsync(";
        for (var at = source.IndexOf(marker, StringComparison.Ordinal); at >= 0;
             at = source.IndexOf(marker, at + marker.Length, StringComparison.Ordinal))
        {
            var lineStart = source.LastIndexOf('\n', at) + 1;
            if (source.AsSpan(lineStart, at - lineStart).TrimStart().StartsWith("//")) continue;
            var args = Arguments(source, at + marker.Length - 1);
            if (args is null) continue;
            total++;
            if (args.Count < 5 || args[1] != "null") continue;
            var line = source.Take(at).Count(ch => ch == '\n') + 1;
            result.Add(new NullUserCall(file, line, EntityLiteral(args[4])));
        }
        return result;
    }

    /// <summary>"X" or nameof(A.X) -> X; anything else (a variable, a call) -> null.</summary>
    private static string? EntityLiteral(string arg)
    {
        arg = arg.Trim();
        if (arg.Length >= 2 && arg[0] == '"' && arg[^1] == '"' && !arg[1..^1].Contains('"')) return arg[1..^1];
        if (arg.StartsWith("nameof(", StringComparison.Ordinal) && arg.EndsWith(')'))
            return arg["nameof(".Length..^1].Split('.').Last().Trim();
        return null;
    }

    /// <summary>The top-level arguments of the call whose '(' is at <paramref name="open"/>; null when unbalanced.</summary>
    private static List<string>? Arguments(string s, int open)
    {
        var args = new List<string>();
        var current = new StringBuilder();
        var depth = 0;
        for (var i = open + 1; i < s.Length;)
        {
            var c = s[i];
            if (IsStringStart(s, i))
            {
                var end = SkipString(s, i);
                current.Append(s, i, end - i);
                i = end;
                continue;
            }
            if (c == '\'')
            {
                var end = Math.Min(s.Length, i + (i + 1 < s.Length && s[i + 1] == '\\' ? 4 : 3));
                current.Append(s, i, end - i);
                i = end;
                continue;
            }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
            {
                var eol = s.IndexOf('\n', i);
                i = eol < 0 ? s.Length : eol;
                continue;
            }
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}')
            {
                if (depth == 0)
                {
                    if (c != ')') return null;
                    args.Add(current.ToString().Trim());
                    return args;
                }
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                args.Add(current.ToString().Trim());
                current.Clear();
                i++;
                continue;
            }
            current.Append(c);
            i++;
        }
        return null;
    }

    private static bool IsStringStart(string s, int i)
    {
        while (i < s.Length && (s[i] == '$' || s[i] == '@')) i++;
        return i < s.Length && s[i] == '"';
    }

    private static int SkipString(string s, int i)
    {
        var interpolated = false;
        var verbatim = false;
        while (s[i] == '$' || s[i] == '@')
        {
            if (s[i] == '$') interpolated = true; else verbatim = true;
            i++;
        }
        i++; // opening quote
        while (i < s.Length)
        {
            var c = s[i];
            if (verbatim && c == '"')
            {
                if (i + 1 < s.Length && s[i + 1] == '"') { i += 2; continue; }
                return i + 1;
            }
            if (!verbatim && c == '\\') { i += 2; continue; }
            if (!verbatim && c == '"') return i + 1;
            if (interpolated && c == '{')
            {
                if (i + 1 < s.Length && s[i + 1] == '{') { i += 2; continue; }
                i = SkipHole(s, i + 1);
                continue;
            }
            i++;
        }
        return i;
    }

    /// <summary>Skips an interpolation hole's code (which may itself hold strings) up to its closing brace.</summary>
    private static int SkipHole(string s, int i)
    {
        var depth = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (IsStringStart(s, i)) { i = SkipString(s, i); continue; }
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']') depth--;
            else if (c == '}')
            {
                if (depth == 0) return i + 1;
                depth--;
            }
            i++;
        }
        return i;
    }

    private static string ResolveApiRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6; i++)
        {
            if (dir?.Parent is null) break;
            dir = dir.Parent;
            var candidate = Path.Combine(dir.FullName, "Zayra.Api");
            if (Directory.Exists(candidate)) return candidate;
        }
        // A guard that SKIPS when it cannot find its input is a false negative, not a safe default.
        throw new InvalidOperationException(
            $"Could not locate the Zayra.Api source root from {AppContext.BaseDirectory}. This ratchet cannot pass without scanning it.");
    }
}
