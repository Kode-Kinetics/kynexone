/**
 * Strings of the hire-to-Self-Service screen fixes: the sign-in code button and its help line (components/employeeAccess/AccessStatesPanel.tsx),
 * the New hires notice after Approve (lib/newHireReview.ts) and the Pay approvals notice (views/PayApprovalsPage.tsx).
 * Terms follow ./releaseA/employeeAccess (sign-in code, slip) and ./payApprovals.
 * Spread into the en and ar dictionaries in translations.ts; add keys here, never there.
 */
export const hireWalkFixes: { en: Record<string, string>; ar: Record<string, string> } = {
  en: {
    'Issue sign-in code': 'Issue sign-in code',
    'The code is emailed to the work email. If you entered that email yourself, a slip is printed for you to hand over.': 'The code is emailed to the work email. If you entered that email yourself, a slip is printed for you to hand over.',
    "{name}'s employee record {code} is created but not active yet. Their pay package is waiting for a second approver; activate them once it is approved.": "{name}'s employee record {code} is created but not active yet. Their pay package is waiting for a second approver; activate them once it is approved.",
    '{name} is now employee {code}. Their pay package is still waiting for a second approver.': '{name} is now employee {code}. Their pay package is still waiting for a second approver.',
    '{count, plural, one {# pay package is waiting for approval, but you cannot approve it.} other {# pay packages are waiting for approval, but you cannot approve them.}}': '{count, plural, one {# pay package is waiting for approval, but you cannot approve it.} other {# pay packages are waiting for approval, but you cannot approve them.}}',
    "A pay package must be approved by someone other than the person who entered it, the employee, or the person who issued the employee's sign-in code.": "A pay package must be approved by someone other than the person who entered it, the employee, or the person who issued the employee's sign-in code.",
    'A different person with the payroll approve permission can approve it.': 'A different person with the payroll approve permission can approve it.',
  },
  ar: {
    'Issue sign-in code': 'إصدار رمز تسجيل الدخول',
    'The code is emailed to the work email. If you entered that email yourself, a slip is printed for you to hand over.': 'يُرسل الرمز إلى البريد الإلكتروني للعمل. وإذا كنت أنت من أدخل هذا البريد، تُطبع ورقة لتسلّمها للموظف.',
    "{name}'s employee record {code} is created but not active yet. Their pay package is waiting for a second approver; activate them once it is approved.": 'أُنشئ سجل الموظف {code} الخاص بـ{name} لكنه غير نشط بعد. باقة راتبه بانتظار معتمِد ثانٍ؛ فعّل الموظف بعد اعتمادها.',
    '{name} is now employee {code}. Their pay package is still waiting for a second approver.': 'أصبح {name} الآن موظفًا برقم {code}. باقة راتبه ما زالت بانتظار معتمِد ثانٍ.',
    '{count, plural, one {# pay package is waiting for approval, but you cannot approve it.} other {# pay packages are waiting for approval, but you cannot approve them.}}': '{count, plural, one {حزمة راتب واحدة بانتظار الموافقة، لكن لا يمكنك الموافقة عليها.} two {حزمتا راتب بانتظار الموافقة، لكن لا يمكنك الموافقة عليهما.} few {# حزم راتب بانتظار الموافقة، لكن لا يمكنك الموافقة عليها.} many {# حزمة راتب بانتظار الموافقة، لكن لا يمكنك الموافقة عليها.} other {# حزمة راتب بانتظار الموافقة، لكن لا يمكنك الموافقة عليها.}}',
    "A pay package must be approved by someone other than the person who entered it, the employee, or the person who issued the employee's sign-in code.": 'يجب أن يوافق على حزمة الراتب شخص آخر غير من أدخلها، وغير الموظف، وغير من أصدر رمز تسجيل دخول الموظف.',
    'A different person with the payroll approve permission can approve it.': 'يمكن لشخص مختلف يملك صلاحية اعتماد الرواتب أن يوافق عليها.',
  },
};
