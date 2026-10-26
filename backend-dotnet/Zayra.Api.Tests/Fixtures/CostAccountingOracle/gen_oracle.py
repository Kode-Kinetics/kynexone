#!/usr/bin/env python3
"""Independent oracle generator for KynexOne cost-centre accounting v1.
Pure stdlib. Usage: python3 gen_oracle.py [--annuity 0.10] [--out DIR]
Hand-accounting rules only; knows nothing of the implementation."""
import csv, os, argparse, datetime
from decimal import Decimal as D, ROUND_HALF_UP, ROUND_FLOOR
from fractions import Fraction
from collections import defaultdict, OrderedDict

ap = argparse.ArgumentParser()
ap.add_argument("--out", default=os.path.dirname(os.path.abspath(__file__)))
A = ap.parse_args()
OUT = A.out
os.makedirs(f"{OUT}/inputs", exist_ok=True); os.makedirs(f"{OUT}/expected", exist_ok=True)

def r2(x): return D(x).quantize(D("0.01"), rounding=ROUND_HALF_UP)
def w(path, header, rows):
    with open(f"{OUT}/{path}", "w", newline="", encoding="utf-8") as f:
        c = csv.writer(f); c.writerow(header)
        for r in rows: c.writerow(["" if v is None else (f"{v:.2f}" if isinstance(v, D) else v) for v in r])

# ---------------------------------------------------------------- rates (app KSA country pack, read-only)
# Rule keys/values from StatutoryRuleSeeder + KsaGosiCohortSchedule + KsaDeductionCalculator.
ap2 = None
PRE = D("0.09")          # gosi.saudi_employee_rate / gosi.saudi_employer_rate (existing subscribers, first registered < 2024-07-03, and Unknown cohort)
NEW = {"2026-09": D("0.10"), "2026-10": D("0.10")}   # gosi.new_entrant_employee_rate / _employer_rate, step effective 2026-07-01 (9% 2024-07, 9.5% 2025-07, 10% 2026-07)
SANED = D("0.0075")      # gosi.saned_rate (each side, Saudis only)
OH = D("0.02")           # gosi.expat_occupational_hazard_rate (employer; ALL employees incl. Saudis)
CAP = D("45000")         # gosi.covered_wage_ceiling_sar (monthly); floor key gosi.covered_wage_floor_sar NOT seeded
rows=[["rule_key","value","applies_to","effective_from","used_for_oracle","note"],
 ["gosi.saudi_employee_rate","0.09","Saudi, cohort PreJuly2024 or Unknown (annuities, employee)","pack default","yes","E001 E002 E005 E006"],
 ["gosi.saudi_employer_rate","0.09","Saudi, cohort PreJuly2024 or Unknown (annuities, employer)","pack default","yes",""],
 ["gosi.new_entrant_employee_rate","0.09 / 0.095 / 0.10 / 0.105 / 0.11","Saudi, cohort NewEntrant (first GOSI registration >= 2024-07-03)","2024-07-01 / 2025-07-01 / 2026-07-01 / 2027-07-01 / 2028-07-01","0.10 (period 2026-09 and 2026-10)","E008 only"],
 ["gosi.new_entrant_employer_rate","0.09 / 0.095 / 0.10 / 0.105 / 0.11","as above (employer)","as above","0.10",""],
 ["gosi.saned_rate","0.0075","Saudi, each side","pack default","yes","not applied to non-Saudi"],
 ["gosi.expat_occupational_hazard_rate","0.02","employer; every employee (Saudi and non-Saudi)","pack default","yes","component GOSI-OH-ER"],
 ["gosi.covered_wage_ceiling_sar","45000","monthly cap on basic+housing","pack default","yes","no employee exceeds it"],
 ["gosi.covered_wage_floor_sar","(not seeded)","","","no","no clamping floor"],
 ["gosi.covered_wage_min_annuities_sar / ..._occupational_hazards_sar","1500 / 400","validator warning only","pack default","no","not a clamp"],
 ["",""," ","","",""],
 ["COMPONENTS (rounded separately per employee, Math.Round 2dp half-even in the app, half-up here - see README flag F-ROUND)","","","","",""],
 ["GOSI-ANN-EE","covered wage x annuity rate","Saudi","","","DED:STATUTORY_EE -> 2101"],
 ["GOSI-SANED-EE","covered wage x 0.0075","Saudi","","","DED:STATUTORY_EE -> 2101"],
 ["GOSI-ANN-ER","covered wage x annuity rate","Saudi","","","employer; DED:STATUTORY_ER -> 2106, pair DR 5101"],
 ["GOSI-SANED-ER","covered wage x 0.0075","Saudi","","","employer"],
 ["GOSI-OH-ER","covered wage x 0.02","everyone","","","employer"],
 ["covered wage","basic + housing (+ GOSI-included bonus in housing slot; oracle bonuses are NOT GOSI-included)","","","",""]]
with open(f"{OUT}/inputs/08_gosi_rates.csv","w",newline="") as f: csv.writer(f).writerows(rows)

