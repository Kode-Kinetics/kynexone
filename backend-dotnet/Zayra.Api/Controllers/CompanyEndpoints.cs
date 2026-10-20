using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Organization;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Organization;

namespace Zayra.Api.Controllers;

/// <summary>
/// The single-company read, create and update rules, shared by <c>/api/companies</c> and
/// <c>/api/organization/companies</c> so the two doors cannot apply different checks.
/// <list type="bullet">
///   <item>Read: the company must be within the request's read scope (narrowed by the switcher),
///   exactly as the list endpoints filter; otherwise 404.</item>
///   <item>Create: a group grant, then the creation gate (plan limit, platform control, single-company
///   account), then the email-domain rule.</item>
///   <item>Update: the company must be within the caller's grant (a group grant covers every company
///   whichever one the switcher selects); otherwise 404, as on read. Then the email-domain rule.</item>
/// </list>
/// </summary>
internal static class CompanyEndpoints
{
    public const string EmailDomainSecurityMessage =
        "Only a security administrator can set or change a company's email domain, because it decides who can sign in.";

    /// <summary>Whether this request may read <paramref name="companyId"/> — the list endpoints' filter.</summary>
    public static bool MayRead(ControllerBase c, Guid companyId) => c.GetEntityScope().CanAccessCompany(companyId);

    /// <summary>Whether the caller's grant covers writing <paramref name="companyId"/>.</summary>
    public static bool MayWrite(ControllerBase c, Guid companyId) => OrgWriteRules.MayWriteCompany(c, companyId);

    /// <summary>F5: setting or changing EmailDomain needs security.manage. <paramref name="current"/> null = a new company.</summary>
    public static bool EmailDomainNeedsSecurityAdmin(ControllerBase c, string? current, string? requested)
    {
        var next = CompanyEmailDomainRules.Normalize(requested);
        var changes = current is null ? next.Length > 0 : !string.Equals(CompanyEmailDomainRules.Normalize(current), next, StringComparison.Ordinal);
        return changes && !c.User.HasPermission(CompanyEmailDomainRules.SecurityPermission);
    }

    public static ObjectResult EmailDomainForbidden(ControllerBase c) =>
        c.StatusCode(StatusCodes.Status403Forbidden, new { code = "email_domain_needs_security_admin", message = EmailDomainSecurityMessage });

    /// <summary>Render a gate refusal as the HTTP status the form has always returned for it.</summary>
    public static ObjectResult GateRefusal(ControllerBase c, CompanyCreationGateDecision gate) => gate.Verdict switch
    {
        CompanyCreationVerdict.SubscriptionLimitReached => c.StatusCode(402, new
        {
            error = gate.ErrorCode,
            currentCount = gate.CurrentCount,
            maxAllowed = gate.MaxAllowed,
            message = gate.Message,
            upgradeRequired = true,
        }),
        CompanyCreationVerdict.PlatformControlled => c.StatusCode(403, new { error = gate.ErrorCode, message = gate.Message }),
        CompanyCreationVerdict.SingleCompanyAccount => c.Conflict(new { error = gate.ErrorCode, message = gate.Message }),
        _ => c.Unauthorized(new { error = gate.ErrorCode, message = gate.Message }),
    };

    public static async Task<ActionResult<CompanyDto>> GetAsync(
        ControllerBase c, IOrganizationSetupService organization, Guid id, CancellationToken ct)
    {
        var tenantId = c.GetTenantId();
        if (tenantId is null) return c.Unauthorized();
        if (!MayRead(c, id)) return c.NotFound();
        var company = await organization.GetCompanyAsync(tenantId.Value, id, ct);
        return company is null ? c.NotFound() : c.Ok(company);
    }

    public static async Task<ActionResult<CompanyDto>> CreateAsync(
        ControllerBase c, IOrganizationSetupService organization, ZayraDbContext db, CompanyRequest request,
        RequestContext context, Func<CompanyDto, ActionResult> created, CancellationToken ct)
    {
        try
        {
            var tenantId = c.GetTenantId();
            if (tenantId is null) return c.Unauthorized();
            if (!c.HasGroupEntityScope())
                return c.StatusCode(StatusCodes.Status403Forbidden, new
                {
                    error = "group_scope_required",
                    message = "Only a group administrator can create another company."
                });

            // The subscription limit and the three governance gates live in CompanyCreationGate so
            // that the CSV importer passes exactly the same four checks this form does.
            var gate = await CompanyCreationGate.EvaluateAsync(db, tenantId.Value, ct);
            if (!gate.Allowed) return GateRefusal(c, gate);
            if (EmailDomainNeedsSecurityAdmin(c, null, request.EmailDomain)) return EmailDomainForbidden(c);

            var company = await organization.CreateCompanyAsync(tenantId.Value, request, context, ct, gate.AsDraft);
            return created(company);
        }
        catch (EmailDomainRefusedException ex) { return c.StatusCode(ex.Status, new { code = ex.Code, message = ex.Message }); }
        catch (CompanyRegistrationNumberTakenException ex) { return c.BadRequest(ex.ToPayload(OrgWriteRules.MayWriteCompany(c, ex.HolderCompanyId))); }
        catch (InvalidOperationException ex) { return c.BadRequest(new { message = ex.Message }); }
    }

    /// <summary>Deleting a legal entity is tenant-level, like creating one: a group grant is required.
    /// The service keeps its own refusals (last active company, active employees).</summary>
    public static async Task<IActionResult> DeleteAsync(
        ControllerBase c, IOrganizationSetupService organization, Guid id, RequestContext context, CancellationToken ct)
    {
        var tenantId = c.GetTenantId();
        if (tenantId is null) return c.Unauthorized();
        if (!OrgWriteRules.GroupGrant(c)) return c.Forbid();
        // The service's refusals propagate exactly as they did before this check existed.
        return await organization.DeleteCompanyAsync(tenantId.Value, id, context, ct) ? c.NoContent() : c.NotFound();
    }

    public static async Task<ActionResult<CompanyDto>> UpdateAsync(
        ControllerBase c, IOrganizationSetupService organization, ZayraDbContext db, Guid id, CompanyRequest request,
        RequestContext context, CancellationToken ct)
    {
        try
        {
            var tenantId = c.GetTenantId();
            if (tenantId is null) return c.Unauthorized();
            if (!MayWrite(c, id)) return c.NotFound();
            var current = await db.Companies.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == id && !x.IsDeleted)
                .Select(x => x.EmailDomain).FirstOrDefaultAsync(ct);
            if (current is not null && EmailDomainNeedsSecurityAdmin(c, current, request.EmailDomain)) return EmailDomainForbidden(c);
            var company = await organization.UpdateCompanyAsync(tenantId.Value, id, request, context, ct);
            return company is null ? c.NotFound() : c.Ok(company);
        }
        catch (EmailDomainRefusedException ex) { return c.StatusCode(ex.Status, new { code = ex.Code, message = ex.Message }); }
        catch (CompanyRegistrationNumberTakenException ex) { return c.BadRequest(ex.ToPayload(OrgWriteRules.MayWriteCompany(c, ex.HolderCompanyId))); }
        catch (InvalidOperationException ex) { return c.BadRequest(new { message = ex.Message }); }
    }
}
