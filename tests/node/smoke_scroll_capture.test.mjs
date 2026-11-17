// Pure Node contract for the scroll-capture smoke's exported judges (goal316
// task #320): after the product's terminal Copy/Save close the screenshot
// scene WebView, only Node-held evidence remains. These tests prove the new
// judges fail closed without touching any page, desktop or clipboard: the
// media-type sniffing for the raw product resources, the session-original
// resource binding, and the terminal-export judge that requires the artifact
// on disk to be exactly the upload bytes bound to that very command. The
// scene-copy observer itself is covered by smoke_native_actions.test.mjs and
// is deliberately not restated here.
import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import path from 'node:path';
import { promisify } from 'node:util';
import { fileURLToPath, pathToFileURL } from 'node:url';

const exec = promisify(execFile);

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const { detectImageMediaType, assertScrollSessionOriginal,
  assertSceneTerminalExport, assertAnnotatedExportRectangle,
  assertRevisionBoundUpload,
  planAnnotationInjection } = await import(
  pathToFileURL(path.join(scriptDir, '..', '..', 'scripts', 'smoke_scroll_capture.mjs')).href);

setTimeout(() => {
  console.error('smoke_scroll_capture contract test hung for 60s.');
  process.exit(1);
}, 60000).unref();

// Importing the smoke module must not run main(): the guard keeps the script
// importable for these pure judges without requiring --product-root.
assert.equal(process.exitCode, undefined,
  'Importing smoke_scroll_capture.mjs must not execute the E2E main().');

const pngBytes = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 1, 2, 3, 4]);
const otherPngBytes = Buffer.from([...pngBytes.subarray(0, 8), 9, 9, 9, 9]);
const bmpBytes = Buffer.alloc(58);
bmpBytes[0] = 0x42;
bmpBytes[1] = 0x4d;

// 1. detectImageMediaType: real artifact forms pass, anything else fails closed.
{
  assert.equal(detectImageMediaType(pngBytes), 'image/png');
  assert.equal(detectImageMediaType(bmpBytes), 'image/bmp');
  assert.throws(() => detectImageMediaType(otherPngBytes.subarray(0, 7)), /missing/);
  assert.throws(() => detectImageMediaType(Buffer.from([0x42, 0x4d, 1])), /missing/);
  assert.throws(() => detectImageMediaType(
    Buffer.from([0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 1, 1])), /neither/);
  assert.throws(() => detectImageMediaType('not-a-buffer'), /missing/);
  console.log('case1 media-type-sniffing: ok');
}

// 2. assertScrollSessionOriginal: the original long image must be the raw
// product __resource bound to the session, with matching published metadata.
{
  const input = { url: 'https://app.vibeocr/__resource/scroll.bmp',
    mediaType: 'image/bmp', byteLength: bmpBytes.length };
  assert.equal(assertScrollSessionOriginal(input, bmpBytes), 'image/bmp');
  // The scroll original is never a PNG export at this point.
  assert.equal(assertScrollSessionOriginal(
    { url: 'https://app.vibeocr/__resource/x.png', mediaType: 'image/png' }, pngBytes),
  'image/png');
  assert.throws(() => assertScrollSessionOriginal(
    { url: 'file:///C:/evil.bmp', mediaType: 'image/bmp' }, bmpBytes), /__resource/);
  assert.throws(() => assertScrollSessionOriginal(
    { url: 'https://app.vibeocr/static/frame.bmp' }, bmpBytes), /__resource/);
  assert.throws(() => assertScrollSessionOriginal({ url: '' }, bmpBytes), /no original input/);
  assert.throws(() => assertScrollSessionOriginal({ mediaType: 'image/bmp' }, bmpBytes),
    /no original input/);
  // Published metadata must describe the actual bytes: a BMP original never
  // passes as the PNG the old non-terminal flow assumed.
  assert.throws(() => assertScrollSessionOriginal(
    { ...input, mediaType: 'image/png' }, bmpBytes), /differs from the actual/);
  assert.throws(() => assertScrollSessionOriginal(
    { ...input, byteLength: bmpBytes.length + 1 }, bmpBytes), /differs from the fetched/);
  // Omitted optional metadata stays tolerant (the URL and pixels still bind).
  assert.equal(assertScrollSessionOriginal(
    { url: 'https://app.vibeocr/__resource/a.bmp' }, bmpBytes), 'image/bmp');
  console.log('case2 scroll-session-original-binding: ok');
}

