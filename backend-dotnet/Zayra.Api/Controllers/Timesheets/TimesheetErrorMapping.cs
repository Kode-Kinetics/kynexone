using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Timesheets;

namespace Zayra.Api.Controllers.Timesheets;

/// <summary>
/// One place that turns a timesheet failure into an HTTP answer, so the ESS screen and the HR
/// screen cannot describe the same refusal differently.
///
/// <para>422 is used for "your data is wrong or your tenant is not configured" — the same code
/// <c>ApprovalRequestsController</c> already returns for a missing approval route — and 409 for
/// "someone else changed this". The body always carries a machine-readable <c>code</c> so the UI
/// can route the message to the right field instead of dumping a sentence in a banner.</para>
/// </summary>
internal static class TimesheetErrorMapping
{
    public static IActionResult ToResult(this ControllerBase controller, Exception ex) => ex switch
    {
        TimesheetNotFoundException e => controller.NotFound(new { code = "not_found", message = e.Message }),

        TimesheetValidationException e => controller.UnprocessableEntity(new
        {
            code = e.Violations.Count == 1 ? e.Violations[0].Code : "timesheet_invalid",
            message = e.Message,
            violations = e.Violations.Select(v => new { date = v.Date, code = v.Code, message = v.Message })
        }),

        TimesheetConflictException e => controller.Conflict(new { code = "conflict", message = e.Message }),

        ApprovalRoutingException e => controller.UnprocessableEntity(new { code = e.Code, message = e.Message }),

        _ => controller.BadRequest(new { code = "invalid_request", message = ex.Message })
    };
}
