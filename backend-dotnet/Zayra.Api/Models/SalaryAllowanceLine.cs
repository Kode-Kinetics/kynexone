using System.Text.Json.Serialization;

namespace Zayra.Api.Models;

/// <summary>
/// One customer-defined monthly cash allowance (<c>ALW_*</c>) line as a salary proposal states it, and as it is kept in
/// <c>salary_change_requests.allowances_json</c> until Finance approves: the code, how it is worked out, and for a percentage the
/// fraction of basic (0.10 = 10%). <see cref="Amount"/> is the monthly amount; for <c>PercentOfBasic</c> the server computes it
/// (rate × the proposed basic, to the halala) and ignores a value sent. The same shape is what the API accepts and returns.
///
/// <para><see cref="NameEn"/> and <see cref="NameAr"/> are for READING only: a screen that shows a line needs its name without being
/// allowed to read the catalogue. The server fills them on what it returns (employee detail, the salary assignment list) and never
/// reads them from a request nor writes them to <c>allowances_json</c> (a null name is not serialised, so the staged text is unchanged).</para>
/// </summary>
public sealed record SalaryAllowanceLine(
    string? Code, string? Basis, decimal? Rate, decimal? Amount,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? NameEn = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? NameAr = null);
