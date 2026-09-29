using System.Globalization;
using System.Text;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports;
namespace Zayra.Api.Tests;

// Independent golden bytes transcribed from the bank's published column table, not from generator output.
public sealed class SaudiBankExportIndependentTests
{
    private static AnbHeaderInput Header() => new("2026090001", "PAYROLL", "1234-5", "0108061198800026",
        new DateOnly(2026, 9, 30), "Example Employer", "Riyadh", "Olaya", "King Fahd Road", "September payroll", "Example LLC");
    private static AnbPaymentInput Row(int id = 1) => new(id, $"TEST{id}", (1000000000L + id).ToString(CultureInfo.InvariantCulture),
        "01080611" + id.ToString("D8", CultureInfo.InvariantCulture), 9600m, 8000m, 2000m, 500m, 900m,
        "ARNBSARI", "Synthetic Employee", "Riyadh", "Olaya", "Building 11");
    [Fact]
    public void GoldenFilesMatchBankColumnsAndReconcileExactly()
    {
        var b = Row(2) with { SalaryAmount = 5750m, BasicSalary = 5000m, HousingAllowance = 1000m, OtherEarnings = 250m, SalaryDeductions = 500m };
        var r = AnbConnectCsvGenerator.Generate(Header(), new[] { b, Row() }, 15350m);
        Assert.True(r.Ok); Assert.Equal(2, r.Files!.Count);
        var header = "batchNumber,batchType,molEstablishmentId,mainAccountNumber,creditValueDate,organizationName,organizationAddress1,organizationAddress2,organizationAddress3,paymentCount,totalPayrollAmount,narrative,companyName\r\n" +
          "2026090001,PAYROLL,1234-5,0108061198800026,260930,Example Employer,Riyadh,Olaya,King Fahd Road,2,15350,September payroll,Example LLC\r\n";
        var body = "employeeId,employeeAccountNumber,salaryAmount,basicSalary,housingAllowance,otherEarnings,salaryDeductions,bicCode,employeeName,employeeAddress1,employeeAddress2,employeeAddress3\r\n" +
          "1000000001,0108061100000001,9600,8000,2000,500,900,ARNBSARI,Synthetic Employee,Riyadh,Olaya,Building 11\r\n" +
          "1000000002,0108061100000002,5750,5000,1000,250,500,ARNBSARI,Synthetic Employee,Riyadh,Olaya,Building 11\r\n";
        Assert.Equal(Encoding.UTF8.GetBytes(header), r.Files.Single(f => f.Name == "header.csv").Content);
        Assert.Equal(Encoding.UTF8.GetBytes(body), r.Files.Single(f => f.Name == "body.csv").Content);
    }
    [Fact]
    public void IdenticalInputIsStableUnderArabicCulture()
    {
        var expected = AnbConnectCsvGenerator.Generate(Header(), new[] { Row() }, 9600m);
        var prior = CultureInfo.CurrentCulture;
        try {
            CultureInfo.CurrentCulture = new CultureInfo("ar-SA");
            var actual = AnbConnectCsvGenerator.Generate(Header(), new[] { Row() }, 9600m);
            Assert.True(actual.Ok);
            for (var i = 0; i < 2; i++) { Assert.Equal(expected.Files![i].Content, actual.Files![i].Content); Assert.Equal(expected.Files[i].Sha256, actual.Files[i].Sha256); }
        } finally { CultureInfo.CurrentCulture = prior; }
    }
    [Fact]
    public void MissingBeneficiaryAddressRefusesWholeFile()
    {
        var r = AnbConnectCsvGenerator.Generate(Header(), new[] { Row() with { Address1 = null, Address2 = null, Address3 = null } }, 9600m);
        Assert.False(r.Ok); Assert.Null(r.Files); Assert.Contains(r.Errors, e => e.Code == "employee_address_missing");
    }
    [Theory]
    [InlineData("=HYPERLINK(\"unsafe\")")]
    [InlineData("+unsafe")]
    [InlineData("@unsafe")]
    [InlineData("-unsafe")]
    public void FormulaPrefixIsBlockedNotSilentlyChanged(string name)
    {
        var r = AnbConnectCsvGenerator.Generate(Header(), new[] { Row() with { EmployeeName = name } }, 9600m);
        Assert.False(r.Ok); Assert.Null(r.Files); Assert.Contains(r.Errors, e => e.Code == "field_formula_prefix");
    }
    [Fact]
    public void RejectsUnreconciledPrecisionAndUnknownFormat()
    {
        var bad = Row() with { SalaryAmount = 9600.001m };
        var r = AnbConnectCsvGenerator.Generate(Header(), new[] { bad }, bad.SalaryAmount);
        Assert.False(r.Ok); Assert.Null(r.Files); Assert.Contains(r.Errors, e => e.Code == "amount_precision");
        Assert.Null(SaudiBankExportFormats.Find("all-saudi-banks"));
        Assert.Null(SaudiBankExportFormats.Find("ANB-CONNECT-CSV-V1"));
        Assert.Single(SaudiBankExportFormats.Supported);
    }
    [Fact]
    public void Synthetic250EmployeeFileIncludesEveryRowExactlyOnce()
    {
        var rows = Enumerable.Range(1, 250).Select(Row).Reverse().ToArray();
        var r = AnbConnectCsvGenerator.Generate(Header(), rows, 2400000m);
        Assert.True(r.Ok); Assert.Equal(250, r.PaymentCount); Assert.Equal(2400000m, r.TotalAmount);
        var lines = Encoding.UTF8.GetString(r.Files!.Single(f => f.Name == "body.csv").Content).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(251, lines.Length); Assert.Equal(250, lines.Skip(1).Select(x => x.Split(',')[0]).Distinct().Count());
    }
    [Fact]
    public void ACommaAndQuoteAreEscapedWithoutChangingBeneficiaryName()
    {
        var r = AnbConnectCsvGenerator.Generate(Header(), new[] { Row() with { EmployeeName = "Example, \"Name\"" } }, 9600m);
        Assert.True(r.Ok);
        Assert.Contains(",\"Example, \"\"Name\"\"\",", Encoding.UTF8.GetString(r.Files!.Single(f => f.Name == "body.csv").Content));
    }
}
