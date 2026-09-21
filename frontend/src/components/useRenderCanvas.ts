'use client';

import { useCallback, useEffect, useRef, useState } from 'react';
import type { RefObject } from 'react';

/**
 * useRenderCanvas — the rendering harness the login hero (or any canvas hero)
 * sits on top of.
 *
 * It owns the boring, easy-to-get-wrong half of a canvas component:
 *
 *   • context acquisition with graceful degradation (WebGL2 → WebGL1 → 2D),
 *     where the *caller* declares which modes its art direction supports and
 *     the hook reports which one it actually got;
 *   • devicePixelRatio (capped, because a 3x buffer on a full-bleed panel is a
 *     fan-spinner for no visible gain) and ResizeObserver-driven resize, with
 *     the right viewport/transform reset per mode;
 *   • a rAF loop that PAUSES when the tab is hidden or the user asked for
 *     reduced motion, painting exactly one static frame instead, and that
 *     reacts live when either of those changes;
 *   • a normalised pointer signal (-1..1) lerped inside the loop, kept in a ref
 *     so that moving the mouse never costs a React render;
 *   • full teardown: rAF cancelled, observers disconnected, listeners removed,
 *     GL context explicitly released via WEBGL_lose_context;
 *   • WebGL context-loss recovery — the scene is rebuilt from scratch when the
 *     browser hands the context back.
 *
 * No dependencies. Callbacks are held in refs, so you may pass inline arrows
 * without re-initialising the scene on every render; use `deps` when you
 * genuinely want a rebuild.
 */

/* ────────────────────────────── types ────────────────────────────── */

export type RenderMode = 'webgl2' | 'webgl' | '2d';

/** Either WebGL flavour. Most GLSL plumbing is identical across the two. */
export type AnyGL = WebGLRenderingContext | WebGL2RenderingContext;

export type ContextFor<M extends RenderMode> =
  M extends 'webgl2' ? WebGL2RenderingContext :
  M extends 'webgl'  ? WebGLRenderingContext  :
  M extends '2d'     ? CanvasRenderingContext2D :
  never;

/** Cursor state. Never stored in React state — read it inside `draw`. */
export interface PointerSignal {
  /** Eased position, -1..1, origin at the centre of the tracked element. */
  x: number;
  y: number;
  /** Un-eased target, -1..1. Useful for snapping on first paint. */
  targetX: number;
  targetY: number;
  /** Eased position in 0..1 (top-left origin) — handy for CSS-style gradients. */
  u: number;
  v: number;
  /** True between pointerenter and pointerleave. */
  inside: boolean;
  /** True until the pointer has moved at least once (so you can idle-animate). */
  idle: boolean;
}

/**
 * Written as a distributive conditional so the result is a *discriminated*
 * union: `if (f.mode === 'webgl2')` narrows `f.ctx` to WebGL2RenderingContext.
 */
export type RenderFrame<M extends RenderMode = RenderMode> = M extends RenderMode
  ? {
      mode: M;
      ctx: ContextFor<M>;
      canvas: HTMLCanvasElement;
      /** CSS pixels. */
      width: number;
      height: number;
      /** Backing-store pixels (== CSS * dpr). This is the GL viewport. */
      pixelWidth: number;
      pixelHeight: number;
      dpr: number;
      /** Seconds since the first frame of this scene. */
      time: number;
      /** Seconds since the previous frame, clamped to 1/15s. */
      dt: number;
      /** Frame counter, starting at 0. */
      frame: number;
      pointer: PointerSignal;
      /**
       * True when this is the single frame painted for a paused scene
       * (hidden tab, reduced motion, or a resize while paused). Use it to draw
       * the "at rest" composition rather than a random moment of the loop.
       */
      isStatic: boolean;
    }
  : never;

export type RenderStatus = 'pending' | 'ready' | 'failed';

