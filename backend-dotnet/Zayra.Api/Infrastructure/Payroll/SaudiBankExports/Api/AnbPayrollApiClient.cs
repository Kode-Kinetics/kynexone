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
/// <c>GET /payroll/v2/{id}</c>, <c>GET /payroll/{id}/WPS</c>. Used through <see cref="AnbPayrollChannel"/>.
/// NOT LIVE: nothing registers it, and it refuses to be built without every endpoint setting (ANB publishes
/// no servers, token URL or security scheme). Bank acceptance is not verified.
///
/// <para>MONEY RULES</para>
/// <list type="bullet">
/// <item>Submit copies the frozen files once, checks the copies against the SHA-256 recorded when they were
/// frozen and the header's count and total against the body, re-hashes the copies immediately before the
/// send, and sends those copies. It never regenerates.</item>
/// <item>Exactly one POST per <see cref="SubmitAsync"/>, never retried. The handler must not follow
/// redirects (<see cref="AnbHttpHandlerFactory"/>; the constructor refuses one that does).</item>
/// <item><see cref="SubmitAsync"/> never throws except <see cref="ArgumentNullException"/>. Anything that
/// goes wrong before the POST is <see cref="BankSubmitOutcomeKind.Refused"/> (cancellation included). Once
/// the POST was attempted, ANY failure, of any exception type, is <see cref="BankSubmitOutcomeKind.Unknown"/>.</item>
/// <item>Reads (status, WPS) may re-acquire the token once on a 401; nothing else is retried here.</item>
/// </list>
/// No response body, secret, token, IBAN or ID is logged or put into an exception, and every response is
/// read through a byte cap.
/// </summary>
public sealed partial class AnbPayrollApiClient
{
    public const int MaxTake = 50;
    public const int MaxWpsBytes = 10 * 1024 * 1024;
    public const int MaxMessageLength = 300;
    /// <summary>Largest payroll the client will page through (2,000 pages of 50).</summary>
    public const int MaxItemCount = 100_000;
    public const string GrantClientCredentials = "client_credentials";
    public const string AuthBasic = "client_secret_basic";
    public const string AuthPost = "client_secret_post";

    private const int MaxSubmitResponseBytes = 16 * 1024;
    private const int MaxTokenResponseBytes = 16 * 1024;
    private const int MaxPageBytes = 256 * 1024;
    private const int MaxErrorBytes = 16 * 1024;
    private static readonly TimeSpan TokenSkew = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaxTokenLifetime = TimeSpan.FromHours(1);

    private readonly HttpClient _http;
    private readonly IAnbEndpointConfig _config;
    private readonly IAnbCredentialSource _credentials;
    private readonly ILogger<AnbPayrollApiClient> _log;
    private readonly TimeProvider _clock;
    private readonly AnbClientLimits _limits;
    private readonly ConcurrentDictionary<string, CachedToken> _tokens = new(StringComparer.Ordinal);

    // Internal: built only by AnbGatedChannelFactory, behind GatedBankPayrollChannel (ratchet I). Tests reach it through InternalsVisibleTo.
    internal AnbPayrollApiClient(HttpMessageHandler handler, IAnbEndpointConfig config, IAnbCredentialSource credentials,
        ILogger<AnbPayrollApiClient> log, TimeProvider? clock = null, AnbClientLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _clock = clock ?? TimeProvider.System;
        _limits = limits ?? AnbClientLimits.Default;
        if (AnbHttpHandlerFactory.FollowsRedirects(handler))
            throw new BankChannelRefusedException(BankChannelCodes.HandlerFollowsRedirects);
        _ = Resolve(_config); // fail closed at construction
        _http = new HttpClient(handler, disposeHandler: false)
        {
            Timeout = _limits.RequestTimeout,
            MaxResponseContentBufferSize = 1024 * 1024,
        };
    }

    // ── Submit ──────────────────────────────────────────────────────────────────────────────────

    private sealed record Prepared(
        BankChannelScope Scope, Endpoint Endpoint, AnbClientCredential Credential, string Token, byte[] Header, byte[] Body,
        string HeaderSha, string BodySha, List<(string Name, string Value)> Headers, IReadOnlyList<string> Redact);

