using System.Net;
using System.Text.Json;
using FluentAssertions;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports.Api;
using static Zayra.Api.Tests.Government.AnbPayrollApi.AnbApiTestKit;

namespace Zayra.Api.Tests.Government.AnbPayrollApi;

/// <summary>
/// Contract between our client and ANB's published OpenAPI "Payroll Services APIs" v0.0.1.
///
/// <para>PUBLIC-REPO RULE: the spec file itself is NOT committed (its terms are unchecked). What is
/// committed is <see cref="Spec"/>: the facts we derived from it (paths, parameter names, multipart part
/// names, required response properties, take maximum). <see cref="DerivedFactsMatchThePublishedSpec"/>
/// re-derives them from a local copy when <c>ANB_PAYROLL_SPEC_PATH</c> points at one, and is reported as
/// SKIPPED, never passed, when it does not.</para>
///
/// <para>The spec publishes no example responses, so the "examples" below are built from the schemas'
/// required properties, with synthetic values.</para>
/// </summary>
public class AnbPayrollSpecContractTests
{
    internal static class Spec
    {
        public const string SubmitPath = "/payroll/v2";
        public const string StatusPath = "/payroll/v2/{id}";
        public const string WpsPath = "/payroll/{id}/WPS";
        public static readonly string[] SubmitParts = { "header", "body" };
        public static readonly string[] SubmitHeaders =
            { "profileid", "feescategorycode", "feesandvataccount", "feesfrequency", "autowpsfileupload", "nationalunifiedno" };
        public static readonly string[] StatusQuery = { "order", "sort", "page", "take", "filter", "include", "q" };
        public static readonly string[] StatusHeaders = { "profileid" };
        public static readonly string[] WpsHeaders = { "profileid" };
        public const int TakeMaximum = 50;
        public static readonly string[] PayrollResponseRequired = { "id", "status", "message" };
        public static readonly string[] FetchPayrollResponseRequired = { "id", "status", "reason", "payrollId", "data", "meta" };
        public static readonly string[] FetchPayrollDetailsRequired =
            { "employeeId", "employeeAccountNumber", "salaryAmount", "bicCode", "employeeName", "paymentId", "paymentStatus" };
        public static readonly string[] PageMetaRequired = { "page", "take", "itemCount", "pageCount", "hasPreviousPage", "hasNextPage" };
    }

    /// <summary>Headers HTTP itself or our transport adds; everything else must be a spec header.</summary>
    private static readonly HashSet<string> TransportHeaders = new(StringComparer.OrdinalIgnoreCase) { "Authorization", "Accept" };

    [Fact]
    public async Task OurSubmitRequest_MatchesTheSpecRequestBodyAndHeaders()
    {
        var batch = BuildBatch(3, autoWps: true);
        var handler = new StubAnbHandler(_ => Json(HttpStatusCode.OK, new { id = PayrollId, status = "ACCEPTED", message = "Created" }));
        await Client(handler, out _).SubmitAsync(Submit(batch, new AnbSubmitHeaders("P1", "FC", "1234567890123456", "MONTHLY", true, "7001234567")),
            CancellationToken.None);

        var post = handler.Posts.Single();
        post.Method.Should().Be(HttpMethod.Post);
        post.Uri.AbsolutePath.Should().Be("/api" + Spec.SubmitPath);
        post.Uri.Query.Should().BeEmpty();
        post.Headers.Keys.Where(h => !TransportHeaders.Contains(h)).Should().BeSubsetOf(Spec.SubmitHeaders);
        post.Headers.Keys.Where(h => !TransportHeaders.Contains(h)).Should().BeEquivalentTo(Spec.SubmitHeaders,
            "every optional spec header was set in this request");
        (await AnbPayrollApiSubmitTests.ReadMultipart(post)).Keys.Should().BeEquivalentTo(Spec.SubmitParts);
    }

