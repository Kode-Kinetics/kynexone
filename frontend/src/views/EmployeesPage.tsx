'use client';

import { useCallback, useEffect, useMemo, useState } from 'react';
import { FileUp, History, Pencil, Plus, RefreshCw, Search, Send, UserRound, Users } from 'lucide-react';
import { useSearchParams } from 'next/navigation';
import { employeesApi } from '../api/employees';
import type { EmployeeCreateRequest, EmployeeDetail, EmployeeListItem } from '../api/employees';
import { ImportExportToolbar, downloadCsv } from '../components/ImportExportToolbar';
import client from '../api/client';

const employeesImportExport = {
  export: async () => {
    const csv = await client.get<string>('/api/employees/export', { responseType: 'text' }).then(r => r.data);
    downloadCsv(csv, 'employees.csv');
  },
  template: async () => {
    const csv = await client.get<string>('/api/employees/import-template', { responseType: 'text' }).then(r => r.data);
    downloadCsv(csv, 'employees-template.csv');
  },
  import: (csvContent: string) =>
    client.post<{ received: number; created: number; skipped: number; errors: string[] }>('/api/employees/import', { csvContent }).then(r => r.data),
};
import { branchesApi, companiesApi, costCentersApi, departmentsApi, designationsApi, gradesApi } from '../api/organization';
import type { BranchDto, CompanyDto, CostCenterDto, DepartmentDto, DesignationDto, GradeDto, GradePayScaleComponentDto } from '../api/organization';
import { Avatar } from '../components/Avatar';
import { useAutoTranslate } from '../hooks/useAutoTranslate';
import { InfoTip } from '../components/InfoTip';
import { Modal } from '../components/Modal';
import { StatusChip } from '../components/StatusChip';
import { useCompany } from '../contexts/CompanyContext';
import { useTenantSettings } from '../contexts/TenantSettingsContext';

type StatusFilter = '' | 'Draft' | 'Pre-boarding' | 'Active' | 'Probation' | 'Confirmed' | 'On leave' | 'Suspended' | 'Resigned' | 'Notice period' | 'Terminated' | 'Retired' | 'Absconded' | 'Inactive' | 'Blacklisted';
type DetailTab = 'personal' | 'employment' | 'payroll' | 'compliance' | 'documents' | 'history' | 'transfers';
type EditField = { key: string; label: string; type?: 'text' | 'email' | 'date' | 'number' | 'select'; options?: string[]; sensitive?: boolean; section: string };
type ComplianceFieldDefinition = {
  fieldKey: string;
  fieldLabel: string;
  entityKey?: string;
  expiryEntityKey?: string;
  required?: boolean;
};

const statusOptions: StatusFilter[] = ['', 'Draft', 'Pre-boarding', 'Active', 'Probation', 'Confirmed', 'On leave', 'Suspended', 'Resigned', 'Notice period', 'Terminated', 'Retired', 'Absconded', 'Inactive', 'Blacklisted'];
const tabs: { id: DetailTab; label: string }[] = [
  { id: 'personal', label: 'Personal Information' },
  { id: 'employment', label: 'Employment Information' },
  { id: 'payroll', label: 'Payroll Profile' },
  { id: 'compliance', label: 'Compliance' },
  { id: 'documents', label: 'Documents' },
  { id: 'history', label: 'History' },
  { id: 'transfers', label: 'Transfers' },
];

const emptyEmployee = (): EmployeeCreateRequest => ({
  manualEmployeeCode: false,
  englishName: '',
  arabicName: '',
  preferredName: '',
  gender: '',
  nationality: '',
  maritalStatus: '',
  personalEmail: '',
  workEmail: '',
  mobileNumber: '',
  companyId: '',
  branchId: '',
  departmentId: '',
  designationId: '',
  gradeId: '',
  costCenterId: '',
  jobTitle: '',
  employmentType: 'Full-Time',
  contractType: 'Unlimited',
  joiningDate: new Date().toISOString().slice(0, 10),
  workLocation: '',
  payrollGroup: '',
  shiftPolicyCode: '',
  leavePolicyCode: '',
  attendancePolicyCode: '',
  payrollProfile: {
    bankName: '',
    iban: '',
    accountNumber: '',
    paymentMethod: 'BankTransfer',
    salaryCurrency: '',
    payrollGroup: '',
    salaryStructureReference: '',
    wpsEligible: true,
    eosbEligible: true,
    socialInsuranceReference: '',
    molId: '',
    bankRoutingCode: '',
  },
  salaryBreakdown: {
    basicSalary: undefined,
    housingAllowance: undefined,
    transportAllowance: undefined,
    foodAllowance: undefined,
    mobileAllowance: undefined,
    otherAllowance: undefined,
    fixedDeduction: undefined,
    salaryStructureCode: '',
    effectiveDate: new Date().toISOString().slice(0, 10),
    currency: '',
  },
  complianceRecords: [],
});

// Editable fields for the Edit Details modal. Sensitive ones are routed by the
// backend into an approval workflow (202) instead of applying immediately.
const EDIT_FIELDS: EditField[] = [
  { section: 'Personal', key: 'englishName', label: 'English full name' },
  { section: 'Personal', key: 'arabicName', label: 'Arabic name' },
  { section: 'Personal', key: 'preferredName', label: 'Preferred name' },
  { section: 'Personal', key: 'gender', label: 'Gender', type: 'select', options: ['Male', 'Female', 'Other'] },
  { section: 'Personal', key: 'nationality', label: 'Nationality' },
  { section: 'Personal', key: 'maritalStatus', label: 'Marital status', type: 'select', options: ['Single', 'Married', 'Divorced', 'Widowed'] },
  { section: 'Personal', key: 'dateOfBirth', label: 'Date of birth', type: 'date', sensitive: true },
  { section: 'Personal', key: 'personalEmail', label: 'Personal email', type: 'email' },
  { section: 'Personal', key: 'workEmail', label: 'Work email', type: 'email' },
  { section: 'Personal', key: 'phone', label: 'Mobile number' },
  { section: 'Personal', key: 'emergencyContactName', label: 'Emergency contact name' },
  { section: 'Personal', key: 'emergencyContactPhone', label: 'Emergency contact phone' },
  { section: 'Employment', key: 'department', label: 'Department' },
  { section: 'Employment', key: 'designation', label: 'Designation' },
  { section: 'Employment', key: 'jobTitle', label: 'Job title' },
  { section: 'Employment', key: 'employmentType', label: 'Employment type', type: 'select', options: ['Full-Time', 'Part-Time', 'Contractor', 'Intern'] },
  { section: 'Employment', key: 'contractType', label: 'Contract type', type: 'select', options: ['Unlimited', 'Fixed-Term', 'Temporary'] },
  { section: 'Employment', key: 'workLocation', label: 'Work location' },
  { section: 'Employment', key: 'grade', label: 'Grade' },
  { section: 'Employment', key: 'costCenter', label: 'Cost center' },
  { section: 'Employment', key: 'joiningDate', label: 'Joining date', type: 'date' },
  { section: 'Employment', key: 'managerEmployeeId', label: 'Manager employee ID', type: 'number' },
  { section: 'Payroll & Banking', key: 'salary', label: 'Salary', type: 'number', sensitive: true },
  { section: 'Payroll & Banking', key: 'bankName', label: 'Bank name', sensitive: true },
  { section: 'Payroll & Banking', key: 'bankIban', label: 'IBAN', sensitive: true },
];

const COMMON_COMPLIANCE_FIELDS: ComplianceFieldDefinition[] = [
  { fieldKey: 'passport_number', fieldLabel: 'Passport Number', entityKey: 'passportNumber', expiryEntityKey: 'passportExpiryDate', required: true },
];

