using System.Globalization;
using System.Text.RegularExpressions;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;

namespace RepServer;

public sealed class RepServerPlugin : BasePlugin
{
    public override string ModuleName => "RepServer";
    public override string ModuleVersion => "0.2.1";

    private const string AdminFile = "/var/lib/repserver/admins.txt";
    private const string ModeFile = "/var/lib/repserver/mode";
    private const string LastMapFile = "/var/lib/repserver/last-map";
    private const string Modes = "bot-game, bot-dm, bot-dm-pistol";
    private static readonly Regex MapName = new(@"\A[a-z0-9_]{1,64}\z");
    private static readonly string[] HelpLines =
    [
        "[RepServer] Kommandon: !help, !maps, !status, !modes",
        "[RepServer] Admin: !map <karta>, !restart / !omstart, !pause, !unpause",
        "[RepServer] Admin: !change-mode <läge> (eller !mode), !live (bot-game)"
    ];
    private string[] maps = [];
    private string activeMode = "bot-game";
    private string? pendingMode;

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

        RegisterListener<Listeners.OnMapStart>(RememberMap);
        if (hotReload)
            RememberMap(Server.MapName);

        RegisterEventHandler<EventPlayerConnectFull>((@event, _) =>
        {
            if (@event.Userid is { IsValid: true, IsBot: false, IsHLTV: false } player)
            {
                player.PrintToChat($"[RepServer] Läge: {ModeLabel(activeMode)} | karta: {Server.MapName}");
                foreach (var line in HelpLines)
                    player.PrintToChat(line);
            }

            return HookResult.Continue;
        });
        AddCommand("css_help", "Visa RepServers kommandon", (_, command) =>
        {
            foreach (var line in HelpLines)
                command.ReplyToCommand(line);
        });
        AddCommand("css_maps", "Visa tillåtna kartor", (_, command) =>
            command.ReplyToCommand("[RepServer] " + string.Join(", ", maps)));
        AddCommand("css_status", "Visa pluginversion och aktuell karta", (_, command) =>
            command.ReplyToCommand($"[RepServer] v{ModuleVersion} | karta: {Server.MapName} | läge: {ModeLabel(activeMode)}"));
        AddCommand("css_modes", "Visa spellägen", (_, command) =>
            command.ReplyToCommand($"[RepServer] {Modes}. Använd bot-game t för att spela som T."));
        AddCommand("css_change-mode", "Byt spelläge", ChangeMode);
        AddCommand("css_mode", "Byt spelläge", ChangeMode);
        AddCommand("css_map", "Byt karta", ChangeMap);
        AddCommand("css_restart", "Starta om matchen", (player, command) =>
            RunMatchCommand(player, command, "mp_restartgame 1", "Startar om matchen."));
        AddCommand("css_omstart", "Starta om matchen", (player, command) =>
            RunMatchCommand(player, command, "mp_restartgame 1", "Startar om matchen."));
        AddCommand("css_pause", "Pausa matchen", (player, command) =>
            RunMatchCommand(player, command, "mp_pause_match", "Begär matchpaus."));
        AddCommand("css_unpause", "Återuppta matchen", (player, command) =>
            RunMatchCommand(player, command, "mp_unpause_match", "Begär att matchen återupptas."));
        AddCommand("css_live", "Avsluta uppvärmningen i bot-game", StartMatch);
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
        var actor = player?.AuthorizedSteamID?.SteamId64.ToString(CultureInfo.InvariantCulture) ?? "console";
        Console.WriteLine($"[RepServer] {actor}: {action}");
        Server.PrintToChatAll("[RepServer] " + message);
        Server.ExecuteCommand(action);
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
