using Zayra.Api.Infrastructure.Seed;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// The platform-owner bootstrap guard. Two separate properties, and the second one is why this file
/// exists: a weak password must refuse the SEED, and must not stop the SERVICE.
///
/// <para>On 2026-09-23 it did stop the service. Setting PLATFORM_ADMIN_BOOTSTRAP with a short password
/// threw at startup, so production went down — on a deployment that already had a platform owner and
/// where the bootstrap would have been a no-op. A control that turns an environment-variable typo into
/// an outage gets disabled by the people it is meant to protect.</para>
/// </summary>
public class PlatformBootstrapGuardTests
{
    [Theory]
    [InlineData("")]                          // unset
    [InlineData("short")]                     // under 16
    [InlineData("123456789012345")]           // 15 — one short
    [InlineData("ChangeMe123!ChangeMe123!")]  // long enough, known default
    [InlineData("changeme-but-lowercase-and-long")]
    [InlineData("MyYourPasswordIsLongEnough")]
    [InlineData("PlatformAdmin123!PlatformAdmin123!")]
    public void WeakOrDefaultPasswords_AreRefused(string password) =>
        Assert.True(PlatformOwnerBootstrap.IsWeakBootstrapPassword(password));

    [Theory]
    [InlineData("YXIJddD8D10VI21YCjuySHcg")]  // 24 random characters
    [InlineData("1234567890123456")]          // exactly 16 — the boundary
    [InlineData("correct horse battery staple")]
    public void StrongPasswords_AreAccepted(string password) =>
        Assert.False(PlatformOwnerBootstrap.IsWeakBootstrapPassword(password));

    [Fact]
    public void NullPassword_IsRefused_NotCrashed() =>
        Assert.True(PlatformOwnerBootstrap.IsWeakBootstrapPassword(null));

    // The property that actually protects uptime: startup must LOG and CONTINUE on a weak password,
    // never throw. Asserted against the source because the branch lives in Program.cs's top-level
    // statements, which no test can instantiate. Same approach as NoSideDoorDataTests.
    [Fact]
    public void Startup_RefusesTheSeed_WithoutStoppingTheService()
    {
        var program = File.ReadAllText(RepoPath("backend-dotnet/Zayra.Api/Program.cs"));
        var start = program.IndexOf("platformBootstrapWeakPassword", StringComparison.Ordinal);
        Assert.True(start > 0, "the weak-password branch has been renamed or removed");
        var branch = program[start..program.IndexOf("PlatformOwnerBootstrap.RunAsync", start, StringComparison.Ordinal)];

        Assert.DoesNotContain("throw new", branch);
        Assert.Contains("LogError", branch);
        Assert.Contains("IsWeakBootstrapPassword", branch);
        // and the seed itself is skipped, not merely warned about
        Assert.Contains("!platformBootstrapWeakPassword", branch);
    }

    private static string RepoPath(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "backend-dotnet")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, relative);
    }
}
