using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql;
using Zayra.Api.Data.V2;

namespace Zayra.Api.Tests;

/// <summary>
/// The gate on the V2 model: it must be the same shape as the database the baseline SQL builds.
///
/// This is not a smoke test. The fixture builds a real postgres:16 from
/// <c>Db/baseline/001..060</c> and these tests read <c>pg_catalog</c>, then read the same facts
/// out of <see cref="KynexDbContext"/>'s finalised relational model, and compare them as sets. If
/// a column is added to the SQL and not to the model, or a type or a nullability or a key or a
/// foreign key drifts in either direction, the comparison fails and names the difference. There is
/// no tolerance and no "close enough" — the whole reason this model exists is that a hundred
/// services are about to be ported onto it.
///
/// The exclusions are few, each named and each argued:
///   * PARTITION CHILDREN are not entities. Access goes through the parent, which is where the
///     grants and the p_tenant policy live (§19.3 consequence 3).
///   * The <c>xmin</c> column is a PostgreSQL system column with a negative attnum. It is in the
///     model because §19.5 makes it the concurrency token; it is not in pg_attribute's user
///     columns. <see cref="EveryMutableTable_UsesXminAsItsConcurrencyToken"/> asserts it directly.
///   * TWO FOREIGN KEYS CANNOT BE MODELLED BY EF AT ALL, because their principal key contains a
///     nullable column, which EF forbids. They are named in <see cref="UnmodellableForeignKeys"/>
///     and asserted to exist in the database, so the omission stays a decision rather than a bug.
/// </summary>
[Trait("Category", "Integration")]
[Collection("KynexBaseline")]
public sealed class KynexModelMatchesBaselineTests
{
    private readonly KynexBaselineFixture _fx;

    public KynexModelMatchesBaselineTests(KynexBaselineFixture fx) => _fx = fx;

    /// <summary>
    /// EF requires every column of a principal key to be non-nullable. Both of these reference a
    /// unique index over a nullable column, which is legal in PostgreSQL and meant:
    ///   * companies.gosi_registration_no is NULL until the establishment is registered;
    ///   * background_jobs.tenant_id is NULL for a platform-tier job.
    /// The database enforces both; the model carries the columns and not the relationship, and a
    /// service that needs the parent joins on it explicitly.
    /// </summary>
    private static readonly HashSet<string> UnmodellableForeignKeys = new(StringComparer.Ordinal)
    {
        "fk_employee_gosi_registrations__gosi_registration_no",
        "fk_payroll_runs__source_import_job_id",
    };

