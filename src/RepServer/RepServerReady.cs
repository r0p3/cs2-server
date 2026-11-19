using System.Text;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace RepServer;

public sealed partial class RepServerPlugin
{
    private const string ReadyPrefix = "[READY] ";
    private const int PlayerNameMaxBytes = 127;
    private sealed record ReadyPlayer(int UserId, string OriginalName, string MarkedName);

    private readonly Dictionary<int, ReadyPlayer> readyPlayers = new();
    private float? readyWarmupStart;
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

        if (ready)
        {
            var originalName = player.PlayerName;
            var markedName = MarkedReadyName(originalName);
            SetDisplayName(player, markedName);
            readyPlayers[player.Slot] = new(userId, originalName, markedName);
        }
        else
        {
            ClearReadyPlayer(player.Slot);
        }

        reply(ready
            ? "[RepServer] You are ready. Hold Tab to see who is ready."
            : "[RepServer] You are no longer marked as ready.");
        QueueReadyCheck();
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
            if (HasConnectingHumans() || !IsReadinessWarmup())
                return;

            var humans = ConnectedHumans();
            if (humans.Length == 0 || !humans.All(IsReady))
                return;

            ResetReadyPlayers();
            Server.PrintToChatAll("[RepServer] Everyone is ready. Ending warmup.");
            Server.ExecuteCommand("mp_warmup_end");
        });
    }
}
