using System.Globalization;
using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Infrastructure.Payroll.ManpowerRecon;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Recon;

public class ReconExportTests
{
    private static async Task<ManpowerReconServiceTests.Kit> SeedAsync(string basicName = "Basic salary")
    {
        var k = await ManpowerReconServiceTests.Kit.CreateAsync();
        k.AddEmployee(1); k.AddSlip(k.Prior, 1, 1000.5m, 250m, 90m, 110m); k.AddSlip(k.Current, 1, 1250.75m, 250m, 99m, 121m);
        k.AddEmployee(2); k.AddSlip(k.Prior, 2, 800m);
        k.Db.PayrollRunEmployeeSelections.Add(new PayrollRunEmployeeSelection
        { TenantId = k.TenantId, CompanyId = k.CompanyId, PayrollRunId = k.Current.Id, EmployeeId = 2, Mode = "Exclude", Reason = "hold", Outcome = PayrollRunSelectionOutcomes.Excluded });
        await k.Db.SaveChangesAsync();
        foreach (var e in k.Db.PayrollEarnings.Where(e => e.ComponentCode == "BASIC")) e.ComponentName = basicName;
        await k.Db.SaveChangesAsync();
        return k;
    }

    private static PayrollReconController Ctrl(ManpowerReconServiceTests.Kit k, DataScope? scope = null) =>
        ReconHarness.Recon(k.Db, k.TenantId, "Finance Approver", ReconHarness.SignPermissions, dataScope: scope);

    private static FileContentResult File(IActionResult r) => r.Should().BeOfType<FileContentResult>().Subject;

    private static List<(string Sheet, List<List<string>> Rows)> ReadXlsx(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var doc = SpreadsheetDocument.Open(stream, false);
        var wb = doc.WorkbookPart!;
        var result = new List<(string, List<List<string>>)>();
        foreach (var sheet in wb.Workbook.Sheets!.Elements<Sheet>())
        {
            var part = (WorksheetPart)wb.GetPartById(sheet.Id!);
            var rows = part.Worksheet.Descendants<Row>().Select(r => r.Elements<Cell>()
                .Select(c => c.DataType?.Value == CellValues.InlineString ? c.InlineString!.Text!.Text : c.CellValue?.Text ?? "").ToList()).ToList();
            result.Add((sheet.Name!.Value!, rows));
        }
        return result;
    }

    private static string Csv(IActionResult r) => Encoding.UTF8.GetString(File(r).FileContents).TrimStart('﻿');

    // ── Shape ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheWorkbook_HasASheetPerBlock_WithHeadersAndTypedNumbers()
    {
        var k = await SeedAsync();
        var res = await Ctrl(k).Export(k.Current.Id, "xlsx", null, default);
        var file = File(res);
        file.ContentType.Should().Be("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        var sheets = ReadXlsx(file.FileContents);
        sheets.Select(s => s.Sheet).Should().Equal("Summary", "Volume", "Value", "Final settlement");

        var value = sheets.Single(s => s.Sheet == "Value").Rows;
        value[0].Should().Equal("Section", "Code", "Item", "Prior", "New joiner", "Rehire", "Late joiner", "Reinstated", "Transfer in",
            "Leaver", "Transfer out", "Not paid", "Unexplained", "Package", "Proration", "Variable", "Arrears", "Attendance", "Loans",
            "Statutory", "Other", "Current", "Delta", "Delta %", "Flagged", "Explanation");
        var basic = value.Single(r => r[0] == "Earning" && r[1] == "BASIC");
        basic[3].Should().Be("1800.50");      // prior: 1000.5 + 800
        basic[21].Should().Be("1250.75");     // current
        value.Should().Contain(r => r[0] == "Headline" && r[1] == "NET");
        value.Should().Contain(r => r[0] == "Employer" && r[1] == "GOSI-ER");

        var volume = sheets.Single(s => s.Sheet == "Volume").Rows;
        volume[0].Should().Equal("Category", "Kind", "Count", "FS approved", "FS missing");
        volume.Should().Contain(r => r[0] == "NOT_PAID" && r[2] == "1");
    }

    [Fact]
    public async Task TheCsv_CarriesTheSameBlocks_UnderLabelledHeadings()
    {
        var k = await SeedAsync();
        var csv = Csv(await Ctrl(k).Export(k.Current.Id, "csv", null, default));
        csv.Should().Contain("# Summary").And.Contain("# Volume").And.Contain("# Value").And.Contain("# Final settlement");
        csv.Should().Contain("\"Earning\",\"BASIC\"");
        File(await Ctrl(k).Export(k.Current.Id, "csv", null, default)).ContentType.Should().StartWith("text/csv");
    }

    [Fact]
    public async Task AnUnknownFormat_IsABadRequest()
    {
        var k = await SeedAsync();
        PayrollReconAccessTests.Status(await Ctrl(k).Export(k.Current.Id, "pdf", null, default)).Should().Be(400);
    }

    // ── Money and injection ──────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("ar-SA")]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    public async Task Money_IsInvariantCulture_TwoDecimals_WhateverTheServerCulture(string culture)
    {
        var k = await SeedAsync();
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            var csv = Csv(await Ctrl(k).Export(k.Current.Id, "csv", null, default));
            csv.Should().Contain("1250.75").And.Contain("1800.50");
            csv.Should().NotContain("1250,75").And.NotContain("١");
        }
        finally { CultureInfo.CurrentCulture = before; }
    }

