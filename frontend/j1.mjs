import { chromium } from '@playwright/test';
const SHOT = '/private/tmp/claude-501/-Users-zackkhan-Downloads-KynexOne/074df5aa-77b3-476e-892a-2f51f2c41b55/scratchpad/journey';
const b = await chromium.launch();
const ctx = await b.newContext({ baseURL: 'https://kynexone.vercel.app', viewport: { width: 1440, height: 900 } });
const p = await ctx.newPage();
const errs = [];
p.on('pageerror', e => errs.push(`${e.message}`));
p.on('console', m => { if (m.type()==='error' && !/favicon|404/.test(m.text())) errs.push('CONSOLE: '+m.text().slice(0,160)); });

await p.goto('/login', { waitUntil: 'networkidle' });
const ins = p.locator('input');
await ins.nth(0).fill('info@kodekinetics.com');
await ins.nth(1).fill('welcome@123');
await ins.nth(2).fill('testclaude');
await p.locator('button[type="submit"], button:has-text("Sign in")').first().click();
await p.waitForTimeout(8000);
console.log('url:', p.url());
if (/login/.test(p.url())) {
  console.log('LOGIN FAILED:', ((await p.locator('body').innerText().catch(()=> ''))||'').slice(0,200).replace(/\n/g,' | '));
  await p.screenshot({ path: `${SHOT}/00-login-failed.png` });
  await b.close(); process.exit(1);
}
console.log('LOGGED IN ✅');
await p.screenshot({ path: `${SHOT}/01-dashboard.png`, fullPage: true });
const t = ((await p.locator('body').innerText().catch(()=> ''))||'');
console.log('dashboard crashed?', /Something went wrong/i.test(t) ? 'YES' : 'no');
console.log('nav items:', (await p.locator('nav a, nav button').allTextContents()).filter(Boolean).slice(0,30).join(' · '));
if (errs.length) console.log('ERRORS:', errs.slice(0,3).join(' || '));
await ctx.storageState({ path: `${SHOT}/state.json` });
await b.close();
