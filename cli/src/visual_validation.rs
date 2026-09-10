use crate::AppError;
use crate::path_display::display_path;
use image::{DynamicImage, GenericImageView, ImageBuffer, Rgba};
use serde::Deserialize;
use serde_json::{Map, Value, json};
use std::fs;
use std::path::{Path, PathBuf};

#[derive(Debug, Clone, PartialEq, Eq)]
pub(crate) enum ViewportPresetSource {
    BuiltIn,
    Catalog(PathBuf),
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub(crate) struct ViewportPreset {
    pub name: String,
    pub width: u32,
    pub height: u32,
    pub description: Option<String>,
    pub source: ViewportPresetSource,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub(crate) struct ViewportSelection {
    pub preset: Option<String>,
    pub width: u32,
    pub height: u32,
}

#[derive(Debug, Deserialize)]
#[serde(deny_unknown_fields, rename_all = "camelCase")]
struct ViewportCatalogDocument {
    #[serde(default)]
    schema_version: Option<String>,
    #[serde(default)]
    presets: Vec<ViewportCatalogPreset>,
}

#[derive(Debug, Deserialize)]
#[serde(deny_unknown_fields, rename_all = "camelCase")]
struct ViewportCatalogPreset {
    name: String,
    width: u32,
    height: u32,
    #[serde(default)]
    description: Option<String>,
}

const BUILT_IN_PRESETS: &[(&str, u32, u32)] = &[
    ("desktop-1080p", 1920, 1080),
    ("desktop-1440p", 2560, 1440),
    ("steam-deck", 1280, 800),
    ("phone-portrait", 1080, 1920),
    ("phone-landscape", 1920, 1080),
];

pub(crate) fn built_in_presets() -> Vec<ViewportPreset> {
    BUILT_IN_PRESETS
        .iter()
        .map(|(name, width, height)| ViewportPreset {
            name: (*name).to_string(),
            width: *width,
            height: *height,
            description: None,
            source: ViewportPresetSource::BuiltIn,
        })
        .collect()
}

pub(crate) fn load_viewport_presets(
    catalog_paths: &[PathBuf],
) -> Result<Vec<ViewportPreset>, AppError> {
    let mut presets = built_in_presets();

    for catalog_path in catalog_paths {
        let contents = fs::read_to_string(catalog_path).map_err(|source| {
            AppError::invalid_query(
                "preset-catalog",
                &format!(
                    "Failed to read viewport preset catalog '{}': {source}",
                    catalog_path.display()
                ),
            )
        })?;
        let document: ViewportCatalogDocument =
            serde_yaml::from_str(&contents).map_err(|source| {
                AppError::invalid_query(
                    "preset-catalog",
                    &format!(
                        "Failed to parse viewport preset catalog '{}': {source}",
                        catalog_path.display()
                    ),
                )
            })?;

        if document.schema_version.as_deref() != Some("spirectl.viewport-presets/v0") {
            return Err(AppError::invalid_query(
                "preset-catalog",
                &format!(
                    "Viewport preset catalog '{}' must declare schemaVersion: spirectl.viewport-presets/v0.",
                    catalog_path.display()
                ),
            ));
        }

        for preset in document.presets {
            if preset.name.trim().is_empty() {
                return Err(AppError::invalid_query(
                    "preset-catalog",
                    &format!(
                        "Viewport preset catalog '{}' contains a preset with a blank name.",
                        catalog_path.display()
                    ),
                ));
            }
            if preset.width == 0 || preset.height == 0 {
                return Err(AppError::invalid_query(
                    "preset-catalog",
                    &format!(
                        "Viewport preset '{}' in '{}' must use width and height greater than zero.",
                        preset.name,
                        catalog_path.display()
                    ),
                ));
            }
            if let Some(existing) = presets.iter().find(|existing| existing.name == preset.name) {
                return Err(AppError::invalid_query(
                    "preset-catalog",
                    &format!(
                        "Viewport preset '{}' from '{}' duplicates existing preset '{}'.",
                        preset.name,
                        catalog_path.display(),
                        existing.name
                    ),
                ));
            }

            presets.push(ViewportPreset {
                name: preset.name,
                width: preset.width,
                height: preset.height,
                description: preset.description,
                source: ViewportPresetSource::Catalog(catalog_path.clone()),
            });
        }
    }

    Ok(presets)
}

pub(crate) fn resolve_viewport_selection(
    presets: &[ViewportPreset],
    preset: Option<&str>,
    width: Option<u32>,
    height: Option<u32>,
) -> Result<Option<ViewportSelection>, AppError> {
    match (preset, width, height) {
        (Some(_), Some(_), _) | (Some(_), _, Some(_)) => Err(AppError::invalid_query(
            "viewport",
            "Use either --preset or --width/--height, not both.",
        )),
        (Some(preset), None, None) => presets
            .iter()
            .find(|candidate| candidate.name == preset)
            .map(|candidate| ViewportSelection {
                preset: Some(candidate.name.clone()),
                width: candidate.width,
                height: candidate.height,
            })
            .map(Some)
            .ok_or_else(|| {
                AppError::invalid_query("preset", &format!("Unknown viewport preset '{preset}'."))
            }),
        (None, Some(width), Some(height)) if width > 0 && height > 0 => {
            Ok(Some(ViewportSelection {
                preset: None,
                width,
                height,
            }))
        }
        (None, Some(_), Some(_)) => Err(AppError::invalid_query(
            "viewport",
            "Viewport width and height must both be greater than zero.",
        )),
        (None, Some(_), None) | (None, None, Some(_)) => Err(AppError::invalid_query(
            "viewport",
            "Use --width and --height together.",
        )),
        (None, None, None) => Ok(None),
    }
}

pub(crate) fn viewport_presets_json(presets: &[ViewportPreset]) -> Value {
    json!({
        "source": "command-catalog",
        "presets": presets
            .iter()
            .map(|preset| {
                let mut value = json!({
                    "name": preset.name,
                    "width": preset.width,
                    "height": preset.height,
                    "source": viewport_preset_source_json(&preset.source),
                });
                if let Some(description) = &preset.description {
                    value["description"] = Value::String(description.clone());
                }
                value
            })
            .collect::<Vec<_>>(),
    })
}

pub(crate) struct ScreenshotComparison {
    pub payload: Value,
    pub matched: bool,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(crate) struct AlphaBounds {
    pub x: u32,
    pub y: u32,
    pub width: u32,
    pub height: u32,
}

#[derive(Debug, Clone)]
pub(crate) struct PngArtifactMetrics {
    pub width: u32,
    pub height: u32,
    pub total_pixels: u64,
    pub nontransparent_pixels: u64,
    pub alpha_bounds: Option<AlphaBounds>,
    pub transparent_ratio: f64,
}

#[derive(Debug, Clone)]
pub(crate) struct ArtifactCheck {
    pub id: &'static str,
    pub status: &'static str,
    pub message: Option<String>,
    pub payload: Value,
}

pub(crate) fn analyze_png_artifact(path: &Path) -> Result<PngArtifactMetrics, AppError> {
    let bytes = fs::read(path).map_err(|source| {
        AppError::invalid_query(
            "artifact",
            &format!("Failed to read PNG artifact '{}': {source}", path.display()),
        )
    })?;
    let image = image::load_from_memory(&bytes).map_err(|source| {
        AppError::invalid_query(
            "artifact",
            &format!(
                "Failed to decode PNG artifact '{}': {source}",
                path.display()
            ),
        )
    })?;
    Ok(metrics_from_image(&image))
}

pub(crate) fn encounter_artifact_checks(
    path: &Path,
    expected_bounds: Option<AlphaBounds>,
    forbid_full_viewport: bool,
    max_transparent_ratio: Option<f64>,
) -> Result<Vec<ArtifactCheck>, AppError> {
    let metrics = analyze_png_artifact(path)?;
    Ok(encounter_artifact_checks_for_metrics(
        &metrics,
        expected_bounds,
        forbid_full_viewport,
        max_transparent_ratio,
    ))
}

pub(crate) fn encounter_artifact_checks_for_metrics(
    metrics: &PngArtifactMetrics,
    expected_bounds: Option<AlphaBounds>,
    forbid_full_viewport: bool,
    max_transparent_ratio: Option<f64>,
) -> Vec<ArtifactCheck> {
    let mut checks = Vec::new();
    let measured = metrics_json(metrics);
    checks.push(ArtifactCheck {
        id: "nonblank-alpha",
        status: if metrics.nontransparent_pixels > 0 {
            "passed"
        } else {
            "failed"
        },
        message: (metrics.nontransparent_pixels == 0)
            .then(|| "artifact has no nontransparent pixels".to_string()),
        payload: json!({ "measured": measured.clone() }),
    });

    if let Some(max_ratio) = max_transparent_ratio {
        let passed = metrics.transparent_ratio <= max_ratio;
        checks.push(ArtifactCheck {
            id: "transparency-ratio",
            status: if passed { "passed" } else { "failed" },
            message: (!passed).then(|| {
                format!(
                    "artifact transparency ratio {:.4} exceeded maximum {:.4}",
                    metrics.transparent_ratio, max_ratio
                )
            }),
            payload: json!({
                "measured": measured.clone(),
                "maxTransparentRatio": max_ratio,
            }),
        });
    }

    if forbid_full_viewport {
        let full = metrics.alpha_bounds.is_some_and(|bounds| {
            bounds.x == 0
                && bounds.y == 0
                && bounds.width == metrics.width
                && bounds.height == metrics.height
        });
        checks.push(ArtifactCheck {
            id: "encounter-isolated-alpha-bounds",
            status: if !full { "passed" } else { "failed" },
            message: full.then(|| {
                "encounter artifact alpha bounds cover the full viewport; expected isolated overlay or part output"
                    .to_string()
            }),
            payload: json!({ "measured": measured.clone() }),
        });
    }

    if let Some(expected) = expected_bounds {
        let overlap_ratio = metrics
            .alpha_bounds
            .map(|actual| bounds_overlap_ratio(actual, expected))
            .unwrap_or(0.0);
        let passed = overlap_ratio >= 0.80;
        checks.push(ArtifactCheck {
            id: "expected-alpha-framing",
            status: if passed { "passed" } else { "failed" },
            message: (!passed).then(|| {
                "artifact alpha bounds are badly framed against expected bounds".to_string()
            }),
            payload: json!({
                "measured": measured,
                "expectedBounds": bounds_json(expected),
                "minOverlapRatio": 0.80,
                "overlapRatio": overlap_ratio,
            }),
        });
    }

    checks
}

pub(crate) fn artifact_checks_json(checks: &[ArtifactCheck]) -> Value {
    Value::Array(
        checks
            .iter()
            .map(|check| {
                let mut value = json!({
                    "id": check.id,
                    "status": check.status,
                });
                if let Some(message) = &check.message {
                    value["message"] = Value::String(message.clone());
                }
                if let Some(object) = value.as_object_mut()
                    && let Value::Object(payload) = check.payload.clone()
                {
                    object.extend(payload);
                }
                value
            })
            .collect(),
    )
}

fn metrics_from_image(image: &DynamicImage) -> PngArtifactMetrics {
    let rgba = image.to_rgba8();
    let width = rgba.width();
    let height = rgba.height();
    let mut min_x = width;
    let mut min_y = height;
    let mut max_x = 0;
    let mut max_y = 0;
    let mut nontransparent_pixels = 0u64;
    for (x, y, pixel) in rgba.enumerate_pixels() {
        if pixel.0[3] != 0 {
            nontransparent_pixels += 1;
            min_x = min_x.min(x);
            min_y = min_y.min(y);
            max_x = max_x.max(x);
            max_y = max_y.max(y);
        }
    }
    let total_pixels = u64::from(width) * u64::from(height);
    let alpha_bounds = (nontransparent_pixels > 0).then_some(AlphaBounds {
        x: min_x,
        y: min_y,
        width: max_x - min_x + 1,
        height: max_y - min_y + 1,
    });
    PngArtifactMetrics {
        width,
        height,
        total_pixels,
        nontransparent_pixels,
        alpha_bounds,
        transparent_ratio: if total_pixels == 0 {
            1.0
        } else {
            1.0 - (nontransparent_pixels as f64 / total_pixels as f64)
        },
    }
}

fn bounds_overlap_ratio(actual: AlphaBounds, expected: AlphaBounds) -> f64 {
    let x1 = actual.x.max(expected.x);
    let y1 = actual.y.max(expected.y);
    let x2 = (actual.x + actual.width).min(expected.x + expected.width);
    let y2 = (actual.y + actual.height).min(expected.y + expected.height);
    if x2 <= x1 || y2 <= y1 {
        return 0.0;
    }
    let overlap = u64::from(x2 - x1) * u64::from(y2 - y1);
    let expected_area = u64::from(expected.width) * u64::from(expected.height);
    if expected_area == 0 {
        0.0
    } else {
        overlap as f64 / expected_area as f64
    }
}

fn metrics_json(metrics: &PngArtifactMetrics) -> Value {
    json!({
        "width": metrics.width,
        "height": metrics.height,
        "totalPixels": metrics.total_pixels,
        "nontransparentPixels": metrics.nontransparent_pixels,
        "transparentRatio": metrics.transparent_ratio,
        "alphaBounds": metrics.alpha_bounds.map(bounds_json),
    })
}

fn bounds_json(bounds: AlphaBounds) -> Value {
    json!({
        "x": bounds.x,
        "y": bounds.y,
        "width": bounds.width,
        "height": bounds.height,
    })
}

pub(crate) struct ComparisonRequest<'a> {
    pub baseline_path: &'a Path,
    pub actual_path: Option<&'a Path>,
    pub baseline_bytes: &'a [u8],
    pub actual_bytes: &'a [u8],
    pub preset: Option<&'a str>,
    pub requested_width: Option<u32>,
    pub requested_height: Option<u32>,
    pub applied_width: Option<u32>,
    pub applied_height: Option<u32>,
    pub restored_viewport: Option<bool>,
    pub restored_width: Option<u32>,
    pub restored_height: Option<u32>,
    pub max_diff_pixels: Option<u64>,
    pub max_diff_ratio: Option<f64>,
    pub pixel_tolerance: PixelTolerance,
    pub mask_path: Option<&'a Path>,
    pub mask_bytes: Option<&'a [u8]>,
    pub regions_path: Option<&'a Path>,
    pub regions_bytes: Option<&'a [u8]>,
    pub foreground_max_diff_ratio: Option<f64>,
    pub roi_max_diff_ratio: Option<f64>,
    pub required_comparisons: &'a [ComparisonMode],
    pub bundle_dir: Option<&'a Path>,
    pub display_base_dir: Option<&'a Path>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(crate) enum ComparisonMode {
    Full,
    Foreground,
    Roi,
}

pub(crate) fn compare_screenshots(
    request: ComparisonRequest<'_>,
) -> Result<ScreenshotComparison, AppError> {
    let baseline = image::load_from_memory(request.baseline_bytes).map_err(|source| {
        AppError::invalid_query(
            "baseline",
            &format!(
                "Failed to decode baseline PNG '{}': {source}",
                request.baseline_path.display()
            ),
        )
    })?;
    let actual = image::load_from_memory(request.actual_bytes)
        .map_err(|source| AppError::invalid_query("actual", &source.to_string()))?;

    let (baseline_width, baseline_height) = baseline.dimensions();
    let (actual_width, actual_height) = actual.dimensions();
    let threshold_pixels = request.max_diff_pixels.unwrap_or(0);
    let threshold_ratio = request.max_diff_ratio.unwrap_or(0.0);
    let tolerance = request.pixel_tolerance;
    let advanced_comparison = request.mask_bytes.is_some()
        || request.regions_bytes.is_some()
        || !request.required_comparisons.is_empty()
        || request.foreground_max_diff_ratio.is_some()
        || request.roi_max_diff_ratio.is_some();

    let mut payload = json!({
        "matched": false,
        "baseline": {
            "path": display_artifact_path(request.baseline_path, request.display_base_dir),
            "width": baseline_width,
            "height": baseline_height,
        },
        "actual": {
            "path": request
                .actual_path
                .map(|path| display_artifact_path(path, request.display_base_dir)),
            "width": actual_width,
            "height": actual_height,
        },
        "preset": request.preset,
        "requestedViewport": viewport_json(request.requested_width, request.requested_height),
        "appliedViewport": viewport_json(request.applied_width, request.applied_height),
        "restoredViewport": request.restored_viewport,
        "restoredViewportSize": viewport_json(request.restored_width, request.restored_height),
        "maxDiffPixels": threshold_pixels,
        "maxDiffRatio": threshold_ratio,
        "pixelTolerance": tolerance.channel,
        "ignoreAlpha": tolerance.ignore_alpha,
    });

    if baseline_width != actual_width || baseline_height != actual_height {
        payload["dimensionMismatch"] = json!({
            "baseline": { "width": baseline_width, "height": baseline_height },
            "actual": { "width": actual_width, "height": actual_height },
        });
        if let Some(bundle) = request.bundle_dir {
            write_bundle(BundleRequest {
                bundle,
                baseline_bytes: request.baseline_bytes,
                actual_bytes: request.actual_bytes,
                diff_bytes: request.actual_bytes,
                foreground_diff_bytes: None,
                roi_diff_bytes: None,
                display_base_dir: request.display_base_dir,
                payload: &mut payload,
            })?;
        }
        return Ok(ScreenshotComparison {
            payload,
            matched: false,
        });
    }

    let diff = build_diff_image(&baseline, &actual, tolerance);
    payload["diffPixels"] = json!(diff.diff_pixels);
    payload["diffRatio"] = json!(diff.diff_ratio);
    let full_matched = diff.diff_pixels <= threshold_pixels && diff.diff_ratio <= threshold_ratio;
    payload["matched"] = json!(full_matched);

    let mut foreground_diff = None;
    let mut roi_diff = None;

    if advanced_comparison {
        let mut comparisons = Map::new();
        comparisons.insert(
            "full".to_string(),
            comparison_result_json(
                "full",
                diff.diff_pixels,
                diff.diff_ratio,
                u64::from(baseline_width) * u64::from(baseline_height),
                threshold_pixels,
                threshold_ratio,
                full_matched,
            ),
        );

        let mut notices = Vec::new();
        let foreground_required = request
            .required_comparisons
            .contains(&ComparisonMode::Foreground);
        let roi_required = request.required_comparisons.contains(&ComparisonMode::Roi);

        if let Some(mask_bytes) = request.mask_bytes {
            let mask = image::load_from_memory(mask_bytes).map_err(|source| {
                AppError::invalid_query(
                    "mask",
                    &format!(
                        "Failed to decode mask PNG '{}': {source}",
                        request
                            .mask_path
                            .map(|path| path.display().to_string())
                            .unwrap_or_else(|| "<memory>".to_string())
                    ),
                )
            })?;
            let foreground = build_masked_diff_image(&baseline, &actual, &mask, None, tolerance)?;
            let foreground_threshold = request.foreground_max_diff_ratio.unwrap_or(threshold_ratio);
            let matched = foreground.diff_ratio <= foreground_threshold;
            comparisons.insert(
                "foreground".to_string(),
                comparison_result_json(
                    "foreground",
                    foreground.diff_pixels,
                    foreground.diff_ratio,
                    foreground.compared_pixels,
                    0,
                    foreground_threshold,
                    matched,
                ),
            );
            foreground_diff = Some(foreground.diff_png);
            payload["matched"] = json!(payload["matched"].as_bool().unwrap_or(false) && matched);
        } else if foreground_required {
            notices.push(missing_notice(
                "missing-mask",
                "Foreground comparison was required, but no mask PNG was supplied.",
                ComparisonMode::Foreground,
            ));
            payload["matched"] = json!(false);
        }

        if let Some(regions_bytes) = request.regions_bytes {
            let regions = parse_regions(regions_bytes, request.regions_path)?;
            let roi = build_roi_diff_image(&baseline, &actual, &regions, tolerance)?;
            let roi_threshold = request.roi_max_diff_ratio.unwrap_or(threshold_ratio);
            let matched = roi.diff_ratio <= roi_threshold;
            let mut roi_result = comparison_result_json(
                "roi",
                roi.diff_pixels,
                roi.diff_ratio,
                roi.compared_pixels,
                0,
                roi_threshold,
                matched,
            );
            roi_result["regions"] = json!(regions.iter().map(region_json).collect::<Vec<Value>>());
            comparisons.insert("roi".to_string(), roi_result);
            roi_diff = Some(roi.diff_png);
            payload["matched"] = json!(payload["matched"].as_bool().unwrap_or(false) && matched);
        } else if roi_required {
            notices.push(missing_notice(
                "missing-region",
                "ROI comparison was required, but no regions JSON was supplied.",
                ComparisonMode::Roi,
            ));
            payload["matched"] = json!(false);
        }

        payload["comparisons"] = Value::Object(comparisons);
        if !notices.is_empty() {
            payload["notices"] = json!(notices);
        }
    }

    if let Some(bundle) = request.bundle_dir {
        write_bundle(BundleRequest {
            bundle,
            baseline_bytes: request.baseline_bytes,
            actual_bytes: request.actual_bytes,
            diff_bytes: &diff.diff_png,
            foreground_diff_bytes: foreground_diff.as_deref(),
            roi_diff_bytes: roi_diff.as_deref(),
            display_base_dir: request.display_base_dir,
            payload: &mut payload,
        })?;
    }

    Ok(ScreenshotComparison {
        matched: payload["matched"].as_bool().unwrap_or(false),
        payload,
    })
}

struct DiffImage {
    diff_pixels: u64,
    diff_ratio: f64,
    diff_png: Vec<u8>,
}

struct ScopedDiffImage {
    diff_pixels: u64,
    diff_ratio: f64,
    compared_pixels: u64,
    diff_png: Vec<u8>,
}

/// How much per-channel difference still counts as "the same pixel".
///
/// The default is exact RGBA equality, which is what every comparator did before this existed.
/// A non-zero `channel` absorbs renderer anti-aliasing jitter without loosening `maxDiffPixels`
/// or `maxDiffRatio` — those still count and threshold whole pixels, unchanged.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub(crate) struct PixelTolerance {
    /// Maximum absolute per-channel delta that is still considered equal. 0 = exact.
    pub channel: u8,
    /// Ignore the alpha channel entirely. Useful when one side was captured over an opaque
    /// backdrop and the other was not.
    pub ignore_alpha: bool,
}

/// The single pixel-equality rule every comparator uses. Keeping one copy is the point: the full,
/// masked and ROI comparators used to each hard-code `==`, so any tolerance added to one of them
/// would silently not apply to the other two.
fn pixels_differ(baseline: &Rgba<u8>, actual: &Rgba<u8>, tolerance: PixelTolerance) -> bool {
    let [br, bg, bb, ba] = baseline.0;
    let [ar, ag, ab, aa] = actual.0;
    let over_tolerance = |left: u8, right: u8| left.abs_diff(right) > tolerance.channel;

    over_tolerance(br, ar)
        || over_tolerance(bg, ag)
        || over_tolerance(bb, ab)
        || (!tolerance.ignore_alpha && over_tolerance(ba, aa))
}

fn build_diff_image(
    baseline: &DynamicImage,
    actual: &DynamicImage,
    tolerance: PixelTolerance,
) -> DiffImage {
    let (width, height) = baseline.dimensions();
    let baseline_rgba = baseline.to_rgba8();
    let actual_rgba = actual.to_rgba8();
    let mut diff_pixels = 0_u64;
    let mut diff_image: ImageBuffer<Rgba<u8>, Vec<u8>> = ImageBuffer::new(width, height);

    for y in 0..height {
        for x in 0..width {
            let baseline_pixel = baseline_rgba.get_pixel(x, y);
            let actual_pixel = actual_rgba.get_pixel(x, y);
            if pixels_differ(baseline_pixel, actual_pixel, tolerance) {
                diff_pixels += 1;
                diff_image.put_pixel(x, y, Rgba([255, 0, 255, 255]));
            } else {
                let [r, g, b, a] = baseline_pixel.0;
                diff_image.put_pixel(x, y, Rgba([r / 3, g / 3, b / 3, a]));
            }
        }
    }

    let total_pixels = u64::from(width) * u64::from(height);
    let diff_ratio = if total_pixels == 0 {
        0.0
    } else {
        diff_pixels as f64 / total_pixels as f64
    };
    let mut cursor = std::io::Cursor::new(Vec::new());
    DynamicImage::ImageRgba8(diff_image)
        .write_to(&mut cursor, image::ImageFormat::Png)
        .expect("diff image png encoding");

    DiffImage {
        diff_pixels,
        diff_ratio,
        diff_png: cursor.into_inner(),
    }
}

fn build_masked_diff_image(
    baseline: &DynamicImage,
    actual: &DynamicImage,
    mask: &DynamicImage,
    roi: Option<&[RoiRegion]>,
    tolerance: PixelTolerance,
) -> Result<ScopedDiffImage, AppError> {
    let (width, height) = baseline.dimensions();
    let (mask_width, mask_height) = mask.dimensions();
    if mask_width != width || mask_height != height {
        return Err(AppError::invalid_query(
            "mask",
            &format!(
                "Mask dimensions {mask_width}x{mask_height} do not match screenshot dimensions {width}x{height}."
            ),
        ));
    }

    let baseline_rgba = baseline.to_rgba8();
    let actual_rgba = actual.to_rgba8();
    let mask_rgba = mask.to_rgba8();
    let mut diff_pixels = 0_u64;
    let mut compared_pixels = 0_u64;
    let mut diff_image: ImageBuffer<Rgba<u8>, Vec<u8>> = ImageBuffer::new(width, height);

    for y in 0..height {
        for x in 0..width {
            let baseline_pixel = baseline_rgba.get_pixel(x, y);
            let [r, g, b, a] = baseline_pixel.0;
            let selected_by_mask = {
                let [mr, mg, mb, ma] = mask_rgba.get_pixel(x, y).0;
                ma > 0 && (mr > 0 || mg > 0 || mb > 0)
            };
            let selected_by_roi = roi
                .map(|regions| regions.iter().any(|region| region.contains(x, y)))
                .unwrap_or(true);
            if selected_by_mask && selected_by_roi {
                compared_pixels += 1;
                let actual_pixel = actual_rgba.get_pixel(x, y);
                if pixels_differ(baseline_pixel, actual_pixel, tolerance) {
                    diff_pixels += 1;
                    diff_image.put_pixel(x, y, Rgba([255, 0, 255, 255]));
                } else {
                    diff_image.put_pixel(x, y, Rgba([r / 3, g / 3, b / 3, a]));
                }
            } else {
                diff_image.put_pixel(x, y, Rgba([r / 8, g / 8, b / 8, a / 3]));
            }
        }
    }

    Ok(scoped_diff(diff_pixels, compared_pixels, diff_image))
}

fn build_roi_diff_image(
    baseline: &DynamicImage,
    actual: &DynamicImage,
    regions: &[RoiRegion],
    tolerance: PixelTolerance,
) -> Result<ScopedDiffImage, AppError> {
    let (width, height) = baseline.dimensions();
    for region in regions {
        if region.x >= width
            || region.y >= height
            || region.width == 0
            || region.height == 0
            || region.x.saturating_add(region.width) > width
            || region.y.saturating_add(region.height) > height
        {
            return Err(AppError::invalid_query(
                "regions",
                &format!(
                    "ROI region '{}' is outside screenshot dimensions {width}x{height}.",
                    region.id
                ),
            ));
        }
    }

    let baseline_rgba = baseline.to_rgba8();
    let actual_rgba = actual.to_rgba8();
    let mut diff_pixels = 0_u64;
    let mut compared_pixels = 0_u64;
    let mut diff_image: ImageBuffer<Rgba<u8>, Vec<u8>> = ImageBuffer::new(width, height);

    for y in 0..height {
        for x in 0..width {
            let baseline_pixel = baseline_rgba.get_pixel(x, y);
            let [r, g, b, a] = baseline_pixel.0;
            if regions.iter().any(|region| region.contains(x, y)) {
                compared_pixels += 1;
                let actual_pixel = actual_rgba.get_pixel(x, y);
                if pixels_differ(baseline_pixel, actual_pixel, tolerance) {
                    diff_pixels += 1;
                    diff_image.put_pixel(x, y, Rgba([255, 0, 255, 255]));
                } else {
                    diff_image.put_pixel(x, y, Rgba([r / 3, g / 3, b / 3, a]));
                }
            } else {
                diff_image.put_pixel(x, y, Rgba([r / 8, g / 8, b / 8, a / 3]));
            }
        }
    }

    Ok(scoped_diff(diff_pixels, compared_pixels, diff_image))
}

fn scoped_diff(
    diff_pixels: u64,
    compared_pixels: u64,
    diff_image: ImageBuffer<Rgba<u8>, Vec<u8>>,
) -> ScopedDiffImage {
    let diff_ratio = if compared_pixels == 0 {
        0.0
    } else {
        diff_pixels as f64 / compared_pixels as f64
    };
    let mut cursor = std::io::Cursor::new(Vec::new());
    DynamicImage::ImageRgba8(diff_image)
        .write_to(&mut cursor, image::ImageFormat::Png)
        .expect("diff image png encoding");
    ScopedDiffImage {
        diff_pixels,
        diff_ratio,
        compared_pixels,
        diff_png: cursor.into_inner(),
    }
}

#[derive(Debug, Deserialize)]
#[serde(untagged)]
enum RoiRegionsDocument {
    List(Vec<RoiRegion>),
    Object { regions: Vec<RoiRegion> },
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct RoiRegion {
    id: String,
    x: u32,
    y: u32,
    width: u32,
    height: u32,
}

impl RoiRegion {
    fn contains(&self, x: u32, y: u32) -> bool {
        x >= self.x
            && y >= self.y
            && x < self.x.saturating_add(self.width)
            && y < self.y.saturating_add(self.height)
    }
}

fn parse_regions(bytes: &[u8], path: Option<&Path>) -> Result<Vec<RoiRegion>, AppError> {
    let document: RoiRegionsDocument = serde_json::from_slice(bytes).map_err(|source| {
        AppError::invalid_query(
            "regions",
            &format!(
                "Failed to parse ROI regions JSON '{}': {source}",
                path.map(|path| path.display().to_string())
                    .unwrap_or_else(|| "<memory>".to_string())
            ),
        )
    })?;
    let regions = match document {
        RoiRegionsDocument::List(regions) => regions,
        RoiRegionsDocument::Object { regions } => regions,
    };
    if regions.is_empty() {
        return Err(AppError::invalid_query(
            "regions",
            "ROI regions JSON must contain at least one region.",
        ));
    }
    Ok(regions)
}

fn comparison_result_json(
    mode: &str,
    diff_pixels: u64,
    diff_ratio: f64,
    compared_pixels: u64,
    max_diff_pixels: u64,
    max_diff_ratio: f64,
    matched: bool,
) -> Value {
    json!({
        "mode": mode,
        "matched": matched,
        "diffPixels": diff_pixels,
        "diffRatio": diff_ratio,
        "comparedPixels": compared_pixels,
        "maxDiffPixels": max_diff_pixels,
        "maxDiffRatio": max_diff_ratio,
    })
}

fn missing_notice(code: &str, message: &str, mode: ComparisonMode) -> Value {
    json!({
        "code": code,
        "message": message,
        "mode": match mode {
            ComparisonMode::Full => "full",
            ComparisonMode::Foreground => "foreground",
            ComparisonMode::Roi => "roi",
        },
    })
}

fn region_json(region: &RoiRegion) -> Value {
    json!({
        "id": region.id,
        "x": region.x,
        "y": region.y,
        "width": region.width,
        "height": region.height,
    })
}

struct BundleRequest<'a> {
    bundle: &'a Path,
    baseline_bytes: &'a [u8],
    actual_bytes: &'a [u8],
    diff_bytes: &'a [u8],
    foreground_diff_bytes: Option<&'a [u8]>,
    roi_diff_bytes: Option<&'a [u8]>,
    display_base_dir: Option<&'a Path>,
    payload: &'a mut Value,
}

fn write_bundle(request: BundleRequest<'_>) -> Result<(), AppError> {
    let bundle_dir = request.bundle;
    fs::create_dir_all(bundle_dir).map_err(|source| AppError::output_write(bundle_dir, &source))?;
    let comparison_path = bundle_dir.join("comparison.json");
    let baseline_path = bundle_dir.join("baseline.png");
    let actual_path = bundle_dir.join("actual.png");
    let diff_path = bundle_dir.join("diff.png");

    fs::write(&baseline_path, request.baseline_bytes)
        .map_err(|source| AppError::output_write(&baseline_path, &source))?;
    fs::write(&actual_path, request.actual_bytes)
        .map_err(|source| AppError::output_write(&actual_path, &source))?;
    fs::write(&diff_path, request.diff_bytes)
        .map_err(|source| AppError::output_write(&diff_path, &source))?;

    let mut files = Map::new();
    files.insert(
        "comparison".to_string(),
        Value::String(display_artifact_path(
            &comparison_path,
            request.display_base_dir,
        )),
    );
    files.insert(
        "baseline".to_string(),
        Value::String(display_artifact_path(
            &baseline_path,
            request.display_base_dir,
        )),
    );
    files.insert(
        "actual".to_string(),
        Value::String(display_artifact_path(
            &actual_path,
            request.display_base_dir,
        )),
    );
    files.insert(
        "diff".to_string(),
        Value::String(display_artifact_path(&diff_path, request.display_base_dir)),
    );
    if let Some(bytes) = request.foreground_diff_bytes {
        let path = bundle_dir.join("foreground-diff.png");
        fs::write(&path, bytes).map_err(|source| AppError::output_write(&path, &source))?;
        files.insert(
            "foregroundDiff".to_string(),
            Value::String(display_artifact_path(&path, request.display_base_dir)),
        );
    }
    if let Some(bytes) = request.roi_diff_bytes {
        let path = bundle_dir.join("roi-diff.png");
        fs::write(&path, bytes).map_err(|source| AppError::output_write(&path, &source))?;
        files.insert(
            "roiDiff".to_string(),
            Value::String(display_artifact_path(&path, request.display_base_dir)),
        );
    }

    request.payload["bundle"] = json!({
        "dir": display_artifact_path(bundle_dir, request.display_base_dir),
        "files": files,
    });

    let mut rendered =
        serde_json::to_string_pretty(request.payload).expect("comparison json render");
    rendered.push('\n');
    fs::write(&comparison_path, rendered)
        .map_err(|source| AppError::output_write(&comparison_path, &source))?;

    Ok(())
}

fn viewport_preset_source_json(source: &ViewportPresetSource) -> Value {
    match source {
        ViewportPresetSource::BuiltIn => json!({
            "kind": "built-in",
        }),
        ViewportPresetSource::Catalog(path) => json!({
            "kind": "catalog",
            "path": absolute_path(path).display().to_string(),
        }),
    }
}

fn viewport_json(width: Option<u32>, height: Option<u32>) -> Value {
    match (width, height) {
        (Some(width), Some(height)) if width > 0 && height > 0 => json!({
            "width": width,
            "height": height,
        }),
        _ => Value::Null,
    }
}

fn absolute_path(path: &Path) -> PathBuf {
    let absolute = if path.is_absolute() {
        path.to_path_buf()
    } else {
        std::env::current_dir()
            .map(|cwd| cwd.join(path))
            .unwrap_or_else(|_| path.to_path_buf())
    };
    absolute.canonicalize().unwrap_or(absolute)
}

fn display_artifact_path(path: &Path, display_base_dir: Option<&Path>) -> String {
    display_base_dir
        .map(|base_dir| display_path(path, base_dir))
        .unwrap_or_else(|| absolute_path(path).display().to_string())
}