export interface UseRenderCanvasOptions<M extends RenderMode> {
  /**
   * Modes this scene can render in, in order of preference.
   * `['webgl2', 'webgl', '2d']` asks for a shader with a hand-drawn fallback;
   * `['2d']` is a plain canvas hero; `['webgl2']` fails loudly rather than
   * silently degrading.
   */
  modes: readonly M[];
  /** Paint one frame. Called from rAF, or once while paused. */
  draw: (frame: RenderFrame<M>) => void;
  /**
   * Build GPU resources (programs, buffers, textures) once per context.
   * May return a disposer, which runs before the context is released.
   * Runs again after a WebGL context-loss/restore cycle.
   */
  setup?: (frame: RenderFrame<M>) => void | (() => void);
  /** Called after every resize, before the next draw. Viewport is already set. */
  resize?: (frame: RenderFrame<M>) => void;
  /** Upper bound on devicePixelRatio. Default 2. */
  maxDpr?: number;
  /** Per-frame easing factor for the pointer, 0..1. Default 0.08. */
  pointerEase?: number;
  /** Element the pointer is measured against. Default: the canvas itself. */
  pointerTarget?: 'canvas' | 'parent' | (() => HTMLElement | null);
  /** Drift back to centre on pointerleave. Default true. */
  pointerRecenter?: boolean;
  /** Honour `prefers-reduced-motion: reduce`. Default true. Only turn this off
   *  for scenes with no looping animation at all. */
  respectReducedMotion?: boolean;
  /** Stop the loop while `document.hidden`. Default true. */
  pauseWhenHidden?: boolean;
  /** Passed to getContext for the WebGL modes. */
  glAttributes?: WebGLContextAttributes;
  /** Passed to getContext for the 2D mode. */
  canvas2dAttributes?: CanvasRenderingContext2DSettings;
  /** Called when no requested mode could be acquired, or setup threw. */
  onError?: (error: unknown) => void;
  /** Rebuild the scene when any of these change (compared by String()). */
  deps?: readonly unknown[];
}

export interface RenderCanvasHandle<M extends RenderMode> {
  /** Attach to your <canvas>. */
  canvasRef: RefObject<HTMLCanvasElement | null>;
  /** The mode actually acquired — null until the first effect runs. */
  mode: M | null;
  status: RenderStatus;
  /** Why acquisition failed, if it did. */
  error: Error | null;
  /** Live pointer signal. Read inside `draw`; it never triggers a render. */
  pointer: RefObject<PointerSignal>;
  /** Paint one frame right now — for scenes that change on a React state
   *  change while the loop is paused (theme switch, auth step change). */
  requestFrame: () => void;
  /** True when the loop is parked (hidden tab or reduced motion). */
  isPaused: () => boolean;
}

/* ─────────────────────────── GLSL helpers ─────────────────────────── */

export class GLSLError extends Error {
  readonly stage: 'vertex' | 'fragment' | 'link';
  readonly log: string;
  readonly source?: string;
  constructor(stage: 'vertex' | 'fragment' | 'link', log: string, source?: string) {
    super(
      `[GLSL ${stage}] ${log.trim() || 'no info log (driver returned nothing)'}` +
        (source ? `\n\n${annotate(log, source)}` : ''),
    );
    this.name = 'GLSLError';
    this.stage = stage;
    this.log = log;
    this.source = source;
  }
}

/**
 * Line-number a shader and mark the lines the driver complained about, so the
 * console shows the offending line instead of `ERROR: 0:47: ...` with no text.
 */
function annotate(log: string, source: string): string {
  const bad = new Set<number>();
  const re = /(?:ERROR|WARNING):\s*\d+:(\d+)/g;
  let m: RegExpExecArray | null;
  while ((m = re.exec(log)) !== null) bad.add(Number(m[1]));
  return source
    .split('\n')
    .map((line, i) => {
      const n = i + 1;
      const gutter = String(n).padStart(4, ' ');
      return `${bad.has(n) ? '>>' : '  '}${gutter} | ${line}`;
    })
    .join('\n');
}

/** Compile one shader stage. Throws a GLSLError carrying the annotated source. */
export function compileShader(gl: AnyGL, type: number, source: string): WebGLShader {
  const shader = gl.createShader(type);
  if (!shader) throw new GLSLError(type === gl.VERTEX_SHADER ? 'vertex' : 'fragment', 'createShader returned null');
  gl.shaderSource(shader, source);
  gl.compileShader(shader);
  if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) {
    const log = gl.getShaderInfoLog(shader) ?? '';
    gl.deleteShader(shader);
    throw new GLSLError(type === gl.VERTEX_SHADER ? 'vertex' : 'fragment', log, source);
  }
  return shader;
}

/**
 * Compile + link a program. The shaders are detached and deleted on success —
 * the program keeps them alive — so callers only have to track the program.
 */