    public async Task<BankSubmitOutcome> SubmitAsync(AnbSubmitRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // ── Before the POST: every failure is a refusal, and no socket is opened for the POST. ──
        Prepared prepared;
        try
        {
            var (ready, code) = await PrepareSubmitAsync(request, ct);
            if (ready is null) return Refuse(code!);
            prepared = ready;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Refuse(BankChannelCodes.CancelledBeforeSend);
        }
        catch (Exception ex)
        {
            var kind = ex.GetType().Name;
            Safe(() => _log.LogWarning("ANB payroll submit preparation failed ({ExceptionType})", kind));
            return Refuse(BankChannelCodes.ArtifactInconsistent);
        }

        if (ct.IsCancellationRequested) return Refuse(BankChannelCodes.CancelledBeforeSend);
        // Last check on the copies, immediately before they are handed to the transport.
        if (!HashEquals(prepared.Header, prepared.HeaderSha) || !HashEquals(prepared.Body, prepared.BodySha))
            return Refuse(BankChannelCodes.ArtifactHashMismatch);

        // ── From here on the request may leave. Nothing below may throw. ──
        int? status = null;
        try
        {
            using var form = new MultipartFormDataContent();
            form.Add(FilePart(prepared.Header, AnbConnectCsvGenerator.HeaderFileName, "header"));
            form.Add(FilePart(prepared.Body, AnbConnectCsvGenerator.BodyFileName, "body"));
            using var post = new HttpRequestMessage(HttpMethod.Post, prepared.Endpoint.Url("/payroll/v2")) { Content = form };
            post.Headers.Authorization = new AuthenticationHeaderValue("Bearer", prepared.Token);
            post.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            foreach (var (name, value) in prepared.Headers) post.Headers.TryAddWithoutValidation(name, value);

            using var response = await _http.SendAsync(post, HttpCompletionOption.ResponseHeadersRead, ct);
            status = (int)response.StatusCode;

            string? text = null;
            try
            {
                var bytes = await ReadBoundedAsync(response, MaxSubmitResponseBytes, ct);
                text = bytes is null ? null : Encoding.UTF8.GetString(bytes);
            }
            catch (Exception)
            {
                text = null; // unreadable body: a 2xx without an id is still Unknown below
            }

            var outcome = Classify(status.Value, text, request.PriorOutcomeUnknown, prepared) with
            {
                SentHeaderSha256 = prepared.HeaderSha,
                SentBodySha256 = prepared.BodySha,
            };
            Safe(() => _log.LogInformation(
                "ANB payroll submit {Outcome} ({Code}) HTTP {Status}; bank id {BankPayrollId}; files {HeaderSha} {LinesSha}",
                outcome.Kind, outcome.Code, outcome.HttpStatus, outcome.BankPayrollId, prepared.HeaderSha, prepared.BodySha));
            return outcome;
        }
        catch (Exception ex)
        {
            // The request may have left in full. Whether the bank holds the payroll is unknown.
            var kind = ex.GetType().Name;
            var code = status is >= 200 and < 300 ? BankChannelCodes.AcceptedWithoutId : BankChannelCodes.OutcomeUnknown;
            Safe(() => _log.LogWarning(
                "ANB payroll submit outcome unknown after {ExceptionType}; HTTP {Status}; files {HeaderSha} {LinesSha}",
                kind, status, prepared.HeaderSha, prepared.BodySha));
            return new BankSubmitOutcome(BankSubmitOutcomeKind.Unknown, code, RequestSent: true, HttpStatus: status,
                SentHeaderSha256: prepared.HeaderSha, SentBodySha256: prepared.BodySha);
        }
    }

