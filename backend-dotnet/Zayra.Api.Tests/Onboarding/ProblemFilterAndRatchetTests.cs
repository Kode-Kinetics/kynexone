using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Zayra.Api.Application.Employees.Onboarding;
using Zayra.Api.Infrastructure.Employees.Onboarding;
using Xunit;

namespace Zayra.Api.Tests.Onboarding;

public class ProblemFilterAndRatchetTests
{
    private static ExceptionContext Ctx(Exception ex)
    {
        var http = new DefaultHttpContext { TraceIdentifier = "trace-1" };
        return new ExceptionContext(new ActionContext(http, new RouteData(), new ActionDescriptor()), []) { Exception = ex };
    }

    private static JsonElement Body(ExceptionContext c)
    {
        var obj = (ObjectResult)c.Result!;
        return JsonSerializer.SerializeToElement(obj.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    [Fact]
    public void ProblemFilter_MapsTo400And409Shapes()
    {
        var errs = new Dictionary<string, string[]> { ["englishName"] = [EmployeeFieldErrors.ValueTooLong] };
        var c400 = Ctx(new EmployeeValidationException(400, EmployeeProblemCodes.ValidationFailed, errs, "Name is too long."));
        new EmployeeProblemFilter().OnException(c400);
        c400.ExceptionHandled.Should().BeTrue();
        var r400 = (ObjectResult)c400.Result!;
        r400.StatusCode.Should().Be(400);
        r400.ContentTypes.Should().Contain("application/problem+json");
        var b = Body(c400);
        b.GetProperty("code").GetString().Should().Be("validation_failed");
        b.GetProperty("status").GetInt32().Should().Be(400);
        b.GetProperty("traceId").GetString().Should().Be("trace-1");
        b.GetProperty("errors").GetProperty("englishName")[0].GetString().Should().Be("value_too_long");
        b.GetProperty("detail").GetString().Should().NotBeNullOrEmpty();

        var c422 = Ctx(new EmployeeValidationException(422, EmployeeProblemCodes.InvalidReference, new Dictionary<string, string[]>(), "That value is not available.", "branchId"));
        new EmployeeProblemFilter().OnException(c422);
        ((ObjectResult)c422.Result!).StatusCode.Should().Be(422);
        Body(c422).GetProperty("field").GetString().Should().Be("branchId");

        var c409 = Ctx(new EmployeeWarningsRequireAckException([new EmployeeWarning("joining_backdated", "joiningDate", "msg", false)]));
        new EmployeeProblemFilter().OnException(c409);
        ((ObjectResult)c409.Result!).StatusCode.Should().Be(409);
        var w = Body(c409);
        w.GetProperty("code").GetString().Should().Be("warnings_require_acknowledgement");
        w.GetProperty("warnings")[0].GetProperty("code").GetString().Should().Be("joining_backdated");
        w.GetProperty("warnings")[0].GetProperty("requiresPermission").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void ProblemFilter_IgnoresOtherExceptions()
    {
        var c = Ctx(new InvalidOperationException("x"));
        new EmployeeProblemFilter().OnException(c);
        c.ExceptionHandled.Should().BeFalse();
        c.Result.Should().BeNull();
    }

    private static string? SrcRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var p = Path.Combine(dir.FullName, "Zayra.Api", "Application", "Employees", "Onboarding");
            if (Directory.Exists(p)) return Path.Combine(dir.FullName, "Zayra.Api");
            dir = dir.Parent;
        }
        return null;
    }

    [Fact]
    public void G23_OnboardingFiles_NeverCatchOrThrowInvalidOperationException()
    {
        var root = SrcRoot();
        if (root is null) return;   // compiled output moved away from the source tree
        var files = new[] { "Application/Employees/Onboarding", "Infrastructure/Employees/Onboarding", "Controllers/Employees" }
            .Select(d => Path.Combine(root, d)).Where(Directory.Exists)
            .SelectMany(d => Directory.GetFiles(d, "*.cs", SearchOption.AllDirectories)).ToList();
        files.Should().NotBeEmpty();
        foreach (var f in files)
        {
            var text = File.ReadAllText(f);
            text.Should().NotContain("catch (InvalidOperationException", $"{Path.GetFileName(f)} must not rely on the global catch-all");
            text.Should().NotContain("throw new InvalidOperationException", $"{Path.GetFileName(f)} must throw typed problem exceptions");
        }
    }
}
