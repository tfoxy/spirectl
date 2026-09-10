use super::*;

#[test]
fn dev_load_fixture_rejects_unsupported_screen() {
    let fixture = tempfile::NamedTempFile::new().expect("temp fixture");
    fs::write(
        fixture.path(),
        r#"schemaVersion: spirectl.fixture/v0
name: not-combat
run:
  currentActIndex: 0
"#,
    )
    .expect("write fixture");

    let response = try_run_error(&[
        "sts2",
        "--json",
        "dev",
        "load-fixture",
        "--path",
        fixture.path().to_str().expect("utf8 path"),
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 2);
    assert_eq!(payload["error"]["code"], "invalid_fixture");
    assert_eq!(payload["error"]["details"][0]["field"], "screen");
}

#[test]
fn dev_load_fixture_rejects_multiplayer_combat_with_true_remote_player() {
    let fixture = tempfile::NamedTempFile::new().expect("temp fixture");
    fs::write(
        fixture.path(),
        r#"schemaVersion: spirectl.fixture/v0
name: remote-combat-player
run:
  players:
    - id: "p:1"
      characterId: IRONCLAD
      isLocal: true
      isHostLocalSeat: false
      slotId: 0
    - id: "p:2"
      characterId: SILENT
      isLocal: false
      isHostLocalSeat: false
      slotId: 1
  currentRoom:
    combat:
      encounterId: NIBBITS_NORMAL
"#,
    )
    .expect("write fixture");

    let response = try_run_error(&[
        "sts2",
        "--json",
        "dev",
        "load-fixture",
        "--path",
        fixture.path().to_str().expect("utf8 path"),
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 2);
    assert_eq!(payload["error"]["code"], "invalid_fixture");
    assert_eq!(
        payload["error"]["details"][0]["field"],
        "run.players[1].isLocal"
    );
}

#[test]
fn dev_load_fixture_rejects_lobby_recipe_without_local_player() {
    let fixture = tempfile::NamedTempFile::new().expect("temp fixture");
    fs::write(
        fixture.path(),
        r#"schemaVersion: spirectl.fixture/v0
name: missing-local-player
characterSelect:
  lobby:
    localPlayerId: p3
    players:
      - id: p1
        characterId: IRONCLAD
      - id: p2
        characterId: SILENT
"#,
    )
    .expect("write fixture");

    let response = try_run_error(&[
        "sts2",
        "--json",
        "dev",
        "load-fixture",
        "--path",
        fixture.path().to_str().expect("utf8 path"),
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 2);
    assert_eq!(payload["error"]["code"], "invalid_fixture");
    assert_eq!(
        payload["error"]["details"][0]["field"],
        "characterSelect.lobby.localPlayerId"
    );
}

#[test]
fn dev_load_fixture_rejects_non_positive_floor() {
    let fixture = tempfile::NamedTempFile::new().expect("temp fixture");
    fs::write(
        fixture.path(),
        r#"schemaVersion: spirectl.fixture/v0
name: floor-zero
run:
  actFloor: 0
  currentRoom:
    combat:
      encounterId: NIBBITS_NORMAL
"#,
    )
    .expect("write fixture");

    let response = try_run_error(&[
        "sts2",
        "--json",
        "dev",
        "load-fixture",
        "--path",
        fixture.path().to_str().expect("utf8 path"),
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 2);
    assert_eq!(payload["error"]["code"], "invalid_fixture");
    assert_eq!(payload["error"]["details"][0]["field"], "run.actFloor");
}

#[test]
fn dev_load_fixture_rejects_missing_schema_version() {
    let fixture = tempfile::NamedTempFile::new().expect("temp fixture");
    fs::write(
        fixture.path(),
        r#"name: missing-schema-version
run:
  currentRoom:
    combat:
      encounterId: NIBBITS_NORMAL
"#,
    )
    .expect("write fixture");

    let response = try_run_error(&[
        "sts2",
        "--json",
        "dev",
        "load-fixture",
        "--path",
        fixture.path().to_str().expect("utf8 path"),
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 2);
    assert_eq!(payload["error"]["code"], "invalid_fixture");
    assert_eq!(payload["error"]["details"][0]["field"], "schemaVersion");
}

#[test]
fn dev_load_fixture_rejects_hp_above_max_hp() {
    let fixture = tempfile::NamedTempFile::new().expect("temp fixture");
    fs::write(
        fixture.path(),
        r#"schemaVersion: spirectl.fixture/v0
name: hp-too-high
run:
  players:
    - id: p1
      characterId: IRONCLAD
      creature:
        currentHp: 81
        maxHp: 80
  currentRoom:
    combat:
      encounterId: NIBBITS_NORMAL
"#,
    )
    .expect("write fixture");

    let response = try_run_error(&[
        "sts2",
        "--json",
        "dev",
        "load-fixture",
        "--path",
        fixture.path().to_str().expect("utf8 path"),
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 2);
    assert_eq!(payload["error"]["code"], "invalid_fixture");
    assert_eq!(
        payload["error"]["details"][0]["field"],
        "run.players[0].creature.currentHp"
    );
    assert_eq!(
        payload["error"]["details"][0]["fieldPath"],
        "run.players[0].creature.currentHp"
    );
    assert_eq!(
        payload["error"]["details"][0]["reasonCode"],
        "hp_exceeds_max_hp"
    );
}

#[test]
fn dev_load_fixture_rejects_enemy_hp_above_max_hp() {
    let fixture = tempfile::NamedTempFile::new().expect("temp fixture");
    fs::write(
        fixture.path(),
        r#"schemaVersion: spirectl.fixture/v0
name: enemy-hp-too-high
run:
  players: 1
  currentRoom:
    combat:
      encounterId: NIBBITS_NORMAL
      enemies:
        - currentHp: 95
          maxHp: 94
"#,
    )
    .expect("write fixture");

    let response = try_run_error(&[
        "sts2",
        "--json",
        "dev",
        "load-fixture",
        "--path",
        fixture.path().to_str().expect("utf8 path"),
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 2);
    assert_eq!(payload["error"]["code"], "invalid_fixture");
    assert_eq!(
        payload["error"]["details"][0]["field"],
        "run.currentRoom.combat.enemies[0].currentHp"
    );
    assert_eq!(
        payload["error"]["details"][0]["reasonCode"],
        "hp_exceeds_max_hp"
    );
}

#[cfg(unix)]
#[tokio::test]
async fn dev_load_fixture_returns_structured_success_over_ipc() {
    let socket_dir = tempfile::tempdir().expect("tempdir");
    let socket_path = socket_dir.path().join("spirectl-fixture.sock");
    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = FixtureLoadBridgeService;

    let server = ipc_bridge_support::spawn_unix_bridge_service(listener, service);

    let fixture_dir = tempfile::tempdir().expect("fixture dir");
    let fixture_path = fixture_dir.path().join("basic-combat.sts2.fixture.yaml");
    fs::write(
        &fixture_path,
        r#"schemaVersion: spirectl.fixture/v0
name: basic-combat
run:
  players:
    - id: p1
      characterId: IRONCLAD
      creature:
        currentHp: 67
        maxHp: 80
  currentRoom:
    combat:
      encounterId: NIBBITS_NORMAL
"#,
    )
    .expect("write fixture");

    let config = write_config(&AppConfig {
        transport: sts2::TransportConfig {
            kind: TransportKind::Ipc,
            ipc_path: Some(socket_path.to_string_lossy().into_owned()),
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    });
    let expected_path = fixture_path.display().to_string();
    let fixture_path_for_run = fixture_path.clone();

    let response = tokio::task::spawn_blocking(move || {
        run(&[
            "sts2",
            "--json",
            "--config",
            config.path().to_str().expect("utf8 path"),
            "dev",
            "load-fixture",
            "--path",
            fixture_path_for_run.to_str().expect("utf8 path"),
        ])
    })
    .await
    .expect("join load-fixture task");
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["requestedPath"], expected_path);
    assert_eq!(payload["resolvedPath"], expected_path);
    assert_eq!(payload["fixture"]["schemaVersion"], "spirectl.fixture/v0");
    assert_eq!(payload["fixture"]["name"], "basic-combat");
    assert_eq!(payload["fixture"]["screen"], "combat");
    assert_eq!(payload["source"], "live");
    assert_eq!(payload["provisional"], false);
    assert_eq!(payload["loaded"]["screen"]["id"], "combat");
    assert_eq!(payload["loaded"]["resolvedPerspective"]["playerId"], "p1");
    assert_eq!(payload["loaded"]["notices"][0]["code"], "fixture.loaded");
    assert_eq!(payload["recipeReport"]["recipeName"], "combat-recipe");
    assert_eq!(
        payload["recipeReport"]["appliedFields"][0]["fieldPath"],
        "run.currentRoom.combat.encounterId"
    );
    assert_eq!(
        payload["recipeReport"]["appliedFields"][0]["reasonCode"],
        "applied_authored_field"
    );
    assert_eq!(
        payload["recipeReport"]["bridgeValidation"]["status"],
        "passed"
    );

    server.abort();
}

#[cfg(unix)]
#[tokio::test]
async fn dev_load_fixture_accepts_multiplayer_combat_with_explicit_host_local_metadata() {
    let socket_dir = tempfile::tempdir().expect("tempdir");
    let socket_path = socket_dir
        .path()
        .join("spirectl-fixture-multiplayer-combat.sock");
    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = FixtureLoadBridgeService;

    let server = ipc_bridge_support::spawn_unix_bridge_service(listener, service);

    let fixture_dir = tempfile::tempdir().expect("fixture dir");
    let fixture_path = fixture_dir
        .path()
        .join("two-player-combat.sts2.fixture.yaml");
    fs::write(
        &fixture_path,
        r#"schemaVersion: spirectl.fixture/v0
name: two-player-combat
run:
  players:
    - id: "p:1"
      characterId: IRONCLAD
      creature:
        currentHp: 67
        maxHp: 80
      isLocal: true
      isHostLocalSeat: false
      slotId: 0
    - id: "p:2"
      characterId: IRONCLAD
      creature:
        currentHp: 67
        maxHp: 80
      isLocal: true
      isHostLocalSeat: true
      slotId: 1
  currentRoom:
    combat:
      encounterId: NIBBITS_NORMAL
"#,
    )
    .expect("write fixture");

    let config = write_config(&AppConfig {
        transport: sts2::TransportConfig {
            kind: TransportKind::Ipc,
            ipc_path: Some(socket_path.to_string_lossy().into_owned()),
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    });

    let response = tokio::task::spawn_blocking(move || {
        run(&[
            "sts2",
            "--json",
            "--config",
            config.path().to_str().expect("utf8 path"),
            "dev",
            "load-fixture",
            "--path",
            fixture_path.to_str().expect("utf8 path"),
        ])
    })
    .await
    .expect("join load-fixture task");
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 0);
    assert_eq!(payload["fixture"]["name"], "two-player-combat");
    assert_eq!(payload["fixture"]["screen"], "combat");

    server.abort();
}

#[cfg(unix)]
#[tokio::test]
async fn dev_load_fixture_preserves_model_ids_and_defaults_blank_seed_before_ipc() {
    let socket_dir = tempfile::tempdir().expect("tempdir");
    let socket_path = socket_dir.path().join("spirectl-fixture-normalized.sock");
    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = NormalizedFixtureLoadBridgeService;

    let server = ipc_bridge_support::spawn_unix_bridge_service(listener, service);

    let fixture_dir = tempfile::tempdir().expect("fixture dir");
    let fixture_path = fixture_dir.path().join("basic-combat.sts2.fixture.yaml");
    fs::write(
        &fixture_path,
        r#"schemaVersion: spirectl.fixture/v0
name: "  basic-combat  "
run:
  ascensionLevel: 3
  seed: "   "
  players:
    - id: "  p1  "
      characterId: IRONCLAD
  currentRoom:
    combat:
      encounterId: NIBBITS_NORMAL
"#,
    )
    .expect("write fixture");

    let config = write_config(&AppConfig {
        transport: sts2::TransportConfig {
            kind: TransportKind::Ipc,
            ipc_path: Some(socket_path.to_string_lossy().into_owned()),
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    });

    let response = tokio::task::spawn_blocking(move || {
        run(&[
            "sts2",
            "--json",
            "--config",
            config.path().to_str().expect("utf8 path"),
            "dev",
            "load-fixture",
            "--path",
            fixture_path.to_str().expect("utf8 path"),
        ])
    })
    .await
    .expect("join load-fixture task");
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 0);
    assert_eq!(payload["fixture"]["schemaVersion"], "spirectl.fixture/v0");
    assert_eq!(payload["fixture"]["name"], "basic-combat");

    server.abort();
}

#[cfg(unix)]
#[tokio::test]
async fn dev_load_fixture_preserves_multiplayer_lobby_model_ids_before_ipc() {
    let socket_dir = tempfile::tempdir().expect("tempdir");
    let socket_path = socket_dir.path().join("spirectl-fixture-lobby.sock");
    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = LobbyFixtureLoadBridgeService;

    let server = ipc_bridge_support::spawn_unix_bridge_service(listener, service);

    let fixture_dir = tempfile::tempdir().expect("fixture dir");
    let fixture_path = fixture_dir.path().join("basic-lobby.sts2.fixture.yaml");
    fs::write(
        &fixture_path,
        r#"schemaVersion: spirectl.fixture/v0
name: "  basic-lobby  "
characterSelect:
  kind: start-run
  lobby:
    seed: "   "
    localPlayerId: "  p1  "
    hostPlayerId: "  p1  "
    players:
      - id: "  p1  "
        characterId: IRONCLAD
        slotId: 3
      - id: "  p2  "
        characterId: SILENT
        isReady: true
  characterButtons:
    - characterId: DEFECT
      isLocked: true
"#,
    )
    .expect("write fixture");

    let config = write_config(&AppConfig {
        transport: sts2::TransportConfig {
            kind: TransportKind::Ipc,
            ipc_path: Some(socket_path.to_string_lossy().into_owned()),
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    });

    let response = tokio::task::spawn_blocking(move || {
        run(&[
            "sts2",
            "--json",
            "--config",
            config.path().to_str().expect("utf8 path"),
            "dev",
            "load-fixture",
            "--path",
            fixture_path.to_str().expect("utf8 path"),
        ])
    })
    .await
    .expect("join load-fixture task");
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 0);
    assert_eq!(payload["fixture"]["name"], "basic-lobby");
    assert_eq!(
        payload["fixture"]["screen"],
        "Screens.CharacterSelect.NCharacterSelectScreen"
    );
    assert_eq!(
        payload["loaded"]["screen"]["id"],
        "Screens.CharacterSelect.NCharacterSelectScreen"
    );

    server.abort();
}

#[cfg(unix)]
#[tokio::test]
async fn dev_load_fixture_accepts_main_menu_recipe_before_ipc() {
    let socket_dir = tempfile::tempdir().expect("tempdir");
    let socket_path = socket_dir.path().join("spirectl-fixture-main-menu.sock");
    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = MainMenuFixtureLoadBridgeService;

    let server = ipc_bridge_support::spawn_unix_bridge_service(listener, service);

    let fixture_dir = tempfile::tempdir().expect("fixture dir");
    let fixture_path = fixture_dir.path().join("basic-main-menu.sts2.fixture.yaml");
    fs::write(
        &fixture_path,
        r#"schemaVersion: spirectl.fixture/v0
name: basic-main-menu
rootScene: main-menu
"#,
    )
    .expect("write fixture");

    let config = write_config(&AppConfig {
        transport: sts2::TransportConfig {
            kind: TransportKind::Ipc,
            ipc_path: Some(socket_path.to_string_lossy().into_owned()),
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    });

    let response = tokio::task::spawn_blocking(move || {
        run(&[
            "sts2",
            "--json",
            "--config",
            config.path().to_str().expect("utf8 path"),
            "dev",
            "load-fixture",
            "--path",
            fixture_path.to_str().expect("utf8 path"),
        ])
    })
    .await
    .expect("join load-fixture task");
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 0);
    assert_eq!(payload["fixture"]["name"], "basic-main-menu");
    assert_eq!(payload["fixture"]["screen"], "main-menu");
    assert_eq!(payload["loaded"]["screen"]["id"], "main-menu");

    server.abort();
}

#[cfg(unix)]
#[tokio::test]
async fn dev_load_fixture_accepts_map_recipe_before_ipc() {
    let socket_dir = tempfile::tempdir().expect("tempdir");
    let socket_path = socket_dir.path().join("spirectl-fixture-map.sock");
    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = MapFixtureLoadBridgeService;

    let server = ipc_bridge_support::spawn_unix_bridge_service(listener, service);

    let fixture_dir = tempfile::tempdir().expect("fixture dir");
    let fixture_path = fixture_dir.path().join("basic-map.sts2.fixture.yaml");
    fs::write(
        &fixture_path,
        r#"schemaVersion: spirectl.fixture/v0
name: basic-map
run:
  actFloor: 3
  currentRoom:
    mapRoom: {}
"#,
    )
    .expect("write fixture");

    let config = write_config(&AppConfig {
        transport: sts2::TransportConfig {
            kind: TransportKind::Ipc,
            ipc_path: Some(socket_path.to_string_lossy().into_owned()),
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    });

    let response = tokio::task::spawn_blocking(move || {
        run(&[
            "sts2",
            "--json",
            "--config",
            config.path().to_str().expect("utf8 path"),
            "dev",
            "load-fixture",
            "--path",
            fixture_path.to_str().expect("utf8 path"),
        ])
    })
    .await
    .expect("join load-fixture task");
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 0);
    assert_eq!(payload["fixture"]["name"], "basic-map");
    assert_eq!(payload["fixture"]["screen"], "Screens.Map.NMapScreen");
    assert_eq!(payload["loaded"]["screen"]["id"], "Screens.Map.NMapScreen");

    server.abort();
}

#[cfg(unix)]
#[tokio::test]
async fn dev_load_fixture_accepts_rewards_recipe_before_ipc() {
    let socket_dir = tempfile::tempdir().expect("tempdir");
    let socket_path = socket_dir.path().join("spirectl-fixture-rewards.sock");
    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = RewardsFixtureLoadBridgeService;

    let server = ipc_bridge_support::spawn_unix_bridge_service(listener, service);

    let fixture_dir = tempfile::tempdir().expect("fixture dir");
    let fixture_path = fixture_dir.path().join("basic-rewards.sts2.fixture.yaml");
    fs::write(
        &fixture_path,
        r#"schemaVersion: spirectl.fixture/v0
name: basic-rewards
run:
  actFloor: 3
  players:
    - id: p1
      characterId: IRONCLAD
      overlays:
        - rewards: {}
"#,
    )
    .expect("write fixture");

    let config = write_config(&AppConfig {
        transport: sts2::TransportConfig {
            kind: TransportKind::Ipc,
            ipc_path: Some(socket_path.to_string_lossy().into_owned()),
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    });

    let response = tokio::task::spawn_blocking(move || {
        run(&[
            "sts2",
            "--json",
            "--config",
            config.path().to_str().expect("utf8 path"),
            "dev",
            "load-fixture",
            "--path",
            fixture_path.to_str().expect("utf8 path"),
        ])
    })
    .await
    .expect("join load-fixture task");
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 0);
    assert_eq!(payload["fixture"]["name"], "basic-rewards");
    assert_eq!(payload["fixture"]["screen"], "Screens.NRewardsScreen");
    assert_eq!(payload["loaded"]["screen"]["id"], "Screens.NRewardsScreen");

    server.abort();
}

#[cfg(unix)]
#[tokio::test]
async fn dev_load_fixture_accepts_rest_site_recipe_before_ipc() {
    let socket_dir = tempfile::tempdir().expect("tempdir");
    let socket_path = socket_dir.path().join("spirectl-fixture-rest-site.sock");
    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = RestSiteFixtureLoadBridgeService;

    let server = ipc_bridge_support::spawn_unix_bridge_service(listener, service);

    let fixture_dir = tempfile::tempdir().expect("fixture dir");
    let fixture_path = fixture_dir.path().join("basic-rest-site.sts2.fixture.yaml");
    fs::write(
        &fixture_path,
        r#"schemaVersion: spirectl.fixture/v0
name: basic-rest-site
run:
  actFloor: 3
  currentRoom:
    restSite: {}
"#,
    )
    .expect("write fixture");

    let config = write_config(&AppConfig {
        transport: sts2::TransportConfig {
            kind: TransportKind::Ipc,
            ipc_path: Some(socket_path.to_string_lossy().into_owned()),
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    });

    let response = tokio::task::spawn_blocking(move || {
        run(&[
            "sts2",
            "--json",
            "--config",
            config.path().to_str().expect("utf8 path"),
            "dev",
            "load-fixture",
            "--path",
            fixture_path.to_str().expect("utf8 path"),
        ])
    })
    .await
    .expect("join load-fixture task");
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 0);
    assert_eq!(payload["fixture"]["name"], "basic-rest-site");
    assert_eq!(payload["fixture"]["screen"], "Rooms.NRestSiteRoom");
    assert_eq!(payload["loaded"]["screen"]["id"], "Rooms.NRestSiteRoom");

    server.abort();
}
