using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Policy;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// PEO-03 — an employee's own profile change goes to HR only when its field is in
/// employees.profile_change_approval_fields; any other field applies directly and is history-logged.
/// </summary>
public class EssProfileChangePolicyTests
{
    private sealed class FixedPolicy(params string[] fields) : IPolicySettings
    {
        public Task<IReadOnlyList<string>> GetCsvAsync(Guid tenantId, string key, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(fields);
        public Task<IReadOnlyList<string>> GetCsvAsync(string key, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(fields);
        public Task<bool> GetBoolAsync(string key, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> GetStringAsync(string key, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> GetEnumAsync(string key, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<TEnum> GetEnumAsync<TEnum>(string key, CancellationToken ct = default) where TEnum : struct, Enum => throw new NotSupportedException();
        public Task<bool> GetBoolAsync(Guid tenantId, string key, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> GetStringAsync(Guid tenantId, string key, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> GetEnumAsync(Guid tenantId, string key, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<TEnum> GetEnumAsync<TEnum>(Guid tenantId, string key, CancellationToken ct = default) where TEnum : struct, Enum => throw new NotSupportedException();
    }

    private sealed class NoStorage : IDocumentStorage
    {
        public Task<StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken ct) => throw new NotSupportedException();
        public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => throw new NotSupportedException();
        public string ResolvePath(string storageUrl) => storageUrl;
    }

    private static (ZayraDbContext Db, Guid Tenant, Employee Emp) Setup()
    {
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenant = Guid.NewGuid();
        var e = new Employee
        {
            TenantId = tenant, EmployeeCode = "E1", FullName = "Employee E1", Department = "Ops", Designation = "Officer",
            Status = "Active", JoiningDate = DateTime.UtcNow.Date.AddYears(-1), Phone = "0500000000", PersonalEmail = "old@example.com",
        };
        db.Employees.Add(e);
        db.SaveChanges();
        return (db, tenant, e);
    }

    private static EmployeeSelfServiceController Ess(ZayraDbContext db, Guid tenant, int employeeId, IPolicySettings? policy)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenant.ToString()), new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new("employee_id", employeeId.ToString()), new("permission", "ess.write"),
        };
        var c = new EmployeeSelfServiceController(db, null!, new PdfRenderGate(1), null!, null!, null!, new NoStorage(), policySettings: policy);
        c.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) },
        };
        return c;
    }

    [Fact]
    public async Task A_field_not_in_the_list_applies_directly_and_is_history_logged()
    {
        var (db, tenant, emp) = Setup();
        var result = await Ess(db, tenant, emp.Id, new FixedPolicy("salary")).ProfileChangeRequest(
            new ProfileChangeRequestDto(new() { ["phone"] = "0555555555" }, "moved"), default);

        result.Result.Should().BeOfType<OkObjectResult>();
        (await db.Employees.SingleAsync()).Phone.Should().Be("0555555555");
        (await db.EmployeeProfileChangeRequests.CountAsync()).Should().Be(0);
        var history = await db.EmployeeHistories.SingleAsync();
        history.FieldName.Should().Be("Phone");
        history.OldValue.Should().Be("0500000000");
    }

    [Fact]
    public async Task A_field_in_the_list_still_waits_for_HR()
    {
        var (db, tenant, emp) = Setup();
        var result = await Ess(db, tenant, emp.Id, new FixedPolicy("phone")).ProfileChangeRequest(
            new ProfileChangeRequestDto(new() { ["phone"] = "0555555555" }, "moved"), default);

        result.Result.Should().BeOfType<CreatedResult>();
        (await db.Employees.SingleAsync()).Phone.Should().Be("0500000000");
        (await db.EmployeeProfileChangeRequests.SingleAsync()).Status.Should().Be("PendingHR");
    }

