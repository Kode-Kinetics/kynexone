import React, { useCallback, useEffect, useRef, useState } from 'react';
import {
  ActivityIndicator,
  Image,
  Modal,
  StyleSheet,
  Text,
  View,
} from 'react-native';
import { CameraView, useCameraPermissions } from 'expo-camera';
import { Ionicons } from '@expo/vector-icons';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import { GlassSurface, MotionPressable } from '@/components/ui';
import { useTheme } from '@/theme/ThemeProvider';
import type { PunchType } from '@/types';

interface Props {
  visible: boolean;
  punchType: PunchType | null;
  onCancel: () => void;
  onConfirm: (uri: string) => Promise<void> | void;
}

export function AttendanceSelfieModal({
  visible,
  punchType,
  onCancel,
  onConfirm,
}: Props) {
  const { theme } = useTheme();
  const insets = useSafeAreaInsets();
  const cameraRef = useRef<CameraView>(null);
  const [permission, requestPermission] = useCameraPermissions();
  const [cameraReady, setCameraReady] = useState(false);
  const [photoUri, setPhotoUri] = useState<string | null>(null);
  const [capturing, setCapturing] = useState(false);
  const [submitting, setSubmitting] = useState(false);

  useEffect(() => {
    if (!visible) {
      setPhotoUri(null);
      setCapturing(false);
      setSubmitting(false);
      setCameraReady(false);
      return;
    }
    if (permission && !permission.granted && permission.canAskAgain) {
      void requestPermission();
    }
  }, [permission, requestPermission, visible]);

  const takeSelfie = useCallback(async () => {
    if (!cameraReady || capturing || !cameraRef.current) return;
    setCapturing(true);
    try {
      const photo = await cameraRef.current.takePictureAsync({
        quality: 0.62,
        skipProcessing: false,
      });
      if (photo?.uri) setPhotoUri(photo.uri);
    } finally {
      setCapturing(false);
    }
  }, [cameraReady, capturing]);

  const confirm = useCallback(async () => {
    if (!photoUri || submitting) return;
    setSubmitting(true);
    try {
      await onConfirm(photoUri);
    } finally {
      setSubmitting(false);
    }
  }, [onConfirm, photoUri, submitting]);

  const actionLabel = punchType === 'CLOCK_OUT' ? 'Clock Out' : 'Clock In';

  return (
    <Modal
      visible={visible}
      animationType="slide"
      presentationStyle="fullScreen"
      onRequestClose={onCancel}
    >
      <View style={[styles.root, { backgroundColor: '#020617' }]}>
        {permission?.granted && !photoUri ? (
          <CameraView
            ref={cameraRef}
            style={StyleSheet.absoluteFill}
            facing="front"
            onCameraReady={() => setCameraReady(true)}
          />
        ) : null}

        {photoUri ? (
          <Image source={{ uri: photoUri }} style={StyleSheet.absoluteFill} resizeMode="cover" />
        ) : null}

        <View style={[styles.topBar, { paddingTop: Math.max(insets.top, 14) }]}>
          <MotionPressable
            onPress={onCancel}
            haptic="selection"
            accessibilityRole="button"
            accessibilityLabel="Close selfie attendance"
            contentStyle={styles.iconButton}
          >
            <Ionicons name="close" size={24} color="#FFFFFF" />
          </MotionPressable>

          <GlassSurface
            elevated={false}
            radius={999}
            tintColor="rgba(8,15,32,0.52)"
            contentStyle={styles.titlePill}
          >
            <Ionicons name="shield-checkmark-outline" size={17} color="#A5F3FC" />
            <Text style={styles.titleText}>{actionLabel} verification</Text>
          </GlassSurface>

          <View style={styles.iconSpacer} />
        </View>

        <View style={styles.center}>
          {!permission ? (
            <ActivityIndicator color="#FFFFFF" />
          ) : !permission.granted ? (
            <GlassSurface radius={24} contentStyle={styles.permissionCard}>
              <Ionicons name="camera-outline" size={34} color={theme.colors.cyan} />
              <Text style={styles.permissionTitle}>Front camera required</Text>
              <Text style={styles.permissionText}>
                KynexOne uses a live selfie as attendance evidence when your company requires it.
              </Text>
              <MotionPressable
                onPress={() => void requestPermission()}
                haptic="medium"
                contentStyle={styles.permissionButton}
              >
                <Text style={styles.permissionButtonText}>Allow Camera</Text>
              </MotionPressable>
            </GlassSurface>
          ) : (
            <View style={styles.faceGuide}>
              <View style={styles.faceGuideInner} />
            </View>
          )}
        </View>

        <View style={[styles.bottom, { paddingBottom: Math.max(insets.bottom + 18, 30) }]}>
          <GlassSurface
            radius={28}
            tintColor="rgba(8,15,32,0.58)"
            contentStyle={styles.instructionCard}
          >
            <View style={styles.verificationRow}>
              <VerificationPill icon="camera-outline" text="Front camera" />
              <VerificationPill icon="location-outline" text="GPS" />
              <VerificationPill icon="phone-portrait-outline" text="Device" />
            </View>
            <Text style={styles.instructionText}>
              {photoUri
                ? 'Review your attendance selfie before submitting.'
                : 'Center your face in the guide. Keep your face unobstructed and the phone steady.'}
            </Text>

            {photoUri ? (
              <View style={styles.reviewActions}>
                <MotionPressable
                  onPress={() => setPhotoUri(null)}
                  haptic="selection"
                  contentStyle={styles.secondaryAction}
                  disabled={submitting}
                >
                  <Ionicons name="refresh-outline" size={19} color="#FFFFFF" />
                  <Text style={styles.secondaryActionText}>Retake</Text>
                </MotionPressable>
                <MotionPressable
                  onPress={() => void confirm()}
                  haptic="medium"
                  contentStyle={styles.primaryAction}
                  disabled={submitting}
                >
                  {submitting ? (
                    <ActivityIndicator color="#FFFFFF" />
                  ) : (
                    <>
                      <Ionicons name="checkmark-circle-outline" size={20} color="#FFFFFF" />
                      <Text style={styles.primaryActionText}>Use Selfie</Text>
                    </>
                  )}
                </MotionPressable>
              </View>
            ) : (
              <MotionPressable
                onPress={() => void takeSelfie()}
                haptic="medium"
                disabled={!cameraReady || capturing}
                accessibilityRole="button"
                accessibilityLabel="Capture attendance selfie"
                contentStyle={styles.captureOuter}
              >
                <View style={styles.captureInner}>
                  {capturing ? <ActivityIndicator color="#0B1020" /> : null}
                </View>
              </MotionPressable>
            )}
          </GlassSurface>
        </View>
      </View>
    </Modal>
  );
}

