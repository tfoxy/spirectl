//! Optional progress channel for the long lifecycle commands.
//!
//! A `game deploy --build --restart --verify` can run for minutes with nothing
//! on the terminal: output is buffered and printed once at exit, so the command
//! is indistinguishable from a hang. `--progress` turns on newline-delimited
//! JSON progress lines.
//!
//! They go to **stderr**, never stdout: the structured result on stdout stays
//! exactly what it was, so `sts2 --json … | jq` keeps working with `--progress`
//! on, and a caller that does not want them can redirect stderr.
//!
//! The switch is process-global on purpose. Progress is emitted from deep
//! inside process-stop and wait loops that do not carry an `AppContext`, and a
//! CLI process runs exactly one command.

use serde_json::{Value, json};
use std::io::Write;
use std::sync::OnceLock;
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::Instant;

static ENABLED: AtomicBool = AtomicBool::new(false);
static STARTED_AT: OnceLock<Instant> = OnceLock::new();

/// Enable progress output for this process. Called once from the CLI entry
/// points with the value of the global `--progress` flag.
pub(crate) fn set_enabled(enabled: bool) {
    ENABLED.store(enabled, Ordering::Relaxed);
    if enabled {
        let _ = STARTED_AT.set(Instant::now());
    }
}

pub(crate) fn is_enabled() -> bool {
    ENABLED.load(Ordering::Relaxed)
}

/// Emit one progress line. `fields` (an object, or `Value::Null`) is merged
/// into the envelope so a phase can carry whatever the reader needs.
pub(crate) fn emit(command: &str, phase: &str, fields: Value) {
    if !is_enabled() {
        return;
    }

    let elapsed_ms = STARTED_AT
        .get()
        .map(|started_at| started_at.elapsed().as_millis())
        .unwrap_or(0);
    let mut line = json!({
        "progress": command,
        "phase": phase,
        "elapsedMs": elapsed_ms,
    });
    if let Some(object) = line.as_object_mut()
        && let Some(extra) = fields.as_object()
    {
        for (key, value) in extra {
            object.insert(key.clone(), value.clone());
        }
    }

    let mut stderr = std::io::stderr().lock();
    let _ = writeln!(stderr, "{line}");
    let _ = stderr.flush();
}
