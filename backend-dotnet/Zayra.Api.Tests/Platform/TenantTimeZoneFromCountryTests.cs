using FluentAssertions;
using Zayra.Api.Application.Common;

namespace Zayra.Api.Tests.Platform;

/// <summary>
/// A tenant's timezone must follow its home jurisdiction, because writing a
/// <c>TenantLocalizationSetting</c> row at all is not neutral.
///
/// <para>THE INCIDENT. Requiring a home country meant creating that row at tenant creation, where
/// before there was none. The entity defaults <c>DefaultTimezone</c> to <b>America/New_York</b>, and
/// the resolver's fallback for a missing row is UTC — so adding the row silently moved every new
/// tenant's "today" from UTC to New York. For a GCC customer that is wrong by 7-11 hours, and it
/// showed up at once: an approved leave request starting today was not live on the day it started,
/// because in New York the tenant's day had not begun. The Browser Pilot caught it as
/// "/leave rendered 0 data row(s)".</para>
///
/// <para>The lesson worth keeping: a column default is a decision, and it takes effect the moment
/// someone starts writing the row.</para>
/// </summary>
public class TenantTimeZoneFromCountryTests
{
    [Theory]
    [InlineData("SA", "Asia/Riyadh")]
    [InlineData("AE", "Asia/Dubai")]
    [InlineData("QA", "Asia/Qatar")]
    [InlineData("KW", "Asia/Kuwait")]
    [InlineData("BH", "Asia/Bahrain")]
    [InlineData("OM", "Asia/Muscat")]
    public void EveryGccJurisdiction_GetsItsOwnZone(string iso2, string expected) =>
        HomeJurisdiction.TimeZoneFor(iso2).Should().Be(expected);

    /// <summary>ISO-3 and lower case are the same jurisdiction — normalisation is shared.</summary>
    [Theory]
    [InlineData("SAU")]
    [InlineData("sa")]
    public void TheCountryCodeIsNormalisedFirst(string given) =>
        HomeJurisdiction.TimeZoneFor(given).Should().Be("Asia/Riyadh");

    /// <summary>
    /// An unknown country resolves to UTC, never to the entity's America/New_York default and never
    /// to a guess. UTC is wrong by a known, uniform amount; a guessed zone is wrong unpredictably.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Atlantis")]
    [InlineData("ZZ")]
    public void AnUnknownJurisdiction_FallsBackToUtc_NotToTheEntityDefault(string? given)
    {
        HomeJurisdiction.TimeZoneFor(given).Should().Be("UTC");
        HomeJurisdiction.TimeZoneFor(given).Should().NotBe("America/New_York");
    }

    /// <summary>Every zone named here must exist on the host, or the resolver silently returns UTC.</summary>
    [Theory]
    [InlineData("SA")] [InlineData("AE")] [InlineData("QA")] [InlineData("KW")]
    [InlineData("BH")] [InlineData("OM")] [InlineData("EG")] [InlineData("IN")]
    [InlineData("GB")] [InlineData("US")]
    public void EveryZoneNamed_IsResolvableOnThisHost(string iso2)
    {
        var id = HomeJurisdiction.TimeZoneFor(iso2);
        var act = () => TimeZoneInfo.FindSystemTimeZoneById(id);
        act.Should().NotThrow($"{iso2} maps to '{id}', which must be a real IANA zone");
    }
}
