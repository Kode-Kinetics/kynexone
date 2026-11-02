import { expect, test } from '@playwright/test';
import {
  SETUP_RELATED_SECTIONS, SETUP_SECTION_GROUPS, SETUP_SECTION_ORDER, SETUP_SETTING_SECTIONS, inSetupOrder, unavailableSectionRedirect,
} from '../src/lib/setupAreas';
import { SETUP_MODULE_SECTIONS } from '../src/lib/setupModuleSections';

const ALL = [...SETUP_SETTING_SECTIONS, ...SETUP_MODULE_SECTIONS];
const inGroup = (group: string) => inSetupOrder(ALL.filter(s => s.group === group)).map(s => s.id);

test.describe('Setup placement', () => {
  test('every section sits in exactly one navigation group, and every group is a known one', () => {
    const groups = new Set(SETUP_SECTION_GROUPS.map(g => g.id));
    for (const s of ALL) expect(groups.has(s.group), s.id).toBe(true);
    expect(new Set(ALL.map(s => s.id)).size).toBe(ALL.length);
  });

  test('every section has a place in the one ordering list, with no strangers', () => {
    const known = new Set(ALL.map(s => s.id as string));
    for (const id of SETUP_SECTION_ORDER) expect(known.has(id), `${id} is not a section`).toBe(true);
    for (const s of ALL) expect(SETUP_SECTION_ORDER as readonly string[], s.id).toContain(s.id);
  });

  test('Workspace opens with Language, Branding, Modules and Plan and billing, and ends with the Audit log', () => {
    const ids = inGroup('workspace');
    expect(ids.slice(0, 4)).toEqual(['localization', 'branding', 'modules', 'planBilling']);
    expect(ids[ids.length - 1]).toBe('adminAuditLogs');
  });

  test('Notifications live with the employee services, once', () => {
    expect(ALL.find(s => s.id === 'notificationTemplates')?.group).toBe('talentServices');
    expect(inGroup('workspace')).not.toContain('notificationTemplates');
  });

  test('Locations follow Branches in Company structure', () => {
    expect(inGroup('companyStructure')).toEqual(['companies', 'branches', 'locations', 'costCenters', 'departments']);
  });

  test('the audit log and the sign-in log point at each other', () => {
    expect(SETUP_RELATED_SECTIONS.adminAuditLogs?.id).toBe('accessLog');
    expect(SETUP_RELATED_SECTIONS.accessLog?.id).toBe('adminAuditLogs');
  });

  test('a hidden section sends the reader to their first available one, never to a dead end', () => {
    const isSection = (id: string) => ALL.some(s => s.id === id);
    const open = [{ id: 'companies' }, { id: 'branches' }];
    expect(unavailableSectionRedirect('roles', open, isSection)).toBe('companies');
    expect(unavailableSectionRedirect('branches', open, isSection)).toBeNull();
    expect(unavailableSectionRedirect('aiSetup', open, isSection)).toBeNull();
    expect(unavailableSectionRedirect(null, open, isSection)).toBeNull();
    expect(unavailableSectionRedirect('roles', [], isSection)).toBeNull();
  });
});
