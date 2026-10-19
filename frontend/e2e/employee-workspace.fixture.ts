import type { Page, Route } from '@playwright/test';

type ResponseMode = 'success' | 'not-found' | 'forbidden' | 'network' | 'server-error';

export interface EmployeeWorkspaceFixtureControl {
  detailMode: Map<number, ResponseMode>;
  readinessMode: Map<number, ResponseMode>;
  detailDelayMs: Map<number, number>;
  readinessDelayMs: Map<number, number>;
  detailCalls: Map<number, number>;
  readinessCalls: Map<number, number>;
  listCalls: number;
  mutationCalls: string[];
  mutations: Array<{ method: string; path: string; body: unknown }>;
  readinessBody: Map<number, unknown>;
}

interface EmployeeWorkspaceFixtureOptions {
  permissions?: string[];
  disabledFeatures?: string[];
}

const employees = {
  901: {
    id: 901,
    employeeCode: 'EMP-SA-QA-FIN-17-2026-0001',
    fullName: 'QA Master Data Test 2026-10-17',
    englishName: 'QA Master Data Test 2026-10-17',
    arabicName: '',
    preferredName: 'QA Master',
    profilePhotoUrl: '',
    personalEmail: 'qa.master@example.test',
    workEmail: 'qa.master@evostel.test',
    phone: '+966500000901',
    gender: 'Female',
    maritalStatus: 'Single',
    nationality: 'Saudi Arabia',
    countryCode: 'SAU',
    companyId: 'fixture-company',
    branchId: 'jeddah-office',
    departmentId: 'qa-finance',
    designationId: 'qa-finance-manager',
    gradeId: 'm2',
    costCenterId: 'qa-fin-cc-17',
    department: 'QA Finance',
    designation: 'QA Finance Manager',
    branch: 'Jeddah Office',
    workLocation: 'Jeddah',
    managerEmployeeId: 901,
    status: 'Draft',
    joiningDate: '2026-10-17',
    contractType: 'Unlimited Term',
    employmentType: 'Seasonal',
    jobTitle: 'QA Finance Manager',
    grade: 'M2',
    costCenter: 'QA-FIN-CC-17',
    payrollProfileCode: '',
    shiftPolicyCode: '',
    leavePolicyCode: '',
    attendancePolicyCode: '',
    bankName: '',
    bankIban: 'SA4420000001234567891234',
    wpsBankDetails: '',
    sponsorName: '',
    passportNumber: 'P-SECRET-901',
    visaNumber: '',
    visaFileNumber: '',
    iqamaNumber: '',
    muqeemNumber: '',
    gosiReference: '',
    qiwaContractNumber: '',
    emiratesId: '',
    laborCardNumber: '',
    qid: '',
    workPermitNumber: '',
    civilId: '',
    residencyNumber: '',
    idNumber: '1000000000',
    profileCompletenessScore: 60,
    complianceRecords: [],
    documents: [],
    history: [],
    transfers: [],
  },
  902: {
    id: 902,
    employeeCode: 'EMP-SA-QA-OPS-18-2026-0002',
    fullName: 'Second Route Employee',
    englishName: 'Second Route Employee',
    arabicName: '',
    preferredName: 'Second Employee',
    profilePhotoUrl: '',
    personalEmail: 'second.employee@example.test',
    workEmail: 'second.employee@evostel.test',
    phone: '+966500000902',
    gender: 'Male',
    maritalStatus: 'Single',
    nationality: 'Saudi Arabia',
    countryCode: 'SAU',
    companyId: 'fixture-company',
    branchId: 'riyadh-office',
    departmentId: 'qa-operations',
    designationId: 'qa-operations-lead',
    gradeId: 'm1',
    costCenterId: 'qa-ops-cc-18',
    department: 'QA Operations',
    designation: 'QA Operations Lead',
    branch: 'Riyadh Office',
    workLocation: 'Riyadh',
    managerEmployeeId: 902,
    status: 'Active',
    joiningDate: '2026-10-18',
    contractType: 'Limited Term',
    employmentType: 'Full Time',
    jobTitle: 'QA Operations Lead',
    grade: 'M1',
    costCenter: 'QA-OPS-CC-18',
    payrollProfileCode: '',
    shiftPolicyCode: '',
    leavePolicyCode: '',
    attendancePolicyCode: '',
    bankName: '',
    bankIban: '',
    wpsBankDetails: '',
    sponsorName: '',
    passportNumber: '',
    visaNumber: '',
    visaFileNumber: '',
    iqamaNumber: '',
    muqeemNumber: '',
    gosiReference: '',
    qiwaContractNumber: '',
    emiratesId: '',
    laborCardNumber: '',
    qid: '',
    workPermitNumber: '',
    civilId: '',
    residencyNumber: '',
    idNumber: '',
    profileCompletenessScore: 100,
    complianceRecords: [],
    documents: [],
    history: [],
    transfers: [],
  },
} as const;

