import React, { useState } from 'react';
import {
  StyleProp,
  StyleSheet,
  Text,
  TextInput,
  TextInputProps,
  View,
  ViewStyle,
} from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import { GlassSurface } from './GlassSurface';
import { useTheme } from '@/theme/ThemeProvider';

interface Props extends TextInputProps {
  label: string;
  icon: React.ComponentProps<typeof Ionicons>['name'];
  error?: string;
  trailing?: React.ReactNode;
  containerStyle?: StyleProp<ViewStyle>;
}

export function GlassTextField({
  label,
  icon,
  error,
  trailing,
  containerStyle,
  onFocus,
  onBlur,
  ...inputProps
}: Props) {
  const { theme } = useTheme();
  const [focused, setFocused] = useState(false);

  return (
    <View style={[styles.group, containerStyle]}>
      <Text style={[theme.typography.caption, styles.label, { color: theme.colors.textSecondary }]}>
        {label}
      </Text>
      <View
        style={[
          styles.focusRing,
          {
            borderColor: error
              ? theme.colors.danger
              : focused
                ? theme.colors.primary
                : 'transparent',
          },
        ]}
      >
        <GlassSurface
          elevated={false}
          radius={16}
          contentStyle={styles.inputRow}
          style={styles.surface}
        >
          <Ionicons
            name={icon}
            size={19}
            color={focused ? theme.colors.primary : theme.colors.textMuted}
          />
          <TextInput
            {...inputProps}
            selectionColor={theme.colors.primary}
            placeholderTextColor={theme.colors.textMuted}
            onFocus={(event) => {
              setFocused(true);
              onFocus?.(event);
            }}
            onBlur={(event) => {
              setFocused(false);
              onBlur?.(event);
            }}
            style={[
              styles.input,
              theme.typography.body,
              { color: theme.colors.text },
              inputProps.style,
            ]}
          />
          {trailing}
        </GlassSurface>
      </View>
      {error ? (
        <Text style={[theme.typography.caption, styles.error, { color: theme.colors.danger }]}>
          {error}
        </Text>
      ) : null}
    </View>
  );
}

const styles = StyleSheet.create({
  group: { marginBottom: 16 },
  label: { marginBottom: 7, fontWeight: '700' },
  focusRing: { borderWidth: 1.5, borderRadius: 18, padding: 1 },
  surface: { minHeight: 54 },
  inputRow: { flexDirection: 'row', alignItems: 'center', paddingHorizontal: 14, gap: 10 },
  input: { flex: 1, minHeight: 50, paddingVertical: 12 },
  error: { marginTop: 5, marginLeft: 4 },
});
