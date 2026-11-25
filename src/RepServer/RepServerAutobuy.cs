using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Utils;

namespace RepServer;

public sealed partial class RepServerPlugin
{
    private sealed record BuyItem(string Id, string Slot, string[] Sides, int Price);
    private sealed class AutobuyProfile
    {
        [JsonPropertyName("T")]
        public string[]? Terrorist { get; set; }

        [JsonPropertyName("CT")]
        public string[]? CounterTerrorist { get; set; }

        public bool Enabled { get; set; } = true;
    }

    private static readonly Regex BuyItemName = new(@"\A[a-z0-9_]+\z");
    private Dictionary<string, BuyItem> buyItems = new(StringComparer.Ordinal);
    private readonly HashSet<string> autobought = new();

    private void LoadTrackerBuyItems()
    {
        var path = Environment.GetEnvironmentVariable("REPSERVER_AUTOBUY_CATALOG") ?? "/opt/repserver/autobuy.json";
        try
        {
            var items = JsonSerializer.Deserialize<BuyItem[]>(File.ReadAllText(path), TrackerJson) ?? [];
            buyItems = items.Where(item => item.Price > 0 && BuyItemName.IsMatch(item.Id))
                .ToDictionary(item => item.Id, StringComparer.Ordinal);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            Console.Error.WriteLine($"[RepServer] Could not load the autobuy catalog: {error.Message}");
        }
    }

    private void SaveAutobuy(CCSPlayerController? player, string[] args, Action<string> reply)
    {
        if (player is null || !IsHuman(player) || !TrackerSteamId.IsMatch(SteamKey(player)))
        {
            reply("[RepServer] Join the server with an authenticated Steam account to use autobuy.");
            return;
        }

        var option = args.Length == 1 ? args[0].ToLowerInvariant() : null;
        if (args.Length != 0 && option is not ("on" or "off"))
        {
            reply("[RepServer] Use !autobuy to save your gear, !autobuy off to pause, or !autobuy on to resume.");
            return;
        }

        var path = $"loadouts/{SteamKey(player)}.json";
        try
        {
            var profile = ReadTrackerFile<AutobuyProfile>(path) ?? new();
            if (option is not null)
            {
                profile.Enabled = option == "on";
                var message = profile.Enabled ? "Autobuy enabled." : "Autobuy disabled. Your loadouts are saved.";
                WriteAutobuyProfile(path, profile, message, reply);
                return;
            }

            if (TrackerClient(player) is not { } client || !player.PawnIsAlive ||
                player.PlayerPawn.Value is not { IsValid: true, ItemServices: { }, WeaponServices: { } } pawn)
            {
                reply("[RepServer] Join T or CT and stay alive to save your loadout.");
                return;
            }
            if (buyItems.Count == 0)
            {
                reply("[RepServer] Autobuy is unavailable.");
                return;
            }

            var side = TeamLabel(client.Team);
            var gear = CaptureAutobuyGear(pawn, side);
            if (client.Team == CsTeam.Terrorist)
                profile.Terrorist = gear;
            else
                profile.CounterTerrorist = gear;
            profile.Enabled = true;
            WriteAutobuyProfile(path, profile, $"Saved your {side} loadout. Autobuy enabled.", reply);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            Console.Error.WriteLine($"[RepServer] Could not load the autobuy profile: {error.GetType().Name}");
            reply("[RepServer] Could not update your autobuy settings.");
        }
    }

    private static void WriteAutobuyProfile(string path, AutobuyProfile profile, string message, Action<string> reply)
    {
        if (TrySaveState(Path.Combine(TrackerDirectory, path), JsonSerializer.Serialize(profile, TrackerJson)))
            reply("[RepServer] " + message);
        else
            reply("[RepServer] Could not update your autobuy settings.");
    }

    private string[] CaptureAutobuyGear(CCSPlayerPawn pawn, string side)
    {
        var weapons = pawn.WeaponServices!;
        var equipped = Inventory(weapons);
        var gear = equipped.Select(weapon =>
        {
            var name = weapon.DesignerName;
            return name.StartsWith("weapon_", StringComparison.Ordinal) ? name[7..] : "";
        }).ToHashSet(StringComparer.Ordinal);
        var armor = pawn.ItemServices!.As<CCSPlayer_ItemServices>();
        if (pawn.ArmorValue > 0 || armor.HasHelmet)
            gear.Add(armor.HasHelmet ? "vesthelm" : "vest");
        if (armor.HasDefuser)
            gear.Add("defuser");

        return buyItems.Values.Where(item => gear.Contains(item.Id) && item.Sides.Contains(side))
            .OrderBy(item => item.Slot switch
            {
                "primary" => 0,
                "armor" => 1,
                "secondary" => 2,
                "equipment" => 3,
                _ => 4
            })
            .SelectMany(item => item.Id == "flashbang"
                ? Enumerable.Repeat(item.Id, Math.Min(2, GrenadeCount(weapons, equipped, "weapon_flashbang")))
                : new[] { item.Id })
            .ToArray();
    }

