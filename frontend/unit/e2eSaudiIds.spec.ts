import { expect, test } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { validateIdNumber } from '../src/lib/hire/saudiId';
import { saudiNationalId, saudiIqama, saudiIdCheckDigit } from '../e2e/bootstrap/saudiIds';

const vectors = JSON.parse(fs.readFileSync(path.resolve(__dirname, '../../backend-dotnet/Zayra.Api.Tests/Fixtures/hire-vectors.json'), 'utf8'));

test.describe('e2e bootstrap Saudi id generator', () => {
  test('check digit completes every shared valid vector', () => {
    for (const id of [...vectors.saudiNationalId.valid, ...vectors.iqama.valid]) {
      expect(saudiIdCheckDigit(id.slice(0, 9))).toBe(Number(id[9]));
    }
  });
  test('generated ids pass the shared validator, with the right class prefix', () => {
    for (let seed = 0; seed < 500; seed++) {
      const n = saudiNationalId(seed);
      const q = saudiIqama(seed);
      expect(n[0]).toBe('1');
      expect(q[0]).toBe('2');
      expect(validateIdNumber('nationalId', n)).toBe('ok');
      expect(validateIdNumber('iqama', q)).toBe('ok');
    }
  });
  test('distinct seeds give distinct ids; same seed is stable', () => {
    const set = new Set(Array.from({ length: 2000 }, (_, i) => saudiNationalId(i)));
    expect(set.size).toBe(2000);
    expect(saudiIqama(7)).toBe(saudiIqama(7));
  });
});
