'use client';

/**
 * LOGIN AURORA SCENE — the production /login background.
 * ─────────────────────────────────────────────────────────────────────────────
 * A merge of three prototypes into ONE lighting model. Concept B (aurora glass)
 * is the base; the two grafts exist to fix B's own stated weakness — that the
 * MID-FRAME of the colour field read as a soft smoky wash that could be
 * mistaken for a stock gradient mesh.
 *
 *   BASE (B)   The twice-domain-warped fbm aurora, the two-light product-shot
 *              grade, and the two panes of glass drawn by the shader itself:
 *              a rounded-box SDF for exact edge distance, a circular bevel
 *              profile for the per-pixel refraction offset, re-sampled once
 *              per colour channel so the rim fringes chromatically, and a lit
 *              side chosen from the live position of the key aurora core.
 *              B's hue-preserving emission (chroma DIRECTION and scalar
 *              INTENSITY separated, only the intensity tone-mapped) is kept
 *              everywhere, including on the combined sum — tone-mapping the
 *              summed RGB per channel is exactly what turns a colour field
 *              into grey milk.
 *
 *   GRAFT (A)  VOLUMETRIC SHAFTS and THREE-LAYER PARALLAXING MOTES. A's shafts
 *              are real shadows: it projects each sample back onto the plane of
 *              an occluder and asks how much light got through. That occluder
 *              is the one thing we replace — see below. The motes are three
 *              depth layers at three scales, three drift rates and three
 *              parallax rates; the DIFFERENCE in rate is the depth cue.
 *
 *   GRAFT (C)  The GIRIH ROSETTE SCREEN — grafted as A's occluder itself, not
 *              as a pattern laid over the picture. `rosetteDist` is concept C's
 *              function unchanged: the strapwork of a decagonal rosette is the
 *              {10/3} star polygram on the edge midpoints, every chord tangent
 *              to rho = r·sin36 with half-length hl = r·sin54, folded ten ways
 *              and laid on the rhombic lattice v1 = 2r(1,0), v2 = 2r(cos72,
 *              sin72). C's QAMARIYA colour comes with it: each aperture is a
 *              leaded pane of gold, amber, emerald, cyan or sapphire, and the
 *              colour changes ON the strapwork, because the core/ring boundary
 *              is computed from the same ten-fold angular fold that draws it.
 *
 * So every shaft crossing the frame is the shadow of a real girih screen, and
 * it carries the hue of the pane it came through. Warm gold meeting the
 * brand's sapphire, cyan and emerald in the shadows is what stops this being
 * "a blue picture".
 *
 * The screen is deliberately almost invisible in its own right — its direct
 * contrast is gated by the luminance of the field behind it, so the strapwork
 * blooms only where the aurora is already bright and dissolves into the dark
 * left side where the brand lockup and the headline live. Enterprise
 * infrastructure for a Riyadh CFO, not decorative-ethnic pastiche.
 *
 * Budget: dpr capped at 1.5 for this scene (B's per-channel refraction and the
 * march are both fill-rate bound, and 2.0 buys nothing visible on a field this
 * soft); the shaft march is 12 taps, not A's 42, because the shafts are a
 * supporting layer here rather than the subject; the exact lattice search runs
 * over 4 candidate cells once per pixel where the screen is SEEN, and the
 * march taps use a nearest-cell approximation that 12 integrated samples make
 * unobservable. `useRenderCanvas` parks the loop on a hidden tab or under
 * prefers-reduced-motion, painting one composed static frame instead.
 */

import { useCallback, useEffect, useRef, useState } from 'react';
import type { RefObject } from 'react';
import {
  useRenderCanvas,
  createProgram,
  createScreenQuad,
  getUniforms,
  type RenderFrame,
  type ScreenQuad,
} from './useRenderCanvas';

/* ══════════════════════════════════════════════════════════════════════════
   SHADER
   ══════════════════════════════════════════════════════════════════════════ */

const VERT = `
attribute vec2 a_position;
void main() { gl_Position = vec4(a_position, 0.0, 1.0); }
`;

/** The aurora field, emitted twice: 4 octaves for open space, 2 for the
 *  interior of the glass (dropping high octaves *is* a low-pass blur, and it
 *  costs three quarters less than a poisson tap set). */