    private async Task<(Prepared? Ready, string? Code)> PrepareSubmitAsync(AnbSubmitRequest request, CancellationToken ct)
    {
        Endpoint endpoint;
        try { endpoint = Resolve(_config); }
        catch (BankChannelRefusedException ex) { return (null, ex.Code); }

        if (!SameEnvironment(endpoint, request.Scope)) return (null, BankChannelCodes.EnvironmentMismatch);
        if (request.Header is null || request.Body is null) return (null, BankChannelCodes.ArtifactInconsistent);
        if (!string.Equals(request.Header.FileName, AnbConnectCsvGenerator.HeaderFileName, StringComparison.Ordinal)
            || !string.Equals(request.Body.FileName, AnbConnectCsvGenerator.BodyFileName, StringComparison.Ordinal))
            return (null, BankChannelCodes.ArtifactInconsistent);

        // One private copy of each file. Everything below checks, and the POST sends, these copies only.
        var header = request.Header.Content is null ? null : (byte[])request.Header.Content.Clone();
        var body = request.Body.Content is null ? null : (byte[])request.Body.Content.Clone();
        if (header is null || body is null
            || !HashEquals(header, request.Header.ExpectedSha256) || !HashEquals(body, request.Body.ExpectedSha256))
            return (null, BankChannelCodes.ArtifactHashMismatch);

        var preflight = AnbArtifactPreflight.Check(header, body, request.Headers);
        if (preflight.Code is not null) return (null, preflight.Code);

        var headers = OptionalHeaders(request.Headers);
        if (headers is null) return (null, BankChannelCodes.SubmitHeaderInvalid);

        var (credential, credentialCode) = await LoadCredentialAsync(request.Scope, endpoint, ct);
        if (credential is null) return (null, credentialCode);

        var token = await GetTokenAsync(endpoint, request.Scope, credential, ct);
        if (token.Value is null) return (null, token.Code);

        var redact = preflight.SentValues.Concat(new[] { credential.ClientId, credential.ClientSecret, token.Value }).ToList();
        return (new Prepared(request.Scope, endpoint, credential, token.Value, header, body,
            AnbConnectCsvGenerator.Sha256Hex(header), AnbConnectCsvGenerator.Sha256Hex(body), headers, redact), null);
    }

