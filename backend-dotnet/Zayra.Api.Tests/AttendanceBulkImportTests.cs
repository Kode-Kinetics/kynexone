using System.Reflection;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Infrastructure.Attendance;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Models;
using static Zayra.Api.Tests.BulkKit;

namespace Zayra.Api.Tests;

/// <summary>
/// B2 — the template, the dry run and the import share ONE judging routine. The invariants this feature must never break
/// (I1 no self-attendance, I2 scope without disclosure, I3 active only, I4 reserved labels, I5 payroll lock, I6 no future punches,
/// I7 idempotent, I8 audit, I10 DTOs) are each pinned per ROW: a bad row is reported and skipped and the rest of the file proceeds.
/// </summary>
public sealed class AttendanceBulkImportTests
{
    private static ImportAttendanceRequest Request(string csv, string name = "punches.csv") => new(name, csv);

    // ── preview writes nothing; the counts add up ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Preview_OnAMixedFile_WritesNothing_AndTheCountsAddUp()
    {
        var k = await CreateAsync();
        // One punch is already on record (08:00 local = 05:00Z).
        k.Db.AttendanceRawEvents.Add(new AttendanceRawEvent { TenantId = k.TenantId, EmployeeId = k.Report.Id, EmployeeCode = k.Report.EmployeeCode, PunchDirection = "In", PunchTimestampUtc = new DateTime(2026, 6, 10, 5, 0, 0, DateTimeKind.Utc) });
        await k.Db.SaveChangesAsync();
        var csv = File(
            Line("E-REPORT", "2026-06-10 08:00"),       // line 2: already on record -> duplicate
            Line("E-REPORT", "2026-06-10 17:00", "Out"), // line 3: valid
            Line("E-STRANGER", "2026-06-10 08:00"),      // line 4: valid
            Line("E-STRANGER", "2026-06-10 08:00"),      // line 5: the same punch twice in the file -> duplicate
            Line("E-NOPE", "2026-06-10 08:00"),          // line 6: unknown employee
            Line("E-STRANGER", "garbage"),               // line 7: unreadable time
            Line("E-STRANGER", "2026-06-10 08:00", "Late")); // line 8: not a direction
        var before = (await k.Db.AttendanceRawEvents.CountAsync(), await k.Db.AttendanceImportBatches.CountAsync(), await k.Db.AttendanceAuditLogs.CountAsync());

        var result = await k.Service().PreviewImportAsync(k.TenantId, Request(csv), k.Authority(), k.Context(), default);

        (await k.Db.AttendanceRawEvents.CountAsync(), await k.Db.AttendanceImportBatches.CountAsync(), await k.Db.AttendanceAuditLogs.CountAsync())
            .Should().Be(before, "a preview writes NOTHING: no punch, no batch, no audit row");
        result.BatchId.Should().BeNull();
        result.Status.Should().BeNull();
        result.ImportedRows.Should().Be(0);
        (result.TotalRows, result.ValidRows, result.DuplicateRows, result.FailedRows).Should().Be((7, 2, 2, 3));
        result.TotalRows.Should().Be(result.ValidRows + result.DuplicateRows + result.FailedRows, "validRows EXCLUDES duplicates");
        result.FileName.Should().Be("punches.csv");
        result.TimeZone.Should().Be("Asia/Riyadh");
        result.Errors.Select(e => e.RowNumber).Should().Equal(6, 7, 8);
        result.Errors.Select(e => e.Column).Should().Equal("employeeCode", "punchTimestamp", "punchDirection");
        result.Errors.Should().OnlyContain(e => e.RawRow.Length > 0 && e.Message.Length > 0);
    }