export function createProgram(gl: AnyGL, vertexSource: string, fragmentSource: string): WebGLProgram {
  const vs = compileShader(gl, gl.VERTEX_SHADER, vertexSource);
  let fs: WebGLShader;
  try {
    fs = compileShader(gl, gl.FRAGMENT_SHADER, fragmentSource);
  } catch (err) {
    gl.deleteShader(vs);
    throw err;
  }
  const program = gl.createProgram();
  if (!program) {
    gl.deleteShader(vs);
    gl.deleteShader(fs);
    throw new GLSLError('link', 'createProgram returned null');
  }
  gl.attachShader(program, vs);
  gl.attachShader(program, fs);
  gl.linkProgram(program);
  const linked = gl.getProgramParameter(program, gl.LINK_STATUS);
  gl.detachShader(program, vs);
  gl.detachShader(program, fs);
  gl.deleteShader(vs);
  gl.deleteShader(fs);
  if (!linked) {
    const log = gl.getProgramInfoLog(program) ?? '';
    gl.deleteProgram(program);
    throw new GLSLError('link', log);
  }
  return program;
}

/** Look up uniforms once. Missing//optimised-out names come back as null. */
export function getUniforms<K extends string>(
  gl: AnyGL,
  program: WebGLProgram,
  names: readonly K[],
): Record<K, WebGLUniformLocation | null> {
  const out = {} as Record<K, WebGLUniformLocation | null>;
  for (const name of names) out[name] = gl.getUniformLocation(program, name);
  return out;
}

export interface ScreenQuad {
  /** Bind the quad's buffer + attribute and draw it. Program must be in use. */
  draw: () => void;
  dispose: () => void;
}

/**
 * The full-screen triangle-pair every fragment-shader hero needs.
 * Positions are clip space (-1..1); derive UVs in the vertex shader.
 */
export function createScreenQuad(gl: AnyGL, program: WebGLProgram, attribName = 'a_position'): ScreenQuad {
  const buffer = gl.createBuffer();
  if (!buffer) throw new GLSLError('link', 'createBuffer returned null');
  const loc = gl.getAttribLocation(program, attribName);
  gl.bindBuffer(gl.ARRAY_BUFFER, buffer);
  gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 3, -1, -1, 3]), gl.STATIC_DRAW);
  return {
    draw() {
      if (loc < 0) return;
      gl.bindBuffer(gl.ARRAY_BUFFER, buffer);
      gl.enableVertexAttribArray(loc);
      gl.vertexAttribPointer(loc, 2, gl.FLOAT, false, 0, 0);
      gl.drawArrays(gl.TRIANGLES, 0, 3);
    },
    dispose() {
      gl.deleteBuffer(buffer);
    },
  };
}

/* ─────────────────────────────── hook ─────────────────────────────── */

const REDUCED_MOTION_QUERY = '(prefers-reduced-motion: reduce)';

function acquire(
  canvas: HTMLCanvasElement,
  mode: RenderMode,
  glAttributes: WebGLContextAttributes | undefined,
  canvas2dAttributes: CanvasRenderingContext2DSettings | undefined,
): RenderingContext | null {
  try {
    if (mode === '2d') return canvas.getContext('2d', canvas2dAttributes);
    const attrs: WebGLContextAttributes = {
      alpha: true,
      antialias: false,
      depth: false,
      stencil: false,
      premultipliedAlpha: true,
      preserveDrawingBuffer: false,
      powerPreference: 'default',
      failIfMajorPerformanceCaveat: false,
      ...glAttributes,
    };
    return canvas.getContext(mode === 'webgl2' ? 'webgl2' : 'webgl', attrs);
  } catch {
    // Safari throws rather than returning null when a context type is blocked.
    return null;
  }
}

