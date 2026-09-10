#!/usr/bin/env node

import { spawnSync } from "node:child_process";

import { binaryNotFoundMessage, resolveOrDownloadSts2Binary } from "../src/resolve-binary.js";

async function main() {
  let binary;
  try {
    binary = await resolveOrDownloadSts2Binary();
  } catch (error) {
    process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`);
    process.exitCode = 1;
    return;
  }

  if (!binary) {
    process.stderr.write(`${binaryNotFoundMessage()}\n`);
    process.exitCode = 1;
    return;
  }

  const result = spawnSync(binary, process.argv.slice(2), { stdio: "inherit" });

  if (result.error) {
    process.stderr.write(`failed to launch sts2: ${result.error.message}\n`);
    process.exitCode = 1;
    return;
  }

  process.exitCode = result.status ?? 1;
}

main();
