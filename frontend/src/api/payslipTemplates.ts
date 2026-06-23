import client from './client';

export interface FieldDef {
  key: string;
  labelEn: string;
  labelAr: string;
  isComplianceLocked: boolean;
}

export interface SectionDef {
  key: string;
  labelEn: string;
  labelAr: string;
  canDisable: boolean;
  fields: FieldDef[];
}

export interface PayslipBranding {
  primaryColor: string;
  accentColor: string;
  fontFamily: string;
  headerTextEn: string;
  headerTextAr: string;
  footerTextEn: string;
  footerTextAr: string;
  logoStorageUrl: string | null;
  locale: string;
}

export interface PayslipSectionConfig {
  key: string;
  enabled: boolean;
  order: number;
  fields: string[];
}

export interface PayslipLayout {
  locale: string;
  sections: PayslipSectionConfig[];
}

export interface TemplateListItem {
  id: string;
  name: string;
  isDefault: boolean;
  version: number;
  status: string;
  createdAtUtc: string;
  updatedAtUtc: string;
  parentTemplateId: string | null;
}

export interface TemplateDto extends TemplateListItem {
  brandingJson: string;
  layoutJson: string;
  createdByUserId: string | null;
}

export const DEFAULT_BRANDING: PayslipBranding = {
  primaryColor: '#1E3A5F',
  accentColor: '#2563EB',
  fontFamily: 'NotoSans',
  headerTextEn: '',
  headerTextAr: '',
  footerTextEn: '',
  footerTextAr: '',
  logoStorageUrl: null,
  locale: 'en',
};

export const DEFAULT_LAYOUT: PayslipLayout = {
  locale: 'en',
  sections: [
    { key: 'earnings',   enabled: true,  order: 1, fields: ['basic_salary', 'housing_allowance', 'transport_allowance', 'other_allowances'] },
    { key: 'deductions', enabled: true,  order: 2, fields: ['gosi_annuities_ee', 'gosi_saned_ee', 'loan_repayment'] },
    { key: 'ytd',        enabled: false, order: 3, fields: ['ytd_gross', 'ytd_deductions', 'ytd_net'] },
    { key: 'bank_wps',   enabled: false, order: 4, fields: ['iban', 'bank_name'] },
    { key: 'signatory',  enabled: false, order: 5, fields: ['signatory_name', 'signatory_title'] },
  ],
};

export const payslipTemplatesApi = {
  registry: () =>
    client.get<SectionDef[]>('/api/payslip-templates/registry').then((r) => r.data),

  list: () =>
    client.get<TemplateListItem[]>('/api/payslip-templates').then((r) => r.data),

  get: (id: string) =>
    client.get<TemplateDto>(`/api/payslip-templates/${id}`).then((r) => r.data),

  versions: (id: string) =>
    client.get<TemplateListItem[]>(`/api/payslip-templates/${id}/versions`).then((r) => r.data),

  create: (name: string, isDefault: boolean, branding: PayslipBranding, layout: PayslipLayout) =>
    client.post<TemplateDto>('/api/payslip-templates', {
      name,
      isDefault,
      brandingJson: JSON.stringify(branding),
      layoutJson: JSON.stringify(layout),
    }).then((r) => r.data),

  update: (id: string, name: string, isDefault: boolean, branding: PayslipBranding, layout: PayslipLayout) =>
    client.put<TemplateDto>(`/api/payslip-templates/${id}`, {
      name,
      isDefault,
      brandingJson: JSON.stringify(branding),
      layoutJson: JSON.stringify(layout),
    }).then((r) => r.data),

  setDefault: (id: string) =>
    client.post(`/api/payslip-templates/${id}/set-default`).then((r) => r.data),

  delete: (id: string) =>
    client.delete(`/api/payslip-templates/${id}`),

  uploadLogo: async (file: File): Promise<string> => {
    const form = new FormData();
    form.append('file', file);
    const res = await client.post<{ storageUrl: string }>('/api/payslip-templates/logo', form, {
      headers: { 'Content-Type': 'multipart/form-data' },
    });
    return res.data.storageUrl;
  },

  previewUrl: (id: string) => `/api/payslip-templates/${id}/preview`,

  preview: async (id: string): Promise<string> => {
    const res = await client.post(`/api/payslip-templates/${id}/preview`, null, { responseType: 'blob' });
    return URL.createObjectURL(new Blob([res.data as BlobPart], { type: 'application/pdf' }));
  },
};
