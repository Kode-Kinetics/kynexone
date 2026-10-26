using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Finance;

namespace Zayra.Api.Tests;

/// <summary>
/// Cost-accounting v1, WP-C3, final security review (AM-21): the Preview's tie-out failure names cost centres and amounts (<c>costCentreDeltas</c>).
/// That detail is for export or manage holders; a finance.gl.read user gets how many cost centres disagree and a generic message.
/// </summary>
public class JournalExportTieOutRedactionTests
{
    private static object Payload() => new
    {
        error = JournalExportErrors.CostCentreTotalMismatch,
        failure = JournalExportErrors.CostCentreTotalMismatch,
        message = "The export does not tie out to the GL, so no file was produced. An export that silently omits or misstates rows is worse than none.",
        roundedDebits = 100m, roundedCredits = 100m, delta = 0m,
        accountDeltas = new[] { "5001: +0.00" },
        costCentreDeltas = new[] { "CC-200 / 5001: debits +10.00", "CC-110 / 5001: debits -10.00" },
        byCompany = Array.Empty<object>(),
    };

    private static GlJournalExportsController Controller(params string[] permissions)
    {
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var exports = new JournalExportService(db, JournalExportFormatterGoldenTests.AllFormatters());
        var claims = new List<Claim> { new("tenant_id", Guid.NewGuid().ToString()), new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) };
        claims.AddRange(permissions.Select(p => new Claim("permission", p)));
        return new GlJournalExportsController(db, exports, new PeriodHandoffReconciler(db, exports))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) } },
        };
    }

    [Fact]
    public void AReadOnlyCaller_GetsTheCountAndAGenericMessage_NotTheCostCentresOrAmounts()
    {
        var redacted = JsonSerializer.SerializeToElement(Controller("finance.gl.read").RedactTieOutDetail(Payload()));

        redacted.TryGetProperty("costCentreDeltas", out _).Should().BeFalse();
        redacted.GetProperty("costCentreDeltaCount").GetInt32().Should().Be(2);
        redacted.GetProperty("error").GetString().Should().Be(JournalExportErrors.CostCentreTotalMismatch);
        var text = redacted.GetRawText();
        text.Should().NotContain("CC-200").And.NotContain("CC-110").And.NotContain("10.00");
        redacted.GetProperty("message").GetString().Should().Contain("export access");
    }

    [Theory]
    [InlineData("finance.gl.export")]
    [InlineData("finance.gl.manage")]
    public void AnExportOrManageHolder_KeepsTheFullDetail(string permission)
    {
        var full = JsonSerializer.SerializeToElement(Controller("finance.gl.read", permission).RedactTieOutDetail(Payload()));

        full.GetProperty("costCentreDeltas").GetArrayLength().Should().Be(2);
        full.TryGetProperty("costCentreDeltaCount", out _).Should().BeFalse();
    }

    [Fact]
    public void ABodyWithNoCostCentreDetail_IsLeftAsItIs()
    {
        var other = new { error = JournalExportErrors.MixedCurrency, message = "m", currencies = new[] { "SAR", "KWD" } };

        JsonSerializer.SerializeToElement(Controller("finance.gl.read").RedactTieOutDetail(other)).GetRawText()
            .Should().Be(JsonSerializer.SerializeToElement(other).GetRawText());
    }
}