function VerificationPill({
  icon,
  text,
}: {
  icon: React.ComponentProps<typeof Ionicons>['name'];
  text: string;
}) {
  return (
    <View style={styles.verifyPill}>
      <Ionicons name={icon} size={14} color="#A5F3FC" />
      <Text style={styles.verifyText}>{text}</Text>
    </View>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1 },
  topBar: {
    position: 'absolute',
    top: 0,
    left: 16,
    right: 16,
    zIndex: 4,
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
  },
  iconButton: {
    width: 44,
    height: 44,
    borderRadius: 22,
    backgroundColor: 'rgba(2,6,23,0.48)',
    alignItems: 'center',
    justifyContent: 'center',
  },
  iconSpacer: { width: 44 },
  titlePill: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: 7,
    paddingHorizontal: 14,
    paddingVertical: 9,
  },
  titleText: { color: '#FFFFFF', fontSize: 14, fontWeight: '700' },
  center: { flex: 1, alignItems: 'center', justifyContent: 'center', paddingHorizontal: 28 },
  faceGuide: {
    width: 244,
    height: 318,
    borderRadius: 122,
    borderWidth: 2,
    borderColor: 'rgba(165,243,252,0.92)',
    padding: 8,
    shadowColor: '#67E8F9',
    shadowOpacity: 0.42,
    shadowRadius: 18,
  },
  faceGuideInner: {
    flex: 1,
    borderRadius: 116,
    borderWidth: 1,
    borderColor: 'rgba(255,255,255,0.38)',
  },
  permissionCard: { width: '100%', alignItems: 'center', gap: 12, padding: 24 },
  permissionTitle: { color: '#FFFFFF', fontSize: 20, fontWeight: '800' },
  permissionText: { color: '#CBD5E1', textAlign: 'center', lineHeight: 21 },
  permissionButton: {
    minHeight: 46,
    borderRadius: 16,
    backgroundColor: '#2F6BFF',
    paddingHorizontal: 22,
    alignItems: 'center',
    justifyContent: 'center',
    marginTop: 4,
  },
  permissionButtonText: { color: '#FFFFFF', fontWeight: '800' },
  bottom: { position: 'absolute', left: 16, right: 16, bottom: 0 },
  instructionCard: { gap: 14, padding: 18, alignItems: 'center' },
  verificationRow: {
    flexDirection: 'row',
    gap: 7,
    flexWrap: 'wrap',
    justifyContent: 'center',
  },
  verifyPill: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: 5,
    borderRadius: 999,
    backgroundColor: 'rgba(15,23,42,0.58)',
    paddingHorizontal: 10,
    paddingVertical: 6,
  },
  verifyText: { color: '#E2E8F0', fontSize: 11, fontWeight: '700' },
  instructionText: {
    color: '#E2E8F0',
    textAlign: 'center',
    lineHeight: 19,
    fontSize: 13,
  },
  captureOuter: {
    width: 78,
    height: 78,
    borderRadius: 39,
    borderWidth: 4,
    borderColor: '#FFFFFF',
    alignItems: 'center',
    justifyContent: 'center',
    marginTop: 2,
  },
  captureInner: {
    width: 58,
    height: 58,
    borderRadius: 29,
    backgroundColor: '#FFFFFF',
    alignItems: 'center',
    justifyContent: 'center',
  },
  reviewActions: { width: '100%', flexDirection: 'row', gap: 10 },
  secondaryAction: {
    flex: 1,
    minHeight: 50,
    borderRadius: 17,
    backgroundColor: 'rgba(255,255,255,0.12)',
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'center',
    gap: 7,
  },
  secondaryActionText: { color: '#FFFFFF', fontWeight: '800' },
  primaryAction: {
    flex: 1.35,
    minHeight: 50,
    borderRadius: 17,
    backgroundColor: '#2F6BFF',
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'center',
    gap: 7,
  },
  primaryActionText: { color: '#FFFFFF', fontWeight: '800' },
});
