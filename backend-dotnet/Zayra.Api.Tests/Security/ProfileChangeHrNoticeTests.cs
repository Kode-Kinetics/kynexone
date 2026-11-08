using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Employees.Ess;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// HR is told when an employee sends a change that needs review (the older mobile request or a profile section), the
/// same way a transfer step is: the HR Manager queue, addressed, never a broadcast. A change that applies at once
/// needs no review and tells nobody.
/// </summary>
public sealed class ProfileChangeHrNoticeTests
{
    private sealed class NoStorage : IDocumentStorage
    {
        public Task<StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken ct) => throw new NotSupportedException();
        public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => throw new NotSupportedException();
        public string ResolvePath(string storageUrl) => storageUrl;
    }

    private sealed class Fixed(params string[] fields) : Zayra.Api.Infrastructure.Policy.IPolicySettings
    {
        public Task<IReadOnlyList<string>> GetCsvAsync(Guid tenantId, string key, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>(fields);
        public Task<IReadOnlyList<string>> GetCsvAsync(string key, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>(fields);
        public Task<bool> GetBoolAsync(string key, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> GetStringAsync(string key, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> GetEnumAsync(string key, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<TEnum> GetEnumAsync<TEnum>(string key, CancellationToken ct = default) where TEnum : struct, Enum => throw new NotSupportedException();
        public Task<bool> GetBoolAsync(Guid tenantId, string key, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> GetStringAsync(Guid tenantId, string key, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> GetEnumAsync(Guid tenantId, string key, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<TEnum> GetEnumAsync<TEnum>(Guid tenantId, string key, CancellationToken ct = default) where TEnum : struct, Enum => throw new NotSupportedException();
    }

    private sealed class W
    {
        public Guid Tenant { get; } = Guid.NewGuid();
        public ZayraDbContext Db { get; } = PeopleAuditFixtures.CreateDb();
        public Guid HrLogin { get; } = Guid.NewGuid();
        public Guid SubjectLogin { get; } = Guid.NewGuid();
        public Employee Subject { get; }

        public W()
        {
            Subject = PeopleAuditFixtures.SeedEmployee(Db, Tenant, "E1", "Sam Subject", userAccountId: SubjectLogin);
            Subject.Phone = "0500000000";
            var role = new Role { TenantId = Tenant, Name = "HR Manager", NormalizedName = "HR MANAGER" };
            Db.Roles.Add(role);
            Db.Users.Add(new User { Id = HrLogin, TenantId = Tenant, Email = "hr@x.test", NormalizedEmail = "HR@X.TEST", FullName = "Hana HR", IsGroupScope = true });
            Db.UserRoles.Add(new UserRole { UserId = HrLogin, RoleId = role.Id });
            Db.SaveChanges();
        }

        private ControllerContext Ctx() => new()
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("tenant_id", Tenant.ToString()), new Claim(ClaimTypes.NameIdentifier, SubjectLogin.ToString()),
                    new Claim("employee_id", Subject.Id.ToString()), new Claim("permission", "ess.write"), new Claim("permission", "ess.read"),
                ], "Test")),
            },
        };

        public EmployeeSelfServiceController Ess(params string[] gated)
        {
            var c = new EmployeeSelfServiceController(Db, null!, new PdfRenderGate(1), null!, null!, null!, new NoStorage(),
                policySettings: new Fixed(gated), notifications: TestNotifications.For(Db));
            c.ControllerContext = Ctx();
            return c;
        }

        public EssCompletionController Completion()
        {
            var c = new EssCompletionController(Db, null!, null!, new NoStorage(), null!, TestNotifications.For(Db));
            c.ControllerContext = Ctx();
            return c;
        }

        public List<Notification> Notices() => Db.Notifications.AsNoTracking().Where(n => n.EntityName == nameof(EmployeeProfileChangeRequest)).ToList();
    }

    [Fact]
    public async Task ProfileChange_LegacyRequestThatNeedsReview_TellsTheHrQueue()
    {
        var w = new W();

        var result = await w.Ess("phone").ProfileChangeRequest(new ProfileChangeRequestDto(new() { ["phone"] = "0555555555" }, "moved"), default);

        result.Result.Should().BeOfType<CreatedResult>();
        var notices = w.Notices();
        notices.Should().ContainSingle();
        notices[0].UserId.Should().Be(w.HrLogin);
        notices[0].Message.Should().Contain("E1");
    }

    [Fact]
    public async Task ProfileChange_ThatAppliesAtOnce_TellsNobody()
    {
        var w = new W();

        var result = await w.Ess("salary").ProfileChangeRequest(new ProfileChangeRequestDto(new() { ["phone"] = "0555555555" }, "moved"), default);

        result.Result.Should().BeOfType<OkObjectResult>();
        w.Notices().Should().BeEmpty();
    }

    [Fact]
    public async Task ProfileChange_SectionSubmit_TellsTheHrQueue()
    {
        var w = new W();

        var result = await w.Completion().Submit("emergency",
            new EssSubmitRequest(EssCompletion.NoticeVersion, false, new Dictionary<string, System.Text.Json.JsonElement>
            {
                ["emergencyContactName"] = System.Text.Json.JsonSerializer.SerializeToElement("Mona Contact"),
                ["emergencyContactPhone"] = System.Text.Json.JsonSerializer.SerializeToElement("+966555555555"),
            }, []), default);

        (result as ObjectResult)?.StatusCode.Should().Be(200, System.Text.Json.JsonSerializer.Serialize((result as ObjectResult)?.Value));
        w.Db.EmployeeProfileChangeRequests.Should().ContainSingle(r => r.Section == "emergency");
        w.Notices().Should().ContainSingle(n => n.UserId == w.HrLogin);
    }
}
