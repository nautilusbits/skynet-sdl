using SkyNet.Game.Campaign;
using SkyNet.Game.Hud;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Vehicles;

/// <summary>
/// The player's vehicle in a <see cref="GameSession"/>: on every map entered it picks the mode
/// from the mission table (<see cref="VehicleModes.ForMission"/>) and climbs in or out, hooking
/// the three places a ride changes:
/// <list type="bullet">
/// <item>the body: <see cref="Player.PlayerController.Mover"/> = the <see cref="VehicleController"/>;</item>
/// <item>the guns: <see cref="WeaponSystem.Mount"/> = <see cref="VehicleGuns"/> (no weapon keys;
/// FIRE the gun, THROW the rockets; the on-foot kit is untouched and back on foot);</item>
/// <item>the target: <see cref="WeaponSystem.Player"/> = the <see cref="Hull"/>, so robots aim at
/// it and their shots and blasts wear the hull, not the soldier; a wrecked hull kills him.</item>
/// </list>
/// Needs no renderer; <see cref="CockpitRenderer"/> draws the cockpit. It is the session's
/// <see cref="GameSession.Ride"/>: a save keeps the ride, a load climbs back in.
/// </summary>
public sealed class VehicleRig : IVehicleSaver
{
    private readonly GameSession _session;

    public VehicleRig(GameSession session)
    {
        _session = session;
        Controller = new VehicleController(session.World, session.Player)
        {
            Sound = id => session.Sounds?.Play2D(id, -6f),
        };
        Hull = new Hull(Controller, session.Soldier);
        Hull.Hurt += _ => session.Sounds?.Play2D(HitSound, -3f);
        Ramming = new Ramming(session.World, session.Weapons.Targets);
        Ramming.Rammed += e => session.Logger?.Invoke(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"[ram] {e.Points:0} at {e.Speed:0} u/s -> {e.Outcome}"));
        Controller.Ram = Ramming;
        session.Ride = this;
        session.Moved = Fence;
    }

    /// <summary>The hurt sound (the Godot port's take_damage).</summary>
    public const string HitSound = "HIT2.RAW";

    public VehicleController Controller { get; }
    public Hull Hull { get; }
    public VehicleGuns? Guns { get; private set; }
    /// <summary>The jeep's ram test, on the session's targets.</summary>
    public Ramming Ramming { get; }
    /// <summary>The current map's fence (marker pairs 30-39); it holds on foot too.</summary>
    public MapBorder Border { get; private set; } = MapBorder.None;
    public VehicleMode Mode => Controller.Mode;
    public bool Riding => Mode != VehicleMode.Foot;

    /// <summary>Call from <see cref="ISessionListener.MapEntered"/>: the map's mode, entered as a
    /// level start (the HK's eye rises 400; the marker-3 ceiling is read).</summary>
    public void MapEntered()
    {
        var mode = _session.InstanceVehicle is { } named
            ? VehicleModes.From(named)
            : VehicleModes.ForMission(_session.Data.Game, _session.Mission, _session.MapNumber);
        Controller.Ceiling = _session.Map is { } map ? VehicleController.CeilingOf(map) : float.PositiveInfinity;
        Border = _session.Map is { } fenced ? MapBorder.From(fenced) : MapBorder.None;
        Ramming.Reset();
        Board(mode, entryRise: true);
    }

    /// <summary>Climbs into <paramref name="mode"/> (a fresh ride, a whole hull) or out of the
    /// vehicle for <see cref="VehicleMode.Foot"/>.</summary>
    public void Board(VehicleMode mode, bool entryRise = false)
    {
        var weapons = _session.Weapons;
        Controller.Enter(mode, entryRise && mode == VehicleMode.Hk);
        if (mode == VehicleMode.Foot)
        {
            Guns = null;
            weapons.Mount = null;
            weapons.Player = _session.Soldier;
            return;
        }

        Hull.Reset();
        Guns = new VehicleGuns(mode);
        foreach (var r in VehicleGuns.Records(mode))
        {
            weapons.Inventory.Grant(r);
        }

        weapons.Mount = Guns;
        weapons.Player = Hull;
        if (mode == VehicleMode.Jeep)
        {
            _session.Sounds?.Play2D(VehicleController.SoundCarStart, -6f);
        }
    }

    /// <summary>The session's <see cref="GameSession.Moved"/>: a move that left the map's fence is
    /// undone and the car stopped; the edge band prints hint slot 8 ([G9]).</summary>
    private void Fence(System.Numerics.Vector3 before)
    {
        if (Border.Clamp(_session.Player, before, out var hint))
        {
            Controller.Speed = 0f;
        }

        if (hint && _session.Progress.HintText(MapBorder.HintSlot) is { Length: > 0 } text)
        {
            _session.Listener?.Hint(text);
        }
    }

    /// <summary>The ride for a save: null on foot.</summary>
    public SavedVehicle? Save() => Guns is { } guns
        ? new SavedVehicle(Mode == VehicleMode.Hk ? PlayerMode.Hk : PlayerMode.Jeep, Hull.Points, Controller.Speed, Controller.CarYaw,
            Controller.AimYaw, Controller.AimPitch, _session.Weapons.Inventory.Pool(VehicleGuns.EnergyPool), guns.Overheated,
            _session.Player.Position.Y)
        : null;

    /// <summary>A loaded ride: <see cref="Board"/> into its mode where the save put the body (no
    /// entry rise), then its hull, energy, heat, motion and height (the view's pitch kept).</summary>
    public void Restore(SavedVehicle saved)
    {
        var body = _session.Player;
        var pitch = body.Pitch;
        Board(VehicleModes.From(saved.Mode));
        if (Guns is not { } guns)
        {
            return;
        }

        body.Position = body.Position with { Y = saved.Altitude };
        body.Pitch = pitch;
        Hull.Points = Math.Clamp(saved.Hull, 0f, Hull.Max);
        var inventory = _session.Weapons.Inventory;
        inventory.AddAmmo(VehicleGuns.EnergyPool, saved.Energy - inventory.Pool(VehicleGuns.EnergyPool));
        guns.Overheated = saved.Overheated;
        Controller.Resume(saved.CarYaw, saved.AimYaw, saved.AimPitch, saved.Speed);
    }

    /// <summary>The panel in a vehicle: CROSHAIR.IMG, the gun's energy, the rockets as the
    /// secondary, the hull on the ARMOR bar. On foot the state comes back as given.</summary>
    public HudState Hud(HudState state)
    {
        if (Guns is not { } guns)
        {
            return state;
        }

        var inventory = _session.Weapons.Inventory;
        return state with
        {
            Vehicle = VehicleModes.Hud(Mode),
            Armour = Hull.Fraction,
            PrimaryRecord = guns.Gun,
            PrimaryAmmo = inventory.AmmoFor(guns.Gun),
            SecondaryRecord = guns.Pod,
            SecondaryAmmo = inventory.AmmoFor(guns.Pod),
        };
    }
}
