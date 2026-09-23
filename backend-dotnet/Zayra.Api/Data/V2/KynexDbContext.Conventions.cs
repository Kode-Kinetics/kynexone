using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Zayra.Api.Data.V2;

/// <summary>
/// The conventions that apply to the whole V2 model, stated once here rather than repeated
/// 78 times in the generated configuration. Everything in <c>KynexDbContext.cs</c> and
/// <c>Entities/</c> is derived from <c>Db/baseline/*.sql</c>; everything in this file is a
/// decision, and each one names the paragraph of TARGET_SCHEMA.md it implements.
/// </summary>
public partial class KynexDbContext
{
    // ────────────────────────────── Tenancy: RLS, not query filters ─────────────────────────────
    //
    // THERE ARE NO GLOBAL QUERY FILTERS IN THIS MODEL, AND NONE MAY BE ADDED.
    //
    // The old model (Data/ZayraDbContext.cs) carries HasQueryFilter on every tenant entity, and a
    // filter is only ever as good as the code that did not write .IgnoreQueryFilters(). In the V2
    // schema isolation is row-level security in PostgreSQL (TARGET_SCHEMA.md §19.2): every tenant
    // relation has FORCE ROW LEVEL SECURITY and a p_tenant policy of
    //   USING (tenant_id = app.current_tenant()) WITH CHECK (tenant_id = app.current_tenant())
    // and a kynex_app session with app.tenant_id unset sees zero rows everywhere — proved by
    // Db/baseline/tests/verify_rls.sh, not asserted here.
    //
    // Consequences a caller must know:
    //   * The connection must run as kynex_app / kynex_job / kynex_platform, never as the owner or
    //     the migrator, both of which are BYPASSRLS.
    //   * app.tenant_id must be SET on the connection before the first query of a request. Failing
    //     to set it is FAIL-CLOSED: empty results, not a leak.
    //   * A WHERE tenant_id = @x in application code is still written for the INDEX (§19.4 rule 1),
    //     not for the isolation. Dropping it costs a sequential scan, not a breach.
    //   * Adding HasQueryFilter here would silently double-filter and, worse, would re-teach the
    //     codebase that isolation is an ORM concern. It is not, any more.

    // ───────────────────────────────── Time: one instant type ───────────────────────────────────
    //
    // Every instant column in the baseline is `timestamptz`, and Npgsql 6+ maps `timestamptz` to a
    // DateTime whose Kind is Utc — it REJECTS a Local or Unspecified DateTime on write. The whole
    // model therefore reads and writes UTC, converted once, here, so no call site has to remember.
    // A value that arrives Unspecified is treated as already-UTC rather than shifted by the
    // server's zone, because an Unspecified DateTime in this codebase always came from a UTC
    // source (a serialised payload, a raw read) and shifting it would move payroll periods.
    //
    // LOCAL DATES ARE A DIFFERENT TYPE AND ARE NOT AFFECTED. work_date, effective_from/to, the
    // payroll (year, month) period and every other `date` column map to DateOnly precisely so that
    // "the business day in the company's timezone" can never be re-derived from a UTC instant at
    // read time (§13.4). `time` columns map to TimeOnly for the same reason.
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);
        configurationBuilder.Properties<DateTime>().HaveConversion(typeof(UtcDateTimeConverter));
        configurationBuilder.Properties<DateTime?>().HaveConversion(typeof(NullableUtcDateTimeConverter));

        // MONEY IS NOT CONFIGURED HERE, DELIBERATELY. Money is numeric(18,2) in the company's
        // currency and nothing else (§Conventions, §13.2) and rates are numeric(9,6), but both
        // precisions are already carried per column in the generated configuration, taken from
        // the column's own typmod. A blanket HavePrecision(18, 2) here would look like belt and
        // braces and would in fact be a second, divergent source of truth: it would silently
        // re-type statutory_rule_bands.lower_bound / .upper_bound, which the baseline declares as
        // unconstrained `numeric` because a band edge can be a service-year count, a sick-day
        // count or a wage. The guarantee that every money column really is numeric(18,2) is
        // KynexModelMatchesBaselineTests, which compares precision and scale column by column
        // against the database, not a convention that would make the model agree with itself.
    }

    public sealed class UtcDateTimeConverter : ValueConverter<DateTime, DateTime>
    {
        public UtcDateTimeConverter()
            : base(
                v => v.Kind == DateTimeKind.Utc ? v : DateTime.SpecifyKind(v, DateTimeKind.Utc),
                v => DateTime.SpecifyKind(v, DateTimeKind.Utc))
        {
        }
    }

    public sealed class NullableUtcDateTimeConverter : ValueConverter<DateTime?, DateTime?>
    {
        public NullableUtcDateTimeConverter()
            : base(
                v => !v.HasValue ? v : (v.Value.Kind == DateTimeKind.Utc ? v : DateTime.SpecifyKind(v.Value, DateTimeKind.Utc)),
                v => !v.HasValue ? v : DateTime.SpecifyKind(v.Value, DateTimeKind.Utc))
        {
        }
    }

    // ─────────────────────────── Concurrency: xmin, never updated_at ────────────────────────────
    //
    // §19.5: "UseXminAsConcurrencyToken() globally — no column, no migration". updated_at is NOT a
    // concurrency token: it is written by the trg_row_stamp BEFORE trigger inside the database, so
    // EF never sees the value it would have to compare, and two HR users editing one employee in
    // the same second would both win. xmin is the row's real version, maintained by PostgreSQL.
    //
    // Exempt, per §19.5, and each for a reason rather than by taste:
    //   * append-only tables — leave_ledger and the three audit tables never UPDATE, so a token
    //     would cost a column in every read and guard nothing;
    //   * tenant_settings — it carries its own per-section version in section_versions jsonb,
    //     because two admins editing different sections of one row must both succeed;
    //   * the immutable / frozen-artefact tables of §Conventions, whose UPDATE is refused by a
    //     trigger, not by a token.
    private static readonly HashSet<string> XminExempt = new(StringComparer.Ordinal)
    {
        "leave_ledger",
        "audit_logs",
        "payroll_audit_logs",
        "retention_purge_audits",
        "tenant_settings",
        "attendance_punches",
        "payroll_slip_lines",
        "wps_lines",
        "final_settlement_lines",
        "gl_journal_lines",
        "nitaqat_snapshots",
        "background_job_items",
        "data_protection_keys",
    };

    /// <summary>
    /// Whether <paramref name="table"/> is one of the §19.5 exemptions from the xmin concurrency
    /// token. Public so the verification test asserts against this list rather than a second copy
    /// of it.
    /// </summary>
    public static bool IsXminExempt(string table) => XminExempt.Contains(table);

    /// <summary>
    /// Applied by the generated <see cref="OnModelCreating"/> after every table has been
    /// configured. Split out so re-deriving the generated half from the baseline SQL cannot
    /// silently drop it.
    /// </summary>
    partial void OnModelCreatingPartial(ModelBuilder modelBuilder)
    {
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            var table = entity.GetTableName();

            // The two views (v_leave_balances, v_employee_current) are keyless projections; a
            // concurrency token on a view is meaningless and Npgsql cannot select xmin from one.
            if (table is null || entity.FindPrimaryKey() is null || XminExempt.Contains(table))
            {
                continue;
            }

            modelBuilder.Entity(entity.ClrType).UseXminAsConcurrencyToken();
        }
    }
}
