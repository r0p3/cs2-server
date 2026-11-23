using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Utils;

namespace RepServer;

public sealed partial class RepServerPlugin
{
    private sealed class RoundPlayerStats(string name, int startingKills, int startingHeadshots)
    {
        public string Name { get; } = name;
        public bool Participated { get; set; }
        public int Kills { get; set; }
        public int Deaths { get; set; }
        public int Assists { get; set; }
        public int Headshots { get; set; }
        public int Damage { get; set; }

        public int HeadshotPercentageChange =>
            HeadshotPercentage(startingKills + Kills, startingHeadshots + Headshots) -
            HeadshotPercentage(startingKills, startingHeadshots);

        private static int HeadshotPercentage(int kills, int headshots) =>
            kills == 0 ? 0 : (int)Math.Round(100.0 * headshots / kills, MidpointRounding.AwayFromZero);
    }

    private Dictionary<int, RoundPlayerStats>? roundStats;

    private void BeginRoundStats(bool liveRound)
    {
        roundStats = IsCompetitiveMode && liveRound ? new() : null;
        if (roundStats is not null)
            CaptureConnectedRoundPlayers();
    }

    private void CaptureConnectedRoundPlayers()
    {
        foreach (var player in ConnectedHumans())
            CapturePlayerRoundBaseline(player);
    }

    private void CapturePlayerRoundBaseline(CCSPlayerController player) =>
        GetRoundPlayerStats(player, player.TeamNum is (byte)CsTeam.Terrorist or (byte)CsTeam.CounterTerrorist);

    private RoundPlayerStats? GetRoundPlayerStats(CCSPlayerController? player, bool participating = true)
    {
        if (roundStats is null || player is null || !IsHuman(player) || player.UserId is not int userId)
            return null;

        if (!roundStats.TryGetValue(userId, out var stats))
        {
            var matchStats = player.ActionTrackingServices?.MatchStats;
            stats = new RoundPlayerStats(player.PlayerName, matchStats?.Kills ?? 0, matchStats?.HeadShotKills ?? 0);
            roundStats.Add(userId, stats);
        }
        stats.Participated |= participating;
        return stats;
    }

    private HookResult TrackRoundDeath(EventPlayerDeath death, GameEventInfo _)
    {
        if (roundStats is null)
            return HookResult.Continue;

        var victim = death.Userid;
        if (GetRoundPlayerStats(victim) is { } victimStats)
            victimStats.Deaths++;

        if (victim is { IsValid: true } && death.Attacker is { IsValid: true } attacker &&
            attacker.TeamNum != victim.TeamNum)
        {
            if (GetRoundPlayerStats(attacker) is { } attackerStats)
            {
                attackerStats.Kills++;
                if (death.Headshot)
                    attackerStats.Headshots++;
            }
            if (GetRoundPlayerStats(death.Assister) is { } assisterStats)
                assisterStats.Assists++;
        }
        return HookResult.Continue;
    }

    private HookResult TrackRoundDamage(EventPlayerHurt hurt, GameEventInfo _)
    {
        if (roundStats is not null && hurt.Userid is { IsValid: true } victim &&
            hurt.Attacker is { IsValid: true } attacker && attacker.TeamNum != victim.TeamNum &&
            GetRoundPlayerStats(attacker) is { } stats)
            stats.Damage += hurt.DmgHealth;
        return HookResult.Continue;
    }

    private void QueueRoundStats(EventRoundEnd round)
    {
        if (roundStats is not { } finishedRound || (RoundEndReason)round.Reason == RoundEndReason.GameCommencing ||
            Utilities.GetGameRules() is not { WarmupPeriod: false })
            return;

        CaptureConnectedRoundPlayers();
        // The final kill and the round result can arrive in the same update.
        Server.NextWorldUpdate(() =>
        {
            if (roundStats != finishedRound)
                return;

            roundStats = null;
            foreach (var stats in finishedRound.Values.Where(stats => stats.Participated))
            {
                Server.PrintToChatAll(FormattableString.Invariant(
                    $"[Round] {stats.Name} | K {stats.Kills} / D {stats.Deaths} / A {stats.Assists} | HS {stats.HeadshotPercentageChange:+0;-0;0}% | DMG {stats.Damage}"));
            }
        });
    }
}