# ---------------------------------------------------------------- master data
CCS = OrderedDict([
 ("CC-100", dict(name="Head Office", parent=None, cls="G_AND_A", seg="4100", active=True)),
 ("CC-110", dict(name="Finance & Admin", parent="CC-100", cls="G_AND_A", seg="4110", active=True)),
 ("CC-200", dict(name="Sales & Marketing", parent=None, cls="SELLING", seg="6200", active=True)),
 ("CC-300", dict(name="Operations", parent=None, cls="COGS", seg="5300", active=True)),
])
DEPTS = OrderedDict([
 ("D-FIN", dict(name="Finance", cc="CC-110", parent=None)),
 ("D-SAL", dict(name="Sales", cc="CC-200", parent=None)),
 ("D-OPS", dict(name="Operations", cc=None, parent=None)),      # no CC, no parent -> position / company default
 ("D-SUP", dict(name="Support Services (sub-dept of Finance)", cc=None, parent="D-FIN")),  # parent walk
])
POS = OrderedDict([("P-TECH",dict(name="Technician",cc=None)),("P-SUPV",dict(name="Supervisor",cc="CC-300")),("P-GEN",dict(name="General",cc=None))])
DEFAULT_CC = "CC-100"
w("inputs/02_cost_centres.csv",["code","name","parent_code","functional_class","gl_segment","active"],
  [[k,v["name"],v["parent"],v["cls"],v["seg"],"true"] for k,v in CCS.items()])
w("inputs/03_departments.csv",["code","name","cost_centre_code","parent_department_code"],[[k,v["name"],v["cc"],v["parent"]] for k,v in DEPTS.items()])
w("inputs/04_positions.csv",["code","name","cost_centre_code"],[[k,v["name"],v["cc"]] for k,v in POS.items()])

# code, nat, dept, pos, override_cc, basic, housing, transport, other, note
EMPS = OrderedDict()
def E(code, nat, dept, pos, ovr, b, h, t, note, hire="2022-01-01"):
    EMPS[code] = dict(nat=nat, dept=dept, pos=pos, ovr=ovr, b=D(b), h=D(h), t=D(t), note=note, hire=hire)
E("E001","Saudi","D-FIN","P-GEN",None,15000,3750,1000,"dept CC; bonus")
E("E002","Saudi","D-SAL","P-GEN",None,9000,2250,750,"dept CC; loan deduction; bonus")
E("E003","Expat","D-SAL","P-GEN",None,8000,2000,500,"dept CC; other allowance 300; advance issued M1, recovered M2")
E("E004","Expat","D-OPS","P-TECH",None,5000,1250,400,"dept+position have no CC -> company default CC-100")
E("E005","Saudi","D-OPS","P-SUPV",None,7000,1750,500,"position CC-300; bonus")
E("E006","Saudi","D-FIN","P-GEN",None,6500,1625,600,"transferred D-SAL->D-FIN on 2026-09-16; v1 rule: CC current at Lock (CC-110) for the WHOLE month")
E("E007","Expat","D-FIN","P-GEN","CC-300",11000,2750,800,"explicit override CC-300 (department is Finance)")
E("E008","Saudi","D-SAL","P-GEN",None,D("10333.33"),D("2583.33"),D("666.67"),"halala-awkward salary; overtime")
E("E009","Expat","D-SAL","P-GEN",None,6000,1500,500,"terminated 2026-09-20; final settlement + EOSB",hire="2020-05-01")
E("E010","Expat","D-SUP","P-GEN",None,4000,1000,300,"dept has no CC; parent dept Finance CC-110")
COHORT = {"E001":("PreJuly2024","2019-03-01"),"E002":("PreJuly2024","2021-06-01"),"E005":("PreJuly2024","2018-01-01"),
          "E006":("Unknown",""),"E008":("NewEntrant","2025-02-10")}
w("inputs/05_employees.csv",["employee_code","nationality","department","position","override_cost_centre","cost_centre_is_override","basic","housing","transport","hire_date","gosi_cohort","gosi_first_registered_on","note"],
  [[k,v["nat"],v["dept"],v["pos"],v["ovr"],"true" if v["ovr"] else "false",v["b"],v["h"],v["t"],v["hire"],COHORT.get(k,("n/a",""))[0],COHORT.get(k,("n/a",""))[1],v["note"]] for k,v in EMPS.items()])

ACCTS = OrderedDict([
 ("1000",("Cash/Bank","Asset","NotAllowed")),("1410",("Employee Salary Advances","Asset","NotAllowed")),
 ("2100",("Salaries Payable","Liability","NotAllowed")),("2101",("Social Insurance Payable (Employee)","Liability","NotAllowed")),
 ("2106",("Social Insurance Employer Payable","Liability","NotAllowed")),("2107",("Loan & Advance Deductions Payable","Liability","NotAllowed")),
 ("2300",("Bonus Payable","Liability","NotAllowed")),("2320",("Final Settlement Payable","Liability","NotAllowed")),
 ("5001",("Basic Salary Expense","Expense","Required")),("5002",("Housing Allowance Expense","Expense","Required")),
 ("5003",("Transport Allowance Expense","Expense","Required")),("5004",("Other Allowances Expense","Expense","Required")),
 ("5005",("Overtime Expense","Expense","Required")),("5101",("Employer Social Insurance Expense","Expense","Required")),
 ("5110",("End of Service Benefit Expense","Expense","Required")),("5111",("Leave Encashment Expense","Expense","Required")),
 ("5113",("Final Settlement Deductions (contra)","Expense","Required")),("5199",("Payroll Rounding Differences","Expense","Required")),
 ("6100",("Employee Bonus Expense","Expense","Required")),
 ("5011",("Direct Labour - Basic (COGS)","Expense","Required")),("5012",("Direct Labour - Housing (COGS)","Expense","Required")),
])
w("inputs/06_gl_accounts.csv",["code","name","type","cost_center_rule","note"],
  [[k,v[0],v[1],v[2],"M2 only (new account for class re-route)" if k in("5011","5012") else ""] for k,v in ACCTS.items()])
