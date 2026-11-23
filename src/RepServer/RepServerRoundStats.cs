using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Utils;

namespace RepServer;

public sealed partial class RepServerPlugin
{
    private sealed class RoundStats
    {
        public Dictionary<int, RoundPlayerStats> Players { get; } = new();
        public Dictionary<CsTeam, HashSet<int>> BotsByTeam { get; } = new();
        public float? BombDetonationTime { get; set; }
        public float? BombTimeRemaining { get; set; }
    }

    private sealed class RoundPlayerStats(string name, int startingKills, int startingHeadshots)
    {
        public string Name { get; } = name;
        public CsTeam Team { get; set; }
        public int Kills { get; set; }
        public HashSet<int> OpponentsKilled { get; } = new();
        public int BotKills { get; set; }
        public int Deaths { get; set; }
        public int Assists { get; set; }
        public int Headshots { get; set; }
        public int Damage { get; set; }
        public int RoundHeadshotPercentage => HeadshotPercentage(Kills, Headshots);

        public int HeadshotPercentageChange =>
            HeadshotPercentage(startingKills + Kills, startingHeadshots + Headshots) -
            HeadshotPercentage(startingKills, startingHeadshots);

        private static int HeadshotPercentage(int kills, int headshots) =>
            kills == 0 ? 0 : (int)Math.Round(100.0 * headshots / kills, MidpointRounding.AwayFromZero);
    }

    private RoundStats? roundStats;

    private void BeginRoundStats(bool liveRound)
    {
        roundStats = IsCompetitiveMode && liveRound ? new() : null;
        if (roundStats is not null)
        {
            CaptureConnectedRoundPlayers();
            foreach (var bot in CurrentBots())
                TrackRoundBot(bot);
        }
    }

    private void CaptureConnectedRoundPlayers()
    {
        foreach (var player in ConnectedHumans())
            GetRoundPlayerStats(player);
    }

    private RoundPlayerStats? GetRoundPlayerStats(CCSPlayerController? player)
    {
        if (roundStats is null || player is null || !IsHuman(player) || player.UserId is not int userId)
            return null;

        if (!roundStats.Players.TryGetValue(userId, out var stats))
        {
            var matchStats = player.ActionTrackingServices?.MatchStats;
            stats = new RoundPlayerStats(player.PlayerName, matchStats?.Kills ?? 0, matchStats?.HeadShotKills ?? 0);
            roundStats.Players.Add(userId, stats);
        }
        if (player.Team is CsTeam.Terrorist or CsTeam.CounterTerrorist)
            stats.Team = player.Team;
        return stats;
    }

    private void TrackRoundBot(CCSPlayerController player)
    {
        if (roundStats is not { } round || !player.IsBot || player.UserId is not int userId ||
            player.Team is not (CsTeam.Terrorist or CsTeam.CounterTerrorist))
            return;

        if (!round.BotsByTeam.TryGetValue(player.Team, out var bots))
        {
            bots = new HashSet<int>();
            round.BotsByTeam.Add(player.Team, bots);
        }
        bots.Add(userId);
    }

