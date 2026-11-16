// New hires review: the government ID a hire needs before activation (National ID for a Saudi hire, Iqama otherwise).
// Terms follow Add Employee (hireWizard.ts): الهوية الوطنية، الإقامة. English keys are the English text (see i18n/translations.ts).
const ar: Record<string, string> = {
  'Government ID needed to activate': 'الهوية الحكومية مطلوبة للتفعيل',
  'Activation needs this person\'s {document}. Enter it here and the check runs again.':
    'يتطلب التفعيل إدخال {document} لهذا الشخص. أدخله هنا وسيُعاد الفحص تلقائيًا.',
  'Save and re-check': 'حفظ وإعادة الفحص',
  'Saving this makes you an editor of this hire, so a different HR approver has to activate it.':
    'بحفظ هذا الرقم تصبح محررًا لهذا التعيين، لذلك يجب أن يفعّله معتمد موارد بشرية آخر.',
  'You already worked on this hire, so a different HR approver has to activate it.':
    'سبق أن عملت على هذا التعيين، لذلك يجب أن يفعّله معتمد موارد بشرية آخر.',
  'Government ID saved. You edited this hire, so a different HR approver has to activate it.':
    'تم حفظ الهوية الحكومية. لقد عدّلت هذا التعيين، لذلك يجب أن يفعّله معتمد موارد بشرية آخر.',
  'Only someone who can edit new hires can add this number.':
    'لا يمكن إضافة هذا الرقم إلا لمن يملك صلاحية تعديل التعيينات الجديدة.',
};

export const draftIdentity = {
  en: Object.fromEntries(Object.keys(ar).map(key => [key, key])) as Record<string, string>,
  ar,
};
