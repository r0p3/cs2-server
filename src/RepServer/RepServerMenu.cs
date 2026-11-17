using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;

namespace RepServer;

public sealed partial class RepServerPlugin
{
    private const int MenuPageSize = 7;
    private const double MenuTimeout = 30;
    private readonly Dictionary<int, MenuSession> menus = new();

    private sealed record MenuOption(string Label, Action<CCSPlayerController> Select);

    private sealed class MenuSession(string title, IReadOnlyList<MenuOption> options,
        Action<CCSPlayerController>? back)
    {
        public string Title { get; } = title;
        public IReadOnlyList<MenuOption> Options { get; } = options;
        public Action<CCSPlayerController>? Back { get; } = back;
        public double ExpiresAt { get; set; }
        public int Page { get; set; }
        public bool SelectionPending { get; set; }
    }

    private void RegisterMenuListeners()
    {
        AddCommandListener("say", (player, command) => OnMenuChat(player, command), HookMode.Pre);
        AddCommandListener("say_team", (player, command) => OnMenuChat(player, command), HookMode.Pre);
        RegisterListener<Listeners.OnClientDisconnect>(slot => menus.Remove(slot));
        RegisterListener<Listeners.OnMapStart>(_ => menus.Clear());
    }

    private HookResult OnMenuChat(CCSPlayerController? player, CommandInfo command)
    {
        if (player is not { IsValid: true, IsBot: false, IsHLTV: false } || command.ArgCount != 2)
            return HookResult.Continue;

        var input = command.ArgByIndex(1).Trim();
        var slot = player.Slot;
        var session = menus.GetValueOrDefault(slot);
        if (session is not null && session.ExpiresAt <= Server.EngineTime)
        {
            menus.Remove(slot);
            session = null;
        }

        if (input == "!")
        {
            if (session is null)
                OpenMainMenu(player);
            else
                CloseMenu(player);
            return HookResult.Handled;
        }

        if (session is null || input.Length != 1 || input[0] is < '0' or > '9')
            return HookResult.Continue;

        if (session.SelectionPending)
            return HookResult.Handled;

        var key = input[0] - '0';
        var offset = session.Page * MenuPageSize;
        switch (key)
        {
            case 0:
                if (session.Back is null)
                    CloseMenu(player);
                else
                    session.Back(player);
                break;
            case 8 when session.Page > 0:
                session.Page--;
                RenderMenu(player, session);
                break;
            case 9 when offset + MenuPageSize < session.Options.Count:
                session.Page++;
                RenderMenu(player, session);
                break;
            case >= 1 and <= MenuPageSize when offset + key <= session.Options.Count:
                QueueMenuSelection(slot, session, session.Options[offset + key - 1]);
                break;
            default:
                player.PrintToChat("[RepServer] Välj en siffra som visas i menyn.");
                break;
        }
        return HookResult.Handled;
    }

    private void QueueMenuSelection(int slot, MenuSession session, MenuOption option)
    {
        session.SelectionPending = true;
        // Map changes and bot actions must not run while CS2 processes the chat packet.
        Server.NextWorldUpdate(() =>
        {
            if (!ReferenceEquals(menus.GetValueOrDefault(slot), session))
                return;

            session.SelectionPending = false;
            if (Utilities.GetPlayerFromSlot(slot) is { IsValid: true } player)
                option.Select(player);
        });
    }

    private void OpenMainMenu(CCSPlayerController player)
    {
        var options = new List<MenuOption>
        {
            new("Serverstatus", client => CompleteMenuAction(client, () =>
                client.PrintToChat($"[RepServer] {ModeLabel(activeMode)} | {Server.MapName} | v{ModuleVersion}")))
        };
        if (IsAdmin(player))
        {
            options.Add(new("Byt karta", OpenMapMenu));
            options.Add(new("Byt spelläge", OpenModeMenu));
            options.Add(new("Matchkontroller", OpenMatchMenu));
            options.Add(new("Bottar", OpenBotMenu));
        }

        OpenMenu(player, "RepServer", options);
    }

    private void OpenMapMenu(CCSPlayerController player) =>
        OpenMenu(player, "Välj karta", maps.Select(map =>
            new MenuOption(map, client => CompleteMenuAction(client, () => SelectMap(client, client.PrintToChat, map)))).ToArray(),
            OpenMainMenu);

    private void OpenModeMenu(CCSPlayerController player)
    {
        var options = new[] { "bot-game", "bot-game-t", "bot-dm", "bot-dm-pistol" }.Select(mode =>
            new MenuOption(ModeLabel(mode), client => OpenMenu(client, "Byt läge och starta om?",
                [new("Bekräfta", confirmed => CompleteMenuAction(confirmed,
                    () => SelectMode(confirmed, confirmed.PrintToChat, mode)))], OpenModeMenu))).ToArray();
        OpenMenu(player, "Välj spelläge", options, OpenMainMenu);
    }

