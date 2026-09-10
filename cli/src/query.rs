use regex::Regex;
use serde_json::Value;

#[derive(Debug, Clone, PartialEq)]
pub enum Predicate {
    Equals(Value),
    Contains(Value),
    Regex(Value),
    GreaterThan(Value),
    GreaterThanOrEqual(Value),
    LessThan(Value),
    LessThanOrEqual(Value),
    Exists,
    NotExists,
}

impl Predicate {
    pub fn from_raw_equals(raw: &str) -> Self {
        Self::Equals(parse_input_value(raw))
    }

    pub fn from_raw_contains(raw: &str) -> Self {
        Self::Contains(parse_input_value(raw))
    }

    pub fn from_raw_regex(raw: &str) -> Result<Self, String> {
        Regex::new(raw)
            .map(|_| Self::Regex(Value::String(raw.to_string())))
            .map_err(|source| format!("Invalid regex pattern '{raw}': {source}"))
    }

    pub fn from_raw_gt(raw: &str) -> Self {
        Self::GreaterThan(parse_input_value(raw))
    }

    pub fn from_raw_gte(raw: &str) -> Self {
        Self::GreaterThanOrEqual(parse_input_value(raw))
    }

    pub fn from_raw_lt(raw: &str) -> Self {
        Self::LessThan(parse_input_value(raw))
    }

    pub fn from_raw_lte(raw: &str) -> Self {
        Self::LessThanOrEqual(parse_input_value(raw))
    }

    pub fn operator_name(&self) -> &'static str {
        match self {
            Self::Equals(_) => "equals",
            Self::Contains(_) => "contains",
            Self::Regex(_) => "regex",
            Self::GreaterThan(_) => "gt",
            Self::GreaterThanOrEqual(_) => "gte",
            Self::LessThan(_) => "lt",
            Self::LessThanOrEqual(_) => "lte",
            Self::Exists => "exists",
            Self::NotExists => "not-exists",
        }
    }

    pub fn expected_value(&self) -> Option<&Value> {
        match self {
            Self::Equals(value)
            | Self::Contains(value)
            | Self::Regex(value)
            | Self::GreaterThan(value)
            | Self::GreaterThanOrEqual(value)
            | Self::LessThan(value)
            | Self::LessThanOrEqual(value) => Some(value),
            Self::Exists | Self::NotExists => None,
        }
    }
}

