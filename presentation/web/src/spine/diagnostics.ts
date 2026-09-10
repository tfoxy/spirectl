/** A machine-readable reason an untrusted clip was refused. */
export interface SpineDiagnostic {
  readonly code:
    | "invalid-type"
    | "missing-field"
    | "unsupported-version"
    | "invalid-value"
    | "invalid-reference"
    | "truncated-data"
    | "inconsistent-data";
  readonly path: string;
  readonly message: string;
}

export type SpineResult<T> =
  | { readonly ok: true; readonly value: T; readonly diagnostics: readonly [] }
  | { readonly ok: false; readonly value: null; readonly diagnostics: readonly SpineDiagnostic[] };

export function spineOk<T>(value: T): SpineResult<T> {
  return { ok: true, value, diagnostics: [] };
}

// Sampling into caller-owned storage is a hot path. Keep its successful result shared so a valid
// animation tick does not need to allocate a result wrapper merely to report success.
const SPINE_INTO_OK = Object.freeze({ ok: true as const, value: undefined, diagnostics: Object.freeze([]) as readonly [] }) as SpineResult<void>;

export function spineIntoOk(): SpineResult<void> {
  return SPINE_INTO_OK;
}

export function spineFail<T>(...diagnostics: readonly SpineDiagnostic[]): SpineResult<T> {
  return { ok: false, value: null, diagnostics };
}

export function diagnostic(
  code: SpineDiagnostic["code"],
  path: string,
  message: string
): SpineDiagnostic {
  return { code, path, message };
}