    private void BuyTrackerLoadouts()
    {
        foreach (var player in ConnectedHumans())
        {
            if (TrackerClient(player) is { } client)
                BuyTrackerLoadout(player, client);
        }
    }

    private void BuyTrackerLoadout(CCSPlayerController player, RoundClient client)
    {
        if (!trackerRoundOpen || autobought.Contains(client.Id) || !player.PawnIsAlive ||
            player.PlayerPawn.Value is not { IsValid: true, ItemServices: { }, WeaponServices: { } } pawn ||
            player.InGameMoneyServices is not { } money || !CanAutobuy(pawn, client.Team))
            return;

        var side = TeamLabel(client.Team);
        string[]? items;
        try
        {
            var profile = ReadTrackerFile<AutobuyProfile>($"loadouts/{client.Id}.json");
            items = profile is { Enabled: true }
                ? client.Team == CsTeam.Terrorist ? profile.Terrorist : profile.CounterTerrorist
                : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            Console.Error.WriteLine($"[RepServer] Could not load an autobuy profile: {error.GetType().Name}");
            return;
        }

        if (items is not { Length: > 0 and <= 12 } || !autobought.Add(client.Id))
            return;

        var itemServices = pawn.ItemServices.As<CCSPlayer_ItemServices>();
        var uniqueSlots = new HashSet<string>(StringComparer.Ordinal);
        var orders = new List<(BuyItem Item, int DesiredCount)>();
        foreach (var group in items.OfType<string>().GroupBy(alias => alias, StringComparer.Ordinal))
        {
            if (!buyItems.TryGetValue(group.Key, out var item) || !item.Sides.Contains(side) ||
                (item.Slot is "primary" or "secondary" or "armor" && !uniqueSlots.Add(item.Slot)))
                continue;

            orders.Add((item, item.Id == "flashbang" ? Math.Min(group.Count(), 2) : 1));
        }

        var inventory = Inventory(pawn.WeaponServices);
        var requiredCash = orders.Sum(order => MissingGearCost(
            pawn, itemServices, inventory, order.Item, order.DesiredCount));
        if (requiredCash > money.Account)
            return;

        var spent = 0;
        foreach (var (item, desiredCount) in orders)
        {
            for (var count = 0; count < desiredCount; count++)
            {
                var price = BuyTrackerItem(player, pawn, itemServices, item, money.Account, desiredCount);
                if (price <= 0)
                    break;
                money.Account -= price;
                spent += price;
            }
        }

        if (spent > 0)
        {
            money.CashSpentThisRound += spent;
            money.TotalCashSpent += spent;
            Utilities.SetStateChanged(player, "CCSPlayerController", "m_pInGameMoneyServices");
        }
    }

    private static bool CanAutobuy(CCSPlayerPawn pawn, CsTeam side)
    {
        if (Utilities.GetGameRules() is not { WarmupPeriod: false, BuyTimeEnded: false })
            return false;
        if (pawn.InBuyZone)
            return true;
        var buyAnywhere = ConVar<int>.Find("mp_buy_anywhere")?.Value;
        return buyAnywhere == 1 || buyAnywhere == 2 && side == CsTeam.Terrorist ||
            buyAnywhere == 3 && side == CsTeam.CounterTerrorist;
    }

    private static long MissingGearCost(CCSPlayerPawn pawn, CCSPlayer_ItemServices items,
        CBasePlayerWeapon[] inventory, BuyItem item, int desiredCount)
    {
        if (item.Slot == "armor")
            return ArmorCost(pawn, items, item);
        if (item.Id == "defuser")
            return items.HasDefuser ? 0 : item.Price;
        var weaponName = "weapon_" + item.Id;
        if (item.Slot == "grenade")
            return (long)item.Price * Math.Max(0, desiredCount - GrenadeCount(pawn.WeaponServices!, inventory, weaponName));
        return inventory.Any(weapon => weapon.DesignerName == weaponName) ? 0 : item.Price;
    }