// 3. assertSceneTerminalExport: after the scene closed, only the Node-held
// timeline decides. The save/copy artifact must be the production PNG upload
// bound to this very command's resourceUri; every mismatch fails closed.
{
  const saveUri = `https://app.vibeocr/__annotation/${'a'.repeat(32)}`;
  const layerUri = `https://app.vibeocr/__annotation/${'b'.repeat(32)}`;
  const upload = (seq, order, resourceUri, status = 201) => ({ seq, requestAt: 1000,
    requestOrder: order, responseStatus: status, responseAt: 1000,
    responseOrder: order + 1, resourceUri, uriAt: 1000, uriOrder: order + 2,
    error: null,
    blobMeta: { mimeType: 'image/png', byteLength: pngBytes.length, problem: null } });
  const saveTimeline = (overrides = {}) => ({
    uploads: [upload(1, 1, layerUri), upload(2, 3, saveUri)],
    copyCommands: [{ envelopeId: 'cmd-save', at: 1000, order: 5, resourceUri: saveUri,
      action: 'saveScreenshotImage', scope: 'recognition' }],
    copyReceipts: [], closedAt: 1000, closedOrder: 7, crashedAt: null, ...overrides,
  });

  assert.equal(assertSceneTerminalExport('saveScreenshotImage', saveTimeline(),
    pngBytes, pngBytes, 'Save judge'), true);
  // A success receipt is welcome but never required; a failed receipt never passes.
  assert.equal(assertSceneTerminalExport('saveScreenshotImage', saveTimeline(
    { copyReceipts: [{ envelopeId: 'cmd-save', at: 1000, order: 6, ok: true,
      problemCode: null }] }), pngBytes, pngBytes, 'Save judge'), true);
  assert.throws(() => assertSceneTerminalExport('saveScreenshotImage', saveTimeline(
    { copyReceipts: [{ envelopeId: 'cmd-save', at: 1000, order: 6, ok: false,
      problemCode: 'desktop_command_failed' }] }), pngBytes, pngBytes, 'Save judge'),
  /host rejected/);
  // The saved file must be the bound upload bytes, not merely any PNG export
  // of the same scene (e.g. the automatic text-layer upload).
  assert.throws(() => assertSceneTerminalExport('saveScreenshotImage', saveTimeline(),
    pngBytes, otherPngBytes, 'Save judge'), /differ from the upload/);
  assert.throws(() => assertSceneTerminalExport('saveScreenshotImage', saveTimeline(),
    otherPngBytes, pngBytes, 'Save judge'), /differ from the upload/);
  // A BMP artifact (or a non-image blob) can never claim to be the PNG export.
  assert.throws(() => assertSceneTerminalExport('saveScreenshotImage', saveTimeline(),
    bmpBytes, pngBytes, 'Save judge'), /not the production PNG export/);
  // Missing Node-held upload bytes or artifact fail closed.
  assert.throws(() => assertSceneTerminalExport('saveScreenshotImage', saveTimeline(),
    pngBytes, Buffer.alloc(0), 'Save judge'), /upload bytes are missing/);
  assert.throws(() => assertSceneTerminalExport('saveScreenshotImage', saveTimeline(),
    Buffer.alloc(0), pngBytes, 'Save judge'), /artifact bytes are missing/);
  // No such terminal command (scene closed for another reason) fails closed.
  assert.throws(() => assertSceneTerminalExport('saveScreenshotImage',
    saveTimeline({ copyCommands: [] }), pngBytes, pngBytes, 'Save judge'),
  /exactly one public saveScreenshotImage/);
  // The copy action is a different command and cannot satisfy the save judge.
  assert.throws(() => assertSceneTerminalExport('copyScreenshotImage', saveTimeline(),
    pngBytes, pngBytes, 'Save judge'), /exactly one public copyScreenshotImage/);
  // Crash instead of the normal post-export close never passes.
  assert.throws(() => assertSceneTerminalExport('saveScreenshotImage',
    saveTimeline({ crashedAt: 1000 }), pngBytes, pngBytes, 'Save judge'),
  /crashed instead of closing/);
  console.log('case3 scene-terminal-export-judge: ok');
}

