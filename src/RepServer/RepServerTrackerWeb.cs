using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace RepServer;

public sealed partial class RepServerPlugin
{
    private sealed record TrackerSession(string TokenHash, string Name, long Expires);
    private sealed record TrackerRequest(string Player, string TokenHash, string Command, string? Argument, long Expires);
    private static readonly Regex TrackerRequestId = new(@"\A[0-9a-f]{32}\z");
    private static readonly Regex TrackerSteamId = new(@"\A[0-9]{17}\z");
    private static readonly string[] TrackerCommands =
        ["live", "pause", "unpause", "restart", "kick_bot", "16t", "16ct", "map", "mode", "set_bot_t", "set_bot_ct"];

    private static long TrackerTime => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private static string SteamKey(CCSPlayerController player) =>
        player.AuthorizedSteamID?.SteamId64.ToString(CultureInfo.InvariantCulture) ?? "";

    private void OpenTrackerAdmin(CCSPlayerController? player, string[] _, Action<string> reply)
    {
        if (player is null || !IsHuman(player) || !TrackerSteamId.IsMatch(SteamKey(player)))
        {
            reply("[Tracker] Join the server with an authenticated Steam account first.");
            return;
        }

        if (!CheckAdmin(player, reply))
            return;

        var url = Environment.GetEnvironmentVariable("REPSERVER_TRACKER_URL") ?? "http://localhost:27000";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var trackerUrl) ||
            trackerUrl.Scheme is not ("http" or "https") || trackerUrl.Fragment.Length > 0)
        {
            reply("[Tracker] The tracker link is not configured.");
            return;
        }

        var id = SteamKey(player);
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(token)));
        var session = new TrackerSession(hash, player.PlayerName, TrackerTime + 12 * 60 * 60);
        if (!TrySaveState(Path.Combine(TrackerDirectory, "sessions", id + ".json"),
                JsonSerializer.Serialize(session, TrackerJson)))
        {
            reply("[Tracker] Could not open a tracker session.");
            return;
        }
        reply("[Tracker] Open your personal admin link. It expires in 12 hours.");
        reply($"{trackerUrl.AbsoluteUri}#admin/{id}.{token}");
    }

    private void ProcessTrackerRequests()
    {
        var directory = Path.Combine(TrackerDirectory, "requests");
        if (!Directory.Exists(directory))
            return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.json").Take(16))
            {
                var id = Path.GetFileNameWithoutExtension(file);
                if (!TrackerRequestId.IsMatch(id))
                    continue;
                TrackerRequest? request = null;
                try
                {
                    request = ReadTrackerFile<TrackerRequest>($"requests/{id}.json");
                }
                catch (JsonException)
                {
                    // Drop malformed queue entries.
                }
                File.Delete(file);
                if (request is null || request.Expires < TrackerTime ||
                    request.Expires > TrackerTime + 15 || !TrackerSteamId.IsMatch(request.Player))
                    continue;
                RunTrackerRequest(id, request);
            }
            foreach (var response in Directory.EnumerateFiles(Path.Combine(TrackerDirectory, "responses"), "*.json"))
            {
                if (File.GetLastWriteTimeUtc(response) < DateTime.UtcNow.AddMinutes(-1))
                    File.Delete(response);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            Console.Error.WriteLine($"[RepServer] Could not process tracker actions: {error.GetType().Name}");
        }
    }

    private void RunTrackerRequest(string id, TrackerRequest request)
    {
        var messages = new List<string>();
        var player = ConnectedHumans().FirstOrDefault(client => SteamKey(client) == request.Player);
        var session = ReadTrackerFile<TrackerSession>($"sessions/{request.Player}.json");
        if (player is null || !IsAdmin(player) || session is null || session.Expires <= TrackerTime ||
            session.TokenHash != request.TokenHash)
        {
            messages.Add("[Tracker] An in-game admin session is required.");
        }
        else if (!TrackerCommands.Contains(request.Command) || !chatCommands.TryGetValue(request.Command, out var handler))
        {
            messages.Add("[Tracker] Unknown tracker action.");
        }
        else
        {
            handler(player, request.Argument is null ? [] : [request.Argument], messages.Add);
        }

        var result = messages.Count == 0 ? "Action sent to the server." : string.Join("\n", messages);
        TrySaveState(Path.Combine(TrackerDirectory, "responses", id + ".json"), JsonSerializer.Serialize(result));
    }
}