const COUNTRY_COMPLIANCE_PROFILES: Record<string, ComplianceFieldDefinition[]> = {
  SA: [
    ...COMMON_COMPLIANCE_FIELDS,
    { fieldKey: 'iqama_number', fieldLabel: 'Saudi National ID / Iqama', entityKey: 'iqamaNumber', required: true },
    { fieldKey: 'muqeem_reference', fieldLabel: 'Muqeem Reference', entityKey: 'muqeemNumber' },
    { fieldKey: 'gosi_reference', fieldLabel: 'GOSI Reference', entityKey: 'gosiReference' },
    { fieldKey: 'qiwa_contract_reference', fieldLabel: 'Qiwa Contract Number', entityKey: 'qiwaContractNumber' },
    { fieldKey: 'work_permit', fieldLabel: 'Work Permit Number', entityKey: 'workPermitNumber', expiryEntityKey: 'workPermitIssueDate' },
  ],
  AE: [
    ...COMMON_COMPLIANCE_FIELDS,
    { fieldKey: 'emirates_id', fieldLabel: 'Emirates ID', entityKey: 'emiratesId', required: true },
    { fieldKey: 'visa_number', fieldLabel: 'Visa Number', entityKey: 'visaNumber', expiryEntityKey: 'visaExpiryDate' },
    { fieldKey: 'visa_file_number', fieldLabel: 'Visa File Number', entityKey: 'visaFileNumber' },
    { fieldKey: 'labor_card_number', fieldLabel: 'Labour Card Number', entityKey: 'laborCardNumber' },
  ],
  QA: [
    ...COMMON_COMPLIANCE_FIELDS,
    { fieldKey: 'qid', fieldLabel: 'Qatar ID', entityKey: 'qid', required: true },
    { fieldKey: 'visa_number', fieldLabel: 'Visa Number', entityKey: 'visaNumber', expiryEntityKey: 'visaExpiryDate' },
    { fieldKey: 'work_permit', fieldLabel: 'Work Permit Number', entityKey: 'workPermitNumber', expiryEntityKey: 'workPermitIssueDate' },
  ],
  KW: [
    ...COMMON_COMPLIANCE_FIELDS,
    { fieldKey: 'civil_id', fieldLabel: 'Civil ID', entityKey: 'civilId', required: true },
    { fieldKey: 'residency_number', fieldLabel: 'Residency Number', entityKey: 'residencyNumber', expiryEntityKey: 'residencyIssueDate' },
    { fieldKey: 'work_permit', fieldLabel: 'Work Permit Number', entityKey: 'workPermitNumber', expiryEntityKey: 'workPermitIssueDate' },
  ],
  OM: [
    ...COMMON_COMPLIANCE_FIELDS,
    { fieldKey: 'civil_id', fieldLabel: 'Civil ID', entityKey: 'civilId', required: true },
    { fieldKey: 'residency_number', fieldLabel: 'Residency Number', entityKey: 'residencyNumber', expiryEntityKey: 'residencyIssueDate' },
    { fieldKey: 'work_permit', fieldLabel: 'Work Permit Number', entityKey: 'workPermitNumber', expiryEntityKey: 'workPermitIssueDate' },
  ],
  BH: [
    ...COMMON_COMPLIANCE_FIELDS,
    { fieldKey: 'civil_id', fieldLabel: 'CPR / Civil ID', entityKey: 'civilId', required: true },
    { fieldKey: 'work_permit', fieldLabel: 'Work Permit Number', entityKey: 'workPermitNumber', expiryEntityKey: 'workPermitIssueDate' },
  ],
};

function normalizeCountryCode(value?: string) {
  const code = (value ?? '').trim().toUpperCase();
  const compact = code.replace(/[^A-Z]/g, '');
  if (['SAU', 'KSA', 'SAUDIARABIA', 'KINGDOMOFSAUDIARABIA'].includes(compact)) return 'SA';
  if (['ARE', 'UAE', 'UNITEDARABEMIRATES', 'UNITEDARABEMIRATE'].includes(compact)) return 'AE';
  if (['QAT', 'QATAR'].includes(compact)) return 'QA';
  if (['KWT', 'KUWAIT'].includes(compact)) return 'KW';
  if (['OMN', 'OMAN'].includes(compact)) return 'OM';
  if (['BHR', 'BAHRAIN'].includes(compact)) return 'BH';
  return code.length === 2 ? code : '';
}

function complianceProfileForCountry(countryCode?: string) {
  const country = normalizeCountryCode(countryCode);
  return COUNTRY_COMPLIANCE_PROFILES[country] ?? COMMON_COMPLIANCE_FIELDS;
}

function complianceRecordsForCountry(countryCode?: string, existing: EmployeeCreateRequest['complianceRecords'] = []) {
  const country = normalizeCountryCode(countryCode);
  if (!country) return [];
  const existingByKey = new Map((existing ?? []).map((record) => [record.fieldKey, record]));
  return complianceProfileForCountry(country).map((field) => {
    const current = existingByKey.get(field.fieldKey);
    return {
      countryCode: country,
      fieldKey: field.fieldKey,
      fieldLabel: field.fieldLabel,
      fieldValue: current?.fieldValue ?? '',
      issueDate: current?.issueDate,
      expiryDate: current?.expiryDate,
      isSensitive: true,
      isRequired: Boolean(field.required),
    };
  });
}

function complianceEditFieldsForCountry(countryCode?: string): EditField[] {
  return complianceProfileForCountry(countryCode).flatMap((field) => {
    const fields: EditField[] = [];
    if (field.entityKey) fields.push({ section: 'Compliance Documents', key: field.entityKey, label: field.fieldLabel, sensitive: true });
    if (field.expiryEntityKey) fields.push({ section: 'Compliance Documents', key: field.expiryEntityKey, label: `${field.fieldLabel} expiry`, type: 'date', sensitive: true });
    return fields;
  });
}

function documentTypesForCountry(countryCode?: string) {
  const statutory = complianceProfileForCountry(countryCode).map((field) => field.fieldLabel);
  return [...new Set([...statutory, 'Contract', 'Offer letter', 'NDA', 'Policy acknowledgment'])];
}

interface EmployeeUsageData {
  activeEmployees: number;
  maxEmployees: number;
  activeUsers: number;
  maxUsers: number;
  storageUsedMb: number;
}