const field = (name: string, FBM: string) => `
vec3 ${name}(vec2 uv) {
  float asp = u_res.x / u_res.y;
  vec2  w   = vec2(uv.x * asp, uv.y);
  vec2  p   = w * 2.15;
  vec2  ps  = vec2(p.x, p.y * 0.62);          // curtains stretch vertically
  float t   = g_t;

  vec2 q = vec2(
    ${FBM}(ps + vec2(0.0, t * 0.055)),
    ${FBM}(ps + vec2(4.7, 2.1) + vec2(t * 0.045, 0.0)));

  vec2 r = vec2(
    ${FBM}(ps + 3.10 * q + vec2(1.7, 9.2) - vec2(0.0, t * 0.030)),
    ${FBM}(ps + 2.80 * q + vec2(8.3, 2.8) + vec2(t * 0.026, 0.0)));

  float f = ${FBM}(ps + 3.30 * r + vec2(t * 0.018, 0.0));

  /* fbm lands in roughly 0.26..0.72. Narrow remaps so each hue owns a REGION
     rather than everything averaging into one milky wash. */
  float fn = smoothstep(0.29, 0.68, f);
  float qn = smoothstep(0.31, 0.65, q.y);
  float r1 = smoothstep(0.31, 0.65, r.x);
  float r2 = smoothstep(0.33, 0.69, r.y);

  /* TWO LIGHTS, lit like a product shot rather than an even wash:
       KEY  — cyan / sapphire / emerald, hard on the glass, right of frame.
       FILL — violet, low and soft on the left, giving the brand side real
              chroma at a luminance the text can still beat.                  */
  vec2  kd = (uv - vec2(0.82, 0.44)) / vec2(0.44, 0.90);
  vec2  fd = (uv - vec2(0.13, 0.62)) / vec2(0.46, 0.86);
  float keyR = mix(0.09, 2.05, smoothstep(0.88, 0.05, length(kd)));
  float keyL = mix(0.20, 1.30, smoothstep(0.84, 0.04, length(fd)));

  vec3 col   = mix(C_DEEP, C_MID * 1.25, fn);
  vec3 emisK = vec3(0.0);
  vec3 emisF = vec3(0.0);

  emisK += C_SAPPH * r1 * r1      * 1.05;
  emisK += C_EMER  * r2 * r2 * r2 * 1.15;
  emisK += C_CYAN  * pow(fn, 3.0) * 0.95;
  emisF += C_VIOLET * qn * qn     * 1.40;
  emisF += C_SAPPH  * qn * qn * qn * 0.45;

  // five luminous cores on incommensurate drifts, staged around the glass
  vec2 k1 = vec2(0.70 * asp + 0.15 * sin(t * 0.061), 0.18 + 0.11 * cos(t * 0.083));
  vec2 k2 = vec2(0.96 * asp + 0.13 * cos(t * 0.047), 0.58 + 0.13 * sin(t * 0.067));
  vec2 k3 = vec2(0.93 * asp + 0.16 * sin(t * 0.037 + 1.7), 0.84 + 0.10 * cos(t * 0.053));
  vec2 k4 = vec2(0.26 * asp + 0.12 * cos(t * 0.029 + 0.6), 0.98 + 0.08 * sin(t * 0.043));
  vec2 k5 = vec2(0.09 * asp + 0.10 * sin(t * 0.034 + 2.4), 0.26 + 0.10 * cos(t * 0.049));
  float lum = 0.30 + 1.05 * fn;
  emisK += C_CYAN   * exp(-dot(w - k1, w - k1) *  7.0) * lum * 1.55;
  emisK += C_SAPPH  * exp(-dot(w - k2, w - k2) *  5.5) * lum * 1.95;
  emisK += C_EMER   * exp(-dot(w - k3, w - k3) *  7.0) * lum * 1.75;
  emisF += C_VIOLET * exp(-dot(w - k4, w - k4) *  6.0) * lum * 1.88;
  emisF += C_VIOLET * exp(-dot(w - k5, w - k5) *  7.0) * lum * 1.58;

  /* ridged curtain filaments — structure, not smoke */
  float cf    = ${FBM}(vec2(p.x * 2.2, p.y * 0.20) + vec2(t * 0.040, -t * 0.060));
  float ridge = pow(clamp(1.0 - abs(cf * 2.0 - 0.94), 0.0, 1.0), 5.0);
  emisK += mix(C_CYAN, C_EMER, r2) * ridge * (0.20 + 0.95 * fn) * 0.95;
  emisF += C_VIOLET * ridge * (0.15 + 0.7 * fn) * 0.55;

  vec3 emis = emisK * keyR + emisF * keyL;

  /* Hue-preserving emission: split the sum into a chroma DIRECTION and a
     scalar INTENSITY, tone-map only the intensity, and let the hue survive
     all the way to the top of the range. Only the very brightest cores are
     allowed to bleach towards white. */
  float ei = max(max(emis.r, emis.g), emis.b);
  vec3  eh = emis / max(ei, 1e-4);
  float ii = 1.0 - exp(-ei * 1.70);
  col  = col * (0.55 + 0.80 * max(keyR, keyL));
  col += mix(eh, vec3(1.0), pow(ii, 5.0) * 0.50) * ii;

  // depth falloff at the extreme frame
  col *= 1.0 - 0.30 * smoothstep(0.72, 1.55, min(length(kd), length(fd)) + 0.35);

  /* one high-frequency layer so the field has filaments rather than an
     airbrushed smear */
  col *= 0.90 + 0.24 * ${FBM}(ps * 3.7 + vec2(t * 0.020, -t * 0.030));

  col = 1.0 - exp(-col * 1.24);                   /* filmic-ish exposure */

  float lu = dot(col, vec3(0.2126, 0.7152, 0.0722));
  return clamp(mix(vec3(lu), col, 1.22), 0.0, 1.0);
}
`;

