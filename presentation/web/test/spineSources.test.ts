import { describe, expect, it } from "vitest";

import {
  applyGeoclipVerts,
  createGeoclipFrameSampleScratch,
  createRasterSpineFrameSampleScratch,
  geoclipFrameIndexAt,
  parseGeoclip,
  parseRasterSpineClip,
  rasterSpineFrameIndexAt,
  sampleGeoclip,
  sampleGeoclipInto,
  sampleRasterSpineClip,
  sampleRasterSpineClipInto,
  refoldGeoclipTextureDimensions,
  validateGeoclipTextureDimensions,
  recoverGeoclip,
  recoverGeoclipVerts,
  recoveringGeoclipFrameIndexAt,
  sampleRecoveringGeoclipFrame,
} from "../src/spine";

function rasterBlob(version = 0): ArrayBuffer {
  const frames = [
    { duration: 100, payload: [1] },
    { duration: 100, payload: [2] },
    { duration: 100, payload: [3] },
    { duration: 100, payload: [4] }, // Endpoint duplicate for looping clips.
  ];
  const buffer = new ArrayBuffer(40 + frames.length * 29);
  const bytes = new Uint8Array(buffer);
  const view = new DataView(buffer);
  bytes.set([0x53, 0x50, 0x43, 0x4c]);
  view.setUint8(4, version);
  view.setUint32(8, frames.length, true);
  view.setUint32(12, 100, true);
  view.setUint32(16, 50, true);
  view.setUint32(20, 400, true);
  view.setFloat32(24, -10, true);
  view.setFloat32(28, 5, true);
  view.setFloat32(32, 20, true);
  view.setFloat32(36, 10, true);
  let offset = 40;
  frames.forEach((frame, index) => {
    view.setUint32(offset, index, true);
    view.setUint32(offset + 12, 1, true);
    view.setUint32(offset + 16, 1, true);
    view.setUint32(offset + 20, frame.duration, true);
    view.setUint32(offset + 24, frame.payload.length, true);
    bytes.set(frame.payload, offset + 28);
    offset += 29;
  });
  return buffer;
}

function geoclipManifest(schema = "geoclip/0"): unknown {
  return {
    meta: {
      schema,
      anim: "idle",
      fps: 10,
      frameCount: 3,
      durationMs: 200,
      placement: {
        canvasWidth: 100,
        canvasHeight: 50,
        localX: -10,
        localY: 5,
        localWidth: 20,
        localHeight: 10,
        fitScale: 5,
      },
    },
    pages: [{ id: "atlas", file: "atlas.webp", width: 64, height: 32 }],
    parts: [
      {
        id: "body",
        pageId: "atlas",
        srcRect: [16, 8, 32, 16],
        indices: [0, 1, 2],
        uvs: [0, 0, 1, 0, 1, 1],
        refVerts: [0, 0, 2, 0, 2, 2],
        blendMode: 1,
      },
    ],
    frames: [
      {
        drawOrder: [2],
        slots: {
          "2": {
            part: "body",
            color: [1, 0.5, 0.25, 1],
            xform: [2, 0, 0, 3, 7, 11],
          },
        },
      },
      {
        drawOrder: [2],
        slots: { "2": { part: "body", verts: [3, 4, 5, 6, 7, 8] } },
      },
      { drawOrder: [2], slots: { "2": { part: null } } },
    ],
  };
}

function geoclipPackedManifest(schema = "geoclip/0"): unknown {
  const manifest = geoclipManifest(schema) as Record<string, unknown>;
  manifest.meta = {
    schema,
    anim: "idle",
    fps: 30,
    frameCount: 1,
    durationMs: 0,
  };
  manifest.frames = [
    { drawOrder: [2], slots: { "2": { part: "body", vref: 0 } } },
  ];
  manifest.vertsBin = {
    file: "verts.bin",
    records: 1,
    offsets: [0],
    quant: { body: [-10, -20, 10, 20] },
  };
  return manifest;
}

function packedVertices(): ArrayBuffer {
  const buffer = new ArrayBuffer(12);
  const view = new DataView(buffer);
  [0, 0, 32768, 32768, 65535, 65535].forEach((value, index) =>
    view.setUint16(index * 2, value, true),
  );
  return buffer;
}