DRV = {"EARN:BASIC":"5001","EARN:HOUSING":"5002","EARN:TRANSPORT":"5003","EARN:OTHER_ALLOWANCES":"5004","EARN:OVERTIME":"5005",
       "EARN:BONUS":"6100","EARN:EOSB":"5110","EARN:LEAVE_ENCASHMENT":"5111","DED:STATUTORY_EE":"2101","DED:STATUTORY_ER":"2106",
       "DED:LOAN":"2107","DED:SETTLEMENT_RECOVERY":"5113","EMPLOYER_STATUTORY_EXPENSE":"5101","NET_PAYABLE":"2100",
       "BONUS_PAYABLE":"2300","SETTLEMENT_PAYABLE":"2320","ADVANCE_RECEIVABLE":"1410","CASH_BANK":"1000","ROUNDING":"5199"}
CLASS_OVR_M2 = {("EARN:BASIC","COGS"):"5011",("EARN:HOUSING","COGS"):"5012"}
w("inputs/07_gl_mappings.csv",["effective_months","scope","driver_key","functional_class","account_code","note"],
  [["M1+M2","tenant default",k,None,v,"driver default"] for k,v in DRV.items()] +
  [["M2 only","company",k[0],k[1],v,"re-route by functional class COGS (cost centre CC-300); saved between M1 Lock and M2 Process"] for k,v in CLASS_OVR_M2.items()])
w("inputs/01_company_params.csv",["key","value","note"],[
 ["company","ACME-SA","fictional; generic"],["currency","SAR","scale 2"],["cost_accounting_enabled","true","M1 and M2"],
 ["default_cost_centre","CC-100","company fallback of last resort and ROUNDING home"],["cost_centre_small_cell_min","0","roll-up OFF"],
 ["account_rule_preset","Expense=Required; Asset/Liability=NotAllowed","explicit user action"],["eosb_provisioning","off","settlement expenses full gratuity"],
 ["periods","2026-09 (M1), 2026-10 (M2)",""],["gosi_annuity_each_side","9% existing/unknown cohort; 10% new entrant (2026-07 step)","app KSA pack; see inputs/08_gosi_rates.csv"],
 ["gosi_saned_each_side","0.75%","Saudis only"],["gosi_occ_hazard_employer","2%","all employees"],["gosi_base","basic+housing, cap 45000",""],
 ["rounding","each employee x component row rounded to 2dp half-up, THEN grouped",""]])

# ---------------------------------------------------------------- cost-centre resolution (spec 2.2, re-derived by hand)
def resolve(e, ctx):
    """returns (cc_code|None, source|reason)"""
    def chk(cc, src):
        if cc is None: return None
        v = ctx["ccs"].get(cc)
        if v is None or not v["active"]: return (None, f"{src}_cost_centre_invalid")
        return (cc, src)
    if e["ovr"]:
        return chk(e["ovr"], "Employee")
    p = ctx["pos"].get(e["pos"])
    if p and p["cc"]: return chk(p["cc"], "Position")
    d, seen, depth, first = e["dept"], set(), 0, True
    while d and d not in seen and depth < 20:
        seen.add(d); dd = ctx["depts"].get(d)
        if not dd: break
        if dd["cc"]: return chk(dd["cc"], "Department" if first else "ParentDepartment")
        d = dd["parent"]; first = False; depth += 1
    if ctx["default"]: return chk(ctx["default"], "CompanyDefault")
    return (None, "none")
BASE_CTX = dict(ccs=CCS, pos=POS, depts=DEPTS, default=DEFAULT_CC)

# ---------------------------------------------------------------- payroll register
def gosi(e, basic, housing, code, month):
    """returns list of (component, amount, employer?) rounded separately (app: one line per component)"""
    base = min(basic + housing, CAP); out = []
    if e["nat"] == "Saudi":
        rate = NEW["2026-09" if month==1 else "2026-10"] if COHORT.get(code,("",))[0]=="NewEntrant" else PRE
        out += [("GOSI-ANN-EE", r2(base*rate), False), ("GOSI-SANED-EE", r2(base*SANED), False),
                ("GOSI-ANN-ER", r2(base*rate), True), ("GOSI-SANED-ER", r2(base*SANED), True)]
    out.append(("GOSI-OH-ER", r2(base*OH), True))
    return out

def register(month, ctx=BASE_CTX):
    """list of rows for the REGULAR run of the month"""
    rows = []
    def add(emp, comp, src, drv, kind, amt, employer=False):
        if amt == 0: return
        rows.append(dict(emp=emp, comp=comp, src=src, drv=drv, kind=kind, amt=D(amt), employer=employer))
    for code, e in EMPS.items():
        if month == 2 and code == "E009": continue
        f = D(20)/D(30) if (month == 1 and code == "E009") else D(1)
        b, h, t = r2(e["b"]*f), r2(e["h"]*f), r2(e["t"]*f)
        add(code,"BASIC","Earning","EARN:BASIC","E",b); add(code,"HOUSING","Earning","EARN:HOUSING","E",h); add(code,"TRANSPORT","Earning","EARN:TRANSPORT","E",t)
        if code == "E003": add(code,"OTHER_ALLOWANCES","Earning","EARN:OTHER_ALLOWANCES","E",D(300))
        if code == "E008": add(code,"OVERTIME","Earning","EARN:OVERTIME","E",D("1234.57") if month==1 else D("987.65"))
        if month == 1 and code in ("E001","E002","E005"): add(code,"BONUS","Bonus","EARN:BONUS","E",D(1000))
        for comp, amt, emp_side in gosi(e, b, h, code, month):
            add(code, comp, "Statutory", "DED:STATUTORY_ER" if emp_side else "DED:STATUTORY_EE", "D", amt, emp_side)
        if code == "E002": add(code,"LOAN_EMI","Loan","DED:LOAN","D",D(500))
        if month == 2 and code == "E003": add(code,"ADV_RECOVERY","Loan","DED:LOAN","D",D(400))
    for r in rows:
        r["cc"], r["ccsrc"] = resolve(EMPS[r["emp"]], ctx)
    return rows

