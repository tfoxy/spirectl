fn asset_catalog_entries() -> Vec<AssetCatalogEntry> {
    vec![
        AssetCatalogEntry {
            key: None,
            key_pattern: "model://cards/<id>/image",
            resolution_kind: "model",
            model_type: Some("cards"),
            model_id: None,
            model_property: Some("PortraitPath/PortraitPngPath/AllPortraitPaths/BetaPortraitPath"),
            source_path: None,
            render_mode: Some("flattened-static"),
            notes: vec!["IDs are read from live ModelDb.AllCards."],
        },
        AssetCatalogEntry {
            key: None,
            key_pattern: "model://relics/<id>/icon",
            resolution_kind: "model",
            model_type: Some("relics"),
            model_id: None,
            model_property: Some("IconPath/PackedIconPath"),
            source_path: None,
            render_mode: Some("flattened-static"),
            notes: vec!["IDs are read from live ModelDb.AllRelics."],
        },
        AssetCatalogEntry {
            key: None,
            key_pattern: "model://relics/<id>/iconOutline",
            resolution_kind: "model",
            model_type: Some("relics"),
            model_id: None,
            model_property: Some("IconOutline"),
            source_path: None,
            render_mode: Some("flattened-relic-model-iconOutline"),
            notes: vec!["IDs are read from live ModelDb.AllRelics."],
        },
        AssetCatalogEntry {
            key: None,
            key_pattern: "model://relics/<id>/bigIcon",
            resolution_kind: "model",
            model_type: Some("relics"),
            model_id: None,
            model_property: Some("BigIcon"),
            source_path: None,
            render_mode: Some("flattened-relic-model-bigIcon"),
            notes: vec!["IDs are read from live ModelDb.AllRelics."],
        },
        AssetCatalogEntry {
            key: None,
            key_pattern: "model://potions/<id>/icon",
            resolution_kind: "model",
            model_type: Some("potions"),
            model_id: None,
            model_property: Some("ImagePath/PackedImagePath"),
            source_path: None,
            render_mode: Some("flattened-static"),
            notes: vec!["IDs are read from live ModelDb.AllPotions."],
        },
        AssetCatalogEntry {
            key: None,
            key_pattern: "model://potions/<id>/outline",
            resolution_kind: "model",
            model_type: Some("potions"),
            model_id: None,
            model_property: Some("OutlinePath"),
            source_path: None,
            render_mode: Some("flattened-static"),
            notes: vec!["IDs are read from live ModelDb.AllPotions."],
        },
        AssetCatalogEntry {
            key: None,
            key_pattern: "model://characters/<id>/icon",
            resolution_kind: "model",
            model_type: Some("characters"),
            model_id: None,
            model_property: Some("IconTexture"),
            source_path: None,
            render_mode: Some("flattened-character-model-icon"),
            notes: vec!["IDs are read from live ModelDb.AllCharacters."],
        },
        AssetCatalogEntry {
            key: None,
            key_pattern: "model://characters/<id>/iconOutline",
            resolution_kind: "model",
            model_type: Some("characters"),
            model_id: None,
            model_property: Some("IconOutlineTexture"),
            source_path: None,
            render_mode: Some("flattened-character-model-iconOutline"),
            notes: vec!["IDs are read from live ModelDb.AllCharacters."],
        },
        AssetCatalogEntry {
            key: None,
            key_pattern: "model://characters/<id>/characterSelectIcon",
            resolution_kind: "model",
            model_type: Some("characters"),
            model_id: None,
            model_property: Some("CharacterSelectIcon"),
            source_path: None,
            render_mode: Some("flattened-character-model-characterSelectIcon"),
            notes: vec![],
        },
        AssetCatalogEntry {
            key: None,
            key_pattern: "model://characters/<id>/characterSelectLockedIcon",
            resolution_kind: "model",
            model_type: Some("characters"),
            model_id: None,
            model_property: Some("CharacterSelectLockedIcon"),
            source_path: None,
            render_mode: Some("flattened-character-model-characterSelectLockedIcon"),
            notes: vec![],
        },
        AssetCatalogEntry {
            key: None,
            key_pattern: "model://characters/<id>/mapMarker",
            resolution_kind: "model",
            model_type: Some("characters"),
            model_id: None,
            model_property: Some("MapMarker"),
            source_path: None,
            render_mode: Some("flattened-character-model-mapMarker"),
            notes: vec![],
        },
        AssetCatalogEntry {
            key: None,
            key_pattern: "model://characters/<id>/characterSelectBg",
            resolution_kind: "model",
            model_type: Some("characters"),
            model_id: None,
            model_property: Some("CharacterSelectBg"),
            source_path: None,
            render_mode: Some("flattened-character-select-background"),
            notes: vec![],
        },
        AssetCatalogEntry {
            key: None,
            key_pattern: "model://characters/<id>/characterSelectBgSpineStill",
            resolution_kind: "model",
            model_type: Some("characters"),
            model_id: None,
            model_property: Some("CharacterSelectBgSpineStill"),
            source_path: None,
            render_mode: Some("flattened-character-select-bg-spine-still"),
            notes: vec![],
        },
        AssetCatalogEntry {
            key: None,
            key_pattern: "model://characters/<id>/energyCounter",
            resolution_kind: "model",
            model_type: Some("characters"),
            model_id: None,
            model_property: Some("EnergyCounterPath"),
            source_path: None,
            render_mode: Some("flattened-scene"),
            notes: vec![],
        },
        AssetCatalogEntry {
            key: None,
            key_pattern: "model://characters/<id>/merchantAnim",
            resolution_kind: "model",
            model_type: Some("characters"),
            model_id: None,
            model_property: Some("MerchantAnimPath"),
            source_path: None,
            render_mode: Some("flattened-scene"),
            notes: vec![],
        },
        AssetCatalogEntry {
            key: None,
            key_pattern: "model://characters/<id>/restSiteAnim",
            resolution_kind: "model",
            model_type: Some("characters"),
            model_id: None,
            model_property: Some("RestSiteAnimPath"),
            source_path: None,
            render_mode: Some("flattened-scene"),
            notes: vec![],
        },
        AssetCatalogEntry {
            key: None,
            key_pattern: "model://characters/<id>/visuals",
            resolution_kind: "model",
            model_type: Some("characters"),
            model_id: None,
            model_property: Some("Visuals"),
            source_path: None,
            render_mode: Some("flattened-character-model-battlefield"),
            notes: vec!["Rendered by live/model visual creation rather than direct .tscn loading."],
        },
        AssetCatalogEntry {
            key: None,
            key_pattern: "model://monsters/<id>/visuals",
            resolution_kind: "model",
            model_type: Some("monsters"),
            model_id: None,
            model_property: Some("VisualsPath"),
            source_path: None,
            render_mode: Some("flattened-scene"),
            notes: vec!["IDs are read from live ModelDb.Monsters."],
        },
        AssetCatalogEntry {
            key: None,
            key_pattern: "model://events/<id>/backgroundScene",
            resolution_kind: "model",
            model_type: Some("events"),
            model_id: None,
            model_property: Some("BackgroundScenePath"),
            source_path: None,
            render_mode: Some("flattened-event-background-scene"),
            notes: vec!["IDs are read from live ModelDb.AllEvents and ModelDb.AllAncients."],
        },
        AssetCatalogEntry {
            key: None,
            key_pattern: "model://events/<id>/backgroundSpineStill",
            resolution_kind: "model",
            model_type: Some("events"),
            model_id: None,
            model_property: Some("BackgroundSpineStill"),
            source_path: None,
            render_mode: Some("flattened-event-background-spine-still"),
            notes: vec!["IDs are read from live ModelDb.AllEvents and ModelDb.AllAncients."],
        },
        AssetCatalogEntry {
            key: None,
            key_pattern: "model://events/<id>/initialPortrait",
            resolution_kind: "model",
            model_type: Some("events"),
            model_id: None,
            model_property: Some("InitialPortraitPath"),
            source_path: None,
            render_mode: Some("flattened-static"),
            notes: vec!["IDs are read from live ModelDb.AllEvents and ModelDb.AllAncients."],
        },
        AssetCatalogEntry {
            key: None,
            key_pattern: "model://acts/<id>/backgroundScene",
            resolution_kind: "model",
            model_type: Some("acts"),
            model_id: None,
            model_property: Some("BackgroundScenePath"),
            source_path: None,
            render_mode: Some("flattened-scene"),
            notes: vec![
                "Root combat-background scene (placeholders only). Combine with combatBackgroundLayers for a faithful render; layers are randomized per map point.",
            ],
        },
        AssetCatalogEntry {
            key: None,
            key_pattern: "model://acts/<id>/backgroundLayer/<stem>",
            resolution_kind: "model",
            model_type: Some("acts"),
            model_id: None,
            model_property: Some("layers"),
            source_path: None,
            render_mode: Some("flattened-scene"),
            notes: vec![
                "One layer scene from the act's combat-background pool (see combatBackgroundLayers).",
            ],
        },
        AssetCatalogEntry {
            key: None,
            key_pattern: "model://acts/<id>/restSiteBackground",
            resolution_kind: "model",
            model_type: Some("acts"),
            model_id: None,
            model_property: Some("RestSiteBackgroundPath"),
            source_path: None,
            render_mode: Some("flattened-scene"),
            notes: vec![],
        },
        AssetCatalogEntry {
            key: None,
            key_pattern: "model://acts/<id>/mapTopBg",
            resolution_kind: "model",
            model_type: Some("acts"),
            model_id: None,
            model_property: Some("MapTopBgPath"),
            source_path: None,
            render_mode: Some("flattened-static"),
            notes: vec![],
        },
        AssetCatalogEntry {
            key: None,
            key_pattern: "model://acts/<id>/mapMidBg",
            resolution_kind: "model",
            model_type: Some("acts"),
            model_id: None,
            model_property: Some("MapMidBgPath"),
            source_path: None,
            render_mode: Some("flattened-static"),
            notes: vec![],
        },
        AssetCatalogEntry {
            key: None,
            key_pattern: "model://acts/<id>/mapBotBg",
            resolution_kind: "model",
            model_type: Some("acts"),
            model_id: None,
            model_property: Some("MapBotBgPath"),
            source_path: None,
            render_mode: Some("flattened-static"),
            notes: vec![],
        },
        AssetCatalogEntry {
            key: None,
            key_pattern: "model://acts/<id>/chestSpine",
            resolution_kind: "model",
            model_type: Some("acts"),
            model_id: None,
            model_property: Some("ChestSpineResourcePath"),
            source_path: None,
            render_mode: Some("flattened-static"),
            notes: vec!["Treasure-room chest Spine skeleton data (.tres)."],
        },
        AssetCatalogEntry {
            key: None,
            key_pattern: "model://encounters/<id>/scene",
            resolution_kind: "model",
            model_type: Some("encounters"),
            model_id: None,
            model_property: Some("ScenePath"),
            source_path: None,
            render_mode: Some("flattened-scene"),
            notes: vec!["Encounter setup scene (monster placement), not a background."],
        },
        AssetCatalogEntry {
            key: None,
            key_pattern: "model://encounters/<id>/background",
            resolution_kind: "model",
            model_type: Some("encounters"),
            model_id: None,
            model_property: Some("BackgroundScenePath"),
            source_path: None,
            render_mode: Some("flattened-scene"),
            notes: vec![
                "Custom-background encounters only (has_custom_background). Others inherit the act's combat-background pool.",
            ],
        },
        AssetCatalogEntry {
            key: None,
            key_pattern: "model://encounters/<id>/backgroundSpineStill",
            resolution_kind: "model",
            model_type: Some("encounters"),
            model_id: None,
            model_property: Some("BackgroundSpineStill"),
            source_path: None,
            render_mode: Some("flattened-event-background-spine-still"),
            notes: vec![
                "Custom-background encounters only; first-frame still of the bg foreground Spine (e.g. the Kaiser Crab body).",
            ],
        },
        AssetCatalogEntry {
            key: None,
            key_pattern: "model://encounters/<id>/backgroundLayer/<stem>",
            resolution_kind: "model",
            model_type: Some("encounters"),
            model_id: None,
            model_property: Some("layers"),
            source_path: None,
            render_mode: Some("flattened-scene"),
            notes: vec![
                "Custom-background encounters only; one layer scene from the encounter's pool.",
            ],
        },
        AssetCatalogEntry {
            key: None,
            key_pattern: "composed://combat-background/<id>/image",
            resolution_kind: "composed",
            model_type: None,
            model_id: None,
            model_property: None,
            source_path: None,
            render_mode: Some("flattened-combat-background-composed"),
            notes: vec![
                "DEPRECATED: pre-flattened single-image background (arbitrary first-per-group layer pick, not seed-faithful). Prefer model://acts/<id>/backgroundScene + combatBackgroundLayers.",
            ],
        },
        AssetCatalogEntry {
            key: None,
            key_pattern: "composed://encounters/<id>/scene-package",
            resolution_kind: "composed",
            model_type: None,
            model_id: None,
            model_property: None,
            source_path: None,
            render_mode: Some("encounter-scene-package"),
            notes: vec!["Cataloged encounter metadata output."],
        },
        AssetCatalogEntry {
            key: None,
            key_pattern: "composed://encounters/<id>/background/image",
            resolution_kind: "composed",
            model_type: None,
            model_id: None,
            model_property: None,
            source_path: None,
            render_mode: Some("flattened-encounter-background"),
            notes: vec![
                "DEPRECATED: pre-flattened custom-encounter background. Prefer model://encounters/<id>/background + combatBackgroundLayers (custom-background encounters only).",
            ],
        },
        AssetCatalogEntry {
            key: None,
            key_pattern: "composed://encounters/<id>/visual-state/<state-id>/overlay/image",
            resolution_kind: "composed",
            model_type: None,
            model_id: None,
            model_property: None,
            source_path: None,
            render_mode: Some("flattened-encounter-visual-overlay"),
            notes: vec![],
        },
        AssetCatalogEntry {
            key: None,
            key_pattern: "composed://encounters/<id>/visual-part/<part-id>/state/<state-id>/image",
            resolution_kind: "composed",
            model_type: None,
            model_id: None,
            model_property: None,
            source_path: None,
            render_mode: Some("flattened-encounter-visual-part"),
            notes: vec![],
        },
    ]
}

