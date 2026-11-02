using System.Text.RegularExpressions;
using FluentAssertions;

namespace Zayra.Api.Tests;

/// <summary>
/// Ratchet on mail that bypasses the notification outbox (retry, lease, dead-letter). Every call to an IEmailService send
/// method outside Infrastructure/Email is listed here with a reason; the count per file may only go DOWN. A new direct send
/// fails this test: route it through NotificationService (no secrets in the body) or, if it carries a credential, through
/// DeliverResilientAsync (in-memory retry + optional idempotency key) and add it to the second list.
/// </summary>
public class DirectEmailSendRatchetTests
{
    // file (relative to Zayra.Api) -> max direct sends. Why each cannot use the outbox:
    //  ChannelDispatchers      - this IS the outbox worker's email channel
    //  SecurityNoticeQueue     - its own durable queue worker (bounded retry)
    //  OfferDispatchService    - claim/fingerprint protocol with attachment; needs the inline outcome
    //  QuoteNotifications      - platform mail with its own sender protocol and timeout
    //  ReportScheduleWorker    - attachments (report data would persist in the outbox) and its own schedule retry
    //  PlatformController      - invoice with PDF (state moves to Sent only on relay acceptance) and the interactive SMTP test
    //  SetupSettingsController - interactive SMTP test whose result is shown to the admin
    private static readonly Dictionary<string, int> DirectSends = new()
    {
        ["Infrastructure/Notifications/ChannelDispatchers.cs"] = 1,
        ["Infrastructure/Auth/SecurityNoticeQueue.cs"] = 1,
        ["Infrastructure/Recruitment/OfferDispatchService.cs"] = 1,
        ["Infrastructure/Pricing/QuoteNotifications.cs"] = 1,
        ["Infrastructure/Reports/ReportScheduleWorker.cs"] = 1,
        ["Controllers/PlatformController.cs"] = 2,
        ["Controllers/Admin/SetupSettingsController.cs"] = 1,
    };

    // Secret-bearing mail stays inline on the resilient path (the outbox would persist the credential in the body).
    private static readonly Dictionary<string, int> ResilientSends = new()
    {
        ["Infrastructure/Auth/EmployeeAccessService.cs"] = 1,
        ["Infrastructure/Auth/MfaService.cs"] = 2,
        ["Infrastructure/Auth/AuthService.cs"] = 1,
        ["Controllers/AccessController.cs"] = 2,
        ["Controllers/PlatformController.cs"] = 1,
    };

    private static readonly Regex Direct = new(@"\b(?:_?[eE]mail(?:Service)?|email)\.(?:SendAsync|DeliverAsync|SendPlatformAsync|DeliverPlatformAsync)\(", RegexOptions.Compiled);
    private static readonly Regex Resilient = new(@"\.Deliver(?:Platform)?ResilientAsync\(", RegexOptions.Compiled);

    private static string ApiDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Zayra.Api", "Controllers"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "Zayra.Api");
    }

    private static Dictionary<string, int> Count(Regex rx)
    {
        var root = ApiDir();
        var result = new Dictionary<string, int>();
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            if (rel.StartsWith("Migrations/") || rel.StartsWith("obj/") || rel.StartsWith("bin/") || rel.StartsWith("Infrastructure/Email/")) continue;
            var n = File.ReadAllLines(file).Count(l => !l.TrimStart().StartsWith("//") && rx.IsMatch(l));
            if (n > 0) result[rel] = n;
        }
        return result;
    }

    [Fact]
    public void DirectEmailSends_MayOnlyGoDown()
    {
        var actual = Count(Direct);
        var over = actual.Where(kv => kv.Value > DirectSends.GetValueOrDefault(kv.Key)).Select(kv => $"{kv.Key}: {kv.Value} > allowed {DirectSends.GetValueOrDefault(kv.Key)}").ToList();
        over.Should().BeEmpty("route new mail through the notification outbox (NotificationService), or DeliverResilientAsync when it carries a credential");
    }

    [Fact]
    public void ResilientInlineSends_MayOnlyGoDown()
    {
        var actual = Count(Resilient);
        var over = actual.Where(kv => kv.Value > ResilientSends.GetValueOrDefault(kv.Key)).Select(kv => $"{kv.Key}: {kv.Value} > allowed {ResilientSends.GetValueOrDefault(kv.Key)}").ToList();
        over.Should().BeEmpty();
    }
}
