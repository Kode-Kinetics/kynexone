import type { BranchDto, CompanyDto, DepartmentDto, DesignationDto, GradeDto } from '@/src/api/organization';
import type { EmployeeListItem } from '@/src/api/employees';
import type { EmployeeReferenceLists } from '@/src/hooks/useReferenceLists';
import type { HireForm } from '@/src/lib/hire/hireForm';
import type { SaBank } from '@/src/lib/hire/iban';
import type { CashAllowanceCatalog } from '@/src/hooks/useCashAllowanceCatalog';
import type { HireCostCentre } from './useHireCostCentre';

export interface Lookups {
  companies: CompanyDto[];
  branches: BranchDto[];
  departments: DepartmentDto[];
  designations: DesignationDto[];
  grades: GradeDto[];
  managers: EmployeeListItem[];
  refs: EmployeeReferenceLists;
}

export interface StepProps {
  form: HireForm;
  set: <K extends keyof HireForm>(key: K, value: HireForm[K]) => void;
  /** Error text for a field, only once it has been blurred or a Next / Save was tried. */
  errorFor: (field: keyof HireForm) => string | undefined;
  blur: (field: keyof HireForm) => void;
  company: CompanyDto | undefined;
  companyCountry: string;
  currency: string;
  lookups: Lookups;
  today: string;
  singleCompany: boolean;
  /** The SAMA bank list, or null when it could not be loaded. */
  banks: SaBank[] | null;
  /** The monthly cash allowances this tenant offers and what the grade gives (Benefits by grade), read once for the whole wizard. */
  allowanceCatalog: { catalog: CashAllowanceCatalog; retry: () => void };
  /** Wave A: the cost centre (dropdown, follow option, its words for Review). */
  costCentre?: HireCostCentre;
}
