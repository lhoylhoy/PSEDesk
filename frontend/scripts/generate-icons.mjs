#!/usr/bin/env node
/**
 * Builds the desk icon set from the one geometry the header badge renders.
 *
 *   node scripts/generate-icons.mjs                     # rewrite public/favicon.* + the touch icon
 *   node scripts/generate-icons.mjs --preview out.png   # also dump a 512px PNG for eyeballing
 *
 * Why a generator instead of three hand-made binaries: the tab icon, the iOS touch icon and the
 * in-app badge must never drift apart. They are the *same* drawing (`src/lib/deskMarkGeometry.ts`,
 * imported below), so moving a candle once updates all three — and every committed bitmap can be
 * reproduced from this file.
 *
 * No dependency is added: `node:zlib` deflates the PNG scanlines and the ICO container is written
 * by hand. ICO entries are PNG-compressed (supported since Windows Vista), which is why there is no
 * BMP encoder here.
 */
import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { deflateSync } from 'node:zlib';
import {
  DESK_MARK_BODY_RADIUS,
  DESK_MARK_BODY_WIDTH,
  DESK_MARK_CANDLES,
  DESK_MARK_COLORS,
  DESK_MARK_TILE_RADIUS,
  DESK_MARK_VIEWBOX,
  DESK_MARK_WICK_WIDTH,
} from '../src/lib/deskMarkGeometry.ts';

const here = dirname(fileURLToPath(import.meta.url));
const publicDir = resolve(here, '../public');

/* ---------------------------------------------------------------------------------------------
 * Vector: the same geometry as an SVG document.
 * ------------------------------------------------------------------------------------------- */

/** Trims float noise (`4.8999999999999995` → `4.9`) so the emitted SVG stays readable. */
function num(value) {
  return Number(value.toFixed(3));
}

/** One candle as an SVG group. Group `opacity` keeps the wick/body overlap from darkening. */
function candleSvg(candle) {
  const wickX = candle.center - DESK_MARK_WICK_WIDTH / 2;
  const bodyX = candle.center - DESK_MARK_BODY_WIDTH / 2;
  const wick = `<rect x="${num(wickX)}" y="${num(candle.wickTop)}" width="${num(DESK_MARK_WICK_WIDTH)}" height="${num(candle.wickBottom - candle.wickTop)}" rx="${num(DESK_MARK_WICK_WIDTH / 2)}"/>`;
  const body = `<rect x="${num(bodyX)}" y="${num(candle.bodyTop)}" width="${num(DESK_MARK_BODY_WIDTH)}" height="${num(candle.bodyBottom - candle.bodyTop)}" rx="${num(DESK_MARK_BODY_RADIUS)}"/>`;

  return `  <g fill="${DESK_MARK_COLORS.mint}" opacity="${candle.opacity}">\n    ${wick}\n    ${body}\n  </g>`;
}

export function buildFaviconSvg() {
  return [
    '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 32 32" width="32" height="32" role="img" aria-label="PSE Signal Desk">',
    '  <title>PSE Signal Desk</title>',
    `  <rect width="${DESK_MARK_VIEWBOX}" height="${DESK_MARK_VIEWBOX}" rx="${DESK_MARK_TILE_RADIUS}" fill="${DESK_MARK_COLORS.tile}"/>`,
    ...DESK_MARK_CANDLES.map(candleSvg),
    '</svg>',
    '',
  ].join('\n');
}

/* ---------------------------------------------------------------------------------------------
 * Raster: supersampled coverage of the same shapes, then a hand-rolled PNG.
 * ------------------------------------------------------------------------------------------- */

/** Point-in-rounded-rect test; a sample in the corner band is measured against the corner circle. */
function insideRoundedRect(x, y, left, top, width, height, radius) {
  const localX = x - left;
  const localY = y - top;

  if (localX < 0 || localY < 0 || localX > width || localY > height) {
    return false;
  }

  if (radius <= 0) {
    return true;
  }

  const nearestX = Math.min(Math.max(localX, radius), width - radius);
  const nearestY = Math.min(Math.max(localY, radius), height - radius);
  const dx = localX - nearestX;
  const dy = localY - nearestY;

  return dx * dx + dy * dy <= radius * radius;
}

function hexToRgb(hex) {
  return [
    Number.parseInt(hex.slice(1, 3), 16),
    Number.parseInt(hex.slice(3, 5), 16),
    Number.parseInt(hex.slice(5, 7), 16),
  ];
}

const [tileR, tileG, tileB] = hexToRgb(DESK_MARK_COLORS.tile);
const [mintR, mintG, mintB] = hexToRgb(DESK_MARK_COLORS.mint);


/**
 * Renders the mark at `size`×`size` into straight-alpha RGBA.
 *
 * `rounded` is false for the iOS touch icon: iOS applies its own squircle mask and paints any
 * transparent corner black, so that icon must be a full-bleed opaque square.
 */
