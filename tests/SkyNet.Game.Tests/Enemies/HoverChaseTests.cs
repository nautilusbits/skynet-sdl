using System.Numerics;
using RetroEng.Physics;
using SkyNet.Formats;
using SkyNet.Game.Collision;
using SkyNet.Game.Enemies;
using SkyNet.Game.Tests.Behaviour;

namespace SkyNet.Game.Tests.Enemies;

/// <summary>Issue #50. A hover (state 6, v1.01 0x137545) with the soldier perceived moves straight
/// at him in 3D, dw4 units a second (0x13bf96, 0x138868), its yaw only its look, and stops within
/// dw6 (0x137576). No ground test: Future Shock's flencer hangs in the air and flies at him.</summary>
public class HoverChaseTests
{
    private const float Dt = 1f / 35f;
    private const int Map = 70, Flencer = 0x1e34f;

    private sealed class ShockDataFactAttribute : FactAttribute
    {
        public ShockDataFactAttribute()
        {
            if (BehaviourData.Dir is not { } dir || GameDataLocator.OtherGameDir(dir) == null)
            {
                Skip = "no Future Shock data beside SKYNET_DATA's SkyNET folder";
            }
        }
    }

    [ShockDataFact]
    public void TheFlencerFliesAtTheSoldier()
    {
        var data = GameData.Open(GameDataLocator.OtherGameDir(BehaviourData.Dir!)!, BehaviourData.Dir)!;
        var map = data.LoadMap(Map)!;
        var world = LevelCollision.Build(data, map, data.LoadWld(Map)).World;
        var robots = new Robots(SkyNet.Data.GameTables.Ais, new EnemyModels(data), world, seed: 7) { HoldFire = true };
        robots.Populate(map);
        var flencer = robots.All.Single(r => r.MarkerOffset == Flencer);

        // Issue #50's save: the soldier on the hill, the flencer in sight below him.
        flencer.Position = new Vector3(15781.842f, 1504.7148f, -48535.297f);
        var soldier = new Vector3(15543.671f, 1892.32f, -47657.938f);
        robots.Tick(Dt, soldier + new Vector3(10f, 0f, 0f));   // he steps up: a trail point a hover can take (0x13b7b6)
        var before = Vector3.Distance(flencer.Position, soldier);
        for (var t = 0f; t < 3f; t += Dt)
        {
            robots.Tick(Dt, soldier);
        }

        Assert.True(flencer.Sees);
        Assert.InRange(before - Vector3.Distance(flencer.Position, soldier), 175f * 3f * 0.8f, 175f * 3f * 1.05f);
    }

    /// <summary>A hover fires only in a firing pose: its caller tests the frame flags' 0x100
    /// (0x1377ad, [+0x70]). The flencer's close-in loop (anim 0x5ec6e, flags 8) has none.</summary>
    [ShockDataFact]
    public void TheFlencerFiresOnlyInAFiringPose()
    {
        var data = GameData.Open(GameDataLocator.OtherGameDir(BehaviourData.Dir!)!, BehaviourData.Dir)!;
        var map = data.LoadMap(Map)!;
        var world = LevelCollision.Build(data, map, data.LoadWld(Map)).World;
        var weapons = new SkyNet.Game.Weapons.WeaponSystem(SkyNet.Game.Weapons.WeaponTable.Load(data), world) { Player = new Soldier() };
        var robots = new Robots(SkyNet.Data.GameTables.Ais, new EnemyModels(data), world, weapons, seed: 7) { Difficulty = Difficulty.High };
        robots.Populate(map);
        var flencer = robots.All.Single(r => r.MarkerOffset == Flencer);
        flencer.Position = new Vector3(15781.842f, 1504.7148f, -48535.297f);
        var soldier = flencer.Position + new Vector3(0f, -100f, 60f);
        var outOfPose = 0;
        for (var t = 0f; t < 20f; t += Dt)
        {
            var shots = flencer.ShotsFired;
            robots.Tick(Dt, soldier);
            outOfPose += flencer.ShotsFired > shots && flencer.Brain is { FiringPose: false } ? 1 : 0;
        }

        Assert.Equal(0, outOfPose);
    }

