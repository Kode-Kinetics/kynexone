namespace Zayra.Api.Application.Organization;

/// <summary>
/// A per-company organisation rule (Addendum B) the request broke, with a stable <see cref="Code"/> for the API and
/// the UI. Still an <see cref="InvalidOperationException"/>, so every caller that already maps that type to 400 keeps
/// working; controllers that know this type add the code to the body.
/// </summary>
public sealed class OrganizationRuleException(string code, string message) : InvalidOperationException(message)
{
    /// <summary>The tenant has several companies and the request did not say which one owns the row.</summary>
    public const string CompanyRequired = "company_required";
    /// <summary>A linked row (branch, parent, cost centre, department, grade) belongs to another company.</summary>
    public const string CompanyMismatch = "company_mismatch";
    /// <summary>A row's company is fixed once set; copy it to the other company instead.</summary>
    public const string CompanyLocked = "company_locked";
    /// <summary>A row's code can't change once the row is in use (change the name, or turn the row off and add a new one).</summary>
    public const string CodeLocked = "code_locked";

    public string Code { get; } = code;

    public object ToPayload() => new { error = Code, message = Message };

    /// <summary>The 400 body for any organisation validation failure: the code when there is one, always the message.</summary>
    public static object Payload(InvalidOperationException ex) =>
        ex is OrganizationRuleException rule ? rule.ToPayload() : new { message = ex.Message };
}
