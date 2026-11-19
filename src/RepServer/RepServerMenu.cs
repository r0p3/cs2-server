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

    private sealed class MenuSession(CPointWorldText panel, CHandle<CCSPlayerPawn> pawn, float velocityModifier)
    {
        public string Title { get; set; } = "";
        public IReadOnlyList<MenuOption> Options { get; set; } = [];
        public Action<CCSPlayerController>? Back { get; set; }
        public CPointWorldText Panel { get; } = panel;
        public CHandle<CCSPlayerPawn> Pawn { get; } = pawn;
        public float VelocityModifier { get; } = velocityModifier;
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
        StopChallenge();
        ResetReadyPlayers();
        CloseAllMenus();
    }

    private HookResult OnRepServerChat(CCSPlayerController? player, CommandInfo command)
    {
        if (player is not { IsValid: true, IsBot: false, IsHLTV: false })
            return HookResult.Continue;

        // Parse chat from both the chat box and bound say/say_team commands.
        var chat = command.ArgString.Trim().Trim('"').Trim();
        if (chat == "!")
        {
            if (menus.ContainsKey(player.Slot))
                CloseMenu(player.Slot);
            else
                QueueOpenMenu(player);
            return HookResult.Handled;
        }
        if (chat.Length == 0 || chat[0] is not ('!' or '/'))
            return HookResult.Continue;

        var parts = chat.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (!chatCommands.TryGetValue(parts[0][1..], out var handler))
            return HookResult.Continue;

        handler(player, parts[1..].Select(arg => arg.Trim('"')).ToArray(), player.PrintToChat);
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
            new("Server status", client => CompleteMenuAction(client, () =>
                client.PrintToChat($"[RepServer] {ModeLabel(activeMode)} | {Server.MapName} | v{ModuleVersion}")))
        };
        if (IsReadinessWarmup())
            options.Add(new("Match readiness", OpenReadyMenu));
        var isAdmin = IsAdmin(player);
        if (isAdmin || workshopMaps.Count != 0)
            options.Add(new("Change map", OpenMapMenu));
        if (isAdmin)
        {
            options.Add(new("Change game mode", OpenModeMenu));
            options.Add(new("Match controls", OpenMatchMenu));
            options.Add(new("Bots", OpenBotMenu));
        }

        OpenMenu(player, "RepServer", options);
    }

    private void OpenReadyMenu(CCSPlayerController player)
    {
        var humans = ConnectedHumans();
        var ready = humans.Count(IsReady);
        OpenMenu(player, $"Match readiness ({ready}/{humans.Length})",
        [
            new("Mark me as ready", client => CompleteMenuAction(client, () => SetReady(client, client.PrintToChat, true))),
            new("Mark me as not ready", client => CompleteMenuAction(client, () => SetReady(client, client.PrintToChat, false))),
            new("Show who is ready", client => CompleteMenuAction(client, () => ShowReadyStatus(client.PrintToChat)))
        ], OpenMainMenu);
    }

    private void OpenMapMenu(CCSPlayerController player) =>
        OpenMenu(player, "Select map", MapChoices(IsAdmin(player)).Select(map =>
            new MenuOption(map, client => CompleteMenuAction(client, () => SelectMap(client, client.PrintToChat, map)))).ToArray(),
            OpenMainMenu);

    private void OpenModeMenu(CCSPlayerController player)
    {
        var options = new[] { "bot-game", "bot-game-t", "bot-dm", "bot-dm-pistol" }.Select(mode =>
            new MenuOption(ModeLabel(mode), client => OpenMenu(client, "Change mode and restart the server?",
                [new("Confirm", confirmed => CompleteMenuAction(confirmed,
                    () => SelectMode(confirmed, confirmed.PrintToChat, mode)))], OpenModeMenu))).ToArray();
        OpenMenu(player, "Select game mode", options, OpenMainMenu);
    }

    private void OpenMatchMenu(CCSPlayerController player) =>
        OpenMenu(player, "Match controls",
        [
            MatchOption("Restart the match", "mp_restartgame 1", "Restarting the match."),
            MatchOption("Pause match", "mp_pause_match", "Requesting a match pause."),
            MatchOption("Resume match", "mp_unpause_match", "Requesting that the match resume."),
            new("End warmup", client => CompleteMenuAction(client, () => StartMatch(client, client.PrintToChat)))
        ], OpenMainMenu);

    private MenuOption MatchOption(string label, string action, string message) =>
        new(label, client => CompleteMenuAction(client, () => RunMatchCommand(client, client.PrintToChat, action, message)));

    private void OpenBotMenu(CCSPlayerController player) =>
        OpenMenu(player, "Bots",
        [
            new("Set T bot target", client => OpenBotCountMenu(client, CsTeam.Terrorist, 5)),
            new("Set CT bot target", client => OpenBotCountMenu(client, CsTeam.CounterTerrorist, 5)),
            new("Bot names", OpenBotNameMenu),
            new("Add one bot to T", client => CompleteMenuAction(client, () => AddBot(client, client.PrintToChat, CsTeam.Terrorist))),
            new("Add one bot to CT", client => CompleteMenuAction(client, () => AddBot(client, client.PrintToChat, CsTeam.CounterTerrorist))),
            new("Remove all bots", client => CompleteMenuAction(client, () => KickBots(client, client.PrintToChat)))
        ], OpenMainMenu);

    private void OpenBotNameMenu(CCSPlayerController player)
    {
        if (botProfiles.Count == 0)
        {
            player.PrintToChat("[RepServer] The bot roster is empty. Add names in config/bot-names.txt.");
            return;
        }

        var options = botProfiles.Select(profile => new MenuOption($"{profile.Name} · {profile.Role}",
            client => client.PrintToChat($"[RepServer] {profile.Name} · {profile.Role}"))).ToArray();
        OpenMenu(player, "Bot roster", options, OpenBotMenu);
    }

    private void OpenBotCountMenu(CCSPlayerController player, CsTeam team, int requested)
    {
        var target = Math.Clamp(requested, 1, Math.Max(1, PlayerLimit));
        var teamName = team == CsTeam.Terrorist ? "T" : "CT";
        OpenMenu(player, $"{teamName} · {target} bots",
        [
            new($"Fill to {target}", client => CompleteMenuAction(client, () => SetBots(client, client.PrintToChat, team, target))),
            new("Increase by 1", client => OpenBotCountMenu(client, team, target + 1)),
            new("Increase by 5", client => OpenBotCountMenu(client, team, target + 5)),
            new("Decrease by 1", client => OpenBotCountMenu(client, team, target - 1)),
            new("Decrease by 5", client => OpenBotCountMenu(client, team, target - 5))
        ], OpenBotMenu);
    }

    private void OpenMenu(CCSPlayerController player, string title, IReadOnlyList<MenuOption> options,
        Action<CCSPlayerController>? back = null)
    {
        if (player.PlayerPawn.Value is not { IsValid: true, MovementServices: not null } pawn || !player.PawnIsAlive)
        {
            player.PrintToChat("[RepServer] Join a team and spawn to open the menu.");
            return;
        }

        var session = menus.GetValueOrDefault(player.Slot);
        if (session is null || !session.Panel.IsValid || session.Pawn.Raw != player.PlayerPawn.Raw)
        {
            CloseMenu(player.Slot);
            var panel = Utilities.CreateEntityByName<CPointWorldText>("point_worldtext");
            if (panel is not { IsValid: true })
            {
                player.PrintToChat("[RepServer] Could not open the menu.");
                return;
            }

            panel.FontName = "Arial Bold";
            panel.FontSize = 30;
            panel.WorldUnitsPerPx = 0.021f;
            panel.Fullbright = true;
            panel.DrawBackground = true;
            panel.Color = Color.FromArgb(255, 238, 235, 227);
            panel.JustifyHorizontal = PointWorldTextJustifyHorizontal_t.POINT_WORLD_TEXT_JUSTIFY_HORIZONTAL_LEFT;
            panel.JustifyVertical = PointWorldTextJustifyVertical_t.POINT_WORLD_TEXT_JUSTIFY_VERTICAL_TOP;
            panel.ReorientMode = PointWorldTextReorientMode_t.POINT_WORLD_TEXT_REORIENT_NONE;
            panel.DispatchSpawn();
            panel.AcceptInput("Enable");

            session = new MenuSession(panel, new CHandle<CCSPlayerPawn>(player.PlayerPawn.Raw),
                pawn.VelocityModifier);
            SetMenuSpeed(pawn, 0);
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
                !session.Panel.IsValid)
            {
                CloseMenu(slot);
                continue;
            }

            SetMenuSpeed(pawn, 0);
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
        const float depth = 24;
        const float left = 16;
        const float up = 9;
        var horizontalForward = depth * cosPitch + up * sinPitch;
        // Offset along the camera's left and up axes so the menu follows the view.
        var position = new Vector(
            origin.X + horizontalForward * cosYaw - left * sinYaw,
            origin.Y + horizontalForward * sinYaw + left * cosYaw,
            origin.Z + pawn.ViewOffset.Z - depth * sinPitch + up * cosPitch);
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
        lines.Add(session.Back is null ? "W/S Move   E Select   R Close" : "W/S Move   E Select   R Back");
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

        if (session.Pawn.Value is { IsValid: true } pawn)
            SetMenuSpeed(pawn, session.VelocityModifier);
        if (session.Panel.IsValid)
            session.Panel.Remove();
    }

    private static void SetMenuSpeed(CCSPlayerPawn pawn, float velocityModifier)
    {
        if (pawn.VelocityModifier == velocityModifier)
            return;

        pawn.VelocityModifier = velocityModifier;
        Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_flVelocityModifier");
    }

    private void CloseAllMenus()
    {
        foreach (var slot in menus.Keys.ToArray())
            CloseMenu(slot);
    }
}
