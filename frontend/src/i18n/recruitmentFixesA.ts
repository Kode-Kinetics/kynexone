/**
 * Strings added by the recruitment walkthrough fixes, region A of views/RecruitmentPage.tsx (one file per agent, so no two edits collide).
 * Spread into the en and ar dictionaries in translations.ts; add keys here, never there. Keys are the English text.
 * Terms follow ./launchRecruitment: requisition = طلب توظيف, opening = وظيفة شاغرة, rejected = مرفوض.
 */
export const recruitmentFixesA: { en: Record<string, string>; ar: Record<string, string> } = {
  en: {
    'Reason: {reason}': 'Reason: {reason}',
    'Edit and resubmit': 'Edit and resubmit',
    'Edit Requisition {number}': 'Edit Requisition {number}',
    'Save and resubmit': 'Save and resubmit',
    'Your changes were saved as a draft, but sending the requisition for approval failed. Use Submit on the list to send it.': 'Your changes were saved as a draft, but sending the requisition for approval failed. Use Submit on the list to send it.',
    'Close opening': 'Close opening',
    'Close this opening?': 'Close this opening?',
    '{title} is marked as closed. Applications already received are not deleted.': '{title} is marked as closed. Applications already received are not deleted.',
    'Requisitions list': 'Requisitions list',
    'This requisition already has an opening.': 'This requisition already has an opening.',
    'Only an approved requisition can be opened as a job.': 'Only an approved requisition can be opened as a job.',
  },
  ar: {
    'Reason: {reason}': 'السبب: {reason}',
    'Edit and resubmit': 'تعديل وإعادة الإرسال',
    'Edit Requisition {number}': 'تعديل طلب التوظيف {number}',
    'Save and resubmit': 'حفظ وإعادة الإرسال',
    'Your changes were saved as a draft, but sending the requisition for approval failed. Use Submit on the list to send it.': 'حُفظت تعديلاتك كمسودة، لكن تعذّر إرسال طلب التوظيف للاعتماد. استخدم «إرسال» في القائمة لإرساله.',
    'Close opening': 'إغلاق الوظيفة الشاغرة',
    'Close this opening?': 'إغلاق هذه الوظيفة الشاغرة؟',
    '{title} is marked as closed. Applications already received are not deleted.': 'ستُسجَّل «{title}» كوظيفة مغلقة. لا تُحذف الطلبات التي وصلت بالفعل.',
    'Requisitions list': 'قائمة طلبات التوظيف',
    'This requisition already has an opening.': 'لهذا الطلب وظيفة شاغرة بالفعل.',
    'Only an approved requisition can be opened as a job.': 'لا يمكن فتح وظيفة شاغرة إلا من طلب توظيف معتمد.',
  },
};
