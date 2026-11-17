using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports;

namespace Zayra.Api.Tests;

/// <summary>
/// CTO gate 8.4.5: the ANB credit value date. A Friday or Saturday (the Saudi bank weekend) and a date before today in
/// Riyadh are refused, and until a bank-holiday calendar source exists every file carries the warning "check bank
/// holidays". "Today" is an input to the pure generator; the export service reads it from its clock on Riyadh time.
/// Pinned golden bytes prove the checks change nothing in a valid file.
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

    /// <summary>Tuesday 17 November 2026 in Riyadh.</summary>
    private static readonly DateOnly Today = new(2026, 11, 17);

    private static AnbValueDateRules Rules(ISaudiBankHolidayCalendar? holidays = null) =>
        new(Today, holidays ?? NoSaudiBankHolidayCalendar.Instance);

    private static AnbGenerationResult Generate(DateOnly valueDate, ISaudiBankHolidayCalendar? holidays = null) =>
        AnbConnectCsvGenerator.Generate(Header(valueDate), new[] { Row() }, 9600m, Rules(holidays));

    [Theory]
    [InlineData(2026, 11, 20)]   // Friday
    [InlineData(2026, 11, 21)]   // Saturday
    [InlineData(2026, 11, 27)]   // Friday
    public void AFridayOrSaturdayValueDate_IsRefused_AndNoFileIsMade(int y, int m, int d)
    {
        var r = Generate(new DateOnly(y, m, d));

        r.Ok.Should().BeFalse();
        r.Files.Should().BeNull();
        r.Errors.Should().ContainSingle(e => e.Code == AnbConnectCsvGenerator.ValueDateWeekend)
            .Which.Field.Should().Be("paymentDate");
    }

    [Theory]
    [InlineData(2026, 11, 22)]   // Sunday
    [InlineData(2026, 11, 23)]
    [InlineData(2026, 11, 24)]
    [InlineData(2026, 11, 25)]
    [InlineData(2026, 11, 26)]   // Thursday
    public void ASundayToThursdayValueDate_IsAccepted(int y, int m, int d) =>
        Generate(new DateOnly(y, m, d)).Ok.Should().BeTrue();

    [Theory]
    [InlineData(2026, 11, 16)]   // yesterday, a Monday
    [InlineData(2025, 11, 17)]
    public void APastValueDate_IsRefused(int y, int m, int d)
    {
        var r = Generate(new DateOnly(y, m, d));

        r.Files.Should().BeNull();
        r.Errors.Should().ContainSingle(e => e.Code == AnbConnectCsvGenerator.ValueDateInPast)
            .Which.Field.Should().Be("paymentDate");
    }

    [Fact]
    public void TodayAsTheValueDate_IsAccepted() => Generate(Today).Ok.Should().BeTrue();

    [Theory]
    [InlineData(2026, 11, 26)]   // valid
    [InlineData(2026, 11, 20)]   // refused (weekend)
    [InlineData(2026, 11, 16)]   // refused (past)
    public void WithoutAHolidayCalendar_EveryResultSaysCheckBankHolidays(int y, int m, int d)
    {
        var warning = Generate(new DateOnly(y, m, d)).Warnings
            .Should().ContainSingle(w => w.Code == AnbConnectCsvGenerator.ValueDateCheckBankHolidays).Subject;
        warning.Message.Should().Contain("bank holiday");
    }

    [Fact]
    public void ACalendarThatKnowsTheDate_DecidesIt()
    {
        var holiday = Generate(new DateOnly(2026, 11, 26), new FixedCalendar(SaudiBankHolidayAnswer.BankHoliday));
        holiday.Files.Should().BeNull();
        holiday.Errors.Should().Contain(e => e.Code == AnbConnectCsvGenerator.ValueDateBankHoliday);

        var bankingDay = Generate(new DateOnly(2026, 11, 26), new FixedCalendar(SaudiBankHolidayAnswer.BankingDay));
        bankingDay.Ok.Should().BeTrue();
        bankingDay.Warnings.Should().NotContain(w => w.Code == AnbConnectCsvGenerator.ValueDateCheckBankHolidays);
    }

    [Fact]
    public void EveryValueDateMessage_HasAnArabicText()
    {
        foreach (var code in new[]
                 {
                     AnbConnectCsvGenerator.ValueDateWeekend, AnbConnectCsvGenerator.ValueDateInPast,
                     AnbConnectCsvGenerator.ValueDateBankHoliday, AnbConnectCsvGenerator.ValueDateCheckBankHolidays,
                 })
        {
            var text = AnbConnectCsvGenerator.ValueDateTexts[code];
            text.English.Should().NotBeNullOrWhiteSpace();
            text.Arabic.Should().MatchRegex("[\u0600-\u06FF]");
        }
    }

    // Package F5 (officer wording only): the refusal says what to choose, and the Arabic travels with the English.
    [Fact]
    public void TheWeekendRefusal_TellsTheOfficerWhichDayToChoose_InBothLanguages()
    {
        var error = Generate(new DateOnly(2026, 11, 20)).Errors.Single(e => e.Code == AnbConnectCsvGenerator.ValueDateWeekend);

        error.Message.Should().Be("The credit value date 2026-11-20 falls on the Saudi bank weekend (Friday or Saturday). Choose a working day, Sunday to Thursday. "
            + "If the salary is due on a weekend or holiday, choose the last working day before it so the salary is not paid late.");
        error.MessageAr.Should().Contain("2026-11-20").And.Contain("اختر يوم عمل من الأحد إلى الخميس").And.Contain("حتى لا يتأخر دفع الراتب");
    }

    [Fact]
    public void EveryValueDateIssue_CarriesItsArabicText_NextToTheEnglish()
    {
        var past = Generate(new DateOnly(2026, 11, 16));
        var weekend = Generate(new DateOnly(2026, 11, 21));
        var holiday = Generate(new DateOnly(2026, 11, 26), new FixedCalendar(SaudiBankHolidayAnswer.BankHoliday));
        var warned = Generate(new DateOnly(2026, 11, 26));

        foreach (var issue in past.Errors.Concat(weekend.Errors).Concat(holiday.Errors))
            issue.MessageAr.Should().MatchRegex("[\u0600-\u06FF]", issue.Code);
        foreach (var warning in warned.Warnings.Where(w => w.Code == AnbConnectCsvGenerator.ValueDateCheckBankHolidays))
            warning.MessageAr.Should().MatchRegex("[\u0600-\u06FF]", warning.Code);
        past.Errors.Single(e => e.Code == AnbConnectCsvGenerator.ValueDateInPast).MessageAr.Should().Contain("2026-11-16").And.Contain("2026-11-17");
    }

    [Fact]
    public void TheHolidayWarningArabic_SaysTheCalendarIsNotSetUp_NotThatItWasNotCounted()
    {
        var text = AnbConnectCsvGenerator.ValueDateTexts[AnbConnectCsvGenerator.ValueDateCheckBankHolidays];

        text.Arabic.Should().Contain("لم يُضبط تقويم لعطل البنوك السعودية").And.NotContain("لم يُعدّ");
    }

    [Fact]
    public void WithoutRules_TheGeneratorRefusesToRun()
    {
        var act = () => AnbConnectCsvGenerator.Generate(Header(new DateOnly(2026, 11, 26)), new[] { Row() }, 9600m, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ── The export service reads "today" on Riyadh time from its clock ─────────────────────────────

    [Fact]
    public async Task TheService_ReadsTodayInRiyadh_SoJustAfterMidnightRiyadhYesterdayIsPast()
    {
        // 21:30 UTC on Tuesday 17 November is 00:30 on Wednesday 18 November in Riyadh.
        var (svc, tenant, batch) = await ArrangeService(new DateTimeOffset(2026, 11, 17, 21, 30, 0, TimeSpan.Zero));

        var past = (await svc.ValidateAsync(tenant, batch, Request("2026-11-17"), default)).Value!;
        past.CanExport.Should().BeFalse();
        past.Errors.Should().Contain(e => e.Code == AnbConnectCsvGenerator.ValueDateInPast);
        past.Warnings.Should().NotContain(w => w.Code == "payment_date_in_past", "the ANB file refuses it; one message, not two");

        var today = (await svc.ValidateAsync(tenant, batch, Request("2026-11-18"), default)).Value!;
        today.CanExport.Should().BeTrue(string.Join("; ", today.Errors.Select(e => e.Code)));
        today.Warnings.Should().Contain(w => w.Code == AnbConnectCsvGenerator.ValueDateCheckBankHolidays);
    }

    [Fact]
    public async Task TheService_RefusesAWeekendValueDate()
    {
        var (svc, tenant, batch) = await ArrangeService(new DateTimeOffset(2026, 11, 17, 9, 0, 0, TimeSpan.Zero));

        var v = (await svc.ValidateAsync(tenant, batch, Request("2026-11-20"), default)).Value!;

        v.CanExport.Should().BeFalse();
        v.Errors.Should().Contain(e => e.Code == AnbConnectCsvGenerator.ValueDateWeekend);
    }

    private static SaudiBankExportBatchRequest Request(string paymentDate) =>
        new() { BatchReference = "2026111701", PaymentDate = paymentDate };

    private static async Task<(SaudiBankExportService Svc, Guid Tenant, Guid Batch)> ArrangeService(DateTimeOffset utcNow)
    {
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenant = Guid.NewGuid();
        var company = await SaudiBankExportTestData.SeedCompanyAsync(db, tenant);
        var batch = await SaudiBankExportTestData.SeedBatchAsync(db, tenant, company, 11);
        var svc = new SaudiBankExportService(db, new SaudiBankExportTestData.FakeAddresses(), new FixedTime(utcNow));
        (await svc.SaveSettingsAsync(tenant, company, Guid.NewGuid(), SaudiBankExportTestData.Settings(), default))
            .Outcome.Should().Be(SaudiBankExportOutcome.Ok);
        return (svc, tenant, batch);
    }

    private sealed class FixedTime(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class FixedCalendar(SaudiBankHolidayAnswer answer) : ISaudiBankHolidayCalendar
    {
        public SaudiBankHolidayAnswer Check(DateOnly date) => answer;
    }

    [Fact]
    public void AValidWeekdayFutureDate_GivesTheSameBytesAsBefore()
    {
        // Thursday 26 November 2026.
        var r = AnbConnectCsvGenerator.Generate(Header(new DateOnly(2026, 11, 26)), new[] { Row() }, 9600m, Rules());

        r.Ok.Should().BeTrue(string.Join("; ", r.Errors.Select(e => e.Code)));
        var header = r.Files!.Single(f => f.Name == AnbConnectCsvGenerator.HeaderFileName);
        var body = r.Files!.Single(f => f.Name == AnbConnectCsvGenerator.BodyFileName);
        header.Content.Should().Equal(Encoding.UTF8.GetBytes(GoldenHeader));
        body.Content.Should().Equal(Encoding.UTF8.GetBytes(GoldenBody));
        header.Sha256.Should().Be(GoldenHeaderSha256);
        body.Sha256.Should().Be(GoldenBodySha256);
    }
}
