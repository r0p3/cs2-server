using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Utils;

namespace RepServer;

public sealed partial class RepServerPlugin
{
    private static readonly JsonSerializerOptions TrackerJson = new(JsonSerializerDefaults.Web);
    private static readonly string TrackerDirectory =
        Environment.GetEnvironmentVariable("REPSERVER_TRACKER_STATE") ?? "/var/lib/repserver/tracker";
    private const string TenWin = "2v10-win";
    private const string TenSweep = "2v10-16-0";
    private const string MaxBots = "2vmax";

    private sealed record TrackerStats
    {
        public required string Name { get; set; }
        public string? Role { get; set; }
        public int Kills { get; set; }
        public int Deaths { get; set; }
        public int Assists { get; set; }
        public int Aces { get; set; }
        public int Headshots { get; set; }
        public int MolotovKills { get; set; }
        public int GrenadeKills { get; set; }
        public int PistolKills { get; set; }
        public int BotKills { get; set; }
        public int Clutches { get; set; }
        public int Defuses { get; set; }
        public int Plants { get; set; }
        public int Rescues { get; set; }
        public int SpoiledSweeps { get; set; }
        public double HeadshotPercentage => Kills == 0 ? 0 : Math.Round(100.0 * Headshots / Kills, 1);
    }

    private sealed record TrackerRecordKey(string Map, string Category, string Side, string First, string Second);
    private sealed record TrackerBest(int Bots, int RoundsWon, int RoundsLost, DateTimeOffset FinishedAt,
        Dictionary<string, TrackerStats> Players);
    private sealed class TrackerRecord
    {
        public required TrackerRecordKey Key { get; init; }
        public string[] Names { get; set; } = [];
        public int Attempts { get; set; }
        public TrackerBest? Best { get; set; }
        public TrackerBest? Pending { get; set; }
    }

    private sealed class TrackerHistory
    {
        public Dictionary<string, TrackerStats> Players { get; init; } = new();
        [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
        public Dictionary<string, TrackerStats> Bots { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<TrackerRecord> Records { get; init; } = [];
    }

    private sealed record RoundClient(string Id, string Name, CsTeam Team, bool Bot)
    {
        public (string Id, bool Bot) Identity => (Id, Bot);
    }

    private sealed class RoundEliminations(RoundClient player)
    {
        public RoundClient Player { get; } = player;
        public HashSet<(string Id, bool Bot)> Victims { get; } = new();
    }

    private sealed class TrackerRound
    {
        public Dictionary<int, RoundClient> Alive { get; } = new();
        public Dictionary<CsTeam, HashSet<(string Id, bool Bot)>> Participants { get; } = new()
        {
            [CsTeam.Terrorist] = [],
            [CsTeam.CounterTerrorist] = []
        };
        public Dictionary<(string Id, CsTeam Side), RoundEliminations> Eliminations { get; } = new();
        public Dictionary<string, CsTeam> ClutchCandidates { get; } = new();
        public RoundClient? Planter { get; set; }
        public RoundClient? LastRescuer { get; set; }
        public RoundClient? Defuser { get; set; }
        public RoundClient? LastEliminator { get; set; }
    }

    private sealed class TrackerMatch
    {
        public TrackerRecord? Record { get; init; }
        public int BotCount { get; init; }
        public CsTeam Side { get; init; }
        public Dictionary<int, RoundClient> Roster { get; init; } = new();
        public Dictionary<string, TrackerStats> Players { get; } = new();
        public Dictionary<string, TrackerStats> Bots { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int TRounds { get; set; }
        public int CtRounds { get; set; }
        public int? LastEngineRound { get; set; }
        public bool Eligible { get; set; } = true;
        public bool Finished { get; set; }
    }

    private TrackerHistory trackerHistory = new();
    private TrackerMatch? trackerMatch;
    private TrackerRound? trackerRound;
    private TrackerRecordKey? announcedRecord;
    private bool trackerOnline;
    private bool trackerDirty;
    private bool trackerSaveEnabled;
    private bool trackerRoundOpen;
    private double nextTrackerTick;

    private void LoadTracker(bool hotReload)
    {
        try
        {
            Directory.CreateDirectory(TrackerDirectory);
            foreach (var name in new[] { "sessions", "loadouts", "requests", "responses" })
                Directory.CreateDirectory(Path.Combine(TrackerDirectory, name));
            trackerHistory = ReadTrackerFile<TrackerHistory>("history.json") ?? new();
            trackerSaveEnabled = true;
            foreach (var record in trackerHistory.Records)
                SettleTrackerRecord(record, announce: false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            Console.Error.WriteLine($"[RepServer] Could not load tracker history: {error.Message}");
        }

        LoadTrackerBuyItems();
        foreach (var bot in botProfiles)
            GetTrackerStats(bot.Name, bot.Name, trackerHistory.Bots).Role = bot.Role;
        trackerDirty = true;
        if (hotReload)
            StartTrackerMap();
        RegisterListener<Listeners.OnTick>(TickTracker);
        RegisterEventHandler<EventPlayerDeath>(TrackLifetimeDeath);
        RegisterEventHandler<EventPlayerSpawn>(OnTrackerSpawn);
        RegisterEventHandler<EventBombPlanted>((e, _) => TrackTrackerObjective(e.Userid, "plant"));
        RegisterEventHandler<EventBombDefused>((e, _) => TrackTrackerObjective(e.Userid, "defuse"));
        RegisterEventHandler<EventHostageRescued>((e, _) => TrackTrackerObjective(e.Userid, "rescue"));
    }

    private static T? ReadTrackerFile<T>(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(TrackerDirectory, path)), TrackerJson);
        }
        catch (FileNotFoundException)
        {
            return default;
        }
    }

