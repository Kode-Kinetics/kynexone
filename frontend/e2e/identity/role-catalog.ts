import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';

/**
 * The tenant role catalog, read from the backend's own source of truth instead of a copy.
 *
 * Every tenant gets its roles from `AuthSeeder.EnsureTenantRolesAsync`, and the permission catalog
 * from `AuthSeeder.EnsurePermissions`. A role matrix written out by hand in the test tree drifts the
 * moment either changes, and a drifted "must lack" list passes vacuously. So the expected matrix is
 * GENERATED here by reading AuthSeeder.cs, and the security gate then asserts that the running API
 * serves exactly this catalog (so a stale build or a stale database is caught too).
 *
 * The parser understands the three bundle shapes AuthSeeder uses today:
 *   • `permissions`                                   — every permission (Admin);
 *   • `Ps(new[] { "a", "b", ... })`                   — an explicit list;
 *   • `permissions.Where(x => x.Key.StartsWith("p.") || ... || x.Key is "a" or "b").ToList()`.
 * Anything else throws with the role's name, so a refactor of AuthSeeder fails here loudly instead of
 * producing an empty or partial matrix.
 */

// __dirname, not import.meta.url: Playwright transpiles the e2e tree to CommonJS.
export const AUTH_SEEDER_PATH = resolve(
  __dirname, '..', '..', '..', 'backend-dotnet', 'Zayra.Api', 'Infrastructure', 'Seed', 'AuthSeeder.cs',
);

export interface RoleDefinition {
  name: string;
  authorityLevel: number;
  /** Resolved permission keys, sorted. */
  permissions: string[];
  /** Keys a `Ps(...)` list names that are NOT in the catalog. `Ps` drops them silently at runtime. */
  unknownKeys: string[];
}

export interface RoleCatalog {
  /** Every permission key AuthSeeder.EnsurePermissions defines, sorted. */
  permissions: string[];
  roles: RoleDefinition[];
}

/** Remove `//` comments outside string literals, keeping line structure. */
function stripLineComments(source: string): string {
  let out = '';
  let inString = false;
  for (let i = 0; i < source.length; i++) {
    const ch = source[i];
    if (inString) {
      out += ch;
      if (ch === '\\') { out += source[++i] ?? ''; continue; }
      if (ch === '"') inString = false;
      continue;
    }
    if (ch === '"') { inString = true; out += ch; continue; }
    if (ch === '/' && source[i + 1] === '/') {
      while (i < source.length && source[i] !== '\n') i++;
      out += '\n';
      continue;
    }
    out += ch;
  }
  return out;
}

/** The text between the parenthesis at `open` and its match, honouring string literals. */
function balanced(source: string, open: number): { inner: string; end: number } {
  let depth = 0;
  let inString = false;
  for (let i = open; i < source.length; i++) {
    const ch = source[i];
    if (inString) {
      if (ch === '\\') { i++; continue; }
      if (ch === '"') inString = false;
      continue;
    }
    if (ch === '"') { inString = true; continue; }
    if (ch === '(' || ch === '{' || ch === '[') depth++;
    if (ch === ')' || ch === '}' || ch === ']') {
      depth--;
      if (depth === 0) return { inner: source.slice(open + 1, i), end: i };
    }
  }
  throw new Error(`[role-catalog] Unbalanced brackets from offset ${open} in AuthSeeder.cs.`);
}

/** Split on commas at nesting depth 0, honouring string literals. */
function splitTopLevel(args: string): string[] {
  const parts: string[] = [];
  let depth = 0;
  let inString = false;
  let start = 0;
  for (let i = 0; i < args.length; i++) {
    const ch = args[i];
    if (inString) {
      if (ch === '\\') { i++; continue; }
      if (ch === '"') inString = false;
      continue;
    }
    if (ch === '"') { inString = true; continue; }
    if (ch === '(' || ch === '{' || ch === '[') depth++;
    else if (ch === ')' || ch === '}' || ch === ']') depth--;
    else if (ch === ',' && depth === 0) { parts.push(args.slice(start, i).trim()); start = i + 1; }
  }
  parts.push(args.slice(start).trim());
  return parts;
}

const literals = (text: string): string[] => [...text.matchAll(/"((?:[^"\\]|\\.)*)"/g)].map((m) => m[1]);

function methodBody(source: string, signature: RegExp, what: string): string {
  const match = signature.exec(source);
  if (!match) throw new Error(`[role-catalog] Could not find ${what} in AuthSeeder.cs.`);
  const open = source.indexOf('{', match.index + match[0].length);
  return balanced(source, open).inner;
}

