using FluentAssertions;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Email;
using Zayra.Api.Application.Auth;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// The Access tab says up front what "give sign-in code" will do for the signed-in caller (email it, or a printed slip),
/// decided by the SAME rule the issue endpoint applies, so the button never surprises the caller.
/// </summary>
public sealed partial class EmployeeAccessTests
{
    private async Task<EmployeeAccessDto> GetWithEmailAsync(World w, int id, Guid caller, IEmailService email)
    {
        await using var db = _fx.CreateDb();
        return (await Service(db, email).GetAsync(w.TenantId, id, Zayra.Api.Application.Common.EntityScopeContext.GroupLevel,
            Ctx(caller, w.TenantId), true, true, default))!;
    }

    private async Task<int> HireEnteredByAsync(World w, Guid enteredBy, string name)
    {
        int id;
        await using (var db = _fx.CreateDb())
            id = (await Employees(db).CreateAsync(w.TenantId, Hire(name, $"{name.Replace(" ", ".").ToLowerInvariant()}@{w.Domain}", w.CompanyId), Ctx(enteredBy, w.TenantId), default)).Id;
        await UpdateEmployeeAsync(id, e => e.Status = EmployeeStatuses.Active);
        return id;
    }

    [Fact]
    public async Task SignInCodeDelivery_TheCallerWhoEnteredTheWorkEmail_IsToldPrintedSlip_AndTheIssueEndpointAgrees()
    {
        var w = await SeedAsync();
        var id = await HireEnteredByAsync(w, w.HrOfficerId, "Forecast Setter");
        var email = new RecordingEmail(configured: true);

        var card = await GetWithEmailAsync(w, id, w.HrOfficerId, email);

        card.WorkEmailEnteredByCaller.Should().BeTrue();
        card.SignInCodeDelivery.Should().Be("PrintedSlip");
        var issued = (await IssueAsync(w, w.HrOfficerId, [id], email: email)).Issued.Single();
        issued.Delivery.Should().Be(IssuedCodeDto.PrintDelivery, "the forecast must match what issuing then does");
    }

    [Fact]
    public async Task SignInCodeDelivery_AnotherHrUser_IsToldEmail_WhenTheCompanyCanEmail_AndTheIssueEndpointAgrees()
    {
        var w = await SeedAsync();
        var id = await HireEnteredByAsync(w, w.HrOfficerId, "Forecast Colleague");
        var email = new RecordingEmail(configured: true);

        var card = await GetWithEmailAsync(w, id, w.HrOfficer2Id, email);

        card.WorkEmailEnteredByCaller.Should().BeFalse();
        card.SignInCodeDelivery.Should().Be("Email");
        (await IssueAsync(w, w.HrOfficer2Id, [id], email: email)).Issued.Single().Delivery.Should().Be(IssuedCodeDto.EmailDelivery);
    }

    [Fact]
    public async Task SignInCodeDelivery_ACompanyThatCannotEmail_IsToldPrintedSlip_EvenForAnotherHrUser()
    {
        var w = await SeedAsync();
        var id = await HireEnteredByAsync(w, w.HrOfficerId, "Forecast NoMail");
        var email = new RecordingEmail(configured: false);

        var card = await GetWithEmailAsync(w, id, w.HrOfficer2Id, email);

        card.WorkEmailEnteredByCaller.Should().BeFalse();
        card.SignInCodeDelivery.Should().Be("PrintedSlip");
        (await IssueAsync(w, w.HrOfficer2Id, [id], email: email)).Issued.Single().Delivery.Should().Be(IssuedCodeDto.PrintDelivery);
    }
}
