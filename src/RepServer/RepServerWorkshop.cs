using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace RepServer;

public sealed partial class RepServerPlugin
{
    private const string WorkshopMapFile = "/var/lib/repserver/workshop-maps.json";
    private const string WorkshopDetailsUrl =
        "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/";
    private static readonly HttpClient WorkshopHttp = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
        MaxResponseContentBufferSize = 1024 * 1024
    };
    private static readonly Regex WorkshopAliasSeparators = new(@"[^a-z0-9]+");
    private sealed record WorkshopMap(ulong Id, string Alias, string Title);

    private readonly Dictionary<string, WorkshopMap> workshopMaps = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource workshopLifetime = new();
    private bool workshopLookupPending;
    private bool workshopCatalogReadable = true;

    private IEnumerable<string> MapChoices(bool includeStandard) =>
        (includeStandard ? maps : Array.Empty<string>()).Concat(workshopMaps.Keys.Order(StringComparer.Ordinal));

    private void LoadWorkshopMaps()
    {
        try
        {
            var items = JsonSerializer.Deserialize<WorkshopMap[]>(File.ReadAllText(WorkshopMapFile))
                ?? throw new JsonException("Empty workshop catalog");
            foreach (var item in items)
            {
                if (item is null || item.Id == 0 || string.IsNullOrEmpty(item.Alias) ||
                    !MapName.IsMatch(item.Alias) || maps.Contains(item.Alias, StringComparer.Ordinal) ||
                    !workshopMaps.TryAdd(item.Alias, item))
                    throw new JsonException("Invalid alias or duplicate workshop entry");
            }
        }
        catch (FileNotFoundException)
        {
            // The catalog is created when the first map is registered.
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            workshopMaps.Clear();
            workshopCatalogReadable = false;
            Console.Error.WriteLine($"[RepServer] Kunde inte läsa workshop-maps.json: {exception.GetType().Name}");
        }
    }

    private void AddWorkshopMap(CCSPlayerController? player, string arguments, Action<string> reply)
    {
        if (player is not null && !IsHuman(player))
            return;

        var parts = arguments.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 2)
        {
            reply("[RepServer] Använd !add_map <Steam Workshop-länk> [namn].");
            return;
        }
        if (!TryGetWorkshopId(parts[0].Trim('"', '<', '>'), out var id))
        {
            reply("[RepServer] Hittade inget kart-ID i länken. Kopiera kartans länk från Steam Workshop.");
            return;
        }

        var alias = parts.Length == 2 ? parts[1].Trim('"').ToLowerInvariant() : null;
        if (alias is not null && !MapName.IsMatch(alias))
        {
            reply("[RepServer] Kartnamnet får ha 1–64 bokstäver (a–z), siffror eller understreck.");
            return;
        }
        if (!workshopCatalogReadable)
        {
            reply("[RepServer] Workshop-listan kunde inte läsas. Kontrollera data/state/workshop-maps.json.");
            return;
        }
        if (workshopMaps.Values.FirstOrDefault(item => item.Id == id) is { } existing)
        {
            reply($"[RepServer] Kartan finns redan. Använd !map {existing.Alias}.");
            return;
        }
        if (workshopLookupPending)
        {
            reply("[RepServer] En Workshop-karta kontrolleras redan. Försök igen strax.");
            return;
        }

        workshopLookupPending = true;
        reply("[RepServer] Hämtar kartans uppgifter från Steam Workshop …");
        _ = LookupWorkshopMap(id, alias, player?.Slot, player?.UserId, workshopLifetime.Token);
    }

    private static bool TryGetWorkshopId(string link, out ulong id)
    {
        id = 0;
        if (link.StartsWith("steamcommunity.com/", StringComparison.OrdinalIgnoreCase) ||
            link.StartsWith("www.steamcommunity.com/", StringComparison.OrdinalIgnoreCase))
            link = "https://" + link;

        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("https" or "http") ||
            uri.Host is not ("steamcommunity.com" or "www.steamcommunity.com") ||
            uri.AbsolutePath.TrimEnd('/') is not ("/sharedfiles/filedetails" or "/workshop/filedetails"))
            return false;

        var ids = uri.Query.TrimStart('?').Split('&')
            .Where(parameter => parameter.StartsWith("id=", StringComparison.Ordinal))
            .ToArray();
        return ids.Length == 1 &&
            ulong.TryParse(ids[0].AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out id) && id != 0;
    }

    private async Task LookupWorkshopMap(ulong id, string? alias, int? slot, int? userId, CancellationToken cancellation)
    {
        string? title = null;
        var error = "[RepServer] Steam hittade ingen tillgänglig Counter-Strike-karta för länken.";
        try
        {
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["itemcount"] = "1",
                ["publishedfileids[0]"] = id.ToString(CultureInfo.InvariantCulture)
            });
            using var response = await WorkshopHttp.PostAsync(WorkshopDetailsUrl, form, cancellation);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
            var item = document.RootElement.GetProperty("response").GetProperty("publishedfiledetails")[0];
            if (item.GetProperty("result").GetInt32() == 1 &&
                item.GetProperty("consumer_app_id").GetInt32() == 730 &&
                item.TryGetProperty("tags", out var tags) &&
                tags.EnumerateArray().Any(tag => string.Equals(tag.GetProperty("tag").GetString(), "Map", StringComparison.OrdinalIgnoreCase)))
                title = item.GetProperty("title").GetString()?.Trim();
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or
            JsonException or InvalidOperationException or KeyNotFoundException or IndexOutOfRangeException or FormatException)
        {
            error = "[RepServer] Steam Workshop svarade inte med kartuppgifter. Försök igen strax.";
        }

        if (cancellation.IsCancellationRequested)
            return;
        Server.NextWorldUpdate(() =>
        {
            if (cancellation.IsCancellationRequested)
                return;
            workshopLookupPending = false;
            var player = slot is int playerSlot ? Utilities.GetPlayerFromSlot(playerSlot) : null;
            void Reply(string message)
            {
                Console.WriteLine(message);
                if (player is { IsValid: true } && player.UserId == userId)
                    player.PrintToChat(message);
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                Reply(error);
                return;
            }
            SaveWorkshopMap(id, alias, title, Reply);
        });
    }

    private void SaveWorkshopMap(ulong id, string? alias, string title, Action<string> reply)
    {
        if (alias is null)
        {
            var suffix = title.IndexOfAny(['(', '[']);
            var name = suffix > 0 ? title[..suffix] : title;
            alias = WorkshopAliasSeparators.Replace(name.ToLowerInvariant(), "_").Trim('_');
            if (!MapName.IsMatch(alias))
            {
                reply("[RepServer] Ange ett kort kartnamn med !add_map <länk> <namn>.");
                return;
            }
        }

        if (maps.Contains(alias, StringComparer.Ordinal) || workshopMaps.ContainsKey(alias))
        {
            reply($"[RepServer] Namnet {alias} är upptaget. Använd !add_map <länk> <annat_namn>.");
            return;
        }

        var item = new WorkshopMap(id, alias, title);
        if (!TrySaveState(WorkshopMapFile, JsonSerializer.Serialize(workshopMaps.Values.Append(item))))
        {
            reply("[RepServer] Workshop-kartan kunde inte sparas. Kontrollera data/state på servern.");
            return;
        }
        workshopMaps.Add(alias, item);
        reply($"[RepServer] {alias} finns nu i kartmenyn. Du kan också skriva !map {alias}.");
    }
}
