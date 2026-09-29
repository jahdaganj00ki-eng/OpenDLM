#!/usr/bin/env node
/*
 * make-icons.mjs - generates the OpenDLM extension icons.
 *
 * Pure Node.js: only node:zlib, node:fs and node:path are used, and the PNG
 * encoder is written by hand so the repository keeps its zero-dependency
 * promise. Run it with:
 *
 *   node scripts/make-icons.mjs
 *
 * It writes icons/icon16.png, icons/icon32.png, icons/icon48.png and
 * icons/icon128.png next to the extension. The result is deterministic, so
 * re-running never produces a spurious diff.
 *
 * The mark is original: a rounded-square tile with a blue-to-teal gradient and
 * a bold white downward arrow dropping into an open tray.
 */

import { deflateSync } from 'node:zlib';
import { mkdirSync, writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join, resolve } from 'node:path';

/* ------------------------------------------------------------------ *
 * Minimal PNG encoder (8-bit RGBA, no interlacing, single IDAT)
 * ------------------------------------------------------------------ */

const CRC_TABLE = (() => {
  const table = new Int32Array(256);
  for (let n = 0; n < 256; n += 1) {
    let c = n;
    for (let k = 0; k < 8; k += 1) {
      c = (c & 1) ? (0xedb88320 ^ (c >>> 1)) : (c >>> 1);
    }
    table[n] = c;
  }
  return table;
})();

function crc32(buffer) {
  let c = 0xffffffff;
  for (let i = 0; i < buffer.length; i += 1) {
    c = CRC_TABLE[(c ^ buffer[i]) & 0xff] ^ (c >>> 8);
  }
  return (c ^ 0xffffffff) >>> 0;
}

function pngChunk(type, data) {
  const length = Buffer.alloc(4);
  length.writeUInt32BE(data.length, 0);

  const typeBytes = Buffer.from(type, 'ascii');
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(Buffer.concat([typeBytes, data])), 0);

  return Buffer.concat([length, typeBytes, data, crc]);
}

/**
 * Encodes a tightly packed RGBA byte array as a PNG.
 * @param {number} width
 * @param {number} height
 * @param {Uint8Array} rgba width * height * 4 bytes
 * @returns {Buffer}
 */
function encodePng(width, height, rgba) {
  const expected = width * height * 4;
  if (rgba.length !== expected) {
    throw new Error(`RGBA buffer is ${rgba.length} bytes, expected ${expected}.`);
  }

  const signature = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);

  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(width, 0);
  ihdr.writeUInt32BE(height, 4);
  ihdr[8] = 8; // bit depth
  ihdr[9] = 6; // colour type: truecolour with alpha
  ihdr[10] = 0; // compression: deflate
  ihdr[11] = 0; // filter method: adaptive
  ihdr[12] = 0; // interlace: none

  const stride = width * 4;
  const raw = Buffer.alloc((stride + 1) * height);
  for (let y = 0; y < height; y += 1) {
    const rowStart = y * (stride + 1);
    raw[rowStart] = 0; // filter type 0 (None) for every scanline
    for (let i = 0; i < stride; i += 1) {
      raw[rowStart + 1 + i] = rgba[y * stride + i];
    }
  }

  const idat = deflateSync(raw, { level: 9 });

  return Buffer.concat([
    signature,
    pngChunk('IHDR', ihdr),
    pngChunk('IDAT', idat),
    pngChunk('IEND', Buffer.alloc(0)),
  ]);
}

/* ------------------------------------------------------------------ *
 * Signed distance helpers. All coordinates are normalised to 0..1.
 * ------------------------------------------------------------------ */

function clamp01(value) {
  return value < 0 ? 0 : value > 1 ? 1 : value;
}

/** Signed distance to a rounded rectangle. Negative means inside. */
function sdRoundRect(px, py, cx, cy, halfW, halfH, radius) {
  const qx = Math.abs(px - cx) - (halfW - radius);
  const qy = Math.abs(py - cy) - (halfH - radius);
  const ax = Math.max(qx, 0);
  const ay = Math.max(qy, 0);
  return Math.hypot(ax, ay) + Math.min(Math.max(qx, qy), 0) - radius;
}

/** Signed distance to a capsule (line segment with a radius). */
function sdCapsule(px, py, ax, ay, bx, by, radius) {
  const pax = px - ax;
  const pay = py - ay;
  const bax = bx - ax;
  const bay = by - ay;
  const lengthSq = bax * bax + bay * bay;
  const h = lengthSq === 0 ? 0 : clamp01((pax * bax + pay * bay) / lengthSq);
  return Math.hypot(pax - bax * h, pay - bay * h) - radius;
}

/** Signed distance to a convex polygon given as [[x, y], ...]. */
function sdConvex(px, py, points) {
  const n = points.length;
  let nearest = Infinity;
  let negative = 0;
  let positive = 0;

  for (let i = 0; i < n; i += 1) {
    const [ax, ay] = points[i];
    const [bx, by] = points[(i + 1) % n];
    nearest = Math.min(nearest, sdCapsule(px, py, ax, ay, bx, by, 0));

    const cross = (bx - ax) * (py - ay) - (by - ay) * (px - ax);
    if (cross < 0) {
      negative += 1;
    } else {
      positive += 1;
    }
  }

  return (negative === 0 || positive === 0) ? -nearest : nearest;
}

/** Converts a distance in normalised units into 0..1 coverage. */
function coverage(distance, pixelsPerUnit) {
  return clamp01(0.5 - distance * pixelsPerUnit);
}

