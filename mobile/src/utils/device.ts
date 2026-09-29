// ============================================================
// ZAYRA MOBILE — Device Utilities
// ============================================================

import * as Device from 'expo-device';
import * as Application from 'expo-application';
import { Platform } from 'react-native';
import { appStorage } from '@/storage';
import { STORAGE_KEYS } from '@/config';
import type { DeviceInfo } from '@/types';
import Constants from 'expo-constants';

export async function generateDeviceId(): Promise<string> {
  // Try stored ID first
  const stored = await appStorage.get<string>(STORAGE_KEYS.DEVICE_ID);
  if (stored) return stored;

  // Generate from device identifiers or a UUID fallback
  let deviceId: string;

  if (Platform.OS === 'ios') {
    deviceId = (await Application.getIosIdForVendorAsync()) ?? generateUUID();
  } else {
    deviceId = (await Application.getAndroidId()) ?? generateUUID();
  }

  await appStorage.set(STORAGE_KEYS.DEVICE_ID, deviceId);
  return deviceId;
}

export async function getDeviceInfo(): Promise<DeviceInfo> {
  const deviceId = await generateDeviceId();
  return {
    deviceId,
    deviceModel: Device.modelName ?? 'Unknown',
    platform: Platform.OS as 'ios' | 'android',
    osVersion: Device.osVersion ?? 'Unknown',
    appVersion: Constants.expoConfig?.version ?? '1.0.0',
  };
}

function generateUUID(): string {
  return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, (c) => {
    const r = (Math.random() * 16) | 0;
    const v = c === 'x' ? r : (r & 0x3) | 0x8;
    return v.toString(16);
  });
}
