/**
 * The line manager's own Recruitment screen: raise a requisition for their department, give feedback on the interviews they are on.
 */
const ar: Record<string, string> = {
  "Hiring for my team": "التوظيف لفريقي",
  "Ask for a new position in your department, and give your feedback on the candidates you interview.": "اطلب وظيفة جديدة في قسمك، وقدّم ملاحظاتك عن المرشحين الذين تقابلهم.",
  "My requisitions": "طلبات التوظيف الخاصة بي",
  "My interviews": "مقابلاتي",
  "Could not load your requisitions.": "تعذر تحميل طلبات التوظيف الخاصة بك.",
  "Could not load your interviews.": "تعذر تحميل مقابلاتك.",
  "Department, position and the reason for the request are required.": "القسم والوظيفة وسبب الطلب مطلوبة.",
  "Your profile has no department yet, so a requisition cannot be raised. Ask HR to complete it.": "ملفك الشخصي لا يتضمن قسمًا بعد، لذلك لا يمكن رفع طلب توظيف. اطلب من الموارد البشرية استكماله.",
  "Position *": "الوظيفة *",
  "Why is this position needed? *": "لماذا هذه الوظيفة مطلوبة؟ *",
  "Save as draft": "حفظ كمسودة",
  "You have not raised any requisitions yet.": "لم ترفع أي طلب توظيف بعد.",
  "You are not on any interview panel yet.": "لست ضمن لجنة أي مقابلة حتى الآن.",
  "Give feedback": "إضافة ملاحظات",
  "Overall rating (1 to 5)": "التقييم العام (من 1 إلى 5)",
  "Your notes": "ملاحظاتك",
  "Send feedback": "إرسال الملاحظات",
  "Write a short note on the candidate.": "اكتب ملاحظة قصيرة عن المرشح.",
  "Could not save your feedback.": "تعذر حفظ ملاحظاتك.",
};

export const managerRecruitment = { ar, en: Object.fromEntries(Object.keys(ar).map(key => [key, key])) as Record<string, string> };