export function EmployeesPage() {
  const searchParams = useSearchParams();
  const { currencyCode } = useTenantSettings();
  const { companies: accessibleCompanies, selectedCompanyId } = useCompany();
  const [employees, setEmployees] = useState<EmployeeListItem[]>([]);
  const [total, setTotal] = useState(0);
  const [page, setPage] = useState(1);
  const [search, setSearch] = useState('');
  const [status, setStatus] = useState<StatusFilter>('');
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [actionNotice, setActionNotice] = useState('');
  const [subscriptionBanner, setSubscriptionBanner] = useState('');
  const [usage, setUsage] = useState<EmployeeUsageData | null>(null);
  const [formOpen, setFormOpen] = useState(false);
  const [form, setForm] = useState<EmployeeCreateRequest>(emptyEmployee());
  const [formOriginal, setFormOriginal] = useState<EmployeeCreateRequest>(emptyEmployee());
  const [formError, setFormError] = useState('');
  const [saving, setSaving] = useState(false);
  const [selectedId, setSelectedId] = useState<number | null>(null);
  const [detail, setDetail] = useState<EmployeeDetail | null>(null);
  const [detailLoading, setDetailLoading] = useState(false);
  const [activeTab, setActiveTab] = useState<DetailTab>('personal');
  const [statusReason, setStatusReason] = useState('');
  const [newStatus, setNewStatus] = useState<StatusFilter>('Active');
  const [transferReason, setTransferReason] = useState('');
  const [transferDepartment, setTransferDepartment] = useState('');
  const [editOpen, setEditOpen] = useState(false);
  const [editForm, setEditForm] = useState<Record<string, string>>({});
  const [editOriginal, setEditOriginal] = useState<Record<string, string>>({});
  const [editSaving, setEditSaving] = useState(false);
  const [editNotice, setEditNotice] = useState('');
  const [documentFile, setDocumentFile] = useState<File | null>(null);
  const [documentType, setDocumentType] = useState('Passport');
  const [documentExpiry, setDocumentExpiry] = useState('');
  const [statusSaving, setStatusSaving] = useState(false);
  const [companies, setCompanies] = useState<CompanyDto[]>([]);
  const [branches, setBranches] = useState<BranchDto[]>([]);
  const [departments, setDepartments] = useState<DepartmentDto[]>([]);
  const [designations, setDesignations] = useState<DesignationDto[]>([]);
  const [grades, setGrades] = useState<GradeDto[]>([]);
  const [gradePayScale, setGradePayScale] = useState<GradePayScaleComponentDto[]>([]);
  const [costCenters, setCostCenters] = useState<CostCenterDto[]>([]);
  const [managerCandidates, setManagerCandidates] = useState<EmployeeListItem[]>([]);

  const pageSize = 25;
  const totalPages = Math.max(1, Math.ceil(total / pageSize));
  const defaultCompany = useMemo(() => companies.length === 1 ? companies[0] : undefined, [companies]);
  const selectedFormCompany = useMemo(
    () => companies.find((item) => item.id === form.companyId) ?? defaultCompany,
    [companies, defaultCompany, form.companyId],
  );
  const formCountryCode = normalizeCountryCode(selectedFormCompany?.countryCode);

  const load = useCallback(async () => {
    setLoading(true);
    setError('');
    try {
      const res = await employeesApi.list({ search, status, page, pageSize });
      setEmployees(res.items);
      setTotal(res.total);
    } catch {
      setError('Could not load employees from the API.');
    } finally {
      setLoading(false);
    }
  }, [page, search, status]);

  const loadLookups = useCallback(async () => {
    const [companyRes, branchRes, deptRes, desigRes, gradeRes, costRes, managerRes] = await Promise.all([
      companiesApi.list(1, 100),
      branchesApi.list(undefined, 1, 100),
      departmentsApi.list(undefined, 1, 100),
      designationsApi.list(undefined, 1, 100),
      gradesApi.list(1, 100),
      costCentersApi.list(undefined, 1, 100),
      employeesApi.list({ status: 'Active', page: 1, pageSize: 100 }),
    ]);
    setCompanies(companyRes.items);
    setBranches(branchRes.items);
    setDepartments(deptRes.items);
    setDesignations(desigRes.items);
    setGrades(gradeRes.items);
    setCostCenters(costRes.items);
    setManagerCandidates(managerRes.items);
  }, []);

  useEffect(() => { load(); }, [load]);
  useEffect(() => { loadLookups().catch(() => setError('Could not load organization setup data.')); }, [loadLookups]);
  useEffect(() => {
    client.get<EmployeeUsageData>('/api/tenant-admin/usage')
      .then(r => setUsage(r.data))
      .catch((e: unknown) => {
        const status = (e as { response?: { status?: number } })?.response?.status;
        if (status === 402) {
          setSubscriptionBanner('Your subscription is inactive or expired. Please contact support.');
        }
      });
  }, []);
  useEffect(() => { setPage(1); }, [search, status]);
  useEffect(() => {
    const searchFromUrl = searchParams?.get('search') ?? null;
    if (searchFromUrl !== null && searchFromUrl !== search) {
      setSearch(searchFromUrl);
    }
  }, [search, searchParams]);
  useEffect(() => {
    const employeeId = searchParams?.get('employeeId') ?? null;
    if (!employeeId) return;
    const id = Number(employeeId);
    if (Number.isFinite(id) && id > 0 && selectedId !== id) {
      openDetail(id);
    }
  }, [searchParams, selectedId]);

  const selectedEmployee = useMemo(() => detail ?? null, [detail]);
  const selectedEmployeeCompanyCountry =
    companies.find((item) => item.id === selectedEmployee?.companyId)?.countryCode
    ?? accessibleCompanies.find((item) => item.id === selectedEmployee?.companyId)?.countryCode
    ?? accessibleCompanies.find((item) => item.id === selectedCompanyId)?.countryCode
    ?? (accessibleCompanies.length === 1 ? accessibleCompanies[0].countryCode : undefined);
  const selectedEmployeeCountryCode = useMemo(
    () => normalizeCountryCode(selectedEmployeeCompanyCountry || selectedEmployee?.countryCode),
    [selectedEmployeeCompanyCountry, selectedEmployee?.countryCode],
  );
  const editFields = useMemo(
    () => [...EDIT_FIELDS, ...complianceEditFieldsForCountry(selectedEmployeeCountryCode)],
    [selectedEmployeeCountryCode],
  );
  const selectedDocumentTypes = useMemo(
    () => documentTypesForCountry(selectedEmployeeCountryCode),
    [selectedEmployeeCountryCode],
  );
  const selectedDesignation = useMemo(
    () => designations.find((item) => item.id === form.designationId),
    [designations, form.designationId],
  );
  const selectedDepartment = useMemo(
    () => departments.find((item) => item.id === form.departmentId),
    [departments, form.departmentId],
  );
  const selectedLineManager = useMemo(
    () => managerCandidates.find((item) => item.id === form.reportingManagerEmployeeId),
    [managerCandidates, form.reportingManagerEmployeeId],
  );
  const selectedGrade = useMemo(
    () => grades.find((item) => item.id === form.gradeId),
    [grades, form.gradeId],
  );
  const eligibleGrades = useMemo(
    () => selectedDesignation?.gradeId ? grades.filter((grade) => grade.id === selectedDesignation.gradeId) : grades,
    [grades, selectedDesignation],
  );
  const salaryTotal = useMemo(() => {
    const s = form.salaryBreakdown;
    return Number(s?.basicSalary ?? 0) + Number(s?.housingAllowance ?? 0) + Number(s?.transportAllowance ?? 0)
      + Number(s?.foodAllowance ?? 0) + Number(s?.mobileAllowance ?? 0) + Number(s?.otherAllowance ?? 0);
  }, [form.salaryBreakdown]);

  useEffect(() => {
    if (!form.gradeId) {
      setGradePayScale([]);
      return;
    }
    gradesApi.getPayScale(form.gradeId)
      .then(setGradePayScale)
      .catch(() => setGradePayScale([]));
  }, [form.gradeId]);

  useEffect(() => {
    if (!formOpen) return;
    setForm((current) => {
      const desired = complianceRecordsForCountry(formCountryCode, current.complianceRecords);
      const currentKeys = (current.complianceRecords ?? []).map((record) => `${normalizeCountryCode(record.countryCode)}:${record.fieldKey}`).join('|');
      const desiredKeys = desired.map((record) => `${record.countryCode}:${record.fieldKey}`).join('|');
      if (currentKeys === desiredKeys) return current;
      return { ...current, complianceRecords: desired };
    });
  }, [formCountryCode, formOpen]);

  useEffect(() => {
    if (selectedDocumentTypes.length > 0 && !selectedDocumentTypes.includes(documentType)) {
      setDocumentType(selectedDocumentTypes[0]);
    }
  }, [documentType, selectedDocumentTypes]);

  const openDetail = async (id: number, preserveTab = false) => {
    setSelectedId(id);
    setDetailLoading(true);
    if (!preserveTab) setActiveTab('personal');
    try {
      setDetail(await employeesApi.get(id));
    } catch {
      setError('Could not load employee detail from the API.');
    } finally {
      setDetailLoading(false);
    }
  };

  const refreshAll = () => {
    load();
    if (selectedId) openDetail(selectedId, true);
  };

  const openCreateEmployee = () => {
    const next = emptyEmployee();
    if (defaultCompany) {
      next.companyId = defaultCompany.id;
      next.payrollProfile = {
        ...next.payrollProfile!,
        salaryCurrency: defaultCompany.defaultCurrency || next.payrollProfile?.salaryCurrency || currencyCode,
      };
      next.salaryBreakdown = {
        ...next.salaryBreakdown!,
        currency: defaultCompany.defaultCurrency || next.salaryBreakdown?.currency || currencyCode,
      };
      next.complianceRecords = complianceRecordsForCountry(defaultCompany.countryCode);
    }
    setForm(next);
    setFormOriginal(next);
    setFormError('');
    setFormOpen(true);
  };

  const setField = (key: keyof EmployeeCreateRequest, value: string | boolean | number | undefined) =>
    setForm((current) => ({ ...current, [key]: value }));

  const setDepartment = (value: string) =>
    setForm((current) => {
      const department = departments.find((item) => item.id === value);
      return {
        ...current,
        departmentId: value,
        reportingManagerEmployeeId: department?.managerEmployeeId,
      };
    });

  const setDesignation = (value: string) =>
    setForm((current) => {
      const designation = designations.find((item) => item.id === value);
      return {
        ...current,
        designationId: value,
        gradeId: designation?.gradeId ?? current.gradeId,
      };
    });

  const setGrade = (value: string) =>
    setForm((current) => ({ ...current, gradeId: value }));

  const { translation: autoArabicName, isTranslating: translatingArabicName } = useAutoTranslate(form.englishName);
  // eslint-disable-next-line react-hooks/exhaustive-deps
  useEffect(() => { if (autoArabicName && !form.arabicName) setField('arabicName', autoArabicName); }, [autoArabicName]);

  const setPayrollField = (key: string, value: string | boolean) =>
    setForm((current) => ({ ...current, payrollProfile: { ...current.payrollProfile!, [key]: value } }));

  const setSalaryField = (key: string, value: string) =>
    setForm((current) => ({
      ...current,
      salaryBreakdown: {
        ...current.salaryBreakdown,
        [key]: ['salaryStructureCode', 'effectiveDate', 'currency'].includes(key) ? value : (value === '' ? undefined : Number(value)),
      },
    }));

  const setComplianceValue = (index: number, key: string, value: string | boolean) =>
    setForm((current) => ({
      ...current,
      complianceRecords: (current.complianceRecords ?? []).map((record, i) => (i === index ? { ...record, [key]: value } : record)),
    }));

  const saveEmployee = async () => {
    setError('');
    setFormError('');
    if (!form.englishName.trim() || !form.gender) {
      setFormError('English full name and gender are required.');
      return;
    }
    if (selectedDesignation?.gradeId && form.gradeId && selectedDesignation.gradeId !== form.gradeId) {
      setFormError('Selected designation is restricted to a different grade.');
      return;
    }
    if (selectedGrade && salaryTotal > 0 && ((selectedGrade.minSalary > 0 && salaryTotal < selectedGrade.minSalary) || (selectedGrade.maxSalary > 0 && salaryTotal > selectedGrade.maxSalary))) {
      setFormError(`Salary package must be within ${selectedGrade.currency} ${selectedGrade.minSalary.toLocaleString()} - ${selectedGrade.maxSalary.toLocaleString()} for ${selectedGrade.code}.`);
      return;
    }
    setSaving(true);
    try {
      const created = await employeesApi.create(cleanPayload(form));
      setFormOpen(false);
      setForm(emptyEmployee());
      setFormOriginal(emptyEmployee());
      await load();
      await openDetail(created.id);
    } catch (e: unknown) {
      const status = (e as { response?: { status?: number; data?: { error?: string; message?: string; current?: number; limit?: number } } })?.response?.status;
      const data = (e as { response?: { data?: { error?: string; message?: string; current?: number; limit?: number } } })?.response?.data;
      if (status === 402) {
        setFormError('Your subscription is inactive or expired. Please contact support.');
      } else if (status === 422 && data?.error === 'employee_limit_reached') {
        setFormError(data.message ?? `Employee limit reached (${data.current}/${data.limit}). Please upgrade your subscription.`);
      } else {
        // Surface the server's actual validation details instead of a generic banner.
        const errors = (data as { errors?: Record<string, string[]> })?.errors;
        const detail = data?.message
          ?? (errors ? Object.entries(errors).map(([field, msgs]) => `${field}: ${msgs.join(' ')}`).join(' · ') : '');
        setFormError(detail ? `Employee could not be saved — ${detail}` : 'Employee could not be saved. Check validation errors and try again.');
      }
    } finally {
      setSaving(false);
    }
  };

  const openEdit = () => {
    if (!selectedEmployee) return;
    const source = selectedEmployee as unknown as Record<string, unknown>;
    const snapshot: Record<string, string> = {};
    for (const f of editFields) {
      const raw = source[f.key];
      if (raw === null || raw === undefined) { snapshot[f.key] = ''; continue; }
      snapshot[f.key] = f.type === 'date' ? String(raw).slice(0, 10) : String(raw);
    }
    setEditOriginal(snapshot);
    setEditForm({ ...snapshot });
    setEditNotice('');
    setEditOpen(true);
  };

  const editChangedKeys = Object.keys(editForm).filter((k) => editForm[k] !== editOriginal[k]);
  const formChanged = JSON.stringify(form) !== JSON.stringify(formOriginal);

  const closeEditModal = () => {
    if (editChangedKeys.length > 0 && !confirm('Discard unsaved employee changes?')) return;
    setEditOpen(false);
  };

  const closeCreateModal = () => {
    if (formChanged && !confirm('Discard this employee draft?')) return;
    setFormOpen(false);
    setFormError('');
  };

  const saveEdit = async () => {
    if (!selectedId || editChangedKeys.length === 0) return;
    setEditSaving(true);
    setEditNotice('');
    setActionNotice('');
    try {
      const changes: Record<string, unknown> = {};
      for (const key of editChangedKeys) {
        const field = editFields.find((f) => f.key === key)!;
        const value = editForm[key].trim();
        if (value === '') changes[key] = null;
        else if (field.type === 'number') changes[key] = Number(value);
        else changes[key] = value;
      }
      const res = await employeesApi.update(selectedId, new Date().toISOString().slice(0, 10), changes);
      if (res.status === 202) {
        const data = res.data as { sensitiveFields?: string[]; approvalRequestId?: string; appliedFields?: string[] };
        const fields = data.sensitiveFields ?? [];
        const applied = data.appliedFields?.length ? ` Immediate fields saved: ${data.appliedFields.join(', ')}.` : '';
        setActionNotice(`Sensitive changes submitted to Approval Center${data.approvalRequestId ? ` (${data.approvalRequestId.slice(0, 8)})` : ''}: ${fields.join(', ')}.${applied}`);
        setEditOpen(false);
        await openDetail(selectedId, true);
        await load();
      } else {
        setActionNotice('Employee details saved.');
        setEditOpen(false);
        await openDetail(selectedId, true);
        await load();
      }
    } catch (e: unknown) {
      const data = (e as { response?: { data?: { message?: string; errors?: Record<string, string[]> } } })?.response?.data;
      const detailMsg = data?.message ?? (data?.errors ? Object.entries(data.errors).map(([f, m]) => `${f}: ${m.join(' ')}`).join(' · ') : '');
      setEditNotice(detailMsg ? `Could not save — ${detailMsg}` : 'Could not save the changes. Please review the values and try again.');
    } finally {
      setEditSaving(false);
    }
  };

  const deleteEmployee = async () => {
    if (!selectedId || !detail) return;
    if (!confirm(`Delete ${detail.fullName}'s record? It will be hidden from all lists; history is kept for audit.`)) return;
    try {
      await employeesApi.remove(selectedId);
      setSelectedId(null);
      setDetail(null);
      await load();
    } catch {
      setError('Could not delete the employee. Only Admins can delete records.');
    }
  };

  const changeStatus = async () => {
    if (!selectedId || !newStatus || !statusReason.trim()) return;
    setStatusSaving(true);
    setActionNotice('');
    setError('');
    try {
      const updated = await employeesApi.changeStatus(selectedId, {
        status: newStatus,
        effectiveDate: new Date().toISOString().slice(0, 10),
        reason: statusReason,
      });
      setDetail(updated);
      setStatusReason('');
      setActionNotice(`Employee status changed to ${newStatus}.`);
      await load();
    } catch (e: unknown) {
      setError((e as { response?: { data?: { message?: string } } })?.response?.data?.message ?? 'Could not change employee status.');
    } finally {
      setStatusSaving(false);
    }
  };

  const activateEmployee = async () => {
    if (!selectedId) return;
    setStatusSaving(true);
    setActionNotice('');
    setError('');
    try {
      const updated = await employeesApi.activate(selectedId, {
        effectiveDate: new Date().toISOString().slice(0, 10),
        reason: statusReason.trim() || 'Activated from employee profile',
      });
      setDetail(updated);
      setNewStatus('Active');
      setStatusReason('');
      setActionNotice('Employee activated and is now live.');
      await load();
    } catch (e: unknown) {
      setError((e as { response?: { data?: { message?: string } } })?.response?.data?.message ?? 'Could not activate employee.');
    } finally {
      setStatusSaving(false);
    }
  };

  const uploadDocument = async () => {
    if (!selectedId || !documentFile) return;
    const uploaded = await employeesApi.uploadDocument(selectedId, {
      documentType,
      expiryDate: documentExpiry || undefined,
      renewalReminderDate: documentExpiry || undefined,
      isRequired: true,
      approvalStatus: 'Pending',
    }, documentFile);
    setDetail((current) => current ? { ...current, documents: [uploaded, ...current.documents] } : current);
    setDocumentFile(null);
    setDocumentExpiry('');
  };

  const requestTransfer = async () => {
    if (!selectedId || !transferDepartment || !transferReason.trim()) return;
    const dept = departments.find((item) => item.id === transferDepartment);
    const transfer = await employeesApi.transfer(selectedId, {
      newDepartment: dept?.nameEn ?? transferDepartment,
      effectiveDate: new Date().toISOString().slice(0, 10),
      reason: transferReason,
    });
    setDetail((current) => current ? { ...current, transfers: [transfer, ...current.transfers] } : current);
    setTransferDepartment('');
    setTransferReason('');
  };

  const atEmployeeLimit = usage !== null && usage.maxEmployees > 0 && usage.activeEmployees >= usage.maxEmployees;

  return (
    <div className="space-y-5">
      {subscriptionBanner && (
        <div className="rounded-lg bg-amber-50 border border-amber-200 px-4 py-3 text-sm text-amber-800 dark:bg-amber-900/20 dark:border-amber-800 dark:text-amber-300">
          {subscriptionBanner}
        </div>
      )}

      <div className="flex flex-col gap-3 md:flex-row md:items-center md:justify-between">
        <div>
          <h1 className="text-2xl font-extrabold text-slate-950 dark:text-white">Employee Management</h1>
          <p className="mt-0.5 text-sm text-slate-500 dark:text-slate-400">{total} employee records</p>
        </div>
        <div className="flex flex-wrap items-center gap-2">
          <ImportExportToolbar
            entityName="Employees"
            onExport={employeesImportExport.export}
            onDownloadTemplate={employeesImportExport.template}
            onImport={employeesImportExport.import}
          />
          <div className="relative group">
            <button
              type="button"
              onClick={() => { if (!atEmployeeLimit) openCreateEmployee(); }}
              disabled={atEmployeeLimit}
              className="btn-primary disabled:opacity-50 disabled:cursor-not-allowed"
            >
              <Plus className="h-4 w-4" />
              Add Employee
            </button>
            {atEmployeeLimit && usage && (
              <div className="absolute bottom-full left-0 mb-1.5 w-64 rounded-lg bg-slate-800 px-3 py-2 text-xs text-white shadow-lg hidden group-hover:block z-10">
                Employee limit reached ({usage.activeEmployees}/{usage.maxEmployees}). Upgrade your plan to add more employees.
              </div>
            )}
          </div>
        </div>
      </div>

      {error && <p className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-600 dark:bg-red-500/10 dark:text-red-300">{error}</p>}
      {actionNotice && <p className="rounded-lg bg-emerald-50 px-3 py-2 text-sm text-emerald-700 dark:bg-emerald-500/10 dark:text-emerald-300">{actionNotice}</p>}

      <div className="grid gap-5 xl:grid-cols-[minmax(0,1fr)_420px]">
        <section className="space-y-4">
          <div className="flex flex-col gap-2 sm:flex-row">
            <div className="relative flex-1">
              <Search className="absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
              <input value={search} onChange={(e) => setSearch(e.target.value)} className="input w-full pl-9" placeholder="Search employee code, name, email" />
            </div>
            <select value={status} onChange={(e) => setStatus(e.target.value as StatusFilter)} className="select sm:w-56" aria-label="Status filter">
              {statusOptions.map((item) => <option key={item || 'all'} value={item}>{item || 'All statuses'}</option>)}
            </select>
            <button type="button" onClick={refreshAll} className="btn-secondary">
              <RefreshCw className="h-4 w-4" />
              Refresh
            </button>
          </div>

          <div className="surface overflow-hidden">
            <div className="overflow-x-auto">
              <table className="w-full min-w-[820px] text-sm">
                <thead>
                  <tr className="border-b border-slate-100 dark:border-white/[0.07]">
                    {['Employee', 'Department', 'Designation', 'Branch', 'Status', 'Profile'].map((head) => (
                      <th key={head} className="px-4 py-3 text-left text-xs font-bold uppercase text-slate-400">{head}</th>
                    ))}
                  </tr>
                </thead>
                <tbody className="divide-y divide-slate-100 dark:divide-white/[0.05]">
                  {loading && <EmptyRow label="Loading live employees..." />}
                  {!loading && employees.length === 0 && <EmptyRow label="No employees found" />}
                  {!loading && employees.map((employee) => (
                    <tr key={employee.id} onClick={() => openDetail(employee.id)} className="cursor-pointer hover:bg-slate-50 dark:hover:bg-white/[0.03]">
                      <td className="px-4 py-3">
                        <div className="flex items-center gap-3">
                          <Avatar name={employee.fullName} size="sm" />
                          <div>
                            <p className="font-semibold text-slate-900 dark:text-white">{employee.fullName}</p>
                            <p className="text-xs text-slate-400">{employee.employeeCode}</p>
                          </div>
                        </div>
                      </td>
                      <td className="px-4 py-3 text-slate-600 dark:text-slate-300">{employee.department || '-'}</td>
                      <td className="px-4 py-3 text-slate-600 dark:text-slate-300">{employee.designation || '-'}</td>
                      <td className="px-4 py-3 text-slate-600 dark:text-slate-300">{employee.branch || '-'}</td>
                      <td className="px-4 py-3"><StatusChip {...statusTone(employee.status)} /></td>
                      <td className="px-4 py-3 font-semibold text-slate-700 dark:text-slate-200">{employee.profileCompletenessScore.toFixed(0)}%</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <div className="flex items-center justify-between border-t border-slate-100 px-4 py-3 text-xs text-slate-500 dark:border-white/[0.07]">
              <span>Page {page} of {totalPages}</span>
              <div className="flex gap-1">
                <button type="button" onClick={() => setPage((p) => Math.max(1, p - 1))} disabled={page === 1} className="btn-secondary h-7 px-2 text-xs disabled:opacity-40">Prev</button>
                <button type="button" onClick={() => setPage((p) => Math.min(totalPages, p + 1))} disabled={page === totalPages} className="btn-secondary h-7 px-2 text-xs disabled:opacity-40">Next</button>
              </div>
            </div>
          </div>
        </section>

        <aside className="surface min-h-[560px] overflow-hidden">
          {!selectedId && (
            <div className="flex h-full min-h-[520px] flex-col items-center justify-center px-6 text-center">
              <Users className="mb-3 h-10 w-10 text-slate-300 dark:text-slate-700" />
              <p className="font-semibold text-slate-900 dark:text-white">Select an employee</p>
              <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">Open a live profile with payroll, compliance, documents, history, and transfers.</p>
            </div>
          )}

          {selectedId && detailLoading && <div className="p-6 text-sm text-slate-500">Loading profile from API...</div>}

          {selectedEmployee && !detailLoading && (
            <div>
              <div className="border-b border-slate-100 p-4 dark:border-white/[0.07]">
                <div className="flex items-center gap-3">
                  <Avatar name={selectedEmployee.fullName} />
                  <div className="min-w-0 flex-1">
                    <p className="truncate font-bold text-slate-900 dark:text-white">{selectedEmployee.fullName}</p>
                    <p className="text-xs text-slate-500">{selectedEmployee.employeeCode} · {selectedEmployee.status}</p>
                  </div>
                  <button type="button" onClick={openEdit} className="btn-secondary h-8 shrink-0 px-3 text-xs">
                    <Pencil className="h-3.5 w-3.5" />
                    Edit
                  </button>
                </div>
                <div className="mt-4 flex gap-1 overflow-x-auto">
                  {tabs.map((tab) => (
                    <button key={tab.id} type="button" onClick={() => setActiveTab(tab.id)} className={`whitespace-nowrap rounded-lg px-2.5 py-1.5 text-xs font-semibold ${activeTab === tab.id ? 'bg-sapphire text-white' : 'text-slate-500 hover:bg-slate-100 dark:hover:bg-white/[0.07]'}`}>
                      {tab.label}
                    </button>
                  ))}
                </div>
              </div>
              <div className="space-y-4 p-4">
                {activeTab === 'personal' && (
                  <DetailGrid rows={[
                    ['English name', selectedEmployee.englishName],
                    ['Arabic name', selectedEmployee.arabicName],
                    ['Preferred name', selectedEmployee.preferredName],
                    ['Gender', selectedEmployee.gender],
                    ['Nationality', selectedEmployee.nationality],
                    ['Personal email', selectedEmployee.personalEmail],
                    ['Work email', selectedEmployee.workEmail],
                    ['Mobile', selectedEmployee.phone],
                  ]} />
                )}
                {activeTab === 'employment' && (
                  <DetailGrid rows={[
                    ['Company', nameById(companies, selectedEmployee.companyId, 'legalNameEn')],
                    ['Branch', selectedEmployee.branch],
                    ['Department', selectedEmployee.department],
                    ['Designation', selectedEmployee.designation],
                    ['Grade', selectedEmployee.grade],
                    ['Job title', selectedEmployee.jobTitle],
                    ['Employment type', selectedEmployee.employmentType],
                    ['Contract type', selectedEmployee.contractType],
                    ['Joining date', dateLabel(selectedEmployee.joiningDate)],
                    ['Work location', selectedEmployee.workLocation],
                  ]} />
                )}
                {activeTab === 'payroll' && (
                  <DetailGrid rows={[
                    ['Bank', detail!.payrollProfile?.bankName],
                    ['IBAN', detail!.payrollProfile?.iban],
                    ['Account', detail!.payrollProfile?.accountNumber],
                    ['Payment method', detail!.payrollProfile?.paymentMethod],
                    ['Currency', detail!.payrollProfile?.salaryCurrency],
                    ['Payroll group', detail!.payrollProfile?.payrollGroup],
                    ['WPS eligible', detail!.payrollProfile?.wpsEligible ? 'Yes' : 'No'],
                    ['EOSB eligible', detail!.payrollProfile?.eosbEligible ? 'Yes' : 'No'],
                  ]} />
                )}
                {activeTab === 'compliance' && (
                  <div className="space-y-2">
                    {detail!.complianceRecords.length === 0 && <SmallEmpty label="No compliance records saved" />}
                    {detail!.complianceRecords.map((record) => (
                      <div key={`${record.countryCode}-${record.fieldKey}`} className="rounded-lg border border-slate-200 p-3 dark:border-white/10">
                        <p className="text-sm font-semibold text-slate-900 dark:text-white">{record.fieldLabel}</p>
                        <p className="mt-1 text-xs text-slate-500">{record.countryCode} · {record.fieldValue || '-'} · Expires {dateLabel(record.expiryDate)}</p>
                      </div>
                    ))}
                  </div>
                )}
                {activeTab === 'documents' && (
                  <div className="space-y-3">
                    <div className="grid gap-2">
                      <select value={documentType} onChange={(e) => setDocumentType(e.target.value)} className="select w-full" aria-label="Document type">
                        {selectedDocumentTypes.map((item) => <option key={item}>{item}</option>)}
                      </select>
                      <input type="date" value={documentExpiry} onChange={(e) => setDocumentExpiry(e.target.value)} className="input w-full" aria-label="Document expiry" />
                      <input type="file" onChange={(e) => setDocumentFile(e.target.files?.[0] ?? null)} className="input w-full" aria-label="Upload document file" />
                      <button type="button" onClick={uploadDocument} disabled={!documentFile} className="btn-primary justify-center disabled:opacity-50">
                        <FileUp className="h-4 w-4" />
                        Upload metadata and file
                      </button>
                    </div>
                    {detail!.documents.map((doc) => (
                      <div key={doc.id} className="rounded-lg border border-slate-200 p-3 dark:border-white/10">
                        <p className="text-sm font-semibold text-slate-900 dark:text-white">{doc.documentType}</p>
                        <p className="text-xs text-slate-500">{doc.fileName} · {doc.approvalStatus} · Expires {dateLabel(doc.expiryDate)}</p>
                      </div>
                    ))}
                  </div>
                )}
                {activeTab === 'history' && (
                  <Timeline items={detail!.history.map((item) => ({ id: item.id, title: item.eventType, body: `${item.oldValue || '-'} -> ${item.newValue || '-'} · ${item.reason || 'No reason'}`, date: item.createdAtUtc }))} />
                )}
                {activeTab === 'transfers' && (
                  <div className="space-y-3">
                    <select value={transferDepartment} onChange={(e) => setTransferDepartment(e.target.value)} className="select w-full" aria-label="Transfer to department">
                      <option value="">New department</option>
                      {departments.map((dept) => <option key={dept.id} value={dept.id}>{dept.nameEn}</option>)}
                    </select>
                    <input value={transferReason} onChange={(e) => setTransferReason(e.target.value)} className="input w-full" placeholder="Transfer reason" />
                    <button type="button" onClick={requestTransfer} className="btn-primary justify-center">
                      <Send className="h-4 w-4" />
                      Request Transfer
                    </button>
                    {detail!.transfers.map((transfer) => (
                      <div key={transfer.id} className="rounded-lg border border-slate-200 p-3 dark:border-white/10">
                        <p className="text-sm font-semibold text-slate-900 dark:text-white">{transfer.newDepartment || 'Transfer request'}</p>
                        <p className="text-xs text-slate-500">{transfer.status} · Effective {dateLabel(transfer.effectiveDate)}</p>
                      </div>
                    ))}
                  </div>
                )}

                <div className="rounded-lg border border-slate-200 p-3 dark:border-white/10">
                  <p className="mb-2 text-xs font-bold uppercase text-slate-400">Status change</p>
                  <div className="grid gap-2">
                    <select value={newStatus} onChange={(e) => setNewStatus(e.target.value as StatusFilter)} className="select w-full" aria-label="New employee status">
                      {statusOptions.filter(Boolean).map((item) => <option key={item} value={item}>{item}</option>)}
                    </select>
                    <input value={statusReason} onChange={(e) => setStatusReason(e.target.value)} className="input w-full" placeholder="Required reason" />
                    {selectedEmployee.status !== 'Active' && (
                      <button type="button" onClick={activateEmployee} disabled={statusSaving} className="btn-primary justify-center disabled:opacity-50">
                        <UserRound className="h-4 w-4" />
                        {statusSaving ? 'Activating...' : 'Activate employee'}
                      </button>
                    )}
                    <button type="button" onClick={changeStatus} disabled={!statusReason.trim() || statusSaving} className="btn-secondary justify-center disabled:opacity-40">
                      <History className="h-4 w-4" />
                      {statusSaving ? 'Saving...' : 'Save status history'}
                    </button>
                  </div>
                </div>

                <div className="rounded-lg border border-red-200 p-3 dark:border-red-500/30">
                  <p className="mb-2 text-xs font-bold uppercase text-red-400">Danger zone</p>
                  <button type="button" onClick={deleteEmployee} className="w-full justify-center rounded-lg border border-red-300 px-3 py-2 text-sm font-medium text-red-600 hover:bg-red-50 dark:border-red-500/40 dark:text-red-400 dark:hover:bg-red-500/10">
                    Delete employee record
                  </button>
                  <p className="mt-1.5 text-[11px] text-slate-400">Hides the record from all lists and reports. History is kept for audit purposes — this is not a permanent erase.</p>
                </div>
              </div>
            </div>
          )}
        </aside>
      </div>

      <Modal isOpen={editOpen} title={`Edit Employee — ${selectedEmployee?.fullName ?? ''}`} size="xl" onClose={closeEditModal} footer={
        <>
          <button type="button" onClick={() => setEditForm({ ...editOriginal })} disabled={editChangedKeys.length === 0} className="btn-secondary disabled:opacity-40">
            Revert changes
          </button>
          <button type="button" onClick={closeEditModal} className="btn-secondary">Cancel</button>
          <button type="button" onClick={saveEdit} disabled={editSaving || editChangedKeys.length === 0} className="btn-primary disabled:opacity-60">
            {editSaving ? 'Saving...' : editChangedKeys.length > 0 ? `Save ${editChangedKeys.length} change${editChangedKeys.length === 1 ? '' : 's'}` : 'No changes'}
          </button>
        </>
      }>
        <div className="space-y-3">
          {editNotice && (
            <p className={`rounded-xl px-3 py-2.5 text-sm shadow-[inset_0_1px_0_rgba(255,255,255,0.75)] ${editNotice.startsWith('Submitted') ? 'bg-amber-50 text-amber-700 ring-1 ring-amber-100 dark:bg-amber-500/10 dark:text-amber-400 dark:ring-amber-500/20' : 'bg-red-50 text-red-600 ring-1 ring-red-100 dark:bg-red-500/10 dark:text-red-400 dark:ring-red-500/20'}`}>{editNotice}</p>
          )}
          <div className="grid gap-3 xl:grid-cols-2">
            {[...new Set(editFields.map((f) => f.section))].map((section) => (
            <fieldset key={section} className="rounded-2xl border border-white/80 bg-white/72 p-3 shadow-[8px_8px_22px_rgba(148,163,184,0.18),-8px_-8px_22px_rgba(255,255,255,0.85),inset_0_1px_0_rgba(255,255,255,0.95)] ring-1 ring-slate-900/[0.03] dark:border-white/10 dark:bg-white/[0.045] dark:shadow-[8px_8px_24px_rgba(0,0,0,0.24),inset_0_1px_0_rgba(255,255,255,0.06)]">
              <legend className="mb-2 flex items-center gap-1.5 px-1 text-xs font-bold uppercase tracking-wide text-slate-500 dark:text-slate-400">
                {section}
                {editFields.some((f) => f.section === section && f.sensitive) && (
                  <InfoTip text="Fields marked 'approval' are sensitive (payroll/identity). Saving them submits a change request that an authorised approver must sign off before it takes effect." />
                )}
              </legend>
              <div className="grid gap-2.5 sm:grid-cols-2 2xl:grid-cols-3">
                {editFields.filter((f) => f.section === section).map((f) => (
                  <label key={f.key} className="block text-sm font-medium text-slate-700 dark:text-slate-300">
                    <span className="flex items-center gap-1.5">
                      {f.label}
                      {f.sensitive && <span className="rounded bg-amber-100 px-1.5 py-0.5 text-[10px] font-semibold uppercase text-amber-700 dark:bg-amber-500/15 dark:text-amber-400">approval</span>}
                      {editForm[f.key] !== editOriginal[f.key] && <span className="h-1.5 w-1.5 rounded-full bg-sapphire" title="Modified" />}
                    </span>
                    {f.type === 'select' ? (
                      <select value={editForm[f.key] ?? ''} onChange={(e) => setEditForm((p) => ({ ...p, [f.key]: e.target.value }))} className="select mt-1.5 w-full">
                        <option value="">Select</option>
                        {f.options!.map((o) => <option key={o} value={o}>{o}</option>)}
                      </select>
                    ) : (
                      <input type={f.type ?? 'text'} value={editForm[f.key] ?? ''} onChange={(e) => setEditForm((p) => ({ ...p, [f.key]: e.target.value }))} className="input mt-1.5 w-full" />
                    )}
                  </label>
                ))}
              </div>
            </fieldset>
            ))}
          </div>
        </div>
      </Modal>

      <Modal isOpen={formOpen} title="Add Employee" size="xl" onClose={closeCreateModal} footer={
        <>
          <button type="button" onClick={closeCreateModal} className="btn-secondary">Cancel</button>
          <button type="button" onClick={saveEmployee} disabled={saving} className="btn-primary disabled:opacity-60">{saving ? 'Saving...' : 'Create Employee'}</button>
        </>
      }>
        <div className="space-y-3">
          {formError && (
            <p className="rounded-xl bg-red-50 px-3 py-2.5 text-sm text-red-600 ring-1 ring-red-100 shadow-[inset_0_1px_0_rgba(255,255,255,0.8)] dark:bg-red-500/10 dark:text-red-300 dark:ring-red-500/20">{formError}</p>
          )}
          <div className="grid gap-3 lg:grid-cols-2 2xl:grid-cols-3">
          <Section title="Master Profile">
            <Input label="Employee code" value={form.employeeCode ?? ''} onChange={(v) => setField('employeeCode', v)} placeholder="Leave blank for auto generation" info="Unique staff ID, e.g. KNX-0001. Leave blank and the system generates the next number automatically; tick 'Manual override' to type your own." infoKey="employees.employee_code" />
            <label className="flex items-center gap-2 text-sm font-medium text-slate-700 dark:text-slate-300">
              <input type="checkbox" checked={form.manualEmployeeCode} onChange={(e) => setField('manualEmployeeCode', e.target.checked)} className="h-4 w-4 accent-sapphire" />
              Manual override
            </label>
            <Input label="English full name" required value={form.englishName} onChange={(v) => setField('englishName', v)} info="Employee's full legal name in English, exactly as on their passport or ID. Required." infoKey="employees.english_name" />
            <Input label="Arabic full name" value={form.arabicName ?? ''} onChange={(v) => setField('arabicName', v)} rtl placeholder={translatingArabicName && !form.arabicName ? 'Translating…' : undefined} />
            <Input label="Preferred name" value={form.preferredName ?? ''} onChange={(v) => setField('preferredName', v)} />
            <Select label="Gender" required value={form.gender} onChange={(v) => setField('gender', v)} options={['Male', 'Female', 'Other']} />
            <Input label="Nationality" value={form.nationality ?? ''} onChange={(v) => setField('nationality', v)} />
            <Input label="Personal email" value={form.personalEmail ?? ''} onChange={(v) => setField('personalEmail', v)} type="email" />
            <Input label="Work email" value={form.workEmail ?? ''} onChange={(v) => setField('workEmail', v)} type="email" info="Company email address. Also used to link this employee to their login account for self-service (ESS)." infoKey="employees.work_email" />
            <Input label="Mobile number" value={form.mobileNumber ?? ''} onChange={(v) => setField('mobileNumber', v)} info="Personal mobile with country code, e.g. +971 50 123 4567." infoKey="employees.mobile_number" />
          </Section>

          <Section title="Employment Details">
            <Lookup
              label="Company"
              value={form.companyId ?? ''}
              onChange={(v) => {
                const company = companies.find((item) => item.id === v);
                setForm((current) => ({
                  ...current,
                  companyId: v,
                  payrollProfile: {
                    ...current.payrollProfile!,
                    salaryCurrency: company?.defaultCurrency || current.payrollProfile?.salaryCurrency || currencyCode,
                  },
                  salaryBreakdown: {
                    ...current.salaryBreakdown!,
                    currency: company?.defaultCurrency || current.salaryBreakdown?.currency || currencyCode,
                  },
                  complianceRecords: complianceRecordsForCountry(company?.countryCode, current.complianceRecords),
                }));
              }}
              items={companies}
              textKey="legalNameEn"
            />
            <Lookup label="Branch" value={form.branchId ?? ''} onChange={(v) => setField('branchId', v)} items={branches} textKey="nameEn" />
            <Lookup label="Department" value={form.departmentId ?? ''} onChange={setDepartment} items={departments} textKey="nameEn" />
            <label className="block text-sm font-medium text-slate-700 dark:text-slate-300">
              <span className="flex items-center gap-1.5">
                Line manager
                <InfoTip text="Auto-selected from the Department master manager. HR can override when the hierarchy requires an exception." fieldKey="employees.line_manager" />
              </span>
              <select
                value={form.reportingManagerEmployeeId ? String(form.reportingManagerEmployeeId) : ''}
                onChange={(e) => setField('reportingManagerEmployeeId', e.target.value ? Number(e.target.value) : undefined)}
                className="select mt-1.5 w-full"
              >
                <option value="">Select</option>
                {managerCandidates.map((manager) => (
                  <option key={manager.id} value={manager.id}>{manager.fullName} ({manager.employeeCode})</option>
                ))}
              </select>
              {selectedDepartment?.managerEmployeeId && form.reportingManagerEmployeeId === selectedDepartment.managerEmployeeId && (
                <span className="mt-1 block text-[11px] font-medium text-emerald-600 dark:text-emerald-400">
                  Auto-selected from {selectedDepartment.nameEn}{selectedLineManager ? `: ${selectedLineManager.fullName}` : ''}
                </span>
              )}
            </label>
            <Lookup label="Designation" value={form.designationId ?? ''} onChange={setDesignation} items={designations} textKey="titleEn" />
            <Lookup label="Grade" value={form.gradeId ?? ''} onChange={setGrade} items={eligibleGrades} textKey="name" />
            {selectedGrade && (
              <div className="rounded-lg border border-slate-200 bg-slate-50 p-3 text-xs text-slate-600 dark:border-white/10 dark:bg-white/[0.04] dark:text-slate-300">
                <p className="font-semibold text-slate-800 dark:text-white">{selectedGrade.code} salary band</p>
                <p className="mt-1">{selectedGrade.currency} {selectedGrade.minSalary.toLocaleString()} - {selectedGrade.maxSalary.toLocaleString()}</p>
              </div>
            )}
            <Lookup label="Cost center" value={form.costCenterId ?? ''} onChange={(v) => setField('costCenterId', v)} items={costCenters} textKey="name" />
            <Input label="Job title" value={form.jobTitle ?? ''} onChange={(v) => setField('jobTitle', v)} />
            <Select label="Employment type" value={form.employmentType ?? ''} onChange={(v) => setField('employmentType', v)} options={['Full-Time', 'Part-Time', 'Contractor', 'Intern']} info="How the employee is engaged. Affects payroll, leave entitlements and reports." infoKey="employees.employment_type" />
            <Select label="Contract type" value={form.contractType ?? ''} onChange={(v) => setField('contractType', v)} options={['Unlimited', 'Fixed-Term', 'Temporary']} />
            <Input label="Joining date" value={form.joiningDate ?? ''} onChange={(v) => setField('joiningDate', v)} type="date" info="First working day. Used for probation tracking, leave accrual and end-of-service (EOSB) calculations." infoKey="employees.joining_date" />
            <Input label="Work location" value={form.workLocation ?? ''} onChange={(v) => setField('workLocation', v)} />
          </Section>

          <Section title="Payroll Profile">
            <Input label="Bank name" value={form.payrollProfile?.bankName ?? ''} onChange={(v) => setPayrollField('bankName', v)} />
            <Input label="IBAN" value={form.payrollProfile?.iban ?? ''} onChange={(v) => setPayrollField('iban', v)} info="International bank account number for salary transfers, e.g. AE07 0331 2345 6789 0123 456. No spaces needed." infoKey="employees.iban" />
            <Input label="Account number" value={form.payrollProfile?.accountNumber ?? ''} onChange={(v) => setPayrollField('accountNumber', v)} />
            <Input label="Bank routing / sort code" value={form.payrollProfile?.bankRoutingCode ?? ''} onChange={(v) => setPayrollField('bankRoutingCode', v)} info="Bank branch routing or sort code required for WPS SIF export (UAE: 6-digit CBQ code; KSA: Mudad bank code)." infoKey="employees.bankRoutingCode" />
            <Input label="MOL ID / National labour number" value={form.payrollProfile?.molId ?? ''} onChange={(v) => setPayrollField('molId', v)} info="Ministry of Labour employee registration number — required in CBUAE WPS v2 SIF E1EDL20 segment and Saudi Mudad WPS." infoKey="employees.molId" />
            <Select label="Salary currency" value={form.payrollProfile?.salaryCurrency || currencyCode} onChange={(v) => setPayrollField('salaryCurrency', v)} options={['USD', 'GBP', 'EUR', 'AED', 'SAR', 'QAR', 'KWD', 'BHD', 'OMR']} />
            <Input label="Payroll group" value={form.payrollProfile?.payrollGroup ?? ''} onChange={(v) => setPayrollField('payrollGroup', v)} />
            <Input label="Salary structure reference" value={form.payrollProfile?.salaryStructureReference ?? ''} onChange={(v) => setPayrollField('salaryStructureReference', v)} />
          </Section>

          <Section title="Salary Structure Breakdown">
            <Input label="Basic salary" type="number" value={String(form.salaryBreakdown?.basicSalary ?? '')} onChange={(v) => setSalaryField('basicSalary', v)} />
            <Input label="Housing allowance" type="number" value={String(form.salaryBreakdown?.housingAllowance ?? '')} onChange={(v) => setSalaryField('housingAllowance', v)} />
            <Input label="Transport allowance" type="number" value={String(form.salaryBreakdown?.transportAllowance ?? '')} onChange={(v) => setSalaryField('transportAllowance', v)} />
            <Input label="Food allowance" type="number" value={String(form.salaryBreakdown?.foodAllowance ?? '')} onChange={(v) => setSalaryField('foodAllowance', v)} />
            <Input label="Mobile allowance" type="number" value={String(form.salaryBreakdown?.mobileAllowance ?? '')} onChange={(v) => setSalaryField('mobileAllowance', v)} />
            <Input label="Other allowance" type="number" value={String(form.salaryBreakdown?.otherAllowance ?? '')} onChange={(v) => setSalaryField('otherAllowance', v)} />
            <Input label="Fixed deduction" type="number" value={String(form.salaryBreakdown?.fixedDeduction ?? '')} onChange={(v) => setSalaryField('fixedDeduction', v)} />
            <Input label="Salary structure code" value={form.salaryBreakdown?.salaryStructureCode ?? ''} onChange={(v) => setSalaryField('salaryStructureCode', v)} placeholder={selectedGrade ? `GRADE-${selectedGrade.code}` : undefined} />
            <Select label="Breakdown currency" value={form.salaryBreakdown?.currency || form.payrollProfile?.salaryCurrency || currencyCode} onChange={(v) => setSalaryField('currency', v)} options={['USD', 'GBP', 'EUR', 'AED', 'SAR', 'QAR', 'KWD', 'BHD', 'OMR']} />
            <Input label="Effective date" type="date" value={form.salaryBreakdown?.effectiveDate ?? form.joiningDate ?? ''} onChange={(v) => setSalaryField('effectiveDate', v)} />
            <div className="rounded-lg border border-slate-200 bg-slate-50 p-3 text-xs text-slate-600 dark:border-white/10 dark:bg-white/[0.04] dark:text-slate-300">
              <p className="font-semibold text-slate-800 dark:text-white">Gross package: {(form.salaryBreakdown?.currency || form.payrollProfile?.salaryCurrency || currencyCode)} {salaryTotal.toLocaleString()}</p>
              {gradePayScale.length > 0 && <p className="mt-1">Grade defaults: {gradePayScale.map((line) => `${line.componentName} ${line.amount || `${line.percentage}%`}`).join(' · ')}</p>}
            </div>
          </Section>

          <Section title="Configurable GCC Compliance">
            {(form.complianceRecords ?? []).map((record, index) => (
              <div key={record.fieldKey} className="grid gap-2 rounded-lg border border-slate-200 p-3 dark:border-white/10">
                <Input label={record.fieldLabel} value={record.fieldValue ?? ''} onChange={(v) => setComplianceValue(index, 'fieldValue', v)} />
                <Input label="Expiry date" value={record.expiryDate ?? ''} onChange={(v) => setComplianceValue(index, 'expiryDate', v)} type="date" />
              </div>
            ))}
          </Section>
          </div>
        </div>
      </Modal>
    </div>
  );
}

function cleanPayload(form: EmployeeCreateRequest): EmployeeCreateRequest {
  // The API expects null/absent for empty Guid and date fields — an empty string
  // fails JSON model binding with a 400 before validation even runs.
  const emptyToUndefined = (value?: string) => value?.trim() ? value.trim() : undefined;
  const salaryBreakdown = form.salaryBreakdown
    ? {
        ...form.salaryBreakdown,
        salaryStructureCode: emptyToUndefined(form.salaryBreakdown.salaryStructureCode),
        effectiveDate: emptyToUndefined(form.salaryBreakdown.effectiveDate),
        currency: emptyToUndefined(form.salaryBreakdown.currency || form.payrollProfile?.salaryCurrency),
      }
    : undefined;
  const hasSalaryBreakdown = salaryBreakdown && Object.entries(salaryBreakdown)
    .some(([key, value]) => !['salaryStructureCode', 'effectiveDate', 'currency'].includes(key) && Number(value ?? 0) > 0);
  return {
    ...form,
    employeeCode: emptyToUndefined(form.employeeCode),
    companyId: emptyToUndefined(form.companyId),
    branchId: emptyToUndefined(form.branchId),
    departmentId: emptyToUndefined(form.departmentId),
    designationId: emptyToUndefined(form.designationId),
    gradeId: emptyToUndefined(form.gradeId),
    costCenterId: emptyToUndefined(form.costCenterId),
    personalEmail: emptyToUndefined(form.personalEmail),
    workEmail: emptyToUndefined(form.workEmail),
    joiningDate: emptyToUndefined(form.joiningDate),
    confirmationDate: emptyToUndefined(form.confirmationDate),
    probationStartDate: emptyToUndefined(form.probationStartDate),
    probationEndDate: emptyToUndefined(form.probationEndDate),
    payrollProfile: form.payrollProfile ? {
      ...form.payrollProfile,
      salaryCurrency: emptyToUndefined(form.payrollProfile.salaryCurrency),
      payrollGroup: emptyToUndefined(form.payrollProfile.payrollGroup),
      salaryStructureReference: emptyToUndefined(form.payrollProfile.salaryStructureReference),
    } : undefined,
    salaryBreakdown: hasSalaryBreakdown ? salaryBreakdown : undefined,
    complianceRecords: form.complianceRecords
      ?.filter((item) => item.fieldValue?.trim() || item.isRequired)
      .map((item) => ({ ...item, expiryDate: emptyToUndefined(item.expiryDate) })),
  };
}

function statusTone(status: string): { label: string; tone: 'emerald' | 'blue' | 'amber' | 'rose' | 'slate' } {
  if (['Active', 'Confirmed'].includes(status)) return { label: status, tone: 'emerald' };
  if (['Draft', 'Pre-boarding', 'Probation', 'Notice period'].includes(status)) return { label: status, tone: 'amber' };
  if (['Suspended', 'Terminated', 'Blacklisted'].includes(status)) return { label: status, tone: 'rose' };
  return { label: status, tone: 'slate' };
}

function EmptyRow({ label }: { label: string }) {
  return <tr><td colSpan={6} className="py-16 text-center text-sm text-slate-400">{label}</td></tr>;
}

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <fieldset className="rounded-2xl border border-white/80 bg-white/72 p-3 shadow-[8px_8px_22px_rgba(148,163,184,0.18),-8px_-8px_22px_rgba(255,255,255,0.85),inset_0_1px_0_rgba(255,255,255,0.95)] ring-1 ring-slate-900/[0.03] dark:border-white/10 dark:bg-white/[0.045] dark:shadow-[8px_8px_24px_rgba(0,0,0,0.24),inset_0_1px_0_rgba(255,255,255,0.06)]">
      <legend className="mb-2 px-1 text-xs font-bold uppercase tracking-wide text-slate-500 dark:text-slate-400">{title}</legend>
      <div className="grid gap-2.5 sm:grid-cols-2">{children}</div>
    </fieldset>
  );
}

function Input({ label, value, onChange, required, type = 'text', placeholder, rtl, info, infoKey }: { label: string; value: string; onChange: (value: string) => void; required?: boolean; type?: string; placeholder?: string; rtl?: boolean; info?: string; infoKey?: string }) {
  return (
    <label className="block min-w-0 text-sm font-medium text-slate-700 dark:text-slate-300">
      <span className="flex min-w-0 items-center gap-1.5 leading-snug">{label} {required && <span className="text-red-500">*</span>}{info && <InfoTip text={info} fieldKey={infoKey} className="ml-1" />}</span>
      <input type={type} value={value} onChange={(e) => onChange(e.target.value)} placeholder={placeholder} dir={rtl ? 'rtl' : undefined} className="input mt-1.5 w-full" />
    </label>
  );
}

function Select({ label, value, onChange, options, required, info, infoKey }: { label: string; value: string; onChange: (value: string) => void; options: string[]; required?: boolean; info?: string; infoKey?: string }) {
  return (
    <label className="block min-w-0 text-sm font-medium text-slate-700 dark:text-slate-300">
      <span className="flex min-w-0 items-center gap-1.5 leading-snug">{label} {required && <span className="text-red-500">*</span>}{info && <InfoTip text={info} fieldKey={infoKey} className="ml-1" />}</span>
      <select value={value} onChange={(e) => onChange(e.target.value)} className="select mt-1.5 w-full">
        <option value="">Select</option>
        {options.map((option) => <option key={option} value={option}>{option}</option>)}
      </select>
    </label>
  );
}

function Lookup<T extends { id: string }>({ label, value, onChange, items, textKey }: { label: string; value: string; onChange: (value: string) => void; items: T[]; textKey: keyof T }) {
  return (
    <label className="block text-sm font-medium text-slate-700 dark:text-slate-300">
      <span>{label}</span>
      <select value={value} onChange={(e) => onChange(e.target.value)} className="select mt-1.5 w-full">
        <option value="">Select</option>
        {items.map((item) => <option key={item.id} value={item.id}>{String(item[textKey])}</option>)}
      </select>
    </label>
  );
}

function DetailGrid({ rows }: { rows: Array<[string, string | number | boolean | null | undefined]> }) {
  return <dl className="grid gap-3">{rows.map(([label, value]) => <div key={label} className="rounded-lg bg-slate-50 p-3 dark:bg-white/[0.04]"><dt className="text-xs font-semibold uppercase text-slate-400">{label}</dt><dd className="mt-1 text-sm text-slate-800 dark:text-slate-100">{value || '-'}</dd></div>)}</dl>;
}

function Timeline({ items }: { items: Array<{ id: string; title: string; body: string; date: string }> }) {
  if (items.length === 0) return <SmallEmpty label="No history records yet" />;
  return <div className="space-y-3">{items.map((item) => <div key={item.id} className="rounded-lg border border-slate-200 p-3 dark:border-white/10"><p className="text-sm font-semibold text-slate-900 dark:text-white">{item.title}</p><p className="mt-1 text-xs text-slate-500">{item.body}</p><p className="mt-2 text-[11px] text-slate-400">{dateLabel(item.date)}</p></div>)}</div>;
}

function SmallEmpty({ label }: { label: string }) {
  return <div className="rounded-lg border border-dashed border-slate-200 p-5 text-center text-sm text-slate-400 dark:border-white/10"><UserRound className="mx-auto mb-2 h-5 w-5" />{label}</div>;
}

function dateLabel(value?: string) {
  if (!value) return '-';
  return value.slice(0, 10);
}

function nameById<T extends { id: string }>(items: T[], id: string | undefined, key: keyof T) {
  if (!id) return '-';
  return String(items.find((item) => item.id === id)?.[key] ?? '-');
}
