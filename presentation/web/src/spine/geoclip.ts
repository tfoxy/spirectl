import {
  diagnostic,
  spineFail,
  spineIntoOk,
  spineOk,
  type SpineDiagnostic,
  type SpineResult,
} from "./diagnostics";
import type { SpinePlacement } from "./rasterClip";

export interface SpineTextureSource {
  /** A renderer-owned, opaque source identifier, commonly a URL. */
  readonly uri: string;
  readonly mimeType?: string;
}

export type SpineTextureSourceResolver = (file: string) => SpineTextureSource;

export interface GeoclipPage {
  readonly id: string;
  readonly file: string;
  readonly source: SpineTextureSource;
  readonly width: number;
  readonly height: number;
}

export interface GeoclipPart {
  readonly id: string;
  readonly pageId: string;
  /** Pre-resolved texture source; sampling never needs to look the page up again. */
  readonly texture: SpineTextureSource;
  readonly indices: Uint32Array;
  /** UVs normalized to the whole referenced texture page. */
  readonly uvs: Float32Array;
  /** Original UVs before the source rectangle is folded into a texture page. */
  readonly sourceUvs: Float32Array;
  /** Source rectangle in decoded texture pixels. */
  readonly sourceRect: readonly [number, number, number, number];
  readonly referencePositions: Float32Array;
  readonly blendMode: number;
}

interface GeoclipSlot {
  readonly partId: string | null;
  readonly tint: readonly [number, number, number, number];
  readonly transform: readonly [number, number, number, number, number, number];
  readonly positions: Float32Array | null;
  /** Packed vertex record ordinal, materialized by applyGeoclipVerts before sampling. */
  readonly vertexRecord: number | null;
}

interface GeoclipFrame {
  readonly drawOrder: readonly number[];
  readonly slots: ReadonlyMap<number, GeoclipSlot>;
}

export interface GeoclipPlacement extends SpinePlacement {
  readonly fitScale: number;
}

export interface GeoclipVertexBin {
  readonly file: string;
  readonly source: SpineTextureSource;
  readonly recordCount: number;
  /** Byte offsets, one per record, into the externally supplied packed buffer. */
  readonly offsets: readonly number[];
  /** Per-part x/y quantization bounds: [minX, minY, maxX, maxY]. */
  readonly quantization: ReadonlyMap<
    string,
    readonly [number, number, number, number]
  >;
}

/** Validated, renderer-independent legacy geoclip/v0 or production geoclip/2 content. */
export interface Geoclip {
  readonly schema: "geoclip/0" | "geoclip/2";
  readonly animation: string | null;
  readonly fps: number;
  readonly durationMs: number;
  readonly pages: readonly GeoclipPage[];
  readonly parts: ReadonlyMap<string, GeoclipPart>;
  readonly placement: GeoclipPlacement | null;
  /** Optional packed vertex records for slots that use vref. */
  readonly vertexBin: GeoclipVertexBin | null;
  readonly frames: readonly GeoclipFrame[];
}

/** One painter-ordered indexed mesh, ready for a canvas/WebGL renderer to consume. */
export interface GeoclipMesh {
  readonly drawOrder: number;
  readonly slotIndex: number;
  readonly partId: string;
  readonly texture: SpineTextureSource;
  readonly positions: Float32Array;
  readonly uvs: Float32Array;
  readonly indices: Uint32Array;
  readonly tint: readonly [number, number, number, number];
  readonly blendMode: number;
  readonly placement: GeoclipPlacement | null;
}

export interface GeoclipFrameSample {
  readonly frameIndex: number;
  readonly meshes: readonly GeoclipMesh[];
  readonly placement: GeoclipPlacement | null;
}

/** Dimensions reported by a renderer after it decodes a geoclip texture page. */
export interface GeoclipTextureDimensions {
  readonly width: number;
  readonly height: number;
}

export type GeoclipTextureDimensionsResolver = (
  page: GeoclipPage,
) => GeoclipTextureDimensions;

/** A mutable, pre-materialized mesh for {@link sampleGeoclipInto}. */
export interface GeoclipMeshScratch {
  drawOrder: number;
  slotIndex: number;
  partId: string;
  texture: SpineTextureSource;
  positions: Float32Array;
  uvs: Float32Array;
  indices: Uint32Array;
  tint: readonly [number, number, number, number];
  blendMode: number;
  placement: GeoclipPlacement | null;
}

interface PreparedGeoclipFrame {
  readonly meshes: readonly GeoclipMeshScratch[];
  readonly unmaterializedSlot: number | null;
}

/**
 * Caller-owned output for a single clip. Create it once with
 * {@link createGeoclipFrameSampleScratch}, then reuse it for every animation tick.
 */
export class GeoclipFrameSampleScratch {
  readonly clip: Geoclip;
  readonly meshes: GeoclipMeshScratch[] = [];
  frameIndex = 0;
  meshCount = 0;
  readonly placement: GeoclipPlacement | null;
  readonly #frames: readonly PreparedGeoclipFrame[];

  constructor(clip: Geoclip, frames: readonly PreparedGeoclipFrame[]) {
    this.clip = clip;
    this.placement = clip.placement;
    this.#frames = frames;
  }

  select(frameIndex: number): number | null {
    const prepared = this.#frames[frameIndex];
    if (!prepared) return null;
    this.frameIndex = frameIndex;
    this.meshCount = prepared.meshes.length;
    this.meshes.length = prepared.meshes.length;
    for (let index = 0; index < prepared.meshes.length; index += 1) {
      this.meshes[index] = prepared.meshes[index];
    }
    return prepared.unmaterializedSlot;
  }
}

/**
 * The compatibility representation used by hosts that intentionally recover a partially malformed
 * artifact. Unlike {@link Geoclip}, this keeps the source schema and the harness-compatible
 * nullable fields so a host can continue with its established fallback policy.
 */
