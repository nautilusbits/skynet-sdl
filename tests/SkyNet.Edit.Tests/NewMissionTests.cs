using RetroEng.Content;
using RetroEng.Loop;
using SkyNet.Formats;
using SkyNet.Game.Campaign;
using SkyNet.Game.Mods;
using SkyNet.Game.Player;
using SkyNet.Game.Weapons;

namespace SkyNet.Edit.Tests;

/// <summary>File > New Map / New Mission (editor plan step T7): the files read back as written, numbers
/// in use are refused, and a new mission in a scratch mod is listed by the game and played from its start
/// through its doorway to MISSION COMPLETE.</summary>
[Collection(EditModsCollection.Name)]
public sealed class NewMissionTests : IDisposable
{
    private const string Mod = "t7";
    private static readonly PlayerActions Keys = new(Forward: 0, Back: 1, Left: 2, Right: 3, Run: 4, Jump: 5, Crouch: 6);
    private static readonly WeaponInput Idle = new(false, false, false);
    private const int UseEvery = GameSession.TicksPerSecond / 2;

    private readonly string _mods = Directory.CreateTempSubdirectory("skynet-new-mission-").FullName;

    public void Dispose()
    {
        ModContent.Unmount();
        try
        {
            Directory.Delete(_mods, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static GameData Data() => GameData.Open(EditDocumentTests.DataDir!)!;

    [MapDataFact]
    public void New_map_files_read_back_as_written()
    {
        var data = Data();
        var spec = new NewMapSpec(ModMaps.FirstNew, MapSetting.Outdoor) { Height = 30, Tile = 5, MapType = 3 };
        var files = NewMaps.Build(data, spec, MapEnd.Doorway, ModMaps.FirstNew + 1);

        var map = MapFile.Parse(files.Map)!;
        Assert.True(map.IsOutdoor);
        Assert.Equal(64, map.GridWidth);
        Assert.Equal(3, SkyNet.Game.Audio.MusicTracks.MapTypeOf(map));
        Assert.Contains(map.Entities, e => e.IsMarker && e.MarkerType == 0);
        Assert.True(map.TryGetEntityAt(files.Doorway, out var door));
        Assert.Equal((0xF0, ModMaps.FirstNew + 1, 0), (door.ActType, door.Param16, door.Param8));

        var wld = WldFile.Read(files.Wld)!;
        Assert.Equal(files.Wld, wld.Write());
        Assert.All(wld.Layer(WldLayer.Height).ToArray(), b => Assert.Equal(30, b));
        Assert.All(wld.Layer(WldLayer.Tile).ToArray(), b => Assert.Equal(5, b));
        var ground = WldTerrain.Parse(files.Wld)!;
        Assert.Equal(WldTerrain.HeightCurve[30], ground.HeightAt(door.X, door.Z));

        // Written into a mod: the same bytes come back, and the game reads the map through the mod.
        var written = NewMaps.Write(Path.Combine(_mods, Mod), files);
        Assert.Equal([ModInfo.FileName, ModMaps.PathOf(spec.Number), MapTerrains.PathOf(spec.Number)], written.ToArray());
        Assert.Equal(files.Map, File.ReadAllBytes(Path.Combine(_mods, Mod, ModMaps.PathOf(spec.Number))));
        ModContent.Mount(_mods, [Mod]);
        Assert.Equal(map.Entities.Length, data.LoadMap(spec.Number)!.Entities.Length);
        Assert.Equal(WldTerrain.HeightCurve[30], data.LoadWld(spec.Number)!.HeightAt(door.X, door.Z));
    }

    [MapDataFact]
    public void Numbers_in_use_are_refused()
    {
        var data = Data();
        var maps = NewMaps.Taken(data, null);
        Assert.Contains(210, maps);
        Assert.NotNull(NewMaps.Refuse(new NewMapSpec(210, MapSetting.Outdoor), data, maps));
        Assert.NotNull(NewMaps.Refuse(new NewMapSpec(ModMaps.LastNew + 1, MapSetting.Outdoor), data, maps));
        Assert.Null(NewMaps.Refuse(new NewMapSpec(ModMaps.FirstNew, MapSetting.Outdoor), data, maps));
        Assert.Null(NewMaps.Refuse(new NewMapSpec(ModMaps.FirstNew, MapSetting.Interior), data, maps));
        Assert.NotNull(NewMaps.Refuse(new NewMapSpec(ModMaps.FirstNew, MapSetting.Interior) { Room = 210 }, data, maps));

        // A map and a mission a mod has are taken too.
        var spec = Spec(ModMissions.FirstNew, ModMaps.FirstNew);
        NewMissions.Write(data, Path.Combine(_mods, Mod), spec);
        using var vfs = new ContentRoot(_mods).Mount([], [Mod]);
        maps = NewMaps.Taken(data, vfs);
        var missions = NewMissions.Taken(data.Game, vfs);
        Assert.Contains(ModMaps.FirstNew, maps);
        Assert.Equal(ModMaps.FirstNew + 1, NewMaps.FirstFree(maps));
        Assert.Equal(ModMissions.FirstNew + 1, NewMissions.FirstFree(missions));
        Assert.NotNull(NewMaps.Refuse(new NewMapSpec(ModMaps.FirstNew, MapSetting.Outdoor), data, maps));
        Assert.NotNull(NewMissions.Refuse(spec, data, missions, maps));
        Assert.NotNull(NewMissions.Refuse(spec with { Number = 1 }, data, missions, maps));
        Assert.NotNull(NewMissions.Refuse(Spec(ModMissions.FirstNew + 1, ModMaps.FirstNew), data, missions, maps));
        Assert.Null(NewMissions.Refuse(Spec(ModMissions.FirstNew + 1, ModMaps.FirstNew + 1), data, missions, maps));
    }

    /// <summary>T7's check: a new two-map mission in a scratch mod is listed by the game, and walking ahead
    /// from its start takes the doorway to the second map and the pad there ends it: MISSION COMPLETE.
    /// SkyNET's data, and Future Shock's when it is beside it.</summary>
    [MapDataFact]
    public void New_mission_plays_from_start_through_the_doorway_to_complete()
    {
        var skynet = Data();
        Play(skynet, Mod);
        if (GameDataLocator.OtherGameDir(skynet.Dir) is { } dir && GameData.Open(dir) is { Game: GameKind.FutureShock } shock)
        {
            Play(shock, Mod + "fs");
        }
    }

    private void Play(GameData data, string mod)
    {
        var spec = Spec(ModMissions.FirstNew, ModMaps.FirstNew, ModMaps.FirstNew + 1);
        var written = NewMissions.Write(data, Path.Combine(_mods, mod), spec);
        Assert.Contains(MissionFile.PathOf(spec.Number), written);
        Assert.Contains(ModMissions.BriefingPath(NewMissions.BriefingName(ModMaps.FirstNew)), written);

        ModContent.Mount(_mods, [mod]);
        var mission = MissionTable.Playable(data.Game).ByNumber(spec.Number);
        Assert.Equal(new Mission(spec.Number, ModMaps.FirstNew, PlayerMode.Foot), mission);
        Assert.Null(MissionTable.For(data.Game).ByNumber(spec.Number));

        var session = new GameSession(data);
        session.StartMission(mission!.Value);
        session.Soldier.God = true;
        Assert.Equal(ModMaps.FirstNew, session.MapNumber);
        Assert.Equal(1, session.Progress.Total);

        Assert.True(WalkUntil(session, s => s.MapNumber == ModMaps.FirstNew + 1, 30f), $"{data.Game}: still on MAP.{session.MapNumber:D3} at {session.Player.Position}");
        Assert.Equal(SessionPhase.Playing, session.Phase);
        Assert.True(WalkUntil(session, s => s.Phase == SessionPhase.Complete, 30f), $"{data.Game}: phase {session.Phase}, {session.Progress.Left} left at {session.Player.Position}");
        Assert.Equal(0, session.Progress.Left);
    }

    /// <summary>An interior made from another map's walls loads and bakes, its start inside them.</summary>
    [MapDataFact]
    public void New_interior_loads_and_bakes()
    {
        var data = Data();
        var spec = Spec(ModMissions.FirstNew, ModMaps.FirstNew) with
        {
            Maps = [new NewMapSpec(ModMaps.FirstNew, MapSetting.Interior)],
        };
        NewMissions.Write(data, Path.Combine(_mods, Mod), spec);
        ModContent.Mount(_mods, [Mod]);

        var session = new GameSession(data);
        session.StartMission(MissionTable.Playable(data.Game).ByNumber(spec.Number)!.Value);
        Assert.Equal(ModMaps.FirstNew, session.MapNumber);
        Assert.False(session.Map!.IsOutdoor);
        Assert.NotNull(SkyNet.Game.Baking.MapBaker.Bake(data, ModMaps.FirstNew));
        Assert.True(session.World.Raycast(session.Player.Position + new System.Numerics.Vector3(0f, 50f, 0f), -System.Numerics.Vector3.UnitY, 400f, out _));
    }

    private static NewMissionSpec Spec(int number, params int[] maps) =>
        new(number, [.. maps.Select(m => new NewMapSpec(m, MapSetting.Outdoor))]);

    // Holds forward (walking, no turn), pressing the use key twice a second as a player at a doorway
    // does, until the check holds or the time runs out.
    private static bool WalkUntil(GameSession s, Func<GameSession, bool> done, float seconds)
    {
        var forward = new TickInput(TickInput.Bit(Keys.Forward), 0UL, 0UL, 0f, 0f, 0f, 0f, 0f);
        for (var i = 0; i < seconds * GameSession.TicksPerSecond; i++)
        {
            if (done(s))
            {
                return true;
            }

            s.Tick(forward, Keys, Idle, i % UseEvery == 0);
        }

        return done(s);
    }
}
