namespace Zayra.Api.Controllers.Finance;

/// <summary>
/// A loan refusal is a sentence the user reads, so it carries English and Arabic, as an advance refusal does
/// (<c>AdvancesController.Refusal</c>): <c>{ error, message, messageAr }</c>, with a stable machine code. The web client shows
/// <c>messageAr</c> to an Arabic reader (<c>loanErrorMessage</c>). LoanRefusalArabicTests keeps a bare English string from
/// coming back in the loan request and payment endpoints.
/// </summary>
internal static class LoanRefusals
{
    public static object Body(string code, string message, string messageAr) => new { error = code, message, messageAr };
}
