// The start-run lobby's per-player record, under the name this game build gives it.
//
// v107 has a single `LobbyPlayer` shared by every lobby kind. v111 splits it three ways; its start-run half
// (`StartRunLobbyPlayer`) keeps exactly the fields the bridge reads. Aliasing keeps every producer site,
// object initializer, `default(...)` sentinel and type pattern in the bridge lane-independent.
global using GameLobbyPlayer = MegaCrit.Sts2.Core.Entities.Multiplayer.LobbyPlayer;
