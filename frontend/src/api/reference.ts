import client from './client';

export interface IsoCountry { code: string; name: string; currency: string }
export interface IsoCurrency { code: string; name: string; symbol: string }

export const referenceApi = {
  countries: () => client.get<IsoCountry[]>('/api/reference/countries').then((r) => r.data),
  currencies: () => client.get<IsoCurrency[]>('/api/reference/currencies').then((r) => r.data),
};
