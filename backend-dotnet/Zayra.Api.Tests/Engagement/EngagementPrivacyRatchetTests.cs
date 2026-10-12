using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Zayra.Api.Controllers.Engagement;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;
using Zayra.Api.Tests.Security;

namespace Zayra.Api.Tests.Engagement;

/// <summary>
/// ENG-05 ratchets — build failures, not reviews, keep the anonymous side anonymous (REGISTER §3.2.2–3).
///
/// <para>(a) The response and answer entities may never gain a property that identifies the respondent or pins the
/// exact time: no CreatedBy/UpdatedBy/UserId/…EmployeeId/…Invitation…/Token, and no DateTime at all
/// (ZayraDbContext.SaveChangesAsync stamps CreatedAtUtc/CreatedBy BY NAME). The one int that names a person —
/// <c>CohortManagerEmployeeId</c>, the respondent's LINE MANAGER as a team attribute — is allow-listed by name with
/// its reason, and it has no foreign key.</para>
/// <para>(b) The submit path writes no log line, no audit row and no notification; no engagement log call anywhere
/// names a user, employee, response, invitation or token; and no engagement route carries a response id or token.</para>
/// </summary>
public sealed class EngagementPrivacyRatchetTests
{
    private static readonly Type[] AnonymousSide = [typeof(EngagementSurveyResponse), typeof(EngagementSurveyAnswer)];

    private static readonly Dictionary<string, string> AllowedPersonColumns = new(StringComparer.Ordinal)
    {
        [nameof(EngagementSurveyResponse.CohortManagerEmployeeId)] =
            "The respondent's line manager at submission — a TEAM attribute, never the respondent. No FK. Used only to " +
            "cut a manager's own view to their team, and every slice it forms is suppressed like any other.",
    };

    private static readonly Regex Forbidden = new(
        @"^(CreatedBy|UpdatedBy|CreatedAtUtc|UpdatedAtUtc|DeletedBy|.*UserId|.*EmployeeId|.*Invitation.*|.*Token.*|.*Email.*|.*Name|.*Ip.*Address.*|.*UserAgent.*)$",
        RegexOptions.IgnoreCase);

