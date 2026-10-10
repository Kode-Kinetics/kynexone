import React, { useEffect, useState } from 'react';
import { StyleSheet, View } from 'react-native';
import { useTranslation } from 'react-i18next';
import { LiquidBackdrop, ScreenHero, SegmentedControl } from '@/components/ui';
import { useTheme } from '@/theme/ThemeProvider';
import type { MyLeaveRequest } from '@/api/leaveRequests';
import { LeaveRequestForm } from './LeaveRequestForm';
import { MyLeaveRequests } from './MyLeaveRequests';

type Segment = 'request' | 'history';

interface Props {
  navigation?: unknown;
  route?: { params?: { segment?: Segment } };
}

/**
 * Leave: one screen, two segments — Request (a single vertical form) and My requests (status,
 * who it is waiting for, withdraw / ask to cancel). Accepts the route param `segment`.
 */
export default function ApplyLeaveScreen({ route }: Props) {
  const { theme } = useTheme();
  const { t } = useTranslation();
  const requested = route?.params?.segment;
  const [segment, setSegment] = useState<Segment>(requested === 'history' ? 'history' : 'request');
  const [sent, setSent] = useState<{ request: MyLeaveRequest; typeName: string } | null>(null);
  const [historyKey, setHistoryKey] = useState(0);

  useEffect(() => {
    if (requested === 'history' || requested === 'request') setSegment(requested);
  }, [requested]);

  return (
    <View style={[styles.root, { backgroundColor: theme.colors.canvas }]}>
      <LiquidBackdrop subtle />
      <ScreenHero eyebrow={t('leavePay.leave.eyebrow')} title={t('leavePay.leave.title')}>
        <SegmentedControl
          testID="leave-segments"
          style={styles.segments}
          value={segment}
          onChange={(next) => {
            setSegment(next);
            if (next === 'request') setSent(null);
          }}
          segments={[
            { key: 'request', label: t('leavePay.leave.segments.request') },
            { key: 'history', label: t('leavePay.leave.segments.history') },
          ]}
        />
      </ScreenHero>
      {segment === 'request' ? (
        <LeaveRequestForm
          onSent={(request, typeName) => {
            setSent({ request, typeName });
            setHistoryKey((k) => k + 1);
            setSegment('history');
          }}
        />
      ) : (
        <MyLeaveRequests
          reloadKey={historyKey}
          sent={sent}
          onDismissSent={() => setSent(null)}
          onRequestNew={() => setSegment('request')}
        />
      )}
    </View>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1 },
  segments: { marginTop: 14 },
});
