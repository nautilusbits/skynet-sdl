using System.Numerics;
using SkyNet.Game.Campaign;
using SkyNet.Game.Navigation;
using SkyNet.Game.Vehicles;

namespace SkyNet.Game.Net;

/// <summary>
/// The bots' nav meshes of a deathmatch arena: baked from the map's static collision over the
/// arena's spots (spawns, items, vehicles) and a margin round them, one for the soldier and one for
/// the jeep; slow bakes are cached (<see cref="NavCache"/>: cache/nav/SkyNet-MAP.605.nav,
/// SkyNet-MAP.605-jeep.nav).
/// </summary>
public static class DmNav
{
    /// <summary>How far round the arena's spots the mesh reaches.</summary>
    private const float Margin = 2048f;

    /// <summary>The mesh for <paramref name="session"/>'s current map; null before a map is in.</summary>
    public static NavMesh? For(GameSession session)
    {
        if (session.StaticWorld is not { } world || session.Map is not { } map)
        {
            return null;
        }

        var arena = DmArena.From(map, session.Wld, session.MapNumber);
        return NavCache.Get($"{session.Data.Game}-MAP.{session.MapNumber:D3}", world, NavAgents.Soldier, Cells(arena, NavAgents.SoldierCells));
    }

    /// <summary>What bots in <paramref name="vehicles"/> need on <paramref name="session"/>'s map:
    /// the jeep's mesh (only when a jeep is parked) and the HK's ceiling.</summary>
    internal static BotRides Rides(GameSession session, IEnumerable<DmVehicle> vehicles)
    {
        if (session.StaticWorld is not { } world || session.Map is not { } map)
        {
            return BotRides.None;
        }

        var ceiling = VehicleController.CeilingOf(map);
        if (!vehicles.Any(v => v.Kind == VehicleMode.Jeep))
        {
            return new BotRides(null, ceiling);
        }

        var arena = DmArena.From(map, session.Wld, session.MapNumber);
        var jeep = NavCache.Get($"{session.Data.Game}-MAP.{session.MapNumber:D3}-jeep", world, NavAgents.Jeep, Cells(arena, NavAgents.JeepCells));
        return new BotRides(jeep, ceiling);
    }

    /// <summary><paramref name="cells"/> over the arena's spots and the margin; the whole map when it has none.</summary>
    private static NavBuild Cells(DmArena arena, NavBuild cells)
    {
        var spots = arena.Spawns.Select(s => s.Feet).Concat(arena.ItemSpots).Concat(arena.JeepSpots).Concat(arena.HkSpots).ToList();
        if (spots.Count == 0)
        {
            return cells;
        }

        var lo = new Vector2(spots.Min(p => p.X), spots.Min(p => p.Z)) - new Vector2(Margin);
        var hi = new Vector2(spots.Max(p => p.X), spots.Max(p => p.Z)) + new Vector2(Margin);
        return cells with { AreaMin = lo, AreaMax = hi };
    }
}