    private void OpenMatchMenu(CCSPlayerController player) =>
        OpenMenu(player, "Matchkontroller",
        [
            MatchOption("Starta om matchen", "mp_restartgame 1", "Startar om matchen."),
            MatchOption("Pausa", "mp_pause_match", "Begär matchpaus."),
            MatchOption("Återuppta", "mp_unpause_match", "Begär att matchen återupptas."),
            new("Avsluta uppvärmning", client => CompleteMenuAction(client, () => StartMatch(client, client.PrintToChat)))
        ], OpenMainMenu);

    private MenuOption MatchOption(string label, string action, string message) =>
        new(label, client => CompleteMenuAction(client, () => RunMatchCommand(client, client.PrintToChat, action, message)));

    private void OpenBotMenu(CCSPlayerController player) =>
        OpenMenu(player, "Bottar",
        [
            new("Välj antal på T", client => OpenBotCountMenu(client, CsTeam.Terrorist, 5)),
            new("Välj antal på CT", client => OpenBotCountMenu(client, CsTeam.CounterTerrorist, 5)),
            new("Botnamn", OpenBotNameMenu),
            new("Lägg till en på T", client => CompleteMenuAction(client, () => AddBot(client, client.PrintToChat, "bot_add_t", "T"))),
            new("Lägg till en på CT", client => CompleteMenuAction(client, () => AddBot(client, client.PrintToChat, "bot_add_ct", "CT"))),
            new("Ta bort alla bottar", client => CompleteMenuAction(client, () => KickBots(client, client.PrintToChat)))
        ], OpenMainMenu);

    private void OpenBotNameMenu(CCSPlayerController player)
    {
        if (botProfiles.Count == 0)
        {
            player.PrintToChat("[RepServer] Namnlistan är tom. Lägg till namn i config/bot-names.txt.");
            return;
        }

        var options = botProfiles.Select(profile => new MenuOption($"{profile.Name} · {profile.Role}", client => CompleteMenuAction(client,
            () => RenameTestBot(client, client.PrintToChat, profile.Name)))).ToArray();
        OpenMenu(player, "Välj botnamn", options, OpenBotMenu);
    }

    private void OpenBotCountMenu(CCSPlayerController player, CsTeam team, int requested)
    {
        var target = Math.Clamp(requested, 1, Math.Max(1, PlayerLimit));
        var teamName = team == CsTeam.Terrorist ? "T" : "CT";
        OpenMenu(player, $"{teamName} · {target} bottar",
        [
            new($"Fyll på till {target}", client => CompleteMenuAction(client, () => SetBots(client, client.PrintToChat, team, target))),
            new("Öka med 1", client => OpenBotCountMenu(client, team, target + 1)),
            new("Öka med 5", client => OpenBotCountMenu(client, team, target + 5)),
            new("Minska med 1", client => OpenBotCountMenu(client, team, target - 1)),
            new("Minska med 5", client => OpenBotCountMenu(client, team, target - 5))
        ], OpenBotMenu);
    }

    private void OpenMenu(CCSPlayerController player, string title, IReadOnlyList<MenuOption> options,
        Action<CCSPlayerController>? back = null)
    {
        var session = new MenuSession(title, options, back);
        menus[player.Slot] = session;
        RenderMenu(player, session);
    }

    private static void RenderMenu(CCSPlayerController player, MenuSession session)
    {
        var offset = session.Page * MenuPageSize;
        var end = Math.Min(offset + MenuPageSize, session.Options.Count);
        player.PrintToChat($"[RepServer] {session.Title} · skriv siffran i chatten");
        for (var index = offset; index < end; index++)
            player.PrintToChat($"  {index - offset + 1}. {session.Options[index].Label}");

        var navigation = new List<string>();
        if (session.Page > 0)
            navigation.Add("8 Föregående sida");
        if (end < session.Options.Count)
            navigation.Add("9 Nästa sida");
        navigation.Add(session.Back is null ? "0 Stäng" : "0 Tillbaka");
        player.PrintToChat("[RepServer] " + string.Join(" · ", navigation));
        session.ExpiresAt = Server.EngineTime + MenuTimeout;
    }

    private void CompleteMenuAction(CCSPlayerController player, Action action)
    {
        CloseMenu(player);
        action();
    }

    private void CloseMenu(CCSPlayerController player)
    {
        menus.Remove(player.Slot);
    }
}
