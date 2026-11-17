using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Zayra.Api.Infrastructure.Government;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll.SaudiBankExports.Channels;

/// <summary>
/// Supplies the live decision for a bank call. An implementation only gathers the gates (the partner agreement and the kill switch
/// from configuration, the tenant's consent, the credential on file) and asks <see cref="GovernmentLivePolicy"/>, the only maker of a
/// <see cref="GovernmentLiveDecision"/>. Nothing implements it in the app yet: wiring is wave 2.
/// </summary>
public interface IBankChannelGate
{
    /// <param name="body">The bank's body key (<see cref="GovernmentBodies"/>, for example <c>bank_anb</c>).</param>
    /// <param name="write">True for a live-WRITE decision (<see cref="GovernmentLivePolicy.DecideLiveWrite(Microsoft.Extensions.Configuration.IConfiguration?, string, GovernmentLiveGates, string)"/>):
    /// the four gates plus, in production, the recorded legal clearance. False for a read decision (the four gates).</param>
    Task<GovernmentLiveDecision> DecideAsync(string body, BankChannelScope scope, bool write, CancellationToken ct);
}

/// <summary>
/// The only way a bank payroll channel is handed out (the ANB one is built solely by its gated factory; ratchet I). Before ANY call
/// that can open a socket to the bank (including the token POST the bank channels make first):
/// <list type="bullet">
/// <item><b>Submit</b> needs the recorder's receipt for a COMMITTED intent of this tenant, bank, environment and exactly these frozen
/// files, and a live-WRITE decision for the call's environment. Either missing: <see cref="BankSubmitOutcomeKind.Refused"/> with a
/// stable code, nothing sent. A receipt sends at most once in the whole process: <see cref="GovernmentHttp"/> spends it immediately
/// before the socket, and this channel burns it when its one attempt ends without a send, so it is never used twice (security P2-A;
/// no per-instance memory, so a channel built per request is covered too). The receipt is bound to the scope's company.</item>
/// <item><b>Status and WPS download</b> need an open decision for the environment. In production they also need the write grade,
/// because the bank channel may first mint a token (a POST), and the foundation's rule is that a token grant is a write.</item>
/// <item><b>The outcome</b> of each submit goes to the ledger through <see cref="IGovernmentExchangeRecorder"/>. Anything thrown
/// after the gate is <see cref="BankSubmitOutcomeKind.Unknown"/> (the request may have left), never Failed, and is never resent.</item>
/// </list>
/// </summary>
public sealed class GatedBankPayrollChannel : IBankPayrollChannel
{
    // Reasons carried in the refusal message when the decision itself has no reason code (the bank clients use the same words).
    internal const string GateUnavailable = "gate_unavailable";
    internal const string WriteNotGranted = "write_not_granted";
    internal const string DecisionNotForThisCall = "decision_not_for_this_call";

    private readonly IBankPayrollChannel _inner;
    private readonly IBankChannelGate _gate;
    private readonly IGovernmentExchangeRecorder _recorder;
    private readonly ILogger<GatedBankPayrollChannel> _log;