// 4. assertAnnotatedExportRectangle: the annotated-pixel judge for the same
// revision's exports. The export must carry the stroke inside the expected
// rectangle (image-pixel mapping from the real editor canvas) and stay clean
// outside it. Existing ClearType colors cannot prove a new annotation.
{
  const orange = [243, 139, 53];
  const background = () => [210, 210, 210];
  const rgbaImage = (width, height, paint) => {
    const data = Buffer.alloc(width * height * 4);
    for (let y = 0; y < height; y++)
      for (let x = 0; x < width; x++) {
        const [r, g, b] = paint(x, y);
        const index = (y * width + x) * 4;
        data[index] = r; data[index + 1] = g; data[index + 2] = b; data[index + 3] = 255;
      }
    return { width, height, rgbaBase64: data.toString('base64') };
  };
  const strokeRect = (rect, half = 2) => (x, y) => {
    const withinVertical = x >= rect.left - half && x <= rect.right + half &&
      (Math.abs(y - rect.top) <= half || Math.abs(y - rect.bottom) <= half);
    const withinHorizontal = y >= rect.top - half && y <= rect.bottom + half &&
      (Math.abs(x - rect.left) <= half || Math.abs(x - rect.right) <= half);
    return (withinVertical || withinHorizontal) ? orange : background();
  };
  const options = { margin: 6, minEdgePixels: 6, context: 'Judge case' };
  const rect = { left: 20, top: 15, right: 60, bottom: 45 };
  const original = rgbaImage(80, 60, background);

  // Correct rectangle passes and reports the edge evidence.
  const verdict = assertAnnotatedExportRectangle(
    rgbaImage(80, 60, strokeRect(rect)), original, rect, options);
  assert(verdict.strayPixels === 0 &&
    Object.values(verdict.edgeCounts).every((count) => count >= 6));
  assert(Object.values(verdict.lineCoordinates)
    .every((coordinate) => Number.isSafeInteger(coordinate)));
  // Correct strokes cannot conceal corrupt pixels, including alpha or the
  // unpainted interior of the rectangle.
  for (const [x, y, channel] of [[1, 1, 0], [40, 30, 1], [1, 1, 3]]) {
    const corrupt = rgbaImage(80, 60, strokeRect(rect));
    const pixels = Buffer.from(corrupt.rgbaBase64, 'base64');
    pixels[(y * 80 + x) * 4 + channel] ^= 1;
    corrupt.rgbaBase64 = pixels.toString('base64');
    assert.throws(() => assertAnnotatedExportRectangle(corrupt, original, rect, options),
      /unannotated pixels differ/);
  }
  // The unannotated original (an export of the wrong revision) fails closed.
  assert.throws(() => assertAnnotatedExportRectangle(original, original, rect, options),
    /is not annotated there/);
  // Orange drawn in the wrong place never satisfies the expected bands.
  assert.throws(() => assertAnnotatedExportRectangle(
    rgbaImage(80, 60, strokeRect({ left: 2, top: 2, right: 12, bottom: 12 })),
    original, rect, options), /is not annotated there|outside the expected rectangle/);
  // Dimension or payload mismatch fails closed.
  assert.throws(() => assertAnnotatedExportRectangle(
    rgbaImage(79, 60, strokeRect(rect)), original, rect, options), /width differs/);
  assert.throws(() => assertAnnotatedExportRectangle(
    { width: 80, height: 60, rgbaBase64: 'AAAA' }, original, rect, options),
    /not width\*height\*4/);
  assert.throws(() => assertAnnotatedExportRectangle(
    rgbaImage(80, 60, strokeRect(rect)), original,
    { left: 70, top: 55, right: 79, bottom: 59 }, options), /is outside the image|not annotated there/);

  // Real ClearType fringes made the former zero-original-color assertion fail.
  const fringe = [224, 156, 72]; // Inside tolerance of the orange stroke.
  const coloredBase = rgbaImage(80, 60, (x, y) =>
    ((x + y) % 7 === 0 ? fringe : background()));
  const overStroke = strokeRect(rect);
  const coloredExport = rgbaImage(80, 60, (x, y) => {
    const [r, g, b] = overStroke(x, y);
    return (r === orange[0] && g === orange[1] && b === orange[2])
      ? orange : ((x + y) % 7 === 0 ? fringe : background());
  });
  const coloredVerdict = assertAnnotatedExportRectangle(coloredExport, coloredBase,
    rect, options);
  assert(Object.values(coloredVerdict.newEdgeCounts).every((count) => count >= 6));
  assert(Object.values(coloredVerdict.originalEdgeCounts).every((count) => count > 0));
  assert(Object.values(coloredVerdict.lineCoordinates)
    .every((coordinate) => Number.isSafeInteger(coordinate)));
  assert.equal(coloredVerdict.strayPixels, 0);
  // Re-exporting the colored base supplies no new annotation evidence.
  assert.throws(() => assertAnnotatedExportRectangle(coloredBase, coloredBase,
    rect, options), /gained only 0 new exact opaque stroke pixels/);

  // F1: near(export) + any RGBA delta used to count as new stroke, so an
  // alpha-only edit or a reverse drift that merely stays inside tolerance
  // could fake >= minEdgePixels per band. Neither is this round's exact
  // opaque public color, so both must fail closed.
  const mutate = (image, edits) => {
    const pixels = Buffer.from(image.rgbaBase64, 'base64');
    for (const edit of edits) {
      const index = (edit.y * image.width + edit.x) * 4;
      if (edit.setAlpha !== undefined) pixels[index + 3] = edit.setAlpha;
      if (edit.rgb) {
        pixels[index] = edit.rgb[0];
        pixels[index + 1] = edit.rgb[1];
        pixels[index + 2] = edit.rgb[2];
      }
    }
    return { ...image, rgbaBase64: pixels.toString('base64') };
  };
  const topBandFringe = [];
  for (let y = rect.top - 6; y <= rect.top + 6; y++)
    for (let x = rect.left + 6; x <= rect.right - 6; x++)
      if ((x + y) % 7 === 0) topBandFringe.push({ x, y });
  assert(topBandFringe.length >= 20, 'Colored-base density is unusable.');
  // Alpha-only edit over unchanged fringe RGB (old judge: near + changed).
  assert.throws(() => assertAnnotatedExportRectangle(
    mutate(coloredBase, topBandFringe.map((p) => ({ ...p, setAlpha: 100 }))),
    coloredBase, rect, options), /gained only 0 new exact opaque stroke pixels/);
  // Reverse drift: R moves FURTHER from the public color yet stays near.
  assert.throws(() => assertAnnotatedExportRectangle(
    mutate(coloredBase, topBandFringe.slice(0, 20).map((p) => ({ ...p, rgb: [223, 156, 72] }))),
    coloredBase, rect, options), /gained only 0 new exact opaque stroke pixels/);

  // F2: counts alone cannot prove a rectangle edge; concentrated or gapped
  // and misaligned exact-opaque segments must fail the line-shape evidence.
  const dashEdits = [];
  for (let x = rect.left + 6; x <= rect.left + 11; x++) dashEdits.push({ x, y: rect.top - 4, rgb: orange });
  for (let x = rect.right - 12; x <= rect.right - 6; x++) dashEdits.push({ x, y: rect.top + 4, rgb: orange });
  assert.throws(() => assertAnnotatedExportRectangle(
    mutate(coloredBase, dashEdits), coloredBase, rect, options),
    /no complete fixed-coordinate line/);
  const gapEdits = [];
  for (let x = rect.left + 6; x <= rect.right - 6; x += 2)
    gapEdits.push({ x, y: rect.top, rgb: orange });
  assert.throws(() => assertAnnotatedExportRectangle(
    mutate(coloredBase, gapEdits), coloredBase, rect, options),
    /no complete fixed-coordinate line/);
  // F2 combined counterexample: the ORIGINAL already carries a complete
  // exact-orange line in every band and the export adds 12 exact dots at
  // OTHER fixed coordinates per band. Line evidence and count evidence must
  // come from the SAME line: the pre-existing line contributes zero new
  // pixels along it, so the pre-fix judge (separate line + separate count)
  // wrongly passed this; it must fail closed now.
  const spanX = rect.right - 6 - (rect.left + 6) + 1;
  const spanY = rect.bottom - 6 - (rect.top + 6) + 1;
  const prelinedBase = mutate(coloredBase, [
    ...Array.from({ length: spanX }, (_, i) => ({ x: rect.left + 6 + i, y: rect.top, rgb: orange })),
    ...Array.from({ length: spanX }, (_, i) => ({ x: rect.left + 6 + i, y: rect.bottom, rgb: orange })),
    ...Array.from({ length: spanY }, (_, i) => ({ x: rect.left, y: rect.top + 6 + i, rgb: orange })),
    ...Array.from({ length: spanY }, (_, i) => ({ x: rect.right, y: rect.top + 6 + i, rgb: orange })),
  ]);
  const dottedNoNewLine = mutate(prelinedBase, [
    ...Array.from({ length: 12 }, (_, i) => ({ x: rect.left + 6 + i, y: rect.top - 4, rgb: orange })),
    ...Array.from({ length: 12 }, (_, i) => ({ x: rect.left + 6 + i, y: rect.bottom - 4, rgb: orange })),
    ...Array.from({ length: 12 }, (_, i) => ({ x: rect.left - 4, y: rect.top + 6 + i, rgb: orange })),
    ...Array.from({ length: 12 }, (_, i) => ({ x: rect.right - 4, y: rect.top + 6 + i, rgb: orange })),
  ]);
  assert.throws(() => assertAnnotatedExportRectangle(dottedNoNewLine, prelinedBase,
    rect, options), /no complete fixed-coordinate line/);
  // An added stroke outside the expected rectangle still fails.
  const misplaced = rgbaImage(80, 60, (x, y) => {
    const [r, g, b] = overStroke(x, y);
    if (r === orange[0] && g === orange[1] && b === orange[2]) return orange;
    if (x >= 74 && x <= 79 && y >= 10 && y <= 20) return orange; // Outside the ring.
    return (x + y) % 7 === 0 ? fringe : background();
  });
  assert.throws(() => assertAnnotatedExportRectangle(misplaced, coloredBase,
    rect, options), /NEW stroke-colored pixels appear outside the expected rectangle/);
  console.log('case4 annotated-export-rectangle-judge: ok');
}

