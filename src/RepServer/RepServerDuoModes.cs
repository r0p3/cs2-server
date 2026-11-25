using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace RepServer;

public sealed partial class RepServerPlugin
{
    private sealed record DuoMode(string Category, CsTeam Side)
    {
        public string Key => $"{Category}-{TeamLabel(Side).ToLowerInvariant()}";
        public string Label => $"{RecordLabel(Category)} ({TeamLabel(Side)})";
        public CsTeam BotSide => Side == CsTeam.Terrorist ? CsTeam.CounterTerrorist : CsTeam.Terrorist;
    }

    private DuoMode? duoMode;
    private int? duoBotTarget;

    private static string? DuoModeCategory(string? name) => name?.ToLowerInvariant() switch
    {
        "win" or "2v10" or TenWin => TenWin,
        "16" or TenSweep => TenSweep,
        "max" or MaxBots => MaxBots,
        _ => null
    };

    private static DuoMode? ReadDuoMode(string mode)
    {
        var separator = mode.LastIndexOf('-');
        if (separator < 0 || mode[..separator] is not (TenWin or TenSweep or MaxBots))
            return null;
        return mode[(separator + 1)..] switch
        {
            "t" => new(mode[..separator], CsTeam.Terrorist),
            "ct" => new(mode[..separator], CsTeam.CounterTerrorist),
            _ => null
        };
    }

    private void SelectDuoMode(CCSPlayerController? player, Action<string> reply, DuoMode mode)
    {
        if (!CheckAdmin(player, reply) || CheckModeSwitchPending(reply))
            return;
        if (!IsCompetitiveMode)
        {
            RestartIntoMode(player, reply, mode.Key);
            return;
        }
        if (Utilities.GetGameRules() is not { } rules)
        {
            reply("[RepServer] Wait for the map to finish loading.");
            return;
        }

        if (!TrySaveState(ModeFile, mode.Key))
        {
            reply("[RepServer] Could not save the mode. Check data/state on the server.");
            return;
        }

        duoMode = mode;
        announcedRecord = null;
        activeMode = mode.Key;
        Environment.SetEnvironmentVariable("REPSERVER_ACTIVE_MODE", activeMode);
        var message = $"[RepServer] Setting up {mode.Label}. Join {TeamLabel(mode.Side)} and ready up after bot fill.";
        reply(message);
        if (player is null)
            Server.PrintToChatAll(message);

        if (!rules.WarmupPeriod)
        {
            ResetReadyPlayers();
            Server.ExecuteCommand("mp_warmup_start");
        }

        var humans = ConnectedHumans();
        if (humans.Length > 0 && !CanFillDuoSide(mode, humans))
        {
            CancelBotFill();
            StopChallenge();
            ResetTrackerMatch();
            duoBotTarget = null;
            botFillTimer = AddTimer(BotFillInterval, StartDuoRosterWhenReady, TimerFlags.STOP_ON_MAPCHANGE);
            return;
        }

        ConfigureDuoRoster(mode, rules);
    }

    private static bool IsOnPlayingTeam(CCSPlayerController player) =>
        player.Team is CsTeam.Terrorist or CsTeam.CounterTerrorist;

    private static bool CanFillDuoSide(DuoMode mode, CCSPlayerController[] humans) =>
        humans.Any(player => player.Team == mode.Side) && humans.All(player => player.Team != mode.BotSide);

    private void StartDuoRosterWhenReady()
    {
        if (duoMode is not { } mode)
            return;
        var humans = ConnectedHumans();
        if (humans.Length == 0)
        {
            botFillTimer = null;
            autoRosterPending = true;
            return;
        }
        if (!CanFillDuoSide(mode, humans) || !CanSpawnBots() || Utilities.GetGameRules() is not { } rules)
        {
            botFillTimer = AddTimer(BotFillInterval, StartDuoRosterWhenReady, TimerFlags.STOP_ON_MAPCHANGE);
            return;
        }

        ConfigureDuoRoster(mode, rules);
    }

    private void ConfigureDuoRoster(DuoMode mode, CCSGameRules rules)
    {
        autoRosterPending = false;
        CancelBotFill();
        StopChallenge();
        ResetTrackerMatch();

        duoBotTarget = mode.Category == MaxBots ? MaximumDuoBots(mode.BotSide, rules) : InitialBotCount;
        if (duoBotTarget <= 0)
        {
            ReportBotFill("No opponent slots are available for 2vMax on this map.");
            return;
        }
        if (mode.Category == TenSweep)
        {
            if (!ApplyChallengeRules())
            {
                duoBotTarget = null;
                ReportBotFill("Could not apply the 16-0 match rules.");
                return;
            }
            challengeSide = mode.Side;
        }

        StartBotFill(null, mode.BotSide, duoBotTarget.Value, prepareRoster: true);
    }

    private int MaximumDuoBots(CsTeam side, CCSGameRules rules)
    {
        var slotsInUse = Utilities.GetPlayers().Count(player => player.IsValid && (!player.IsBot || player.IsHLTV));
        var availableSlots = PlayerLimit - ReservedPlayerSlots - Math.Max(2, slotsInUse);
        var spawnPoints = side == CsTeam.Terrorist ? rules.SpawnPointCount_Terrorist : rules.SpawnPointCount_CT;
        var max = Math.Min(availableSlots, spawnPoints);
        var snipers = botProfiles.Count(profile => profile.Role == "sniper");
        var rifles = botProfiles.Count - snipers;
        while (max > 0 && (max / 5 > snipers || max - max / 5 > rifles))
            max--;
        return Math.Max(0, max);
    }

    private string? DuoRosterIssue()
    {
        if (duoMode is not { } mode)
            return null;
        if (ConnectedHumans().Any(player => player.Team == mode.BotSide))
            return $"All players must join {TeamLabel(mode.Side)}. {TeamLabel(mode.BotSide)} is for bots.";
        if (botFillTimer is not null)
            return "Wait for the bot roster to finish filling.";
        if (duoBotTarget is not > 0)
            return "The bot roster is unavailable. Select the mode again to retry.";
        var bots = CurrentBots();
        if (bots.Length != duoBotTarget.Value || bots.Any(bot => bot.Team != mode.BotSide))
            return $"The match needs {duoBotTarget} bots on {TeamLabel(mode.BotSide)}. Select the mode again to refill.";
        return null;
    }
}
