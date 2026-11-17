using System.Net;
using System.Text;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;

namespace RepServer;

public sealed partial class RepServerPlugin
{
    private const PlayerButtons MenuButtons =
        PlayerButtons.Forward | PlayerButtons.Back | PlayerButtons.Use | PlayerButtons.Reload;
    private const int VisibleOptions = 4;
    private readonly Dictionary<int, MenuSession> menus = new();

    private sealed record MenuOption(string Label, Action<CCSPlayerController> Select);

    private sealed class MenuSession(string title, IReadOnlyList<MenuOption> options,
        Action<CCSPlayerController>? back, PlayerButtons buttons, double expiresAt)
    {
        public string Title { get; } = title;
        public IReadOnlyList<MenuOption> Options { get; } = options;
        public Action<CCSPlayerController>? Back { get; } = back;
        public PlayerButtons PreviousButtons { get; set; } = buttons;
        public double ExpiresAt { get; set; } = expiresAt;
        public double RefreshAt { get; set; }
        public int Selected { get; set; }
    }

    private void RegisterMenuListeners()
    {
        AddCommandListener("say", (player, command) => OnMenuChat(player, command), HookMode.Pre);
        AddCommandListener("say_team", (player, command) => OnMenuChat(player, command), HookMode.Pre);
        RegisterListener<Listeners.OnTick>(UpdateMenus);
        RegisterListener<Listeners.OnClientDisconnect>(slot => menus.Remove(slot));
        RegisterListener<Listeners.OnMapStart>(_ => menus.Clear());
    }

    private HookResult OnMenuChat(CCSPlayerController? player, CommandInfo command)
    {
        if (player is not { IsValid: true, IsBot: false, IsHLTV: false } ||
            command.ArgCount != 2 || command.ArgByIndex(1).Trim() != "!")
            return HookResult.Continue;

        if (menus.ContainsKey(player.Slot))
            CloseMenu(player);
        else
            OpenMainMenu(player);
        return HookResult.Handled;
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
            new("Lägg till en på T", client => CompleteMenuAction(client, () => AddBot(client, client.PrintToChat, "bot_add_t", "T"))),
            new("Lägg till en på CT", client => CompleteMenuAction(client, () => AddBot(client, client.PrintToChat, "bot_add_ct", "CT"))),
            new("Ta bort alla bottar", client => CompleteMenuAction(client, () => KickBots(client, client.PrintToChat)))
        ], OpenMainMenu);

    private void OpenBotCountMenu(CCSPlayerController player, CsTeam team, int requested, int selected = 0)
    {
        var target = Math.Clamp(requested, 1, Math.Max(1, PlayerLimit));
        var teamName = team == CsTeam.Terrorist ? "T" : "CT";
        OpenMenu(player, $"{teamName} · {target} bottar",
        [
            new($"Fyll på till {target}", client => CompleteMenuAction(client, () => SetBots(client, client.PrintToChat, team, target))),
            new("Öka med 1", client => OpenBotCountMenu(client, team, target + 1, 1)),
            new("Öka med 5", client => OpenBotCountMenu(client, team, target + 5, 2)),
            new("Minska med 1", client => OpenBotCountMenu(client, team, target - 1, 3)),
            new("Minska med 5", client => OpenBotCountMenu(client, team, target - 5, 4))
        ], OpenBotMenu, selected);
    }

    private void OpenMenu(CCSPlayerController player, string title, IReadOnlyList<MenuOption> options,
        Action<CCSPlayerController>? back = null, int selected = 0)
    {
        var session = new MenuSession(title, options, back, ReadMenuButtons(player), Server.EngineTime + 30) { Selected = selected };
        menus[player.Slot] = session;
        RenderMenu(player, session);
    }

    private void UpdateMenus()
    {
        if (menus.Count == 0)
            return;

        foreach (var (slot, session) in menus.ToArray())
        {
            if (Utilities.GetPlayerFromSlot(slot) is not { IsValid: true } player)
            {
                menus.Remove(slot);
                continue;
            }
            var now = Server.EngineTime;
            if (now >= session.ExpiresAt)
            {
                CloseMenu(player);
                continue;
            }

            var buttons = ReadMenuButtons(player);
            var pressed = buttons & ~session.PreviousButtons;
            session.PreviousButtons = buttons;
            if (pressed != 0)
                session.ExpiresAt = now + 30;

            if ((pressed & PlayerButtons.Reload) != 0)
            {
                if (session.Back is null)
                    CloseMenu(player);
                else
                    session.Back(player);
                continue;
            }
            if ((pressed & PlayerButtons.Use) != 0 && session.Options.Count > 0)
            {
                session.Options[session.Selected].Select(player);
                continue;
            }
            if ((pressed & (PlayerButtons.Forward | PlayerButtons.Back)) != 0 && session.Options.Count > 0)
            {
                var direction = (pressed & PlayerButtons.Forward) != 0 ? -1 : 1;
                session.Selected = (session.Selected + direction + session.Options.Count) % session.Options.Count;
                RenderMenu(player, session);
            }
            else if (now >= session.RefreshAt)
            {
                RenderMenu(player, session);
            }
        }
    }

    private static PlayerButtons ReadMenuButtons(CCSPlayerController player) =>
        player.Pawn.Value is { IsValid: true, MovementServices: { } movement }
            ? (PlayerButtons)movement.Buttons.ButtonStates[0] & MenuButtons
            : 0;

    private static void RenderMenu(CCSPlayerController player, MenuSession session)
    {
        var html = new StringBuilder("<font color='#ffcc66'><b>")
            .Append(WebUtility.HtmlEncode(session.Title))
            .Append("</b></font><br>");
        var start = Math.Clamp(session.Selected - VisibleOptions + 1, 0, Math.Max(0, session.Options.Count - VisibleOptions));
        for (var index = start; index < Math.Min(start + VisibleOptions, session.Options.Count); index++)
        {
            if (index == session.Selected)
                html.Append("<font color='#aaddff'>&gt; ");
            html.Append(WebUtility.HtmlEncode(session.Options[index].Label));
            if (index == session.Selected)
                html.Append("</font>");
            html.Append("<br>");
        }
        html.Append("W/S Flytta · E Välj · R ").Append(session.Back is null ? "Stäng" : "Tillbaka");
        player.PrintToCenterHtml(html.ToString(), 2);
        session.RefreshAt = Server.EngineTime + 1;
    }

    private void CompleteMenuAction(CCSPlayerController player, Action action)
    {
        CloseMenu(player);
        action();
    }

    private void CloseMenu(CCSPlayerController player)
    {
        menus.Remove(player.Slot);
        player.PrintToCenterHtml(" ", 1);
    }
}
