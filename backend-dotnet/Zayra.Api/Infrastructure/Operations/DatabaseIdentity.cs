using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql;

namespace Zayra.Api.Infrastructure.Operations;

/// <summary>
/// Which database this API is connected to — its NAME and HOST only — for platform operators.
/// </summary>
/// <remarks>
/// Surfaced on the authenticated <c>GET /api/platform/health</c> (platform Owner/Admin/Support/Auditor),
/// never on the public health endpoints, and never with a port, user, password or any other part of
/// the connection string. The e2e preflight (<c>frontend/e2e/preflight</c>) reads it to refuse a run
/// whose API is attached to a production database (<c>kynexone_clean</c>, <c>neondb</c>, a Neon or
/// Render host) or to a database other than the one the run prepared. Register item F07: the suites
/// used to have no way to tell which database they were writing fixture tenants into.
/// </remarks>
public static class DatabaseIdentity
{
    public sealed record Identity(string? Name, string? Host);

    public static Identity Describe(DatabaseFacade database)
    {
        if (!database.IsRelational()) return new Identity(null, null);
        try
        {
            return FromConnectionString(database.GetConnectionString());
        }
        catch
        {
            // Reporting must never break the health endpoint; an unidentified database is reported as
            // such, and the preflight treats that as a refusal.
            return new Identity(null, null);
        }
    }

    /// <summary>Pure parse, exposed for tests. Reads only Database and Host.</summary>
    public static Identity FromConnectionString(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return new Identity(null, null);
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        return new Identity(
            string.IsNullOrWhiteSpace(builder.Database) ? null : builder.Database,
            string.IsNullOrWhiteSpace(builder.Host) ? null : builder.Host);
    }
}