describe("pure raster Spine sources", () => {
  it("parses production SPCL v2 metadata and samples its true looping period without an image decoder", () => {
    const blob = rasterBlob(2);
    const parsed = parseRasterSpineClip(blob);
    expect(parsed.ok).toBe(true);
    if (!parsed.ok) return;
    expect(parsed.value.frames[1].imageBytes).toEqual(new Uint8Array([2]));
    expect(parsed.value.frames[1].imageBytes.buffer).toBe(blob);
    new Uint8Array(blob)[97] = 9;
    expect(parsed.value.frames[1].imageBytes).toEqual(new Uint8Array([9]));
    expect(rasterSpineFrameIndexAt(parsed.value, 300)).toMatchObject({
      ok: true,
      value: 0,
    });
    expect(rasterSpineFrameIndexAt(parsed.value, 300, false)).toMatchObject({
      ok: true,
      value: 3,
    });
    const sampled = sampleRasterSpineClip(parsed.value, 200);
    expect(sampled).toMatchObject({
      ok: true,
      value: { frameIndex: 2, placement: { localX: -10, localHeight: 10 } },
    });
  });

  it("continues to parse legacy SPCL v0", () => {
    const parsed = parseRasterSpineClip(rasterBlob(0));
    expect(parsed.ok).toBe(true);
    if (parsed.ok)
      expect(parsed.value).toMatchObject({
        canvasWidth: 100,
        frames: expect.any(Array),
      });
  });

  it("fails closed with a typed diagnostic when the duration is inconsistent", () => {
    const blob = rasterBlob();
    new DataView(blob).setUint32(20, 399, true);
    expect(parseRasterSpineClip(blob)).toMatchObject({
      ok: false,
      diagnostics: [
        { code: "inconsistent-data", path: "header.totalDurationMs" },
      ],
    });
  });

  it("rejects unknown SPCL versions", () => {
    expect(parseRasterSpineClip(rasterBlob(1))).toMatchObject({
      ok: false,
      diagnostics: [{ code: "unsupported-version", path: "header.version" }],
    });
  });

  it("samples into a stable caller scratch without replacing its placement or success result", () => {
    const parsed = parseRasterSpineClip(rasterBlob());
    expect(parsed.ok).toBe(true);
    if (!parsed.ok) return;
    const scratch = createRasterSpineFrameSampleScratch();
    const placement = scratch.placement;
    const first = sampleRasterSpineClipInto(parsed.value, 0, scratch);
    const frame = scratch.frame;
    const second = sampleRasterSpineClipInto(parsed.value, 100, scratch);
    expect(first).toBe(second);
    expect(scratch.placement).toBe(placement);
    expect(scratch.frame).not.toBe(frame);
    expect(scratch.frameIndex).toBe(1);
    expect(scratch.placement).toMatchObject({ canvasWidth: 100, localX: -10 });
  });
});

