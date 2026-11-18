using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;

namespace RepServer;

public sealed partial class RepServerPlugin
{
    private const int MenuPageSize = 5;
    private const MoveType_t MenuMoveType = MoveType_t.MOVETYPE_OBSOLETE;
    private const PlayerButtons MenuButtons =
        PlayerButtons.Forward | PlayerButtons.Back | PlayerButtons.Use | PlayerButtons.Reload;
    private readonly Dictionary<int, MenuSession> menus = new();

    private sealed record MenuOption(string Label, Action<CCSPlayerController> Select);

    private sealed class MenuSession(CPointWorldText panel, CHandle<CCSPlayerPawn> pawn,
        MoveType_t moveType, MoveType_t actualMoveType)
    {
        public string Title { get; set; } = "";
        public IReadOnlyList<MenuOption> Options { get; set; } = [];
        public Action<CCSPlayerController>? Back { get; set; }
        public CPointWorldText Panel { get; } = panel;
        public CHandle<CCSPlayerPawn> Pawn { get; } = pawn;
        public MoveType_t MoveType { get; } = moveType;
        public MoveType_t ActualMoveType { get; } = actualMoveType;
        public PlayerButtons PreviousButtons { get; set; }
        public int Selected { get; set; }
    }

    private void RegisterMenuListeners()
    {
        AddCommandListener("say", OnRepServerChat, HookMode.Pre);
        AddCommandListener("say_team", OnRepServerChat, HookMode.Pre);
        RegisterListener<Listeners.OnTick>(UpdateMenus);
        RegisterListener<Listeners.CheckTransmit>(FilterMenuPanels);
        RegisterListener<Listeners.OnClientDisconnect>(CloseMenu);
        RegisterListener<Listeners.OnMapEnd>(CloseAllMenus);
    }

    public override void Unload(bool hotReload)
    {
        workshopLifetime.Cancel();
        ResetReadyPlayers();
        CloseAllMenus();
    }

    private HookResult OnRepServerChat(CCSPlayerController? player, CommandInfo command)
    {
        if (player is not { IsValid: true, IsBot: false, IsHLTV: false })
            return HookResult.Continue;

        // Read the original chat line before CounterStrikeSharp tokenizes it as a console command.
        var chat = command.ArgString.Trim().Trim('"');
        var parts = chat.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 0 && (parts[0].Equals("!add_map", StringComparison.OrdinalIgnoreCase) ||
                                 parts[0].Equals("/add_map", StringComparison.OrdinalIgnoreCase)))
        {
            AddWorkshopMap(player, parts.Length == 2 ? parts[1] : "", player.PrintToChat);
            return HookResult.Handled;
        }

        if (chat.Trim() != "!")
            return HookResult.Continue;