# ---------------------------------------------------------------- allocator (largest remainder)
def allocate(total_minor, parts):
    """parts [(code, weight_minor)] -> {code: minor}; returns also detail rows"""
    sign = -1 if total_minor < 0 else 1; T = abs(total_minor)
    W = sum(x for _, x in parts)
    if W == 0: return {None: sign*T}, []
    det = []
    for c, x in parts:
        fl = (T*x)//W; fr = Fraction(T*x, W) - fl
        det.append([c, x, fl, fr])
    rem = T - sum(d[2] for d in det)
    order = sorted(det, key=lambda d: (-d[3], d[0]))
    give = {d[0]: 1 for d in order[:rem]}
    res = {d[0]: sign*(d[2] + give.get(d[0],0)) for d in det}
    for d in det: d.append(d[0] in give)
    return res, det
def m(x): return int((x*100).to_integral_value(rounding=ROUND_HALF_UP))
def un(x): return D(x)/100

ALLOC_LOG = []   # (case, total_minor, detail)

def acct(drv, cc, month):
    if month == 2 and cc:
        k = (drv, CCS[cc]["cls"])
        if k in CLASS_OVR_M2: return CLASS_OVR_M2[k]
    return DRV[drv]

# ---------------------------------------------------------------- journals
def L(jid, period, event, drv, code, side, amt, cc, desc): 
    return dict(journal=jid, period=period, event=event, driver=drv, code=code, side=side, amount=D(amt), cc=cc, desc=desc)

BONUS_ACCRUED = D("2899.99")
def bonus_accrual_journal():
    rows = [r for r in register(1) if r["comp"]=="BONUS"]
    byc = defaultdict(int)
    for r in rows: byc[r["cc"]] += m(r["amt"])
    parts = sorted(byc.items())
    res, det = allocate(m(BONUS_ACCRUED), parts); ALLOC_LOG.append(("BON-ACCR: accrual 2899.99 over bonus weights", m(BONUS_ACCRUED), det))
    out = []
    for c in sorted(res): out.append(L("J-BON-ACCR-2026-09","2026-09","BonusAccrual","EARN:BONUS","6100","DR",un(res[c]),c,"Bonus accrual"))
    out.append(L("J-BON-ACCR-2026-09","2026-09","BonusAccrual","BONUS_PAYABLE","2300","CR",BONUS_ACCRUED,None,"Bonus accrual"))
    return out

def run_journal(jid, period, month, rows, ctx=BASE_CTX, bonus_clear=False, settle=None):
    """rows: register rows. Returns lines in oracle order."""
    out = []; ev = "PayrollLock"
    earn = [r for r in rows if r["kind"]=="E" and r["src"] != "Bonus"]
    ORDER = ["BASIC","HOUSING","TRANSPORT","OTHER_ALLOWANCES","OVERTIME","EOSB_GRATUITY","LEAVE_ENCASHMENT"]
    grp = defaultdict(D)
    for r in earn: grp[(r["comp"], r["drv"], r["cc"])] += r["amt"]
    for (comp, drv, cc) in sorted(grp, key=lambda k: (ORDER.index(k[0]), k[2] or "")):
        if settle and comp in ("EOSB_GRATUITY","LEAVE_ENCASHMENT"): continue
        out.append(L(jid,period,ev,drv,acct(drv,cc,month),"DR",grp[(comp,drv,cc)],cc,f"Payroll earning: {comp}"))
    bon = [r for r in rows if r["src"]=="Bonus"]
    if bon:
        tot = sum(r["amt"] for r in bon)
        if bonus_clear:
            out.append(L(jid,period,"BonusPayrollClearing","BONUS_PAYABLE","2300","DR",BONUS_ACCRUED,None,"Bonus payroll clearing"))
            rem = tot - BONUS_ACCRUED
            byc = defaultdict(int)
            for r in bon: byc[r["cc"]] += m(r["amt"])
            res, det = allocate(m(rem), sorted(byc.items())); ALLOC_LOG.append(("BON-REMAINDER: un-accrued 100.01 over bonus weights", m(rem), det))
            for c in sorted(res): out.append(L(jid,period,ev,"EARN:BONUS","6100","DR",un(res[c]),c,"Payroll earning: BONUS (un-accrued)"))
        else:
            g = defaultdict(D)
            for r in bon: g[r["cc"]] += r["amt"]
            for c in sorted(g): out.append(L(jid,period,ev,"EARN:BONUS","6100","DR",g[c],c,"Payroll earning: BONUS"))
    if settle:   # off-cycle: clearing of the settlement payable
        out.append(L(jid,period,"SettlementPayrollClearing","SETTLEMENT_PAYABLE","2320","DR",settle,None,"Settlement payroll clearing"))
    ded = defaultdict(D); drvof = {}
    for r in rows:
        if r["kind"]=="D" and not r["employer"]:
            if r["drv"]=="DED:SETTLEMENT_RECOVERY": continue
            ded[r["comp"]] += r["amt"]; drvof[r["comp"]] = r["drv"]
    for comp in ["GOSI-ANN-EE","GOSI-SANED-EE","LOAN_EMI","ADV_RECOVERY"]:
        if ded.get(comp): out.append(L(jid,period,ev,drvof[comp],DRV[drvof[comp]],"CR",ded[comp],None,f"Payroll deduction: {comp}"))
    sr = defaultdict(D)
    for r in rows:
        if r["drv"]=="DED:SETTLEMENT_RECOVERY": sr[r["cc"]] += r["amt"]
    for c in sorted(sr): out.append(L(jid,period,ev,"DED:SETTLEMENT_RECOVERY","5113","CR",sr[c],c,"Payroll deduction: SETTLEMENT_RECOVERY"))
    er = defaultdict(D); ercomp = defaultdict(D)
    for r in rows:
        if r["employer"]: er[r["cc"]] += r["amt"]; ercomp[r["comp"]] += r["amt"]
    for comp in ["GOSI-ANN-ER","GOSI-SANED-ER","GOSI-OH-ER"]:
        if ercomp.get(comp): out.append(L(jid,period,ev,"DED:STATUTORY_ER","2106","CR",ercomp[comp],None,f"Payroll deduction: {comp}"))
    for c in sorted(er): out.append(L(jid,period,ev,"EMPLOYER_STATUTORY_EXPENSE","5101","DR",er[c],c,"Employer statutory contribution"))
    gross = sum(r["amt"] for r in rows if r["kind"]=="E")
    eed = sum(r["amt"] for r in rows if r["kind"]=="D" and not r["employer"])
    out.append(L(jid,period,ev,"NET_PAYABLE","2100","CR",gross-eed,None,"Net salaries payable"))
    return out

