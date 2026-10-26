using System.Globalization;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Zayra.Api.Data;

namespace Zayra.Api.Tests;

/// <summary>
/// WP-E harness: ONE real PostgreSQL 16 database migrated through the full EF chain (so migration M2,
/// <c>*_FinanceGlEntriesAppendOnly</c>, is exactly what production gets), shared by the ledger-protection test
/// classes. Every test seeds its own rows under fresh ids, so classes in the collection do not interfere.
/// Raw SQL on purpose: the rules under test are database rules, and an ORM would hide the statement order
/// that the deferred checks must be independent of.
/// </summary>
public sealed class LedgerDbFixture : IAsyncLifetime
{
    public const string Cc23001 = "23001"; // restrict_violation: an immutability / state-machine rule (immediate)
    public const string Cc23514 = "23514"; // check_violation: a rule that is evaluated at COMMIT (deferred)
    public const string Cc42501 = "42501"; // insufficient_privilege

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();

    public string ConnectionString => _container.GetConnectionString();
    public Guid Tenant { get; } = Guid.NewGuid();
    public Guid CompanyA { get; } = Guid.NewGuid();
    public Guid CompanyB { get; } = Guid.NewGuid();
    public Guid CcA1 { get; } = Guid.NewGuid();
    public Guid CcA2 { get; } = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await using (var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql(ConnectionString).Options))
            await db.Database.MigrateAsync();

        foreach (var (id, name) in new[] { (CompanyA, "A"), (CompanyB, "B") })
            await Exec(await Insert("companies", new()
            {
                ["id"] = Q(id), ["tenant_id"] = Q(Tenant), ["legal_name_en"] = $"'Co {name}'", ["registration_number"] = $"'R-{name}'",
                ["email_domain"] = $"'{name.ToLowerInvariant()}.example'", ["is_active"] = "true", ["is_deleted"] = "false",
            }));
        await Exec(await Insert("cost_centers", CostCentre(CcA1, CompanyA, "CC-A1")));
        await Exec(await Insert("cost_centers", CostCentre(CcA2, CompanyA, "CC-A2")));
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    // ── fixtures ──────────────────────────────────────────────────────────────────────────────────────────────────

    public static string Q(Guid g) => $"'{g}'";
    public static string Q(string s) => "'" + s.Replace("'", "''") + "'";

    public Dictionary<string, string> CostCentre(Guid id, Guid? company, string code, Guid? parent = null, Guid? tenant = null) => new()
    {
        ["id"] = Q(id), ["tenant_id"] = Q(tenant ?? Tenant), ["company_id"] = company is null ? "NULL" : Q(company.Value),
        ["code"] = Q(code), ["name"] = Q(code), ["is_active"] = "true", ["is_deleted"] = "false",
        ["parent_id"] = parent is null ? "NULL" : Q(parent.Value),
    };

    /// <summary>One INSERT of a ledger row, every column explicit. Defaults describe a two-sided payroll accrual of 100.</summary>
    public string InsertEntry(Guid id, Guid? company, Guid? tenant = null, string debit = "5001 - Salary", string credit = "2100 - Net pay",
        decimal amount = 100m, string currency = "SAR", Guid? reversalOf = null, string eventType = "PayrollAccrual",
        string module = "Payroll", Guid? source = null, string status = "NotReady", string? doc = null,
        Guid? cc = null, string? ccCode = null, string? ccName = null, string? ccSegment = null, string? key = null,
        bool reversed = false, string period = "2026-09", string description = "row", string sourceRef = "RUN-1") =>
        "INSERT INTO finance_gl_entries (id, tenant_id, company_id, source_module, source_entity_id, source_entity_ref, event_type, " +
        "debit_account, credit_account, amount, currency, entry_date, period, description, posted_by_name, posted_by, is_reversed, " +
        "reversal_of_entry_id, created_at_utc, erp_posting_status, erp_document_number, cost_center_id, cost_center_code, " +
        "cost_center_name, cost_center_gl_segment, posting_key) VALUES (" +
        $"{Q(id)}, {Q(tenant ?? Tenant)}, {(company is null ? "NULL" : Q(company.Value))}, {Q(module)}, {Q(source ?? Guid.NewGuid())}, {Q(sourceRef)}, {Q(eventType)}, " +
        $"{Q(debit)}, {Q(credit)}, {amount.ToString(CultureInfo.InvariantCulture)}, {Q(currency)}, DATE '2026-09-15', {Q(period)}, {Q(description)}, 'poster', " +
        $"{Q(Guid.NewGuid())}, {(reversed ? "true" : "false")}, {(reversalOf is null ? "NULL" : Q(reversalOf.Value))}, now(), {Q(status)}, " +
        $"{(doc is null ? "NULL" : Q(doc))}, {(cc is null ? "NULL" : Q(cc.Value))}, {(ccCode is null ? "NULL" : Q(ccCode))}, " +
        $"{(ccName is null ? "NULL" : Q(ccName))}, {(ccSegment is null ? "NULL" : Q(ccSegment))}, {(key is null ? "NULL" : Q(key))})";

    /// <summary>Posts a row in its own transaction and returns its id.</summary>
    public async Task<Guid> Post(Guid? company, Guid? tenant = null, string debit = "5001 - Salary", string credit = "2100 - Net pay",
        decimal amount = 100m, string currency = "SAR", string eventType = "PayrollAccrual", string module = "Payroll", Guid? source = null,
        string status = "NotReady", string? doc = null, Guid? cc = null, string? ccCode = null, string? ccName = null, string? ccSegment = null,
        string? key = null, bool reversed = false, string sourceRef = "RUN-1")
    {
        var id = Guid.NewGuid();
        await Exec(InsertEntry(id, company, tenant, debit, credit, amount, currency, null, eventType, module, source, status, doc,
            cc, ccCode, ccName, ccSegment, key, reversed, sourceRef: sourceRef));
        return id;
    }

    public Task<string> ExportSql(Guid exportId, string docNumber, string kind = "Full", string status = "Confirmed",
        string? confirmationsJson = null, Guid? tenant = null) => Insert("gl_journal_exports", new()
    {
        ["id"] = Q(exportId), ["tenant_id"] = Q(tenant ?? Tenant), ["status"] = Q(status), ["kind"] = Q(kind),
        ["erp_document_number"] = confirmationsJson is null ? Q(docNumber) : "NULL",
        ["period"] = Q("Z" + Guid.NewGuid().ToString("N")[..6]), // ux_gl_journal_exports_live_scope: one live export per scope
        ["journal_confirmations_json"] = Q(confirmationsJson ?? "[]"),
    });

    public Task<string> ExportLineSql(Guid exportId, Guid entryId, Guid? tenant = null) => Insert("gl_journal_export_lines", new()
    {
        ["id"] = Q(Guid.NewGuid()), ["tenant_id"] = Q(tenant ?? Tenant), ["gl_journal_export_id"] = Q(exportId),
        ["finance_gl_entry_id"] = Q(entryId), ["line_no"] = "1",
    });

    /// <summary>An export that carries <paramref name="entryId"/> and the ERP's document number for it.</summary>
    public async Task<Guid> AddExportEvidence(Guid entryId, string docNumber, string kind = "Full", string status = "Confirmed",
        string? confirmationsJson = null, Guid? tenant = null)
    {
        var exportId = Guid.NewGuid();
        await Exec(await ExportSql(exportId, docNumber, kind, status, confirmationsJson, tenant));
        await Exec(await ExportLineSql(exportId, entryId, tenant));
        return exportId;
    }

    /// <summary>An export that only carries a line (nothing confirmed): the entry "was exported".</summary>
    public async Task<Guid> AddExportLineOnly(Guid entryId, Guid? tenant = null)
        => await AddExportEvidence(entryId, "n/a", status: "Exported", tenant: tenant, confirmationsJson: "[]");

    // ── SQL helpers ───────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<int> Exec(string sql, string? connectionString = null)
    {
        await using var conn = new NpgsqlConnection(connectionString ?? ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return await cmd.ExecuteNonQueryAsync();
    }

    public async Task<T> Scalar<T>(string sql, string? connectionString = null)
    {
        await using var conn = new NpgsqlConnection(connectionString ?? ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        var value = await cmd.ExecuteScalarAsync();
        return value is null or DBNull ? default! : (T)Convert.ChangeType(value, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T), CultureInfo.InvariantCulture);
    }

    public async Task<List<string>> Column(string sql)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        var list = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) list.Add(reader.GetString(0));
        return list;
    }

    /// <summary>The statement(s) must fail with <paramref name="sqlState"/> (autocommit: a deferred rule fails at its implicit COMMIT).</summary>
    public async Task<PostgresException> Expect(string sqlState, string sql, string because = "", string? connectionString = null)
    {
        var act = async () => await Exec(sql, connectionString);
        var ex = (await act.Should().ThrowAsync<PostgresException>(because + " :: " + sql)).Which;
        ex.SqlState.Should().Be(sqlState, because + " :: " + sql + " :: " + ex.MessageText);
        return ex;
    }

    /// <summary>Runs the statements in ONE transaction and commits. Whatever fails, statement or COMMIT, must carry <paramref name="sqlState"/>.</summary>
    public async Task<PostgresException> ExpectTx(string sqlState, string because, params string[] statements)
    {
        var act = async () => await RunTx(statements);
        var ex = (await act.Should().ThrowAsync<PostgresException>(because)).Which;
        ex.SqlState.Should().Be(sqlState, because + " :: " + ex.MessageText);
        return ex;
    }

    /// <summary>Runs the statements in ONE transaction and commits; any failure surfaces.</summary>
    public async Task RunTx(params string[] statements)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        foreach (var sql in statements)
        {
            await using var cmd = new NpgsqlCommand(sql, conn, tx);
            await cmd.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }

    public Task<string> Insert(string table, Dictionary<string, string> overrides) => InsertRowSql(table, overrides);

    /// <summary>An INSERT filling every NOT NULL column without a default with a neutral value of its type, plus overrides.</summary>
    public async Task<string> InsertRowSql(string table, Dictionary<string, string> overrides)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT column_name, data_type FROM information_schema.columns WHERE table_schema = 'public' AND table_name = @t " +
            "AND is_nullable = 'NO' AND column_default IS NULL AND is_generated = 'NEVER' ORDER BY ordinal_position", conn);
        cmd.Parameters.AddWithValue("t", table);
        var columns = new List<string>(); var values = new List<string>();
        await using (var reader = await cmd.ExecuteReaderAsync())
            while (await reader.ReadAsync())
            {
                var name = reader.GetString(0);
                columns.Add(name);
                values.Add(overrides.TryGetValue(name, out var v) ? v : reader.GetString(1) switch
                {
                    "uuid" => "gen_random_uuid()",
                    "integer" or "smallint" or "bigint" or "numeric" or "double precision" or "real" => "0",
                    "boolean" => "false",
                    "timestamp with time zone" or "timestamp without time zone" => "now()",
                    "date" => "current_date",
                    "jsonb" or "json" => "'{}'",
                    _ => "'x'",
                });
            }
        foreach (var (name, value) in overrides.Where(o => !columns.Contains(o.Key))) { columns.Add(name); values.Add(value); }
        return new StringBuilder($"INSERT INTO {table} (").AppendJoin(", ", columns).Append(") VALUES (").AppendJoin(", ", values).Append(')').ToString();
    }
}

[CollectionDefinition("LedgerDb")]
public sealed class LedgerDbCollection : ICollectionFixture<LedgerDbFixture> { }