fn resolve_asset_key_response(query: &str) -> AssetResolveResponse {
    let normalized = normalize_path_fragment(query);
    if let Some(parsed) = try_parse_model_character_visual_query(&normalized) {
        let key = canonical_model_character_key(&parsed.character_id, &parsed.variant);
        let property = match parsed.variant.as_str() {
            "icon" => "IconTexture",
            "iconoutline" => "IconOutlineTexture",
            "selecticon" => "CharacterSelectIcon",
            "selectlockedicon" => "CharacterSelectLockedIcon",
            "mapmarker" => "MapMarker",
            "characterselectbg" => "CharacterSelectBg",
            "selectbg" => "CharacterSelectBg",
            "characterselectbgspinestill" => "CharacterSelectBgSpineStill",
            "visuals" => "Visuals",
            _ => "",
        };
        return AssetResolveResponse {
            command: "resolve",
            status: "ok",
            query: query.to_string(),
            key: Some(key.clone()),
            resolution_kind: Some("model"),
            model_type: Some("characters"),
            model_id: Some(parsed.character_id),
            model_property: Some(property),
            source_path: Some(key.clone()),
            load_path: Some(key),
            render_mode: Some(if property == "Visuals" {
                "flattened-character-model-battlefield"
            } else if property == "CharacterSelectBgSpineStill" {
                "flattened-character-select-bg-spine-still"
            } else {
                "model-resource-or-texture"
            }),
            notes: vec![
                "Exact model ids are discovered from the live game's ModelDb catalog.".to_string(),
            ],
        };
    }

    if let Some(candidate) = try_parse_model_resource_asset_query(&normalized)
        .or_else(|| try_parse_composed_combat_background_query(&normalized))
        .or_else(|| try_parse_composed_encounter_asset_query(&normalized))
    {
        let (resolution_kind, model_type, model_id, model_property) =
            resolve_candidate_metadata(&candidate);
        return AssetResolveResponse {
            command: "resolve",
            status: "ok",
            query: query.to_string(),
            key: Some(candidate.logical_path.clone()),
            resolution_kind: Some(resolution_kind),
            model_type,
            model_id,
            model_property,
            source_path: Some(candidate.source_path.clone()),
            load_path: Some(candidate.load_path.clone()),
            render_mode: candidate
                .resource_type
                .clone()
                .map(|_| "resolved-by-provider"),
            notes: vec![],
        };
    }

    AssetResolveResponse {
        command: "resolve",
        status: "unsupported",
        query: query.to_string(),
        key: None,
        resolution_kind: None,
        model_type: None,
        model_id: None,
        model_property: None,
        source_path: None,
        load_path: None,
        render_mode: None,
        notes: vec!["Use direct res:// resources or keys listed by assets catalog.".to_string()],
    }
}

