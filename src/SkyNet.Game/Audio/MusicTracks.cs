using SkyNet.Formats;

namespace SkyNet.Game.Audio;

/// <summary>Which song plays where: a map's maptype (marker type 6, its sub+2 value; 0 without
/// one) indexes the twelve-entry track table of SKYNET.EXE (VA 0x4f9f9 in v1.00, 0x24-byte entries
/// naming gamedata\TNNN.hmi). The 300-series songs are the multiplayer arenas; menus play
/// TITLE.HMI.</summary>
public static class MusicTracks
{
    public const string Title = "TITLE.HMI";
    /// <summary>The marker type that carries a map's maptype.</summary>
    public const int MapTypeMarker = 6;

    public static readonly IReadOnlyList<string> ByMapType =
    [
        "T200.HMI", "T205.HMI", "T204.HMI", "T206.HMI", "T201.HMI", "T202.HMI",
        "T205.HMI", "T300NEW.HMI", "T301.HMI", "T303.HMI", "T302.HMI", "T305.HMI",
    ];

    /// <summary>The map's maptype: the first type-6 marker's value, 0 when there is none.</summary>
    public static int MapTypeOf(MapFile map)
    {
        foreach (var e in map.Entities)
        {
            if (e.Variant == MapEntityVariant.Sprite && e.IsMarker && e.MarkerType == MapTypeMarker)
            {
                return e.Param16;
            }
        }

        return 0;
    }

    /// <summary>The song of a maptype; out of range plays maptype 0's.</summary>
    public static string ForMapType(int mapType) =>
        ByMapType[mapType >= 0 && mapType < ByMapType.Count ? mapType : 0];

    public static string ForMap(MapFile map) => ForMapType(MapTypeOf(map));
}