export interface RecoveringGeoclip {
  readonly schema: string;
  readonly anim: string | null;
  readonly fps: number;
  readonly frameCount: number;
  readonly durationMs: number;
  readonly pages: readonly RecoveringGeoclipPage[];
  readonly parts: ReadonlyMap<string, RecoveringGeoclipPart>;
  readonly frames: readonly RecoveringGeoclipFrame[];
  readonly vertsBin: RecoveringGeoclipVertexBin | null;
  readonly placement: RecoveringGeoclipPlacement | null;
}
export interface RecoveringGeoclipPage {
  readonly id: string;
  readonly file: string;
  readonly width: number;
  readonly height: number;
}
export interface RecoveringGeoclipPart {
  readonly id: string;
  readonly pageId: string;
  readonly srcRect: readonly [number, number, number, number] | null;
  readonly indices: readonly number[];
  readonly uvs: readonly number[];
  readonly refVerts: Float32Array;
  readonly rigid: boolean;
  readonly blendMode: number;
}
export interface RecoveringGeoclipSlot {
  readonly part: string | null;
  readonly color: readonly [number, number, number, number] | null;
  readonly xform:
    | readonly [number, number, number, number, number, number]
    | null;
  readonly verts: Float32Array | null;
  readonly vref: number | null;
}
export interface RecoveringGeoclipFrame {
  readonly drawOrder: readonly number[] | null;
  readonly slots: ReadonlyMap<number, RecoveringGeoclipSlot>;
}
export interface RecoveringGeoclipVertexBin {
  readonly file: string;
  readonly records: number;
  readonly offsets: readonly number[];
  readonly quant: ReadonlyMap<
    string,
    readonly [number, number, number, number]
  >;
}
export interface RecoveringGeoclipPlacement {
  readonly canvasWidth: number;
  readonly canvasHeight: number;
  readonly localX: number;
  readonly localY: number;
  readonly localWidth: number;
  readonly localHeight: number;
  readonly fitScale: number;
}
export interface GeoclipRecoveryReport<T> {
  readonly value: T | null;
  readonly diagnostics: readonly SpineDiagnostic[];
  readonly recovered: boolean;
}

/** A painter-ordered recovery mesh. Hosts may retain their own texture and GPU ownership. */
export interface RecoveringGeoclipMesh {
  readonly drawOrder: number;
  readonly slotIndex: number;
  readonly partId: string;
  readonly part: RecoveringGeoclipPart;
  readonly slot: RecoveringGeoclipSlot;
  readonly positions: Float32Array;
}

export interface RecoveringGeoclipFrameSample {
  readonly frameIndex: number;
  readonly meshes: readonly RecoveringGeoclipMesh[];
}

type JsonRecord = Record<string, unknown>;

function recoveryDiagnostic(path: string, message: string): SpineDiagnostic {
  return diagnostic("invalid-value", path, message);
}

function recoveryNumber(value: unknown, fallback = 0): number {
  const number = typeof value === "number" ? value : Number(value);
  return Number.isFinite(number) ? number : fallback;
}

function recoveryNumbers(value: unknown): number[] {
  return Array.isArray(value)
    ? value.map((entry) => recoveryNumber(entry))
    : [];
}

function recoveryPlacement(value: unknown): RecoveringGeoclipPlacement | null {
  const source = record(value);
  if (!source) return null;
  const values = [
    "canvasWidth",
    "canvasHeight",
    "localX",
    "localY",
    "localWidth",
    "localHeight",
    "fitScale",
  ].map((key) => source[key]);
  if (
    values.some((entry) => typeof entry !== "number" || !Number.isFinite(entry))
  )
    return null;
  const [
    canvasWidth,
    canvasHeight,
    localX,
    localY,
    localWidth,
    localHeight,
    fitScale,
  ] = values as number[];
  return canvasWidth > 0 && canvasHeight > 0
    ? {
        canvasWidth,
        canvasHeight,
        localX,
        localY,
        localWidth,
        localHeight,
        fitScale,
      }
    : null;
}

/**
 * Parse using the historical harness recovery rules. It is intentionally separate from
 * {@link parseGeoclip}: a malformed numeric declared frameCount remains fatal, whereas omitted
 * or stringly metadata and individual malformed entries are recovered with diagnostics.
 */
export function recoverGeoclip(
  raw: unknown,
): GeoclipRecoveryReport<RecoveringGeoclip> {
  const diagnostics: SpineDiagnostic[] = [];
  const doc = record(raw);
  const meta = doc && record(doc.meta);
  const schema = meta && typeof meta.schema === "string" ? meta.schema : "";
  if (!doc || !meta || schema.split("/")[0] !== "geoclip") {
    return {
      value: null,
      diagnostics: [
        recoveryDiagnostic(
          "meta.schema",
          `Manifest has unsupported schema '${schema}'.`,
        ),
      ],
      recovered: false,
    };
  }
  let recovered = false;
  const mark = (path: string, message: string) => {
    recovered = true;
    diagnostics.push(recoveryDiagnostic(path, message));
  };
  if (typeof meta.fps !== "number" || !Number.isFinite(meta.fps))
    mark("meta.fps", "Defaulted malformed fps.");
  if (meta.frameCount === undefined)
    mark("meta.frameCount", "Defaulted missing frame count.");
  const recoveredPlacement = recoveryPlacement(meta.placement);
  if (meta.placement !== undefined && !recoveredPlacement) {
    mark("meta.placement", "Dropped malformed placement.");
  }
  const pages: RecoveringGeoclipPage[] = [];
  for (const [index, entry] of (Array.isArray(doc.pages)
    ? doc.pages
    : []
  ).entries()) {
    const page = record(entry);
    if (!page || page.id == null || typeof page.file !== "string") {
      mark(`pages[${index}]`, "Dropped malformed page.");
      continue;
    }
    pages.push({
      id: String(page.id),
      file: page.file,
      width: recoveryNumber(page.width),
      height: recoveryNumber(page.height),
    });
  }
  const parts = new Map<string, RecoveringGeoclipPart>();
  for (const [index, entry] of (Array.isArray(doc.parts)
    ? doc.parts
    : []
  ).entries()) {
    const part = record(entry);
    if (!part || part.id == null) {
      mark(`parts[${index}]`, "Dropped malformed part.");
      continue;
    }
    const refVerts = Float32Array.from(recoveryNumbers(part.refVerts));
    if (refVerts.length < 6) {
      mark(
        `parts[${index}].refVerts`,
        "Dropped part with fewer than three vertices.",
      );
      continue;
    }
    const rect = recoveryNumbers(part.srcRect);
    parts.set(String(part.id), {
      id: String(part.id),
      pageId: String(part.pageId ?? ""),
      srcRect: rect.length >= 4 ? [rect[0], rect[1], rect[2], rect[3]] : null,
      indices: recoveryNumbers(part.indices),
      uvs: recoveryNumbers(part.uvs),
      refVerts,
      rigid: part.rigid !== false,
      blendMode: recoveryNumber(part.blendMode),
    });
  }
  const frames: RecoveringGeoclipFrame[] = [];
  for (const [frameIndex, entry] of (Array.isArray(doc.frames)
    ? doc.frames
    : []
  ).entries()) {
    const frame = record(entry);
    const slots = new Map<number, RecoveringGeoclipSlot>();
    const rawSlots = frame && record(frame.slots);
    for (const [key, entrySlot] of Object.entries(rawSlots ?? {})) {
      const slotIndex = Number(key);
      if (!Number.isFinite(slotIndex)) {
        mark(`frames[${frameIndex}].slots.${key}`, "Dropped non-numeric slot.");
        continue;
      }
      const slot = record(entrySlot) ?? {};
      const color = recoveryNumbers(slot.color);
      const xform = recoveryNumbers(slot.xform);
      const verts = Array.isArray(slot.verts)
        ? Float32Array.from(recoveryNumbers(slot.verts))
        : null;
      slots.set(slotIndex, {
        part: slot.part == null ? null : String(slot.part),
        color:
          color.length >= 4
            ? [color[0], color[1], color[2], color[3] as number]
            : null,
        xform:
          xform.length >= 6
            ? [xform[0], xform[1], xform[2], xform[3], xform[4], xform[5]]
            : null,
        verts,
        vref:
          typeof slot.vref === "number" && Number.isFinite(slot.vref)
            ? slot.vref
            : null,
      });
    }
    const drawOrder = Array.isArray(frame?.drawOrder)
      ? recoveryNumbers(frame.drawOrder)
      : null;
    frames.push({
      drawOrder: drawOrder && drawOrder.length > 0 ? drawOrder : null,
      slots,
    });
  }
  if (
    typeof meta.frameCount === "number" &&
    Number.isFinite(meta.frameCount) &&
    meta.frameCount !== frames.length
  ) {
    return {
      value: null,
      diagnostics: [
        ...diagnostics,
        diagnostic(
          "inconsistent-data",
          "meta.frameCount",
          `Manifest declares ${meta.frameCount} frames but carries ${frames.length}.`,
        ),
      ],
      recovered,
    };
  }
  const rawBin = record(doc.vertsBin);
  const quant = new Map<string, readonly [number, number, number, number]>();
  if (rawBin)
    for (const [id, rawBox] of Object.entries(record(rawBin.quant) ?? {})) {
      const box = recoveryNumbers(rawBox);
      if (box.length >= 4) quant.set(id, [box[0], box[1], box[2], box[3]]);
      else mark(`vertsBin.quant.${id}`, "Dropped malformed quantization box.");
    }
  const vertsBin = rawBin
    ? {
        file: typeof rawBin.file === "string" ? rawBin.file : "verts.bin",
        records: recoveryNumber(rawBin.records),
        offsets: recoveryNumbers(rawBin.offsets),
        quant,
      }
    : null;
  return {
    value: {
      schema,
      anim: typeof meta.anim === "string" ? meta.anim : null,
      fps: recoveryNumber(meta.fps, 30),
      frameCount: recoveryNumber(meta.frameCount, frames.length),
      durationMs: recoveryNumber(meta.durationMs),
      pages,
      parts,
      frames,
      vertsBin,
      placement: recoveredPlacement,
    },
    diagnostics,
    recovered,
  };
}

