using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// R07 — the exit interview. PATCH /api/offboarding/{id}/exit-interview stored any status string it was
/// sent ("Bogus", "completed"), silently ignored an out-of-range rating instead of refusing it, let a
/// completed or withdrawn separation be rewritten, accepted a waiver with no reason and wrote no audit
/// row. It now takes only Pending/Scheduled/Completed/Waived and a 0–5 rating, requires a reason to
/// waive, edits only an InProgress offboarding under a row lock, and audits the decision without the
/// employee's confidential notes.
///
/// <para>Run against real Postgres with production's retrying execution strategy, because the write
/// goes through the controller's transactional path and the FOR UPDATE rewrite.</para>
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class ExitInterviewWorkflowPostgresTests
{
    private const string ConfidentialNotes = "Confidential: left because of a named line manager";

    private readonly PostgresFixture _fixture;
    public ExitInterviewWorkflowPostgresTests(PostgresFixture fixture) => _fixture = fixture;

    private static OffboardingController Controller(ZayraDbContext db, Guid tenantId) => new(db)
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("tenant_id", tenantId.ToString()),
                    new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                    new Claim("permission", "employees.write"),
                }, "test")),
            },
        },
    };

    private static async Task<(Guid TenantId, EmployeeOffboarding Offboarding)> SeedAsync(
        ZayraDbContext db, string status = "InProgress")
    {
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var employee = new Employee
        {
            TenantId = tenantId, EmployeeCode = "EXIT-001", FullName = "Leaving Employee",
            Status = "Offboarded", JoiningDate = DateTime.UtcNow.AddYears(-2),
        };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        var offboarding = new EmployeeOffboarding
        {
            TenantId = tenantId, EmployeeId = employee.Id, EmployeeCode = employee.EmployeeCode,
            EmployeeName = employee.FullName, Status = status,
            NoticeDate = new DateOnly(2026, 9, 1), LastWorkingDay = new DateOnly(2026, 9, 30),
        };
        db.EmployeeOffboardings.Add(offboarding);
        await db.SaveChangesAsync();
        return (tenantId, offboarding);
    }

    private static async Task<EmployeeOffboarding> ReloadAsync(ZayraDbContext db, Guid id)
    {
        db.ChangeTracker.Clear();
        return await db.EmployeeOffboardings.AsNoTracking().SingleAsync(x => x.Id == id);
    }

    [Theory]
    [InlineData("Bogus", 4, "Some notes")]          // not a status the workflow knows
    [InlineData("Completed", 9, "Some notes")]      // rating above 5 used to be ignored, not refused
    [InlineData("Completed", -1, "Some notes")]
    [InlineData("Waived", 0, "")]                   // a waiver has to say why
    [InlineData("Waived", 0, "   ")]
    public async Task InvalidInput_IsRefused_AndNothingIsSaved(string status, int rating, string notes)
    {
        await using var db = _fixture.CreateDb();
        var (tenantId, offboarding) = await SeedAsync(db);

        var result = await Controller(db, tenantId).ExitInterview(offboarding.Id,
            new ExitInterviewRequest(status, new DateOnly(2026, 9, 26), "Personal", rating, notes), default);

        Assert.IsType<BadRequestObjectResult>(result);
        var saved = await ReloadAsync(db, offboarding.Id);
        Assert.Equal("Pending", saved.ExitInterviewStatus);
        Assert.Equal(0, saved.ExitInterviewRating);
        Assert.Null(saved.ExitInterviewDate);
        Assert.False(await db.AuditLogs.AnyAsync(a => a.TenantId == tenantId
            && a.Action == "offboarding.exit_interview_updated"));
    }

    [Theory]
    [InlineData("Completed")]
    [InlineData("Cancelled")]
    public async Task ClosedOrWithdrawnSeparation_CannotBeEdited(string offboardingStatus)
    {
        await using var db = _fixture.CreateDb();
        var (tenantId, offboarding) = await SeedAsync(db, offboardingStatus);

        var result = await Controller(db, tenantId).ExitInterview(offboarding.Id,
            new ExitInterviewRequest("Completed", new DateOnly(2026, 9, 26), "Personal", 4, "Some notes"), default);

        Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal("Pending", (await ReloadAsync(db, offboarding.Id)).ExitInterviewStatus);
    }

    [Theory]
    [InlineData("completed", "Completed")]
    [InlineData(" Scheduled ", "Scheduled")]
    [InlineData("WAIVED", "Waived")]
    public async Task ValidInput_IsStoredCanonically_AndAudited(string status, string canonical)
    {
        await using var db = _fixture.CreateDb();
        var (tenantId, offboarding) = await SeedAsync(db);

        var result = await Controller(db, tenantId).ExitInterview(offboarding.Id,
            new ExitInterviewRequest(status, new DateOnly(2026, 9, 26), "Personal", 4, "Employee declined a meeting"),
            default);

        Assert.IsType<OkObjectResult>(result);
        var saved = await ReloadAsync(db, offboarding.Id);
        Assert.Equal(canonical, saved.ExitInterviewStatus);
        Assert.Equal(new DateOnly(2026, 9, 26), saved.ExitInterviewDate);
        Assert.Equal("Personal", saved.ExitReasonCategory);
        Assert.Equal(4, saved.ExitInterviewRating);
        Assert.Equal(1, await db.AuditLogs.CountAsync(a => a.TenantId == tenantId
            && a.Action == "offboarding.exit_interview_updated" && a.EntityId == offboarding.Id.ToString()));
    }

    /// <summary>The audit row records the decision; the employee's free-text notes stay out of it.</summary>
    [Fact]
    public async Task AuditRecordsTheDecision_ButNeverTheConfidentialNotes()
    {
        await using var db = _fixture.CreateDb();
        var (tenantId, offboarding) = await SeedAsync(db);

        Assert.IsType<OkObjectResult>(await Controller(db, tenantId).ExitInterview(offboarding.Id,
            new ExitInterviewRequest("Completed", new DateOnly(2026, 9, 26), "Management", 2, ConfidentialNotes),
            default));

        Assert.Equal(ConfidentialNotes, (await ReloadAsync(db, offboarding.Id)).ExitInterviewNotes);
        var audit = await db.AuditLogs.AsNoTracking().SingleAsync(a => a.TenantId == tenantId
            && a.Action == "offboarding.exit_interview_updated");
        Assert.NotNull(audit.Metadata);
        Assert.Contains("Completed", audit.Metadata);
        Assert.Contains("\"hasNotes\":true", audit.Metadata);
        Assert.DoesNotContain("line manager", audit.Metadata);
        Assert.DoesNotContain("Confidential", audit.Metadata);
    }

    [Fact]
    public async Task AnotherTenant_CannotEditTheInterview()
    {
        await using var db = _fixture.CreateDb();
        var (_, offboarding) = await SeedAsync(db);
        var otherTenant = await PostgresFixture.SeedMinimalTenant(db);

        var result = await Controller(db, otherTenant).ExitInterview(offboarding.Id,
            new ExitInterviewRequest("Completed", new DateOnly(2026, 9, 26), "Personal", 4, "Some notes"), default);

        Assert.IsType<NotFoundResult>(result);
        Assert.Equal("Pending", (await ReloadAsync(db, offboarding.Id)).ExitInterviewStatus);
    }
}
