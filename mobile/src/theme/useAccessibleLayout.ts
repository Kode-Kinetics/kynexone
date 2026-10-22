import { I18nManager, useWindowDimensions } from 'react-native';
import { resolveAccessibleLayout } from './accessibleLayout';

export function useAccessibleLayout() {
  const { width, fontScale } = useWindowDimensions();
  return resolveAccessibleLayout(width, fontScale, I18nManager.isRTL);
}

export { ACCESSIBLE_LAYOUT, columnsFor } from './accessibleLayout';
