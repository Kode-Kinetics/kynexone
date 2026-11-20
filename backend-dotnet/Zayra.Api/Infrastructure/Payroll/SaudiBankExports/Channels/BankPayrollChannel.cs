using System.Globalization;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Infrastructure.Government;

namespace Zayra.Api.Infrastructure.Payroll.SaudiBankExports.Channels;

// Bank-neutral seam for sending a frozen payroll instruction to a bank over an API and reading the result
// back. One implementation per bank (ANB is the first). Callers (the submit job, the poll job, the
// unknown-outcome screen) depend on THIS file only, never on a bank's own types. It is deliberately small:
// it is not a generic, configuration-driven REST adapter.

/// <summary>A bank payroll API. No implementation is live: <see cref="IsLiveIntegration"/> is false until
/// a bank has accepted a file through it, and nothing registers one in DI yet.</summary>
public interface IBankPayrollChannel
{
    /// <summary>Stable bank key, for example "anb".</summary>
    string BankId { get; }

    /// <summary>False for every implementation until bank acceptance is verified. Only a live channel may
    /// produce a "submitted via the bank" state.</summary>
    bool IsLiveIntegration { get; }

    /// <summary>Sends the frozen files exactly as given, at most once. Never throws once the request may have
    /// left; an ambiguous result is <see cref="BankSubmitOutcomeKind.Unknown"/> and must never be resubmitted
    /// automatically.</summary>
    Task<BankSubmitOutcome> SubmitAsync(BankSubmitRequest request, CancellationToken ct);

    /// <summary>Reads the bank's status for one payroll and maps it onto the batch it was submitted for.
    /// Throws <see cref="BankChannelException"/> when the bank's answer cannot be trusted for this batch.</summary>
    Task<BankStatusSnapshot> GetStatusAsync(BankChannelScope scope, string bankPayrollId, BankBatchBinding binding, CancellationToken ct);

    /// <summary>Downloads the WPS file the bank produced for a payroll.</summary>
    Task<BankWpsFile> DownloadWpsAsync(BankChannelScope scope, string bankPayrollId, string? profileId, CancellationToken ct);
}

/// <summary>Who the call is for. Credentials and cached tokens are keyed by all of it.</summary>
public sealed record BankChannelScope(Guid TenantId, Guid? CompanyId, string Environment);

/// <summary>One frozen file exactly as the approver saw it, with the SHA-256 recorded when it was frozen.
/// <para>WIRING RULE: <see cref="ExpectedSha256"/> must be loaded from the stored export envelope AFTER the
/// envelope has been checked against its sealed audit entry (the existing download path does this), never
/// recomputed from <see cref="Content"/> at submit time; otherwise the check proves nothing.</para></summary>
public sealed record BankArtifactFile(string FileName, byte[] Content, string ExpectedSha256)
{
    public override string ToString() => $"BankArtifactFile({FileName}, {Content.Length} bytes, sha256={ExpectedSha256})";
}

/// <summary>Bank-neutral submit options. A bank ignores what it does not support.</summary>
public sealed record BankSubmitOptions(string? ProfileId = null, bool AutoWpsUpload = false, string? NationalUnifiedNo = null)
{
    public override string ToString() => $"BankSubmitOptions(autoWps={AutoWpsUpload})";
}

/// <param name="PriorOutcomeUnknown">True when an earlier submit of this batch ended Unknown. A later
/// structural refusal is then NOT a safe "regenerate" signal (it may be the bank refusing a duplicate).</param>
/// <param name="Intent">The recorder's receipt for the committed submission intent of exactly these files, and who acts.
/// <see cref="GatedBankPayrollChannel"/> refuses a submit without one; the bank channels themselves ignore it.</param>
public sealed record BankSubmitRequest(
    BankChannelScope Scope, IReadOnlyList<BankArtifactFile> Files, BankSubmitOptions Options, bool PriorOutcomeUnknown = false,
    BankSubmitIntent? Intent = null);

/// <summary>The committed intent a submit is sent under (<see cref="GovernmentExchangeIntentReceipt"/>), and the actor the ledger
/// update is recorded for.</summary>
public sealed record BankSubmitIntent(GovernmentExchangeIntentReceipt Receipt, RequestContext Context)
{
    public override string ToString() => $"BankSubmitIntent({Receipt.ExchangeId})";
}

