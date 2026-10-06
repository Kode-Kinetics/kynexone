using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;

namespace Zayra.Api.Infrastructure.Common;

public enum CallerEmployeeMatch { Linked, NotLinked, Ambiguous }

/// <summary>
/// Which employee record the signed-in caller IS. One lookup for the data scope and for self-service, so the
/// two can never disagree: the <c>employee_id</c> claim when the login carries one, otherwise the single
/// employee whose work or personal email matches the login's email, compared case-insensitively. Two matches
/// are ambiguous and resolve to no employee — never to "the first one".
///
/// <para>The data scope used to compare the lower-cased login email with the stored email as written, so a
/// WorkEmail with capitals resolved in self-service but not in the data scope; and it took the first of
/// several matches.</para>
/// </summary>
public static class CallerEmployeeResolver
{
    public static async Task<(int? EmployeeId, CallerEmployeeMatch Match)> ResolveAsync(
        ZayraDbContext db, ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
    {
        if (int.TryParse(caller.FindFirstValue("employee_id"), out var claimed))
            return (claimed, CallerEmployeeMatch.Linked);

        var email = caller.FindFirstValue(JwtRegisteredClaimNames.Email) ?? caller.FindFirstValue(ClaimTypes.Email);
        if (string.IsNullOrWhiteSpace(email)) return (null, CallerEmployeeMatch.NotLinked);

        var normalized = email.Trim().ToUpperInvariant();
        var matches = await db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && !e.IsDeleted
                && (e.WorkEmail.ToUpper() == normalized || e.PersonalEmail.ToUpper() == normalized))
            .Select(e => e.Id)
            .Take(2)
            .ToListAsync(ct);
        return matches.Count switch
        {
            1 => (matches[0], CallerEmployeeMatch.Linked),
            0 => (null, CallerEmployeeMatch.NotLinked),
            _ => (null, CallerEmployeeMatch.Ambiguous),
        };
    }
}
