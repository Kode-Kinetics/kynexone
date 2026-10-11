// Motion tokens. Restrained and fast: this is a productivity app used many times a day.
// No rotateX / perspective tilts anywhere. Under Reduce Motion, press feedback becomes an
// opacity change (never removed) and entrances are instant.

export const motion = {
  springs: {
    /** Press, toggles, selection lens — settles in ~200ms without visible overshoot. */
    snappy: { damping: 26, stiffness: 380, mass: 0.8 },
    /** Sheets and larger surfaces. */
    smooth: { damping: 30, stiffness: 240, mass: 1 },
  },
  durations: {
    fast: 150,
    base: 220,
  },
  press: {
    scale: 0.97,
    /** Feedback used instead of scale when Reduce Motion is on. */
    reducedOpacity: 0.7,
  },
  /** Entrance offset for content fades (points). */
  enterOffset: 6,
} as const;

export type MotionTokens = typeof motion;