const FRAG = `
precision highp float;

uniform vec2  u_res;
uniform float u_dpr;
uniform float u_time;
uniform vec2  u_ptr;
uniform vec4  u_card;
uniform vec4  u_back;
uniform vec2  u_rad;
uniform vec2  u_light;

float g_t;
vec2  g_par;

#define TAU 6.283185307179586

const vec3 C_DEEP   = vec3(0.016, 0.026, 0.062);
const vec3 C_MID    = vec3(0.043, 0.063, 0.125);   /* #0B1020 midnight     */
const vec3 C_VIOLET = vec3(0.400, 0.180, 0.880);   /* sapphire→midnight    */
const vec3 C_SAPPH  = vec3(0.184, 0.420, 1.000);   /* #2F6BFF sapphire     */
const vec3 C_CYAN   = vec3(0.180, 0.880, 1.000);   /* #5EEBFF, deepened    */
const vec3 C_EMER   = vec3(0.000, 0.784, 0.588);   /* #00C896 emeraldZ     */

/* Qamariya glass, from concept C: the apertures of the screen are leaded
   panes, and warm gold/amber against the brand's cool triad is the whole
   reason this frame is colourful rather than blue. */
const vec3 Q_GOLD  = vec3(1.00, 0.775, 0.360);
const vec3 Q_AMBER = vec3(1.00, 0.505, 0.150);
const vec3 Q_EMER  = vec3(0.110, 0.980, 0.680);
const vec3 Q_CYAN  = vec3(0.260, 0.880, 1.000);
const vec3 Q_SAPP  = vec3(0.220, 0.460, 1.000);

const mat2 ROT = mat2(0.80, 0.60, -0.60, 0.80);

float hash21(vec2 p) {
  p = fract(p * vec2(127.1, 311.7));
  p += dot(p, p + 34.56);
  return fract(p.x * p.y);
}

vec2 hash22(vec2 p) {
  vec3 p3 = fract(vec3(p.xyx) * vec3(0.1031, 0.1030, 0.0973));
  p3 += dot(p3, p3.yzx + 33.33);
  return fract((p3.xx + p3.yz) * p3.zy);
}

float vnoise(vec2 p) {
  vec2 i = floor(p), f = fract(p);
  vec2 u = f * f * (3.0 - 2.0 * f);
  float a = hash21(i);
  float b = hash21(i + vec2(1.0, 0.0));
  float c = hash21(i + vec2(0.0, 1.0));
  float d = hash21(i + vec2(1.0, 1.0));
  return mix(mix(a, b, u.x), mix(c, d, u.x), u.y);
}

float fbm4(vec2 p) {
  float s = 0.0, a = 0.5;
  for (int i = 0; i < 4; i++) { s += a * vnoise(p); p = ROT * p * 2.03 + 0.31; a *= 0.5; }
  return s;
}
float fbm2(vec2 p) {
  float s = 0.0, a = 0.62;
  for (int i = 0; i < 2; i++) { s += a * vnoise(p); p = ROT * p * 2.03 + 0.31; a *= 0.5; }
  return s;
}

/* ══════════════════════════════════════════════════════════════════════════
   THE OCCLUDER — A ROSTER MATRIX
   A's shafts are real shadows: every sample is projected back onto an occluder
   plane and asked how much light got through. The mechanic is general, so the
   plane can be anything — and on a workforce platform it should be the
   product's own core dataset rather than ornament borrowed from somewhere else.

   So the plane is a ROSTER. Columns are days, rows are people. A lit cell is a
   shift worked or a salary paid; a dark cell is absence. Two real rhythms beat
   through it:
     · two columns in every seven are the GCC weekend, so the grid breathes on
       a working-week period rather than looking random;
     · every fifteenth column a pay period CLOSES — that column runs fully open
       and warm, because that is the money leaving the building. The warm gold
       in this picture is payroll, not decoration.
   A handful of rows are dark right across the frame: someone on long leave.

   Abstract and architectural on purpose — a punched panel, never a readable
   chart. And cheap: a rounded-box SDF per cell, no transcendentals at all,
   which is what buys the 12-tap march its headroom.
   ══════════════════════════════════════════════════════════════════════════ */

/* One day-column by one person-row, in aspect-corrected y-units. */
const vec2  S_CELL = vec2(0.0880, 0.0500);
const vec2  S_GUT  = vec2(0.0135, 0.0098);   /* the metal between apertures  */
const float S_RAD  = 0.0058;
const float S_EDGE = 0.0016;                 /* a hard carved edge, not a glow */

/* Transmission through the roster plane. Returns the signed distance to the
   aperture edge (<0 inside the opening); 'lit' is whether the cell passes light
   at all; 'accent' marks a pay-period close. */
float rosterDist(vec2 q, out vec2 cid, out float lit, out float accent) {
  vec2 g  = q / S_CELL;
  vec2 id = floor(g);
  vec2 fp = (fract(g) - 0.5) * S_CELL;       /* offset from centre, y-units  */
  cid = id;

  /* the working week: Thursday and Friday are dark */
  float weekend = step(4.5, mod(id.x, 7.0));
  /* the pay period closing */
  accent = step(14.5, mod(id.x, 15.0));
  /* a few people are on long leave */
  float away = step(0.93, hash21(vec2(id.y * 1.7, 4.21)));

  float h = hash21(id + vec2(11.3, 5.9));
  float open = h * mix(1.0, 0.13, weekend) * (1.0 - away * 0.94);
  lit = max(smoothstep(0.29, 0.47, open), accent);

  vec2 hb = S_CELL * 0.5 - S_GUT;
  vec2 d = abs(fp) - hb + S_RAD;
  return min(max(d.x, d.y), 0.0) + length(max(d, 0.0)) - S_RAD;
}

/* The hue this cell passes. Concept C's per-aperture dispersion, kept: the
   brand's cool triad across the ordinary days, warm gold and amber on the pay
   close. Cool field, warm accent — that is what stops this being a blue
   picture. */
vec3 paneTint(vec2 cid, float accent) {
  float h = hash21(cid * 1.37 + vec2(3.11, 7.43));
  vec3 t = h < 0.44 ? Q_CYAN : (h < 0.72 ? Q_SAPP : (h < 0.91 ? Q_EMER : Q_GOLD));
  return mix(t, mix(Q_GOLD, Q_AMBER, hash21(cid.yx + 2.7)), accent);
}

/* The screen's own plane: aspect-corrected, parallaxed by the pointer, and
   drifting on a very long period so a still frame is still a composed one. */
vec2 screenPlane(vec2 uv, float asp) {
  return vec2(uv.x * asp, uv.y)
       + vec2(0.016 * u_ptr.x, 0.011 * u_ptr.y)
       + vec2(0.010 * sin(g_t * 0.026), -0.007 * cos(g_t * 0.021));
}

/* ══════════════════════════════════════════════════════════════════════════
   GRAFT A — VOLUMETRIC SHAFTS, OCCLUDED BY THE GIRIH SCREEN
   A's shafts are shadows: it projects each sample back onto the occluder plane
   and asks how much light got through. Here that occluder is C's screen, so
   the fan of light crossing the frame is literally the shadow of the rosettes,
   and each shaft carries the hue of the pane it passed through — including the
   warm ones. A few apertures are simply more open than the rest, so the beam
   has a hierarchy instead of an even corduroy of light.
   ══════════════════════════════════════════════════════════════════════════ */
vec3 shafts(vec2 uv, vec2 lightUV, float asp, float dither, out float energy) {
  const int N = 12;
  vec2 delta = (uv - lightUV) * (0.97 / float(N));
  vec2 p = uv - delta * dither;

  float illum = 1.0;
  vec3  acc   = vec3(0.0);
  float sum   = 0.0;

  for (int i = 0; i < N; i++) {
    p -= delta;
    vec2 cid; float lit; float accent;
    float d = rosterDist(screenPlane(p, asp), cid, lit, accent);
    float open = lit * (1.0 - smoothstep(-S_EDGE, S_EDGE, d));
    /* the pay close throws a hero shaft; ordinary days are quieter, so the
       beam has a hierarchy instead of an even corduroy of light */
    open *= mix(0.72, 2.70, accent);

    acc += open * illum * paneTint(cid, accent);
    sum += open * illum;
    illum *= 0.962;
  }

  float inv = 1.0 / float(N);
  energy = sum * inv;
  return acc * inv;
}

/* ══════════════════════════════════════════════════════════════════════════
   GRAFT A — PARALLAXING MOTES
   Sparse motes on a hashed lattice, called three times at three scales and
   three drift rates. The difference in parallax rate IS the depth cue.
   ══════════════════════════════════════════════════════════════════════════ */
float motes(vec2 uv, float scale, float rate, float seed, float thresh) {
  vec2 p  = uv * scale + vec2(seed * 17.0, -g_t * rate);
  vec2 g  = floor(p);
  vec2 f  = fract(p);
  float m = 0.0;
  for (int y = -1; y <= 1; y++) {
    for (int x = -1; x <= 1; x++) {
      vec2 o = vec2(float(x), float(y));
      vec2 h = hash22(g + o + seed * 41.0);
      if (h.x < thresh) continue;
      vec2  c = o + 0.18 + 0.64 * vec2(h.y, fract(h.x * 7.31));
      float d = length(f - c);
      float r = 0.030 + 0.055 * h.y;
      m += smoothstep(r, r * 0.12, d) * (0.45 + 0.55 * h.y);
    }
  }
  return m;
}

${field('fieldHi', 'fbm4')}
${field('fieldLo', 'fbm2')}

/* ══════════════════════════════════════════════════════════════════════════
   BASE B — THE GLASS
   ══════════════════════════════════════════════════════════════════════════ */

float sdBox(vec2 p, vec2 b, float r) {
  vec2 d = abs(p) - b + r;
  return min(max(d.x, d.y), 0.0) + length(max(d, 0.0)) - r;
}
vec2 sdNorm(vec2 p, vec2 b, float r) {
  vec2 e = vec2(1.0, 0.0);
  return normalize(vec2(
    sdBox(p + e.xy, b, r) - sdBox(p - e.xy, b, r),
    sdBox(p + e.yx, b, r) - sdBox(p - e.yx, b, r)) + 1e-6);
}

/* One pane of physical glass. 'shaftCol' is the volumetric light arriving at
   this pixel: the pane TRANSMITS it, which is what keeps the glass optically
   connected to the shafts crossing the frame behind it. */
vec3 glassPane(vec3 bg, vec2 px, vec2 uv, vec4 rect, float rad, float rot,
               float bevel, float refrAmt, float tintAmt, float sheenAmt,
               vec2 lightPx, vec3 shaftCol) {
  vec2  c  = rect.xy + rect.zw * 0.5;
  vec2  b  = rect.zw * 0.5;
  float cs = cos(rot), sn = sin(rot);
  vec2  q  = mat2(cs, -sn, sn, cs) * (px - c);
  float d  = sdBox(q, b, rad);

  vec3 col = bg;

  /* contact shadow + coloured bloom spilling off the edge */
  col = mix(col, col * 0.46, smoothstep(80.0, 1.0, d) * step(0.0, d) * 0.75);
  col += (bg * 0.80 + vec3(0.06, 0.22, 0.40)) * smoothstep(40.0, 0.0, abs(d)) * 0.34;

  if (d > 3.2) return col;

  float inside = smoothstep(0.8, -0.8, d);

  /* circular bevel: ti=0 at the edge, 1 once we are past the bevel width */
  float ti    = clamp(-d / bevel, 0.0, 1.0);
  float inv   = 1.0 - ti;
  float hgt   = sqrt(max(0.0, 1.0 - inv * inv));
  float slope = inv / max(hgt, 0.12);

  vec2 nl = sdNorm(q, b, rad);
  vec2 n  = mat2(cs, sn, -sn, cs) * nl;

  vec2 off = n * slope * refrAmt;
  vec2 jit = (vec2(hash21(px * 1.7), hash21(px * 1.7 + 37.1)) - 0.5) * 2.2;

  /* refraction, sampled once per channel → chromatic fringe at the rim */
  vec3 sA = fieldLo(uv + g_par + (off * 0.84 + jit) / u_res);
  vec3 sB = fieldLo(uv + g_par + (off * 1.00 + jit) / u_res);
  vec3 sC = fieldLo(uv + g_par + (off * 1.22 + jit) / u_res);
  vec3 refr = vec3(sA.r, sB.g, sC.b);

  /* the shafts refract too, at the same bevel slope — smeared, because the
     pane is frosted, which is also why the strapwork itself dissolves here */
  refr += shaftCol * (0.40 + 0.60 * ti) * 0.62;

  vec2  lp   = (q + b) / rect.zw;
  vec2  ldir = normalize(lightPx - c);
  float lam  = dot(n, ldir);
  float lamS = smoothstep(-0.60, 0.95, lam);

  vec3 rimCol = mix(vec3(0.14, 0.26, 0.54), vec3(0.84, 0.98, 1.00), lamS);
  rimCol = mix(rimCol, vec3(0.36, 1.00, 0.82), smoothstep(0.55, 1.0, lamS) * 0.34);

  vec3 interior = mix(vec3(0.018, 0.029, 0.066), refr, tintAmt);
  interior += refr * refr * 0.28;
  interior *= mix(1.26, 0.72, clamp(lp.x * 0.40 + lp.y * 0.70, 0.0, 1.0));
  /* Dichroic transmission: the pane is TINTED glass, not a neutral ND filter.
     Sapphire through the head of the sheet, emerald through its foot. */
  interior *= mix(vec3(0.78, 0.97, 1.18), vec3(0.74, 1.14, 1.00), clamp(lp.y, 0.0, 1.0));
  float il = dot(interior, vec3(0.2126, 0.7152, 0.0722));
  interior = mix(vec3(il), interior, 1.95);
  interior += rimCol * inv * inv * 0.22 * lamS;          /* inner bevel glow */

  float sw = lp.x * 0.70 + lp.y * 0.64;
  interior += vec3(0.58, 0.82, 1.00)
            * exp(-pow((sw - (0.30 + 0.32 * sin(g_t * 0.053))) * 3.0, 2.0)) * sheenAmt;
  interior += vec3(0.72, 1.00, 0.94)
            * exp(-pow((sw - (0.80 + 0.16 * sin(g_t * 0.039 + 2.1))) * 9.0, 2.0)) * sheenAmt * 0.75;

  /* hard ceiling on interior luminance — the text has to survive the field */
  interior = interior / (1.0 + interior * 1.62);

  col = mix(col, interior, inside);

  float rim = smoothstep(2.4, 0.0, abs(d));
  col = mix(col, col * 0.20, rim * (1.0 - lamS) * 0.85);      /* shadow rim  */
  col += rimCol * (0.18 + 1.55 * lamS) * rim * 1.00;          /* specular rim*/
  col += vec3(0.70, 0.90, 1.00)
       * exp(-pow((d + 2.2) / 1.3, 2.0)) * 0.16 * (0.25 + 0.75 * lamS);  /* hairline */

  return col;
}

void main() {
  g_t   = u_time * 0.42 + 42.0;
  g_par = u_ptr * vec2(0.016, 0.011);

  vec2 fc = gl_FragCoord.xy / u_dpr;
  vec2 px = vec2(fc.x, u_res.y - fc.y);
  vec2 uv = px / u_res;
  float asp = u_res.x / u_res.y;

  vec3 col = fieldHi(uv + g_par);

  /* THE SOURCE IS THE MARK. u_light is the live centre of the KynexOne logo
     element, measured from the DOM, so the rays provably leave the mark at
     every viewport instead of near it. Everything lit in this frame — the
     shafts, the roster panel they fall through, the rim on the glass — is
     that light. The brand is not lit by the scene; it IS the scene's light. */
  vec2  lightPx = u_light + vec2(5.0 * sin(g_t * 0.041), 4.0 * cos(g_t * 0.057));
  vec2  lightUV = lightPx / u_res;

  /* ── GRAFT A × the roster: the shafts, which ARE its shadow ───────── */
  float dither = hash21(px * 3.71 + fract(g_t) * 11.0);
  float energy;
  vec3  shaftCol = shafts(uv, lightUV, asp, dither, energy);

  vec2  toL  = vec2((uv.x - lightUV.x) * asp, uv.y - lightUV.y);
  float dl   = length(toL);
  /* Directional, not a starburst: the fan opens down and to the right, across
     the workforce structure and on to the glass. smoothstep() on dl keeps the
     immediate neighbourhood of the mark clean, so the wordmark beside it never
     sits in a ray. */
  float cone = mix(0.030, 1.00,
    smoothstep(-0.42, 0.99, dot(normalize(toL + 1e-5), vec2(0.755, 0.656))));
  float reach = exp(-dl * 0.40) * smoothstep(0.035, 0.20, dl);
  vec3  beam  = shaftCol * cone * reach;

  col += beam * 4.40;
  /* the source itself: a corona at the mark, so the rays are seen to LEAVE
     something rather than to arrive from off-frame */
  col += vec3(0.60, 0.86, 1.00) * exp(-dl * 7.6) * 0.62;
  col += vec3(0.42, 0.68, 1.00) * exp(-dl * 2.9) * 0.16;
  energy *= cone * reach;

  /* ── the plane itself, seen rather than inferred ───────────────────────
     Three gates keep it architecture instead of wallpaper: it lives near its
     own aperture and falls away from it; it keeps off the far left where the
     brand lockup sits; and its contrast is bought by the luminance of the
     field behind it, so the panel exists only where there is light to block. */
  vec2 cid; float lit; float accent;
  float dS = rosterDist(screenPlane(uv, asp), cid, lit, accent);
  float pxw = 1.0 / u_res.y;
  float metal = smoothstep(-pxw * 0.9, pxw * 0.9, dS);        /* 1 on the metal */
  /* one drawn lip on the inside of every opening: a milled edge */
  float lip = (1.0 - smoothstep(0.0, pxw * 1.6, abs(dS))) ;

  float bright = dot(col, vec3(0.2126, 0.7152, 0.0722));
  /* the plane hangs in the middle distance: present where the beam is, gone
     near the mark itself and gone again once the light has run out */
  float near = smoothstep(0.10, 0.34, dl) * exp(-dl * 0.92);
  float vis = smoothstep(0.050, 0.28, bright) * near
            * smoothstep(0.015, 0.24, energy) * 1.00;

  vec3 tint = paneTint(cid, accent);
  float aperture = (1.0 - metal) * lit * vis;
  col += tint * aperture * 0.26;

  col = mix(col, col * 0.40, metal * vis * 0.84);
  col += mix(vec3(0.62, 0.86, 1.00), tint, 0.60) * lip * lit * vis * 0.30;

  /* ── GRAFT A: motes, two layers behind the glass ──────────────────── */
  vec2 mw = vec2(uv.x * asp, uv.y);
  float moteLit = 0.10 + 1.35 * energy;
  col += motes(mw + u_ptr * 0.010, 11.4, 0.030, 1.0, 0.912)
       * mix(C_SAPPH, C_CYAN, 0.55) * moteLit * 0.55;
  col += motes(mw + u_ptr * 0.045,  6.8, 0.056, 2.0, 0.936)
       * mix(C_CYAN, Q_GOLD, 0.35) * moteLit * 0.44;

  /* Hue-preserving compression of the SUM. A scalar divide scales all three
     channels by the same factor, so the chroma direction is untouched — a
     per-channel tonemap here is what would turn every hot shaft grey. */
  float ci = max(max(col.r, col.g), col.b);
  col /= 1.0 + max(ci - 0.90, 0.0) * 0.92;

  /* ── BASE B: the two panes ────────────────────────────────────────── */
  col = glassPane(col, px, uv, u_back, u_rad.y, -0.056, 52.0, 26.0, 0.46, 0.015, lightPx, beam * 0.50);
  col = glassPane(col, px, uv, u_card, u_rad.x,  0.0,   34.0, 48.0, 0.60, 0.034, lightPx, beam * 0.38);

  /* ── GRAFT A: the near mote layer, IN FRONT of the glass ──────────── */
  col += motes(mw + u_ptr * 0.110, 3.9, 0.098, 3.0, 0.954)
       * vec3(0.84, 0.98, 1.00) * (0.05 + 0.65 * energy) * 0.26;

  col += (hash21(px * 1.31 + fract(g_t) * 7.0) - 0.5) * 0.008;   /* de-band */

  gl_FragColor = vec4(clamp(col, 0.0, 1.0), 1.0);
}
`;

