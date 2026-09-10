import { writeFile } from "node:fs/promises";

import { createSts2Client } from "./index.js";

function shouldCaptureFailureArtifacts(testInfo, options) {
  return options?.enabled === true && testInfo?.status !== testInfo?.expectedStatus;
}

function resolvedLogsOptions(options = {}) {
  if (options.tail !== undefined || options.limit !== undefined) {
    return options;
  }

  return { tail: 50, ...options };
}

function artifactCaptureLabel(error) {
  return error instanceof Error ? error.message : String(error);
}

async function captureArtifact(name, attempt) {
  try {
    await attempt();
  } catch (error) {
    console.warn(`sts2 Playwright failure artifact capture skipped for ${name}: ${artifactCaptureLabel(error)}`);
  }
}

async function captureFailureArtifacts(client, testInfo, options = {}) {
  const diagnosticsPath = testInfo.outputPath("sts2-diagnostics.json");
  const bundleDir = testInfo.outputPath("sts2-evidence");

  await captureArtifact("sts2-diagnostics.json", async () => {
    const diagnostics = await client.diagnostics({
      bundleDir,
      ...resolvedLogsOptions(options.logs),
      ...(options.screenshot ?? {}),
    });
    await writeFile(diagnosticsPath, `${JSON.stringify(diagnostics, null, 2)}\n`, "utf8");
  });
}

export async function hotReloadAndWait(sts2, options = {}) {
  if (!sts2?.hotReload || !sts2?.hotReloadStatus) {
    throw new TypeError("hotReloadAndWait requires an sts2 client with hotReload and hotReloadStatus.");
  }
  if (!options.project) {
    throw new TypeError("hotReloadAndWait requires project.");
  }

  const before = await sts2.hotReloadStatus({ project: options.project });
  const result = await sts2.hotReload({ ...options, wait: true });
  const after = await sts2.hotReloadStatus({ project: options.project });
  const beforeGeneration = before?.shell?.activeGeneration;
  const afterGeneration = result?.reload?.generation ?? after?.shell?.activeGeneration;
  const generationChanged =
    Number.isInteger(beforeGeneration) &&
    Number.isInteger(afterGeneration) &&
    afterGeneration > beforeGeneration;

  if (options.expectGenerationChanged !== false && !generationChanged) {
    const error = new Error("Hot reload completed but generation did not increase.");
    error.before = before;
    error.result = result;
    error.after = after;
    throw error;
  }

  return { before, result, after, generationChanged };
}

export function withSts2(base, options = {}) {
  if (!base?.extend) {
    throw new TypeError("withSts2 requires a Playwright base test object with extend().");
  }

  return base.extend({
    async sts2({}, use, testInfo) {
      const client = createSts2Client(options.client ?? {});
      await use(client);

      if (shouldCaptureFailureArtifacts(testInfo, options.failureArtifacts)) {
        await captureFailureArtifacts(client, testInfo, options.failureArtifacts);
      }
    },
  });
}
