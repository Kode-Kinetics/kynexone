import { chromium } from '@playwright/test';
const SHOT='/private/tmp/claude-501/-Users-zackkhan-Downloads-KynexOne/074df5aa-77b3-476e-892a-2f51f2c41b55/scratchpad/journey';
const b=await chromium.launch();
const ctx=await b.newContext({baseURL:'https://kynexone.vercel.app',viewport:{width:1440,height:900},storageState:`${SHOT}/state.json`});
const p=await ctx.newPage();
p.on('response',async r=>{if(r.url().includes('/api/companies?')){try{const j=await r.json();
  const c=(j.items||[])[0]||{}; console.log('company:',c.legalNameEn,'| countryCode:',JSON.stringify(c.countryCode),'| emailDomain:',JSON.stringify(c.emailDomain));}catch{}}});
await p.goto('/setup?tab=companies',{waitUntil:'networkidle'}); await p.waitForTimeout(2500);
// open edit on the company
const edit=p.locator('button[title*="Edit" i], button:has-text("Edit")').first();
if(await edit.count()){await edit.click(); await p.waitForTimeout(2000);
  const labs=(await p.locator('[role=dialog] label, .modal label').allTextContents()).map(x=>x.trim().split('\n')[0]).filter(Boolean);
  console.log('\ncompany form fields:', labs.slice(0,16).join(' | '));
  await p.screenshot({path:`${SHOT}/95-company-edit.png`,fullPage:true});
}
await b.close();