/** Materialize packed vertices with the same per-slot fallback behavior as the legacy harness. */
export function recoverGeoclipVerts(
  clip: RecoveringGeoclip,
  input: ArrayBuffer | Uint8Array,
): GeoclipRecoveryReport<RecoveringGeoclip> {
  if (!clip.vertsBin) return { value: clip, diagnostics: [], recovered: false };
  const bytes = input instanceof Uint8Array ? input : new Uint8Array(input);
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  const diagnostics: SpineDiagnostic[] = [];
  let recovered = false;
  const frames = clip.frames.map((frame, frameIndex) => ({
    ...frame,
    slots: new Map(
      [...frame.slots].map(([slotIndex, slot]) => {
        if (slot.vref === null || slot.part === null || slot.verts !== null)
          return [slotIndex, slot] as const;
        const part = clip.parts.get(slot.part);
        const box = clip.vertsBin!.quant.get(slot.part);
        const offset = clip.vertsBin!.offsets[slot.vref];
        const count = part?.refVerts.length ?? 0;
        const out =
          part && box && Number.isFinite(offset) && offset! >= 0
            ? decodePackedGeoclipPositions(view, offset!, count, box)
            : null;
        if (!out) {
          recovered = true;
          diagnostics.push(
            recoveryDiagnostic(
              `frames[${frameIndex}].slots.${slotIndex}.vref`,
              "Packed vertex record was unavailable; sampling will use the rigid transform.",
            ),
          );
          return [slotIndex, { ...slot, vref: null }] as const;
        }
        return [slotIndex, { ...slot, verts: out, vref: null }] as const;
      }),
    ),
  }));
  return { value: { ...clip, frames }, diagnostics, recovered };
}

/**
 * Decode one little-endian packed record. Both strict and recovering lanes deliberately use this
 * exact dequantization, while deciding differently whether an unavailable record is fatal.
 */
function decodePackedGeoclipPositions(
  view: DataView,
  offset: number,
  coordinateCount: number,
  [minX, minY, maxX, maxY]: readonly [number, number, number, number],
): Float32Array | null {
  const byteLength = coordinateCount * Uint16Array.BYTES_PER_ELEMENT;
  if (
    !Number.isFinite(offset) ||
    offset < 0 ||
    offset + byteLength > view.byteLength
  )
    return null;
  const xStep = (maxX - minX) / 65535;
  const yStep = (maxY - minY) / 65535;
  const positions = new Float32Array(coordinateCount);
  for (let index = 0; index < coordinateCount; index += 2) {
    positions[index] =
      minX +
      view.getUint16(offset + index * Uint16Array.BYTES_PER_ELEMENT, true) *
        xStep;
    positions[index + 1] =
      minY +
      view.getUint16(
        offset + (index + 1) * Uint16Array.BYTES_PER_ELEMENT,
        true,
      ) *
        yStep;
  }
  return positions;
}

/** Select a recovery frame using the historical tolerant timing defaults. */
export function recoveringGeoclipFrameIndexAt(
  clip: {
    readonly frames: readonly unknown[];
    readonly fps: number;
    readonly durationMs?: number;
  },
  timeMs: number,
  loop = true,
  skipLoopEndpoint = true,
): number {
  return geoclipFrameIndexValue(
    clip.frames.length,
    clip.fps,
    clip.durationMs ?? 0,
    timeMs,
    loop,
    skipLoopEndpoint,
    true,
  );
}

