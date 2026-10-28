using System.Numerics;
using RetroEng.Render;
using SkyNet.Game.Enemies;
using SkyNet.Game.Mods;
using SkyNet.Game.Vehicles;

namespace SkyNet.Game.Net;

/// <summary>
/// Draws the other players of a deathmatch (dm_avatar.gd's meshes): each avatar's body and head
/// (<see cref="DmAvatar.Look"/>, in its DOS avatar's skin as <see cref="Menu.AvatarPreview"/> draws it) at the frame its clip shows, both hung off one origin so the head
/// stays on the neck, the feet on the reported position, turned by the yaw. Without the avatar
/// models in the data, T800RFL with its own clips (the Godot port's first body). One mesh per
/// model frame, built on first use, as <see cref="EnemyRenderer"/> does; indoors at the shade of the
/// level's light where the avatar stands. Build one per level (its meshes live in the level's scope).
/// Step 9: a driver is drawn as the vehicle (SKYNET.EXE's avatar table records 13 and 14: NETHUMER.3D
/// with its turret NETHMTRT.3D and barrel NETHMBRL.3D turned to the driver's view, NET_HK.3D), the
/// parked ones as DOS parks them (0x51F04: NETHUMMT.3D, NET_HKMT.3D), each falling back to the other.
/// In the enhanced look a model a mod replaces is drawn as its replacement for the frame
/// (<see cref="ReplacedModels"/>), placed as the DOS model is.
/// </summary>
public sealed class AvatarRenderer(Renderer renderer, EnemyModels models, TextureStore textures, Level level)
{
    private readonly Dictionary<(string File, int Frame, int Skin), ModelMesh?> _meshes = [];
    private readonly ReplacedModels _replaced = new(renderer, level.Palette, textures);
    private readonly DmNavLines _nav = new(renderer);

    public Level Level => level;

    public int MeshesBuilt => _meshes.Count;

    /// <summary>Builds now (the map's load) every mesh <paramref name="looks"/> can draw (<see cref="Wanted"/>):
    /// built on first sight, they cost frames of 230 to 930 ms in the first seconds. All 13 looks
    /// take 2.5 s (2 400 meshes); a look that joins later is built on sight. Each mesh is a step of <paramref name="progress"/>.</summary>
    internal void Prepare(IEnumerable<DmAvatarLook> looks, LoadProgress? progress = null)
    {
        var wanted = Wanted(models, looks).ToArray();
        progress?.Begin(wanted.Length);
        foreach (var (model, frame, skin) in wanted)
        {
            Build(model, frame, skin);
            progress?.Step();
        }
    }

    // One wanted mesh: the mod's replacement in the enhanced look, else the DOS frame.
    private void Build(EnemyModelInfo model, int frame, (int From, int To)? skin)
    {
        if (level.Enhanced && _replaced.Mesh(model.File, model.Model, frame) != null)
        {
            return;
        }

        Mesh(model, frame, skin);
    }

    /// <summary>Each look's body and head in its skin, each frame; the guns in its hand (every record),
    /// each frame; the fallback body's frames; the vehicles.</summary>
    internal static IEnumerable<(EnemyModelInfo Model, int Frame, (int From, int To)? Skin)> Wanted(EnemyModels models,
        IEnumerable<DmAvatarLook> looks)
    {
        string[] vehicles = [ParkedJeep, ParkedHk, DrivenJeep, JeepTurret, JeepBarrel, DrivenHk];
        var plain = looks.SelectMany(l => Enumerable.Range(0, DmAvatarTable.GunCount).Select(r => DmAvatarTable.Gun(l.Number, r)))
            .Append(DmAvatar.FallbackBody).Select(f => (File: f, Skin: ((int From, int To)?)null));
        var skinned = looks.SelectMany(l => new[] { (File: (string?)l.Body, Skin: l.SkinRemap), (File: (string?)l.Head, Skin: l.SkinRemap) });
        var all = skinned.Concat(plain).Distinct();
        foreach (var (file, skin) in all)
        {
            if (file == null || models.Get(file) is not { } model)
            {
                continue;
            }

            for (var frame = 0; frame < model.Frames; frame++)
            {
                yield return (model, frame, skin);
            }
        }

        foreach (var file in vehicles)
        {
            if (models.Get(file) is { } model)
            {
                yield return (model, 0, null);
            }
        }
    }

