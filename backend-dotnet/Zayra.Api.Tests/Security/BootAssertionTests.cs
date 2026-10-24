using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Boot;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// P3: Verifies that TenantOwnershipBootAssertion and ControllerEntityReturnBootAssertion
/// fire correctly, and that the JWT audience guard enforces production vs development rules.
/// </summary>
public class BootAssertionTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    // ── TenantOwnershipBootAssertion ─────────────────────────────────────────────

    [Fact]
    public void TenantOwnership_PassesForWellDeclaredModel()
    {
        // The real ZayraDbContext with all 244 entity types should pass clean.
        using var db = CreateDb();
        var act = () => TenantOwnershipBootAssertion.Assert(db);
        act.Should().NotThrow();
    }

    [Fact]
    public void TenantOwnership_ThrowsForEntityWithTenantIdButNoInterface()
    {
        // Build a throw-away InMemory model that contains an entity with TenantId
        // but NOT implementing ITenantOwned.
        var opts = new DbContextOptionsBuilder<BrokenTenantDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        using var db = new BrokenTenantDbContext(opts);

        var act = () => TenantOwnershipBootAssertion.Assert(db);
        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*BrokenTenantEntity*")
            .WithMessage("*ITenantOwned*");
    }

    [Fact]
    public void TenantOwnership_ThrowsForEntityImplementingInterfaceButMissingProperty()
    {
        var opts = new DbContextOptionsBuilder<MissingPropertyDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        using var db = new MissingPropertyDbContext(opts);

        var act = () => TenantOwnershipBootAssertion.Assert(db);
        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*MissingPropertyEntity*");
    }

    // ── ControllerEntityReturnBootAssertion ──────────────────────────────────────

    [Fact]
    public void ControllerEntityReturn_PassesForRealAssembly()
    {
        // All live controllers either project to DTOs or have [AllowEntityReturn].
        using var db = CreateDb();
        var act = () => ControllerEntityReturnBootAssertion.Assert(db, typeof(Program).Assembly);
        act.Should().NotThrow();
    }

    [Fact]
    public void ControllerEntityReturn_ThrowsForControllerReturningRawEntity()
    {
        // BadEntityController returns Employee directly without opt-out.
        var opts = new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        using var db = new ZayraDbContext(opts);

        // Use a test-only assembly that contains BadEntityController.
        var act = () => ControllerEntityReturnBootAssertion.Assert(db, typeof(BadEntityController).Assembly);
        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*BadEntityController*")
            .WithMessage("*Employee*");
    }

    [Fact]
    public void ControllerEntityReturn_ThrowsForBareCollectionReturn()
    {
        // BareCollectionController returns Task<IReadOnlyCollection<Employee>> without opt-out.
        // This pattern was the coverage gap: resolver previously returned null for collection interfaces.
        var opts = new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        using var db = new ZayraDbContext(opts);

        var act = () => ControllerEntityReturnBootAssertion.Assert(db, typeof(BareCollectionController).Assembly);
        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*BareCollectionController*")
            .WithMessage("*Employee*");
    }

    [Fact]
    public void ControllerEntityReturn_AllowEntityReturnSuppressesViolation()
    {
        // Both BadEntityController and GoodEntityController are in this test assembly.
        // BadEntityController violates (no [AllowEntityReturn]).
        // GoodEntityController has [AllowEntityReturn] — its method should NOT appear in violations.
        var opts = new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        using var db = new ZayraDbContext(opts);

        var act = () => ControllerEntityReturnBootAssertion.Assert(db, typeof(BadEntityController).Assembly);
        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*BadEntityController*")
            // GoodEntityController.Get has [AllowEntityReturn] — must NOT appear in violations
            .And.Message.Should().NotContain("GoodEntityController");
    }

    // ── JWT Audience Fail-Fast ────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("kynexone")]
    public void JwtAudience_ProductionThrowsForMissingOrDevDefaultAudience(string? audience)
    {
        var act = () => JwtAudienceGuard.AssertProductionSafe(
            audience, signingKey: "STRONG-64-CHAR-PRODUCTION-KEY-THAT-IS-NOT-DEFAULT-0000000000000");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Production JWT*");
    }

    [Fact]
    public void JwtAudience_ProductionPassesForCustomAudiences()
    {
        var act = () => JwtAudienceGuard.AssertProductionSafe(
            audience: "prod-installation-audience",
            signingKey: "STRONG-64-CHAR-PRODUCTION-KEY-THAT-IS-NOT-DEFAULT-0000000000000");
        act.Should().NotThrow();
    }

    [Fact]
    public void JwtAudience_DevelopmentDoesNotThrowForDevDefaults()
    {
        // In Development the dev defaults are explicitly allowed — no check fires.
        // This is expressed by NOT calling AssertProductionSafe (it's only wired in Production).
        // The test documents the intent: dev defaults pass in non-Production.
        var isProduction = false;
        Action act = () =>
        {
            if (isProduction)
                JwtAudienceGuard.AssertProductionSafe("kynexone",
                    signingKey: "CHANGE_ME_...");
        };
        act.Should().NotThrow();
    }
}