fn resolve_candidate_metadata(
    candidate: &AssetCandidate,
) -> (
    &'static str,
    Option<&'static str>,
    Option<String>,
    Option<&'static str>,
) {
    let parts = candidate.logical_path.split('/').collect::<Vec<_>>();
    match parts.as_slice() {
        ["model:", "", "cards", id, "image"] => (
            "model",
            Some("cards"),
            Some((*id).to_string()),
            Some("PortraitPath"),
        ),
        ["model:", "", "relics", id, "icon"] => (
            "model",
            Some("relics"),
            Some((*id).to_string()),
            Some("IconPath"),
        ),
        ["model:", "", "relics", id, "iconOutline"] => (
            "model",
            Some("relics"),
            Some((*id).to_string()),
            Some("IconOutline"),
        ),
        ["model:", "", "relics", id, "bigIcon"] => (
            "model",
            Some("relics"),
            Some((*id).to_string()),
            Some("BigIcon"),
        ),
        ["model:", "", "potions", id, "icon"] => (
            "model",
            Some("potions"),
            Some((*id).to_string()),
            Some("ImagePath"),
        ),
        ["model:", "", "potions", id, "outline"] => (
            "model",
            Some("potions"),
            Some((*id).to_string()),
            Some("OutlinePath"),
        ),
        ["model:", "", "characters", id, "energyCounter"] => (
            "model",
            Some("characters"),
            Some((*id).to_string()),
            Some("EnergyCounterPath"),
        ),
        ["model:", "", "characters", id, "characterSelectBg"] => (
            "model",
            Some("characters"),
            Some((*id).to_string()),
            Some("CharacterSelectBg"),
        ),
        [
            "model:",
            "",
            "characters",
            id,
            "characterSelectBgSpineStill",
        ] => (
            "model",
            Some("characters"),
            Some((*id).to_string()),
            Some("CharacterSelectBgSpineStill"),
        ),
        ["model:", "", "characters", id, "merchantAnim"] => (
            "model",
            Some("characters"),
            Some((*id).to_string()),
            Some("MerchantAnimPath"),
        ),
        ["model:", "", "characters", id, "restSiteAnim"] => (
            "model",
            Some("characters"),
            Some((*id).to_string()),
            Some("RestSiteAnimPath"),
        ),
        ["model:", "", "monsters", id, "visuals"] => (
            "model",
            Some("monsters"),
            Some((*id).to_string()),
            Some("VisualsPath"),
        ),
        ["model:", "", "events", id, "backgroundScene"] => (
            "model",
            Some("events"),
            Some((*id).to_string()),
            Some("BackgroundScenePath"),
        ),
        ["model:", "", "events", id, "backgroundSpineStill"] => (
            "model",
            Some("events"),
            Some((*id).to_string()),
            Some("BackgroundSpineStill"),
        ),
        ["model:", "", "events", id, "initialPortrait"] => (
            "model",
            Some("events"),
            Some((*id).to_string()),
            Some("InitialPortraitPath"),
        ),
        ["model:", "", "acts", id, "backgroundScene"] => (
            "model",
            Some("acts"),
            Some((*id).to_string()),
            Some("BackgroundScenePath"),
        ),
        ["model:", "", "acts", id, "restSiteBackground"] => (
            "model",
            Some("acts"),
            Some((*id).to_string()),
            Some("RestSiteBackgroundPath"),
        ),
        ["model:", "", "acts", id, "mapTopBg"] => (
            "model",
            Some("acts"),
            Some((*id).to_string()),
            Some("MapTopBgPath"),
        ),
        ["model:", "", "acts", id, "mapMidBg"] => (
            "model",
            Some("acts"),
            Some((*id).to_string()),
            Some("MapMidBgPath"),
        ),
        ["model:", "", "acts", id, "mapBotBg"] => (
            "model",
            Some("acts"),
            Some((*id).to_string()),
            Some("MapBotBgPath"),
        ),
        ["model:", "", "acts", id, "chestSpine"] => (
            "model",
            Some("acts"),
            Some((*id).to_string()),
            Some("ChestSpineResourcePath"),
        ),
        ["model:", "", "acts", id, "backgroundLayer", _stem] => (
            "model",
            Some("acts"),
            Some((*id).to_string()),
            Some("layers"),
        ),
        ["model:", "", "encounters", id, "scene"] => (
            "model",
            Some("encounters"),
            Some((*id).to_string()),
            Some("ScenePath"),
        ),
        ["model:", "", "encounters", id, "background"] => (
            "model",
            Some("encounters"),
            Some((*id).to_string()),
            Some("BackgroundScenePath"),
        ),
        ["model:", "", "encounters", id, "backgroundSpineStill"] => (
            "model",
            Some("encounters"),
            Some((*id).to_string()),
            Some("BackgroundSpineStill"),
        ),
        ["model:", "", "encounters", id, "backgroundLayer", _stem] => (
            "model",
            Some("encounters"),
            Some((*id).to_string()),
            Some("layers"),
        ),
        ["composed:", "", ..] => ("composed", None, None, None),
        ["font", ..] => ("font", None, None, None),
        _ => ("resource", None, None, None),
    }
}

