'use client';

import { Field } from '../ui/Field';
import { useT } from '../../hooks/useT';
import type { DesignationAccessOptions } from '../../api/organization';

/**
 * "Access roles" on the designation form: which EXISTING roles (Setup → Roles) people get when they are given this designation, and lose when they
 * move to another one. Shown only when the feature is on for the tenant AND the signed-in user holds security.manage: the server decides both and
 * says so in `options`. The roles offered never include a privileged one (Admin, or any role holding security.manage) and only roles inside the
 * user's own access; the server checks again on save.
 */
export function DesignationAccessRolesField({ options, roles, onChange }: {
  options: DesignationAccessOptions;
  roles: string[];
  onChange: (roles: string[]) => void;
}) {
  const t = useT();
  if (!options.enabled || !options.canConfigure) return null;
  const toggle = (name: string, on: boolean) => onChange(on ? [...roles, name] : roles.filter((r) => r !== name));
  // A role already saved but no longer on offer (deactivated, or above this user) stays visible so nothing is dropped silently.
  const offered = new Set(options.roles.map((r) => r.name));
  const rows = [...options.roles, ...roles.filter((r) => !offered.has(r)).map((name) => ({ name, description: '' }))];

  return (
    <Field
      label={t('Access roles')}
      info={t('The existing roles people get when they are given this designation, and lose when they move to another one. Roles you give by hand are never removed. Administrators and roles that can manage security are not offered; give those from User Management.')}
    >
      <div className="max-h-44 space-y-1 overflow-y-auto rounded-lg border border-slate-200 p-2 dark:border-white/10" role="group">
        {rows.length === 0 && <p className="text-meta text-fg-muted">{t('No roles can be given automatically.')}</p>}
        {rows.map((r) => (
          <label key={r.name} className="flex items-start gap-2 text-sm text-slate-700 dark:text-slate-300">
            <input type="checkbox" className="mt-0.5 h-4 w-4 accent-sapphire" checked={roles.includes(r.name)} onChange={(e) => toggle(r.name, e.target.checked)} />
            <span>{r.name}{r.description && <span className="block text-meta text-fg-muted">{r.description}</span>}</span>
          </label>
        ))}
      </div>
    </Field>
  );
}
