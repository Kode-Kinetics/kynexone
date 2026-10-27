using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Operations;

namespace Zayra.Api.Tests;

/// <summary>
/// P1-1 follow-up: the ledger triggers only hold against a role that cannot switch them off. The app reports, without refusing to start and
/// without touching <c>status</c>, whether its own DB role is one of those roles: <c>ledgerProtection</c> is "degraded" when it is, else "enforced".
/// </summary>
[Collection("LedgerDb")]
[Trait("Category", "Integration")]
public sealed class LedgerProtectionPostgresTests
{
    private const string Role = "kx_ledger_runtime_probe";
    private readonly LedgerDbFixture _db;
    public LedgerProtectionPostgresTests(LedgerDbFixture db) => _db = db;

    private static ZayraDbContext Open(string connectionString) =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql(connectionString).Options);

    private async Task<string> NonOwnerConnection()
    {
        await _db.Exec($"""
            DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{Role}') THEN CREATE ROLE {Role} LOGIN PASSWORD 'pw'; END IF; END $$;
            GRANT USAGE ON SCHEMA public TO {Role};
            GRANT SELECT, INSERT, UPDATE ON ALL TABLES IN SCHEMA public TO {Role};
            """);
        return new NpgsqlConnectionStringBuilder(_db.ConnectionString) { Username = Role, Password = "pw", Pooling = false }.ConnectionString;
    }

    [Fact]
    public async Task AnApplicationRoleThatOwnsTheLedger_IsReportedDegraded()
    {
        await using var db = Open(_db.ConnectionString);
        (await LedgerProtection.ProbeAsync(db, default)).Should().Be("degraded");
    }

    [Fact]
    public async Task ANonOwnerApplicationRole_IsReportedEnforced()
    {
        await using var db = Open(await NonOwnerConnection());
        (await LedgerProtection.ProbeAsync(db, default)).Should().Be("enforced");
    }

    [Fact]
    public async Task WithoutTheBypassFunction_TheProbeSaysEnforced_AndDoesNotThrow()
    {
        await _db.Exec("ALTER FUNCTION finance_ledger_trigger_bypass_roles() RENAME TO finance_ledger_trigger_bypass_roles_hidden");
        try
        {
            await using var db = Open(_db.ConnectionString);
            (await LedgerProtection.ProbeAsync(db, default)).Should().Be("enforced");
        }
        finally { await _db.Exec("ALTER FUNCTION finance_ledger_trigger_bypass_roles_hidden() RENAME TO finance_ledger_trigger_bypass_roles"); }
    }
}

public class LedgerProtectionReadinessBodyTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("degraded")]
    [InlineData("enforced")]
    public void ThePublicReadinessBody_CarriesLedgerProtection_WithoutChangingStatus(string value)
    {
        var evidence = new ReadinessEvidence("ready", DateTime.UtcNow, null!, 0, 0, 0, null!, null, value);
        var body = JsonSerializer.SerializeToElement(PublicReadiness.From(evidence), Web);
        body.GetProperty("ledgerProtection").GetString().Should().Be(value);
        body.GetProperty("status").GetString().Should().Be("ready");
    }

    [Fact]
    public async Task TheCriticalMessage_IsLoggedOnce_WhenDegraded()
    {
        LedgerProtection.ResetForTests();
        var log = new CapturingLogger();
        for (var i = 0; i < 3; i++) await LedgerProtection.ReportAsync(() => Task.FromResult("degraded"), log, force: true);
        log.Critical.Should().HaveCount(1);
        log.Critical.Single().Should().Contain("finance_ledger_trigger_bypass_roles");
    }

    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger
    {
        public List<string> Critical { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { if (logLevel == Microsoft.Extensions.Logging.LogLevel.Critical) Critical.Add(formatter(state, exception)); }
    }
}
