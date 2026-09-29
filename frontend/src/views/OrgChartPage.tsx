'use client';

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { ChevronDown, ChevronRight, RefreshCw, Search, Users, List, GitBranch, LayoutGrid, Maximize2 } from 'lucide-react';
import { employeesApi } from '../api/employees';
import type { OrgChartNodeDto } from '../api/employees';
import { Avatar } from '../components/Avatar';

type ViewMode = 'list' | 'tree' | 'cards';

// ── Tree layout constants ──────────────────────────────────────────────────────
const NODE_W = 216;
const NODE_H = 80;
const H_GAP  = 32;
const V_GAP  = 60;

interface PositionedNode {
  node: OrgChartNodeDto;
  x: number;
  y: number;
  subtreeWidth: number;
  children: PositionedNode[];
}

function buildLayout(node: OrgChartNodeDto): PositionedNode {
  if (node.directReports.length === 0)
    return { node, x: 0, y: 0, subtreeWidth: NODE_W, children: [] };
  const children = node.directReports.map(buildLayout);
  const totalW = children.reduce((s, c) => s + c.subtreeWidth, 0) + (children.length - 1) * H_GAP;
  return { node, x: 0, y: 0, subtreeWidth: Math.max(NODE_W, totalW), children };
}

function assignXY(p: PositionedNode, px: number, py: number) {
  p.x = px; p.y = py;
  if (!p.children.length) return;
  const totalW = p.children.reduce((s, c) => s + c.subtreeWidth, 0) + (p.children.length - 1) * H_GAP;
  let cx = px + NODE_W / 2 - totalW / 2;
  for (const c of p.children) { assignXY(c, cx, py + NODE_H + V_GAP); cx += c.subtreeWidth + H_GAP; }
}

function flatten(p: PositionedNode): PositionedNode[] {
  return [p, ...p.children.flatMap(flatten)];
}

interface Edge { px: number; py: number; cx: number; cy: number; }
function edges(p: PositionedNode): Edge[] {
  return [
    ...p.children.map(c => ({ px: p.x + NODE_W / 2, py: p.y + NODE_H, cx: c.x + NODE_W / 2, cy: c.y })),
    ...p.children.flatMap(edges),
  ];
}

function flattenTree(nodes: OrgChartNodeDto[]): OrgChartNodeDto[] {
  return nodes.flatMap(n => [n, ...flattenTree(n.directReports)]);
}

// ── Main page ─────────────────────────────────────────────────────────────────