const readiness = {
  901: {
    employeeId: 901,
    state: 'NeedsAttention',
    score: 60,
    progress: { present: 3, requiredTotal: 5 },
    policy: { countryCode: 'SAU', tier: 'Activation', sources: ['Fixture policy'] },
    blocking: [{ key: 'contract', label: 'Contract document', category: 'document', reason: 'Required before activation.' }],
    payBlocking: [{ key: 'iban', label: 'IBAN', category: 'pay', reason: 'Required before payroll.' }],
    recommended: [],
    present: [],
    expiringSoon: [],
    disclaimer: 'Fixture readiness data.',
  },
  902: {
    employeeId: 902,
    state: 'Ready',
    score: 100,
    progress: { present: 5, requiredTotal: 5 },
    policy: { countryCode: 'SAU', tier: 'Activation', sources: ['Fixture policy'] },
    blocking: [],
    payBlocking: [],
    recommended: [],
    present: [],
    expiringSoon: [],
    disclaimer: 'Fixture readiness data.',
  },
} as const;

function delay(milliseconds: number) {
  return new Promise(resolve => setTimeout(resolve, milliseconds));
}

function count(map: Map<number, number>, employeeId: number) {
  map.set(employeeId, (map.get(employeeId) ?? 0) + 1);
}

async function fulfillMode(route: Route, mode: ResponseMode, body: unknown) {
  if (mode === 'network') return route.abort('failed');
  if (mode === 'not-found') return route.fulfill({ status: 404, json: { message: 'Employee not found.' } });
  if (mode === 'forbidden') return route.fulfill({ status: 403, json: { message: 'This employee is outside your scope.' } });
  if (mode === 'server-error') return route.fulfill({ status: 500, json: { message: 'Fixture service failure.' } });
  return route.fulfill({ json: body });
}