    /// <summary>Out of sight, a hover follows the soldier's trail (v1.01: a point each 5 units he
    /// moves, ring 0x90d3 written by 0x122a32; the next point within 15, 0x13b817): round the end of
    /// a wall it cannot see past, to where he went.</summary>
    [BehaviourDataFact]
    public void AHoverFollowsTheTrailOutOfSight()
    {
        // A wall at x = 200 from z = 0 north: the soldier walks east, then north behind it.
        var a = new Vector3(200f, -500f, 0f);
        var b = new Vector3(200f, -500f, 3000f);
        var c = new Vector3(200f, 3000f, 3000f);
        var d = new Vector3(200f, 3000f, 0f);
        var world = new CollisionWorld().Add(new TriangleGrid([new Triangle(a, b, c), new Triangle(a, c, d)], 256f));
        var data = BehaviourData.Data;
        var robots = new Robots(SkyNet.Data.GameTables.Ais, new EnemyModels(data), world, seed: 7) { HoldFire = true };
        var hover = robots.Add(SkyNet.Data.GameTables.Ais.Types.First(t => t.Name == "flencer").Index, new Vector3(0f, 0f, 0f));
        Vector3[] path = [new(0f, 0f, -500f), new(400f, 0f, -500f), new(400f, 0f, 500f)];
        var soldier = path[0];
        var leg = 1;
        for (var t = 0f; t < 25f; t += Dt)
        {
            if (leg < path.Length)
            {
                var to = path[leg] - soldier;
                var step = 200f * Dt;
                soldier = to.Length() <= step ? path[leg++] : soldier + Vector3.Normalize(to) * step;
            }

            robots.Tick(Dt, soldier);
        }

        Assert.InRange(Vector3.Distance(hover.Position, soldier), 0f, 200f);
    }
    /// <summary>DOS's hovers never block on robots (records carry 0x20, the hover's slide masks it,
    /// 0x13b35e): three on one spot stay stacked under CLASSIC; IMPROVED pushes them apart.</summary>
    [BehaviourDataTheory]
    [InlineData(RobotAi.Classic, false)]
    [InlineData(RobotAi.Improved, true)]
    public void HoversOnOneSpotSpread(RobotAi ai, bool spread)
    {
        const float L = 20000f;
        var floor = new List<Triangle>
        {
            new(new Vector3(-L, 0f, -L), new Vector3(-L, 0f, L), new Vector3(L, 0f, L)),
            new(new Vector3(-L, 0f, -L), new Vector3(L, 0f, L), new Vector3(L, 0f, -L)),
        };
        var world = new CollisionWorld().Add(new TriangleGrid(floor, 256f));
        var robots = new Robots(SkyNet.Data.GameTables.Ais, new EnemyModels(BehaviourData.Data), world, seed: 7) { HoldFire = true, Ai = ai };
        var type = SkyNet.Data.GameTables.Ais.Types.First(t => t.Name == "flencer").Index;
        var spot = new Vector3(0f, 150f, 0f);
        Enemy[] hovers = [robots.Add(type, spot), robots.Add(type, spot), robots.Add(type, spot)];
        var soldier = new Vector3(0f, 0f, -1500f);
        for (var t = 0f; t < 4f; t += Dt)
        {
            robots.Tick(Dt, soldier);
        }

        var room = hovers[0].BodySize;
        var closest = float.MaxValue;
        for (var i = 0; i < hovers.Length; i++)
        {
            for (var j = i + 1; j < hovers.Length; j++)
            {
                closest = MathF.Min(closest, Vector3.Distance(hovers[i].Centre, hovers[j].Centre));
            }
        }

        Assert.True(spread ? closest >= room : closest < 1f, $"closest {closest}, room {room}");
    }
}
