using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Employees;
using Zayra.Api.Application.Finance;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.ReleaseA;

/// <summary>Additive read fields: names travel with the allowance lines (employee detail, salary assignment list), and the list is gated like the slot amounts.</summary>
public partial class CashAllowanceReviewFixTests
{
    // ══ A. Names travel with the lines (no catalogue access needed to read them) ════════════════

    [Fact]
    public async Task TheEmployeeDetails_AllowanceLines_CarryTheirEnglishAndArabicNames()
    {
        await using var w = await NewWorld();
        await GiveAsync(w, w.A, Car, 500m);
        var service = new EmployeeManagementService(w.Db, new AuditService(w.Db), new NullDocumentStorage(), TestNotifications.For(w.Db));

        var detail = await service.GetAsync(w.Tid, w.A.Id, includeSensitive: true, new RequestContext(null, "test", Guid.NewGuid(), w.Tid), CancellationToken.None);

        var line = detail!.SalaryBreakdown!.Allowances.Should().ContainSingle().Subject;
        (line.Code, line.NameEn, line.NameAr, line.Basis, line.Amount).Should().Be((Car, "Car allowance", "Car allowance ar", "Amount", 500m));
        var json = JsonSerializer.SerializeToElement(detail.SalaryBreakdown, Web).GetProperty("allowances").EnumerateArray().Single();
        json.GetProperty("nameEn").GetString().Should().Be("Car allowance");
        json.GetProperty("nameAr").GetString().Should().Be("Car allowance ar");

        var withoutPayAccess = await service.GetAsync(w.Tid, w.A.Id, includeSensitive: false, new RequestContext(null, "test", Guid.NewGuid(), w.Tid), CancellationToken.None);
        withoutPayAccess!.SalaryBreakdown.Should().BeNull("a viewer without pay access gets no package at all, lines included");
    }

    [Fact]
    public void TheNamesAreForReading_TheStagedTextCarriesNone_AndARequestsNamesAreNeverTrusted()
    {
        SalaryAllowanceLines.Serialize([new ResolvedAllowanceLine(Car, AllowanceBases.Amount, null, 500m)])
            .Should().Be("[{\"code\":\"ALW_CAR\",\"basis\":\"Amount\",\"rate\":null,\"amount\":500}]", "the staged JSON is byte-identical to what it was before names existed");
        JsonSerializer.Serialize(new SalaryAllowanceLine(Car, "Amount", null, 500m, "Car", "سيارة"), Web).Should().Contain("\"nameEn\":\"Car\"");
    }

    // ══ B. The salary assignment list returns each record's lines, gated like the slot amounts ══

    [Fact]
    public async Task TheSalaryAssignmentList_ReturnsEachRecordsNamedLines()
    {
        await using var w = await NewWorld();
        await GiveAsync(w, w.A, Car, 1_000m, rate: 0.10m);

        var listed = (OkObjectResult)await Hr(w).ListEmployeeSalaryStructures(null, CancellationToken.None);
        var records = JsonSerializer.SerializeToElement(listed.Value, Web).EnumerateArray().ToList();

        var mine = records.Single(r => r.GetProperty("employeeId").GetInt32() == w.A.Id).GetProperty("allowances").EnumerateArray().Single();
        (mine.GetProperty("code").GetString(), mine.GetProperty("nameEn").GetString(), mine.GetProperty("nameAr").GetString(), mine.GetProperty("basis").GetString(),
                mine.GetProperty("rate").GetDecimal(), mine.GetProperty("amount").GetDecimal())
            .Should().Be((Car, "Car allowance", "Car allowance ar", "PercentOfBasic", 0.10m, 1_000m));
        records.Single(r => r.GetProperty("employeeId").GetInt32() == w.B.Id).GetProperty("allowances").GetArrayLength().Should().Be(0, "a record with none lists none");
    }

    [Fact]
    public async Task TheSalaryAssignmentProjection_MasksTheLines_WhereItMasksTheAmounts()
    {
        await using var w = await NewWorld();
        await GiveAsync(w, w.A, Car, 500m);
        var record = await w.Db.EmployeeSalaryStructures.AsNoTracking().SingleAsync(x => x.EmployeeId == w.A.Id);
        var lines = (await SalaryAllowanceLines.NamedByRecordAsync(w.Db, w.Tid, [record.Id], default))[record.Id];

        var shown = SalaryStructureAssignmentDto.Project(record, includeSensitive: true, lines);
        var masked = SalaryStructureAssignmentDto.Project(record, includeSensitive: false, lines);

        shown.BasicSalary.Should().Be(10_000m);
        shown.Allowances.Should().ContainSingle().Which.NameEn.Should().Be("Car allowance");
        masked.BasicSalary.Should().BeNull();
        masked.Allowances.Should().BeNull("the lines are pay: as hidden as the amounts");
        // Check values, not the raw JSON text: a substring check for "500" also matched random GUIDs such as "a6500a55-…"
        // (a ~1-in-140 CI flake). No string value may name the allowance and no number may carry its amount.
        var values = LeafValues(JsonSerializer.SerializeToElement(masked, Web)).ToList();
        values.Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!)
            .Should().NotContain(s => s.Contains("Car allowance") || s.Contains(Car));
        values.Where(v => v.ValueKind == JsonValueKind.Number).Select(v => v.GetDecimal())
            .Should().NotContain(500m, "the allowance amount is pay and stays hidden");
    }

    private static IEnumerable<JsonElement> LeafValues(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().SelectMany(p => LeafValues(p.Value)),
        JsonValueKind.Array => element.EnumerateArray().SelectMany(LeafValues),
        _ => [element],
    };
}