# ---- M1
reg1 = register(1)
J = []
J += bonus_accrual_journal()
J += run_journal("RUN-REG-2026-09","2026-09",1,reg1,bonus_clear=True)

# E009 final settlement
e9 = EMPS["E009"]; WAGE9 = e9["b"]+e9["h"]+e9["t"]
hire = datetime.date(2020,5,1); last = datetime.date(2026,9,20)
days = (last-hire).days + 1; yrs = D(days)/D(365)
eosb = r2(WAGE9*D("0.5")*5 + WAGE9*(yrs-5))
leave_enc = r2(D("7.5")*WAGE9/30)
SETT_GROSS = eosb + leave_enc
J += [L("J-SETT-ACCR-2026-09","2026-09","SettlementAccrual","EARN:EOSB","5110","DR",eosb,"CC-200","Settlement accrual: EOSB_GRATUITY"),
      L("J-SETT-ACCR-2026-09","2026-09","SettlementAccrual","EARN:LEAVE_ENCASHMENT","5111","DR",leave_enc,"CC-200","Settlement accrual: LEAVE_ENCASHMENT"),
      L("J-SETT-ACCR-2026-09","2026-09","SettlementAccrual","SETTLEMENT_PAYABLE","2320","CR",SETT_GROSS,None,"Settlement accrual")]
oc_rows = [dict(emp="E009",comp="EOSB_GRATUITY",src="Settlement",drv="EARN:EOSB",kind="E",amt=eosb,employer=False),
           dict(emp="E009",comp="LEAVE_ENCASHMENT",src="Settlement",drv="EARN:LEAVE_ENCASHMENT",kind="E",amt=leave_enc,employer=False),
           dict(emp="E009",comp="SETTLEMENT_RECOVERY",src="Settlement",drv="DED:SETTLEMENT_RECOVERY",kind="D",amt=D(300),employer=False)]
for r in oc_rows: r["cc"], r["ccsrc"] = resolve(e9, BASE_CTX)
J += run_journal("RUN-OC-2026-09","2026-09",1,oc_rows,settle=SETT_GROSS)
J += [L("J-ADV-ISS-2026-09","2026-09","AdvanceIssue","ADVANCE_RECEIVABLE","1410","DR",D(1200),None,"Salary advance issued"),
      L("J-ADV-ISS-2026-09","2026-09","AdvanceIssue","CASH_BANK","1000","CR",D(1200),None,"Salary advance issued")]
M1 = J

# ---- M2
reg2 = register(2)
M2 = run_journal("RUN-REG-2026-10","2026-10",2,reg2)
# void OC run + repost without the 300 recovery
orig_oc = [l for l in M1 if l["journal"]=="RUN-OC-2026-09"]
VOID = []
for l in orig_oc:
    c = dict(l); c["journal"]="VOID-RUN-OC-2026-09"; c["period"]="2026-10"; c["event"]="PayrollVoid"; c["side"]="CR" if l["side"]=="DR" else "DR"
    c["desc"]="Void contra: "+l["desc"]; VOID.append(c)
oc2 = [dict(r) for r in oc_rows if r["comp"]!="SETTLEMENT_RECOVERY"]
REPOST = run_journal("RUN-OC-2026-09-R1","2026-10",2,oc2,settle=SETT_GROSS)
M2 += VOID + REPOST

# ---------------------------------------------------------------- write journals & summaries
def side_sum(lines, side): return sum((l["amount"] for l in lines if l["side"]==side), D(0))
HDR = ["journal_id","line_no","period","event_type","driver_key","account_code","account_name","side","amount","cost_centre_code","cc_functional_class","description"]
def jrows(lines):
    n = defaultdict(int); out = []
    for l in lines:
        n[l["journal"]] += 1
        out.append([l["journal"],n[l["journal"]],l["period"],l["event"],l["driver"],l["code"],ACCTS[l["code"]][0],l["side"],l["amount"],l["cc"],CCS[l["cc"]]["cls"] if l["cc"] else None,l["desc"]])
    return out
w("expected/m1_expected_journal.csv",HDR,jrows(M1)); w("expected/m2_expected_journal.csv",HDR,jrows(M2))

def bal(lines):
    out = []
    for j in OrderedDict.fromkeys(l["journal"] for l in lines):
        s = [l for l in lines if l["journal"]==j]; d, c = side_sum(s,"DR"), side_sum(s,"CR")
        out.append([j,s[0]["period"],d,c,d-c,"BALANCED" if d==c else "OUT_OF_BALANCE"])
    return out
