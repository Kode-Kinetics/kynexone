using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Employees;

/// <summary>
/// The "360" employee search behind <c>GET /api/employees?search=</c>: ONE predicate for the group-level list
/// (<see cref="EmployeeManagementService.SearchAsync(Guid, string?, string?, string?, string?, Guid?, string?, string?, int, int, CancellationToken, bool)"/>),
/// the company-scoped list and the bulk-action resolver, so the three can never disagree about who a search finds.
///
/// <list type="bullet">
///   <item>Everyone who may list employees: code, full / English / Arabic name, work email, job title, department and branch
///     (the stored label, or the linked branch's English or Arabic name). Case-insensitive, anywhere in the value; the term is
///     matched literally (no LIKE wildcards).</item>
///   <item>Only with <c>employees.sensitive</c>: phone (digits only, so "055 123 4567", "+966551234567" and "00966…" all find
///     the same number; at least 4 digits), personal email, and the ID / Iqama number on its EXACT full value. These are the
///     personal fields the record view masks without that permission; matching on them without it would let a caller confirm
///     a number they may not see.</item>
/// </list>
/// </summary>
public static class EmployeeSearch
{
    /// <summary>GCC country calling codes stripped from a typed international number, so it matches a locally stored one.</summary>
    private static readonly string[] GccCallingCodes = ["966", "965", "973", "968", "971", "974"];
    private const int MinPhoneDigits = 4;

    public static IQueryable<Employee> Apply(IQueryable<Employee> query, IQueryable<Branch> branches, string? search, bool includeSensitive)
    {
        if (string.IsNullOrWhiteSpace(search)) return query;
        var term = search.Trim();
        var lower = term.ToLowerInvariant();
        var phone = includeSensitive ? PhoneDigits(term) : null;
        var idNumber = includeSensitive ? term.ToUpperInvariant() : null;
        var searchPhone = phone != null;

        return query.Where(e =>
            (e.EmployeeCode != null && e.EmployeeCode.ToLower().Contains(lower))
            || (e.FullName != null && e.FullName.ToLower().Contains(lower))
            || (e.EnglishName != null && e.EnglishName.ToLower().Contains(lower))
            || (e.ArabicName != null && e.ArabicName.Contains(term))
            || (e.WorkEmail != null && e.WorkEmail.ToLower().Contains(lower))
            || (e.Designation != null && e.Designation.ToLower().Contains(lower))
            || (e.Department != null && e.Department.ToLower().Contains(lower))
            || (e.Branch != null && e.Branch.ToLower().Contains(lower))
            || branches.Any(b => b.Id == e.BranchId && (b.NameEn.ToLower().Contains(lower) || b.NameAr.Contains(term)))
            || (includeSensitive && e.PersonalEmail != null && e.PersonalEmail.ToLower().Contains(lower))
            || (searchPhone && e.Phone != null
                && e.Phone.Replace(" ", "").Replace("-", "").Replace("+", "").Replace("(", "").Replace(")", "").Replace(".", "").Contains(phone!))
            || (includeSensitive && e.IdNumber != null && e.IdNumber.Trim().ToUpper() == idNumber));
    }

    /// <summary>
    /// The digits of a term that looks like a phone number (only digits, spaces, + - ( ) .), without an international prefix
    /// ("00", a GCC calling code) or the trunk "0", or null when it is not a phone-shaped term or has too few digits.
    /// </summary>
    internal static string? PhoneDigits(string term)
    {
        if (term.Any(c => !(char.IsAsciiDigit(c) || c is ' ' or '-' or '+' or '(' or ')' or '.'))) return null;
        var digits = new string(term.Where(char.IsAsciiDigit).ToArray());
        if (digits.StartsWith("00", StringComparison.Ordinal)) digits = digits[2..];
        foreach (var code in GccCallingCodes)
        {
            // Only a full international number carries the code: a short run starting "966" may be part of a local number.
            if (digits.StartsWith(code, StringComparison.Ordinal) && digits.Length >= code.Length + 8) { digits = digits[code.Length..]; break; }
        }
        digits = digits.TrimStart('0');
        return digits.Length >= MinPhoneDigits ? digits : null;
    }
}
