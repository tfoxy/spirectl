# Developer console

Use this reference for explicit developer setup through the game's console. Resolve `STS2_BIN` using
SKILL.md first. Prefer semantic `act` for ordinary player actions.

## Jump to a scene

Start or load a run before using `fight`, `room`, `event`, `ancient`, `act`, or `travel`. Discover ids
from the attached game, then jump and wait for the destination:

```bash
"${STS2_BIN[@]}" --json dev console args fight --prefix KNIGHTS
"${STS2_BIN[@]}" --json dev console fight <ENCOUNTER_ID>
"${STS2_BIN[@]}" dev wait-for run.currentRoom.scene --equals rooms/combat_room
"${STS2_BIN[@]}" --json dev console args room
"${STS2_BIN[@]}" --json dev console room <RoomType>
"${STS2_BIN[@]}" --json dev console args event
"${STS2_BIN[@]}" --json dev console event <EVENT_ID>
"${STS2_BIN[@]}" --json dev console act <ACT_NUMBER_OR_ID>
"${STS2_BIN[@]}" --json dev console travel
```

`travel` enables map travel; `win` ends the current combat and requires combat; `win run` wins the
entire run. `die` ends the player's run and requires a run. Read state after a jump; console acceptance does not mean a room
transition has finished.

## Discover commands and ids

```bash
"${STS2_BIN[@]}" --json dev console commands
"${STS2_BIN[@]}" --json dev console args fight
"${STS2_BIN[@]}" --json dev console args fight --prefix KNIGHTS
"${STS2_BIN[@]}" --json dev console args card <CARD_ID> --position 1
"${STS2_BIN[@]}" --json dev console help draw
```

`commands` and `args` are reserved discovery subcommands. Other names retain console passthrough,
including `help draw` and `help --verbose`. `help` is special and does not appear in the registered
command catalog.

`args <command> [PRECEDING...] --prefix P` completes a partial token after PRECEDING. `--position N`
uses a zero-based argument position, defaults to PRECEDING's length, truncates extra preceding tokens,
and pads missing ones with empty strings. Each token must contain no whitespace. Use these commands
for id lists instead of maintaining ids in prompts or docs.

`source:"live"` and `complete:true` use the attached game's registered commands and completion
candidates, including mods. Mods can add commands, decorate candidates with display names (exposed
as `candidateDetails[].label`), and change matching; pass `candidateDetails[].value` to the console.
Stub data uses `source:"stub"`, `provisional:true`, and `complete:false`.
Only an unreachable bridge triggers the local decompiled-reference fallback. It uses
`source:"static-reference"`, `complete:false`, and a `static-fallback-incomplete` notice: mods and
content filters can change the result. Unsupported static positions return an empty list with
`static-fallback-unsupported`. Errors returned by a reachable bridge remain errors. Create a missing
reference with `sts2 project recover --kind decompile`.

## Command reference

This base-game table includes registered debug commands. Query `dev console commands` for the current
runtime catalog. `requires` describes the necessary runtime state; mod commands use `unknown`.
Commands marked dangerous require `--mode dangerous`; all others use normal mode.

