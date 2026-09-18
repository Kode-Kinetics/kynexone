using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Migrations;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// F2 — EXECUTES the AddPayComponentEffectiveDating migration on top of the real migration history (a fresh
/// Postgres, migrated to the last pre-F2 migration, then seeded in the pre-F2 shape with raw SQL), and pins
/// the frozen backfill VALUES to the compiled catalog so the two can never drift apart silently.
/// Own container (not the shared fixture): the backfill writes rows for EVERY tenant in the database.
/// </summary>
public sealed class PayComponentMigrationBackfillTests : IAsyncLifetime
{
    private const string PreF2Migration = "20260816200703_AddLiveSeparationUniqueIndex";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
    private string _cs = string.Empty;

    public async Task InitializeAsync() { await _container.StartAsync(); _cs = _container.GetConnectionString(); }
    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
    private ZayraDbContext CreateDb() => new(new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql(_cs).Options);

    [Fact]
    public async Task Migration_BackfillsEveryUnseededTenant_LeavesSeededAndCustomisedOnesAlone_AndReKeysTheUnique()
    {
        var empty = Guid.NewGuid();      // pre-F2 reality for most tenants: zero rows
        var seeded = Guid.NewGuid();     // already seeded via /finance/gl/seed-defaults
        var relabelled = Guid.NewGuid(); // holds ONLY a tenant-default TRANSPORT relabel (non-system)

        await using (var db = CreateDb())
        {
            await db.Database.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync(PreF2Migration);
            foreach (var t in new[] { empty, seeded, relabelled })
                db.Tenants.Add(new Tenant { Id = t, Name = $"T {t:N}", Slug = $"t-{t:N}" });
            await db.SaveChangesAsync();
            // Pre-F2 column list only (no effective_from / effective_to yet).
            const string cols = "id, tenant_id, company_id, code, name_en, name_ar, component_type, calc_method, structure_field, " +
                                "value, formula_expression, provider_key, is_taxable, gosi_subject, wps_included, eosb_included, " +
                                "gl_driver_key, emit_when_zero, is_family, display_order, is_system, is_statutory, is_active, " +
                                "is_deleted, created_at_utc";
            await db.Database.ExecuteSqlRawAsync($@"
INSERT INTO pay_components ({cols}) VALUES
 ('{Guid.NewGuid()}', '{seeded}', NULL, 'BASIC', 'Basic salary', 'x', 'Earning', 'StructureField', 'BasicSalary',
  NULL, NULL, NULL, TRUE, TRUE, TRUE, TRUE, 'EARN:BASIC', TRUE, FALSE, 30, TRUE, FALSE, TRUE, FALSE, now()),
 ('{Guid.NewGuid()}', '{relabelled}', NULL, 'TRANSPORT', 'Transport (custom)', 'x', 'Earning', 'StructureField', 'TransportAllowance',
  NULL, NULL, NULL, FALSE, FALSE, TRUE, FALSE, 'EARN:TRANSPORT', FALSE, FALSE, 50, FALSE, FALSE, TRUE, FALSE, now());");
        }

        await using (var db = CreateDb())
            await db.Database.MigrateAsync();

        await using (var db = CreateDb())
        {
            var rows = await db.PayComponents.IgnoreQueryFilters().AsNoTracking().ToListAsync();

            // Unseeded tenant: the full system catalog, as real rows, open-ended.
            var e = rows.Where(r => r.TenantId == empty).ToList();
            Assert.Equal(17, e.Count);
            Assert.All(e, r => Assert.True(r.IsSystem && r.IsActive && r.EffectiveFrom is null && r.EffectiveTo is null));
            var expected = PayComponentCatalog.SystemComponentSeeds(empty)
                .Select(c => (c.Code, c.ComponentType, c.CalcMethod, c.StructureField, c.ProviderKey, c.GlDriverKey, c.DisplayOrder,
                              c.EmitWhenZero, c.IsFamily, c.IsStatutory, c.IsTaxable, c.GosiSubject, c.WpsIncluded, c.EosbIncluded, c.NameEn, c.NameAr))
                .OrderBy(x => x.Code).ThenBy(x => x.ComponentType).ToList();
            var actual = e
                .Select(c => (c.Code, c.ComponentType, c.CalcMethod, c.StructureField, c.ProviderKey, c.GlDriverKey, c.DisplayOrder,
                              c.EmitWhenZero, c.IsFamily, c.IsStatutory, c.IsTaxable, c.GosiSubject, c.WpsIncluded, c.EosbIncluded, c.NameEn, c.NameAr))
                .OrderBy(x => x.Code).ThenBy(x => x.ComponentType).ToList();
            Assert.Equal(expected, actual);

            // Already-seeded tenant (it had a system row): untouched.
            Assert.Single(rows, r => r.TenantId == seeded);

            // Customised tenant: system catalog added AROUND its own TRANSPORT row, which is kept verbatim.
            var r3 = rows.Where(r => r.TenantId == relabelled).ToList();
            Assert.Equal(17, r3.Count);
            Assert.Equal("Transport (custom)", r3.Single(r => r.Code == "TRANSPORT").NameEn);
            Assert.False(r3.Single(r => r.Code == "TRANSPORT").IsSystem);

            // The UNIQUE now includes the version start and is partial on live rows.
            await using var cmd = db.Database.GetDbConnection().CreateCommand();
            await db.Database.OpenConnectionAsync();
            cmd.CommandText = "SELECT indexdef FROM pg_indexes WHERE indexname = 'ux_pay_components_scope_code_type_from'";
            var def = (string?)await cmd.ExecuteScalarAsync();
            Assert.NotNull(def);
            Assert.Contains("UNIQUE", def);
            Assert.Contains("effective_from", def);
            Assert.Contains("NULLS NOT DISTINCT", def);
            Assert.Contains("is_deleted = false", def);
            cmd.CommandText = "SELECT count(*) FROM pg_indexes WHERE indexname = 'ux_pay_components_scope_code_type'";
            Assert.Equal(0L, (long)(await cmd.ExecuteScalarAsync())!);

            // Behaviour: two dated versions coexist; two versions starting the same day do not.
            await db.Database.ExecuteSqlRawAsync($@"
INSERT INTO pay_components (id, tenant_id, company_id, code, name_en, name_ar, component_type, calc_method, value,
  is_taxable, gosi_subject, wps_included, eosb_included, gl_driver_key, emit_when_zero, is_family, display_order,
  is_system, is_statutory, is_active, is_deleted, created_at_utc, effective_from, effective_to) VALUES
 ('{Guid.NewGuid()}', '{empty}', NULL, 'UNION_DUES', 'U', 'U', 'Deduction', 'Fixed', 100, FALSE, FALSE, FALSE, FALSE,
  'DED:OTHER', FALSE, FALSE, 500, FALSE, FALSE, TRUE, FALSE, now(), '2026-06-01', '2026-07-31'),
 ('{Guid.NewGuid()}', '{empty}', NULL, 'UNION_DUES', 'U', 'U', 'Deduction', 'Fixed', 150, FALSE, FALSE, FALSE, FALSE,
  'DED:OTHER', FALSE, FALSE, 500, FALSE, FALSE, TRUE, FALSE, now(), '2026-08-01', NULL);");
            var dup = await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync($@"
INSERT INTO pay_components (id, tenant_id, company_id, code, name_en, name_ar, component_type, calc_method, value,
  is_taxable, gosi_subject, wps_included, eosb_included, gl_driver_key, emit_when_zero, is_family, display_order,
  is_system, is_statutory, is_active, is_deleted, created_at_utc, effective_from) VALUES
 ('{Guid.NewGuid()}', '{empty}', NULL, 'UNION_DUES', 'U', 'U', 'Deduction', 'Fixed', 175, FALSE, FALSE, FALSE, FALSE,
  'DED:OTHER', FALSE, FALSE, 500, FALSE, FALSE, TRUE, FALSE, now(), '2026-08-01');"));
            Assert.Contains("ux_pay_components_scope_code_type_from", dup.ToString());
        }
    }

    /// <summary>The migration's frozen VALUES must stay exactly the compiled catalog (same rows, same order).</summary>
    [Fact]
    public void FrozenBackfillValues_MatchTheCompiledCatalog()
    {
        static string Q(string? s) => s is null ? "NULL" : "'" + s.Replace("'", "''") + "'";
        static string B(bool b) => b ? "TRUE" : "FALSE";
        var expected = PayComponentCatalog.SystemComponentSeeds(Guid.Empty)
            .Select(c => $"({Q(c.Code)}, {Q(c.NameEn)}, {Q(c.NameAr)}, {Q(c.ComponentType)}, {Q(c.CalcMethod)}, " +
                         $"{Q(c.StructureField)}, {Q(c.ProviderKey)}, {Q(c.GlDriverKey)}, {c.DisplayOrder}, {B(c.EmitWhenZero)}, " +
                         $"{B(c.IsFamily)}, {B(c.IsStatutory)}, {B(c.IsTaxable)}, {B(c.GosiSubject)}, {B(c.WpsIncluded)}, {B(c.EosbIncluded)})")
            .ToList();
        var actual = Regex.Matches(AddPayComponentEffectiveDating.BackfillValues, @"^\s*(\(.*\)),?\s*$", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(expected, actual);
    }
}
