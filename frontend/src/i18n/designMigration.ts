/**
 * Confirmations and failure notices that used to be native window.confirm / alert, now routed
 * through the shared confirm dialog and toast. Terms follow the rest of the product.
 */
const ar: Record<string, string> = {
  'Archive': 'أرشفة',
  'Archive "{name}"? It will no longer be applied to new requests.': 'أرشفة «{name}»؟ لن يُطبَّق بعد الآن على الطلبات الجديدة.',
  'Archive policy': 'أرشفة السياسة',
  'Delete calendar': 'حذف التقويم',
  'Delete calendar "{name}"? All {count} holidays will be removed.': 'حذف التقويم «{name}»؟ ستُزال جميع العطلات ({count}).',
  'Remove holiday': 'إزالة العطلة',
  'Remove "{name}"?': 'إزالة «{name}»؟',
  'Remove': 'إزالة',
  'Delete': 'حذف',
  'Submit for approval': 'إرسال للاعتماد',
  'Submit this batch for approval?': 'إرسال هذه الدفعة للاعتماد؟',
  'Approve batch': 'اعتماد الدفعة',
  'Approve this bonus batch? This will generate GL entries.': 'اعتماد دفعة المكافآت هذه؟ سيؤدي ذلك إلى إنشاء قيود في دفتر الأستاذ.',
  'Mark as paid': 'تحديد كمدفوعة',
  'Mark this batch as paid? This action is final.': 'تحديد هذه الدفعة كمدفوعة؟ هذا الإجراء نهائي.',
  'Cannot delete batch.': 'تعذّر حذف الدفعة.',
  'Role summary copied to clipboard.': 'تم نسخ ملخص الدور إلى الحافظة.',
  'Copy failed. Please copy from the visible summary.': 'تعذّر النسخ. يُرجى النسخ من الملخص الظاهر.',
  'Failed to save': 'تعذّر الحفظ',
  'Remove dependant': 'إزالة المعال',
  'Remove this dependant from the employee’s file?': 'إزالة هذا المعال من ملف الموظف؟',
  'Cancel leave request': 'إلغاء طلب الإجازة',
  'Cancel request': 'إلغاء الطلب',
  'Remove help text': 'إزالة نص المساعدة',
  'Remove the custom help text for "{key}"? The built-in default will be shown again.': 'إزالة نص المساعدة المخصص لـ «{key}»؟ سيظهر النص الافتراضي المضمّن مرة أخرى.',
  'Delete saved report': 'حذف التقرير المحفوظ',
  'Delete this saved report?': 'حذف هذا التقرير المحفوظ؟',
  'Delete schedule': 'حذف الجدولة',
  'Delete this schedule?': 'حذف هذه الجدولة؟',
};

export const designMigration = {
  en: Object.fromEntries(Object.keys(ar).map(key => [key, key])) as Record<string, string>,
  ar,
};