w("expected/m1_balance_check.csv",["journal_id","period","total_dr","total_cr","difference","status"],bal(M1)+[["ALL-M1","2026-09",side_sum(M1,"DR"),side_sum(M1,"CR"),side_sum(M1,"DR")-side_sum(M1,"CR"),"BALANCED" if side_sum(M1,"DR")==side_sum(M1,"CR") else "OUT_OF_BALANCE"]])
w("expected/m2_balance_check.csv",["journal_id","period","total_dr","total_cr","difference","status"],bal(M2)+[["ALL-M2","2026-10",side_sum(M2,"DR"),side_sum(M2,"CR"),side_sum(M2,"DR")-side_sum(M2,"CR"),"BALANCED" if side_sum(M2,"DR")==side_sum(M2,"CR") else "OUT_OF_BALANCE"]])

def cc_totals(lines):
    t = defaultdict(lambda: [D(0),D(0)])
    for l in lines:
        if ACCTS[l["code"]][1] != "Expense": continue
        t[(l["cc"] or "(UNCOSTED)", l["code"])][0 if l["side"]=="DR" else 1] += l["amount"]
    rows = [[cc,code,ACCTS[code][0],d,c,d-c] for (cc,code),(d,c) in sorted(t.items())]
    byc = defaultdict(D)
    for cc,code,_,d,c,n in rows: byc[cc] += n
    rows += [[cc,"TOTAL","P&L net by cost centre",None,None,v] for cc,v in sorted(byc.items())]
    rows += [["ALL","TOTAL","P&L net all cost centres",None,None,sum(byc.values(),D(0))]]
    return rows
H2 = ["cost_centre_code","account_code","account_name","debit","credit","net_dr_minus_cr"]
w("expected/m1_totals_by_cost_centre.csv",H2,cc_totals(M1)); w("expected/m2_totals_by_cost_centre.csv",H2,cc_totals(M2))
w("expected/cumulative_totals_by_cost_centre.csv",H2,cc_totals(M1+M2))

def tb(lines):
    t = defaultdict(lambda:[D(0),D(0)])
    for l in lines: t[l["code"]][0 if l["side"]=="DR" else 1] += l["amount"]
    return [[k,ACCTS[k][0],ACCTS[k][1],d,c,d-c] for k,(d,c) in sorted(t.items())]
HT=["account_code","account_name","type","debit","credit","net_dr_minus_cr"]
w("expected/m1_trial_balance.csv",HT,tb(M1)); w("expected/m2_trial_balance.csv",HT,tb(M2)); w("expected/cumulative_trial_balance.csv",HT,tb(M1+M2))

# register export (inputs to the posting) + resolution
def regrows(rows, month):
    return [[r["emp"],r["comp"],r["src"],r["drv"],"Earning" if r["kind"]=="E" else "Deduction","true" if r["employer"] else "false",r["amt"],r["cc"],r["ccsrc"]] for r in rows]
HR=["employee_code","component_code","source","driver_key","kind","is_employer_contribution","amount","resolved_cost_centre","resolution_source"]
w("inputs/09_payroll_register_m1_regular.csv",HR,regrows(reg1,1)); w("inputs/10_payroll_register_m1_offcycle_E009.csv",HR,regrows(oc_rows,1))
w("inputs/11_payroll_register_m2_regular.csv",HR,regrows(reg2,2))
w("inputs/12_other_module_inputs.csv",["item","employee_code","period","value","detail"],[
 ["bonus_batch_B1","E001","2026-09",D(1000),"approved 2026-09; accrual booked 2899.99 in total (A-BON-1)"],
 ["bonus_batch_B1","E002","2026-09",D(1000),""],["bonus_batch_B1","E005","2026-09",D(1000),""],
 ["bonus_accrued_total","","2026-09",BONUS_ACCRUED,"un-accrued remainder 100.01 is expensed by payroll Lock"],
 ["loan_instalment","E002","2026-09/10",D(500),"DED:LOAN -> 2107; loan origination predates M1 and is out of scope"],
 ["advance_issued","E003","2026-09",D(1200),"J-ADV-ISS DR 1410 / CR 1000, both uncosted"],
 ["advance_recovery","E003","2026-10",D(400),"DED:LOAN -> 2107 via component ADV_RECOVERY"],
 ["settlement_wage_base","E009","2026-09",WAGE9,"basic+housing+transport"],
 ["settlement_hire_date","E009","",hire.isoformat(),""],["settlement_last_day","E009","",last.isoformat(),""],
 ["settlement_service_days_inclusive","E009","",days,f"years = {days}/365 = {yrs:.6f}"],
 ["settlement_eosb","E009","2026-09",eosb,f"0.5*{WAGE9}*5 + {WAGE9}*({yrs:.6f}-5), employer termination, Art.84"],
 ["settlement_leave_encashment","E009","2026-09",leave_enc,"7.5 days x wage/30"],
 ["settlement_gross","E009","2026-09",SETT_GROSS,""],["settlement_recovery","E009","2026-09",D(300),"unreturned asset; DED:SETTLEMENT_RECOVERY -> contra 5113"],
 ["settlement_approval_date","E009","2026-09","2026-09-21","CC at approval date = CC-200"],
 ["E009_prorata","E009","2026-09","20/30","each salary component rounded half-up after proration"],
])

# resolution table (M1 and M2 identical)
res_rows = []
for code,e in EMPS.items():
    cc, src = resolve(e, BASE_CTX); res_rows.append([code,e["nat"],e["dept"],e["pos"],e["ovr"],cc,src,e["note"]])
w("expected/cc_resolution_base.csv",["employee_code","nationality","department","position","override_cc","expected_cost_centre","expected_source","note"],res_rows)

# register-to-journal tie-out (per cc), with explained bonus allocation difference
reg_cost = defaultdict(D)
for r in reg1:
    if r["kind"]=="E" or r["employer"]: reg_cost[r["cc"]] += r["amt"]
