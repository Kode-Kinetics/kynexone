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
import { useLocale } from '../../../contexts/LocaleContext';

function fmt(n: number) {
  return n.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
}

export function ReportsPayrollTrendChart({
  data,
}: {
  data: { period: string; TotalNetSalary: number; TotalGrossSalary: number }[];
}) {
  const { t } = useLocale();
  return (
    <ResponsiveContainer width="100%" height={200}>
      <BarChart data={[...data].reverse()}>
        <CartesianGrid strokeDasharray="3 3" stroke="#e2e8f0" />
        <XAxis dataKey="period" tick={{ fontSize: 11 }} />
        <YAxis tick={{ fontSize: 11 }} tickFormatter={(v) => `${(v / 1000).toFixed(0)}k`} />
        <Tooltip formatter={(v) => fmt(v as number)} />
        <Bar dataKey="TotalNetSalary" name={t('Net Salary')} fill="#00C896" radius={[3, 3, 0, 0]} />
      </BarChart>
    </ResponsiveContainer>
  );
}
