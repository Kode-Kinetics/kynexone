'use client';

type SaudiAddressValue = {
  addressLine1?: string;
  addressLine2?: string;
  city?: string;
  region?: string;
  postalCode?: string;
  country?: string;
  buildingNo?: string;
  additionalNo?: string;
  district?: string;
};

interface SaudiAddressFieldsProps {
  value: SaudiAddressValue;
  onChange: (value: SaudiAddressValue) => void;
  compact?: boolean;
}

const fieldClass = 'w-full rounded-2xl border border-slate-200/80 bg-white/85 px-3.5 py-3 text-sm text-slate-900 outline-none transition placeholder:text-slate-400 focus:border-cyan-400 focus:ring-4 focus:ring-cyan-500/10 dark:border-white/10 dark:bg-white/[0.04] dark:text-white';

export function SaudiAddressFields({ value, onChange, compact }: SaudiAddressFieldsProps) {
  const update = (key: keyof SaudiAddressValue, next: string) => onChange({ ...value, [key]: next });

  return (
    <div className={compact ? 'grid gap-3 md:grid-cols-2' : 'grid gap-3 sm:grid-cols-2'}>
      <label className="space-y-1">
        <span className="text-[10px] font-bold uppercase tracking-[0.22em] text-slate-400">Address line 1</span>
        <input className={fieldClass} value={value.addressLine1 ?? ''} onChange={(e) => update('addressLine1', e.target.value)} placeholder="Building or street" />
      </label>
      <label className="space-y-1">
        <span className="text-[10px] font-bold uppercase tracking-[0.22em] text-slate-400">Address line 2</span>
        <input className={fieldClass} value={value.addressLine2 ?? ''} onChange={(e) => update('addressLine2', e.target.value)} placeholder="Unit / floor / landmark" />
      </label>
      <label className="space-y-1">
        <span className="text-[10px] font-bold uppercase tracking-[0.22em] text-slate-400">City</span>
        <input className={fieldClass} value={value.city ?? ''} onChange={(e) => update('city', e.target.value)} placeholder="Riyadh" />
      </label>
      <label className="space-y-1">
        <span className="text-[10px] font-bold uppercase tracking-[0.22em] text-slate-400">Region</span>
        <input className={fieldClass} value={value.region ?? ''} onChange={(e) => update('region', e.target.value)} placeholder="Central Province" />
      </label>
      <label className="space-y-1">
        <span className="text-[10px] font-bold uppercase tracking-[0.22em] text-slate-400">Postal code</span>
        <input className={fieldClass} value={value.postalCode ?? ''} onChange={(e) => update('postalCode', e.target.value)} placeholder="11564" />
      </label>
      <label className="space-y-1">
        <span className="text-[10px] font-bold uppercase tracking-[0.22em] text-slate-400">Country</span>
        <input className={fieldClass} value={value.country ?? ''} onChange={(e) => update('country', e.target.value)} placeholder="Saudi Arabia" />
      </label>
      <label className="space-y-1">
        <span className="text-[10px] font-bold uppercase tracking-[0.22em] text-slate-400">Building no.</span>
        <input className={fieldClass} value={value.buildingNo ?? ''} onChange={(e) => update('buildingNo', e.target.value)} placeholder="1234" />
      </label>
      <label className="space-y-1">
        <span className="text-[10px] font-bold uppercase tracking-[0.22em] text-slate-400">Additional no.</span>
        <input className={fieldClass} value={value.additionalNo ?? ''} onChange={(e) => update('additionalNo', e.target.value)} placeholder="5678" />
      </label>
      <label className="space-y-1 md:col-span-2">
        <span className="text-[10px] font-bold uppercase tracking-[0.22em] text-slate-400">District</span>
        <input className={fieldClass} value={value.district ?? ''} onChange={(e) => update('district', e.target.value)} placeholder="Al Olaya" />
      </label>
    </div>
  );
}