    private void FlushTracker()
    {
        if (trackerDirty && trackerSaveEnabled)
            trackerDirty = !TrySaveState(Path.Combine(TrackerDirectory, "history.json"),
                JsonSerializer.Serialize(trackerHistory, TrackerJson));
    }

    private void ResetTrackerMatch()
    {
        if (trackerMatch is { Eligible: true, Record: { } record })
            SettleTrackerRecord(record, announce: trackerOnline);
        trackerMatch = null;
        trackerRound = null;
        trackerRoundOpen = false;
        autobought.Clear();
        FlushTracker();
    }

    private void StartTrackerMap()
    {
        ResetTrackerMatch();
        announcedRecord = null;
        trackerOnline = true;
        nextTrackerTick = Server.EngineTime + 1;
    }

    private void StopTracker()
    {
        trackerOnline = false;
        ResetTrackerMatch();
    }

    private string TrackerMapKey()
    {
        var map = Server.MapName ?? "";
        var parts = map.Split('/');
        if (parts.Length >= 2 && parts[0] == "workshop")
            return $"workshop/{parts[1]}";
        var workshop = workshopMaps.Values.FirstOrDefault(item => item.Alias == map);
        return workshop is null ? map : $"workshop/{workshop.Id}";
    }

    private void TickTracker()
    {
        if (!trackerOnline || Server.EngineTime < nextTrackerTick)
            return;
        nextTrackerTick = Server.EngineTime + 1;
        FlushTracker();
        PublishTracker();
        ProcessTrackerRequests();
    }