        if (menus.ContainsKey(player.Slot))
            CloseMenu(player.Slot);
        else
            QueueOpenMenu(player);
        return HookResult.Handled;
    }

    private void QueueOpenMenu(CCSPlayerController player)
    {
        var slot = player.Slot;
        var identity = player.UserId;
        Server.NextWorldUpdate(() =>
        {
            if (Utilities.GetPlayerFromSlot(slot) is { IsValid: true } current && current.UserId == identity)
                OpenMainMenu(current);
        });
    }

    private void OpenMainMenu(CCSPlayerController player)
    {
        var options = new List<MenuOption>
        {
            new("Serverstatus", client => CompleteMenuAction(client, () =>
                client.PrintToChat($"[RepServer] {ModeLabel(activeMode)} | {Server.MapName} | v{ModuleVersion}")))
        };
        if (IsReadinessWarmup())
            options.Add(new("Redo för match", OpenReadyMenu));
        var isAdmin = IsAdmin(player);
        if (isAdmin || workshopMaps.Count != 0)
            options.Add(new("Byt karta", OpenMapMenu));
        if (isAdmin)
        {
            options.Add(new("Byt spelläge", OpenModeMenu));
            options.Add(new("Matchkontroller", OpenMatchMenu));
            options.Add(new("Bottar", OpenBotMenu));
        }

        OpenMenu(player, "RepServer", options);
    }

    private void OpenReadyMenu(CCSPlayerController player)
    {
        var humans = ConnectedHumans();
        var ready = humans.Count(IsReady);
        OpenMenu(player, $"Redo för match ({ready}/{humans.Length})",
        [
            new("Jag är redo", client => CompleteMenuAction(client, () => SetReady(client, client.PrintToChat, true))),
            new("Ta tillbaka redo", client => CompleteMenuAction(client, () => SetReady(client, client.PrintToChat, false))),
            new("Visa vilka som är redo", client => CompleteMenuAction(client, () => ShowReadyStatus(client.PrintToChat)))
        ], OpenMainMenu);
    }

    private void OpenMapMenu(CCSPlayerController player) =>
        OpenMenu(player, "Välj karta", MapChoices(IsAdmin(player)).Select(map =>
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
        if (player.PlayerPawn.Value is not { IsValid: true, MovementServices: not null } pawn || !player.PawnIsAlive)
        {
            player.PrintToChat("[RepServer] Gå med i ett lag och vänta tills du har spawnat för att öppna menyn.");
            return;
        }

        var session = menus.GetValueOrDefault(player.Slot);
        if (session is null || !session.Panel.IsValid || session.Pawn.Raw != player.PlayerPawn.Raw)
        {
            CloseMenu(player.Slot);
            var panel = Utilities.CreateEntityByName<CPointWorldText>("point_worldtext");
            if (panel is not { IsValid: true })
            {
                player.PrintToChat("[RepServer] Kunde inte öppna den grafiska menyn.");
                return;
            }

            panel.FontName = "Arial Bold";
            panel.FontSize = 30;
            panel.WorldUnitsPerPx = 0.022f;
            panel.Fullbright = true;
            panel.DrawBackground = true;
            panel.BackgroundMaterialName = "materials/tools/toolsblack.vmat";
            panel.BackgroundBorderWidth = 0.6f;
            panel.BackgroundBorderHeight = 0.4f;
            panel.Color = Color.FromArgb(255, 238, 235, 227);
            panel.JustifyHorizontal = PointWorldTextJustifyHorizontal_t.POINT_WORLD_TEXT_JUSTIFY_HORIZONTAL_LEFT;
            panel.JustifyVertical = PointWorldTextJustifyVertical_t.POINT_WORLD_TEXT_JUSTIFY_VERTICAL_CENTER;
            panel.ReorientMode = PointWorldTextReorientMode_t.POINT_WORLD_TEXT_REORIENT_NONE;
            panel.DispatchSpawn();
            panel.AcceptInput("Enable");

            session = new MenuSession(panel, new CHandle<CCSPlayerPawn>(player.PlayerPawn.Raw),
                pawn.MoveType, pawn.ActualMoveType);
            SetMenuMovement(pawn, MenuMoveType, MenuMoveType);
            menus[player.Slot] = session;
        }

        session.Title = title;
        session.Options = options;
        session.Back = back;
        session.PreviousButtons = player.Buttons & MenuButtons;
        session.Selected = 0;
        PositionMenuPanel(player, session.Panel);
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
                player.PlayerPawn.Raw != session.Pawn.Raw ||
                player.PlayerPawn.Value is not { IsValid: true, MovementServices: not null } pawn ||
                pawn.MoveType != MenuMoveType || pawn.ActualMoveType != MenuMoveType ||
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
        lines.Add(session.Back is null ? "W/S Markera   E Välj   R Stäng" : "W/S Markera   E Välj   R Tillbaka");
        session.Panel.AcceptInput("SetMessage", value: string.Join('\n', lines));
    }

    private void CompleteMenuAction(CCSPlayerController player, Action action)
    {
        CloseMenu(player.Slot);
        action();
    }

    private void CloseMenu(int slot)
    {
        if (!menus.Remove(slot, out var session))
            return;

        if (session.Pawn.Value is { IsValid: true } pawn &&
            pawn.MoveType == MenuMoveType && pawn.ActualMoveType == MenuMoveType)
            SetMenuMovement(pawn, session.MoveType, session.ActualMoveType);
        if (session.Panel.IsValid)
            session.Panel.Remove();
    }

    private static void SetMenuMovement(CCSPlayerPawn pawn, MoveType_t moveType, MoveType_t actualMoveType)
    {
        pawn.MoveType = moveType;
        pawn.ActualMoveType = actualMoveType;
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_MoveType");
    }

    private void CloseAllMenus()
    {
        foreach (var slot in menus.Keys.ToArray())
            CloseMenu(slot);
    }
}
