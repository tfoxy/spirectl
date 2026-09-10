using System.Text.Json;
using System.Text.RegularExpressions;
using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.Perspective;
using Spirectl.Sts2.Core.Protocol;
using Spirectl.Sts2.Core.State;

namespace Spirectl.Sts2.Core.Debugging;


public static class DebugStateQueryEvaluator
{
    public static string? ValidatePath(string path)
    {
        try
        {
            ParsePath(path);
            return null;
        }
        catch (DebugStateQueryException ex)
        {
            return ex.Message;
        }
    }

    public static string? ValidatePredicate(DebugPredicateSnapshot predicate)
    {
        if (predicate.Operator != DebugPredicateOperatorSnapshot.Regex)
        {
            return null;
        }

        var expected = ParseInputValue(predicate.ExpectedJson);
        if (expected.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        try
        {
            _ = new Regex(expected.GetString() ?? string.Empty);
            return null;
        }
        catch (ArgumentException ex)
        {
            return $"Invalid regex pattern '{expected.GetString()}': {ex.Message}";
        }
    }

    public static DebugStateQueryEvaluationSnapshot Evaluate(
        GameStateSnapshot snapshot,
        string path,
        DebugPredicateSnapshot predicate)
    {
        var segments = ParsePath(path);
        using var document = JsonSerializer.SerializeToDocument(Project(snapshot));
        var values = ResolvePath(document.RootElement, segments);
        var actual = FindMatchedValue(values, predicate);

        return new DebugStateQueryEvaluationSnapshot(
            QueryPath: path,
            Predicate: predicate,
            Matched: actual is not null,
            ActualJson: actual?.GetRawText());
    }

    public static string? ObserveValueJson(
        GameStateSnapshot snapshot,
        string path)
    {
        var segments = ParsePath(path);
        using var document = JsonSerializer.SerializeToDocument(Project(snapshot));
        var values = ResolvePath(document.RootElement, segments);
        return values.Count switch
        {
            0 => null,
            1 => values[0].GetRawText(),
            _ => JsonSerializer.Serialize(values.Select(value => JsonSerializer.Deserialize<JsonElement>(value.GetRawText())).ToArray()),
        };
    }

    private static JsonElement? FindMatchedValue(
        IReadOnlyList<JsonElement> values,
        DebugPredicateSnapshot predicate)
    {
        if (values.Count == 0)
        {
            return predicate.Operator == DebugPredicateOperatorSnapshot.NotExists
                ? default(JsonElement?)
                : null;
        }

        if (predicate.Operator == DebugPredicateOperatorSnapshot.Exists)
        {
            return values[0];
        }

        if (predicate.Operator == DebugPredicateOperatorSnapshot.NotExists)
        {
            return null;
        }

        var expected = ParseInputValue(predicate.ExpectedJson);
        foreach (var value in values)
        {
            if (MatchesPredicate(value, predicate.Operator, expected))
            {
                return value;
            }
        }

        return null;
    }

    private static bool MatchesPredicate(
        JsonElement actual,
        DebugPredicateOperatorSnapshot @operator,
        JsonElement expected)
    {
        return @operator switch
        {
            DebugPredicateOperatorSnapshot.Equals => JsonElementEquals(actual, expected),
            DebugPredicateOperatorSnapshot.Contains => ContainsValue(actual, expected),
            DebugPredicateOperatorSnapshot.Regex => MatchesRegex(actual, expected),
            DebugPredicateOperatorSnapshot.GreaterThan => CompareNumbers(actual, expected, (left, right) => left > right),
            DebugPredicateOperatorSnapshot.GreaterThanOrEqual => CompareNumbers(actual, expected, (left, right) => left >= right),
            DebugPredicateOperatorSnapshot.LessThan => CompareNumbers(actual, expected, (left, right) => left < right),
            DebugPredicateOperatorSnapshot.LessThanOrEqual => CompareNumbers(actual, expected, (left, right) => left <= right),
            DebugPredicateOperatorSnapshot.Exists => true,
            DebugPredicateOperatorSnapshot.NotExists => false,
            _ => false,
        };
    }

    private static bool JsonElementEquals(JsonElement actual, JsonElement expected)
    {
        return actual.ValueKind == expected.ValueKind
            && string.Equals(actual.GetRawText(), expected.GetRawText(), StringComparison.Ordinal);
    }

    private static bool ContainsValue(JsonElement actual, JsonElement expected)
    {
        return actual.ValueKind switch
        {
            JsonValueKind.String when expected.ValueKind == JsonValueKind.String =>
                actual.GetString()?.Contains(expected.GetString() ?? string.Empty, StringComparison.Ordinal) == true,
            JsonValueKind.Array => actual.EnumerateArray().Any(item => JsonElementEquals(item, expected)),
            JsonValueKind.Object when expected.ValueKind == JsonValueKind.String =>
                actual.EnumerateObject().Any(property => string.Equals(property.Name, expected.GetString(), StringComparison.Ordinal)),
            _ => false,
        };
    }

    private static bool MatchesRegex(JsonElement actual, JsonElement expected)
    {
        if (actual.ValueKind != JsonValueKind.String || expected.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        try
        {
            return new Regex(expected.GetString() ?? string.Empty).IsMatch(actual.GetString() ?? string.Empty);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool CompareNumbers(
        JsonElement actual,
        JsonElement expected,
        Func<double, double, bool> compare)
    {
        if (!actual.TryGetDouble(out var left) || !expected.TryGetDouble(out var right))
        {
            return false;
        }

        return compare(left, right);
    }

    private static JsonElement ParseInputValue(string? raw)
    {
        raw ??= string.Empty;
        try
        {
            using var document = JsonDocument.Parse(raw);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(raw));
            return document.RootElement.Clone();
        }
    }

    private static IReadOnlyList<PathSegment> ParsePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new DebugStateQueryException("State query path cannot be empty.");
        }

        var segments = new List<PathSegment>();
        foreach (var part in SplitPathParts(path))
        {
            var remainder = part;
            if (!remainder.StartsWith("[", StringComparison.Ordinal))
            {
                var fieldEnd = remainder.IndexOf('[');
                if (fieldEnd < 0)
                {
                    fieldEnd = remainder.Length;
                }

                var field = remainder[..fieldEnd];
                if (field.Length == 0)
                {
                    throw new DebugStateQueryException(
                        $"State query path '{path}' has an empty field segment.");
                }

                segments.Add(new FieldPathSegment(field));
                remainder = remainder[fieldEnd..];
            }

            while (remainder.Length > 0)
            {
                if (!remainder.StartsWith("[", StringComparison.Ordinal))
                {
                    throw new DebugStateQueryException(
                        $"State query path '{path}' has invalid index syntax near '{remainder}'.");
                }

                var end = remainder.IndexOf(']');
                if (end < 0)
                {
                    throw new DebugStateQueryException(
                        $"State query path '{path}' is missing a closing ']' in '{remainder}'.");
                }

                var index = remainder[1..end];
                if (index == "*")
                {
                    segments.Add(new WildcardIndexPathSegment());
                }
                else if (TryParseFilter(index, out var filterOperator, out var field, out var expected))
                {
                    if (string.IsNullOrWhiteSpace(expected))
                    {
                        throw new DebugStateQueryException(
                            $"State query path '{path}' uses an invalid filter expression '[{index}]'.");
                    }

                    segments.Add(new FilterPathSegment(
                        index,
                        field,
                        ParseFilterFieldSegments(path, index, field),
                        filterOperator,
                        ParseInputValue(expected)));
                }
                else if (int.TryParse(index, out var numericIndex) && numericIndex >= 0)
                {
                    segments.Add(new IndexPathSegment(numericIndex));
                }
                else
                {
                    throw new DebugStateQueryException(
                        $"State query path '{path}' uses a non-numeric index '{index}'.");
                }

                remainder = remainder[(end + 1)..];
            }
        }

        return segments;
    }

    private static IReadOnlyList<string> SplitPathParts(string path)
    {
        var parts = new List<string>();
        var start = 0;
        var bracketDepth = 0;

        for (var index = 0; index < path.Length; index++)
        {
            switch (path[index])
            {
                case '[':
                    bracketDepth++;
                    break;
                case ']':
                    bracketDepth = Math.Max(0, bracketDepth - 1);
                    break;
                case '.' when bracketDepth == 0:
                    var part = path[start..index];
                    if (part.Length == 0)
                    {
                        throw new DebugStateQueryException(
                            $"State query path '{path}' contains an empty segment.");
                    }

                    parts.Add(part);
                    start = index + 1;
                    break;
            }
        }

        var tail = path[start..];
        if (tail.Length == 0)
        {
            throw new DebugStateQueryException(
                $"State query path '{path}' contains an empty segment.");
        }

        parts.Add(tail);
        return parts;
    }

    private static IReadOnlyList<PathSegment> ParseFilterFieldSegments(
        string path,
        string filterExpression,
        string field)
    {
        if (field.Length == 0)
        {
            return [];
        }

        try
        {
            var segments = ParsePath(field);
            if (segments.Any(segment => segment is WildcardIndexPathSegment or FilterPathSegment))
            {
                throw new DebugStateQueryException(
                    $"State query path '{path}' uses an invalid filter expression '[{filterExpression}]'.");
            }

            return segments;
        }
        catch (DebugStateQueryException)
        {
            throw new DebugStateQueryException(
                $"State query path '{path}' uses an invalid filter expression '[{filterExpression}]'.");
        }
    }

    private static bool TryParseFilter(
        string expression,
        out FilterOperator @operator,
        out string field,
        out string expected)
    {
        foreach (var entry in FilterOperatorTokens)
        {
            var index = expression.IndexOf(entry.Token, StringComparison.Ordinal);
            if (index < 0)
            {
                continue;
            }

            @operator = entry.Operator;
            field = expression[..index];
            expected = expression[(index + entry.Token.Length)..];
            return true;
        }

        @operator = default;
        field = string.Empty;
        expected = string.Empty;
        return false;
    }

    private static IReadOnlyList<JsonElement> ResolvePath(JsonElement root, IReadOnlyList<PathSegment> segments)
    {
        var current = new List<JsonElement> { root };

        foreach (var segment in segments)
        {
            var next = new List<JsonElement>();

            foreach (var value in current)
            {
                switch (segment)
                {
                    case FieldPathSegment field:
                        if (value.ValueKind == JsonValueKind.Object
                            && value.TryGetProperty(field.Name, out var property))
                        {
                            next.Add(property.Clone());
                        }
                        break;
                    case IndexPathSegment index:
                        if (value.ValueKind == JsonValueKind.Array)
                        {
                            var items = value.EnumerateArray().ToArray();
                            if (index.Index < items.Length)
                            {
                                next.Add(items[index.Index].Clone());
                            }
                        }
                        break;
                    case WildcardIndexPathSegment:
                        if (value.ValueKind == JsonValueKind.Array)
                        {
                            next.AddRange(value.EnumerateArray().Select(item => item.Clone()));
                        }
                        break;
                    case FilterPathSegment filter:
                        if (value.ValueKind != JsonValueKind.Array)
                        {
                            break;
                        }

                        foreach (var item in value.EnumerateArray())
                        {
                            var actuals = filter.FieldSegments.Count == 0
                                ? [item.Clone()]
                                : ResolvePath(item, filter.FieldSegments);
                            if (actuals.Any(actual => MatchesFilter(actual, filter.Operator, filter.Expected)))
                            {
                                next.Add(item.Clone());
                            }
                        }
                        break;
                }
            }

            if (next.Count == 0)
            {
                return [];
            }

            current = next;
        }

        return current;
    }

    private static bool MatchesFilter(JsonElement actual, FilterOperator @operator, JsonElement expected)
    {
        return @operator switch
        {
            FilterOperator.Equals => JsonElementEquals(actual, expected),
            FilterOperator.NotEquals => !JsonElementEquals(actual, expected),
            FilterOperator.Contains => ContainsValue(actual, expected),
            FilterOperator.Regex => MatchesRegex(actual, expected),
            FilterOperator.GreaterThan => CompareNumbers(actual, expected, (left, right) => left > right),
            FilterOperator.GreaterThanOrEqual => CompareNumbers(actual, expected, (left, right) => left >= right),
            FilterOperator.LessThan => CompareNumbers(actual, expected, (left, right) => left < right),
            FilterOperator.LessThanOrEqual => CompareNumbers(actual, expected, (left, right) => left <= right),
            _ => false,
        };
    }

    private static object Project(GameStateSnapshot snapshot)
    {
        return new Dictionary<string, object?>
        {
            ["schemaVersion"] = snapshot.SchemaVersion,
            ["gameVersion"] = snapshot.GameVersion,
            ["bridgeVersion"] = snapshot.BridgeVersion,
            ["source"] = snapshot.Source == DataSourceKind.Live ? "live" : "stub",
            ["provisional"] = snapshot.Provisional,
            ["screen"] = new Dictionary<string, object?>
            {
                ["id"] = snapshot.ScreenType,
                ["title"] = snapshot.ScreenTitle,
                ["instanceId"] = snapshot.ScreenInstanceId,
            },
            ["resolvedPerspective"] = new Dictionary<string, object?>
            {
                ["scope"] = snapshot.ResolvedPerspective.Scope == PlayerScope.Omniscient ? "omniscient" : "local",
                ["playerId"] = snapshot.ResolvedPerspective.PlayerId,
                ["usesDefault"] = snapshot.ResolvedPerspective.UsesDefault,
            },
            ["menu"] = snapshot.Menu is null
                ? null
                : new Dictionary<string, object?>
                {
                    ["menuId"] = snapshot.Menu.MenuId,
                    ["title"] = snapshot.Menu.Title,
                },
            ["lobby"] = snapshot.Lobby is null ? null : ProjectLobby(snapshot.Lobby),
            ["run"] = snapshot.Run is null ? null : ProjectRun(snapshot.Run),
            ["combat"] = snapshot.Combat is null ? null : ProjectCombat(snapshot.Combat),
            ["choices"] = snapshot.Choices.Select(ProjectChoice).ToArray(),
            ["availableActions"] = snapshot.AvailableActions.Select(ProjectAvailableAction).ToArray(),
            ["notices"] = snapshot.Notices.Select(ProjectNotice).ToArray(),
            ["debug"] = snapshot.Debug is null
                ? null
                : new Dictionary<string, object?>
                {
                    ["notes"] = snapshot.Debug.Notes.ToArray(),
                },
        };
    }

    private static object ProjectChoice(ChoiceSnapshot choice)
    {
        return new Dictionary<string, object?>
        {
            ["id"] = choice.Id,
            ["label"] = choice.Label,
            ["kind"] = choice.Kind,
            ["provisional"] = choice.Provisional,
            ["ownerPlayerId"] = choice.OwnerPlayerId,
        };
    }

    private static object ProjectAvailableAction(AvailableActionSnapshot action)
    {
        return new Dictionary<string, object?>
        {
            ["id"] = action.Id,
            ["kind"] = ActionKindName(action.Kind),
            ["summary"] = action.Summary,
            ["cliCommandHint"] = action.CliCommandHint,
            ["provisional"] = action.Provisional,
            ["arguments"] = ProjectActionArguments(action.Arguments),
        };
    }

    private static object ProjectActionArguments(ActionArgumentsSnapshot? arguments)
    {
        return new Dictionary<string, object?>
        {
            ["playerId"] = arguments?.PlayerId,
            ["cardId"] = arguments?.CardId,
            ["targetId"] = arguments?.TargetId,
            ["choiceId"] = arguments?.ChoiceId,
            ["characterId"] = arguments?.CharacterId,
            ["mapNodeId"] = arguments?.MapNodeId,
            ["potionId"] = arguments?.PotionId,
        };
    }

    private static object ProjectNotice(StateNoticeSnapshot notice)
    {
        return new Dictionary<string, object?>
        {
            ["code"] = notice.Code,
            ["message"] = notice.Message,
            ["provisional"] = notice.Provisional,
        };
    }

    private static object ProjectLobby(LobbyStateSnapshot lobby)
    {
        return new Dictionary<string, object?>
        {
            ["lobbyId"] = lobby.LobbyId,
            ["phase"] = lobby.Phase,
            ["localPlayerId"] = lobby.LocalPlayerId,
            ["hostPlayerId"] = lobby.HostPlayerId,
            ["localPlayerRole"] = lobby.LocalPlayerRole,
            ["waitingText"] = lobby.WaitingText,
            ["players"] = lobby.Players.Select(ProjectLobbyPlayer).ToArray(),
            ["playersById"] = lobby.PlayersById.ToDictionary(entry => entry.Key, entry => ProjectLobbyPlayer(entry.Value)),
            ["availableCharacters"] = lobby.AvailableCharacters.Select(ProjectLobbyCharacter).ToArray(),
            ["availableCharactersById"] = lobby.AvailableCharactersById.ToDictionary(entry => entry.Key, entry => ProjectLobbyCharacter(entry.Value)),
        };
    }

    private static object ProjectLobbyPlayer(LobbyPlayerSnapshot player)
    {
        return new Dictionary<string, object?>
        {
            ["id"] = player.Id,
            ["status"] = player.Status,
            ["name"] = player.Name,
            ["selectedCharacterId"] = player.SelectedCharacterId,
            ["isReady"] = player.IsReady,
            ["slotId"] = player.SlotId,
            ["isLocal"] = player.IsLocal,
            ["isHost"] = player.IsHost,
            ["isRemote"] = player.IsRemote,
        };
    }

    private static object ProjectLobbyCharacter(LobbyCharacterSnapshot character)
    {
        return new Dictionary<string, object?>
        {
            ["id"] = character.Id,
            ["name"] = character.Name,
            ["isUnlocked"] = character.IsUnlocked,
            ["nameKey"] = character.NameKey,
            ["description"] = character.Description,
            ["startingHp"] = character.StartingHp,
            ["startingGold"] = character.StartingGold,
            ["passiveId"] = character.PassiveId,
            ["passiveName"] = character.PassiveName,
            ["passiveDescription"] = character.PassiveDescription,
            ["passiveIconAssetKey"] = character.PassiveIconAssetKey,
            ["portraitAssetKey"] = character.PortraitAssetKey,
            ["iconAssetKey"] = character.IconAssetKey,
            ["selectBackgroundAssetKey"] = character.SelectBackgroundAssetKey,
            ["nameColor"] = character.NameColor,
        };
    }

    private static object ProjectRun(RunStateSnapshot run)
    {
        return new Dictionary<string, object?>
        {
            ["seed"] = run.Seed,
            ["floor"] = run.Floor,
            ["act"] = run.Act,
            ["players"] = run.Players.Select(ProjectPlayer).ToArray(),
            ["playersById"] = run.PlayersById.ToDictionary(entry => entry.Key, entry => ProjectPlayer(entry.Value)),
        };
    }

    private static object ProjectPlayer(PlayerStateSnapshot player)
    {
        return new Dictionary<string, object?>
        {
            ["id"] = player.Id,
            ["character"] = player.Character,
            ["hp"] = player.Hp,
            ["maxHp"] = player.MaxHp,
            ["isLocal"] = player.IsLocal,
            ["isHost"] = player.IsHost,
            ["isRemote"] = player.IsRemote,
        };
    }

    private static object ProjectCombat(CombatStateSnapshot combat)
    {
        return new Dictionary<string, object?>
        {
            ["turn"] = combat.Turn,
            ["activePlayerId"] = combat.ActivePlayerId,
            ["isPlayerTurn"] = combat.IsPlayerTurn,
            ["hand"] = combat.Hand.Select(ProjectCard).ToArray(),
            ["potions"] = (combat.Potions ?? []).Select(ProjectPotion).ToArray(),
            ["players"] = combat.Players.Select(ProjectCombatPlayer).ToArray(),
            ["playersById"] = combat.PlayersById.ToDictionary(entry => entry.Key, entry => ProjectCombatPlayer(entry.Value)),
            ["enemies"] = combat.Enemies.Select(ProjectEnemy).ToArray(),
        };
    }

    private static object ProjectCombatPlayer(CombatPlayerStateSnapshot player)
    {
        return new Dictionary<string, object?>
        {
            ["id"] = player.Id,
            ["character"] = player.Character,
            ["hp"] = player.Hp,
            ["maxHp"] = player.MaxHp,
            ["block"] = player.Block,
            ["energy"] = player.Energy,
            ["maxEnergy"] = player.MaxEnergy,
            ["isLocal"] = player.IsLocal,
            ["isHost"] = player.IsHost,
            ["isRemote"] = player.IsRemote,
            ["hand"] = player.Hand.Select(ProjectCard).ToArray(),
            ["potions"] = (player.Potions ?? []).Select(ProjectPotion).ToArray(),
        };
    }

    private static object ProjectCard(CardStateSnapshot card)
    {
        var result = new Dictionary<string, object?>
        {
            ["id"] = card.Id,
            ["name"] = card.Name,
            ["cost"] = card.Cost,
            ["ownerPlayerId"] = card.OwnerPlayerId,
            ["playable"] = card.Playable,
            ["unplayableReason"] = card.UnplayableReason,
            ["targetIds"] = card.TargetIds.ToArray(),
            ["upgraded"] = card.Upgraded,
        };
        if (!string.IsNullOrWhiteSpace(card.AfflictionModelId))
        {
            result["afflictionModelId"] = card.AfflictionModelId;
            result["afflictionAmount"] = card.AfflictionAmount;
        }

        return result;
    }

    private static object ProjectPotion(PotionStateSnapshot potion)
    {
        return new Dictionary<string, object?>
        {
            ["id"] = potion.Id,
            ["name"] = potion.Name,
            ["ownerPlayerId"] = potion.OwnerPlayerId,
            ["slotIndex"] = potion.SlotIndex,
            ["usable"] = potion.Usable,
            ["unusableReason"] = potion.UnusableReason,
            ["targetIds"] = potion.TargetIds.ToArray(),
        };
    }

    private static object ProjectEnemy(EnemyStateSnapshot enemy)
    {
        return new Dictionary<string, object?>
        {
            ["id"] = enemy.Id,
            ["name"] = enemy.Name,
            ["hp"] = enemy.Hp,
            ["intent"] = enemy.Intent,
            ["maxHp"] = enemy.MaxHp,
            ["block"] = enemy.Block,
            ["isAlive"] = enemy.IsAlive,
            ["intents"] = enemy.Intents.Select(intent => new Dictionary<string, object?>
            {
                ["type"] = intent.Type,
                ["damage"] = intent.Damage,
                ["hits"] = intent.Hits,
                ["totalDamage"] = intent.TotalDamage,
            }).ToArray(),
        };
    }

    private static string ActionKindName(SemanticActionKind kind)
    {
        return kind switch
        {
            SemanticActionKind.PlayCard => "play-card",
            SemanticActionKind.Choose => "choose",
            SemanticActionKind.ConfirmSelection => "confirm-selection",
            SemanticActionKind.CancelSelection => "cancel-selection",
            SemanticActionKind.EndTurn => "end-turn",
            SemanticActionKind.CancelEndTurn => "cancel-end-turn",
            SemanticActionKind.UsePotion => "use-potion",
            SemanticActionKind.Ready => "ready",
            SemanticActionKind.Unready => "unready",
            SemanticActionKind.SelectCharacter => "select-character",
            SemanticActionKind.SelectMapNode => "select-map-node",
            SemanticActionKind.ToggleMap => "toggle-map",
            SemanticActionKind.ToggleDeck => "toggle-deck",
            SemanticActionKind.ToggleSettings => "toggle-settings",
            SemanticActionKind.SortDeckView => "sort-deck-view",
            SemanticActionKind.ToggleDeckViewUpgrades => "toggle-deck-view-upgrades",
            SemanticActionKind.MouseClick => "mouse-click",
            _ => kind.ToString(),
        };
    }

    private static readonly (string Token, FilterOperator Operator)[] FilterOperatorTokens =
    [
        ("~=", FilterOperator.Regex),
        ("*=", FilterOperator.Contains),
        (">=", FilterOperator.GreaterThanOrEqual),
        ("<=", FilterOperator.LessThanOrEqual),
        ("!=", FilterOperator.NotEquals),
        ("=", FilterOperator.Equals),
        (">", FilterOperator.GreaterThan),
        ("<", FilterOperator.LessThan),
    ];

    private enum FilterOperator
    {
        Equals,
        NotEquals,
        Contains,
        Regex,
        GreaterThan,
        GreaterThanOrEqual,
        LessThan,
        LessThanOrEqual,
    }

    private abstract record PathSegment;

    private sealed record FieldPathSegment(string Name) : PathSegment;

    private sealed record IndexPathSegment(int Index) : PathSegment;

    private sealed record WildcardIndexPathSegment : PathSegment;

    private sealed record FilterPathSegment(
        string Expression,
        string Field,
        IReadOnlyList<PathSegment> FieldSegments,
        FilterOperator Operator,
        JsonElement Expected) : PathSegment;

    private sealed class DebugStateQueryException : Exception
    {
        public DebugStateQueryException(string message)
            : base(message)
        {
        }
    }
}