    private void PublishTracker()
    {
        if (!Directory.Exists(TrackerDirectory))
            return;

        var rules = Utilities.GetGameRules();
        var live = rules is { WarmupPeriod: false };
        if (rules is { WarmupPeriod: true } && trackerMatch is not null)
            ResetTrackerMatch();
        var teams = live ? Utilities.FindAllEntitiesByDesignerName<CCSTeam>("cs_team_manager")
            .Where(team => team.TeamNum is (byte)CsTeam.Terrorist or (byte)CsTeam.CounterTerrorist)
            .ToDictionary(team => TeamLabel((CsTeam)team.TeamNum), team => team.Score) : null;
        var roster = TrackerRoster();
        var match = live ? trackerMatch : null;
        if (live)
            ValidateTrackerRoster(roster);
        var board = roster.Values.Select(client =>
        {
            var stats = (client.Bot ? match?.Bots : match?.Players)?.GetValueOrDefault(client.Id);
            return new { client.Name, Side = TeamLabel(client.Team), client.Bot, Stats = stats };
        });
        var availableMaps = maps.Select(map => new { Key = map, Label = map, Command = map })
            .Concat(workshopMaps.Values.OrderBy(map => map.Alias).Select(map =>
                new { Key = $"workshop/{map.Id}", Label = map.Title, Command = map.Alias }));
        var snapshot = new
        {
            UpdatedAt = DateTimeOffset.UtcNow, Map = TrackerMapKey(),
            Mode = ModeLabel(activeMode), Online = trackerOnline, Warmup = rules?.WarmupPeriod,
            Paused = rules?.GamePaused, FreezeTime = rules?.FreezePeriod,
            Round = live && IsCompetitiveMode ? (rules?.TotalRoundsPlayed + (trackerRoundOpen ? 1 : 0)) : null,
            MaxRounds = ConVar<int>.Find("mp_maxrounds")?.Value,
            Challenge = restoreChallengeRules is null ? null : TeamLabel(challengeSide),
            Record = match is { Eligible: true } ? match.Record?.Key.Category : null,
            Finished = match?.Finished ?? false, Teams = teams, Roster = board, Maps = availableMaps
        };
        TrySaveState(Path.Combine(TrackerDirectory, "live.json"), JsonSerializer.Serialize(snapshot, TrackerJson));
    }

    private static TrackerStats GetTrackerStats(string id, string name, Dictionary<string, TrackerStats> stats)
    {
        if (!stats.TryGetValue(id, out var row))
            stats.Add(id, row = new TrackerStats { Name = name });
        row.Name = name;
        return row;
    }

    private RoundClient? TrackerClient(CCSPlayerController? player)
    {
        if (player is not { IsValid: true, IsHLTV: false } ||
            player.Team is not (CsTeam.Terrorist or CsTeam.CounterTerrorist))
            return null;
        if (player.IsBot)
        {
            var name = FindBotProfile(player)?.Name ??
                botProfiles.FirstOrDefault(bot => bot.SpawnAlias == player.PlayerName)?.Name ?? player.PlayerName;
            return new(name, name, player.Team, true);
        }
        if (player.AuthorizedSteamID is not { } steam)
            return null;

        var displayName = IsReady(player) ? readyPlayers[player.Slot].OriginalName : player.PlayerName;
        return new(steam.SteamId64.ToString(CultureInfo.InvariantCulture), displayName, player.Team, false);
    }

    private Dictionary<int, RoundClient> TrackerRoster()
    {
        var roster = new Dictionary<int, RoundClient>();
        foreach (var player in Utilities.GetPlayers())
        {
            if (TrackerClient(player) is { } client)
                roster[player.Slot] = client;
        }
        return roster;
    }

    private void TrackerClientLeft(int slot, bool joinsActiveTeam = false)
    {
        if (trackerMatch is { Finished: false, Record: not null } match &&
            (joinsActiveTeam || match.Roster.ContainsKey(slot)))
            InvalidateTrackerMatch(match);
        trackerRound?.Alive.Remove(slot);
    }

    private void ValidateTrackerRoster(Dictionary<int, RoundClient> roster)
    {
        if (trackerMatch is not { Eligible: true, Finished: false, Record: not null } match)
            return;
        var sameRoster = roster.Count == match.Roster.Count && match.Roster.All(entry =>
            roster.TryGetValue(entry.Key, out var current) && current.Id == entry.Value.Id &&
            current.Team == entry.Value.Team && current.Bot == entry.Value.Bot);
        if (!sameRoster)
            InvalidateTrackerMatch(match);
    }

    private void InvalidateTrackerMatch(TrackerMatch match)
    {
        match.Eligible = false;
        if (match.Record?.Pending is not null)
        {
            match.Record.Pending = null;
            trackerDirty = true;
            FlushTracker();
        }
    }

    private static RoundClient[] TrackerDuo(Dictionary<int, RoundClient> roster) =>
        roster.Values.Where(client => !client.Bot).OrderBy(client => client.Id, StringComparer.Ordinal).ToArray();

