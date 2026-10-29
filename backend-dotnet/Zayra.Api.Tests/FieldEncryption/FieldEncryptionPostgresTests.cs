using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.FieldEncryption;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.FieldEncryption;

/// <summary>
/// The database half of field encryption on real Postgres (CI integration lane): the migration, the envelope-shape and pairing
/// CHECKs, the one-active-key unique index, and the whole DualWrite -> backfill -> verify loop through Npgsql and the real
/// <see cref="EfFieldKeyStore"/> (own scope per call, as in production).
/// </summary>
[Trait("Category", "Integration")]
public sealed class FieldEncryptionPostgresTests : IAsyncLifetime
{
    private PostgreSqlContainer _pg = null!;
    private string Cs => _pg.GetConnectionString();

    public async Task InitializeAsync()
    {
        _pg = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await _pg.StartAsync();
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql(Cs).Options);
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => _pg.DisposeAsync().AsTask();

    private ServiceProvider Services(FeKit kit)
    {
        var s = new ServiceCollection();
        s.AddSingleton(kit.Options);
        s.AddSingleton<IKeyEncryptionKeyProvider>(kit.Kek);
        s.AddDbContext<ZayraDbContext>(o => o.UseNpgsql(Cs), ServiceLifetime.Scoped);
        s.AddSingleton<IFieldKeyStore, EfFieldKeyStore>();
        s.AddSingleton<FieldKeyService>();
        s.AddSingleton<FieldEncryptionSaveInterceptor>();
        s.AddSingleton<FieldEncryptionQueryGuard>();
        s.AddScoped<ZayraDbContext>(sp => new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql(Cs)
            .AddInterceptors(sp.GetRequiredService<FieldEncryptionSaveInterceptor>(), sp.GetRequiredService<FieldEncryptionQueryGuard>()).Options));
        return s.BuildServiceProvider();
    }

    private async Task<Guid> SeedTenantAsync()
    {
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql(Cs).Options);
        return await PostgresFixture.SeedMinimalTenant(db);
    }

    private async Task<(bool ok, string? error)> TryExec(string sql, params (string, object?)[] p)
    {
        await using var c = new NpgsqlConnection(Cs); await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        foreach (var (k, v) in p) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        try { await cmd.ExecuteNonQueryAsync(); return (true, null); } catch (PostgresException ex) { return (false, ex.ConstraintName ?? ex.MessageText); }
    }

    [Fact]
    public async Task Migration_CreatesTheKeyTable_TheShadowColumns_AndTheChecks()
    {
        await using var c = new NpgsqlConnection(Cs); await c.OpenAsync();
        async Task<long> Scalar(string sql) { await using var cmd = new NpgsqlCommand(sql, c); return (long)(await cmd.ExecuteScalarAsync())!; }
        (await Scalar("SELECT count(*) FROM information_schema.tables WHERE table_name = 'field_encryption_keys'")).Should().Be(1);
        foreach (var f in EncryptedFieldCatalog.All)
        {
            await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql(Cs).Options);
            var table = db.Model.FindEntityType(f.Entity)!.GetTableName();
            (await Scalar($"SELECT count(*) FROM information_schema.columns WHERE table_name='{table}' AND column_name='{f.CipherColumn}' AND data_type='bytea'")).Should().Be(1, $"{table}.{f.CipherColumn}");
            if (f.BlindIndexed)
                (await Scalar($"SELECT count(*) FROM information_schema.columns WHERE table_name='{table}' AND column_name='{f.BlindIndexColumn}' AND data_type='bytea'")).Should().Be(1);
        }
    }

    [Fact]
    public async Task EnvelopeShapeCheck_RejectsJunk_AndAcceptsARealEnvelope()
    {
        var t = await SeedTenantAsync();
        var kit = new FeKit(FieldEncryptionMode.DualWrite);
        var junk = await TryExec("UPDATE employees SET iqama_number_enc = @b WHERE tenant_id = @t", ("b", new byte[] { 1, 1, 2 }), ("t", t));
        // No employee rows match yet, so insert one through EF first, then attack it.
        await using var sp = Services(kit);
        await using (var scope = sp.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
            db.Employees.Add(FeKit.NewEmployee(t, "PG1", iqama: "2123456789")); await db.SaveChangesAsync();
        }
        (await TryExec("UPDATE employees SET iqama_number_enc = @b WHERE tenant_id = @t", ("b", new byte[] { 1, 1, 2 }), ("t", t))).ok.Should().BeFalse("too short to be an envelope");
        (await TryExec("UPDATE employees SET iqama_number_enc = @b WHERE tenant_id = @t", ("b", new byte[40]), ("t", t))).ok.Should().BeFalse("format byte must be 1");
        (await TryExec("UPDATE employees SET iqama_number_bidx = @b WHERE tenant_id = @t", ("b", new byte[7]), ("t", t))).ok.Should().BeFalse("a blind index is exactly 32 bytes");
        junk.ok.Should().BeTrue("zero rows matched, so nothing was checked");
    }

    [Fact]
    public async Task OnlyOneActiveKeyPerTenant_AndRevokedRowsCannotCarryMaterial()
    {
        var t = await SeedTenantAsync();
        var id = () => Guid.NewGuid();
        const string ins = "INSERT INTO field_encryption_keys (id, tenant_id, key_version, status, wrapped_dek, wrapped_blind_index_root, kek_version, created_at_utc, row_version, active_at_revoke) VALUES (@id,@t,@v,@s,@d,@r,1,now(),0,false)";
        (await TryExec(ins, ("id", id()), ("t", t), ("v", 1), ("s", "Active"), ("d", new byte[40]), ("r", new byte[40]))).ok.Should().BeTrue();
        (await TryExec(ins, ("id", id()), ("t", t), ("v", 2), ("s", "Active"), ("d", new byte[40]), ("r", new byte[40]))).ok.Should().BeFalse("ux_field_encryption_keys_one_active");
        (await TryExec(ins, ("id", id()), ("t", t), ("v", 1), ("s", "Retired"), ("d", new byte[40]), ("r", new byte[40]))).ok.Should().BeFalse("version is unique per tenant");
        (await TryExec(ins, ("id", id()), ("t", t), ("v", 3), ("s", "Revoked"), ("d", new byte[40]), ("r", new byte[40]))).ok.Should().BeFalse("revoked rows must hold no material");
        (await TryExec(ins, ("id", id()), ("t", t), ("v", 4), ("s", "Bogus"), ("d", new byte[40]), ("r", new byte[40]))).ok.Should().BeFalse();
        (await TryExec(ins, ("id", id()), ("t", t), ("v", 5), ("s", "Revoked"), ("d", null), ("r", null))).ok.Should().BeTrue();
    }

    [Fact]
    public async Task DualWrite_Backfill_Verify_RoundTripOnRealPostgres_WithRealKeyStore()
    {
        var t = await SeedTenantAsync();
        // Legacy rows: plaintext only.
        await using (var legacy = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql(Cs).Options))
        {
            for (var i = 0; i < 3; i++) legacy.Employees.Add(FeKit.NewEmployee(t, $"PGL{i}", iqama: $"21000000{i:D2}", iban: "SA0380000000608010167519"));
            await legacy.SaveChangesAsync();
        }
        var kit = new FeKit(FieldEncryptionMode.DualWrite);
        await using var sp = Services(kit);
        await using var scope = sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var job = new FieldEncryptionBackfill(db, sp.GetRequiredService<FieldKeyService>(), kit.Options);

        // A fresh write is born encrypted.
        db.Employees.Add(FeKit.NewEmployee(t, "PGN", iqama: "2999999999")); await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        (await job.EncryptMissingAsync(t, 1000, default)).Complete.Should().BeTrue();
        (await job.ProgressAsync(t, default)).Should().OnlyContain(p => p.Remaining == 0);
        var report = await job.VerifyAsync(t, 10_000, null, default);
        report.Complete.Should().BeTrue();
        report.MismatchTotal.Should().Be(0);
        report.Ok.Should().BeGreaterThan(6);

        // Blind-index lookup works on the real index.
        var ring = await sp.GetRequiredService<FieldKeyService>().GetRingAsync(t, false, default);
        (await db.Employees.WhereBlindIndex(db, ring, e => e.IqamaNumber, "2999999999").Select(e => e.EmployeeCode).ToListAsync()).Should().Equal("PGN");

        // The database never holds the plaintext inside the key table or the ciphertext.
        await using var c = new NpgsqlConnection(Cs); await c.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM employees WHERE tenant_id=@t AND position(convert_to('2999999999','UTF8') in iqama_number_enc) > 0", c);
        cmd.Parameters.AddWithValue("t", t);
        ((long)(await cmd.ExecuteScalarAsync())!).Should().Be(0, "ciphertext must not contain the plaintext");
    }
}