export function OrgChartPage() {
  const [tree, setTree]       = useState<OrgChartNodeDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError]     = useState<string | null>(null);
  const [search, setSearch]   = useState('');
  const [view, setView]       = useState<ViewMode>('list');
  const [collapsed, setCollapsed] = useState<Set<number>>(new Set());

  const load = useCallback(async () => {
    setLoading(true); setError(null);
    try { setTree(await employeesApi.orgChart(undefined, 8)); }
    catch { setError('Failed to load org chart.'); }
    finally { setLoading(false); }
  }, []);
  useEffect(() => { load(); }, [load]);

  const toggle = (id: number) =>
    setCollapsed(prev => { const n = new Set(prev); n.has(id) ? n.delete(id) : n.add(id); return n; });

  const matchesSearch = useCallback((node: OrgChartNodeDto): boolean => {
    if (!search) return true;
    const q = search.toLowerCase();
    return node.fullName.toLowerCase().includes(q) ||
      node.employeeCode.toLowerCase().includes(q) ||
      node.designation.toLowerCase().includes(q) ||
      node.department.toLowerCase().includes(q) ||
      node.directReports.some(matchesSearch);
  }, [search]);

  return (
    <div className="p-6 max-w-full mx-auto">
      {/* Header */}
      <div className="flex flex-wrap items-center justify-between gap-3 mb-6">
        <div className="flex items-center gap-3">
          <Users className="w-6 h-6 text-sapphire shrink-0" />
          <div>
            <h1 className="text-xl font-semibold text-slate-900 dark:text-white">Org Chart</h1>
            <p className="text-sm text-slate-500">Reporting hierarchy</p>
          </div>
        </div>
        <div className="flex items-center gap-2 flex-wrap">
          {/* View toggle */}
          <div className="flex items-center rounded-lg border border-slate-200 dark:border-white/[0.1] p-0.5 bg-slate-50 dark:bg-white/[0.03]">
            {([['list', 'List', List], ['tree', 'Tree', GitBranch], ['cards', 'Cards', LayoutGrid]] as [ViewMode, string, React.ElementType][]).map(([id, label, Icon]) => (
              <button key={id} type="button" onClick={() => setView(id)}
                className={`flex items-center gap-1.5 px-3 py-1.5 rounded-md text-sm font-medium transition-colors ${view === id ? 'bg-white dark:bg-white/[0.08] text-sapphire shadow-sm' : 'text-slate-500 hover:text-slate-700 dark:hover:text-slate-300'}`}>
                <Icon className="w-3.5 h-3.5" />{label}
              </button>
            ))}
          </div>
          {/* Search */}
          <div className="relative">
            <Search className="w-4 h-4 absolute start-2.5 top-1/2 -translate-y-1/2 text-slate-400" />
            <input type="text" placeholder="Search people…" value={search} onChange={e => setSearch(e.target.value)}
              className="ps-8 pe-3 py-1.5 text-sm border border-slate-200 dark:border-white/[0.1] rounded-lg bg-white dark:bg-white/[0.05] text-slate-900 dark:text-white focus:outline-none focus:ring-2 focus:ring-sapphire/30 w-48" />
          </div>
          <button type="button" onClick={load}
            className="flex items-center gap-1.5 text-sm text-slate-500 hover:text-slate-700 dark:hover:text-slate-300 px-3 py-1.5 border border-slate-200 dark:border-white/[0.1] rounded-lg hover:bg-slate-50 dark:hover:bg-white/[0.05] transition-colors">
            <RefreshCw className="w-3.5 h-3.5" /> Refresh
          </button>
        </div>
      </div>

      {error && <div className="bg-red-50 dark:bg-red-900/20 border border-red-200 dark:border-red-800 text-red-700 dark:text-red-300 rounded-lg px-4 py-3 text-sm mb-4">{error}</div>}

      {loading ? (
        <div className="flex items-center justify-center py-24 text-slate-400 text-sm">Loading org chart…</div>
      ) : tree.length === 0 ? (
        <div className="flex flex-col items-center justify-center py-24 text-slate-400">
          <Users className="w-10 h-10 mb-3 opacity-40" />
          <p className="text-sm">No employees found. Import employees to populate the org chart.</p>
        </div>
      ) : view === 'list' ? (
        <ListView tree={tree} search={search} collapsed={collapsed} toggle={toggle} matchesSearch={matchesSearch} />
      ) : view === 'tree' ? (
        <TreeView tree={tree} search={search} />
      ) : (
        <CardsView tree={tree} search={search} />
      )}
    </div>
  );
}

// ── List view (original) ──────────────────────────────────────────────────────

function ListView({ tree, search, collapsed, toggle, matchesSearch }: {
  tree: OrgChartNodeDto[];
  search: string;
  collapsed: Set<number>;
  toggle: (id: number) => void;
  matchesSearch: (n: OrgChartNodeDto) => boolean;
}) {
  return (
    <div className="space-y-1">
      {tree.filter(matchesSearch).map(node => (
        <OrgNode key={node.id} node={node} collapsed={collapsed} toggle={toggle} search={search} matchesSearch={matchesSearch} depth={0} />
      ))}
    </div>
  );
}

