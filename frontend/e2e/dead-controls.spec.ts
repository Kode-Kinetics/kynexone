import { expect, test } from '@playwright/test';
import * as ts from 'typescript';
import { lineOf, parseSource, rel, walkSource } from './source-scan';

/**
 * Dead controls (Screen Standard section 8c). A control that looks interactive and does nothing is
 * a lie to the user. Hard zero, scanned with the TypeScript AST over app/ and src/:
 *
 *   onClick={() => {}}   onClick={noop}   href="#"   href=""   href="javascript:..."
 *   <button> that does nothing: no onClick, not a submit (type=submit, or untyped inside a <form>), not disabled
 *   "coming soon" / "not available yet" / lorem ipsum / TODO in JSX text or a string literal
 *
 * ALLOWED: entries below, each with the reason it is not a dead control.
 */

const ALLOWED: Record<string, string> = {
  'src/components/platform/ComingSoon.tsx': 'Platform-console placeholder component that is not imported by any page (audit 2026-11-06); it says what is missing by design.',
};

/** Copy that says "not available yet" because it states WHY and what to do instead (a CapabilityNote), not a promise. */
const ALLOWED_TEXT: Record<string, string> = {
  'Delegations are not available yet.': 'CapabilityNote on a section whose capability is off: it says nothing reads a delegation and tells the user to change the workflow approver instead.',
  'Approval limits are not available yet.': 'CapabilityNote on a section whose capability is off: it says why and names the working alternative.',
};

const PLACEHOLDER_TEXT = /\b(?:coming soon|not available yet|lorem ipsum)\b|\bTODO\b/i;
// "TODO" is matched in upper case only: a lower-case todo is a status value (companySetup).
const isPlaceholder = (text: string) => !ALLOWED_TEXT[text.trim()] && PLACEHOLDER_TEXT.test(text) && (!/\btodo\b/i.test(text) || /\bTODO\b/.test(text) || /coming soon|not available yet|lorem ipsum/i.test(text));
const NOOP_NAMES = new Set(['noop', 'NOOP', 'emptyFn']);

interface Finding { where: string; what: string }

function scan(): Finding[] {
  const out: Finding[] = [];
  for (const file of walkSource()) {
    const path = rel(file);
    if (ALLOWED[path]) continue;
    const sf = parseSource(file);
    const flag = (node: ts.Node, what: string) => out.push({ where: `${path}:${lineOf(sf, node)}`, what });
    const insideForm = (node: ts.Node) => {
      for (let p: ts.Node | undefined = node.parent; p; p = p.parent) {
        if (ts.isJsxElement(p) && p.openingElement.tagName.getText(sf) === 'form') return true;
      }
      return false;
    };
    const visit = (node: ts.Node) => {
      if (ts.isJsxAttribute(node) && ts.isIdentifier(node.name)) {
        const name = node.name.text;
        const init = node.initializer;
        if (name === 'onClick' && init && ts.isJsxExpression(init) && init.expression) {
          const e = init.expression;
          if ((ts.isArrowFunction(e) || ts.isFunctionExpression(e)) && ts.isBlock(e.body) && e.body.statements.length === 0) flag(node, 'onClick is an empty function');
          if (ts.isArrowFunction(e) && !ts.isBlock(e.body) && ts.isVoidExpression(e.body) && ts.isNumericLiteral(e.body.expression)) flag(node, 'onClick is void 0');
          if (ts.isIdentifier(e) && NOOP_NAMES.has(e.text)) flag(node, `onClick={${e.text}}`);
        }
        if (name === 'href') {
          const value = init && ts.isStringLiteral(init) ? init.text
            : init && ts.isJsxExpression(init) && init.expression && (ts.isStringLiteral(init.expression) || ts.isNoSubstitutionTemplateLiteral(init.expression)) ? init.expression.text : null;
          if (value !== null && (value === '#' || value.trim() === '' || /^javascript:/i.test(value))) flag(node, `href="${value}"`);
        }
      }
      if (ts.isJsxOpeningElement(node) || ts.isJsxSelfClosingElement(node)) {
        if (node.tagName.getText(sf) === 'button') {
          const attrs = node.attributes.properties;
          const has = (n: string) => attrs.some(a => ts.isJsxAttribute(a) && ts.isIdentifier(a.name) && a.name.text === n);
          const typeValue = attrs.flatMap(a => (ts.isJsxAttribute(a) && ts.isIdentifier(a.name) && a.name.text === 'type' && a.initializer && ts.isStringLiteral(a.initializer) ? [a.initializer.text] : []))[0];
          const spread = attrs.some(a => ts.isJsxSpreadAttribute(a));
          const submits = typeValue === 'submit' || typeValue === 'reset' || has('form') || (typeValue === undefined && !has('type') && insideForm(node));
          const inert = has('disabled') || has('aria-disabled');
          const handled = ['onClick', 'formAction', 'onMouseDown', 'onPointerDown', 'onKeyDown'].some(has);
          if (!spread && !submits && !inert && !handled) flag(node, '<button> that does nothing: no onClick, not a submit, not disabled');
        }
      }
      if (ts.isJsxText(node) && isPlaceholder(node.text)) flag(node, `placeholder text "${node.text.trim().slice(0, 40)}"`);
      if ((ts.isStringLiteral(node) || ts.isNoSubstitutionTemplateLiteral(node)) && isPlaceholder(node.text) && !ts.isImportDeclaration(node.parent)) {
        flag(node, `placeholder text "${node.text.slice(0, 40)}"`);
      }
      ts.forEachChild(node, visit);
    };
    visit(sf);
  }
  return out;
}

test('no dead controls in app/ or src/ (hard zero)', () => {
  const found = scan().map(f => `${f.where}  ${f.what}`);
  expect(found, 'a control that does nothing, or copy that promises a feature that is not there').toEqual([]);
});

test('every allow-list entry is a real file and still needed', () => {
  for (const file of Object.keys(ALLOWED)) {
    expect(walkSource().map(rel), file).toContain(file);
    expect(ALLOWED[file].length, `${file} needs a real reason`).toBeGreaterThan(30);
  }
});
