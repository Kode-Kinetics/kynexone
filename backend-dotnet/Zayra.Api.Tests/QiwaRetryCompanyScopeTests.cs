using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Qiwa;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Retrying a dead-lettered Qiwa sync follows the same company scope as the sync-log list: the log is found through its employee, so a
/// one-company caller can retry their own company's logs and not another company's.
/// </summary>
public sealed class QiwaRetryCompanyScopeTests
{
    private sealed class Accessor : IHttpContextAccessor { public HttpContext? HttpContext { get; set; } }

    private static HttpContext ScopedTo(Guid tenantId, Guid companyId) => new DefaultHttpContext
    {
        User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim("entity_access", JsonSerializer.Serialize(new { c = companyId, r = "Admin" })),
        }, "Test")),
    };

    [Fact]
    public async Task RetryDeadLetter_FindsOnlyLogsOfTheCallersCompanies()
    {
        var name = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(name).Options;
        var tenantId = Guid.NewGuid();
        var companyA = Guid.NewGuid();
        var companyB = Guid.NewGuid();
        var logA = Guid.NewGuid();
        var logB = Guid.NewGuid();
        await using (var seed = new ZayraDbContext(options))
        {
            seed.Employees.Add(new Employee { Id = 1, TenantId = tenantId, CompanyId = companyA, EmployeeCode = "A1", FullName = "A", Status = EmployeeStatuses.Active });
            seed.Employees.Add(new Employee { Id = 2, TenantId = tenantId, CompanyId = companyB, EmployeeCode = "B1", FullName = "B", Status = EmployeeStatuses.Active });
            seed.QiwaSyncLogs.Add(new QiwaSyncLog { Id = logA, TenantId = tenantId, EmployeeId = 1, Status = QiwaSyncLogStatuses.DeadLetter, RetryCount = 5 });
            seed.QiwaSyncLogs.Add(new QiwaSyncLog { Id = logB, TenantId = tenantId, EmployeeId = 2, Status = QiwaSyncLogStatuses.DeadLetter, RetryCount = 5 });
            await seed.SaveChangesAsync();
        }

        await using var scoped = new ZayraDbContext(options, new Accessor { HttpContext = ScopedTo(tenantId, companyA) });
        var service = new QiwaIntegrationService(scoped, NullLogger<QiwaIntegrationService>.Instance, DataProtectionProvider.Create("qiwa-retry"));

        var otherCompany = () => service.RetryDeadLetterAsync(tenantId, logB, Guid.NewGuid());
        await otherCompany.Should().ThrowAsync<InvalidOperationException>();
        await service.RetryDeadLetterAsync(tenantId, logA, Guid.NewGuid());

        await using var check = new ZayraDbContext(options);
        (await check.QiwaSyncLogs.AsNoTracking().SingleAsync(l => l.Id == logB)).Status.Should().Be(QiwaSyncLogStatuses.DeadLetter);
        (await check.QiwaSyncLogs.AsNoTracking().SingleAsync(l => l.Id == logA)).Status.Should().Be(QiwaSyncLogStatuses.Pending);
    }
}