function OrgNode({ node, collapsed, toggle, search, matchesSearch, depth }: {
  node: OrgChartNodeDto; collapsed: Set<number>; toggle: (id: number) => void;
  search: string; matchesSearch: (n: OrgChartNodeDto) => boolean; depth: number;
}) {
  const isCollapsed = collapsed.has(node.id);
  const hasReports  = node.directReports.length > 0;
  const visible     = node.directReports.filter(matchesSearch);

  const hi = (text: string) => {
    if (!search) return <>{text}</>;
    const idx = text.toLowerCase().indexOf(search.toLowerCase());
    if (idx === -1) return <>{text}</>;
    return <>{text.slice(0, idx)}<mark className="bg-yellow-100 text-yellow-900 rounded px-0.5">{text.slice(idx, idx + search.length)}</mark>{text.slice(idx + search.length)}</>;
  };

  return (
    <div className={depth > 0 ? 'ps-6' : ''}>
      <div className="flex items-center gap-3 rounded-lg px-3 py-2 hover:bg-slate-50 dark:hover:bg-white/[0.03] cursor-pointer group transition-colors"
        onClick={() => hasReports && toggle(node.id)}>
        <div className="w-5 shrink-0">
          {hasReports ? (isCollapsed ? <ChevronRight className="w-4 h-4 text-slate-400 group-hover:text-slate-600 dark:group-hover:text-slate-300" /> : <ChevronDown className="w-4 h-4 text-slate-400 group-hover:text-slate-600 dark:group-hover:text-slate-300" />) : <span className="block w-4 h-4" />}
        </div>
        <Avatar name={node.fullName} size="sm" />
        <div className="flex-1 min-w-0">
          <div className="flex items-center gap-2">
            <span className="text-sm font-medium text-slate-900 dark:text-white truncate">{hi(node.fullName)}</span>
            <span className="text-xs text-slate-400 shrink-0">{hi(node.employeeCode)}</span>
          </div>
          <div className="text-xs text-slate-500 truncate">
            {hi(node.designation)}{node.department && <> · <span className="text-slate-400">{hi(node.department)}</span></>}
          </div>
        </div>
        {hasReports && <span className="text-xs text-slate-400 shrink-0 bg-slate-100 dark:bg-white/[0.07] rounded-full px-2 py-0.5">{node.directReports.length}</span>}
      </div>
      {!isCollapsed && visible.length > 0 && (
        <div className="border-s-2 border-slate-100 dark:border-white/[0.06] ms-[22px]">
          {visible.map(c => <OrgNode key={c.id} node={c} collapsed={collapsed} toggle={toggle} search={search} matchesSearch={matchesSearch} depth={depth + 1} />)}
        </div>
      )}
    </div>
  );
}

// ── Tree view (pan + zoom canvas) ─────────────────────────────────────────────

