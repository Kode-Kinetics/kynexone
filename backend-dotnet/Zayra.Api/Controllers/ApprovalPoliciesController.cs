using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Zayra.Api.Controllers;

/// <summary>
/// RETIRED (F1 — approval engine convergence). <c>ApprovalPolicy</c> was a second approval
/// configuration model that tenants wrote to while leave routing read it and everything else read
/// <c>ApprovalWorkflow</c>; the two never reconciled. Its data was migrated into
/// <c>ApprovalWorkflow</c> (same ids, department/grade/default scoping carried over), and it is now
/// configured only through <c>/api/approval-workflows</c>.
///
/// <para>Every route answers 410 Gone with a pointer rather than 404, and none of them read or
/// write the frozen table: accepting a write here would recreate exactly the silent
/// misconfiguration F1 removes — configuration that is stored but never applied.</para>
/// </summary>
[ApiController]
[Route("api/approval-policies")]
[Authorize]
public class ApprovalPoliciesController : ControllerBase
{
    public const string RetiredMessage =
        "Approval policies have been merged into approval workflows. Configure approval routing (including department- and grade-specific routing) under /api/approval-workflows.";

    [HttpGet]
    [HttpPost]
    [HttpGet("{**rest}")]
    [HttpPost("{**rest}")]
    [HttpPut("{**rest}")]
    [HttpDelete("{**rest}")]
    public IActionResult Retired()
        => StatusCode(StatusCodes.Status410Gone, new { code = "approval_policies_retired", message = RetiredMessage, replacement = "/api/approval-workflows" });
}
