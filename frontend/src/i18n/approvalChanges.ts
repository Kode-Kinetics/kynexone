// Approval Center → employee change card (components/approvals/EmployeeChangeApprovalCard.tsx) and the
// multi-step progress label on the approvals list. Whole sentences with {placeholders}; Arabic first-class.
// Reused, not redefined: 'Proposed', 'Not set', 'Effective {date}' (compensation / translations).
const ar: Record<string, string> = {
  'What this change does': 'ما الذي يغيّره هذا الطلب',
  'Field': 'الحقل',
  'On file now': 'المسجّل حاليًا',
  'Identity and bank numbers show their last 4 characters only. Pay is shown only to people with pay access.':
    'تظهر أرقام الهوية والحسابات البنكية بآخر 4 خانات فقط، ولا يظهر الراتب إلا لمن لديه صلاحية الاطلاع على الرواتب.',
  'Loading the proposed values…': 'جارٍ تحميل القيم المقترحة…',
  'Could not load the proposed values. Close and open the request again.': 'تعذّر تحميل القيم المقترحة. أغلق الطلب وافتحه مرة أخرى.',
  'The proposed values are shown to the person who can decide this step.': 'تظهر القيم المقترحة لمن يستطيع البتّ في هذه الخطوة.',
  'Step {step}': 'الخطوة {step}',
  'Step {step} of {count}': 'الخطوة {step} من {count}',
  'Step {step} of {count} approved': 'تمت الموافقة على الخطوة {step} من {count}',
};

export const approvalChanges = { ar, en: Object.fromEntries(Object.keys(ar).map(key => [key, key])) };
