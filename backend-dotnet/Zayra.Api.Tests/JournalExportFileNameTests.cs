using FluentAssertions;
using Zayra.Api.Infrastructure.Finance;
using static Zayra.Api.Tests.CostExportHarness;
using static Zayra.Api.Tests.Security.SeededRoleBundles;

namespace Zayra.Api.Tests;

/// <summary>
/// The journal file name is shown to finance users and sent to the ERP team. When the company has no ERP company code the
/// name must not carry the company's registration (CR) number: it falls back to a neutral, stable, filesystem-safe token.
/// The CompanyCode column INSIDE the file keeps its existing fallback (registration number) and is not changed here.
/// </summary>
public class JournalExportFileNameTests
{
    private const string Cr = "7001234567";

    private static async Task<(string FileName, string Csv)> ExportAsync(string? erpCode)
    {
        var (db, tenantId) = await TenantAsync(erpCode: erpCode);
        SeedChart(db, tenantId);
        db.Add(Row(tenantId, "Accrual", "5000 - Salaries", "", 10_000m));
        db.Add(Row(tenantId, "Accrual", "", "2100 - Salaries Payable", 10_000m));
        db.SaveChanges();
        var maker = await CallerAsync(db, tenantId, "Payroll Manager");

        var created = await CreateAsync(db, maker, "generic-csv");
        var fileName = Body(created).GetProperty("fileName").GetString()!;
        var csv = Utf8(await DownloadAsync(db, tenantId, IdOf(created)));
        return (fileName, csv);
    }

    [Fact]
    public async Task BlankErpCode_FileNameDoesNotCarryTheRegistrationNumber_ButTheFileColumnStillDoes()
    {
        var (fileName, csv) = await ExportAsync(erpCode: null);

        fileName.Should().NotContain(Cr, "the CR number is not a file-name token");
        fileName.Should().StartWith("journal-AAAAAAAA-", "a neutral token derived from the company id is used instead");
        fileName.Should().MatchRegex("^[A-Za-z0-9._-]+$", "the name stays filesystem-safe");
        csv.Should().Contain(Cr, "the CompanyCode column inside the file keeps its registration-number fallback");
    }

    [Fact]
    public async Task BlankErpCode_FileNameTokenIsStableAcrossExports()
    {
        var (first, _) = await ExportAsync(erpCode: "   ");
        var (second, _) = await ExportAsync(erpCode: "");

        first.Split('-')[1].Should().Be(second.Split('-')[1]);
        first.Should().NotContain(Cr);
    }

    [Fact]
    public async Task ConfiguredErpCode_IsStillTheFileNameToken()
    {
        var (fileName, csv) = await ExportAsync(erpCode: "SA01");

        fileName.Should().StartWith("journal-SA01-");
        csv.Should().Contain("SA01");
    }
}
