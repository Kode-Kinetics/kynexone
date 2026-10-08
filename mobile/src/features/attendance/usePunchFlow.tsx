import React, { useCallback, useEffect, useRef, useState } from 'react';
import { Alert, Linking, type AlertButton } from 'react-native';
import * as Location from 'expo-location';
import { useFocusEffect } from '@react-navigation/native';
import { useTranslation } from 'react-i18next';
import { attendanceApi, selfieAttendanceApi } from '@/api/services';
import { getDeviceInfo } from '@/utils/device';
import type { GeoLocation, PunchType } from '@/types';
import { SelfieCaptureModal } from './SelfieCaptureModal';
import { deleteTempPhoto } from './selfieFiles';
import {
  mapPunchRefusal,
  planPunch,
  refusalPrompt,
  type AttendanceVerification,
  type PunchPlan,
  type PunchRefusal,
  type RefusalButton,
} from './selfieAttendance';

/** Plain-language lines for the attendance card: what applies to this employee's punch, with a way to the consent screen. */
export interface AttendanceVerificationNotes {
  selfie: { text: string; actionLabel: string; onPress: () => void } | null;
  geofence: string | null;
}

export interface PunchOutcome {
  punchType: PunchType;
  selfieAttached: boolean;
}

interface Options {
  /** Called after the server recorded the punch (refresh the screen's attendance here). */
  onPunched: (outcome: PunchOutcome) => Promise<void> | void;
  /** Opens the consent screen (review, agree or withdraw). Only from the attendance card's link, never from a refusal. */
  onOpenConsent: () => void;
}

interface SelfieRequest {
  punchType: PunchType;
  mode: 'optional' | 'required';
}

/**
 * The self-punch flow shared by the employee and manager home screens. Punches are self-only: the
 * request never names another employee.
 *
 * 1. Read discovery (GET /ess/attendance-verification, cached briefly).
 * 2. Location permission first, so a refusal does not waste a selfie.
 * 3. If the employee consented and the tenant uses selfies: capture, upload, get the evidence id,
 *    delete the photo from the phone. Without consent, or with the feature off: no selfie step.
 * 4. Punch with latitude, longitude, accuracy, the mocked flag (where the platform has one) and the
 *    evidence id. A refusal is shown in plain words with a next step.
 */