    [Fact]
    public async Task OurStatusRequests_UseOnlySpecQueryParameters_WithinTheTakeMaximum()
    {
        var batch = BuildBatch(120);
        var handler = new StubAnbHandler(PagedStatus(batch.Rows.Select(r => Line(r, "PENDING")).ToList()));
        await Client(handler, out _).GetStatusAsync(TestScope, PayrollId, "P1", CancellationToken.None);

        foreach (var get in handler.ApiRequests)
        {
            get.Method.Should().Be(HttpMethod.Get);
            get.Uri.AbsolutePath.Should().Be("/api" + Spec.StatusPath.Replace("{id}", PayrollId));
            var q = ParseQuery(get.Uri);
            q.Keys.Should().BeSubsetOf(Spec.StatusQuery);
            int.Parse(q["take"]).Should().BeInRange(1, Spec.TakeMaximum);
            int.Parse(q["page"]).Should().BeGreaterThanOrEqualTo(1);
            if (q.TryGetValue("order", out var order)) order.Should().BeOneOf("ASC", "DESC");
            get.Headers.Keys.Where(h => !TransportHeaders.Contains(h)).Should().BeSubsetOf(Spec.StatusHeaders);
        }
        AnbPayrollApiClient.MaxTake.Should().Be(Spec.TakeMaximum);
    }

