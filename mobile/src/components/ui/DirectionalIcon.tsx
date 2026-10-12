import React from 'react';
import { I18nManager, StyleProp, TextStyle } from 'react-native';
import { Ionicons } from '@expo/vector-icons';

/**
 * Icons that point along the reading direction. Named by meaning, not by side:
 *   forward       → arrow toward the next step      (→ in English, ← in Arabic)
 *   back          → arrow toward the previous step
 *   chevron       → disclosure chevron on a row that opens something
 *   chevronBack   → back chevron (navigation)
 *   forwardCircle → circled forward arrow
 * Mirrors with the native layout direction (I18nManager), which is what the layout uses.
 */
export type DirectionalIconName = 'forward' | 'back' | 'chevron' | 'chevronBack' | 'forwardCircle';

const LTR: Record<DirectionalIconName, React.ComponentProps<typeof Ionicons>['name']> = {
  forward: 'arrow-forward',
  back: 'arrow-back',
  chevron: 'chevron-forward',
  chevronBack: 'chevron-back',
  forwardCircle: 'arrow-forward-circle-outline',
};

const RTL: typeof LTR = {
  forward: 'arrow-back',
  back: 'arrow-forward',
  chevron: 'chevron-back',
  chevronBack: 'chevron-forward',
  forwardCircle: 'arrow-back-circle-outline',
};

export interface DirectionalIconProps {
  name: DirectionalIconName;
  size?: number;
  color?: string;
  style?: StyleProp<TextStyle>;
}

export function directionalIconName(name: DirectionalIconName): React.ComponentProps<typeof Ionicons>['name'] {
  return (I18nManager.isRTL ? RTL : LTR)[name];
}

export function DirectionalIcon({ name, size = 18, color, style }: DirectionalIconProps) {
  return (
    <Ionicons
      accessible={false}
      importantForAccessibility="no"
      name={directionalIconName(name)}
      size={size}
      color={color}
      style={style}
    />
  );
}