    [Fact]
    public async Task ACellStartingWithAFormulaCharacter_IsNeutralisedInCsv_AndStaysTextInXlsx()
    {
        var k = await SeedAsync(basicName: "=HYPERLINK(\"http://evil\",\"x\")");
        var csv = Csv(await Ctrl(k).Export(k.Current.Id, "csv", null, default));
        csv.Should().Contain("\"'=HYPERLINK(");
        csv.Should().NotContain(",\"=HYPERLINK(");
        var xlsx = ReadXlsx(File(await Ctrl(k).Export(k.Current.Id, "xlsx", null, default)).FileContents);
        var item = xlsx.Single(s => s.Sheet == "Value").Rows.Single(r => r[0] == "Earning" && r[1] == "BASIC");
        item[2].Should().StartWith("=HYPERLINK(", "an inline string cell is text, never evaluated");
    }

    [Fact]
    public async Task ANegativeAmount_IsNotMangledByTheInjectionGuard()
    {
        var k = await SeedAsync();
        var csv = Csv(await Ctrl(k).Export(k.Current.Id, "csv", null, default));
        csv.Should().Contain("\"-800.00\"");          // the held-out employee leaves: an exit is negative
        csv.Should().NotContain("'-800.00");
    }

    // ── Watermarks and file names ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WithoutASignoff_TheExportSaysUnsignedDraft_InTheSummaryAndTheFileName()
    {
        var k = await SeedAsync();
        var res = await Ctrl(k).Export(k.Current.Id, "xlsx", null, default);
        File(res).FileDownloadName.Should().Be("payroll-recon-2026-10-UNSIGNED-DRAFT.xlsx");
        var summary = ReadXlsx(File(res).FileContents).Single(s => s.Sheet == "Summary").Rows;
        summary.Should().Contain(r => r[0] == "Status" && r[1] == "UNSIGNED DRAFT");
    }

    [Fact]
    public async Task APartialScope_IsLabelledPartial_InTheSummaryAndTheFileName()
    {
        var k = await SeedAsync();
        var scoped = new DataScope { Level = DataScopeLevel.Department, AllowedEmployeeIds = new[] { 1 } };
        var res = await Ctrl(k, scoped).Export(k.Current.Id, "csv", null, default);
        File(res).FileDownloadName.Should().Be("payroll-recon-2026-10-PARTIAL.csv");
        Csv(res).Should().Contain("PARTIAL");
    }

    [Fact]
    public async Task WithASignoff_TheExportIsRenderedFromTheStoredSnapshot_NotTheLiveFigures()
    {
        var k = await SeedAsync();
        var ctrl = Ctrl(k);
        StatusOf(await ctrl.Signoff(k.Current.Id, await ReconHarness.ValidRequestAsync(ctrl, k.Current.Id), default)).Should().Be(201);

        // The live figures move after the review (a late slip edit); the signed export must keep showing what was signed.
        var slip = await k.Db.PayrollSlips.FirstAsync(s => s.RunId == k.Current.Id && s.EmployeeId == 1);
        foreach (var e in k.Db.PayrollEarnings.Where(e => e.PayrollRunId == k.Current.Id && e.EmployeeId == 1 && e.ComponentCode == "BASIC")) e.Amount = 9999m;
        slip.GrossSalary += 9999m - 1250.75m; slip.BasicSalary = 9999m;
        await k.Db.SaveChangesAsync();

        var res = await ctrl.Export(k.Current.Id, "csv", null, default);
        File(res).FileDownloadName.Should().Be("payroll-recon-2026-10.csv");
        var csv = Csv(res);
        csv.Should().Contain("1250.75").And.NotContain("9999.00");
        csv.Should().Contain("SIGNED OFF").And.NotContain("UNSIGNED DRAFT");
        csv.Should().Contain("# Explanations");
    }

    [Fact]
    public async Task ASupersededSignoff_NoLongerBacksTheExport()
    {
        var k = await SeedAsync();
        var ctrl = Ctrl(k);
        await ctrl.Signoff(k.Current.Id, await ReconHarness.ValidRequestAsync(ctrl, k.Current.Id), default);
        await ctrl.WithdrawSignoff(k.Current.Id, new ReconWithdrawRequest("Housing needs another look."), default);
        File(await ctrl.Export(k.Current.Id, "csv", null, default)).FileDownloadName.Should().Contain("UNSIGNED-DRAFT");
    }

    [Fact]
    public async Task EveryExport_IsAudited()
    {
        var k = await SeedAsync();
        await Ctrl(k).Export(k.Current.Id, "xlsx", null, default);
        await Ctrl(k).Export(k.Current.Id, "csv", null, default);
        var rows = await k.Db.PayrollAuditLogs.AsNoTracking().Where(a => a.Action == "payroll.recon.exported").ToListAsync();
        rows.Should().HaveCount(2);
        rows.Select(r => r.MetadataJson).Should().Contain(m => m.Contains("xlsx")).And.Contain(m => m.Contains("csv"));
        rows[0].EntityId.Should().Be(k.Current.Id.ToString());
    }

    private static int StatusOf(IActionResult r) => PayrollReconAccessTests.Status(r);
}
