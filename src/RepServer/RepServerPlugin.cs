using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.ValveConstants.Protobuf;

namespace RepServer;

public sealed partial class RepServerPlugin : BasePlugin
{
    public override string ModuleName => "RepServer";
    public override string ModuleVersion => "0.5.0";

    private const string AdminFile = "/var/lib/repserver/admins.txt";
    private const string ModeFile = "/var/lib/repserver/mode";
    private const string LastMapFile = "/var/lib/repserver/last-map";
    private const string Modes = "bot-game, bot-dm, bot-dm-pistol";
    private static bool IsLiveGame() => Utilities.GetGameRules() is { WarmupPeriod: false };
    private bool IsCompetitiveMode => activeMode is "bot-game" or "bot-game-t" || duoMode is not null;
    private delegate void RepCommand(CCSPlayerController? player, string[] args, Action<string> reply);
    private readonly Dictionary<string, RepCommand> chatCommands = new(StringComparer.OrdinalIgnoreCase);
    private const string BotTeamSettings = "bot_auto_vacate 1; mp_autoteambalance 0; mp_limitteams 0";
    private const int InitialBotCount = 10;
    private const float BotFillInterval = 1.0f;
    private const float JoinGracePeriod = 5.0f;
    private const int UnexpectedBotBurstLimit = 3;
    private const double UnexpectedBotBurstInterval = 0.25;
    private const int ReservedPlayerSlots = 1;
    private static readonly Regex MapName = new(@"\A[a-z0-9_]{1,64}\z");
    private string[] maps = [];
    private sealed record BotProfile(string Name, string Role, string SpawnAlias);
    private sealed record BotSpawn(BotProfile Profile, string SpawnName);
    private readonly List<BotProfile> botProfiles = new();
    private readonly HashSet<string> priorityBotNames = new(StringComparer.OrdinalIgnoreCase);
    private string activeMode = "bot-game";
    private string? pendingMode;
    private CounterStrikeSharp.API.Modules.Timers.Timer? botFillTimer;
    private int? botFillRequesterSlot;
    private bool autoRosterPending = true;
    private readonly HashSet<int> connectingClients = new();
    private readonly Dictionary<string, BotProfile> requestedBots = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string> managedBotSlots = new();
    private bool loggedUnexpectedBot;
    private Action? restoreChallengeRules;
    private CsTeam challengeSide;
    private bool challengeRoundActive;
    private bool challengeRestartPending;
    private CounterStrikeSharp.API.Modules.Timers.Timer? challengeRetryTimer;
    private int unexpectedBotBurst;
    private double lastUnexpectedBotAt = double.NegativeInfinity;
    private double botSpawnReadyAt;

    private static int PlayerLimit =>
        int.TryParse(Environment.GetEnvironmentVariable("CS2_MAXPLAYERS"), out var configured) && configured > 0
            ? Math.Min(configured, Server.MaxPlayers)
            : Server.MaxPlayers;