    private static bool IsDuoOnOneSide(RoundClient[] players) =>
        players.Length == 2 && players[0].Team == players[1].Team;

    private TrackerRecordKey? CurrentRecordKey(Dictionary<int, RoundClient> roster, out RoundClient[] players,
        out int botCount)
    {
        players = TrackerDuo(roster);
        botCount = roster.Count - players.Length;
        var side = players.FirstOrDefault()?.Team;
        if (!IsCompetitiveMode || !IsDuoOnOneSide(players) ||
            roster.Values.Any(client => client.Bot && client.Team == side) ||
            ConnectedHumans().Any(client => client.Team is CsTeam.Terrorist or CsTeam.CounterTerrorist &&
                client.AuthorizedSteamID is null) ||
            (restoreChallengeRules is not null && challengeSide != players[0].Team))
            return null;
        string? category;
        if (duoMode is { } mode)
        {
            category = mode.Side == side && duoBotTarget is > 0 && duoBotTarget == botCount && botFillTimer is null
                ? mode.Category : null;
        }
        else
        {
            category = botCount switch
            {
                InitialBotCount => restoreChallengeRules is not null ? TenSweep : TenWin,
                > InitialBotCount when restoreChallengeRules is null => MaxBots,
                _ => null
            };
        }
        return category is null ? null : new(TrackerMapKey(), category, TeamLabel(players[0].Team),
            players[0].Id, players[1].Id);
    }

    private static string RecordLabel(string category) => category switch
    {
        TenWin => "2v10 Win",
        TenSweep => "2v10 16-0",
        _ => "2vMax"
    };

    private static string TrackerRecordSummary(TrackerRecordKey key, IEnumerable<string> names, TrackerRecord? record)
    {
        var best = record?.Best is { } result
            ? $"Best: {result.RoundsWon}-{result.RoundsLost} vs {result.Bots} bots"
            : "No completed rounds yet";
        return $"[Tracker] {RecordLabel(key.Category)} ({key.Side}) | {string.Join(" + ", names)} | {best} | Attempts: {record?.Attempts ?? 0}";
    }

    private void ShowTrackerRecord(CCSPlayerController? player, string[] args, Action<string> reply)
    {
        var filter = args.Length == 1 ? DuoModeCategory(args[0]) : null;
        if (args.Length != 0 && filter is null)
        {
            reply("[Tracker] Use !record for all modes, or !record win, !record 16, or !record max.");
            return;
        }

        var players = TrackerDuo(TrackerRoster());
        if (!IsDuoOnOneSide(players))
        {
            reply("[Tracker] Join the same side as your duo partner to see your records.");
            return;
        }
        if (player is not null && (TrackerClient(player) is not { } client ||
            !players.Any(member => member.Id == client.Id)))
        {
            reply("[Tracker] Join the active duo to see its records in chat.");
            return;
        }

        var map = TrackerMapKey();
        var side = TeamLabel(players[0].Team);
        string[] categories = filter is null ? [TenWin, TenSweep, MaxBots] : [filter];
        foreach (var category in categories)
        {
            var key = new TrackerRecordKey(map, category, side, players[0].Id, players[1].Id);
            var record = trackerHistory.Records.Find(entry => entry.Key == key);
            reply(TrackerRecordSummary(key, players.Select(client => client.Name), record));
            if (record?.Best is not { } best)
                continue;
            foreach (var stats in best.Players.Values)
            {
                reply(FormattableString.Invariant(
                    $"[Tracker] {stats.Name} | K {stats.Kills} / D {stats.Deaths} / A {stats.Assists} | HS {stats.Headshots} ({stats.HeadshotPercentage}%) | Aces {stats.Aces} | 1v5+ {stats.Clutches} | Defuses {stats.Defuses}"));
            }
        }
    }

