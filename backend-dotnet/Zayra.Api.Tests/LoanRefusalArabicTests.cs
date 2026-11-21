using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// A loan refusal is a sentence the user reads, so it is given in English and Arabic as an advance refusal is:
/// <c>{ error, message, messageAr }</c>. The loan request and the loan payment refusals used to be bare English strings.
/// </summary>
public class LoanRefusalArabicTests
{
    private static readonly Regex BareRefusal = new(@"\b(BadRequest|Conflict|NotFound|UnprocessableEntity)\(\s*""", RegexOptions.Compiled);

    [Theory]
    [InlineData("LoansController.cs")]
    [InlineData("LoansController.Payments.cs")]
    public void NoLoanRefusalIsABareEnglishString(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "backend-dotnet", "Zayra.Api", "Program.cs"))) dir = dir.Parent;
        var source = File.ReadAllText(Path.Combine(dir!.FullName, "backend-dotnet", "Zayra.Api", "Controllers", "Finance", file));

        var bare = source.Split('\n').Select((line, i) => (line, i: i + 1)).Where(x => BareRefusal.IsMatch(x.line)).Select(x => $"{x.i}: {x.line.Trim()}").ToList();

        Assert.True(bare.Count == 0, "English-only refusals:\n" + string.Join("\n", bare));
    }

    [Fact]
    public async Task AnInvalidLoanRequest_IsRefusedInEnglishAndArabic()
    {
        var (controller, db) = Controller();
        await using var _ = db;

        var result = await controller.CreateLoan(new CreateLoanRequest(Guid.Empty, string.Empty, Guid.NewGuid(), 0m, 3, null), default);

        var body = Json(Assert.IsType<BadRequestObjectResult>(result).Value);
        Assert.Equal("loan_amount_invalid", body.GetProperty("error").GetString());
        Assert.Contains("two decimal places", body.GetProperty("message").GetString());
        Assert.Matches(@"\p{IsArabic}", body.GetProperty("messageAr").GetString()!);
    }

    [Fact]
    public async Task AMissingPaymentBatchSelection_IsRefusedInEnglishAndArabic()
    {
        var (controller, db) = Controller();
        await using var _ = db;

        var result = await controller.CreatePaymentBatch(new CreateLoanPaymentBatchRequest(Array.Empty<Guid>()), default);

        var body = Json(Assert.IsType<BadRequestObjectResult>(result).Value);
        Assert.Matches(@"\p{IsArabic}", body.GetProperty("messageAr").GetString()!);
    }

    private static (LoansController, ZayraDbContext) Controller()
    {
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        return (new LoansController(db, new Scope())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim("tenant_id", Guid.NewGuid().ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                        new Claim(ClaimTypes.Role, "Finance"), new Claim("permission", "loans.write"),
                    }, "test")),
                },
            },
        }, db);
    }

    private static JsonElement Json(object? value)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return doc.RootElement.Clone();
    }

    private sealed class Scope : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) =>
            Task.FromResult(new DataScope { Level = DataScopeLevel.Organization });
    }
}
