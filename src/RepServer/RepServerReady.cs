using System.Text;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace RepServer;

public sealed partial class RepServerPlugin
{
    private const string ReadyPrefix = "[READY] ";
    private const int PlayerNameMaxBytes = 127;
    private sealed record ReadyPlayer(int UserId, string OriginalName, string MarkedName);
    private readonly Dictionary<int, ReadyPlayer> readyPlayers = new();
    private float? readyWarmupStart;
    private double nextReadyHudUpdate;
    private bool readyCheckQueued;

    private static bool IsHuman(CCSPlayerController? player) =>
        player is { IsValid: true, IsBot: false, IsHLTV: false };

    private static CCSPlayerController[] ConnectedHumans() =>
        Utilities.GetPlayers().Where(IsHuman).ToArray();

    private bool IsReadinessWarmup()
    {
        var rules = Utilities.GetGameRules();
        if (rules?.WarmupPeriod != true)
        {
            ResetReadyPlayers();
            return false;
        }

        if (readyWarmupStart != rules.WarmupPeriodStart)
        {
            ResetReadyPlayers();
            readyWarmupStart = rules.WarmupPeriodStart;
        }

        return true;
    }

    private void ResetReadyPlayers()
    {
        foreach (var slot in readyPlayers.Keys.ToArray())
            ClearReadyPlayer(slot);
        readyWarmupStart = null;
    }

    private bool IsReady(CCSPlayerController player) =>
        readyPlayers.TryGetValue(player.Slot, out var state) && player.UserId == state.UserId;

    private void ClearReadyPlayer(int slot)
    {
        if (readyPlayers.Remove(slot, out var state) &&
            Utilities.GetPlayerFromSlot(slot) is { IsValid: true } player &&
            player.UserId == state.UserId && player.PlayerName == state.MarkedName)
            SetDisplayName(player, state.OriginalName);
    }

    private static void SetDisplayName(CCSPlayerController player, string name)
    {
        player.PlayerName = name;
        Utilities.SetStateChanged(player, "CBasePlayerController", "m_iszPlayerName");
    }

    private static string MarkedReadyName(string name)
    {
        var marked = new StringBuilder(ReadyPrefix);
        var remaining = PlayerNameMaxBytes - ReadyPrefix.Length;
        foreach (var rune in name.EnumerateRunes())
        {
            remaining -= rune.Utf8SequenceLength;
            if (remaining < 0)
                break;
            marked.Append(rune.ToString());
        }
        return marked.ToString();
    }

    private void SetReady(CCSPlayerController? player, Action<string> reply, bool ready)
    {
        if (player is null || !IsHuman(player) || player.UserId is not int userId)
        {
            reply("[RepServer] Use !ready in the game chat.");
            return;
        }

        if (!IsReadinessWarmup())
        {
            reply("[RepServer] Warmup is not active.");
            return;
        }

        if (ready == IsReady(player))
        {
            reply(ready ? "[RepServer] You are already ready." : "[RepServer] You are already marked as not ready.");
            return;
        }

        var name = ready ? player.PlayerName : readyPlayers[player.Slot].OriginalName;
        if (ready)
        {
            var markedName = MarkedReadyName(name);
            SetDisplayName(player, markedName);
            readyPlayers[player.Slot] = new(userId, name, markedName);
        }
        else
        {
            ClearReadyPlayer(player.Slot);
        }

        var humans = ConnectedHumans();
        var status = ready ? "ready" : "unready";
        var statusColor = ready ? ChatColors.Green : ChatColors.Red;
        Server.PrintToChatAll(
            $" {ChatColors.Gold}[RepServer]{ChatColors.Default} {name} is {statusColor}{status}{ChatColors.Default}, {humans.Count(IsReady)} out of {humans.Length}");
        nextReadyHudUpdate = 0;
        QueueReadyCheck();
    }

    private void UpdateReadyHud()
    {
        var now = Server.EngineTime;
        if (now < nextReadyHudUpdate)
            return;
        nextReadyHudUpdate = now + 1;
        if (!IsReadinessWarmup())
            return;

        var humans = ConnectedHumans();
        var readyCount = humans.Count(IsReady);
        foreach (var player in humans)
        {
            var ready = IsReady(player);
            var label = ready ? "Ready" : "Unready";
            var color = ready ? "#81E874" : "#FF7272";
            player.PrintToCenterHtml(
                $"<font class='fontSize-s' color='{color}'>{label}</font> <font class='fontSize-s'>| {readyCount}/{humans.Length}</font>",
                duration: 2);
        }
    }

    private void ShowReadyStatus(Action<string> reply)
    {
        if (!IsReadinessWarmup())
        {
            reply("[RepServer] Warmup is not active.");
            return;
        }

        PrintReadyStatus(reply);
    }

    private void PrintReadyStatus(Action<string> reply)
    {
        var humans = ConnectedHumans();
        var ready = humans.Where(IsReady).Select(player => readyPlayers[player.Slot].OriginalName).ToArray();
        var waiting = humans.Where(player => !IsReady(player)).Select(player => player.PlayerName).ToArray();

        reply($"[RepServer] {ready.Length}/{humans.Length} ready.");
        foreach (var group in ready.Chunk(4))
            reply("[RepServer] Ready: " + string.Join(", ", group));
        foreach (var group in waiting.Chunk(4))
            reply("[RepServer] Waiting for: " + string.Join(", ", group));
        if (HasConnectingHumans())
            reply("[RepServer] Also waiting for players to finish connecting.");
    }

    private void QueueReadyCheck()
    {
        if (readyCheckQueued || readyPlayers.Count == 0)
            return;

        readyCheckQueued = true;
        Server.NextWorldUpdate(() =>
        {
            readyCheckQueued = false;
            if (HasConnectingHumans() || !IsReadinessWarmup() || DuoRosterIssue() is not null)
                return;

            var humans = ConnectedHumans();
            if (humans.Length == 0 || !humans.All(IsReady))
                return;

            ResetReadyPlayers();
            Server.PrintToChatAll(
                $" {ChatColors.Gold}[RepServer]{ChatColors.Green} Everyone is ready.{ChatColors.Default} Ending warmup.");
            Server.ExecuteCommand("mp_warmup_end");
        });
    }
}