function TreeView({ tree, search }: { tree: OrgChartNodeDto[]; search: string }) {
  const canvasRef = useRef<HTMLDivElement>(null);
  const [pan, setPan]   = useState({ x: 40, y: 40 });
  const [zoom, setZoom] = useState(0.85);
  const drag = useRef<{ startX: number; startY: number; panX: number; panY: number } | null>(null);

  // Only layout the first root (CEO/top) or all roots if multiple
  const layouts = useMemo(() => {
    return tree.map(root => {
      const laid = buildLayout(root);
      assignXY(laid, 0, 0);
      return laid;
    });
  }, [tree]);

  // Stack multiple roots side by side
  const positioned = useMemo(() => {
    let offsetX = 0;
    const result: PositionedNode[] = [];
    for (const l of layouts) {
      const shift = (laid: PositionedNode, dx: number) => {
        laid.x += dx;
        laid.children.forEach(c => shift(c, dx));
      };
      shift(l, offsetX);
      result.push(...flatten(l));
      offsetX += l.subtreeWidth + H_GAP * 3;
    }
    return result;
  }, [layouts]);

  const allEdges = useMemo(() => layouts.flatMap(edges), [layouts]);

  const totalW = useMemo(() => layouts.reduce((s, l) => s + l.subtreeWidth, 0) + (layouts.length - 1) * H_GAP * 3, [layouts]);
  const totalH = useMemo(() => {
    const allY = positioned.map(p => p.y + NODE_H);
    return Math.max(...allY) + 40;
  }, [positioned]);

  const fitToScreen = useCallback(() => {
    if (!canvasRef.current) return;
    const { clientWidth, clientHeight } = canvasRef.current;
    const newZoom = Math.min(1, Math.min(clientWidth / (totalW + 80), clientHeight / (totalH + 80)));
    setZoom(newZoom);
    setPan({ x: (clientWidth - totalW * newZoom) / 2, y: 32 });
  }, [totalW, totalH]);

  useEffect(() => { if (positioned.length > 0) fitToScreen(); }, [positioned.length, fitToScreen]);

  const onMouseDown = (e: React.MouseEvent) => {
    drag.current = { startX: e.clientX, startY: e.clientY, panX: pan.x, panY: pan.y };
  };
  const onMouseMove = (e: React.MouseEvent) => {
    if (!drag.current) return;
    setPan({ x: drag.current.panX + e.clientX - drag.current.startX, y: drag.current.panY + e.clientY - drag.current.startY });
  };
  const onMouseUp = () => { drag.current = null; };

  const onWheel = (e: React.WheelEvent) => {
    e.preventDefault();
    const newZoom = Math.min(2, Math.max(0.25, zoom * (1 - e.deltaY * 0.001)));
    const rect = canvasRef.current!.getBoundingClientRect();
    const mx = e.clientX - rect.left; const my = e.clientY - rect.top;
    const scale = newZoom / zoom;
    setPan(p => ({ x: mx - (mx - p.x) * scale, y: my - (my - p.y) * scale }));
    setZoom(newZoom);
  };

  const matchIds = useMemo(() => {
    if (!search) return null;
    const q = search.toLowerCase();
    return new Set(positioned.filter(p =>
      p.node.fullName.toLowerCase().includes(q) ||
      p.node.employeeCode.toLowerCase().includes(q) ||
      p.node.designation.toLowerCase().includes(q) ||
      p.node.department.toLowerCase().includes(q)
    ).map(p => p.node.id));
  }, [search, positioned]);

  return (
    <div className="relative">
      {/* Controls */}
      <div className="absolute top-3 end-3 z-10 flex items-center gap-1.5">
        <span className="text-xs text-slate-400 bg-white dark:bg-slate-900 border border-slate-200 dark:border-white/[0.1] rounded px-2 py-1">{Math.round(zoom * 100)}%</span>
        <button type="button" onClick={() => setZoom(z => Math.min(2, z + 0.1))} className="w-7 h-7 flex items-center justify-center rounded border border-slate-200 dark:border-white/[0.1] bg-white dark:bg-slate-900 text-slate-600 dark:text-slate-300 hover:bg-slate-50 dark:hover:bg-white/[0.05] text-sm font-bold">+</button>
        <button type="button" onClick={() => setZoom(z => Math.max(0.25, z - 0.1))} className="w-7 h-7 flex items-center justify-center rounded border border-slate-200 dark:border-white/[0.1] bg-white dark:bg-slate-900 text-slate-600 dark:text-slate-300 hover:bg-slate-50 dark:hover:bg-white/[0.05] text-sm font-bold">−</button>
        <button type="button" onClick={fitToScreen} title="Fit to screen"
          className="w-7 h-7 flex items-center justify-center rounded border border-slate-200 dark:border-white/[0.1] bg-white dark:bg-slate-900 text-slate-600 dark:text-slate-300 hover:bg-slate-50 dark:hover:bg-white/[0.05]">
          <Maximize2 className="w-3.5 h-3.5" />
        </button>
      </div>
      <p className="absolute bottom-3 start-3 text-[11px] text-slate-400 z-10 pointer-events-none">Drag to pan · Scroll to zoom</p>

      <div
        ref={canvasRef}
        className={`rounded-xl border border-slate-200 dark:border-white/[0.08] bg-slate-50 dark:bg-slate-950 overflow-hidden select-none h-[72vh] ${drag.current ? 'cursor-grabbing' : 'cursor-grab'}`}
        onMouseDown={onMouseDown} onMouseMove={onMouseMove} onMouseUp={onMouseUp} onMouseLeave={onMouseUp}
        onWheel={onWheel}
      >
        <div style={{ position: 'absolute', transform: `translate(${pan.x}px,${pan.y}px) scale(${zoom})`, transformOrigin: '0 0', width: totalW, height: totalH }}>
          {/* SVG connector lines */}
          <svg style={{ position: 'absolute', inset: 0, width: totalW, height: totalH, overflow: 'visible' }}>
            <defs>
              <pattern id="dots" width="20" height="20" patternUnits="userSpaceOnUse">
                <circle cx="1" cy="1" r="1" fill="currentColor" className="text-slate-200 dark:text-slate-800" />
              </pattern>
            </defs>
            <rect width={totalW} height={totalH} fill="url(#dots)" />
            {allEdges.map((e, i) => {
              const midY = (e.py + e.cy) / 2;
              const d = `M ${e.px} ${e.py} L ${e.px} ${midY} L ${e.cx} ${midY} L ${e.cx} ${e.cy}`;
              return <path key={i} d={d} fill="none" stroke="#cbd5e1" strokeWidth="1.5" className="dark:stroke-slate-700" />;
            })}
          </svg>
          {/* Node cards */}
          {positioned.map(p => {
            const isMatch = matchIds ? matchIds.has(p.node.id) : true;
            const isDim   = matchIds && !isMatch;
            return (
              <div key={p.node.id} style={{ position: 'absolute', left: p.x, top: p.y, width: NODE_W }}
                className={`bg-white dark:bg-slate-800 rounded-xl border shadow-sm p-3 flex items-center gap-3 transition-all
                  ${isDim ? 'opacity-25' : ''}
                  ${isMatch && matchIds ? 'border-sapphire/60 ring-2 ring-sapphire/20' : 'border-slate-200 dark:border-white/[0.08]'}`}>
                <Avatar name={p.node.fullName} size="sm" />
                <div className="flex-1 min-w-0">
                  <p className="text-sm font-semibold text-slate-900 dark:text-white truncate leading-tight">{p.node.fullName}</p>
                  <p className="text-xs text-slate-500 dark:text-slate-400 truncate">{p.node.designation}</p>
                  {p.node.department && <p className="text-[10px] text-slate-400 dark:text-slate-500 truncate mt-0.5">{p.node.department}</p>}
                </div>
                {p.children.length > 0 && (
                  <span className="text-[10px] font-semibold text-sapphire bg-sapphire/10 rounded-full px-1.5 py-0.5 shrink-0">{p.children.length}</span>
                )}
              </div>
            );
          })}
        </div>
      </div>
    </div>
  );
}

