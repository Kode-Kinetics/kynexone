module.exports = {
  root: true,
  extends: ['expo'],
  ignorePatterns: [
    'node_modules/',
    '.expo/',
    'dist/',
    'ios/',
    'android/',
  ],
  rules: {
    // Async screen bootstraps intentionally start stateful network loads.
    // React Native has no external-store alternative for these screen effects.
    'react-hooks/set-state-in-effect': 'off',
  },
};
