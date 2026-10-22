using System.Numerics;
using RetroEng.Content;
using RetroEng.Diagnostics;
using RetroEng.Json;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Mods;

/// <summary>
/// A mod's <c>models/viewmodels/NAME.json</c>, the sidecar of the gun in hand <c>NAME.glb</c>
/// (docs/modding.md#view-models): where the model sits in the camera's space (world units, Y up,
/// ahead −Z, the box centre placed), its size and turn, and how it moves. Every key is optional.
/// </summary>
/// <param name="Offset">Where the model's box centre sits, from the eye (x right, y up, z ahead is −).</param>
/// <param name="Scale">World units per glTF unit; 0: the longest side is <see cref="ViewModelMotion.FitLength"/>.</param>
/// <param name="Rotation">Degrees about X, then Y, then Z, applied first (the barrel must point along −Z).</param>
/// <param name="RecoilBack">World units a shot pushes the gun back at the kick's peak.</param>
/// <param name="RecoilPitch">Degrees a shot lifts the muzzle at the kick's peak.</param>
/// <param name="Swing">Degrees of the pipe's swing (the melee record only).</param>
/// <param name="Sway">World units of the idle sway (0: none).</param>
public sealed record ViewModelSidecar(Vector3 Offset, float Scale, Vector3 Rotation, float RecoilBack, float RecoilPitch, float Swing, float Sway)
{
    public static readonly ViewModelSidecar Default = new(new Vector3(6f, -9f, -24f), 0f, Vector3.Zero, 2f, 5f, 70f, 0.25f);

    /// <summary>The view's aspects <see cref="OffsetFor"/> slides between: the DOS screen's and a wide screen's.</summary>
    public const float NarrowAspect = 4f / 3f, WideAspect = 16f / 9f;

    /// <summary><see cref="Offset"/> on a 16:9 view (the panel takes more of its height); null: the same.</summary>
    public Vector3? OffsetWide { get; init; }

    /// <summary>The point (model space) the kick, the swing and the sway turn about; null: the box centre.</summary>
    public Vector3? Pivot { get; init; }

    /// <summary>The muzzle (model space): a shot's first frame draws the DOS flash there; null: none.</summary>
    public Vector3? Muzzle { get; init; }

    /// <summary>The flash's size at the muzzle (world units).</summary>
    public float FlashSize { get; init; } = 6f;

    /// <summary>The light every surface has at least, as a share of its base colour added to its
    /// emission (0..1): the DOS art is full-bright, a night map would leave the gun black.</summary>
    public float MinLight { get; init; } = 0.35f;

    /// <summary>Where the box centre sits on a view of <paramref name="aspect"/> (width / height):
    /// <see cref="Offset"/> at 4:3 and narrower, <see cref="OffsetWide"/> at 16:9 and wider, between them in step.</summary>
    public Vector3 OffsetFor(float aspect)
    {
        if (OffsetWide is not { } wide)
        {
            return Offset;
        }

        var t = Math.Clamp((aspect - NarrowAspect) / (WideAspect - NarrowAspect), 0f, 1f);
        return t <= 0f ? Offset : t >= 1f ? wide : Vector3.Lerp(Offset, wide, t);
    }

    /// <summary>Reads a sidecar; an unknown key or a bad value throws <see cref="FormatException"/>.</summary>
    public static ViewModelSidecar Parse(byte[] json)
    {
        var s = Default;
        try
        {
            foreach (var p in JsonItem.Parse(json, lenient: true).AsObject().Members)
            {
                s = p.Key switch
                {
                    "offset" => s with { Offset = Vec(p.Value) },
                    "scale" => s with { Scale = Number(p.Value) },
                    "rotation" => s with { Rotation = Vec(p.Value) },
                    "recoilBack" => s with { RecoilBack = Number(p.Value) },
                    "recoilPitch" => s with { RecoilPitch = Number(p.Value) },
                    "swing" => s with { Swing = Number(p.Value) },
                    "sway" => s with { Sway = Number(p.Value) },
                    "offsetWide" => s with { OffsetWide = Vec(p.Value) },
                    "pivot" => s with { Pivot = Vec(p.Value) },
                    "muzzle" => s with { Muzzle = Vec(p.Value) },
                    "flashSize" => s with { FlashSize = Number(p.Value) },
                    "minLight" => s with { MinLight = Number(p.Value) },
                    "comment" or "source" or "licence" or "license" => s,
                    _ => throw new FormatException($"unknown key '{p.Key}'"),
                };
            }
        }
        catch (JsonDataException e)
        {
            throw new FormatException(e.Message, e);
        }

        if (s.Scale < 0f || s.Sway < 0f || s.FlashSize < 0f)
        {
            throw new FormatException("scale, sway and flashSize must not be negative");
        }

        if (s.MinLight is < 0f or > 1f)
        {
            throw new FormatException("minLight must be 0 to 1");
        }

        return s;

        static float Number(JsonItem e)
        {
            var f = (float)e.AsDouble();
            return float.IsFinite(f) ? f : throw new FormatException("a number must be finite");
        }

        static Vector3 Vec(JsonItem e)
        {
            var f = e.ToArray(Number);
            return f.Length == 3 ? new Vector3(f[0], f[1], f[2]) : throw new FormatException("a vector needs 3 numbers");
        }
    }
}

