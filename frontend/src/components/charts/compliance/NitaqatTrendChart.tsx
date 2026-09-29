'use client';

import {
  Area, AreaChart, CartesianGrid, Label, ReferenceLine,
  ResponsiveContainer, Tooltip, XAxis, YAxis,
} from 'recharts';

interface Point {
  asOfDate: string;
  achievedPercent: number;
}

/**
 * Saudization over time against the band floors that matter.
 *
 * The two reference lines are the whole point: a bare percentage line tells an HR
 * director nothing, whereas a line approaching the floor of the band they are
 * standing in tells them a visa freeze is coming. The floor is drawn in rose and
 * the band above in emerald so the reading survives a glance.
 */
export function NitaqatTrendChart({
  points,
  floor,
  nextFloor,
}: {
  points: Point[];
  floor: number;
  nextFloor: number | null;
}) {
  const values = points.map((p) => p.achievedPercent);
  const candidates = [...values, floor, ...(nextFloor != null ? [nextFloor] : [])];
  const min = Math.max(0, Math.floor(Math.min(...candidates) - 3));
  const max = Math.min(100, Math.ceil(Math.max(...candidates) + 3));

  return (
    <ResponsiveContainer width="100%" height="100%">
      <AreaChart data={points} margin={{ top: 8, right: 8, bottom: 0, left: -12 }}>
        <CartesianGrid strokeDasharray="3 3" stroke="currentColor" className="text-slate-200 dark:text-white/10" />
        <XAxis dataKey="asOfDate" tick={{ fontSize: 11 }} stroke="currentColor" className="text-slate-500 dark:text-slate-400" />
        <YAxis
          domain={[min, max]}
          tickFormatter={(v: number) => `${v}%`}
          tick={{ fontSize: 11 }}
          stroke="currentColor"
          className="text-slate-500 dark:text-slate-400"
        />
        <Tooltip
          formatter={(v) => [`${Number(v ?? 0).toFixed(2)}%`, 'Saudization']}
          contentStyle={{ fontSize: 12, borderRadius: 8 }}
        />

        <ReferenceLine y={floor} stroke="#EF4444" strokeDasharray="4 4">
          <Label value={`Band floor ${floor.toFixed(1)}%`} position="insideBottomLeft" fontSize={10} fill="#EF4444" />
        </ReferenceLine>

        {nextFloor != null && (
          <ReferenceLine y={nextFloor} stroke="#00C896" strokeDasharray="4 4">
            <Label value={`Next band ${nextFloor.toFixed(1)}%`} position="insideTopLeft" fontSize={10} fill="#00C896" />
          </ReferenceLine>
        )}

        <Area
          type="monotone"
          dataKey="achievedPercent"
          stroke="#2F6BFF"
          fill="#2F6BFF"
          fillOpacity={0.15}
          strokeWidth={2}
          dot={points.length <= 30}
        />
      </AreaChart>
    </ResponsiveContainer>
  );
}