fn try_parse_composed_combat_background_query(query: &str) -> Option<AssetCandidate> {
    let normalized = normalize_path_fragment(query);
    let path = normalized.strip_prefix("composed://")?;
    let parts = path.split('/').collect::<Vec<_>>();
    let [family, background_id, variant] = parts.as_slice() else {
        return None;
    };
    if *family != "combat-background"
        || background_id.is_empty()
        || *variant != "image"
        || !background_id
            .chars()
            .all(|ch| ch.is_ascii_alphanumeric() || ch == '_' || ch == '-')
    {
        return None;
    }

    let source_path = format!("scenes/backgrounds/{background_id}/{background_id}_background.tscn");
    Some(AssetCandidate {
        source_root: "resources".to_string(),
        logical_path: format!("res://{source_path}"),
        source_path: source_path.clone(),
        relative_path: PathBuf::from(&source_path),
        asset_kind: AssetKind::Scene,
        storage_kind: "composed".to_string(),
        load_path: format!("composed://combat-background/{background_id}/image"),
        file_path: None,
        container_path: None,
        offline_readable: false,
        resource_type: Some("PackedScene".to_string()),
    })
}

fn try_parse_explain_asset_query(query: &str) -> Option<AssetCandidate> {
    try_parse_composed_combat_background_query(query).or_else(|| {
        let candidate = try_parse_composed_encounter_asset_query(query)?;
        matches!(candidate.asset_kind, AssetKind::EncounterScenePackage).then_some(candidate)
    })
}

