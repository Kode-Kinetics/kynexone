'use client';

import {
  Bar,
  BarChart,
  CartesianGrid,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
} from 'recharts';
import type { NamedValue } from '../../../api/dashboard';
import { useTenantSettings } from '../../../contexts/TenantSettingsContext';

const tooltipStyle = {
  borderRadius: 8,
  border: '1px solid rgba(148,163,184,0.25)',
  fontSize: 12,
  boxShadow: '0 4px 16px rgba(0,0,0,0.08)',
};

function fmtMoney(n: number, currency: string): string {
  if (Math.abs(n) >= 1_000_000) return `${currency} ${(n / 1_000_000).toFixed(1)}M`;
  if (Math.abs(n) >= 1_000) return `${currency} ${(n / 1_000).toFixed(1)}K`;
  return `${currency} ${Math.round(n).toLocaleString()}`;
}

export function DashboardPayrollByEntityChart({ data }: { data: NamedValue[] }) {
  const { currencyCode } = useTenantSettings();
  return (
    <ResponsiveContainer width="100%" height="100%">
      <BarChart data={data} margin={{ left: -22, right: 4, top: 4, bottom: 0 }}>
        <CartesianGrid strokeDasharray="3 3" stroke="currentColor" className="text-slate-100 dark:text-white/[0.06]" />
        <XAxis dataKey="name" tickLine={false} axisLine={false} tick={{ fontSize: 10 }} />
        <YAxis tickLine={false} axisLine={false} tick={{ fontSize: 10 }} />
        <Tooltip contentStyle={tooltipStyle} formatter={(v) => [fmtMoney(Number(v), currencyCode), 'Net']} />
        <Bar dataKey="value" fill="#2F6BFF" radius={[4, 4, 0, 0]} />
      </BarChart>
    </ResponsiveContainer>
  );
}