/* ══════════════════════════════════════════════════════════════════════════
   SCENE
   ══════════════════════════════════════════════════════════════════════════ */

type UniformName =
  | 'u_res' | 'u_dpr' | 'u_time' | 'u_ptr' | 'u_card' | 'u_back' | 'u_rad'
  | 'u_light';

interface GLScene {
  program: WebGLProgram;
  quad: ScreenQuad;
  u: Record<UniformName, WebGLUniformLocation | null>;
}

interface Rect { x: number; y: number; w: number; h: number }

const BACK_INSET = { dx: -10, dy: -10, dw: 34, dh: 34 };

export interface LoginAuroraSceneProps {
  /** The untransformed box the glass pane is drawn around. */
  slotRef: RefObject<HTMLDivElement | null>;
  /** The element the scene parallaxes against the field. */
  paneRef?: RefObject<HTMLDivElement | null>;
  /**
   * The KynexOne mark. Its live centre IS the scene's key light, so the rays
   * originate from the brand at every viewport rather than from a constant
   * that merely happens to sit near it.
   */
  markRef?: RefObject<HTMLElement | null>;
  /** Changes whenever the card's size can have changed (auth state, error). */
  stateKey?: string;
  /**
   * Called once if the scene cannot hold a usable frame rate on this machine.
   * The caller is expected to stop rendering it; unmounting runs the hook's
   * teardown, which cancels the loop and releases the GL context.
   */
  onTooSlow?: () => void;
}

