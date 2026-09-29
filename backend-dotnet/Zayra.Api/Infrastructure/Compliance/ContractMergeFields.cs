using System.Text.RegularExpressions;

namespace Zayra.Api.Infrastructure.Compliance;

/// <summary>
/// Substitutes <c>{{merge_field}}</c> tokens when a contract is generated from a template, and
/// gives <see cref="Models.ContractTemplate.Variables"/> — the template's declared merge-field list
/// — something to do.
///
/// <para><b>What was wrong.</b> <c>ContractsController.Create</c> copied the template body onto the
/// new contract verbatim. There was no substitution step anywhere: HR writes a template saying
/// "This agreement is made between the Company and {{employee_name}}", and every contract generated
/// from it contains those literal braces. If nobody opens the PDF before it goes out, that is what
/// the employee signs. The <c>Variables</c> column, documented in the model as
/// <c>"CSV list of merge fields: {{employee_name}},{{start_date}}"</c>, was written on create and
/// read by nothing.</para>
///
/// <para><b>Two things now depend on it.</b> A declared variable that this class cannot supply
/// refuses the generation, naming the field — so a typo is caught once, at the template, instead of
/// silently on every contract. And after substitution any surviving <c>{{...}}</c> refuses too:
/// unresolved beats emitted, because an emitted placeholder is a legal document with a hole in it.
/// Both are failures the previous code could not have: it had nothing to compare against and
/// nothing to fail on.</para>
///
/// <para><b>Double braces only.</b> <c>NotificationBodyPolicy.Interpolate</c> also substitutes
/// single <c>{token}</c>, which is right for a short SMS body and wrong for a contract: contract
/// HTML carries inline CSS and legal text where a lone brace pair must survive untouched. The
/// template documentation uses double braces, so that is the only form honoured here.</para>
/// </summary>
public static partial class ContractMergeFields
{
    [GeneratedRegex(@"\{\{\s*([A-Za-z0-9_]+)\s*\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex MergeToken();

    /// <summary>
    /// The fields a contract template may merge. An allow-list, not a free dictionary: a contract is
    /// a legal document and the set of facts it may interpolate is a deliberate decision, not
    /// whatever happens to be on the entity. Bank details, national IDs and passport numbers are
    /// absent on purpose.
    /// </summary>
    public static readonly IReadOnlyCollection<string> SupportedFields = new[]
    {
        "employee_name",
        "employee_code",
        "designation",
        "department",
        "start_date",
        "end_date",
        "basic_salary",
        "currency",
        "contract_number",
        "contract_type",
        "company_name",
    };

    /// <param name="Html">The merged document.</param>
    /// <param name="Error">Null on success; otherwise a machine-readable code and message.</param>
    public readonly record struct MergeResult(string Html, (string Code, string Message)? Error)
    {
        public bool IsSuccess => Error is null;
    }

    /// <summary>
    /// Parses the template's declared CSV merge-field list. Tolerates the documented brace form
    /// (<c>{{employee_name}},{{start_date}}</c>) and the bare form (<c>employee_name, start_date</c>),
    /// because both appear in practice and rejecting one of them would be a new way to fail.
    /// </summary>
    public static IReadOnlyList<string> ParseDeclaredVariables(string? csv)
        => (csv ?? string.Empty)
            .Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => token.Trim().TrimStart('{').TrimEnd('}').Trim())
            .Where(token => token.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Refuses the declared list if it names a field this engine cannot supply. Called before any
    /// substitution so the refusal points at the template, which is what the author must fix.
    /// </summary>
    public static (string Code, string Message)? ValidateDeclaredVariables(string? csv)
    {
        var unsupported = ParseDeclaredVariables(csv)
            .Where(name => !SupportedFields.Contains(name, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (unsupported.Count == 0) return null;

        return ("contract_merge_field_unsupported",
            $"This contract template declares merge field(s) the product cannot supply: {string.Join(", ", unsupported)}. "
            + $"Supported merge fields are: {string.Join(", ", SupportedFields)}. "
            + "Correct the template's variable list before generating a contract from it.");
    }

    /// <summary>
    /// Substitutes every supported token in <paramref name="html"/> and fails if any
    /// <c>{{...}}</c> survives. An empty body is a no-op success: a contract written by hand with no
    /// template has nothing to merge.
    /// </summary>
    public static MergeResult Merge(string? html, IReadOnlyDictionary<string, string> values)
    {
        if (string.IsNullOrEmpty(html)) return new MergeResult(string.Empty, null);

        var unknown = new List<string>();
        var merged = MergeToken().Replace(html, match =>
        {
            var key = match.Groups[1].Value;
            if (values.TryGetValue(key, out var value)) return System.Net.WebUtility.HtmlEncode(value ?? string.Empty);
            // Left in place deliberately: the check below fails closed on it rather than quietly
            // deleting a clause's subject, which would be a worse document than a visible hole.
            unknown.Add(key);
            return match.Value;
        });

        if (unknown.Count > 0)
        {
            return new MergeResult(merged, ("contract_unresolved_merge_field",
                $"The contract body contains merge field(s) that could not be filled: "
                + $"{string.Join(", ", unknown.Distinct(StringComparer.OrdinalIgnoreCase))}. "
                + $"Supported merge fields are: {string.Join(", ", SupportedFields)}. "
                + "A contract is never issued with an unfilled placeholder."));
        }

        return new MergeResult(merged, null);
    }

    /// <summary>The values for one contract. Keys match <see cref="SupportedFields"/> exactly.</summary>
    public static IReadOnlyDictionary<string, string> BuildValues(
        string employeeName, string employeeCode, string designation, string department,
        DateOnly startDate, DateOnly? endDate, decimal basicSalary, string currency,
        string contractNumber, string contractType, string companyName)
        => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["employee_name"] = employeeName,
            ["employee_code"] = employeeCode,
            ["designation"] = designation,
            ["department"] = department,
            ["start_date"] = startDate.ToString("dd MMMM yyyy"),
            // An indefinite contract has no end date; the template's own wording decides whether it
            // asks for one, and "Indefinite" is the truthful filling rather than an empty gap.
            ["end_date"] = endDate?.ToString("dd MMMM yyyy") ?? "Indefinite",
            ["basic_salary"] = basicSalary.ToString("N2"),
            ["currency"] = currency,
            ["contract_number"] = contractNumber,
            ["contract_type"] = contractType,
            ["company_name"] = companyName,
        };
}
