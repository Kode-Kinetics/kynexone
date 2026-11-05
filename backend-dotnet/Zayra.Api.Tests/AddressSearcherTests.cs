using System.Net;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Zayra.Api.Controllers.Geo;
using Zayra.Api.Infrastructure.Geo;

namespace Zayra.Api.Tests;

/// <summary>GET /api/geo/search-address: a typed address becomes up to five candidate places. A fake handler records what goes out.</summary>
public class AddressSearcherTests
{
    private const string Body = """
        [{"lat":"24.7113741","lon":"46.6743887","name":"Kingdom Centre","display_name":"Kingdom Centre, Olaya Street, Riyadh, Saudi Arabia","address":{"city":"Riyadh","country_code":"sa"}},
         {"lat":"95","lon":"46","display_name":"Out of range"},
         {"lat":"x","lon":"46","display_name":"Not a number"},
         {"lat":"21.5","lon":"39.2","display_name":"Jeddah","address":{"town":"Jeddah","country_code":"sa"}}]
        """;

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(answer(request));
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static (AddressSearcher Searcher, Handler Handler) Make(Func<HttpRequestMessage, HttpResponseMessage> answer)
    {
        var handler = new Handler(answer);
        return (new AddressSearcher(new Factory(handler)), handler);
    }

    [Fact]
    public async Task ReadsValidPlacesAndDropsMalformedOnes()
    {
        var (searcher, _) = Make(_ => Json(Body));
        var found = await searcher.SearchAsync("Kingdom Centre Riyadh", "SA", "en", CancellationToken.None);
        found.Select(f => f.Label).Should().Equal("Kingdom Centre, Olaya Street, Riyadh, Saudi Arabia", "Jeddah");
        found[0].Should().Match<AddressSuggestion>(f => f.Latitude == 24.711374m && f.Longitude == 46.674389m && f.City == "Riyadh" && f.CountryCode == "SA");
        found[1].City.Should().Be("Jeddah");
    }

