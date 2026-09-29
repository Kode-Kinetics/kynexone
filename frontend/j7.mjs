import { chromium } from '@playwright/test';
const SHOT='/private/tmp/claude-501/-Users-zackkhan-Downloads-KynexOne/074df5aa-77b3-476e-892a-2f51f2c41b55/scratchpad/journey';
const b=await chromium.launch();
const ctx=await b.newContext({baseURL:'https://kynexone.vercel.app',viewport:{width:1440,height:900},storageState:`${SHOT}/state.json`});
const p=await ctx.newPage(); let errs=[];
p.on('pageerror',e=>errs.push(e.message.slice(0,130)));
async function make(tab,btnRx,vals,name,shot){
  errs=[];
  await p.goto('/setup?tab='+tab,{waitUntil:'networkidle'}); await p.waitForTimeout(2200);
  const add=p.locator('button').filter({hasText:btnRx}).first();
  if(!await add.count()){console.log(`${name}: NO ADD BUTTON`);return;}
  await add.click(); await p.waitForTimeout(1800);
  const ins=p.locator('[role=dialog] input, .modal input');
  const n=await ins.count();
  for(let i=0;i<Math.min(vals.length,n);i++) await ins.nth(i).fill(vals[i]).catch(()=>{});
  const save=p.locator('[role=dialog] button, .modal button').filter({hasText:/^Save$/}).first();
  await save.click().catch(()=>{}); await p.waitForTimeout(3200);
  const t=((await p.locator('body').innerText().catch(()=>''))||'');
  console.log(`${name}: ${t.includes(vals[1])?'CREATED ✅':'NOT IN LIST ❌'}${errs.length?' :: '+errs[0].slice(0,80):''}`);
  await p.screenshot({path:`${SHOT}/${shot}.png`,fullPage:true});
}
await make('departments',/Add Department/i,['ENG','Engineering'],'Department','50-dept');
await make('designations',/Add Designation/i,['SWE','Software Engineer'],'Designation','51-desig');
await make('grades',/Add Grade/i,['G3','Grade 3'],'Grade','52-grade');
await make('branches',/Add Branch/i,['HQ','Head Office'],'Branch','53-branch');
await b.close();
