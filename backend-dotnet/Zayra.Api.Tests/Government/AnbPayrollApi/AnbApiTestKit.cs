using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports.Api;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports.Channels;

namespace Zayra.Api.Tests.Government.AnbPayrollApi;

/// <summary>
/// Fake ANB for the payroll API client. No network: every request ends in <see cref="StubAnbHandler"/>.
/// Hosts use the reserved <c>.test</c> TLD. Every person, ID, account and secret here is synthetic.
/// </summary>
internal static class AnbApiTestKit
{
    public const string BaseUrl = "https://anb-sandbox.test/api";
    public const string TokenUrl = "https://anb-auth.test/oauth/token";
    public const string ClientId = "kx-synthetic-client-7f3a";
    public const string ClientSecret = "s3cr3t-SYNTHETIC-do-not-log-91c2";
    public const string AccessToken = "tok-SYNTHETIC-bearer-55aa77";
    public const string PayrollId = "3f2b8c1e-9d4a-4e7b-8a61-0c5d2e9f1a77";

    public static readonly BankChannelScope TestScope = new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Guid.Parse("22222222-2222-2222-2222-222222222222"),
        "sandbox");

    public static AnbPayrollApiOptions Config() => new()
    {
        Environment = "sandbox",
        BaseUrl = BaseUrl,
        TokenUrl = TokenUrl,
        GrantType = "client_credentials",
        Scope = "payroll",
        ClientAuthMethod = "client_secret_basic",
        AllowedHostSuffixes = "anb-sandbox.test,anb-auth.test,anb-sandbox-2.test",
    };

    public static AnbPayrollApiClient Client(HttpMessageHandler handler, out ListLogger<AnbPayrollApiClient> log,
        IAnbEndpointConfig? config = null, IAnbCredentialSource? credentials = null, TimeSpan? timeout = null,
        TimeProvider? clock = null, TimeSpan? deadline = null)
    {
        log = new ListLogger<AnbPayrollApiClient>();
        return new AnbPayrollApiClient(handler, config ?? Config(), credentials ?? new FixedCredentials(), log, clock,
            new AnbClientLimits(timeout ?? TimeSpan.FromSeconds(10), deadline ?? TimeSpan.FromSeconds(30)));
    }

    public static AnbClientCredential Credential(string clientId = ClientId, string secret = ClientSecret, string version = "v1",
        string environment = "sandbox") =>
        new(clientId, secret, version, environment, "anb-sandbox.test", "anb-auth.test");

    public static BankBatchBinding Binding(Batch batch, DateTimeOffset? intentAt = null) =>
        new(batch.Submitted, batch.Total, intentAt ?? IntentAt);

    /// <summary>Intent time used by the synthetic status lines (createdAt is later).</summary>
    public static readonly DateTimeOffset IntentAt = new(2026, 11, 27, 5, 0, 0, TimeSpan.Zero);

    // ── Synthetic batch through the REAL generator, at real size ─────────────────────────────────

    /// <summary>"Today" in Riyadh for the generator's value-date checks; the synthetic value date is the Thursday after.</summary>
    public static readonly DateOnly Today = new(2026, 11, 17);

    public sealed record Batch(BankArtifactFile Header, BankArtifactFile Body, IReadOnlyList<AnbPaymentInput> Rows,
        IReadOnlyList<BankSubmittedLine> Submitted, decimal Total);

    /// <summary>A 35-character Arabic full name, distinct per index.</summary>
    public static string ArabicName35(int i)
    {
        const string words = "عبدالرحمن محمد سعد ال";
        var suffix = new string('ا', 2) + ArabicDigits(i);
        var name = (words + " " + "ق" + suffix).PadRight(35, 'ي');
        return name.Length > 35 ? name[..35] : name;
    }

    private static string ArabicDigits(int i) =>
        string.Concat(i.ToString(CultureInfo.InvariantCulture).Select(c => (char)('٠' + (c - '0'))));

    public static string NationalId(int i) => "1" + (100_000_000 + i).ToString(CultureInfo.InvariantCulture);

    public static string Iban(int i) =>
        IbanValidator.WithValidCheckDigits("SA0080" + (100_000_000_000_000_000L + i).ToString(CultureInfo.InvariantCulture));

    public static string WpsReference(int i) => $"WPS-E{i:D5}-202611";

    public static Batch BuildBatch(int lines, bool autoWps = false)
    {
        var rows = new List<AnbPaymentInput>(lines);
        for (var i = 1; i <= lines; i++)
        {
            var basic = 4000m + i % 97;
            var housing = 1000.50m;
            var other = 250m;
            var deductions = (i % 3) * 10m;
            rows.Add(new AnbPaymentInput(i, $"E{i:D5}", NationalId(i), Iban(i),
                basic + housing + other - deductions, basic, housing, other, deductions,
                "RJHISARI", ArabicName35(i), "Riyadh", "Olaya", "KSA"));
        }
        var total = rows.Sum(r => r.SalaryAmount);
        var header = new AnbHeaderInput("2026111601", "PAYROLL", "7001234567", "0108061198800026",
            new DateOnly(2026, 11, 26), "Test Establishment Co", "Riyadh", "Olaya", "KSA", "Payroll Nov 2026",
            "Test Establishment", autoWps, autoWps ? "7001234567" : null);
        var gen = AnbConnectCsvGenerator.Generate(header, rows, total, AnbValueDateRules.WithoutHolidayCalendar(Today));
        if (!gen.Ok) throw new InvalidOperationException("synthetic batch invalid: " + string.Join(";", gen.Errors.Select(e => e.Code)));
        var h = gen.Files![0];
        var b = gen.Files![1];
        return new Batch(
            new BankArtifactFile(h.Name, h.Content, h.Sha256),
            new BankArtifactFile(b.Name, b.Content, b.Sha256),
            rows,
            rows.Select(r => new BankSubmittedLine(r.EmployeeNationalId!, r.AccountNumber!, WpsReference(r.EmployeeRef))).ToList(),
            total);
    }

    public static AnbSubmitRequest Submit(Batch batch, AnbSubmitHeaders? headers = null) =>
        new(TestScope, batch.Header, batch.Body, headers ?? new AnbSubmitHeaders());

    // ── Response builders shaped by the published schemas ───────────────────────────────────────

    public static HttpResponseMessage Json(HttpStatusCode status, object body) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    public static HttpResponseMessage TokenOk(string token = AccessToken, int expiresIn = 3600) =>
        Json(HttpStatusCode.OK, new { access_token = token, token_type = "Bearer", expires_in = expiresIn });

    public static object Line(AnbPaymentInput r, string status, string? paymentId = null, string? description = null) => new
    {
        employeeId = r.EmployeeNationalId,
        employeeAccountNumber = r.AccountNumber,
        salaryAmount = r.SalaryAmount,
        bicCode = r.BicCode,
        employeeName = r.EmployeeName,
        paymentId,
        paymentStatus = status,
        paymentStatusDescription = description,
        paymentTransactionReferenceNumber = paymentId is null ? null : "TRX" + paymentId,
        createdAt = "2026-11-27T06:00:00.000Z",
        updatedAt = "2026-11-27T07:00:00.000Z",
    };

    public static object Page(string payrollStatus, IReadOnlyList<object> data, int page, int take, int itemCount,
        string id = PayrollId, string? reason = null, string? createdAt = null)
    {
        var pageCount = (int)Math.Ceiling(itemCount / (double)take);
        return new
        {
            id,
            status = payrollStatus,
            reason = reason ?? string.Empty,
            createdAt,
            payrollId = id,
            data,
            meta = new { page, take, itemCount, pageCount, hasPreviousPage = page > 1, hasNextPage = page < pageCount },
        };
    }

    /// <summary>Serves lines page by page in the order the client asks, exactly as ANB's meta describes.</summary>
    public static Func<CapturedRequest, HttpResponseMessage> PagedStatus(IReadOnlyList<object> lines, string payrollStatus = "IN_PROGRESS")
        => req =>
        {
            var q = ParseQuery(req.Uri);
            var page = int.Parse(q.GetValueOrDefault("page", "1"), CultureInfo.InvariantCulture);
            var take = int.Parse(q.GetValueOrDefault("take", "10"), CultureInfo.InvariantCulture);
            var slice = lines.Skip((page - 1) * take).Take(take).ToList();
            return Json(HttpStatusCode.OK, Page(payrollStatus, slice, page, take, lines.Count));
        };

    public static Dictionary<string, string> ParseQuery(Uri uri) =>
        uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => p.Length > 1 ? Uri.UnescapeDataString(p[1]) : string.Empty);
}

