using System.Data.Common;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Attendance;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Models;
using static Zayra.Api.Tests.BulkKit;

namespace Zayra.Api.Tests;

/// <summary>
/// Review hardening of the bulk attendance paths: a busy tenant lock answers 409 instead of waiting forever, the payroll-lock decision is
/// made in memory from one snapshot (same answers as the per-day check), dates that cannot be real never reach the work-date maths,
/// request size limits, and a retried commit whose first attempt actually succeeded returns its stored outcome instead of a 500.
/// </summary>
public sealed class AttendanceBulkHardeningTests
{
    private static ImportAttendanceRequest Request(string csv) => new("punches.csv", csv);

    // ── a busy bulk lock is a 409 ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WhenTheBulkLockCannotBeTaken_ImportAndMassEntryAnswer409WithThePlainMessage_AndWriteNothing()
    {
        var k = await CreateAsync();
        var service = k.Service();
        service.BulkLockOverride = (_, _) => throw new AttendanceBusyException();
        var c = k.Controller(service: service);

        var import = await c.Import(Request(File(Line("E-REPORT", "2026-06-10 08:00"))), default);
        var entry = await c.BulkEntry(new BulkAttendanceEntryRequest(new[] { k.Report.Id }, Wednesday, Wednesday, "08:00", "17:00", "Device down"), default);
        var preview = await c.ImportPreview(Request(File(Line("E-REPORT", "2026-06-10 08:00"))), default);

        foreach (var busy in new[] { import.Result, entry.Result })
        {
            var conflict = busy.Should().BeOfType<ConflictObjectResult>().Subject;
            JsonSerializer.Serialize(conflict.Value).Should().Contain("Another bulk attendance operation is running for this company; try again in a moment.");
        }
        preview.Value.Should().NotBeNull("a dry run takes no lock");
        (await k.Db.AttendanceRawEvents.CountAsync()).Should().Be(0);
        (await k.Db.AttendanceImportBatches.CountAsync()).Should().Be(0);
    }

    // ── the payroll-lock snapshot answers exactly like IsLockedAsync ─────────────────────────────────────────

    [Fact]
    public async Task TheLockSnapshot_AnswersExactlyLikeThePerDayCheck_ForEveryEmployeeAndDay()
    {
        var k = await CreateAsync();
        var (a, b, c) = (k.Report.Id, k.Stranger.Id, k.Outsider.Id);
        var company = Guid.NewGuid();
        var lockedRun = new PayrollRun { TenantId = k.TenantId, CompanyId = company, Year = 2026, Month = 4, Status = "Locked" };
        var draftRun = new PayrollRun { TenantId = k.TenantId, CompanyId = company, Year = 2026, Month = 3, Status = "Draft" };
        var paidRun = new PayrollRun { TenantId = k.TenantId, CompanyId = company, Year = 2026, Month = 2, Status = "Paid" };
        k.Db.PayrollRuns.AddRange(lockedRun, draftRun, paidRun);
        k.Db.PayrollSlips.AddRange(
            new PayrollSlip { TenantId = k.TenantId, CompanyId = company, RunId = lockedRun.Id, EmployeeId = a, Status = "Processed" },
            new PayrollSlip { TenantId = k.TenantId, CompanyId = company, RunId = lockedRun.Id, EmployeeId = b, Status = "Voided" },   // voided: no lock
            new PayrollSlip { TenantId = k.TenantId, CompanyId = company, RunId = draftRun.Id, EmployeeId = a, Status = "Processed" }, // run not locked
            new PayrollSlip { TenantId = k.TenantId, CompanyId = company, RunId = paidRun.Id, EmployeeId = b, Status = "Processed" });
        k.Db.AttendanceLockPeriods.AddRange(
            new AttendanceLockPeriod { TenantId = k.TenantId, PeriodStart = new DateOnly(2026, 5, 3), PeriodEnd = new DateOnly(2026, 5, 9), LockType = "Payroll", Status = "Locked" },   // tenant-wide
            new AttendanceLockPeriod { TenantId = k.TenantId, PeriodStart = new DateOnly(2026, 1, 1), PeriodEnd = new DateOnly(2026, 1, 31), LockType = "Payroll", Status = "Open" },    // not locked
            new AttendanceLockPeriod { TenantId = k.TenantId, PeriodStart = new DateOnly(2026, 4, 1), PeriodEnd = new DateOnly(2026, 4, 30), LockType = AttendancePayrollLock.RunLockType(lockedRun.Id), Status = "Locked" },
            new AttendanceLockPeriod { TenantId = k.TenantId, PeriodStart = new DateOnly(2026, 3, 1), PeriodEnd = new DateOnly(2026, 3, 31), LockType = AttendancePayrollLock.RunLockType(draftRun.Id), Status = "Locked" },
            new AttendanceLockPeriod { TenantId = k.TenantId, PeriodStart = new DateOnly(2026, 2, 1), PeriodEnd = new DateOnly(2026, 2, 28), LockType = AttendancePayrollLock.RunLockType(paidRun.Id), Status = "Locked" });
        k.Db.AttendanceDailyRecords.Add(new AttendanceDailyRecord { TenantId = k.TenantId, EmployeeId = b, WorkDate = new DateOnly(2026, 6, 2), IsPayrollLocked = true });
        await k.Db.SaveChangesAsync();
        var from = new DateOnly(2026, 1, 1);
        var to = new DateOnly(2026, 6, 30);

        var snapshot = await AttendancePayrollLock.SnapshotAsync(k.Db, k.TenantId, from, to, new[] { a, b, c }, default);

        var locked = 0;
        for (var day = from; day <= to; day = day.AddDays(1))
            foreach (var employee in new[] { a, b, c })
            {
                var expected = await AttendancePayrollLock.IsLockedAsync(k.Db, k.TenantId, employee, day, default);
                snapshot.IsLocked(employee, day).Should().Be(expected, $"employee {employee} on {day}");
                if (expected) locked++;
            }
        locked.Should().BeGreaterThan(50, "the scenario must actually lock something, or the comparison proves nothing");
    }

