import { chromium } from '@playwright/test';
const SHOT='/private/tmp/claude-501/-Users-zackkhan-Downloads-KynexOne/074df5aa-77b3-476e-892a-2f51f2c41b55/scratchpad/journey';
const b=await chromium.launch();
const ctx=await b.newContext({baseURL:'https://kynexone.vercel.app',viewport:{width:1440,height:900},storageState:`${SHOT}/state.json`});
const p=await ctx.newPage(); let errs=[];
p.on('pageerror',e=>errs.push(e.message.slice(0,140)));
await p.goto('/people',{waitUntil:'networkidle'}); await p.waitForTimeout(2500);
console.log('--- People page buttons ---');
console.log((await p.locator('button').allTextContents()).filter(x=>x.trim()).slice(0,18).join(' | '));
const addBtn=p.locator('button:has-text("Add Employee"), button:has-text("Add employee"), button:has-text("New Employee")').first();
console.log('Add button found:', await addBtn.count());
if(await addBtn.count()){
  await addBtn.click(); await p.waitForTimeout(2500);
  await p.screenshot({path:`${SHOT}/20-add-employee-modal.png`,fullPage:true});
  const labels=(await p.locator('label').allTextContents()).map(x=>x.trim()).filter(Boolean);
  console.log('\n--- form fields ('+labels.length+') ---');
  console.log(labels.slice(0,45).join(' | '));
  const selects=p.locator('select');
  const n=await selects.count();
  console.log('\n--- dropdowns ('+n+') and whether they have options ---');
  for(let i=0;i<Math.min(n,14);i++){
    const opts=await selects.nth(i).locator('option').allTextContents();
    const lbl=await selects.nth(i).evaluate(el=>{const l=el.closest('label'); return (l?l.innerText:el.getAttribute('id')||'?').split('\n')[0].slice(0,26);}).catch(()=>'?');
    console.log(`  ${lbl.padEnd(28)} ${opts.length-1} option(s)${opts.length<=1?'   <-- EMPTY':''}`);
  }
}
if(errs.length)console.log('\nERRORS:',errs.slice(0,3).join(' || '));
await b.close();
