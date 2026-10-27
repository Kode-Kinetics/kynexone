using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Npgsql;

namespace KynexOne.TenantExporter;

/// <summary>
/// Exports one customer of the retired hosted service for import into its own installation
/// (<c>--import-legacy</c>, WP13.2). Output, under <c>--out</c>:
/// <list type="bullet">
/// <item><c>tables/&lt;table&gt;.jsonl</c>: one JSON object per row (Postgres <c>row_to_json</c>, column names as stored);</item>
/// <item><c>blobs/&lt;storage key&gt;</c>: every stored document under the tenant's prefix;</item>
/// <item><c>manifest.json</c>: source version, row counts and file hashes, and the source-side totals the
/// import's verification report must reproduce exactly (WP13.3).</item>
/// </list>
/// The export is read-only and runs in one REPEATABLE READ snapshot, so counts and totals agree with the rows.
/// </summary>
public static class Program
{
    public const int FormatVersion = 1;

    /// <summary>Never exported: vendor, billing and platform tables, credentials, and per-installation state.
    /// The data-protection key ring is the vendor's and protects every tenant, so two-step sign-in secrets
    /// cannot travel; users set two-step sign-in up again on the new installation.</summary>
    public static readonly IReadOnlySet<string> ExcludedTables = new HashSet<string>(StringComparer.Ordinal)
    {
        "__EFMigrationsHistory", "DataProtectionKeys", "refresh_tokens", "password_reset_tokens",
        "installation_licences", "worker_heartbeats",
        "platform_users", "platform_announcements", "platform_compliance_controls", "platform_config_entries",
        "platform_leads", "platform_security_incidents",
        "pricing_config", "pricing_module_configs", "pricing_quotes",
        "tenant_subscriptions", "subscription_invoices", "subscription_plans",
    };

