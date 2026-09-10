import { spawn } from "node:child_process";

import {
  Sts2CliError,
  invalidJsonOutputError,
  spawnFailureError,
} from "./error.js";
import { binaryNotFoundMessage, resolveOrDownloadSts2Binary } from "./resolve-binary.js";

function parseJson(stdout) {
  if (stdout.trim() === "") {
    return null;
  }

  try {
    return JSON.parse(stdout);
  } catch {
    return null;
  }
}

function baseArgv(options) {
  const argv = ["--json"];

  if (options.configPath) {
    argv.push("--config", options.configPath);
  }

  if (options.mode) {
    argv.push("--mode", options.mode);
  }

  return argv;
}

export async function runJsonCommand(options, commandArgv) {
  const argv = [...baseArgv(options), ...commandArgv];
  let binary;
  try {
    binary = await resolveOrDownloadSts2Binary({
      binaryPath: options.binaryPath,
      env: options.env,
    });
  } catch (error) {
    throw spawnFailureError(error instanceof Error ? error.message : String(error), { argv });
  }

  if (!binary) {
    throw spawnFailureError(binaryNotFoundMessage(), { argv });
  }

  const result = await new Promise((resolve, reject) => {
    const child = spawn(binary, argv, {
      cwd: options.cwd,
      env: options.env,
      stdio: ["ignore", "pipe", "pipe"],
    });

    let stdout = "";
    let stderr = "";

    child.stdout.setEncoding("utf8");
    child.stderr.setEncoding("utf8");

    child.stdout.on("data", (chunk) => {
      stdout += chunk;
    });

    child.stderr.on("data", (chunk) => {
      stderr += chunk;
    });

    child.on("error", (error) => {
      reject(
        spawnFailureError(`failed to launch sts2: ${error.message}`, {
          argv,
          stdout,
          stderr,
        }),
      );
    });

    child.on("close", (exitCode) => {
      resolve({
        exitCode: exitCode ?? 1,
        stdout,
        stderr,
      });
    });
  });

  const payload = parseJson(result.stdout);

  if (result.exitCode !== 0) {
    throw new Sts2CliError({
      exitCode: result.exitCode,
      argv,
      stdout: result.stdout,
      stderr: result.stderr,
      payload,
    });
  }

  if (payload === null) {
    throw invalidJsonOutputError({
      argv,
      stdout: result.stdout,
      stderr: result.stderr,
    });
  }

  return payload;
}