    public override void Load(bool hotReload)
    {
        activeMode = Environment.GetEnvironmentVariable("REPSERVER_ACTIVE_MODE") ?? "bot-game";
        duoMode = ReadDuoMode(activeMode);
        if (activeMode == "bot-dm-pistol")
        {
            RegisterEventHandler<EventPlayerSpawn>((@event, _) => StripNonPistols(@event.Userid));
            RegisterEventHandler<EventItemPickup>((@event, _) => StripNonPistols(@event.Userid));
        }
        else if (IsCompetitiveMode)
        {
            RegisterEventHandler<EventPlayerSpawn>((@event, _) =>
            {
                if (@event.Userid is { IsValid: true, IsHLTV: false } player &&
                    player.PlayerPawn.Value is { IsValid: true })
                {
                    TrackRoundSpawn(player);
                    var pawnHandle = new CHandle<CCSPlayerPawn>(player.PlayerPawn.Raw);
                    Server.NextWorldUpdate(() =>
                    {
                        if (pawnHandle.Value is { IsValid: true, ItemServices: { } items })
                            items.As<CCSPlayer_ItemServices>().HasDefuser = true;
                    });
                }
                return HookResult.Continue;
            });
        }

        maps = File.ReadLines("/opt/repserver/maps.txt")
            .Select(line => line.Trim())
            .Where(line => MapName.IsMatch(line))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        LoadWorkshopMaps();

        LoadBotProfiles();
        LoadBotPriorities();
        LoadTracker(hotReload);

        RegisterListener<Listeners.OnMapStart>(map =>
        {
            botFillTimer = null;
            botFillRequesterSlot = null;
            requestedBots.Clear();
            managedBotSlots.Clear();
            loggedUnexpectedBot = false;
            unexpectedBotBurst = 0;
            lastUnexpectedBotAt = double.NegativeInfinity;
            autoRosterPending = true;
            botSpawnReadyAt = Server.EngineTime + JoinGracePeriod;
            ResetReadyPlayers();
            nextReadyHudUpdate = 0;
            roundStats = null;
            StartTrackerMap();
            RememberMap(map);
            duoBotTarget = null;
            if (duoMode is not null)
                botFillTimer = AddTimer(JoinGracePeriod, StartDuoRosterWhenReady, TimerFlags.STOP_ON_MAPCHANGE);
        });
        RegisterListener<Listeners.OnMapEnd>(() =>
        {
            StopChallenge();
            StopTracker();
            ResetReadyPlayers();
            roundStats = null;
        });
        RegisterListener<Listeners.OnClientConnected>(slot => connectingClients.Add(slot));
        RegisterListener<Listeners.OnClientDisconnect>(slot =>
        {
            connectingClients.Remove(slot);
            TrackerClientLeft(slot);
            ClearReadyPlayer(slot);
            QueueReadyCheck();
            if (managedBotSlots.Remove(slot, out var botName))
            {
                requestedBots.Remove(botName);
                SyncBotQuota();
            }
            if (botFillRequesterSlot == slot)
            {
                CancelBotFill();
                Console.WriteLine("[RepServer] Bot fill stopped because the requesting player disconnected.");
            }
        });
        if (hotReload)
        {
            autoRosterPending = duoMode is not null;
            RememberMap(Server.MapName);
            if (duoMode is not null)
                botFillTimer = AddTimer(JoinGracePeriod, StartDuoRosterWhenReady, TimerFlags.STOP_ON_MAPCHANGE);
        }

        RegisterListener<Listeners.OnClientPutInServer>(slot => Server.NextWorldUpdate(() =>
        {
            if (Utilities.GetPlayerFromSlot(slot) is not { IsValid: true } client)
                return;
            if (client.IsHLTV || client.IsBot)
            {
                connectingClients.Remove(slot);
                QueueReadyCheck();
            }
            if (client.IsBot && !client.IsHLTV && !TrackRequestedBot(client))
                RejectUnrequestedBot(client);
        }));
        if (activeMode != "bot-dm-pistol")
            RegisterEventHandler<EventItemPickup>(RestrictSniperWeapons);

        RegisterMenuListeners();
        RegisterEventHandler<EventRoundEnd>((round, _) =>
        {
            QueueTrackerRoundEnd(round);
            QueueRoundStats(round);
            return OnChallengeRoundEnd(round);
        });
        if (IsCompetitiveMode)
        {
            RegisterEventHandler<EventPlayerDeath>(TrackRoundDeath);
            RegisterEventHandler<EventPlayerHurt>(TrackRoundDamage);
            RegisterEventHandler<EventBombPlanted>(TrackBombPlant);
            RegisterEventHandler<EventBombDefused>(TrackBombDefuse, HookMode.Pre);
        }
        RegisterEventHandler<EventCsWinPanelMatch>((_, _) =>
        {
            FinishTrackerMatch();
            return OnChallengeMatchEnd();
        });
        RegisterEventHandler<EventPlayerConnect>((connection, info) =>
        {
            if (connection.Bot)
                info.DontBroadcast = true;
            return HookResult.Continue;
        }, HookMode.Pre);
        RegisterEventHandler<EventPlayerTeam>((change, _) =>
        {
            if (change.Userid is { IsValid: true } changed && change.Team != change.Oldteam)
            {
                TrackerClientLeft(changed.Slot, change.Team is (int)CsTeam.Terrorist or (int)CsTeam.CounterTerrorist);
                QueueReadyCheck();
            }
            if (change.Userid is { IsValid: true, IsBot: true, IsHLTV: false })
                change.Silent = true;
            return HookResult.Continue;
        }, HookMode.Pre);
        RegisterEventHandler<EventPlayerDisconnect>((connection, info) =>
        {
            if (connection.Networkid == "BOT" ||
                connection.Userid is { IsValid: true, IsBot: true, IsHLTV: false })
                info.DontBroadcast = true;
            return HookResult.Continue;
        }, HookMode.Pre);
        RegisterEventHandler<EventRoundStart>((_, _) =>
        {
            var liveRound = IsLiveGame();
            if (challengeRetryTimer is null)
                challengeRestartPending = false;
            challengeRoundActive = restoreChallengeRules is not null && liveRound;
            BeginRoundStats(liveRound);
            BeginTrackerRound(liveRound);
            if (readyPlayers.Count != 0)
                IsReadinessWarmup();
            return HookResult.Continue;
        });

        var commands = new List<(string Name, string Syntax, string Description, bool Admin)>();

        void RegisterChatCommand(string name, string description, RepCommand handler,
            bool admin = false, string arguments = "")
        {
            var syntax = arguments.Length == 0 ? $"!{name}" : $"!{name} {arguments}";
            commands.Add(($"!{name}", syntax, description, admin));
            chatCommands.Add(name, handler);
            AddCommand($"css_{name}", description, (player, command) =>
            {
                var args = Enumerable.Range(1, command.ArgCount - 1).Select(command.ArgByIndex).ToArray();
                handler(player, args, command.ReplyToCommand);
            });
        }

        RegisterChatCommand("help", "Show all commands", (_, _, reply) =>
        {
            foreach (var entry in commands)
            {
                var access = entry.Admin ? " (admin)" : "";
                reply($"[RepServer] {entry.Syntax}{access} – {entry.Description}");
            }
        });
        RegisterChatCommand("menu", "Open the RepServer menu", (player, _, reply) =>
        {
            if (player is { IsValid: true, IsBot: false, IsHLTV: false })
                QueueOpenMenu(player);
            else
                reply("[RepServer] Type ! in game to open the menu.");
        });
        RegisterChatCommand("maps", "List available maps", (player, _, reply) =>
        {
            var choices = MapChoices(IsAdmin(player)).ToArray();
            if (choices.Length == 0)
                reply("[RepServer] Add a Workshop map with !add_map <workshop_url>.");
            foreach (var group in choices.Chunk(6))
                reply("[RepServer] " + string.Join(", ", group));
        });
        RegisterChatCommand("status", "Show the plugin version, map and game mode", (_, _, reply) =>
            reply($"[RepServer] v{ModuleVersion} | map: {Server.MapName} | mode: {ModeLabel(activeMode)}"));
        RegisterChatCommand("modes", "List game modes", (_, _, reply) =>
        {
            reply($"[RepServer] {Modes}. Use bot-game t to play as T.");
            reply("[RepServer] Duo modes: !mode win, !mode 16, !mode max. Add t or ct to choose your side.");
        });
        RegisterChatCommand("ready", "Mark yourself ready during warmup", (player, _, reply) =>
            SetReady(player, reply, true));
        RegisterChatCommand("unready", "Clear your ready status", (player, _, reply) =>
            SetReady(player, reply, false));
        RegisterChatCommand("record", "Show your duo's record for the current map and challenge", ShowTrackerRecord);
        RegisterChatCommand("autobuy", "Save equipped gear or turn autobuy on or off", SaveAutobuy, arguments: "[on|off]");
        RegisterChatCommand("trackeradmin", "Get a personal link to the web admin panel", OpenTrackerAdmin, admin: true);
        RegisterChatCommand("readystatus", "Show which players are ready", (_, _, reply) =>
            ShowReadyStatus(reply));
        RegisterChatCommand("change-mode", "Change game mode", ChangeMode, admin: true, arguments: "<mode> [t|ct]");
        RegisterChatCommand("mode", "Choose a game mode or set up a duo match", ChangeMode, admin: true, arguments: "<mode> [t|ct]");
        RegisterChatCommand("map", "Change map (standard maps require admin)", ChangeMap, arguments: "<name>");
        RegisterChatCommand("add_map", "Save a Workshop map with an optional alias",
            (player, args, reply) => AddWorkshopMap(player, string.Join(' ', args), reply),
            arguments: "<workshop_url> [name]");
        RegisterChatCommand("restart", "Restart the match", (player, _, reply) =>
            RunMatchCommand(player, reply, "mp_restartgame 1", "Restarting the match."), admin: true);
        RegisterChatCommand("pause", "Pause the match", (player, _, reply) =>
            RunMatchCommand(player, reply, "mp_pause_match", "Requesting a match pause."), admin: true);
        RegisterChatCommand("unpause", "Resume the match", (player, _, reply) =>
            RunMatchCommand(player, reply, "mp_unpause_match", "Requesting that the match resume."), admin: true);
        RegisterChatCommand("kick_bot", "Remove all bots and disable automatic fill", (player, _, reply) => KickBots(player, reply), admin: true);
        RegisterChatCommand("bot_add_t", "Add bots to T", (player, args, reply) =>
            AddBotsFromCommand(player, args, reply, CsTeam.Terrorist), admin: true, arguments: "[count]");
        RegisterChatCommand("bot_add_ct", "Add bots to CT", (player, args, reply) =>
            AddBotsFromCommand(player, args, reply, CsTeam.CounterTerrorist), admin: true, arguments: "[count]");
        RegisterChatCommand("add_bot_t", "Alias for !bot_add_t", (player, args, reply) =>
            AddBotsFromCommand(player, args, reply, CsTeam.Terrorist), admin: true, arguments: "[count]");
        RegisterChatCommand("add_bot_ct", "Alias for !bot_add_ct", (player, args, reply) =>
            AddBotsFromCommand(player, args, reply, CsTeam.CounterTerrorist), admin: true, arguments: "[count]");
        RegisterChatCommand("set_bot_t", "Fill T to the specified bot count", (player, args, reply) =>
            SetBotsFromCommand(player, args, reply, CsTeam.Terrorist), admin: true, arguments: "<count>");
        RegisterChatCommand("set_bot_ct", "Fill CT to the specified bot count", (player, args, reply) =>
            SetBotsFromCommand(player, args, reply, CsTeam.CounterTerrorist), admin: true, arguments: "<count>");
        RegisterChatCommand("bot_roster", "Preview the bot roster with one sniper per five bots", (_, args, reply) =>
        {
            if (args.Length != 1 ||
                !int.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out var target) ||
                target < 1 || target > PlayerLimit)
            {
                reply($"[RepServer] Use !bot_roster <count> (1–{PlayerLimit}).");
                return;
            }

            PreviewBotRoster(target, reply);
        }, arguments: "<count>");
        RegisterChatCommand("16t", "Set up the T 16-0 challenge against CT bots",
            (player, _, reply) => SetChallenge(player, reply, CsTeam.Terrorist), admin: true);
        RegisterChatCommand("16ct", "Set up the CT 16-0 challenge against T bots",
            (player, _, reply) => SetChallenge(player, reply, CsTeam.CounterTerrorist), admin: true);
        RegisterChatCommand("live", "End warmup in competitive modes", (player, _, reply) => StartMatch(player, reply), admin: true);

