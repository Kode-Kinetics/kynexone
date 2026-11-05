using FluentAssertions;
using Zayra.Api.Infrastructure.Auth;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Pure rules for moving one tenant's mandatory-MFA date (no database). "Follow the platform date" (no tenant date)
/// is always allowed; an explicit date is judged against the platform date and today.
/// </summary>
public sealed class PrivilegedMfaTenantDateChangeTests
{
    private static readonly DateTime Now = new(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Platform = Now.AddDays(60);
    private static readonly DateTime Past = Now.AddDays(-1);

    // name -> (requested, platform, callerIsOwner, expected status or null = allowed)
    private static (DateTime? Requested, DateTime? PlatformDate, bool Owner, int? Expected) Case(string name) => name switch
    {
        "nothing-nothing" => (null, null, false, null),
        "follow-a-past-platform-date-admin" => (null, Past, false, null),
        "follow-a-past-platform-date-owner" => (null, Past, true, null),
        "follow-a-near-platform-date-admin" => (null, Now.AddDays(1), false, null),
        "date-without-platform-date-admin" => (Now.AddDays(30), null, false, 403),
        "date-without-platform-date-owner" => (Now.AddDays(30), null, true, null),
        "owner-30-days-past-platform" => (Platform.AddDays(30), Platform, true, null),
        "owner-30-days-and-a-tick-past-platform" => (Platform.AddDays(30).AddTicks(1), Platform, true, 400),
        "admin-1-day-past-platform" => (Platform.AddDays(1), Platform, false, 403),
        "admin-exactly-7-days-out" => (Now.AddDays(7), Platform, false, null),
        "admin-a-tick-under-7-days-out" => (Now.AddDays(7).AddTicks(-1), Platform, false, 403),
        "owner-past-date" => (Past, Platform, true, null),
        "admin-past-date" => (Past, Platform, false, 403),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [InlineData("nothing-nothing")]
    [InlineData("follow-a-past-platform-date-admin")]
    [InlineData("follow-a-past-platform-date-owner")]
    [InlineData("follow-a-near-platform-date-admin")]
    [InlineData("date-without-platform-date-admin")]
    [InlineData("date-without-platform-date-owner")]
    [InlineData("owner-30-days-past-platform")]
    [InlineData("owner-30-days-and-a-tick-past-platform")]
    [InlineData("admin-1-day-past-platform")]
    [InlineData("admin-exactly-7-days-out")]
    [InlineData("admin-a-tick-under-7-days-out")]
    [InlineData("owner-past-date")]
    [InlineData("admin-past-date")]
    public void CheckTenantDateChange_FollowsTheRules(string name)
    {
        var (requested, platformDate, owner, expected) = Case(name);

        var result = PrivilegedMfaPolicy.CheckTenantDateChange(requested, platformDate, Now, owner);

        if (expected is null) result.Should().BeNull();
        else
        {
            result.Should().NotBeNull();
            result!.Value.Status.Should().Be(expected.Value);
        }
    }
}