    private void BeginTrackerRound(bool live)
    {
        autobought.Clear();
        if (!live)
        {
            ResetTrackerMatch();
            return;
        }
        var rules = Utilities.GetGameRules();
        var roster = TrackerRoster();
        if (trackerMatch is { LastEngineRound: int last } && rules is not null &&
            (rules.TotalRoundsPlayed == 0 || rules.TotalRoundsPlayed < last))
            ResetTrackerMatch();
        trackerRoundOpen = true;

        if (trackerMatch is null)
        {
            var key = CurrentRecordKey(roster, out var players, out var bots);
            TrackerRecord? record = null;
            // After a reload, lifetime tracking can resume, but a partial match cannot set a record.
            if (key is not null && rules?.TotalRoundsPlayed == 0)
            {
                record = trackerHistory.Records.Find(entry => entry.Key == key);
                if (record is null)
                {
                    record = new TrackerRecord { Key = key };
                    trackerHistory.Records.Add(record);
                }
                record.Names = players.Select(player => player.Name).ToArray();
                record.Attempts++;
                trackerDirty = true;
                FlushTracker();
                if (announcedRecord != key)
                {
                    announcedRecord = key;
                    Server.PrintToChatAll(TrackerRecordSummary(key, record.Names, record));
                }
            }

            trackerMatch = new TrackerMatch
            {
                Record = record, Roster = roster, BotCount = bots,
                Side = players.FirstOrDefault()?.Team ?? CsTeam.None
            };
        }
        trackerMatch.LastEngineRound = rules?.TotalRoundsPlayed;
        ValidateTrackerRoster(roster);
        trackerRound = new TrackerRound();
        foreach (var entry in roster)
        {
            // Round start follows the engine's respawn pass. Spawn events also update this roster.
            if (Utilities.GetPlayerFromSlot(entry.Key) is { PawnIsAlive: true })
            {
                trackerRound.Alive.Add(entry.Key, entry.Value);
                trackerRound.Participants[entry.Value.Team].Add(entry.Value.Identity);
            }
        }
        if (IsCompetitiveMode)
        {
            CaptureClutchCandidates(trackerRound);
            var round = trackerRound;
            Server.NextWorldUpdate(() =>
            {
                if (trackerRound == round)
                    BuyTrackerLoadouts();
            });
        }
    }

    private HookResult OnTrackerSpawn(EventPlayerSpawn spawn, GameEventInfo _)
    {
        if (trackerRound is not null && IsLiveGame() && spawn.Userid is { } player && TrackerClient(player) is { } client)
        {
            trackerRound.Alive[player.Slot] = client;
            trackerRound.Participants[client.Team].Add(client.Identity);
            if (!client.Bot && IsCompetitiveMode)
            {
                var round = trackerRound;
                var slot = player.Slot;
                Server.NextWorldUpdate(() =>
                {
                    if (trackerRound == round && Utilities.GetPlayerFromSlot(slot) is { } respawned &&
                        TrackerClient(respawned)?.Id == client.Id)
                        BuyTrackerLoadout(respawned, client);
                });
            }
        }
        return HookResult.Continue;
    }

    private bool CountTrackerEvent() => trackerOnline && IsLiveGame() &&
        (!IsCompetitiveMode || trackerRoundOpen);

    private void UpdateTrackerStats(RoundClient client, Action<TrackerStats> update)
    {
        update(GetTrackerStats(client.Id, client.Name, client.Bot ? trackerHistory.Bots : trackerHistory.Players));
        if (trackerMatch is { Finished: false } match)
            update(GetTrackerStats(client.Id, client.Name, client.Bot ? match.Bots : match.Players));
        trackerDirty = true;
    }

