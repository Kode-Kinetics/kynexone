using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;

namespace Zayra.Api.Tests.CompanyStructure;

/// <summary>The row cap of an import package counts rows the way the parser reads them: a trailing newline is not a row, a lone CR is a line break.</summary>
public class CsvRowCountTests
{
    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("Code,Name", 0)]
    [InlineData("Code,Name\n", 0)]
    [InlineData("Code,Name\nA,a", 1)]
    [InlineData("Code,Name\nA,a\n", 1)]
    [InlineData("Code,Name\nA,a\n\n\n", 1)]
    [InlineData("Code,Name\r\nA,a\r\nB,b\r\n", 2)]
    [InlineData("Code,Name\rA,a\rB,b\rC,c", 3)]
    [InlineData("Code,Name\nA,a\n   \nB,b", 2)]
    [InlineData("\uFEFFCode,Name\nA,a\nB,b\n", 2)]
    public void CountDataRows_CountsTheNonBlankLinesAfterTheHeader(string? csv, int expected) =>
        Csv.CountDataRows(csv).Should().Be(expected);

    [Fact]
    public void CountDataRows_AgreesWithTheParser()
    {
        foreach (var text in new[] { "Code,Name\nA,a\n", "Code,Name\r\nA,a\r\n\r\nB,b\r\n", "Code,Name\rA,a\rB,b", "Code,Name\n\nA,a\n  \nB,b\n\n" })
            Csv.CountDataRows(text).Should().Be(Csv.Parse(text).Count, text.Replace("\r", "\\r").Replace("\n", "\\n"));
    }

    private static OrganizationStructureImportController Controller()
    {
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("tenant_id", Guid.NewGuid().ToString()), new Claim(ClaimTypes.Role, "Admin") }, "Test"));
        return new(db, new AuditService(db)) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } } };
    }

    [Fact]
    public async Task APackageOfCarriageReturnOnlyRows_IsHeldToTheRowCap_LikeAnyOther()
    {
        // 20,001 records separated by a lone CR: no '\n' at all, so counting newlines saw one line and let it through.
        var package = new OrganizationStructureImportRequest(null, null, null, null, "Code,Name,Band,Level\r" + string.Join("\r", Enumerable.Repeat("G,Grade,B,1", 20_001)),
            null, null, null);
        var preview = await Controller().Preview(package, default);
        (preview.Result as ObjectResult)?.StatusCode.Should().Be(422);
    }
}