internal sealed class FixedCredentials : IAnbCredentialSource
{
    private readonly AnbClientCredential? _credential;
    public int Calls;
    public FixedCredentials(AnbClientCredential? credential = null, bool none = false) =>
        _credential = none ? null : credential ?? AnbApiTestKit.Credential();
    public Task<AnbClientCredential?> GetAsync(BankChannelScope scope, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        return Task.FromResult(_credential);
    }
}

internal sealed class ManualClock : TimeProvider
{
    public DateTimeOffset Now = new(2026, 11, 27, 6, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed record CapturedRequest(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers,
    string? ContentType, byte[] Content);

/// <summary>Records every request (body read in full, so "the body was sent") and answers through
/// a routing function. A responder may throw to simulate a timeout or reset AFTER the body was read.</summary>
internal sealed class StubAnbHandler : HttpMessageHandler
{
    private readonly Func<CapturedRequest, CancellationToken, Task<HttpResponseMessage>> _api;
    private readonly Func<CapturedRequest, CancellationToken, Task<HttpResponseMessage>> _token;
    public readonly ConcurrentQueue<CapturedRequest> Requests = new();

    public StubAnbHandler(Func<CapturedRequest, HttpResponseMessage> api, Func<CapturedRequest, HttpResponseMessage>? token = null)
        : this((r, _) => Task.FromResult(api(r)), token is null ? null : (r, _) => Task.FromResult(token(r))) { }

    public StubAnbHandler(Func<CapturedRequest, CancellationToken, Task<HttpResponseMessage>> api,
        Func<CapturedRequest, CancellationToken, Task<HttpResponseMessage>>? token = null)
    {
        _api = api;
        _token = token ?? ((_, _) => Task.FromResult(AnbApiTestKit.TokenOk()));
    }

    public IReadOnlyList<CapturedRequest> TokenRequests =>
        Requests.Where(r => r.Uri.AbsoluteUri.StartsWith(AnbApiTestKit.TokenUrl, StringComparison.Ordinal)).ToList();
    public IReadOnlyList<CapturedRequest> ApiRequests =>
        Requests.Where(r => !r.Uri.AbsoluteUri.StartsWith(AnbApiTestKit.TokenUrl, StringComparison.Ordinal)).ToList();
    public IReadOnlyList<CapturedRequest> Posts => ApiRequests.Where(r => r.Method == HttpMethod.Post).ToList();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var bytes = request.Content is null ? Array.Empty<byte>() : await request.Content.ReadAsByteArrayAsync(ct);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        var captured = new CapturedRequest(request.Method, request.RequestUri!, headers,
            request.Content?.Headers.ContentType?.ToString(), bytes);
        Requests.Enqueue(captured);
        return request.RequestUri!.AbsoluteUri.StartsWith(AnbApiTestKit.TokenUrl, StringComparison.Ordinal)
            ? await _token(captured, ct)
            : await _api(captured, ct);
    }
}

internal sealed class ListLogger<T> : ILogger<T>
{
    public readonly ConcurrentQueue<string> Entries = new();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        Entries.Enqueue("scope: " + state);
        return null;
    }
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Entries.Enqueue($"{logLevel}: {formatter(state, exception)} {state} {exception}");
    }
    public string All => string.Join("\n", Entries);
}
