using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Zayra.Api.Application.Common;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports.Channels;

namespace Zayra.Api.Infrastructure.Payroll.SaudiBankExports.Api;

/// <summary>
/// Transport for ANB's published Payroll Services API (OpenAPI v0.0.1): <c>POST /payroll/v2</c>,
/// <c>GET /payroll/v2/{id}</c>, <c>GET /payroll/{id}/WPS</c>. NOT LIVE: nothing registers it, and it
/// refuses to be built without every endpoint setting (ANB publishes no servers, token URL or security
/// scheme, so all of them are configuration). Bank acceptance is not verified.
///
/// <para>MONEY RULES</para>
/// <list type="bullet">
/// <item>Submit sends the FROZEN files exactly as given, after checking each against the SHA-256 recorded
/// when it was frozen and checking the header's count and total against the body. It never regenerates.</item>
/// <item>Exactly one POST per <see cref="SubmitAsync"/>. No retry, ever. A transport failure, timeout or
/// server error after the request left is <see cref="BankSubmitOutcomeKind.Unknown"/>: the bank may hold
/// the payroll, so only a person who has checked with the bank may decide what happens next.</item>
/// <item>Any failure before the POST (settings, environment, credential, token, artifact) is
/// <see cref="BankSubmitOutcomeKind.Refused"/> and no socket is opened for the POST.</item>
/// <item>Reads (status, WPS) may re-acquire the token once on a 401; nothing else is retried here.</item>
/// </list>
///
/// <para>The HttpClient handed in must not carry a retry handler (Polly or similar): the no-retry rule for
/// the POST depends on it. No response body, secret, token, IBAN or ID is logged or put into an exception.</para>
/// </summary>
public sealed partial class AnbPayrollApiClient
{
    public const int MaxTake = 50;
    public const int MaxWpsBytes = 10 * 1024 * 1024;
    public const int MaxMessageLength = 300;
    /// <summary>Hard ceiling on pages per read (100,000 lines), whatever the bank's meta says.</summary>
    public const int MaxPages = 2_000;
    public const string GrantClientCredentials = "client_credentials";
    public const string AuthBasic = "client_secret_basic";
    public const string AuthPost = "client_secret_post";

    private const int MaxResponseChars = 16 * 1024;
    private static readonly TimeSpan TokenSkew = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;
    private readonly IAnbEndpointConfig _config;
    private readonly IAnbCredentialSource _credentials;
    private readonly ILogger<AnbPayrollApiClient> _log;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, CachedToken> _tokens = new(StringComparer.Ordinal);

    public AnbPayrollApiClient(HttpMessageHandler handler, IAnbEndpointConfig config, IAnbCredentialSource credentials,
        ILogger<AnbPayrollApiClient> log, TimeProvider? clock = null, AnbClientLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _http = new HttpClient(handler, disposeHandler: false) { Timeout = (limits ?? AnbClientLimits.Default).RequestTimeout };
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _clock = clock ?? TimeProvider.System;
        _ = Resolve(_config); // fail closed at construction
    }

    // ── Submit ──────────────────────────────────────────────────────────────────────────────────

