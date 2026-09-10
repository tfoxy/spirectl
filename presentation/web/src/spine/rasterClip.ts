import { diagnostic, spineFail, spineIntoOk, spineOk, type SpineDiagnostic, type SpineResult } from "./diagnostics";

const MAGIC = 0x4c435053; // "SPCL", interpreted as a little-endian u32.
const LEGACY_VERSION = 0;
const PRODUCTION_VERSION = 2;
const HEADER_BYTES = 40;
const FRAME_HEADER_BYTES = 28;

export interface SpinePlacement {
  readonly canvasWidth: number;
  readonly canvasHeight: number;
  readonly localX: number;
  readonly localY: number;
  readonly localWidth: number;
  readonly localHeight: number;
}

/** Encoded image data. Decoding and ownership belong to the consuming renderer. */
export interface RasterSpineFrame {
  readonly index: number;
  readonly offsetX: number;
  readonly offsetY: number;
  readonly width: number;
  readonly height: number;
  readonly durationMs: number;
  readonly startMs: number;
  readonly imageBytes: Uint8Array;
}

/** The pure binary representation emitted by the existing raster Spine clip producer. */
export interface RasterSpineClip extends SpinePlacement {
  readonly totalDurationMs: number;
  readonly frames: readonly RasterSpineFrame[];
}

export interface RasterSpineFrameSample {
  readonly frameIndex: number;
  readonly frame: RasterSpineFrame;
  readonly placement: SpinePlacement;
}

/** Reusable output for {@link sampleRasterSpineClipInto}. The fields are updated in place. */
export interface RasterSpineFrameSampleScratch {
  frameIndex: number;
  frame: RasterSpineFrame | null;
  readonly placement: {
    canvasWidth: number;
    canvasHeight: number;
    localX: number;
    localY: number;
    localWidth: number;
    localHeight: number;
  };
}

function finite(value: number): boolean {
  return Number.isFinite(value);
}

/**
 * Decode the legacy `SPCL` v0 or production v2 wire format without decoding image payloads.
 * Invalid data is never partially exposed.
 */
export function parseRasterSpineClip(input: ArrayBuffer | Uint8Array): SpineResult<RasterSpineClip> {
  const bytes = input instanceof Uint8Array ? input : new Uint8Array(input);
  if (bytes.byteLength < HEADER_BYTES) {
    return spineFail(diagnostic("truncated-data", "header", "SPCL header is truncated."));
  }
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  if (view.getUint32(0, true) !== MAGIC) {
    return spineFail(diagnostic("invalid-value", "header.magic", "Expected SPCL magic."));
  }
  const version = view.getUint8(4);
  if (version !== LEGACY_VERSION && version !== PRODUCTION_VERSION) {
    return spineFail(diagnostic("unsupported-version", "header.version", "Only SPCL v0 and v2 are supported."));
  }

  const frameCount = view.getUint32(8, true);
  const canvasWidth = view.getUint32(12, true);
  const canvasHeight = view.getUint32(16, true);
  const totalDurationMs = view.getUint32(20, true);
  const localX = view.getFloat32(24, true);
  const localY = view.getFloat32(28, true);
  const localWidth = view.getFloat32(32, true);
  const localHeight = view.getFloat32(36, true);
  if (frameCount === 0 || canvasWidth === 0 || canvasHeight === 0) {
    return spineFail(diagnostic("invalid-value", "header", "Frame count and canvas dimensions must be positive."));
  }
  if (![localX, localY, localWidth, localHeight].every(finite) || localWidth < 0 || localHeight < 0) {
    return spineFail(diagnostic("invalid-value", "header.placement", "Placement must contain finite, non-negative dimensions."));
  }

  const frames: RasterSpineFrame[] = [];
  let offset = HEADER_BYTES;
  let startMs = 0;
  for (let ordinal = 0; ordinal < frameCount; ordinal += 1) {
    const path = `frames[${ordinal}]`;
    if (offset + FRAME_HEADER_BYTES > bytes.byteLength) {
      return spineFail(diagnostic("truncated-data", path, "Frame header is truncated."));
    }
    const index = view.getUint32(offset, true);
    const offsetX = view.getInt32(offset + 4, true);
    const offsetY = view.getInt32(offset + 8, true);
    const width = view.getUint32(offset + 12, true);
    const height = view.getUint32(offset + 16, true);
    const durationMs = view.getUint32(offset + 20, true);
    const imageLength = view.getUint32(offset + 24, true);
    offset += FRAME_HEADER_BYTES;
    if (index !== ordinal || width === 0 || height === 0 || durationMs === 0 || imageLength === 0) {
      return spineFail(diagnostic("invalid-value", path, "Frame index, dimensions, duration, and image payload must be valid."));
    }
    if (imageLength > bytes.byteLength - offset) {
      return spineFail(diagnostic("truncated-data", `${path}.imageBytes`, "Frame image payload is truncated."));
    }
    // Keep every frame as a view into the original clip. A multi-frame bake can be several megabytes;
    // copying each payload here doubles its retained memory before the consuming renderer decodes it.
    frames.push({ index, offsetX, offsetY, width, height, durationMs, startMs, imageBytes: bytes.subarray(offset, offset + imageLength) });
    offset += imageLength;
    startMs += durationMs;
  }
  if (offset !== bytes.byteLength) {
    return spineFail(diagnostic("inconsistent-data", "payload", "SPCL has trailing bytes."));
  }
  if (startMs !== totalDurationMs) {
    return spineFail(diagnostic("inconsistent-data", "header.totalDurationMs", "Total duration does not equal frame durations."));
  }
  return spineOk({ canvasWidth, canvasHeight, totalDurationMs, localX, localY, localWidth, localHeight, frames });
}

