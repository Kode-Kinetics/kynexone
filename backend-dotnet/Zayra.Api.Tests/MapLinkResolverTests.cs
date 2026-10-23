using System.Net;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Zayra.Api.Controllers.Geo;
using Zayra.Api.Infrastructure.Geo;

namespace Zayra.Api.Tests;

/// <summary>
/// GET /api/geo/resolve-map-link: a pasted Google Maps short link becomes coordinates. The outbound requests are
/// recorded by a fake handler, so these tests prove which hosts are contacted, not just what is returned.
/// </summary>
public class MapLinkResolverTests
{
    private const string Short = "https://maps.app.goo.gl/AbC123xyz";
    private const string Place = "https://www.google.com/maps/place/Kingdom+Centre/@24.7114,46.6744,17z/data=!3m1!4b1!4m6!3m5!1s0x0:0x0!8m2!3d24.7113741!4d46.6743887";

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return answer(request, ct);
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static HttpResponseMessage Redirect(string to) =>
        new(HttpStatusCode.Found) { Headers = { Location = new Uri(to, UriKind.RelativeOrAbsolute) } };

    private static (MapLinkResolver Resolver, FakeHandler Handler) Make(Func<HttpRequestMessage, HttpResponseMessage> answer, TimeSpan? timeout = null)
    {
        var handler = new FakeHandler((r, _) => Task.FromResult(answer(r)));
        return (new MapLinkResolver(new Factory(handler)) { Timeout = timeout ?? TimeSpan.FromSeconds(5) }, handler);
    }

    [Fact]
    public async Task AnAllowedShortLinkResolvesFromItsRedirectWithoutFetchingGoogle()
    {
        var (resolver, handler) = Make(_ => Redirect(Place));
        var found = await resolver.ResolveAsync(Short, CancellationToken.None);
        found.Should().Be(new MapCoordinates(24.7113741m, 46.6743887m), "the place's own pin (!3d!4d) wins over the viewport centre");
        handler.Requests.Should().ContainSingle();
        var request = handler.Requests[0];
        request.Method.Should().Be(HttpMethod.Get);
        request.Content.Should().BeNull("no request body is ever sent");
        request.Headers.Contains("Cookie").Should().BeFalse();
        request.RequestUri!.ToString().Should().Be(Short);
    }

    [Fact]
    public async Task AGooGlMapsLinkFollowsAGoogleMapsHopUntilItCarriesCoordinates()
    {
        var (resolver, handler) = Make(r => r.RequestUri!.Host == "goo.gl"
            ? Redirect("https://www.google.com/maps?cid=123")
            : Redirect("https://www.google.com/maps/search/21.5433,+39.1728?entry=tts"));
        var found = await resolver.ResolveAsync("https://goo.gl/maps/xYz987", CancellationToken.None);
        found.Should().Be(new MapCoordinates(21.5433m, 39.1728m));
        handler.Requests.Select(r => r.RequestUri!.Host).Should().Equal("goo.gl", "www.google.com");
    }

