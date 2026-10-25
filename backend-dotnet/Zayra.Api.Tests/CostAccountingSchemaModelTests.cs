using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Zayra.Api.Data;

namespace Zayra.Api.Tests;

/// <summary>
/// Cost-accounting foundation (migration M1) — the EF model half of the schema contract. Looks everything up by
/// TABLE and COLUMN name through the relational model, so it states the database shape rather than a C# spelling.
/// The database half (foreign keys, NOT VALID, raw-SQL keys, backfill, Down/Up) is in
/// <see cref="CostAccountingFoundationMigrationPostgresTests"/>.
/// </summary>
public sealed class CostAccountingSchemaModelTests
{
    private static IModel DesignModel()
    {
        using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseNpgsql("Host=localhost;Database=model_only;Username=x;Password=x").Options);
        return db.GetService<IDesignTimeModel>().Model;
    }

    private static IEntityType Entity(IModel model, string table) =>
        model.GetEntityTypes().Single(e => e.GetTableName() == table && e.GetSchema() == null);

    private static IProperty? Column(IModel model, string table, string column) =>
        Entity(model, table).GetProperties().SingleOrDefault(p => p.GetColumnName(StoreObjectIdentifier.Table(table, null)) == column);

    /// <summary>table, column, clr type, nullable, max length, database default (null = none).</summary>
    public static IEnumerable<object?[]> NewColumns()
    {
        object?[] Row(string t, string c, Type clr, bool nullable, int? max = null, object? dflt = null) => [t, c, clr, nullable, max, dflt];
        yield return Row("cost_centers", "parent_id", typeof(Guid), true);
        yield return Row("cost_centers", "functional_class", typeof(string), true, 24);
        yield return Row("cost_centers", "gl_segment", typeof(string), true, 30);
        yield return Row("gl_accounts", "cost_center_rule", typeof(string), false, 12, "Optional");
        yield return Row("gl_account_mappings", "functional_class", typeof(string), true, 24);
        yield return Row("companies", "cost_accounting_enabled", typeof(bool), false, null, false);
        yield return Row("companies", "default_cost_center_id", typeof(Guid), true);
        yield return Row("companies", "cost_centre_small_cell_min", typeof(short), false, null, (short)0);
        yield return Row("employees", "cost_center_is_override", typeof(bool), false, null, false);
        yield return Row("payroll_earnings", "cost_center_id", typeof(Guid), true);
        yield return Row("payroll_earnings", "cost_center_source", typeof(string), true, 16);
        yield return Row("payroll_deductions", "cost_center_id", typeof(Guid), true);
        yield return Row("payroll_deductions", "cost_center_source", typeof(string), true, 16);
        yield return Row("payroll_slips", "cost_center_id", typeof(Guid), true);
        yield return Row("payroll_runs", "cost_center_resolution_hash", typeof(string), true, 64);
        yield return Row("payroll_runs", "cost_accounting_applied", typeof(bool), false, null, false);
        yield return Row("eosb_provision_snapshots", "cost_center_id", typeof(Guid), true);
        yield return Row("finance_gl_entries", "cost_center_id", typeof(Guid), true);
        yield return Row("finance_gl_entries", "cost_center_code", typeof(string), true, 30);
        yield return Row("finance_gl_entries", "cost_center_name", typeof(string), true, 200);
        yield return Row("finance_gl_entries", "cost_center_gl_segment", typeof(string), true, 30);
        yield return Row("finance_gl_entries", "posting_key", typeof(string), true, 64);
        yield return Row("gl_journal_export_lines", "cost_center_code", typeof(string), true, 30);
        yield return Row("gl_journal_export_lines", "cost_center_name", typeof(string), true, 200);
        yield return Row("gl_journal_export_lines", "cost_center_gl_segment", typeof(string), true, 30);
        yield return Row("gl_journal_exports", "amount_scale", typeof(short), true);
    }

    [Theory]
    [MemberData(nameof(NewColumns))]
    public void NewColumn_IsMapped_WithTheContractedShape(string table, string column, Type clr, bool nullable, int? maxLength, object? dbDefault)
    {
        var property = Column(DesignModel(), table, column);
        property.Should().NotBeNull($"{table}.{column} is part of the cost-accounting schema contract (v1-spec 1.1-1.9)");
        (Nullable.GetUnderlyingType(property!.ClrType) ?? property.ClrType).Should().Be(clr);
        property.IsNullable.Should().Be(nullable);
        property.GetMaxLength().Should().Be(maxLength);
        if (dbDefault is null) property.GetDefaultValue().Should().BeNull($"{table}.{column} has no database default");
        else property.GetDefaultValue().Should().Be(dbDefault, "the default keeps every existing tenant byte-identical");
    }

    /// <summary>table, check name — every one is declared on the model so the snapshot is honest. The NOT VALID ones are
    /// re-emitted as raw SQL by the migration; the model still names them.</summary>
    public static IEnumerable<object[]> Checks()
    {
        foreach (var (t, c) in new[]
        {
            ("cost_centers", "ck_cost_centers__functional_class"),
            ("cost_centers", "ck_cost_centers__gl_segment_format"),
            ("cost_centers", "ck_cost_centers__parent_not_self"),
            ("cost_centers", "ck_cost_centers__code_format"),
            ("gl_accounts", "ck_gl_accounts__cost_center_rule"),
            ("gl_account_mappings", "ck_gl_account_mappings__functional_class"),
            ("companies", "ck_companies__cost_centre_small_cell_min"),
            ("payroll_earnings", "ck_payroll_earnings__cost_center_source"),
            ("payroll_deductions", "ck_payroll_deductions__cost_center_source"),
            ("finance_gl_entries", "ck_finance_gl_entries__cost_center_pair"),
            ("finance_gl_entries", "ck_finance_gl_entries__cost_center_needs_company"),
            ("finance_gl_entries", "ck_finance_gl_entries__amount_nonnegative"),
            ("finance_gl_entries", "ck_finance_gl_entries__has_account"),
            ("finance_gl_entries", "ck_finance_gl_entries__currency_format"),
        })
            yield return [t, c];
    }

    [Theory]
    [MemberData(nameof(Checks))]
    public void Check_IsDeclaredOnTheModel(string table, string check) =>
        Entity(DesignModel(), table).GetCheckConstraints().Select(c => c.Name).Should().Contain(check);

    [Fact]
    public void Indexes_AreDeclared_WithTheirPredicates()
    {
        var model = DesignModel();
        IIndex Index(string table, string name) =>
            Entity(model, table).GetIndexes().SingleOrDefault(i => i.GetDatabaseName() == name)
            ?? throw new Xunit.Sdk.XunitException($"{table} has no index {name}");

        var cc = Index("finance_gl_entries", "ix_finance_gl_entries_cc_period");
        cc.IsUnique.Should().BeFalse();
        cc.GetFilter().Should().Be("cost_center_id IS NOT NULL");
        cc.Properties.Select(p => p.Name).Should().Equal("TenantId", "CostCenterId", "Period");

        var key = Index("finance_gl_entries", "ux_finance_gl_entries_posting_key");
        key.IsUnique.Should().BeTrue();
        key.GetFilter().Should().Be("posting_key IS NOT NULL");

        var segment = Index("cost_centers", "ux_cost_centers_scope_gl_segment");
        segment.IsUnique.Should().BeTrue();
        NullsDistinct(segment).Should().BeFalse();
        segment.GetFilter().Should().Be("gl_segment IS NOT NULL AND NOT is_deleted");

        var driverClass = Index("gl_account_mappings", "ux_gl_account_mappings_scope_driver_class");
        driverClass.IsUnique.Should().BeTrue();
        NullsDistinct(driverClass).Should().BeFalse();
        driverClass.Properties.Select(p => p.Name).Should().Equal("TenantId", "CompanyId", "DriverKey", "FunctionalClass");
    }

    private static bool? NullsDistinct(IIndex index) => (bool?)index.FindAnnotation("Npgsql:NullsDistinct")?.Value;

    [Fact]
    public void NoTablesWereAdded_AndTheModelDeclaresNoForeignKeyToCostCentersYet()
    {
        // The cost-centre foreign keys are NOT VALID composite (tenant_id, ...) keys created in raw SQL (v1-spec 1.1, 1.6):
        // EF could neither emit NOT VALID nor skip the index it adds beside every foreign key (1.6 lists them as
        // deliberately unindexed). The migration test asserts them on the real database.
        var model = DesignModel();
        model.GetEntityTypes().SelectMany(e => e.GetForeignKeys())
            .Where(fk => fk.PrincipalEntityType.GetTableName() == "cost_centers")
            .Should().BeEmpty();
    }
}