    [Fact]
    public async Task Preview_AndImport_JudgeTheSameFileIdentically()
    {
        var k = await CreateAsync();
        var csv = File(
            Line("E-REPORT", "2026-06-10 08:00"), Line("E-CALLER", "2026-06-10 08:00"), Line("E-OUT", "2026-06-10 08:00"),
            Line("E-GONE", "2026-06-10 08:00"), Line("E-STRANGER", "2026-06-21 08:00"), Line("E-STRANGER", "2026-06-10 08:00", "In", "HQ", "Selfie"));

        var preview = await k.Service().PreviewImportAsync(k.TenantId, Request(csv), k.Authority(k.Report.Id, k.Stranger.Id), k.Context(), default);
        var import = await k.Service().ImportCsvAsync(k.TenantId, Request(csv), k.Authority(k.Report.Id, k.Stranger.Id), k.Context(), default);

        import.Errors.Should().BeEquivalentTo(preview.Errors);
        (import.TotalRows, import.ValidRows, import.DuplicateRows, import.FailedRows).Should().Be((preview.TotalRows, preview.ValidRows, preview.DuplicateRows, preview.FailedRows));
        import.ImportedRows.Should().Be(preview.ValidRows);
    }

    // ── the import ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Import_StoresTheValidRows_InTenantTime_WithSourceLocationAndMethod_AndAnImportBatch()
    {
        var k = await CreateAsync();
        var csv = File(
            Line("E-REPORT", "2026-06-10 08:00", "In", "Head office", "Manual"),
            Line("E-REPORT", "16/06/2026 17:00", "Check-out"),   // day-first, other direction spelling
            Line("E-STRANGER", "2026-06-10T08:00:00Z"),          // explicit zone honoured
            Line("E-STRANGER", "garbage"));

        var result = await k.Service().ImportCsvAsync(k.TenantId, Request(csv, "june.csv"), k.Authority(), k.Context(), default);

        result.BatchId.Should().NotBeNull();
        result.Status.Should().Be("CompletedWithErrors");
        (result.TotalRows, result.ValidRows, result.ImportedRows, result.DuplicateRows, result.FailedRows).Should().Be((4, 3, 3, 0, 1));
        var events = k.Events();
        events.Should().HaveCount(3);
        var first = events.Single(e => e.EmployeeId == k.Report.Id && e.PunchDirection == "In");
        first.PunchTimestampUtc.Should().Be(new DateTime(2026, 6, 10, 5, 0, 0, DateTimeKind.Utc), "08:00 Riyadh");
        (first.Source, first.LocationName, first.VerificationMethod, first.EmployeeCode, first.CreatedBy)
            .Should().Be(("CSV import", "Head office", "Manual", "E-REPORT", k.UserId));
        first.SyncBatchReference.Should().Be(result.BatchId.ToString());
        events.Single(e => e.EmployeeId == k.Report.Id && e.PunchDirection == "Out").PunchTimestampUtc
            .Should().Be(new DateTime(2026, 6, 16, 14, 0, 0, DateTimeKind.Utc));
        events.Single(e => e.EmployeeId == k.Stranger.Id).PunchTimestampUtc.Should().Be(new DateTime(2026, 6, 10, 8, 0, 0, DateTimeKind.Utc));
        events.Single(e => e.EmployeeId == k.Stranger.Id).VerificationMethod.Should().Be("CSV");

        var batch = await k.Db.AttendanceImportBatches.SingleAsync();
        (batch.Id, batch.FileName, batch.Source, batch.TotalRows, batch.ImportedRows, batch.FailedRows, batch.Status, batch.CreatedBy)
            .Should().Be((result.BatchId!.Value, "june.csv", "CSV import", 4, 3, 1, "CompletedWithErrors", k.UserId));
        var persisted = await k.Db.AttendanceImportErrors.SingleAsync();
        (persisted.ImportBatchId, persisted.RowNumber, persisted.RawRow).Should().Be((batch.Id, 5, "E-STRANGER,garbage,In,,"));
        persisted.ErrorMessage.Should().Contain("garbage");
    }