    private KynexDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<KynexDbContext>().UseNpgsql(_fx.ConnectionString).Options);

    // ───────────────────────────────────────── tables ───────────────────────────────────────────

    [Fact]
    public async Task EveryBaselineTable_IsMappedByExactlyOneEntity_AndNothingElseIs()
    {
        var inDatabase = await _fx.QueryAsync(
            """
            SELECT c.relname
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p') AND NOT c.relispartition
            ORDER BY 1
            """,
            r => r.GetString(0));

        using var db = CreateContext();
        var inModel = db.Model.GetRelationalModel().Tables
            .Where(t => t.Schema is null or "public")
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(inDatabase, inModel);
        Assert.Equal(76, inModel.Count);
    }

    [Fact]
    public async Task BothSecurityInvokerViews_AreMappedAsKeylessProjections()
    {
        var inDatabase = await _fx.QueryAsync(
            "SELECT viewname FROM pg_views WHERE schemaname = 'public' AND viewname LIKE 'v\\_%' ORDER BY 1",
            r => r.GetString(0));

        using var db = CreateContext();
        var inModel = db.Model.GetRelationalModel().Views
            .Select(v => v.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(new[] { "v_employee_current", "v_leave_balances" }, inDatabase);
        Assert.Equal(inDatabase, inModel);

        foreach (var entity in db.Model.GetEntityTypes().Where(e => e.GetViewName() is not null))
        {
            Assert.Null(entity.FindPrimaryKey());
        }
    }

    [Fact]
    public async Task NoPartitionChild_IsAnEntity()
    {
        var children = await _fx.QueryAsync(
            """
            SELECT c.relname
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public' AND c.relispartition
            """,
            r => r.GetString(0));

        Assert.NotEmpty(children);

        using var db = CreateContext();
        var mapped = db.Model.GetRelationalModel().Tables.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Empty(children.Where(mapped.Contains));
    }

    // ──────────────────────────────── columns, types, nullability ───────────────────────────────

    [Fact]
    public async Task EveryColumn_MatchesTheBaseline_InNameTypeAndNullability()
    {
        var inDatabase = await ReadColumnsAsync(relkinds: "'r', 'p'", includePartitionChildren: false);

        using var db = CreateContext();
        var inModel = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var table in db.Model.GetRelationalModel().Tables)
        {
            foreach (var column in table.Columns)
            {
                if (column.Name == "xmin")
                {
                    continue; // system column; asserted by EveryMutableTable_UsesXminAsItsConcurrencyToken
                }

                inModel[$"{table.Name}.{column.Name}"] = Describe(column.StoreType, column.IsNullable);
            }
        }

        AssertSameMap(inDatabase, inModel, "column");
    }

    [Fact]
    public async Task EveryViewColumn_MatchesTheBaseline_InNameTypeAndNullability()
    {
        // A view's columns are always reported nullable by pg_attribute's attnotnull (a view has no
        // constraints), so only name and type are compared here; the projections are read-only.
        var inDatabase = await ReadColumnsAsync(relkinds: "'v'", includePartitionChildren: false, typeOnly: true);
        inDatabase = new SortedDictionary<string, string>(
            inDatabase.Where(kv => kv.Key.StartsWith("v_", StringComparison.Ordinal))
                      .ToDictionary(kv => kv.Key, kv => kv.Value),
            StringComparer.Ordinal);

        using var db = CreateContext();
        var inModel = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var view in db.Model.GetRelationalModel().Views)
        {
            foreach (var column in view.Columns)
            {
                inModel[$"{view.Name}.{column.Name}"] = column.StoreType;
            }
        }

        AssertSameMap(inDatabase, inModel, "view column");
    }

    // ────────────────────────────────────────── keys ────────────────────────────────────────────

    [Fact]
    public async Task EveryPrimaryKey_MatchesTheBaseline_InNameAndColumnOrder()
    {
        var inDatabase = await ReadConstraintsAsync('p');

        using var db = CreateContext();
        var inModel = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var table in db.Model.GetRelationalModel().Tables)
        {
            var pk = table.PrimaryKey;
            if (pk is null)
            {
                continue;
            }

            inModel[$"{table.Name}.{pk.Name}"] = string.Join(",", pk.Columns.Select(c => c.Name));
        }

        AssertSameMap(inDatabase, inModel, "primary key");
    }

    /// <summary>
    /// The five partitioned parents must key on (id, partition key): PostgreSQL refuses a primary
    /// key on a partitioned table that does not contain the partition column, and §19.3 says so.
    /// Asserted by name because it is the one place the model's keys differ in SHAPE from the
    /// tenant convention, and a future edit that "tidied" them to (id) would not compile in SQL
    /// but would look harmless in C#.
    /// </summary>
    [Theory]
    [InlineData("attendance_days", "id,work_date")]
    [InlineData("attendance_punches", "id,occurred_at")]
    [InlineData("audit_logs", "id,created_at")]
    [InlineData("background_job_items", "id,created_at")]
    [InlineData("timesheet_entries", "id,work_date")]
    public void EveryPartitionedParent_KeysOnItsPartitionKey(string table, string expected)
    {
        using var db = CreateContext();
        var pk = db.Model.GetRelationalModel().Tables.Single(t => t.Name == table).PrimaryKey;
        Assert.NotNull(pk);
        Assert.Equal(expected, string.Join(",", pk!.Columns.Select(c => c.Name)));
    }

    [Fact]
    public async Task EveryUniqueConstraint_MatchesTheBaseline_InNameAndColumns()
    {
        var inDatabase = await ReadConstraintsAsync('u');

        using var db = CreateContext();
        var inModel = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var table in db.Model.GetRelationalModel().Tables)
        {
            foreach (var unique in table.UniqueConstraints)
            {
                inModel[$"{table.Name}.{unique.Name}"] = string.Join(",", unique.Columns.Select(c => c.Name));
            }

            foreach (var index in table.Indexes.Where(i => i.IsUnique))
            {
                inModel[$"{table.Name}.{index.Name}"] = string.Join(",", index.Columns.Select(c => c.Name));
            }
        }

        // The model may carry a unique constraint under both spellings (EF materialises an
        // alternate key for a principal end AND keeps the scaffolded index); the dictionary keys on
        // the constraint name, so the two collapse. What must not happen is a name in one and not
        // the other, or the same name over different columns.
        foreach (var (key, value) in inDatabase)
        {
            Assert.True(inModel.ContainsKey(key), $"unique constraint missing from the model: {key} ({value})");
            Assert.Equal(value, inModel[key]);
        }
    }

    [Fact]
    public async Task EveryForeignKey_MatchesTheBaseline_InNameColumnsAndTarget()
    {
        var inDatabase = await _fx.QueryAsync(
            """
            SELECT rel.relname, con.conname, frel.relname,
                   (SELECT string_agg(a.attname, ',' ORDER BY k.ord)
                      FROM unnest(con.conkey) WITH ORDINALITY AS k(attnum, ord)
                      JOIN pg_attribute a ON a.attrelid = con.conrelid AND a.attnum = k.attnum),
                   (SELECT string_agg(a.attname, ',' ORDER BY k.ord)
                      FROM unnest(con.confkey) WITH ORDINALITY AS k(attnum, ord)
                      JOIN pg_attribute a ON a.attrelid = con.confrelid AND a.attnum = k.attnum)
            FROM pg_constraint con
            JOIN pg_class rel ON rel.oid = con.conrelid
            JOIN pg_class frel ON frel.oid = con.confrelid
            JOIN pg_namespace n ON n.oid = rel.relnamespace
            WHERE n.nspname = 'public' AND con.contype = 'f' AND NOT rel.relispartition
            """,
            r => (Table: r.GetString(0), Name: r.GetString(1), Target: r.GetString(2),
                  From: r.GetString(3), To: r.GetString(4)));

        var expected = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var fk in inDatabase.Where(f => !UnmodellableForeignKeys.Contains(f.Name)))
        {
            expected[$"{fk.Table}.{fk.Name}"] = $"({fk.From}) -> {fk.Target}({fk.To})";
        }

        // Every excluded key must actually exist, or the exclusion list has gone stale.
        foreach (var name in UnmodellableForeignKeys)
        {
            Assert.Contains(inDatabase, f => f.Name == name);
        }

        using var db = CreateContext();
        var inModel = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var table in db.Model.GetRelationalModel().Tables)
        {
            foreach (var fk in table.ForeignKeyConstraints)
            {
                inModel[$"{table.Name}.{fk.Name}"] =
                    $"({string.Join(",", fk.Columns.Select(c => c.Name))}) -> " +
                    $"{fk.PrincipalTable.Name}({string.Join(",", fk.PrincipalColumns.Select(c => c.Name))})";
            }
        }

        AssertSameMap(expected, inModel, "foreign key");
    }

    // ─────────────────────────────────── modelling decisions ────────────────────────────────────

    [Fact]
    public void NoEntity_CarriesAGlobalQueryFilter()
    {
        using var db = CreateContext();
        var filtered = db.Model.GetEntityTypes()
            .Where(e => e.GetQueryFilter() is not null)
            .Select(e => e.ClrType.Name)
            .ToList();

        Assert.True(
            filtered.Count == 0,
            "Tenant isolation in the V2 schema is row-level security (TARGET_SCHEMA.md §19.2), not a " +
            "query filter. A filter here would be a second, weaker rule that .IgnoreQueryFilters() " +
            "turns off. Entities carrying one: " + string.Join(", ", filtered));
    }

    [Fact]
    public void EveryMutableTable_UsesXminAsItsConcurrencyToken()
    {
        using var db = CreateContext();
        var missing = new List<string>();
        var unexpected = new List<string>();

        foreach (var entity in db.Model.GetEntityTypes())
        {
            var table = entity.GetTableName();
            if (table is null || entity.FindPrimaryKey() is null)
            {
                continue;
            }

            var hasXmin = entity.GetProperties()
                .Any(p => p.IsConcurrencyToken && p.GetColumnName() == "xmin");

            if (KynexDbContext.IsXminExempt(table))
            {
                if (hasXmin)
                {
                    unexpected.Add(table);
                }
            }
            else if (!hasXmin)
            {
                missing.Add(table);
            }
        }

        Assert.True(missing.Count == 0, "no xmin concurrency token on: " + string.Join(", ", missing));
        Assert.True(unexpected.Count == 0, "xmin token on an exempt table: " + string.Join(", ", unexpected));
    }

    [Fact]
    public void NoEntity_CarriesTheFiveCollectionGraph()
    {
        using var db = CreateContext();

        var collections = db.Model.GetEntityTypes()
            .Select(e => (Entity: e.ClrType.Name,
                          Navigations: e.GetNavigations().Where(n => n.IsCollection).Select(n => n.Name).ToList()))
            .Where(x => x.Navigations.Count > 0)
            .ToList();

        // Nine compositions, one per parent, each a "its own lines" relationship the design already
        // makes ON DELETE CASCADE. Everything else is an explicit join.
        Assert.Equal(9, collections.Count);
        Assert.Empty(collections.Where(c => c.Navigations.Count > 1));

        var expected = new[]
        {
            "ApprovalRequest", "BackgroundJob", "FinalSettlement", "GlJournal", "Loan",
            "PayrollSlip", "StatutoryRule", "Timesheet", "WpsBatch",
        };
        Assert.Equal(expected, collections.Select(c => c.Entity).OrderBy(n => n, StringComparer.Ordinal));

        // payroll_runs -> payroll_slips is deliberately NOT one of them: a 5,000-employee run would
        // load 5,000 slips and 75,000 lines behind one Include (§19.5, the chunked-batch rule).
        Assert.DoesNotContain(collections, c => c.Entity == "PayrollRun");
    }

    /// <summary>
    /// Money is <c>numeric(18,2)</c> and nothing else (§Conventions, §13.2). This asserts it from
    /// the database side — every column the baseline declares numeric(18,2) must be a decimal with
    /// precision 18 and scale 2 in the model — so a hand edit that widened one cannot pass.
    /// </summary>
    [Fact]
    public async Task EveryMoneyColumn_IsDecimalWithTheBaselinePrecision()
    {
        var money = await _fx.QueryAsync(
            """
            SELECT c.relname, a.attname, format_type(a.atttypid, a.atttypmod)
            FROM pg_attribute a
            JOIN pg_class c ON c.oid = a.attrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p') AND NOT c.relispartition
              AND a.attnum > 0 AND NOT a.attisdropped
              AND format_type(a.atttypid, a.atttypmod) = 'numeric(18,2)'
            """,
            r => (Table: r.GetString(0), Column: r.GetString(1)));

        Assert.NotEmpty(money);

        using var db = CreateContext();
        var tables = db.Model.GetRelationalModel().Tables.ToDictionary(t => t.Name, StringComparer.Ordinal);
        foreach (var (table, column) in money)
        {
            var mapped = tables[table].Columns.Single(c => c.Name == column);
            Assert.Equal("numeric(18,2)", mapped.StoreType);
            Assert.Equal(typeof(decimal), Nullable.GetUnderlyingType(mapped.ProviderClrType) ?? mapped.ProviderClrType);
        }
    }

    /// <summary>
    /// Minutes are <c>int</c>, local dates are <c>DateOnly</c>, instants are <c>DateTime</c>, and a
    /// <c>date</c> column may never surface as a DateTime — that is how "today" drifts between a
    /// dashboard and a payroll run (§13.4).
    /// </summary>
    [Fact]
    public async Task LocalDatesAreDateOnly_InstantsAreDateTime_AndMinutesAreInt()
    {
        var typed = await _fx.QueryAsync(
            """
            SELECT c.relname, a.attname, format_type(a.atttypid, a.atttypmod)
            FROM pg_attribute a
            JOIN pg_class c ON c.oid = a.attrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p') AND NOT c.relispartition
              AND a.attnum > 0 AND NOT a.attisdropped
              AND (format_type(a.atttypid, a.atttypmod) IN ('date', 'timestamp with time zone')
                   OR a.attname LIKE '%\_minutes')
            """,
            r => (Table: r.GetString(0), Column: r.GetString(1), Type: r.GetString(2)));

        using var db = CreateContext();
        var tables = db.Model.GetRelationalModel().Tables.ToDictionary(t => t.Name, StringComparer.Ordinal);

        foreach (var (table, column, type) in typed)
        {
            var mapped = tables[table].Columns.Single(c => c.Name == column);
            var clr = Nullable.GetUnderlyingType(mapped.ProviderClrType) ?? mapped.ProviderClrType;
            var where = $"{table}.{column} ({type})";

            switch (type)
            {
                case "date":
                    Assert.True(clr == typeof(DateOnly), $"{where} must be DateOnly, is {clr.Name}");
                    break;
                case "timestamp with time zone":
                    Assert.True(clr == typeof(DateTime), $"{where} must be DateTime, is {clr.Name}");
                    break;
                default:
                    Assert.True(clr == typeof(int), $"{where} is a minute count and must be int, is {clr.Name}");
                    break;
            }
        }
    }

    // ───────────────────────── what EF cannot express, asserted as absent ────────────────────────

    /// <summary>
    /// The half of the baseline EF has no vocabulary for. This does not assert that the model is
    /// missing them — it asserts that the DATABASE has them, so a reader of the model knows the
    /// rules exist and are not being re-implemented in C#. If one of these counts ever drops to
    /// zero the baseline has regressed, whatever the model says.
    /// </summary>
    [Fact]
    public async Task TheRulesEfCannotExpress_AreInTheDatabase()
    {
        var counts = await _fx.QueryAsync(
            """
            SELECT 'policies',   count(*)::int FROM pg_policy
            UNION ALL SELECT 'triggers', count(*)::int FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid
                      JOIN pg_namespace n ON n.oid = c.relnamespace
                      WHERE n.nspname = 'public' AND NOT t.tgisinternal AND NOT c.relispartition
            UNION ALL SELECT 'exclusions', count(*)::int FROM pg_constraint con JOIN pg_class c ON c.oid = con.conrelid
                      JOIN pg_namespace n ON n.oid = c.relnamespace
                      WHERE n.nspname = 'public' AND con.contype = 'x'
            UNION ALL SELECT 'checks', count(*)::int FROM pg_constraint con JOIN pg_class c ON c.oid = con.conrelid
                      JOIN pg_namespace n ON n.oid = c.relnamespace
                      WHERE n.nspname = 'public' AND con.contype = 'c' AND NOT c.relispartition
            UNION ALL SELECT 'partitioned', count(*)::int FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                      WHERE n.nspname = 'public' AND c.relkind = 'p'
            """,
            r => (Kind: r.GetString(0), Count: r.GetInt32(1)));

        var by = counts.ToDictionary(c => c.Kind, c => c.Count, StringComparer.Ordinal);
        Assert.True(by["policies"] > 0, "RLS policies vanished from the baseline");
        Assert.True(by["triggers"] > 0, "the row-stamp / state-machine / frozen-row triggers vanished");
        Assert.True(by["exclusions"] > 0, "the gist no-overlap EXCLUDE constraints vanished");
        Assert.True(by["checks"] > 0, "the CHECK constraints vanished");
        Assert.Equal(5, by["partitioned"]);

        // And none of them reaches the model, because EF has no way to say them.
        using var db = CreateContext();
        Assert.Empty(db.Model.GetEntityTypes().SelectMany(e => e.GetCheckConstraints()));
    }

    // ───────────────────────────────────────── helpers ──────────────────────────────────────────

    private static string Describe(string storeType, bool nullable) =>
        storeType + (nullable ? " NULL" : " NOT NULL");

    private async Task<SortedDictionary<string, string>> ReadColumnsAsync(
        string relkinds, bool includePartitionChildren, bool typeOnly = false)
    {
        var rows = await _fx.QueryAsync(
            $"""
            SELECT c.relname, a.attname, format_type(a.atttypid, a.atttypmod), a.attnotnull
            FROM pg_attribute a
            JOIN pg_class c ON c.oid = a.attrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public' AND c.relkind IN ({relkinds})
              AND a.attnum > 0 AND NOT a.attisdropped
              {(includePartitionChildren ? string.Empty : "AND NOT c.relispartition")}
            """,
            r => (Table: r.GetString(0), Column: r.GetString(1), Type: r.GetString(2), NotNull: r.GetBoolean(3)));

        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row.Table.StartsWith("pg_stat_", StringComparison.Ordinal))
            {
                continue; // pg_stat_statements' own views, installed by 001
            }

            map[$"{row.Table}.{row.Column}"] = typeOnly ? row.Type : Describe(row.Type, !row.NotNull);
        }

        return map;
    }

    private async Task<SortedDictionary<string, string>> ReadConstraintsAsync(char contype)
    {
        var rows = await _fx.QueryAsync(
            $"""
            SELECT rel.relname, con.conname,
                   (SELECT string_agg(a.attname, ',' ORDER BY k.ord)
                      FROM unnest(con.conkey) WITH ORDINALITY AS k(attnum, ord)
                      JOIN pg_attribute a ON a.attrelid = con.conrelid AND a.attnum = k.attnum)
            FROM pg_constraint con
            JOIN pg_class rel ON rel.oid = con.conrelid
            JOIN pg_namespace n ON n.oid = rel.relnamespace
            WHERE n.nspname = 'public' AND con.contype = '{contype}' AND NOT rel.relispartition
            """,
            r => (Table: r.GetString(0), Name: r.GetString(1), Columns: r.GetString(2)));

        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            map[$"{row.Table}.{row.Name}"] = row.Columns;
        }

        return map;
    }

    private static void AssertSameMap(
        SortedDictionary<string, string> expected, SortedDictionary<string, string> actual, string what)
    {
        var missing = expected.Keys.Except(actual.Keys, StringComparer.Ordinal).ToList();
        var extra = actual.Keys.Except(expected.Keys, StringComparer.Ordinal).ToList();
        var different = expected.Keys.Intersect(actual.Keys, StringComparer.Ordinal)
            .Where(k => expected[k] != actual[k])
            .Select(k => $"{k}: baseline says '{expected[k]}', model says '{actual[k]}'")
            .ToList();

        var report = new List<string>();
        if (missing.Count > 0)
        {
            report.Add($"in the baseline and NOT in the model ({missing.Count} {what}): " + string.Join(", ", missing.Take(40)));
        }

        if (extra.Count > 0)
        {
            report.Add($"in the model and NOT in the baseline ({extra.Count} {what}): " + string.Join(", ", extra.Take(40)));
        }

        if (different.Count > 0)
        {
            report.Add($"different ({different.Count} {what}): " + string.Join("; ", different.Take(40)));
        }

        Assert.True(report.Count == 0, string.Join("\n", report));
    }
}