    // ── a 1,000-row file is judged with a bounded number of queries ──────────────────────────────────────────

    [Fact]
    public async Task ALockPeriodOverAThousandRowFile_IsDecidedCorrectly_WithABoundedNumberOfQueries()
    {
        var connection = new SqliteConnection($"Data Source=bulk-lock-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        await connection.OpenAsync();
        var counter = new CommandCounter();
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(connection).AddInterceptors(counter).Options);
        await db.Database.EnsureCreatedAsync();
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Bulk lock", Slug = $"bl-{tenantId:N}" });
        db.TenantLocalizationSettings.Add(new TenantLocalizationSetting { TenantId = tenantId, CountryCode = "SA", DefaultTimezone = "Asia/Riyadh" });
        var employees = Enumerable.Range(1, 50).Select(i => new Employee
        {
            TenantId = tenantId, EmployeeCode = $"B-{i:000}", FullName = $"B {i}", EnglishName = $"B {i}", Status = EmployeeStatuses.Active, JoiningDate = DateTime.UtcNow.AddYears(-2),
        }).ToList();
        db.Employees.AddRange(employees);
        db.AttendanceLockPeriods.Add(new AttendanceLockPeriod { TenantId = tenantId, PeriodStart = new DateOnly(2026, 6, 5), PeriodEnd = new DateOnly(2026, 6, 12), Status = "Locked", LockType = "Payroll" });
        await db.SaveChangesAsync();
        var lines = new List<string>();
        foreach (var e in employees)
            for (var day = 1; day <= 20; day++) lines.Add(Line(e.EmployeeCode, $"2026-06-{day:00} 08:00"));
        var service = new AttendanceService(db, new Stub.Notifications(), new Stub.HttpClients(), new Stub.Clock(Now));

        counter.Reset();
        var result = await service.PreviewImportAsync(tenantId, Request(File(lines.ToArray())), AttendanceBulkAuthority.Unrestricted,
            new Zayra.Api.Application.Auth.RequestContext("127.0.0.1", "t", null, tenantId), default);

        (result.TotalRows, result.ValidRows, result.FailedRows).Should().Be((1000, 600, 400), "8 locked days x 50 employees are refused, the other 12 days are valid");
        result.Errors.Should().HaveCount(400).And.OnlyContain(e => e.Message.Contains("locked"));
        counter.Count.Should().BeLessThan(20, "the lock decision is one snapshot, not a query per employee-day");
    }

    // ── dates that cannot be real never reach the work-date maths ────────────────────────────────────────────

    [Fact]
    public async Task ImplausibleDates_AreRowErrors_AndNeverBreakTheFile()
    {
        var k = await CreateAsync();
        var csv = File(
            Line("E-REPORT", "0001-01-01 08:00"),               // 2
            Line("E-REPORT", "0001-01-01T00:00:00+03:00"),      // 3
            Line("E-REPORT", "1999-12-31 08:00"),               // 4
            Line("E-REPORT", "2025-01-01 08:00"),               // 5: 535 days back
            Line("E-REPORT", "9999-12-31 23:59"),               // 6
            Line("E-REPORT", "2025-06-01 08:00"),               // 7: 384 days back: fine
            Line("E-STRANGER", "2026-06-10 08:00"));            // 8: fine

        var result = await k.Service().ImportCsvAsync(k.TenantId, Request(csv), k.Authority(), k.Context(), default);

        (result.ImportedRows, result.FailedRows).Should().Be((2, 5));
        result.Errors.Select(e => e.RowNumber).Should().Equal(2, 3, 4, 5, 6);
        result.Errors.Should().OnlyContain(e => e.Column == "punchTimestamp");
        result.Errors.Single(e => e.RowNumber == 5).Message.Should().Contain("400 days");
    }

