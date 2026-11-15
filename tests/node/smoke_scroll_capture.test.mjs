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
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const { detectImageMediaType, assertScrollSessionOriginal,
  assertSceneTerminalExport, assertAnnotatedExportRectangle,
  planAnnotationInjection } = await import(
  pathToFileURL(path.join(scriptDir, '..', '..', 'scripts', 'smoke_scroll_capture.mjs')).href);

setTimeout(() => {
  console.error('smoke_scroll_capture contract test hung for 30s.');
  process.exit(1);
}, 30000).unref();

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
// revision's exports. The original has no stroke color; the export must carry
// the stroke inside the expected rectangle (image-pixel mapping from the real
// editor canvas) and stay clean outside it.
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

console.log('smoke_scroll_capture pure-judge contract: ok');
