using System.Globalization;
using System.Text.RegularExpressions;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace RepServer;

public sealed class RepServerPlugin : BasePlugin
{
    public override string ModuleName => "RepServer";
    public override string ModuleVersion => "0.2.5";

    private const string AdminFile = "/var/lib/repserver/admins.txt";
    private const string ModeFile = "/var/lib/repserver/mode";
    private const string LastMapFile = "/var/lib/repserver/last-map";
    private const string Modes = "bot-game, bot-dm, bot-dm-pistol";
    private const string BotTeamSettings = "bot_auto_vacate 1; bot_join_team any; mp_autoteambalance 0; mp_limitteams 0";
    private const float BotFillInterval = 2.0f;
    private const int ReservedPlayerSlots = 1;
    private static readonly Regex MapName = new(@"\A[a-z0-9_]{1,64}\z");
    private string[] maps = [];
    private string activeMode = "bot-game";
    private string? pendingMode;
    private CounterStrikeSharp.API.Modules.Timers.Timer? botFillTimer;
    private int? botFillRequesterSlot;

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

        maps = File.ReadLines("/opt/repserver/maps.txt")
            .Select(line => line.Trim())
            .Where(line => MapName.IsMatch(line))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        RegisterListener<Listeners.OnMapStart>(map =>
        {
            botFillTimer = null;
            botFillRequesterSlot = null;
            RememberMap(map);
        });
        RegisterListener<Listeners.OnClientDisconnect>(slot =>
        {
            if (botFillRequesterSlot == slot)
            {
                CancelBotFill();
                Console.WriteLine("[RepServer] Botpåfyllningen avbröts eftersom admin kopplades bort.");
            }
        });
        if (hotReload)
            RememberMap(Server.MapName);

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
        RegisterChatCommand("maps", "Visa tillåtna kartor", (_, command) =>
            command.ReplyToCommand("[RepServer] " + string.Join(", ", maps)));
        RegisterChatCommand("status", "Visa pluginversion, karta och spelläge", (_, command) =>
            command.ReplyToCommand($"[RepServer] v{ModuleVersion} | karta: {Server.MapName} | läge: {ModeLabel(activeMode)}"));
        RegisterChatCommand("modes", "Visa spellägen", (_, command) =>
            command.ReplyToCommand($"[RepServer] {Modes}. Använd bot-game t för att spela som T."));
        RegisterChatCommand("change-mode", "Byt spelläge", ChangeMode, admin: true, arguments: "<läge>");
        RegisterChatCommand("mode", "Kortform av !change-mode", ChangeMode, admin: true, arguments: "<läge>");
        RegisterChatCommand("map", "Byt karta", ChangeMap, admin: true, arguments: "<karta>");
        RegisterChatCommand("restart", "Starta om matchen", (player, command) =>
            RunMatchCommand(player, command, "mp_restartgame 1", "Startar om matchen."), admin: true);
        RegisterChatCommand("omstart", "Samma som !restart", (player, command) =>
            RunMatchCommand(player, command, "mp_restartgame 1", "Startar om matchen."), admin: true);
        RegisterChatCommand("pause", "Pausa matchen", (player, command) =>
            RunMatchCommand(player, command, "mp_pause_match", "Begär matchpaus."), admin: true);
        RegisterChatCommand("unpause", "Återuppta matchen", (player, command) =>
            RunMatchCommand(player, command, "mp_unpause_match", "Begär att matchen återupptas."), admin: true);
        RegisterChatCommand("kick_bot", "Ta bort alla bots och stäng av påfyllning", KickBots, admin: true);
        RegisterChatCommand("add_bot_t", "Lägg till en bot på T", (player, command) =>
            AddBot(player, command, "bot_add_t", "T"), admin: true);
        RegisterChatCommand("add_bot_ct", "Lägg till en bot på CT", (player, command) =>
            AddBot(player, command, "bot_add_ct", "CT"), admin: true);
        RegisterChatCommand("set_bot_t", "Fyll på T upp till valt antal bots", (player, command) =>
            SetBots(player, command, CsTeam.Terrorist), admin: true, arguments: "<antal>");
        RegisterChatCommand("set_bot_ct", "Fyll på CT upp till valt antal bots", (player, command) =>
            SetBots(player, command, CsTeam.CounterTerrorist), admin: true, arguments: "<antal>");
        RegisterChatCommand("live", "Avsluta uppvärmningen i bot-game", StartMatch, admin: true);

