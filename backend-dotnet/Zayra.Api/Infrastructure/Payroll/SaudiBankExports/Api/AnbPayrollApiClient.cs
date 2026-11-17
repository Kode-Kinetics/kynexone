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
using Zayra.Api.Infrastructure.Government;
using Zayra.Api.Infrastructure.Government.Credentials;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports.Channels;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll.SaudiBankExports.Api;

/// <summary>
/// Client for ANB's published Payroll Services API (OpenAPI v0.0.1): <c>POST /payroll/v2</c>,
/// <c>GET /payroll/v2/{id}</c>, <c>GET /payroll/{id}/WPS</c>. Used through <see cref="AnbPayrollChannel"/>, which only the gated
/// factory builds. NOT LIVE: nothing registers it. Bank acceptance is not verified.
///
/// <para>TRANSPORT (CTO directive 1, A1 and E4). It has NO HTTP client of its own. Every request, the token grant included, goes
/// through <see cref="GovernmentHttp"/>: the <c>gov-bank_anb</c> named client (SsrfGuard primary handler, no redirects, no proxy,
/// no retry), base and token URLs from the operator's allow-list under <c>Government:Banks:Anb</c> per environment (a sandbox URL
/// may not be a production host), the token path from the same tree, and the credential from the government credential store,
/// bound to its body, environment and hosts. Its own settings (<see cref="ScopeSetting"/>, <see cref="ClientAuthMethodSetting"/>)
/// live in that tree too.</para>
///
/// <para>THE LIVE GATE (E4). Before every token request and every submit, the client asks the live gate itself for a live-WRITE
/// decision for this tenant, bank and environment (four gates; in production also the recorded legal clearance; the sandbox needs
/// none) and refuses with <see cref="BankChannelCodes.GateRefused"/> otherwise. A read asks for an open read decision.</para>
///
/// <para>MONEY RULES</para>
/// <list type="bullet">
/// <item>Submit needs the recorder's receipt for the committed intent of this tenant, company, bank, environment and exactly these
/// files. The transport spends it immediately before the socket: one send per receipt.</item>
/// <item>Submit copies the frozen files once, checks the copies against the SHA-256 recorded when they were frozen and the header's
/// count and total against the body, re-hashes the copies immediately before the send, and sends those copies. It never
/// regenerates.</item>
/// <item>Exactly one POST per <see cref="SubmitAsync"/>, never retried; a redirect is refused, never followed.</item>
/// <item><see cref="SubmitAsync"/> never throws except <see cref="ArgumentNullException"/>. Anything that goes wrong before the
/// POST is <see cref="BankSubmitOutcomeKind.Refused"/> (cancellation and every transport refusal before the socket included). Once
/// the POST was attempted, ANY failure, of any exception type, is <see cref="BankSubmitOutcomeKind.Unknown"/>.</item>
/// <item>Reads (status, WPS) may re-acquire the token once on a 401; nothing else is retried.</item>
/// </list>
/// No response body, secret, token, IBAN or ID is logged or put into an exception, and every response is read through a byte cap.
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

    /// <summary>The ledger, policy and transport body of ANB.</summary>
    public const string Body = GovernmentBodies.BankAnb;
    /// <summary>OAuth scope (not published by ANB): <c>Government:Banks:Anb:OAuth:Scope</c>.</summary>
    public const string ScopeSetting = "OAuth:Scope";
    /// <summary><c>client_secret_basic</c> or <c>client_secret_post</c> (not published by ANB): <c>Government:Banks:Anb:OAuth:ClientAuthMethod</c>.</summary>
    public const string ClientAuthMethodSetting = "OAuth:ClientAuthMethod";
    public static string ScopeKey => GovernmentHttp.SettingKey(Body, ScopeSetting);
    public static string ClientAuthMethodKey => GovernmentHttp.SettingKey(Body, ClientAuthMethodSetting);

    private const string SubmitPath = "/payroll/v2";
    private const int MaxSubmitResponseBytes = 16 * 1024;
    private const int MaxTokenResponseBytes = 16 * 1024;
    private const int MaxPageBytes = 256 * 1024;
    private static readonly TimeSpan TokenSkew = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaxTokenLifetime = TimeSpan.FromHours(1);

    private readonly GovernmentHttp _http;
    private readonly IBankCredentialSource _credentials;
    private readonly IBankChannelGate _gate;
    private readonly ILogger<AnbPayrollApiClient> _log;
    private readonly TimeProvider _clock;
    private readonly AnbClientLimits _limits;
    private readonly ConcurrentDictionary<string, CachedToken> _tokens = new(StringComparer.Ordinal);

    // Internal: built only by AnbGatedChannelFactory, behind GatedBankPayrollChannel (ratchet I). Tests reach it through InternalsVisibleTo.
    // The transport is GovernmentHttp and nothing else: there is no handler, client or factory parameter to inject.
    internal AnbPayrollApiClient(GovernmentHttp http, IBankCredentialSource credentials, IBankChannelGate gate,
        ILogger<AnbPayrollApiClient> log, TimeProvider? clock = null, AnbClientLimits? limits = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _clock = clock ?? TimeProvider.System;
        _limits = limits ?? AnbClientLimits.Default;
        _ = OAuthSettings(); // fail closed at construction
    }

    // ── Submit ──────────────────────────────────────────────────────────────────────────────────

    private sealed record Prepared(
        BankChannelScope Scope, Endpoint Endpoint, UnsealedGovernmentCredential Credential, string Token, GovernmentLiveDecision Decision,
        GovernmentExchangeIntentReceipt Intent, byte[] Header, byte[] Body, string HeaderSha, string BodySha,
        List<(string Name, string Value)> Headers, IReadOnlyList<string> Redact)
    {
        // Security P3-3: the generated ToString would print the token, the credential and the redaction list.
        public override string ToString() => $"Prepared(header={HeaderSha}, body={BodySha})";
    }

    public async Task<BankSubmitOutcome> SubmitAsync(AnbSubmitRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // ── Before the POST: every failure is a refusal, and no socket is opened for the POST. ──
        Prepared prepared;
        try
        {
            var (ready, code, reason) = await PrepareSubmitAsync(request, ct);
            if (ready is null) return Refuse(code!, reason);
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
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(_limits.RequestTimeout);

            using var response = await _http.ExchangeAsync(prepared.Decision, prepared.Credential, HttpMethod.Post, SubmitPath, form,
                post => Authorize(post, prepared.Token, "application/json", prepared.Headers), deadline.Token, prepared.Intent,
                HttpCompletionOption.ResponseHeadersRead);
            status = (int)response.StatusCode;

            string? text = null;
            try
            {
                var bytes = await ReadBoundedAsync(response, MaxSubmitResponseBytes, deadline.Token);
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
        catch (GovernmentTransportRefusedException refused) when (!refused.RequestSent)
        {
            // The transport refused before opening the socket (gate, host, credential binding, intent): nothing left.
            return Refuse(TransportCode(refused.Code));
        }
        catch (Exception ex)
        {
            // The request may have left in full. Whether the bank holds the payroll is unknown.
            var kind = ex.GetType().Name;
            var sentStatus = (ex as GovernmentTransportRefusedException)?.HttpStatus ?? status;
            var code = sentStatus is >= 200 and < 300 ? BankChannelCodes.AcceptedWithoutId : BankChannelCodes.OutcomeUnknown;
            Safe(() => _log.LogWarning(
                "ANB payroll submit outcome unknown after {ExceptionType}; HTTP {Status}; files {HeaderSha} {LinesSha}",
                kind, sentStatus, prepared.HeaderSha, prepared.BodySha));
            return new BankSubmitOutcome(BankSubmitOutcomeKind.Unknown, code, RequestSent: true, HttpStatus: sentStatus,
                SentHeaderSha256: prepared.HeaderSha, SentBodySha256: prepared.BodySha);
        }
    }

    private async Task<(Prepared? Ready, string? Code, string? Reason)> PrepareSubmitAsync(AnbSubmitRequest request, CancellationToken ct)
    {
        // The committed intent of exactly this call (the transport refuses a write without it, and spends it on the send).
        if (!GovernmentEnvironments.IsKnown(request.Scope?.Environment)) return (null, BankChannelCodes.EnvironmentMismatch, null);
        var intent = request.Intent;
        if (intent is null) return (null, BankChannelCodes.IntentMissing, null);
        if (!IntentFor(intent, request.Scope)) return (null, BankChannelCodes.IntentMismatch, null);

        // E4: the live gate, for the write grade, before anything else (and so before the token request).
        var (decision, refusal) = await DecideAsync(request.Scope, write: true, ct);
        if (decision is null) return (null, BankChannelCodes.GateRefused, refusal);

        Endpoint endpoint;
        try { endpoint = Resolve(request.Scope); }
        catch (BankChannelRefusedException ex) { return (null, ex.Code, null); }

        if (request.Header is null || request.Body is null) return (null, BankChannelCodes.ArtifactInconsistent, null);
        if (!string.Equals(request.Header.FileName, AnbConnectCsvGenerator.HeaderFileName, StringComparison.Ordinal)
            || !string.Equals(request.Body.FileName, AnbConnectCsvGenerator.BodyFileName, StringComparison.Ordinal))
            return (null, BankChannelCodes.ArtifactInconsistent, null);

        // One private copy of each file. Everything below checks, and the POST sends, these copies only.
        var header = request.Header.Content is null ? null : (byte[])request.Header.Content.Clone();
        var body = request.Body.Content is null ? null : (byte[])request.Body.Content.Clone();
        if (header is null || body is null
            || !HashEquals(header, request.Header.ExpectedSha256) || !HashEquals(body, request.Body.ExpectedSha256))
            return (null, BankChannelCodes.ArtifactHashMismatch, null);
        // The receipt was committed for exactly these two files.
        if (!IntentBindsFiles(intent, request.Header.ExpectedSha256, request.Body.ExpectedSha256))
            return (null, BankChannelCodes.IntentMismatch, null);

        var preflight = AnbArtifactPreflight.Check(header, body, request.Headers);
        if (preflight.Code is not null) return (null, preflight.Code, null);

        var headers = OptionalHeaders(request.Headers);
        if (headers is null) return (null, BankChannelCodes.SubmitHeaderInvalid, null);

        var (credential, credentialCode) = await LoadCredentialAsync(request.Scope, endpoint, ct);
        if (credential is null) return (null, credentialCode, null);

        var token = await GetTokenAsync(endpoint, request.Scope, credential, decision, ct);
        if (token.Value is null) return (null, token.Code, token.Reason);

        var redact = preflight.SentValues.Concat(new[] { credential.ClientIdentifier!, credential.Secret, token.Value }).ToList();
        return (new Prepared(request.Scope, endpoint, credential, token.Value, decision, intent, header, body,
            AnbConnectCsvGenerator.Sha256Hex(header), AnbConnectCsvGenerator.Sha256Hex(body), headers, redact), null, null);
    }

    /// <summary>The receipt is for this tenant, company, bank and environment.</summary>
    private static bool IntentFor(GovernmentExchangeIntentReceipt intent, BankChannelScope scope) =>
        intent.TenantId == scope.TenantId && intent.CompanyId == scope.CompanyId && intent.Body == Body
        && string.Equals(intent.Environment, scope.Environment, StringComparison.Ordinal);

    private static bool IntentBindsFiles(GovernmentExchangeIntentReceipt intent, string headerSha, string bodySha)
    {
        var bound = new[] { intent.ArtifactSha256, intent.ArtifactSecondarySha256 }.OfType<string>().ToList();
        return bound.Count == 2 && !string.Equals(headerSha, bodySha, StringComparison.OrdinalIgnoreCase)
            && bound.Contains(headerSha, StringComparer.OrdinalIgnoreCase) && bound.Contains(bodySha, StringComparer.OrdinalIgnoreCase);
    }

    private async Task<(UnsealedGovernmentCredential? Credential, string Code)> LoadCredentialAsync(BankChannelScope scope, Endpoint endpoint,
        CancellationToken ct)
    {
        UnsealedGovernmentCredential? credential;
        try
        {
            credential = await _credentials.OpenAsync(Body, scope, ct);
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
        if (!CredentialFits(credential, endpoint, scope)) return (null, BankChannelCodes.CredentialEndpointMismatch);
        return (credential, string.Empty);
    }

    /// <summary>
    /// A credential is used only for ANB, the environment and both hosts it was issued for, as an OAuth client with a client id, and
    /// for the scope's company or tenant-wide. The transport checks the body, environment and hosts again before it sends.
    /// </summary>
    private static bool CredentialFits(UnsealedGovernmentCredential c, Endpoint endpoint, BankChannelScope scope) =>
        c.Body == Body
        && string.Equals(c.Environment, endpoint.Environment, StringComparison.Ordinal)
        && c.Kind == GovernmentCredentialKinds.OAuthClient
        && !string.IsNullOrWhiteSpace(c.ClientIdentifier) && !string.IsNullOrEmpty(c.Secret)
        && (c.CompanyId is null || c.CompanyId == scope.CompanyId)
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

        // 1xx, 408, 409, 425, 429, other 4xx, 5xx: the bank may hold the payroll. (A 3xx never gets here: the transport refuses
        // it after the send, which is Unknown as well.)
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

    /// <param name="reason">The live gate's reason code when <paramref name="code"/> is <see cref="BankChannelCodes.GateRefused"/>.</param>
    private BankSubmitOutcome Refuse(string code, string? reason = null)
    {
        Safe(() => _log.LogWarning("ANB payroll submit refused before sending ({Code}, {Reason})", code, reason));
        return new BankSubmitOutcome(BankSubmitOutcomeKind.Refused, code, RequestSent: false, Message: reason);
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

    /// <summary>Sets the bearer token, Accept and the given extra headers on a request the transport is about to send.</summary>
    private static void Authorize(HttpRequestMessage request, string token, string accept, IEnumerable<(string Name, string Value)> extra)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        foreach (var (name, value) in extra) request.Headers.TryAddWithoutValidation(name, value);
    }

    // ── Status ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>What every read of one call shares: the scope, the resolved endpoint, the credential and the open read decision.</summary>
    private sealed record ReadContext(BankChannelScope Scope, Endpoint Endpoint, UnsealedGovernmentCredential Credential, GovernmentLiveDecision Decision)
    {
        public override string ToString() => $"ReadContext({Endpoint.Environment})";
    }

    public async Task<AnbPayrollStatusSnapshot> GetStatusAsync(BankChannelScope scope, string bankPayrollId,
        string? profileId, CancellationToken ct)
    {
        var read = await PrepareReadAsync(scope, bankPayrollId, profileId, ct);
        var id = bankPayrollId;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_limits.StatusReadDeadline);
        try
        {
            return await ReadAllPagesAsync(read, id, profileId, deadline.Token);
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
        catch (BankChannelRefusedException)
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

    private async Task<AnbPayrollStatusSnapshot> ReadAllPagesAsync(ReadContext read, string id, string? profileId, CancellationToken ct)
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
            var path = $"/payroll/v2/{Uri.EscapeDataString(id)}" +
                      $"?page={page.ToString(CultureInfo.InvariantCulture)}&take={MaxTake.ToString(CultureInfo.InvariantCulture)}&order=ASC";
            var text = await ReadJsonAsync(read, path, profileId, ct);
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
        var read = await PrepareReadAsync(scope, bankPayrollId, profileId, ct);
        var id = bankPayrollId;
        var path = $"/payroll/{Uri.EscapeDataString(id)}/WPS";

        using var response = await SendReadAsync(read, path, profileId, "*/*", ct);
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

    /// <summary>The open READ decision of the live gate first, then the settings, the id and the credential. Any refusal throws
    /// <see cref="BankChannelRefusedException"/> before a socket is opened.</summary>
    private async Task<ReadContext> PrepareReadAsync(BankChannelScope scope, string bankPayrollId, string? profileId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (!GovernmentEnvironments.IsKnown(scope.Environment)) throw new BankChannelRefusedException(BankChannelCodes.EnvironmentMismatch);
        var (decision, refusal) = await DecideAsync(scope, write: false, ct);
        if (decision is null) throw new BankChannelRefusedException(BankChannelCodes.GateRefused, new[] { refusal! });
        var endpoint = Resolve(scope);
        if (bankPayrollId is null || !PayrollIdShape().IsMatch(bankPayrollId))
            throw new BankChannelRefusedException(BankChannelCodes.PayrollIdInvalid);
        if (OptionalHeaders(new AnbSubmitHeaders(ProfileId: profileId)) is null)
            throw new BankChannelRefusedException(BankChannelCodes.SubmitHeaderInvalid);
        var (credential, code) = await LoadCredentialAsync(scope, endpoint, ct);
        if (credential is null) throw new BankChannelRefusedException(code);
        return new ReadContext(scope, endpoint, credential, decision);
    }

    private async Task<string> ReadJsonAsync(ReadContext read, string path, string? profileId, CancellationToken ct)
    {
        using var response = await SendReadAsync(read, path, profileId, "application/json", ct);
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

    /// <summary>GET with the bearer token, through the transport. A 401 drops the cached token and tries once more with a fresh one
    /// (reads only: a submit is never repeated). Minting a token asks the live gate for the write grade first.</summary>
    private async Task<HttpResponseMessage> SendReadAsync(ReadContext read, string path, string? profileId, string accept, CancellationToken ct)
    {
        var extra = string.IsNullOrEmpty(profileId) ? new List<(string, string)>() : new List<(string, string)> { ("profileid", profileId) };
        for (var attempt = 0; ; attempt++)
        {
            var token = await GetTokenAsync(read.Endpoint, read.Scope, read.Credential, write: null, ct);
            if (token.Value is null)
                throw token.Code == BankChannelCodes.GateRefused
                    ? new BankChannelRefusedException(token.Code, new[] { token.Reason ?? GatedBankPayrollChannel.GateUnavailable })
                    : (Exception)new BankChannelException(token.Code);

            HttpResponseMessage response;
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(_limits.RequestTimeout);
                response = await _http.ExchangeAsync(read.Decision, read.Credential, HttpMethod.Get, path, null,
                    get => Authorize(get, token.Value, accept, extra), deadline.Token, intent: null, HttpCompletionOption.ResponseHeadersRead);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (GovernmentTransportRefusedException refused) when (!refused.RequestSent)
            {
                throw new BankChannelRefusedException(TransportCode(refused.Code));
            }
            catch (Exception ex)
            {
                var kind = ex.GetType().Name;
                Safe(() => _log.LogWarning("ANB payroll read failed in transport ({ExceptionType})", kind));
                throw new BankChannelException(BankChannelCodes.HttpError);
            }

            if (response.StatusCode != HttpStatusCode.Unauthorized || attempt > 0) return response;
            response.Dispose();
            _tokens.TryRemove(TokenKey(read.Endpoint, read.Scope, read.Credential), out _);
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

    // ── The live gate ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Asks the live gate (E4). Returns the decision only when it is open, made for ANB and for exactly this scope's environment, and,
    /// for <paramref name="write"/>, carries the write grade (four gates, plus the legal clearance in production). Otherwise the reason,
    /// and no decision: the caller refuses before any socket. A gate that throws fails closed.
    /// </summary>
    private async Task<(GovernmentLiveDecision? Decision, string? Refusal)> DecideAsync(BankChannelScope scope, bool write, CancellationToken ct)
    {
        GovernmentLiveDecision? decision;
        try
        {
            decision = await _gate.DecideAsync(Body, scope, write, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var kind = ex.GetType().Name;
            Safe(() => _log.LogWarning("ANB live gate unavailable ({ExceptionType}); refused", kind));
            return (null, GatedBankPayrollChannel.GateUnavailable);
        }
        var refusal = decision is null ? GatedBankPayrollChannel.GateUnavailable
            : !decision.IsOpen ? decision.ReasonCode ?? GatedBankPayrollChannel.GateUnavailable
            : decision.Body != Body || decision.Environment is null || decision.Environment != scope.Environment
                ? GatedBankPayrollChannel.DecisionNotForThisCall
            : write && !decision.AllowsWrites ? GatedBankPayrollChannel.WriteNotGranted
            : null;
        return refusal is null ? (decision, null) : (null, refusal);
    }

    /// <summary>A transport refusal before the socket, as a bank channel code.</summary>
    private static string TransportCode(string code) => code switch
    {
        GovernmentHttp.HostRefusedCode => BankChannelCodes.HostNotAllowed,
        GovernmentHttp.CredentialBindingCode => BankChannelCodes.CredentialEndpointMismatch,
        GovernmentHttp.EnvironmentMismatchCode => BankChannelCodes.EnvironmentMismatch,
        GovernmentHttp.WriteRefusedCode or GovernmentLivePolicy.RefusedCode => BankChannelCodes.GateRefused,
        GovernmentHttp.WriteWithoutIntentCode => BankChannelCodes.IntentMissing,
        GovernmentHttp.IntentAlreadyUsedCode => BankChannelCodes.IntentAlreadyUsed,
        GovernmentHttp.IntentCompanyMismatchCode => BankChannelCodes.IntentMismatch,
        _ => BankChannelCodes.TransportRefused,
    };

    // ── Token ───────────────────────────────────────────────────────────────────────────────────

    private sealed record CachedToken(string Value, DateTimeOffset UseUntil)
    {
        public override string ToString() => $"CachedToken(until={UseUntil:O})";
    }

    /// <param name="write">The live-write decision the caller already holds (a submit), or null to ask the gate now (a read). A token is
    /// minted only under an open write decision for this tenant, bank and environment: the token grant is a POST.</param>
    private async Task<(string? Value, string Code, string? Reason)> GetTokenAsync(Endpoint endpoint, BankChannelScope scope,
        UnsealedGovernmentCredential credential, GovernmentLiveDecision? write, CancellationToken ct)
    {
        var key = TokenKey(endpoint, scope, credential);
        var now = _clock.GetUtcNow();
        if (_tokens.TryGetValue(key, out var cached))
        {
            if (cached.UseUntil > now) return (cached.Value, string.Empty, null);
            _tokens.TryRemove(key, out _);
        }
        if (_tokens.Count > 1_000)
            foreach (var stale in _tokens.Where(t => t.Value.UseUntil <= now).Select(t => t.Key).ToList())
                _tokens.TryRemove(stale, out _);

        // E4: no token request unless the live gate grants the write grade for this call.
        if (write is null)
        {
            var (decision, refusal) = await DecideAsync(scope, write: true, ct);
            if (decision is null) return (null, BankChannelCodes.GateRefused, refusal);
            write = decision;
        }

        try
        {
            var form = new List<KeyValuePair<string, string>>
            {
                new("grant_type", GrantClientCredentials),
                new("scope", endpoint.Scope),
            };
            if (!endpoint.BasicAuth)
            {
                form.Add(new("client_id", credential.ClientIdentifier!));
                form.Add(new("client_secret", credential.Secret));
            }
            using var content = new FormUrlEncodedContent(form);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(_limits.RequestTimeout);

            using var response = await _http.GrantTokenAsync(write, credential, content, request =>
            {
                if (endpoint.BasicAuth)
                {
                    var pair = Uri.EscapeDataString(credential.ClientIdentifier!) + ":" + Uri.EscapeDataString(credential.Secret);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(pair)));
                }
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            }, deadline.Token);
            var status = (int)response.StatusCode;
            string? text;
            try
            {
                var bytes = await ReadBoundedAsync(response, MaxTokenResponseBytes, deadline.Token);
                text = bytes is null ? null : Encoding.UTF8.GetString(bytes);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { text = null; }

            if (status is < 200 or >= 300)
            {
                // Never the raw body: an identity provider may echo the client id or the request back.
                var oauthError = OAuthErrorCode(text);
                Safe(() => _log.LogWarning("ANB token request refused: HTTP {Status} {OAuthError}", status, oauthError));
                return (null, status is 400 or 401 or 403 ? BankChannelCodes.TokenRejected : BankChannelCodes.TokenUnavailable, null);
            }

            var token = ParseToken(text);
            if (token is null)
            {
                Safe(() => _log.LogWarning("ANB token response was not a usable bearer token (HTTP {Status})", status));
                return (null, BankChannelCodes.TokenUnavailable, null);
            }

            var (value, lifetime) = token.Value;
            if (lifetime is { } life && life > TokenSkew)
                _tokens[key] = new CachedToken(value, now + life - TokenSkew);
            return (value, string.Empty, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // the caller cancelled before anything payroll-related was sent
        }
        catch (Exception ex)
        {
            // Includes a refused redirect (never followed, so the secret is not re-sent) and every transport refusal: no token.
            var kind = ex.GetType().Name;
            Safe(() => _log.LogWarning("ANB token request failed ({ExceptionType})", kind));
            return (null, BankChannelCodes.TokenUnavailable, null);
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

    /// <summary>Tenant + company + environment + both URLs (the token URL with its path) + scope + credential version + a hash of the
    /// client id AND secret, so a secret changed under the same version never reuses a token.</summary>
    private static string TokenKey(Endpoint endpoint, BankChannelScope scope, UnsealedGovernmentCredential credential) =>
        string.Join("|",
            scope.TenantId.ToString("N"), scope.CompanyId?.ToString("N") ?? "-",
            endpoint.Environment, endpoint.TokenUrl.AbsoluteUri, endpoint.TokenPath, endpoint.BaseUrl.AbsoluteUri, endpoint.Scope,
            credential.Version.ToString(CultureInfo.InvariantCulture),
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(credential.ClientIdentifier + "\n" + credential.Secret)))[..32]);

    // ── Settings ────────────────────────────────────────────────────────────────────────────────

    /// <summary>One environment's endpoint, every part from <see cref="GovernmentHttp"/>'s allow-list and the Government:Banks:Anb tree.</summary>
    private sealed record Endpoint(string Environment, Uri BaseUrl, Uri TokenUrl, string TokenPath, string Scope, bool BasicAuth);

    /// <summary>The OAuth settings ANB does not publish (scope and client authentication). Missing or unusable: a refusal naming the
    /// setting, never its value.</summary>
    private (string Scope, bool BasicAuth) OAuthSettings()
    {
        var scope = _http.Setting(Body, ScopeSetting);
        var auth = _http.Setting(Body, ClientAuthMethodSetting);
        var missing = new[] { (ScopeSetting, scope), (ClientAuthMethodSetting, auth) }
            .Where(v => string.IsNullOrWhiteSpace(v.Item2)).Select(v => v.Item1).ToList();
        if (missing.Count > 0) throw new BankChannelRefusedException(BankChannelCodes.ConfigMissing, missing);

        var invalid = new List<string>();
        if (scope!.Trim().Length > 256 || scope.Any(ch => ch < 0x20 || ch > 0x7E)) invalid.Add(ScopeSetting);
        var method = auth!.Trim();
        if (method is not (AuthBasic or AuthPost)) invalid.Add(ClientAuthMethodSetting);
        if (invalid.Count > 0) throw new BankChannelRefusedException(BankChannelCodes.ConfigInvalid, invalid);
        return (scope.Trim(), method == AuthBasic);
    }

    /// <summary>
    /// The endpoint for the scope's environment. The environment must be one GovernmentHttp knows (sandbox or production; a free-text
    /// label is refused), and the base URL, token URL and token path come only from its allow-list, which refuses a sandbox URL on a
    /// production host.
    /// </summary>
    private Endpoint Resolve(BankChannelScope scope)
    {
        var environment = scope.Environment;
        if (!GovernmentEnvironments.IsKnown(environment)) throw new BankChannelRefusedException(BankChannelCodes.EnvironmentMismatch);
        var (oauthScope, basic) = OAuthSettings();
        try
        {
            return new Endpoint(environment, _http.ResolveBaseUrl(Body, environment), _http.ResolveTokenUrl(Body, environment),
                _http.ResolveTokenPath(Body, environment), oauthScope, basic);
        }
        catch (GovernmentTransportRefusedException)
        {
            throw new BankChannelRefusedException(BankChannelCodes.HostNotAllowed);
        }
    }

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