    private async Task<(AnbClientCredential? Credential, string Code)> LoadCredentialAsync(BankChannelScope scope, Endpoint endpoint,
        CancellationToken ct)
    {
        AnbClientCredential? credential;
        try
        {
            credential = await _credentials.GetAsync(scope, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var kind = ex.GetType().Name;
            Safe(() => _log.LogWarning("ANB credential source failed ({ExceptionType})", kind));
            return (null, BankChannelCodes.CredentialSourceFailed);
        }
        if (credential is null) return (null, BankChannelCodes.CredentialMissing);
        if (!CredentialFits(credential, endpoint)) return (null, BankChannelCodes.CredentialEndpointMismatch);
        return (credential, string.Empty);
    }

    /// <summary>A credential is used only for the environment and hosts it was issued for.</summary>
    private static bool CredentialFits(AnbClientCredential c, Endpoint endpoint) =>
        string.Equals(c.Environment?.Trim(), endpoint.Environment, StringComparison.OrdinalIgnoreCase)
        && string.Equals(c.BaseUrlHost?.Trim(), endpoint.BaseUrl.IdnHost, StringComparison.OrdinalIgnoreCase)
        && string.Equals(c.TokenUrlHost?.Trim(), endpoint.TokenUrl.IdnHost, StringComparison.OrdinalIgnoreCase);

    private BankSubmitOutcome Classify(int status, string? text, bool priorOutcomeUnknown, Prepared p)
    {
        if (status is >= 200 and < 300)
        {
            var (id, bankStatus, message) = ParseSubmitResponse(text);
            var word = WordOrNull(bankStatus);
            if (id is null || !PayrollIdShape().IsMatch(id))
                return new BankSubmitOutcome(BankSubmitOutcomeKind.Unknown, BankChannelCodes.AcceptedWithoutId, true, status,
                    BankStatus: word, Message: Clean(message, p.Redact));
            if (word is not null && AnbPayrollStatusMapper.TryMapPayrollStatus(word, out var phase)
                && phase is BankPayrollPhase.Failed or BankPayrollPhase.Rejected)
                // The bank answered 2xx but says the payroll failed: poll before treating it as live.
                return new BankSubmitOutcome(BankSubmitOutcomeKind.Unknown, BankChannelCodes.AcceptedWithFailedStatus, true, status, id,
                    word, Clean(message, p.Redact));
            return new BankSubmitOutcome(BankSubmitOutcomeKind.Accepted, BankChannelCodes.Accepted, true, status, id,
                word, Clean(message, p.Redact));
        }

        if (status is 401 or 403)
        {
            _tokens.TryRemove(TokenKey(p.Endpoint, p.Scope, p.Credential), out _);
            return new BankSubmitOutcome(BankSubmitOutcomeKind.RejectedAuthorization, BankChannelCodes.RejectedAuthorization, true, status);
        }

        // A wrong path or method is configuration, never a file problem to regenerate.
        if (status is 404 or 405 or 406 or 410)
            return new BankSubmitOutcome(BankSubmitOutcomeKind.Unknown, BankChannelCodes.EndpointWrong, true, status);

        // Only these codes are read as "the bank refused the files". After an earlier Unknown, the same
        // answer may be the bank refusing a duplicate of a payroll it already holds: not a regenerate signal.
        if (status is 400 or 413 or 415 or 422)
        {
            var message = Clean(ParseSubmitResponse(text).Message, p.Redact);
            return priorOutcomeUnknown
                ? new BankSubmitOutcome(BankSubmitOutcomeKind.Unknown, BankChannelCodes.RejectedAfterUnknown, true, status, Message: message)
                : new BankSubmitOutcome(BankSubmitOutcomeKind.RejectedStructure, BankChannelCodes.RejectedStructure, true, status, Message: message);
        }

        // 1xx, 3xx (never followed), 408, 409, 425, 429, other 4xx, 5xx: the bank may hold the payroll.
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
            var message = Str("message");
            if (message is null && root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.Array)
                message = string.Join("; ", m.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()));
            var id = Str("id");
            return (string.IsNullOrEmpty(id) ? null : id, Str("status"), message);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            return (null, null, null);
        }
    }

    private BankSubmitOutcome Refuse(string code)
    {
        Safe(() => _log.LogWarning("ANB payroll submit refused before sending ({Code})", code));
        return new BankSubmitOutcome(BankSubmitOutcomeKind.Refused, code, RequestSent: false);
    }

    private static ByteArrayContent FilePart(byte[] content, string fileName, string part)
    {
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        file.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "\"" + part + "\"",
            FileName = "\"" + fileName + "\"",
        };
        return file;
    }

    private static bool HashEquals(byte[] content, string? expected)
    {
        if (string.IsNullOrEmpty(expected) || expected.Length != 64) return false;
        var actual = Encoding.ASCII.GetBytes(AnbConnectCsvGenerator.Sha256Hex(content));
        return CryptographicOperations.FixedTimeEquals(actual, Encoding.ASCII.GetBytes(expected.ToLowerInvariant()));
    }

    /// <summary>The spec's optional headers, or null when a value could split a header line.</summary>
    private static List<(string, string)>? OptionalHeaders(AnbSubmitHeaders? h)
    {
        if (h is null) return null;
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
        var id = bankPayrollId;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_limits.StatusReadDeadline);
        try
        {
            return await ReadAllPagesAsync(endpoint, scope, credential, id, profileId, deadline.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (BankChannelException) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new BankChannelException(BankChannelCodes.PollDeadline);
        }
        catch (BankChannelException)
        {
            throw;
        }
        catch (Exception) when (deadline.IsCancellationRequested)
        {
            throw new BankChannelException(BankChannelCodes.PollDeadline);
        }
        catch (Exception ex)
        {
            var kind = ex.GetType().Name;
            Safe(() => _log.LogWarning("ANB payroll status read failed ({ExceptionType})", kind));
            throw new BankChannelException(BankChannelCodes.HttpError);
        }
    }

    private async Task<AnbPayrollStatusSnapshot> ReadAllPagesAsync(Endpoint endpoint, BankChannelScope scope,
        AnbClientCredential credential, string id, string? profileId, CancellationToken ct)
    {
        var merger = new LineMerger();
        BankPayrollPhase? phase = null;
        var payrollStatus = string.Empty;
        string? reason = null;
        DateTimeOffset? payrollCreatedAt = null;
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
            if (parsed.ItemCount == 0 && parsed.Data.Count > 0) throw new BankChannelException(BankChannelCodes.PollInconsistent);
            payrollCreatedAt ??= parsed.CreatedAt;

            if (itemCount is null)
            {
                if (parsed.ItemCount > MaxItemCount) throw new BankChannelException(BankChannelCodes.PollInconsistent);
                var expectedPages = (parsed.ItemCount + MaxTake - 1) / MaxTake;
                var fits = parsed.ItemCount == 0 ? parsed.PageCount is 0 or 1 : parsed.PageCount == expectedPages;
                if (!fits) throw new BankChannelException(BankChannelCodes.PollInconsistent);
                itemCount = parsed.ItemCount;
                pageCount = parsed.PageCount;
            }
            else if (itemCount != parsed.ItemCount || pageCount != parsed.PageCount)
            {
                throw new BankChannelException(BankChannelCodes.PollInconsistent);
            }

            var pagePhase = AnbPayrollStatusMapper.MapPayrollStatus(parsed.Status);
            if (phase is { } prior && AnbPayrollStatusMapper.IsTerminal(prior) && pagePhase != prior)
                throw new BankChannelException(BankChannelCodes.PollInconsistent); // a terminal payroll never changes
            var advanced = AnbPayrollStatusMapper.AdvancePayroll(phase, pagePhase, out _);
            if (phase is null || advanced != phase)
            {
                payrollStatus = parsed.Status.Trim();
                reason = parsed.Reason;
            }
            phase = advanced;

            foreach (var line in parsed.Data) merger.Add(line);

            var morePages = page < pageCount;
            if (parsed.HasNextPage != morePages) throw new BankChannelException(BankChannelCodes.PollInconsistent);
            if (!morePages) break;
            page++;
        }

        var lines = merger.Lines();
        var complete = itemCount > 0 && merger.DistinctKeys == itemCount;
        Safe(() => _log.LogInformation(
            "ANB payroll status read {BankPayrollId}: {Phase}, {Pages} page(s), {Distinct} of {ItemCount} line(s), {Collapsed} duplicate(s), complete {Complete}",
            id, phase, pages, merger.DistinctKeys, itemCount, merger.Collapsed, complete));
        return new AnbPayrollStatusSnapshot(id, payrollStatus, MaskText(reason), lines, itemCount ?? 0, pages, merger.Collapsed, complete,
            payrollCreatedAt);
    }

    /// <summary>
    /// Merges lines by (employeeId, account), which the generator makes unique per batch. A repeated line
    /// never moves backwards; two DIFFERENT terminal statuses for one line are both kept so the existing
    /// confirmation import refuses that employee as conflicting instead of picking one. A line with an
    /// unpublished status word is kept verbatim for the mapper to hold; it never stops the read.
    /// </summary>
    private sealed class LineMerger
    {
        private readonly Dictionary<(string, string), List<AnbStatusLine>> _byKey = new();
        private readonly List<(string, string)> _order = new();
        public int Collapsed { get; private set; }
        public int DistinctKeys => _order.Count;

        public void Add(AnbStatusLine line)
        {
            var key = (line.EmployeeId.Trim(), line.EmployeeAccountNumber.Trim());
            if (!_byKey.TryGetValue(key, out var held))
            {
                _byKey[key] = new List<AnbStatusLine> { line };
                _order.Add(key);
                return;
            }

            if (held.Any(h => string.Equals(h.PaymentStatus.Trim(), line.PaymentStatus.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                Collapsed++;
                return;
            }

            if (!AnbPayrollStatusMapper.TryMapTransactionStatus(line.PaymentStatus, out var phase))
            {
                held.Add(line); // unknown word: kept so the mapper can hold it
                return;
            }

            var known = held.Where(h => AnbPayrollStatusMapper.TryMapTransactionStatus(h.PaymentStatus, out _)).ToList();
            if (known.Count == 0) { held.Add(line); return; }

            var knownPhases = known.Select(h => { AnbPayrollStatusMapper.TryMapTransactionStatus(h.PaymentStatus, out var p); return p; }).ToList();
            var heldTerminal = knownPhases.Any(AnbPayrollStatusMapper.IsTerminal);
            if (AnbPayrollStatusMapper.IsTerminal(phase))
            {
                if (heldTerminal) { held.Add(line); return; } // conflicting terminals: keep both, loudly
                foreach (var k in known) held.Remove(k);
                held.Add(line);
                Collapsed++;
                return;
            }

            Collapsed++;
            if (!heldTerminal && Rank(phase) > knownPhases.Max(Rank))
            {
                foreach (var k in known) held.Remove(k);
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
        int Page, int ItemCount, int PageCount, bool HasNextPage, DateTimeOffset? CreatedAt)
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
                    Int(meta, "page"), Int(meta, "itemCount"), Int(meta, "pageCount"), Bool(meta, "hasNextPage"), Date(root, "createdAt"));
            }
            catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or OverflowException)
            {
                throw Unparsable();
            }
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
            if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetDecimal(out var d)) throw Unparsable();
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
        var id = bankPayrollId;
        var url = endpoint.Url($"/payroll/{Uri.EscapeDataString(id)}/WPS");

        using var response = await SendReadAsync(endpoint, scope, credential, url, profileId, "*/*", ct);
        var status = (int)response.StatusCode;
        if (status == 404) throw new BankChannelException(BankChannelCodes.WpsNotReady, status);
        ThrowIfReadFailed(status);

        byte[] bytes;
        try
        {
            bytes = await ReadBoundedAsync(response, MaxWpsBytes, ct) ?? throw new BankChannelException(BankChannelCodes.WpsTooLarge);
        }
        catch (BankChannelException) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { throw new BankChannelException(BankChannelCodes.HttpError, status); }

        string? name = null;
        try
        {
            var disposition = response.Content.Headers.ContentDisposition;
            name = (disposition?.FileNameStar ?? disposition?.FileName)?.Trim('"');
        }
        catch (Exception) { name = null; }
        var file = new BankWpsFile(bytes, AnbConnectCsvGenerator.Sha256Hex(bytes), SafeMediaType(response), SafeFileName(name) ? name : null);
        Safe(() => _log.LogInformation("ANB WPS file downloaded for {BankPayrollId}: {Length} bytes, sha256 {Sha}", id, bytes.Length, file.Sha256));
        return file;
    }

    private static string? SafeMediaType(HttpResponseMessage response)
    {
        try { return response.Content.Headers.ContentType?.MediaType; }
        catch (Exception) { return null; }
    }

    // ── Shared read plumbing ────────────────────────────────────────────────────────────────────

    private async Task<(Endpoint, AnbClientCredential)> PrepareReadAsync(BankChannelScope scope, string bankPayrollId,
        string? profileId, CancellationToken ct)
    {
        var endpoint = Resolve(_config);
        if (!SameEnvironment(endpoint, scope)) throw new BankChannelRefusedException(BankChannelCodes.EnvironmentMismatch);
        if (bankPayrollId is null || !PayrollIdShape().IsMatch(bankPayrollId))
            throw new BankChannelRefusedException(BankChannelCodes.PayrollIdInvalid);
        if (OptionalHeaders(new AnbSubmitHeaders(ProfileId: profileId)) is null)
            throw new BankChannelRefusedException(BankChannelCodes.SubmitHeaderInvalid);
        var (credential, code) = await LoadCredentialAsync(scope, endpoint, ct);
        if (credential is null) throw new BankChannelRefusedException(code);
        return (endpoint, credential);
    }

    private async Task<string> ReadJsonAsync(Endpoint endpoint, BankChannelScope scope, AnbClientCredential credential,
        string url, string? profileId, CancellationToken ct)
    {
        using var response = await SendReadAsync(endpoint, scope, credential, url, profileId, "application/json", ct);
        var status = (int)response.StatusCode;
        ThrowIfReadFailed(status);
        byte[]? bytes;
        try { bytes = await ReadBoundedAsync(response, MaxPageBytes, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { throw new BankChannelException(BankChannelCodes.HttpError, status); }
        if (bytes is null) throw new BankChannelException(BankChannelCodes.ResponseTooLarge, status);
        return Encoding.UTF8.GetString(bytes);
    }

    private static void ThrowIfReadFailed(int status)
    {
        if (status == 401) throw new BankChannelException(BankChannelCodes.Unauthorized, status);
        if (status is < 200 or >= 300) throw new BankChannelException(BankChannelCodes.HttpError, status);
    }

    /// <summary>GET with the bearer token. A 401 drops the cached token and tries once more with a fresh one.</summary>
    private async Task<HttpResponseMessage> SendReadAsync(Endpoint endpoint, BankChannelScope scope, AnbClientCredential credential,
        string url, string? profileId, string accept, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var token = await GetTokenAsync(endpoint, scope, credential, ct);
            if (token.Value is null) throw new BankChannelException(token.Code);

            using var get = new HttpRequestMessage(HttpMethod.Get, url);
            get.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
            get.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
            if (!string.IsNullOrEmpty(profileId)) get.Headers.TryAddWithoutValidation("profileid", profileId);

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(get, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                var kind = ex.GetType().Name;
                Safe(() => _log.LogWarning("ANB payroll read failed in transport ({ExceptionType})", kind));
                throw new BankChannelException(BankChannelCodes.HttpError);
            }

            if (response.StatusCode != HttpStatusCode.Unauthorized || attempt > 0) return response;
            response.Dispose();
            _tokens.TryRemove(TokenKey(endpoint, scope, credential), out _);
            Safe(() => _log.LogInformation("ANB payroll read got 401; re-acquiring the token once"));
        }
    }

    /// <summary>Reads at most <paramref name="maxBytes"/>; null when the body is larger. Never buffers more.</summary>
    private static async Task<byte[]?> ReadBoundedAsync(HttpResponseMessage response, int maxBytes, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is { } declared && declared > maxBytes) return null;
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    // ── Token ───────────────────────────────────────────────────────────────────────────────────

    private sealed record CachedToken(string Value, DateTimeOffset UseUntil);

    private async Task<(string? Value, string Code)> GetTokenAsync(Endpoint endpoint, BankChannelScope scope,
        AnbClientCredential credential, CancellationToken ct)
    {
        var key = TokenKey(endpoint, scope, credential);
        var now = _clock.GetUtcNow();
        if (_tokens.TryGetValue(key, out var cached))
        {
            if (cached.UseUntil > now) return (cached.Value, string.Empty);
            _tokens.TryRemove(key, out _);
        }
        if (_tokens.Count > 1_000)
            foreach (var stale in _tokens.Where(t => t.Value.UseUntil <= now).Select(t => t.Key).ToList())
                _tokens.TryRemove(stale, out _);

        try
        {
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

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var status = (int)response.StatusCode;
            string? text;
            try
            {
                var bytes = await ReadBoundedAsync(response, MaxTokenResponseBytes, ct);
                text = bytes is null ? null : Encoding.UTF8.GetString(bytes);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { text = null; }

            if (status is < 200 or >= 300)
            {
                // Never the raw body: an identity provider may echo the client id or the request back. A 3xx is
                // never followed (the secret must not be re-sent) and is a refusal like any other non-2xx.
                var oauthError = OAuthErrorCode(text);
                Safe(() => _log.LogWarning("ANB token request refused: HTTP {Status} {OAuthError}", status, oauthError));
                return (null, status is 400 or 401 or 403 ? BankChannelCodes.TokenRejected : BankChannelCodes.TokenUnavailable);
            }

            var token = ParseToken(text);
            if (token is null)
            {
                Safe(() => _log.LogWarning("ANB token response was not a usable bearer token (HTTP {Status})", status));
                return (null, BankChannelCodes.TokenUnavailable);
            }

            var (value, lifetime) = token.Value;
            if (lifetime is { } life && life > TokenSkew)
                _tokens[key] = new CachedToken(value, now + life - TokenSkew);
            return (value, string.Empty);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // the caller cancelled before anything payroll-related was sent
        }
        catch (Exception ex)
        {
            var kind = ex.GetType().Name;
            Safe(() => _log.LogWarning("ANB token request failed ({ExceptionType})", kind));
            return (null, BankChannelCodes.TokenUnavailable);
        }
    }

    /// <summary>Bearer token and its cache lifetime, clamped to one hour; null lifetime means do not cache.</summary>
    private static (string Value, TimeSpan? Lifetime)? ParseToken(string? text)
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

            TimeSpan? lifetime = null;
            if (root.TryGetProperty("expires_in", out var e) && e.ValueKind == JsonValueKind.Number
                && e.TryGetDouble(out var seconds) && double.IsFinite(seconds) && seconds > 0)
                lifetime = seconds >= MaxTokenLifetime.TotalSeconds ? MaxTokenLifetime : TimeSpan.FromSeconds(seconds);
            return (value, lifetime);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or OverflowException or ArgumentException or InvalidOperationException)
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

    /// <summary>Tenant + company + environment + both URLs + scope + credential version + a hash of the client id
    /// AND secret, so a secret changed under the same version never reuses a token.</summary>
    private static string TokenKey(Endpoint endpoint, BankChannelScope scope, AnbClientCredential credential) =>
        string.Join("|",
            scope.TenantId.ToString("N"), scope.CompanyId?.ToString("N") ?? "-",
            endpoint.Environment, endpoint.TokenUrl.AbsoluteUri, endpoint.BaseUrl.AbsoluteUri, endpoint.Scope, credential.Version,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(credential.ClientId + "\n" + credential.ClientSecret)))[..32]);

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
            (nameof(IAnbEndpointConfig.AllowedHostSuffixes), c.AllowedHostSuffixes),
        };
        var missing = values.Where(v => string.IsNullOrWhiteSpace(v.Value)).Select(v => v.Name).ToList();
        if (missing.Count > 0) throw new BankChannelRefusedException(BankChannelCodes.ConfigMissing, missing);

        var invalid = new List<string>();
        var suffixes = HostSuffixes(c.AllowedHostSuffixes!);
        if (suffixes.Count == 0) invalid.Add(nameof(IAnbEndpointConfig.AllowedHostSuffixes));
        var baseUrl = HttpsUri(c.BaseUrl!, suffixes);
        var tokenUrl = HttpsUri(c.TokenUrl!, suffixes);
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

    /// <summary>
    /// Lower-cased DNS suffixes, or an empty list when the setting is unusable. Every non-blank entry must be a
    /// registrable domain: letters, digits and hyphens in at least two labels, not a bare TLD and not a known
    /// public second-level suffix such as com.sa or co.uk (a short list, not the full public-suffix list: the
    /// value is operator configuration). One bad entry refuses the whole list; a wildcard or dot-only entry is bad.
    /// </summary>
    private static List<string> HostSuffixes(string raw)
    {
        var result = new List<string>();
        foreach (var entry in raw.Split(','))
        {
            var s = entry.Trim().Trim('.').ToLowerInvariant();
            if (s.Length == 0)
            {
                if (entry.Trim().Length > 0) return new List<string>(); // "." or ".." alone
                continue;
            }
            var labels = s.Split('.');
            if (labels.Any(l => l.Length == 0 || l.Length > 63 || l[0] == '-' || l[^1] == '-'
                                || !l.All(ch => ch is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-')))
                return new List<string>();
            if (labels.Length < 2) return new List<string>();
            if (labels.Length == 2 && labels[1].Length == 2 && PublicSecondLevelLabels.Contains(labels[0])) return new List<string>();
            if (!result.Contains(s)) result.Add(s);
        }
        return result;
    }

    private static readonly HashSet<string> PublicSecondLevelLabels = new(StringComparer.Ordinal)
    {
        "com", "net", "org", "gov", "edu", "med", "sch", "ac", "co", "mil", "ltd", "plc", "nic", "biz", "info",
    };

    /// <summary>Absolute https, no user info, query or fragment, a DNS name (never an IP literal) with at least
    /// two labels, under one of the allowed suffixes.</summary>
    private static Uri? HttpsUri(string value, IReadOnlyList<string> suffixes)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var u)) return null;
        if (u.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(u.UserInfo) || !string.IsNullOrEmpty(u.Query)
            || !string.IsNullOrEmpty(u.Fragment) || u.HostNameType != UriHostNameType.Dns)
            return null;
        var host = u.IdnHost.ToLowerInvariant();
        if (!host.Contains('.') || host == "localhost" || host.EndsWith(".localhost", StringComparison.Ordinal)) return null;
        return suffixes.Any(s => host == s || host.EndsWith("." + s, StringComparison.Ordinal)) ? u : null;
    }

    private static bool SameEnvironment(Endpoint endpoint, BankChannelScope scope) =>
        string.Equals(endpoint.Environment, scope.Environment?.Trim(), StringComparison.OrdinalIgnoreCase);

    // ── Text hygiene ────────────────────────────────────────────────────────────────────────────

    /// <summary>A failing log sink must never turn a bank answer into an exception.</summary>
    private static void Safe(Action log)
    {
        try { log(); }
        catch (Exception) { /* logging is best effort here */ }
    }

    /// <summary>Bank free text, cleaned for display: every value we sent and every secret removed, IBAN and
    /// ID shapes masked (on the full text, before truncation), control characters dropped.</summary>
    private static string? Clean(string? message, IReadOnlyList<string> redact)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        var text = message.Length > MaxSubmitResponseBytes ? message[..MaxSubmitResponseBytes] : message;
        foreach (var value in redact.Where(v => !string.IsNullOrEmpty(v) && v.Length >= 3).OrderByDescending(v => v.Length))
            if (text.Contains(value, StringComparison.Ordinal)) text = text.Replace(value, "***", StringComparison.Ordinal);
        text = SensitiveFieldClassifier.MaskEmbedded(text);
        text = new string(text.Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        return text.Length > MaxMessageLength ? text[..MaxMessageLength] : text;
    }

    private static string? MaskText(string? text) => Clean(text, Array.Empty<string>());

    private static string? WordOrNull(string? s) => s is not null && AnbStatusWords.IsWord(s) ? s.Trim() : null;

    private static bool SafeFileName(string? name) => name is not null && FileNameShape().IsMatch(name);

    [GeneratedRegex(@"\A[A-Za-z0-9-]{1,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex PayrollIdShape();

    [GeneratedRegex(@"\A[A-Za-z0-9._-]{1,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex FileNameShape();
}