describe("pure geoclip sources", () => {
  it("produces painter-ordered, texture-addressed indexed geometry", () => {
    const parsed = parseGeoclip(geoclipManifest(), (file) => ({
      uri: `memory://${file}`,
      mimeType: "image/webp",
    }));
    expect(parsed.ok).toBe(true);
    if (!parsed.ok) return;
    const sampled = sampleGeoclip(parsed.value, 0);
    expect(sampled.ok).toBe(true);
    if (!sampled.ok) return;
    expect(sampled.value.frameIndex).toBe(0);
    expect(sampled.value.meshes).toHaveLength(1);
    expect(sampled.value.meshes[0]).toMatchObject({
      drawOrder: 0,
      slotIndex: 2,
      texture: { uri: "memory://atlas.webp" },
      tint: [1, 0.5, 0.25, 1],
      blendMode: 1,
      placement: { fitScale: 5 },
    });
    expect(Array.from(sampled.value.meshes[0].positions)).toEqual([
      7, 11, 11, 11, 11, 17,
    ]);
    expect(Array.from(sampled.value.meshes[0].uvs)).toEqual([
      0.25, 0.25, 0.75, 0.25, 0.75, 0.75,
    ]);
    expect(Array.from(sampled.value.meshes[0].indices)).toEqual([0, 1, 2]);
    expect(geoclipFrameIndexAt(parsed.value, 200)).toMatchObject({
      ok: true,
      value: 0,
    });
    expect(sampleGeoclip(parsed.value, 100)).toMatchObject({
      ok: true,
      value: { frameIndex: 1 },
    });
    expect(sampleGeoclip(parsed.value, 200, false)).toMatchObject({
      ok: true,
      value: { meshes: [] },
    });
  });

  it("validates and materializes production geoclip/2 packed vertex records without any resource loading", () => {
    const parsed = parseGeoclip(geoclipPackedManifest("geoclip/2"), (file) => ({
      uri: `memory://${file}`,
    }));
    expect(parsed).toMatchObject({
      ok: true,
      value: {
        schema: "geoclip/2",
        vertexBin: { source: { uri: "memory://verts.bin" } },
      },
    });
    if (!parsed.ok) return;
    expect(sampleGeoclip(parsed.value, 0)).toMatchObject({
      ok: false,
      diagnostics: [{ code: "inconsistent-data" }],
    });
    const applied = applyGeoclipVerts(parsed.value, packedVertices());
    expect(applied.ok).toBe(true);
    if (!applied.ok) return;
    const sampled = sampleGeoclip(applied.value, 0);
    expect(sampled.ok).toBe(true);
    if (!sampled.ok) return;
    expect(Array.from(sampled.value.meshes[0].positions)).toEqual([
      -10, -20, 0.00015259021893143654, 0.0003051804378628731, 10, 20,
    ]);
  });

  it("reuses the pre-materialized mesh collection and transform storage for every tick", () => {
    const parsed = parseGeoclip(geoclipManifest(), (file) => ({
      uri: `memory://${file}`,
    }));
    expect(parsed.ok).toBe(true);
    if (!parsed.ok) return;
    const scratch = createGeoclipFrameSampleScratch(parsed.value);
    const collection = scratch.meshes;
    const first = sampleGeoclipInto(parsed.value, 0, scratch);
    expect(first.ok).toBe(true);
    const mesh = scratch.meshes[0];
    const positions = mesh.positions;
    const second = sampleGeoclipInto(parsed.value, 0, scratch);
    expect(second).toBe(first);
    expect(scratch.meshes).toBe(collection);
    expect(scratch.meshes[0]).toBe(mesh);
    expect(scratch.meshes[0].positions).toBe(positions);
    expect(Array.from(positions)).toEqual([7, 11, 11, 11, 11, 17]);
  });

  it("can strictly reject or immutably refold decoded texture dimension mismatches", () => {
    const parsed = parseGeoclip(geoclipManifest(), (file) => ({
      uri: `memory://${file}`,
    }));
    expect(parsed.ok).toBe(true);
    if (!parsed.ok) return;
    expect(
      validateGeoclipTextureDimensions(parsed.value, () => ({
        width: 128,
        height: 64,
      })),
    ).toMatchObject({
      ok: false,
      diagnostics: [
        { code: "inconsistent-data", path: "pages[0].decodedDimensions" },
      ],
    });
    const refolded = refoldGeoclipTextureDimensions(parsed.value, () => ({
      width: 128,
      height: 64,
    }));
    expect(refolded.ok).toBe(true);
    if (!refolded.ok) return;
    expect(refolded.value).not.toBe(parsed.value);
    expect(refolded.value.pages[0]).toMatchObject({ width: 128, height: 64 });
    expect(Array.from(parsed.value.parts.get("body")!.uvs)).toEqual([
      0.25, 0.25, 0.75, 0.25, 0.75, 0.75,
    ]);
    expect(Array.from(refolded.value.parts.get("body")!.uvs)).toEqual([
      0.125, 0.125, 0.375, 0.125, 0.375, 0.375,
    ]);
    expect(
      validateGeoclipTextureDimensions(refolded.value, () => ({
        width: 128,
        height: 64,
      })),
    ).toMatchObject({ ok: true });
    expect(
      refoldGeoclipTextureDimensions(parsed.value, () => ({
        width: 32,
        height: 16,
      })),
    ).toMatchObject({
      ok: false,
      diagnostics: [{ code: "invalid-value", path: "parts.body.srcRect" }],
    });
  });

  it("keeps legacy sampling lazy when another packed frame has not been applied", () => {
    const raw = geoclipPackedManifest() as {
      meta: { frameCount: number };
      frames: Array<{ drawOrder: number[]; slots: Record<string, unknown> }>;
    };
    raw.meta.frameCount = 2;
    raw.frames = [
      {
        drawOrder: [2],
        slots: { "2": { part: "body", verts: [3, 4, 5, 6, 7, 8] } },
      },
      { drawOrder: [2], slots: { "2": { part: "body", vref: 0 } } },
    ];
    const parsed = parseGeoclip(raw, (file) => ({ uri: `memory://${file}` }));
    expect(parsed.ok).toBe(true);
    if (!parsed.ok) return;
    // A legacy selected-frame sample must not even inspect the unrelated packed frame.
    const unrelatedSlots = parsed.value.frames[1].slots as Map<number, unknown>;
    unrelatedSlots.get = () => {
      throw new Error("legacy sampler inspected an unrelated frame");
    };
    expect(sampleGeoclip(parsed.value, 0)).toMatchObject({
      ok: true,
      value: { frameIndex: 0 },
    });
  });

  it("fails closed for a bad packed offset, quantization bounds, or incomplete draw order", () => {
    const truncated = parseGeoclip(geoclipPackedManifest(), () => ({
      uri: "memory://resource",
    }));
    expect(truncated.ok).toBe(true);
    if (!truncated.ok) return;
    expect(
      applyGeoclipVerts(truncated.value, new ArrayBuffer(2)),
    ).toMatchObject({ ok: false, diagnostics: [{ code: "truncated-data" }] });

    const badQuant = geoclipPackedManifest() as {
      vertsBin: { quant: Record<string, number[]> };
    };
    badQuant.vertsBin.quant.body = [10, 0, -10, 0];
    expect(
      parseGeoclip(badQuant, () => ({ uri: "memory://resource" })),
    ).toMatchObject({
      ok: false,
      diagnostics: [{ path: "vertsBin.quant.body" }],
    });

    const badOffset = geoclipPackedManifest() as {
      vertsBin: { offsets: number[] };
    };
    badOffset.vertsBin.offsets = [-1];
    expect(
      parseGeoclip(badOffset, () => ({ uri: "memory://resource" })),
    ).toMatchObject({ ok: false, diagnostics: [{ path: "vertsBin.offsets" }] });

    const badRecord = geoclipPackedManifest() as {
      frames: Array<{ slots: Record<string, { vref: number }> }>;
    };
    badRecord.frames[0].slots["2"].vref = 1;
    expect(
      parseGeoclip(badRecord, () => ({ uri: "memory://resource" })),
    ).toMatchObject({
      ok: false,
      diagnostics: [{ path: "frames[0].slots.2.vref" }],
    });

    const incompleteOrder = geoclipManifest() as {
      frames: Array<{ drawOrder: number[]; slots: Record<string, unknown> }>;
    };
    incompleteOrder.frames[0].slots["3"] = { part: "body" };
    expect(
      parseGeoclip(incompleteOrder, () => ({ uri: "memory://resource" })),
    ).toMatchObject({
      ok: false,
      diagnostics: [{ path: "frames[0].drawOrder" }],
    });
  });

  it("recovers harness-compatible metadata but keeps a numeric frame-count mismatch fatal", () => {
    const accepted = geoclipManifest("geoclip/1") as {
      meta: Record<string, unknown>;
    };
    delete accepted.meta.frameCount;
    accepted.meta.fps = "30";
    const recovered = recoverGeoclip(accepted);
    expect(recovered.value).not.toBeNull();
    expect(recovered.value?.fps).toBe(30);
    expect(recovered).toMatchObject({
      recovered: true,
      diagnostics: [{ path: "meta.fps" }, { path: "meta.frameCount" }],
    });

    accepted.meta.frameCount = 99;
    const refused = recoverGeoclip(accepted);
    expect(refused.value).toBeNull();
    expect(refused.diagnostics).toContainEqual(
      expect.objectContaining({
        code: "inconsistent-data",
        path: "meta.frameCount",
      }),
    );
  });

  it("characterizes tolerant recovery for empty and malformed manifest branches", () => {
    const cases: ReadonlyArray<{
      readonly name: string;
      readonly mutate: (doc: Record<string, unknown>) => void;
      readonly schema?: string;
      readonly recovered: boolean;
      readonly diagnostic?: string;
    }> = [
      {
        name: "the historical bare schema spelling",
        mutate: (doc) => {
          (doc.meta as Record<string, unknown>).schema = "geoclip";
        },
        schema: "geoclip",
        recovered: false,
      },
      {
        name: "empty collections",
        mutate: (doc) => {
          doc.pages = [];
          doc.parts = [];
          doc.frames = [];
          (doc.meta as Record<string, unknown>).frameCount = 0;
        },
        recovered: false,
      },
      {
        name: "an invalid page",
        mutate: (doc) => {
          doc.pages = [{}];
        },
        recovered: true,
        diagnostic: "pages[0]",
      },
      {
        name: "an invalid part",
        mutate: (doc) => {
          doc.parts = [{ id: "body", refVerts: [0, 0] }];
        },
        recovered: true,
        diagnostic: "parts[0].refVerts",
      },
      {
        name: "a non-numeric slot",
        mutate: (doc) => {
          (doc.frames as Array<Record<string, unknown>>)[0].slots = { nope: {} };
        },
        recovered: true,
        diagnostic: "frames[0].slots.nope",
      },
      {
        name: "a malformed placement",
        mutate: (doc) => {
          (doc.meta as Record<string, unknown>).placement = { canvasWidth: "100" };
        },
        recovered: true,
        diagnostic: "meta.placement",
      },
    ];

    for (const entry of cases) {
      const doc = geoclipManifest("geoclip/1") as Record<string, unknown>;
      entry.mutate(doc);
      const result = recoverGeoclip(doc);
      expect(result.value, entry.name).not.toBeNull();
      expect(result.recovered, entry.name).toBe(entry.recovered);
      if (entry.schema) expect(result.value?.schema, entry.name).toBe(entry.schema);
      if (entry.diagnostic) {
        expect(result.diagnostics, entry.name).toContainEqual(
          expect.objectContaining({ path: entry.diagnostic }),
        );
      }
    }
  });

  it("keeps recovery draw order literal, including incomplete orders", () => {
    const cases: ReadonlyArray<{
      readonly name: string;
      readonly drawOrder: unknown;
      readonly expected: readonly number[];
    }> = [
      { name: "ascending fallback", drawOrder: undefined, expected: [2, 9] },
      { name: "explicit order", drawOrder: [9, 2], expected: [9, 2] },
      { name: "incomplete explicit order", drawOrder: [9], expected: [9] },
    ];
    for (const entry of cases) {
      const doc = geoclipManifest("geoclip/1") as Record<string, unknown>;
      doc.frames = [{
        ...(entry.drawOrder === undefined ? {} : { drawOrder: entry.drawOrder }),
        slots: { "2": { part: "body" }, "9": { part: "body" } },
      }];
      (doc.meta as Record<string, unknown>).frameCount = 1;
      const result = recoverGeoclip(doc);
      expect(result.value, entry.name).not.toBeNull();
      expect(
        sampleRecoveringGeoclipFrame(result.value!, 0).meshes.map(
          (mesh) => mesh.slotIndex,
        ),
        entry.name,
      ).toEqual(entry.expected);
    }
  });

  it("recovers a bad packed vertex record to transform/reference-pose fallback", () => {
    const parsed = recoverGeoclip(geoclipPackedManifest("geoclip/2"));
    expect(parsed.value).not.toBeNull();
    const recovered = recoverGeoclipVerts(parsed.value!, new Uint8Array(0));
    expect(recovered.value).not.toBeNull();
    expect(recovered.recovered).toBe(true);
    expect(recovered.diagnostics).toHaveLength(1);
    const sampled = sampleRecoveringGeoclipFrame(recovered.value!, 0);
    expect(sampled.meshes).toHaveLength(1);
    expect(Array.from(sampled.meshes[0].positions)).toEqual([0, 0, 2, 0, 2, 2]);
  });

  it("distinguishes recoverable packed bytes from strict packed validation", () => {
    const cases: ReadonlyArray<{
      readonly name: string;
      readonly bytes: ArrayBuffer;
      readonly quant: readonly [number, number, number, number];
      readonly offset?: number;
      readonly expected: readonly number[] | null;
    }> = [
      {
        name: "truncated bytes",
        bytes: new ArrayBuffer(2),
        quant: [-10, -20, 10, 20],
        expected: null,
      },
      {
        name: "a zero-span quantization box",
        bytes: packedVertices(),
        quant: [7, -3, 7, -3],
        expected: [7, -3, 7, -3, 7, -3],
      },
      {
        name: "a historical fractional offset",
        bytes: (() => {
          const bytes = new ArrayBuffer(14);
          const view = new DataView(bytes);
          [0, 0, 32768, 32768, 65535, 65535].forEach((value, index) =>
            view.setUint16(index * 2 + 1, value, true),
          );
          return bytes;
        })(),
        quant: [0, 0, 10, 20],
        offset: 1.5,
        expected: [0, 0, 5.0000762939453125, 10.000152587890625, 10, 20],
      },
    ];
    for (const entry of cases) {
      const doc = geoclipPackedManifest("geoclip/2") as {
        vertsBin: { offsets: number[]; quant: Record<string, number[]> };
      };
      doc.vertsBin.quant.body = [...entry.quant];
      if (entry.offset !== undefined) doc.vertsBin.offsets = [entry.offset];
      const parsed = recoverGeoclip(doc);
      const recovered = recoverGeoclipVerts(parsed.value!, entry.bytes);
      const verts = recovered.value!.frames[0].slots.get(2)!.verts;
      expect(verts === null ? null : Array.from(verts), entry.name).toEqual(entry.expected);
    }

    const strict = geoclipPackedManifest() as {
      vertsBin: { offsets: number[] };
    };
    strict.vertsBin.offsets = [0.5];
    expect(parseGeoclip(strict, () => ({ uri: "memory://resource" }))).toMatchObject({
      ok: false,
      diagnostics: [{ path: "vertsBin.offsets" }],
    });
  });

  it("shares the recovering lane's endpoint timing and malformed-inline fallback", () => {
    const recovered = recoverGeoclip(geoclipManifest("geoclip/1"));
    expect(recovered.value).not.toBeNull();
    const clip = recovered.value!;
    expect(recoveringGeoclipFrameIndexAt(clip, 200)).toBe(0);
    expect(
      recoveringGeoclipFrameIndexAt(
        { frames: new Array(3), fps: 0, durationMs: 66 },
        34,
      ),
    ).toBe(1);
    const malformed = recoverGeoclip({
      ...(geoclipManifest("geoclip/1") as Record<string, unknown>),
      frames: [{ slots: { "2": { part: "body", verts: [1, 2] } } }],
      meta: { schema: "geoclip/1", fps: 10, frameCount: 1, durationMs: 0 },
    });
    const sampled = sampleRecoveringGeoclipFrame(malformed.value!, 0);
    expect(Array.from(sampled.meshes[0].positions)).toEqual([0, 0, 2, 0, 2, 2]);
  });

  it("rejects malformed or unsupported source data without yielding partial geometry", () => {
    const unsupported = parseGeoclip({ meta: { schema: "geoclip/3" } }, () => ({
      uri: "memory://unused",
    }));
    expect(unsupported).toMatchObject({
      ok: false,
      diagnostics: [{ code: "unsupported-version", path: "meta.schema" }],
    });
    const broken = geoclipManifest() as {
      parts: Array<Record<string, unknown>>;
    };
    broken.parts[0].indices = [0, 1, 9];
    expect(
      parseGeoclip(broken, () => ({ uri: "memory://atlas" })),
    ).toMatchObject({
      ok: false,
      diagnostics: [{ code: "invalid-value", path: "parts[0].indices" }],
    });
  });
});