    /// <summary>Tables without a tenant_id column that belong to the tenant through another table.</summary>
    private static readonly IReadOnlyDictionary<string, string> LinkedTables = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["tenants"] = "id = @tenant",
        ["user_roles"] = "user_id IN (SELECT id FROM users WHERE tenant_id = @tenant)",
        ["role_permissions"] = "role_id IN (SELECT id FROM roles WHERE tenant_id = @tenant)",
        ["permissions"] = "TRUE",
    };

    public static async Task<int> Main(string[] args)
    {
        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(Options.Usage);
            return 2;
        }

        try
        {
            await ExportAsync(options, Console.Out, CancellationToken.None);
            return 0;
        }
        catch (ExportRefusedException ex)
        {
            Console.Error.WriteLine($"Export refused: {ex.Message}");
            return 1;
        }
    }

    public static async Task ExportAsync(Options options, TextWriter log, CancellationToken ct)
    {
        if (Directory.Exists(options.OutDirectory) && Directory.EnumerateFileSystemEntries(options.OutDirectory).Any())
            throw new ExportRefusedException($"{options.OutDirectory} is not empty.");
        var tablesDir = Directory.CreateDirectory(Path.Combine(options.OutDirectory, "tables")).FullName;

        await using var connection = new NpgsqlConnection(options.ConnectionString);
        await connection.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, tx))
            await readOnly.ExecuteNonQueryAsync(ct);

        var tenant = await ReadTenantAsync(connection, tx, options.TenantId, ct)
                     ?? throw new ExportRefusedException($"No tenant {options.TenantId} in the source database.");
        var sourceMigration = await ScalarAsync<string>(connection, tx,
            "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\" ORDER BY \"MigrationId\" DESC LIMIT 1", options.TenantId, ct);
        log.WriteLine($"Exporting {tenant.Name} ({tenant.Slug}) from a database at migration {sourceMigration}.");

        var columns = await ReadColumnsAsync(connection, tx, ct);
        var tables = new List<TableEntry>();
        var skipped = new List<string>();
        foreach (var (table, tableColumns) in columns.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            string? filter = ExcludedTables.Contains(table) ? null
                : LinkedTables.TryGetValue(table, out var linked) ? linked
                : tableColumns.Contains("tenant_id") ? "tenant_id = @tenant"
                : null;
            if (filter is null)
            {
                skipped.Add(table);
                continue;
            }
            var entry = await ExportTableAsync(connection, tx, table, filter, options.TenantId, tablesDir, ct);
            tables.Add(entry);
            log.WriteLine($"  {table}: {entry.Rows} rows");
        }

        var totals = await ReadTotalsAsync(connection, tx, columns, options.TenantId, ct);
        await tx.CommitAsync(ct);

        var blobs = await ExportBlobsAsync(options, log, ct);

        var manifest = new Manifest(FormatVersion, DateTime.UtcNow, sourceMigration, tenant, tables, skipped, blobs, totals);
        await File.WriteAllTextAsync(Path.Combine(options.OutDirectory, "manifest.json"),
            JsonSerializer.Serialize(manifest, ManifestJson.Options), ct);
        log.WriteLine($"Done: {tables.Count} tables, {tables.Sum(t => t.Rows)} rows, {blobs.Count} documents. "
                      + $"Not exported: {string.Join(", ", skipped)}.");
    }

    private static async Task<TenantInfo?> ReadTenantAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid tenantId, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT name, slug FROM tenants WHERE id = @tenant", connection, tx);
        command.Parameters.AddWithValue("tenant", tenantId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new TenantInfo(tenantId, reader.GetString(0), reader.GetString(1)) : null;
    }

    private static async Task<Dictionary<string, HashSet<string>>> ReadColumnsAsync(NpgsqlConnection connection, NpgsqlTransaction tx, CancellationToken ct)
    {
        const string sql = """
            SELECT c.table_name, c.column_name
            FROM information_schema.columns c
            JOIN information_schema.tables t USING (table_schema, table_name)
            WHERE c.table_schema = 'public' AND t.table_type = 'BASE TABLE'
            """;
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        await using var command = new NpgsqlCommand(sql, connection, tx);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var table = reader.GetString(0);
            if (!result.TryGetValue(table, out var set)) result[table] = set = new HashSet<string>(StringComparer.Ordinal);
            set.Add(reader.GetString(1));
        }
        return result;
    }

    private static async Task<TableEntry> ExportTableAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string table, string filter,
        Guid tenantId, string tablesDir, CancellationToken ct)
    {
        var path = Path.Combine(tablesDir, table + ".jsonl");
        long rows = 0;
        await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        await using (var writer = new StreamWriter(file, new UTF8Encoding(false)) { NewLine = "\n" })
        {
            // Ordered by the whole row so two exports of the same data are byte-identical.
            await using var command = new NpgsqlCommand(
                $"SELECT row_to_json(t)::text FROM {QuoteIdentifier(table)} t WHERE {filter} ORDER BY row_to_json(t)::text", connection, tx);
            command.Parameters.AddWithValue("tenant", tenantId);
            command.CommandTimeout = 0;
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                await writer.WriteLineAsync(reader.GetString(0));
                rows++;
            }
        }
        return new TableEntry(table, rows, await Sha256Async(path, ct));
    }

    /// <summary>The figures WP13.3 requires the import to reproduce: payroll net per run, leave balances, loan
    /// outstanding and GL totals. A table or column the source version lacks is left out, not guessed.</summary>
    private static async Task<Dictionary<string, Dictionary<string, decimal>>> ReadTotalsAsync(NpgsqlConnection connection, NpgsqlTransaction tx,
        Dictionary<string, HashSet<string>> columns, Guid tenantId, CancellationToken ct)
    {
        bool Has(string table, params string[] names) => columns.TryGetValue(table, out var set) && names.All(set.Contains);
        var queries = new List<(string Name, string Sql)>();
        if (Has("payroll_runs", "id", "total_net_salary"))
            queries.Add(("payrollNetByRun", "SELECT id::text, total_net_salary FROM payroll_runs WHERE tenant_id = @tenant"));
        if (Has("employee_leave_balances", "employee_id", "leave_type_id", "year", "entitled", "accrued", "used", "carried_forward"))
            queries.Add(("leaveBalanceByEmployeeTypeYear",
                "SELECT employee_id::text || '/' || leave_type_id::text || '/' || year::text, entitled + accrued + carried_forward - used "
                + "FROM employee_leave_balances WHERE tenant_id = @tenant"));
        if (Has("employee_loans", "id", "outstanding_balance"))
            queries.Add(("loanOutstandingByLoan", "SELECT id::text, outstanding_balance FROM employee_loans WHERE tenant_id = @tenant"));
        if (Has("gl_journal_exports", "id", "total_debits", "total_credits"))
            queries.Add(("glDebitsMinusCreditsByExport", "SELECT id::text, total_debits - total_credits FROM gl_journal_exports WHERE tenant_id = @tenant"));

        var totals = new Dictionary<string, Dictionary<string, decimal>>(StringComparer.Ordinal);
        foreach (var (name, sql) in queries)
        {
            var values = new SortedDictionary<string, decimal>(StringComparer.Ordinal);
            await using var command = new NpgsqlCommand(sql, connection, tx);
            command.Parameters.AddWithValue("tenant", tenantId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                values[reader.GetString(0)] = reader.IsDBNull(1) ? 0m : reader.GetDecimal(1);
            totals[name] = new Dictionary<string, decimal>(values, StringComparer.Ordinal);
        }
        return totals;
    }

    private static async Task<List<BlobEntry>> ExportBlobsAsync(Options options, TextWriter log, CancellationToken ct)
    {
        var blobs = new List<BlobEntry>();
        var blobsDir = Path.Combine(options.OutDirectory, "blobs");
        var prefix = options.TenantId.ToString("N");
        if (options.StorageDirectory is { } root)
        {
            // Local storage keys are "storage/documents/<tenant>/…", relative to Storage:LocalRoot.
            var tenantDir = Path.Combine(root, "storage", "documents", prefix);
            if (!Directory.Exists(tenantDir))
            {
                log.WriteLine($"No documents under {tenantDir}.");
                return blobs;
            }
            foreach (var file in Directory.EnumerateFiles(tenantDir, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                var key = Path.GetRelativePath(root, file).Replace('\\', '/');
                var target = SafeTarget(blobsDir, key);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
                blobs.Add(new BlobEntry(key, new FileInfo(target).Length, await Sha256Async(target, ct)));
            }
        }
        else if (options.S3Bucket is { } bucket)
        {
            // S3 keys are "<tenant>/…".
            using var s3 = new AmazonS3Client(new BasicAWSCredentials(options.S3AccessKey, options.S3SecretKey),
                new AmazonS3Config { ServiceURL = options.S3Endpoint, ForcePathStyle = true, AuthenticationRegion = options.S3Region });
            var request = new ListObjectsV2Request { BucketName = bucket, Prefix = prefix + "/" };
            ListObjectsV2Response page;
            do
            {
                page = await s3.ListObjectsV2Async(request, ct);
                foreach (var item in page.S3Objects ?? [])
                {
                    var target = SafeTarget(blobsDir, item.Key);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    using (var response = await s3.GetObjectAsync(bucket, item.Key, ct))
                    await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write))
                        await response.ResponseStream.CopyToAsync(output, ct);
                    blobs.Add(new BlobEntry(item.Key, new FileInfo(target).Length, await Sha256Async(target, ct)));
                }
                request.ContinuationToken = page.NextContinuationToken;
            } while (page.IsTruncated == true);
        }
        else
        {
            log.WriteLine("No storage given (--storage-dir or --s3-bucket): documents are not exported.");
        }
        log.WriteLine($"  documents: {blobs.Count} files, {blobs.Sum(b => b.Bytes)} bytes");
        return blobs;
    }

    /// <summary>A storage key as a path under the output folder; a key that would leave it is refused.</summary>
    internal static string SafeTarget(string blobsDir, string key)
    {
        var full = Path.GetFullPath(Path.Combine(blobsDir, key));
        var rootWithSeparator = Path.GetFullPath(blobsDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootWithSeparator, StringComparison.Ordinal))
            throw new ExportRefusedException($"Storage key '{key}' points outside the export folder.");
        return full;
    }

    internal static string QuoteIdentifier(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, NpgsqlTransaction tx, string sql, Guid tenantId, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection, tx);
        command.Parameters.AddWithValue("tenant", tenantId);
        return (T)(await command.ExecuteScalarAsync(ct))!;
    }

    private static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
    }
}