public enum BankSubmitOutcomeKind
{
    /// <summary>2xx with a payroll id: the bank accepted the structure.</summary>
    Accepted,
    /// <summary>The bank refused the files. Regenerate through the existing re-export path, unless an earlier
    /// attempt was Unknown (then the outcome is Unknown instead).</summary>
    RejectedStructure,
    /// <summary>401/403 on the POST: the bank refused our token, so it did not take the payroll.</summary>
    RejectedAuthorization,
    /// <summary>The request may have reached the bank. NEVER resubmit automatically.</summary>
    Unknown,
    /// <summary>Refused before the POST: no socket was opened for it.</summary>
    Refused,
}

/// <summary>Result of one submit. <see cref="Message"/> is masked and truncated.</summary>
public sealed record BankSubmitOutcome(
    BankSubmitOutcomeKind Kind,
    string Code,
    bool RequestSent,
    int? HttpStatus = null,
    string? BankPayrollId = null,
    string? BankStatus = null,
    string? Message = null,
    string? SentHeaderSha256 = null,
    string? SentBodySha256 = null);

/// <summary>What we sent for one employee, from the frozen body, and our own WPS reference for that payment record.</summary>
public sealed record BankSubmittedLine(string EmployeeId, string AccountNumber, string WpsReference)
{
    public override string ToString() => $"BankSubmittedLine({WpsReference})";
}

/// <summary>Ties a status read to the batch it is applied to. A snapshot whose count, total or dates do not
/// fit is refused (<see cref="BankChannelCodes.PayrollNotThisBatch"/>).
/// <para>TODO(ledger, wave 2): the submission ledger must also hold a unique (tenant, bank, bankPayrollId) so
/// one bank id can never be bound to two batches; this binding is the second line of defence.</para></summary>
/// <param name="IntentAtUtc">When the FIRST submit intent row for this batch was written, before its POST. Must be the
/// EARLIEST intent of the batch: after an Unknown that actually landed and a later second intent, the bank's lines
/// date from the first attempt and the latest intent would wrongly refuse them.</param>
public sealed record BankBatchBinding(
    IReadOnlyList<BankSubmittedLine> Lines, decimal ExpectedTotal, DateTimeOffset IntentAtUtc, string? ProfileId = null)
{
    // Security P3-3: no money amount in a log or exception.
    public override string ToString() => $"BankBatchBinding({Lines.Count} lines, intent={IntentAtUtc:O})";
}

public enum BankPayrollPhase { Accepted, Initiated, Validated, InProgress, Completed, Failed, Rejected }

/// <summary>A bank line that is reported, never applied (not in the batch, or an unpublished status word).</summary>
public sealed record BankUnresolvedLine(int Position, string MaskedEmployeeId, string Reason, string? RawStatus = null);

public sealed record BankLineAnomaly(string WpsReference, string Code);

/// <summary>
/// The bank's status mapped onto one batch. <see cref="Rows"/> feed <see cref="BankConfirmationService"/>
/// through <see cref="BankChannelConfirmationFile.Render"/>.
/// <para>Only a per-line PAID ever becomes Paid. <see cref="PayrollPhase"/> Completed does NOT mean every
/// employee was paid: treat the batch as settled only when <see cref="Complete"/> is true and the import
/// reports full coverage.</para>
/// </summary>
public sealed record BankStatusSnapshot(
    string BankPayrollId,
    BankPayrollPhase PayrollPhase,
    IReadOnlyList<BankConfirmationRow> Rows,
    IReadOnlyList<BankUnresolvedLine> Unresolved,
    IReadOnlyList<string> MissingFromBank,
    IReadOnlyList<BankLineAnomaly> Anomalies,
    bool Complete);

public sealed record BankWpsFile(byte[] Content, string Sha256, string? ContentType, string? FileName)
{
    public override string ToString() => $"BankWpsFile({Content.Length} bytes, sha256={Sha256})";
}

/// <summary>Renders status rows as a file for the EXISTING <c>generic-csv</c> confirmation parser. The bank
/// payroll id is a column, so two payrolls with the same people never render the same bytes (the import's
/// tenant-wide file-hash dedupe would otherwise refuse the second as already imported).</summary>
public static class BankChannelConfirmationFile
{
    public const string ParserKey = "generic-csv";

    private static readonly string[] Columns =
        { "WpsReference", "Amount", "Status", "ReasonCode", "ReasonDescription", "BankReference", "BankPayrollId" };

    public static string Render(string bankPayrollId, IReadOnlyList<BankConfirmationRow> rows) =>
        Csv.Build(Columns, rows.Select(r => (IReadOnlyList<object?>)new object?[]
        {
            r.WpsReference,
            r.Amount?.ToString(CultureInfo.InvariantCulture),
            r.Outcome,
            r.ReasonCode,
            r.ReasonText,
            r.BankReference,
            bankPayrollId,
        }));
}

