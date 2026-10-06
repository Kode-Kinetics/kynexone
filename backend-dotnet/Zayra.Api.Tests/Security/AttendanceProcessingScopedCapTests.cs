using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Attendance;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Models;
using static Zayra.Api.Tests.Security.SeededRoleBundles;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// A Mobile-mode employee reaches POST /attendance/process/async for their own record (attendance.write from the
/// access mode). A fresh Idempotency-Key or date range per call let them queue jobs without limit; a caller with a
/// restricted data scope now gets one active job per employee.
/// </summary>
public sealed class AttendanceProcessingScopedCapTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task AScopedCaller_GetsOneActiveJobPerEmployee_WhateverKeyOrRangeTheySend()
    {
        var (db, tenantId, employeeId) = await SeedAsync();
        var controller = Controller(db, await CallerAsync(db, tenantId, "Employee"), new FixedScope(new[] { employeeId }));
        var store = Store(db);

        var first = await EnqueueAsync(controller, store, new ProcessAttendanceRequest(new DateOnly(2026, 8, 2), new DateOnly(2026, 8, 3), employeeId), "key-1");
        var second = await EnqueueAsync(controller, store, new ProcessAttendanceRequest(new DateOnly(2026, 8, 4), new DateOnly(2026, 8, 5), employeeId), "key-2");

        second.JobId.Should().Be(first.JobId);
        second.Deduplicated.Should().BeTrue();
        (await db.BackgroundJobs.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task AnOrgWideCaller_KeepsClientIdempotencyKeys()
    {
        var (db, tenantId, employeeId) = await SeedAsync();
        var controller = Controller(db, await CallerAsync(db, tenantId, "HR Manager"), new FixedScope(null));
        var store = Store(db);

        var first = await EnqueueAsync(controller, store, new ProcessAttendanceRequest(new DateOnly(2026, 8, 2), new DateOnly(2026, 8, 3), employeeId), "key-1");
        var second = await EnqueueAsync(controller, store, new ProcessAttendanceRequest(new DateOnly(2026, 8, 4), new DateOnly(2026, 8, 5), employeeId), "key-2");

        second.JobId.Should().NotBe(first.JobId);
    }

    private static async Task<BackgroundJobEnqueueResponse> EnqueueAsync(AttendanceController controller, BackgroundJobStore store, ProcessAttendanceRequest request, string key) =>
        (BackgroundJobEnqueueResponse)((AcceptedResult)(await controller.ProcessInBackground(request, store, key, Ct)).Result!).Value!;

    private static async Task<(ZayraDbContext Db, Guid TenantId, int EmployeeId)> SeedAsync()
    {
        var (db, tenantId) = await NewTenantAsync("att-async-cap");
        var employee = new Employee { TenantId = tenantId, EmployeeCode = "E-1", FullName = "Mobile User", Status = "Active" };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        return (db, tenantId, employee.Id);
    }

    private static BackgroundJobStore Store(ZayraDbContext db) =>
        new(db, new BackgroundJobTypeRegistry(new[] { AttendanceProcessingJobHandler.Descriptor }));

    private static AttendanceController Controller(ZayraDbContext db, ClaimsPrincipal caller, IDataScopeService scope)
    {
        var controller = Bind(new AttendanceController(
            new AttendanceService(db, new NullNotifications(), new NullHttpClientFactory()),
            scope, new HrmHierarchyService(db, new NullAudit()), db), caller);
        return controller;
    }

    private sealed class FixedScope(int[]? allowed) : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) =>
            Task.FromResult(allowed is null
                ? new DataScope { Level = DataScopeLevel.Organization }
                : new DataScope { Level = DataScopeLevel.Own, CallerEmployeeId = allowed[0], AllowedEmployeeIds = allowed });
    }

    private sealed class NullNotifications : INotificationService
    {
        public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken ct) => Task.CompletedTask;
        public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NullHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class NullAudit : IAuditService
    {
        public Task WriteAsync(string action, string entityName, string? entityId, RequestContext context, string? metadata, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