        var joinLines = commands.GroupBy(entry => entry.Admin)
            .SelectMany(group => group.Select(entry => entry.Name).Chunk(5)
                .Select(chunk => $"[RepServer] {(group.Key ? "Admin" : "Kommandon")}: {string.Join(", ", chunk)}"))
            .ToArray();

        RegisterEventHandler<EventPlayerConnectFull>((@event, _) =>
        {
            if (@event.Userid is { IsValid: true, IsBot: false, IsHLTV: false } player)
            {
                player.PrintToChat($"[RepServer] Läge: {ModeLabel(activeMode)} | karta: {Server.MapName}");
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
        if (!CheckAdmin(player, command))
            return;

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

        if (requested is not ("bot-game" or "bot-game-t" or "bot-dm" or "bot-dm-pistol"))
        {
            command.ReplyToCommand($"[RepServer] !mode <läge>. Välj {Modes}. I bot-game kan du lägga till t eller ct.");
            return;
        }
        if (pendingMode is not null)
        {
            command.ReplyToCommand($"[RepServer] Byte till {ModeLabel(pendingMode)} pågår redan.");
            return;
        }
        if (requested == activeMode)
        {
            command.ReplyToCommand($"[RepServer] {ModeLabel(activeMode)} är redan aktivt.");
            return;
        }

        if (!TrySaveState(ModeFile, requested))
        {
            command.ReplyToCommand("[RepServer] Läget kunde inte sparas. Kontrollera data/state på servern.");
            return;
        }

        pendingMode = requested;
        var message = $"Byter till {ModeLabel(requested)}. Servern startar om om tre sekunder. Anslut igen när den är uppe.";
        if (player is null)
            command.ReplyToCommand("[RepServer] " + message);
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

    private void KickBots(CCSPlayerController? player, CommandInfo command)
    {
        if (!CheckAdmin(player, command))
            return;

        CancelBotFill();
        const string action = "bot_quota_mode normal; bot_kick all; bot_quota 0";
        AnnounceCommand(player, action, "Tar bort alla bots och stänger av automatisk påfyllning för kartan.");
        Server.NextWorldUpdate(() => Server.ExecuteCommand(action));
    }

    private void AddBot(CCSPlayerController? player, CommandInfo command, string botCommand, string team)
    {
        if (!CanAddBots(player, command))
            return;

        var clients = Utilities.GetPlayers();
        if (clients.Count >= PlayerLimit - ReservedPlayerSlots)
        {
            command.ReplyToCommand("[RepServer] Botgränsen är nådd. En plats hålls ledig för spelare.");
            return;
        }

        AnnounceCommand(player, botCommand, $"Begär en bot på {team}.");
        Server.NextWorldUpdate(() =>
        {
            var connected = Utilities.GetPlayers();
            if (botFillTimer is null && connected.Count < PlayerLimit - ReservedPlayerSlots)
                Server.ExecuteCommand(ManualBotCommand(botCommand, connected.Count(client => client.IsBot && !client.IsHLTV)));
        });
    }

    private void SetBots(CCSPlayerController? player, CommandInfo command, CsTeam team)
    {
        if (!CanAddBots(player, command))
            return;

        if (command.ArgCount != 2 ||
            !int.TryParse(command.ArgByIndex(1), NumberStyles.None, CultureInfo.InvariantCulture, out var target) ||
            target < 1 || target > PlayerLimit)
        {
            command.ReplyToCommand($"[RepServer] Ange ett heltal mellan 1 och {PlayerLimit}.");
            return;
        }

        var teamLabel = team == CsTeam.Terrorist ? "T" : "CT";
        var clients = Utilities.GetPlayers();
        var current = clients.Count(client => client.IsBot && client.TeamNum == (byte)team);
        if (current >= target)
        {
            command.ReplyToCommand($"[RepServer] {teamLabel} har redan {current} bots. Inga bots har tagits bort.");
            return;
        }
        if (clients.Count >= PlayerLimit - ReservedPlayerSlots)
        {
            command.ReplyToCommand($"[RepServer] Botgränsen är nådd. {teamLabel} har {current} av {target} bots. Använd !kick_bot för att börja om.");
            return;
        }

        var botCommand = team == CsTeam.Terrorist ? "bot_add_t" : "bot_add_ct";
        AnnounceCommand(player, $"{botCommand} (mål: {target})", $"Försöker fylla {teamLabel} till {target} bots.");
        botFillRequesterSlot = player?.Slot;

        // Bot creation can stall a game tick. Keep it outside the client's chat packet handler.
        botFillTimer = AddTimer(0.1f, () => CheckBotFill(team, teamLabel, botCommand, target, previous: null), TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void CheckBotFill(CsTeam team, string teamLabel, string botCommand, int target, int? previous)
    {
        botFillTimer = null;
        var clients = Utilities.GetPlayers();
        var count = clients.Count(client => client.IsBot && client.TeamNum == (byte)team);
        if (count >= target)
            ReportBotFill($"{teamLabel} har nu {count} bots.");
        else if (clients.Count >= PlayerLimit - ReservedPlayerSlots)
            ReportBotFill($"En plats hålls ledig för spelare. {teamLabel} har {count} av {target} bots.");
        else if (previous is int previousCount && count <= previousCount)
            ReportBotFill($"Ingen ytterligare bot kunde läggas till. {teamLabel} har {count} av {target} bots.");
        else
        {
            botFillTimer = AddTimer(BotFillInterval, () => CheckBotFill(team, teamLabel, botCommand, target, count), TimerFlags.STOP_ON_MAPCHANGE);
            Server.ExecuteCommand(previous is null
                ? ManualBotCommand(botCommand, clients.Count(client => client.IsBot && !client.IsHLTV))
                : botCommand);
        }
    }

    private static string ManualBotCommand(string botCommand, int currentBots) =>
        $"{BotTeamSettings}; bot_quota_mode normal; bot_quota {currentBots.ToString(CultureInfo.InvariantCulture)}; {botCommand}";

    private void CancelBotFill()
    {
        botFillTimer?.Kill();
        botFillTimer = null;
        botFillRequesterSlot = null;
    }

    private bool CanAddBots(CCSPlayerController? player, CommandInfo command)
    {
        if (!CheckAdmin(player, command))
            return false;
        if (botFillTimer is null)
            return true;

        command.ReplyToCommand("[RepServer] En botpåfyllning pågår redan. !kick_bot avbryter och tar bort alla bots.");
        return false;
    }

    private void ReportBotFill(string message)
    {
        botFillRequesterSlot = null;
        Console.WriteLine("[RepServer] " + message);
        Server.PrintToChatAll("[RepServer] " + message);
    }

    private void StartMatch(CCSPlayerController? player, CommandInfo command)
    {
        if (!CheckAdmin(player, command))
            return;

        if (activeMode is not ("bot-game" or "bot-game-t"))
        {
            command.ReplyToCommand("[RepServer] !live används i bot-game.");
            return;
        }

        Execute(player, "mp_warmup_end", "Avslutar uppvärmningen och startar matchen.");
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

    private void ChangeMap(CCSPlayerController? player, CommandInfo command)
    {
        if (!CheckAdmin(player, command))
            return;

        var map = command.ArgCount == 2 ? command.ArgByIndex(1) : "";
        if (!maps.Contains(map, StringComparer.Ordinal))
        {
            command.ReplyToCommand("[RepServer] Ange en karta från !maps.");
            return;
        }

        Execute(player, $"changelevel {map}", $"Byter karta till {map}.");
    }

    private static void RunMatchCommand(CCSPlayerController? player, CommandInfo command, string action, string message)
    {
        if (!CheckAdmin(player, command))
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

    private static bool CheckAdmin(CCSPlayerController? player, CommandInfo command)
    {
        if (IsAdmin(player))
            return true;

        command.ReplyToCommand("[RepServer] Kommandot kräver adminbehörighet.");
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