fn try_parse_composed_encounter_asset_query(query: &str) -> Option<AssetCandidate> {
    let normalized = normalize_path_fragment(query);
    let without_prefix = normalized.strip_prefix("composed://")?;
    let parsed = parse_encounter_asset_query(without_prefix)?;
    let (source_path, relative_path, asset_kind, resource_type) = match &parsed {
        EncounterAssetQuery::ScenePackage { encounter_id } => (
            format!("composed://encounters/{encounter_id}/scene-package"),
            PathBuf::from("encounters")
                .join(encounter_id)
                .join("scene-package"),
            AssetKind::EncounterScenePackage,
            Some("EncounterScenePackage".to_string()),
        ),
        EncounterAssetQuery::Background { encounter_id } => (
            format!("composed://encounters/{encounter_id}/background/image"),
            PathBuf::from("encounters")
                .join(encounter_id)
                .join("background"),
            AssetKind::EncounterVisual,
            Some("EncounterBackgroundImage".to_string()),
        ),
        EncounterAssetQuery::VisualStateOverlay {
            encounter_id,
            state_id,
        } => (
            format!("composed://encounters/{encounter_id}/visual-state/{state_id}/overlay/image"),
            PathBuf::from("encounters")
                .join(encounter_id)
                .join("visual-state")
                .join(state_id)
                .join("overlay"),
            AssetKind::EncounterVisual,
            Some("EncounterVisualStateOverlay".to_string()),
        ),
        EncounterAssetQuery::VisualPartState {
            encounter_id,
            part_id,
            state_id,
        } => (
            format!(
                "composed://encounters/{encounter_id}/visual-part/{part_id}/state/{state_id}/image"
            ),
            PathBuf::from("encounters")
                .join(encounter_id)
                .join("visual-part")
                .join(part_id)
                .join("state")
                .join(state_id),
            AssetKind::EncounterVisual,
            Some("EncounterVisualPartState".to_string()),
        ),
    };

    Some(AssetCandidate {
        source_root: "composed".to_string(),
        logical_path: source_path.clone(),
        source_path: source_path.clone(),
        relative_path,
        asset_kind,
        storage_kind: "composed".to_string(),
        load_path: source_path,
        file_path: None,
        container_path: None,
        offline_readable: false,
        resource_type,
    })
}

