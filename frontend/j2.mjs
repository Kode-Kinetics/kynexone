import { chromium } from '@playwright/test';
const SHOT='/private/tmp/claude-501/-Users-zackkhan-Downloads-KynexOne/074df5aa-77b3-476e-892a-2f51f2c41b55/scratchpad/journey';
const ROUTES=['/dashboard','/people','/org-chart','/hr-letters','/attendance','/leave','/shifts','/overtime','/timesheets',
 '/payroll','/payslip-templates','/loans','/benefits','/recruitment','/offboarding','/performance','/compliance',
 '/reports','/approvals','/setup','/user-management','/tenant-admin','/saudi-compliance','/ess','/opening-balances'];
const b=await chromium.launch();
const ctx=await b.newContext({baseURL:'https://kynexone.vercel.app',viewport:{width:1440,height:900},storageState:`${SHOT}/state.json`});
const p=await ctx.newPage(); let errs=[];
p.on('pageerror',e=>errs.push(e.message.slice(0,140)));
p.on('console',m=>{if(m.type()==='error'&&!/favicon|404|Download the React/.test(m.text()))errs.push('C:'+m.text().slice(0,140));});
const bad=[];
for(const r of ROUTES){
  errs=[]; const api=[];
  const h=x=>{const u=x.url(); if(u.includes('/api/')&&x.status()>=400)api.push(`${x.status()} ${u.replace(/^https?:\/\/[^/]*/,'').slice(0,60)}`)};
  p.on('response',h);
  await p.goto(r,{waitUntil:'networkidle',timeout:45000}).catch(e=>errs.push('GOTO '+e.message.slice(0,60)));
  await p.waitForTimeout(2200);
  const t=((await p.locator('body').innerText().catch(()=>''))||'');
  const crashed=/Something went wrong/i.test(t);
  const denied=/Access Denied|do not have permission/i.test(t);
  const status=crashed?'CRASHED':denied?'DENIED':errs.length?'ERRORS':'ok';
  if(status!=='ok'){bad.push({r,status,errs:errs.slice(0,2),api:api.slice(0,3)});
    await p.screenshot({path:`${SHOT}/bad-${r.replace(/\//g,'_')}.png`,fullPage:true});}
  console.log(`${status.padEnd(8)} ${r}${api.length?'  ['+api.slice(0,2).join(', ')+']':''}`);
  p.off('response',h);
}
console.log('\n=== BROKEN ===');
for(const x of bad) console.log(`${x.r} -> ${x.status}\n   ${(x.errs[0]||'').slice(0,200)}`);
await b.close();