/** Sample recovery geometry with the harness fallback: inline verts, affine reference pose, then reference pose. */
export function sampleRecoveringGeoclipFrame(
  clip: RecoveringGeoclip,
  frameIndex: number,
): RecoveringGeoclipFrameSample {
  const frame = clip.frames[frameIndex];
  if (!frame) return { frameIndex, meshes: [] };
  const order =
    frame.drawOrder ??
    [...frame.slots.keys()].sort((left, right) => left - right);
  const meshes: RecoveringGeoclipMesh[] = [];
  for (let drawOrder = 0; drawOrder < order.length; drawOrder += 1) {
    const slotIndex = order[drawOrder];
    const slot = frame.slots.get(slotIndex);
    const part =
      slot?.part === null || !slot ? null : clip.parts.get(slot.part);
    if (!slot || !part) continue;
    const positions =
      slot.verts && slot.verts.length === part.refVerts.length
        ? slot.verts
        : applyGeoclipTransform(part.refVerts, slot.xform);
    meshes.push({
      drawOrder,
      slotIndex,
      partId: part.id,
      part,
      slot,
      positions,
    });
  }
  return { frameIndex, meshes };
}

function record(value: unknown): JsonRecord | null {
  return value !== null && typeof value === "object" && !Array.isArray(value)
    ? (value as JsonRecord)
    : null;
}

function id(value: unknown): string | null {
  if (typeof value === "string" && value.length > 0) return value;
  if (typeof value === "number" && Number.isFinite(value)) return String(value);
  return null;
}

function finiteNumber(value: unknown): number | null {
  return typeof value === "number" && Number.isFinite(value) ? value : null;
}

function foldUvs(
  sourceUvs: Float32Array,
  [x, y, width, height]: readonly [number, number, number, number],
  pageWidth: number,
  pageHeight: number,
): Float32Array {
  const folded = new Float32Array(sourceUvs.length);
  for (let index = 0; index < sourceUvs.length; index += 2) {
    folded[index] = (x + sourceUvs[index] * width) / pageWidth;
    folded[index + 1] = (y + sourceUvs[index + 1] * height) / pageHeight;
  }
  return folded;
}

/** Fold source-rectangle-normalized UVs into whole-page UVs without renderer dependencies. */
export function foldGeoclipUvs(
  sourceUvs: ArrayLike<number>,
  sourceRect: readonly [number, number, number, number],
  pageWidth: number,
  pageHeight: number,
): Float32Array {
  return foldUvs(
    Float32Array.from(sourceUvs),
    sourceRect,
    pageWidth,
    pageHeight,
  );
}

function positiveInteger(value: unknown): number | null {
  const number = finiteNumber(value);
  return number !== null && Number.isInteger(number) && number > 0
    ? number
    : null;
}

function numberArray(
  value: unknown,
  length: number | null,
  path: string,
): SpineResult<number[]> {
  if (!Array.isArray(value) || (length !== null && value.length !== length)) {
    return spineFail(
      diagnostic(
        "invalid-type",
        path,
        length === null
          ? "Expected a numeric array."
          : `Expected ${length} numeric values.`,
      ),
    );
  }
  const values = value.map(finiteNumber);
  if (values.some((entry) => entry === null)) {
    return spineFail(
      diagnostic("invalid-value", path, "Values must be finite numbers."),
    );
  }
  return spineOk(values as number[]);
}

function placement(value: unknown): SpineResult<GeoclipPlacement | null> {
  if (value === undefined) return spineOk(null);
  const source = record(value);
  if (!source)
    return spineFail(
      diagnostic(
        "invalid-type",
        "meta.placement",
        "Placement must be an object.",
      ),
    );
  const fields = [
    "canvasWidth",
    "canvasHeight",
    "localX",
    "localY",
    "localWidth",
    "localHeight",
    "fitScale",
  ];
  const values = fields.map((field) => finiteNumber(source[field]));
  if (values.some((entry) => entry === null)) {
    return spineFail(
      diagnostic(
        "invalid-value",
        "meta.placement",
        "Placement must contain all finite fields.",
      ),
    );
  }
  const [
    canvasWidth,
    canvasHeight,
    localX,
    localY,
    localWidth,
    localHeight,
    fitScale,
  ] = values as number[];
  if (
    canvasWidth <= 0 ||
    canvasHeight <= 0 ||
    localWidth < 0 ||
    localHeight < 0 ||
    fitScale <= 0
  ) {
    return spineFail(
      diagnostic(
        "invalid-value",
        "meta.placement",
        "Placement dimensions and fitScale must be positive.",
      ),
    );
  }
  return spineOk({
    canvasWidth,
    canvasHeight,
    localX,
    localY,
    localWidth,
    localHeight,
    fitScale,
  });
}

function resolveSource(
  file: string,
  path: string,
  resolver: SpineTextureSourceResolver,
): SpineResult<SpineTextureSource> {
  let source: SpineTextureSource;
  try {
    source = resolver(file);
  } catch {
    return spineFail(
      diagnostic(
        "invalid-reference",
        path,
        "Texture source resolver rejected this file.",
      ),
    );
  }
  if (!source || typeof source.uri !== "string" || source.uri.length === 0) {
    return spineFail(
      diagnostic(
        "invalid-reference",
        path,
        "Texture source resolver returned no URI.",
      ),
    );
  }
  return spineOk(source);
}

function parseVertexBin(
  raw: unknown,
  parts: ReadonlyMap<string, GeoclipPart>,
  resolver: SpineTextureSourceResolver,
): SpineResult<GeoclipVertexBin | null> {
  if (raw === undefined) return spineOk(null);
  const bin = record(raw);
  if (!bin)
    return spineFail(
      diagnostic(
        "invalid-type",
        "vertsBin",
        "Packed vertex data must be a descriptor when supplied.",
      ),
    );
  const file =
    typeof bin.file === "string" && bin.file.length > 0 ? bin.file : null;
  const recordCount = finiteNumber(bin.records);
  if (
    !file ||
    recordCount === null ||
    !Number.isInteger(recordCount) ||
    recordCount < 0
  ) {
    return spineFail(
      diagnostic(
        "invalid-value",
        "vertsBin",
        "Vertex descriptor requires file and a non-negative integer records count.",
      ),
    );
  }
  const offsets = numberArray(bin.offsets, recordCount, "vertsBin.offsets");
  if (
    !offsets.ok ||
    offsets.value.some((offset) => !Number.isInteger(offset) || offset < 0)
  ) {
    return spineFail(
      diagnostic(
        "invalid-value",
        "vertsBin.offsets",
        "Offsets must be non-negative integer byte positions.",
      ),
    );
  }
  const rawQuantization = record(bin.quant);
  if (!rawQuantization)
    return spineFail(
      diagnostic(
        "missing-field",
        "vertsBin.quant",
        "Per-part quantization bounds are required.",
      ),
    );
  const quantization = new Map<
    string,
    readonly [number, number, number, number]
  >();
  for (const [partId, rawBox] of Object.entries(rawQuantization)) {
    if (!parts.has(partId)) {
      return spineFail(
        diagnostic(
          "invalid-reference",
          `vertsBin.quant.${partId}`,
          "Quantization names an unknown part.",
        ),
      );
    }
    const box = numberArray(rawBox, 4, `vertsBin.quant.${partId}`);
    if (!box.ok) return spineFail(...box.diagnostics);
    if (box.value[2] < box.value[0] || box.value[3] < box.value[1]) {
      return spineFail(
        diagnostic(
          "invalid-value",
          `vertsBin.quant.${partId}`,
          "Quantization maxima must not be below minima.",
        ),
      );
    }
    quantization.set(partId, box.value as [number, number, number, number]);
  }
  const source = resolveSource(file, "vertsBin.file", resolver);
  if (!source.ok) return spineFail(...source.diagnostics);
  return spineOk({
    file,
    source: source.value,
    recordCount,
    offsets: offsets.value,
    quantization,
  });
}