| name | usage | description | requires | dangerous |
| --- | --- | --- | --- | --- |
| achievement | `<operation:string> [id:string]` | Unlocks or revokes an achievement. If no achievement is provided, all achievements are unlocked or revoked. | none | yes |
| act | `<int\|string: act>` | Jumps to an act. If integer, will jump to that act. Otherwise, replaces the current act with the act passed. | run | no |
| afflict | `<id:string> [amount:int] [hand-index:int]` | Apply the specified affliction to a card in the player's hand | combat | no |
| ancient | `<id:string> <choice:string>` | Opens an ancient event with the selected choice | run | no |
| art | `<type:string>` | Lists all the content of the specified type that is missing art. Types: affliction, card, enchantment, power, relic. | none | no |
| bestiary |  | Opens the bestiary (WIP) | none | no |
| block | `<int:amount> <target-index:int>` | Gives block to player, or to target creature if index is given (0 is player). | combat | no |
| card | `<card-id:string> [pileName:string]` | Spawns a card into a pile (hand by default). Screaming snake case ('BODY_SLAM', not 'Body Slam'). | run | no |
| cloud | `delete` | Deletes all save files from Steam Cloud, if you are running on Steam | none | yes |
| damage | `<amount:int> <target-index:int>` | Damage all enemies, or target creature if index is given (0 is player). | combat | no |
| die |  | You die | run | no |
| draw | `<count:int>` | Draw X many cards. | combat | no |
| dump |  | Dumps Model ID database to console & logs. | none | no |
| enchant | `<id:string> [amount:int] [hand-index:int]` | Enchants a card in the player's hand with the specified enchantment. | combat | no |
| energy | `<amount:int>` | Adds energy to player | combat | no |
| event | `<id:string>` | Jumps a player to a specific event. | run | no |
| fight | `<id:string> [rng-seed:ulong]` | Jumps a player to a specific encounter. | run | no |
| getlogs | `[test-feedback] <name:string>` | Gathers logs, automatically zips them to a file containing 'name', and opens the directory containing the zip file. | none | no |
| godmode |  | Become invincible! | run | no |
| gold | `<amount:int>` | Manipulate player gold. Cha-ching! | run | no |
| heal | `<amount:int> [index:int]` | Heal the player some amount of HP. | run | no |
| instant |  | Turns instant mode on. | none | no |
| kill | `<target-index:int>\|'all'` | Will kill one target if the index is given, or all if 'all', or the first if no arguments. | combat | no |
| leaderboard | `[option:string] [name:string] <score:int> [count:int]` | Adds scores to the leaderboard. Option can be upload\|random. If random, <count> random scores will be uploaded to leaderboard <name>. If upload, one score will be uploaded to leaderboard <name> with <score>. | none | no |
| log | `[type:string] <level:string>` | Set log level for specific log types. Type can be: Generic,Network,Actions,GameSync,VisualSync. Levels can be: VeryDebug,Load,Debug,Info,Warn,Error | none | no |
| multiplayer |  | Opens the multiplayer menu, or the test scene if test is the first argument | none | no |
| open | `logs\|saves\|root\|build-logs\|loc-override` | Opens a common path in the local OS file browser. | none | no |
| potion | `<id:string>` | Adds potion to belt. Screaming snake case ('ENTROPIC_BREW', not 'Entropic Brew'). | run | no |
| power | `<id:string> <amount:int> <target-index:int>` | Grant power to given target at index. | combat | no |
| relic | `[add\|remove] <relic-id:string>` | Adds/Removes relic from player (add by default) | run | no |
| remove_card | `<id:string> [pileName:string]` | Removes a card from your Hand or Deck. Screaming snake case ('BODY_SLAM', not 'Body Slam'). | run | no |
| room | `<id:string>` | Jumps a player to a specific room. | run | no |
| screenshot | `[label:string]` | Writes the current viewport to a PNG under user://screenshots/. | none | no |
| sentry | `<test\|message\|exception\|crash\|status> [text]` | Test Sentry error reporting. 'test' sends a test message and exception, 'message <text>' sends a custom message, 'exception' throws a test exception, 'crash confirm' triggers a native crash (terminates game!), 'status' shows Sentry status. | none | no |
| stars | `<amount:int>` | Adds stars to player | combat | no |
| trailer |  | Toggles the ability to show and hide UI elements via 0 - 9 and +- keys. | none | no |
| travel |  | Enables you to jump to any room on the map. | run | no |
| unlock | `<type:string>` | Marks all cards/potions/relics/monsters/events/acts/epochs/ascensions as discovered, or 'all' to unlock everything. | none | yes |
| upgrade | `<hand-index:int>` | Upgrade the target card based on its hand position (0 is the left most). | combat | no |
| win | `[run]` | You win the combat or the entire run (`win run`). | combat | no |
