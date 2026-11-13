using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Controllers.Finance;

namespace Zayra.Api.Tests;

/// <summary>
/// The sign-in token carries the display name as the short "name" claim (JwtRegisteredClaimNames.Name), not as
/// ClaimTypes.Name. Finance controllers that only read ClaimTypes.Name stamped "Unknown" on the GL rows they
/// post (loan disbursement, advance disbursement, bonus lines, bank confirmations) even though posted_by was
/// set, so the ledger could not say who posted a line. They now read the same two claims GlJournalExportsController does.
/// </summary>
public class FinanceGlPostedByNameTests
{
    private static ClaimsPrincipal Token(params Claim[] claims) => new(new ClaimsIdentity(claims, "Test"));

    private static string NameFor(Type controller, string method, ClaimsPrincipal user)
    {
        // The name helper only reads `User`, so the controller is built without running its constructor.
        var c = (ControllerBase)RuntimeHelpers.GetUninitializedObject(controller);
        c.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } };
        var m = controller.GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException($"{controller.Name}.{method} not found");
        return (string)m.Invoke(c, null)!;
    }

    public static IEnumerable<object[]> Controllers() => new[]
    {
        new object[] { typeof(LoansController), "GetUserName" },
        new object[] { typeof(AdvancesController), "GetUserName" },
        new object[] { typeof(BonusesController), "GetUserName" },
        new object[] { typeof(BankConfirmationsController), "UserName" },
        new object[] { typeof(GlJournalExportsController), "UserName" },
    };

    [Theory, MemberData(nameof(Controllers))]
    public void ATokenWithOnlyTheShortNameClaim_GivesTheDisplayName(Type controller, string method) =>
        Assert.Equal("Hana Finance", NameFor(controller, method, Token(new Claim("name", "Hana Finance"))));

    [Theory, MemberData(nameof(Controllers))]
    public void TheMappedClaimStillWins(Type controller, string method) =>
        Assert.Equal("Mapped Name", NameFor(controller, method,
            Token(new Claim(ClaimTypes.Name, "Mapped Name"), new Claim("name", "Hana Finance"))));

    [Theory, MemberData(nameof(Controllers))]
    public void NoNameAtAll_StaysUnknown(Type controller, string method) =>
        Assert.Equal("Unknown", NameFor(controller, method, Token()));
}