/* ── the frame budget ──────────────────────────────────────────────────────
   Measured on this branch with software rendering (SwiftShader, no GPU) the
   scene ran at a mean 142ms/frame — about 7fps — with 428 dropped frames in
   8 seconds. That is not a background; it is a stutter that reads as a broken
   page, and it costs battery to produce. A shader hero is worth having when
   the machine can draw it and worth abandoning when it cannot.

   So: ignore the first frames (shader compile, texture upload and first-paint
   contention all land there), then take a rolling mean. If the scene cannot
   beat ~22fps over a full sample window, it retires and the static CSS field
   — which is the same composition — is what the customer sees. One shot: it
   never oscillates, because retiring is permanent for the life of the page.

   A budget counted only in FRAMES decides slowest on exactly the machines it
   exists to rescue: 30 + 45 = 75 animated frames is 1.2s at 60fps, but 10.6s at
   7fps and ~25s at 3fps. The customer whose machine cannot draw this waits the
   longest to be let off it — backwards. Every window below therefore has a
   wall-clock twin, and whichever arrives first ends that phase. On a machine
   that holds frame rate the frame counts always win and nothing changes; on a
   slow one the scene now retires in about three seconds. */
const WARMUP_FRAMES = 30;
const SAMPLE_FRAMES = 45;
const SLOW_FRAME_MS = 45;
/** Wall-clock twin of WARMUP_FRAMES. 30 frames is 0.5s at 60fps, so this only
 *  binds when frames are already slower than ~50ms — which is itself the
 *  signal, and still leaves room for shader compile and first-paint contention. */
