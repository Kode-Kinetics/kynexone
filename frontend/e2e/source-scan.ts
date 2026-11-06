import fs from 'node:fs';
import path from 'node:path';
import * as ts from 'typescript';

/** Shared by the browserless source-scan specs (route-wiring, dead-controls). Pure Node, no browser. */
export const FRONTEND_ROOT = process.cwd();
export const REPO_ROOT = path.resolve(FRONTEND_ROOT, '..');

export function walkSource(dirs: string[] = ['app', 'src']): string[] {
  const out: string[] = [];
  const visit = (dir: string) => {
    for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
      if (entry.name === 'node_modules' || entry.name === '.next' || entry.name === 'dist') continue;
      const full = path.join(dir, entry.name);
      if (entry.isDirectory()) visit(full);
      else if (/\.tsx?$/.test(entry.name) && !/\.(spec|test)\.tsx?$/.test(entry.name) && !entry.name.endsWith('.d.ts')) out.push(full);
    }
  };
  for (const d of dirs) visit(path.join(FRONTEND_ROOT, d));
  return out.sort();
}

export function parseSource(file: string): ts.SourceFile {
  const text = fs.readFileSync(file, 'utf8');
  return ts.createSourceFile(file, text, ts.ScriptTarget.Latest, true, file.endsWith('x') ? ts.ScriptKind.TSX : ts.ScriptKind.TS);
}

export const rel = (file: string) => path.relative(FRONTEND_ROOT, file).replaceAll('\\', '/');
export const lineOf = (sf: ts.SourceFile, node: ts.Node) => sf.getLineAndCharacterOfPosition(node.getStart(sf)).line + 1;

/** Placeholder for a `${...}` hole in a template literal. */
export const HOLE = '\u0000';

/** The literal text(s) an expression can evaluate to, with holes for interpolations. Conditionals yield both branches. */
export function literalsOf(expr: ts.Expression | undefined): string[] {
  if (!expr) return [];
  if (ts.isParenthesizedExpression(expr) || ts.isAsExpression(expr) || ts.isNonNullExpression(expr) || ts.isSatisfiesExpression(expr)) return literalsOf(expr.expression);
  if (ts.isStringLiteral(expr) || ts.isNoSubstitutionTemplateLiteral(expr)) return [expr.text];
  if (ts.isTemplateExpression(expr)) {
    let text = expr.head.text;
    for (const span of expr.templateSpans) text += HOLE + span.literal.text;
    return [text];
  }
  if (ts.isConditionalExpression(expr)) return [...literalsOf(expr.whenTrue), ...literalsOf(expr.whenFalse)];
  if (ts.isBinaryExpression(expr) && expr.operatorToken.kind === ts.SyntaxKind.PlusToken) {
    const left = literalsOf(expr.left);
    const right = literalsOf(expr.right);
    if (left.length && right.length) return left.flatMap(l => right.map(r => l + r));
    if (left.length) return left.map(l => l + HOLE);
  }
  return [];
}
