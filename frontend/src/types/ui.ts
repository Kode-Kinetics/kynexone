import type { ComponentType, SVGProps } from 'react';
import type { LucideIcon } from 'lucide-react';

export type ThemeMode = 'light' | 'dark';
export type TrendDir = 'up' | 'down' | 'neutral';

export type IconComponent = ComponentType<SVGProps<SVGSVGElement>>;

export interface NavItem {
  label: string;
  icon: LucideIcon;
  badge?: number;
  path?: string;
  /** At least one permission must be present for this item to show. Empty/absent = visible to all logged-in users. */
  requiredPermissions?: string[];
  /** Tenant feature flag key that must be enabled for this item to show. Empty/absent = always visible. */
  requiredFeatureKey?: string;
  /** Show only for Group account tenants (multi-company). */
  groupAccountOnly?: boolean;
}

export interface NavGroup {
  label: string;
  items: NavItem[];
}

export interface KpiMetric {
  label: string;
  value: string;
  delta: string;
  tone: 'blue' | 'cyan' | 'emerald' | 'amber' | 'rose';
  trend?: TrendDir;
}

export interface ApprovalQueueItem {
  id: string;
  title: string;
  owner: string;
  module: string;
  due: string;
  priority: 'High' | 'Medium' | 'Low';
}

export interface QuickAction {
  label: string;
  description: string;
  icon: LucideIcon;
}

export interface AiInsight {
  title: string;
  body: string;
  severity?: 'info' | 'warning' | 'critical';
}
