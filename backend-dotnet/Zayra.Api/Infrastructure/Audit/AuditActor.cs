using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;

namespace Zayra.Api.Infrastructure.Audit;

/// <summary>
/// The name an audit row shows for its actor (WP14.4): the signed-in user's name when the change came from
/// one, and only otherwise the label of the automatic process that made it.
/// </summary>
public static class AuditActor
{
    public static async Task<string> NameAsync(ZayraDbContext db, Guid? userId, string processLabel, CancellationToken ct)
    {
        if (userId is not { } id) return processLabel;
        var name = await db.Users.AsNoTracking().Where(u => u.Id == id).Select(u => u.FullName).FirstOrDefaultAsync(ct);
        return string.IsNullOrWhiteSpace(name) ? processLabel : name;
    }
}
