using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Operations;

namespace Zayra.Api.Infrastructure.Provisioning;

/// <summary>
/// <c>dotnet Zayra.Api.dll --import-legacy &lt;dir&gt;</c> (WP13.2): loads one customer exported from the retired
/// hosted service by <c>tools/TenantExporter</c> into this installation, which must be empty (run it instead of
/// <c>--setup-organisation</c>). The exported tenant becomes the installation's organisation.
/// </summary>
/// <remarks>
/// <para>Steps: verify every file against the manifest; in ONE transaction, with foreign keys and triggers off
/// (<c>session_replication_role = replica</c>, so rows load in any order and append-only triggers do not refuse
/// historical rows), insert every table's rows into the columns both versions share, keeping primary keys; map the
/// permission catalogue by key; clear two-step sign-in secrets (they were encrypted with the hosted service's key
/// ring, which never leaves it, so those users set two-step sign-in up again); reset identity sequences; recompute
/// the manifest's totals and roll everything back unless they match exactly. Documents are stored after the
/// commit, at the keys the imported rows point to.</para>
/// <para>This version maps onto today's schema, which still carries <c>tenant_id</c>; the single-organisation
/// restructure (WP5) extends the mapping. Writes <c>import-report.json</c> next to the export.</para>
/// <para>Exit codes: 0 imported; 1 refused or verification failed (nothing imported); 2 database not ready.</para>
/// </remarks>
public static class LegacyImportCommand
{
    public const string Argument = "--import-legacy";
    public const int SupportedFormatVersion = 1;
    private const int BatchSize = 500;

    /// <summary>Global tables the installation seeds itself: never inserted, mapped instead.</summary>
    private static readonly HashSet<string> SeededGlobalTables = new(StringComparer.Ordinal) { "permissions" };

    public static bool ShouldHandle(string[] args) => args.Contains(Argument, StringComparer.Ordinal);

    public static async Task<int> RunAsync(IServiceProvider services, string[] args, TextWriter output, TextWriter error, CancellationToken ct = default)
    {
        var at = Array.IndexOf(args, Argument);
        var dir = at + 1 < args.Length ? args[at + 1] : null;
        if (string.IsNullOrWhiteSpace(dir) || !File.Exists(Path.Combine(dir, "manifest.json")))
        {
            await error.WriteLineAsync($"[{Argument}] REFUSED: give the export folder (it holds manifest.json): {Argument} <dir>");
            return 1;
        }

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var readiness = await ProductionReadinessEvidence.BuildReadinessAsync(db, configuration, ct, includeDetail: false);
        if (!readiness.Dependencies.Database.Healthy || readiness.PendingMigrations != 0)
        {
            await error.WriteLineAsync($"[{Argument}] REFUSED: the database is not reachable or not fully migrated. Run `--migrate` first.");
            return 2;
        }

        try
        {
            var report = await ImportAsync(db, scope.ServiceProvider.GetService<IDocumentStorage>(), dir, output, ct);
            var reportJson = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            var reportPath = Path.Combine(dir, "import-report.json");
            try
            {
                await File.WriteAllTextAsync(reportPath, reportJson, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The data is committed; a read-only export folder must not make the import look failed.
                reportPath = "(not written: " + ex.Message + ")";
                output.WriteLine(reportJson);
            }
            output.WriteLine($"[{Argument}] Imported '{report.Organisation}': {report.Tables.Sum(t => t.Imported)} rows in {report.Tables.Count} tables, "
                             + $"{report.Documents} documents. Totals match the source exactly. Report: {reportPath}");
            if (report.MfaReset > 0)
                output.WriteLine($"[{Argument}] {report.MfaReset} user(s) set two-step sign-in up again at their next sign-in.");
            output.WriteLine($"[{Argument}] Next: import the licence (--import-licence).");
            return 0;
        }
        catch (LegacyImportRefusedException ex)
        {
            await error.WriteLineAsync($"[{Argument}] REFUSED, nothing imported: {ex.Message}");
            return 1;
        }
    }

    public sealed record TableReport(string Table, long Exported, long Imported, IReadOnlyList<string> DroppedColumns);
    public sealed record ImportReport(string Organisation, Guid OrganisationId, string SourceMigration, DateTime ImportedAtUtc,
        IReadOnlyList<TableReport> Tables, int Documents, int MfaReset, IReadOnlyList<string> Notes);

    internal static async Task<ImportReport> ImportAsync(ZayraDbContext db, IDocumentStorage? storage, string dir, TextWriter output, CancellationToken ct)
    {
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(dir, "manifest.json"), ct))!.AsObject();
        if ((int?)manifest["formatVersion"] != SupportedFormatVersion)
            throw new LegacyImportRefusedException($"export format {manifest["formatVersion"]} is not supported (expected {SupportedFormatVersion}).");
        var tenantId = Guid.Parse((string)manifest["tenant"]!["id"]!);
        var tenantName = (string)manifest["tenant"]!["name"]!;
        var tables = manifest["tables"]!.AsArray().Select(t => (Table: (string)t!["table"]!, Rows: (long)t["rows"]!, Sha: (string)t["sha256"]!)).ToList();
        var documents = manifest["documents"]!.AsArray().Select(d => (Key: (string)d!["key"]!, Sha: (string)d["sha256"]!)).ToList();

