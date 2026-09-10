use serde_json::Value;

const REFERENCE_TOPICS_CATALOG: &str = include_str!("../../docs/reference-topics.json");

pub(crate) fn reference_topics_json() -> Value {
    serde_json::from_str(REFERENCE_TOPICS_CATALOG)
        .expect("checked-in reference topics catalog is valid JSON")
}
