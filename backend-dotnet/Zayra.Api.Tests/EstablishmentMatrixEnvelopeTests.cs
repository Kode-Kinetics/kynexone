using System.Text.RegularExpressions;

namespace Zayra.Api.Tests;

/// <summary>
/// THE MATRIX ENVELOPE, pinned across the language boundary.
///
/// <para><c>GET /api/establishment/matrix</c> returns an OBJECT —
/// <c>{ enforcementMode, unresolvedDepartmentCount, departments: [...] }</c> — and the rows live
/// under <c>departments</c>. The TypeScript client typed it as <c>MatrixRow[]</c> and handed the
/// whole object to <c>setRows</c>. The grouping <c>useMemo</c> in <c>EstablishmentPanel</c> then
/// iterated it and threw <c>TypeError: … is not iterable</c>, which the error boundary turned into
/// "Something went wrong" for the ENTIRE Setup page — so Cost Centres &amp; Budget was unreachable.
/// Reproduced against production on 2026-09-23.</para>
///
/// <para>This is the second instance of the same class in one week; the first was the employee
/// field catalogue (<c>{fields: […]}</c> read as an array), and it hid for months because the
/// client silently discarded every response. Both times the server was right and stable and the
/// client's reading was wrong, so no server-side test could have caught it. This one asserts the
/// controller still emits the property the client unwraps by.</para>
/// </summary>
public class EstablishmentMatrixEnvelopeTests
{
    [Fact]
    public void TheController_EmitsThePropertyTheClientUnwrapsBy()
    {
        var clientKey = FrontendMatrixEnvelopeKey();
        var controller = RepoText("backend-dotnet/Zayra.Api/Controllers/EstablishmentController.cs");

        Assert.True(
            Regex.IsMatch(controller, $@"\b{Regex.Escape(clientKey)}\s*="),
            $"frontend/src/api/establishment.ts unwraps MATRIX_ENVELOPE_KEY = '{clientKey}', but "
            + "EstablishmentController's matrix response no longer assigns a property by that name. "
            + "Renaming it without updating the client makes the Setup page throw "
            + "'is not iterable' and the whole page disappears behind the error boundary.");
    }

    /// <summary>The client must not go back to treating the response as a bare array.</summary>
    [Fact]
    public void TheClient_UnwrapsTheEnvelope_AndDoesNotAssumeAnArray()
    {
        var src = RepoText("frontend/src/api/establishment.ts");

        Assert.DoesNotContain("client.get<MatrixRow[]>('/api/establishment/matrix')", src);
        Assert.Contains("unwrapMatrix(", src);
    }

    private static string FrontendMatrixEnvelopeKey()
    {
        var m = Regex.Match(RepoText("frontend/src/api/establishment.ts"),
            @"MATRIX_ENVELOPE_KEY\s*=\s*'([^']+)'");
        Assert.True(m.Success, "the client must declare MATRIX_ENVELOPE_KEY for this contract to be checkable");
        return m.Groups[1].Value;
    }

    private static string RepoText(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, relative)))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, relative));
    }
}