/** Parse and validate a legacy geoclip/v0 or production geoclip/2 manifest. It performs no I/O. */
export function parseGeoclip(
  raw: unknown,
  resolveTextureSource: SpineTextureSourceResolver,
): SpineResult<Geoclip> {
  const doc = record(raw);
  if (!doc)
    return spineFail(
      diagnostic("invalid-type", "manifest", "Manifest must be an object."),
    );
  const meta = record(doc.meta);
  if (!meta)
    return spineFail(
      diagnostic("missing-field", "meta", "Manifest metadata is required."),
    );
  if (meta.schema !== "geoclip/0" && meta.schema !== "geoclip/2") {
    return spineFail(
      diagnostic(
        "unsupported-version",
        "meta.schema",
        "Only geoclip/0 and geoclip/2 are supported.",
      ),
    );
  }
  const schema = meta.schema;
  const fps = finiteNumber(meta.fps);
  const durationMs = finiteNumber(meta.durationMs);
  if (fps === null || fps <= 0 || durationMs === null || durationMs < 0) {
    return spineFail(
      diagnostic(
        "invalid-value",
        "meta",
        "fps must be positive and durationMs must be non-negative.",
      ),
    );
  }
  const rawFrames = Array.isArray(doc.frames) ? doc.frames : null;
  const frameCount = positiveInteger(meta.frameCount);
  if (
    !rawFrames ||
    rawFrames.length === 0 ||
    frameCount === null ||
    frameCount !== rawFrames.length
  ) {
    return spineFail(
      diagnostic(
        "inconsistent-data",
        "meta.frameCount",
        "frameCount must equal a non-empty frames array.",
      ),
    );
  }
  const parsedPlacement = placement(meta.placement);
  if (!parsedPlacement.ok) return spineFail(...parsedPlacement.diagnostics);

  const rawPages = Array.isArray(doc.pages) ? doc.pages : null;
  if (!rawPages || rawPages.length === 0)
    return spineFail(
      diagnostic(
        "missing-field",
        "pages",
        "At least one texture page is required.",
      ),
    );
  const pages: GeoclipPage[] = [];
  const pagesById = new Map<string, GeoclipPage>();
  for (let ordinal = 0; ordinal < rawPages.length; ordinal += 1) {
    const path = `pages[${ordinal}]`;
    const page = record(rawPages[ordinal]);
    const pageId = page && id(page.id);
    const file =
      page && typeof page.file === "string" && page.file.length > 0
        ? page.file
        : null;
    const width = page && positiveInteger(page.width);
    const height = page && positiveInteger(page.height);
    if (
      !page ||
      !pageId ||
      !file ||
      width === null ||
      height === null ||
      pagesById.has(pageId)
    ) {
      return spineFail(
        diagnostic(
          "invalid-value",
          path,
          "Page requires a unique id, file, and positive dimensions.",
        ),
      );
    }
    const source = resolveSource(file, `${path}.file`, resolveTextureSource);
    if (!source.ok) return spineFail(...source.diagnostics);
    const resolved = { id: pageId, file, source: source.value, width, height };
    pages.push(resolved);
    pagesById.set(pageId, resolved);
  }

  const rawParts = Array.isArray(doc.parts) ? doc.parts : null;
  if (!rawParts || rawParts.length === 0)
    return spineFail(
      diagnostic(
        "missing-field",
        "parts",
        "At least one mesh part is required.",
      ),
    );
  const parts = new Map<string, GeoclipPart>();
  for (let ordinal = 0; ordinal < rawParts.length; ordinal += 1) {
    const path = `parts[${ordinal}]`;
    const rawPart = record(rawParts[ordinal]);
    const partId = rawPart && id(rawPart.id);
    const pageId = rawPart && id(rawPart.pageId);
    if (
      !rawPart ||
      !partId ||
      !pageId ||
      parts.has(partId) ||
      !pagesById.has(pageId)
    ) {
      return spineFail(
        diagnostic(
          "invalid-reference",
          path,
          "Part must have a unique id and an existing pageId.",
        ),
      );
    }
    const positions = numberArray(rawPart.refVerts, null, `${path}.refVerts`);
    if (!positions.ok) return spineFail(...positions.diagnostics);
    if (positions.value.length < 6 || positions.value.length % 2 !== 0) {
      return spineFail(
        diagnostic(
          "invalid-value",
          `${path}.refVerts`,
          "At least three x/y vertices are required.",
        ),
      );
    }
    const uvs = numberArray(rawPart.uvs, positions.value.length, `${path}.uvs`);
    if (!uvs.ok) return spineFail(...uvs.diagnostics);
    const indices = numberArray(rawPart.indices, null, `${path}.indices`);
    if (!indices.ok) return spineFail(...indices.diagnostics);
    if (
      indices.value.length === 0 ||
      indices.value.length % 3 !== 0 ||
      indices.value.some(
        (value) =>
          !Number.isInteger(value) ||
          value < 0 ||
          value >= positions.value.length / 2,
      )
    ) {
      return spineFail(
        diagnostic(
          "invalid-value",
          `${path}.indices`,
          "Indices must be in-range triangle indices.",
        ),
      );
    }
    const sourceRect =
      rawPart.srcRect === undefined
        ? null
        : numberArray(rawPart.srcRect, 4, `${path}.srcRect`);
    if (sourceRect && !sourceRect.ok)
      return spineFail(...sourceRect.diagnostics);
    const page = pagesById.get(pageId)!;
    const rect = Object.freeze([
      ...(sourceRect ? sourceRect.value : [0, 0, page.width, page.height]),
    ]) as readonly [number, number, number, number];
    if (rect[2] <= 0 || rect[3] <= 0)
      return spineFail(
        diagnostic(
          "invalid-value",
          `${path}.srcRect`,
          "Source rectangle dimensions must be positive.",
        ),
      );
    if (
      rect[0] < 0 ||
      rect[1] < 0 ||
      rect[0] + rect[2] > page.width ||
      rect[1] + rect[3] > page.height
    ) {
      return spineFail(
        diagnostic(
          "invalid-value",
          `${path}.srcRect`,
          "Source rectangle must be contained by its texture page.",
        ),
      );
    }
    const sourceUvs = Float32Array.from(uvs.value);
    const foldedUvs = foldUvs(sourceUvs, rect, page.width, page.height);
    const blendMode =
      rawPart.blendMode === undefined ? 0 : finiteNumber(rawPart.blendMode);
    if (blendMode === null)
      return spineFail(
        diagnostic(
          "invalid-value",
          `${path}.blendMode`,
          "blendMode must be finite.",
        ),
      );
    parts.set(partId, {
      id: partId,
      pageId,
      texture: page.source,
      indices: Uint32Array.from(indices.value),
      uvs: foldedUvs,
      sourceUvs,
      sourceRect: rect,
      referencePositions: Float32Array.from(positions.value),
      blendMode,
    });
  }

  const vertexBin = parseVertexBin(doc.vertsBin, parts, resolveTextureSource);
  if (!vertexBin.ok) return spineFail(...vertexBin.diagnostics);

  const frames: GeoclipFrame[] = [];
  for (let ordinal = 0; ordinal < rawFrames.length; ordinal += 1) {
    const path = `frames[${ordinal}]`;
    const rawFrame = record(rawFrames[ordinal]);
    const rawSlots = rawFrame && record(rawFrame.slots);
    if (!rawFrame || !rawSlots)
      return spineFail(
        diagnostic(
          "missing-field",
          `${path}.slots`,
          "Frame slots must be an object.",
        ),
      );
    const slots = new Map<number, GeoclipSlot>();
    for (const [key, rawSlot] of Object.entries(rawSlots)) {
      const slotIndex = Number(key);
      const slot = record(rawSlot);
      if (!Number.isInteger(slotIndex) || slotIndex < 0 || !slot) {
        return spineFail(
          diagnostic(
            "invalid-value",
            `${path}.slots.${key}`,
            "Slot keys must be non-negative integers.",
          ),
        );
      }
      const partId = slot.part === null ? null : id(slot.part);
      if (slot.part !== null && (!partId || !parts.has(partId))) {
        return spineFail(
          diagnostic(
            "invalid-reference",
            `${path}.slots.${key}.part`,
            "Slot references an unknown part.",
          ),
        );
      }
      const tint =
        slot.color === undefined
          ? spineOk([1, 1, 1, 1])
          : numberArray(slot.color, 4, `${path}.slots.${key}.color`);
      if (!tint.ok || tint.value.some((value) => value < 0 || value > 1)) {
        return spineFail(
          diagnostic(
            "invalid-value",
            `${path}.slots.${key}.color`,
            "Tint channels must be finite values in [0, 1].",
          ),
        );
      }
      const transform =
        slot.xform === undefined
          ? spineOk([1, 0, 0, 1, 0, 0])
          : numberArray(slot.xform, 6, `${path}.slots.${key}.xform`);
      if (!transform.ok) return spineFail(...transform.diagnostics);
      const vertices =
        slot.verts === undefined
          ? null
          : numberArray(
              slot.verts,
              partId ? parts.get(partId)!.referencePositions.length : 0,
              `${path}.slots.${key}.verts`,
            );
      if (vertices && !vertices.ok) return spineFail(...vertices.diagnostics);
      const vertexRecord =
        slot.vref === undefined ? null : finiteNumber(slot.vref);
      if (
        slot.vref !== undefined &&
        (vertexRecord === null ||
          !Number.isInteger(vertexRecord) ||
          vertexRecord < 0)
      ) {
        return spineFail(
          diagnostic(
            "invalid-value",
            `${path}.slots.${key}.vref`,
            "vref must be a non-negative integer record ordinal.",
          ),
        );
      }
      if (vertexRecord !== null) {
        if (
          !partId ||
          vertices ||
          !vertexBin.value ||
          vertexRecord >= vertexBin.value.recordCount
        ) {
          return spineFail(
            diagnostic(
              "invalid-reference",
              `${path}.slots.${key}.vref`,
              "vref must select one packed record for a visible, non-inline part.",
            ),
          );
        }
        if (!vertexBin.value.quantization.has(partId)) {
          return spineFail(
            diagnostic(
              "invalid-reference",
              `${path}.slots.${key}.vref`,
              "Part needs quantization bounds for packed vertices.",
            ),
          );
        }
      }
      if (partId === null && vertices) {
        return spineFail(
          diagnostic(
            "invalid-value",
            `${path}.slots.${key}.verts`,
            "A hidden slot cannot carry vertices.",
          ),
        );
      }
      slots.set(slotIndex, {
        partId,
        tint: tint.value as [number, number, number, number],
        transform: transform.value as [
          number,
          number,
          number,
          number,
          number,
          number,
        ],
        positions: vertices ? Float32Array.from(vertices.value) : null,
        vertexRecord,
      });
    }
    const explicitOrder =
      rawFrame.drawOrder === undefined
        ? null
        : numberArray(rawFrame.drawOrder, null, `${path}.drawOrder`);
    if (explicitOrder && !explicitOrder.ok)
      return spineFail(...explicitOrder.diagnostics);
    const drawOrder = explicitOrder
      ? explicitOrder.value
      : [...slots.keys()].sort((a, b) => a - b);
    if (
      drawOrder.length !== slots.size ||
      drawOrder.some(
        (value) => !Number.isInteger(value) || !slots.has(value),
      ) ||
      new Set(drawOrder).size !== drawOrder.length
    ) {
      return spineFail(
        diagnostic(
          "invalid-reference",
          `${path}.drawOrder`,
          "Draw order must list every frame slot exactly once.",
        ),
      );
    }
    frames.push({ drawOrder, slots });
  }
  return spineOk({
    schema,
    animation: typeof meta.anim === "string" ? meta.anim : null,
    fps,
    durationMs,
    pages,
    parts,
    placement: parsedPlacement.value,
    vertexBin: vertexBin.value,
    frames,
  });
}