    public GatedBankPayrollChannel(IBankPayrollChannel inner, IBankChannelGate gate, IGovernmentExchangeRecorder recorder,
        ILogger<GatedBankPayrollChannel> log)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        Body = "bank_" + inner.BankId;
        if (!GovernmentBodies.IsKnown(Body) || !GovernmentBodies.IsBank(Body))
            throw new BankChannelRefusedException(BankChannelCodes.ConfigInvalid);
    }

    /// <summary>The ledger and policy body of the wrapped bank (<see cref="GovernmentBodies.BankAnb"/> for ANB).</summary>
    public string Body { get; }

    public string BankId => _inner.BankId;

    public bool IsLiveIntegration => _inner.IsLiveIntegration;

    public async Task<BankSubmitOutcome> SubmitAsync(BankSubmitRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var intent = request.Intent;
        if (intent is null) return Refused(BankChannelCodes.IntentMissing);
        if (!Binds(intent.Receipt, request)) return Refused(BankChannelCodes.IntentMismatch);
        if (!request.Files.All(f => HashMatches(f))) return Refused(BankChannelCodes.ArtifactHashMismatch);
        // A spent receipt (sent, or burnt by an earlier attempt that ended without sending) never sends again, and its ledger row
        // belongs to that first use: nothing is recorded for this one.
        if (intent.Receipt.IsSpent) return Refused(BankChannelCodes.IntentAlreadyUsed);

        var decision = await DecideAsync(request.Scope, write: true, ct);
        var refusal = WriteRefusal(decision, request.Scope);
        if (refusal is not null)
        {
            // Burnt here, so the same receipt cannot try again once the gate opens. Only the use that burns it records the refusal.
            if (!intent.Receipt.TryConsume()) return Refused(BankChannelCodes.IntentAlreadyUsed);
            await RecordAsync(intent, GovernmentExchangeStates.Refused, BankChannelCodes.GateRefused, null, null, completed: true);
            return Refused(BankChannelCodes.GateRefused, refusal);
        }

        // ── From here the inner channel may open a socket. Nothing below throws. ──
        BankSubmitOutcome outcome;
        try
        {
            outcome = await _inner.SubmitAsync(request, ct);
        }
        catch (Exception ex)
        {
            var kind = ex.GetType().Name;
            Safe(() => _log.LogWarning("Bank payroll submit ended without an answer ({ExceptionType}); outcome unknown", kind));
            outcome = new BankSubmitOutcome(BankSubmitOutcomeKind.Unknown, BankChannelCodes.OutcomeUnknown, RequestSent: true);
        }

        // Every exit below has used the receipt once. Burnt now if the transport did not spend it (an attempt refused before the
        // socket), so it is never tried again; TryConsume tells whether THIS use still owned it.
        var ownedUnspent = intent.Receipt.TryConsume();
        if (outcome.Kind == BankSubmitOutcomeKind.Refused && !outcome.RequestSent)
        {
            // Spent by another, concurrent use of the same receipt (the transport refused this one): that use owns the ledger row.
            if (outcome.Code == BankChannelCodes.IntentAlreadyUsed || !ownedUnspent) return outcome;
        }

        var live = _inner.IsLiveIntegration;
        var state = outcome.Kind switch
        {
            BankSubmitOutcomeKind.Accepted => live ? GovernmentExchangeStates.Accepted : null,
            BankSubmitOutcomeKind.RejectedStructure => GovernmentExchangeStates.RejectedStructure,
            BankSubmitOutcomeKind.RejectedAuthorization => GovernmentExchangeStates.Failed,
            BankSubmitOutcomeKind.Refused => GovernmentExchangeStates.Refused,
            _ => GovernmentExchangeStates.Unknown,
        };
        if (state is null)
        {
            // TODO(wave 2, D8): only a live adapter may record Accepted. Until the real-endpoint split lands, a non-live bank's
            // acceptance leaves the intent as it is (SubmitIntended, which still holds the batch), and nothing else is written.
            Safe(() => _log.LogInformation("Bank payroll accepted by a non-live channel; ledger acceptance deferred"));
            return outcome;
        }
        await RecordAsync(intent, state, outcome.Code, outcome.HttpStatus, outcome.BankPayrollId,
            completed: state != GovernmentExchangeStates.Unknown);
        return outcome;
    }

    public async Task<BankStatusSnapshot> GetStatusAsync(BankChannelScope scope, string bankPayrollId, BankBatchBinding binding,
        CancellationToken ct)
    {
        await RequireReadAsync(scope, ct);
        return await _inner.GetStatusAsync(scope, bankPayrollId, binding, ct);
    }

    public async Task<BankWpsFile> DownloadWpsAsync(BankChannelScope scope, string bankPayrollId, string? profileId, CancellationToken ct)
    {
        await RequireReadAsync(scope, ct);
        return await _inner.DownloadWpsAsync(scope, bankPayrollId, profileId, ct);
    }

    private async Task RequireReadAsync(BankChannelScope scope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var read = await DecideAsync(scope, write: false, ct);
        var refusal = read is null ? GateUnavailable : !read.IsOpen ? read.ReasonCode ?? GateUnavailable
            : !IsForThisCall(read, scope) ? DecisionNotForThisCall : null;
        // A read may first mint a token (a POST to the bank's token host): in production that needs the write grade.
        if (refusal is null && scope.Environment == GovernmentEnvironments.Production)
            refusal = WriteRefusal(await DecideAsync(scope, write: true, ct), scope);
        if (refusal is not null) throw new BankChannelRefusedException(BankChannelCodes.GateRefused, new[] { refusal });
    }

    /// <summary>Null when <paramref name="decision"/> allows a write for this bank and environment, else the reason.</summary>
    private string? WriteRefusal(GovernmentLiveDecision? decision, BankChannelScope scope) =>
        decision is null ? GateUnavailable
        : !decision.IsOpen ? decision.ReasonCode ?? GateUnavailable
        : !IsForThisCall(decision, scope) ? DecisionNotForThisCall
        : !decision.AllowsWrites ? WriteNotGranted
        : null;

    private bool IsForThisCall(GovernmentLiveDecision decision, BankChannelScope scope) =>
        decision.Body == Body && decision.Environment is { } environment && environment == scope.Environment;

    private async Task<GovernmentLiveDecision?> DecideAsync(BankChannelScope scope, bool write, CancellationToken ct)
    {
        try
        {
            return await _gate.DecideAsync(Body, scope, write, ct);
        }
        catch (Exception ex)
        {
            var kind = ex.GetType().Name;
            Safe(() => _log.LogWarning("Bank live gate unavailable ({ExceptionType}); refused", kind));
            return null; // fail closed
        }
    }

    /// <summary>The receipt is for this tenant, this company, this bank, this environment, and exactly the frozen files of the request.</summary>
    private bool Binds(GovernmentExchangeIntentReceipt receipt, BankSubmitRequest request)
    {
        if (receipt.TenantId != request.Scope.TenantId || receipt.CompanyId != request.Scope.CompanyId || receipt.Body != Body
            || receipt.Environment != request.Scope.Environment)
            return false;
        var bound = new[] { receipt.ArtifactSha256, receipt.ArtifactSecondarySha256 }.OfType<string>().ToList();
        var sent = request.Files.Select(f => f.ExpectedSha256).ToList();
        return bound.Count > 0 && sent.Count == bound.Count
            && sent.Distinct(StringComparer.OrdinalIgnoreCase).Count() == sent.Count
            && sent.All(h => bound.Contains(h, StringComparer.OrdinalIgnoreCase));
    }

    private static bool HashMatches(BankArtifactFile file) =>
        string.Equals(Convert.ToHexString(SHA256.HashData(file.Content)), file.ExpectedSha256, StringComparison.OrdinalIgnoreCase);

    /// <summary>Records an outcome on the intent's ledger row. Never throws and is not cancelled with the caller: the outcome of a
    /// call that may have been made must reach the ledger.</summary>
    private async Task RecordAsync(BankSubmitIntent intent, string state, string code, int? httpStatus, string? bankReference, bool completed)
    {
        try
        {
            await _recorder.UpdateAsync(intent.Receipt.TenantId, intent.Receipt.ExchangeId,
                new GovernmentExchangeOutcome(state, intent.Context, HttpStatus: httpStatus, ErrorCode: code,
                    ExternalReference: bankReference, Completed: completed),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            var kind = ex.GetType().Name;
            Safe(() => _log.LogError("Bank payroll ledger update failed ({ExceptionType}, {Code}); a person must reconcile it", kind, code));
        }
    }

    private static BankSubmitOutcome Refused(string code, string? reason = null) =>
        new(BankSubmitOutcomeKind.Refused, code, RequestSent: false, Message: reason);

    private static void Safe(Action log)
    {
        try { log(); } catch { /* logging never changes the outcome */ }
    }
}