function parsePermissionKeys(source: string): string[] {
  const body = methodBody(source, /Task<List<Permission>>\s+EnsurePermissions\s*\(/, 'EnsurePermissions');
  const start = body.search(/var\s+definitions\s*=\s*new\s*\(/);
  if (start < 0) throw new Error('[role-catalog] EnsurePermissions no longer declares `var definitions = new (...)[] {...}`.');
  const open = body.indexOf('{', start);
  const block = balanced(body, open).inner;
  const keys = [...block.matchAll(/\(\s*"([^"]+)"\s*,\s*"[^"]*"\s*,\s*"(?:[^"\\]|\\.)*"\s*\)/g)].map((m) => m[1]);
  if (keys.length === 0) throw new Error('[role-catalog] EnsurePermissions defines no permission tuples the parser recognises.');
  const duplicates = keys.filter((k, i) => keys.indexOf(k) !== i);
  if (duplicates.length) throw new Error(`[role-catalog] Duplicate permission keys: ${duplicates.join(', ')}`);
  return [...keys].sort();
}

/** Evaluate `x.Key.StartsWith("a") || ... || x.Key is "b" or "c"` — and nothing else. */
function evaluatePredicate(role: string, predicate: string, catalog: string[]): { keys: string[]; unknown: string[] } {
  const prefixes = [...predicate.matchAll(/x\.Key\.StartsWith\(\s*"([^"]+)"\s*\)/g)].map((m) => m[1]);
  let exact: string[] = [];
  const isClause = /x\.Key\s+is\s+((?:"[^"]+"\s*(?:or\s*)?)+)/.exec(predicate);
  if (isClause) exact = literals(isClause[1]);

  // Everything the parser did not account for must be operators and whitespace. Anything left over is
  // a shape this file does not understand, and guessing would produce a wrong matrix.
  const residue = predicate
    .replace(/x\.Key\.StartsWith\(\s*"[^"]+"\s*\)/g, '')
    .replace(/x\.Key\s+is\s+(?:"[^"]+"\s*(?:or\s*)?)+/g, '')
    .replace(/\|\||[\s()]/g, '');
  if (residue.length) {
    throw new Error(
      `[role-catalog] Role '${role}': unrecognised predicate fragment '${residue.slice(0, 80)}'. `
      + 'Teach e2e/identity/role-catalog.ts the new shape rather than hand-copying the role.',
    );
  }
  return {
    keys: catalog.filter((key) => prefixes.some((p) => key.startsWith(p)) || exact.includes(key)),
    unknown: exact.filter((key) => !catalog.includes(key)),
  };
}

function resolveBundle(role: string, expression: string, catalog: string[]): { keys: string[]; unknown: string[] } {
  const expr = expression.replace(/\s+/g, ' ').trim();
  if (expr === 'permissions') return { keys: [...catalog], unknown: [] };

  const ps = /^Ps\(\s*new\s*\[\s*\]\s*\{([\s\S]*)\}\s*\)$/.exec(expr);
  if (ps) {
    const named = literals(ps[1]);
    return {
      keys: catalog.filter((key) => named.includes(key)),
      unknown: named.filter((key) => !catalog.includes(key)),
    };
  }

  const where = /^permissions\.Where\(\s*x\s*=>([\s\S]*)\)\s*\.ToList\(\)$/.exec(expr);
  if (where) return evaluatePredicate(role, where[1], catalog);

  throw new Error(
    `[role-catalog] Role '${role}': unrecognised bundle expression '${expr.slice(0, 120)}'. `
    + 'Teach e2e/identity/role-catalog.ts the new shape rather than hand-copying the role.',
  );
}

/** Parse AuthSeeder.cs source text into the catalog. Exported separately so it can be unit-tested. */
export function parseAuthSeeder(sourceText: string): RoleCatalog {
  const source = stripLineComments(sourceText);
  const permissions = parsePermissionKeys(source);
  const body = methodBody(source, /Task<Role>\s+EnsureTenantRolesAsync\s*\(/, 'EnsureTenantRolesAsync');

  const roles: RoleDefinition[] = [];
  const call = /EnsureRole\s*\(/g;
  let match: RegExpExecArray | null;
  while ((match = call.exec(body)) !== null) {
    const open = match.index + match[0].length - 1;
    const { inner, end } = balanced(body, open);
    call.lastIndex = end;
    // EnsureRole(tenantId, "Name", "Description", <bundle>, <authorityLevel>, <isEditable>, ct)
    const args = splitTopLevel(inner);
    if (args.length < 5) throw new Error(`[role-catalog] EnsureRole call with ${args.length} arguments: ${inner.slice(0, 80)}`);
    const name = literals(args[1])[0];
    if (!name) throw new Error(`[role-catalog] EnsureRole call without a literal role name: ${args[1]}`);
    const authorityLevel = Number(args[4]);
    if (!Number.isInteger(authorityLevel)) {
      throw new Error(`[role-catalog] Role '${name}': authority level '${args[4]}' is not a literal integer.`);
    }
    const { keys, unknown } = resolveBundle(name, args[3], permissions);
    roles.push({ name, authorityLevel, permissions: [...keys].sort(), unknownKeys: unknown });
  }
  if (roles.length === 0) throw new Error('[role-catalog] EnsureTenantRolesAsync installs no roles the parser recognises.');
  return { permissions, roles };
}

let cached: RoleCatalog | null = null;

/** The catalog as the checked-out backend source defines it. */
export function loadRoleCatalog(path: string = AUTH_SEEDER_PATH): RoleCatalog {
  if (path === AUTH_SEEDER_PATH && cached) return cached;
  let text: string;
  try {
    text = readFileSync(path, 'utf8');
  } catch (error) {
    throw new Error(
      `[role-catalog] Cannot read ${path}: ${error instanceof Error ? error.message : String(error)}.\n`
      + 'The role matrix is generated from the backend source, so the e2e tree must run from a full checkout.',
    );
  }
  const catalog = parseAuthSeeder(text);
  if (path === AUTH_SEEDER_PATH) cached = catalog;
  return catalog;
}

export function roleDefinition(catalog: RoleCatalog, name: string): RoleDefinition {
  const found = catalog.roles.find((r) => r.name === name);
  if (!found) {
    throw new Error(
      `[role-catalog] AuthSeeder installs no role named '${name}'. Installed: ${catalog.roles.map((r) => r.name).join(', ')}`,
    );
  }
  return found;
}

/** The permissions a user holding exactly these roles resolves to (union), sorted. */
export function expectedPermissions(catalog: RoleCatalog, roleNames: string[]): string[] {
  const union = new Set<string>();
  for (const name of roleNames) for (const key of roleDefinition(catalog, name).permissions) union.add(key);
  return [...union].sort();
}
