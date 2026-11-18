using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.ValveConstants.Protobuf;

namespace RepServer;

public sealed partial class RepServerPlugin : BasePlugin
{
    public override string ModuleName => "RepServer";
    public override string ModuleVersion => "0.4.7";

    private const string AdminFile = "/var/lib/repserver/admins.txt";
    private const string ModeFile = "/var/lib/repserver/mode";
    private const string LastMapFile = "/var/lib/repserver/last-map";
    private const string Modes = "bot-game, bot-dm, bot-dm-pistol";
    private const string BotTeamSettings = "bot_auto_vacate 1; mp_autoteambalance 0; mp_limitteams 0";
    private const int InitialBotCount = 10;
    private const float BotFillInterval = 2.0f;
    private const float JoinGracePeriod = 5.0f;
    private const int ReservedPlayerSlots = 1;
    private static readonly Regex MapName = new(@"\A[a-z0-9_]{1,64}\z");
    private string[] maps = [];
    private sealed record BotProfile(string Name, string Role);
    private readonly List<BotProfile> botProfiles = new();
    private readonly HashSet<string> priorityBotNames = new(StringComparer.OrdinalIgnoreCase);
    private string activeMode = "bot-game";
    private string? pendingMode;
    private CounterStrikeSharp.API.Modules.Timers.Timer? botFillTimer;
    private int? botFillRequesterSlot;
    private bool autoRosterPending = true;
    private readonly HashSet<int> connectingClients = new();
    private readonly HashSet<string> requestedBots = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string> managedBotSlots = new();
    private double botSpawnReadyAt;

    private static int PlayerLimit =>
        int.TryParse(Environment.GetEnvironmentVariable("CS2_MAXPLAYERS"), out var configured) && configured > 0
            ? Math.Min(configured, Server.MaxPlayers)
            : Server.MaxPlayers;