    /// <summary>The visible avatars' and the parked vehicles' poses, for a draw on another thread.</summary>
    public static void Capture(IEnumerable<DmAvatar> avatars, IEnumerable<DmVehicle> vehicles, DmFrame frame)
    {
        // A dictionary's values walked as such: through the interface each walk boxes an enumerator.
        frame.Bodies.Clear();
        if (avatars is Dictionary<int, DmAvatar>.ValueCollection live)
        {
            foreach (var a in live)
            {
                Catch(a, frame);
            }
        }
        else
        {
            foreach (var a in avatars)
            {
                Catch(a, frame);
            }
        }

        frame.Parked.Clear();
        if (vehicles is Dictionary<int, DmVehicle>.ValueCollection table)
        {
            foreach (var v in table)
            {
                Park(v, frame);
            }
        }
        else
        {
            foreach (var v in vehicles)
            {
                Park(v, frame);
            }
        }
    }

    private static void Catch(DmAvatar a, DmFrame frame)
    {
        if (a.Visible)
        {
            var gun = DmAvatarTable.Gun(a.Look.Number, DmProtocol.WeaponOf(a.Flags));
            frame.Bodies.Add(new DmBody(a.Vehicle, a.Position, a.CarYaw, a.Yaw, a.Look, a.Frame, a.T800Frame(), gun));
        }
    }

    // Driven ones are their drivers' avatars; wrecks are gone.
    private static void Park(DmVehicle v, DmFrame frame)
    {
        if (v.Driver == 0 && !v.Wrecked)
        {
            frame.Parked.Add(new DmParked(v.Kind, v.Position, v.Yaw));
        }
    }

    /// <summary>Queues the avatars and the parked vehicles as <paramref name="caught"/> has them.</summary>
    public void Draw(DmFrame caught, long nowMs)
    {
        foreach (var a in caught.Bodies)
        {
            if (a.Vehicle != VehicleMode.Foot)
            {
                DrawVehicle(a.Vehicle, a.Position, a.CarYaw, a.Yaw - a.CarYaw, driven: true, nowMs);
                continue;
            }

            var look = a.Look;
            string? headFile = look.Head;
            var skin = look.SkinRemap;
            var body = models.Get(look.Body);
            var frame = a.Frame;
            var gunFile = a.Gun;
            if (body == null)
            {
                body = models.Get(DmAvatar.FallbackBody);
                (headFile, skin, gunFile) = (null, null, null);
                frame = a.T800Frame;
            }

            if (body == null)
            {
                continue;
            }

            var world = Matrix4x4.CreateTranslation(0f, -body.FootY, 0f) * Matrix4x4.CreateRotationY(a.Yaw) * Matrix4x4.CreateTranslation(a.Position);
            int? shade = level.Outdoor ? null : level.ShadeAt(a.Position + new Vector3(0f, 40f, 0f));
            Draw(body, frame, shade, world, nowMs, skin);
            if (headFile != null && models.Get(headFile) is { } head && frame < head.Frames)
            {
                Draw(head, frame, shade, world, nowMs, skin);
            }

            // The gun in hand: its own model on the body's frame and place (DOS's actor +0x61).
            if (gunFile != null && models.Get(gunFile) is { } gun && frame < gun.Frames)
            {
                Draw(gun, frame, shade, world, nowMs);
            }
        }

        foreach (var v in caught.Parked)
        {
            DrawVehicle(v.Kind, v.Position, v.Yaw, 0f, driven: false, nowMs);
        }

        _nav.Draw(caught);
    }

    public const string ParkedJeep = "NETHUMMT.3D", ParkedHk = "NET_HKMT.3D";
    public const string DrivenJeep = "NETHUMER.3D", JeepTurret = "NETHMTRT.3D", JeepBarrel = "NETHMBRL.3D", DrivenHk = "NET_HK.3D";

