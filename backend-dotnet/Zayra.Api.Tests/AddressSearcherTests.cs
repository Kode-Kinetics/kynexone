using System.Net;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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
        var uri = handler.Requests.Should().ContainSingle().Subject.RequestUri!;
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
}