// 5. planAnnotationInjection: the editor's real fit (rotation 0): logical
// coordinate space from data-coordinate-*, scale=min(logicalW/imgW,
// logicalH/imgH) with centering, then the CSS bounding box. Non-1:1 and
// letterbox cases must map the expected NATURAL fractions onto the injected
// CSS points; the judge keeps using the natural rectangle.
{
  const fractions = { x0: 0.35, y0: 0.45, x1: 0.65, y1: 0.55 };
  const css = (width, height, x = 10, y = 20) => ({ x, y, width, height });

  // 1:1 logical == natural: injection equals natural offsets plus css origin.
  {
    const plan = planAnnotationInjection({ fractions,
      natural: { width: 900, height: 600 }, logical: { width: 900, height: 600 },
      css: css(450, 300) });
    assert.deepEqual(plan.naturalRect, { left: 315, top: 270, right: 585, bottom: 330 });
    assert.deepEqual(plan.layout, { scale: 1, offsetX: 0, offsetY: 0 });
    assert.equal(plan.start.x, 10 + 315 * 0.5);
    assert.equal(plan.start.y, 20 + 270 * 0.5);
    assert.equal(plan.end.x, 10 + 585 * 0.5);
    assert.equal(plan.end.y, 20 + 330 * 0.5);
  }

  // Tall long image in a wide logical space (letterbox on X): height-limited
  // scale, horizontal centering; a viewport-fraction drag would land on the
  // letterbox instead of the image.
  {
    const natural = { width: 428, height: 664 };
    const logical = { width: 900, height: 600 };
    const plan = planAnnotationInjection({ fractions, natural, logical, css: css(900, 600, 0, 0) });
    const scale = Math.min(900 / 428, 600 / 664);
    assert.equal(plan.layout.scale, scale);
    assert.equal(plan.layout.offsetX, (900 - 428 * scale) / 2);
    assert.equal(plan.layout.offsetY, 0);
    assert.deepEqual(plan.naturalRect,
      { left: Math.round(428 * 0.35), top: Math.round(664 * 0.45),
        right: Math.round(428 * 0.65), bottom: Math.round(664 * 0.55) });
    // Injection follows the fit, NOT the viewport fractions.
    assert(Math.abs(plan.start.x - 900 * 0.35) > 10,
      'letterbox x mapping must differ from naive viewport fractions');
    assert.ok(Math.abs(plan.start.y - 600 * 0.45) < 1, 'y follows the height-limited fit');
  }

  // CSS aspect different from logical aspect maps each axis independently.
  {
    const plan = planAnnotationInjection({ fractions,
      natural: { width: 400, height: 300 }, logical: { width: 800, height: 600 },
      css: css(400, 450, 5, 7) });
    assert.deepEqual(plan.layout, { scale: 2, offsetX: 0, offsetY: 0 });
    assert.equal(plan.start.x, 5 + 140 * 2 * 0.5);
    assert.equal(plan.start.y, 7 + 135 * 2 * 0.75);
  }

  // Wide short image in a taller logical space (letterbox on Y): width-limited
  // scale, vertical centering.
  {
    const plan = planAnnotationInjection({ fractions,
      natural: { width: 1000, height: 400 }, logical: { width: 900, height: 600 },
      css: css(450, 300, 0, 0) });
    const scale = Math.min(900 / 1000, 600 / 400);
    assert.equal(plan.layout.scale, scale);
    assert.equal(plan.layout.offsetX, 0);
    assert.equal(plan.layout.offsetY, (600 - 400 * scale) / 2);
    // X fills the width (offsetX=0); only Y is letterboxed.
    assert.equal(plan.start.x, Math.round(1000 * 0.35) * scale * (450 / 900));
    assert.equal(plan.start.y,
      (plan.layout.offsetY + Math.round(400 * 0.45) * scale) * (300 / 600));
  }

  // Out-of-range fractions and unusable geometry fail closed.
  assert.throws(() => planAnnotationInjection({ fractions: { x0: 0.01, y0: 0.45, x1: 0.65, y1: 0.55 },
    natural: { width: 100, height: 100 }, logical: { width: 100, height: 100 },
    css: css(100, 100) }), /out of the safely drawable range/);
  assert.throws(() => planAnnotationInjection({ fractions: { x0: 0.2, y0: 0.3, x1: 0.25, y1: 0.9 },
    natural: { width: 100, height: 100 }, logical: { width: 100, height: 100 },
    css: css(100, 100) }), /do not describe a usable rectangle/);
  assert.throws(() => planAnnotationInjection({ fractions,
    natural: { width: 0, height: 100 }, logical: { width: 100, height: 100 },
    css: css(100, 100) }), /geometry is invalid/);
  console.log('case5 annotation-injection-mapping: ok');
}

