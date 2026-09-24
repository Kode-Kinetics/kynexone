import { chromium } from '@playwright/test';
const SHOT='/private/tmp/claude-501/-Users-zackkhan-Downloads-KynexOne/074df5aa-77b3-476e-892a-2f51f2c41b55/scratchpad/journey';
const b=await chromium.launch();
const ctx=await b.newContext({baseURL:'https://kynexone.vercel.app',viewport:{width:1440,height:900},storageState:`${SHOT}/state.json`});
const p=await ctx.newPage();
const log=[]; let errs=[];
p.on('pageerror',e=>errs.push(e.message.slice(0,150)));
async function step(n,label,url,fn){
  errs=[];
  if(url) await p.goto(url,{waitUntil:'networkidle',timeout:45000}).catch(()=>{});
  await p.waitForTimeout(1800);
  if(fn) await fn().catch(e=>errs.push('ACTION: '+e.message.slice(0,120)));
  await p.waitForTimeout(1500);
  const t=((await p.locator('body').innerText().catch(()=>''))||'');
  const crashed=/Something went wrong/i.test(t);
  await p.screenshot({path:`${SHOT}/${String(n).padStart(2,'0')}-${label.replace(/[^a-z0-9]+/gi,'-')}.png`,fullPage:true});
  const st=crashed?'CRASHED':errs.length?'ERRORS':'ok';
  log.push({n,label,st,errs:[...errs]});
  console.log(`${String(n).padStart(2,'0')} ${st.padEnd(8)} ${label}${errs.length?'  :: '+errs[0].slice(0,110):''}`);
  return st;
}
// ---- what exists today ----
await step(1,'Setup Companies','/setup?tab=companies');
await step(2,'Setup Departments','/setup?tab=departments');
await step(3,'Setup Designations','/setup?tab=designations');
await step(4,'Setup Grades','/setup?tab=grades');
await step(5,'Setup Cost Centres Budget','/setup?tab=establishment');
await step(6,'Setup Leave Types','/setup?tab=masterData');
await step(7,'People list','/people');
await step(8,'Leave','/leave');
await step(9,'Payroll','/payroll');
await step(10,'HR Letters','/hr-letters');
await step(11,'Org Chart','/org-chart');
await step(12,'Reports','/reports');
console.log('\n=== SUMMARY ===');
for(const l of log) console.log(`${l.st.padEnd(8)} ${l.label}`);
await b.close();
