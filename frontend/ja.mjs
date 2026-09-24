import { chromium } from '@playwright/test';
const SHOT='/private/tmp/claude-501/-Users-zackkhan-Downloads-KynexOne/074df5aa-77b3-476e-892a-2f51f2c41b55/scratchpad/journey';
const b=await chromium.launch();
const ctx=await b.newContext({baseURL:'https://kynexone.vercel.app',viewport:{width:1440,height:900},storageState:`${SHOT}/state.json`});
const p=await ctx.newPage();
// edit the existing G3 grade to sane bands
await p.goto('/setup?tab=grades',{waitUntil:'networkidle'}); await p.waitForTimeout(2200);
const edit=p.locator('button[title*="Edit" i], button:has-text("Edit")').first();
if(await edit.count()){ await edit.click(); await p.waitForTimeout(1600);
  const ins=p.locator('[role=dialog] input, .modal input');
  const vals=['G3','3','Grade 3','Professional','5000','7500','12000'];
  for(let i=0;i<Math.min(vals.length,await ins.count());i++) await ins.nth(i).fill(vals[i]).catch(()=>{});
  await p.locator('[role=dialog] button, .modal button').filter({hasText:/^Save$/}).first().click().catch(()=>{});
  await p.waitForTimeout(3000);
  console.log('grade edited');
}
await p.screenshot({path:`${SHOT}/80-grade-fixed.png`,fullPage:true});
// retry employee
await p.goto('/people',{waitUntil:'networkidle'}); await p.waitForTimeout(2500);
await p.locator('button:has-text("Add Employee")').first().click(); await p.waitForTimeout(2800);
const byLabel=async(rx,val)=>{const el=p.locator('label').filter({hasText:rx}).locator('input,select').first();
  if(!await el.count())return false; const tag=await el.evaluate(e=>e.tagName);
  if(tag==='SELECT'){const o=await el.locator('option').count(); if(o>1){await el.selectOption({index:1});return true;} return false;}
  await el.fill(val); return true;};
await byLabel(/English full name/i,'Aisha Al-Rashid');
await byLabel(/Personal email/i,'aisha.test@example.com');
await byLabel(/Mobile number/i,'+966500000123');
for(const f of [/^Company/i,/^Branch/i,/^Department/i,/^Designation/i,/^Grade/i,/Employment type/i,/Contract type/i,/Salary currency/i]) await byLabel(f,'');
await byLabel(/Joining date/i,'2026-09-01');
await byLabel(/Basic salary/i,'8000');
await p.locator('button').filter({hasText:/Create Employee/i}).last().click().catch(()=>{});
await p.waitForTimeout(5000);
const t=((await p.locator('body').innerText().catch(()=>''))||'');
const err=t.match(/(required|invalid|must|cannot|failed|error)[^\n]{0,130}/i);
console.log('Employee:', t.includes('Aisha')?'CREATED ✅':'NOT CREATED ❌', err?(':: '+err[0].slice(0,150)):'');
await p.screenshot({path:`${SHOT}/81-employee-result.png`,fullPage:true});
await b.close();