    public async Task<BankSubmitOutcome> SubmitAsync(AnbSubmitRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        Endpoint endpoint;
        try { endpoint = Resolve(_config); }
        catch (BankChannelRefusedException ex) { return Refuse(ex.Code); }

        if (!SameEnvironment(endpoint, request.Scope)) return Refuse(BankChannelCodes.EnvironmentMismatch);
        if (!HashMatches(request.Header) || !HashMatches(request.Body)) return Refuse(BankChannelCodes.ArtifactHashMismatch);
        if (!SafeFileName(request.Header.FileName) || !SafeFileName(request.Body.FileName))
            return Refuse(BankChannelCodes.ArtifactInconsistent);

        var preflight = AnbArtifactPreflight.Check(request.Header.Content, request.Body.Content, request.Headers);
        if (preflight.Code is not null) return Refuse(preflight.Code);

        var headers = OptionalHeaders(request.Headers);
        if (headers is null) return Refuse(BankChannelCodes.SubmitHeaderInvalid);

        var credential = await _credentials.GetAsync(request.Scope, ct);
        if (credential is null) return Refuse(BankChannelCodes.CredentialMissing);

        var token = await GetTokenAsync(endpoint, request.Scope, credential, ct);
        if (token.Value is null) return Refuse(token.Code);

        var redact = preflight.SentValues.Concat(new[] { credential.ClientId, credential.ClientSecret, token.Value }).ToList();

        using var form = new MultipartFormDataContent();
        form.Add(FilePart(request.Header, "header"));
        form.Add(FilePart(request.Body, "body"));
        using var post = new HttpRequestMessage(HttpMethod.Post, endpoint.Url("/payroll/v2")) { Content = form };
        post.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
        post.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        foreach (var (name, value) in headers) post.Headers.TryAddWithoutValidation(name, value);

        var headerSha = request.Header.ExpectedSha256;
        var linesSha = request.Body.ExpectedSha256;
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(post, HttpCompletionOption.ResponseContentRead, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            // The request may have left in full. Whether the bank holds the payroll is unknown.
            var kind = ex.GetType().Name;
            _log.LogWarning("ANB payroll submit outcome unknown after a transport failure ({ExceptionType}); files {HeaderSha} {LinesSha}",
                kind, headerSha, linesSha);
            return new BankSubmitOutcome(BankSubmitOutcomeKind.Unknown, BankChannelCodes.OutcomeUnknown, RequestSent: true);
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            string? text;
            try { text = await ReadCappedAsync(response, ct); }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
            {
                text = null;
                if (status is >= 200 and < 300)
                {
                    _log.LogWarning("ANB payroll submit outcome unknown: HTTP {Status} but the response could not be read; files {HeaderSha} {LinesSha}",
                        status, headerSha, linesSha);
                    return new BankSubmitOutcome(BankSubmitOutcomeKind.Unknown, BankChannelCodes.AcceptedWithoutId, true, status);
                }
            }

            var outcome = Classify(status, text, redact, endpoint, request.Scope, credential);
            _log.LogInformation("ANB payroll submit {Outcome} ({Code}) HTTP {Status}; bank id {BankPayrollId}; files {HeaderSha} {LinesSha}",
                outcome.Kind, outcome.Code, status, outcome.BankPayrollId, headerSha, linesSha);
            return outcome;
        }
    }

    private BankSubmitOutcome Classify(int status, string? text, IReadOnlyList<string> redact, Endpoint endpoint,
        BankChannelScope scope, AnbClientCredential credential)
    {
        if (status is >= 200 and < 300)
        {
            var (id, bankStatus, message) = ParseSubmitResponse(text);
            if (id is null || !PayrollIdShape().IsMatch(id))
                return new BankSubmitOutcome(BankSubmitOutcomeKind.Unknown, BankChannelCodes.AcceptedWithoutId, true, status,
                    BankStatus: WordOrNull(bankStatus), Message: Clean(message, redact));
            return new BankSubmitOutcome(BankSubmitOutcomeKind.Accepted, BankChannelCodes.Accepted, true, status, id,
                WordOrNull(bankStatus), Clean(message, redact));
        }

        if (status is 401 or 403)
        {
            _tokens.TryRemove(TokenKey(endpoint, scope, credential), out _);
            return new BankSubmitOutcome(BankSubmitOutcomeKind.RejectedAuthorization, BankChannelCodes.RejectedAuthorization, true, status);
        }

        // 408 timeout, 409 conflict (perhaps "already exists"), 425 too early, 429 throttled: the bank may
        // or may not hold the payroll. Every 1xx/3xx/5xx likewise. Only a clear 4xx is a structural refusal.
        if (status is >= 400 and < 500 and not (408 or 409 or 425 or 429))
            return new BankSubmitOutcome(BankSubmitOutcomeKind.RejectedStructure, BankChannelCodes.RejectedStructure, true, status,
                Message: Clean(ParseSubmitResponse(text).Message, redact));

        return new BankSubmitOutcome(BankSubmitOutcomeKind.Unknown, BankChannelCodes.OutcomeUnknown, true, status);
    }