    [Fact]
    public async Task TheWorkDateMaths_FailingForOneRow_FailsThatRowOnly()
    {
        var k = await CreateAsync();
        var service = k.Service();
        service.WorkDateResolverOverride = (_, punches) =>
            punches.Any(p => p.PunchUtc.Day == 11) ? throw new ArgumentOutOfRangeException("boom") : null;
        var csv = File(Line("E-REPORT", "2026-06-10 08:00"), Line("E-REPORT", "2026-06-11 08:00"), Line("E-STRANGER", "2026-06-12 08:00"));

        var result = await service.ImportCsvAsync(k.TenantId, Request(csv), k.Authority(), k.Context(), default);

        (result.ImportedRows, result.FailedRows).Should().Be((2, 1));
        result.Errors.Single().RowNumber.Should().Be(3);
    }

    // ── request and row size limits ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheImportEndpoints_AreLimitedTo2MbOfRequest()
    {
        foreach (var action in new[] { nameof(AttendanceController.Import), nameof(AttendanceController.ImportPreview), nameof(AttendanceController.BulkEntry) })
            ((Microsoft.AspNetCore.Http.Metadata.IRequestSizeLimitMetadata)typeof(AttendanceController).GetMethod(action)!
                .GetCustomAttributes<RequestSizeLimitAttribute>().Should().ContainSingle().Subject).MaxRequestBodySize.Should().Be(2_000_000, action);
    }

    [Fact]
    public void MassEntry_NeedsBothAttendanceWriteAndBulkImport()
    {
        var policies = typeof(AttendanceController).GetMethod(nameof(AttendanceController.BulkEntry))!
            .GetCustomAttributes<AuthorizeAttribute>().Select(a => a.Policy).ToList();

        policies.Should().Contain(HasPermissionAttribute.PolicyPrefix + "attendance.write");
        policies.Should().Contain(HasPermissionAttribute.PolicyPrefix + "attendance.bulk_import");
    }

    // ── a retried commit whose first attempt actually succeeded ──────────────────────────────────────────────

    [Fact]
    public async Task ARetriedImport_WhoseFirstAttemptCommitted_ReturnsTheStoredOutcome_NotA500()
    {
        var k = await CreateAsync();
        var csv = File(Line("E-REPORT", "2026-06-10 08:00"), Line("E-STRANGER", "2026-06-10 08:00"), Line("E-NOPE", "2026-06-10 08:00"));
        var batchId = Guid.NewGuid();
        var first = await k.Service().ImportCsvWithBatchIdAsync(k.TenantId, Request(csv), k.Authority(), k.Context(), batchId, default);

        var retried = await k.Service().ImportCsvWithBatchIdAsync(k.TenantId, Request(csv), k.Authority(), k.Context(), batchId, default);

        retried.BatchId.Should().Be(batchId);
        (retried.TotalRows, retried.ValidRows, retried.ImportedRows, retried.DuplicateRows, retried.FailedRows)
            .Should().Be((first.TotalRows, first.ValidRows, first.ImportedRows, first.DuplicateRows, first.FailedRows));
        retried.Status.Should().Be(first.Status);
        // The column is not persisted with an error row, so a replayed outcome carries the row, message and raw row.
        retried.Errors.Select(e => (e.RowNumber, e.Message, e.RawRow)).Should().Equal(first.Errors.Select(e => (e.RowNumber, e.Message, e.RawRow)));
        (await k.Db.AttendanceImportBatches.CountAsync()).Should().Be(1);
        (await k.Db.AttendanceRawEvents.CountAsync()).Should().Be(2);
        (await k.Db.AttendanceAuditLogs.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task ARetriedMassEntry_WhoseFirstAttemptCommitted_ReturnsTheStoredOutcome_NotDuplicates()
    {
        var k = await CreateAsync();
        var request = new BulkAttendanceEntryRequest(new[] { k.Report.Id, k.Caller.Id }, Wednesday, Wednesday.AddDays(1), "08:00", "17:00", "Device down");
        var first = await k.Service().BulkEntryWithBatchAsync(k.TenantId, request, k.Authority(), k.Context(), "batch-1", default);

        var retried = await k.Service().BulkEntryWithBatchAsync(k.TenantId, request, k.Authority(), k.Context(), "batch-1", default);

        (retried.Requested, retried.Created, retried.Duplicates, retried.Failed).Should().Be((first.Requested, first.Created, first.Duplicates, first.Failed));
        retried.Errors.Should().BeEquivalentTo(first.Errors);
        first.Created.Should().Be(4);
        (await k.Db.AttendanceAuditLogs.CountAsync()).Should().Be(1);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────────

    private sealed class CommandCounter : DbCommandInterceptor
    {
        public int Count { get; private set; }
        public void Reset() => Count = 0;
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            Count++;
            return ValueTask.FromResult(result);
        }
        public override ValueTask<object?> ScalarExecutedAsync(DbCommand command, CommandExecutedEventData eventData, object? result, CancellationToken cancellationToken = default)
        {
            Count++;
            return ValueTask.FromResult(result);
        }
    }

    private static class Stub
    {
        public sealed class Notifications : INotificationService
        {
            public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken ct) => Task.CompletedTask;
            public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken ct) => Task.CompletedTask;
        }
        public sealed class HttpClients : IHttpClientFactory
        {
            public HttpClient CreateClient(string name) => new();
        }
        public sealed class Clock(DateTimeOffset now) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => now;
        }
    }
}
