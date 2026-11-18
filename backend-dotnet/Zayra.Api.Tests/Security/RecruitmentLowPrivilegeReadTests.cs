using System.Net;
using FluentAssertions;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Read-side authorization for recruitment management data, through the REAL pipeline (real JWT, real role gates).
/// A plain Employee (self-service) token must not read manpower requisitions (budget range, justification) or workforce
/// plans; the write side is already role-gated, this pins the read side.
/// </summary>
public sealed class RecruitmentLowPrivilegeReadTests(SchedulingScopeFixture f) : IClassFixture<SchedulingScopeFixture>
{
    [Theory]
    [InlineData("/api/recruitment/requisitions")]
    [InlineData("/api/recruitment/requisitions/stats")]
    [InlineData("/api/recruitment/workforce-planning")]
    [InlineData("/api/recruitment/workforce-planning/summary")]
    public async Task PlainEmployee_CannotReadManagementLists(string url)
    {
        (await f.Status(HttpMethod.Get, url, f.Token("Ess"))).Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task PlainEmployee_CannotReadASingleRequisition_OrItsBudget()
    {
        (await f.Status(HttpMethod.Get, $"/api/recruitment/requisitions/{f.RequisitionA}", f.Token("Ess"))).Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task PlainEmployee_CannotExportOpenings()
    {
        (await f.Status(HttpMethod.Get, "/api/recruitment/openings/export", f.Token("Ess"))).Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task HrManager_StillReadsTheSameEndpoints()
    {
        (await f.Status(HttpMethod.Get, "/api/recruitment/requisitions", f.HrA)).Should().Be(HttpStatusCode.OK);
        (await f.Status(HttpMethod.Get, "/api/recruitment/workforce-planning", f.HrA)).Should().Be(HttpStatusCode.OK);
        (await f.Status(HttpMethod.Get, "/api/recruitment/openings/export", f.HrA)).Should().Be(HttpStatusCode.OK);
    }
}
