using Zayra.Api.Infrastructure.Government;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports.Channels;

namespace Zayra.Api.Infrastructure.Payroll.SaudiBankExports.Api;

// ANB-only shapes for the ANB Payroll Services API client (published OpenAPI "Payroll Services APIs" v0.0.1).
// Callers outside the Anb* files use IBankPayrollChannel and the neutral types in Channels/ instead.
// The client has no endpoint or credential types of its own: base URL, token URL and token path come from GovernmentHttp's
// allow-list under Government:Banks:Anb, the OAuth settings from the same tree, and the credential from the government
// credential store (IBankCredentialSource). Nothing is wired into DI; an empty configuration refuses (fail closed).

/// <summary>Time limits. Defaults suit production; tests shorten them.</summary>
public sealed record AnbClientLimits(TimeSpan RequestTimeout, TimeSpan StatusReadDeadline)
{
    public static readonly AnbClientLimits Default = new(TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(2));
}

/// <summary>A status string outside ANB's published vocabulary. Never guessed into a known one.</summary>
public sealed class AnbUnknownStatusException : BankChannelException
{
    public AnbUnknownStatusException(string kind, string? token)
        : base(BankChannelCodes.StatusUnknown, $"ANB {kind} status '{AnbStatusWords.Printable(token)}' is not in the published vocabulary; refusing to guess.")
    {
        Kind = kind;
        Token = AnbStatusWords.Printable(token);
    }

    public string Kind { get; }
    /// <summary>The token if it looks like a status word, else a placeholder (a crafted value is never echoed).</summary>
    public string Token { get; }
}

internal static class AnbStatusWords
{
    public static string Printable(string? token)
    {
        if (token is null) return "<null>";
        if (token.Length == 0) return "<empty>";
        return IsWord(token) ? token : "<withheld>";
    }

    /// <summary>ASCII letters, space, underscore, hyphen, at most 32. Anything else (digits, look-alike
    /// letters such as a dotless i) is not a status word.</summary>
    public static bool IsWord(string token) =>
        token.Length <= 32 && token.All(c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or ' ' or '_' or '-');
}

/// <summary>Optional request headers from the spec. Fee semantics are not published, so the client only
/// passes them through (after rejecting control characters); it gives them no meaning.</summary>
public sealed record AnbSubmitHeaders(
    string? ProfileId = null,
    string? FeesCategoryCode = null,
    string? FeesAndVatAccount = null,
    string? FeesFrequency = null,
    bool AutoWpsFileUpload = false,
    string? NationalUnifiedNo = null)
{
    public override string ToString() => $"AnbSubmitHeaders(autoWps={AutoWpsFileUpload})";
}

/// <param name="Intent">The recorder's receipt for the committed intent of exactly these files, for this tenant, company, bank and
/// environment. The client refuses a submit without it, and the transport spends it on the one send.</param>
public sealed record AnbSubmitRequest(
    BankChannelScope Scope, BankArtifactFile Header, BankArtifactFile Body, AnbSubmitHeaders Headers, bool PriorOutcomeUnknown = false,
    GovernmentExchangeIntentReceipt? Intent = null)
{
    public override string ToString() => $"AnbSubmitRequest({Header}, {Body}, {Headers}, priorUnknown={PriorOutcomeUnknown})";
}

/// <summary>One per-employee line from <c>GET /payroll/v2/{id}</c>. Carries personal data:
/// <see cref="ToString"/> prints none of it.</summary>
public sealed record AnbStatusLine(
    string EmployeeId,
    string EmployeeAccountNumber,
    decimal SalaryAmount,
    string? BicCode,
    string? EmployeeName,
    string? PaymentId,
    string PaymentStatus,
    string? PaymentStatusDescription,
    string? PaymentTransactionReferenceNumber,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt)
{
    // Security P3-3: no wage amount (nor any other value of the line) in a log or exception.
    public override string ToString() => $"AnbStatusLine(status={AnbStatusWords.Printable(PaymentStatus)})";
}

/// <summary>All pages of one payroll, merged. <see cref="Complete"/> is true only when the distinct lines
/// equal the bank's <c>itemCount</c> and the count is not zero. Lines with an unpublished status word are
/// kept verbatim; the mapper holds them.</summary>
public sealed record AnbPayrollStatusSnapshot(
    string BankPayrollId,
    string PayrollStatus,
    string? Reason,
    IReadOnlyList<AnbStatusLine> Lines,
    int ItemCount,
    int PagesFetched,
    int DuplicateLinesCollapsed,
    bool Complete,
    DateTimeOffset? PayrollCreatedAt = null);