    private void TrackRoundSpawn(CCSPlayerController player)
    {
        if (player.IsBot)
            TrackRoundBot(player);
        else
            GetRoundPlayerStats(player);
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
                if (victim.UserId is int victimId)
                    attackerStats.OpponentsKilled.Add(victimId);
                if (victim.IsBot && !victim.IsHLTV)
                {
                    TrackRoundBot(victim);
                    attackerStats.BotKills++;
                }
                if (death.Headshot)
                    attackerStats.Headshots++;
            }
            if (GetRoundPlayerStats(death.Assister) is { } assisterStats)
                assisterStats.Assists++;
        }
        return HookResult.Continue;
    }

    private static CPlantedC4? FindPlantedBomb(bool tickingOnly) =>
        Utilities.FindAllEntitiesByDesignerName<CPlantedC4>("planted_c4")
            .FirstOrDefault(bomb => bomb.IsValid && bomb.C4Blow > 0 && (!tickingOnly || bomb.BombTicking));

    private HookResult TrackBombPlant(EventBombPlanted _, GameEventInfo info)
    {
        if (roundStats is { } round)
        {
            // The planted entity may finish initializing after bomb_planted.
            Server.NextWorldUpdate(() =>
            {
                if (roundStats == round)
                    round.BombDetonationTime = FindPlantedBomb(tickingOnly: true)?.C4Blow;
            });
        }
        return HookResult.Continue;
    }

    private HookResult TrackBombDefuse(EventBombDefused _, GameEventInfo info)
    {
        if (roundStats is { } round &&
            (round.BombDetonationTime ?? FindPlantedBomb(tickingOnly: false)?.C4Blow) is float detonationTime)
            round.BombTimeRemaining = Math.Max(0, detonationTime - Server.CurrentTime);
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
            PlayRoundKillSound(finishedRound);
            foreach (var stats in finishedRound.Players.Values.Where(stats => stats.Team is CsTeam.Terrorist or CsTeam.CounterTerrorist))
            {
                var opponent = stats.Team == CsTeam.Terrorist ? CsTeam.CounterTerrorist : CsTeam.Terrorist;
                var opposingBots = finishedRound.BotsByTeam.GetValueOrDefault(opponent)?.Count ?? 0;
                Server.PrintToChatAll(FormatRoundStats(stats, opposingBots));
            }

            if (finishedRound.BombTimeRemaining is float remaining)
                Server.PrintToChatAll(FormattableString.Invariant(
                    $" {ChatColors.Gold}[Bomb]{ChatColors.Default} Defused with {ChatColors.Green}{remaining:0.00} s{ChatColors.Default} left."));
        });
    }

    private static void PlayRoundKillSound(RoundStats round)
    {
        var sound = SelectRoundKillSound(round);
        if (sound is null)
            return;

        foreach (var player in ConnectedHumans())
            player.ExecuteClientCommand($"play sounds/repserver/{sound}.vsnd");
    }

    private static string? SelectRoundKillSound(RoundStats round)
    {
        HashSet<int> PlayersOn(CsTeam team)
        {
            var players = round.Players.Where(entry => entry.Value.Team == team).Select(entry => entry.Key).ToHashSet();
            if (round.BotsByTeam.TryGetValue(team, out var bots))
                players.UnionWith(bots);
            return players;
        }

        var terrorists = PlayersOn(CsTeam.Terrorist);
        var counterTerrorists = PlayersOn(CsTeam.CounterTerrorist);
        var fewestMissing = int.MaxValue;
        foreach (var stats in round.Players.Values)
        {
            if (stats.Team is not (CsTeam.Terrorist or CsTeam.CounterTerrorist))
                continue;

            var opposingPlayers = stats.Team == CsTeam.Terrorist ? counterTerrorists : terrorists;
            // Count opponents once even if respawns are enabled for a live round.
            var kills = stats.OpponentsKilled.Count(opposingPlayers.Contains);
            if (kills > 0)
                fewestMissing = Math.Min(fewestMissing, opposingPlayers.Count - kills);
        }

        return fewestMissing switch
        {
            0 => "ace",
            1 => "ace-stealer",
            2 => "acestealelele",
            _ => null
        };
    }

    private static string FormatRoundStats(RoundPlayerStats stats, int opposingBots)
    {
        static string StatValue<T>(T value, bool reached) =>
            FormattableString.Invariant($"{(reached ? ChatColors.Green : ChatColors.Red)}{value}{ChatColors.Default}");

        var hsChange = stats.HeadshotPercentageChange;
        var hsChangeText = hsChange == 0 ? "+/- 0%" : FormattableString.Invariant($"{hsChange:+0;-0}%");
        var kills = StatValue(stats.Kills, opposingBots > 0 && stats.BotKills * 2 >= opposingBots);
        var deaths = StatValue(stats.Deaths, stats.Deaths == 0);
        var assists = StatValue(stats.Assists, stats.Assists > 0);
        var roundHs = StatValue($"{stats.RoundHeadshotPercentage}%", stats.Kills > 0 && stats.Headshots * 2 >= stats.Kills);
        var totalHsChange = StatValue(hsChangeText, hsChange >= 0);
        var damage = StatValue(stats.Damage, opposingBots > 0 && stats.Damage >= opposingBots * 50);

        return $" {ChatColors.Gold}[Round]{ChatColors.Default} {stats.Name} | K {kills} / D {deaths} / A {assists}" +
            $" | HS {roundHs} ({totalHsChange}) | DMG {damage}";
    }
}
