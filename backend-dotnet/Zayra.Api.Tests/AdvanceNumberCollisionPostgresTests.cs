using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Audit P3: the advance number is allocated MAX+1 under the tenant's advance-create lock, so two requests never collide. A writer
/// outside that lock (an opening-balance import landing at the same moment) can still take the number first; the unique index
/// then refuses the insert, and the request answers a clean 409 with a bilingual reason instead of an unhandled 500, leaving
/// nothing half-written. Real PostgreSQL, real unique index.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class AdvanceNumberCollisionPostgresTests
{
    private readonly PostgresFixture _fx;
    public AdvanceNumberCollisionPostgresTests(PostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task ANumberTakenByAWriterOutsideTheLock_IsAClean409_AndNothingIsSaved()
    {
        Guid tenantId;
        await using (var seed = _fx.CreateDb()) tenantId = await PostgresFixture.SeedMinimalTenant(seed);
        var (publicId, employeeId) = await SeedEmployeeAsync(tenantId);
        var thief = new NumberThief(_fx.ConnectionString, tenantId);

        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseNpgsql(_fx.ConnectionString, PostgresFixture.ProductionProviderOptions)
            .AddInterceptors(Zayra.Api.Infrastructure.Data.AdvisoryXactLockGuardInterceptor.Instance, thief)
            .Options);
        var result = await Controller(db, tenantId).Create(
            new CreateAdvanceRequest(publicId, "Collision", 1_000m, "Installments", 2, null, "rent", employeeId), CancellationToken.None);

        thief.Fired.Should().BeTrue("the import took the number between the allocation and the insert");
        var conflict = result.Should().BeOfType<ConflictObjectResult>().Subject;
        var body = System.Text.Json.JsonSerializer.Serialize(conflict.Value);
        body.Should().Contain("advance_number_conflict").And.Contain("messageAr");
        await using var verify = _fx.CreateDb();
        (await verify.SalaryAdvances.IgnoreQueryFilters().CountAsync(x => x.TenantId == tenantId))
            .Should().Be(1, "only the imported row exists; the refused request saved nothing");
        (await verify.AdvanceAuditLogs.IgnoreQueryFilters().CountAsync(x => x.TenantId == tenantId)).Should().Be(0);

        // Retrying allocates past the imported number.
        await using var retryDb = _fx.CreateDb();
        var retry = await Controller(retryDb, tenantId).Create(
            new CreateAdvanceRequest(publicId, "Collision", 1_000m, "Installments", 2, null, "rent", employeeId), CancellationToken.None);
        retry.Should().BeOfType<OkObjectResult>();
        ((Zayra.Api.Application.Finance.SalaryAdvanceDto)((OkObjectResult)retry).Value!).AdvanceNumber
            .Should().Be($"ADV-{DateTime.UtcNow.Year}-00002");
    }

    /// <summary>Plays the import: just before the request's insert, commits a row with the number the request is about to use.</summary>
    private sealed class NumberThief(string connectionString, Guid tenantId) : SaveChangesInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var added = eventData.Context?.ChangeTracker.Entries<SalaryAdvance>().FirstOrDefault(e => e.State == EntityState.Added)?.Entity;
            if (!Fired && added is not null)
            {
                Fired = true;
                await using var conn = new NpgsqlConnection(connectionString);
                await conn.OpenAsync(cancellationToken);
                await using var cmd = new NpgsqlCommand(
                    "INSERT INTO salary_advances (id, tenant_id, employee_id, employee_name, advance_number, requested_amount, approved_amount, " +
                    "repayment_type, installments, installment_amount, total_repaid, outstanding_balance, reason, status, is_locked_by_payroll, " +
                    "is_deleted, created_at_utc) VALUES (gen_random_uuid(), @t, gen_random_uuid(), 'Imported', @n, 500, 500, 'OneTime', 1, 500, " +
                    "0, 500, '', 'Active', false, false, now())", conn);
                cmd.Parameters.AddWithValue("t", tenantId);
                cmd.Parameters.AddWithValue("n", added.AdvanceNumber);
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            return result;
        }
    }

    private async Task<(Guid PublicId, int Id)> SeedEmployeeAsync(Guid tenantId)
    {
        await using var db = _fx.CreateDb();
        var company = new Company { TenantId = tenantId, LegalNameEn = "Numbering Co", CountryCode = "SAU", DefaultCurrency = "SAR", IsActive = true };
        var structure = new SalaryStructure { TenantId = tenantId, Code = "BASE", Name = "Base", Currency = "SAR", EffectiveDate = new DateOnly(2025, 1, 1), IsActive = true };
        var employee = new Employee
        {
            TenantId = tenantId, CompanyId = company.Id, EmployeeCode = $"N-{Guid.NewGuid():N}"[..16], FullName = "Collision",
            Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-2),
        };
        db.AddRange(company, structure, employee);
        db.AdvancePolicies.Add(new AdvancePolicy
        {
            TenantId = tenantId, PolicyName = "Std", MaxPercentageOfSalary = 50, MaxAdvancesPerYear = 5,
            MinServiceMonths = 0, AllowInstallments = true, MaxInstallments = 3, CooldownMonths = 0, RequiresApproval = true, IsActive = true,
        });
        await db.SaveChangesAsync();
        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = tenantId, EmployeeId = employee.Id, SalaryStructureId = structure.Id, BasicSalary = 10_000m, Currency = "SAR",
            EffectiveDate = new DateOnly(2025, 1, 1), IsActive = true,
        });
        await db.SaveChangesAsync();
        return (employee.PublicId, employee.Id);
    }

    private static AdvancesController Controller(ZayraDbContext db, Guid tenantId) => new(db, new OrgScope())
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("tenant_id", tenantId.ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                    new Claim(ClaimTypes.Name, "HR"), new Claim(ClaimTypes.Role, "HR Manager"), new Claim("permission", "loans.write"),
                }, "Test")),
            },
        },
    };

    private sealed class OrgScope : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) =>
            Task.FromResult(new DataScope { Level = DataScopeLevel.Organization });
    }
}