    private static int ArmorCost(CCSPlayerPawn pawn, CCSPlayer_ItemServices items, BuyItem item)
    {
        var helmet = item.Id == "vesthelm";
        if (pawn.ArmorValue >= 100 && (!helmet || items.HasHelmet))
            return 0;
        if (helmet && items.HasHelmet)
            return 650;
        return helmet && pawn.ArmorValue >= 100 ? item.Price - 650 : item.Price;
    }

    private static int BuyTrackerItem(CCSPlayerController player, CCSPlayerPawn pawn,
        CCSPlayer_ItemServices items, BuyItem item, int cash, int desiredCount)
    {
        if (item.Slot == "armor")
        {
            var price = ArmorCost(pawn, items, item);
            if (price <= 0 || cash < price)
                return 0;

            pawn.ArmorValue = 100;
            items.HasHelmet |= item.Id == "vesthelm";
            Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_ArmorValue");
            Utilities.SetStateChanged(pawn, "CBasePlayerPawn", "m_pItemServices");
            return price;
        }
        if (cash < item.Price)
            return 0;
        if (item.Id == "defuser")
        {
            if (items.HasDefuser)
                return 0;
            items.HasDefuser = true;
            Utilities.SetStateChanged(pawn, "CBasePlayerPawn", "m_pItemServices");
            return item.Price;
        }

        var weaponName = "weapon_" + item.Id;
        var weapons = pawn.WeaponServices!;
        var inventory = Inventory(weapons);
        var isGrenade = item.Slot == "grenade";
        var grenadeCount = isGrenade ? GrenadeCount(weapons, inventory, weaponName) : 0;
        if (isGrenade)
        {
            var limit = item.Id == "flashbang" ? ConVar<int>.Find("ammo_grenade_limit_flashbang")?.Value ?? 2 : 1;
            if (grenadeCount >= Math.Min(desiredCount, limit) || !CanAutobuyGrenade(weapons, inventory))
                return 0;
        }
        else if (inventory.Any(weapon => weapon.DesignerName == weaponName))
            return 0;

        var occupiedSlot = item.Slot switch
        {
            "primary" => gear_slot_t.GEAR_SLOT_RIFLE,
            "secondary" => gear_slot_t.GEAR_SLOT_PISTOL,
            _ => (gear_slot_t?)null
        };
        var oldWeapon = occupiedSlot is null ? null :
            inventory.FirstOrDefault(weapon => weapon.As<CCSWeaponBase>().VData?.GearSlot == occupiedSlot);

        if (oldWeapon is { IsValid: true })
        {
            var active = weapons.ActiveWeapon;
            var previous = active.Raw;
            var oldHandle = oldWeapon.EntityHandle.Raw;
            // CS2 drops the active weapon, so select the item being replaced first.
            active.Raw = oldHandle;
            items.DropActivePlayerWeapon(oldWeapon);
            if (previous != oldHandle && new CHandle<CBasePlayerWeapon>(previous).IsValid)
                active.Raw = previous;
        }

        var purchased = player.GiveNamedItem<CCSWeaponBase>(weaponName);
        if (isGrenade && GrenadeCount(weapons, Inventory(weapons), weaponName) > grenadeCount)
            return item.Price;
        if (purchased is { IsValid: true })
        {
            if (!isGrenade && purchased.OwnerEntity.Value?.Handle == pawn.Handle)
                return item.Price;
            if (purchased.OwnerEntity.Value?.Handle != pawn.Handle)
                purchased.Remove();
        }
        return 0;
    }

    private static CBasePlayerWeapon[] Inventory(CPlayer_WeaponServices weapons) =>
        weapons.MyWeapons.Select(handle => handle.Value).OfType<CBasePlayerWeapon>()
            .Where(weapon => weapon.IsValid).ToArray();

    private static int GrenadeCount(CPlayer_WeaponServices weapons, CBasePlayerWeapon[] inventory, string? name = null)
    {
        var ammo = weapons.Ammo;
        var carried = 0;
        foreach (var weapon in inventory)
        {
            if ((name is not null && weapon.DesignerName != name) ||
                weapon.As<CCSWeaponBase>().VData is not { GearSlot: gear_slot_t.GEAR_SLOT_GRENADES } data)
                continue;
            var ammoType = data.PrimaryAmmoType;
            carried += ammoType < ammo.Length ? Math.Max(1, (int)ammo[ammoType]) : 1;
        }
        return carried;
    }

    private static bool CanAutobuyGrenade(CPlayer_WeaponServices weapons, CBasePlayerWeapon[] inventory) =>
        ConVar<bool>.Find("mp_buy_allow_grenades")?.Value != false &&
        GrenadeCount(weapons, inventory) < (ConVar<int>.Find("ammo_grenade_limit_total")?.Value ?? 4);
}
