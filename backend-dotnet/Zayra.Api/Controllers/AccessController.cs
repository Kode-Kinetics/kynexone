using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Email;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers;

[ApiController]
[Route("api/access")]
// RBAC / user / override management crown jewel — every action was Admin-only. security.manage is
// held ONLY by the seeded Admin role, so this preserves Admin-exclusive access exactly (no downgrade,
// no lockout) while letting a client mint a real custom "Console Admin" role by granting security.manage.
// The two grant-permission endpoints keep their method-level [Authorize]; it AND-combines to security.manage.
[HasPermission("security.manage")]
public class AccessController : ControllerBase
{
    private readonly IAccessManagementService _accessManagement;
    private readonly ZayraDbContext _db;
    private readonly IAuthService? _authService;
    private readonly StepUpService? _stepUp;
    private readonly Zayra.Api.Infrastructure.Notifications.INotificationService? _adminNotices;
    private readonly IEmailService _emailService;

    public AccessController(
        IAccessManagementService accessManagement,
        ZayraDbContext db,
        IEmailService emailService,
        IAuthService? authService = null,
        StepUpService? stepUp = null,
        Zayra.Api.Infrastructure.Notifications.INotificationService? adminNotices = null)
    {
        _accessManagement = accessManagement;
        _db = db;
        _emailService = emailService;
        _authService = authService;
        _stepUp = stepUp;
        _adminNotices = adminNotices;
    }

    [HttpGet("roles")]
    public async Task<ActionResult<IReadOnlyCollection<RoleDto>>> Roles(CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        if (tenantId is null) return Unauthorized();
        return Ok(await _accessManagement.GetRolesAsync(tenantId.Value, cancellationToken));
    }

    /// <summary>
    /// The caller's privilege ceiling: their own effective permissions and, per role, whether they may assign it or
    /// edit it and why not. The Access screen greys out exactly what the server would refuse (PrivilegeCeiling).
    /// </summary>
    [HttpGet("ceiling")]
    public async Task<ActionResult<AccessCeilingDto>> Ceiling(CancellationToken cancellationToken)
    {
        try
        {
            var tenantId = GetTenantId();
            if (tenantId is null) return Unauthorized();
            return Ok(await _accessManagement.GetAccessCeilingAsync(tenantId.Value, GetContext(), cancellationToken));
        }
        catch (PrivilegeCeilingException ex) { return Refused(ex); }
    }