fn parse_encounter_asset_query(query: &str) -> Option<EncounterAssetQuery> {
    let parts = query.split('/').collect::<Vec<_>>();
    if parts.first().copied()? != "encounters" {
        return None;
    }
    let encounter_id = parts.get(1).copied()?;
    if !is_virtual_asset_id(encounter_id) {
        return None;
    }

    match parts.as_slice() {
        ["encounters", _, "scene-package"] => Some(EncounterAssetQuery::ScenePackage {
            encounter_id: encounter_id.to_string(),
        }),
        ["encounters", _, "background", "image"] => Some(EncounterAssetQuery::Background {
            encounter_id: encounter_id.to_string(),
        }),
        [
            "encounters",
            _,
            "visual-state",
            state_id,
            "overlay",
            "image",
        ] if is_virtual_asset_id(state_id) => Some(EncounterAssetQuery::VisualStateOverlay {
            encounter_id: encounter_id.to_string(),
            state_id: (*state_id).to_string(),
        }),
        [
            "encounters",
            _,
            "visual-part",
            part_id,
            "state",
            state_id,
            "image",
        ] if is_virtual_asset_id(part_id) && is_virtual_asset_id(state_id) => {
            Some(EncounterAssetQuery::VisualPartState {
                encounter_id: encounter_id.to_string(),
                part_id: (*part_id).to_string(),
                state_id: (*state_id).to_string(),
            })
        }
        _ => None,
    }
}

