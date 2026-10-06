// UI-contract test only: actual Next/React page, all API calls intercepted with synthetic responses.
import { pathToFileURL, fileURLToPath } from 'node:url';
import fs from 'node:fs/promises';
import assert from 'node:assert/strict';
const ROOT=process.env.HRM_REPO_ROOT ?? fileURLToPath(new URL('../../', import.meta.url)).replace(/\/$/, '');
const OUT=process.env.SAUDI_EXPORT_EVIDENCE_DIR ?? ROOT+'/artifacts/saudi-bank-export';
await fs.mkdir(OUT,{recursive:true});
const { chromium } = await import(pathToFileURL(ROOT+'/frontend/node_modules/playwright/index.mjs').href);
const companyId='11111111-1111-4111-8111-111111111111',runId='22222222-2222-4222-8222-222222222222',batchId='33333333-3333-4333-8333-333333333333';
const formatId='anb-connect-csv-v1';
const settings={formatId,molEstablishmentId:'7001234567',mainAccountNumber:'0108061198800026',organizationName:'Synthetic Employer',organizationAddress1:'Riyadh',organizationAddress2:'Olaya',organizationAddress3:'Building 10',companyName:'Synthetic LLC',narrative:'September payroll',batchType:'PAYROLL'};
const user={id:'fixture-user',tenantId:'fixture-tenant',tenantSlug:'export-fixture',email:'admin@export-fixture.test',fullName:'Synthetic Export Operator',roles:['Admin'],accountType:'Group',isGroupScope:true,companies:[],permissions:['dashboard.read','payroll.read','payroll.export','payroll.structure_manage','employees.read','employees.write','employees.sensitive','approvals.read']};
const run={id:runId,companyId,year:2026,month:9,status:'Locked',employeeCount:2,totalNetSalary:11550.5};
const batch={id:batchId,payrollRunId:runId,batchNumber:'TEST-PAYROLL-EXPORT',paymentMethod:'WPS',totalAmount:11550.5,currency:'SAR',status:'Pending',wpsStatus:'Draft'};
const records=[{id:'r1',paymentBatchId:batchId,employeeId:1,amount:6300,iban:'SYNTHETIC-NOT-FOR-PAYMENT',status:'Pending',wpsReference:'FIXTURE-1'}];
records.push({...records[0],id:'r2',employeeId:2,amount:5250.5,wpsReference:'FIXTURE-2'});
const formats=[{id:formatId,name:'ANB Connect payroll payment — header.csv + body.csv',bank:'Arab National Bank (ANB)',channel:'ANB Connect API channel; not corporate portal WPY',sourceUrl:'https://connect.anb.com.sa/apis/api/payroll-payment',reviewedOn:'2026-09-26',acceptanceStatus:'specification-implemented; bank-acceptance-not-verified'}];
const results=[];const browser=await chromium.launch({channel:'chrome',headless:true});
try {
 for (const [name,width,height,activation] of [['desktop',1440,1000,true],['phone',390,844,true],['desktop-disabled',1440,1000,false],['phone-disabled',390,844,false]]) {
  const context=await browser.newContext({viewport:{width,height},acceptDownloads:true});
  const page=await context.newPage();page.setDefaultTimeout(10000);const errors=[],writes=[],unexpected=[],bankRequests=[];let artifact=null;
  page.on('pageerror',e=>{errors.push(String(e));console.log('PAGE_ERROR',String(e));});page.on('console',m=>{if(m.type()==='error')console.log('CONSOLE',m.text());});
  await page.addInitScript(()=>{localStorage.setItem('zayra_access_token','fixture-token');localStorage.setItem('zayra_refresh_token','fixture-refresh');localStorage.setItem('kynexone.theme','light');localStorage.setItem('kynexone-locale-choice-v2','en');});
  await page.route('**/api/**',async route=>{
   const req=route.request(),p=new URL(req.url()).pathname,m=req.method();
   const json=(body,status=200)=>route.fulfill({status,contentType:'application/json',body:JSON.stringify(body)});
   if(!['GET','HEAD','OPTIONS'].includes(m))writes.push({path:p,method:m});
   if(p==='/api/auth/me')return json(user);if(p==='/api/ai/insights')return json({items:[]});if(p==='/api/help-texts')return json([]);
   if(['/api/features/disabled-keys','/api/features/modules','/api/notifications'].includes(p))return json([]);
   if(p==='/api/tenant-admin/localization')return json({defaultTimezone:'Asia/Riyadh',currencyCode:'SAR',calendarSystem:'Gregorian',hijriDatesEnabled:false});
   if(p==='/api/payroll/companies')return json([{id:companyId,name:'Synthetic Employer',tradeName:'Synthetic Employer',defaultCurrency:'SAR',wpsEmployerId:'7001234567',gosiEmployerId:''}]);
   if(p==='/api/payroll/overview')return json({year:2026,month:9,totalCompanies:1,totalActiveEmployees:2,totalGrossPayroll:12000.5,totalNetPayroll:11550.5,totalValidationErrors:0,totalPendingApprovals:0,companies:[]});
   if(p==='/api/payroll/readiness')return json({year:2026,month:9,companyId:null,completionPercent:100,isReadyForProcessing:true,totalActiveEmployees:2,employeesWithSalary:2,salaryCoveragePercent:100,validationErrors:0,payrollRunStatus:'Locked',steps:[]});
   if(p==='/api/payroll/reports/summary')return json({totalRuns:1,lockedRuns:1,totalEmployeesPaid:0,totalGrossYtd:12000.5,totalNetYtd:11550.5});
   if(p==='/api/payroll/runs')return json({items:[run],total:1,page:1,pageSize:50});
   if(p==='/api/payroll/payment-batches')return json([batch]);
   if(p===`/api/payroll/payment-batches/${batchId}/records`)return json(records);
   const bp='/api/payroll/bank-exports';
   if(p.startsWith(bp))bankRequests.push(p);
   if(p===bp+`/batches/${batchId}/availability`)return json({enabled:activation});
   if(p===bp+'/formats')return json(formats);
   if(p===bp+`/companies/${companyId}/settings`)return json(settings);
   if(p===bp+`/batches/${batchId}/context`)return json({companyId,companyName:'Synthetic Employer',countryCode:'SA',runStatus:'Locked',existingExport:artifact});
   if(p===bp+`/batches/${batchId}/validate`)return json({canExport:true,errors:[],warnings:[],employeeCount:2,totalAmount:11550.5,currency:'SAR',formatId});
   if(p===bp+`/batches/${batchId}/generate`){const b=req.postDataJSON();artifact={id:'44444444-4444-4444-8444-444444444444',formatId,files:[{name:'header.csv',sha256:'a'.repeat(64)},{name:'body.csv',sha256:'b'.repeat(64)}],batchReference:b.batchReference,paymentDate:b.paymentDate,employeeCount:2,totalAmount:11550.5,downloadUrl:bp+`/batches/${batchId}/download`};return json(artifact);}
   if(p===bp+`/batches/${batchId}/download`)return route.fulfill({status:200,contentType:'application/zip',headers:{'Content-Disposition':'attachment; filename="synthetic-ui-test.zip"'},body:Buffer.from('UEsFBgAAAAAAAAAAAAAAAAAAAAAAAA==','base64')});
   if(p==='/api/employees/1'&&m==='GET')return json({id:1,employeeCode:'SYN001',fullName:'Synthetic Employee',englishName:'Synthetic Employee',wpsBankDetails:JSON.stringify({schema:'saudi-bank-beneficiary-v1',bicCode:'ARNBSARI'})});
   if(p==='/api/employees/1'&&m==='PUT'){const b=req.postDataJSON();const d=JSON.parse(b.changes.wpsBankDetails);assert.equal(d.schema,'saudi-bank-beneficiary-v1');assert.equal(d.employeeAddress1,'Riyadh');return json({requiresApproval:true,approvalRequestId:'synthetic-approval',changeRequestId:'synthetic-change'},202);}
   if(p.startsWith(bp)){unexpected.push(p);return json({message:'Unconfigured test route'},500);}
   return json({items:[],total:0});
  });
  const steps=[];
  try {
   await page.goto((process.env.E2E_BASE_URL ?? 'http://127.0.0.1:5200')+'/payroll',{waitUntil:'domcontentloaded',timeout:60000});
   await page.getByRole('tab',{name:'Bank / WPS Files',exact:true}).click({timeout:30000});
   await page.locator('select').filter({has:page.locator(`option[value="${runId}"]`)}).first().selectOption(runId);
   const availability=page.waitForResponse(r=>new URL(r.url()).pathname.endsWith('/availability'));
   await page.getByText('TEST-PAYROLL-EXPORT',{exact:true}).click();
   await availability;
   if(!activation){
    assert.equal(await page.getByRole('heading',{name:'Bank instruction file — ANB Connect CSV',exact:true}).count(),0);
    assert.deepEqual(bankRequests,[`/api/payroll/bank-exports/batches/${batchId}/availability`]);
    assert.deepEqual(writes,[]);assert.deepEqual(errors,[]);
    await page.screenshot({path:OUT+`/browser-${name}.png`,fullPage:false});
    results.push({name,status:'PASS',steps:['existing payroll renders','export disabled: no panel, settings, export or write requests'],scope:'UI fixtures only; server gate separately tested'});
    await context.close();continue;
   }
   const heading=page.getByRole('heading',{name:'Bank instruction file — ANB Connect CSV',exact:true});await heading.waitFor();steps.push('panel rendered');
   assert.equal(await page.getByRole('button',{name:'Generate files',exact:true}).isDisabled(),true);
   await page.getByLabel('Batch number',{exact:true}).fill('202609123');
   await page.getByLabel('Credit value date',{exact:true}).fill('2026-09-30');
   await page.getByRole('button',{name:'Validate',exact:true}).click();
   await page.getByText('Validation passed. No blockers found.',{exact:true}).waitFor();steps.push('validation renders');
   await page.getByRole('button',{name:'Generate files',exact:true}).click();
   await page.getByText('header.csv',{exact:true}).waitFor();await page.getByText('body.csv',{exact:true}).waitFor();steps.push('artifact metadata renders');
   const downloaded=page.waitForEvent('download');await page.getByRole('button',{name:'Download zip',exact:true}).click();
   assert.equal((await downloaded).suggestedFilename(),'synthetic-ui-test.zip');steps.push('download received');
   await page.getByLabel('Employee (from this batch)',{exact:true}).selectOption('1');
   await page.getByLabel('BIC (SWIFT) code',{exact:true}).fill('ARNBSARI');
   for(const [n,v] of [[1,'Riyadh'],[2,'Olaya'],[3,'Building 11']])await page.getByLabel(`Employee address line ${n}`,{exact:true}).fill(v);
   await page.getByRole('button',{name:'Submit for approval',exact:true}).click();
   await page.getByText(/Submitted for separate approval.*Not applied yet/).waitFor();steps.push('beneficiary approval pending, not self-applied');
   assert.equal(await page.getByRole('button',{name:'Generate files',exact:true}).isDisabled(),true);steps.push('beneficiary change invalidates validation');
   await heading.scrollIntoViewIfNeeded();await page.screenshot({path:OUT+`/browser-${name}.png`,fullPage:false});
   const overflow=await page.evaluate(()=>document.documentElement.scrollWidth-document.documentElement.clientWidth);
   assert.deepEqual(errors,[]);assert.deepEqual(unexpected,[]);
   results.push({name,status:'PASS',steps,pageErrors:errors,unexpectedBankRoutes:unexpected,overflowPixels:overflow,writes,scope:'UI fixtures only; API responses intercepted; no bank calls'});
  } catch(e){results.push({name,status:'FAIL',steps,error:String(e),pageErrors:errors,unexpectedBankRoutes:unexpected,writes});await page.screenshot({path:OUT+`/browser-${name}-failure.png`,fullPage:false});}
  await context.close();
 }
} finally {await browser.close();}
await fs.writeFile(OUT+'/browser-results.json',JSON.stringify(results,null,2));console.log(JSON.stringify(results,null,2));
if(results.some(x=>x.status!=='PASS'))process.exitCode=1;