    private HookResult TrackLifetimeDeath(EventPlayerDeath death, GameEventInfo _)
    {
        if (!CountTrackerEvent() || TrackerClient(death.Userid) is not { } victim)
            return HookResult.Continue;
        UpdateTrackerStats(victim, stats => stats.Deaths++);
        var attacker = TrackerClient(death.Attacker);
        if (attacker is not null && (attacker.Team != victim.Team || !IsCompetitiveMode && death.Attacker?.Slot != death.Userid?.Slot))
        {
            var weapon = ClassifyKillWeapon(death.Weapon ?? "");
            UpdateTrackerStats(attacker, stats =>
            {
                stats.Kills++;
                if (death.Headshot)
                    stats.Headshots++;
                if (!attacker.Bot)
                {
                    if (victim.Bot)
                        stats.BotKills++;
                    CountWeaponKill(stats, weapon);
                }
            });

            if (TrackerClient(death.Assister) is { Bot: false } assister &&
                assister.Identity != attacker.Identity && assister.Identity != victim.Identity &&
                (!IsCompetitiveMode || assister.Team == attacker.Team))
                UpdateTrackerStats(assister, stats => stats.Assists++);

            if (!attacker.Bot)
            {
                if (IsCompetitiveMode && trackerRound is { } activeRound)
                    TrackAceKill(activeRound, attacker, victim);
                AnnounceKillMilestones(attacker, victim, weapon);
            }
        }
        if (trackerRound is { } round && death.Userid is { } player)
        {
            round.Participants[victim.Team].Add(victim.Identity);
            round.Alive.Remove(player.Slot);
            if (attacker is { Bot: true } && !victim.Bot && attacker.Team != victim.Team &&
                !round.Alive.Values.Any(client => client.Team == victim.Team))
                round.LastEliminator = attacker;
            CaptureClutchCandidates(round);
        }

        return HookResult.Continue;
    }

    private static void TrackAceKill(TrackerRound round, RoundClient attacker, RoundClient victim)
    {
        var key = (attacker.Id, attacker.Team);
        if (!round.Eliminations.TryGetValue(key, out var eliminations))
            round.Eliminations.Add(key, eliminations = new RoundEliminations(attacker));
        eliminations.Victims.Add(victim.Identity);
    }

    private void CreditRoundAces(TrackerRound round)
    {
        var credited = new HashSet<string>(StringComparer.Ordinal);
        foreach (var eliminations in round.Eliminations.Values)
        {
            var opponent = eliminations.Player.Team == CsTeam.Terrorist ? CsTeam.CounterTerrorist : CsTeam.Terrorist;
            var team = round.Participants[opponent];
            if (team.Count > 0 && team.IsSubsetOf(eliminations.Victims) && credited.Add(eliminations.Player.Id))
                UpdateTrackerStats(eliminations.Player, stats => stats.Aces++);
        }
    }

    private static void CaptureClutchCandidates(TrackerRound round)
    {
        foreach (var team in new[] { CsTeam.Terrorist, CsTeam.CounterTerrorist })
        {
            var survivors = round.Alive.Values.Where(client => client.Team == team).ToArray();
            if (survivors is [{ Bot: false } player] && round.Alive.Count - survivors.Length >= 5)
                round.ClutchCandidates[player.Id] = team;
        }
    }

    private HookResult TrackTrackerObjective(CCSPlayerController? player, string objective)
    {
        if (!CountTrackerEvent() || TrackerClient(player) is not { } client)
            return HookResult.Continue;
        UpdateTrackerStats(client, stats =>
        {
            switch (objective)
            {
                case "plant": stats.Plants++; break;
                case "defuse": stats.Defuses++; break;
                case "rescue": stats.Rescues++; break;
            }
        });
        if (trackerRound is { } round)
        {
            switch (objective)
            {
                case "plant": round.Planter = client; break;
                case "defuse": round.Defuser = client; break;
                case "rescue": round.LastRescuer = client; break;
            }
        }
        return HookResult.Continue;
    }

