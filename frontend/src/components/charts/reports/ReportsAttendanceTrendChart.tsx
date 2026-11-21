'use client';

import {
  CartesianGrid,
  Line,
  LineChart,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
} from 'recharts';
import { useLocale } from '../../../contexts/LocaleContext';

export function ReportsAttendanceTrendChart({
  data,
}: {
  data: { date: string; present: number; absent: number; late: number }[];
}) {
  const { t } = useLocale();
  return (
    <ResponsiveContainer width="100%" height={200}>
      <LineChart data={data.slice(-14)}>
        <CartesianGrid strokeDasharray="3 3" stroke="#e2e8f0" />
        <XAxis dataKey="date" tick={{ fontSize: 10 }} />
        <YAxis tick={{ fontSize: 11 }} />
        <Tooltip />
        <Line type="monotone" dataKey="present" stroke="#2F6BFF" strokeWidth={2} dot={false} name={t('Present')} />
        <Line type="monotone" dataKey="late" stroke="#F59E0B" strokeWidth={2} dot={false} name={t('Late')} />
        <Line type="monotone" dataKey="absent" stroke="#EF4444" strokeWidth={2} dot={false} name={t('Absent')} />
      </LineChart>
    </ResponsiveContainer>
  );
}
