using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Organization;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Http;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.CompanyStructure;

/// <summary>
/// Two people saving the same new code at once: one wins, the others are told the code is taken. The unique index is the
/// arbiter, and its violation must reach the caller as a conflict, never as an unhandled database error.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class OrgCodeRacePostgresTests(PostgresFixture fixture)
{
    private const int Writers = 12;
    private static RequestContext Ctx(Guid tenant) => new("127.0.0.1", "test", Guid.NewGuid(), tenant);
    private static OrganizationSetupService Svc(ZayraDbContext db) => new(db, new AuditService(db));

    private async Task<(Guid Tenant, Guid Company)> TenantAsync()
    {
        await using var db = fixture.CreateDb();
        var tenant = Guid.NewGuid();
        var company = new Company { TenantId = tenant, LegalNameEn = "Alpha", RegistrationNumber = $"A-{tenant:N}", DefaultCurrency = "SAR", IsActive = true };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        return (tenant, company.Id);
    }

    private async Task AssertOneWinnerAsync(Func<ZayraDbContext, Task> create)
    {
        var start = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, Writers).Select(async _ =>
        {
            await start.Task;
            await using var db = fixture.CreateDb();
            try { await create(db); return (Exception?)null; }
            catch (Exception ex) { return ex; }
        }).ToList();
        start.SetResult();
        var results = await Task.WhenAll(tasks);

        results.Count(r => r is null).Should().Be(1, "exactly one writer creates the row");
        var failures = results.Where(r => r is not null).Select(r => r!).ToList();
        failures.Should().NotContain(f => f is DbUpdateException, "a lost race is a conflict the caller can read, not a database error");
        failures.Should().OnlyContain(f => f is OrgCodeConflictException || f is InvalidOperationException);
    }

    [Fact]
    public async Task ConcurrentDepartmentCreates_YieldOneWinner_NoDatabaseError()
    {
        var (t, c) = await TenantAsync();
        await AssertOneWinnerAsync(db => Svc(db).CreateDepartmentAsync(t, new DepartmentRequest(null, null, null, "RACE", "Race", null, null, CompanyId: c), Ctx(t), default));
    }

    [Fact]
    public async Task ConcurrentDesignationCreates_YieldOneWinner_NoDatabaseError()
    {
        var (t, c) = await TenantAsync();
        await AssertOneWinnerAsync(db => Svc(db).CreateDesignationAsync(t, new DesignationRequest(null, "RACE", "Race", null, null, null, null, null, CompanyId: c), Ctx(t), default));
    }

    [Fact]
    public async Task ConcurrentGradeCreates_YieldOneWinner_NoDatabaseError()
    {
        var (t, c) = await TenantAsync();
        await AssertOneWinnerAsync(db => Svc(db).CreateGradeAsync(t, new GradeRequest("RACE", "Race", null, 1, CompanyId: c), Ctx(t), default));
    }

    [Fact]
    public async Task ConcurrentBranchCreates_YieldOneWinner_NoDatabaseError()
    {
        var (t, c) = await TenantAsync();
        await AssertOneWinnerAsync(db => Svc(db).CreateBranchAsync(t, new BranchRequest(c, "RACE", "Race", null, "SA", "Riyadh", null, null, "Asia/Riyadh", null), Ctx(t), default));
    }

    [Fact]
    public void ACodeConflict_IsAnswered409_WithItsStableCode()
    {
        var (status, code, message) = ExceptionResponseMapper.Map(new OrgCodeConflictException("Department", "RACE"));
        status.Should().Be(409);
        code.Should().Be("code_taken");
        message.Should().Contain("RACE");
    }
}
