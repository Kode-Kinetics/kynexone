using FluentAssertions;
using Xunit;

namespace Zayra.Api.Tests.WaveMisc;

public class RejectProfileChangeShapeTests
{
    internal static string ApiFile(string rel)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var c = Path.Combine(dir.FullName, "Zayra.Api", rel);
            if (File.Exists(c)) return File.ReadAllText(c);
            dir = dir.Parent;
        }
        throw new FileNotFoundException(rel);
    }

    [Fact]
    public void RejectProfileChange_returns_the_explicit_decision_shape_not_the_entity()
    {
        var src = ApiFile("Controllers/EmployeeSelfServiceController.cs");
        var start = src.IndexOf("RejectProfileChange(Guid id", StringComparison.Ordinal);
        var end = src.IndexOf("ProfileChangeDecisionRefusalAsync(\n", start, StringComparison.Ordinal);
        if (end < 0) end = src.IndexOf("Strict separation of duties", start, StringComparison.Ordinal);
        var body = src[start..end];
        body.Should().NotContain("Ok(change)");
        body.Should().Contain("change.DecidedAtUtc, change.DecidedBy, change.ReviewNote");
    }
}
