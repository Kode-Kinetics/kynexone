using Microsoft.Extensions.Configuration;
using Xunit;
using Zayra.Api.Infrastructure.Attendance;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.SelfieAttendance;

/// <summary>The demo exception's configuration fails closed, and its retention stamp only ever shortens a selfie's life.</summary>
public sealed class SelfieDemoExceptionTests
{
    private static readonly DateTime Now = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);

    private static SelfieDemoExceptionOptions Bind(params (string Key, string? Value)[] values) => SelfieDemoExceptionOptions.From(
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => "SelfieDemoException:" + v.Key, v => v.Value)).Build());

    [Fact]
    public void TheRenderYamlValues_GrantEvostel_UntilTheExpiry_AndNobodyElse()
    {
        var options = Bind(("TenantSlugs:0", "evostel"), ("ExpiresUtc", "2026-10-22T23:59:59Z"), ("EvidenceRetentionDays", "7"),
            ("ApprovedBy", "owner 2026-10-08 (demo, test data only)"));

        var grant = options.GrantFor("Evostel", Now);
        Assert.NotNull(grant);
        Assert.Equal(new DateTime(2026, 10, 22, 23, 59, 59, DateTimeKind.Utc), grant!.ExpiresUtc);
        Assert.Equal(TimeSpan.FromDays(7), grant.EvidenceRetention);
        Assert.Null(options.GrantFor("zayra", Now));
        Assert.Null(options.GrantFor("evostel", new DateTime(2026, 10, 22, 23, 59, 59, DateTimeKind.Utc)));
    }

    [Theory]
    [InlineData(null, "owner", "7")]          // no expiry
    [InlineData("2026-10-22T23:59:59Z", null, "7")]  // no approval recorded
    [InlineData("2026-10-22T23:59:59Z", "owner", "0")]
    [InlineData("2026-10-22T23:59:59Z", "owner", "31")]
    public void AnIncompleteOrOverlongException_DoesNothing(string? expires, string? approvedBy, string days)
    {
        var options = Bind(("TenantSlugs:0", "evostel"), ("ExpiresUtc", expires), ("ApprovedBy", approvedBy), ("EvidenceRetentionDays", days));
        Assert.False(options.IsActive(Now));
        Assert.Null(options.GrantFor("evostel", Now));
    }

    [Fact]
    public void NoConfigurationAtAll_IsOff() => Assert.False(SelfieDemoExceptionOptions.From(null).IsActive(Now));

    [Fact]
    public void TheStamp_ShortensAUsedSelfie_ButNeverLengthensAnyRule()
    {
        var created = Now;
        var stamp = created.AddDays(7);
        var workDate = DateOnly.FromDateTime(created);
        // Used: 120 days normally, 7 with the stamp.
        Assert.Equal(stamp, SelfieEvidenceRetention.DueAtUtc(created, created, workDate, null, purgeDueOverrideUtc: stamp));
        Assert.Equal(created.Date.AddDays(120), SelfieEvidenceRetention.DueAtUtc(created, created, workDate, null));
        // Unused (24 h) and unfinished (1 h) keep their shorter rules.
        Assert.Equal(created.AddHours(24), SelfieEvidenceRetention.DueAtUtc(created, null, null, null, purgeDueOverrideUtc: stamp));
        Assert.Equal(created.AddHours(1), SelfieEvidenceRetention.DueAtUtc(created, null, null, null, AttendanceEvidencePurgeStates.Pending, purgeDueOverrideUtc: stamp));
    }

    // A typo in a manual "end it early" edit must never stop the API: the binder threw at startup on a malformed date
    // or number; From() now reads by hand, switches the exception OFF and reports why.
    [Theory]
    [InlineData("22/10/2026", "7")]
    [InlineData("next friday", "7")]
    [InlineData("2026-10-22T23:59:59Z", "7 days")]
    public void AMalformedValue_SwitchesItOff_AndNeverThrows(string expires, string days)
    {
        var options = Bind(("TenantSlugs:0", "evostel"), ("ExpiresUtc", expires), ("EvidenceRetentionDays", days), ("ApprovedBy", "owner"));
        Assert.False(options.IsActive(Now));
        Assert.Null(options.GrantFor("evostel", Now));
        Assert.False(string.IsNullOrWhiteSpace(options.ConfigError));
    }

    [Fact]
    public void ANoticeInArabic_CountsTheDaysGrammatically()
    {
        Assert.Equal("يوم واحد", SelfieDemoExceptionGrant.DaysAr(1));
        Assert.Equal("يومين", SelfieDemoExceptionGrant.DaysAr(2));
        Assert.Equal("7 أيام", SelfieDemoExceptionGrant.DaysAr(7));
        Assert.Equal("14 يومًا", SelfieDemoExceptionGrant.DaysAr(14));
        var grant = new SelfieDemoExceptionGrant("evostel", Now.AddDays(1), 7, "owner");
        Assert.Contains("7 أيام", grant.NoticeAr);
    }
}
