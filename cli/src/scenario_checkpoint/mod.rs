use crate::{
    AppContext, AppError, LifecycleWaitArgs, PerspectiveScopeArg, ScenarioExportArgs,
    ScenarioLoadArgs, StateArgs, TransportKind, bridge, execute_state_json, lifecycle,
};
use serde::{Deserialize, Serialize};
use serde_json::{Value, json};
use sha2::{Digest, Sha256};
use std::collections::BTreeSet;
use std::fs;
use std::path::{Component, Path, PathBuf};
use std::thread;
use std::time::{Duration, Instant};

const SCENARIO_SCHEMA_VERSION: &str = "spirectl.scenario/v0";
const SCENARIO_EXACT_BUNDLE_JSON_SUFFIX: &str = ".exact-bundle.json";
const SCENARIO_EXACT_SAVE_SUFFIX: &str = ".exact-save";
const SCENARIO_FALLBACK_POLICY_DEGRADED: &str = "fall-back-to-sparse-with-degraded-quality";

include!("types.rs");
include!("scenario_flow.rs");
include!("multiplayer.rs");
include!("validation.rs");
include!("helpers.rs");
