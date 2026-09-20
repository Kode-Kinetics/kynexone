using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Expenses;

namespace Zayra.Api.Controllers.Expenses;

/// <summary>One error contract for every expense endpoint (web and mobile):
/// <c>{ code, message, violations? }</c>. Policy violations are 422 and list every problem.</summary>
internal static class ExpenseErrorMapping
{
    public static async Task<IActionResult> Run(ControllerBase controller, Func<Task<IActionResult>> action)
    {
        try
        {
            return await action();
        }
        catch (ExpenseValidationException ex)
        {
            return controller.UnprocessableEntity(new { code = ex.Violations.Count == 1 ? ex.Violations[0].Code : "expense_policy_violation", message = ex.Message, violations = ex.Violations });
        }
        catch (ApprovalRoutingException ex)
        {
            return controller.UnprocessableEntity(new { code = ex.Code, message = ex.Message });
        }
        catch (ExpenseNotFoundException ex)
        {
            return controller.NotFound(new { code = "not_found", message = ex.Message });
        }
        catch (ExpenseConflictException ex)
        {
            return controller.Conflict(new { code = "conflict", message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            // The approval engine's own refusals (maker-checker, wrong approver, already decided).
            return controller.BadRequest(new { code = "approval_refused", message = ex.Message });
        }
    }
}