    [Fact]
    public async Task OnlyEverContactsTheFixedHostWithAnEncodedQueryAndCountry()
    {
        var (searcher, handler) = Make(_ => Json("[]"));
        await searcher.SearchAsync("a&countrycodes=us#x https://evil.example/", "sa", "ar", CancellationToken.None);
        var uri = handler.Requests.First().RequestUri!;
        uri.Host.Should().Be("nominatim.openstreetmap.org");
        uri.Scheme.Should().Be("https");
        uri.Query.Should().Contain("countrycodes=sa").And.Contain("accept-language=ar").And.Contain("q=a%26countrycodes%3Dus%23x%20https%3A%2F%2Fevil.example%2F");
        uri.Query.Split("countrycodes=").Length.Should().Be(2, "the typed text cannot inject a second countrycodes parameter");
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ab ")]
    public async Task ATooShortQueryNeverLeavesTheServer(string q)
    {
        var (searcher, handler) = Make(_ => Json(Body));
        (await searcher.SearchAsync(q, null, "en", CancellationToken.None)).Should().BeEmpty();
        handler.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("US/../")]
    [InlineData("saa")]
    [InlineData("1a")]
    public void ACountryThatIsNotTwoLettersIsIgnored(string country) => AddressSearcher.NormaliseCountry(country).Should().BeNull();

    [Fact]
    public async Task AFailingOrGarbledProviderIsAnEmptyListNotAnError()
    {
        (await Make(_ => Json("oops", HttpStatusCode.TooManyRequests)).Searcher.SearchAsync("Riyadh", null, "en", CancellationToken.None)).Should().BeEmpty();
        (await Make(_ => Json("not json")).Searcher.SearchAsync("Riyadh", null, "en", CancellationToken.None)).Should().BeEmpty();
        (await Make(_ => Json("{\"a\":1}")).Searcher.SearchAsync("Riyadh", null, "en", CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task AtMostFiveSuggestionsComeBack()
    {
        var many = "[" + string.Join(",", Enumerable.Range(1, 9).Select(i => $"{{\"lat\":\"24.{i}\",\"lon\":\"46.{i}\",\"display_name\":\"Place {i}\"}}")) + "]";
        (await Make(_ => Json(many)).Searcher.SearchAsync("Riyadh", null, "en", CancellationToken.None)).Should().HaveCount(5);
    }

    [Fact]
    public async Task TheEndpointNeedsATenantAndAValidQuery()
    {
        GeoController Controller(bool tenant)
        {
            var http = new DefaultHttpContext();
            if (tenant) http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("tenant_id", Guid.NewGuid().ToString())], "Test"));
            var (searcher, _) = Make(_ => Json(Body));
            return new GeoController(null!, searcher) { ControllerContext = new ControllerContext { HttpContext = http } };
        }
        (await Controller(false).SearchAddress("Riyadh", null, null, CancellationToken.None)).Should().BeOfType<UnauthorizedResult>();
        (await Controller(true).SearchAddress("ab", null, null, CancellationToken.None)).Should().BeOfType<UnprocessableEntityObjectResult>();
        (await Controller(true).SearchAddress("Riyadh", "SA", "en", CancellationToken.None)).Should().BeOfType<OkObjectResult>();
    }

    // ---- fallback chain: Nominatim first, then Photon -------------------------------------------------------------

    // Real response captured from https://photon.komoot.io/api/?q=Dubai%20Mall&limit=2
    private const string PhotonBody = """
        {"type":"FeatureCollection","features":[{"type":"Feature","properties":{"osm_type":"W","osm_id":144136267,"osm_key":"shop","osm_value":"mall","type":"house","name":"Dubai Outlet Mall","street":"طريق دبي - العين","district":"مدينة هند 3","state":"إمارة دبي","country":"الإمارات العربية المتحدة","countrycode":"AE","extent":[55.3980745,25.0742212,55.4027241,25.0685253]},"geometry":{"type":"Point","coordinates":[55.40093,25.0714139]}},{"type":"Feature","properties":{"osm_type":"R","osm_id":18195959,"osm_key":"shop","osm_value":"mall","type":"house","name":"دبي مول","street":"شارع الدوحة","district":"وسط مدينة دبي","city":"دبي","state":"إمارة دبي","country":"الإمارات العربية المتحدة","countrycode":"AE","extent":[55.2745845,25.1997331,55.2825636,25.1942815]},"geometry":{"type":"Point","coordinates":[55.2789516,25.197044]}},{"type":"Feature","properties":{"name":"Dubai Mall","housenumber":"1","street":"Doha St","city":"Dubai","state":"Dubai","postcode":"12345","country":"United Arab Emirates","countrycode":"AE"},"geometry":{"type":"Point","coordinates":[55.28,25.19]}},{"type":"Feature","properties":{"name":"Dubai Mall Cafe","city":"Springfield","country":"United States","countrycode":"US"},"geometry":{"type":"Point","coordinates":[-90.1,38.2]}}]}
        """;

    private static bool IsPhoton(HttpRequestMessage r) => r.RequestUri!.Host == "photon.komoot.io";

    private static HttpResponseMessage Chain(HttpRequestMessage r, string nominatim, string photon, HttpStatusCode nStatus = HttpStatusCode.OK) =>
        IsPhoton(r) ? Json(photon) : Json(nominatim, nStatus);

    [Fact]
    public async Task NominatimEmptyFallsBackToPhoton()
    {
        var (searcher, handler) = Make(r => Chain(r, "[]", PhotonBody));
        var found = await searcher.SearchAsync("Dubai Mall", null, "en", CancellationToken.None);
        found.Should().NotBeEmpty();
        handler.Requests.Select(r => r.RequestUri!.Host).Should().Equal("nominatim.openstreetmap.org", "photon.komoot.io");
    }

    [Theory]
    [InlineData("[]", HttpStatusCode.InternalServerError)]
    [InlineData("[]", HttpStatusCode.TooManyRequests)]
    [InlineData("<html>blocked</html>", HttpStatusCode.OK)]
    [InlineData("{\"a\":1}", HttpStatusCode.OK)]
    public async Task NominatimFailureFallsBackToPhoton(string body, HttpStatusCode status)
    {
        var (searcher, handler) = Make(r => Chain(r, body, PhotonBody, status));
        (await searcher.SearchAsync("Dubai Mall", null, "en", CancellationToken.None)).Should().NotBeEmpty();
        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task NominatimTimeoutFallsBackToPhoton()
    {
        var (searcher, _) = Make(r => IsPhoton(r) ? Json(PhotonBody) : throw new TaskCanceledException("timeout"));
        (await searcher.SearchAsync("Dubai Mall", null, "en", CancellationToken.None)).Should().NotBeEmpty();
    }

    [Fact]
    public async Task ASlowProviderIsCutOffAtTheTimeoutAndTheNextOneIsTried()
    {
        var factory = new Factory(new SlowFirst());
        var searcher = new AddressSearcher(factory) { Timeout = TimeSpan.FromMilliseconds(100) };
        (await searcher.SearchAsync("Dubai Mall", null, "en", CancellationToken.None)).Should().NotBeEmpty();
    }

    private sealed class SlowFirst : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (IsPhoton(request)) return Json(PhotonBody);
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return Json("[]");
        }
    }

