using System.Text.RegularExpressions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Zayra.Api.Tests;

/// <summary>
/// A throwaway postgres:16 built from <c>backend-dotnet/Zayra.Api/Db/baseline/*.sql</c>, in the
/// order and with the role switching the deploy uses: 001 and 002 bootstrap the extensions and the
/// six roles, everything after them runs with <c>SET ROLE kynex_owner</c> so every object is owned
/// the way 060's grants assume.
///
/// The one deliberate difference from <c>Db/baseline/tests/verify_rls.sh</c>: the connection logs
/// in as the container superuser and then SET ROLEs, rather than logging in as kynex_migrator,
/// because a password login for kynex_migrator would need the container's pg_hba and buys nothing
/// here — the login role has no effect on the SHAPE of the resulting schema, which is all these
/// tests read. Role fidelity is verify_rls.sh's job and is proved there over genuine per-role
/// connections.
///
/// postgres:16 and not 17 for the same reason the shell script gives: production is Neon PG 17.11
/// and the baseline must not depend on anything newer than 16.
/// </summary>
public sealed class KynexBaselineFixture : IAsyncLifetime
{
    private static readonly string[] Bootstrap =
    {
        "001_extensions.sql",
        "002_roles.sql",
    };

    private static readonly string[] Migrated =
    {
        "010_platform.sql",
        "011_identity.sql",
        "012_org.sql",
        "013_employees.sql",
        "014_statutory.sql",
        "015_payroll.sql",
        "016_wps_gl.sql",
        "017_leave_attendance.sql",
        "018_workflow_audit.sql",
        "020_constraints_a_f.sql",
        "021_constraints_g_r.sql",
        "022_constraints_cross.sql",
        "030_partitions.sql",
        "040_indexes.sql",
        "050_triggers.sql",
        "060_policies.sql",
    };

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:16")
        .Build();

    public string ConnectionString { get; private set; } = string.Empty;

    /// <summary>The 18 baseline files, resolved from the test assembly's location.</summary>
    public static string BaselineDirectory
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                var candidate = Path.Combine(dir.FullName, "backend-dotnet", "Zayra.Api", "Db", "baseline");
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }

                dir = dir.Parent;
            }

            throw new InvalidOperationException(
                "backend-dotnet/Zayra.Api/Db/baseline was not found above " + AppContext.BaseDirectory);
        }
    }

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();

        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();

        foreach (var file in Bootstrap)
        {
            await ExecuteAsync(conn, await ReadAsync(file));
        }

        foreach (var file in Migrated)
        {
            await ExecuteAsync(conn, "SET ROLE kynex_owner;\n" + await ReadAsync(file));
            await ExecuteAsync(conn, "RESET ROLE;");
        }
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    private static Task<string> ReadAsync(string file) =>
        File.ReadAllTextAsync(Path.Combine(BaselineDirectory, file));

    private static async Task ExecuteAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 300 };
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<T>> QueryAsync<T>(string sql, Func<NpgsqlDataReader, T> read)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 120 };
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<T>();
        while (await reader.ReadAsync())
        {
            rows.Add(read(reader));
        }

        return rows;
    }

    /// <summary>Partition children, which are never entities. Matches the PartitionMaintenance template.</summary>
    public static readonly Regex PartitionChild = new(@"_(y\d{4}m\d{2}|default)$", RegexOptions.Compiled);
}

[CollectionDefinition("KynexBaseline")]
public sealed class KynexBaselineCollection : ICollectionFixture<KynexBaselineFixture>
{
}
