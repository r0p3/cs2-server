using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;

namespace RepServer;

public sealed partial class RepServerPlugin
{
    private const int MenuPageSize = 5;
    private const PlayerButtons MenuButtons =
        PlayerButtons.Forward | PlayerButtons.Back | PlayerButtons.Use | PlayerButtons.Reload;
    private readonly Dictionary<int, MenuSession> menus = new();

    private sealed record MenuOption(string Label, Action<CCSPlayerController> Select);

    private sealed class MenuSession(string title, IReadOnlyList<MenuOption> options,
        Action<CCSPlayerController>? back, CPointWorldText panel, PlayerButtons buttons)
    {
        public string Title { get; } = title;
        public IReadOnlyList<MenuOption> Options { get; } = options;
        public Action<CCSPlayerController>? Back { get; } = back;
        public CPointWorldText Panel { get; } = panel;
        public PlayerButtons PreviousButtons { get; set; } = buttons;
        public int Selected { get; set; }
    }

    private void RegisterMenuListeners()
    {
        AddCommandListener("say", (player, command) => OnMenuChat(player, command), HookMode.Pre);
        AddCommandListener("say_team", (player, command) => OnMenuChat(player, command), HookMode.Pre);
        RegisterListener<Listeners.OnTick>(UpdateMenus);
        RegisterListener<Listeners.CheckTransmit>(FilterMenuPanels);
        RegisterListener<Listeners.OnClientDisconnect>(CloseMenu);
        RegisterListener<Listeners.OnMapEnd>(CloseAllMenus);
    }

    public override void Unload(bool hotReload) => CloseAllMenus();