export function useRenderCanvas<M extends RenderMode>(
  options: UseRenderCanvasOptions<M>,
): RenderCanvasHandle<M> {
  const canvasRef = useRef<HTMLCanvasElement | null>(null);
  const [mode, setMode] = useState<M | null>(null);
  const [status, setStatus] = useState<RenderStatus>('pending');
  const [error, setError] = useState<Error | null>(null);
  /** Bumped to force a full rebuild after WebGL context restore. */
  const [generation, setGeneration] = useState(0);

  const pointer = useRef<PointerSignal>({
    x: 0, y: 0, targetX: 0, targetY: 0, u: 0.5, v: 0.5, inside: false, idle: true,
  });

  /** Options live in a ref so inline callbacks don't rebuild the scene. */
  const opts = useRef(options);
  opts.current = options;

  /** Set by the effect so `requestFrame` can reach the live painter. */
  const paintOnce = useRef<(() => void) | null>(null);
  const paused = useRef(true);

  const requestFrame = useCallback(() => {
    paintOnce.current?.();
  }, []);
  const isPaused = useCallback(() => paused.current, []);

  const modesKey = options.modes.join(',');
  const depsKey = options.deps ? options.deps.map(v => String(v)).join('\u0000') : '';

  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas) return;

    const o = opts.current;
    const modes = o.modes;

    /* ── context acquisition, best mode first ───────────────────────── */
    let active: RenderMode | null = null;
    let ctx: RenderingContext | null = null;
    for (const m of modes) {
      const got = acquire(canvas, m, o.glAttributes, o.canvas2dAttributes);
      if (got) { active = m; ctx = got; break; }
    }
    if (!active || !ctx) {
      const err = new Error(
        `useRenderCanvas: none of [${modes.join(', ')}] could be created on this canvas.`,
      );
      setMode(null);
      setStatus('failed');
      setError(err);
      o.onError?.(err);
      return;
    }

    const isGL = active !== '2d';
    const gl = isGL ? (ctx as AnyGL) : null;
    const c2d = isGL ? null : (ctx as CanvasRenderingContext2D);

    setMode(active as M);
    setError(null);

    /* ── frame object, mutated in place so no garbage per frame ─────── */
    const frame = {
      mode: active,
      ctx,
      canvas,
      width: 0,
      height: 0,
      pixelWidth: 0,
      pixelHeight: 0,
      dpr: 1,
      time: 0,
      dt: 0,
      frame: 0,
      pointer: pointer.current,
      isStatic: true,
    } as unknown as RenderFrame<M>;
    // A private, non-generic view for the hook's own bookkeeping; RenderFrame<M>
    // is a union, so its members are not directly assignable.
    const f = frame as unknown as {
      width: number; height: number; pixelWidth: number; pixelHeight: number;
      dpr: number; time: number; dt: number; frame: number; isStatic: boolean;
    };

    /* ── reduced motion / visibility ────────────────────────────────── */
    const motion = window.matchMedia(REDUCED_MOTION_QUERY);
    const wantsStill = () =>
      (o.respectReducedMotion !== false && motion.matches) ||
      (o.pauseWhenHidden !== false && document.hidden);

    /* ── sizing ─────────────────────────────────────────────────────── */
    const maxDpr = o.maxDpr ?? 2;

    const measure = (entry?: ResizeObserverEntry) => {
      const cap = Math.max(1, maxDpr);
      const dpr = Math.min(window.devicePixelRatio || 1, cap);
      let cssW = 0;
      let cssH = 0;
      if (entry) {
        const box = entry.contentBoxSize?.[0];
        if (box) { cssW = box.inlineSize; cssH = box.blockSize; }
        else { cssW = entry.contentRect.width; cssH = entry.contentRect.height; }
      } else {
        const r = canvas.getBoundingClientRect();
        cssW = r.width; cssH = r.height;
      }
      if (!cssW || !cssH) return false;

      // Deliberately NOT using entry.devicePixelContentBoxSize: Chromium reports
      // it in CSS pixels under device-scale emulation, which silently renders a
      // retina hero at half resolution. css * dpr is boring and always right.
      const pxW = Math.max(1, Math.round(cssW * dpr));
      const pxH = Math.max(1, Math.round(cssH * dpr));

      f.width = cssW;
      f.height = cssH;
      f.dpr = dpr;

      if (canvas.width !== pxW || canvas.height !== pxH) {
        canvas.width = pxW;
        canvas.height = pxH;
      }
      f.pixelWidth = canvas.width;
      f.pixelHeight = canvas.height;

      if (gl) {
        gl.viewport(0, 0, canvas.width, canvas.height);
      } else if (c2d) {
        // Resizing the backing store resets the 2D transform, so (re)apply the
        // DPR scale here and let callers draw in plain CSS pixels.
        const sx = canvas.width / cssW;
        const sy = canvas.height / cssH;
        c2d.setTransform(sx, 0, 0, sy, 0, 0);
      }
      return true;
    };

    /* ── caller setup ───────────────────────────────────────────────── */
    measure();
    let disposeSetup: (() => void) | void;
    try {
      disposeSetup = o.setup?.(frame);
    } catch (err) {
      setStatus('failed');
      setError(err instanceof Error ? err : new Error(String(err)));
      o.onError?.(err);
      return () => { /* nothing built yet */ };
    }
    setStatus('ready');

    /* ── pointer ────────────────────────────────────────────────────── */
    const pointerHost: HTMLElement =
      typeof o.pointerTarget === 'function'
        ? (o.pointerTarget() ?? canvas)
        : o.pointerTarget === 'parent'
          ? ((canvas.parentElement as HTMLElement | null) ?? canvas)
          : canvas;

    const p = pointer.current;
    const onPointerMove = (e: PointerEvent) => {
      const r = pointerHost.getBoundingClientRect();
      if (!r.width || !r.height) return;
      const nx = (e.clientX - r.left) / r.width;
      const ny = (e.clientY - r.top) / r.height;
      p.targetX = Math.max(-1, Math.min(1, nx * 2 - 1));
      p.targetY = Math.max(-1, Math.min(1, ny * 2 - 1));
      p.inside = true;
      p.idle = false;
      if (paused.current) {
        // No loop running: snap rather than leave the signal half-eased.
        p.x = p.targetX; p.y = p.targetY;
        p.u = (p.x + 1) / 2; p.v = (p.y + 1) / 2;
      }
    };
    const onPointerLeave = () => {
      p.inside = false;
      if (o.pointerRecenter !== false) { p.targetX = 0; p.targetY = 0; }
    };
    pointerHost.addEventListener('pointermove', onPointerMove, { passive: true });
    pointerHost.addEventListener('pointerleave', onPointerLeave, { passive: true });
    pointerHost.addEventListener('pointercancel', onPointerLeave, { passive: true });

    /* ── the loop ───────────────────────────────────────────────────── */
    const ease = o.pointerEase ?? 0.08;
    let raf = 0;
    let started = 0;
    let last = 0;
    let lost = false;

    const paint = (now: number, isStatic: boolean) => {
      // A zero-size canvas (display:none, or mounted before layout) is not an
      // error — skip the frame and let the ResizeObserver wake us.
      if (!f.width || !f.height) { if (!measure()) return; }
      if (!started) { started = now; last = now; }
      f.dt = Math.min((now - last) / 1000, 1 / 15);
      f.time = (now - started) / 1000;
      last = now;
      f.isStatic = isStatic;

      if (isStatic) {
        p.x = p.targetX; p.y = p.targetY;
      } else {
        p.x += (p.targetX - p.x) * ease;
        p.y += (p.targetY - p.y) * ease;
      }
      p.u = (p.x + 1) / 2;
      p.v = (p.y + 1) / 2;

      try {
        o.draw(frame);
      } catch (err) {
        o.onError?.(err);
        // A throwing draw would otherwise throw 60 times a second.
        cancelAnimationFrame(raf);
        raf = 0;
        return;
      }
      f.frame += 1;
    };

    const tick = (now: number) => {
      raf = requestAnimationFrame(tick);
      paint(now, false);
    };

    const still = () => {
      // One frame, then nothing: the composited result stays on screen.
      paint(performance.now(), true);
    };

    const sync = () => {
      if (lost) return;
      cancelAnimationFrame(raf);
      raf = 0;
      if (wantsStill()) {
        paused.current = true;
        still();
      } else {
        paused.current = false;
        // Reset the clock so a long pause doesn't teleport the animation.
        last = performance.now();
        raf = requestAnimationFrame(tick);
      }
    };

    paintOnce.current = () => {
      if (lost) return;
      paint(performance.now(), paused.current);
    };

    sync();

    /* ── observers & listeners ──────────────────────────────────────── */
    const onResizeEntry = (entries: ResizeObserverEntry[]) => {
      if (!measure(entries[entries.length - 1])) return;
      try { o.resize?.(frame); } catch (err) { o.onError?.(err); }
      // The backing store was just cleared; repaint immediately when parked.
      if (paused.current) still();
    };
    const ro = new ResizeObserver(onResizeEntry);
    ro.observe(canvas);

    // devicePixelRatio changes (monitor swap, browser zoom) don't resize the
    // element, so the ResizeObserver stays silent — watch the ratio directly.
    let dprQuery: MediaQueryList | null = null;
    const onDprChange = () => {
      watchDpr();
      if (measure()) {
        try { opts.current.resize?.(frame); } catch (err) { opts.current.onError?.(err); }
        if (paused.current) still();
      }
    };
    const watchDpr = () => {
      dprQuery?.removeEventListener('change', onDprChange);
      dprQuery = window.matchMedia(`(resolution: ${window.devicePixelRatio || 1}dppx)`);
      dprQuery.addEventListener('change', onDprChange);
    };
    watchDpr();

    const onVisibility = () => sync();
    const onMotionChange = () => sync();
    document.addEventListener('visibilitychange', onVisibility);
    motion.addEventListener('change', onMotionChange);

    /* ── WebGL context loss / restore ───────────────────────────────── */
    const onContextLost = (e: Event) => {
      e.preventDefault(); // required, or the context is never restored
      lost = true;
      paused.current = true;
      cancelAnimationFrame(raf);
      raf = 0;
      setStatus('pending');
    };
    const onContextRestored = () => {
      // Every GL object died with the context; rebuild by re-running the effect.
      setGeneration(g => g + 1);
    };
    if (isGL) {
      canvas.addEventListener('webglcontextlost', onContextLost as EventListener);
      canvas.addEventListener('webglcontextrestored', onContextRestored);
    }

    /* ── teardown ───────────────────────────────────────────────────── */
    return () => {
      paintOnce.current = null;
      paused.current = true;
      cancelAnimationFrame(raf);
      raf = 0;
      ro.disconnect();
      document.removeEventListener('visibilitychange', onVisibility);
      motion.removeEventListener('change', onMotionChange);
      dprQuery?.removeEventListener('change', onDprChange);
      pointerHost.removeEventListener('pointermove', onPointerMove);
      pointerHost.removeEventListener('pointerleave', onPointerLeave);
      pointerHost.removeEventListener('pointercancel', onPointerLeave);
      if (isGL) {
        canvas.removeEventListener('webglcontextlost', onContextLost as EventListener);
        canvas.removeEventListener('webglcontextrestored', onContextRestored);
      }
      try { disposeSetup?.(); } catch { /* a failing disposer must not block teardown */ }
      if (gl && !lost) {
        // Hand the GPU memory back now instead of waiting for GC — browsers cap
        // live WebGL contexts (~16), and a hot-reloading dev session hits that.
        const ext = gl.getExtension('WEBGL_lose_context') as { loseContext(): void } | null;
        ext?.loseContext();
      }
    };
    // `opts` is a ref; only an explicit mode/deps change rebuilds the scene.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [modesKey, depsKey, generation]);

  return { canvasRef, mode, status, error, pointer, requestFrame, isPaused };
}

