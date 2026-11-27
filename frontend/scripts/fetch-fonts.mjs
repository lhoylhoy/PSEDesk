#!/usr/bin/env node
/**
 * Refreshes the bundled typeface — Outfit — from the Google Fonts API.
 *
 *   node scripts/fetch-fonts.mjs            # rewrite public/fonts/*.woff2 + OFL.txt
 *   node scripts/fetch-fonts.mjs --check    # report only; exit 1 if a subset is missing
 *
 * Why a fetcher instead of "whatever the CDN link served that day": the terminal ships its font
 * (the @font-face block in src/index.css) so that loading the desk makes no request to a third
 * party — no IP handed to a font CDN, and the app still renders with the API on loopback and
 * nothing else. Committing the binary keeps that promise, and this script is the only accepted
 * way to change it, so the exact bytes are reproducible instead of hand-downloaded.
 *
 * One subset, deliberately. The API also serves `latin-ext`, and its `unicode-range` even claims
 * the peso sign (U+20B1, inside U+20AD-20C4), but the upstream `Outfit[wght].ttf` was measured on
 * 2026-11-26: its cmap has 360 glyphs and no U+20B1, no direction triangles (U+25B2/U+25BC) and no
 * → (U+2192, which the served `latin` range omits) — a `unicode-range` is a claim, not coverage.
 * Those characters therefore keep resolving through the system font at ~14.8 kB less, so SUBSETS
 * stays at `latin`; widen it only with a measurement that shows a real gain.
 */
import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const fontDir = resolve(here, '../public/fonts');

/** The variable-weight CSS endpoint. `wght@100..900` is what keeps one file per subset. */
const CSS_URL = 'https://fonts.googleapis.com/css2?family=Outfit:wght@100..900&display=swap';
const LICENCE_URL = 'https://raw.githubusercontent.com/google/fonts/main/ofl/outfit/OFL.txt';

/** The subsets index.css declares. Keep this list and the @font-face blocks in step. */
const SUBSETS = ['latin'];

/** A modern Chrome UA is what makes the API answer woff2 + variable `font-weight: 100 900`. */
const DESKTOP_UA =
  'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) ' +
  'Chrome/131.0 Safari/537.36';

const checkOnly = process.argv.includes('--check');

function fileFor(subset) {
  return join(fontDir, `outfit-${subset}.woff2`);
}

/**
 * Splits the Google Fonts sheet into `{ subset, url }` pairs. The sheet is a flat list of
 * `/* subset *\/` comments followed by one `@font-face` block, which is stable enough to parse
 * without a CSS dependency.
 */
function parseFontFaces(css) {
  const faces = [];
  const pattern = /\/\*\s*([\w-]+)\s*\*\/\s*@font-face\s*\{([\s\S]*?)\}/g;

  for (const match of css.matchAll(pattern)) {
    const [, subset, body] = match;
    const url = body.match(/url\(([^)]+)\)/);
    if (!url) {
      continue;
    }

    faces.push({ subset, url: url[1].replace(/['"]/g, '') });
  }

  return faces;
}

async function main() {
  mkdirSync(fontDir, { recursive: true });

  const css = await fetch(CSS_URL, { headers: { 'User-Agent': DESKTOP_UA } });
  if (!css.ok) {
    throw new Error(`Google Fonts answered HTTP ${css.status} for the Outfit sheet.`);
  }

  const faces = parseFontFaces(await css.text());
  /** @type {string[]} */
  const missing = [];

  for (const subset of SUBSETS) {
    const face = faces.find((candidate) => candidate.subset === subset);
    if (!face) {
      missing.push(subset);
      continue;
    }

    if (checkOnly) {
      console.log(`${subset.padEnd(9)} ${face.url}`);
      continue;
    }

    const response = await fetch(face.url, { headers: { 'User-Agent': DESKTOP_UA } });
    if (!response.ok) {
      throw new Error(`Subset '${subset}' answered HTTP ${response.status}.`);
    }

    const bytes = Buffer.from(await response.arrayBuffer());
    // A font CDN answering an HTML error page with a 200 must not overwrite a good binary.
    if (bytes.subarray(0, 4).toString('latin1') !== 'wOF2') {
      throw new Error(`Subset '${subset}' did not come back as WOFF2 (${bytes.length} bytes).`);
    }

    writeFileSync(fileFor(subset), bytes);
    console.log(`${`outfit-${subset}.woff2`.padEnd(34)} ${bytes.length} bytes`);
  }

  if (missing.length > 0) {
    throw new Error(
      `The Google Fonts sheet no longer serves: ${missing.join(', ')}. ` +
        'Update SUBSETS and the @font-face blocks in src/index.css together.',
    );
  }

  if (checkOnly) {
    return;
  }

  const licence = await fetch(LICENCE_URL);
  if (!licence.ok) {
    throw new Error(`The OFL licence answered HTTP ${licence.status}.`);
  }

  const licenceText = await licence.text();
  // OFL §1 requires the licence to travel with the font, so it is shipped next to the binary.
  if (!licenceText.includes('SIL OPEN FONT LICENSE')) {
    throw new Error('The licence file does not look like the SIL Open Font License.');
  }

  writeFileSync(join(fontDir, 'OFL.txt'), licenceText);
  console.log(`OFL.txt                                   ${licenceText.length} bytes`);
}

main().catch((error) => {
  console.error(`fetch-fonts: ${error.message}`);
  process.exit(1);
});
