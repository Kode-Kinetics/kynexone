/**
 * Checksum-valid synthetic Saudi identity numbers for the e2e fixture seeds. The onboarding rules
 * (Luhn variant: odd positions doubled, minus 9 when above 9, total mod 10 = 0) refuse anything
 * else. Same seed gives the same number; prefix 1 = National ID, prefix 2 = Iqama.
 */
export function saudiIdCheckDigit(nine: string): number {
  let sum = 0;
  for (let i = 0; i < 9; i++) {
    let d = Number(nine[i]);
    if (i % 2 === 0) { d *= 2; if (d > 9) d -= 9; }
    sum += d;
  }
  return (10 - (sum % 10)) % 10;
}

function build(prefix: '1' | '2', seed: number): string {
  const body = String(Math.abs(Math.trunc(seed)) % 100_000_000).padStart(8, '0');
  const nine = `${prefix}${body}`;
  return `${nine}${saudiIdCheckDigit(nine)}`;
}

export const saudiNationalId = (seed: number): string => build('1', seed);
export const saudiIqama = (seed: number): string => build('2', seed);
