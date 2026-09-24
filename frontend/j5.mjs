import { chromium } from '@playwright/test';
const SHOT='/private/tmp/claude-501/-Users-zackkhan-Downloads-KynexOne/074df5aa-77b3-476e-892a-2f51f2c41b55/scratchpad/journey';
const b=await chromium.launch();
const ctx=await b.newContext({baseURL:'https://kynexone.vercel.app',viewport:{width:1440,height:900},storageState:`${SHOT}/state.json`});
const p=await ctx.newPage(); let errs=[];
p.on('pageerror',e=>errs.push(e.message.slice(0,140)));

async function createIn(tab,label,fields,shot){
  errs=[];
  await p.goto('/setup?tab='+tab,{waitUntil:'networkidle'}); await p.waitForTimeout(2200);
  const add=p.locator('button').filter({hasText:/^\s*(\+\s*)?(Add|New)\s/i}).first();
  if(!await add.count()){console.log(`${label}: no Add button`);return;}
  await add.click(); await p.waitForTimeout(1800);
  for(const [labelRx,val] of fields){
    const inp=p.locator('label').filter({hasText:labelRx}).locator('input,select').first();
    if(await inp.count()){
      const tag=await inp.evaluate(e=>e.tagName);
      if(tag==='SELECT'){ await inp.selectOption({index:1}).catch(()=>{}); }
      else await inp.fill(val).catch(()=>{});
    }
  }
  await p.screenshot({path:`${SHOT}/${shot}-form.png`});
  const save=p.locator('button').filter({hasText:/^(Save|Create|Add)\b/i}).last();
  await save.click().catch(()=>{}); await p.waitForTimeout(3000);
  const t=((await p.locator('body').innerText().catch(()=>''))||'');
  const ok=t.includes(fields[1]?.[1]||fields[0][1]);
  console.log(`${label}: ${ok?'CREATED ✅':'not visible in list'}${errs.length?' :: '+errs[0].slice(0,90):''}`);
  await p.screenshot({path:`${SHOT}/${shot}-after.png`,fullPage:true});
}
await createIn('departments','Department',[[/^Code/i,'ENG'],[/Name \(EN\)|English name|^Name/i,'Engineering']],'30-dept');
await createIn('designations','Designation',[[/^Code/i,'SWE'],[/Title \(EN\)|English title|^Title/i,'Software Engineer']],'31-desig');
await createIn('grades','Grade',[[/^Code/i,'G3'],[/^Name/i,'Grade 3']],'32-grade');
await createIn('branches','Branch',[[/^Code/i,'HQ'],[/Name \(EN\)|English name|^Name/i,'Head Office']],'33-branch');
await b.close();
