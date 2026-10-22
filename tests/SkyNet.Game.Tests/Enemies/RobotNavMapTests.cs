using System.Diagnostics;
using System.Numerics;
using SkyNet.Game.Collision;
using SkyNet.Game.Enemies;
using SkyNet.Game.Player;
using SkyNet.Game.Tests.Behaviour;
using SkyNet.Game.Weapons;
using Xunit.Abstractions;

namespace SkyNet.Game.Tests.Enemies;

/// <summary>The walkers' nav meshes on the campaign maps: bake times and sizes, and IMPROVED's
/// chase with and without them (how many reach the soldier, how often they back off stuck).</summary>
public class RobotNavMapTests(ITestOutputHelper output)
{
    private const float Dt = 1f / 60f;
    private const float ChaseTime = 120f;

    /// <summary>Every campaign map with walkers (missions 1-8: MAP.200-289), baked as a session does.</summary>
    [BehaviourDataFact]
    public void Bake_times_on_the_campaign_maps()
    {
        var data = BehaviourData.Data;
        long total = 0, bytes = 0;
        for (var m = 200; m < 290; m++)
        {
            if (LevelCollision.Load(data, m) is not { } level)
            {
                continue;
            }

            var robots = new Robots(SkyNet.Data.GameTables.Ais, new EnemyModels(data), level.World);
            robots.Populate(level.Map);
            var walkers = robots.All.Count(r => r.NavBody != null);
            if (walkers == 0)
            {
                continue;
            }

            var clock = Stopwatch.StartNew();
            var nav = RobotNav.Bake(level.World, robots, [PlayerStart.Find(level.Map, level.Wld).Feet], null);
            var ms = clock.ElapsedMilliseconds;
            total += ms;
            var sizes = new List<string>();
            foreach (var mesh in nav.Meshes)
            {
                using var s = new MemoryStream();
                mesh.Save(s);
                bytes += s.Length;
                sizes.Add($"r{mesh.Agent.Radius:0} {s.Length / 1024} KB");
            }

            Assert.NotEmpty(nav.Meshes);
            output.WriteLine($"MAP.{m} {(level.Wld != null ? "outdoor" : "indoor ")} {walkers,3} walkers: {ms,5} ms, {string.Join(", ", sizes)}");
        }

        output.WriteLine($"total {total} ms, {bytes / 1024} KB");
    }

    /// <summary>The walkers in reach of the soldier (at the start, or on MAP.271's catwalks), told
    /// where he is every look, two minutes: on the mesh they back off stuck no more often.</summary>
    [BehaviourDataTheory]
    [InlineData(271, 0f, 0f, 0f)]
    [InlineData(271, 35500f, 4888f, -42900f)]
    [InlineData(271, 35000f, 4888f, -43530f)]
    [InlineData(217, 0f, 0f, 0f)]
    [InlineData(220, 0f, 0f, 0f)]
    public void Walkers_chase_the_soldier_on_the_mesh(int map, float x, float y, float z)
    {
        Vector3? at = x == 0f ? null : new Vector3(x, y, z);
        var walk = Chase(map, at, nav: false);
        var nav = Chase(map, at, nav: true);
        var where = at == null ? "start" : $"{x:0},{z:0}";
        output.WriteLine($"MAP.{map} {where} walk: {walk}");
        output.WriteLine($"MAP.{map} {where} nav:  {nav}");

        Assert.True(nav.Unstuck <= walk.Unstuck, $"{nav.Unstuck} back-offs on the mesh, {walk.Unstuck} without");
    }

    private readonly record struct ChaseResult(int Walkers, int Reached, float Mean, int Unstuck)
    {
        public override string ToString() => $"{Walkers} walkers, {Reached} reached (mean {Mean:0.0} s), {Unstuck} back-offs";
    }

    private static ChaseResult Chase(int map, Vector3? at, bool nav)
    {
        var data = BehaviourData.Data;
        var level = LevelCollision.Load(data, map)!.Value;
        var world = level.World;
        var start = PlayerStart.Find(level.Map, level.Wld).Feet;
        var feet = at ?? start;
        if (world.Raycast(feet + new Vector3(0f, 20f, 0f), -Vector3.UnitY, 400f, out var floor))
        {
            feet = floor.Point;
        }

        var weapons = new WeaponSystem(WeaponTable.Load(data), world) { Player = new Soldier { Feet = feet } };
        var robots = new Robots(SkyNet.Data.GameTables.Ais, new EnemyModels(data), world, weapons, seed: 5) { Ai = RobotAi.Improved, HoldFire = true };
        robots.Populate(level.Map);
        if (nav)
        {
            robots.Nav = RobotNav.Bake(world, robots, [start], null);
        }

        var walkers = robots.All.Where(e => e.NavBody != null && !e.Hidden && TickWindow.ContainsImproved(e.Position, feet)).ToList();
        var reached = new Dictionary<Enemy, float>();
        for (var t = 0; t < ChaseTime / Dt; t++)
        {
            // A hit's alert on every look: each walker knows where the soldier is.
            if (t % 4 == 0)
            {
                foreach (var w in walkers.Where(w => w.Alive))
                {
                    w.TakeDamage(new Damage(0f, DamageKind.Direct, w.Centre, true));
                }
            }

            robots.Tick(Dt, feet);
            foreach (var w in walkers)
            {
                var flat = Vector2.Distance(new Vector2(w.Position.X, w.Position.Z), new Vector2(feet.X, feet.Z));
                if (!reached.ContainsKey(w) && flat <= (w.Type?.Near ?? 100) + 80f && MathF.Abs(w.Position.Y + w.FootOffset - feet.Y) < 60f)
                {
                    reached[w] = t * Dt;
                }
            }
        }

        return new ChaseResult(walkers.Count, reached.Count, reached.Count > 0 ? reached.Values.Average() : 0f, walkers.Sum(w => w.Unstuck));
    }
}
