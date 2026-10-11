/** The CMP journey world written by compensation-seed.ts and read by compensation-journey.spec.ts. */
export const CMP_WORLD = 'e2e/.auth/cmp-world.json';

export interface CmpWorld {
  slug: string; betaSlug: string; password: string; companyId: string; engDepartmentId: string; opsDepartmentId: string;
  gradeId: string; designationId: string;
  users: { admin: string; hr: string; hrDirector: string; finance: string; manager: string; betaAdmin: string };
  employees: Record<string, number>;
}