const WARMUP_MS = 1500;
/** Wall-clock twin of SAMPLE_FRAMES, with a floor on how few frames may decide:
 *  a mean over fewer than this is noise, not a measurement. 45 frames is 0.75s
 *  at 60fps, well inside SAMPLE_MS, so the fast path is untouched. */
const SAMPLE_MS = 1500;
const MIN_SAMPLE_FRAMES = 8;

export function LoginAuroraScene({
  slotRef, paneRef, markRef, stateKey, onTooSlow,
}: LoginAuroraSceneProps) {
  const scene = useRef<GLScene | null>(null);
  const rect = useRef<Rect>({ x: 820, y: 130, w: 420, h: 470 });
  const light = useRef({ x: 150, y: 150 });
  const broken = useRef(false);
  const [fallback, setFallback] = useState(false);
  /** Set whenever the slot or the mark can have moved; consumed in draw(). */
  const dirty = useRef(true);
  /** Frame-budget watchdog state. */
  const budget = useRef({
    seen: 0, acc: 0, n: 0, last: 0, retired: false,
    /** performance.now() of the first counted frame — starts the warmup clock. */
    firstAt: 0,
    /** performance.now() at which the current sample window opened. */
    sampleAt: 0,
  });
  /* Held in a ref so a caller passing an inline arrow cannot rebuild the
     scene, matching how useRenderCanvas treats its own callbacks. */
  const tooSlow = useRef(onTooSlow);
  tooSlow.current = onTooSlow;

  const measure = useCallback(() => {
    const el = slotRef.current;
    if (el) {
      const r = el.getBoundingClientRect();
      if (r.width >= 2 && r.height >= 2) {
        const c = rect.current;
        c.x = r.left; c.y = r.top; c.w = r.width; c.h = r.height;
      }
    }
    const m = markRef?.current;
    if (m) {
      const r = m.getBoundingClientRect();
      if (r.width >= 2) {
        light.current.x = r.left + r.width * 0.5;
        light.current.y = r.top + r.height * 0.5;
      }
    }
  }, [slotRef, markRef]);

  const { canvasRef, requestFrame } = useRenderCanvas<'webgl2' | 'webgl' | '2d'>({
    // A canvas that has handed out a WebGL context can never hand out a 2D one,
    // so the fallback remounts the element (see the `key` below) as well as
    // narrowing the mode list.
    modes: fallback ? (['2d'] as const) : (['webgl2', 'webgl', '2d'] as const),
    // 1.5, not 2: B's per-channel refraction and A's march are both fill-rate
    // bound, and a field this soft shows nothing at 2x that it does not at 1.5x.
    maxDpr: 1.5,
    pointerEase: 0.055,
    pointerTarget: 'parent',
    glAttributes: { alpha: false, antialias: false, depth: false },

    // A shader that fails to compile must not leave a customer looking at a
    // black rectangle. Report it, then remount on the 2D painter.
    onError(err) {
      if (!broken.current) {
        broken.current = true;
        // eslint-disable-next-line no-console
        console.error('[LoginAuroraScene] falling back to the 2D painter:', err);
        setFallback(true);
      }
    },

    setup(frame) {
      if (frame.mode === '2d') return;
      const gl = frame.ctx;
      const program = createProgram(gl, VERT, FRAG);
      const quad = createScreenQuad(gl, program);
      const u = getUniforms(gl, program, [
        'u_res', 'u_dpr', 'u_time', 'u_ptr', 'u_card', 'u_back', 'u_rad',
        'u_light',
      ] as const);
      scene.current = { program, quad, u };
      return () => {
        quad.dispose();
        gl.deleteProgram(program);
        scene.current = null;
      };
    },

    draw(frame) {
      const { width, height, pointer } = frame;

      /* Frame budget. Only animated frames count: a static frame (reduced
         motion, hidden tab) is drawn once and its interval is meaningless,
         and those paths are already cheap.

         Timed from performance.now() rather than frame.dt, because the hook
         clamps dt to 1/15s so that a long stall cannot teleport an
         animation. That clamp is right for the animation and wrong for
         measuring it: every frame worse than 66.7ms reports as 66.7ms, which
         is exactly the range this watchdog has to tell apart. */
      if (!frame.isStatic) {
        const b = budget.current;
        if (!b.retired) {
          const now = performance.now();
          const gap = b.last ? now - b.last : 0;
          b.last = now;
          b.seen += 1;
          if (!b.firstAt) b.firstAt = now;
          const warmedUp = b.seen > WARMUP_FRAMES || now - b.firstAt > WARMUP_MS;
          if (warmedUp && gap > 0) {
            if (!b.sampleAt) b.sampleAt = now;
            b.acc += gap;
            b.n += 1;
            // Whichever window closes first: enough frames, or enough time with
            // enough frames to mean anything.
            const decide = b.n >= SAMPLE_FRAMES
              || (b.n >= MIN_SAMPLE_FRAMES && now - b.sampleAt >= SAMPLE_MS);
            if (decide) {
              if (b.acc / b.n > SLOW_FRAME_MS) {
                b.retired = true;
                // eslint-disable-next-line no-console
                console.info(
                  `[LoginAuroraScene] retiring: ${(b.acc / b.n).toFixed(1)}ms/frame `
                  + `over ${b.n} frames exceeds the ${SLOW_FRAME_MS}ms budget. `
                  + 'Falling back to the static field.',
                );
                tooSlow.current?.();
                return;
              }
              // Survived this window — open a fresh one, clock included.
              b.acc = 0;
              b.n = 0;
              b.sampleAt = 0;
            }
          }
        }
      }

      /* The slot's position is re-read when something can have MOVED it —
         a scroll, a resize, a state change — and not on every frame.
         measure() is two getBoundingClientRect() calls, each of which forces
         a synchronous layout; doing that from inside rAF at 60fps is a
         layout thrash that shows up as dropped frames on the machines this
         scene is already most expensive on. `dirty` is set by the scroll and
         resize listeners below and by requestFrame()'s callers. */
      if (dirty.current) { dirty.current = false; measure(); }
      const c = rect.current;

      /* restrained parallax: the glass drifts against the field */
      const ox = pointer.x * 6;
      const oy = pointer.y * 4;
      if (paneRef?.current) {
        paneRef.current.style.transform =
          `translate3d(${ox.toFixed(2)}px, ${oy.toFixed(2)}px, 0)`;
      }

      if (frame.mode === '2d') { draw2d(frame, c, ox, oy); return; }

      const gl = frame.ctx;
      const s = scene.current;
      if (!s) return;

      gl.useProgram(s.program);
      gl.uniform2f(s.u.u_res, width, height);
      gl.uniform1f(s.u.u_dpr, frame.dpr);
      gl.uniform1f(s.u.u_time, frame.time);
      gl.uniform2f(s.u.u_ptr, pointer.x, pointer.y);
      gl.uniform4f(s.u.u_card, c.x + ox, c.y + oy, c.w, c.h);
      gl.uniform4f(
        s.u.u_back,
        c.x + ox + BACK_INSET.dx - BACK_INSET.dw / 2,
        c.y + oy + BACK_INSET.dy - BACK_INSET.dh / 2,
        c.w + BACK_INSET.dw,
        c.h + BACK_INSET.dh,
      );
      gl.uniform2f(s.u.u_rad, 24, 30);
      gl.uniform2f(s.u.u_light, light.current.x, light.current.y);
      s.quad.draw();
    },

    resize() { measure(); },
  });

  /* draw() writes an inline transform onto the pane for the parallax. If the
     scene goes away — retired for being too slow, or unmounted by the width
     gate — that transform would otherwise stick, leaving the card frozen a
     few pixels off centre with nothing left to move it back. */
  useEffect(() => () => {
    if (paneRef?.current) paneRef.current.style.transform = '';
  }, [paneRef]);

  useEffect(() => {
    const touch = () => { dirty.current = true; requestFrame(); };
    measure();
    requestFrame();
    const ro = new ResizeObserver(touch);
    if (slotRef.current) ro.observe(slotRef.current);
    if (markRef?.current) ro.observe(markRef.current);
    window.addEventListener('resize', touch);
    /* The shell — not the window — is the scroller here: .lx-shell is
       position:fixed with overflow-y:auto, so a tall auth state scrolls
       inside it and window scroll events never fire. Listening on the
       canvas's scrolling ancestor is what keeps the glass registered with
       the card it is drawn around. Capture, so it is heard wherever in the
       subtree the scroll originates. */
    window.addEventListener('scroll', touch, { passive: true, capture: true });
    return () => {
      ro.disconnect();
      window.removeEventListener('resize', touch);
      window.removeEventListener('scroll', touch, { capture: true });
    };
  }, [measure, requestFrame, slotRef, markRef]);

  // A state change resizes the card; repaint even when the loop is parked
  // (hidden tab, or prefers-reduced-motion) so the pane follows the form.
  useEffect(() => {
    dirty.current = true;
    measure();
    requestFrame();
    const id = window.setTimeout(() => {
      dirty.current = true; measure(); requestFrame();
    }, 60);
    return () => window.clearTimeout(id);
  }, [stateKey, measure, requestFrame]);

  return (
    <>
      <canvas key={fallback ? '2d' : 'gl'} ref={canvasRef} className="lx-canvas" aria-hidden="true" />
      <div className="lx-scrim" aria-hidden="true" />
    </>
  );
}

