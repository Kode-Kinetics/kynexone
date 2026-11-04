using FluentAssertions;
using Zayra.Api.Application.Common;
using Zayra.Api.Infrastructure.Attendance;

namespace Zayra.Api.Tests;

/// <summary>
/// B2 — THE reader of an attendance punch file (shared by preview and import): columns by name or position, three delimiters,
/// timestamps in the tenant's zone unless they carry their own, day-first dates but never month-first, strict directions, and
/// per-row errors that name the real line of the file the user opened.
/// </summary>
public sealed class AttendancePunchCsvTests
{
    private static readonly TimeZoneInfo Riyadh = TimeZoneInfo.FindSystemTimeZoneById("Asia/Riyadh");

    private static ParsedPunchRow Only(string csv, TimeZoneInfo? tz = null) =>
        Assert.Single(AttendancePunchCsv.Parse(csv, tz ?? Riyadh).Rows);

    // ── the template ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheTemplate_IsHeaderPlusOneExampleRow_OfTheSameWidth_AndReadsBackAsAValidRow()
    {
        var csv = Csv.Template(AttendancePunchCsv.TemplateHeaders, AttendancePunchCsv.TemplateExample);

        Csv.SplitRow(csv.Split('\n')[0]).Should().Equal("employeeCode", "punchTimestamp", "punchDirection", "location", "method");
        var row = Only(csv);
        row.Error.Should().BeNull("the example in the template must itself be importable");
        row.EmployeeCode.Should().Be("EMP-001");
        row.PunchUtc.Should().Be(new DateTime(2026, 6, 16, 6, 0, 0, DateTimeKind.Utc), "09:00 in Riyadh");
        row.Direction.Should().Be("In");
    }

    // ── columns ──────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Employee Code,Date Time,Type,Location,Verification Method")]
    [InlineData("EMPLOYEECODE,PUNCHTIMESTAMP,PUNCHDIRECTION,LOCATION,METHOD")]
    [InlineData("emp code,timestamp,direction,location,method")]
    [InlineData("code,punch time,in/out,location,verificationmethod")]
    [InlineData("employee_code,punch_timestamp,punch_direction,location,method")]
    public void Columns_AreFoundByName_IgnoringCaseSpacesAndAliases(string header)
    {
        var row = Only(header + "\nE-1,2026-06-16 09:00,In,HQ,Manual\n");

        row.Error.Should().BeNull();
        (row.EmployeeCode, row.Direction, row.Location, row.Method).Should().Be(("E-1", "In", "HQ", "Manual"));
    }

