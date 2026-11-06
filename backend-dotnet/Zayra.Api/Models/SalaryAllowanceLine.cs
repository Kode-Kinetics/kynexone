namespace Zayra.Api.Models;

/// <summary>
/// One customer-defined monthly cash allowance (<c>ALW_*</c>) line as a salary proposal states it, and as it is kept in
/// <c>salary_change_requests.allowances_json</c> until Finance approves: the code, how it is worked out, and for a percentage the
/// fraction of basic (0.10 = 10%). <see cref="Amount"/> is the monthly amount; for <c>PercentOfBasic</c> the server computes it
/// (rate × the proposed basic, to the halala) and ignores a value sent. The same shape is what the API accepts and returns.
/// </summary>
public sealed record SalaryAllowanceLine(string? Code, string? Basis, decimal? Rate, decimal? Amount);