/* ══════════════════════════════════════════════════════════════════════════
   2D FALLBACK — a hand-painted approximation for contexts with no WebGL
   ══════════════════════════════════════════════════════════════════════════ */

function draw2d(
  frame: RenderFrame<'webgl2' | 'webgl' | '2d'>,
  c: Rect, ox: number, oy: number,
) {
  if (frame.mode !== '2d') return;
  const g = frame.ctx;
  const { width: W, height: H, time } = frame;
  const t = time * 0.42 + 42;

  g.fillStyle = '#05080F';
  g.fillRect(0, 0, W, H);

  const blobs: Array<[number, number, number, string]> = [
    [0.26 + 0.06 * Math.sin(t * 0.061), 0.30 + 0.11 * Math.cos(t * 0.083), 0.62, 'rgba(94,235,255,0.50)'],
    [0.78 + 0.05 * Math.cos(t * 0.047), 0.70 + 0.13 * Math.sin(t * 0.067), 0.70, 'rgba(47,107,255,0.58)'],
    [0.52 + 0.08 * Math.sin(t * 0.037), 0.12 + 0.09 * Math.cos(t * 0.053), 0.46, 'rgba(0,200,150,0.38)'],
    [0.16, 0.30, 0.40, 'rgba(255,190,110,0.34)'],
    [0.12, 0.86, 0.52, 'rgba(37,31,148,0.52)'],
  ];
  g.globalCompositeOperation = 'lighter';
  for (const [bx, by, br, col] of blobs) {
    const rad = br * Math.max(W, H);
    const grd = g.createRadialGradient(bx * W, by * H, 0, bx * W, by * H, rad);
    grd.addColorStop(0, col);
    grd.addColorStop(1, 'rgba(0,0,0,0)');
    g.fillStyle = grd;
    g.fillRect(0, 0, W, H);
  }

  // A flat stand-in for the shafts: a fan out of the same light position.
  const lx = 0.24 * W, ly = 0.30 * H;
  const hues = ['rgba(120,225,255,', 'rgba(255,196,110,', 'rgba(80,235,180,', 'rgba(110,150,255,'];
  for (let i = 0; i < 12; i += 1) {
    const a = 0.34 + i * 0.070;
    const len = Math.max(W, H) * 1.4;
    const wdt = 14 + 30 * (((i * 7919) % 11) / 11);
    const grd = g.createLinearGradient(lx, ly, lx + Math.cos(a) * len, ly + Math.sin(a) * len);
    grd.addColorStop(0, `${hues[i % hues.length]}0.17)`);
    grd.addColorStop(1, `${hues[i % hues.length]}0)`);
    g.fillStyle = grd;
    g.save();
    g.translate(lx, ly);
    g.rotate(a);
    g.fillRect(0, -wdt / 2, len, wdt);
    g.restore();
  }
  g.globalCompositeOperation = 'source-over';

  const rr = (x: number, y: number, w: number, h: number, r: number) => {
    g.beginPath();
    g.moveTo(x + r, y);
    g.arcTo(x + w, y, x + w, y + h, r);
    g.arcTo(x + w, y + h, x, y + h, r);
    g.arcTo(x, y + h, x, y, r);
    g.arcTo(x, y, x + w, y, r);
    g.closePath();
  };

  const bx = c.x + ox - 42, by = c.y + oy - 30;
  rr(bx, by, c.w + 56, c.h + 40, 30);
  g.fillStyle = 'rgba(8,14,30,0.42)';
  g.fill();
  g.strokeStyle = 'rgba(120,190,255,0.30)';
  g.lineWidth = 1;
  g.stroke();

  rr(c.x + ox, c.y + oy, c.w, c.h, 24);
  const gg = g.createLinearGradient(c.x, c.y, c.x + c.w, c.y + c.h);
  gg.addColorStop(0, 'rgba(12,21,45,0.80)');
  gg.addColorStop(1, 'rgba(4,8,18,0.90)');
  g.fillStyle = gg;
  g.fill();
  const sg = g.createLinearGradient(c.x, c.y, c.x + c.w, c.y + c.h);
  sg.addColorStop(0, 'rgba(180,240,255,0.80)');
  sg.addColorStop(0.5, 'rgba(70,120,210,0.25)');
  sg.addColorStop(1, 'rgba(0,200,150,0.35)');
  g.strokeStyle = sg;
  g.lineWidth = 1.2;
  g.stroke();
}

export default LoginAuroraScene;