// 6. assertRevisionBoundUpload: the prepared flow's pixel source must be
// the accepted 201 upload whose lease (resourceUri) was consumed by the
// public command bound to THIS session/revision - the auto text layer and
// the explicit recognition can each export multiple revisions, so the first
// upload or a stale revision must never satisfy the binding. The command
// must use the production scene scope (recognition), a product __annotation
// resource, strict upload-before-command ordering, and no failure receipt.
{
  const session = 'scroll-session';
  const uri = (name) => `https://app.vibeocr/__annotation/${name}`;
  const command = (name, action, revision, order, scope = 'recognition',
    resourceUri) => ({ envelopeId: `cmd-${name}`, at: 1000, order, action,
    scope, resourceUri: resourceUri ?? uri(name), sessionId: session, revision });
  const upload = (name, requestOrder, responseOrder, responseStatus = 201) =>
    ({ seq: 1, requestAt: 1000, requestOrder, responseStatus, responseAt: 1000,
      responseOrder, resourceUri: uri(name), uriAt: 1000, uriOrder: responseOrder,
      error: null, blobMeta: { mimeType: 'image/png', byteLength: 12, problem: null } });
  const timeline = {
    copyCommands: [
      command('rev0', 'prepareScreenshotTextLayer', 0, 3),
      command('rev1', 'prepareScreenshotTextLayer', 1, 6),
      command('rec1', 'recognizeScreenshotImage', 1, 9),
    ],
    uploads: [upload('rev0', 1, 2), upload('rev1', 4, 5), upload('rec1', 7, 8)],
    copyReceipts: [],
  };

  // The final annotated revision binds to its own command and upload.
  const bound = assertRevisionBoundUpload(timeline, 'prepareScreenshotTextLayer',
    session, 1, 'Binding judge');
  assert.equal(bound.command.resourceUri, uri('rev1'));
  assert.equal(bound.upload.resourceUri, uri('rev1'));
  const recognized = assertRevisionBoundUpload(timeline, 'recognizeScreenshotImage',
    session, 1, 'Binding judge');
  assert.equal(recognized.upload.resourceUri, uri('rec1'));
  // A stale revision (the pre-annotation export) never satisfies the binding.
  assert.throws(() => assertRevisionBoundUpload(timeline, 'recognizeScreenshotImage',
    session, 0, 'Binding judge'), /found 0/);
  assert.throws(() => assertRevisionBoundUpload(timeline, 'prepareScreenshotTextLayer',
    'other-session', 1, 'Binding judge'), /found 0/);
  // Duplicate commands for the same revision (incl. the verify-time final
  // re-check catching a LATE duplicate) fail closed.
  assert.throws(() => assertRevisionBoundUpload({ ...timeline,
    copyCommands: [...timeline.copyCommands,
      command('dup', 'prepareScreenshotTextLayer', 1, 12)] },
    'prepareScreenshotTextLayer', session, 1, 'Binding judge'), /found 2/);
  // A rejected upload never passes.
  assert.throws(() => assertRevisionBoundUpload({ ...timeline,
    uploads: [upload('rev0', 1, 2), upload('rev1', 4, 5, 413), upload('rec1', 7, 8)] },
    'prepareScreenshotTextLayer', session, 1, 'Binding judge'), /not accepted/);
  // A command whose lease has no matching upload record never passes.
  assert.throws(() => assertRevisionBoundUpload({ ...timeline,
    uploads: [upload('rev0', 1, 2), upload('rec1', 7, 8)] },
    'prepareScreenshotTextLayer', session, 1, 'Binding judge'),
    /match exactly one upload/);
  // Wrong production scope, non-product resource URIs and unsound ordering
  // each fail closed (minimal one-field counterexamples).
  assert.throws(() => assertRevisionBoundUpload({ ...timeline,
    copyCommands: [command('rev1', 'prepareScreenshotTextLayer', 1, 6, 'imageEdit'),
      timeline.copyCommands[0], timeline.copyCommands[2]] },
    'prepareScreenshotTextLayer', session, 1, 'Binding judge'),
    /production scene scope/);
  assert.throws(() => assertRevisionBoundUpload({ ...timeline,
    copyCommands: [timeline.copyCommands[0],
      command('rev1', 'prepareScreenshotTextLayer', 1, 6, 'recognition',
        'https://evil.example/__annotation/rev1'),
      timeline.copyCommands[2]] },
    'prepareScreenshotTextLayer', session, 1, 'Binding judge'),
    /not a product .*__annotation/);
  assert.throws(() => assertRevisionBoundUpload({ ...timeline,
    copyCommands: [timeline.copyCommands[0],
      command('rev1', 'prepareScreenshotTextLayer', 1, 6, 'recognition',
        'https://app.vibeocr/__resource/rev1'),
      timeline.copyCommands[2]] },
    'prepareScreenshotTextLayer', session, 1, 'Binding judge'),
    /not a product .*__annotation/);
  assert.throws(() => assertRevisionBoundUpload({ ...timeline,
    uploads: [upload('rev0', 1, 2), upload('rev1', 5, 6), upload('rec1', 7, 8)] },
    'prepareScreenshotTextLayer', session, 1, 'Binding judge'),
    /ordering is unsound/);
  // A failure receipt for the very matched envelope is a host rejection.
  assert.throws(() => assertRevisionBoundUpload({ ...timeline,
    copyReceipts: [{ envelopeId: 'cmd-rev1', at: 1000, order: 7,
      ok: false, problemCode: 'desktop_command_failed' }] },
    'prepareScreenshotTextLayer', session, 1, 'Binding judge'), /host rejected/);
  // Missing timeline evidence fails closed.
  assert.throws(() => assertRevisionBoundUpload(null, 'prepareScreenshotTextLayer',
    session, 1, 'Binding judge'), /timeline evidence is missing/);
  console.log('case6 prepared-revision-upload-binding: ok');
}