// ── Cards view (department grid) ──────────────────────────────────────────────

function CardsView({ tree, search }: { tree: OrgChartNodeDto[]; search: string }) {
  const all = useMemo(() => flattenTree(tree), [tree]);

  const filtered = useMemo(() => {
    if (!search) return all;
    const q = search.toLowerCase();
    return all.filter(n =>
      n.fullName.toLowerCase().includes(q) ||
      n.employeeCode.toLowerCase().includes(q) ||
      n.designation.toLowerCase().includes(q) ||
      n.department.toLowerCase().includes(q)
    );
  }, [all, search]);

  const grouped = useMemo(() => {
    const map: Record<string, OrgChartNodeDto[]> = {};
    for (const n of filtered) {
      const dept = n.department || 'Other';
      (map[dept] = map[dept] || []).push(n);
    }
    return Object.entries(map).sort(([a], [b]) => a.localeCompare(b));
  }, [filtered]);

  if (grouped.length === 0)
    return <div className="flex flex-col items-center justify-center py-24 text-slate-400"><Users className="w-10 h-10 mb-3 opacity-40" /><p className="text-sm">No results for "{search}"</p></div>;

  return (
    <div className="space-y-8">
      {grouped.map(([dept, members]) => (
        <div key={dept}>
          <div className="flex items-center gap-3 mb-4">
            <h2 className="text-sm font-bold uppercase tracking-widest text-slate-500 dark:text-slate-400">{dept}</h2>
            <span className="text-xs text-slate-400 bg-slate-100 dark:bg-white/[0.07] rounded-full px-2 py-0.5">{members.length}</span>
            <div className="flex-1 h-px bg-slate-100 dark:bg-white/[0.06]" />
          </div>
          <div className="grid grid-cols-2 sm:grid-cols-3 lg:grid-cols-4 xl:grid-cols-5 gap-3">
            {members.map(emp => (
              <div key={emp.id} className="bg-white dark:bg-white/[0.03] border border-slate-200 dark:border-white/[0.07] rounded-xl p-4 flex flex-col items-center gap-2 hover:border-sapphire/40 hover:shadow-sm transition-all group">
                <Avatar name={emp.fullName} size="md" />
                <div className="text-center min-w-0 w-full">
                  <p className="text-sm font-semibold text-slate-900 dark:text-white truncate group-hover:text-sapphire transition-colors">{emp.fullName}</p>
                  <p className="text-xs text-slate-500 dark:text-slate-400 truncate mt-0.5">{emp.designation}</p>
                  <p className="text-[10px] text-slate-400 font-mono mt-1">{emp.employeeCode}</p>
                </div>
                {emp.directReports.length > 0 && (
                  <span className="text-[10px] font-medium text-sapphire bg-sapphire/10 rounded-full px-2 py-0.5">
                    {emp.directReports.length} direct {emp.directReports.length === 1 ? 'report' : 'reports'}
                  </span>
                )}
              </div>
            ))}
          </div>
        </div>
      ))}
    </div>
  );
}