    [Fact]
    public async Task Import_OfACleanFile_IsCompleted()
    {
        var k = await CreateAsync();

        var result = await k.Service().ImportCsvAsync(k.TenantId, Request(File(Line("E-REPORT", "2026-06-10 08:00"))), k.Authority(), k.Context(), default);

        result.Status.Should().Be("Completed");
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Import_WritesExactlyOneAuditRow_WithTheCountsTheCallerAndTheFile()
    {
        var k = await CreateAsync();
        var csv = File(Line("E-REPORT", "2026-06-10 08:00"), Line("E-STRANGER", "2026-06-10 08:00"), Line("E-REPORT", "2026-06-10 08:00"), Line("E-NOPE", "2026-06-10 08:00"));

        var result = await k.Service().ImportCsvAsync(k.TenantId, Request(csv, "audit-me.csv"), k.Authority(), k.Context(), default);

        var audit = await k.Db.AttendanceAuditLogs.SingleAsync(a => a.Action == "attendance.import.completed");
        (audit.UserId, audit.EntityName, audit.EntityId).Should().Be((k.UserId, "AttendanceImportBatch", result.BatchId.ToString()));
        audit.MetadataJson.Should().Contain("audit-me.csv").And.Contain("\"imported\":2").And.Contain("\"duplicates\":1").And.Contain("\"failed\":1");
        (await k.Db.AttendanceAuditLogs.CountAsync(a => a.Action.StartsWith("attendance.import"))).Should().Be(1, "one audit row per batch, not per punch");
    }

    [Fact]
    public async Task Import_OfTheSeedShape_ThreeColumnHeaderAndIsoZTimestamps_Works()
    {
        var k = await CreateAsync();
        var csv = "employeeCode,punchTimestamp,punchDirection\nE-REPORT,2026-06-10T05:00:00Z,In\nE-REPORT,2026-06-10T14:00:00Z,Out";

        var result = await k.Service().ImportCsvAsync(k.TenantId, Request(csv), k.Authority(), k.Context(), default);

        (result.ImportedRows, result.FailedRows).Should().Be((2, 0));
        k.Events().Select(e => e.PunchTimestampUtc.Hour).Should().Equal(5, 14);
    }

    // ── I7 idempotent ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ResendingTheSameFile_CreatesNoDuplicates_AndCountsThem()
    {
        var k = await CreateAsync();
        var csv = File(Line("E-REPORT", "2026-06-10 08:00"), Line("E-REPORT", "2026-06-10 17:00", "Out"), Line("E-STRANGER", "2026-06-10 08:00"));

        var first = await k.Service().ImportCsvAsync(k.TenantId, Request(csv), k.Authority(), k.Context(), default);
        var second = await k.Service().ImportCsvAsync(k.TenantId, Request(csv), k.Authority(), k.Context(), default);
        var preview = await k.Service().PreviewImportAsync(k.TenantId, Request(csv), k.Authority(), k.Context(), default);

        first.ImportedRows.Should().Be(3);
        (second.ValidRows, second.ImportedRows, second.DuplicateRows, second.FailedRows).Should().Be((0, 0, 3, 0), "duplicates are counted, not errors");
        second.Status.Should().Be("Completed");
        (preview.ValidRows, preview.DuplicateRows).Should().Be((0, 3));
        (await k.Db.AttendanceRawEvents.CountAsync()).Should().Be(3);
    }

    [Fact]
    public async Task APunchRepeatedWithinOneFile_IsStoredOnce_AndTheRepeatIsADuplicate()
    {
        var k = await CreateAsync();
        var csv = File(Line("E-REPORT", "2026-06-10 08:00"), Line("E-REPORT", "2026-06-10 08:00"), Line("E-REPORT", "2026-06-10T05:00:00Z"));

        var result = await k.Service().ImportCsvAsync(k.TenantId, Request(csv), k.Authority(), k.Context(), default);

        (result.ImportedRows, result.DuplicateRows).Should().Be((1, 2));
        (await k.Db.AttendanceRawEvents.CountAsync()).Should().Be(1);
    }

    // ── I1 / I2 / I3 per row ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheCallersOwnRow_IsRefusedPerRow_NothingIsWrittenForIt_AndTheRestProceeds()
    {
        var k = await CreateAsync();
        var csv = File(Line("E-REPORT", "2026-06-10 08:00"), Line("E-CALLER", "2026-06-10 08:00"), Line("E-STRANGER", "2026-06-10 08:00"));

        var result = await k.Service().ImportCsvAsync(k.TenantId, Request(csv), k.Authority(), k.Context(), default);

        (result.ImportedRows, result.FailedRows).Should().Be((2, 1));
        result.Errors.Should().ContainSingle().Which.Should().Match<AttendanceImportRowError>(e => e.RowNumber == 3 && e.Message.Contains("own"));
        k.Events().Should().NotContain(e => e.EmployeeId == k.Caller.Id);
        (await k.Db.AttendanceImportErrors.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task AnOutOfScopeRow_ReadsExactlyLikeAnUnknownCode_NothingIsWritten_AndTheRestProceeds()
    {
        var k = await CreateAsync();
        // The caller's scope is {Report}. E-STRANGER and E-OUT exist; E-NOPE does not.
        var csv = File(Line("E-REPORT", "2026-06-10 08:00"), Line("E-STRANGER", "2026-06-10 08:00"), Line("E-OUT", "2026-06-10 08:00"), Line("E-NOPE", "2026-06-10 08:00"));

        var result = await k.Service().ImportCsvAsync(k.TenantId, Request(csv), k.Authority(k.Report.Id), k.Context(), default);

        (result.ImportedRows, result.FailedRows).Should().Be((1, 3));
        var messages = result.Errors.Select(e => e.Message).Distinct().ToList();
        messages.Should().ContainSingle("an out-of-scope code must be indistinguishable from an unknown one, so the error doesn't reveal that the employee exists");
        result.Errors.Select(e => e.RowNumber).Should().Equal(3, 4, 5);
        k.Events().Should().ContainSingle().Which.EmployeeId.Should().Be(k.Report.Id);
    }

    [Fact]
    public async Task AnEmptyScopeSet_WritesForNobody_NeverEverybody()
    {
        var k = await CreateAsync();

        var result = await k.Service().ImportCsvAsync(k.TenantId, Request(File(Line("E-REPORT", "2026-06-10 08:00"))),
            new AttendanceBulkAuthority(Array.Empty<int>(), k.Caller.Id), k.Context(), default);

        (result.ImportedRows, result.FailedRows).Should().Be((0, 1));
        k.Events().Should().BeEmpty();
    }

    [Fact]
    public async Task AnInactiveEmployee_IsARowError_WithTheSameMessageAsAnUnknownCode()
    {
        var k = await CreateAsync();

        var result = await k.Service().ImportCsvAsync(k.TenantId, Request(File(Line("E-GONE", "2026-06-10 08:00"), Line("E-NOPE", "2026-06-10 08:00"))), k.Authority(), k.Context(), default);

        result.FailedRows.Should().Be(2);
        result.Errors.Select(e => e.Message).Distinct().Should().ContainSingle();
        k.Events().Should().BeEmpty();
    }

    // ── I4 reserved labels ───────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Selfie")]
    [InlineData("geofence")]
    [InlineData("Selfie+Geofence")]
    public async Task AReservedVerificationLabel_IsRefusedPerRow(string method)
    {
        var k = await CreateAsync();
        var csv = File(Line("E-REPORT", "2026-06-10 08:00", "In", "HQ", method), Line("E-STRANGER", "2026-06-10 08:00", "In", "HQ", "Fingerprint"));

        var result = await k.Service().ImportCsvAsync(k.TenantId, Request(csv), k.Authority(), k.Context(), default);

        (result.ImportedRows, result.FailedRows).Should().Be((1, 1));
        result.Errors.Single().Message.Should().Contain("can only be recorded by a punch from the KynexOne app");
        result.Errors.Single().Column.Should().Be("method");
        k.Events().Should().ContainSingle().Which.VerificationMethod.Should().Be("Fingerprint");
    }

    // ── I5 payroll lock ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task APunchInATenantWideLockedPeriod_IsRefusedPerRow_AndOtherDaysProceed()
    {
        var k = await CreateAsync();
        k.Db.AttendanceLockPeriods.Add(new AttendanceLockPeriod { TenantId = k.TenantId, PeriodStart = new DateOnly(2026, 5, 1), PeriodEnd = new DateOnly(2026, 5, 31), Status = "Locked", LockType = "Payroll" });
        await k.Db.SaveChangesAsync();
        var csv = File(Line("E-REPORT", "2026-05-20 08:00"), Line("E-REPORT", "2026-06-10 08:00"));

        var result = await k.Service().ImportCsvAsync(k.TenantId, Request(csv), k.Authority(), k.Context(), default);

        (result.ImportedRows, result.FailedRows).Should().Be((1, 1));
        result.Errors.Single().Should().Match<AttendanceImportRowError>(e => e.RowNumber == 2 && e.Message.Contains("2026-05-20") && e.Message.Contains("locked"));
        k.Events().Should().ContainSingle().Which.PunchTimestampUtc.Month.Should().Be(6);
    }

    [Fact]
    public async Task APunchOnADayThatPayrollLockedForThatEmployee_IsRefusedPerRow_ButNotForOthers()
    {
        var k = await CreateAsync();
        k.Db.AttendanceDailyRecords.Add(new AttendanceDailyRecord { TenantId = k.TenantId, EmployeeId = k.Report.Id, WorkDate = Wednesday, IsPayrollLocked = true });
        await k.Db.SaveChangesAsync();
        var csv = File(Line("E-REPORT", "2026-06-10 08:00"), Line("E-STRANGER", "2026-06-10 08:00"));

        var result = await k.Service().ImportCsvAsync(k.TenantId, Request(csv), k.Authority(), k.Context(), default);

        (result.ImportedRows, result.FailedRows).Should().Be((1, 1));
        k.Events().Should().ContainSingle().Which.EmployeeId.Should().Be(k.Stranger.Id);
    }

    [Fact]
    public async Task ResendingAFileForALockedPeriod_ReportsDuplicates_NotLockErrors()
    {
        var k = await CreateAsync();
        var csv = File(Line("E-REPORT", "2026-06-10 08:00"));
        await k.Service().ImportCsvAsync(k.TenantId, Request(csv), k.Authority(), k.Context(), default);
        k.Db.AttendanceLockPeriods.Add(new AttendanceLockPeriod { TenantId = k.TenantId, PeriodStart = new DateOnly(2026, 6, 1), PeriodEnd = new DateOnly(2026, 6, 30), Status = "Locked", LockType = "Payroll" });
        await k.Db.SaveChangesAsync();

        var again = await k.Service().ImportCsvAsync(k.TenantId, Request(csv), k.Authority(), k.Context(), default);

        (again.DuplicateRows, again.FailedRows).Should().Be((1, 0), "re-sending a file is safe: a punch already on record is a duplicate even once its period is locked");
    }

    // ── I6 no future punches ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AFuturePunch_IsRefusedPerRow_ButUpToFiveMinutesAheadIsAccepted()
    {
        var k = await CreateAsync();
        // Now is 09:00Z = 12:00 Riyadh.
        var csv = File(
            Line("E-REPORT", "2026-06-20 12:04"),    // 4 minutes ahead: fine
            Line("E-REPORT", "2026-06-20 12:06"),    // 6 minutes ahead: refused
            Line("E-STRANGER", "2026-06-21 08:00")); // tomorrow: refused

        var result = await k.Service().ImportCsvAsync(k.TenantId, Request(csv), k.Authority(), k.Context(), default);

        (result.ImportedRows, result.FailedRows).Should().Be((1, 2));
        result.Errors.Select(e => e.RowNumber).Should().Equal(3, 4);
        result.Errors.Should().OnlyContain(e => e.Message.Contains("future") && e.Column == "punchTimestamp");
    }

    // ── whole-file refusals ──────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("employeeCode,punchTimestamp,punchDirection\n")]
    public async Task AnEmptyOrHeaderOnlyFile_IsRefused_AndWritesNothing(string csv)
    {
        var k = await CreateAsync();

        var act = async () => await k.Service().ImportCsvAsync(k.TenantId, Request(csv), k.Authority(), k.Context(), default);

        await act.Should().ThrowAsync<AttendanceImportFileException>();
        (await k.Db.AttendanceImportBatches.CountAsync()).Should().Be(0);
        (await k.Db.AttendanceAuditLogs.CountAsync()).Should().Be(0);
    }

    // ── the controller: contract, status codes, permissions ──────────────────────────────────────────────────

    [Fact]
    public async Task TheController_ReturnsADto_NotAnEntity_AndAnEmptyFileIsA400()
    {
        var k = await CreateAsync();
        var c = k.Controller();

        var ok = await c.Import(Request(File(Line("E-REPORT", "2026-06-10 08:00"))), default);
        var empty = await c.Import(Request(""), default);
        var headerOnly = await c.ImportPreview(Request(Header + "\n"), default);

        ok.Value.Should().BeOfType<AttendanceImportResult>().Which.ImportedRows.Should().Be(1);
        Assert.IsType<BadRequestObjectResult>(empty.Result);
        Assert.IsType<BadRequestObjectResult>(headerOnly.Result);
    }

    [Fact]
    public async Task TheController_SelfRowForTheCaller_IsAPerRowError_NotAFileWide403()
    {
        var k = await CreateAsync();
        var c = k.Controller();

        var result = await c.Import(Request(File(Line("E-REPORT", "2026-06-10 08:00"), Line("E-CALLER", "2026-06-10 08:00"))), default);

        var dto = result.Value.Should().BeOfType<AttendanceImportResult>().Subject;
        (dto.ImportedRows, dto.FailedRows).Should().Be((1, 1));
        k.Events().Should().NotContain(e => e.EmployeeId == k.Caller.Id);
    }

    [Fact]
    public async Task TheController_OutOfScopeRow_IsAPerRowError_NotAFileWide403_AndNothingIsWrittenForIt()
    {
        var k = await CreateAsync();
        var c = k.Controller(allowed: new[] { k.Report.Id });

        var result = await c.Import(Request(File(Line("E-REPORT", "2026-06-10 08:00"), Line("E-STRANGER", "2026-06-10 08:00"))), default);

        var dto = result.Value.Should().BeOfType<AttendanceImportResult>().Subject;
        (dto.ImportedRows, dto.FailedRows).Should().Be((1, 1));
        k.Events().Should().ContainSingle().Which.EmployeeId.Should().Be(k.Report.Id);
    }

    [Fact]
    public async Task TheTemplate_IsATextCsvFile_WithTheFiveColumns_AndOneExampleRow()
    {
        var k = await CreateAsync();

        var result = k.Controller().ImportTemplate();

        var file = result.Should().BeOfType<FileContentResult>().Subject;
        file.ContentType.Should().StartWith("text/csv");
        var text = Encoding.UTF8.GetString(file.FileContents).TrimStart('﻿');
        var lines = text.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(2);
        lines[0].Should().Be("employeeCode,punchTimestamp,punchDirection,location,method");
        lines[1].Should().StartWith("EMP-001,2026-06-16 09:00,In,");
    }

    [Fact]
    public void TheNewEndpoints_AreGatedByThePermissionsTheSpecNames()
    {
        static string[] Needs(string action) => typeof(AttendanceController).GetMethod(action)!.GetCustomAttributes<HasPermissionAttribute>().Single().Permissions.ToArray();

        Needs(nameof(AttendanceController.ImportTemplate)).Should().Equal("attendance.bulk_import");
        Needs(nameof(AttendanceController.ImportPreview)).Should().Equal("attendance.bulk_import");
        Needs(nameof(AttendanceController.BulkEntry)).Should().Equal("attendance.write");
        typeof(AttendanceController).GetMethod(nameof(AttendanceController.Import))!.GetCustomAttributes<AllowEntityReturnAttribute>().Should().BeEmpty("the import returns a DTO");
        typeof(AttendanceController).GetMethod(nameof(AttendanceController.Import))!.GetCustomAttributes<HasPermissionAttribute>().Should().BeEmpty("Import keeps its role gate, which the legacy resolver maps to attendance.bulk_import");
        LegacyMap(nameof(AttendanceController.Import), "POST").Should().Be("attendance.bulk_import");
    }

    private static string? LegacyMap(string action, string verb) =>
        LegacyRolePermissionResolver.Resolve("Attendance", action, new[] { verb });
}
