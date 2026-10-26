import type { BranchDto, CompanyDto, DepartmentDto, DesignationDto, GradeDto } from '@/src/api/organization';
import type { EmployeeListItem } from '@/src/api/employees';
import type { EmployeeReferenceLists } from '@/src/hooks/useReferenceLists';
import type { HireForm } from '@/src/lib/hire/hireForm';

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
}
