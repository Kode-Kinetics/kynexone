using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Platform;

/// <summary>
/// Regression coverage for compliance-control endpoints. The PATCH must accept a partial
/// body (status only) and create must reject duplicate (Category, ControlId) with 409, not 500.
/// </summary>
public class PlatformComplianceTests : PlatformTestBase
{
    private static ComplianceControlRequest NewControl(string id = "CC6.1") =>
        new("CC6 — Access", id, "Logical Access", null, "NotStarted", null, null, null, null);

    private static Guid IdOf(IActionResult created) =>
        (Guid)((CreatedResult)created).Value!.GetType().GetProperty("Id")!.GetValue(((CreatedResult)created).Value!)!;

    [Fact]
    public async Task CreateControl_Returns201_AndPersists()
    {
        await using var db = CreateDb();
        var controller = CreateController(db);

        var result = await controller.CreateComplianceControl(NewControl(), CancellationToken.None);

        result.Should().BeOfType<CreatedResult>();
        (await db.PlatformComplianceControls.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task CreateControl_DuplicateCategoryAndControlId_Returns409_NotServerError()
    {
        await using var db = CreateDb();
        var controller = CreateController(db);
        await controller.CreateComplianceControl(NewControl(), CancellationToken.None);

        var dup = await controller.CreateComplianceControl(NewControl(), CancellationToken.None);

        dup.Should().BeOfType<ConflictObjectResult>();
        (await db.PlatformComplianceControls.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task UpdateControl_WithStatusOnlyPartialBody_Succeeds_AndPersists()
    {
        await using var db = CreateDb();
        var controller = CreateController(db);
        var id = IdOf(await controller.CreateComplianceControl(NewControl(), CancellationToken.None));

        // Partial PATCH — only status + reviewed, no Category/ControlId/Title (the prod-breaking case).
        var patch = new PatchComplianceControlRequest(
            Title: null, Description: null, Status: "Implemented", Owner: null,
            EvidenceNote: null, EvidenceUrl: null, Reviewed: true);
        var result = await controller.UpdateComplianceControl(id, patch, CancellationToken.None);

        result.Should().Match(r => r is OkObjectResult || r is NoContentResult || r is OkResult);
        var stored = await db.PlatformComplianceControls.FirstAsync(c => c.Id == id);
        stored.Status.Should().Be("Implemented");
        stored.ReviewedAtUtc.Should().NotBeNull();
    }
}
