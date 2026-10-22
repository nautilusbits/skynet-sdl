using RetroEng.Loop;
using SkyNet.Game.Campaign;

namespace SkyNet.Game.Demos;

/// <summary>
/// The per-tick state hash of a <see cref="GameSession"/> (docs/demos.md), in components so a
/// desync says where it is: the soldier and his ride, the weapons (kit, cooldowns, shots in
/// flight), the robots and their wreckage, the level behaviour, the simulation generator, and the
/// session (mission, map, phase, counter, pickups, drops, clock). A few probes (where the soldier
/// stands, his health) are stored beside the hashes so a desync can show the values. Cosmetic
/// state (effects, impact sparks, sounds, the cosmetic generator) is left out on purpose.
/// </summary>
public static class SessionHash
{
    public static readonly string[] Components = ["player", "weapons", "robots", "level", "random", "session"];
    public static readonly string[] ProbeNames = ["x", "y", "z", "yaw", "health", "armour"];

    /// <summary>Fills <paramref name="hashes"/> (<see cref="Components"/>) and
    /// <paramref name="probes"/> (<see cref="ProbeNames"/>).</summary>
    public static void Compute(GameSession s, Span<uint> hashes, Span<float> probes)
    {
        var p = s.Player;
        var h = new StateHash();
        h.Add(p.Position);
        h.Add(p.Velocity);
        h.Add(p.Yaw);
        h.Add(p.Pitch);
        h.Add(p.Eye);
        h.Add(p.OnFloor);
        h.Add(p.Noclip);
        h.Add(p.FloorOwner);
        h.Add(s.Soldier.Health);
        h.Add(s.Soldier.Armour);
        h.Add(s.Soldier.God);
        if (s.Ride?.Save() is { } ride)
        {
            h.Add((int)ride.Mode);
            h.Add(ride.Hull);
            h.Add(ride.Speed);
            h.Add(ride.CarYaw);
            h.Add(ride.AimYaw);
            h.Add(ride.AimPitch);
            h.Add(ride.Energy);
            h.Add(ride.Overheated);
        }

        hashes[0] = h.Value32;

        h = new StateHash();
        var inv = s.Weapons.Inventory;
        var kit = inv.Save();
        foreach (var owned in kit.Owned)
        {
            h.Add(owned);
        }

        foreach (var pool in kit.Pools)
        {
            h.Add(pool);
        }

        h.Add(kit.Primary);
        h.Add(kit.Thrown);
        h.Add(inv.FireCooldown);
        h.Add(inv.ThrowCooldown);
        foreach (var shot in s.Weapons.Projectiles)
        {
            h.Add(shot.Weapon.Index);
            h.Add(shot.Position);
            h.Add(shot.Velocity);
            h.Add(shot.Age);
            h.Add(shot.Done);
            h.Add(shot.Bounces);
            h.Add(shot.Followed);
        }

        hashes[1] = h.Value32;

        h = new StateHash();
        if (s.Robots is { } robots)
        {
            foreach (var r in robots.All)
            {
                h.Add(r.Position);
                h.Add(r.Yaw);
                h.Add(r.Hp);
                h.Add(r.Dead);
                h.Add(r.Hidden);
                h.Add(r.Sees);
                h.Add(r.SenseCount);
                h.Add(r.Perceived);
                h.Add(r.Alerted);
                h.Add(r.ShotsFired);
                h.Add(r.DormantDistance);
                h.Add(r.Root.Yaw);
                h.Add(r.Root.Pitch);
                if (r.Brain is { } b)
                {
                    h.Add(b.Pc);
                    h.Add(b.WaitLeft);
                    h.Add(b.Frame);
                }
            }

            h.Add(robots.PendingBlasts);
            h.Add(robots.Wreckage.Count);
            foreach (var part in robots.Wreckage.Parts)
            {
                h.Add(part.Live);
                h.Add(part.Position);
            }
        }

        hashes[2] = h.Value32;

        h = new StateHash();
        s.Behaviour?.HashInto(ref h);
        h.Add(s.Water?.Surface);
        h.Add(s.Breath.Under);
        hashes[3] = h.Value32;

        h = new StateHash();
        h.Add(s.Random.State);
        hashes[4] = h.Value32;

        h = new StateHash();
        h.Add(s.Mission?.Number ?? 0);
        h.Add(s.MapNumber);
        h.Add((int)s.Phase);
        h.Add(s.Clock);
        h.Add(s.Progress.Left);
        h.Add(s.Progress.Complete);
        h.Add(s.Progress.Failed);
        foreach (var (offset, at) in s.PickupsLeft)
        {
            h.Add(offset);
            h.Add(at);
        }

        foreach (var d in s.Drops)
        {
            h.Add(d.Id);
            h.Add(d.Sprite);
        }

        h.Add(s.Torpedo is { Riding: true });
        hashes[5] = h.Value32;

        probes[0] = p.Position.X;
        probes[1] = p.Position.Y;
        probes[2] = p.Position.Z;
        probes[3] = p.Yaw;
        probes[4] = s.Soldier.Health;
        probes[5] = s.Soldier.Armour;
    }

    /// <summary>All components folded into one word (a run's fingerprint).</summary>
    public static ulong Fingerprint(GameSession s)
    {
        Span<uint> hashes = stackalloc uint[Components.Length];
        Span<float> probes = stackalloc float[ProbeNames.Length];
        Compute(s, hashes, probes);
        var h = new StateHash();
        foreach (var w in hashes)
        {
            h.Add(w);
        }

        return h.Value;
    }
}