fn is_virtual_asset_id(value: &str) -> bool {
    !value.is_empty()
        && value
            .chars()
            .all(|ch| ch.is_ascii_alphanumeric() || ch == '_' || ch == '-')
}

fn try_parse_exact_runtime_resource_query(query: &str) -> Option<AssetCandidate> {
    let normalized = query.replace('\\', "/").trim().to_string();
    let source_path = if let Some(stripped) = normalized.strip_prefix("res://") {
        stripped.trim_start_matches('/').to_string()
    } else if normalized.contains('/') {
        normalized.trim_start_matches('/').to_string()
    } else {
        return None;
    };
    if source_path.is_empty() {
        return None;
    }

    let extension = Path::new(&source_path)
        .extension()
        .and_then(OsStr::to_str)
        .map(|extension| extension.to_ascii_lowercase())?;
    let (asset_kind, resource_type) = match extension.as_str() {
        "png" | "jpg" | "jpeg" | "webp" | "svg" | "exr" | "hdr" => {
            (AssetKind::Image, Some("Texture2D".to_string()))
        }
        "tscn" | "escn" | "scn" => (AssetKind::Scene, Some("PackedScene".to_string())),
        "tres" | "res" => (AssetKind::Resource, None),
        // Shader source served verbatim as text (the WebGL gdshader runtime fetches
        // it to transpile). A plain file under the resources root, so it rides the
        // same offline read/write path as `.tres`.
        "gdshader" | "gdshaderinc" => (AssetKind::Resource, Some("Shader".to_string())),
        "ttf" | "otf" | "woff" | "woff2" | "fontdata" => {
            (AssetKind::Font, Some("FontFile".to_string()))
        }
        _ => return None,
    };

    Some(AssetCandidate {
        source_root: "resources".to_string(),
        logical_path: format!("res://{source_path}"),
        source_path: source_path.clone(),
        relative_path: PathBuf::from(&source_path),
        asset_kind,
        storage_kind: "runtime-resource".to_string(),
        load_path: format!("res://{source_path}"),
        file_path: None,
        container_path: None,
        offline_readable: false,
        resource_type,
    })
}