/// <summary>A held gun's 3D model from a mod: the glTF model (its materials as a prop replacement's),
/// its sidecar and its box in its own space.</summary>
public sealed class ViewModelModel
{
    public required int Record { get; init; }
    public required ModelReplacement Model { get; init; }
    public required ViewModelSidecar Sidecar { get; init; }
    public required (Vector3 Min, Vector3 Max) Box { get; init; }
}

/// <summary>
/// The guns in hand a mod draws as glTF models in the enhanced look: <c>models/viewmodels/NAME.glb</c>
/// (or <c>.gltf</c>) and its optional sidecar <c>NAME.json</c> (<see cref="ViewModelSidecar"/>), NAME
/// the weapon's name (<see cref="WeaponTable.Names"/>, spaces as underscores). Read from the mounted
/// mods (<see cref="ModContent.Files"/>) on first use, again after a remount; a file that does not
/// read is logged and the gun keeps its DOS art.
/// <code>
///   models/viewmodels/UZI.glb ─► Drawn(1, enhanced) ─► ViewModelModel ─► ViewModel.DrawModel (an inset)
///                                Drawn(1, original) ─► null ─► WEAPON01.CFA on the canvas
/// </code>
/// </summary>
public static class ViewModelModels
{
    public const string Folder = "models/viewmodels/";

    /// <summary><see cref="Folder"/> below <c>models/</c>, as <see cref="ModelReplacement.Load(VirtualFileSystem, string, ModelSidecar)"/> names it.</summary>
    private const string SubFolder = "viewmodels/";

    private sealed record Loaded(VirtualFileSystem? Files, ViewModelModel?[] Models, bool[] Read);

    private static Loaded? _loaded;

    /// <summary>The file name (without extension) of a held record: "UZI", "ASSAULT_RIFLE"; null for
    /// what has no 3D view-model: the motion detector (its plot is drawn on its art), the thrown
    /// items and the vehicle guns.</summary>
    public static string? FileName(int record) =>
        record is >= 0 and < WeaponTable.MotionDetector ? WeaponTable.Names[record].Replace(' ', '_') : null;

    /// <summary>The model drawn for the held <paramref name="record"/>; null: its DOS art (always in
    /// the original look).</summary>
    public static ViewModelModel? Drawn(int record, bool enhanced)
    {
        if (!enhanced || FileName(record) is not { } name)
        {
            return null;
        }

        var files = ModContent.Files;
        var loaded = Volatile.Read(ref _loaded);
        if (loaded == null || !ReferenceEquals(loaded.Files, files))
        {
            loaded = new Loaded(files, new ViewModelModel?[WeaponTable.MotionDetector], new bool[WeaponTable.MotionDetector]);
            Volatile.Write(ref _loaded, loaded);
        }

        lock (loaded)
        {
            if (!loaded.Read[record])
            {
                loaded.Models[record] = files != null && ModContent.HasModels ? Read(files, record, name) : null;
                loaded.Read[record] = true;
            }

            return loaded.Models[record];
        }
    }

    private static ViewModelModel? Read(VirtualFileSystem files, int record, string name)
    {
        ViewModelSidecar sidecar;
        try
        {
            sidecar = files.TryReadAllBytes($"{Folder}{name}.json") is { } json ? ViewModelSidecar.Parse(json) : ViewModelSidecar.Default;
        }
        catch (FormatException e)
        {
            Log.Warn("Mods", $"{Folder}{name}.json: {e.Message}; the DOS art stays");
            return null;
        }

        // The prop sidecar is not read (ours above): the model in its own space.
        if (ModelReplacement.Load(files, SubFolder + name, ModelSidecar.Default) is not { } loaded)
        {
            return null;
        }

        // The light floor in the surfaces' emission (the gun reads on a night map as the DOS art does).
        var model = sidecar.MinLight > 0f
            ? new ModelReplacement
            {
                Name = loaded.Name, Model = loaded.Model, Sidecar = loaded.Sidecar,
                Surfaces = [.. loaded.Surfaces.Select(x => x.WithLightFloor(sidecar.MinLight))],
            }
            : loaded;

        var corners = model.Model.Primitives.SelectMany(p => p.Positions).ToArray();
        if (corners.Length == 0)
        {
            Log.Warn("Mods", $"{Folder}{name}: no vertices; the DOS art stays");
            return null;
        }

        Log.Info("Mods", $"{Folder}{name}: the {WeaponTable.Names[record]} in hand drawn as a model in the enhanced look");
        return new ViewModelModel { Record = record, Model = model, Sidecar = sidecar, Box = (corners.Aggregate(Vector3.Min), corners.Aggregate(Vector3.Max)) };
    }
}
