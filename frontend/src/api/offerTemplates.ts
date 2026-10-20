import client from './client';

export interface OfferDesignBlock {
  id: string; kind: 'heading' | 'text' | 'table' | 'compensation' | 'image' | 'signature' | 'divider' | 'spacer' | 'pageBreak';
  text: string; enabled: boolean; align: string; fontSize: number | null; color: string | null;
  background: string | null; bold: boolean; italic: boolean; underline: boolean; spacing: number;
  image: string | null; imageWidth: number; rows: { label: string; value: string }[];
}
export interface OfferDesign {
  schemaVersion: number; pageSize: string; landscape: boolean; margin: number; direction: string;
  fontFamily: string; headingFontFamily: string; customFont: string | null; customFontName: string;
  fontSize: number; headingSize: number; lineHeight: number;
  textColor: string; accentColor: string; backgroundColor: string; pageBorder: boolean;
  header: string; footer: string; pageNumbers: boolean;
  logo: string | null; logoWidth: number; logoAlign: string;
  signature: string | null; stamp: string | null; signatory: string; signatoryTitle: string; candidateAcceptance: string;
  customFields: Record<string, string>; blocks: OfferDesignBlock[];
}
export interface OfferTemplateSummary { id: string; name: string; companyId: string | null; version: number; updatedAtUtc: string }
export interface OfferTemplate extends OfferTemplateSummary { design: OfferDesign; archived: boolean }
export const offerTemplatesApi = {
  defaults: () => client.get<{ design: OfferDesign; tokens: string[] }>('/api/recruitment/offer-templates/defaults').then(r => r.data),
  list: () => client.get<OfferTemplateSummary[]>('/api/recruitment/offer-templates').then(r => r.data),
  get: (id: string) => client.get<OfferTemplate>(`/api/recruitment/offer-templates/${id}`).then(r => r.data),
  create: (name: string, design: OfferDesign) => client.post<OfferTemplate>('/api/recruitment/offer-templates', { name, design }).then(r => r.data),
  update: (id: string, name: string, design: OfferDesign, expectedVersion: number) => client.put<OfferTemplate>(`/api/recruitment/offer-templates/${id}`, { name, design, expectedVersion }).then(r => r.data),
  archive: (id: string, expectedVersion: number) => client.post(`/api/recruitment/offer-templates/${id}/archive`, { expectedVersion }),
  preview: (design: OfferDesign, signal?: AbortSignal) => client.post<Blob>('/api/recruitment/offer-templates/preview', { design }, { responseType: 'blob', signal }).then(r => r.data),
  candidatePreview: (offerId: string, expectedVersion: number, design: OfferDesign, signal?: AbortSignal) => client.post<Blob>('/api/recruitment/offer-templates/preview', { offerId, expectedVersion, design }, { responseType: 'blob', signal }).then(r => r.data),
};

export function newOfferBlock(kind: OfferDesignBlock['kind']): OfferDesignBlock {
  return { id: crypto.randomUUID(), kind, text: kind === 'heading' ? 'Section heading' : kind === 'text' ? 'Enter your wording here.' : '', enabled: true, align: 'left', fontSize: null, color: null, background: null, bold: false, italic: false, underline: false, spacing: 12, image: null, imageWidth: 180, rows: kind === 'table' ? [{ label: 'Label', value: 'Value' }] : [] };
}

export function downloadOfferFile(blob: Blob, filename: string) {
  const url = URL.createObjectURL(blob);
  const link = document.createElement('a'); link.href = url; link.download = filename; link.click();
  window.setTimeout(() => URL.revokeObjectURL(url), 10000);
}
