use crate::{AppError, bridge};
use serde_json::{Value, json};

pub(super) fn measure_runtime_scene_text_ink(
    client: &bridge::RuntimeBridgeClient,
    node: &bridge::proto::RuntimeSceneNodeInfo,
) -> Result<Option<Value>, AppError> {
    if node
        .properties
        .as_ref()
        .and_then(|properties| properties.text.as_ref())
        .is_none()
    {
        return Ok(None);
    }

    let Some(rect) = node
        .computed_transform
        .as_ref()
        .and_then(|transform| transform.global_rect.as_ref())
    else {
        return Ok(None);
    };
    let Some(position) = rect.position.as_ref() else {
        return Ok(None);
    };
    let Some(size) = rect.size.as_ref() else {
        return Ok(None);
    };

    let screenshot = client
        .screenshot(bridge::proto::ScreenshotRequest {
            viewport_width: 0,
            viewport_height: 0,
        })
        .map_err(AppError::bridge)?;
    let image = image::load_from_memory(&screenshot.contents).map_err(|source| AppError {
        exit_code: 2,
        payload: json!({
            "error": {
                "code": "screenshot_ink_probe_failed",
                "message": "failed to decode screenshot for text ink measurement",
                "details": source.to_string()
            }
        }),
    })?;
    let rgba = image.to_rgba8();
    let image_width = rgba.width() as i32;
    let image_height = rgba.height() as i32;
    let crop_x = position.x.floor().max(0.0) as i32;
    let crop_y = position.y.floor().max(0.0) as i32;
    let crop_right = (position.x + size.x).ceil().min(f64::from(image_width)) as i32;
    let crop_bottom = (position.y + size.y).ceil().min(f64::from(image_height)) as i32;
    if crop_right <= crop_x || crop_bottom <= crop_y {
        return Ok(None);
    }

    let crop_width = crop_right - crop_x;
    let crop_height = crop_bottom - crop_y;
    let mut min_x = crop_width;
    let mut min_y = crop_height;
    let mut max_x = -1;
    let mut max_y = -1;
    let mut pixel_count = 0_u64;
    let mut row_counts = vec![0_u32; crop_height as usize];

    for local_y in 0..crop_height {
        for local_x in 0..crop_width {
            let pixel = rgba.get_pixel((crop_x + local_x) as u32, (crop_y + local_y) as u32);
            let [r, g, b, a] = pixel.0;
            if is_text_ink_pixel(r, g, b, a) {
                min_x = min_x.min(local_x);
                min_y = min_y.min(local_y);
                max_x = max_x.max(local_x);
                max_y = max_y.max(local_y);
                pixel_count += 1;
                row_counts[local_y as usize] += 1;
            }
        }
    }

    if pixel_count == 0 {
        return Ok(Some(json!({
            "metricSource": "screenshot-ink",
            "colorFilter": "bright-text",
            "screenshot": {
                "width": screenshot.width,
                "height": screenshot.height,
            },
            "cropRect": {
                "x": crop_x,
                "y": crop_y,
                "width": crop_width,
                "height": crop_height,
            },
            "inkBounds": Value::Null,
            "inkHeightPx": Value::Null,
            "pixelCount": 0,
            "rowRuns": [],
        })));
    }

    let row_runs = screenshot_ink_row_runs(&row_counts);
    Ok(Some(json!({
        "metricSource": "screenshot-ink",
        "colorFilter": "bright-text",
        "screenshot": {
            "width": screenshot.width,
            "height": screenshot.height,
        },
        "cropRect": {
            "x": crop_x,
            "y": crop_y,
            "width": crop_width,
            "height": crop_height,
        },
        "inkBounds": {
            "x": crop_x + min_x,
            "y": crop_y + min_y,
            "width": max_x - min_x + 1,
            "height": max_y - min_y + 1,
            "localX": min_x,
            "localY": min_y,
        },
        "inkHeightPx": max_y - min_y + 1,
        "pixelCount": pixel_count,
        "rowRuns": row_runs,
    })))
}

pub(super) fn attach_runtime_scene_text_screenshot_ink(payload: &mut Value, ink: Value) {
    let Some(text) = payload.pointer_mut("/node/properties/text") else {
        return;
    };
    let Some(text_object) = text.as_object_mut() else {
        return;
    };
    if !text_object.contains_key("renderedMetrics") || text_object["renderedMetrics"].is_null() {
        text_object.insert("renderedMetrics".to_string(), json!({}));
    }
    if let Some(metrics) = text_object
        .get_mut("renderedMetrics")
        .and_then(Value::as_object_mut)
    {
        metrics.insert("screenshotInk".to_string(), ink);
    }
}

fn screenshot_ink_row_runs(row_counts: &[u32]) -> Vec<Value> {
    let mut runs = Vec::new();
    let mut start: Option<usize> = None;
    let mut pixels = 0_u64;
    for (index, count) in row_counts.iter().enumerate() {
        if *count >= 3 {
            if start.is_none() {
                start = Some(index);
                pixels = 0;
            }
            pixels += u64::from(*count);
        } else if let Some(run_start) = start.take() {
            runs.push(json!({
                "y": run_start,
                "height": index - run_start,
                "pixelCount": pixels,
            }));
        }
    }
    if let Some(run_start) = start {
        runs.push(json!({
            "y": run_start,
            "height": row_counts.len() - run_start,
            "pixelCount": pixels,
        }));
    }
    runs
}

fn is_text_ink_pixel(r: u8, g: u8, b: u8, a: u8) -> bool {
    if a < 32 {
        return false;
    }
    let max = r.max(g).max(b);
    let min = r.min(g).min(b);
    (r > 150 && g > 145 && b > 130 && max - min < 80)
        || (r > 150 && g > 115 && b < 125 && r >= g)
        || (g > 145 && r < 170 && b < 130)
}
