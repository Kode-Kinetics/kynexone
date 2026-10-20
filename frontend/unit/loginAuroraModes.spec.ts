import { expect, test } from '@playwright/test';
import { loginAuroraModes } from '../src/components/LoginAuroraScene';

test('the GLSL 1.00 login scene never requests a WebGL2 context', () => {
  expect(loginAuroraModes(false)).toEqual(['webgl', '2d']);
});

test('a failed WebGL login scene remounts directly on the 2D painter', () => {
  expect(loginAuroraModes(true)).toEqual(['2d']);
});