        AddCommand("css_workshop_result", "Check the result of a Workshop lookup started from the server console",
            (player, command) =>
            {
                if (player is null)
                    command.ReplyToCommand(command.ArgCount == 2
                        ? WorkshopResult(command.ArgByIndex(1))
                        : "[RepServer] Use css_workshop_result <workshop_id>.");
            });

        var joinLines = commands.GroupBy(entry => entry.Admin)
            .SelectMany(group => group.Select(entry => entry.Name).Chunk(5)
                .Select(chunk => $"[RepServer] {(group.Key ? "Admin" : "Commands")}: {string.Join(", ", chunk)}"))
            .ToArray();

        RegisterEventHandler<EventPlayerConnectFull>((@event, _) =>
        {
            if (@event.Userid is { IsValid: true } player)
            {
                connectingClients.Remove(player.Slot);
                QueueReadyCheck();
                if (player.IsBot || player.IsHLTV)
                    return HookResult.Continue;

                GetRoundPlayerStats(player);
                botSpawnReadyAt = Server.EngineTime + JoinGracePeriod;
                ScheduleInitialFill(player);
                player.PrintToChat($"[RepServer] Mode: {ModeLabel(activeMode)} | map: {Server.MapName}");
                player.PrintToChat("[RepServer] Type ! to open the menu.");
                foreach (var line in joinLines)
                    player.PrintToChat(line);
            }

            return HookResult.Continue;
        });
        Console.WriteLine($"[RepServer] v{ModuleVersion} loaded | mode: {ModeLabel(activeMode)}");
    }

    private static string ModeLabel(string mode) => ReadDuoMode(mode)?.Label ?? (mode switch
    {
        "bot-game" => "bot-game (bots on T)",
        "bot-game-t" => "bot-game (bots on CT)",
        _ => mode
    });

    private void SetChallenge(CCSPlayerController? player, Action<string> reply, CsTeam side)
    {
        if (duoMode is not null)
        {
            SelectDuoMode(player, reply, new(TenSweep, side));
            return;
        }
        if (!CheckAdmin(player, reply))
            return;
        if (!IsCompetitiveMode || pendingMode is not null || Utilities.GetGameRules() is null)
        {
            reply("[RepServer] The 16-0 challenge requires an active competitive map.");
            return;
        }

        if (restoreChallengeRules is null && !ApplyChallengeRules())
        {
            reply("[RepServer] Could not apply the 16-0 match rules.");
            return;
        }

        challengeRetryTimer?.Kill();
        challengeRetryTimer = null;
        challengeRestartPending = false;
        challengeRoundActive = false;
        challengeSide = side;
        if (trackerMatch is { } match)
            match.Eligible = false;
        autoRosterPending = false;
        CancelBotFill();
        var botTeam = side == CsTeam.CounterTerrorist ? CsTeam.Terrorist : CsTeam.CounterTerrorist;
        StartBotFill(null, botTeam, InitialBotCount, prepareRoster: true);

        var message = $"[RepServer] {TeamLabel(side)} 16-0 challenge set up. Join {TeamLabel(side)} to play against {InitialBotCount} {TeamLabel(botTeam)} bots.";
        if (player is null)
            reply(message);
        Server.PrintToChatAll(message);
    }

    private bool ApplyChallengeRules()
    {
        var maxRounds = ConVar.Find("mp_maxrounds");
        var halftime = ConVar.Find("mp_halftime");
        var clinch = ConVar.Find("mp_match_can_clinch");
        if (maxRounds is null || halftime is null || clinch is null)
            return false;

        var originalMaxRounds = maxRounds.GetPrimitiveValue<int>();
        var originalHalftime = halftime.GetPrimitiveValue<bool>();
        var originalClinch = clinch.GetPrimitiveValue<bool>();
        restoreChallengeRules = () =>
        {
            maxRounds.SetValue(originalMaxRounds);
            halftime.SetValue(originalHalftime);
            clinch.SetValue(originalClinch);
        };
        maxRounds.SetValue(30);
        halftime.SetValue(false);
        clinch.SetValue(true);

        return true;
    }

    private void StopChallenge()
    {
        challengeRetryTimer?.Kill();
        challengeRetryTimer = null;
        challengeRestartPending = false;
        challengeRoundActive = false;
        restoreChallengeRules?.Invoke();
        restoreChallengeRules = null;
    }

    private HookResult OnChallengeRoundEnd(EventRoundEnd round)
    {
        var reason = (RoundEndReason)round.Reason;
        if (!challengeRoundActive || !IsLiveGame() ||
            reason == RoundEndReason.GameCommencing)
            return HookResult.Continue;

        if (reason is RoundEndReason.CTsSurrender or RoundEndReason.TerroristsSurrender)
            RetryChallenge("A team surrendered.");
        else if (reason == RoundEndReason.RoundDraw)
            RetryChallenge("The round was a draw.");
        else if (round.Winner is (int)CsTeam.Terrorist or (int)CsTeam.CounterTerrorist &&
            round.Winner != (int)challengeSide)
            RetryChallenge($"{TeamLabel((CsTeam)round.Winner)} won a round.");
        return HookResult.Continue;
    }

    private HookResult OnChallengeMatchEnd()
    {
        if (!challengeRoundActive || !IsLiveGame() || challengeRestartPending || pendingMode is not null)
            return HookResult.Continue;

        var teams = Utilities.FindAllEntitiesByDesignerName<CCSTeam>("cs_team_manager").ToArray();
        var side = teams.FirstOrDefault(team => team.TeamNum == (byte)challengeSide);
        var opponent = teams.FirstOrDefault(team => team.TeamNum != (byte)challengeSide &&
            team.TeamNum is (byte)CsTeam.Terrorist or (byte)CsTeam.CounterTerrorist);
        if (side is not null && opponent is not null)
        {
            if (side.Score == 16 && opponent.Score == 0)
            {
                Server.PrintToChatAll($"[RepServer] Challenge complete. {TeamLabel(challengeSide)} won 16-0!");
                if (duoMode?.Category == TenSweep)
                    challengeRoundActive = false;
                else
                    StopChallenge();
            }
            else
            {
                RetryChallenge($"The match ended {side.Score}-{opponent.Score}.");
            }
        }
        return HookResult.Continue;
    }

    private void RetryChallenge(string reason)
    {
        if (restoreChallengeRules is null || challengeRestartPending || pendingMode is not null)
            return;

        challengeRestartPending = true;
        const string restart = "mp_restartgame 1";
        AnnounceCommand(null, restart, $"{TeamLabel(challengeSide)} 16-0 challenge failed. {reason} Restarting in three seconds.");
        challengeRetryTimer = AddTimer(2.0f, () =>
        {
            challengeRetryTimer = null;
            if (pendingMode is null)
                Server.ExecuteCommand(restart);
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void ChangeMode(CCSPlayerController? player, string[] args, Action<string> reply)
    {
        var category = args.FirstOrDefault()?.ToLowerInvariant() switch
        {
            "win" or "2v10" or TenWin => TenWin,
            "16" or TenSweep => TenSweep,
            "max" or MaxBots => MaxBots,
            _ => null
        };
        if (category is not null)
        {
            var side = args.Length switch
            {
                1 when player?.Team is CsTeam.Terrorist or CsTeam.CounterTerrorist => player.Team,
                1 => duoMode?.Side ?? (activeMode == "bot-game-t" ? CsTeam.Terrorist : CsTeam.CounterTerrorist),
                2 when args[1].Equals("t", StringComparison.OrdinalIgnoreCase) => CsTeam.Terrorist,
                2 when args[1].Equals("ct", StringComparison.OrdinalIgnoreCase) => CsTeam.CounterTerrorist,
                _ => CsTeam.None
            };
            if (side == CsTeam.None)
                reply("[RepServer] Use !mode win, !mode 16, or !mode max. Add t or ct to choose your side.");
            else
                SelectDuoMode(player, reply, new(category, side));
            return;
        }

        var requested = args.Length == 1 ? args[0].ToLowerInvariant() : null;
        if (args.Length == 2 && args[0].Equals("bot-game", StringComparison.OrdinalIgnoreCase))
        {
            requested = args[1].ToLowerInvariant() switch
            {
                "t" => "bot-game-t",
                "ct" => "bot-game",
                _ => null
            };
        }

        SelectMode(player, reply, requested);
    }

    private void SelectMode(CCSPlayerController? player, Action<string> reply, string? requested)
    {
        if (!CheckAdmin(player, reply))
            return;

        if (requested is not ("bot-game" or "bot-game-t" or "bot-dm" or "bot-dm-pistol"))
        {
            reply($"[RepServer] !mode <mode>. Choose win, 16, max, or {Modes}.");
            return;
        }
        if (CheckModeSwitchPending(reply))
            return;
        if (requested == activeMode)
        {
            reply($"[RepServer] {ModeLabel(activeMode)} is already active.");
            return;
        }

        RestartIntoMode(player, reply, requested);
    }

    private bool CheckModeSwitchPending(Action<string> reply)
    {
        if (pendingMode is null)
            return false;
        reply($"[RepServer] A switch to {ModeLabel(pendingMode)} is already in progress.");
        return true;
    }

    private void RestartIntoMode(CCSPlayerController? player, Action<string> reply, string requested)
    {
        if (!TrySaveState(ModeFile, requested))
        {
            reply("[RepServer] Could not save the mode. Check data/state on the server.");
            return;
        }

        pendingMode = requested;
        var message = $"Switching to {ModeLabel(requested)}. The server will restart in three seconds. Reconnect when it is back online.";
        if (player is null)
            reply("[RepServer] " + message);
        Server.PrintToChatAll("[RepServer] " + message);
        AddTimer(3.0f, () => Server.ExecuteCommand("quit"));
    }

    private void RememberMap(string? map)
    {
        if (map is not null && maps.Contains(map, StringComparer.Ordinal))
            TrySaveState(LastMapFile, map);
    }

    private static bool TrySaveState(string path, string value)
    {
        try
        {
            File.WriteAllText(path + ".tmp", value + "\n");
            File.Move(path + ".tmp", path, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[RepServer] Could not save {Path.GetFileName(path)}: {exception.GetType().Name}");
            return false;
        }
    }

    private void KickBots(CCSPlayerController? player, Action<string> reply)
    {
        if (!CheckAdmin(player, reply))
            return;

        autoRosterPending = false;
        CancelBotFill();
        requestedBots.Clear();
        managedBotSlots.Clear();
        const string action = "bot_quota_mode normal; bot_quota 0; bot_kick";
        Console.WriteLine("[RepServer] Removing all bots.");
        reply("[RepServer] Removing all bots.");
        Server.NextWorldUpdate(() =>
        {
            Server.ExecuteCommand(action);
            foreach (var bot in CurrentBots())
            {
                if (bot.IsValid)
                    bot.Disconnect(NetworkDisconnectionReason.NETWORK_DISCONNECT_KICKED);
            }
        });
    }

    private static CCSPlayerController[] CurrentBots() =>
        Utilities.GetPlayers().Where(client => client.IsBot && !client.IsHLTV).ToArray();

    private bool TrackRequestedBot(CCSPlayerController bot)
    {
        if (managedBotSlots.ContainsKey(bot.Slot))
            return true;

        var spawnName = bot.PlayerName;
        if (!requestedBots.TryGetValue(spawnName, out var profile) || managedBotSlots.ContainsValue(spawnName))
            return false;

        managedBotSlots[bot.Slot] = spawnName;
        SyncBotQuota();
        if (spawnName != profile.Name)
            SetDisplayName(bot, profile.Name);
        return true;
    }

    private BotProfile? FindBotProfile(CCSPlayerController bot) =>
        managedBotSlots.TryGetValue(bot.Slot, out var spawnName)
            ? requestedBots.GetValueOrDefault(spawnName)
            : requestedBots.GetValueOrDefault(bot.PlayerName);

    private void RejectUnrequestedBot(CCSPlayerController bot)
    {
        if (unexpectedBotBurst >= UnexpectedBotBurstLimit)
            return;

        var now = Server.EngineTime;
        unexpectedBotBurst = now - lastUnexpectedBotAt <= UnexpectedBotBurstInterval ? unexpectedBotBurst + 1 : 1;
        lastUnexpectedBotAt = now;
        if (unexpectedBotBurst >= UnexpectedBotBurstLimit)
        {
            autoRosterPending = false;
            CancelBotFill();
            requestedBots.Clear();
            managedBotSlots.Clear();
            SyncBotQuota();
            Server.ExecuteCommand("bot_quota_mode normal; bot_quota 0; bot_kick");
            ReportBotFill("Stopped repeated bot spawning by CS2. Admins can refill with !set_bot_t or !set_bot_ct.");
            return;
        }

        if (managedBotSlots.Remove(bot.Slot, out var botName))
            requestedBots.Remove(botName);
        // Lower the quota before disconnecting, so the engine won't fill the slot again.
        SyncBotQuota();
        if (!loggedUnexpectedBot)
        {
            Console.Error.WriteLine($"[RepServer] Removing {bot.PlayerName} because it spawned on the wrong team or outside a bot fill.");
            loggedUnexpectedBot = true;
        }
        bot.Disconnect(NetworkDisconnectionReason.NETWORK_DISCONNECT_KICKED);
    }

    private void SyncBotQuota() =>
        ConVar.Find("bot_quota")?.SetValue(managedBotSlots.Count);

    private HookResult RestrictSniperWeapons(EventItemPickup pickup, GameEventInfo info)
    {
        if (pickup.Userid is { IsValid: true, IsBot: true, IsHLTV: false } bot &&
            FindBotProfile(bot)?.Role == "rifle")
        {
            var weapon = pickup.Item;
            if (weapon.StartsWith("weapon_", StringComparison.Ordinal))
                weapon = weapon[7..];
            if (weapon is "awp" or "ssg08" or "scar20" or "g3sg1")
                bot.RemoveItemByDesignerName("weapon_" + weapon);
        }
        return HookResult.Continue;
    }

    private void LoadBotProfiles()
    {
        botProfiles.Clear();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var profiles = new List<(string Name, string Role)>();
        var lineNumber = 0;
        foreach (var line in File.ReadLines("/opt/repserver/bot-names.txt"))
        {
            lineNumber++;
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                continue;

            var fields = trimmed.Split('|', 2, StringSplitOptions.TrimEntries);
            var name = fields[0];
            var role = fields.Length == 2 ? fields[1].ToLowerInvariant() : "rifle";
            if (name.Length == 0 || name.Any(c => char.IsControl(c) || c is '"' or '\\' or ';') || Encoding.UTF8.GetByteCount(name) > 31 ||
                role is not ("rifle" or "sniper") || !seen.Add(name))
            {
                Console.Error.WriteLine($"[RepServer] Invalid or duplicate bot name in bot-names.txt on line {lineNumber}.");
                continue;
            }

            profiles.Add((name, role));
        }

        // Match the fallback profile names in build-bot-profile.py.
        var aliasNumber = 0;
        foreach (var (name, role) in profiles)
        {
            string alias;
            do
            {
                alias = "__repbot_" + (++aliasNumber).ToString(CultureInfo.InvariantCulture);
            } while (seen.Contains(alias));

            botProfiles.Add(new BotProfile(name, role, alias));
        }
    }

    private void PreviewBotRoster(int target, Action<string> reply)
    {
        var rifle = RandomizedBots("rifle");
        var sniper = RandomizedBots("sniper");
        var roster = new List<BotProfile>();

        for (var position = 1; position <= target; position++)
        {
            var available = position % 5 == 0 ? sniper : rifle;
            if (!available.TryDequeue(out var profile))
                break;

            roster.Add(profile);
        }

        var sniperCount = roster.Count(profile => profile.Role == "sniper");
        reply($"[RepServer] Preview: {roster.Count} of {target} bots, including {sniperCount} snipers.");
        if (roster.Count < target)
        {
            var missingRole = (roster.Count + 1) % 5 == 0 ? "sniper" : "rifle";
            reply($"[RepServer] The next slot requires an unused name with the {missingRole} role.");
        }

        foreach (var group in roster.Chunk(2))
            reply("[RepServer] " + string.Join(", ", group.Select(profile => $"{profile.Name} ({profile.Role})")));
    }

    private void LoadBotPriorities()
    {
        priorityBotNames.Clear();
        foreach (var line in File.ReadLines("/opt/repserver/bot-priority.txt"))
        {
            var name = line.Trim();
            if (name.Length == 0 || name.StartsWith('#'))
                continue;

            if (botProfiles.Any(profile => string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase)))
                priorityBotNames.Add(name);
            else
                Console.Error.WriteLine($"[RepServer] Priority name missing from bot-names.txt: {name}");
        }
    }

    private Queue<BotProfile> RandomizedBots(string role, HashSet<string>? inUse = null)
    {
        var candidates = botProfiles.Where(profile => profile.Role == role && inUse?.Contains(profile.Name) != true).ToArray();
        Random.Shared.Shuffle(candidates.AsSpan());
        return new Queue<BotProfile>(candidates.OrderByDescending(profile => priorityBotNames.Contains(profile.Name)));
    }

    private void AddBot(CCSPlayerController? player, Action<string> reply, CsTeam team, int count = 1)
    {
        var current = CurrentBots().Count(bot => bot.TeamNum == (byte)team);
        SetBots(player, reply, team, Math.Min(current + count, PlayerLimit));
    }

    private void AddBotsFromCommand(CCSPlayerController? player, string[] args, Action<string> reply, CsTeam team)
    {
        var count = 1;
        if (args.Length > 0 && (args.Length != 1 ||
            !int.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out count) ||
            count < 1 || count > PlayerLimit))
        {
            reply($"[RepServer] Enter a whole number between 1 and {PlayerLimit}.");
            return;
        }

        AddBot(player, reply, team, count);
    }

    private void SetBotsFromCommand(CCSPlayerController? player, string[] args, Action<string> reply, CsTeam team)
    {
        var target = args.Length == 1 &&
            int.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed : 0;
        SetBots(player, reply, team, target);
    }

    private void SetBots(CCSPlayerController? player, Action<string> reply, CsTeam team, int target)
    {
        if (!CanAddBots(player, reply))
            return;
        if (target < 1 || target > PlayerLimit)
        {
            reply($"[RepServer] Enter a whole number between 1 and {PlayerLimit}.");
            return;
        }

        var current = CurrentBots().Count(bot => bot.TeamNum == (byte)team);
        if (current >= target)
        {
            reply($"[RepServer] {TeamLabel(team)} already has {current} bots.");
            return;
        }

        autoRosterPending = false;
        if (player is null)
            reply($"[RepServer] Filling {TeamLabel(team)} to {target} bots.");
        StartBotFill(player, team, target);
    }

    private static string TeamLabel(CsTeam team) => team == CsTeam.Terrorist ? "T" : "CT";

    private void ScheduleInitialFill(CCSPlayerController player)
    {
        if (!autoRosterPending || botFillTimer is not null)
            return;

        autoRosterPending = false;
        botFillRequesterSlot = duoMode is null ? player.Slot : null;
        botFillTimer = AddTimer(JoinGracePeriod, duoMode is null ? StartInitialFillWhenReady : StartDuoRosterWhenReady,
            TimerFlags.STOP_ON_MAPCHANGE);
    }

    private bool HasConnectingHumans() => connectingClients.Any(slot =>
        Utilities.GetPlayerFromSlot(slot) is not ({ IsValid: true, IsBot: true } or { IsValid: true, IsHLTV: true }));

    private bool CanSpawnBots() => !HasConnectingHumans() && Server.EngineTime >= botSpawnReadyAt;

    private void StartInitialFillWhenReady()
    {
        var humans = ConnectedHumans();
        var requester = humans.FirstOrDefault(client => client.Slot == botFillRequesterSlot);
        if (requester is null)
        {
            botFillTimer = null;
            botFillRequesterSlot = null;
            return;
        }
        if (!CanSpawnBots() || !humans.Any(IsOnPlayingTeam))
        {
            botFillTimer = AddTimer(BotFillInterval, StartInitialFillWhenReady, TimerFlags.STOP_ON_MAPCHANGE);
            return;
        }

        var team = activeMode == "bot-game-t" ? CsTeam.CounterTerrorist : CsTeam.Terrorist;
        StartBotFill(requester, team, InitialBotCount);
    }

    private void StartBotFill(CCSPlayerController? player, CsTeam team, int target, bool prepareRoster = false)
    {
        unexpectedBotBurst = 0;
        lastUnexpectedBotAt = double.NegativeInfinity;
        var teamLabel = TeamLabel(team);
        Console.WriteLine($"[RepServer] Filling {teamLabel} to {target} bots.");
        botFillRequesterSlot = player?.Slot;
        var rejectedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // CS2 must finish the chat packet before it creates a bot.
        botFillTimer = AddTimer(0.1f, () =>
        {
            if (prepareRoster && PrepareBotRoster(team, target))
            {
                botFillTimer = AddTimer(BotFillInterval, () => CheckBotFill(team, target, rejectedNames),
                    TimerFlags.STOP_ON_MAPCHANGE);
                return;
            }
            CheckBotFill(team, target, rejectedNames);
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }

    private bool PrepareBotRoster(CsTeam team, int target)
    {
        var bots = CurrentBots();
        foreach (var bot in bots)
            TrackRequestedBot(bot);

        var keep = bots.Where(bot => bot.TeamNum == (byte)team && managedBotSlots.ContainsKey(bot.Slot))
            .Take(target).ToList();
        // The fill expects one sniper in each group of five, including any bots we keep.
        if (keep.Count(bot => FindBotProfile(bot)?.Role == "sniper") != keep.Count / 5)
            keep.Clear();

        var keptSlots = keep.Select(bot => bot.Slot).ToHashSet();
        foreach (var slot in managedBotSlots.Keys.Where(slot => !keptSlots.Contains(slot)).ToArray())
            managedBotSlots.Remove(slot);
        var keptNames = managedBotSlots.Values.ToHashSet(StringComparer.Ordinal);
        foreach (var name in requestedBots.Keys.Where(name => !keptNames.Contains(name)).ToArray())
            requestedBots.Remove(name);

        Server.ExecuteCommand($"{BotTeamSettings}; bot_quota_mode normal; bot_join_team {TeamLabel(team).ToLowerInvariant()}");
        SyncBotQuota();
        foreach (var bot in bots)
        {
            if (!keptSlots.Contains(bot.Slot) && bot.IsValid)
                bot.Disconnect(NetworkDisconnectionReason.NETWORK_DISCONNECT_KICKED);
        }
        return bots.Length != keep.Count;
    }

    private void CheckBotFill(CsTeam team, int target, HashSet<string> rejectedNames, BotSpawn? expected = null)
    {
        if (!CanSpawnBots())
        {
            botFillTimer = AddTimer(BotFillInterval, () => CheckBotFill(team, target, rejectedNames, expected), TimerFlags.STOP_ON_MAPCHANGE);
            return;
        }

        botFillTimer = null;
        var clients = Utilities.GetPlayers();
        var bots = clients.Where(client => client.IsBot && !client.IsHLTV).ToArray();
        var teamLabel = TeamLabel(team);
        var isT = team == CsTeam.Terrorist;
        var rules = Utilities.GetGameRules();
        var teamHumans = clients.Count(client => !client.IsBot && client.TeamNum == (byte)team);
        var occupiedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var client in clients.Where(client => client.IsValid))
        {
            occupiedNames.Add(client.PlayerName);
            if (IsReady(client))
                occupiedNames.Add(readyPlayers[client.Slot].OriginalName);
        }

        foreach (var bot in bots.Where(bot => bot.IsValid))
        {
            if (!TrackRequestedBot(bot))
                RejectUnrequestedBot(bot);
            if (unexpectedBotBurst >= UnexpectedBotBurstLimit)
                return;
        }

        bots = bots.Where(bot => bot.IsValid && managedBotSlots.ContainsKey(bot.Slot)).ToArray();
        var teamCount = bots.Count(bot => bot.TeamNum == (byte)team);
        if (expected is not null)
        {
            var spawned = bots.FirstOrDefault(bot => managedBotSlots[bot.Slot] == expected.SpawnName);
            if (spawned is null)
            {
                requestedBots.Remove(expected.SpawnName);
                rejectedNames.Add(expected.SpawnName);
                SyncBotQuota();
                Console.WriteLine($"[RepServer] CS2 did not add {expected.Profile.Name} to {teamLabel} using {expected.SpawnName}.");
            }
            else if (spawned.TeamNum != (byte)team)
            {
                rejectedNames.Add(expected.SpawnName);
                Console.WriteLine($"[RepServer] CS2 added {expected.Profile.Name} to the wrong team using {expected.SpawnName}.");
                RejectUnrequestedBot(spawned);
                if (unexpectedBotBurst < UnexpectedBotBurstLimit)
                {
                    // Check the roster again after the wrong-team bot disconnects.
                    botFillTimer = AddTimer(BotFillInterval, () => CheckBotFill(team, target, rejectedNames), TimerFlags.STOP_ON_MAPCHANGE);
                }
                return;
            }
        }
        if (teamCount >= target)
        {
            ReportBotFill($"{teamLabel} now has {teamCount} bots from the roster.");
            return;
        }
        var occupiedSlots = clients.Count(client => client.IsValid && (!client.IsBot || client.IsHLTV)) + bots.Length;
        if (occupiedSlots >= PlayerLimit - ReservedPlayerSlots)
        {
            ReportBotFill($"One slot is reserved. {teamLabel} has {teamCount} of {target} bots.");
            return;
        }

        var actualSnipers = bots.Count(bot => FindBotProfile(bot)?.Role == "sniper");
        var requiredSnipers = (bots.Length + 1) / 5;
        if (actualSnipers > requiredSnipers || actualSnipers + 1 < requiredSnipers)
        {
            ReportBotFill("The current roster does not match the sniper ratio. Run !kick_bot and fill again.");
            return;
        }

        var role = actualSnipers < requiredSnipers ? "sniper" : "rifle";
        occupiedNames.UnionWith(requestedBots.Keys);
        occupiedNames.UnionWith(rejectedNames);
        var inUse = requestedBots.Values.Select(profile => profile.Name).ToHashSet(StringComparer.Ordinal);
        var next = RandomizedBots(role, inUse)
            .SelectMany(profile => new[] { new BotSpawn(profile, profile.Name), new BotSpawn(profile, profile.SpawnAlias) })
            .FirstOrDefault(candidate => !occupiedNames.Contains(candidate.SpawnName));
        if (next is null)
        {
            if (rejectedNames.Count > 0 && rules is not null)
            {
                var teamLimit = isT ? rules.MaxNumTerrorists : rules.MaxNumCTs;
                var spawnCount = isT ? rules.SpawnPointCount_Terrorist : rules.SpawnPointCount_CT;
                Console.WriteLine($"[RepServer] {Server.MapName}: {teamLabel} {teamHumans + teamCount}/{teamLimit}, {spawnCount} spawn points, server {clients.Count}/{PlayerLimit} slots.");
            }
            ReportBotFill($"No available {role} names remain. {teamLabel} has {teamCount} of {target} bots.");
            return;
        }

        if (rules is not null)
        {
            // Only raise the team cap to a count the map can spawn.
            var spawnCount = isT ? rules.SpawnPointCount_Terrorist : rules.SpawnPointCount_CT;
            var requiredTeamSlots = Math.Min(target + teamHumans, Math.Min(spawnCount, PlayerLimit));
            ref var teamLimit = ref (isT ? ref rules.MaxNumTerrorists : ref rules.MaxNumCTs);
            teamLimit = Math.Max(teamLimit, requiredTeamSlots);
        }

        var botCommand = isT ? "bot_add_t" : "bot_add_ct";
        requestedBots.Add(next.SpawnName, next.Profile);
        botFillTimer = AddTimer(BotFillInterval, () => CheckBotFill(team, target, rejectedNames, next), TimerFlags.STOP_ON_MAPCHANGE);
        if (expected is null)
            Server.ExecuteCommand($"{BotTeamSettings}; bot_quota_mode normal; bot_join_team {teamLabel.ToLowerInvariant()}");

        // A named add can change the engine's quota. Pin it in the same command batch
        // so the quota manager sees only one new slot before the bot's join callback.
        var currentQuota = managedBotSlots.Count;
        Server.ExecuteCommand(FormattableString.Invariant(
            $"bot_quota {currentQuota}; {botCommand} \"{next.SpawnName}\"; bot_quota {currentQuota + 1}"));
    }

    private void CancelBotFill()
    {
        botFillTimer?.Kill();
        botFillTimer = null;
        botFillRequesterSlot = null;
    }

    private bool CanAddBots(CCSPlayerController? player, Action<string> reply)
    {
        if (!CheckAdmin(player, reply))
            return false;
        if (botFillTimer is null)
            return true;

        reply("[RepServer] A bot fill is already running. Use !kick_bot to cancel it and remove all bots.");
        return false;
    }

    private void ReportBotFill(string message)
    {
        botFillRequesterSlot = null;
        Console.WriteLine("[RepServer] " + message);
        Server.PrintToChatAll("[RepServer] " + message);
        if (duoMode is not null)
            QueueReadyCheck();
    }

    private void StartMatch(CCSPlayerController? player, Action<string> reply)
    {
        if (!CheckAdmin(player, reply))
            return;

        if (!IsCompetitiveMode)
        {
            reply("[RepServer] Use !live in a competitive mode.");
            return;
        }

        if (DuoRosterIssue() is { } issue)
        {
            reply("[RepServer] " + issue);
            return;
        }

        const string action = "mp_warmup_end";
        const string message = "Ending warmup and starting the match.";
        if (player is null)
            reply("[RepServer] " + message);
        AnnounceCommand(player, action, message);
        Server.NextWorldUpdate(() =>
        {
            ResetReadyPlayers();
            Server.ExecuteCommand(action);
        });
    }

    private static HookResult StripNonPistols(CCSPlayerController? player)
    {
        if (player is { IsValid: true, IsHLTV: false })
        {
            player.RemoveItemBySlot(gear_slot_t.GEAR_SLOT_RIFLE);
            player.RemoveItemBySlot(gear_slot_t.GEAR_SLOT_GRENADES);
            player.RemoveItemByDesignerName("weapon_taser");
        }

        return HookResult.Continue;
    }

    private void ChangeMap(CCSPlayerController? player, string[] args, Action<string> reply) =>
        SelectMap(player, reply, args.Length == 1 ? args[0] : "");

    private void SelectMap(CCSPlayerController? player, Action<string> reply, string map)
    {
        map = map.ToLowerInvariant();
        if (workshopMaps.TryGetValue(map, out var item))
        {
            Execute(player, reply, $"host_workshop_map {item.Id.ToString(CultureInfo.InvariantCulture)}",
                $"Loading Workshop map {item.Alias}.");
            return;
        }

        if (!maps.Contains(map, StringComparer.Ordinal))
        {
            reply("[RepServer] Choose a map from !maps.");
            return;
        }

        if (CheckAdmin(player, reply))
            Execute(player, reply, $"changelevel {map}", $"Changing map to {map}.");
    }

    private static void RunMatchCommand(CCSPlayerController? player, Action<string> reply, string action, string message)
    {
        if (!CheckAdmin(player, reply))
            return;

        Execute(player, reply, action, message);
    }

    private static void Execute(CCSPlayerController? player, Action<string> reply, string action, string message)
    {
        if (player is null)
            reply("[RepServer] " + message);
        AnnounceCommand(player, action, message);
        Server.ExecuteCommand(action);
    }

    private static void AnnounceCommand(CCSPlayerController? player, string action, string message)
    {
        var actor = player?.AuthorizedSteamID?.SteamId64.ToString(CultureInfo.InvariantCulture) ?? "console";
        Console.WriteLine($"[RepServer] {actor}: {action}");
        Server.PrintToChatAll("[RepServer] " + message);
    }

    private static bool CheckAdmin(CCSPlayerController? player, Action<string> reply)
    {
        if (IsAdmin(player))
            return true;

        reply("[RepServer] This command requires admin access.");
        return false;
    }

    private static bool IsAdmin(CCSPlayerController? player)
    {
        if (player is null)
            return true;

        if (!player.IsValid || player.IsBot || player.AuthorizedSteamID is not { } steamId)
            return false;

        var id = steamId.SteamId64.ToString(CultureInfo.InvariantCulture);
        try
        {
            return File.ReadLines(AdminFile).Any(line => line.Trim() == id);
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (IOException exception)
        {
            Console.Error.WriteLine($"[RepServer] Could not read the admin list: {exception.GetType().Name}");
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            Console.Error.WriteLine("[RepServer] Permission denied while reading the admin list.");
            return false;
        }
    }
}
