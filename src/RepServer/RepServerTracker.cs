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
    private const int BotKillMilestoneInterval = 1000;
    private const string TenWin = "2v10-win";
    private const string TenSweep = "2v10-16-0";
    private const string MaxBots = "2vmax";

    private sealed class TrackerStats
    {
        public required string Name { get; set; }
        public string? Role { get; set; }
        public int Kills { get; set; }
        public int Deaths { get; set; }
        public int Headshots { get; set; }
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
    }

    private sealed class TrackerHistory
    {
        public Dictionary<string, TrackerStats> Players { get; init; } = new();
        [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
        public Dictionary<string, TrackerStats> Bots { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<TrackerRecord> Records { get; init; } = [];
    }

    private sealed record RoundClient(string Id, string Name, CsTeam Team, bool Bot);
    private sealed class TrackerRound
    {
        public Dictionary<int, RoundClient> Alive { get; } = new();
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
        public bool SawSurrender { get; set; }
    }

    private TrackerHistory trackerHistory = new();
    private TrackerMatch? trackerMatch;
    private TrackerRound? trackerRound;
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
        trackerMatch = null;
        trackerRound = null;
        trackerRoundOpen = false;
        autobought.Clear();
        FlushTracker();
    }

    private void StartTrackerMap()
    {
        ResetTrackerMatch();
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
            match.Eligible = false;
        trackerRound?.Alive.Remove(slot);
    }

    private void ValidateTrackerRoster(Dictionary<int, RoundClient> roster)
    {
        if (trackerMatch is not { Eligible: true, Finished: false, Record: not null } match)
            return;
        match.Eligible = roster.Count == match.Roster.Count && match.Roster.All(entry =>
            roster.TryGetValue(entry.Key, out var current) && current.Id == entry.Value.Id &&
            current.Team == entry.Value.Team && current.Bot == entry.Value.Bot);
    }

    private TrackerRecordKey? CurrentRecordKey(Dictionary<int, RoundClient> roster, out RoundClient[] players,
        out int botCount)
    {
        players = roster.Values.Where(client => !client.Bot).OrderBy(client => client.Id, StringComparer.Ordinal).ToArray();
        botCount = roster.Count - players.Length;
        var side = players.FirstOrDefault()?.Team;
        if (!IsCompetitiveMode || players.Length != 2 || players[0].Team != players[1].Team ||
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

    private void ShowTrackerRecord(CCSPlayerController? player, string[] args, Action<string> reply)
    {
        var roster = TrackerRoster();
        var key = CurrentRecordKey(roster, out var players, out _);
        if (key?.Category == TenWin && trackerMatch is { Eligible: true, Finished: true, Record.Key: var previous } &&
            previous == key with { Category = TenSweep })
            key = previous;
        if (key is null)
        {
            reply(duoMode is not null
                ? "[Tracker] Records require two players together on the selected side and the full bot roster."
                : "[Tracker] Records require two players on the same side against 10 or more bots.");
            return;
        }
        if (player is not null && (TrackerClient(player) is not { } client ||
            client.Id != key.First && client.Id != key.Second))
        {
            reply("[Tracker] Join the active duo to see its record in chat.");
            return;
        }

        var record = trackerHistory.Records.Find(entry => entry.Key == key);
        var best = record?.Best is { } result
            ? $"{result.RoundsWon}-{result.RoundsLost} vs {result.Bots} bots"
            : "No winning result yet";
        reply($"[Tracker] {RecordLabel(key.Category)} ({key.Side}) | {players[0].Name} + {players[1].Name} | {best} | Attempts: {record?.Attempts ?? 0}");
        if (record?.Best is { } bestResult)
        {
            foreach (var stats in bestResult.Players.Values)
            {
                reply(FormattableString.Invariant(
                    $"[Tracker] {stats.Name} | K {stats.Kills} / D {stats.Deaths} | HS {stats.Headshots} ({stats.HeadshotPercentage}%) | 1v5+ {stats.Clutches} | Defuses {stats.Defuses}"));
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
                trackerRound.Alive.Add(entry.Key, entry.Value);
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
            UpdateTrackerStats(attacker, stats =>
            {
                stats.Kills++;
                if (death.Headshot)
                    stats.Headshots++;
                if (!attacker.Bot && victim.Bot)
                    stats.BotKills++;
            });
            if (!attacker.Bot && victim.Bot && trackerHistory.Players[attacker.Id].BotKills is var kills &&
                (kills is 10 or 25 or 50 or 100 or 250 or 500 || kills % BotKillMilestoneInterval == 0))
            {
                FlushTracker();
                Server.PrintToChatAll(FormattableString.Invariant(
                    $"[Tracker] {attacker.Name} just killed bot #{kills:N0}. {victim.Name} had the honor!"));
            }
        }
        if (trackerRound is { } round && death.Userid is { } player)
        {
            round.Alive.Remove(player.Slot);
            if (attacker is { Bot: true } && !victim.Bot && attacker.Team != victim.Team &&
                !round.Alive.Values.Any(client => client.Team == victim.Team))
                round.LastEliminator = attacker;
            CaptureClutchCandidates(round);
        }

        return HookResult.Continue;
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
            match.SawSurrender |= surrender;
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
            if (challenge && (winner != sweepSide || surrender))
                match.Finished = true;
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
            match.Finished = true;
            if (!match.Eligible || match.SawSurrender || match.Record is not { } record)
                return;
            var won = match.Side == CsTeam.Terrorist ? match.TRounds : match.CtRounds;
            var lost = match.Side == CsTeam.Terrorist ? match.CtRounds : match.TRounds;
            if (won <= lost || (record.Key.Category == TenSweep && (won != 16 || lost != 0)))
                return;
            if (record.Best is { } old &&
                (old.Bots, old.RoundsWon - old.RoundsLost, -old.RoundsLost).CompareTo(
                    (match.BotCount, won - lost, -lost)) >= 0)
                return;
            var players = match.Roster.Values.Where(client => !client.Bot).ToDictionary(client => client.Id,
                client => match.Players.GetValueOrDefault(client.Id) ?? new TrackerStats { Name = client.Name });
            record.Best = new TrackerBest(match.BotCount, won, lost, DateTimeOffset.UtcNow, players);
            trackerDirty = true;
            FlushTracker();
            Server.PrintToChatAll($"[Tracker] New record! {string.Join(" + ", record.Names)} | {RecordLabel(record.Key.Category)} ({record.Key.Side}) | {won}-{lost} vs {match.BotCount} bots");
        }));
    }
}
