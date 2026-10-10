import React, { useState } from 'react';
import { Platform, StyleSheet, Text, View } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import DateTimePicker, { type DateTimePickerEvent } from '@react-native-community/datetimepicker';
import { useTranslation } from 'react-i18next';
import { BottomSheet, LiquidButton, MotionPressable } from '@/components/ui';
import { useTheme } from '@/theme/ThemeProvider';
import { useFormat } from '@/utils/format';

/**
 * A labelled date or time field backed by the native picker — never free text, so Arabic digits,
 * 12/24-hour habits and typos cannot produce a wrong value.
 *   date: value 'YYYY-MM-DD' (a calendar date, no time zone)
 *   time: value 'HH:mm' (wall clock on the company calendar)
 * iOS opens the picker in a bottom sheet with a Done button; Android uses its system dialog.
 */
export interface PickerFieldProps {
  mode: 'date' | 'time';
  label: string;
  value: string | null;
  onChange: (value: string) => void;
  placeholder: string;
  /** 'YYYY-MM-DD' bounds (date mode). */
  minimumDate?: string;
  maximumDate?: string;
  error?: string;
  testID?: string;
}

const pad = (n: number) => String(n).padStart(2, '0');

function toPickerDate(mode: 'date' | 'time', value: string | null): Date {
  if (mode === 'date' && value) {
    const [y, m, d] = value.split('-').map(Number);
    return new Date(y, m - 1, d);
  }
  const now = new Date();
  if (mode === 'time' && value) {
    const [h, min] = value.split(':').map(Number);
    return new Date(now.getFullYear(), now.getMonth(), now.getDate(), h, min);
  }
  return mode === 'time' ? new Date(now.getFullYear(), now.getMonth(), now.getDate(), now.getHours(), 0) : now;
}

function fromPickerDate(mode: 'date' | 'time', d: Date): string {
  return mode === 'date'
    ? `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`
    : `${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

function isoToLocalDate(iso?: string): Date | undefined {
  if (!iso) return undefined;
  const [y, m, d] = iso.split('-').map(Number);
  return new Date(y, m - 1, d);
}

export function PickerField({ mode, label, value, onChange, placeholder, minimumDate, maximumDate, error, testID }: PickerFieldProps) {
  const { theme } = useTheme();
  const { t, i18n } = useTranslation();
  const f = useFormat();
  const [open, setOpen] = useState(false);
  const [draft, setDraft] = useState<Date>(() => toPickerDate(mode, value));
  // 'ar' alone keeps the Gregorian calendar; 'ar-SA' would switch the iOS wheel to Hijri.
  const pickerLocale = i18n.language?.startsWith('ar') ? 'ar' : 'en-GB';
  const shown = value ? (mode === 'date' ? f.date(value, 'full') : f.time(value)) : null;

  const openPicker = () => {
    setDraft(toPickerDate(mode, value));
    setOpen(true);
  };

  const onAndroidChange = (event: DateTimePickerEvent, date?: Date) => {
    setOpen(false);
    if (event.type === 'set' && date) onChange(fromPickerDate(mode, date));
  };

  return (
    <View style={styles.group}>
      <Text style={[theme.typography.footnote, styles.label, { color: theme.colors.textSecondary }]}>{label}</Text>
      <MotionPressable
        testID={testID}
        accessibilityRole="button"
        accessibilityLabel={`${label}, ${shown ?? placeholder}`}
        accessibilityHint={error}
        haptic="selection"
        onPress={openPicker}
        contentStyle={[
          styles.field,
          {
            backgroundColor: theme.colors.surfaceInset,
            borderColor: error ? theme.colors.status.danger.fg : theme.colors.hairline,
          },
        ]}
      >
        <Ionicons
          accessible={false}
          name={mode === 'date' ? 'calendar-outline' : 'time-outline'}
          size={20}
          color={theme.colors.accent}
        />
        <Text
          style={[
            theme.typography.callout,
            styles.value,
            { color: shown ? theme.colors.text : theme.colors.textMuted },
          ]}
        >
          {shown ?? placeholder}
        </Text>
        <Ionicons accessible={false} name="chevron-down" size={16} color={theme.colors.textMuted} />
      </MotionPressable>
      {error ? (
        <Text style={[theme.typography.footnote, styles.error, { color: theme.colors.status.danger.fg }]}>{error}</Text>
      ) : null}

      {Platform.OS === 'ios' ? (
        <BottomSheet
          visible={open}
          onClose={() => setOpen(false)}
          title={label}
          actions={
            <LiquidButton
              label={t('leavePay.common.done')}
              onPress={() => {
                onChange(fromPickerDate(mode, draft));
                setOpen(false);
              }}
            />
          }
        >
          <DateTimePicker
            value={draft}
            mode={mode}
            display={mode === 'date' ? 'inline' : 'spinner'}
            locale={pickerLocale}
            themeVariant={theme.isDark ? 'dark' : 'light'}
            accentColor={theme.colors.accent}
            minimumDate={isoToLocalDate(minimumDate)}
            maximumDate={isoToLocalDate(maximumDate)}
            onChange={(_, date) => {
              if (date) setDraft(date);
            }}
            style={styles.iosPicker}
          />
        </BottomSheet>
      ) : open ? (
        <DateTimePicker
          value={draft}
          mode={mode}
          minimumDate={isoToLocalDate(minimumDate)}
          maximumDate={isoToLocalDate(maximumDate)}
          onChange={onAndroidChange}
        />
      ) : null}
    </View>
  );
}

const styles = StyleSheet.create({
  group: { gap: 6 },
  label: { fontWeight: '600' },
  field: {
    minHeight: 52,
    flexDirection: 'row',
    alignItems: 'center',
    gap: 10,
    paddingHorizontal: 14,
    paddingVertical: 10,
    borderRadius: 14,
    borderWidth: StyleSheet.hairlineWidth,
  },
  value: { flex: 1, textAlign: 'auto' },
  error: { marginTop: 2 },
  iosPicker: { alignSelf: 'stretch' },
});