function decodedPageDimensions(
  clip: Geoclip,
  resolveDimensions: GeoclipTextureDimensionsResolver,
): SpineResult<readonly GeoclipTextureDimensions[]> {
  const dimensions: GeoclipTextureDimensions[] = [];
  for (let index = 0; index < clip.pages.length; index += 1) {
    const page = clip.pages[index];
    let decoded: GeoclipTextureDimensions;
    try {
      decoded = resolveDimensions(page);
    } catch {
      return spineFail(
        diagnostic(
          "invalid-reference",
          `pages[${index}].decodedDimensions`,
          "Texture dimensions resolver rejected this page.",
        ),
      );
    }
    if (
      !decoded ||
      !positiveInteger(decoded.width) ||
      !positiveInteger(decoded.height)
    ) {
      return spineFail(
        diagnostic(
          "invalid-value",
          `pages[${index}].decodedDimensions`,
          "Decoded texture dimensions must be positive integers.",
        ),
      );
    }
    dimensions.push(decoded);
  }
  return spineOk(dimensions);
}

/**
 * Verify that renderer-decoded image dimensions still match the manifest. Renderers that require
 * strict atlas provenance can use this to refuse an unexpected decoded image with a typed error.
 */
export function validateGeoclipTextureDimensions(
  clip: Geoclip,
  resolveDimensions: GeoclipTextureDimensionsResolver,
): SpineResult<void> {
  const decoded = decodedPageDimensions(clip, resolveDimensions);
  if (!decoded.ok) return spineFail(...decoded.diagnostics);
  for (let index = 0; index < clip.pages.length; index += 1) {
    const page = clip.pages[index];
    const dimensions = decoded.value[index];
    if (page.width !== dimensions.width || page.height !== dimensions.height) {
      return spineFail(
        diagnostic(
          "inconsistent-data",
          `pages[${index}].decodedDimensions`,
          `Decoded texture dimensions ${dimensions.width}x${dimensions.height} do not match manifest ${page.width}x${page.height}.`,
        ),
      );
    }
  }
  return spineOk(undefined);
}