    [Theory]
    [InlineData("https://evil.example/maps")]
    [InlineData("http://maps.app.goo.gl/AbC123xyz")]
    [InlineData("https://maps.app.goo.gl:8443/AbC123xyz")]
    [InlineData("https://user:pass@maps.app.goo.gl/AbC123xyz")]
    [InlineData("https://goo.gl/notmaps")]
    [InlineData("https://maps.app.goo.gl.evil.example/AbC")]
    [InlineData("https://www.google.com/maps/@24.7,46.6,17z")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    [InlineData("file:///etc/passwd")]
    [InlineData("not a url")]
    [InlineData("")]
    public async Task ADisallowedHostIsNeverContacted(string link)
    {
        var (resolver, handler) = Make(_ => Redirect(Place));
        (await resolver.ResolveAsync(link, CancellationToken.None)).Should().BeNull();
        handler.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("https://evil.example/maps/@24.7,46.6,17z")]
    [InlineData("http://www.google.com/maps/@24.7,46.6,17z")]
    [InlineData("https://www.google.com.evil.example/maps/@24.7,46.6,17z")]
    [InlineData("https://www.google.com/url?q=https://evil.example")]
    [InlineData("https://maps.app.goo.gl/another")]
    [InlineData("https://127.0.0.1/maps/@24.7,46.6")]
    public async Task ARedirectOffGoogleMapsIsRefusedAndNotFollowed(string target)
    {
        var (resolver, handler) = Make(_ => Redirect(target));
        (await resolver.ResolveAsync(Short, CancellationToken.None)).Should().BeNull();
        handler.Requests.Should().ContainSingle("only the short link itself was requested");
    }

    [Fact]
    public async Task AtMostThreeRequestsAreMade()
    {
        var hop = 0;
        var (resolver, handler) = Make(_ => Redirect($"https://www.google.com/maps?step={++hop}"));
        (await resolver.ResolveAsync(Short, CancellationToken.None)).Should().BeNull();
        handler.Requests.Should().HaveCount(MapLinkResolver.MaxHops);
    }

    [Fact]
    public async Task ASlowAnswerTimesOut()
    {
        var handler = new FakeHandler(async (_, ct) => { await Task.Delay(TimeSpan.FromSeconds(30), ct); return Redirect(Place); });
        var resolver = new MapLinkResolver(new Factory(handler)) { Timeout = TimeSpan.FromMilliseconds(200) };
        var watch = System.Diagnostics.Stopwatch.StartNew();
        (await resolver.ResolveAsync(Short, CancellationToken.None)).Should().BeNull();
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ANetworkFailureIsUnreadableNotAnError()
    {
        var handler = new FakeHandler((_, _) => throw new HttpRequestException("connection refused"));
        (await new MapLinkResolver(new Factory(handler)).ResolveAsync(Short, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task ALandingPageWithNoCoordinatesIsUnreadable()
    {
        var (resolver, _) = Make(r => r.RequestUri!.Host == "maps.app.goo.gl" ? Redirect("https://www.google.com/maps/place/Somewhere") : new HttpResponseMessage(HttpStatusCode.OK));
        (await resolver.ResolveAsync(Short, CancellationToken.None)).Should().BeNull();
    }

    [Theory]
    [InlineData("https://www.google.com/maps/@24.7136,46.6753,15z", 24.7136, 46.6753)]
    [InlineData("https://www.google.com/maps?q=21.5433,39.1728", 21.5433, 39.1728)]
    [InlineData("https://www.google.com/maps?ll=26.2235,50.5876&z=12", 26.2235, 50.5876)]
    [InlineData("https://www.google.com/maps/search/?api=1&query=25.2854%2C51.5310", 25.2854, 51.5310)]
    [InlineData("https://www.google.com/maps/place/24.774265,46.738586", 24.774265, 46.738586)]
    [InlineData("https://www.google.com/maps/place/X/@24.1,46.1,17z/data=!3d24.7113741!4d46.6743887", 24.7113741, 46.6743887)]
    [InlineData("https://maps.google.com/?q=-33.8688,151.2093", -33.8688, 151.2093)]
    public void FullGoogleMapsUrlsAreParsed(string url, double lat, double lng)
    {
        MapLinkResolver.Parse(new Uri(url)).Should().Be(new MapCoordinates((decimal)lat, (decimal)lng));
    }

    [Theory]
    [InlineData("https://www.google.com/maps/place/Riyadh")]
    [InlineData("https://www.google.com/maps?q=95.1,46.2")]
    [InlineData("https://www.google.com/maps/@24.7,200.1,15z")]
    public void UrlsWithoutValidCoordinatesAreNotParsed(string url)
    {
        MapLinkResolver.Parse(new Uri(url)).Should().BeNull();
    }

    [Fact]
    public void ARedirectToMapsGoogleComIsReadButNeverRequested()
    {
        var (resolver, handler) = Make(_ => Redirect("https://maps.google.com/?q=24.7,46.6"));
        resolver.ResolveAsync(Short, CancellationToken.None).Result.Should().Be(new MapCoordinates(24.7m, 46.6m));
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task TheEndpointReturnsOnlyCoordinatesOr422()
    {
        typeof(GeoController).GetCustomAttributes(typeof(AuthorizeAttribute), true).Should().ContainSingle();
        typeof(GeoController).GetMethod(nameof(GeoController.ResolveMapLink))!.GetCustomAttributes(typeof(EnableRateLimitingAttribute), true).Should().ContainSingle();

        var ok = await Controller(_ => Redirect(Place)).ResolveMapLink(Short, CancellationToken.None);
        var body = JsonSerializer.SerializeToElement(ok.Should().BeOfType<OkObjectResult>().Subject.Value);
        body.EnumerateObject().Select(p => p.Name).Should().Equal("latitude", "longitude");
        body.GetProperty("latitude").GetDecimal().Should().Be(24.7113741m);

        var refused = await Controller(_ => Redirect("https://evil.example/")).ResolveMapLink(Short, CancellationToken.None);
        var problem = refused.Should().BeOfType<UnprocessableEntityObjectResult>().Subject;
        JsonSerializer.SerializeToElement(problem.Value).GetProperty("error").GetString().Should().Be("ERR_MAP_LINK_UNREADABLE");

        (await Controller(_ => Redirect(Place), withTenant: false).ResolveMapLink(Short, CancellationToken.None)).Should().BeOfType<UnauthorizedResult>();
    }

    private static GeoController Controller(Func<HttpRequestMessage, HttpResponseMessage> answer, bool withTenant = true)
    {
        var http = new DefaultHttpContext();
        if (withTenant) http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("tenant_id", Guid.NewGuid().ToString())], "Test"));
        return new GeoController(Make(answer).Resolver) { ControllerContext = new ControllerContext { HttpContext = http } };
    }
}