/**
 * Probe which mode this browser would give you, without mounting anything.
 * Handy for e2e assertions and for logging why a hero fell back.
 */
export function probeRenderMode(preferred: readonly RenderMode[] = ['webgl2', 'webgl', '2d']): {
  mode: RenderMode | null;
  renderer: string | null;
  vendor: string | null;
} {
  if (typeof document === 'undefined') return { mode: null, renderer: null, vendor: null };
  const canvas = document.createElement('canvas');
  canvas.width = 1;
  canvas.height = 1;
  for (const m of preferred) {
    const ctx = acquire(canvas, m, undefined, undefined);
    if (!ctx) continue;
    if (m === '2d') return { mode: m, renderer: null, vendor: null };
    const gl = ctx as AnyGL;
    const dbg = gl.getExtension('WEBGL_debug_renderer_info') as {
      UNMASKED_RENDERER_WEBGL: number;
      UNMASKED_VENDOR_WEBGL: number;
    } | null;
    const renderer = dbg
      ? String(gl.getParameter(dbg.UNMASKED_RENDERER_WEBGL))
      : String(gl.getParameter(gl.RENDERER));
    const vendor = dbg
      ? String(gl.getParameter(dbg.UNMASKED_VENDOR_WEBGL))
      : String(gl.getParameter(gl.VENDOR));
    gl.getExtension('WEBGL_lose_context')?.loseContext();
    return { mode: m, renderer, vendor };
  }
  return { mode: null, renderer: null, vendor: null };
}