/** Deterministically select a clip frame. Looping skips an endpoint duplicate tail by default. */
export function rasterSpineFrameIndexAt(
  clip: RasterSpineClip,
  timeMs: number,
  loop = true,
  skipLoopEndpoint = true
): SpineResult<number> {
  const selected = rasterSpineFrameIndex(clip, timeMs, loop, skipLoopEndpoint);
  return typeof selected === "number" ? spineOk(selected) : spineFail(selected);
}

function rasterSpineFrameIndex(
  clip: RasterSpineClip,
  timeMs: number,
  loop: boolean,
  skipLoopEndpoint: boolean
): number | SpineDiagnostic {
  if (!finite(timeMs)) {
    return diagnostic("invalid-value", "timeMs", "Playback time must be finite.");
  }
  const { frames } = clip;
  if (frames.length === 0) {
    return diagnostic("inconsistent-data", "frames", "A clip must contain at least one frame.");
  }
  if (frames.length === 1) {
    return 0;
  }
  const lastStart = frames[frames.length - 1].startMs;
  const period = loop && skipLoopEndpoint && lastStart > 0 ? lastStart : clip.totalDurationMs;
  if (!(period > 0)) {
    return diagnostic("inconsistent-data", "totalDurationMs", "Clip duration must be positive.");
  }
  const t = loop ? ((timeMs % period) + period) % period : Math.max(0, Math.min(timeMs, period - 1));
  let low = 0;
  let high = frames.length - 1;
  while (low < high) {
    const mid = (low + high + 1) >>> 1;
    if (frames[mid].startMs <= t) low = mid;
    else high = mid - 1;
  }
  return low;
}

/** Allocate one reusable result holder for a raster clip sampling loop. */
export function createRasterSpineFrameSampleScratch(): RasterSpineFrameSampleScratch {
  return {
    frameIndex: 0,
    frame: null,
    placement: { canvasWidth: 0, canvasHeight: 0, localX: 0, localY: 0, localWidth: 0, localHeight: 0 }
  };
}

/**
 * Select a raster frame into caller-owned storage. On valid samples this neither allocates a
 * placement/result wrapper nor changes the identity of any scratch field.
 */
export function sampleRasterSpineClipInto(
  clip: RasterSpineClip,
  timeMs: number,
  scratch: RasterSpineFrameSampleScratch,
  loop = true,
  skipLoopEndpoint = true
): SpineResult<void> {
  const selected = rasterSpineFrameIndex(clip, timeMs, loop, skipLoopEndpoint);
  if (typeof selected !== "number") return spineFail(selected);
  scratch.frameIndex = selected;
  scratch.frame = clip.frames[selected];
  const placement = scratch.placement;
  placement.canvasWidth = clip.canvasWidth;
  placement.canvasHeight = clip.canvasHeight;
  placement.localX = clip.localX;
  placement.localY = clip.localY;
  placement.localWidth = clip.localWidth;
  placement.localHeight = clip.localHeight;
  return spineIntoOk();
}

export function sampleRasterSpineClip(
  clip: RasterSpineClip,
  timeMs: number,
  loop = true,
  skipLoopEndpoint = true
): SpineResult<RasterSpineFrameSample> {
  const selected = rasterSpineFrameIndex(clip, timeMs, loop, skipLoopEndpoint);
  if (typeof selected !== "number") return spineFail(selected);
  return spineOk({
    frameIndex: selected,
    frame: clip.frames[selected],
    placement: {
      canvasWidth: clip.canvasWidth,
      canvasHeight: clip.canvasHeight,
      localX: clip.localX,
      localY: clip.localY,
      localWidth: clip.localWidth,
      localHeight: clip.localHeight
    }
  });
}