    /// <summary>One vehicle on its wheels or skids at <paramref name="at"/>, heading <paramref name="yaw"/>;
    /// the jeep's turret and barrel sit on its roof turned <paramref name="turret"/> from the bonnet
    /// (the Godot port's dm_vehicle.gd seats them from the bounds: the turret's underside on the
    /// body's top, the barrel at the turret's half height).</summary>
    private void DrawVehicle(VehicleMode kind, Vector3 at, float yaw, float turret, bool driven, long nowMs)
    {
        var (first, second) = kind == VehicleMode.Hk
            ? driven ? (DrivenHk, ParkedHk) : (ParkedHk, DrivenHk)
            : driven ? (DrivenJeep, ParkedJeep) : (ParkedJeep, DrivenJeep);
        var file = models.Get(first) != null ? first : second;
        if (models.Get(file) is not { } body)
        {
            return;
        }

        var place = Matrix4x4.CreateRotationY(yaw) * Matrix4x4.CreateTranslation(at);
        var world = Matrix4x4.CreateTranslation(0f, -body.FootY, 0f) * place;
        int? shade = level.Outdoor ? null : level.ShadeAt(at + new Vector3(0f, 40f, 0f));
        Draw(body, 0, shade, world, nowMs);
        if (file != DrivenJeep || models.Get(JeepTurret) is not { } top)
        {
            return;
        }

        // Model space is Y up from the body's foot; the turret turns about its own upright.
        var roof = body.Bounds.Max.Y - body.FootY;
        var spin = Matrix4x4.CreateRotationY(turret);
        Draw(top, 0, shade, Matrix4x4.CreateTranslation(0f, roof - top.Bounds.Min.Y, 0f) * spin * place, nowMs);
        if (models.Get(JeepBarrel) is { } barrel)
        {
            var mid = roof + (top.Bounds.Max.Y - top.Bounds.Min.Y) * 0.5f;
            var centre = (barrel.Bounds.Min.Y + barrel.Bounds.Max.Y) * 0.5f;
            Draw(barrel, 0, shade, Matrix4x4.CreateTranslation(0f, mid - centre, 0f) * spin * place, nowMs);
        }
    }

    /// <summary>The materials of every mesh built so far, for <see cref="Renderer.WarmUp"/>.</summary>
    public IEnumerable<Material> Materials() =>
        _meshes.Values.Where(m => m != null).SelectMany(m => m!.Parts).Select(p => p.Material).Concat(_replaced.Materials());

    private void Draw(EnemyModelInfo model, int frame, int? shade, in Matrix4x4 world, long nowMs, (int From, int To)? skin = null)
    {
        if (level.Enhanced && _replaced.Mesh(model.File, model.Model, Math.Clamp(frame, 0, model.Frames - 1)) is { } replaced)
        {
            _replaced.Draw(replaced, world);
            return;
        }

        if (Mesh(model, frame, skin) is not { } mesh)
        {
            return;
        }

        foreach (var p in mesh.Parts)
        {
            var material = shade is { } s ? p.Material with { Shade = s } : p.Material;
            renderer.Draw(mesh.Mesh, p.First, p.Count, material, p.Texture?.FrameAt(nowMs) ?? 0, world);
        }
    }

    private ModelMesh? Mesh(EnemyModelInfo model, int frame, (int From, int To)? skin)
    {
        frame = Math.Clamp(frame, 0, model.Frames - 1);
        var key = (model.File, frame, skin?.To ?? -1);
        if (_meshes.TryGetValue(key, out var mesh))
        {
            return mesh;
        }

        var batch = new StaticBatch();
        batch.Add(ModelMesh.Surfaces(model.Model, textures, frame, smooth: true, skin: skin), Matrix4x4.Identity);
        mesh = batch.Build(renderer, level.Palette, RenderState.Solid);
        _meshes[key] = mesh;
        return mesh;
    }
}

/// <summary>A visible avatar as caught for a frame (<see cref="DmAvatar"/>).</summary>
public readonly record struct DmBody(VehicleMode Vehicle, Vector3 Position, float CarYaw, float Yaw, DmAvatarLook Look, int Frame, int T800Frame, string? Gun);

/// <summary>A parked vehicle as caught for a frame (<see cref="DmVehicle"/>).</summary>
public readonly record struct DmParked(VehicleMode Kind, Vector3 Position, float Yaw);

/// <summary>A deathmatch's bodies for one frame (<see cref="AvatarRenderer.Capture"/>), pooled: drawn
/// on another thread while the next tick runs (engine-threads plan B1).</summary>
public sealed class DmFrame
{
    internal readonly List<DmBody> Bodies = [];
    internal readonly List<DmParked> Parked = [];
    /// <summary>The bots' nav mesh outline and their paths (<see cref="DmNavLines"/>); null / empty: not shown.</summary>
    internal Vertex[]? NavMesh;
    internal readonly List<Vertex> NavPaths = [];
    /// <summary>The level's avatar drawer (null: no deathmatch on screen).</summary>
    public AvatarRenderer? View { get; internal set; }

    /// <summary>No bodies and no drawer: no deathmatch.</summary>
    public void Clear()
    {
        Bodies.Clear();
        Parked.Clear();
        NavMesh = null;
        NavPaths.Clear();
        View = null;
    }
}