function renderRgba(size, { rounded }) {
  const scale = DESK_MARK_VIEWBOX / size;
  // Small icons benefit most from extra samples and cost almost nothing; large ones do not need it.
  const samples = size <= 48 ? 8 : 4;
  const total = samples * samples;
  const rgba = Buffer.alloc(size * size * 4);
  const tileRadius = rounded ? DESK_MARK_TILE_RADIUS : 0;

  for (let py = 0; py < size; py += 1) {
    for (let px = 0; px < size; px += 1) {
      let covered = 0;
      let red = 0;
      let green = 0;
      let blue = 0;

      for (let sy = 0; sy < samples; sy += 1) {
        for (let sx = 0; sx < samples; sx += 1) {
          const x = (px + (sx + 0.5) / samples) * scale;
          const y = (py + (sy + 0.5) / samples) * scale;

          if (!insideRoundedRect(x, y, 0, 0, DESK_MARK_VIEWBOX, DESK_MARK_VIEWBOX, tileRadius)) {
            continue;
          }

          // The tile is opaque, so a covered sample always resolves to one solid colour.
          let r = tileR;
          let g = tileG;
          let b = tileB;

          for (const candle of DESK_MARK_CANDLES) {
            const inWick = insideRoundedRect(
              x,
              y,
              candle.center - DESK_MARK_WICK_WIDTH / 2,
              candle.wickTop,
              DESK_MARK_WICK_WIDTH,
              candle.wickBottom - candle.wickTop,
              DESK_MARK_WICK_WIDTH / 2,
            );
            const inBody =
              inWick ||
              insideRoundedRect(
                x,
                y,
                candle.center - DESK_MARK_BODY_WIDTH / 2,
                candle.bodyTop,
                DESK_MARK_BODY_WIDTH,
                candle.bodyBottom - candle.bodyTop,
                DESK_MARK_BODY_RADIUS,
              );

            if (inBody) {
              r = mintR * candle.opacity + r * (1 - candle.opacity);
              g = mintG * candle.opacity + g * (1 - candle.opacity);
              b = mintB * candle.opacity + b * (1 - candle.opacity);
            }
          }

          covered += 1;
          red += r;
          green += g;
          blue += b;
        }
      }

      const offset = (py * size + px) * 4;
      if (covered > 0) {
        rgba[offset] = Math.round(red / covered);
        rgba[offset + 1] = Math.round(green / covered);
        rgba[offset + 2] = Math.round(blue / covered);
        rgba[offset + 3] = Math.round((covered / total) * 255);
      }
    }
  }

  return rgba;
}

const CRC_TABLE = (() => {
  const table = new Uint32Array(256);
  for (let n = 0; n < 256; n += 1) {
    let c = n;
    for (let k = 0; k < 8; k += 1) {
      c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    }
    table[n] = c >>> 0;
  }
  return table;
})();

function crc32(buffer) {
  let c = 0xffffffff;
  for (const byte of buffer) {
    c = CRC_TABLE[(c ^ byte) & 0xff] ^ (c >>> 8);
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

/** 8-bit RGBA PNG, filter type 0 on every scanline. */
function encodePng(size, rgba) {
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(size, 0);
  ihdr.writeUInt32BE(size, 4);
  ihdr[8] = 8; // bit depth
  ihdr[9] = 6; // colour type: truecolour with alpha
  ihdr[10] = 0; // deflate
  ihdr[11] = 0; // adaptive filtering
  ihdr[12] = 0; // no interlace

  const stride = size * 4;
  const raw = Buffer.alloc((stride + 1) * size);
  for (let y = 0; y < size; y += 1) {
    raw[y * (stride + 1)] = 0;
    rgba.copy(raw, y * (stride + 1) + 1, y * stride, (y + 1) * stride);
  }

  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    pngChunk('IHDR', ihdr),
    pngChunk('IDAT', deflateSync(raw, { level: 9 })),
    pngChunk('IEND', Buffer.alloc(0)),
  ]);
}

/** ICO container holding PNG entries (Vista+); 16/32/48 covers tabs, bookmarks and shortcuts. */
function encodeIco(images) {
  const header = Buffer.alloc(6);
  header.writeUInt16LE(0, 0); // reserved
  header.writeUInt16LE(1, 2); // 1 = icon
  header.writeUInt16LE(images.length, 4);

  const entries = Buffer.alloc(16 * images.length);
  let offset = header.length + entries.length;

  images.forEach((image, index) => {
    const at = index * 16;
    entries[at] = image.size; // width (0 would mean 256)
    entries[at + 1] = image.size; // height
    entries[at + 2] = 0; // palette size
    entries[at + 3] = 0; // reserved
    entries.writeUInt16LE(1, at + 4); // colour planes
    entries.writeUInt16LE(32, at + 6); // bits per pixel
    entries.writeUInt32LE(image.png.length, at + 8);
    entries.writeUInt32LE(offset, at + 12);
    offset += image.png.length;
  });

  return Buffer.concat([header, entries, ...images.map((image) => image.png)]);
}

/* ---------------------------------------------------------------------------------------------
 * Write.
 * ------------------------------------------------------------------------------------------- */

function main() {
  const previewIndex = process.argv.indexOf('--preview');
  const previewPath = previewIndex >= 0 ? process.argv[previewIndex + 1] : null;

  mkdirSync(publicDir, { recursive: true });

  const svg = buildFaviconSvg();
  writeFileSync(join(publicDir, 'favicon.svg'), svg, 'utf8');

  const icoPngs = [16, 32, 48].map((size) => ({
    size,
    png: encodePng(size, renderRgba(size, { rounded: true })),
  }));
  const ico = encodeIco(icoPngs);
  writeFileSync(join(publicDir, 'favicon.ico'), ico);

  // Full-bleed square: iOS masks it itself and fills transparent corners black.
  const touch = encodePng(180, renderRgba(180, { rounded: false }));
  writeFileSync(join(publicDir, 'apple-touch-icon.png'), touch);

  console.log(`favicon.svg            ${svg.length} bytes`);
  console.log(
    `favicon.ico            ${ico.length} bytes (${icoPngs.map((image) => image.size).join('/')} px)`,
  );
  console.log(`apple-touch-icon.png   ${touch.length} bytes (180 px)`);

  if (previewPath) {
    const preview = encodePng(512, renderRgba(512, { rounded: true }));
    writeFileSync(previewPath, preview);
    console.log(`preview                ${preview.length} bytes -> ${previewPath}`);
  }
}

main();

