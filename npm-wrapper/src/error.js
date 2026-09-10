export class Sts2CliError extends Error {
  constructor({ exitCode, argv, stdout, stderr, payload }) {
    const message =
      payload?.error?.message ?? (stderr.trim() || `sts2 exited with code ${exitCode}.`);

    super(message);
    this.name = "Sts2CliError";
    this.exitCode = exitCode;
    this.argv = argv;
    this.stdout = stdout;
    this.stderr = stderr;
    this.payload = payload;
  }
}

export function invalidJsonOutputError({ argv, stdout, stderr }) {
  const error = new Error("sts2 returned exit code 0 but stdout was not valid JSON.");
  error.name = "Sts2CliProtocolError";
  error.argv = argv;
  error.stdout = stdout;
  error.stderr = stderr;
  return error;
}

export function spawnFailureError(message, { argv, stdout = "", stderr = "" }) {
  const error = new Error(message);
  error.name = "Sts2CliSpawnError";
  error.argv = argv;
  error.stdout = stdout;
  error.stderr = stderr;
  return error;
}