for r in oc_rows:
    if r["kind"]=="E": pass
oc_cost = defaultdict(D)
oc_cost["CC-200"] += eosb + leave_enc - D(300)
jr = defaultdict(D)
for l in M1:
    if ACCTS[l["code"]][1]=="Expense" and l["cc"]: jr[l["cc"]] += l["amount"] if l["side"]=="DR" else -l["amount"]
tie = []
for c in CCS:
    regv = reg_cost[c] + oc_cost[c]
    diff = jr[c] - regv
    tie.append([c, reg_cost[c], oc_cost[c], regv, jr[c], diff, "bonus LR allocation: CC-110 +0.01, CC-200 0.00, CC-300 -0.01 (net 0) (see allocation_cases.csv)" if diff != 0 else ""])
tie.append(["ALL", sum(reg_cost.values(),D(0)), sum(oc_cost.values(),D(0)), sum(reg_cost.values(),D(0))+sum(oc_cost.values(),D(0)), sum(jr.values(),D(0)), sum(jr.values(),D(0))-sum(reg_cost.values(),D(0))-sum(oc_cost.values(),D(0)), "must be 0.00"])
w("expected/m1_register_to_journal_tieout.csv",["cost_centre","register_gross_plus_employer_gosi","settlement_net_cost","register_total","journal_pnl_net","difference","explanation"],tie)

# void netting
def netting(orig, contra, name):
    t = defaultdict(D)
    for l in orig+contra:
        sgn = 1 if l["side"]=="DR" else -1
        t[(l["cc"] or "(UNCOSTED)", l["code"])] += sgn*l["amount"]
    return [[name,k[0],k[1],ACCTS[k[1]][0],v,"ZERO" if v==0 else "NON_ZERO"] for k,v in sorted(t.items())]
vn = netting(orig_oc, VOID, "void RUN-OC-2026-09 (one employee, E009)")
# hypothetical whole-run void of M1 regular run (+bonus clearing)
reg_orig = [l for l in M1 if l["journal"]=="RUN-REG-2026-09"]
fullvoid = [dict(l, journal="VOID-RUN-REG-2026-09", period="2026-10", event="PayrollVoid", side=("CR" if l["side"]=="DR" else "DR")) for l in reg_orig]
vn += netting(reg_orig, fullvoid, "HYPOTHETICAL whole-run void RUN-REG-2026-09")
w("expected/void_netting.csv",["scenario","cost_centre_code","account_code","account_name","net_dr_minus_cr","status"],vn)
w("expected/m2_void_and_repost_lines.csv",HDR,jrows(VOID+REPOST))
# also whole-run void contra lines for reference
w("expected/hypothetical_full_void_contra_lines.csv",HDR,jrows(fullvoid))

# ---------------------------------------------------------------- allocation vectors
VEC = [
 ("AV1","SAR",10001,[("CC-110",1),("CC-200",1),("CC-300",1)],"T-013: 100.01 over 3 equal parts"),
 ("AV2","KWD (scale 3)",100001,[("CC-110",1),("CC-200",1),("CC-300",1)],"100.001 over 3, fils"),
 ("AV3","SAR",289999,[("CC-110",100000),("CC-200",100000),("CC-300",100000)],"bonus accrual 2899.99 over equal bonuses"),
 ("AV4","SAR",10001,[("CC-110",100000),("CC-200",100000),("CC-300",100000)],"bonus un-accrued remainder 100.01"),
 ("AV5","SAR",66667,[("CC-200",100000),("CC-300",300000)],"666.67 over 1:3 - larger fraction wins"),
 ("AV6","SAR",10001,[("CC-110",0),("CC-200",0)],"all weights zero -> single uncosted part"),
 ("AV7","SAR",-10001,[("CC-110",1),("CC-200",1),("CC-300",1)],"negative total: allocate |x| then re-sign"),
 ("AV8","SAR",1,[("CC-110",5),("CC-1000",5),("CC-200",5)],"tie broken by ordinal code: CC-1000 < CC-110 < CC-200"),
 ("AV9","SAR",1,[("CC-110",0),("CC-200",5),("CC-300",5)],"zero-weight part never receives the spare unit"),
 ("AV10","SAR",100,[("CC-110",1),("CC-200",2)],"exact division, no remainder"),
]
vrows = []
for vid, cur, tot, parts, note in VEC:
    sc = 3 if "KWD" in cur else 2
    res, det = allocate(tot, parts)
    for c in (p[0] for p in parts) if None not in res else [None]:
        amt = res[c]
        d = next((x for x in det if x[0]==c), None)
        vrows.append([vid,cur,tot,c,(d[1] if d else None),(d[2] if d else None),(f"{d[3]}" if d else None),("yes" if d and d[4] else "no"),amt,f"{D(amt)/D(10**sc):.{sc}f}",note])
w("expected/allocation_vectors.csv",["vector","currency","total_minor_units","cost_centre_code","weight_minor","floor_share_minor","fractional_remainder","gets_extra_unit","allocated_minor","allocated_amount","note"],vrows)
arows = []
for case,tot,det in ALLOC_LOG:
    for c,wt,fl,fr,gets in det: arows.append([case,tot,c,wt,fl,str(fr),"yes" if gets else "no",fl+(1 if gets else 0),f"{D(fl+(1 if gets else 0))/100:.2f}"])
w("expected/allocation_cases.csv",["case","total_minor","cost_centre_code","weight_minor","floor_minor","fractional_remainder","gets_extra_minor","final_minor","final_amount"],arows)