/// <summary>Stable codes. Callers and the UI switch on these, never on message text.</summary>
public static class BankChannelCodes
{
    public const string ConfigMissing = "bank_config_missing";
    public const string ConfigInvalid = "bank_config_invalid";
    public const string EnvironmentMismatch = "bank_environment_mismatch";
    public const string CredentialMissing = "bank_credential_missing";
    public const string CredentialSourceFailed = "bank_credential_source_failed";
    public const string CredentialEndpointMismatch = "bank_credential_endpoint_mismatch";
    public const string TokenUnavailable = "bank_token_unavailable";
    public const string TokenRejected = "bank_token_rejected";
    public const string CancelledBeforeSend = "bank_cancelled_before_send";
    /// <summary>The environment's base URL, token URL or token path is not on GovernmentHttp's allow-list (missing, not a plain https
    /// origin, or a sandbox URL on a production host). Names no value.</summary>
    public const string HostNotAllowed = "bank_host_not_allowed";
    /// <summary>The shared transport refused before opening a socket for a reason with no more specific code.</summary>
    public const string TransportRefused = "bank_transport_refused";
    public const string ArtifactHashMismatch = "bank_artifact_hash_mismatch";
    public const string ArtifactInconsistent = "bank_artifact_inconsistent";
    public const string SubmitHeaderInvalid = "bank_submit_header_invalid";
    public const string PayrollIdInvalid = "bank_payroll_id_invalid";

    public const string Accepted = "bank_accepted";
    public const string AcceptedWithoutId = "bank_accepted_without_id";
    public const string AcceptedWithFailedStatus = "bank_accepted_with_failed_status";
    public const string RejectedStructure = "bank_rejected_structure";
    public const string RejectedAfterUnknown = "bank_rejected_after_unknown";
    public const string RejectedAuthorization = "bank_rejected_authorization";
    public const string EndpointWrong = "bank_endpoint_wrong";
    public const string OutcomeUnknown = "bank_outcome_unknown";

    public const string Unauthorized = "bank_unauthorized";
    public const string HttpError = "bank_http_error";
    public const string ResponseTooLarge = "bank_response_too_large";
    public const string ResponseUnparsable = "bank_response_unparsable";
    public const string PagePayrollMismatch = "bank_poll_payroll_mismatch";
    public const string PageMismatch = "bank_poll_page_mismatch";
    public const string PollInconsistent = "bank_poll_inconsistent";
    public const string PollDeadline = "bank_poll_deadline";
    public const string PayrollNotThisBatch = "bank_payroll_not_this_batch";
    public const string WpsNotReady = "bank_wps_not_ready";
    public const string WpsTooLarge = "bank_wps_too_large";
    public const string StatusUnknown = "bank_status_unknown";

    /// <summary>The live gate refused the call (a closed gate, a read decision for a write, another environment or bank, or the
    /// production legal clearance missing). The outcome's message carries the policy's reason code.</summary>
    public const string GateRefused = "bank_gate_refused";
    /// <summary>A submit without the recorder's receipt for a committed intent.</summary>
    public const string IntentMissing = "bank_intent_missing";
    /// <summary>The receipt is for another tenant, company, bank, environment or other files.</summary>
    public const string IntentMismatch = "bank_intent_mismatch";
    /// <summary>The receipt was already used: sent once, or burnt by an earlier attempt that ended without sending.</summary>
    public const string IntentAlreadyUsed = "bank_intent_already_used";
}

/// <summary>A channel refused to be built or to call. No socket was opened. Names settings, never values.</summary>
public sealed class BankChannelRefusedException : Exception
{
    public BankChannelRefusedException(string code, IReadOnlyList<string>? settings = null)
        : base(settings is { Count: > 0 }
            ? $"Bank payroll channel refused ({code}): {string.Join(", ", settings)}."
            : $"Bank payroll channel refused ({code}).")
    {
        Code = code;
        Settings = settings ?? Array.Empty<string>();
    }

    public string Code { get; }
    public IReadOnlyList<string> Settings { get; }
}

/// <summary>A read call failed or returned something that cannot be trusted. Carries a stable code and the
/// HTTP status only: never a response body, which can echo an IBAN, an ID or a token.</summary>
public class BankChannelException : Exception
{
    public BankChannelException(string code, int? httpStatus = null)
        : base(httpStatus is null ? $"Bank payroll channel error ({code})." : $"Bank payroll channel error ({code}, HTTP {httpStatus}).")
    {
        Code = code;
        HttpStatus = httpStatus;
    }

    protected BankChannelException(string code, string message) : base(message)
    {
        Code = code;
    }

    public string Code { get; }
    public int? HttpStatus { get; }
}
