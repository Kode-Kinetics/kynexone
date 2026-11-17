using System.Text;
using FluentAssertions;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports;

namespace Zayra.Api.Tests;

/// <summary>
/// CTO gate 8.4.5: the ANB credit value date. Pinned golden bytes prove the value-date checks change nothing in a valid
/// file.
/// </summary>
public class AnbValueDateTests
{
    private const string GoldenHeader =
        "batchNumber,batchType,molEstablishmentId,mainAccountNumber,creditValueDate,organizationName,organizationAddress1,organizationAddress2,organizationAddress3,paymentCount,totalPayrollAmount,narrative,companyName\r\n"
        + "2026112601,PAYROLL,7001234567,0108061198800026,261126,Example Employer,Riyadh,Olaya,King Fahd Road,1,9600,November payroll,Example LLC\r\n";
    private const string GoldenBody =
        "employeeId,employeeAccountNumber,salaryAmount,basicSalary,housingAllowance,otherEarnings,salaryDeductions,bicCode,employeeName,employeeAddress1,employeeAddress2,employeeAddress3\r\n"
        + "1000000001,0108061100000001,9600,8000,2000,500,900,ARNBSARI,Synthetic Employee,Riyadh,Olaya,Building 11\r\n";
    private const string GoldenHeaderSha256 = "deecd7b546d6cee112965bde016e6d56005c649230d27c5e7f944e200ee7230e";
    private const string GoldenBodySha256 = "cf476813628cfede2c0c2e7cb399549721643005d7ab3d3e767949132cdc2d7a";

    private static AnbHeaderInput Header(DateOnly valueDate) => new("2026112601", "PAYROLL", "7001234567", "0108061198800026",
        valueDate, "Example Employer", "Riyadh", "Olaya", "King Fahd Road", "November payroll", "Example LLC");

    private static AnbPaymentInput Row() => new(1, "TEST1", "1000000001", "0108061100000001", 9600m, 8000m, 2000m, 500m, 900m,
        "ARNBSARI", "Synthetic Employee", "Riyadh", "Olaya", "Building 11");

    [Fact]
    public void AValidWeekdayFutureDate_GivesTheSameBytesAsBefore()
    {
        // Thursday 26 November 2026.
        var r = AnbConnectCsvGenerator.Generate(Header(new DateOnly(2026, 11, 26)), new[] { Row() }, 9600m);

        r.Ok.Should().BeTrue(string.Join("; ", r.Errors.Select(e => e.Code)));
        var header = r.Files!.Single(f => f.Name == AnbConnectCsvGenerator.HeaderFileName);
        var body = r.Files!.Single(f => f.Name == AnbConnectCsvGenerator.BodyFileName);
        header.Content.Should().Equal(Encoding.UTF8.GetBytes(GoldenHeader));
        body.Content.Should().Equal(Encoding.UTF8.GetBytes(GoldenBody));
        header.Sha256.Should().Be(GoldenHeaderSha256);
        body.Sha256.Should().Be(GoldenBodySha256);
    }
}