    private void QueueTrackerRoundEnd(EventRoundEnd end)
    {
        var reason = (RoundEndReason)end.Reason;
        if (reason == RoundEndReason.GameCommencing)
        {
            ResetTrackerMatch();
            return;
        }
        if (!IsCompetitiveMode || !CountTrackerEvent() || trackerRound is not { } round ||
            trackerMatch is not { Finished: false } match)
            return;
        var winner = (CsTeam)end.Winner;
        var challenge = restoreChallengeRules is not null;
        var sweepSide = challenge ? challengeSide : match.Side;
        Server.NextWorldUpdate(() =>
        {
            if (trackerRound != round || trackerMatch != match || !CountTrackerEvent())
                return;
            trackerRoundOpen = false;
            ValidateTrackerRoster(TrackerRoster());
            var surrender = reason is RoundEndReason.CTsSurrender or RoundEndReason.TerroristsSurrender;
            if (!surrender)
                CreditRoundAces(round);
            if (winner is CsTeam.Terrorist or CsTeam.CounterTerrorist && !surrender)
            {
                foreach (var survivor in round.Alive.Values.Where(client => !client.Bot && client.Team == winner &&
                    round.ClutchCandidates.GetValueOrDefault(client.Id) == winner))
                    UpdateTrackerStats(survivor, stats => stats.Clutches++);

                if ((challenge || match.Record?.Key.Category == TenWin && match.Eligible) &&
                    winner != sweepSide && (winner == CsTeam.Terrorist ? match.TRounds : match.CtRounds) == 0)
                    CreditSweepSpoiler(round, reason, winner);

                if (winner == CsTeam.Terrorist)
                    match.TRounds++;
                else
                    match.CtRounds++;
            }
            if (!surrender && (winner is CsTeam.Terrorist or CsTeam.CounterTerrorist || reason == RoundEndReason.RoundDraw))
                StageTrackerResult(match);
            if (challenge && (winner != sweepSide || surrender))
                CompleteTrackerMatch(match);
            FlushTracker();
        });
    }

    private void CreditSweepSpoiler(TrackerRound round, RoundEndReason reason, CsTeam winner)
    {
        var spoiler = reason switch
        {
            RoundEndReason.TargetBombed => round.Planter,
            RoundEndReason.BombDefused => round.Defuser,
            RoundEndReason.AllHostageRescued => round.LastRescuer,
            RoundEndReason.CTsWin or RoundEndReason.TerroristsWin => round.LastEliminator,
            _ => null
        };
        if (spoiler is { Bot: true } && spoiler.Team == winner)
            UpdateTrackerStats(spoiler, stats => stats.SpoiledSweeps++);
    }

    private void FinishTrackerMatch()
    {
        if (trackerMatch is not { } match || !IsLiveGame())
            return;
        Server.NextWorldUpdate(() => Server.NextWorldUpdate(() =>
        {
            if (trackerMatch != match || match.Finished || !IsLiveGame())
                return;
            ValidateTrackerRoster(TrackerRoster());
            CompleteTrackerMatch(match);
            FlushTracker();
        }));
    }

    private void StageTrackerResult(TrackerMatch match)
    {
        if (!match.Eligible || match.Record is not { } record)
            return;
        var won = match.Side == CsTeam.Terrorist ? match.TRounds : match.CtRounds;
        var lost = match.Side == CsTeam.Terrorist ? match.CtRounds : match.TRounds;
        var players = match.Roster.Values.Where(client => !client.Bot).ToDictionary(client => client.Id,
            client => match.Players.TryGetValue(client.Id, out var stats)
                ? stats with { }
                : new TrackerStats { Name = client.Name });
        record.Pending = new TrackerBest(match.BotCount, won, lost, DateTimeOffset.UtcNow, players);
        trackerDirty = true;
    }

    private void CompleteTrackerMatch(TrackerMatch match)
    {
        match.Finished = true;
        if (match.Eligible && match.Record is { } record)
            SettleTrackerRecord(record, announce: true);
    }

    private void SettleTrackerRecord(TrackerRecord record, bool announce)
    {
        if (record.Pending is not { } result)
            return;
        record.Pending = null;
        trackerDirty = true;

        static (int Wins, int Bots, int FewerLosses) Rank(TrackerBest attempt) =>
            (attempt.RoundsWon, attempt.Bots, -attempt.RoundsLost);

        if (record.Best is { } best && Rank(best).CompareTo(Rank(result)) >= 0)
            return;
        record.Best = result;
        if (announce)
        {
            Server.PrintToChatAll(
                $"[Tracker] New record! {string.Join(" + ", record.Names)} | {RecordLabel(record.Key.Category)} ({record.Key.Side}) | {result.RoundsWon}-{result.RoundsLost} vs {result.Bots} bots");
        }
    }
}
