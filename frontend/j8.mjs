import { chromium } from '@playwright/test';
const SHOT='/private/tmp/claude-501/-Users-zackkhan-Downloads-KynexOne/074df5aa-77b3-476e-892a-2f51f2c41b55/scratchpad/journey';
const b=await chromium.launch();
const ctx=await b.newContext({baseURL:'https://kynexone.vercel.app',viewport:{width:1440,height:900},storageState:`${SHOT}/state.json`});
const p=await ctx.newPage();
for(const [tab,btn] of [['grades',/Add Grade/i],['branches',/Add Branch/i]]){
  await p.goto('/setup?tab='+tab,{waitUntil:'networkidle'}); await p.waitForTimeout(2000);
  await p.locator('button').filter({hasText:btn}).first().click(); await p.waitForTimeout(1800);
  const labs=(await p.locator('[role=dialog] label, .modal label').allTextContents()).map(x=>x.trim().split('\n')[0]).filter(Boolean);
  console.log(`\n=== ${tab} required(*) and all fields ===`);
  console.log(labs.join(' | '));
  const ins=p.locator('[role=dialog] input, .modal input');
  console.log('inputs:',await ins.count(),' placeholders:',(await ins.evaluateAll(e=>e.map(x=>x.placeholder||x.type))).join(' | '));
  // fill everything then save, capture the validation message
  const n=await ins.count();
  for(let i=0;i<n;i++){const t=await ins.nth(i).getAttribute('type');
    await ins.nth(i).fill(t==='number'?'1':(tab==='grades'?['G3','Grade 3','Professional'][i]||'1':['HQ','Head Office','Riyadh'][i]||'x')).catch(()=>{});}
  const sels=p.locator('[role=dialog] select, .modal select');
  for(let i=0;i<await sels.count();i++) await sels.nth(i).selectOption({index:1}).catch(()=>{});
  await p.locator('[role=dialog] button, .modal button').filter({hasText:/^Save$/}).first().click().catch(()=>{});
  await p.waitForTimeout(3000);
  const t=((await p.locator('body').innerText().catch(()=>''))||'');
  const err=t.match(/(required|invalid|must|cannot|failed)[^\n]{0,110}/i);
  console.log('after save ->', t.includes(tab==='grades'?'Grade 3':'Head Office')?'CREATED ✅':'still failing', err?(' :: '+err[0]):'');
  await p.screenshot({path:`${SHOT}/60-${tab}.png`,fullPage:true});
}
await b.close();