export async function bootEmployeeWorkspaceFixture(
  page: Page,
  options: EmployeeWorkspaceFixtureOptions = {},
): Promise<EmployeeWorkspaceFixtureControl> {
  const permissions = options.permissions ?? [
    'employees.read',
    'employees.write',
    'employees.sensitive',
    'employees.documents',
    'employees.approve',
    'employees.delete',
    'employees.access.issue',
    'employees.access.reset',
    'manager.approve',
    'dashboard.read',
  ];
  const control: EmployeeWorkspaceFixtureControl = {
    detailMode: new Map(),
    readinessMode: new Map(),
    detailDelayMs: new Map(),
    readinessDelayMs: new Map(),
    detailCalls: new Map(),
    readinessCalls: new Map(),
    listCalls: 0,
    mutationCalls: [],
    mutations: [],
    readinessBody: new Map(),
  };

  await page.addInitScript(() => {
    localStorage.setItem('zayra_access_token', 'employee-workspace-fixture');
    localStorage.setItem('kynexone-locale-choice-v2', 'en');
    localStorage.setItem('kynexone-company:workspace-reviewer', 'fixture-company');
  });

  await page.route('**/api/**', async route => {
    const url = new URL(route.request().url());
    const path = url.pathname;
    const reply = (json: unknown) => route.fulfill({ json });

    if (path === '/api/auth/me') return reply({
      id: 'workspace-reviewer', tenantId: 'tenant', tenantSlug: 'fixture', email: 'reviewer@example.test',
      fullName: 'Workspace Reviewer', roles: ['Admin'],
      permissions,
      accountType: 'SingleCompany', isGroupScope: false,
      companies: [{ id: 'fixture-company', name: 'Evostel', code: 'fixture-company', countryCode: 'SAU', isActive: true }],
    });
    if (path === '/api/auth/mfa/status') return reply({ promptToEnroll: false });
    if (path === '/api/tenant-admin/localization') return reply({ countryCode: 'SAU', currencyCode: 'SAR', defaultLanguage: 'en' });
    if (path === '/api/features/disabled-keys') return reply(options.disabledFeatures ?? ['release_a']);
    if (path.includes('/features/')) return reply([]);
    if (path === '/api/notifications' || path.startsWith('/api/ai/policy/')) return reply([]);
    if (path === '/api/admin/system-settings' || path === '/api/country-packs/available') return reply([]);
    if (path === '/api/admin/master-data/values') {
      const typeCode = url.searchParams.get('typeCode');
      const values = typeCode === 'EmploymentType'
        ? ['Full-Time', 'Seasonal']
        : typeCode === 'ContractType'
          ? ['Unlimited', 'Unlimited Term']
          : [];
      return reply(values.map((valueEn, index) => ({ id: `${typeCode}-${index}`, valueEn, valueAr: '', sortOrder: index, isDefault: false, isActive: true })));
    }
    if (path === '/api/companies') return reply({ items: [{ id: 'fixture-company', legalNameEn: 'Evostel', tradeName: 'Evostel', countryCode: 'SAU', emailDomain: 'evostel.test', workEmailPattern: 'first.last', isActive: true, approvalStatus: 'Active' }], total: 1, page: 1, pageSize: 100 });
    if (path === '/api/departments') return reply({ items: [
      { id: 'qa-finance', branchId: 'jeddah-office', costCenterId: 'qa-fin-cc-17', code: 'QA-FIN', nameEn: 'QA Finance', nameAr: '', managerEmployeeId: 901, isActive: true },
      { id: 'qa-operations', branchId: 'riyadh-office', costCenterId: 'qa-ops-cc-18', code: 'QA-OPS', nameEn: 'QA Operations', nameAr: '', managerEmployeeId: 902, isActive: true },
    ], total: 2, page: 1, pageSize: 100 });
    if (path === '/api/designations') return reply({ items: [
      { id: 'qa-finance-manager', departmentId: 'qa-finance', code: 'QA-FIN-MGR', titleEn: 'QA Finance Manager', titleAr: '', gradeId: 'm2', isActive: true },
      { id: 'qa-operations-lead', departmentId: 'qa-operations', code: 'QA-OPS-LEAD', titleEn: 'QA Operations Lead', titleAr: '', gradeId: 'm1', isActive: true },
    ], total: 2, page: 1, pageSize: 100 });
    if (path === '/api/grades') return reply({ items: [
      { id: 'm1', code: 'M1', name: 'M1', band: 'M1', level: 1, minSalary: 10000, midSalary: 15000, maxSalary: 20000, currency: 'SAR', isActive: true },
      { id: 'm2', code: 'M2', name: 'M2', band: 'M2', level: 2, minSalary: 18000, midSalary: 24000, maxSalary: 30000, currency: 'SAR', isActive: true },
    ], total: 2, page: 1, pageSize: 100 });
    if (path === '/api/organization/cost-centers' || path === '/api/cost-centers') return reply({ items: [
      { id: 'qa-fin-cc-17', code: 'QA-FIN-CC-17', name: 'QA Finance Cost Center', isActive: true },
      { id: 'qa-ops-cc-18', code: 'QA-OPS-CC-18', name: 'QA Operations Cost Center', isActive: true },
    ], total: 2, page: 1, pageSize: 100 });
    if (path === '/api/employees/field-catalog') return reply({ fields: [
      { key: 'passportNumber', complianceFieldKey: 'passport_number', label: 'Passport number', section: 'compliance', inputType: 'text', entityKey: 'passportNumber', countries: ['SA'], visible: true },
      { key: 'gosiReference', complianceFieldKey: 'gosi_reference', label: 'GOSI reference', section: 'compliance', inputType: 'text', entityKey: 'gosiReference', countries: ['SA'], visible: true },
      { key: 'idNumber', complianceFieldKey: 'id_number', label: 'National ID (Hawiyya)', section: 'compliance', inputType: 'text', entityKey: 'idNumber', countries: ['SA'], visible: true },
    ] });
    if (/^\/api\/employee-access\/\d+$/.test(path)) return reply({ employeeId: 901, employeeName: employees[901].fullName, employeeCode: employees[901].employeeCode, workEmail: employees[901].workEmail, state: 'not_started', canIssue: true, emailDelivery: false });
    if (/^\/api\/employee-completion\/\d+$/.test(path)) return reply({ requested: false, status: 'NotRequested', selfServiceAvailable: true });

    const readinessMatch = path.match(/^\/api\/employees\/(\d+)\/readiness$/);
    if (readinessMatch) {
      const employeeId = Number(readinessMatch[1]);
      count(control.readinessCalls, employeeId);
      await delay(control.readinessDelayMs.get(employeeId) ?? 0);
      return fulfillMode(
        route,
        control.readinessMode.get(employeeId) ?? 'success',
        control.readinessBody.get(employeeId) ?? readiness[employeeId as keyof typeof readiness] ?? readiness[901],
      );
    }

    const detailMatch = path.match(/^\/api\/employees\/(\d+)$/);
    if (detailMatch) {
      const employeeId = Number(detailMatch[1]);
      if (route.request().method() !== 'GET') {
        const method = route.request().method();
        const body = route.request().postDataJSON();
        control.mutationCalls.push(`${method} ${path}`);
        control.mutations.push({ method, path, body });
        return reply(employees[employeeId as keyof typeof employees] ?? employees[901]);
      }
      count(control.detailCalls, employeeId);
      await delay(control.detailDelayMs.get(employeeId) ?? 0);
      const source = employees[employeeId as keyof typeof employees] ?? employees[901];
      const body = permissions.includes('employees.sensitive')
        ? source
        : { ...source, bankIban: '', passportNumber: '', salary: undefined };
      return fulfillMode(
        route,
        control.detailMode.get(employeeId) ?? 'success',
        body,
      );
    }

    if (path === '/api/employees') {
      control.listCalls += 1;
      const search = (url.searchParams.get('search') ?? '').toLowerCase();
      const items = Object.values(employees)
        .filter(employee => !search || `${employee.fullName} ${employee.employeeCode}`.toLowerCase().includes(search))
        .map(employee => ({
          id: employee.id,
          publicId: String(employee.id),
          employeeCode: employee.employeeCode,
          fullName: employee.fullName,
          arabicName: employee.arabicName,
          department: employee.department,
          designation: employee.designation,
          branch: employee.branch,
          managerEmployeeId: employee.managerEmployeeId,
          status: employee.status,
          profileCompletenessScore: employee.profileCompletenessScore,
          iqamaNumber: employee.iqamaNumber,
          readinessState: employee.id === 902 ? 'Ready' : 'NeedsAttention',
          activationBlockersCount: employee.id === 902 ? 0 : 2,
          workEmail: employee.workEmail,
        }));
      return reply({ items, total: items.length, page: 1, pageSize: 100 });
    }

    if (route.request().method() !== 'GET') {
      const method = route.request().method();
      const body = route.request().postDataJSON();
      control.mutationCalls.push(`${method} ${path}`);
      control.mutations.push({ method, path, body });
    }
    return reply({ items: [], total: 0, page: 1, pageSize: 100 });
  });

  return control;
}