#[derive(Debug, Clone, PartialEq)]
pub struct Evaluation {
    pub path: String,
    pub operator: &'static str,
    pub expected: Option<Value>,
    pub actual: Option<Value>,
    pub matched: bool,
    pub resolution: Option<PathResolution>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum QueryError {
    InvalidPath(String),
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PathResolution {
    pub resolved_prefix: String,
    pub missing_field: Option<String>,
    pub missing_index: Option<usize>,
    pub available_keys: Vec<String>,
    pub array_length: Option<usize>,
    pub encountered_type: Option<String>,
}

pub fn evaluate(root: &Value, path: &str, predicate: &Predicate) -> Result<Evaluation, QueryError> {
    let segments = parse_path(path)?;
    let collection_used = segments
        .iter()
        .any(|segment| matches!(segment, PathSegment::WildcardIndex | PathSegment::Filter(_)));
    let (actual, resolution) = match resolve_path(root, &segments) {
        Ok(values) if collection_used => (
            Some(Value::Array(
                values.into_iter().cloned().collect::<Vec<Value>>(),
            )),
            None,
        ),
        Ok(values) => (values.into_iter().next().cloned(), None),
        Err(resolution) => (None, Some(resolution)),
    };
    let matched = matches_predicate(actual.as_ref(), predicate, collection_used);

    Ok(Evaluation {
        path: path.to_string(),
        operator: predicate.operator_name(),
        expected: predicate.expected_value().cloned(),
        actual,
        matched,
        resolution,
    })
}

fn matches_predicate(actual: Option<&Value>, predicate: &Predicate, collection_used: bool) -> bool {
    if collection_used {
        return match predicate {
            Predicate::Exists => actual
                .and_then(Value::as_array)
                .is_some_and(|values| !values.is_empty()),
            Predicate::NotExists => {
                actual.is_none()
                    || actual
                        .and_then(Value::as_array)
                        .is_some_and(|values| values.is_empty())
            }
            _ => actual.and_then(Value::as_array).is_some_and(|values| {
                values
                    .iter()
                    .any(|value| matches_predicate(Some(value), predicate, false))
            }),
        };
    }

    match predicate {
        Predicate::Exists => actual.is_some(),
        Predicate::NotExists => actual.is_none(),
        Predicate::Equals(expected) => actual.is_some_and(|value| value == expected),
        Predicate::Contains(expected) => {
            actual.is_some_and(|value| contains_value(value, expected))
        }
        Predicate::Regex(expected) => actual.is_some_and(|value| matches_regex(value, expected)),
        Predicate::GreaterThan(expected) => {
            compare_numbers(actual, expected, |left, right| left > right)
        }
        Predicate::GreaterThanOrEqual(expected) => {
            compare_numbers(actual, expected, |left, right| left >= right)
        }
        Predicate::LessThan(expected) => {
            compare_numbers(actual, expected, |left, right| left < right)
        }
        Predicate::LessThanOrEqual(expected) => {
            compare_numbers(actual, expected, |left, right| left <= right)
        }
    }
}

fn contains_value(actual: &Value, expected: &Value) -> bool {
    match actual {
        Value::String(text) => expected
            .as_str()
            .is_some_and(|needle| text.contains(needle)),
        Value::Array(items) => items.iter().any(|item| item == expected),
        Value::Object(map) => expected
            .as_str()
            .is_some_and(|needle| map.contains_key(needle)),
        _ => false,
    }
}

fn matches_regex(actual: &Value, expected: &Value) -> bool {
    match (actual.as_str(), expected.as_str()) {
        (Some(text), Some(pattern)) => Regex::new(pattern)
            .ok()
            .is_some_and(|regex| regex.is_match(text)),
        _ => false,
    }
}

fn compare_numbers(
    actual: Option<&Value>,
    expected: &Value,
    compare: impl FnOnce(f64, f64) -> bool,
) -> bool {
    let left = actual.and_then(Value::as_f64);
    let right = expected.as_f64();

    match (left, right) {
        (Some(left), Some(right)) => compare(left, right),
        _ => false,
    }
}

fn parse_input_value(raw: &str) -> Value {
    serde_json::from_str(raw).unwrap_or_else(|_| Value::String(raw.to_string()))
}

#[derive(Debug, Clone, PartialEq, Eq)]
enum FilterOperator {
    Equals,
    NotEquals,
    Contains,
    Regex,
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual,
}

impl FilterOperator {
    fn parse(expression: &str) -> Option<(Self, &str, &str)> {
        for (token, operator) in [
            ("~=", Self::Regex),
            ("*=", Self::Contains),
            (">=", Self::GreaterThanOrEqual),
            ("<=", Self::LessThanOrEqual),
            ("!=", Self::NotEquals),
            ("=", Self::Equals),
            (">", Self::GreaterThan),
            ("<", Self::LessThan),
        ] {
            if let Some((field, expected)) = expression.split_once(token) {
                return Some((operator, field, expected));
            }
        }

        None
    }

    fn matches(&self, actual: &Value, expected: &Value) -> bool {
        match self {
            Self::Equals => actual == expected,
            Self::NotEquals => actual != expected,
            Self::Contains => contains_value(actual, expected),
            Self::Regex => matches_regex(actual, expected),
            Self::GreaterThan => {
                compare_numbers(Some(actual), expected, |left, right| left > right)
            }
            Self::GreaterThanOrEqual => {
                compare_numbers(Some(actual), expected, |left, right| left >= right)
            }
            Self::LessThan => compare_numbers(Some(actual), expected, |left, right| left < right),
            Self::LessThanOrEqual => {
                compare_numbers(Some(actual), expected, |left, right| left <= right)
            }
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
struct FilterSegment {
    field: String,
    field_segments: Vec<PathSegment>,
    operator: FilterOperator,
    expected: Value,
}

#[derive(Debug, Clone, PartialEq, Eq)]
enum PathSegment {
    Field(String),
    Index(usize),
    WildcardIndex,
    Filter(FilterSegment),
}

fn parse_path(path: &str) -> Result<Vec<PathSegment>, QueryError> {
    if path.trim().is_empty() {
        return Err(QueryError::InvalidPath(
            "State query path cannot be empty.".to_string(),
        ));
    }

    let mut segments = Vec::new();
    for part in split_path_parts(path)? {
        let mut remainder = part;
        if !remainder.starts_with('[') {
            let field_end = remainder.find('[').unwrap_or(remainder.len());
            let field = &remainder[..field_end];
            if field.is_empty() {
                return Err(QueryError::InvalidPath(format!(
                    "State query path '{path}' has an empty field segment."
                )));
            }
            segments.push(PathSegment::Field(field.to_string()));
            remainder = &remainder[field_end..];
        }

        while !remainder.is_empty() {
            if !remainder.starts_with('[') {
                return Err(QueryError::InvalidPath(format!(
                    "State query path '{path}' has invalid index syntax near '{remainder}'."
                )));
            }

            let end = remainder.find(']').ok_or_else(|| {
                QueryError::InvalidPath(format!(
                    "State query path '{path}' is missing a closing ']' in '{remainder}'."
                ))
            })?;
            let index = &remainder[1..end];
            if index == "*" {
                segments.push(PathSegment::WildcardIndex);
            } else if let Some((operator, field, expected)) = FilterOperator::parse(index) {
                if expected.trim().is_empty() {
                    return Err(QueryError::InvalidPath(format!(
                        "State query path '{path}' uses an invalid filter expression '[{index}]'."
                    )));
                }
                let field = field.trim();
                segments.push(PathSegment::Filter(FilterSegment {
                    field: field.to_string(),
                    field_segments: parse_filter_field_segments(path, index, field)?,
                    operator,
                    expected: parse_input_value(expected.trim()),
                }));
            } else {
                let index = index.parse::<usize>().map_err(|_| {
                    QueryError::InvalidPath(format!(
                        "State query path '{path}' uses a non-numeric index '{}'.",
                        index
                    ))
                })?;
                segments.push(PathSegment::Index(index));
            }
            remainder = &remainder[end + 1..];
        }
    }

    Ok(segments)
}

fn split_path_parts(path: &str) -> Result<Vec<&str>, QueryError> {
    let mut parts = Vec::new();
    let mut segment_start = 0;
    let mut bracket_depth = 0usize;

    for (index, ch) in path.char_indices() {
        match ch {
            '.' if bracket_depth == 0 => {
                let part = &path[segment_start..index];
                if part.is_empty() {
                    return Err(QueryError::InvalidPath(format!(
                        "State query path '{path}' contains an empty segment."
                    )));
                }
                parts.push(part);
                segment_start = index + ch.len_utf8();
            }
            '[' => bracket_depth += 1,
            ']' => bracket_depth = bracket_depth.saturating_sub(1),
            _ => {}
        }
    }

    let part = &path[segment_start..];
    if part.is_empty() {
        return Err(QueryError::InvalidPath(format!(
            "State query path '{path}' contains an empty segment."
        )));
    }
    parts.push(part);

    Ok(parts)
}

fn parse_filter_field_segments(
    path: &str,
    filter_expression: &str,
    field: &str,
) -> Result<Vec<PathSegment>, QueryError> {
    if field.is_empty() {
        return Ok(Vec::new());
    }

    let segments = parse_path(field).map_err(|_| {
        QueryError::InvalidPath(format!(
            "State query path '{path}' uses an invalid filter expression '[{filter_expression}]'."
        ))
    })?;

    if segments
        .iter()
        .any(|segment| matches!(segment, PathSegment::WildcardIndex | PathSegment::Filter(_)))
    {
        return Err(QueryError::InvalidPath(format!(
            "State query path '{path}' uses an invalid filter expression '[{filter_expression}]'."
        )));
    }

    Ok(segments)
}

#[allow(
    clippy::result_large_err,
    reason = "PathResolution carries structured diagnostics directly for query callers."
)]
fn resolve_path<'a>(
    value: &'a Value,
    segments: &[PathSegment],
) -> Result<Vec<&'a Value>, PathResolution> {
    let mut current_values = vec![value];
    let mut resolved_prefixes = vec![String::new()];

    for segment in segments {
        let mut next_values = Vec::new();
        let mut next_prefixes = Vec::new();
        let mut first_error = None;

        for (current, resolved_prefix) in current_values.into_iter().zip(resolved_prefixes) {
            match segment {
                PathSegment::Field(field) => {
                    let Some(object) = current.as_object() else {
                        if first_error.is_none() {
                            first_error = Some(PathResolution {
                                resolved_prefix: resolved_prefix.clone(),
                                missing_field: Some(field.clone()),
                                missing_index: None,
                                available_keys: Vec::new(),
                                array_length: None,
                                encountered_type: Some(value_type_name(current).to_string()),
                            });
                        }
                        continue;
                    };

                    let Some(value) = object.get(field) else {
                        if first_error.is_none() {
                            first_error = Some(PathResolution {
                                resolved_prefix: resolved_prefix.clone(),
                                missing_field: Some(field.clone()),
                                missing_index: None,
                                available_keys: sorted_keys(object),
                                array_length: None,
                                encountered_type: Some("object".to_string()),
                            });
                        }
                        continue;
                    };

                    next_values.push(value);
                    next_prefixes.push(if resolved_prefix.is_empty() {
                        field.clone()
                    } else {
                        format!("{resolved_prefix}.{field}")
                    });
                }
                PathSegment::Index(index) => {
                    let Some(array) = current.as_array() else {
                        if first_error.is_none() {
                            first_error = Some(PathResolution {
                                resolved_prefix: resolved_prefix.clone(),
                                missing_field: None,
                                missing_index: Some(*index),
                                available_keys: Vec::new(),
                                array_length: None,
                                encountered_type: Some(value_type_name(current).to_string()),
                            });
                        }
                        continue;
                    };

                    let Some(value) = array.get(*index) else {
                        if first_error.is_none() {
                            first_error = Some(PathResolution {
                                resolved_prefix: resolved_prefix.clone(),
                                missing_field: None,
                                missing_index: Some(*index),
                                available_keys: Vec::new(),
                                array_length: Some(array.len()),
                                encountered_type: Some("array".to_string()),
                            });
                        }
                        continue;
                    };

                    next_values.push(value);
                    next_prefixes.push(format!("{resolved_prefix}[{index}]"));
                }
                PathSegment::WildcardIndex => {
                    let Some(array) = current.as_array() else {
                        if first_error.is_none() {
                            first_error = Some(PathResolution {
                                resolved_prefix: resolved_prefix.clone(),
                                missing_field: None,
                                missing_index: Some(0),
                                available_keys: Vec::new(),
                                array_length: None,
                                encountered_type: Some(value_type_name(current).to_string()),
                            });
                        }
                        continue;
                    };

                    for (index, value) in array.iter().enumerate() {
                        next_values.push(value);
                        next_prefixes.push(format!("{resolved_prefix}[{index}]"));
                    }
                }
                PathSegment::Filter(filter) => {
                    let Some(array) = current.as_array() else {
                        if first_error.is_none() {
                            first_error = Some(PathResolution {
                                resolved_prefix: resolved_prefix.clone(),
                                missing_field: Some(filter.field.clone()),
                                missing_index: Some(0),
                                available_keys: Vec::new(),
                                array_length: None,
                                encountered_type: Some(value_type_name(current).to_string()),
                            });
                        }
                        continue;
                    };

                    for (index, value) in array.iter().enumerate() {
                        let matches = if filter.field_segments.is_empty() {
                            matches_filter_recursively(value, filter)
                        } else {
                            let Ok(actuals) = resolve_path(value, &filter.field_segments) else {
                                continue;
                            };
                            actuals
                                .into_iter()
                                .any(|actual| filter.operator.matches(actual, &filter.expected))
                        };

                        if matches {
                            next_values.push(value);
                            next_prefixes.push(format!("{resolved_prefix}[{index}]"));
                        }
                    }
                }
            }
        }

        if next_values.is_empty() {
            if let Some(error) = first_error {
                return Err(error);
            }
            return Ok(Vec::new());
        }

        current_values = next_values;
        resolved_prefixes = next_prefixes;
    }

    Ok(current_values)
}

fn sorted_keys(object: &serde_json::Map<String, Value>) -> Vec<String> {
    let mut keys = object.keys().cloned().collect::<Vec<_>>();
    keys.sort();
    keys
}

fn matches_filter_recursively(value: &Value, filter: &FilterSegment) -> bool {
    if filter.operator.matches(value, &filter.expected) {
        return true;
    }

    match value {
        Value::Array(items) => items
            .iter()
            .any(|item| matches_filter_recursively(item, filter)),
        Value::Object(map) => map
            .values()
            .any(|item| matches_filter_recursively(item, filter)),
        _ => false,
    }
}

fn value_type_name(value: &Value) -> &'static str {
    match value {
        Value::Null => "null",
        Value::Bool(_) => "boolean",
        Value::Number(_) => "number",
        Value::String(_) => "string",
        Value::Array(_) => "array",
        Value::Object(_) => "object",
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn resolves_nested_paths_with_array_indexes() {
        let value = json!({
            "combat": {
                "players": [
                    { "energy": 3 }
                ]
            }
        });

        let evaluation = evaluate(
            &value,
            "combat.players[0].energy",
            &Predicate::from_raw_equals("3"),
        )
        .expect("evaluation");

        assert!(evaluation.matched);
        assert_eq!(evaluation.actual, Some(json!(3)));
    }

    #[test]
    fn rejects_invalid_path_syntax() {
        let error =
            evaluate(&json!({}), "combat..turn", &Predicate::Exists).expect_err("invalid path");

        assert_eq!(
            error,
            QueryError::InvalidPath(
                "State query path 'combat..turn' contains an empty segment.".to_string()
            )
        );
    }

    #[test]
    fn rejects_invalid_filter_syntax() {
        let error =
            evaluate(&json!({}), "choices[=]", &Predicate::Exists).expect_err("invalid path");

        assert_eq!(
            error,
            QueryError::InvalidPath(
                "State query path 'choices[=]' uses an invalid filter expression '[=]'."
                    .to_string()
            )
        );
    }

    #[test]
    fn reports_missing_segment_resolution_details() {
        let value = json!({
            "lobby": {
                "playersById": {
                    "p:100": {
                        "isReady": false
                    }
                }
            }
        });

        let evaluation = evaluate(
            &value,
            "lobby.playersById.p:404.isReady",
            &Predicate::Exists,
        )
        .expect("evaluation");

        assert!(!evaluation.matched);
        assert_eq!(
            evaluation.resolution,
            Some(PathResolution {
                resolved_prefix: "lobby.playersById".to_string(),
                missing_field: Some("p:404".to_string()),
                missing_index: None,
                available_keys: vec!["p:100".to_string()],
                array_length: None,
                encountered_type: Some("object".to_string()),
            })
        );
    }

    #[test]
    fn matches_string_values_with_regex_predicates() {
        let value = json!({
            "screen": {
                "type": "main-menu"
            }
        });

        let evaluation = evaluate(
            &value,
            "screen.type",
            &Predicate::from_raw_regex("^main-.*$").expect("regex"),
        )
        .expect("evaluation");

        assert!(evaluation.matched);
        assert_eq!(evaluation.operator, "regex");
        assert_eq!(evaluation.expected, Some(json!("^main-.*$")));
    }

    #[test]
    fn matches_any_value_from_array_wildcard_paths() {
        let value = json!({
            "combat": {
                "players": [
                    { "energy": 1 },
                    { "energy": 3 }
                ]
            }
        });

        let evaluation = evaluate(
            &value,
            "combat.players[*].energy",
            &Predicate::from_raw_gte("3"),
        )
        .expect("evaluation");

        assert!(evaluation.matched);
        assert_eq!(evaluation.actual, Some(json!([1, 3])));
    }

    #[test]
    fn matches_any_value_from_array_filter_paths() {
        let value = json!({
            "choices": [
                {
                    "id": "menu:start-run",
                    "label": "Start Run"
                },
                {
                    "id": "menu:settings",
                    "label": "Settings"
                }
            ]
        });

        let evaluation = evaluate(
            &value,
            "choices[id=menu:start-run].label",
            &Predicate::from_raw_equals("Start Run"),
        )
        .expect("evaluation");

        assert!(evaluation.matched);
        assert_eq!(evaluation.actual, Some(json!(["Start Run"])));
    }

    #[test]
    fn matches_any_value_from_array_inequality_filter_paths() {
        let value = json!({
            "choices": [
                {
                    "id": "menu:start-run",
                    "label": "Start Run"
                },
                {
                    "id": "menu:settings",
                    "label": "Settings"
                }
            ]
        });

        let evaluation = evaluate(
            &value,
            "choices[id!=menu:settings].label",
            &Predicate::from_raw_equals("Start Run"),
        )
        .expect("evaluation");

        assert!(evaluation.matched);
        assert_eq!(evaluation.actual, Some(json!(["Start Run"])));
    }

    #[test]
    fn matches_any_value_from_array_numeric_comparison_filter_paths() {
        let value = json!({
            "combat": {
                "players": [
                    {
                        "id": "p1",
                        "energy": 1
                    },
                    {
                        "id": "p2",
                        "energy": 3
                    }
                ]
            }
        });

        let evaluation = evaluate(
            &value,
            "combat.players[energy>=3].id",
            &Predicate::from_raw_equals("p2"),
        )
        .expect("evaluation");

        assert!(evaluation.matched);
        assert_eq!(evaluation.actual, Some(json!(["p2"])));
    }

    #[test]
    fn matches_any_value_from_array_contains_filter_paths() {
        let value = json!({
            "choices": [
                {
                    "id": "menu:start-run",
                    "label": "Start Run"
                },
                {
                    "id": "menu:settings",
                    "label": "Settings"
                }
            ]
        });

        let evaluation = evaluate(
            &value,
            "choices[label*=Run].id",
            &Predicate::from_raw_equals("menu:start-run"),
        )
        .expect("evaluation");

        assert!(evaluation.matched);
        assert_eq!(evaluation.actual, Some(json!(["menu:start-run"])));
    }

    #[test]
    fn matches_any_value_from_array_regex_filter_paths() {
        let value = json!({
            "choices": [
                {
                    "id": "reward:p1:0",
                    "label": "Claim 30 Gold"
                },
                {
                    "id": "reward:p1:1",
                    "label": "Take Anchor"
                },
                {
                    "id": "reward-flow:skip",
                    "label": "Skip Rewards"
                }
            ]
        });

        let evaluation = evaluate(
            &value,
            "choices[id~=^reward:p1:].label",
            &Predicate::from_raw_equals("Claim 30 Gold"),
        )
        .expect("evaluation");

        assert!(evaluation.matched);
        assert_eq!(
            evaluation.actual,
            Some(json!(["Claim 30 Gold", "Take Anchor"]))
        );
    }

    #[test]
    fn matches_any_value_from_nested_field_filter_paths() {
        let value = json!({
            "choices": [
                {
                    "id": "menu:start-run",
                    "preferredAction": "choose"
                },
                {
                    "id": "menu:settings",
                    "preferredAction": "choose"
                }
            ]
        });

        let evaluation = evaluate(
            &value,
            "choices[id=menu:start-run].preferredAction",
            &Predicate::from_raw_equals("choose"),
        )
        .expect("evaluation");

        assert!(evaluation.matched);
        assert_eq!(evaluation.actual, Some(json!(["choose"])));
    }

    #[test]
    fn matches_any_value_from_object_wide_filter_paths() {
        let value = json!({
            "choices": [
                {
                    "id": "menu:start-run",
                    "preferredAction": "choose"
                },
                {
                    "id": "menu:settings",
                    "preferredAction": "choose"
                }
            ]
        });

        let evaluation = evaluate(
            &value,
            "choices[=menu:start-run].preferredAction",
            &Predicate::from_raw_equals("choose"),
        )
        .expect("evaluation");

        assert!(evaluation.matched);
        assert_eq!(evaluation.actual, Some(json!(["choose"])));
    }

    #[test]
    fn treats_empty_wildcard_results_as_not_existing() {
        let value = json!({
            "choices": []
        });

        let evaluation = evaluate(&value, "choices[*]", &Predicate::NotExists).expect("evaluation");

        assert!(evaluation.matched);
        assert_eq!(evaluation.actual, Some(json!([])));
        assert_eq!(evaluation.resolution, None);
    }

    #[test]
    fn treats_empty_filter_results_as_not_existing() {
        let value = json!({
            "choices": [
                {
                    "id": "menu:start-run"
                }
            ]
        });

        let evaluation = evaluate(&value, "choices[id=menu:missing].id", &Predicate::NotExists)
            .expect("evaluation");

        assert!(evaluation.matched);
        assert_eq!(evaluation.actual, Some(json!([])));
        assert_eq!(evaluation.resolution, None);
    }
}
