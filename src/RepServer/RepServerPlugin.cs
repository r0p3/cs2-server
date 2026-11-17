using System.Globalization;
using System.Text.RegularExpressions;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;

namespace RepServer;

public sealed class RepServerPlugin : BasePlugin
{
    public override string ModuleName => "RepServer";
    public override string ModuleVersion => "0.1.0";

    private const string AdminFile = "/var/lib/repserver/admins.txt";
    private string[] maps = [];

    public override void Load(bool hotReload)
    {
        maps = File.ReadLines("/opt/repserver/maps.txt")
            .Select(line => line.Trim())
            .Where(line => Regex.IsMatch(line, @"\A[a-z0-9_]{1,64}\z"))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        AddCommand("css_help", "Visa RepServers kommandon", (player, command) =>
        {
            command.ReplyToCommand("[RepServer] !help, !maps, !status");
            if (IsAdmin(player))
                command.ReplyToCommand("[RepServer] Admin: !map <karta>, !restart, !pause, !unpause");
        });
        AddCommand("css_maps", "Visa tillåtna kartor", (_, command) =>
            command.ReplyToCommand("[RepServer] " + string.Join(", ", maps)));
        AddCommand("css_status", "Visa pluginversion och aktuell karta", (_, command) =>
            command.ReplyToCommand($"[RepServer] v{ModuleVersion} | karta: {Server.MapName}"));
        AddCommand("css_map", "Byt karta", ChangeMap);
        AddCommand("css_restart", "Starta om matchen", (player, command) =>
            RunMatchCommand(player, command, "mp_restartgame 1", "Startar om matchen."));
        AddCommand("css_omstart", "Starta om matchen", (player, command) =>
            RunMatchCommand(player, command, "mp_restartgame 1", "Startar om matchen."));
        AddCommand("css_pause", "Pausa matchen", (player, command) =>
            RunMatchCommand(player, command, "mp_pause_match", "Begär matchpaus."));
        AddCommand("css_unpause", "Återuppta matchen", (player, command) =>
            RunMatchCommand(player, command, "mp_unpause_match", "Begär att matchen återupptas."));
        Console.WriteLine($"[RepServer] v{ModuleVersion} laddad");
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