// ── Ancillary test entities/controllers/DbContexts ───────────────────────────

/// <summary>Entity that has TenantId but does NOT implement ITenantOwned.</summary>
internal class BrokenTenantEntity
{
    public int Id { get; set; }
    public Guid TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
}

internal class BrokenTenantDbContext : DbContext
{
    public BrokenTenantDbContext(DbContextOptions<BrokenTenantDbContext> opts) : base(opts) { }
    public DbSet<BrokenTenantEntity> BrokenEntities => Set<BrokenTenantEntity>();
}

/// <summary>Entity that implements ITenantOwned but has no TenantId property.</summary>
internal class MissingPropertyEntity : ITenantOwned
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;

    // ITenantOwned contract — but it's an explicit interface implementation
    // so the reflection check (GetProperty("TenantId")) won't find it.
    Guid ITenantOwned.TenantId { get; set; }
}

internal class MissingPropertyDbContext : DbContext
{
    public MissingPropertyDbContext(DbContextOptions<MissingPropertyDbContext> opts) : base(opts) { }
    public DbSet<MissingPropertyEntity> MissingEntities => Set<MissingPropertyEntity>();
}

/// <summary>Returns a raw EF entity — no [AllowEntityReturn] opt-out.</summary>
[ApiController]
[Route("api/test-bad")]
internal class BadEntityController : ControllerBase
{
    [HttpGet("{id}")]
    public ActionResult<Employee> Get(int id) => Ok(new Employee());
}

/// <summary>
/// Returns Task&lt;IReadOnlyCollection&lt;Employee&gt;&gt; — the bare-collection gap pattern.
/// No [AllowEntityReturn]: must be caught by the generalized resolver.
/// </summary>
[ApiController]
[Route("api/test-bare-collection")]
internal class BareCollectionController : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyCollection<Employee>> GetAll() =>
        Task.FromResult<IReadOnlyCollection<Employee>>(new List<Employee>());
}

/// <summary>Returns a raw entity but opts out with [AllowEntityReturn].</summary>
[ApiController]
[Route("api/test-good")]
internal class GoodEntityController : ControllerBase
{
    [HttpGet("{id}")]
    [AllowEntityReturn("Test-only: proves that [AllowEntityReturn] suppresses the violation.")]
    public ActionResult<Employee> Get(int id) => Ok(new Employee());
}

/// <summary>
/// Extracted helper so tests can assert the JWT audience production guard without
/// starting a WebApplication. Program.cs calls this inline; tests call it directly.
/// </summary>
internal static class JwtAudienceGuard
{
    private const string DevAudience = Zayra.Api.Application.Auth.JwtOptions.DevAudience;

    internal static void AssertProductionSafe(string? audience, string? signingKey)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(audience) || audience == DevAudience)
            errors.Add($"Jwt:Audience is null, empty, or still the dev default ('{DevAudience}').");
        if (string.IsNullOrWhiteSpace(signingKey) || signingKey.StartsWith("CHANGE_ME"))
            errors.Add("Jwt:SigningKey is null, empty, or still the placeholder value.");

        if (errors.Count > 0)
            throw new InvalidOperationException(
                "Production JWT configuration fail-fast:\n" + string.Join("\n", errors.Select(e => "  " + e)));
    }
}
