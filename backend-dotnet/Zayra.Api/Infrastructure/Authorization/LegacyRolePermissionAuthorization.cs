using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace Zayra.Api.Infrastructure.Authorization;

/// <summary>
/// Compatibility boundary for controllers that still carry historical role-name gates.
/// A role gate is never sufficient on its own: the endpoint's effective permission must
/// also be present, so a per-user Deny remains authoritative. A custom role carrying the
/// same permission can satisfy the legacy role requirement during the migration window.
/// </summary>
public sealed class PermissionAwareRolesAuthorizationHandler
    : AuthorizationHandler<RolesAuthorizationRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, RolesAuthorizationRequirement requirement)
    {
        if (context.Resource is HttpContext http
            && LegacyRolePermissionResolver.Resolve(http) is { } permission
            && HasPermission(context.User, permission))
            context.Succeed(requirement);
        return Task.CompletedTask;
    }

    internal static bool HasPermission(ClaimsPrincipal user, string permission) =>
        user.Claims.Any(c => c.Type == "permission"
                             && string.Equals(c.Value, permission, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Post-authorization Deny guard for built-in role claims accepted by ASP.NET's default handler.</summary>
public sealed class PermissionAwareAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _fallback = new();

    public Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult result)
    {
        var required = LegacyRolePermissionResolver.Resolve(context);
        if (required is not null
            && context.User.Identity?.IsAuthenticated == true
            && !PermissionAwareRolesAuthorizationHandler.HasPermission(context.User, required))
            result = PolicyAuthorizationResult.Forbid();
        return _fallback.HandleAsync(next, context, policy, result);
    }
}

public static class LegacyRolePermissionResolver
{
    private sealed record ModulePermissions(string Read, string Write, string? Approve = null, string? Delete = null, string? Manage = null, string? Export = null, string? Import = null);

    private static readonly IReadOnlyDictionary<string, ModulePermissions> Modules =
        new Dictionary<string, ModulePermissions>(StringComparer.OrdinalIgnoreCase)
        {
            ["GroupDashboard"] = new("dashboard.read", "dashboard.read", Export: "dashboard.export"),
            ["AuditLogs"] = new("audit.read", "audit.read", Export: "audit.export"),
            ["Attendance"] = new("attendance.read", "attendance.write", "attendance.lock", "attendance.delete", "attendance.write", "attendance.read", "attendance.bulk_import"),
            ["Shifts"] = new("shifts.read", "shifts.write", Manage: "shifts.manage"),
            ["Overtime"] = new("overtime.read", "overtime.write", "overtime.approve", Manage: "overtime.policy_manage"),
            ["Payroll"] = new("payroll.read", "payroll.write", "payroll.approve", "payroll.run_delete", "payroll.structure_manage", "payroll.export"),
            ["PayslipTemplates"] = new("payroll.read", "payroll.write", Manage: "payroll.structure_manage", Export: "payroll.export"),
            ["Gosi"] = new("payroll.read", "payroll.write", "payroll.approve", Manage: "payroll.rates.manage", Export: "payroll.export"),
            ["StatutoryRules"] = new("payroll.rates.read", "payroll.rates.manage", Manage: "payroll.rates.manage"),
            ["Reports"] = new("reports.read", "reports.schedule", Manage: "reports.schedule", Export: "reports.export"),
            ["Analytics"] = new("reports.read", "reports.read", Export: "reports.export"),
            ["AIAssistant"] = new("ai.insights_view", "ai.query", Manage: "ai.query"),
            ["PolicyDocument"] = new("ai.query", "ai.query", Manage: "ai.query"),
            ["ApprovalWorkflows"] = new("approvals.read", "approvals.write", "approvals.decide", Manage: "approvals.manage"),
            ["ApprovalRequests"] = new("approvals.read", "approvals.write", "approvals.decide", Manage: "approvals.manage"),
            ["ApprovalPolicies"] = new("approvals.read", "approvals.manage", Manage: "approvals.manage"),
            ["EmployeeSelfService"] = new("ess.read", "ess.write", "manager.approve"),
            ["HRRequestCenter"] = new("employees.read", "employees.write", "employees.approve"),
            ["Offboarding"] = new("employees.read", "employees.write", "employees.approve", "employees.delete"),
            ["Employees"] = new("employees.read", "employees.write", "employees.approve", "employees.delete", "employees.write", "employees.documents", "employees.bulk_import"),
            ["MigrationImport"] = new("employees.bulk_import", "employees.bulk_import", Import: "employees.bulk_import"),
            ["OrganizationStructureImport"] = new("organization.read", "organization.write", Import: "organization.write"),
            ["SetupAssistant"] = new("organization.read", "organization.setup.apply", Manage: "organization.setup.apply"),
            ["TenantHrConfig"] = new("organization.read", "organization.write", Manage: "organization.write"),
            ["HelpText"] = new("security.manage", "security.manage", Manage: "security.manage"),
            ["CountryPack"] = new("localization.read", "localization.manage", Manage: "localization.manage"),
            ["CompanyGovernance"] = new("compliance.read", "compliance.write", "compliance.approve", Manage: "compliance.write"),
            ["CompanyTaxPolicies"] = new("payroll.rates.read", "payroll.rates.manage", Manage: "payroll.rates.manage"),
            ["CompanyComplianceProfiles"] = new("compliance.read", "compliance.write", "compliance.approve", Manage: "compliance.write"),
            ["Benefits"] = new("employees.read", "employees.write", "employees.approve"),
            ["Bonuses"] = new("payroll.read", "payroll.write", "payroll.approve", Export: "payroll.export"),
            ["SetupSettings"] = new("security.manage", "security.manage", Manage: "security.manage", Export: "audit.export"),
        };

