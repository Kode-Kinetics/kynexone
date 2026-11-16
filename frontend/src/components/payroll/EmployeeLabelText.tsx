/**
 * "CODE · Name" for an employee (see lib/employeeLabel). The code is kept on one line, so it never breaks at its hyphen (EVT2- / 0001);
 * the name is what wraps. Each part is isolated for bidirectional text.
 */
export function EmployeeLabelText({ label }: { label: string }) {
  const at = label.indexOf(' · ');
  if (at < 0) return <bdi>{label}</bdi>;
  return (
    <>
      <span className="whitespace-nowrap"><bdi>{label.slice(0, at)}</bdi></span>{' · '}<bdi>{label.slice(at + 3)}</bdi>
    </>
  );
}
