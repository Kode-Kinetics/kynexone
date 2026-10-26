using System.IO;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Zayra.Api.Application.Employees.Onboarding;
using Zayra.Api.Controllers.Employees;
using Zayra.Api.Infrastructure.Employees.Onboarding;
using Zayra.Api.Infrastructure.Http;
using Xunit;

namespace Zayra.Api.Tests.Onboarding;

/// <summary>
/// I-1: the frontend's hire-error fixtures (frontend/unit/fixtures/hire-errors/*.json) are the contract the wizard
/// parses. Each one is deserialised and compared with the body the backend really produces for that refusal: same
/// status, same content type, and every key the fixture carries (except the cosmetic ones named per fixture) present.
/// </summary>
public class HireErrorContractTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly string[] Cosmetic = ["title", "type", "detail"];

    private static string? FixtureDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var p = Path.Combine(dir.FullName, "frontend", "unit", "fixtures", "hire-errors");
            if (Directory.Exists(p)) return p;
            dir = dir.Parent;
        }
        return null;
    }

    private sealed record Fixture(int Status, string ContentType, Dictionary<string, string> Headers, JsonElement Body);

    private static Fixture Load(string name)
    {
        var dir = FixtureDir();
        dir.Should().NotBeNull("the repository's frontend fixtures must be reachable from the test output");
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!, name + ".json")));
        var r = doc.RootElement;
        return new Fixture(r.GetProperty("status").GetInt32(), r.GetProperty("contentType").GetString()!,
            r.GetProperty("headers").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!), r.GetProperty("body").Clone());
    }

    private static (int Status, string? ContentType, JsonElement Body) Real(IActionResult result)
    {
        var obj = result.Should().BeAssignableTo<ObjectResult>().Subject;
        return (obj.StatusCode!.Value, obj.ContentTypes.FirstOrDefault(), JsonSerializer.SerializeToElement(obj.Value, Web));
    }

    private static ExceptionContext Ctx(Exception ex) =>
        new(new ActionContext(new DefaultHttpContext { TraceIdentifier = "trace-1" }, new RouteData(), new ActionDescriptor()), []) { Exception = ex };

    private static (int, string?, JsonElement) Filtered(Exception ex)
    {
        var c = Ctx(ex);
        new EmployeeProblemFilter().OnException(c);
        return Real(c.Result!);
    }

    private static void AssertMatches(string name, (int Status, string? ContentType, JsonElement Body) real, params string[] optionalKeys)
    {
        var fx = Load(name);
        real.Status.Should().Be(fx.Status, name);
        (real.ContentType ?? "application/json").Should().Be(fx.ContentType, name);
        foreach (var prop in fx.Body.EnumerateObject())
        {
            if (Cosmetic.Contains(prop.Name) || optionalKeys.Contains(prop.Name)) continue;
            real.Body.TryGetProperty(prop.Name, out var actual).Should().BeTrue($"{name}: the body must carry '{prop.Name}'");
            if (prop.Name is "code" or "error" or "field") actual.GetString().Should().Be(prop.Value.GetString(), $"{name}.{prop.Name}");
        }
    }

    [Fact]
    public void ValidationFailed_400_MatchesFixture_AndEveryFixtureFieldKeyIsOneTheValidatorEmits()
    {
        var fx = Load("validation_failed");
        var errors = fx.Body.GetProperty("errors").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.EnumerateArray().Select(v => v.GetString()!).ToArray());
        AssertMatches("validation_failed", Filtered(new EmployeeValidationException(400, EmployeeProblemCodes.ValidationFailed, errors, "x")));

        // The fixture's field names are not invented: the validator emits exactly these for the same mistakes.
        var req = EmployeeInputValidatorTests.Req(name: new string('a', 151), dob: new DateOnly(2999, 1, 1),
                salary: new(0, -5, 0, 0, 0, 0, 0, null, null, "SAR"),
                payroll: new(null, "GB82WEST12345698765432", null, null, null, null, null, true, true, null, null, null));
        var emitted = EmployeeInputValidator.Validate(req, new EmployeeInputValidationContext("SA", new DateOnly(2026, 10, 27), EmployeeGuardMode.Create, TimeZoneInfo.Utc)).Errors.Keys;
        emitted.Should().Contain(["englishName", "dateOfBirth", "salaryBreakdown.housingAllowance", "payrollProfile.iban"]);
        errors.Keys.Where(k => k != "idNumber").Should().BeSubsetOf(emitted);
        foreach (var codes in errors.Values.SelectMany(c => c)) codes.Should().MatchRegex("^[a-z0-9_]+$");
    }

    [Fact]
    public void WarningsRequireAcknowledgement_409_MatchesFixture()
    {
        var fx = Load("warnings_require_acknowledgement");
        var warnings = fx.Body.GetProperty("warnings").EnumerateArray()
            .Select(w => new EmployeeWarning(w.GetProperty("code").GetString()!, w.GetProperty("field").GetString()!, w.GetProperty("message").GetString()!, w.GetProperty("requiresPermission").GetBoolean())).ToList();
        var real = Filtered(new EmployeeWarningsRequireAckException(warnings));
        AssertMatches("warnings_require_acknowledgement", real);
        var first = real.Item3.GetProperty("warnings")[0];
        foreach (var key in new[] { "code", "field", "message", "requiresPermission" }) first.TryGetProperty(key, out _).Should().BeTrue(key);
    }

    [Fact]
    public void InvalidReference_422_MatchesFixture() =>
        AssertMatches("invalid_reference", Filtered(new EmployeeValidationException(422, EmployeeProblemCodes.InvalidReference, new Dictionary<string, string[]>(), "x", "departmentId")));

    [Fact]
    public void OverrideReasonRequired_422_MatchesFixture_FromTheGuardAndFromTheDuplicateOverridePolicy()
    {
        AssertMatches("override_reason_required", Filtered(new EmployeeValidationException(422, EmployeeProblemCodes.OverrideReasonRequired, new Dictionary<string, string[]>(), "x")));
        AssertMatches("override_reason_required", Real(DuplicateOverridePolicy.Refusal(true, "short", holdsPermission: true, traceId: "trace-1")!));
    }

    [Fact]
    public void IdempotencyKeyReuseMismatch_422_MatchesFixture() =>
        AssertMatches("idempotency_key_reuse_mismatch", Real(IdempotentEmployeeCreateFilter.Problem(new DefaultHttpContext { TraceIdentifier = "trace-1" }, 422,
            IdempotentEmployeeCreateFilter.MismatchCode, "x", null)));

    [Fact]
    public void DraftChangedElsewhere_409_MatchesFixture() =>
        AssertMatches("draft_changed_elsewhere", Real(EmployeeHireController.BuildProblem("trace-1", 409, "draft_changed_elsewhere", "x")));

    [Fact]
    public async Task RateLimited_429_MatchesFixture()
    {
        var fx = Load("rate_limited");
        var http = new DefaultHttpContext();
        http.Response.Body = new MemoryStream();
        await RateLimitResponse.WriteAsync(http.Response, 17);
        http.Response.StatusCode.Should().Be(fx.Status);
        http.Response.Headers.RetryAfter.ToString().Should().Be(fx.Headers["retry-after"]);
        http.Response.Body.Position = 0;
        var body = await JsonDocument.ParseAsync(http.Response.Body);
        body.RootElement.GetProperty("error").GetString().Should().Be(fx.Body.GetProperty("error").GetString());
        body.RootElement.TryGetProperty("message", out _).Should().BeTrue();
    }

    [Fact]
    public void PossibleDuplicate_409_FixtureMatchKeys_AreCarriedByTheRealMatchDto()
    {
        // The create controller answers { error: "possible_duplicate", matches: [DuplicateMatchDto] }. The fixture adds
        // 'message' and 'joinedYear'; the wizard types both as optional, the server does not send them.
        var fx = Load("possible_duplicate");
        fx.Body.GetProperty("error").GetString().Should().Be("possible_duplicate");
        var real = JsonSerializer.SerializeToElement(new Zayra.Api.Controllers.DuplicateMatchDto(1, "C", "N", "B", null, "Terminated", "strong", ["s"], true), Web);
        foreach (var key in fx.Body.GetProperty("matches")[0].EnumerateObject().Select(p => p.Name).Where(k => k != "joinedYear"))
            real.TryGetProperty(key, out _).Should().BeTrue($"the match must carry '{key}'");
    }
}