    public override void Load(bool hotReload)
    {
        activeMode = Environment.GetEnvironmentVariable("REPSERVER_ACTIVE_MODE") ?? "bot-game";
        if (activeMode == "bot-dm-pistol")
        {
            RegisterEventHandler<EventPlayerSpawn>((@event, _) => StripNonPistols(@event.Userid));
            RegisterEventHandler<EventItemPickup>((@event, _) => StripNonPistols(@event.Userid));
        }
        else if (activeMode is "bot-game" or "bot-game-t")
        {
            RegisterEventHandler<EventPlayerSpawn>((@event, _) =>
            {
                if (@event.Userid is { IsValid: true, IsHLTV: false } player &&
                    player.PlayerPawn.Value is { IsValid: true })
                {
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

        RegisterListener<Listeners.OnMapStart>(map =>
        {
            botFillTimer = null;
            botFillRequesterSlot = null;
            requestedBots.Clear();
            managedBotSlots.Clear();
            autoRosterPending = true;
            botSpawnReadyAt = Server.EngineTime + JoinGracePeriod;
            ResetReadyPlayers();
            RememberMap(map);
        });
        RegisterListener<Listeners.OnMapEnd>(ResetReadyPlayers);
        RegisterListener<Listeners.OnClientConnected>(slot => connectingClients.Add(slot));
        RegisterListener<Listeners.OnClientDisconnect>(slot =>
        {
            connectingClients.Remove(slot);
            ClearReadyPlayer(slot);
            QueueReadyCheck();
            if (managedBotSlots.Remove(slot, out var botName))
                requestedBots.Remove(botName);
            if (botFillRequesterSlot == slot)
            {
                CancelBotFill();
                Console.WriteLine("[RepServer] Botpåfyllningen avbröts eftersom spelaren kopplades bort.");
            }
        });
        if (hotReload)
        {
            autoRosterPending = false;
            RememberMap(Server.MapName);
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
            if (client.IsBot && !client.IsHLTV)
            {
                if (requestedBots.Contains(client.PlayerName) && !managedBotSlots.ContainsValue(client.PlayerName))
                    managedBotSlots[slot] = client.PlayerName;
                else if (managedBotSlots.GetValueOrDefault(slot) != client.PlayerName)
                    RejectUnrequestedBot(client);
            }
        }));
        if (activeMode != "bot-dm-pistol")
            RegisterEventHandler<EventItemPickup>(RestrictSniperWeapons);

        RegisterMenuListeners();
        RegisterEventHandler<EventPlayerConnect>((connection, info) =>
        {
            if (connection.Bot)
                info.DontBroadcast = true;
            return HookResult.Continue;
        }, HookMode.Pre);
        RegisterEventHandler<EventPlayerTeam>((change, _) =>
        {
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
            if (readyPlayers.Count != 0)
                IsReadinessWarmup();
            return HookResult.Continue;
        });

        var commands = new List<(string Name, string Syntax, string Description, bool Admin)>();

        void RegisterChatCommand(string name, string description, Action<CCSPlayerController?, CommandInfo> handler,
            bool admin = false, string arguments = "")
        {
            var syntax = arguments.Length == 0 ? $"!{name}" : $"!{name} {arguments}";
            commands.Add(($"!{name}", syntax, description, admin));
            AddCommand($"css_{name}", description, (player, command) => handler(player, command));
        }

        RegisterChatCommand("help", "Visa alla kommandon", (_, command) =>
        {
            foreach (var entry in commands)
            {
                var access = entry.Admin ? " (admin)" : "";
                command.ReplyToCommand($"[RepServer] {entry.Syntax}{access} – {entry.Description}");
            }
        });
        RegisterChatCommand("menu", "Öppna RepServer-menyn", (player, command) =>
        {
            if (player is { IsValid: true, IsBot: false, IsHLTV: false })
                QueueOpenMenu(player);
            else
                command.ReplyToCommand("[RepServer] Öppna menyn i spelet med !.");
        });
        RegisterChatCommand("maps", "Visa tillåtna kartor", (player, command) =>
        {
            var choices = MapChoices(IsAdmin(player)).ToArray();
            if (choices.Length == 0)
                command.ReplyToCommand("[RepServer] Lägg till en Workshop-karta med !add_map <workshoplänk>.");
            foreach (var group in choices.Chunk(6))
                command.ReplyToCommand("[RepServer] " + string.Join(", ", group));
        });
        RegisterChatCommand("status", "Visa pluginversion, karta och spelläge", (_, command) =>
            command.ReplyToCommand($"[RepServer] v{ModuleVersion} | karta: {Server.MapName} | läge: {ModeLabel(activeMode)}"));
        RegisterChatCommand("modes", "Visa spellägen", (_, command) =>
            command.ReplyToCommand($"[RepServer] {Modes}. Använd bot-game t för att spela som T."));
        RegisterChatCommand("ready", "Markera dig som redo under uppvärmningen", (player, command) =>
            SetReady(player, command.ReplyToCommand, true));
        RegisterChatCommand("unready", "Ta tillbaka din ready-markering", (player, command) =>
            SetReady(player, command.ReplyToCommand, false));
        RegisterChatCommand("readystatus", "Visa vilka spelare som är redo", (_, command) =>
            ShowReadyStatus(command.ReplyToCommand));
        RegisterChatCommand("change-mode", "Byt spelläge", ChangeMode, admin: true, arguments: "<läge>");
        RegisterChatCommand("mode", "Kortform av !change-mode", ChangeMode, admin: true, arguments: "<läge>");
        RegisterChatCommand("map", "Byt karta (standardkartor kräver admin)", ChangeMap, arguments: "<namn>");
        RegisterChatCommand("add_map", "Spara en Workshop-karta med valfritt eget namn", AddWorkshopMap,
            arguments: "<workshoplänk> [namn]");
        RegisterChatCommand("restart", "Starta om matchen", (player, command) =>
            RunMatchCommand(player, command.ReplyToCommand, "mp_restartgame 1", "Startar om matchen."), admin: true);
        RegisterChatCommand("omstart", "Samma som !restart", (player, command) =>
            RunMatchCommand(player, command.ReplyToCommand, "mp_restartgame 1", "Startar om matchen."), admin: true);
        RegisterChatCommand("pause", "Pausa matchen", (player, command) =>
            RunMatchCommand(player, command.ReplyToCommand, "mp_pause_match", "Begär matchpaus."), admin: true);
        RegisterChatCommand("unpause", "Återuppta matchen", (player, command) =>
            RunMatchCommand(player, command.ReplyToCommand, "mp_unpause_match", "Begär att matchen återupptas."), admin: true);
        RegisterChatCommand("kick_bot", "Ta bort alla bots och stäng av påfyllning", (player, command) => KickBots(player, command.ReplyToCommand), admin: true);
        RegisterChatCommand("add_bot_t", "Lägg till en bot på T", (player, command) =>
            AddBot(player, command.ReplyToCommand, CsTeam.Terrorist), admin: true);
        RegisterChatCommand("add_bot_ct", "Lägg till en bot på CT", (player, command) =>
            AddBot(player, command.ReplyToCommand, CsTeam.CounterTerrorist), admin: true);
        RegisterChatCommand("set_bot_t", "Fyll på T upp till valt antal bots", (player, command) =>
            SetBotsFromCommand(player, command, CsTeam.Terrorist), admin: true, arguments: "<antal>");
        RegisterChatCommand("set_bot_ct", "Fyll på CT upp till valt antal bots", (player, command) =>
            SetBotsFromCommand(player, command, CsTeam.CounterTerrorist), admin: true, arguments: "<antal>");
        RegisterChatCommand("bot_roster", "Förhandsvisa botlistan med en sniper per fem", (_, command) =>
        {
            if (command.ArgCount != 2 ||
                !int.TryParse(command.ArgByIndex(1), NumberStyles.None, CultureInfo.InvariantCulture, out var target) ||
                target < 1 || target > PlayerLimit)
            {
                command.ReplyToCommand($"[RepServer] Använd !bot_roster <antal> (1–{PlayerLimit}).");
                return;
            }

            PreviewBotRoster(target, command.ReplyToCommand);
        }, arguments: "<antal>");
        RegisterChatCommand("live", "Avsluta uppvärmningen i bot-game", (player, command) => StartMatch(player, command.ReplyToCommand), admin: true);

        var joinLines = commands.GroupBy(entry => entry.Admin)
            .SelectMany(group => group.Select(entry => entry.Name).Chunk(5)
                .Select(chunk => $"[RepServer] {(group.Key ? "Admin" : "Kommandon")}: {string.Join(", ", chunk)}"))
            .ToArray();

        RegisterEventHandler<EventPlayerConnectFull>((@event, _) =>
        {
            if (@event.Userid is { IsValid: true } player)
            {
                connectingClients.Remove(player.Slot);
                QueueReadyCheck();
                if (player.IsBot || player.IsHLTV)
                    return HookResult.Continue;

                botSpawnReadyAt = Server.EngineTime + JoinGracePeriod;
                ScheduleInitialFill(player);
                player.PrintToChat($"[RepServer] Läge: {ModeLabel(activeMode)} | karta: {Server.MapName}");
                player.PrintToChat("[RepServer] Skriv ! för att öppna menyn.");
                foreach (var line in joinLines)
                    player.PrintToChat(line);
            }

            return HookResult.Continue;
        });
        Console.WriteLine($"[RepServer] v{ModuleVersion} laddad | läge: {ModeLabel(activeMode)}");
    }

    private static string ModeLabel(string mode) => mode switch
    {
        "bot-game" => "bot-game (bots på T)",
        "bot-game-t" => "bot-game (bots på CT)",
        _ => mode
    };

    private void ChangeMode(CCSPlayerController? player, CommandInfo command)
    {
        var requested = command.ArgCount == 2 ? command.ArgByIndex(1).ToLowerInvariant() : null;
        if (command.ArgCount == 3 && command.ArgByIndex(1).Equals("bot-game", StringComparison.OrdinalIgnoreCase))
        {
            requested = command.ArgByIndex(2).ToLowerInvariant() switch
            {
                "t" => "bot-game-t",
                "ct" => "bot-game",
                _ => null
            };
        }

        SelectMode(player, command.ReplyToCommand, requested);
    }

    private void SelectMode(CCSPlayerController? player, Action<string> reply, string? requested)
    {
        if (!CheckAdmin(player, reply))
            return;

        if (requested is not ("bot-game" or "bot-game-t" or "bot-dm" or "bot-dm-pistol"))
        {
            reply($"[RepServer] !mode <läge>. Välj {Modes}. I bot-game kan du lägga till t eller ct.");
            return;
        }
        if (pendingMode is not null)
        {
            reply($"[RepServer] Byte till {ModeLabel(pendingMode)} pågår redan.");
            return;
        }
        if (requested == activeMode)
        {
            reply($"[RepServer] {ModeLabel(activeMode)} är redan aktivt.");
            return;
        }

        if (!TrySaveState(ModeFile, requested))
        {
            reply("[RepServer] Läget kunde inte sparas. Kontrollera data/state på servern.");
            return;
        }

        pendingMode = requested;
        var message = $"Byter till {ModeLabel(requested)}. Servern startar om om tre sekunder. Anslut igen när den är uppe.";
        if (player is null)
            reply("[RepServer] " + message);
        Server.PrintToChatAll("[RepServer] " + message);
        AddTimer(3.0f, () => Server.ExecuteCommand("quit"));
    }

    private static void RememberMap(string? map)
    {
        if (!string.IsNullOrEmpty(map) && MapName.IsMatch(map))
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
            Console.Error.WriteLine($"[RepServer] Kunde inte spara {Path.GetFileName(path)}: {exception.GetType().Name}");
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
        Console.WriteLine("[RepServer] Tar bort alla bottar.");
        reply("[RepServer] Tar bort alla bottar.");
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

    private BotProfile? FindBotProfile(string name) =>
        botProfiles.FirstOrDefault(profile => string.Equals(profile.Name, name, StringComparison.Ordinal));

    private void RejectUnrequestedBot(CCSPlayerController bot)
    {
        Console.Error.WriteLine($"[RepServer] CS2 skapade botten {bot.PlayerName} utan en begäran från RepServer. Botten tas bort.");
        bot.Disconnect(NetworkDisconnectionReason.NETWORK_DISCONNECT_KICKED);
        SyncBotQuota();
    }

    private void SyncBotQuota() =>
        Server.ExecuteCommand($"bot_quota {requestedBots.Count.ToString(CultureInfo.InvariantCulture)}");

    private HookResult RestrictSniperWeapons(EventItemPickup pickup, GameEventInfo info)
    {
        if (pickup.Userid is { IsValid: true, IsBot: true, IsHLTV: false } bot &&
            FindBotProfile(bot.PlayerName)?.Role == "rifle")
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
                Console.Error.WriteLine($"[RepServer] Ogiltigt eller upprepat botnamn i bot-names.txt på rad {lineNumber}.");
                continue;
            }

            botProfiles.Add(new BotProfile(name, role));
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
        reply($"[RepServer] Förhandsvisning: {roster.Count} av {target} bottar, varav {sniperCount} snipers.");
        if (roster.Count < target)
        {
            var missingRole = (roster.Count + 1) % 5 == 0 ? "sniper" : "rifle";
            reply($"[RepServer] Nästa plats kräver ett ledigt namn med rollen {missingRole}.");
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
                Console.Error.WriteLine($"[RepServer] Prioriterat namn saknas i bot-names.txt: {name}");
        }
    }

    private Queue<BotProfile> RandomizedBots(string role, HashSet<string>? inUse = null)
    {
        var candidates = botProfiles.Where(profile => profile.Role == role && inUse?.Contains(profile.Name) != true).ToArray();
        Random.Shared.Shuffle(candidates.AsSpan());
        return new Queue<BotProfile>(candidates.OrderByDescending(profile => priorityBotNames.Contains(profile.Name)));
    }

    private void AddBot(CCSPlayerController? player, Action<string> reply, CsTeam team) =>
        SetBots(player, reply, team, CurrentBots().Count(bot => bot.TeamNum == (byte)team) + 1);

    private void SetBotsFromCommand(CCSPlayerController? player, CommandInfo command, CsTeam team)
    {
        var target = command.ArgCount == 2 &&
            int.TryParse(command.ArgByIndex(1), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed : 0;
        SetBots(player, command.ReplyToCommand, team, target);
    }

    private void SetBots(CCSPlayerController? player, Action<string> reply, CsTeam team, int target)
    {
        if (!CanAddBots(player, reply))
            return;
        if (target < 1 || target > PlayerLimit)
        {
            reply($"[RepServer] Ange ett heltal mellan 1 och {PlayerLimit}.");
            return;
        }

        var current = CurrentBots().Count(bot => bot.TeamNum == (byte)team);
        if (current >= target)
        {
            reply($"[RepServer] {BotTeamLabel(team)} har redan {current} bottar.");
            return;
        }

        autoRosterPending = false;
        StartBotFill(player, team, target);
    }

    private static string BotTeamLabel(CsTeam team) => team == CsTeam.Terrorist ? "T" : "CT";

    private void ScheduleInitialFill(CCSPlayerController player)
    {
        if (!autoRosterPending || botFillTimer is not null)
            return;

        autoRosterPending = false;
        botFillRequesterSlot = player.Slot;
        botFillTimer = AddTimer(JoinGracePeriod, StartInitialFillWhenReady, TimerFlags.STOP_ON_MAPCHANGE);
    }

    private bool CanSpawnBots() => connectingClients.Count == 0 && Server.EngineTime >= botSpawnReadyAt;

    private void StartInitialFillWhenReady()
    {
        if (!CanSpawnBots())
        {
            botFillTimer = AddTimer(BotFillInterval, StartInitialFillWhenReady, TimerFlags.STOP_ON_MAPCHANGE);
            return;
        }

        var requester = Utilities.GetPlayers().FirstOrDefault(client =>
            !client.IsBot && !client.IsHLTV && client.Slot == botFillRequesterSlot);
        if (requester is null)
        {
            botFillTimer = null;
            botFillRequesterSlot = null;
            return;
        }

        var team = activeMode == "bot-game-t" ? CsTeam.CounterTerrorist : CsTeam.Terrorist;
        StartBotFill(requester, team, InitialBotCount);
    }

    private void StartBotFill(CCSPlayerController? player, CsTeam team, int target)
    {
        var teamLabel = BotTeamLabel(team);
        Console.WriteLine($"[RepServer] Botlista: {teamLabel}, mål {target}.");
        botFillRequesterSlot = player?.Slot;
        // CS2 must finish the chat packet before it creates a bot.
        botFillTimer = AddTimer(0.1f, () => CheckBotFill(team, target, null), TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void CheckBotFill(CsTeam team, int target, string? expected)
    {
        if (!CanSpawnBots())
        {
            botFillTimer = AddTimer(BotFillInterval, () => CheckBotFill(team, target, expected), TimerFlags.STOP_ON_MAPCHANGE);
            return;
        }

        botFillTimer = null;
        var clients = Utilities.GetPlayers();
        var bots = clients.Where(client => client.IsBot && !client.IsHLTV).ToArray();
        var teamLabel = BotTeamLabel(team);
        var isT = team == CsTeam.Terrorist;
        var rules = Utilities.GetGameRules();
        var teamHumans = clients.Count(client => !client.IsBot && client.TeamNum == (byte)team);
        foreach (var bot in bots.Where(bot => !requestedBots.Contains(bot.PlayerName)))
        {
            if (bot.IsValid)
                RejectUnrequestedBot(bot);
        }

        bots = bots.Where(bot => bot.IsValid && requestedBots.Contains(bot.PlayerName)).ToArray();
        var teamCount = bots.Count(bot => bot.TeamNum == (byte)team);
        if (expected is not null && !bots.Any(bot => bot.PlayerName == expected && bot.TeamNum == (byte)team))
        {
            requestedBots.Remove(expected);
            var wrongTeam = bots.FirstOrDefault(bot => bot.PlayerName == expected);
            if (wrongTeam is not null)
                RejectUnrequestedBot(wrongTeam);
            else
                SyncBotQuota();
            if (rules is not null)
            {
                var teamLimit = isT ? rules.MaxNumTerrorists : rules.MaxNumCTs;
                var spawnCount = isT ? rules.SpawnPointCount_Terrorist : rules.SpawnPointCount_CT;
                Console.WriteLine($"[RepServer] {Server.MapName}: {teamLabel} {teamHumans + teamCount}/{teamLimit}, {spawnCount} spawnpunkter, server {clients.Count}/{PlayerLimit} platser.");
            }
            ReportBotFill($"CS2 lade inte till {expected} på {teamLabel}. {teamCount} av {target} bottar.");
            return;
        }
        if (teamCount >= target)
        {
            ReportBotFill($"{teamLabel} har nu {teamCount} bottar från listan.");
            return;
        }
        var occupiedSlots = clients.Count(client => client.IsValid && (!client.IsBot || client.IsHLTV)) + bots.Length;
        if (occupiedSlots >= PlayerLimit - ReservedPlayerSlots)
        {
            ReportBotFill($"En plats hålls ledig. {teamLabel} har {teamCount} av {target} bottar.");
            return;
        }

        var actualSnipers = bots.Count(bot => FindBotProfile(bot.PlayerName)?.Role == "sniper");
        var requiredSnipers = (bots.Length + 1) / 5;
        if (actualSnipers > requiredSnipers || actualSnipers + 1 < requiredSnipers)
        {
            ReportBotFill("Den nuvarande uppställningen bryter mot sniperregeln. Kör !kick_bot och fyll på igen.");
            return;
        }

        var role = actualSnipers < requiredSnipers ? "sniper" : "rifle";
        if (!RandomizedBots(role, requestedBots).TryDequeue(out var profile))
        {
            ReportBotFill($"Inget oanvänt {role}-namn kvar. {teamLabel} har {teamCount} av {target} bottar.");
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
        requestedBots.Add(profile.Name);
        botFillTimer = AddTimer(BotFillInterval, () => CheckBotFill(team, target, profile.Name), TimerFlags.STOP_ON_MAPCHANGE);
        if (expected is null)
            Server.ExecuteCommand($"{BotTeamSettings}; bot_quota_mode normal; bot_join_team {teamLabel.ToLowerInvariant()}");
        // Pin the quota in the same command batch before the bot manager can auto-fill.
        Server.ExecuteCommand($"{botCommand} \"{profile.Name}\"; bot_quota {requestedBots.Count.ToString(CultureInfo.InvariantCulture)}");
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

        reply("[RepServer] En botpåfyllning pågår redan. !kick_bot avbryter och tar bort alla bots.");
        return false;
    }

    private void ReportBotFill(string message)
    {
        botFillRequesterSlot = null;
        Console.WriteLine("[RepServer] " + message);
        Server.PrintToChatAll("[RepServer] " + message);
    }

    private void StartMatch(CCSPlayerController? player, Action<string> reply)
    {
        if (!CheckAdmin(player, reply))
            return;

        if (activeMode is not ("bot-game" or "bot-game-t"))
        {
            reply("[RepServer] !live används i bot-game.");
            return;
        }

        const string action = "mp_warmup_end";
        AnnounceCommand(player, action, "Avslutar uppvärmningen och startar matchen.");
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

    private void ChangeMap(CCSPlayerController? player, CommandInfo command) =>
        SelectMap(player, command.ReplyToCommand, command.ArgCount == 2 ? command.ArgByIndex(1) : "");

    private void SelectMap(CCSPlayerController? player, Action<string> reply, string map)
    {
        map = map.ToLowerInvariant();
        if (workshopMaps.TryGetValue(map, out var item))
        {
            Execute(player, $"host_workshop_map {item.Id.ToString(CultureInfo.InvariantCulture)}",
                $"Laddar Workshop-kartan {item.Alias}.");
            return;
        }

        if (!maps.Contains(map, StringComparer.Ordinal))
        {
            reply("[RepServer] Ange en karta från !maps.");
            return;
        }

        if (CheckAdmin(player, reply))
            Execute(player, $"changelevel {map}", $"Byter karta till {map}.");
    }

    private static void RunMatchCommand(CCSPlayerController? player, Action<string> reply, string action, string message)
    {
        if (!CheckAdmin(player, reply))
            return;

        Execute(player, action, message);
    }

    private static void Execute(CCSPlayerController? player, string action, string message)
    {
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

        reply("[RepServer] Kommandot kräver adminbehörighet.");
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
            Console.Error.WriteLine($"[RepServer] Kunde inte läsa adminlistan: {exception.GetType().Name}");
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            Console.Error.WriteLine("[RepServer] Saknar läsrättighet till adminlistan.");
            return false;
        }
    }
}
