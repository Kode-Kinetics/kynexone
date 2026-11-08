import fs from 'node:fs';
import path from 'node:path';
import { createRequire } from 'node:module';
import * as ts from 'typescript';
import { createElement, type ComponentType } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';

/**
 * Server-render a project component to static markup inside the unit config (no browser, no bundler).
 *
 * Why this exists: Playwright's own TSX transform rewrites JSX to its component-testing runtime, so a .tsx
 * component cannot be rendered by importing it in a spec. Here every project .ts/.tsx module is transpiled with the
 * TypeScript compiler (automatic JSX runtime, CommonJS) and loaded by a tiny loader; packages (react, lucide-react,
 * next/link) resolve through Node. `@/` and relative imports resolve inside the frontend. Effects do not run and a
 * portal cannot render on the server, so this proves markup (roles, attributes, text), not behaviour.
 */
const root = path.resolve(__dirname, '..', '..');
const nodeRequire = createRequire(path.join(root, 'package.json'));
const cache = new Map<string, unknown>();

const EXTENSIONS = ['.tsx', '.ts', '/index.tsx', '/index.ts'];
function resolveProject(from: string, request: string): string | null {
  const base = request.startsWith('@/') ? path.join(root, request.slice(2)) : request.startsWith('.') ? path.resolve(path.dirname(from), request) : null;
  if (!base) return null;
  if (fs.existsSync(base) && fs.statSync(base).isFile()) return base;
  for (const ext of EXTENSIONS) if (fs.existsSync(base + ext)) return base + ext;
  return null;
}

function load(file: string): Record<string, unknown> {
  const hit = cache.get(file);
  if (hit) return hit as Record<string, unknown>;
  const source = fs.readFileSync(file, 'utf8');
  const { outputText } = ts.transpileModule(source, {
    fileName: file,
    compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2020, jsx: ts.JsxEmit.ReactJSX, esModuleInterop: true },
  });
  const module = { exports: {} as Record<string, unknown> };
  cache.set(file, module.exports);
  const localRequire = (request: string): unknown => {
    const project = resolveProject(file, request);
    if (project) return /\.tsx?$/.test(project) ? load(project) : nodeRequire(project);
    return nodeRequire(request);
  };
  new Function('exports', 'require', 'module', '__filename', '__dirname', outputText)(module.exports, localRequire, module, file, path.dirname(file));
  cache.set(file, module.exports);
  return module.exports;
}

/** `renderComponent('src/components/ui/ResultBanner.tsx', 'ResultBanner', { changed: 'Saved' })` */
export function renderComponent(file: string, exportName: string, props: Record<string, unknown> = {}): string {
  const exports = load(path.join(root, file));
  const component = exports[exportName] as ComponentType<Record<string, unknown>> | undefined;
  if (!component) throw new Error(`${file} does not export ${exportName}`);
  return renderToStaticMarkup(createElement(component, props));
}