/**
 * Return a new clip whose already-parsed UVs are folded against renderer-decoded texture sizes.
 * The original manifest clip remains unchanged, so existing callers retain manifest-dimension
 * semantics and strict renderers can instead use validateGeoclipTextureDimensions above.
 */
export function refoldGeoclipTextureDimensions(
  clip: Geoclip,
  resolveDimensions: GeoclipTextureDimensionsResolver,
): SpineResult<Geoclip> {
  const decoded = decodedPageDimensions(clip, resolveDimensions);
  if (!decoded.ok) return spineFail(...decoded.diagnostics);
  if (
    clip.pages.every(
      (page, index) =>
        page.width === decoded.value[index].width &&
        page.height === decoded.value[index].height,
    )
  ) {
    return spineOk(clip);
  }
  const pages = clip.pages.map((page, index) => ({
    ...page,
    width: decoded.value[index].width,
    height: decoded.value[index].height,
  }));
  const pageById = new Map(pages.map((page) => [page.id, page]));
  const parts = new Map<string, GeoclipPart>();
  for (const [partId, part] of clip.parts) {
    const page = pageById.get(part.pageId);
    if (!page)
      return spineFail(
        diagnostic(
          "invalid-reference",
          `parts.${partId}.pageId`,
          "Part references an unavailable page.",
        ),
      );
    const [x, y, width, height] = part.sourceRect;
    if (x < 0 || y < 0 || x + width > page.width || y + height > page.height) {
      return spineFail(
        diagnostic(
          "invalid-value",
          `parts.${partId}.srcRect`,
          "Source rectangle is outside the decoded texture page.",
        ),
      );
    }
    parts.set(partId, {
      ...part,
      texture: page.source,
      uvs: foldUvs(part.sourceUvs, part.sourceRect, page.width, page.height),
    });
  }
  return spineOk({ ...clip, pages, parts });
}

/**
 * Decode geoclip/v0 or geoclip/2 external little-endian u16 vertex records into a new clip. This does not fetch or mutate
 * either input; renderers decide how to obtain the bytes named by `clip.vertexBin.source`.
 */
export function applyGeoclipVerts(
  clip: Geoclip,
  input: ArrayBuffer | Uint8Array,
): SpineResult<Geoclip> {
  const vertexBin = clip.vertexBin;
  if (!vertexBin) return spineOk(clip);
  const bytes = input instanceof Uint8Array ? input : new Uint8Array(input);
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  const frames: GeoclipFrame[] = [];
  for (let frameIndex = 0; frameIndex < clip.frames.length; frameIndex += 1) {
    const frame = clip.frames[frameIndex];
    const slots = new Map<number, GeoclipSlot>();
    for (const [slotIndex, slot] of frame.slots) {
      if (slot.vertexRecord === null) {
        slots.set(slotIndex, slot);
        continue;
      }
      const path = `frames[${frameIndex}].slots.${slotIndex}.vref`;
      const part = slot.partId ? clip.parts.get(slot.partId) : null;
      const quantization = slot.partId
        ? vertexBin.quantization.get(slot.partId)
        : null;
      const offset = vertexBin.offsets[slot.vertexRecord];
      if (
        !part ||
        !quantization ||
        offset === undefined ||
        slot.vertexRecord >= vertexBin.recordCount
      ) {
        return spineFail(
          diagnostic(
            "invalid-reference",
            path,
            "Packed vertex record no longer has a valid part, bounds, or offset.",
          ),
        );
      }
      const coordinateCount = part.referencePositions.length;
      const positions = decodePackedGeoclipPositions(
        view,
        offset,
        coordinateCount,
        quantization,
      );
      if (!positions) {
        return spineFail(
          diagnostic(
            "truncated-data",
            path,
            "Packed vertex record exceeds the supplied buffer.",
          ),
        );
      }
      slots.set(slotIndex, { ...slot, positions, vertexRecord: null });
    }
    frames.push({ drawOrder: frame.drawOrder, slots });
  }
  return spineOk({ ...clip, frames });
}

/** Select an endpoint-inclusive geoclip frame, keeping an optional looping endpoint out of the repeat period. */
export function geoclipFrameIndexAt(
  clip: Pick<Geoclip, "frames" | "fps" | "durationMs">,
  timeMs: number,
  loop = true,
  skipLoopEndpoint = true,
): SpineResult<number> {
  const selected = geoclipFrameIndex(clip, timeMs, loop, skipLoopEndpoint);
  return typeof selected === "number" ? spineOk(selected) : spineFail(selected);
}

