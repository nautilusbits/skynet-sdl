using SkyNet.Formats;

namespace SkyNet.Formats.Tests;

/// <summary>MapWriter (editor plan step T7): a written MAP reads back with the records as given.</summary>
public sealed class MapWriterTests
{
    [Fact]
    public void Written_records_read_back_equal()
    {
        MapRecord[] records =
        [
            MapRecord.Marker(0, 500, -296, 600),
            new(MapEntityVariant.Mesh, 2000, -10, 3000) { Pitch = 5, Yaw = 1024, Roll = 7, NameIndex = 1, HitPoints = 50, State = 4, Act = 0xEF, Next = 2 },
            new(MapEntityVariant.Sprite, 2100, -20, 3100) { Sprite = 203 << 7, Param16 = 721, Param8 = 3, State = 1, Act = 0xF0 },
            new(MapEntityVariant.Light, 9000, -100, 9000) { Intensity = 300, LightEnable = 40, State = 1, Act = 0x26, Next = 0 },
            new(MapEntityVariant.Mesh, 2050, -10, 3050) { NameIndex = 0, Actor = true },
        ];

        var bytes = MapWriter.Write(16, 16, MapSetting.Interior, ["CRATE01", "BUTTON01"], records, out var offsets);
        var map = MapFile.Parse(bytes)!;

        Assert.Equal(16, map.GridWidth);
        Assert.False(map.IsOutdoor);
        Assert.Equal(["CRATE01", "BUTTON01"], map.Names.ToArray());
        Assert.Equal(records.Length, map.Entities.Length);
        Assert.False(map.WalkCut);
        for (var i = 0; i < records.Length; i++)
        {
            var r = records[i];
            Assert.True(map.TryGetEntityAt(offsets[i], out var e));
            Assert.Equal((r.Variant, r.X, r.Y, r.Z), (e.Variant, e.X, e.Y, e.Z));
            Assert.Equal((r.X >> 10, r.Z >> 10), (e.CellX, e.CellZ));
            Assert.Equal(r.Actor, e.IsActor);
            Assert.Equal(r.State, e.StateByte);
            Assert.Equal(r.Act, e.ActType);
            Assert.Equal(r.Next >= 0 ? offsets[r.Next] : 0, e.LinkNext);
        }

        Assert.True(map.TryGetEntityAt(offsets[1], out var mesh));
        Assert.Equal((5, 1024, 7, 1, 50), (mesh.Pitch, mesh.Yaw, mesh.Roll, mesh.NameIndex, mesh.HitPoints));
        Assert.Equal("BUTTON01", map.EntityName(mesh));
        Assert.True(map.TryGetEntityAt(offsets[2], out var sprite));
        Assert.Equal((203, 0, 721, 3), (sprite.SpriteBank, sprite.SpriteRecord, sprite.Param16, sprite.Param8));
        Assert.True(map.TryGetEntityAt(offsets[3], out var light));
        Assert.Equal((300, 40), (light.LightIntensity, light.LightEnable));
        Assert.True(map.TryGetEntityAt(offsets[0], out var marker));
        Assert.Equal(0, marker.MarkerType);
    }

    [Fact]
    public void Outdoor_flag_and_empty_cells_read_back()
    {
        var bytes = MapWriter.Write(64, 64, MapSetting.Outdoor, [], [MapRecord.Marker(0, 100, 0, 100)], out _);
        var map = MapFile.Parse(bytes)!;
        Assert.True(map.IsOutdoor);
        Assert.Single(map.Entities);
        Assert.Equal(0xFFFFFFFFu, map.CellHead(5, 5));
    }

    [Fact]
    public void A_record_outside_the_grid_is_refused()
    {
        Assert.Throws<ArgumentException>(() => MapWriter.Write(4, 4, MapSetting.Interior, [], [MapRecord.Marker(0, 5000, 0, 100)], out _));
        Assert.Throws<ArgumentException>(() => MapWriter.Write(4, 4, MapSetting.Interior, ["TOOLONGNAME"], [], out _));
    }
}
