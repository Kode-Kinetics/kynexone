using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Zayra.Api.Infrastructure.Finance;

/// <summary>
/// AM-29: a <see cref="GlCostCentreRuleException"/> thrown by the ledger guard inside <c>SaveChanges</c> is a refusal, never a 500.
/// Applied to every controller that writes ledger lines (payroll, bonuses, loans, advances, GL adjustments). The response carries the
/// machine code, an English message and an Arabic message; nothing was written (the unit of work's transaction is not committed).
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class GlCostCentreRuleFilterAttribute : ExceptionFilterAttribute
{
    public override void OnException(ExceptionContext context)
    {
        if (context.Exception is not GlCostCentreRuleException ex) return;
        context.Result = new UnprocessableEntityObjectResult(Body(ex));
        context.ExceptionHandled = true;
    }

    public static object Body(GlCostCentreRuleException ex) => new { error = ex.Code, code = ex.Code, message = ex.Message, messageAr = ArabicFor(ex.Code) };

    public static string ArabicFor(string code) => code switch
    {
        "cost_centre_required" => "هذا الحساب يتطلب مركز تكلفة. عيّن مركز تكلفة للموظف أو لمنصبه أو لقسمه، أو حدّد مركز تكلفة افتراضيًا للشركة.",
        "cost_centre_not_allowed" => "هذا الحساب لا يقبل مركز تكلفة. أزل مركز التكلفة من قاعدة الترحيل أو غيّر قاعدة مركز التكلفة للحساب.",
        "cost_centre_invalid" => "مركز التكلفة على هذا القيد غير نشط أو محذوف أو لا يتبع شركة الترحيل.",
        "cost_centre_both_sides_pnl" => "لا يجوز أن يحتوي القيد على حسابات قائمة الدخل (إيرادات أو مصروفات) في الطرفين المدين والدائن معًا عند تفعيل محاسبة التكلفة. قسّم القيد إلى قيدين.",
        "finance_gl_entry_company_required" => "يجب أن ينتمي القيد إلى شركة. رحِّله إلى الكيان القانوني المعني.",
        "finance_gl_entry_cost_centre_immutable" => "لا يمكن تغيير مركز التكلفة ومفتاح الترحيل لقيد مرحّل. أنشئ قيدًا عكسيًا وارحِّله بدلًا من ذلك.",
        _ => "رُفض هذا القيد المحاسبي لمخالفته قواعد مراكز التكلفة أو سلامة الترحيل.",
    };
}
