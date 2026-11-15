/**
 * Payroll pay population: who a run leaves out and why, and the people it pays with a warning Finance acknowledges
 * before Approve. Terms follow GLOSSARY.md: مسير الرواتب, آخر يوم عمل, الاعتماد.
 */
const ar: Record<string, string> = {
  // Left-out reasons (PayPopulationReasons on the server).
  "Not employed yet": "لم يبدأ العمل بعد",
  "Left before this period": "غادر قبل هذه الفترة",
  "Final wage already paid": "صُرف الراتب الأخير مسبقًا",
  "Suspended — not paid (pay hold)": "موقوف — لا يُصرف له (إيقاف الصرف)",
  "Inactive or archived": "غير نشط أو مؤرشف",
  "Exit status with no last working day": "حالة خروج دون آخر يوم عمل",
  "Deleted — no wages owed": "محذوف — لا أجور مستحقة",
  "Status not recognised": "حالة غير معروفة",
  "Excluded from this run": "مستبعد من هذا المسير",
  "Selected but not eligible": "محدد لكنه غير مؤهل",
  // Paid with a warning.
  "Resigned or on notice with no last working day — paid in full": "مستقيل أو في فترة الإشعار دون آخر يوم عمل — يُصرف له كاملًا",
  "Status On leave with no approved leave this period — paid in full": "حالته «في إجازة» دون إجازة معتمدة في هذه الفترة — يُصرف له كاملًا",
  "Record deleted but wages owed — paid to the last working day": "السجل محذوف لكن الأجور مستحقة — يُصرف له حتى آخر يوم عمل",
  // Not paid, though the status says employed: acknowledged at Approve with the paid-with-warning list.
  "Status says employed, but not paid by this run": "حالته تفيد أنه على رأس العمل، لكن هذا المسير لا يصرف له",
  // Approve gate.
  "{payWarningCount} employee(s) need a check before approval": "{payWarningCount} موظفًا يحتاجون إلى مراجعة قبل الاعتماد",
  "Some are paid although their record suggests they may not be owed a full month; others are not paid although their status says they are employed. Check each one before approving.":
    "بعضهم يُصرف له مع أن سجله يشير إلى أنه قد لا يستحق شهرًا كاملًا، وبعضهم لا يُصرف له مع أن حالته تفيد أنه على رأس العمل. راجع كل حالة قبل الاعتماد.",
  "I have reviewed the list and confirm exactly {payWarningCount} employee(s) need a check.":
    "راجعت القائمة وأؤكد أن {payWarningCount} موظفًا بالضبط يحتاجون إلى مراجعة.",
  "{leftOutCount} employee(s) in this company are not paid by this run": "{leftOutCount} موظفًا في هذه الشركة لا يشملهم هذا المسير",
  "Everyone the run leaves out is listed with the reason. Nobody is dropped without one.":
    "يُدرج كل من لا يشمله المسير مع السبب، ولا يُستبعد أحد دون سبب.",
  "Show the names": "عرض الأسماء",
  "Last working day {date}": "آخر يوم عمل {date}",
  "Status: {status}": "الحالة: {status}",
};

export const payPopulation = { ar, en: Object.fromEntries(Object.keys(ar).map(key => [key, key])) as Record<string, string> };