        // 1. Every file is what the exporter wrote.
        foreach (var (table, _, sha) in tables)
            if (await Sha256Async(Path.Combine(dir, "tables", table + ".jsonl"), ct) != sha)
                throw new LegacyImportRefusedException($"tables/{table}.jsonl does not match the manifest.");
        foreach (var (key, sha) in documents)
            if (await Sha256Async(Path.Combine(dir, "blobs", key), ct) != sha)
                throw new LegacyImportRefusedException($"document {key} does not match the manifest.");
        if (documents.Count > 0 && storage is null)
            throw new LegacyImportRefusedException("the export has documents but no document storage is configured.");

        // Documents move to this installation's key layout; rows that name a key are rewritten to match.
        var keyMap = documents.ToDictionary(d => d.Key, d => TargetKey(storage!, tenantId, d.Key), StringComparer.Ordinal);

        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        ImportReport report;
        try
        {
            if (await ScalarAsync<long>(connection, tx, "SELECT count(*) FROM tenants", ct) > 0)
                throw new LegacyImportRefusedException("this installation already has an organisation. Import into a new, empty installation.");
            try
            {
                await ExecAsync(connection, tx, "SET LOCAL session_replication_role = replica", ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InsufficientPrivilege)
            {
                throw new LegacyImportRefusedException("the database user may not switch off constraints for the load. Run the import as the database owner (the kit's POSTGRES_USER).");
            }

            var targetColumns = await ColumnsAsync(connection, tx, ct);
            var permissionMap = await PermissionMapAsync(connection, tx, dir, tables.Any(t => t.Table == "permissions"), ct);
            var reports = new List<TableReport>();
            var notes = new List<string>();
            foreach (var (table, exported, _) in tables)
            {
                if (SeededGlobalTables.Contains(table)) continue;
                if (!targetColumns.TryGetValue(table, out var columns))
                {
                    if (exported > 0) throw new LegacyImportRefusedException($"table {table} ({exported} rows) does not exist in this version.");
                    notes.Add($"{table}: empty, and not in this version.");
                    continue;
                }
                var (imported, dropped) = await ImportTableAsync(connection, tx, dir, table, columns, keyMap, permissionMap, ct);
                if (imported != exported) throw new LegacyImportRefusedException($"{table}: {imported} rows imported, {exported} exported.");
                reports.Add(new TableReport(table, exported, imported, dropped));
                output.WriteLine($"  {table}: {imported}");
            }

            // The organisation is the installation's one: same slug as a fresh setup.
            await ExecAsync(connection, tx, $"UPDATE tenants SET slug = '{OrganisationProvisioningService.OrganisationSlug}', is_active = TRUE", ct);
            var mfaReset = await ResetMfaAsync(connection, tx, targetColumns, ct);
            await ResetSequencesAsync(connection, tx, ct);

            // 3. Verification: the money and balances must be exactly the source's.
            var mismatches = await CompareTotalsAsync(connection, tx, manifest["totals"]?.AsObject(), targetColumns, tenantId, ct);
            if (mismatches.Count > 0)
                throw new LegacyImportRefusedException("totals differ from the source: " + string.Join("; ", mismatches.Take(10)));

            notes.Add("Foreign keys are not added by this version; the orphan report runs with the schema restructure (WP5.7).");
            await tx.CommitAsync(ct);
            report = new ImportReport(tenantName, tenantId, (string?)manifest["sourceMigration"] ?? "", DateTime.UtcNow, reports, documents.Count, mfaReset, notes);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }

        // After the commit: a document that fails to store is reported, and the import can be completed by copying
        // the remaining files; the rows are already in place.
        foreach (var (key, _) in documents)
        {
            var bytes = await File.ReadAllBytesAsync(Path.Combine(dir, "blobs", key), ct);
            await storage!.PutAtAsync(tenantId, keyMap[key], bytes, "application/octet-stream", ct);
        }
        return report;
    }

