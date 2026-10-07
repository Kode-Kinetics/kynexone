using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>The pure rules behind employee access: code input, hashing, expiry, the state precedence and the work-email rules.</summary>
public sealed class EmployeeAccessUnitTests
{
    [Theory]
    [InlineData("48217730", "48217730")]
    [InlineData("4821 7730", "48217730")]
    [InlineData("4821-7730", "48217730")]
    [InlineData("٤٨٢١٧٧٣٠", "48217730")]
    [InlineData("۴۸۲۱ ۷۷۳۰", "48217730")]
    [InlineData("4821773", null)]
    [InlineData("482177301", null)]
    [InlineData("4821a730", null)]
    [InlineData("", null)]
    public void CodeInput_AcceptsArabicIndicAndPersianDigits_IgnoresSpacesAndDashes(string typed, string? expected) =>
        WelcomeCodes.NormalizeInput(typed).Should().Be(expected);

    [Fact]
    public void Generate_IsEightDigits_AndTheHashBindsLinkEmailAndCode()
    {
        var code = WelcomeCodes.Generate();
        code.Should().MatchRegex("^[0-9]{8}$");
        var key = WelcomeCodes.DeriveKey("a-server-secret-of-reasonable-length-for-tests");
        var link = Guid.NewGuid();
        var hash = WelcomeCodes.Hash(key, link, "A@B.TEST", code);
        hash.Should().HaveLength(64).And.NotContain(code);
        WelcomeCodes.Hash(key, link, "C@B.TEST", code).Should().NotBe(hash, "a different username");
        WelcomeCodes.Hash(key, Guid.NewGuid(), "A@B.TEST", code).Should().NotBe(hash, "a different link");
        WelcomeCodes.Hash(WelcomeCodes.DeriveKey("another-secret-entirely-xxxxxxxxxxxxxxxx"), link, "A@B.TEST", code).Should().NotBe(hash, "rotating the secret invalidates codes");
    }

    [Fact]
    public void Expiry_IsSevenDays_OrJoiningPlusSeven_CappedAtThirty()
    {
        var issued = new DateTime(2026, 10, 7, 9, 0, 0, DateTimeKind.Utc);
        WelcomeCodes.ExpiresAt(issued, new DateTime(2026, 9, 1)).Should().Be(issued.AddDays(7));
        WelcomeCodes.ExpiresAt(issued, new DateTime(2026, 10, 20)).Should().Be(new DateTime(2026, 10, 27, 0, 0, 0, DateTimeKind.Utc));
        WelcomeCodes.ExpiresAt(issued, new DateTime(2027, 1, 1)).Should().Be(issued.AddDays(30));
    }

    [Fact]
    public void Ladder_LocksAtFiveAndTen()
    {
        var at = DateTime.UtcNow;
        WelcomeCodes.LockedUntil(4, at).Should().BeNull();
        WelcomeCodes.LockedUntil(5, at).Should().Be(at.AddMinutes(15));
        WelcomeCodes.LockedUntil(10, at).Should().Be(at.AddHours(1));
    }

    [Theory]
    [InlineData("noah+hr@acme.sa", true)]
    [InlineData("noah@acme.sa", false)]
    [InlineData("no+ah", true)]
    [InlineData("noah@ac+me.sa", false)]
    public void PlusAddressing_IsDetectedInTheLocalPartOnly(string email, bool plus) =>
        WorkEmailSetterRule.IsPlusAddressed(email).Should().Be(plus);

    [Fact]
    public void Resolve_NeverSavesADerivedAddress_AndRefusesAWrongDomain()
    {
        static bool NotTaken(string _) => false;
        WorkEmailDeriver.Resolve("", "Noah Williams", null, "acme.sa", WorkEmailPatterns.FirstLast, NotTaken, out var outcome, out _)
            .Should().BeEmpty();
        outcome.Should().Be("blank");
        WorkEmailDeriver.Suggest("Noah Williams", null, "acme.sa", WorkEmailPatterns.FirstLast, NotTaken).Should().Be("noah.williams@acme.sa");
        WorkEmailDeriver.Resolve("noah", null, null, "acme.sa", WorkEmailPatterns.FirstLast, NotTaken, out _, out _).Should().Be("noah@acme.sa");
        var wrong = Assert.Throws<WorkEmailRejectedException>(() =>
            WorkEmailDeriver.Resolve("noah@gmail.com", null, null, "acme.sa", WorkEmailPatterns.FirstLast, NotTaken, out _, out _));
        wrong.Code.Should().Be("work_email_wrong_domain");
        wrong.Message.Should().Be("Work email must end in @acme.sa.");
        Assert.Throws<WorkEmailRejectedException>(() =>
            WorkEmailDeriver.Resolve("noah+1@acme.sa", null, null, "acme.sa", WorkEmailPatterns.FirstLast, NotTaken, out _, out _))
            .Code.Should().Be("work_email_plus_address");
    }