# ---------------------------------------------------------------- lock blockers
MSG1 = "{n} employee(s) have no cost centre, and account {code} {name} requires one. Assign a cost centre to each employee, their position, their department, or set a company default cost centre, then lock again."
MSG2 = "The cost centre set at the {lvl} level is inactive, deleted or belongs to another company, so cost cannot be assigned. Fix it in Setup, then lock again."
MSG4 = "A rounding difference of {amt} {cur} must be posted to account {code}, which requires a cost centre. Set a default cost centre for the company, then lock again."
BL = []
def costable_rows(rows, ctx):
    return [r for r in rows if (r["kind"]=="E" or r["employer"]) and r["src"]!="Bonus"]
def blockers(scn, ctx, rules, desc):
    rows = register(1, ctx); unres = defaultdict(lambda: [set(), D(0)]); inval = set()
    for r in rows:
        if r["cc"] is None:
            code = DRV["EMPLOYER_STATUTORY_EXPENSE"] if r["employer"] else DRV[r["drv"]]
            if r["kind"]=="D" and not r["employer"]: continue
            if r["src"]=="Bonus": code = "6100"
            if rules.get(code, ACCTS[code][2]) == "Required":
                if str(r["ccsrc"]).endswith("_invalid"): inval.add((r["ccsrc"], r["emp"]))
                else: unres[code][0].add(r["emp"]); unres[code][1] += r["amt"]
    for lvlkey in sorted({s_ for s_,_ in inval}):
        emps_ = sorted({e for s_,e in inval if s_==lvlkey})
        BL.append([scn,"cost_centre_invalid",422,"M-2",None,len(emps_),";".join(emps_),None,MSG2.format(lvl=lvlkey.replace("_cost_centre_invalid","")),desc])
    for code in sorted(unres):
        es = sorted(unres[code][0])
        BL.append([scn,"cost_centre_required",422,"M-1",code,len(es),";".join(es[:10]),unres[code][1],MSG1.format(n=len(es),code=code,name=ACCTS[code][0]),desc])
    if not inval and not unres: BL.append([scn,"(none - Lock succeeds)",200,"",None,0,"",None,"",desc])
# (a1) default cleared, E004 unresolved
cx = dict(BASE_CTX, default=None); blockers("S-A1",cx,{},"costing ON, no company default, E004 (dept D-OPS + position P-TECH have no CC) unresolved; E010 resolves via parent dept")
# (a2) also break D-SUP parent link -> E004 + E010 unresolved
cx2 = dict(BASE_CTX, default=None, depts=dict(DEPTS, **{"D-SUP":dict(DEPTS["D-SUP"], parent=None)})); blockers("S-A2",cx2,{},"as S-A1 and D-SUP loses its parent: E004 and E010 unresolved")
# (a3) Optional rules, unresolved allowed
opt = {c:"Optional" for c in ACCTS}; blockers("S-A3",cx,opt,"all accounts Optional: no blocker; warning W-1 (1 employee: E004) and E004's lines post with NO cost centre")
# (a4) invalid CC at position level - hard error not fall-through
ccs_bad = {k:dict(v) for k,v in CCS.items()}; ccs_bad["CC-300"]["active"]=False
blockers("S-A4",dict(BASE_CTX,ccs=ccs_bad),{},"CC-300 deactivated: E005 (position level) and E007 (employee override) hard-fail; company default NOT used as fall-through. E007's level is Employee")
# (b1) rounding
BL.append(["S-B1","cost_centre_rounding_required",422,"M-4","5199",0,"",D("0.01"),MSG4.format(amt="0.01",cur="SAR",code="5199"),
  "fixture: stored slip nets sum 0.01 below component sums (drift); ROUNDING line must be posted to 5199 (Required) and company has NO default cost centre. With default CC-100 the line posts DR 5199 0.01 to CC-100 and Lock succeeds"])
# (b2) fixed-CC mapping onto a NotAllowed account: spec rejects at SAVE time, nothing persisted, no journal change
BL.append(["S-B2","cost_centre_not_allowed",422,"M-3","5005",0,"",None,"Account 5005 Overtime Expense does not accept a cost centre. Remove the cost centre from this posting rule or change the account's cost centre rule.",
  "SAVE-TIME: account 5005 rule = NotAllowed and a mapping (EARN:OVERTIME, fixed cost centre CC-200) is saved -> 422 M-3, mapping NOT persisted, M1/M2 journals unchanged. Same code from the write-time interceptor if a costed line is ever inserted on a NotAllowed account"])
# (b3) Required with unresolved employee in bonus remainder path (6100)
BL.append(["S-B3","cost_centre_required",422,"M-1","6100",1,"E001",D("1000.00"),MSG1.format(n=1,code="6100",name=ACCTS["6100"][0]),
  "E001 (bonus holder) unresolvable (dept D-FIN cc removed, no default): un-accrued bonus remainder and bonus accrual both need a cc; the accrual at approval fails with the same code (interceptor) - the weight set for LR is never built with a null cc"])
w("expected/lock_blockers.csv",["scenario","error_code","http","message_ref","account_code","n_employees","employee_codes(max10)","aggregate_amount","message_en","scenario_description"],BL)

# ---------------------------------------------------------------- headline numbers for README
if __name__ == "__main__":
    print("eosb", eosb, "leave", leave_enc, "gross", SETT_GROSS, "days", days, "years", yrs)
    for t in ("M1","M2"):
        L_ = M1 if t=="M1" else M2
        print(t, "DR", side_sum(L_,"DR"), "CR", side_sum(L_,"CR"))
    for r in cc_totals(M1)[-6:]: print("M1", r)
    for r in cc_totals(M2)[-6:]: print("M2", r)
    print("void nonzero:", [r for r in vn if r[-1]!="ZERO"][:3])
    print([r for r in tie])