/* ------------------------------------------------------------------ *
 * The OpenDLM mark
 * ------------------------------------------------------------------ */

/** Tile gradient, roughly #2563EB -> #0D9488 along the diagonal. */
const GRADIENT_FROM = [38, 102, 236];
const GRADIENT_TO = [13, 150, 138];

/** The glyph, in normalised tile coordinates. */
const GLYPH = {
  shaft: [0.5, 0.238, 0.5, 0.505, 0.058],
  head: [[0.5, 0.678], [0.338, 0.456], [0.662, 0.456]],
  trayLeft: [0.238, 0.648, 0.238, 0.768, 0.055],
  trayRight: [0.762, 0.648, 0.762, 0.768, 0.055],
  trayBottom: [0.238, 0.768, 0.762, 0.768, 0.055],
};

const TILE_INSET = 0.02;
const TILE_RADIUS = 0.215;
const SUPERSAMPLE = 4;

/**
 * Renders the mark at `size` x `size`, supersampled and box-downsampled.
 * Returns a tightly packed RGBA byte array.
 */
function renderIcon(size) {
  const hi = size * SUPERSAMPLE;
  const pixelsPerUnit = hi;

  // Premultiplied RGBA scratch buffer, so edge averaging cannot produce halos.
  const accum = new Float64Array(size * size * 4);

  for (let y = 0; y < hi; y += 1) {
    const py = (y + 0.5) / hi;

    for (let x = 0; x < hi; x += 1) {
      const px = (x + 0.5) / hi;

      const tileDistance = sdRoundRect(px, py, 0.5, 0.5, 0.5 - TILE_INSET, 0.5 - TILE_INSET, TILE_RADIUS);
      const tileAlpha = coverage(tileDistance, pixelsPerUnit);

      // Everything below is only interesting where the tile exists.
      let red = 0;
      let green = 0;
      let blue = 0;
      let alpha = 0;

      if (tileAlpha > 0) {
        const mix = clamp01(px * 0.45 + py * 0.55);
        let cr = GRADIENT_FROM[0] + (GRADIENT_TO[0] - GRADIENT_FROM[0]) * mix;
        let cg = GRADIENT_FROM[1] + (GRADIENT_TO[1] - GRADIENT_FROM[1]) * mix;
        let cb = GRADIENT_FROM[2] + (GRADIENT_TO[2] - GRADIENT_FROM[2]) * mix;

        // Soft sheen in the upper-left keeps the flat tile from looking dead.
        const sheen = Math.max(0, 1 - Math.hypot(px - 0.26, py - 0.16) / 0.92) * 26;
        cr = Math.min(255, cr + sheen);
        cg = Math.min(255, cg + sheen);
        cb = Math.min(255, cb + sheen);

        const glyphDistance = Math.min(
          sdCapsule(px, py, ...GLYPH.shaft),
          sdConvex(px, py, GLYPH.head),
          sdCapsule(px, py, ...GLYPH.trayLeft),
          sdCapsule(px, py, ...GLYPH.trayRight),
          sdCapsule(px, py, ...GLYPH.trayBottom),
        );
        const glyphAlpha = coverage(glyphDistance, pixelsPerUnit) * tileAlpha;

        red = cr * (1 - glyphAlpha) + 255 * glyphAlpha;
        green = cg * (1 - glyphAlpha) + 255 * glyphAlpha;
        blue = cb * (1 - glyphAlpha) + 255 * glyphAlpha;
        alpha = tileAlpha;
      }

      const outX = (x / SUPERSAMPLE) | 0;
      const outY = (y / SUPERSAMPLE) | 0;
      const index = (outY * size + outX) * 4;
      accum[index] += red * alpha;
      accum[index + 1] += green * alpha;
      accum[index + 2] += blue * alpha;
      accum[index + 3] += alpha;
    }
  }

  const samples = SUPERSAMPLE * SUPERSAMPLE;
  const rgba = new Uint8Array(size * size * 4);

  for (let i = 0; i < size * size; i += 1) {
    const index = i * 4;
    const alphaSum = accum[index + 3] / samples;
    if (alphaSum <= 0.0001) {
      continue;
    }

    // Un-premultiply using the supersampled coverage.
    const coverageSum = accum[index + 3];
    rgba[index] = Math.round(clamp01(accum[index] / coverageSum / 255) * 255);
    rgba[index + 1] = Math.round(clamp01(accum[index + 1] / coverageSum / 255) * 255);
    rgba[index + 2] = Math.round(clamp01(accum[index + 2] / coverageSum / 255) * 255);
    rgba[index + 3] = Math.round(clamp01(alphaSum) * 255);
  }

  return rgba;
}

/* ------------------------------------------------------------------ *
 * Entry point
 * ------------------------------------------------------------------ */

const SIZES = [16, 32, 48, 128];

function main() {
  const here = dirname(fileURLToPath(import.meta.url));
  const outputDirectory = resolve(here, '..', 'extension', 'icons');
  mkdirSync(outputDirectory, { recursive: true });

  for (const size of SIZES) {
    const rgba = renderIcon(size);
    const png = encodePng(size, size, rgba);
    const target = join(outputDirectory, `icon${size}.png`);
    writeFileSync(target, png);
    console.log(`wrote ${target} (${size}x${size}, ${png.length} bytes)`);
  }

  console.log(`Done: ${SIZES.length} icons in ${outputDirectory}`);
}

main();