    [Fact]
    public void State_Precedence_StoppedOverBlockedOverCodeOverActive()
    {
        var now = DateTime.UtcNow;
        EmployeeAccessStates.Facts F(string status = "Active", string email = "a@acme.sa", string domain = "acme.sa",
            EmployeeUserAccount? link = null, EmployeeAccessStates.LoginFacts? login = null, string? owner = null, Guid? pointer = null) =>
            new(1, "A", "", "E1", "", "", status, false, false, email, Guid.NewGuid(), domain, pointer, now, link, login, owner);
        var activeLogin = new EmployeeAccessStates.LoginFacts(Guid.NewGuid(), "a@acme.sa", "Active", true, false, now);
        var activeLink = new EmployeeUserAccount { AccessMode = AccessModes.EssOnly, RequiresPasswordSetup = false };
        var liveCodeLink = new EmployeeUserAccount
        {
            AccessMode = AccessModes.EssOnly, RequiresPasswordSetup = false,
            WelcomeCodeHash = new string('A', 64), WelcomeCodeExpiresAtUtc = now.AddDays(1),
        };

        EmployeeAccessStates.Evaluate(F(email: ""), now).State.Should().Be(EmployeeAccessStates.WaitingForWorkEmail);
        EmployeeAccessStates.Evaluate(F(), now).State.Should().Be(EmployeeAccessStates.NotStarted);
        EmployeeAccessStates.Evaluate(F(domain: ""), now).BlockedCode.Should().Be("company_email_domain_missing");
        EmployeeAccessStates.Evaluate(F(email: "a@other.sa"), now).BlockedCode.Should().Be("work_email_wrong_domain");
        EmployeeAccessStates.Evaluate(F(owner: "email_belongs_to_existing_login"), now).State.Should().Be(EmployeeAccessStates.Blocked);
        EmployeeAccessStates.Evaluate(F(link: activeLink, login: activeLogin), now).State.Should().Be(EmployeeAccessStates.Active);
        EmployeeAccessStates.Evaluate(F(link: liveCodeLink, login: activeLogin), now).State.Should().Be(EmployeeAccessStates.CodeGiven, "a live reset code outranks active");
        EmployeeAccessStates.Evaluate(F(status: "Terminated", link: liveCodeLink, login: activeLogin), now).State.Should().Be(EmployeeAccessStates.Stopped);
        EmployeeAccessStates.Evaluate(F(status: "Terminated", domain: ""), now).State.Should().Be(EmployeeAccessStates.Stopped, "stopped outranks blocked");
    }

    [Theory]
    [InlineData("gmail.com")]
    [InlineData("OUTLOOK.com")]
    [InlineData("icloud.com")]
    [InlineData("proton.me")]
    public void PublicMailDomains_AreListed(string domain) =>
        CompanyEmailDomainRules.PublicDomains.Contains(domain).Should().BeTrue();
}

/// <summary>The employee-access endpoints through the REAL pipeline: permission gates and anonymous redeem.</summary>
[Collection("AuthorizationPipeline")]
public sealed class EmployeeAccessHttpTests
{
    private readonly AuthorizationPipelineFixture _fixture;
    public EmployeeAccessHttpTests(AuthorizationPipelineFixture fixture) => _fixture = fixture;

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? bearer, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await _fixture.Client.SendAsync(request);
    }

    [Fact]
    public async Task Writes_NeedEmployeesAccessIssue_ReadsNeedEmployeesRead_AndAnonymousIsUnauthorized()
    {
        (await SendAsync(HttpMethod.Get, "/api/employee-access/1", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await SendAsync(HttpMethod.Post, "/api/employee-access/codes", null, new { employeeIds = new[] { 1 } })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        // employees.read only: the read passes the gate (and finds nothing), the writes are forbidden.
        (await SendAsync(HttpMethod.Get, "/api/employee-access/1", _fixture.TenantTokenWithoutPermission)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await SendAsync(HttpMethod.Post, "/api/employee-access/codes", _fixture.TenantTokenWithoutPermission, new { employeeIds = new[] { 1 } }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Post, "/api/employee-access/work-emails", _fixture.TenantTokenWithoutPermission, new { rows = Array.Empty<object>(), dryRun = true }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        // notifications.manage only: no employees.read either.
        (await SendAsync(HttpMethod.Get, "/api/employee-access/1", _fixture.TenantTokenWithPermission)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Redeem_IsAnonymous_AndAnswersWithACodeOnly()
    {
        var response = await SendAsync(HttpMethod.Post, "/api/auth/welcome/redeem", null,
            new { email = "nobody@unknown-domain.test", code = "12345678", newPassword = "Whatever1!pass" });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        body.GetProperty("code").GetString().Should().Be("workspace_required");
        body.TryGetProperty("message", out _).Should().BeFalse();

        var policy = await SendAsync(HttpMethod.Get, "/api/auth/password-policy", null);
        policy.StatusCode.Should().Be(HttpStatusCode.OK);
        (await policy.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("minLength").GetInt32().Should().Be(10);
    }
}
