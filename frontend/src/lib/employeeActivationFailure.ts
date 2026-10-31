export function employeeActivationFailure(error: unknown, translate: (message: string) => string = message => message): string {
  const data = (error as { response?: { data?: { blocking?: { label?: string }[]; message?: string } } } | null)?.response?.data;
  const labels = Array.isArray(data?.blocking)
    ? data.blocking.map(item => item?.label).filter((label): label is string => typeof label === 'string' && !!label.trim())
    : [];
  if (labels.length) return [...new Set(labels)].map(translate).join(' · ');
  if (typeof data?.message === 'string' && data.message.trim()) return translate(data.message);
  return translate('The employee could not be activated. Reload the readiness checks and try again.');
}