    [Fact]
    public void Columns_CanBeInAnyOrder_AndExtraColumnsAreIgnored()
    {
        var row = Only("notes,Direction,Timestamp,Code\nlate bus,Out,2026-06-16 17:30,E-7\n");

        row.Error.Should().BeNull();
        (row.EmployeeCode, row.Direction, row.PunchUtc).Should().Be(("E-7", "Out", new DateTime(2026, 6, 16, 14, 30, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void WithNoRecognisedHeader_ColumnsArePositional_AndTheFirstLineIsData()
    {
        var file = AttendancePunchCsv.Parse("E-1,2026-06-16 09:00,In,HQ,Manual\nE-2,2026-06-16 09:05,Out\n", Riyadh);

        file.Rows.Should().HaveCount(2);
        file.Rows[0].RowNumber.Should().Be(1);
        file.Rows[0].EmployeeCode.Should().Be("E-1");
        file.Rows[1].Error.Should().BeNull("location and method are optional");
        file.Rows[1].RowNumber.Should().Be(2);
    }

    [Fact]
    public void AHeaderMissingARequiredColumn_RefusesTheWholeFile_NamingIt()
    {
        var act = () => AttendancePunchCsv.Parse("employeeCode,punchTimestamp,location\nE-1,2026-06-16 09:00,HQ\n", Riyadh);

        act.Should().Throw<AttendanceImportFileException>().WithMessage("*punchDirection*");
    }

    // ── delimiters, BOM, quoting ─────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(';')]
    [InlineData('\t')]
    [InlineData(',')]
    public void TheDelimiter_IsDetected(char delimiter)
    {
        string J(params string[] cells) => string.Join(delimiter, cells);
        var csv = J("employeeCode", "punchTimestamp", "punchDirection", "location", "method") + "\n" + J("E-1", "2026-06-16 09:00", "In", "HQ", "Manual") + "\n";

        var row = Only(csv);

        row.Error.Should().BeNull();
        (row.EmployeeCode, row.Location, row.Method).Should().Be(("E-1", "HQ", "Manual"));
    }

    [Fact]
    public void ABom_AndWindowsLineEndings_AreHandled()
    {
        var row = Only("﻿employeeCode,punchTimestamp,punchDirection\r\nE-1,2026-06-16 09:00,In\r\n");

        row.Error.Should().BeNull();
        row.EmployeeCode.Should().Be("E-1");
    }

    [Fact]
    public void AQuotedValueMayContainTheDelimiter_AndATooWideRowIsAnErrorNotAShiftedRow()
    {
        var file = AttendancePunchCsv.Parse(
            "employeeCode,punchTimestamp,punchDirection,location,method\n" +
            "E-1,2026-06-16 09:00,In,\"Riyadh, HQ\",Manual\n" +
            "E-2,2026-06-16 09:00,In,Riyadh, HQ,Manual\n", Riyadh);

        file.Rows[0].Location.Should().Be("Riyadh, HQ");
        file.Rows[1].Error.Should().Contain("6 values").And.Contain("header has 5");
    }

    // ── timestamps ───────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("2026-06-16T06:00:00Z", 6, 0)]                 // explicit Z: an instant, honoured
    [InlineData("2026-06-16T09:00:00+03:00", 6, 0)]            // explicit offset
    [InlineData("2026-06-16T05:00:00-01:00", 6, 0)]
    [InlineData("2026-06-16 09:00", 6, 0)]                     // zone-less: Riyadh wall clock
    [InlineData("2026-06-16 09:00:30", 6, 0)]
    [InlineData("2026-06-16T09:00", 6, 0)]
    [InlineData("2026-06-16T09:00:00", 6, 0)]
    [InlineData("2026-6-16 9:00", 6, 0)]
    [InlineData("16/06/2026 09:00", 6, 0)]                     // day-first
    [InlineData("16/06/2026 09:00:00", 6, 0)]
    [InlineData("16-06-2026 09:00", 6, 0)]
    [InlineData("16-6-2026 9:00", 6, 0)]
    [InlineData("6/6/2026 9:00", 6, 0)]
    public void Timestamps_ZonelessIsTenantTime_ExplicitZonesAreHonoured(string text, int hourUtc, int minute)
    {
        AttendancePunchCsv.TryParseTimestamp(text, Riyadh, out var utc, out var error).Should().BeTrue(error);

        utc.Day.Should().Be(text.Contains("6/6/") ? 6 : 16);
        utc.Hour.Should().Be(hourUtc);
        utc.Minute.Should().Be(minute);
        utc.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void Timestamps_AreNeverReadMonthFirst()
    {
        // 06/16/2026 would be June 16th month-first: it is refused rather than guessed.
        AttendancePunchCsv.TryParseTimestamp("06/16/2026 09:00", Riyadh, out _, out var error).Should().BeFalse();
        error.Should().Contain("06/16/2026 09:00").And.Contain("day/month/year");

        // 01/02/2026 is the 1st of February, day-first.
        AttendancePunchCsv.TryParseTimestamp("01/02/2026 09:00", Riyadh, out var utc, out _).Should().BeTrue();
        (utc.Month, utc.Day).Should().Be((2, 1));
    }

    [Theory]
    [InlineData("")]
    [InlineData("yesterday")]
    [InlineData("2026-06-16")]            // a date without a time
    [InlineData("09:00")]
    [InlineData("2026-13-01 09:00")]
    [InlineData("2026-06-31 09:00")]
    [InlineData("16/06/2026 9:00 AM")]
    [InlineData("2026-06-16 25:00")]
    public void Timestamps_ThatAreNotADateAndTime_AreRefusedWithAnExample(string text)
    {
        AttendancePunchCsv.TryParseTimestamp(text, Riyadh, out _, out var error).Should().BeFalse();

        error.Should().Contain("2026-06-16 09:00");
    }

    [Fact]
    public void ALocalTimeTheClocksSkip_IsRefused_NotShifted()
    {
        var newYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

        AttendancePunchCsv.TryParseTimestamp("2026-03-08 02:30", newYork, out _, out var error).Should().BeFalse();
        error.Should().Contain("does not exist");
        AttendancePunchCsv.TryParseTimestamp("2026-03-08 03:30", newYork, out var utc, out _).Should().BeTrue();
        utc.Should().Be(new DateTime(2026, 3, 8, 7, 30, 0, DateTimeKind.Utc));
    }

    // ── directions ───────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("In", "In")]
    [InlineData("in", "In")]
    [InlineData("IN", "In")]
    [InlineData("Check-in", "In")]
    [InlineData("check in", "In")]
    [InlineData("Clock_In", "In")]
    [InlineData("Out", "Out")]
    [InlineData("OUT", "Out")]
    [InlineData("Check-out", "Out")]
    [InlineData("BreakIn", "BreakIn")]
    [InlineData("Break In", "BreakIn")]
    [InlineData("break-in", "BreakIn")]
    [InlineData("BreakOut", "BreakOut")]
    [InlineData("Break_Out", "BreakOut")]
    public void Directions_IgnoreCaseSpacesHyphensAndUnderscores(string text, string expected) =>
        AttendancePunchCsv.ParseDirection(text).Should().Be(expected);

    [Theory]
    [InlineData("Late")]
    [InlineData("Unknown")]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("Inn")]
    public void AnythingElse_IsARowError_NotASilentUnknown(string text)
    {
        AttendancePunchCsv.ParseDirection(text).Should().BeNull();

        var row = Only($"employeeCode,punchTimestamp,punchDirection\nE-1,2026-06-16 09:00,{text}\n");
        row.Error.Should().NotBeNull();
        row.Column.Should().Be("punchDirection");
        row.Error.Should().Contain("In, Out, BreakIn or BreakOut");
    }

    // ── rows and errors ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RowNumbers_AreTheRealLinesOfTheFile_BlankLinesIncluded()
    {
        var file = AttendancePunchCsv.Parse(
            "employeeCode,punchTimestamp,punchDirection\n" +   // 1
            "E-1,2026-06-16 09:00,In\n" +                      // 2
            "\n" +                                             // 3 (blank)
            "E-2,not a time,In\n" +                            // 4
            ",2026-06-16 09:00,In\n" +                         // 5
            "E-4,2026-06-16 09:00,Late\n" +                    // 6
            "E-5,2026-06-16 17:00,Out\n", Riyadh);             // 7

        file.Rows.Select(r => r.RowNumber).Should().Equal(2, 4, 5, 6, 7);
        file.Rows.Select(r => r.Column).Should().Equal(new string?[] { null, "punchTimestamp", "employeeCode", "punchDirection", null });
        file.Rows.Where(r => r.Error is not null).Should().OnlyContain(r => r.RawRow.Length > 0);
        file.Rows[1].Error.Should().Contain("not a time");
        file.Rows[2].Error.Should().Contain("employee code is missing");
    }

    [Fact]
    public void AMissingTimestamp_OrDirection_NamesTheColumn()
    {
        var file = AttendancePunchCsv.Parse("employeeCode,punchTimestamp,punchDirection\nE-1,,In\nE-2,2026-06-16 09:00,\n", Riyadh);

        file.Rows[0].Column.Should().Be("punchTimestamp");
        file.Rows[1].Column.Should().Be("punchDirection");
    }

    // ── whole-file refusals ──────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   \n\n  ")]
    public void AnEmptyFile_IsRefusedWithAnExplanation(string csv)
    {
        var act = () => AttendancePunchCsv.Parse(csv, Riyadh);

        act.Should().Throw<AttendanceImportFileException>().WithMessage("*empty*template*");
    }

    [Fact]
    public void AHeaderOnlyFile_IsRefusedWithAnExplanation()
    {
        var act = () => AttendancePunchCsv.Parse("employeeCode,punchTimestamp,punchDirection\n\n", Riyadh);

        act.Should().Throw<AttendanceImportFileException>().WithMessage("*no punch rows*");
    }

    [Fact]
    public void AFileOverTheRowCap_IsRefusedWhole_AndTheCapItselfIsAccepted()
    {
        string File(int rows) => "employeeCode,punchTimestamp,punchDirection\n" +
            string.Join("\n", Enumerable.Range(0, rows).Select(i => $"E-{i},2026-06-16 09:00,In"));

        AttendancePunchCsv.Parse(File(AttendancePunchCsv.MaxRows), Riyadh).Rows.Should().HaveCount(5000);
        var act = () => AttendancePunchCsv.Parse(File(AttendancePunchCsv.MaxRows + 1), Riyadh);

        act.Should().Throw<AttendanceImportFileException>().WithMessage("*5,000*5,001*");
    }

    [Fact]
    public void TheSeedFiles_ThreeColumnHeader_IsoZTimestamps_StillRead()
    {
        // e2e/bootstrap/provision.ts and e2e/release-a/demo-seed.ts send exactly this shape.
        var file = AttendancePunchCsv.Parse(
            "employeeCode,punchTimestamp,punchDirection\nE-1,2026-10-01T05:00:00Z,In\nE-1,2026-10-01T14:00:00Z,Out", Riyadh);

        file.Rows.Should().OnlyContain(r => r.Error == null);
        file.Rows.Select(r => r.PunchUtc!.Value.Hour).Should().Equal(5, 14);
    }
}