    /// <summary>The key in this installation's store: the part after the tenant prefix, placed under this store's layout.</summary>
    internal static string TargetKey(IDocumentStorage storage, Guid tenantId, string sourceKey)
    {
        var prefix = tenantId.ToString("N") + "/";
        var at = sourceKey.IndexOf(prefix, StringComparison.Ordinal);
        if (at < 0) throw new LegacyImportRefusedException($"document {sourceKey} is not under the tenant's prefix.");
        return storage.TenantKey(tenantId, sourceKey[(at + prefix.Length)..]);
    }

    private static async Task<(long Imported, List<string> Dropped)> ImportTableAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string dir,
        string table, HashSet<string> targetColumns, IReadOnlyDictionary<string, string> keyMap, IReadOnlyDictionary<string, string> permissionMap,
        CancellationToken ct)
    {
        var dropped = new HashSet<string>(StringComparer.Ordinal);
        var batch = new List<JsonObject>(BatchSize);
        long imported = 0;
        HashSet<string>? shared = null;

        async Task FlushAsync()
        {
            if (batch.Count == 0) return;
            shared ??= batch[0].Select(p => p.Key).Where(targetColumns.Contains).ToHashSet(StringComparer.Ordinal);
            var columnList = string.Join(", ", shared.Order(StringComparer.Ordinal).Select(Quote));
            var sql = $"INSERT INTO {Quote(table)} ({columnList}) SELECT {columnList} FROM json_populate_recordset(NULL::{Quote(table)}, @rows::json)";
            await using var command = new NpgsqlCommand(sql, connection, tx) { CommandTimeout = 0 };
            command.Parameters.AddWithValue("rows", new JsonArray(batch.Select(r => (JsonNode)r).ToArray()).ToJsonString());
            imported += await command.ExecuteNonQueryAsync(ct);
            batch.Clear();
        }

        foreach (var line in File.ReadLines(Path.Combine(dir, "tables", table + ".jsonl")))
        {
            if (line.Length == 0) continue;
            var row = JsonNode.Parse(line)!.AsObject();
            foreach (var name in row.Select(p => p.Key).ToList())
            {
                if (!targetColumns.Contains(name))
                {
                    if (row[name] is not null) dropped.Add(name);
                    row.Remove(name);
                    continue;
                }
                if (row[name] is JsonValue value && value.TryGetValue<string>(out var text))
                {
                    if (keyMap.TryGetValue(text, out var newKey)) row[name] = newKey;
                    else if (table == "role_permissions" && name == "permission_id")
                        row[name] = permissionMap.TryGetValue(text, out var mapped) ? mapped
                            : throw new LegacyImportRefusedException($"permission {text} has no match in this version.");
                }
            }
            batch.Add(row);
            if (batch.Count == BatchSize) await FlushAsync();
        }
        await FlushAsync();
        return (imported, dropped.Order(StringComparer.Ordinal).ToList());
    }

    /// <summary>Source permission id → this installation's id for the same permission key.</summary>
    private static async Task<Dictionary<string, string>> PermissionMapAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string dir, bool exported, CancellationToken ct)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!exported) return map;
        var target = new Dictionary<string, string>(StringComparer.Ordinal);
        await using (var command = new NpgsqlCommand("SELECT permission_key, id::text FROM permissions", connection, tx))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) target[reader.GetString(0)] = reader.GetString(1);
        foreach (var line in File.ReadLines(Path.Combine(dir, "tables", "permissions.jsonl")))
        {
            if (line.Length == 0) continue;
            var row = JsonNode.Parse(line)!.AsObject();
            if ((string?)row["permission_key"] is { } key && target.TryGetValue(key, out var id))
                map[(string)row["id"]!] = id;
        }
        return map;
    }

    private static async Task<int> ResetMfaAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Dictionary<string, HashSet<string>> columns, CancellationToken ct)
    {
        if (!columns.TryGetValue("users", out var users) || !users.Contains("mfa_secret_encrypted")) return 0;
        var clear = new[] { "mfa_secret_encrypted", "mfa_configured_at_utc", "mfa_last_verified_at_utc", "mfa_last_totp_step", "mfa_recovery_code_hashes" }
            .Where(users.Contains).Select(c => $"{Quote(c)} = NULL");
        return await ExecAsync(connection, tx,
            $"UPDATE users SET {string.Join(", ", clear)} WHERE mfa_secret_encrypted IS NOT NULL AND mfa_secret_encrypted <> ''", ct);
    }

    private static async Task ResetSequencesAsync(NpgsqlConnection connection, NpgsqlTransaction tx, CancellationToken ct)
    {
        const string sql = """
            SELECT c.table_name, c.column_name, pg_get_serial_sequence(quote_ident(c.table_name), c.column_name)
            FROM information_schema.columns c
            WHERE c.table_schema = 'public' AND pg_get_serial_sequence(quote_ident(c.table_name), c.column_name) IS NOT NULL
            """;
        var sequences = new List<(string Table, string Column, string Sequence)>();
        await using (var command = new NpgsqlCommand(sql, connection, tx))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) sequences.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        foreach (var (table, column, sequence) in sequences)
            await ExecAsync(connection, tx,
                $"SELECT setval('{sequence.Replace("'", "''")}', COALESCE((SELECT MAX({Quote(column)}) FROM {Quote(table)}), 0) + 1, false)", ct);
    }

    /// <summary>The same figures, by the same queries, as the exporter wrote; any difference is reported.</summary>
    private static async Task<List<string>> CompareTotalsAsync(NpgsqlConnection connection, NpgsqlTransaction tx, JsonObject? expected,
        Dictionary<string, HashSet<string>> columns, Guid tenantId, CancellationToken ct)
    {
        var queries = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["payrollNetByRun"] = "SELECT id::text, total_net_salary FROM payroll_runs WHERE tenant_id = @tenant",
            ["leaveBalanceByEmployeeTypeYear"] = "SELECT employee_id::text || '/' || leave_type_id::text || '/' || year::text, entitled + accrued + carried_forward - used FROM employee_leave_balances WHERE tenant_id = @tenant",
            ["loanOutstandingByLoan"] = "SELECT id::text, outstanding_balance FROM employee_loans WHERE tenant_id = @tenant",
            ["glDebitsMinusCreditsByExport"] = "SELECT id::text, total_debits - total_credits FROM gl_journal_exports WHERE tenant_id = @tenant",
        };
        var mismatches = new List<string>();
        foreach (var (name, values) in expected ?? new JsonObject())
        {
            if (!queries.TryGetValue(name, out var sql)) { mismatches.Add($"{name}: unknown total"); continue; }
            var actual = new Dictionary<string, decimal>(StringComparer.Ordinal);
            await using (var command = new NpgsqlCommand(sql, connection, tx))
            {
                command.Parameters.AddWithValue("tenant", tenantId);
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) actual[reader.GetString(0)] = reader.IsDBNull(1) ? 0m : reader.GetDecimal(1);
            }
            foreach (var (key, value) in values!.AsObject())
            {
                var want = value!.GetValue<decimal>();
                if (!actual.TryGetValue(key, out var got)) mismatches.Add($"{name} {key}: missing");
                else if (got != want) mismatches.Add($"{name} {key}: {got} instead of {want}");
            }
            foreach (var extra in actual.Keys.Except(values.AsObject().Select(p => p.Key)))
                mismatches.Add($"{name} {extra}: not in the source");
        }
        return mismatches;
    }

    private static async Task<Dictionary<string, HashSet<string>>> ColumnsAsync(NpgsqlConnection connection, NpgsqlTransaction tx, CancellationToken ct)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        await using var command = new NpgsqlCommand("""
            SELECT c.table_name, c.column_name FROM information_schema.columns c
            JOIN information_schema.tables t USING (table_schema, table_name)
            WHERE c.table_schema = 'public' AND t.table_type = 'BASE TABLE' AND c.is_generated = 'NEVER'
            """, connection, tx);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (!result.TryGetValue(reader.GetString(0), out var set)) result[reader.GetString(0)] = set = new HashSet<string>(StringComparer.Ordinal);
            set.Add(reader.GetString(1));
        }
        return result;
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    private static async Task<int> ExecAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string sql, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection, tx) { CommandTimeout = 0 };
        return await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, NpgsqlTransaction tx, string sql, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection, tx);
        return (T)(await command.ExecuteScalarAsync(ct))!;
    }

    private static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return string.Empty;
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
    }
}

public sealed class LegacyImportRefusedException(string message) : Exception(message);
