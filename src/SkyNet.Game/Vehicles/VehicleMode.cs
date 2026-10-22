using SkyNet.Formats;
using SkyNet.Game.Campaign;
using SkyNet.Game.Hud;

namespace SkyNet.Game.Vehicles;

/// <summary>What the player rides (mission table +0x08: 0 foot, 4 jeep, 8 HK).</summary>
public enum VehicleMode { Foot = 0, Jeep = 1, Hk = 2 }

/// <summary>
/// Which vehicle a map is played in, from the mission table (docs/game-logic/vehicles.md; the Godot
/// port's main.gd _vehicle_for_map):
/// <list type="bullet">
/// <item>SkyNET: DOS sets the mode once at the mission start (0x11c99e) and every map the mission's
/// exits reach inherits it, so a map is played in the mode of the mission whose start map shares
/// its decade (MAP.220/260 and their side maps: jeep; MAP.270 and MAP.271's tunnel: HK);</item>
/// <item>Future Shock: its vehicle missions (20, 60, 100, 110, 120, 160, 170) are one outdoor map
/// with no exit, so only the start map itself rides; the rest of the decade (MAP.103, MAP.129
/// rooms no mission reaches) is on foot.</item>
/// </list>
/// </summary>
public static class VehicleModes
{
    /// <summary>The table's mode as a vehicle.</summary>
    public static VehicleMode From(PlayerMode mode) => mode switch
    {
        PlayerMode.Jeep => VehicleMode.Jeep,
        PlayerMode.Hk => VehicleMode.Hk,
        _ => VehicleMode.Foot,
    };

    /// <summary>The vehicle MAP.<paramref name="map"/> is played in during
    /// <paramref name="mission"/> (null: a loose map, looked up by its decade or start map).</summary>
    public static VehicleMode ForMission(GameKind game, Mission? mission, int map)
    {
        var table = MissionTable.For(game);
        if (game == GameKind.FutureShock)
        {
            return table.StartingOn(map) is { } exact ? From(exact.Mode) : VehicleMode.Foot;
        }

        var m = mission ?? table.ForMap(map);
        return m is { } row && row.StartMap / 10 == map / 10 ? From(row.Mode) : VehicleMode.Foot;
    }

    /// <summary>The HUD's vehicle (the crosshair, the rocket record).</summary>
    public static HudVehicle Hud(VehicleMode mode) => (HudVehicle)(int)mode;
}