    [Fact]
    public async Task NominatimResultsMeanPhotonIsNeverCalled()
    {
        var (searcher, handler) = Make(r => Chain(r, Body, PhotonBody));
        (await searcher.SearchAsync("Kingdom Centre", null, "en", CancellationToken.None)).Should().NotBeEmpty();
        handler.Requests.Should().ContainSingle().Which.RequestUri!.Host.Should().Be("nominatim.openstreetmap.org");
    }

    [Fact]
    public async Task BothProvidersFailingIsAnEmptyListNotAnError()
    {
        var (searcher, handler) = Make(r => throw new HttpRequestException("down"));
        (await searcher.SearchAsync("Dubai Mall", null, "en", CancellationToken.None)).Should().BeEmpty();
        handler.Requests.Should().HaveCount(2);
        (await Make(_ => Json("nope", HttpStatusCode.BadGateway)).Searcher.SearchAsync("Dubai Mall", "ae", "ar", CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task PhotonFeaturesBecomeSuggestionsWithALabelWithoutBlanksOrDuplicates()
    {
        var (searcher, _) = Make(r => Chain(r, "[]", PhotonBody));
        var found = await searcher.SearchAsync("Dubai Mall", null, "en", CancellationToken.None);
        found.Should().HaveCount(4);
        found[1].Should().Match<AddressSuggestion>(f => f.Name == "دبي مول" && f.City == "دبي" && f.CountryCode == "AE"
            && f.Latitude == 25.197044m && f.Longitude == 55.278952m);
        found[1].Label.Should().Be("دبي مول, شارع الدوحة, وسط مدينة دبي, دبي, إمارة دبي, الإمارات العربية المتحدة");
        found[2].Label.Should().Be("Dubai Mall, Doha St 1, Dubai, 12345, United Arab Emirates", "the state equals the city, so it is not repeated");
        found[0].City.Should().Be("مدينة هند 3", "no city: the district stands in");
    }

    [Fact]
    public async Task PhotonResultsAreFilteredByTheRequestedCountry()
    {
        var (searcher, handler) = Make(r => Chain(r, "[]", PhotonBody));
        (await searcher.SearchAsync("Dubai Mall", "us", "en", CancellationToken.None)).Should().ContainSingle().Which.CountryCode.Should().Be("US");
        (await searcher.SearchAsync("Dubai Mall", "AE", "en", CancellationToken.None)).Should().OnlyContain(f => f.CountryCode == "AE").And.HaveCount(3);
        handler.Requests.Where(IsPhoton).Select(r => r.RequestUri!.Query).Should().OnlyContain(q => !q.Contains("countrycode"));
    }

    [Fact]
    public async Task PhotonGetsLangOnlyWhenItSupportsIt()
    {
        var (searcher, handler) = Make(r => Chain(r, "[]", "{\"features\":[]}"));
        await searcher.SearchAsync("a&lang=de#x", null, "ar", CancellationToken.None);
        await searcher.SearchAsync("Dubai", null, "en", CancellationToken.None);
        var photon = handler.Requests.Where(IsPhoton).Select(r => r.RequestUri!).ToList();
        photon[0].Scheme.Should().Be("https");
        photon[0].AbsolutePath.Should().Be("/api/");
        photon[0].Query.Should().NotContain("lang=").And.Contain("q=a%26lang%3Dde%23x").And.Contain("limit=");
        photon[1].Query.Should().Contain("lang=en");
    }

    [Fact]
    public async Task AnOversizedBodyIsIgnoredFromEitherProvider()
    {
        var huge = "[" + string.Join(",", Enumerable.Range(0, 20000).Select(i => "{\"lat\":\"1\",\"lon\":\"1\",\"display_name\":\"xxxxxxxxxxxxxxxx\"}")) + "]";
        var hugePhoton = "{\"features\":[" + string.Join(",", Enumerable.Range(0, 20000).Select(i => "{\"properties\":{\"name\":\"xxxxxxxxxxxxxxxxx\"},\"geometry\":{\"coordinates\":[1,1]}}")) + "]}";
        var (searcher, _) = Make(r => IsPhoton(r) ? Json(hugePhoton) : Json(huge));
        (await searcher.SearchAsync("Dubai Mall", null, "en", CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task TheProviderListIsConfigurable()
    {
        static IConfiguration Cfg(string v) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Geo:Providers"] = v }).Build();
        var (photonOnly, h1) = Make(r => Chain(r, Body, PhotonBody));
        photonOnly = new AddressSearcher(new Factory(h1), Cfg("photon"));
        await photonOnly.SearchAsync("Dubai Mall", null, "en", CancellationToken.None);
        h1.Requests.Should().ContainSingle().Which.RequestUri!.Host.Should().Be("photon.komoot.io");

        var h2 = new Handler(r => Chain(r, "[]", PhotonBody));
        await new AddressSearcher(new Factory(h2), Cfg(" Photon , NOMINATIM ")).SearchAsync("Dubai Mall", null, "en", CancellationToken.None);
        h2.Requests.Select(r => r.RequestUri!.Host).Should().Equal("photon.komoot.io");

        var h3 = new Handler(r => Chain(r, "[]", "{\"features\":[]}"));
        await new AddressSearcher(new Factory(h3), Cfg("photon,nominatim")).SearchAsync("Dubai Mall", null, "en", CancellationToken.None);
        h3.Requests.Select(r => r.RequestUri!.Host).Should().Equal("photon.komoot.io", "nominatim.openstreetmap.org");

        var h4 = new Handler(r => Chain(r, Body, PhotonBody));
        (await new AddressSearcher(new Factory(h4), Cfg("bogus, ")).SearchAsync("Dubai Mall", null, "en", CancellationToken.None)).Should().BeEmpty();
        h4.Requests.Should().BeEmpty("unknown names are ignored; an empty chain is an empty result");

        var h5 = new Handler(r => Chain(r, "[]", PhotonBody));
        await new AddressSearcher(new Factory(h5), Cfg("")).SearchAsync("Dubai Mall", null, "en", CancellationToken.None);
        h5.Requests.Select(r => r.RequestUri!.Host).Should().Equal("nominatim.openstreetmap.org", "photon.komoot.io");
    }

    private sealed class Recorder : HttpMessageHandler
    {
        public int Sent;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Sent++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); }
    }

    [Theory]
    [InlineData("https://nominatim.openstreetmap.org/search?q=a", true)]
    [InlineData("https://photon.komoot.io/api/?q=a", true)]
    [InlineData("https://evil.example/api/?q=a", false)]
    [InlineData("https://photon.komoot.io.evil.example/api/", false)]
    [InlineData("http://photon.komoot.io/api/", false)]
    [InlineData("https://maps.googleapis.com/x", false)]
    public async Task TheHandlerOnlyEverReachesTheTwoAllowedHosts(string url, bool allowed)
    {
        var inner = new Recorder();
        var handler = AddressSearcher.WithHostAllowList(inner);
        using var invoker = new HttpMessageInvoker(handler);
        var send = () => invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, url), CancellationToken.None);
        if (allowed) { (await send()).StatusCode.Should().Be(HttpStatusCode.OK); inner.Sent.Should().Be(1); }
        else { await send.Should().ThrowAsync<HttpRequestException>(); inner.Sent.Should().Be(0); }
    }

    [Fact]
    public void ThePrimaryHandlerIsTheAllowListedOne() => AddressSearcher.CreateHandler().Should().BeAssignableTo<DelegatingHandler>();
}