    [Fact]
    public async Task A_mixed_change_splits_the_listed_field_to_HR_and_applies_the_rest()
    {
        var (db, tenant, emp) = Setup();
        var result = await Ess(db, tenant, emp.Id, new FixedPolicy("personal_email")).ProfileChangeRequest(
            new ProfileChangeRequestDto(new() { ["phone"] = "0555555555", ["personalEmail"] = "new@example.com" }, null), default);

        result.Result.Should().BeOfType<CreatedResult>();
        var e = await db.Employees.SingleAsync();
        e.Phone.Should().Be("0555555555");
        e.PersonalEmail.Should().Be("old@example.com");
        var pending = await db.EmployeeProfileChangeRequests.SingleAsync();
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(pending.RequestedChangesJson)!.Keys.Should().Equal("personalEmail");
    }

    [Fact]
    public async Task A_bad_direct_value_is_refused_and_nothing_is_applied()
    {
        var (db, tenant, emp) = Setup();
        var result = await Ess(db, tenant, emp.Id, new FixedPolicy()).ProfileChangeRequest(
            new ProfileChangeRequestDto(new() { ["personalEmail"] = "not-an-email" }, null), default);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        (await db.Employees.SingleAsync()).PersonalEmail.Should().Be("old@example.com");
    }
    // Security review L1: keys differing only by case used to make the ToDictionary(..., OrdinalIgnoreCase) throw (500).
    // (c) still not a 500; differing values for one directly-applied field are the only ambiguous case: refused, naming the field.
    [Theory]
    [InlineData("phone", "Phone")]
    [InlineData("personalEmail", "PERSONALEMAIL")]
    public async Task Case_variant_keys_with_different_values_are_refused_as_a_conflict_not_a_500(string first, string second)
    {
        var (db, tenant, emp) = Setup();
        var result = await Ess(db, tenant, emp.Id, new FixedPolicy()).ProfileChangeRequest(
            new ProfileChangeRequestDto(new() { [first] = "0555555555", [second] = "0566666666" }, null), default);

        var refusal = result.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        refusal.Value!.GetType().GetProperty("code")!.GetValue(refusal.Value).Should().Be("duplicate_field_conflict");
        refusal.Value.GetType().GetProperty("message")!.GetValue(refusal.Value).Should().BeOfType<string>().Which.Should().Contain(first);
        (await db.Employees.SingleAsync()).Phone.Should().Be("0500000000", "nothing is applied when the request is ambiguous");
        (await db.EmployeeProfileChangeRequests.CountAsync()).Should().Be(0);
    }

    // (a) the same field under two casings with the same value is not ambiguous: accepted, applied once.
    [Fact]
    public async Task Case_variant_keys_with_identical_values_are_accepted_and_applied_once()
    {
        var (db, tenant, emp) = Setup();
        var result = await Ess(db, tenant, emp.Id, new FixedPolicy()).ProfileChangeRequest(
            new ProfileChangeRequestDto(new() { ["phone"] = "0555555555", ["Phone"] = "0555555555" }, null), default);

        result.Result.Should().BeOfType<OkObjectResult>();
        (await db.Employees.SingleAsync()).Phone.Should().Be("0555555555");
    }

    // Gated fields never went through the throwing dictionary: both casings are stored as sent and HR's approval applies
    // them in order (the generic route's long-standing behaviour, pinned by EmployeeCompletionHttpTests). Not refused.
    [Fact]
    public async Task Case_variant_keys_on_a_gated_field_are_queued_as_sent()
    {
        var (db, tenant, emp) = Setup();
        var result = await Ess(db, tenant, emp.Id, new FixedPolicy("phone")).ProfileChangeRequest(
            new ProfileChangeRequestDto(new() { ["phone"] = "0555555555", ["Phone"] = "0566666666" }, null), default);

        result.Result.Should().BeOfType<CreatedResult>();
        (await db.Employees.SingleAsync()).Phone.Should().Be("0500000000");
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>((await db.EmployeeProfileChangeRequests.SingleAsync()).RequestedChangesJson)!
            .Keys.Should().Equal("phone", "Phone");
    }
}