    private HookResult OnMenuChat(CCSPlayerController? player, CommandInfo command)
    {
        if (player is not { IsValid: true, IsBot: false, IsHLTV: false } ||
            command.ArgCount != 2 || command.ArgByIndex(1).Trim() != "!")
            return HookResult.Continue;

        if (menus.ContainsKey(player.Slot))
            CloseMenu(player.Slot);
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
            new("Botnamn", OpenBotNameMenu),
            new("Lägg till en på T", client => CompleteMenuAction(client, () => AddBot(client, client.PrintToChat, CsTeam.Terrorist))),
            new("Lägg till en på CT", client => CompleteMenuAction(client, () => AddBot(client, client.PrintToChat, CsTeam.CounterTerrorist))),
            new("Ta bort alla bottar", client => CompleteMenuAction(client, () => KickBots(client, client.PrintToChat)))
        ], OpenMainMenu);

    private void OpenBotNameMenu(CCSPlayerController player)
    {
        if (botProfiles.Count == 0)
        {
            player.PrintToChat("[RepServer] Namnlistan är tom. Lägg till namn i config/bot-names.txt.");
            return;
        }

        var options = botProfiles.Select(profile => new MenuOption($"{profile.Name} · {profile.Role}",
            client => client.PrintToChat($"[RepServer] {profile.Name} · {profile.Role}"))).ToArray();
        OpenMenu(player, "Botlistan", options, OpenBotMenu);
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
        if (player.PlayerPawn.Value is not { IsValid: true, MovementServices: not null } || !player.PawnIsAlive)
        {
            player.PrintToChat("[RepServer] Gå med i ett lag och vänta tills du har spawnat för att öppna menyn.");
            return;
        }

        var panel = menus.GetValueOrDefault(player.Slot)?.Panel;
        if (panel is not { IsValid: true })
        {
            panel = Utilities.CreateEntityByName<CPointWorldText>("point_worldtext");
            if (panel is not { IsValid: true })
            {
                player.PrintToChat("[RepServer] Kunde inte öppna den grafiska menyn.");
                return;
            }

            panel.FontName = "Arial Bold";
            panel.FontSize = 30;
            panel.WorldUnitsPerPx = 0.017f;
            panel.Fullbright = true;
            panel.DrawBackground = true;
            panel.Color = Color.FromArgb(255, 238, 235, 227);
            panel.JustifyHorizontal = PointWorldTextJustifyHorizontal_t.POINT_WORLD_TEXT_JUSTIFY_HORIZONTAL_LEFT;
            panel.JustifyVertical = PointWorldTextJustifyVertical_t.POINT_WORLD_TEXT_JUSTIFY_VERTICAL_CENTER;
            panel.ReorientMode = PointWorldTextReorientMode_t.POINT_WORLD_TEXT_REORIENT_NONE;
            panel.DispatchSpawn();
            panel.AcceptInput("Enable");
        }

        var session = new MenuSession(title, options, back, panel, player.Buttons & MenuButtons);
        menus[player.Slot] = session;
        PositionMenuPanel(player, panel);
        RenderMenu(session);
    }

    private void UpdateMenus()
    {
        if (menus.Count == 0)
            return;

        foreach (var (slot, session) in menus.ToArray())
        {
            var player = Utilities.GetPlayerFromSlot(slot);
            if (player is not { IsValid: true, PawnIsAlive: true } ||
                player.PlayerPawn.Value is not { IsValid: true, MovementServices: not null } ||
                !session.Panel.IsValid)
            {
                CloseMenu(slot);
                continue;
            }

            PositionMenuPanel(player, session.Panel);
            var buttons = player.Buttons & MenuButtons;
            var pressed = buttons & ~session.PreviousButtons;
            session.PreviousButtons = buttons;
            if ((pressed & PlayerButtons.Reload) != 0)
            {
                if (session.Back is null)
                    CloseMenu(slot);
                else
                    session.Back(player);
            }
            else if ((pressed & PlayerButtons.Use) != 0 && session.Options.Count > 0)
            {
                session.Options[session.Selected].Select(player);
            }
            else if ((pressed & (PlayerButtons.Forward | PlayerButtons.Back)) != 0 && session.Options.Count > 0)
            {
                var step = (pressed & PlayerButtons.Forward) != 0 ? -1 : 1;
                session.Selected = (session.Selected + step + session.Options.Count) % session.Options.Count;
                RenderMenu(session);
            }
        }
    }

    private static void PositionMenuPanel(CCSPlayerController player, CPointWorldText panel)
    {
        var pawn = player.PlayerPawn.Value;
        if (pawn?.AbsOrigin is not { } origin)
            return;

        var look = pawn.EyeAngles;
        const float radians = MathF.PI / 180;
        var (sinYaw, cosYaw) = MathF.SinCos(look.Y * radians);
        var (sinPitch, cosPitch) = MathF.SinCos(look.X * radians);
        // Place the panel in front of the camera and to the left of the crosshair.
        var position = new Vector(
            origin.X + 24 * cosPitch * cosYaw - 12 * sinYaw,
            origin.Y + 24 * cosPitch * sinYaw + 12 * cosYaw,
            origin.Z + pawn.ViewOffset.Z - 24 * sinPitch);
        panel.Teleport(position, new QAngle(0, look.Y - 90, 90 - look.X));
    }

    private void FilterMenuPanels(CCheckTransmitInfoList recipients)
    {
        foreach (var (info, client) in recipients)
        {
            foreach (var (owner, session) in menus)
            {
                if (client?.Slot != owner && session.Panel.IsValid)
                    info.TransmitEntities.Remove(session.Panel);
            }
        }
    }

    private static void RenderMenu(MenuSession session)
    {
        var page = session.Selected / MenuPageSize;
        var start = page * MenuPageSize;
        var pages = Math.Max(1, (session.Options.Count + MenuPageSize - 1) / MenuPageSize);
        var lines = new List<string> { $"{session.Title} [{page + 1}/{pages}]", "" };
        for (var index = start; index < Math.Min(start + MenuPageSize, session.Options.Count); index++)
            lines.Add((index == session.Selected ? "> " : "   ") + session.Options[index].Label);
        lines.Add("");
        lines.Add(session.Back is null ? "W/S Flytta   E Välj   R Stäng" : "W/S Flytta   E Välj   R Tillbaka");
        session.Panel.AcceptInput("SetMessage", value: string.Join('\n', lines));
    }

    private void CompleteMenuAction(CCSPlayerController player, Action action)
    {
        CloseMenu(player.Slot);
        action();
    }

    private void CloseMenu(int slot)
    {
        if (menus.Remove(slot, out var session) && session.Panel.IsValid)
            session.Panel.Remove();
    }

    private void CloseAllMenus()
    {
        foreach (var slot in menus.Keys.ToArray())
            CloseMenu(slot);
    }
}
