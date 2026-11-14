/** An employee in a dropdown or table cell: "CODE · Name" when a name is known, else the caller's fallback words (never a bare "#226"). */
export function employeeLabel(id: number, labels: Record<number, string> | undefined, fallback: (id: number) => string): string {
  return labels?.[id] || fallback(id);
}

export function employeeLabelsFromRecords(records: Array<{ employeeId: number; employeeName?: string | null; employeeCode?: string | null }>): Record<number, string> {
  const out: Record<number, string> = {};
  for (const r of records) if (r.employeeName) out[r.employeeId] = r.employeeCode ? `${r.employeeCode} · ${r.employeeName}` : r.employeeName;
  return out;
}
