using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Boot;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Phase 1B boot assertion (K12): a CompanyId column that isn't wired into ICompanyScoped
/// silently escapes the company query filter (how LeavePolicy.CompanyId sat unenforced).
/// CI is strict; production logs until ZAYRA_COMPANY_SCOPE_ASSERT=strict is set.
/// </summary>
public class CompanyScopeBootAssertionTests
{
    [Fact]
    public void RealModel_HasNoCompanyScopeViolations()
    {
        using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        CompanyScopeBootAssertion.FindViolations(db).Should().BeEmpty(
            "every entity with CompanyId must implement ICompanyScoped/Operational or carry a justified allow-list entry");
    }

    [Fact]
    public void EntityWithCompanyId_WithoutInterface_IsDetected_AndStrictModeThrows()
    {
        using var db = new RogueDbContext(new DbContextOptionsBuilder<RogueDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        var violations = CompanyScopeBootAssertion.FindViolations(db);
        violations.Should().ContainSingle(v => v.Contains("RogueOperationalRecord"),
            "a CompanyId column without ICompanyScoped must be flagged (K12)");

        var strict = () => CompanyScopeBootAssertion.Assert(db, strict: true);
        strict.Should().Throw<InvalidOperationException>()
            .WithMessage("*RogueOperationalRecord*");

        var lenient = () => CompanyScopeBootAssertion.Assert(db, strict: false);
        lenient.Should().NotThrow("production stays up and logs until the strict env var is flipped");
    }

    [Fact]
    public void StrictModeResolution_MatchesRolloutPolicy()
    {
        var saved = Environment.GetEnvironmentVariable(CompanyScopeBootAssertion.StrictEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(CompanyScopeBootAssertion.StrictEnvVar, null);
            CompanyScopeBootAssertion.ResolveStrictMode(isProduction: false).Should().BeTrue("dev/test/CI are always strict");
            CompanyScopeBootAssertion.ResolveStrictMode(isProduction: true).Should().BeFalse("production defaults to log-only");

            Environment.SetEnvironmentVariable(CompanyScopeBootAssertion.StrictEnvVar, "strict");
            CompanyScopeBootAssertion.ResolveStrictMode(isProduction: true).Should().BeTrue("production opts into hard-fail via env var");
        }
        finally
        {
            Environment.SetEnvironmentVariable(CompanyScopeBootAssertion.StrictEnvVar, saved);
        }
    }

    // ── A deliberately mis-declared entity: CompanyId, no ICompanyScoped ────────

    private sealed class RogueOperationalRecord
    {
        public Guid Id { get; set; }
        public Guid? CompanyId { get; set; }
        public string Payload { get; set; } = string.Empty;
    }

    private sealed class RogueDbContext : DbContext
    {
        public RogueDbContext(DbContextOptions<RogueDbContext> options) : base(options) { }
        public DbSet<RogueOperationalRecord> Rogues => Set<RogueOperationalRecord>();
    }
}
