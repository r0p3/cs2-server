using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace RepServer;

public sealed partial class RepServerPlugin
{
    private static readonly int[] EarlyKillMilestones = [10, 25, 50, 100, 250, 500];
    private const int KillMilestoneInterval = 1000;

    private enum KillWeapon { Other, Molotov, Grenade, Pistol }

    private void ShowLifetimeStats(CCSPlayerController? player, string[] _, Action<string> reply)
    {
        var stats = GetPersonalTrackerStats(player, reply);
        if (stats is null)
            return;

        reply(FormattableString.Invariant(
            $"[Stats] Lifetime | Kills: {stats.Kills:N0} | Deaths: {stats.Deaths:N0} | Assists: {stats.Assists:N0} | HS: {stats.Headshots:N0} ({stats.HeadshotPercentage}%)"));
        reply(FormattableString.Invariant(
            $"[Stats] Aces: {stats.Aces:N0} | 1v5+ clutches: {stats.Clutches:N0} | Defuses: {stats.Defuses:N0}"));
    }

    private void ShowKillMilestones(CCSPlayerController? player, string[] _, Action<string> reply)
    {
        var stats = GetPersonalTrackerStats(player, reply);
        if (stats is null)
            return;

        reply(FormattableString.Invariant(
            $"[Milestones] Targets: {string.Join(", ", EarlyKillMilestones)}, then every {KillMilestoneInterval:N0} kills."));
        foreach (var (category, kills) in new[]
        {
            ("Bot kills", stats.BotKills), ("Molotov/incendiary kills", stats.MolotovKills),
            ("HE grenade kills", stats.GrenadeKills), ("Pistol kills", stats.PistolKills)
        })
        {
            var earlyTarget = EarlyKillMilestones.FirstOrDefault(target => target > kills);
            var next = earlyTarget > 0 ? earlyTarget : ((long)kills / KillMilestoneInterval + 1) * KillMilestoneInterval;
            reply(FormattableString.Invariant($"[Milestones] {category}: {kills:N0} | Next: {next:N0}"));
        }
    }

    private TrackerStats? GetPersonalTrackerStats(CCSPlayerController? player, Action<string> reply)
    {
        if (player is null || !IsHuman(player) || !TrackerSteamId.IsMatch(SteamKey(player)))
        {
            reply("[Tracker] Join the server with an authenticated Steam account to see your stats.");
            return null;
        }

        return trackerHistory.Players.GetValueOrDefault(SteamKey(player)) ?? new TrackerStats { Name = player.PlayerName };
    }

    private static KillWeapon ClassifyKillWeapon(string weapon)
    {
        if (weapon.StartsWith("weapon_", StringComparison.Ordinal))
            weapon = weapon[7..];
        return weapon switch
        {
            "inferno" or "molotov" or "incgrenade" => KillWeapon.Molotov,
            "hegrenade" => KillWeapon.Grenade,
            "glock" or "hkp2000" or "usp_silencer" or "p250" or "deagle" or
                "revolver" or "elite" or "tec9" or "fiveseven" or "cz75a" => KillWeapon.Pistol,
            _ => KillWeapon.Other
        };
    }

    private static void CountWeaponKill(TrackerStats stats, KillWeapon weapon)
    {
        switch (weapon)
        {
            case KillWeapon.Molotov: stats.MolotovKills++; break;
            case KillWeapon.Grenade: stats.GrenadeKills++; break;
            case KillWeapon.Pistol: stats.PistolKills++; break;
        }
    }

    private void AnnounceKillMilestones(RoundClient attacker, RoundClient victim, KillWeapon weapon)
    {
        var stats = trackerHistory.Players[attacker.Id];
        if (victim.Bot)
            AnnounceKillMilestone(attacker, victim, "BOT", stats.BotKills);

        switch (weapon)
        {
            case KillWeapon.Molotov:
                AnnounceKillMilestone(attacker, victim, "MOLOTOV / INCENDIARY", stats.MolotovKills);
                break;
            case KillWeapon.Grenade:
                AnnounceKillMilestone(attacker, victim, "HE GRENADE", stats.GrenadeKills);
                break;
            case KillWeapon.Pistol:
                AnnounceKillMilestone(attacker, victim, "PISTOL", stats.PistolKills);
                break;
        }
    }

    private void AnnounceKillMilestone(RoundClient attacker, RoundClient victim, string category, int kills)
    {
        if (kills < 1 || (!EarlyKillMilestones.Contains(kills) && kills % KillMilestoneInterval != 0))
            return;

        FlushTracker();
        Server.PrintToChatAll($" {ChatColors.Gold}=== {category} KILL MILESTONE ==={ChatColors.Default}");
        Server.PrintToChatAll(FormattableString.Invariant(
            $" {ChatColors.Green}{attacker.Name}{ChatColors.Default} reached {ChatColors.Gold}{kills:N0} kills!{ChatColors.Default} Milestone victim: {ChatColors.Gold}{victim.Name}{ChatColors.Default}"));
    }
}
