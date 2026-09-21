using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Zayra.Api.Controllers;
using Zayra.Api.Infrastructure.Attendance;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Jobs;

namespace Zayra.Api.Tests;

/// <summary>F3 — static guards on the job API surface (no database).</summary>
public sealed class BackgroundJobInfrastructureTests
{
    /// <summary>Every production job type registered in Program.cs. Wave 2 adds payroll descriptors here.</summary>
    private static readonly BackgroundJobTypeDescriptor[] ProductionTypes = [AttendanceProcessingJobHandler.Descriptor];

    [Fact]
    public void JobsController_CoarsePermissionGate_CoversEveryRegisteredJobType()
    {
        var gate = typeof(JobsController).GetCustomAttribute<HasPermissionAttribute>();
        Assert.NotNull(gate);
        Assert.Equal(BackgroundJobPermissions.AnyView.OrderBy(x => x), gate!.Permissions.OrderBy(x => x));
        foreach (var type in ProductionTypes)
            Assert.True(type.ViewPermissions.Any(p => gate.Permissions.Contains(p)),
                $"Job type {type.JobType}: none of its view permissions pass the controller gate, so nobody could see it.");
    }

    [Fact]
    public void AsyncAttendanceEndpoint_HasExactlyTheSyncEndpointsRoleGate()
    {
        static string Roles(string method) => typeof(AttendanceController).GetMethod(method)!
            .GetCustomAttributes<AuthorizeAttribute>().Single().Roles!;
        Assert.Equal(Roles(nameof(AttendanceController.Process)), Roles(nameof(AttendanceController.ProcessInBackground)));
    }

    [Fact]
    public void Registry_RejectsDuplicateTypes_AndNonHandlers()
    {
        Assert.Throws<InvalidOperationException>(() => new BackgroundJobTypeRegistry(
            [AttendanceProcessingJobHandler.Descriptor, AttendanceProcessingJobHandler.Descriptor]));
        Assert.Throws<InvalidOperationException>(() => new BackgroundJobTypeRegistry(
            [new BackgroundJobTypeDescriptor("x", typeof(string), [], [])]));
    }

    [Fact]
    public void AttendanceDefaultKey_DependsOnRangeEmployeeAndCallerScope_NotOnTheCaller()
    {
        var company = Guid.NewGuid();
        var a = new AttendanceProcessingJobPayload(new(2026, 9, 1), new(2026, 9, 30), null, true, [], Guid.NewGuid(), "1.1.1.1", "x");
        var b = a with { RequestedByUserId = Guid.NewGuid(), IpAddress = "2.2.2.2" };
        Assert.Equal(AttendanceProcessingJobHandler.DefaultIdempotencyKey(a), AttendanceProcessingJobHandler.DefaultIdempotencyKey(b));
        Assert.NotEqual(AttendanceProcessingJobHandler.DefaultIdempotencyKey(a),
            AttendanceProcessingJobHandler.DefaultIdempotencyKey(a with { EmployeeId = 7 }));
        Assert.NotEqual(AttendanceProcessingJobHandler.DefaultIdempotencyKey(a),
            AttendanceProcessingJobHandler.DefaultIdempotencyKey(a with { GroupScope = false, CompanyIds = [company] }));
    }
}