    private static readonly HashSet<string> OrganizationControllers = new(StringComparer.OrdinalIgnoreCase)
    {
        "Locations", "Companies", "Organization", "Grades", "Branches", "Departments", "Designations", "CostCenters", "Positions"
    };

    public static string? Resolve(HttpContext context)
    {
        var endpoint = context.GetEndpoint();
        if (endpoint is null || !endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Any(x => !string.IsNullOrWhiteSpace(x.Roles)))
            return null;

        // Explicit permission metadata always wins over inference.
        var explicitPermission = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .Select(x => x.Policy).FirstOrDefault(x => x?.StartsWith(HasPermissionAttribute.PolicyPrefix, StringComparison.Ordinal) == true);
        if (explicitPermission is not null)
            return explicitPermission[HasPermissionAttribute.PolicyPrefix.Length..].Split('|', StringSplitOptions.RemoveEmptyEntries)[0];

        var action = endpoint.Metadata.GetMetadata<ControllerActionDescriptor>();
        if (action is null) return null;
        var methods = endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods
                      ?? Array.Empty<string>();
        return Resolve(action.ControllerName, action.ActionName, methods);
    }

    public static string? Resolve(string controller, string action, IReadOnlyList<string> httpMethods)
    {
        ModulePermissions? module = null;
        if (OrganizationControllers.Contains(controller))
            module = new("organization.read", "organization.write", Delete: "organization.delete", Manage: "organization.write", Export: "organization.read", Import: "organization.write");
        else if (controller.StartsWith("Leave", StringComparison.OrdinalIgnoreCase) || controller is "Absence" or "CompOff" or "Encashment" or "HolidayCalendar")
            module = new("leave.read", "leave.write", "leave.approve", "leave.cancel", "leave.policy_manage", "leave.read", "leave.write");
        else if (controller is "Openings" or "Candidates" or "Applications" or "Interviews" or "Assessments" or "Requisitions" or "Offers" or "Onboarding" or "WorkforcePlanning" or "RecruitmentReports" or "RecruitmentAi")
            module = new("recruitment.read", "recruitment.write", "recruitment.approve", "recruitment.delete", "recruitment.write", "recruitment.read", "recruitment.write");
        else if (controller is "ScorecardTemplates" or "Reviews" or "PIP" or "Recommendations" or "Goals" or "Probation" or "Competencies" or "Calibration" or "Cycles")
            module = new("performance.read", "performance.write", "performance.approve", Manage: "performance.cycle_manage", Export: "performance.read");
        else if (controller is "ComplianceReports" or "Contracts" or "VisaTracking" or "SaudiCompliance")
            module = new("compliance.read", "compliance.write", "compliance.approve", Manage: "compliance.write", Export: "compliance.read");
        else if (controller is "Advances" or "Loans")
            module = new("loans.read", "loans.write", "loans.approve", Manage: "loans.policy_manage", Export: "loans.read");
        else if (controller is "BankConfirmations")
            module = new("payroll.read", "payroll.export", "payroll.approve", Manage: "payroll.export", Export: "payroll.export");
        else if (Modules.TryGetValue(controller, out var exact))
            module = exact;
        if (module is null) return null;

        var verb = httpMethods.FirstOrDefault() ?? "GET";
        var name = action.ToLowerInvariant();
        if (verb.Equals("GET", StringComparison.OrdinalIgnoreCase))
            return ContainsAny(name, "export", "download", "csv", "pdf") ? module.Export ?? module.Read : module.Read;
        if (ContainsAny(name, "approve", "reject", "decision", "decide", "finalize", "submitforapproval", "recommend"))
            return module.Approve ?? module.Write;
        if (ContainsAny(name, "delete", "remove", "purge")) return module.Delete ?? module.Write;
        if (ContainsAny(name, "import", "upload")) return module.Import ?? module.Write;
        if (ContainsAny(name, "export", "download", "generatewps", "bankfile")) return module.Export ?? module.Write;
        if (ContainsAny(name, "policy", "setting", "configure", "template", "cycle", "type", "lock", "void", "close"))
            return module.Manage ?? module.Write;
        return module.Write;
    }

    private static bool ContainsAny(string value, params string[] needles) => needles.Any(value.Contains);
}
