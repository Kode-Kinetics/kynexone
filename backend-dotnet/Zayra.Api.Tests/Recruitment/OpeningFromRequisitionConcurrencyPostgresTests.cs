using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers.Recruitment;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Recruitment;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Recruitment;

/// <summary>
/// <c>OpeningsController.Create</c> read the requisition's status, then wrote the opening and the Converted stamp later, with nothing
/// between two creates that arrived together: both read Approved and each inserted its own opening for the one requisition. Create now
/// runs as one serialized unit per requisition (advisory lock, re-read, guard, stamp, insert, commit), so the second create reads the
/// committed Converted status and is refused with a 409.
///
/// <para>Real Postgres only: the in-memory and SQLite providers take no locks, so a race there passes whatever the code does. Each
/// contender has its own <see cref="ZayraDbContext"/> and connection, and the race repeats over fresh requisitions so one serial round
/// cannot pass the test by accident.</para>
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class OpeningFromRequisitionConcurrencyPostgresTests
{
    private const int Rounds = 6;
    private readonly PostgresFixture _fx;
    public OpeningFromRequisitionConcurrencyPostgresTests(PostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task TwoSimultaneousCreates_ForOneApprovedRequisition_MakeExactlyOneOpening()
    {
        Guid tenantId;
        await using (var seed = _fx.CreateDb()) tenantId = await PostgresFixture.SeedMinimalTenant(seed);

        for (var round = 0; round < Rounds; round++)
        {
            var requisitionId = await SeedApprovedAsync(tenantId, round);

            var results = await RaceAsync(
                () => CreateAsync(tenantId, requisitionId),
                () => CreateAsync(tenantId, requisitionId));

            results.Should().NotContainNulls($"round {round}: the losing create must be refused cleanly, never a 500");
            results.OfType<CreatedResult>().Should().HaveCount(1, $"round {round}: exactly one create wins");
            results.OfType<ConflictObjectResult>().Should().HaveCount(1, $"round {round}: the other reads the committed Converted status");

            await using var db = _fx.CreateDb();
            var openings = await db.JobOpenings.AsNoTracking().Where(o => o.TenantId == tenantId && o.RequisitionId == requisitionId).ToListAsync();
            openings.Should().ContainSingle($"round {round}: one requisition, one opening");
            (await db.ManpowerRequisitions.AsNoTracking().SingleAsync(r => r.Id == requisitionId)).Status.Should().Be("Converted");
        }
    }

    private async Task<Guid> SeedApprovedAsync(Guid tenantId, int round)
    {
        await using var db = _fx.CreateDb();
        var req = new ManpowerRequisition
        {
            TenantId = tenantId,
            RequisitionNumber = $"REQ-OPN-{round:D2}-{Guid.NewGuid():N}"[..24],
            DepartmentName = "Operations",
            DesignationTitle = "Site Supervisor",
            HeadCount = 2,
            Status = "Approved",
            RequestedByName = "Line Manager",
        };
        db.ManpowerRequisitions.Add(req);
        await db.SaveChangesAsync();
        return req.Id;
    }

    private async Task<IActionResult> CreateAsync(Guid tenantId, Guid requisitionId)
    {
        await using var db = _fx.CreateDb();
        await db.Database.OpenConnectionAsync();      // warm before the barrier releases
        var controller = new OpeningsController(db, new RecruitmentService(db), new NoNotifications())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim("tenant_id", tenantId.ToString()),
                        new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                        new Claim(ClaimTypes.Role, "HR Manager"),
                        new Claim("is_group_scope", "true"),
                    }, "Test")),
                },
            },
        };
        var request = new CreateOpeningRequest(requisitionId, "Site Supervisor", "Operations", "Site Supervisor", "Full-Time", 2,
            "d", "r", "p", 1000m, 2000m, "HQ", "HR");
        return await controller.Create(request, CancellationToken.None);
    }

    /// <summary>Both contenders started, both connections warm, then released together. A contender that throws yields null, which the
    /// assertions treat as a failure.</summary>
    private static async Task<IActionResult?[]> RaceAsync(Func<Task<IActionResult>> first, Func<Task<IActionResult>> second)
    {
        var ready = new SemaphoreSlim(0, 2);
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<IActionResult?> Run(Func<Task<IActionResult>> contender)
        {
            await Task.Yield();
            ready.Release();
            await go.Task;
            try { return await contender(); }
            catch { return null; }
        }

        var a = Task.Run(() => Run(first));
        var b = Task.Run(() => Run(second));
        await ready.WaitAsync();
        await ready.WaitAsync();
        go.SetResult();
        return await Task.WhenAll(a, b);
    }

    private sealed class NoNotifications : INotificationService
    {
        public Task NotifyAsync(Guid t, Guid? u, string title, string msg, string entity, string? entityId, CancellationToken ct) => Task.CompletedTask;
        public Task SendEmailAsync(Guid t, string code, string to, string name, Dictionary<string, string> vars, CancellationToken ct) => Task.CompletedTask;
    }
}