public sealed class ExportRefusedException(string message) : Exception(message);

public sealed record TenantInfo(Guid Id, string Name, string Slug);
public sealed record TableEntry(string Table, long Rows, string Sha256);
public sealed record BlobEntry(string Key, long Bytes, string Sha256);
public sealed record Manifest(
    int FormatVersion,
    DateTime ExportedAtUtc,
    string SourceMigration,
    TenantInfo Tenant,
    IReadOnlyList<TableEntry> Tables,
    IReadOnlyList<string> NotExported,
    IReadOnlyList<BlobEntry> Documents,
    IReadOnlyDictionary<string, Dictionary<string, decimal>> Totals);

internal static class ManifestJson
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
}

public sealed record Options(
    string ConnectionString,
    Guid TenantId,
    string OutDirectory,
    string? StorageDirectory,
    string? S3Endpoint,
    string? S3Bucket,
    string? S3Region,
    string? S3AccessKey,
    string? S3SecretKey)
{
    public const string Usage = """
        kynexone-tenant-exporter --connection <connection string> --tenant <tenant id> --out <empty folder>
                                 [--storage-dir <Storage:LocalRoot>]
                                 [--s3-endpoint <url> --s3-bucket <bucket> --s3-region <region>]
        S3 credentials come from KX_EXPORT_S3_ACCESS_KEY and KX_EXPORT_S3_SECRET_KEY, never the command line.
        """;

    public static Options Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
                throw new ArgumentException($"Unexpected argument '{args[i]}'.");
            values[args[i][2..]] = args[++i];
        }
        string Required(string name) => values.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v)
            ? v : throw new ArgumentException($"--{name} is required.");
        string? Optional(string name) => values.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

        if (!Guid.TryParse(Required("tenant"), out var tenantId)) throw new ArgumentException("--tenant must be a tenant id (GUID).");
        var options = new Options(Required("connection"), tenantId, Path.GetFullPath(Required("out")), Optional("storage-dir"),
            Optional("s3-endpoint"), Optional("s3-bucket"), Optional("s3-region") ?? "us-east-1",
            Environment.GetEnvironmentVariable("KX_EXPORT_S3_ACCESS_KEY"), Environment.GetEnvironmentVariable("KX_EXPORT_S3_SECRET_KEY"));
        if (options.StorageDirectory is not null && options.S3Bucket is not null)
            throw new ArgumentException("Give --storage-dir or --s3-bucket, not both.");
        if (options.S3Bucket is not null && (options.S3Endpoint is null || options.S3AccessKey is null || options.S3SecretKey is null))
            throw new ArgumentException("--s3-bucket needs --s3-endpoint and the KX_EXPORT_S3_ACCESS_KEY / KX_EXPORT_S3_SECRET_KEY variables.");
        return options;
    }
}