    [Fact]
    public void ResponseAndAnswerEntities_HaveNoIdentityOrTimestampProperty()
    {
        var offenders = new List<string>();
        foreach (var type in AnonymousSide)
        {
            foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (Forbidden.IsMatch(p.Name) && !AllowedPersonColumns.ContainsKey(p.Name))
                    offenders.Add($"{type.Name}.{p.Name}");
                var t = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
                if (t == typeof(DateTime) || t == typeof(DateTimeOffset) || t == typeof(TimeOnly))
                    offenders.Add($"{type.Name}.{p.Name} ({t.Name} — only a DateOnly submission date is allowed)");
            }
        }
        offenders.Should().BeEmpty("survey responses and answers must not carry identity or an exact time (REGISTER §3.2.2)");
    }

    [Fact]
    public void TheRatchetCatchesWhatItIsFor()
    {
        foreach (var name in new[] { "CreatedBy", "UpdatedBy", "CreatedAtUtc", "UserId", "EmployeeId", "RespondentEmployeeId", "InvitationId", "SessionTokenHash", "FullName" })
            Forbidden.IsMatch(name).Should().BeTrue(name);
        foreach (var name in new[] { "SurveyId", "ResponseId", "DepartmentId", "GradeBand", "TenureBand", "SubmittedOn", "QuestionKey" })
            Forbidden.IsMatch(name).Should().BeFalse(name);
    }

    [Fact]
    public void TheModelHasNoForeignKeyFromTheAnonymousSideToAPersonOrTheLedger()
    {
        using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql("Host=unused").Options);
        foreach (var type in AnonymousSide)
        {
            var entity = db.Model.FindEntityType(type)!;
            var targets = entity.GetForeignKeys().Select(fk => fk.PrincipalEntityType.ClrType).ToList();
            targets.Should().NotContain(typeof(Employee));
            targets.Should().NotContain(typeof(User));
            targets.Should().NotContain(typeof(EngagementSurveyInvitation));
            targets.Should().OnlyContain(t => t == typeof(EngagementSurvey) || t == typeof(EngagementSurveyResponse),
                "{0} may reference only its survey (and an answer its response)", type.Name);
        }
        // The ledger's only pointer at a response is the identified link, and a CHECK limits it to Identified mode.
        var designModel = db.GetService<Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel>().Model;
        var invitation = designModel.FindEntityType(typeof(EngagementSurveyInvitation))!;
        invitation.GetCheckConstraints().Select(c => c.Name)
            .Should().Contain("ck_survey_invitations__identified_link_only_when_identified");
    }

    private static readonly string[] SilentFiles =
    [
        "Controllers/Engagement/EssSurveysController.cs",
        "Infrastructure/Engagement/EngagementResponseService.cs",
    ];

    [Fact]
    public void TheSubmitPath_WritesNoLogNoAuditAndNoNotification()
    {
        var root = SourceScan.ResolveApiRoot();
        var files = SourceScan.Files(root, "*.cs").Where(f => SilentFiles.Contains(f.Relative)).ToList();
        files.Should().HaveCount(SilentFiles.Length, "the ratchet must find the files it guards");
        foreach (var (relative, code) in files)
        {
            var text = SourceScan.BlankLiterals(code);
            Regex.IsMatch(text, @"\bILogger\b|\.Log(?:Trace|Debug|Information|Warning|Error|Critical)?\s*\(|BeginScope\s*\(|Console\.")
                .Should().BeFalse($"{relative} is on the response path and must not log");
            Regex.IsMatch(text, @"\bIAuditService\b|\bAuditLogs\b|\.WriteAsync\s*\(")
                .Should().BeFalse($"{relative} must not audit anything per response");
            Regex.IsMatch(text, @"\bINotificationService\b|\bEnqueueAsync\s*\(|\bNotifyAsync\s*\(")
                .Should().BeFalse($"{relative} must not notify anyone about a response");
        }
    }

    [Fact]
    public void NoEngagementLogCall_NamesAPersonAResponseOrAToken()
    {
        var root = SourceScan.ResolveApiRoot();
        var leak = new Regex(@"\b\w*(?:userid|employeeid|responseid|invitationid|token|respondent)\w*\b", RegexOptions.IgnoreCase);
        var call = new Regex(@"\.(?:Log(?:Trace|Debug|Information|Warning|Error|Critical)?|BeginScope)\s*\(");
        var offences = new List<string>();
        foreach (var (relative, code) in SourceScan.Files(root, "*.cs")
                     .Where(f => f.Relative.Contains("/Engagement/", StringComparison.Ordinal) || f.Relative.Contains("EngagementFreeText", StringComparison.Ordinal)))
        {
            foreach (Match m in call.Matches(code))
            {
                var open = m.Index + m.Length - 1;
                var args = code[(open + 1)..SourceScan.MatchingClose(code, open, '(', ')')];
                if (leak.IsMatch(SourceScan.BlankLiterals(args))) offences.Add($"{relative}:{SourceScan.LineOf(code, m.Index)}");
            }
        }
        offences.Should().BeEmpty("engagement code never logs who someone is, what they answered or their session token");
    }

    [Fact]
    public void NoEngagementRoute_CarriesAResponseIdOrAToken()
    {
        var routes = typeof(EngagementController).Assembly.GetTypes()
            .Where(t => t.Namespace == typeof(EngagementController).Namespace && typeof(ControllerBase).IsAssignableFrom(t))
            .SelectMany(t => t.GetMethods().SelectMany(m => m.GetCustomAttributes<Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute>())
                .Select(a => a.Template ?? string.Empty))
            .ToList();
        routes.Should().NotBeEmpty();
        routes.Should().NotContain(r => Regex.IsMatch(r, "response|token|invitation", RegexOptions.IgnoreCase)
                                        && !r.Equals("surveys/{id:guid}/identified-responses", StringComparison.Ordinal),
            "a response id or a session token in a URL lands in request logs and traces");
    }
}