    [HttpPost("roles")]
    public async Task<ActionResult<RoleDto>> CreateRole(CreateRoleRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var tenantId = GetTenantId();
            if (tenantId is null) return Unauthorized();
            var gate = RequireGroupEntityScope();
            if (gate is not null) return gate;
            var role = await _accessManagement.CreateRoleAsync(tenantId.Value, request, GetContext(), cancellationToken);
            return CreatedAtAction(nameof(Roles), role);
        }
        catch (PrivilegeCeilingException ex) { return await CeilingRefusedAsync(ex, "access.role_created", "Role", null); }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPut("roles/{roleId:guid}")]
    public async Task<ActionResult<RoleDto>> UpdateRole(Guid roleId, UpdateRoleRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var tenantId = GetTenantId();
            if (tenantId is null) return Unauthorized();
            var gate = RequireGroupEntityScope();
            if (gate is not null) return gate;
            var role = await _accessManagement.UpdateRoleAsync(tenantId.Value, roleId, request, GetContext(), cancellationToken);
            return role is null ? NotFound() : Ok(role);
        }
        catch (PrivilegeCeilingException ex) { return await CeilingRefusedAsync(ex, "access.role_updated", "Role", roleId.ToString()); }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPatch("roles/{roleId:guid}/activate")]
    public async Task<IActionResult> ActivateRole(Guid roleId, CancellationToken cancellationToken)
    {
        try
        {
            var tenantId = GetTenantId();
            if (tenantId is null) return Unauthorized();
            var gate = RequireGroupEntityScope();
            if (gate is not null) return gate;
            return await _accessManagement.ActivateRoleAsync(tenantId.Value, roleId, GetContext(), cancellationToken) ? NoContent() : NotFound();
        }
        catch (PrivilegeCeilingException ex) { return await CeilingRefusedAsync(ex, "access.role_activated", "Role", roleId.ToString()); }
    }

    [HttpPatch("roles/{roleId:guid}/deactivate")]
    public async Task<IActionResult> DeactivateRole(Guid roleId, CancellationToken cancellationToken)
    {
        try
        {
            var tenantId = GetTenantId();
            if (tenantId is null) return Unauthorized();
            var gate = RequireGroupEntityScope();
            if (gate is not null) return gate;
            return await _accessManagement.DeactivateRoleAsync(tenantId.Value, roleId, GetContext(), cancellationToken) ? NoContent() : NotFound();
        }
        catch (PrivilegeCeilingException ex) { return await CeilingRefusedAsync(ex, "access.role_deactivated", "Role", roleId.ToString()); }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPut("roles/{roleId:guid}/permissions")]
    public async Task<ActionResult<RoleDto>> SetRolePermissions(Guid roleId, BulkRolePermissionsRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var tenantId = GetTenantId();
            if (tenantId is null) return Unauthorized();
            var gate = RequireGroupEntityScope();
            if (gate is not null) return gate;
            var role = await _accessManagement.SetRolePermissionsAsync(tenantId.Value, roleId, request, GetContext(), cancellationToken);
            return role is null ? NotFound() : Ok(role);
        }
        catch (PrivilegeCeilingException ex) { return await CeilingRefusedAsync(ex, "access.role_permissions_set", "Role", roleId.ToString()); }
    }

    [HttpGet("permission-matrix")]
    public async Task<ActionResult<PermissionMatrixDto>> GetPermissionMatrix(CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        if (tenantId is null) return Unauthorized();
        var gate = RequireGroupEntityScope();
        if (gate is not null) return gate;
        return Ok(await _accessManagement.GetPermissionMatrixAsync(tenantId.Value, cancellationToken));
    }

    [HttpPut("permission-matrix")]
    public async Task<IActionResult> SavePermissionMatrix(PermissionMatrixUpdateRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var tenantId = GetTenantId();
            if (tenantId is null) return Unauthorized();
            var gate = RequireGroupEntityScope();
            if (gate is not null) return gate;
            await _accessManagement.SavePermissionMatrixAsync(tenantId.Value, request, GetContext(), cancellationToken);
            return NoContent();
        }
        catch (PrivilegeCeilingException ex) { return await CeilingRefusedAsync(ex, "access.permission_matrix_saved", "Tenant", null); }
    }

    [HttpGet("users/{userId:guid}/effective-permissions")]
    public async Task<ActionResult<EffectivePermissionsDto>> GetEffectivePermissions(Guid userId, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        if (tenantId is null) return Unauthorized();
        var result = await _accessManagement.GetEffectivePermissionsAsync(tenantId.Value, userId, ManagementScope(), cancellationToken);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpDelete("users/{userId:guid}/permission-overrides/{overrideId:guid}")]
    public async Task<IActionResult> DeletePermissionOverride(Guid userId, Guid overrideId, CancellationToken cancellationToken)
    {
        try
        {
            var tenantId = GetTenantId();
            if (tenantId is null) return Unauthorized();
            return await _accessManagement.DeletePermissionOverrideAsync(tenantId.Value, userId, overrideId, ManagementScope(), GetContext(), cancellationToken) ? NoContent() : NotFound();
        }
        catch (PrivilegeCeilingException ex) { return await CeilingRefusedAsync(ex, "access.permission_override_deleted", "User", userId.ToString()); }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpGet("permissions")]
    public async Task<ActionResult<IReadOnlyCollection<PermissionDto>>> Permissions(CancellationToken cancellationToken)
    {
        return Ok(await _accessManagement.GetPermissionsAsync(cancellationToken));
    }

    [HttpGet("users")]
    public async Task<ActionResult<PagedResult<UserListDto>>> ListUsers([FromQuery] string? search, [FromQuery] string? status, [FromQuery] string? role, [FromQuery] int page = 1, [FromQuery] int pageSize = 30, CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var tenantId = GetTenantId();
        if (tenantId is null) return Unauthorized();
        var result = await _accessManagement.ListUsersAsync(tenantId.Value, new UserListQuery(search, status, role, page, pageSize), this.GetEntityScope(), cancellationToken);
        return Ok(result);
    }

    [HttpGet("users/{userId:guid}")]
    public async Task<ActionResult<UserListDto>> GetUser(Guid userId, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        if (tenantId is null) return Unauthorized();
        var user = await _accessManagement.GetUserAsync(tenantId.Value, userId, ManagementScope(), cancellationToken);
        return user is null ? NotFound() : Ok(user);
    }

    [HttpPost("users")]
    public async Task<ActionResult<AuthUserDto>> CreateUser(CreateUserRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var tenantId = GetTenantId();
            if (tenantId is null) return Unauthorized();
            var callerScope = this.GetEntityScope();
            var createsAdministrator = request.Roles.Any(role =>
                string.Equals(role?.Trim(), "Admin", StringComparison.OrdinalIgnoreCase));
            // A new Admin is a new key to the whole workspace: confirm it's you first.
            if (createsAdministrator && (await AdminTargetGateAsync(tenantId.Value, Guid.Empty, cancellationToken, always: true)).Refusal is { } stepUp)
                return stepUp;
            if (request.IsGroupScope && !this.HasGroupEntityScope()) return Forbid();
            if (request.IsGroupScope && request.CompanyId.HasValue)
                return BadRequest(new { message = "Choose either group-wide access or one company, not both." });
            // An administrator without an explicit scope is a privilege-escalation ambiguity. Keep
            // legacy non-admin service accounts compatible, while requiring every new Admin to be
            // deliberately bound to either one company or the group.
            if (createsAdministrator && !request.IsGroupScope && !request.CompanyId.HasValue)
                return BadRequest(new { message = "Choose the company this administrator will support, or explicitly grant group-wide access." });
            if (request.CompanyId.HasValue)
            {
                // A group grant may place an administrator in any company of the tenant whichever one
                // is selected in the switcher; a company-scoped caller only in a company they hold.
                if (!this.HasGroupEntityScope() && !callerScope.CanAccessCompany(request.CompanyId.Value)) return Forbid();
                var targetExists = await ScopedBypass.TenantWide(_db.Companies, tenantId.Value,
                        "Validate the authorised administrator target across the tenant company catalog.")
                    .AsNoTracking()
                    .AnyAsync(c => c.Id == request.CompanyId.Value && c.IsActive && !c.IsDeleted, cancellationToken);
                if (!targetExists) return BadRequest(new { message = "Choose an active company in this workspace." });
            }

            // Enforce user limit (shared with the create-company wizard's company super admin). A preflight: the
            // service recounts under the tenant row lock, and a seat lost to a concurrent create answers the same 422.
            if (await UserSeatLimit.CheckAsync(_db, tenantId.Value, cancellationToken) is { } seats)
                return UnprocessableEntity(seats.Body);

            var user = await _accessManagement.CreateUserAsync(tenantId.Value, request, GetContext(), cancellationToken);
            return CreatedAtAction(nameof(GetUser), new { userId = user.Id }, user);
        }
        catch (PrivilegeCeilingException ex) { return await CeilingRefusedAsync(ex, "access.user_created", "User", null); }
        catch (UserSeatLimitReachedException ex) { return UnprocessableEntity(ex.Refusal.Body); }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPut("users/{userId:guid}")]
    public async Task<ActionResult<UserListDto>> UpdateUser(Guid userId, UpdateUserRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var tenantId = GetTenantId();
            if (tenantId is null) return Unauthorized();
            if (GetUserId() is null) return Unauthorized();
            var user = await _accessManagement.UpdateUserAsync(tenantId.Value, userId, request, ManagementScope(), GetContext(), cancellationToken);
            return user is null ? NotFound() : Ok(user);
        }
        catch (PrivilegeCeilingException ex) { return await CeilingRefusedAsync(ex, "access.user_updated", "User", userId.ToString()); }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPatch("users/{userId:guid}/activate")]
    public async Task<IActionResult> ActivateUser(Guid userId, CancellationToken cancellationToken)
    {
        try
        {
            var tenantId = GetTenantId();
            if (tenantId is null) return Unauthorized();
            if (GetUserId() is null) return Unauthorized();
            await _accessManagement.ActivateUserAsync(tenantId.Value, userId, ManagementScope(), GetContext(), cancellationToken);
            return NoContent();
        }
        catch (PrivilegeCeilingException ex) { return await CeilingRefusedAsync(ex, "access.user_activated", "User", userId.ToString()); }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPatch("users/{userId:guid}/suspend")]
    public async Task<IActionResult> SuspendUser(Guid userId, [FromBody] ReasonRequest? body, CancellationToken cancellationToken)
    {
        try
        {
            var tenantId = GetTenantId();
            if (tenantId is null) return Unauthorized();
            if (GetUserId() is null) return Unauthorized();
            var gate = await AdminTargetGateAsync(tenantId.Value, userId, cancellationToken);
            if (gate.Refusal is not null) return gate.Refusal;
            await _accessManagement.SuspendUserAsync(tenantId.Value, userId, body?.Reason ?? string.Empty, ManagementScope(), GetContext(), cancellationToken);
            await NoticeAdminActionAsync(tenantId.Value, gate, "suspended the account of", cancellationToken);
            return NoContent();
        }
        catch (PrivilegeCeilingException ex) { return await CeilingRefusedAsync(ex, "access.user_suspended", "User", userId.ToString()); }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPatch("users/{userId:guid}/lock")]
    public async Task<IActionResult> LockUser(Guid userId, [FromBody] ReasonRequest? body, CancellationToken cancellationToken)
    {
        try
        {
            var tenantId = GetTenantId();
            if (tenantId is null) return Unauthorized();
            if (GetUserId() is null) return Unauthorized();
            var gate = await AdminTargetGateAsync(tenantId.Value, userId, cancellationToken);
            if (gate.Refusal is not null) return gate.Refusal;
            await _accessManagement.LockUserAsync(tenantId.Value, userId, body?.Reason ?? string.Empty, ManagementScope(), GetContext(), cancellationToken);
            await NoticeAdminActionAsync(tenantId.Value, gate, "locked the account of", cancellationToken);
            return NoContent();
        }
        catch (PrivilegeCeilingException ex) { return await CeilingRefusedAsync(ex, "access.user_locked", "User", userId.ToString()); }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPatch("users/{userId:guid}/unlock")]
    public async Task<IActionResult> UnlockUser(Guid userId, CancellationToken cancellationToken)
    {
        try
        {
            var tenantId = GetTenantId();
            if (tenantId is null) return Unauthorized();
            if (GetUserId() is null) return Unauthorized();
            await _accessManagement.UnlockUserAsync(tenantId.Value, userId, ManagementScope(), GetContext(), cancellationToken);
            return NoContent();
        }
        catch (PrivilegeCeilingException ex) { return await CeilingRefusedAsync(ex, "access.user_unlocked", "User", userId.ToString()); }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    /// <summary>
    /// Sets a TEMPORARY password for another user — the in-person alternative to <see cref="IssuePasswordResetLink"/>
    /// when the user cannot receive email. The administrator knows this password, so it can only ever be spent on
    /// choosing a new one: sign-in proves it, opens no session, and asks for the user's own password
    /// (AuthService.LoginAsync, Phase 4a''). Every session, reset link and open challenge ends, and the user is notified.
    ///
    /// AUTHORIZATION: the class-level <c>security.manage</c> and the account ceiling, never yourself, a reason is
    /// required (audited). 400 for an account that cannot take one (no login, SSO, not yet set up, inactive) or a
    /// password the workspace policy rejects; 403 above your ceiling; the caller becomes a credential handler.
    /// </summary>
    [HttpPost("users/{userId:guid}/admin-reset-password")]
    public async Task<IActionResult> AdminResetPassword(Guid userId, AdminResetPasswordRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var tenantId = GetTenantId();
            if (tenantId is null) return Unauthorized();
            if (GetUserId() is null) return Unauthorized();
            if (_authService is null) return StatusCode(StatusCodes.Status503ServiceUnavailable);
            var gate = await AdminTargetGateAsync(tenantId.Value, userId, cancellationToken);
            if (gate.Refusal is not null) return gate.Refusal;
            var context = GetContext();
            await _accessManagement.AuthorizeAdminPasswordSetAsync(tenantId.Value, userId, request.Reason, ManagementScope(), context, cancellationToken);
            await _authService.AdminSetTemporaryPasswordAsync(tenantId.Value, userId, request.NewPassword, request.Reason!.Trim(), context, cancellationToken);
            await NoticeAdminActionAsync(tenantId.Value, gate, "set a temporary password for", cancellationToken);
            return Ok(new
            {
                userId,
                mustChangePassword = true,
                sessionsRevoked = true,
                message = "Temporary password set. Give it to the user in person or over a trusted channel — they must choose their own password when they next sign in. They were signed out everywhere."
            });
        }
        catch (PrivilegeCeilingException ex) { return await CeilingRefusedAsync(ex, "access.admin_password_reset", "User", userId.ToString()); }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    /// <summary>
    /// Issues a single-use password-reset link for a user in the caller's workspace and, when a mail
    /// transport is configured for the workspace, emails it to them.
    ///
    /// AUTHORIZATION: the class-level <c>security.manage</c> requirement — the same permission that
    /// creates a user through <see cref="CreateUser"/>. Anyone who can mint the identity can already
    /// mint a credential for it, so disclosing the link to that caller grants no new authority.
    ///
    /// DISCLOSURE: the raw link is returned to the administrator ONLY when the email did not go out,
    /// which on a workspace with no SMTP is every time. That is the whole point: the alternative is
    /// reporting success over mail that was never sent. Every issuance is audited, and every
    /// response that carried the link is recorded as a disclosure in <c>AdminAuditLogs</c>.
    ///
    /// The link stays single-use and one-hour-lived, is tied to this user and workspace, and is
    /// stored only as a hash — the administrator learns a one-shot link, never a password.
    /// </summary>
    [HttpPost("users/{userId:guid}/password-reset-link")]
    public async Task<IActionResult> IssuePasswordResetLink(Guid userId, CancellationToken cancellationToken,
        [FromServices] Zayra.Api.Infrastructure.Notifications.INotificationService? notifications = null)
    {
        try
        {
            var tenantId = GetTenantId();
            if (tenantId is null) return Unauthorized();

            if (GetUserId() is null) return Unauthorized();
            var gate = await AdminTargetGateAsync(tenantId.Value, userId, cancellationToken);
            if (gate.Refusal is not null) return gate.Refusal;
            var link = await _accessManagement.IssuePasswordResetLinkAsync(
                tenantId.Value, userId, ManagementScope(), GetContext(), cancellationToken);

            var emailDeliveryConfigured = await _emailService.IsConfiguredAsync(tenantId.Value, cancellationToken);
            var emailSent = false;
            var captured = false;
            // WorkEmailSetterRule: the caller set the address this link would go to — never email it.
            if (emailDeliveryConfigured && !link.HandOverInPerson)
            {
                try
                {
                    // F09: the delivery result, not the absence of an exception, decides what the
                    // administrator is told. Test capture mode is "handled" but reaches nobody.
                    var delivery = await _emailService.DeliverAsync(
                        tenantId.Value,
                        link.Email,
                        link.FullName,
                        "Set a new password for your KynexOne account",
                        BuildResetLinkEmailHtml(link),
                        cancellationToken: cancellationToken);
                    emailSent = delivery.ReachedARelay;
                    captured = delivery.Status == EmailDeliveryStatus.Captured;
                    emailDeliveryConfigured = delivery.Status != EmailDeliveryStatus.NotConfigured;
                }
                catch (Exception)
                {
                    // Swallowed on purpose: the token is already committed and the link below is
                    // still valid, so a relay failure must degrade to "copy this link", not to a
                    // 500 that leaves the administrator with no way through. The provider message
                    // is not echoed — it routinely contains the relay host and credentials.
                    emailSent = false;
                }
            }

            var message = link.HandOverInPerson
                ? WorkEmailSetterRule.HandOverMessage
                : emailSent
                ? $"Reset link accepted by the mail server for {link.Email}. It can be used once and expires at {link.ExpiresAtUtc:HH:mm} UTC."
                : captured
                    ? "This server is in test delivery mode, so the email was captured and not sent. Copy the link below and give it to the user directly — it can be used once and expires in 1 hour."
                    : emailDeliveryConfigured
                        ? "Email delivery is configured but the message could not be sent. Copy the link below and give it to the user directly — it can be used once and expires in 1 hour."
                        : "No email delivery is configured for this workspace, so nothing was sent. Copy the link below and give it to the user directly — it can be used once and expires in 1 hour.";

            // A disclosure is a security event in its own right, separate from the issuance audit
            // the service wrote: it records that a human saw a live credential link.
            _db.AdminAuditLogs.Add(new Models.AdminAuditLog
            {
                TenantId = tenantId.Value,
                EntityType = "User",
                EntityId = userId.ToString(),
                Action = emailSent ? "PasswordResetLinkEmailed" : "PasswordResetLinkDisclosedToAdmin",
                OldValuesJson = "{}",
                NewValuesJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    emailDeliveryConfigured,
                    emailSent,
                    linkShownToAdmin = !emailSent,
                    expiresAtUtc = link.ExpiresAtUtc
                }),
                PerformedBy = GetUserId(),
                PerformedByName = User.FindFirstValue("name") ?? GetUserId()?.ToString() ?? "unknown",
                IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? ""
            });
            await _db.SaveChangesAsync(cancellationToken);

            // A link an administrator was shown is a credential someone else holds: the owner is told, so a link they
            // did not ask for is noticed. An emailed link reached them directly and needs no second message.
            if (!emailSent && notifications is not null)
                await notifications.EnqueueAsync(new Zayra.Api.Infrastructure.Notifications.NotificationRequest
                {
                    TenantId = tenantId.Value,
                    UserId = userId,
                    EventCode = "security.password_reset_link_handed_over",
                    EntityName = "User",
                    EntityId = $"{userId:D}:{link.ExpiresAtUtc:yyyyMMddHHmmss}",
                    Title = "A password reset link was created for your account",
                    Message = "An administrator created a one-time link to set a new password on your KynexOne account. It expires in 1 hour. "
                        + "If you did not ask for this, contact your administrator now.",
                }, cancellationToken);

            return Ok(new
            {
                userId = link.UserId,
                email = link.Email,
                expiresAtUtc = link.ExpiresAtUtc,
                emailDeliveryConfigured,
                emailSent,
                // Withheld once the user has it in their inbox; there is no reason for a second copy
                // to sit in an admin's browser or in an API log.
                resetUrl = emailSent ? null : link.ResetUrl,
                handOverInPerson = link.HandOverInPerson,
                message
            });
        }
        catch (PrivilegeCeilingException ex) { return await CeilingRefusedAsync(ex, "access.password_reset_link_issued", "User", userId.ToString()); }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    private static string BuildResetLinkEmailHtml(AdminPasswordResetLinkDto link) => $"""
        <p>Hi {System.Web.HttpUtility.HtmlEncode(link.FullName)},</p>
        <p>An administrator in your workspace asked us to help you set a new password. Click the link below to choose one. This link can be used once and expires in <strong>1 hour</strong>.</p>
        <p><a href="{link.ResetUrl}" style="background:#2563EB;color:#fff;padding:10px 20px;border-radius:6px;text-decoration:none;display:inline-block">Set a new password</a></p>
        <p>If you did not expect this, contact your administrator.</p>
        <hr/>
        <p style="font-size:12px;color:#666">KynexOne Workforce</p>
        """;

    /// <summary>
    /// Resets ANOTHER user's two-step sign-in (lost phone): their factor, sessions, open challenges, trusted browsers
    /// and backup codes all end at once; at their next sign-in they set it up again if it is required for them.
    ///
    /// AUTHORIZATION: the class-level <c>security.manage</c> (the permission of every other account action here) and
    /// the same account ceiling (<see cref="PrivilegeCeiling"/>): nobody resets a user above them. Never yourself.
    /// 400 ERR_MFA_RESET_REASON, 403 ERR_MFA_RESET_SELF or the ceiling code, 404 unknown / another tenant / outside
    /// your company scope, 409 ERR_MFA_NOT_ENABLED. The user is told through the tenant notification outbox.
    /// </summary>
    [HttpPost("users/{userId:guid}/mfa/reset")]
    public async Task<IActionResult> ResetUserMfa(
        Guid userId,
        [FromBody] MfaAdminResetRequest? body,
        [FromServices] Zayra.Api.Infrastructure.Notifications.INotificationService? notifications,
        CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        if (tenantId is null || GetUserId() is null) return Unauthorized();
        var gate = await AdminTargetGateAsync(tenantId.Value, userId, cancellationToken);
        if (gate.Refusal is not null) return gate.Refusal;
        MfaAdminResetResult result;
        try
        {
            result = await _accessManagement.ResetUserMfaAsync(tenantId.Value, userId, body?.Reason, ManagementScope(), GetContext(), cancellationToken);
        }
        catch (PrivilegeCeilingException ex) { return await CeilingRefusedAsync(ex, "auth.mfa.admin_reset", "User", userId.ToString()); }

        switch (result.Outcome)
        {
            case MfaAdminResetOutcome.Reset:
                break;
            case MfaAdminResetOutcome.ReasonInvalid:
                return BadRequest(new { code = MfaErrorCodes.ResetReason, message = "Give a reason of 3 to 500 characters." });
            case MfaAdminResetOutcome.Self:
                return StatusCode(StatusCodes.Status403Forbidden, new
                {
                    code = MfaErrorCodes.ResetSelf,
                    message = "You cannot reset your own two-step sign-in. Ask another administrator.",
                });
            case MfaAdminResetOutcome.NotEnabled:
                return Conflict(new { code = MfaErrorCodes.NotEnabled, message = "Two-step sign-in is already off for this person." });
            default:
                return NotFound();
        }

        // After commit, never inside it: the outbox writes in its own scope and a failure never undoes the reset.
        if (notifications is not null)
            await notifications.EnqueueAsync(new Zayra.Api.Infrastructure.Notifications.NotificationRequest
            {
                TenantId = tenantId.Value,
                UserId = userId,
                EventCode = "security.mfa_admin_reset",
                EntityName = "User",
                EntityId = result.ResetId?.ToString("D") ?? userId.ToString("D"),
                Title = "Your two-step sign-in was reset",
                Message = "An administrator reset two-step sign-in on your KynexOne account and signed you out everywhere. "
                    + "Your trusted browsers and backup codes no longer work. Set up two-step sign-in again the next time you sign in. "
                    + "If you did not ask for this, contact your administrator now.",
            }, cancellationToken);
        await NoticeAdminActionAsync(tenantId.Value, gate, "reset two-step sign-in for", cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// The workspace owner hands ownership to another active Admin (User.IsWorkspaceOwner). Only the owner may, and only
    /// after confirming it's them. The new owner and every Admin are told. 400 when the caller is not the owner or the
    /// new owner is not an active Admin who can sign in.
    /// </summary>
    [HttpPost("users/{userId:guid}/make-owner")]
    public async Task<IActionResult> MakeOwner(Guid userId, CancellationToken cancellationToken)
    {
        try
        {
            var tenantId = GetTenantId();
            if (tenantId is null || GetUserId() is null) return Unauthorized();
            var gate = await AdminTargetGateAsync(tenantId.Value, userId, cancellationToken, always: true);
            if (gate.Refusal is not null) return gate.Refusal;
            await _accessManagement.TransferOwnershipAsync(tenantId.Value, userId, GetContext(), cancellationToken);
            await NoticeAdminActionAsync(tenantId.Value, gate with { TargetIsAdmin = true }, "made the workspace owner:", cancellationToken);
            return NoContent();
        }
        catch (PrivilegeCeilingException ex) { return await CeilingRefusedAsync(ex, "access.ownership_transferred", "User", userId.ToString()); }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    /// <summary>Who an Admin-sensitive action is about, and the refusal when the caller has not confirmed it's them.</summary>
    private sealed record AdminTargetGate(ActionResult? Refusal, bool TargetIsAdmin, Guid TargetUserId, string TargetName);

    /// <summary>
    /// "Confirm it's you" (StepUpService) before any action against an Admin or the workspace owner — or always, for
    /// actions that create one. Fails closed: without a valid X-Step-Up token the answer is 403 step_up_required, and
    /// the page asks for the password (and two-step code) and repeats the request. Never asked for your own account.
    /// </summary>
    private async Task<AdminTargetGate> AdminTargetGateAsync(Guid tenantId, Guid targetUserId, CancellationToken cancellationToken, bool always = false)
    {
        // Through the caller's own scope: a user they cannot see is answered by the action itself (404 / 403), never
        // with a confirmation prompt that would say the account exists and is an Admin.
        var target = targetUserId == Guid.Empty ? null
            : await _accessManagement.GetUserAsync(tenantId, targetUserId, ManagementScope(), cancellationToken);
        var name = target is null ? string.Empty : (string.IsNullOrWhiteSpace(target.FullName) ? target.Email : target.FullName);
        var isAdmin = target is not null
            && (target.IsOwner || target.Roles.Any(role => string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase)));
        var callerId = GetUserId();
        if (callerId is null) return new(Unauthorized(), isAdmin, targetUserId, name);
        if ((!isAdmin && !always) || callerId == targetUserId) return new(null, isAdmin, targetUserId, name);
        if (_stepUp is not null
            && await _stepUp.IsConfirmedAsync(callerId.Value, tenantId, Request.Headers[StepUpService.HeaderName].ToString(), cancellationToken))
            return new(null, isAdmin, targetUserId, name);
        return new(StatusCode(StatusCodes.Status403Forbidden, new
        {
            code = StepUpService.RequiredCode,
            error = StepUpService.RequiredCode,
            message = "Confirm it's you to continue: enter your password, and your two-step code if it is on.",
        }), isAdmin, targetUserId, name);
    }

    /// <summary>
    /// After an action against an Admin: the Admin and every other active Admin are told who did what, so an action
    /// nobody expected is seen at once. Never fails the action it reports.
    /// </summary>
    private async Task NoticeAdminActionAsync(Guid tenantId, AdminTargetGate gate, string what, CancellationToken cancellationToken)
    {
        if (!gate.TargetIsAdmin || _adminNotices is null) return;
        try
        {
            var callerId = GetUserId();
            var actor = await _db.Users.AsNoTracking().Where(u => u.Id == callerId)
                .Select(u => string.IsNullOrWhiteSpace(u.FullName) ? u.Email : u.FullName).FirstOrDefaultAsync(cancellationToken) ?? "An administrator";
            var admins = await _db.Users.AsNoTracking()
                .Where(u => u.TenantId == tenantId && !u.IsDeleted && u.IsActive
                    && u.UserRoles.Any(r => r.Role != null && r.Role.NormalizedName == "ADMIN" && r.Role.IsActive && !r.Role.IsDeleted))
                .Select(u => u.Id).ToListAsync(cancellationToken);
            var sentAt = DateTime.UtcNow;
            foreach (var recipient in admins.Append(gate.TargetUserId).Where(id => id != Guid.Empty && id != callerId).Distinct())
                await _adminNotices.EnqueueAsync(new Zayra.Api.Infrastructure.Notifications.NotificationRequest
                {
                    TenantId = tenantId,
                    UserId = recipient,
                    EventCode = "security.admin_account_action",
                    EntityName = "User",
                    EntityId = $"{gate.TargetUserId:D}:{sentAt:yyyyMMddHHmmssfff}",
                    Title = "An administrator account was changed",
                    Message = $"{actor} {what} {gate.TargetName}. If this was not expected, contact the workspace owner or KynexOne support now.",
                }, cancellationToken);
        }
        catch (Exception)
        {
            // The action is already committed; a notice that could not be queued must not turn it into an error.
        }
    }

    [HttpDelete("users/{userId:guid}")]
    public async Task<IActionResult> DeleteUser(Guid userId, CancellationToken cancellationToken)
    {
        try
        {
            var tenantId = GetTenantId();
            if (tenantId is null) return Unauthorized();
            if (GetUserId() is null) return Unauthorized();
            var gate = await AdminTargetGateAsync(tenantId.Value, userId, cancellationToken);
            if (gate.Refusal is not null) return gate.Refusal;
            if (!await _accessManagement.DeleteUserAsync(tenantId.Value, userId, ManagementScope(), GetContext(), cancellationToken)) return NotFound();
            await NoticeAdminActionAsync(tenantId.Value, gate, "deleted the account of", cancellationToken);
            return NoContent();
        }
        catch (PrivilegeCeilingException ex) { return await CeilingRefusedAsync(ex, "access.user_deleted", "User", userId.ToString()); }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPut("users/{userId:guid}/roles")]
    public async Task<ActionResult<AuthUserDto>> AssignRoles(Guid userId, AssignRolesRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var tenantId = GetTenantId();
            if (tenantId is null) return Unauthorized();
            // Making someone an Admin is as sensitive as acting on one.
            var grantsAdmin = request.Roles?.Any(role => string.Equals(role?.Trim(), "Admin", StringComparison.OrdinalIgnoreCase)) == true;
            var gate = await AdminTargetGateAsync(tenantId.Value, userId, cancellationToken, always: grantsAdmin);
            if (gate.Refusal is not null) return gate.Refusal;
            var assigned = await _accessManagement.AssignRolesAsync(tenantId.Value, userId, request, ManagementScope(), GetContext(), cancellationToken);
            await NoticeAdminActionAsync(tenantId.Value, gate, "changed the roles of", cancellationToken);
            return Ok(assigned);
        }
        catch (PrivilegeCeilingException ex) { return await CeilingRefusedAsync(ex, "access.roles_assigned", "User", userId.ToString()); }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPost("employee-logins/invite")]
    public async Task<ActionResult<EmployeeLoginInvitationDto>> InviteEmployeeLogin(InviteEmployeeLoginRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var tenantId = GetTenantId();
            if (tenantId is null) return Unauthorized();

            // Enforce seat limit — inviting an employee creates or reactivates a login user.
            var sub = await _db.TenantSubscriptions
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.TenantId == tenantId, cancellationToken);

            if (sub is not null && sub.MaxUsers > 0)
            {
                var count = await _db.Users.CountAsync(u => u.TenantId == tenantId && u.IsActive && !u.IsDeleted, cancellationToken);
                if (count >= sub.MaxUsers)
                    return UnprocessableEntity(new
                    {
                        error = "user_limit_reached",
                        message = $"You have reached your user limit ({count}/{sub.MaxUsers}). Please contact your administrator to upgrade.",
                        current = count,
                        limit = sub.MaxUsers
                    });
            }

            var invite = await _accessManagement.InviteEmployeeLoginAsync(
                tenantId.Value,
                request,
                this.GetEntityScope(),
                GetContext(),
                cancellationToken);

            // DELIVERY TRUTH. Before this, the invitation was minted and the response said nothing
            // at all about email — so a caller could only assume one had gone out. On a workspace
            // with no SMTP (the production default) nothing ever did. The invitation is now actually
            // emailed when a transport exists, and the response states plainly which happened.
            invite = await AttachInvitationDeliveryAsync(tenantId.Value, invite, cancellationToken);

            // The raw token never leaves the server; the link is handed back ONLY when nothing reached the
            // invitee, and then the inviter has held a credential for this login — recorded, so the two-person
            // rule (AccessManagementService.TwoPersonRefusalAsync) never lets them link it to an employee.
            var disclosed = !invite.EmailSent && !string.IsNullOrEmpty(invite.InvitationUrl);
            if (disclosed)
                await RecordInvitationLinkDisclosedAsync(tenantId.Value, invite.UserId, invite.EmployeeId,
                    invite.EmailDeliveryConfigured, invite.InvitationExpiresAtUtc, "invite", cancellationToken);
            invite = invite with
            {
                InvitationToken = string.Empty,
                InvitationUrl = disclosed ? invite.InvitationUrl : string.Empty
            };
            return Created($"/api/access/users/{invite.UserId}", invite);
        }
        catch (PrivilegeCeilingException ex) { return await CeilingRefusedAsync(ex, "access.employee_invited", "Employee", request.EmployeeId.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        catch (WorkEmailConfirmationRequiredException ex)
        {
            return BadRequest(new { error = ex.Code, code = ex.Code, message = ex.Message, messageAr = ex.MessageAr });
        }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    /// <summary>
    /// Where one employee record stands on the way to Self-Service: the login linked to it, an unlinked login
    /// carrying its work email, and the one next step (linked | link_existing | invite | needs_work_email | blocked).
    /// </summary>
    [HttpGet("employee-logins/{employeeId:int}")]
    public async Task<ActionResult<EmployeeLoginStatusDto>> EmployeeLoginStatus(int employeeId, CancellationToken cancellationToken)
    {
        try
        {
            var tenantId = GetTenantId();
            if (tenantId is null || GetUserId() is null) return Unauthorized();
            var status = await _accessManagement.GetEmployeeLoginStatusAsync(
                tenantId.Value, employeeId, this.GetEntityScope(), GetContext(), cancellationToken);
            return status is null ? NotFound(new { message = "Employee not found." }) : Ok(status);
        }
        catch (PrivilegeCeilingException ex) { return Refused(ex); }
    }

    /// <summary>
    /// Links an existing, active login to an employee record whose work email it carries, so the next sign-in
    /// carries employee_id and Self-Service works. Does not use a seat: the login is already one.
    /// </summary>
    [HttpPost("employee-logins/link-existing")]
    public async Task<ActionResult<EmployeeLoginLinkResultDto>> LinkExistingLogin(LinkExistingLoginRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var tenantId = GetTenantId();
            if (tenantId is null || GetUserId() is null) return Unauthorized();
            var linked = await _accessManagement.LinkExistingLoginAsync(
                tenantId.Value, request, this.GetEntityScope(), GetContext(), cancellationToken);
            if (!linked.CredentialReset || string.IsNullOrEmpty(linked.InvitationUrl)) return Ok(linked with { InvitationUrl = null });

            // The link rotated the credential: deliver the fresh invitation exactly as the invite endpoint does.
            var delivered = await AttachInvitationDeliveryAsync(tenantId.Value, new EmployeeLoginInvitationDto(
                linked.UserId, linked.EmployeeId, linked.Email, linked.AccessMode, linked.Status, string.Empty,
                linked.InvitationExpiresAtUtc, linked.InvitationUrl) { HandOverInPerson = linked.HandOverInPerson }, cancellationToken);
            if (!delivered.EmailSent)
                await RecordInvitationLinkDisclosedAsync(tenantId.Value, linked.UserId, linked.EmployeeId,
                    delivered.EmailDeliveryConfigured, linked.InvitationExpiresAtUtc, "link_credential_reset", cancellationToken);
            return Ok(linked with
            {
                InvitationUrl = delivered.EmailSent ? null : linked.InvitationUrl,
                EmailSent = delivered.EmailSent,
                DeliveryMessage = delivered.DeliveryMessage,
            });
        }
        catch (PrivilegeCeilingException ex) { return await CeilingRefusedAsync(ex, "access.employee_login_linked", "User", request.UserId.ToString()); }
        catch (AccessTargetNotFoundException ex) { return NotFound(new { message = ex.Message }); }
        catch (EmployeeLinkRefusedException ex)
        {
            var body = new { message = ex.Refusal.Message, code = ex.Refusal.Code, subject = ex.Refusal.Subject };
            return ex.Refusal.StatusCode == StatusCodes.Status400BadRequest ? BadRequest(body) : StatusCode(ex.Refusal.StatusCode, body);
        }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    /// <summary>
    /// An invitation link was handed back to the caller instead of reaching the invitee: the caller has held a
    /// credential for that login. Recorded so the two-person rule never lets them link it to an employee.
    /// </summary>
    private async Task RecordInvitationLinkDisclosedAsync(Guid tenantId, Guid userId, int employeeId, bool emailDeliveryConfigured,
        DateTime? expiresAtUtc, string source, CancellationToken cancellationToken)
    {
        _db.AuditLogs.Add(AuthAuditEntry.Create(
            Guid.NewGuid(),
            DateTime.UtcNow,
            AccessManagementService.InvitationLinkDisclosedAction,
            "User",
            userId.ToString(),
            GetContext() with { TenantId = tenantId },
            System.Text.Json.JsonSerializer.Serialize(new { employeeId, emailDeliveryConfigured, expiresAtUtc, source })));
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task<EmployeeLoginInvitationDto> AttachInvitationDeliveryAsync(
        Guid tenantId, EmployeeLoginInvitationDto invite, CancellationToken cancellationToken)
    {
        // A no-login mapping has no invitation to deliver; saying "no email was sent" about it would
        // be its own small untruth.
        if (string.IsNullOrEmpty(invite.InvitationUrl))
            return invite with
            {
                DeliveryMessage = "This person was given employee access without a portal login, so no invitation was sent."
            };

        // WorkEmailSetterRule: the caller set the address — the invitation is handed back, never emailed.
        if (invite.HandOverInPerson)
            return invite with
            {
                EmailDeliveryConfigured = await _emailService.IsConfiguredAsync(tenantId, cancellationToken),
                EmailSent = false,
                DeliveryMessage = WorkEmailSetterRule.HandOverMessage,
            };

        var emailDeliveryConfigured = await _emailService.IsConfiguredAsync(tenantId, cancellationToken);
        var emailSent = false;
        var captured = false;
        if (emailDeliveryConfigured)
        {
            try
            {
                // F09: see IssuePasswordResetLink — the delivery result decides the message.
                var delivery = await _emailService.DeliverAsync(
                    tenantId,
                    invite.Email,
                    invite.Email,
                    "You have been invited to KynexOne",
                    $"""
                    <p>Hello,</p>
                    <p>You have been invited to your organisation's KynexOne workspace. Click the link below to accept the invitation and set your password.</p>
                    <p><a href="{invite.InvitationUrl}" style="background:#2563EB;color:#fff;padding:10px 20px;border-radius:6px;text-decoration:none;display:inline-block">Accept invitation</a></p>
                    <hr/>
                    <p style="font-size:12px;color:#666">KynexOne Workforce</p>
                    """,
                    cancellationToken: cancellationToken);
                emailSent = delivery.ReachedARelay;
                captured = delivery.Status == EmailDeliveryStatus.Captured;
                emailDeliveryConfigured = delivery.Status != EmailDeliveryStatus.NotConfigured;
            }
            catch (Exception)
            {
                // See IssuePasswordResetLink: the invitation is already committed and its link is in
                // this response, so a relay failure degrades to "share the link", never to a 500.
                emailSent = false;
            }
        }

        return invite with
        {
            EmailDeliveryConfigured = emailDeliveryConfigured,
            EmailSent = emailSent,
            DeliveryMessage = emailSent
                ? $"Invitation accepted by the mail server for {invite.Email}."
                : captured
                    ? "This server is in test delivery mode, so the invitation email was captured and not sent. Share the invitation link with them directly."
                    : emailDeliveryConfigured
                        ? "Email delivery is configured but the invitation could not be sent. Share the invitation link with them directly."
                        : "No email delivery is configured for this workspace, so no invitation was sent. Share the invitation link with them directly."
        };
    }

    [HttpGet("users/{userId:guid}/access")]
    public async Task<ActionResult<UserAccessDto>> UserAccess(Guid userId, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        if (tenantId is null) return Unauthorized();
        var access = await _accessManagement.GetUserAccessAsync(tenantId.Value, userId, ManagementScope(), cancellationToken);
        return access is null ? NotFound() : Ok(access);
    }

    [HttpPut("users/{userId:guid}/access-mode")]
    public async Task<ActionResult<UserAccessDto>> SetAccessMode(Guid userId, AccessModeRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var tenantId = GetTenantId();
            if (tenantId is null) return Unauthorized();
            if (GetUserId() is null) return Unauthorized();
            var gate = await AdminTargetGateAsync(tenantId.Value, userId, cancellationToken);
            if (gate.Refusal is not null) return gate.Refusal;
            var access = await _accessManagement.SetAccessModeAsync(tenantId.Value, userId, request, ManagementScope(), GetContext(), cancellationToken);
            if (access is null) return NotFound();
            await NoticeAdminActionAsync(tenantId.Value, gate, "changed the sign-in access of", cancellationToken);
            return Ok(access);
        }
        catch (PrivilegeCeilingException ex) { return await CeilingRefusedAsync(ex, "access.mode_changed", "User", userId.ToString()); }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPost("users/{userId:guid}/permission-overrides")]
    public async Task<ActionResult<UserAccessDto>> SetPermissionOverride(Guid userId, PermissionOverrideRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var tenantId = GetTenantId();
            if (tenantId is null) return Unauthorized();
            var access = await _accessManagement.SetPermissionOverrideAsync(tenantId.Value, userId, request, ManagementScope(), GetContext(), cancellationToken);
            return access is null ? NotFound() : Ok(access);
        }
        catch (PrivilegeCeilingException ex) { return await CeilingRefusedAsync(ex, "access.permission_override", "User", userId.ToString()); }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpGet("employees/{managerEmployeeId:int}/team")]
    public async Task<ActionResult<IReadOnlyCollection<EmployeeTeamMemberDto>>> Team(int managerEmployeeId, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        if (tenantId is null) return Unauthorized();
        return Ok(await _accessManagement.GetTeamAsync(tenantId.Value, managerEmployeeId, cancellationToken));
    }

    [HttpGet("approval-delegations")]
    public async Task<ActionResult<IReadOnlyCollection<ApprovalDelegationDto>>> ApprovalDelegations(CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        if (tenantId is null) return Unauthorized();
        return Ok(await _accessManagement.GetDelegationsAsync(tenantId.Value, cancellationToken));
    }

    // ── Approval delegation and approval authority: writes refuse ─────────────────────────────
    //
    // Both of these are configuration a tenant could store and nothing has ever read. They now
    // answer 501 on every write, following the rule this codebase already states in
    // ApprovalPoliciesController — "accepting a write here would recreate exactly the silent
    // misconfiguration F1 removes: configuration that is stored but never applied" — and the status
    // EnterpriseIdentityController uses for the same reason: 501 for never-built, not 410 for
    // retired, because neither of these was ever wired.
    //
    // The GETs are deliberately left alive. A tenant that already stored rows must still be able to
    // see them; hiding the data would be a second deception on top of the first.

    /// <summary>The reason a delegation write refuses. Public so the test and the API agree on one string.</summary>
    public const string DelegationNotImplementedMessage =
        "Approval delegation is not implemented in this build. Delegations were stored and never consulted: "
        + "no approval router, workflow service or leave path has ever read them, so an approver who recorded "
        + "a delegation before going on leave would have had their queue stall anyway, with the UI showing the "
        + "delegation as active. Accepting the write would restate that. Reassign the approver on the approval "
        + "workflow's step, or deactivate the employee to reroute their pending approvals.";

    /// <summary>The reason an approval-authority write refuses.</summary>
    public const string AuthorityNotImplementedMessage =
        "Approval authority limits are not enforced in this build. An amount limit has nothing to compare "
        + "against: ApprovalRequest carries no monetary amount, so 'department managers may approve up to "
        + "SAR 50,000' could never have been applied and a manager could final-approve any sum with the audit "
        + "log recording a legitimate approval. Storing the rule would be evidence of a control that does not "
        + "exist. Express approval seniority as ordered steps on the approval workflow instead.";

    [HttpPost("approval-delegations")]
    public IActionResult CreateApprovalDelegation(ApprovalDelegationRequest request)
        => StatusCode(StatusCodes.Status501NotImplemented, new
        {
            code = "approval_delegation_not_implemented",
            message = DelegationNotImplementedMessage,
            replacement = "/api/approval-workflows",
        });

    [HttpGet("approval-authorities")]
    public async Task<ActionResult<IReadOnlyCollection<ApprovalAuthorityDto>>> ApprovalAuthorities(CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        if (tenantId is null) return Unauthorized();
        return Ok(await _accessManagement.GetAuthoritiesAsync(tenantId.Value, cancellationToken));
    }

    [HttpPost("approval-authorities")]
    public IActionResult CreateApprovalAuthority(ApprovalAuthorityRequest request)
        => StatusCode(StatusCodes.Status501NotImplemented, new
        {
            code = "approval_authority_not_implemented",
            message = AuthorityNotImplementedMessage,
            replacement = "/api/approval-workflows",
        });

    // Cancel stays live: it is the one write that makes an already-stored delegation LESS
    // believable, which is the direction this change is going in. Refusing it would leave a tenant
    // holding rows the UI shows as active with no way to clear them.
    [HttpPatch("approval-delegations/{delegationId:guid}/cancel")]
    public async Task<IActionResult> CancelDelegation(Guid delegationId, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        if (tenantId is null) return Unauthorized();
        var found = await _accessManagement.CancelDelegationAsync(tenantId.Value, delegationId, GetContext(), cancellationToken);
        return found ? NoContent() : NotFound();
    }

    [HttpPut("approval-authorities/{authorityId:guid}")]
    public IActionResult UpdateApprovalAuthority(Guid authorityId, ApprovalAuthorityRequest request)
        => StatusCode(StatusCodes.Status501NotImplemented, new
        {
            code = "approval_authority_not_implemented",
            message = AuthorityNotImplementedMessage,
            replacement = "/api/approval-workflows",
        });

    // ── Permission Grantors ───────────────────────────────────────────────────

    [HttpGet("permission-grantors")]
    public async Task<ActionResult<IReadOnlyCollection<PermissionGrantorDto>>> GetGrantors(CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        if (tenantId is null) return Unauthorized();
        return Ok(await _accessManagement.GetGrantorsAsync(tenantId.Value, cancellationToken));
    }

    [HttpPost("permission-grantors")]
    public async Task<ActionResult<PermissionGrantorDto>> AddGrantor(AddGrantorRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var tenantId = GetTenantId();
            if (tenantId is null) return Unauthorized();
            return Ok(await _accessManagement.AddGrantorAsync(tenantId.Value, request, GetContext(), cancellationToken));
        }
        catch (PrivilegeCeilingException ex) { return await CeilingRefusedAsync(ex, "access.grantor_added", "User", request.GrantorUserId.ToString()); }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpDelete("permission-grantors/{recordId:guid}")]
    public async Task<IActionResult> RevokeGrantor(Guid recordId, CancellationToken cancellationToken)
    {
        try
        {
            var tenantId = GetTenantId();
            if (tenantId is null || GetUserId() is null) return Unauthorized();
            var found = await _accessManagement.RevokeGrantorAsync(tenantId.Value, recordId, GetContext(), cancellationToken);
            return found ? NoContent() : NotFound();
        }
        catch (PrivilegeCeilingException ex) { return await CeilingRefusedAsync(ex, "access.grantor_revoked", "PermissionGrantorRecord", recordId.ToString()); }
    }

    // Not restricted to Admin role — service layer checks grantor authority or Admin claim.
    // Plain [Authorize] overrides the class-level Admin-only policy while still requiring
    // an authenticated user (AllowAnonymous here would open a permission-grant endpoint
    // to unauthenticated callers).
    [HttpPost("users/{userId:guid}/grant-permission")]
    [Microsoft.AspNetCore.Authorization.Authorize]
    public async Task<ActionResult<UserAccessDto>> GrantPermission(Guid userId, GrantPermissionRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var tenantId = GetTenantId();
            if (tenantId is null) return Unauthorized();
            var isAdmin = User.IsInRole("Admin");
            var access = await _accessManagement.GrantPermissionAsync(tenantId.Value, userId, request, ManagementScope(), GetUserId(), isAdmin, cancellationToken);
            return access is null ? NotFound() : Ok(access);
        }
        catch (PrivilegeCeilingException ex) { return await CeilingRefusedAsync(ex, "access.permission_granted", "User", userId.ToString()); }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    // Atomic bulk grant/deny/reset of many permission overrides (powers the Overrides "Allow all / Deny all / Reset all").
    // Same [Authorize] posture as grant-permission: overrides the class Admin-only policy so designated grantors work too,
    // while the service enforces Admin-or-in-scope authority (all-or-nothing) and audits every affected key.
    [HttpPost("users/{userId:guid}/grant-permissions-bulk")]
    [Microsoft.AspNetCore.Authorization.Authorize]
    public async Task<ActionResult<UserAccessDto>> GrantPermissionsBulk(Guid userId, BulkGrantPermissionsRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var tenantId = GetTenantId();
            if (tenantId is null) return Unauthorized();
            var isAdmin = User.IsInRole("Admin");
            var access = await _accessManagement.GrantPermissionsBulkAsync(tenantId.Value, userId, request, ManagementScope(), GetUserId(), isAdmin, cancellationToken);
            return access is null ? NotFound() : Ok(access);
        }
        catch (PrivilegeCeilingException ex) { return await CeilingRefusedAsync(ex, "access.permission_bulk_grant", "User", userId.ToString()); }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpGet("security-settings")]
    public async Task<ActionResult<SecuritySettingDto>> GetSecuritySettings(CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        if (tenantId is null) return Unauthorized();
        var gate = RequireGroupEntityScope();
        if (gate is not null) return gate;
        return Ok(await _accessManagement.GetSecuritySettingsAsync(tenantId.Value, cancellationToken));
    }

    [HttpPut("security-settings")]
    public async Task<ActionResult<SecuritySettingDto>> UpdateSecuritySettings(UpdateSecuritySettingRequest request, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        if (tenantId is null) return Unauthorized();
        var gate = RequireGroupEntityScope();
        if (gate is not null) return gate;
        if (request.MfaTrustedDeviceDays is < 0 or > Models.SecuritySetting.MaxMfaTrustedDeviceDays)
            return BadRequest(new
            {
                code = MfaErrorCodes.TrustDaysRange,
                message = "Trusted browser days must be between 0 (off) and 90.",
            });
        if (request.SessionMaxHours is < Models.SecuritySetting.MinSessionMaxHours or > Models.SecuritySetting.MaxSessionMaxHours)
            return BadRequest(new
            {
                code = SessionErrorCodes.MaxHoursRange,
                message = "Maximum session length must be between 1 and 168 hours.",
            });
        try
        {
            return Ok(await _accessManagement.UpdateSecuritySettingsAsync(tenantId.Value, request, GetContext(), cancellationToken));
        }
        catch (MfaPolicyException ex)
        {
            return BadRequest(new { code = ex.Code, message = ex.Message });
        }
    }

    /// <summary>The coded, bilingual 403 for a PrivilegeCeiling refusal.</summary>
    private ObjectResult Refused(PrivilegeCeilingException ex) => StatusCode(StatusCodes.Status403Forbidden, new
    {
        error = ex.Refusal.Code,
        code = ex.Refusal.Code,
        message = ex.Refusal.MessageEn,
        messageAr = ex.Refusal.MessageAr,
        role = ex.Refusal.Role,
        missingPermissions = ex.Refusal.MissingPermissions,
    });

    /// <summary>
    /// Answers a refused access change with 403 and records the attempt: an escalation that was stopped is a
    /// security event in its own right. The refused operation rolled back, so the audit row is written on its own.
    /// </summary>
    private async Task<ObjectResult> CeilingRefusedAsync(PrivilegeCeilingException ex, string attemptedAction, string entityName, string? entityId)
    {
        var tenantId = GetTenantId();
        if (tenantId is not null)
        {
            _db.ChangeTracker.Clear();
            _db.AuditLogs.Add(Zayra.Api.Infrastructure.Auth.AuthAuditEntry.Create(
                Guid.NewGuid(),
                DateTime.UtcNow,
                "access.change_refused",
                entityName,
                entityId,
                GetContext() with { TenantId = tenantId },
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    attempted = attemptedAction,
                    code = ex.Refusal.Code,
                    role = ex.Refusal.Role,
                    missingPermissions = ex.Refusal.MissingPermissions,
                })));
            await _db.SaveChangesAsync(HttpContext?.RequestAborted ?? CancellationToken.None);
        }
        return Refused(ex);
    }

    private RequestContext GetContext()
    {
        return new RequestContext(HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString(), GetUserId(), GetTenantId());
    }

    private Guid? GetUserId()
    {
        var value = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        return Guid.TryParse(value, out var id) ? id : null;
    }

    private Guid? GetTenantId()
    {
        var value = User.FindFirstValue("tenant_id");
        return Guid.TryParse(value, out var id) ? id : null;
    }

    /// <summary>
    /// Tenant-level security administration (roles, permission matrix, security settings, group
    /// scope) needs a GROUP grant. Judged on the caller's grant, not on <c>GetEntityScope()</c>: the
    /// X-Company-Id switcher narrows a group administrator's reads to one company, it does not revoke
    /// their tenant-level authority. A company-scoped administrator has no group grant either way.
    /// </summary>
    /// <summary>
    /// The scope single-user administration is judged on: the caller's GRANT. A group administrator
    /// manages every user of the tenant whichever company the switcher selects (the user they just
    /// created for another company included); a company-scoped administrator only the users of the
    /// companies they hold. The user LIST stays on the request's read scope, like every list.
    /// </summary>
    private EntityScopeContext ManagementScope() =>
        this.HasGroupEntityScope() ? EntityScopeContext.GroupLevel : this.GetEntityScope();

    private ActionResult? RequireGroupEntityScope()
    {
        return this.HasGroupEntityScope() ? null : Forbid();
    }

    // ── Entity Access (Legal Entity Grants) ──────────────────────────────────────

    [HttpGet("entity-grants")]
    public async Task<ActionResult<IReadOnlyCollection<EntityGrantDto>>> ListEntityGrants([FromQuery] Guid? userId, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        if (tenantId is null) return Unauthorized();
        var scope = this.GetEntityScope();
        var query = _db.UserEntityAccesses.AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.IsActive);
        // Judged on the grant: a group administrator sees every grant whichever company the switcher
        // selects; a company-scoped administrator only grants for the companies they hold.
        if (!this.HasGroupEntityScope())
        {
            var accessible = scope.AccessibleCompanyIds.ToList();
            query = query.Where(e =>
                e.GrantMode == EntityGrantModes.SelectedCompanies
                && e.CompanyId.HasValue
                && accessible.Contains(e.CompanyId.Value));
        }
        if (userId.HasValue) query = query.Where(e => e.UserId == userId.Value);
        var results = await query
            .OrderBy(e => e.UserId).ThenBy(e => e.CompanyId)
            .Select(e => new EntityGrantDto(e.Id, e.UserId, e.CompanyId, e.Role, e.CreatedAtUtc, e.GrantMode))
            .ToListAsync(cancellationToken);
        return Ok(results);
    }

    [HttpPost("entity-grants")]
    public async Task<ActionResult<EntityGrantDto>> CreateEntityGrant([FromBody] CreateEntityGrantRequest request, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        var actorId = GetUserId();
        if (tenantId is null || actorId is null) return Unauthorized();
        var scope = this.GetEntityScope();

        // Validate target user belongs to this tenant
        if (!await _db.Users.AnyAsync(u => u.Id == request.UserId && u.TenantId == tenantId && !u.IsDeleted, cancellationToken))
            return BadRequest(new { message = "User not found in this tenant." });

        // Validate grant mode (Phase 2): Selected requires a company; all-company modes must not pin one.
        var grantMode = request.GrantMode ?? EntityGrantModes.SelectedCompanies;
        if (!EntityGrantModes.IsValid(grantMode))
            return BadRequest(new { message = "GrantMode must be SelectedCompanies, AllCurrentCompanies or AllCurrentAndFutureCompanies." });
        if (grantMode == EntityGrantModes.SelectedCompanies && !request.CompanyId.HasValue)
            return BadRequest(new { message = "SelectedCompanies grants require a CompanyId." });
        if (grantMode != EntityGrantModes.SelectedCompanies && request.CompanyId.HasValue)
            return BadRequest(new { message = "All-company grant modes must not specify a CompanyId." });

        if (request.UserId == actorId)
            return Forbid();
        // PRIVILEGE CEILING: company access is access; nobody widens the company scope of a user above them.
        try { await _accessManagement.AssertMayChangeUserAccessAsync(tenantId.Value, request.UserId, GetContext(), cancellationToken); }
        catch (PrivilegeCeilingException ex) { return await CeilingRefusedAsync(ex, "access.entity_grant_created", "User", request.UserId.ToString()); }
        // A group grant may grant any company of the tenant (or all of them) whichever company the
        // switcher selects; a company-scoped administrator only a company within their own grant.
        if (!this.HasGroupEntityScope())
        {
            if (grantMode != EntityGrantModes.SelectedCompanies)
                return Forbid();
            if (!request.CompanyId.HasValue || !scope.CanAccessCompany(request.CompanyId))
                return Forbid();
            var target = await _accessManagement.GetUserAsync(tenantId.Value, request.UserId, scope, cancellationToken);
            if (target is null) return NotFound();
        }

        // Validate company if specified
        if (request.CompanyId.HasValue && !await _db.Companies.AnyAsync(c => c.Id == request.CompanyId.Value && c.TenantId == tenantId && !c.IsDeleted, cancellationToken))
            return BadRequest(new { message = "Company not found in this tenant." });

        // The one grant writer (also used by the create-company wizard): duplicates, token revocation, audit.
        UserEntityAccess? grant;
        try { grant = await _accessManagement.CreateEntityGrantAsync(tenantId.Value, request.UserId, request.CompanyId, request.Role, grantMode, GetContext(), cancellationToken); }
        catch (PrivilegeCeilingException ex) { return await CeilingRefusedAsync(ex, "access.entity_grant_created", "User", request.UserId.ToString()); }
        if (grant is null)
            return Conflict(new { message = "This entity grant already exists." });
        return CreatedAtAction(nameof(ListEntityGrants), new EntityGrantDto(grant.Id, grant.UserId, grant.CompanyId, grant.Role, grant.CreatedAtUtc, grant.GrantMode));
    }

    [HttpDelete("entity-grants/{id:guid}")]
    public async Task<IActionResult> DeleteEntityGrant(Guid id, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        if (tenantId is null) return Unauthorized();
        var gate = RequireGroupEntityScope();
        if (gate is not null) return gate;
        var grant = await _db.UserEntityAccesses.FirstOrDefaultAsync(e => e.Id == id && e.TenantId == tenantId, cancellationToken);
        if (grant is null) return NotFound();
        if (GetUserId() is null) return Unauthorized();
        // PRIVILEGE CEILING: removing company access of yourself or of a user above you is refused too.
        try { await _accessManagement.AssertMayChangeUserAccessAsync(tenantId.Value, grant.UserId, GetContext(), cancellationToken); }
        catch (PrivilegeCeilingException ex) { return await CeilingRefusedAsync(ex, "access.entity_grant_deleted", "User", grant.UserId.ToString()); }
        grant.IsActive = false;
        grant.UpdatedAtUtc = DateTime.UtcNow;
        await RevokeActiveRefreshTokensAsync(grant.UserId, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    // Elevate or restrict a user's group-scope flag (cross-company visibility).
    // Class-level [Authorize(Roles = "Admin")] applies — no [AllowAnonymous] here.
    [HttpPatch("users/{userId:guid}/group-scope")]
    public async Task<IActionResult> SetGroupScope(Guid userId, [FromBody] SetGroupScopeRequest request, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        var actorId = GetUserId();
        if (tenantId is null || actorId is null) return Unauthorized();
        var gate = RequireGroupEntityScope();
        if (gate is not null) return gate;
        if (userId == actorId) return Forbid();

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId && u.TenantId == tenantId && !u.IsDeleted, cancellationToken);
        if (user is null) return NotFound();
        // PRIVILEGE CEILING: group scope is cross-company visibility; not for a user above the caller.
        try { await _accessManagement.AssertMayChangeUserAccessAsync(tenantId.Value, userId, GetContext(), cancellationToken); }
        catch (PrivilegeCeilingException ex) { return await CeilingRefusedAsync(ex, request.IsGroupScope ? "access.group_scope_granted" : "access.group_scope_revoked", "User", userId.ToString()); }

        var oldValue = user.IsGroupScope;
        user.IsGroupScope = request.IsGroupScope;
        user.UpdatedAtUtc = DateTime.UtcNow;

        _db.AdminAuditLogs.Add(new Models.AdminAuditLog
        {
            TenantId = tenantId.Value,
            EntityType = "User",
            EntityId = userId.ToString(),
            Action = request.IsGroupScope ? "GroupScopeGranted" : "GroupScopeRevoked",
            OldValuesJson = System.Text.Json.JsonSerializer.Serialize(new { isGroupScope = oldValue }),
            NewValuesJson = System.Text.Json.JsonSerializer.Serialize(new { isGroupScope = request.IsGroupScope }),
            PerformedBy = actorId,
            PerformedByName = User.FindFirstValue("name") ?? actorId.ToString()!,
        });

        await RevokeActiveRefreshTokensAsync(userId, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    private async Task RevokeActiveRefreshTokensAsync(Guid userId, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var activeTokens = await _db.RefreshTokens
            .Where(x => x.UserId == userId && x.RevokedAtUtc == null && x.ExpiresAtUtc > now)
            .ToListAsync(cancellationToken);
        foreach (var token in activeTokens)
        {
            token.RevokedAtUtc = now;
            token.RevokedByIp = ip;
        }
    }
}

public record SetGroupScopeRequest(bool IsGroupScope);

public record EntityGrantDto(Guid Id, Guid UserId, Guid? CompanyId, string Role, DateTime CreatedAtUtc, string GrantMode = EntityGrantModes.SelectedCompanies);
// GrantMode: SelectedCompanies (default, requires CompanyId) | AllCurrentCompanies | AllCurrentAndFutureCompanies
public record CreateEntityGrantRequest(Guid UserId, Guid? CompanyId, string Role, string? GrantMode = null);