// 7. Dynamic-fixture content contract (#321): executes the fixture's real
// dynamic paint path off-screen (no window, no input; same non-GUI
// self-check pattern as smoke_windows_interaction case4). Asserts on the
// actually rendered rows: vertical texture (the old solid-color paint failed
// here and let a stray identical sample trip the low-texture first-frame
// error), per-tick change, no shift the product's minimum overlap would
// accept as scrolling, and a positive control proving the translation judge
// really detects a rendered shift.
if (process.platform === 'win32') {
  const { stdout } = await exec('pwsh', ['-NoProfile', '-NonInteractive', '-File',
    path.join(scriptDir, '..', '..', 'scripts', 'scroll_capture_fixture.ps1'),
    '-Action', 'dynamic-selfcheck'], { timeout: 45000, windowsHide: true });
  const report = JSON.parse(stdout.trim());
  assert.equal(report.ok, true,
    `Dynamic fixture off-screen self-check failed: ${stdout.trim()}`);
  assert(report.distinctRowColors >= 2,
    `Dynamic frames must carry vertical texture; got ${report.distinctRowColors} row colors.`);
  assert.equal(report.framesDiffer, true,
    'Dynamic frames must change across tick phases.');
  assert(Number.isInteger(report.controlShiftDetected) && report.controlShiftDetected !== 0,
    'Positive control shift was not detected by the translation judge.');
  assert.equal(report.translatableShift, null,
    'Dynamic frames must never satisfy the product-overlap translation judge.');
  console.log('case7 dynamic-fixture-content-selfcheck: ok');
} else {
  console.log('case7 windows-only: skipped (non-Windows)');
}

console.log('smoke_scroll_capture pure-judge contract: ok');