function geoclipFrameIndex(
  clip: Pick<Geoclip, "frames" | "fps" | "durationMs">,
  timeMs: number,
  loop: boolean,
  skipLoopEndpoint: boolean,
): number | SpineDiagnostic {
  if (!Number.isFinite(timeMs))
    return diagnostic(
      "invalid-value",
      "timeMs",
      "Playback time must be finite.",
    );
  if (clip.frames.length === 0)
    return diagnostic(
      "inconsistent-data",
      "frames",
      "A clip must contain at least one frame.",
    );
  return geoclipFrameIndexValue(
    clip.frames.length,
    clip.fps,
    clip.durationMs,
    timeMs,
    loop,
    skipLoopEndpoint,
    false,
  );
}

function geoclipFrameIndexValue(
  count: number,
  fps: number,
  durationMs: number,
  timeMs: number,
  loop: boolean,
  skipLoopEndpoint: boolean,
  fallbackFps: boolean,
): number {
  if (count <= 1) return 0;
  const rate = fallbackFps && (!(fps > 0) || !Number.isFinite(fps)) ? 30 : fps;
  const frameAtTime = Math.floor((timeMs * rate) / 1000);
  if (!loop) return Math.max(0, Math.min(count - 1, frameAtTime));
  const hasDuration = Number.isFinite(durationMs) && durationMs > 0;
  const durationPeriod = hasDuration
    ? Math.round((durationMs * rate) / 1000)
    : count - 1;
  const period = skipLoopEndpoint
    ? Math.max(1, Math.min(count, durationPeriod))
    : count;
  const wrapped = frameAtTime % period;
  return wrapped < 0 ? wrapped + period : wrapped;
}

/** Pre-materialize a clip's stable mesh records and transform buffers for allocation-free sampling. */
export function createGeoclipFrameSampleScratch(
  clip: Geoclip,
): GeoclipFrameSampleScratch {
  const frames: PreparedGeoclipFrame[] = [];
  for (const frame of clip.frames) {
    const meshes: GeoclipMeshScratch[] = [];
    let unmaterializedSlot: number | null = null;
    for (let order = 0; order < frame.drawOrder.length; order += 1) {
      const slotIndex = frame.drawOrder[order];
      const slot = frame.slots.get(slotIndex)!;
      if (slot.partId === null) continue;
      const part = clip.parts.get(slot.partId)!;
      if (slot.vertexRecord !== null && slot.positions === null)
        unmaterializedSlot = slotIndex;
      const positions =
        slot.positions ?? new Float32Array(part.referencePositions.length);
      meshes.push({
        drawOrder: order,
        slotIndex,
        partId: part.id,
        texture: part.texture,
        positions,
        uvs: part.uvs,
        indices: part.indices,
        tint: slot.tint,
        blendMode: part.blendMode,
        placement: clip.placement,
      });
    }
    frames.push({ meshes, unmaterializedSlot });
  }
  return new GeoclipFrameSampleScratch(clip, frames);
}

/**
 * Sample an already materialized geoclip into caller-owned storage. On a valid sample it performs
 * no page/map lookup, creates no mesh collection, and reuses the transform Float32Arrays allocated
 * by createGeoclipFrameSampleScratch.
 */
export function sampleGeoclipInto(
  clip: Geoclip,
  timeMs: number,
  scratch: GeoclipFrameSampleScratch,
  loop = true,
  skipLoopEndpoint = true,
): SpineResult<void> {
  if (scratch.clip !== clip) {
    return spineFail(
      diagnostic(
        "invalid-value",
        "scratch.clip",
        "Scratch belongs to a different geoclip.",
      ),
    );
  }
  const selected = geoclipFrameIndex(clip, timeMs, loop, skipLoopEndpoint);
  if (typeof selected !== "number") return spineFail(selected);
  const unresolvedSlot = scratch.select(selected);
  if (unresolvedSlot !== null) {
    return spineFail(
      diagnostic(
        "inconsistent-data",
        `frames[${selected}].slots.${unresolvedSlot}.vref`,
        "Packed vertices must be applied before sampling.",
      ),
    );
  }
  const frame = clip.frames[selected];
  for (let meshIndex = 0; meshIndex < scratch.meshCount; meshIndex += 1) {
    const mesh = scratch.meshes[meshIndex];
    const slot = frame.slots.get(mesh.slotIndex)!;
    if (slot.positions === null) {
      const part = clip.parts.get(mesh.partId)!;
      applyGeoclipTransformInto(
        part.referencePositions,
        slot.transform,
        mesh.positions,
      );
    }
  }
  return spineIntoOk();
}

/** Materialize only the visible, painter-ordered indexed meshes for a playback instant. */
export function sampleGeoclip(
  clip: Geoclip,
  timeMs: number,
  loop = true,
  skipLoopEndpoint = true,
): SpineResult<GeoclipFrameSample> {
  const selected = geoclipFrameIndex(clip, timeMs, loop, skipLoopEndpoint);
  if (typeof selected !== "number") return spineFail(selected);
  const meshes: GeoclipMesh[] = [];
  const frame = clip.frames[selected];
  for (let order = 0; order < frame.drawOrder.length; order += 1) {
    const slotIndex = frame.drawOrder[order];
    const slot = frame.slots.get(slotIndex)!;
    if (slot.partId === null) continue;
    if (slot.vertexRecord !== null && slot.positions === null) {
      return spineFail(
        diagnostic(
          "inconsistent-data",
          `frames[${selected}].slots.${slotIndex}.vref`,
          "Packed vertices must be applied before sampling.",
        ),
      );
    }
    const part = clip.parts.get(slot.partId)!;
    const positions =
      slot.positions ??
      applyGeoclipTransform(part.referencePositions, slot.transform);
    meshes.push({
      drawOrder: order,
      slotIndex,
      partId: part.id,
      texture: part.texture,
      positions,
      uvs: part.uvs,
      indices: part.indices,
      tint: slot.tint,
      blendMode: part.blendMode,
      placement: clip.placement,
    });
  }
  return spineOk({ frameIndex: selected, meshes, placement: clip.placement });
}

export function applyGeoclipTransform(
  positions: Float32Array,
  transform: readonly [number, number, number, number, number, number] | null,
): Float32Array {
  if (!transform) return Float32Array.from(positions);
  const transformed = new Float32Array(positions.length);
  applyGeoclipTransformInto(positions, transform, transformed);
  return transformed;
}

export function applyGeoclipTransformInto(
  positions: Float32Array,
  [a, b, c, d, tx, ty]: readonly [
    number,
    number,
    number,
    number,
    number,
    number,
  ],
  transformed: Float32Array,
): void {
  for (let index = 0; index < positions.length; index += 2) {
    const x = positions[index];
    const y = positions[index + 1];
    transformed[index] = a * x + b * y + tx;
    transformed[index + 1] = c * x + d * y + ty;
  }
}