fn try_parse_exact_runtime_resource_query_with_search_paths(
    query: &str,
    search_paths: &ResolvedSceneSearchPaths,
) -> Option<AssetCandidate> {
    let mut candidate = try_parse_exact_runtime_resource_query(query)?;
    let file_path = search_paths.resources_dir.join(&candidate.relative_path);
    if file_path.is_file() {
        candidate.file_path = Some(file_path);
        candidate.offline_readable = true;
    }

    Some(candidate)
}

/// `res://localization/<lang>/<table>.json` is served verbatim as JSON by the bridge
/// asset seam (renderMode `localization-table`). The static helper index drops it as a
/// non-reportable `.json` blob and `try_parse_exact_runtime_resource_query` excludes the
/// `.json` extension, so without this branch the localization seam is not reachable from
/// `assets extract`. Route it straight to live export, mirroring the bridge's
/// `TryExtractLocalizationJson` contract; the live JSON passthrough then writes it as-is.
fn try_parse_localization_resource_query(query: &str) -> Option<AssetCandidate> {
    // Accept both the raw `res://…` form and the prefix-stripped/lowercased form that
    // `normalize_query` produces before discovery runs (mirrors the dual handling in
    // `try_parse_exact_runtime_resource_query`).
    let normalized = query.replace('\\', "/").trim().to_string();
    let source_path = match normalized.strip_prefix("res://") {
        Some(stripped) => stripped.trim_start_matches('/').to_string(),
        None => normalized.trim_start_matches('/').to_string(),
    };
    let lowered = source_path.to_ascii_lowercase();
    if !lowered.starts_with("localization/") || !lowered.ends_with(".json") {
        return None;
    }

    Some(AssetCandidate {
        source_root: "resources".to_string(),
        logical_path: format!("res://{source_path}"),
        source_path: source_path.clone(),
        relative_path: PathBuf::from(&source_path),
        asset_kind: AssetKind::Resource,
        storage_kind: "runtime-resource".to_string(),
        load_path: format!("res://{source_path}"),
        file_path: None,
        container_path: None,
        offline_readable: false,
        resource_type: Some("LocalizationTable".to_string()),
    })
}

fn is_virtual_character_visual(candidate: &AssetCandidate) -> bool {
    matches!(candidate.source_root.as_str(), "model" | "virtual")
        && candidate.asset_kind == AssetKind::CharacterVisual
}

fn is_virtual_encounter_visual(candidate: &AssetCandidate) -> bool {
    matches!(candidate.source_root.as_str(), "composed" | "virtual")
        && candidate.asset_kind == AssetKind::EncounterVisual
}

fn resolve_absolute_path(path: &Path) -> PathBuf {
    let joined = if path.is_absolute() {
        path.to_path_buf()
    } else {
        std::env::current_dir()
            .map(|cwd| cwd.join(path))
            .unwrap_or_else(|_| path.to_path_buf())
    };
    // Drop "." components so a configured relative dir like "./.sts2/artifacts" does not leave a
    // literal "/./" in every reported output path (join() preserves CurDir components verbatim).
    normalize_curdir_components(&joined)
}

fn normalize_curdir_components(path: &Path) -> PathBuf {
    use std::path::Component;
    let mut normalized = PathBuf::new();
    for component in path.components() {
        match component {
            Component::CurDir => {}
            other => normalized.push(other.as_os_str()),
        }
    }
    normalized
}