export function usePunchFlow({ onPunched, onOpenConsent }: Options) {
  const { t, i18n } = useTranslation();
  const tx = t as unknown as (key: string, options?: Record<string, unknown>) => string;
  const [verification, setVerification] = useState<AttendanceVerification | null>(null);
  const [punching, setPunching] = useState(false);
  const [selfie, setSelfie] = useState<SelfieRequest | null>(null);
  const [uploading, setUploading] = useState(false);
  const busy = useRef(false);
  // The latest discovery answer for event handlers (state is for rendering; handlers must not read a stale closure).
  const latest = useRef<AttendanceVerification | null>(null);
  const remember = useCallback((value: AttendanceVerification) => {
    latest.current = value;
    setVerification(value);
  }, []);

  const refresh = useCallback(async (force = false): Promise<AttendanceVerification | null> => {
    try {
      const value = await selfieAttendanceApi.getVerification({ force });
      remember(value);
      return value;
    } catch (error) {
      // Discovery is advisory: without it the app punches with location and no selfie, and the
      // server's answer (a refusal with a reason) is still authoritative.
      console.warn('[Attendance] Verification discovery unavailable:', error);
      return null;
    }
  }, [remember]);

  useFocusEffect(useCallback(() => { void refresh(false); }, [refresh]));

  const finish = useCallback(() => {
    busy.current = false;
    setPunching(false);
  }, []);

  const showLocationNeeded = useCallback(() => {
    Alert.alert(tx('selfie.punch.locationNeededTitle'), tx('selfie.punch.locationNeededBody'), [
      { text: tx('selfie.punch.openSettings'), onPress: () => void Linking.openSettings() },
      { text: tx('selfie.punch.ok'), style: 'cancel' },
    ]);
  }, [tx]);

  const readLocation = useCallback(async (plan: PunchPlan): Promise<GeoLocation | null> => {
    const current = await Location.getCurrentPositionAsync({
      // The server compares accuracy against the tenant's limit: ask for the best fix when it does.
      accuracy: plan.locationRequired ? Location.Accuracy.Highest : Location.Accuracy.High,
    });
    return {
      latitude: current.coords.latitude,
      longitude: current.coords.longitude,
      accuracy: current.coords.accuracy ?? undefined,
      mocked: typeof current.mocked === 'boolean' ? current.mocked : undefined,
      timestamp: current.timestamp,
    };
  }, []);

  // The refusal handler re-submits through this ref (submit is declared after it).
  const submitRef = useRef<(punchType: PunchType, evidenceId: string | undefined) => Promise<void>>(async () => undefined);

  /**
   * Shows a refusal with its next step. `stage` says where it happened: a retry after a failed upload
   * means taking the selfie again, a retry after a refused punch means punching again.
   */
  const showRefusal = useCallback(async (
    refusal: PunchRefusal,
    punchType: PunchType,
    evidenceId: string | undefined,
    stage: 'upload' | 'punch' = 'punch',
  ) => {
    const fresh = refusal.refreshVerification ? (await refresh(true)) ?? latest.current : latest.current;
    const plan = planPunch(fresh);
    const prompt = refusalPrompt(refusal, plan.selfie, stage, tx);
    const openSelfie = () => {
      if (plan.selfie === 'skip') return void submitRef.current(punchType, undefined);
      busy.current = true;
      setPunching(true);
      setSelfie({ punchType, mode: plan.selfie });
    };
    const toAlertButton = (button: RefusalButton): AlertButton => {
      switch (button) {
        case 'cancel':
          return { text: tx('common.cancel'), style: 'cancel' };
        case 'ok':
          return { text: tx('selfie.punch.ok'), style: 'cancel' };
        case 'try_again':
          // A location refusal does not use the evidence: retry with it while it is still valid.
          return { text: tx('selfie.punch.tryAgain'), onPress: () => void submitRef.current(punchType, evidenceId) };
        case 'open_settings':
          return { text: tx('selfie.punch.openSettings'), onPress: () => void Linking.openSettings() };
        case 'without_selfie':
          return { text: tx('selfie.punch.withoutSelfie'), onPress: () => void submitRef.current(punchType, undefined) };
        case 'selfie_try_again':
          return { text: tx('selfie.punch.tryAgain'), onPress: openSelfie };
        case 'take_selfie':
          return { text: tx('selfie.punch.takeSelfie'), onPress: openSelfie };
        case 'take_new_selfie':
          return { text: tx('selfie.punch.takeNewSelfie'), onPress: openSelfie };
      }
    };
    Alert.alert(tx(prompt.titleKey), prompt.body, prompt.buttons.map(toAlertButton));
  }, [refresh, tx]);

  const submit = useCallback(async (punchType: PunchType, evidenceId: string | undefined) => {
    busy.current = true;
    setPunching(true);
    try {
      const plan = planPunch(latest.current);
      const permission = await Location.requestForegroundPermissionsAsync();
      if (permission.status !== 'granted') {
        showLocationNeeded();
        return;
      }
      const location = await readLocation(plan);
      const result = await attendanceApi.punch({
        punchType,
        timestamp: new Date().toISOString(),
        location: location ?? undefined,
        deviceInfo: await getDeviceInfo(),
        evidenceId,
      });
      await onPunched({ punchType, selfieAttached: /selfie/i.test(result.verificationMethod) });
    } catch (error) {
      await showRefusal(mapPunchRefusal(error, i18n.language), punchType, evidenceId);
    } finally {
      finish();
    }
  }, [finish, i18n.language, onPunched, readLocation, showLocationNeeded, showRefusal]);
  useEffect(() => { submitRef.current = submit; }, [submit]);

  const start = useCallback(async (punchType: PunchType) => {
    if (busy.current) return;
    busy.current = true;
    setPunching(true);
    let handedToModal = false;
    try {
      const plan = planPunch((await refresh(false)) ?? latest.current);
      const permission = await Location.requestForegroundPermissionsAsync();
      if (permission.status !== 'granted') {
        showLocationNeeded();
        return;
      }
      if (plan.selfie === 'skip') {
        handedToModal = true; // submit owns the busy state from here
        await submit(punchType, undefined);
        return;
      }
      handedToModal = true;
      setSelfie({ punchType, mode: plan.selfie });
    } catch (error) {
      await showRefusal(mapPunchRefusal(error, i18n.language), punchType, undefined);
    } finally {
      if (!handedToModal) finish();
    }
  }, [finish, i18n.language, refresh, showLocationNeeded, showRefusal, submit]);

  const closeSelfie = useCallback(() => setSelfie(null), []);

  const onUse = useCallback(async (uri: string) => {
    if (!selfie) return;
    const { punchType } = selfie;
    setUploading(true);
    let evidenceId: string | null = null;
    try {
      evidenceId = (await selfieAttendanceApi.uploadSelfie(uri)).evidenceId;
    } catch (error) {
      setUploading(false);
      closeSelfie();
      finish();
      await showRefusal(mapPunchRefusal(error, i18n.language), punchType, undefined, 'upload');
      return;
    } finally {
      // The photo never stays on the phone after the upload attempt, whatever the outcome.
      void deleteTempPhoto(uri);
    }
    setUploading(false);
    closeSelfie();
    await submit(punchType, evidenceId);
  }, [closeSelfie, finish, i18n.language, selfie, showRefusal, submit]);

  const onSkip = useCallback(() => {
    if (!selfie) return;
    const { punchType } = selfie;
    closeSelfie();
    void submit(punchType, undefined);
  }, [closeSelfie, selfie, submit]);

  const onCancel = useCallback(() => {
    closeSelfie();
    finish();
  }, [closeSelfie, finish]);

  const modal = (
    <SelfieCaptureModal
      visible={selfie != null}
      punchType={selfie?.punchType ?? 'CLOCK_IN'}
      mode={selfie?.mode ?? 'optional'}
      uploading={uploading}
      onUse={(uri) => void onUse(uri)}
      onSkip={selfie?.mode === 'optional' ? onSkip : undefined}
      onCancel={onCancel}
    />
  );

  const plan = planPunch(verification);
  const notes: AttendanceVerificationNotes = {
    selfie: plan.offerConsent
      ? { text: tx('selfie.card.consentNeeded'), actionLabel: tx('selfie.card.consentNeededAction'), onPress: onOpenConsent }
      : plan.selfie !== 'skip'
        ? {
          text: plan.selfie === 'required' ? tx('selfie.card.selfieOn') : tx('selfie.card.selfieOptional'),
          actionLabel: tx('selfie.card.manage'),
          onPress: onOpenConsent,
        }
        : null,
    geofence: plan.locationRequired ? tx('selfie.card.geofenceOn') : null,
  };

  return {
    punch: start,
    punching,
    verification,
    plan,
    notes,
    refreshVerification: refresh,
    modal,
  };
}