    private static (string? Id, string? Status, string? Message) ParseSubmitResponse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, null, null);
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return (null, null, null);
            var root = doc.RootElement;
            string? Str(string name) =>
                root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            string? message = Str("message");
            if (message is null && root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.Array)
                message = string.Join("; ", m.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()));
            var id = Str("id");
            return (string.IsNullOrWhiteSpace(id) ? null : id.Trim(), Str("status"), message);
        }
        catch (JsonException)
        {
            return (null, null, null);
        }
    }

    private BankSubmitOutcome Refuse(string code)
    {
        _log.LogWarning("ANB payroll submit refused before sending ({Code})", code);
        return new BankSubmitOutcome(BankSubmitOutcomeKind.Refused, code, RequestSent: false);
    }

    private static ByteArrayContent FilePart(BankArtifactFile file, string part)
    {
        var content = new ByteArrayContent(file.Content);
        content.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        content.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "\"" + part + "\"",
            FileName = "\"" + file.FileName + "\"",
        };
        return content;
    }

    private static bool HashMatches(BankArtifactFile file)
    {
        if (file.Content is null || string.IsNullOrEmpty(file.ExpectedSha256) || file.ExpectedSha256.Length != 64) return false;
        var actual = Encoding.ASCII.GetBytes(AnbConnectCsvGenerator.Sha256Hex(file.Content));
        var expected = Encoding.ASCII.GetBytes(file.ExpectedSha256.ToLowerInvariant());
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static bool SafeFileName(string? name) => name is not null && FileNameShape().IsMatch(name);

    /// <summary>The spec's optional headers, or null when a value could split a header line.</summary>
    private static List<(string, string)>? OptionalHeaders(AnbSubmitHeaders h)
    {
        var list = new List<(string, string)>();
        bool Add(string name, string? value)
        {
            if (value is null || value.Length == 0) return true;
            if (value.Length > 128 || value.Any(c => c < 0x20 || c > 0x7E) || value.Trim().Length == 0) return false;
            list.Add((name, value));
            return true;
        }
        var ok = Add("profileid", h.ProfileId)
                 && Add("feescategorycode", h.FeesCategoryCode)
                 && Add("feesandvataccount", h.FeesAndVatAccount)
                 && Add("feesfrequency", h.FeesFrequency)
                 && Add("autowpsfileupload", h.AutoWpsFileUpload ? "YES" : null)
                 && Add("nationalunifiedno", h.NationalUnifiedNo);
        return ok ? list : null;
    }

    // ── Status ──────────────────────────────────────────────────────────────────────────────────

    public async Task<AnbPayrollStatusSnapshot> GetStatusAsync(BankChannelScope scope, string bankPayrollId,
        string? profileId, CancellationToken ct)
    {
        var (endpoint, credential) = await PrepareReadAsync(scope, bankPayrollId, profileId, ct);
        var id = bankPayrollId.Trim();

        var merger = new LineMerger();
        BankPayrollPhase? phase = null;
        string payrollStatus = string.Empty;
        string? reason = null;
        int? itemCount = null, pageCount = null;
        var page = 1;
        var pages = 0;

        while (true)
        {
            var url = endpoint.Url($"/payroll/v2/{Uri.EscapeDataString(id)}") +
                      $"?page={page.ToString(CultureInfo.InvariantCulture)}&take={MaxTake.ToString(CultureInfo.InvariantCulture)}&order=ASC";
            var text = await ReadJsonAsync(endpoint, scope, credential, url, profileId, ct);
            pages++;
            var parsed = StatusPage.Parse(text);

            if (!string.Equals(parsed.Id, id, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(parsed.PayrollId, id, StringComparison.OrdinalIgnoreCase))
                throw new BankChannelException(BankChannelCodes.PagePayrollMismatch);
            if (parsed.Page != page) throw new BankChannelException(BankChannelCodes.PageMismatch);
            if (parsed.Data.Count > MaxTake) throw new BankChannelException(BankChannelCodes.PollInconsistent);
            if (itemCount is null) { itemCount = parsed.ItemCount; pageCount = parsed.PageCount; }
            else if (itemCount != parsed.ItemCount || pageCount != parsed.PageCount)
                throw new BankChannelException(BankChannelCodes.PollInconsistent);

            var pagePhase = AnbPayrollStatusMapper.MapPayrollStatus(parsed.Status);
            var advanced = AnbPayrollStatusMapper.AdvancePayroll(phase, pagePhase, out var held);
            if (held && phase is { } prior && AnbPayrollStatusMapper.IsTerminal(prior) && AnbPayrollStatusMapper.IsTerminal(pagePhase))
                throw new BankChannelException(BankChannelCodes.PollInconsistent);
            if (phase is null || advanced != phase)
            {
                payrollStatus = parsed.Status.Trim();
                reason = parsed.Reason;
            }
            phase = advanced;

            foreach (var line in parsed.Data) merger.Add(line);

            if (!parsed.HasNextPage)
            {
                if (page < parsed.PageCount) throw new BankChannelException(BankChannelCodes.PollInconsistent);
                break;
            }
            page++;
            if (page > parsed.PageCount || page > MaxPages) throw new BankChannelException(BankChannelCodes.PollInconsistent);
        }

        var lines = merger.Lines();
        var complete = merger.DistinctKeys == itemCount;
        _log.LogInformation("ANB payroll status read {BankPayrollId}: {Phase}, {Pages} page(s), {Distinct} of {ItemCount} line(s), {Collapsed} duplicate(s), complete {Complete}",
            id, phase, pages, merger.DistinctKeys, itemCount, merger.Collapsed, complete);
        return new AnbPayrollStatusSnapshot(id, payrollStatus, MaskText(reason), lines, itemCount ?? 0, pages, merger.Collapsed, complete);
    }

    /// <summary>
    /// Merges lines by (employeeId, account), which the generator makes unique per batch. A repeated line
    /// never moves backwards; two DIFFERENT terminal statuses for one line are both kept so the existing
    /// confirmation import refuses that employee as conflicting instead of picking one.
    /// </summary>
    private sealed class LineMerger
    {
        private readonly Dictionary<(string, string), List<AnbStatusLine>> _byKey = new();
        private readonly List<(string, string)> _order = new();
        public int Collapsed { get; private set; }
        public int DistinctKeys => _order.Count;

        public void Add(AnbStatusLine line)
        {
            var phase = AnbPayrollStatusMapper.MapTransactionStatus(line.PaymentStatus);
            var key = (line.EmployeeId.Trim(), line.EmployeeAccountNumber.Trim());
            if (!_byKey.TryGetValue(key, out var held))
            {
                _byKey[key] = new List<AnbStatusLine> { line };
                _order.Add(key);
                return;
            }

            var heldPhases = held.Select(h => AnbPayrollStatusMapper.MapTransactionStatus(h.PaymentStatus)).ToList();
            if (heldPhases.Contains(phase)) { Collapsed++; return; }

            var heldTerminal = heldPhases.Any(AnbPayrollStatusMapper.IsTerminal);
            if (AnbPayrollStatusMapper.IsTerminal(phase))
            {
                if (heldTerminal) { held.Add(line); return; } // conflicting terminals: keep both, loudly
                held.Clear();
                held.Add(line);
                Collapsed++;
                return;
            }

            Collapsed++;
            if (!heldTerminal && Rank(phase) > Rank(heldPhases[0]))
            {
                held.Clear();
                held.Add(line);
            }
        }

        public List<AnbStatusLine> Lines() => _order.SelectMany(k => _byKey[k]).ToList();

        private static int Rank(AnbTransactionPhase p) => p switch
        {
            AnbTransactionPhase.Initiated => 0,
            AnbTransactionPhase.Pending => 1,
            AnbTransactionPhase.InProgress => 2,
            _ => 3,
        };
    }

    private sealed record StatusPage(string? Id, string? PayrollId, string Status, string? Reason, List<AnbStatusLine> Data,
        int Page, int ItemCount, int PageCount, bool HasNextPage)
    {
        public static StatusPage Parse(string text)
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) throw Unparsable();
                var meta = Obj(root, "meta");
                var data = root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array ? d : throw Unparsable();
                var lines = data.EnumerateArray().Select(ParseLine).ToList();
                return new StatusPage(
                    OptStr(root, "id"), OptStr(root, "payrollId"), ReqStr(root, "status"), OptStr(root, "reason"), lines,
                    Int(meta, "page"), Int(meta, "itemCount"), Int(meta, "pageCount"), Bool(meta, "hasNextPage"));
            }
            catch (JsonException) { throw Unparsable(); }
            catch (FormatException) { throw Unparsable(); }
            catch (InvalidOperationException) { throw Unparsable(); }
        }

        private static AnbStatusLine ParseLine(JsonElement e)
        {
            if (e.ValueKind != JsonValueKind.Object) throw Unparsable();
            var amount = e.TryGetProperty("salaryAmount", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetDecimal() : throw Unparsable();
            return new AnbStatusLine(
                ReqStr(e, "employeeId"), ReqStr(e, "employeeAccountNumber"), amount,
                OptStr(e, "bicCode"), OptStr(e, "employeeName"), Blank(OptStr(e, "paymentId")), ReqStr(e, "paymentStatus"),
                OptStr(e, "paymentStatusDescription"), Blank(OptStr(e, "paymentTransactionReferenceNumber")),
                Date(e, "createdAt"), Date(e, "updatedAt"));
        }

        private static BankChannelException Unparsable() => new(BankChannelCodes.ResponseUnparsable);

        private static JsonElement Obj(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : throw Unparsable();

        private static string ReqStr(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
                ? v.GetString()! : throw Unparsable();

        private static string? OptStr(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        /// <summary>The spec types these as "number"; a whole number written as 1.0 is accepted, 1.5 is not.</summary>
        private static int Int(JsonElement e, string name)
        {
            if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number) throw Unparsable();
            var d = v.GetDecimal();
            if (d != decimal.Truncate(d) || d < 0 || d > int.MaxValue) throw Unparsable();
            return (int)d;
        }

        private static bool Bool(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : throw Unparsable();

        private static DateTimeOffset? Date(JsonElement e, string name) =>
            OptStr(e, name) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d)
                ? d : null;
    }

    // ── WPS file ────────────────────────────────────────────────────────────────────────────────

    public async Task<BankWpsFile> DownloadWpsAsync(BankChannelScope scope, string bankPayrollId, string? profileId,
        CancellationToken ct)
    {
        var (endpoint, credential) = await PrepareReadAsync(scope, bankPayrollId, profileId, ct);
        var id = bankPayrollId.Trim();
        var url = endpoint.Url($"/payroll/{Uri.EscapeDataString(id)}/WPS");

        using var response = await SendReadAsync(endpoint, scope, credential, url, profileId, ct);
        var status = (int)response.StatusCode;
        if (status == 404) throw new BankChannelException(BankChannelCodes.WpsNotReady, status);
        ThrowIfReadFailed(status);

        if (response.Content.Headers.ContentLength is > MaxWpsBytes) throw new BankChannelException(BankChannelCodes.WpsTooLarge);
        byte[] bytes;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + read > MaxWpsBytes) throw new BankChannelException(BankChannelCodes.WpsTooLarge);
                buffer.Write(chunk, 0, read);
            }
            bytes = buffer.ToArray();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            throw new BankChannelException(BankChannelCodes.HttpError, status);
        }

        var disposition = response.Content.Headers.ContentDisposition;
        var name = (disposition?.FileNameStar ?? disposition?.FileName)?.Trim('"');
        var file = new BankWpsFile(bytes, AnbConnectCsvGenerator.Sha256Hex(bytes), response.Content.Headers.ContentType?.MediaType,
            SafeFileName(name) ? name : null);
        _log.LogInformation("ANB WPS file downloaded for {BankPayrollId}: {Length} bytes, sha256 {Sha}", id, bytes.Length, file.Sha256);
        return file;
    }

    // ── Shared read plumbing ────────────────────────────────────────────────────────────────────

    private async Task<(Endpoint, AnbClientCredential)> PrepareReadAsync(BankChannelScope scope, string bankPayrollId,
        string? profileId, CancellationToken ct)
    {
        var endpoint = Resolve(_config);
        if (!SameEnvironment(endpoint, scope)) throw new BankChannelRefusedException(BankChannelCodes.EnvironmentMismatch);
        if (bankPayrollId is null || !PayrollIdShape().IsMatch(bankPayrollId.Trim()))
            throw new BankChannelRefusedException(BankChannelCodes.PayrollIdInvalid);
        if (OptionalHeaders(new AnbSubmitHeaders(ProfileId: profileId)) is null)
            throw new BankChannelRefusedException(BankChannelCodes.SubmitHeaderInvalid);
        var credential = await _credentials.GetAsync(scope, ct)
                         ?? throw new BankChannelRefusedException(BankChannelCodes.CredentialMissing);
        return (endpoint, credential);
    }

    private async Task<string> ReadJsonAsync(Endpoint endpoint, BankChannelScope scope, AnbClientCredential credential,
        string url, string? profileId, CancellationToken ct)
    {
        using var response = await SendReadAsync(endpoint, scope, credential, url, profileId, ct);
        var status = (int)response.StatusCode;
        ThrowIfReadFailed(status);
        try
        {
            return await ReadCappedAsync(response, ct, MaxTake * 4096) ?? throw new BankChannelException(BankChannelCodes.ResponseUnparsable, status);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            throw new BankChannelException(BankChannelCodes.HttpError, status);
        }
    }

    private static void ThrowIfReadFailed(int status)
    {
        if (status == 401) throw new BankChannelException(BankChannelCodes.Unauthorized, status);
        if (status is < 200 or >= 300) throw new BankChannelException(BankChannelCodes.HttpError, status);
    }

    /// <summary>GET with the bearer token. A 401 drops the cached token and tries once more with a fresh one.</summary>
    private async Task<HttpResponseMessage> SendReadAsync(Endpoint endpoint, BankChannelScope scope, AnbClientCredential credential,
        string url, string? profileId, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var token = await GetTokenAsync(endpoint, scope, credential, ct);
            if (token.Value is null) throw new BankChannelException(token.Code);

            using var get = new HttpRequestMessage(HttpMethod.Get, url);
            get.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
            get.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (!string.IsNullOrEmpty(profileId)) get.Headers.TryAddWithoutValidation("profileid", profileId);

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(get, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                var kind = ex.GetType().Name;
                _log.LogWarning("ANB payroll read failed in transport ({ExceptionType})", kind);
                throw new BankChannelException(BankChannelCodes.HttpError);
            }

            if (response.StatusCode != HttpStatusCode.Unauthorized || attempt > 0) return response;
            response.Dispose();
            _tokens.TryRemove(TokenKey(endpoint, scope, credential), out _);
            _log.LogInformation("ANB payroll read got 401; re-acquiring the token once");
        }
    }

    // ── Token ───────────────────────────────────────────────────────────────────────────────────

    private sealed record CachedToken(string Value, DateTimeOffset UseUntil);

    private async Task<(string? Value, string Code)> GetTokenAsync(Endpoint endpoint, BankChannelScope scope,
        AnbClientCredential credential, CancellationToken ct)
    {
        var key = TokenKey(endpoint, scope, credential);
        var now = _clock.GetUtcNow();
        if (_tokens.TryGetValue(key, out var cached) && cached.UseUntil > now) return (cached.Value, string.Empty);

        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", GrantClientCredentials),
            new("scope", endpoint.Scope),
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint.TokenUrl);
        if (endpoint.BasicAuth)
        {
            var pair = Uri.EscapeDataString(credential.ClientId) + ":" + Uri.EscapeDataString(credential.ClientSecret);
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(pair)));
        }
        else
        {
            form.Add(new("client_id", credential.ClientId));
            form.Add(new("client_secret", credential.ClientSecret));
        }
        request.Content = new FormUrlEncodedContent(form);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // the caller cancelled before anything payroll-related was sent
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            var kind = ex.GetType().Name;
            _log.LogWarning("ANB token request failed in transport ({ExceptionType})", kind);
            return (null, BankChannelCodes.TokenUnavailable);
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            string? text;
            try { text = await ReadCappedAsync(response, ct); }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                text = null;
            }

            if (status is < 200 or >= 300)
            {
                // Never the raw body: an identity provider may echo the client id or the request back.
                var oauthError = OAuthErrorCode(text);
                _log.LogWarning("ANB token request refused: HTTP {Status} {OAuthError}", status, oauthError);
                return (null, status is 400 or 401 or 403 ? BankChannelCodes.TokenRejected : BankChannelCodes.TokenUnavailable);
            }

            var token = ParseToken(text);
            if (token is null)
            {
                _log.LogWarning("ANB token response was not a usable bearer token (HTTP {Status})", status);
                return (null, BankChannelCodes.TokenUnavailable);
            }

            var (value, expiresIn) = token.Value;
            if (expiresIn is { } seconds && TimeSpan.FromSeconds(seconds) > TokenSkew)
                _tokens[key] = new CachedToken(value, now + TimeSpan.FromSeconds(seconds) - TokenSkew);
            return (value, string.Empty);
        }
    }

    private static (string Value, double? ExpiresIn)? ParseToken(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var value = root.TryGetProperty("access_token", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            var type = root.TryGetProperty("token_type", out var tt) && tt.ValueKind == JsonValueKind.String ? tt.GetString() : null;
            if (string.IsNullOrEmpty(value) || value.Length > 8192 || value.Any(c => c <= 0x20 || c > 0x7E)) return null;
            if (!string.Equals(type, "Bearer", StringComparison.OrdinalIgnoreCase)) return null;
            double? expires = root.TryGetProperty("expires_in", out var e) && e.ValueKind == JsonValueKind.Number ? e.GetDouble() : null;
            return (value, expires);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The RFC 6749 5.2 <c>error</c> code, or a placeholder. Spec codes are short snake_case words.</summary>
    internal static string OAuthErrorCode(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "empty";
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
            {
                var code = error.GetString() ?? string.Empty;
                if (code.Length is > 0 and <= 64 && code.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_')) return code;
            }
        }
        catch (JsonException) { }
        return "unparsed";
    }

    /// <summary>Tenant + company + environment + endpoint + credential version + client id fingerprint.</summary>
    private static string TokenKey(Endpoint endpoint, BankChannelScope scope, AnbClientCredential credential)
    {
        var clientPrint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(credential.ClientId)))[..16];
        return string.Join("|", scope.TenantId.ToString("N"), scope.CompanyId?.ToString("N") ?? "-",
            endpoint.Environment, endpoint.TokenUrl.AbsoluteUri, endpoint.Scope, credential.Version, clientPrint);
    }

    // ── Settings ────────────────────────────────────────────────────────────────────────────────

    private sealed record Endpoint(string Environment, Uri BaseUrl, Uri TokenUrl, string Scope, bool BasicAuth)
    {
        public string Url(string path) => BaseUrl.AbsoluteUri.TrimEnd('/') + path;
    }

    private static Endpoint Resolve(IAnbEndpointConfig c)
    {
        var values = new (string Name, string? Value)[]
        {
            (nameof(IAnbEndpointConfig.Environment), c.Environment),
            (nameof(IAnbEndpointConfig.BaseUrl), c.BaseUrl),
            (nameof(IAnbEndpointConfig.TokenUrl), c.TokenUrl),
            (nameof(IAnbEndpointConfig.GrantType), c.GrantType),
            (nameof(IAnbEndpointConfig.Scope), c.Scope),
            (nameof(IAnbEndpointConfig.ClientAuthMethod), c.ClientAuthMethod),
        };
        var missing = values.Where(v => string.IsNullOrWhiteSpace(v.Value)).Select(v => v.Name).ToList();
        if (missing.Count > 0) throw new BankChannelRefusedException(BankChannelCodes.ConfigMissing, missing);

        var invalid = new List<string>();
        var baseUrl = HttpsUri(c.BaseUrl!);
        var tokenUrl = HttpsUri(c.TokenUrl!);
        if (baseUrl is null) invalid.Add(nameof(IAnbEndpointConfig.BaseUrl));
        if (tokenUrl is null) invalid.Add(nameof(IAnbEndpointConfig.TokenUrl));
        if (!string.Equals(c.GrantType!.Trim(), GrantClientCredentials, StringComparison.Ordinal))
            invalid.Add(nameof(IAnbEndpointConfig.GrantType));
        var auth = c.ClientAuthMethod!.Trim();
        if (auth is not (AuthBasic or AuthPost)) invalid.Add(nameof(IAnbEndpointConfig.ClientAuthMethod));
        if (c.Scope!.Any(ch => ch < 0x20 || ch > 0x7E)) invalid.Add(nameof(IAnbEndpointConfig.Scope));
        if (invalid.Count > 0) throw new BankChannelRefusedException(BankChannelCodes.ConfigInvalid, invalid);

        return new Endpoint(c.Environment!.Trim(), baseUrl!, tokenUrl!, c.Scope!.Trim(), auth == AuthBasic);
    }

    private static Uri? HttpsUri(string value) =>
        Uri.TryCreate(value.Trim(), UriKind.Absolute, out var u)
        && u.Scheme == Uri.UriSchemeHttps
        && string.IsNullOrEmpty(u.UserInfo)
        && string.IsNullOrEmpty(u.Query)
        && string.IsNullOrEmpty(u.Fragment)
            ? u : null;

    private static bool SameEnvironment(Endpoint endpoint, BankChannelScope scope) =>
        string.Equals(endpoint.Environment, scope.Environment?.Trim(), StringComparison.OrdinalIgnoreCase);

    // ── Text hygiene ────────────────────────────────────────────────────────────────────────────

    private static async Task<string?> ReadCappedAsync(HttpResponseMessage response, CancellationToken ct, int maxChars = MaxResponseChars)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        return text.Length > maxChars ? null : text;
    }

    /// <summary>Bank free text, cleaned for display: every value we sent and every secret removed, IBAN and
    /// ID shapes masked (on the full text, before truncation), control characters dropped.</summary>
    private static string? Clean(string? message, IReadOnlyList<string> redact)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        var text = message.Length > MaxResponseChars ? message[..MaxResponseChars] : message;
        foreach (var value in redact.Where(v => !string.IsNullOrEmpty(v) && v.Length >= 3).OrderByDescending(v => v.Length))
            if (text.Contains(value, StringComparison.Ordinal)) text = text.Replace(value, "***", StringComparison.Ordinal);
        text = SensitiveFieldClassifier.MaskEmbedded(text);
        text = new string(text.Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        return text.Length > MaxMessageLength ? text[..MaxMessageLength] : text;
    }

    private static string? MaskText(string? text) => Clean(text, Array.Empty<string>());

    private static string? WordOrNull(string? s) =>
        s is not null && s.Length <= 32 && s.All(c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or '_' or ' ') ? s.Trim() : null;

    [GeneratedRegex("^[A-Za-z0-9-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex PayrollIdShape();

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex FileNameShape();
}
