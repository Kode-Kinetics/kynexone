// Loans and advances at exit (components/offboarding/ExitDebtsPanel.tsx, the offboarding card and the final settlement), the
// loan book by currency (Reports, Loans audit report) and loan refusal fallbacks (components/loans/LoanPaymentsTab.tsx).
const ar: Record<string, string> = {
  'Loans and advances still owed': 'القروض والسلف المستحقة',
  'Nothing owed: no open loan or salary advance.': 'لا مستحقات: لا يوجد قرض أو سلفة مفتوحة.',
  'Loan balance': 'رصيد قرض',
  'Salary advance balance': 'رصيد سلفة راتب',
  'On hold, not recovered from the final pay': 'موقوف، ولا يُسترد من المستحقات النهائية',
  'Planned from final pay: {amount}': 'المخطط استرداده من المستحقات النهائية: {amount}',
  '{name} still owes {amount}. The final settlement recovers what it can; what it cannot is moved to the employee receivable when the settlement is paid.':
    'لا يزال على {name} مبلغ {amount}. تسترد التسوية النهائية ما تستطيع، وما لا تستطيع استرداده يُنقل إلى ذمم الموظفين المدينة عند صرف التسوية.',
  '{amount} is on hold and is not recovered by the final settlement. HR must release the hold or Finance must record its recovery.':
    'مبلغ {amount} موقوف ولا تسترده التسوية النهائية. على الموارد البشرية رفع الإيقاف أو على الإدارة المالية تسجيل استرداده.',
  '{name}: {amount} cannot be recovered from this settlement. It is not written off: it moves to the employee receivable when the settlement is paid.':
    '{name}: لا يمكن استرداد {amount} من هذه التسوية. لا يُشطب المبلغ، بل يُنقل إلى ذمم الموظفين المدينة عند صرف التسوية.',
  'Unable to process this loan payment.': 'تعذّرت معالجة دفعة القرض هذه.',
  'Outstanding by currency: {amounts}': 'المتبقي حسب العملة: {amounts}',
  'Open employee loans, overdue ones included. Outstanding is what is still to be repaid on them, per currency.':
    'قروض الموظفين المفتوحة، بما فيها المتأخرة. المتبقي هو ما لم يُسدَّد منها بعد، لكل عملة على حدة.',
  'Overdue loans': 'القروض المتأخرة',
};

export const loanExit = { ar, en: Object.fromEntries(Object.keys(ar).map(key => [key, key])) };