    [Fact]
    public async Task OurWpsRequest_MatchesTheSpecPath()
    {
        var handler = new StubAnbHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1 }) });
        await Client(handler, out _).DownloadWpsAsync(TestScope, PayrollId, "P1", CancellationToken.None);
        var get = handler.ApiRequests.Single();
        get.Uri.AbsolutePath.Should().Be("/api" + Spec.WpsPath.Replace("{id}", PayrollId));
        get.Headers.Keys.Where(h => !TransportHeaders.Contains(h)).Should().BeSubsetOf(Spec.WpsHeaders);
    }

    [Fact]
    public async Task SchemaShapedExampleResponses_Parse()
    {
        // PayrollResponse: exactly the required properties.
        var submitted = new Dictionary<string, object?> { ["id"] = PayrollId, ["status"] = "ACCEPTED", ["message"] = "Created payroll payment" };
        submitted.Keys.Should().BeEquivalentTo(Spec.PayrollResponseRequired);
        var postHandler = new StubAnbHandler(_ => Json(HttpStatusCode.OK, submitted));
        (await Client(postHandler, out _).SubmitAsync(Submit(BuildBatch(1)), CancellationToken.None)).BankPayrollId.Should().Be(PayrollId);

        // FetchPayrollResponse with one FetchPayrollDetailsResponse and PageMetaDto: required properties
        // only, plus an explicit null paymentId (null until paid, per ANB's overview).
        var r = BuildBatch(1).Rows[0];
        var detail = new Dictionary<string, object?>
        {
            ["employeeId"] = r.EmployeeNationalId, ["employeeAccountNumber"] = r.AccountNumber, ["salaryAmount"] = r.SalaryAmount,
            ["bicCode"] = r.BicCode, ["employeeName"] = r.EmployeeName, ["paymentId"] = null, ["paymentStatus"] = "INITIATED",
        };
        var meta = new Dictionary<string, object?>
        {
            ["page"] = 1, ["take"] = 50, ["itemCount"] = 1, ["pageCount"] = 1, ["hasPreviousPage"] = false, ["hasNextPage"] = false,
        };
        var fetched = new Dictionary<string, object?>
        {
            ["id"] = PayrollId, ["status"] = "VALIDATED", ["reason"] = "", ["payrollId"] = PayrollId,
            ["data"] = new[] { detail }, ["meta"] = meta,
        };
        detail.Keys.Should().BeEquivalentTo(Spec.FetchPayrollDetailsRequired);
        meta.Keys.Should().BeEquivalentTo(Spec.PageMetaRequired);
        fetched.Keys.Should().BeEquivalentTo(Spec.FetchPayrollResponseRequired);

        var getHandler = new StubAnbHandler(_ => Json(HttpStatusCode.OK, fetched));
        var snapshot = await Client(getHandler, out _).GetStatusAsync(TestScope, PayrollId, null, CancellationToken.None);
        snapshot.Lines.Single().PaymentStatus.Should().Be("INITIATED");
        snapshot.Lines.Single().PaymentId.Should().BeNull();
        snapshot.PayrollStatus.Should().Be("VALIDATED");
        snapshot.Complete.Should().BeTrue();
    }

    [Fact]
    public async Task SpecNumbersMayArriveAsJsonNumbersWithFractions()
    {
        // PageMetaDto declares "number", not "integer"; 1.0 is a legal JSON number for page 1.
        var r = BuildBatch(1).Rows[0];
        var json = "{\"id\":\"" + PayrollId + "\",\"status\":\"COMPLETED\",\"reason\":\"\",\"payrollId\":\"" + PayrollId + "\",\"data\":[{" +
                   "\"employeeId\":\"" + r.EmployeeNationalId + "\",\"employeeAccountNumber\":\"" + r.AccountNumber + "\",\"salaryAmount\":" +
                   r.SalaryAmount.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"bicCode\":\"RJHISARI\",\"employeeName\":\"x\"," +
                   "\"paymentId\":\"P1\",\"paymentStatus\":\"PAID\"}],\"meta\":{\"page\":1.0,\"take\":50.0,\"itemCount\":1.0,\"pageCount\":1.0," +
                   "\"hasPreviousPage\":false,\"hasNextPage\":false}}";
        var handler = new StubAnbHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        var snapshot = await Client(handler, out _).GetStatusAsync(TestScope, PayrollId, null, CancellationToken.None);
        snapshot.Lines.Single().SalaryAmount.Should().Be(r.SalaryAmount);
    }

    [AnbSpecFact]
    public void DerivedFactsMatchThePublishedSpec()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(AnbSpecFactAttribute.Path!));
        var root = doc.RootElement;
        root.GetProperty("openapi").GetString().Should().StartWith("3.0");
        root.GetProperty("servers").GetArrayLength().Should().Be(0, "if ANB publishes servers, revisit the config-only base URL");
        root.GetProperty("components").TryGetProperty("securitySchemes", out _).Should().BeFalse(
            "if ANB publishes a security scheme, revisit the configured token flow");

        var paths = root.GetProperty("paths");
        var post = paths.GetProperty(Spec.SubmitPath).GetProperty("post");
        HeaderNames(post).Should().BeEquivalentTo(Spec.SubmitHeaders);
        post.GetProperty("requestBody").GetProperty("content").GetProperty("multipart/form-data").GetProperty("schema")
            .GetProperty("properties").EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(Spec.SubmitParts);

        var get = paths.GetProperty(Spec.StatusPath).GetProperty("get");
        get.GetProperty("parameters").EnumerateArray().Where(p => p.GetProperty("in").GetString() == "query")
            .Select(p => p.GetProperty("name").GetString()).Should().BeEquivalentTo(Spec.StatusQuery);
        HeaderNames(get).Should().BeEquivalentTo(Spec.StatusHeaders);
        get.GetProperty("parameters").EnumerateArray().Single(p => p.GetProperty("name").GetString() == "take")
            .GetProperty("schema").GetProperty("maximum").GetInt32().Should().Be(Spec.TakeMaximum);
        HeaderNames(paths.GetProperty(Spec.WpsPath).GetProperty("get")).Should().BeEquivalentTo(Spec.WpsHeaders);

        var schemas = root.GetProperty("components").GetProperty("schemas");
        Required(schemas, "PayrollResponse").Should().BeEquivalentTo(Spec.PayrollResponseRequired);
        Required(schemas, "FetchPayrollResponse").Should().BeEquivalentTo(Spec.FetchPayrollResponseRequired);
        Required(schemas, "FetchPayrollDetailsResponse").Should().BeEquivalentTo(Spec.FetchPayrollDetailsRequired);
        Required(schemas, "PageMetaDto").Should().BeEquivalentTo(Spec.PageMetaRequired);
    }

    private static IEnumerable<string?> HeaderNames(JsonElement op) =>
        op.GetProperty("parameters").EnumerateArray().Where(p => p.GetProperty("in").GetString() == "header")
            .Select(p => p.GetProperty("name").GetString());

    private static IEnumerable<string?> Required(JsonElement schemas, string name) =>
        schemas.GetProperty(name).GetProperty("required").EnumerateArray().Select(e => e.GetString());
}

/// <summary>Runs only when <c>ANB_PAYROLL_SPEC_PATH</c> names a readable local copy of the spec;
/// otherwise the test is reported SKIPPED (visible), not passed.</summary>
public sealed class AnbSpecFactAttribute : FactAttribute
{
    public static string? Path => Environment.GetEnvironmentVariable("ANB_PAYROLL_SPEC_PATH");

    public AnbSpecFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Path) || !File.Exists(Path))
            Skip = "ANB_PAYROLL_SPEC_PATH is not set to a local copy of ANB's PayrollPaymentUpdated.json (not committed: public repo).";
    }
}
