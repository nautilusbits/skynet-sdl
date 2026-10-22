using System.Drawing;
using System.Numerics;
using RetroEng.Render;
using SkyNet.Formats;
using SkyNet.Game.Hud;
using SkyNet.Game.Mods;

namespace SkyNet.Game.Weapons;

/// <summary>
/// The gun in the soldier's hands (fly_camera.gd viewmodel): WEAPONnn.CFA of the held record on
/// the canvas, its foot on the panel and its art aimed at the crosshair (<see cref="ViewModelLayout"/>),
/// animated by <see cref="ViewModelAnimation"/>. The low set's frames come from MDMDIMGS.BSA; the
/// hi-res set's from MDMDHRES.BSA (a CFA it lacks falls back to the low one), drawn into the low
/// frame's rectangle. Draw it in the canvas pass before the HUD, so the panel covers its foot.
/// <para>In the enhanced look a mod's 3D model of the held gun (<see cref="ViewModelModels"/>) is
/// drawn instead (<see cref="DrawModel"/>): an inset under the canvas through the frame's camera, its
/// depth its own (it never sinks into a wall), lit by the frame's light and point lights, moved by
/// <see cref="ViewModelMotion"/>.</para>
/// </summary>
public sealed class ViewModel : IDisposable
{
    private readonly Renderer _renderer;
    private readonly GameData _data;
    private readonly WeaponTable _table;
    private readonly TextureHandle _palette;
    /// <summary>A mod's surfaces kept across maps (the guns' textures made once).</summary>
    private readonly KeptSurfaces? _kept;
    private readonly Dictionary<ViewModelModel, ModelMesh?> _meshes = new(ReferenceEqualityComparer.Instance);
    private HudArt _low;
    private HudArt? _high;

    public ViewModelAnimation Animation { get; } = new();

    /// <param name="palette">The game palette, for the 3D models' materials.</param>
    public ViewModel(Renderer renderer, GameData data, WeaponTable table, HudArtSet set = HudArtSet.LowRes, TextureHandle palette = default,
        KeptSurfaces? kept = null)
    {
        _renderer = renderer;
        _data = data;
        _table = table;
        _palette = palette;
        _kept = kept;
        _low = new HudArt(renderer, data, HudArtSet.LowRes);
        _high = set == HudArtSet.HiRes ? new HudArt(renderer, data, HudArtSet.HiRes) : null;
    }

    /// <summary>The art set (as <see cref="DosHud.ArtSet"/>); changing it reloads the frames.</summary>
    public HudArtSet ArtSet
    {
        get => _high != null ? HudArtSet.HiRes : HudArtSet.LowRes;
        set
        {
            if (value != ArtSet)
            {
                Load(value);
            }
        }
    }

    /// <summary>Makes the frames again (a renderer scope released them).</summary>
    public void ReloadArt() => Load(ArtSet);

    private void Load(HudArtSet set)
    {
        _low.Dispose();
        _high?.Dispose();
        _low = new HudArt(_renderer, _data, HudArtSet.LowRes);
        _high = set == HudArtSet.HiRes ? new HudArt(_renderer, _data, HudArtSet.HiRes) : null;
    }

    /// <summary>Hooks the weapon system's shots: a gun's shot or the pipe's swing plays the fire
    /// animation, a throw lowers the gun.</summary>
    public void Attach(WeaponSystem weapons) => weapons.Fired += (record, thrown) =>
    {
        if (thrown)
        {
            Animation.Throw();
        }
        else if (ViewModelLayout.CfaName(record) != null)
        {
            Animation.Fire();
        }
    };

    /// <summary>Frames of a record in the current set (empty: no view-model).</summary>
    public HudImage[] Frames(int record) =>
        ViewModelLayout.CfaName(record) is { } name ? (_high ?? _low).Cel(name) : [];

    /// <summary>The view X of a record: the table's, or the Godot port's when the table has none.</summary>
    public int ViewX(int record)
    {
        var x = record >= 0 && record < _table.Weapons.Length ? _table.Weapons[record].ViewX : 0;
        return x > 0 ? x
            : record >= 0 && record < ViewModelLayout.GodotViewX.Count ? ViewModelLayout.GodotViewX[record] : ViewModelLayout.ScreenCentreX;
    }

    /// <summary>Advances the animation by <paramref name="dt"/> seconds (a frame or a tick).</summary>
    public void Tick(float dt, in ViewModelState state)
    {
        var record = state.Visible ? state.Record : -1;
        var frames = record >= 0 ? Frames(record).Length : 0;
        var cooldown = record >= 0 && record < _table.Weapons.Length ? _table.Weapons[record].Cooldown : 1f;
        Animation.Tick(dt, record, frames, cooldown);
    }

    /// <summary>Queues the gun on the canvas of a <paramref name="width"/> × <paramref name="height"/> screen.</summary>
    public void Draw(in ViewModelState state, int width, int height, HudStyle style = HudStyle.Classic) =>
        Draw(state, Pose(), width, height, style);

    /// <summary>The animation's state now, for a draw on another thread.</summary>
    public ViewModelPose Pose() => new(Animation.Record, Animation.Frame, Animation.ThrowDip, Animation.FireProgress, Animation.Clock);

    /// <summary>Queues the gun at <paramref name="pose"/> (<see cref="Pose"/>, caught by the simulation).</summary>
    public void Draw(in ViewModelState state, in ViewModelPose pose, int width, int height, HudStyle style = HudStyle.Classic)
    {
        if (!state.Visible || width <= 0 || height <= 0)
        {
            return;
        }

        var frames = Frames(state.Record);
        if (frames.Length == 0)
        {
            return;
        }

        var image = frames[Math.Clamp(pose.Record == state.Record ? pose.Frame : 0, 0, frames.Length - 1)];
        var rect = Place(state.Record, image, width, height, style, pose.ThrowDip);
        var unit = Renderer.CanvasHeight / height;
        var target = new RectangleF(rect.X * unit, rect.Y * unit, rect.Width * unit, rect.Height * unit);
        var palette = (_high ?? _low).Palette;
        _renderer.DrawImage(image.Handle, palette, image.Size, new RectangleF(0, 0, image.Width, image.Height), target, Vector4.One);
    }

    /// <summary>Queues the held gun's 3D model (<see cref="ViewModelModels.Drawn"/>) at <paramref name="pose"/>
    /// through <paramref name="view"/> (the frame's camera, with its lens: the frame's point lights
    /// light it) on a <paramref name="width"/> × <paramref name="height"/> frame with <paramref name="style"/>'s
    /// panel, as an inset under the canvas with its own
    /// depth; on a shot's first frame <paramref name="flash"/> draws the muzzle flash (world point,
    /// size, eye) in the inset too. False when there is none to draw (the DOS art is drawn then).</summary>
    public bool DrawModel(in ViewModelState state, in ViewModelPose pose, in View view, bool enhanced, int width, int height,
        HudStyle style, Action<Vector3, float, Vector3>? flash = null)
    {
        if (!state.Visible || view.Lens is not { } lens || ViewModelModels.Drawn(state.Record, enhanced) is not { } model
            || Mesh(model) is not { } mesh || !Matrix4x4.Invert(lens.View, out var toWorld))
        {
            return false;
        }

        var melee = state.Record == WeaponTable.Pipe;
        var current = pose.Record == state.Record;
        var input = new ViewModelMotion.Input(current ? pose.Fire : -1f, pose.ThrowDip, pose.Clock, melee);
        // The projection's vertical scale: the view-projection less the view.
        var projection = toWorld * view.ViewProjection;
        var frame = ViewModelMotion.Frame.Of(width, height, style, projection.M22 > 1e-6f ? 1f / projection.M22 : ViewModelMotion.Frame.Reference.TanHalfFovY);
        var w = _table.Weapons[state.Record];
        var world = ViewModelMotion.Local(model.Sidecar, model.Box, input, frame, new Vector2(w.MuzzleX, w.MuzzleY)) * toWorld;
        _renderer.BeginInset(view, new RectangleF(0f, 0f, _renderer.CanvasWidth, Renderer.CanvasHeight), InsetContent.Own, InsetLayer.UnderCanvas);
        foreach (var part in mesh.Parts)
        {
            mesh.Draw(_renderer, part, 0, world);
        }

        if (flash != null && current && model.Sidecar.Muzzle is { } muzzle && ViewModelMotion.Flashes(pose.Frame, pose.Fire, melee))
        {
            flash(Vector3.Transform(muzzle, world), model.Sidecar.FlashSize, view.Position);
        }

        _renderer.EndInset();
        return true;
    }

    /// <summary>Makes now (a map's load) the 3D models of the guns <paramref name="inventory"/> holds,
    /// instead of on their first draw; returns their materials for <see cref="Renderer.WarmUp"/>.</summary>
    public List<Material> Prepare(Inventory inventory, Menu.Look look)
    {
        var materials = new List<Material>();
        for (var record = 0; record < WeaponTable.MotionDetector; record++)
        {
            if (inventory.Owns(record) && ViewModelModels.Drawn(record, look == Menu.Look.Enhanced) is { } model && Mesh(model) is { } mesh)
            {
                materials.AddRange(mesh.Parts.Select(p => p.Material));
            }
        }

        return materials;
    }

    /// <summary>The model's mesh, built on first use and again when a renderer scope released it; a
    /// remount's models are new keys (the old ones' meshes are dropped).</summary>
    private ModelMesh? Mesh(ViewModelModel model)
    {
        lock (_meshes)
        {
            if (_meshes.TryGetValue(model, out var mesh) && (mesh == null || _renderer.Exists(mesh.Mesh)))
            {
                return mesh;
            }

            foreach (var old in _meshes.Keys.Where(k => k.Record == model.Record && !ReferenceEquals(k, model)).ToList())
            {
                _meshes.Remove(old);
            }

            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            _meshes[model] = mesh = ReplacementMesh.Build(_renderer, _palette, model.Model, Matrix4x4.Identity, _kept);
            DevTools.LoadHitches.Since("view-model mesh", started);
            return mesh;
        }
    }

    /// <summary>Where the gun at <paramref name="pose"/> is drawn (screen pixels; one DOS pixel of its
    /// low frame is <see cref="ViewModelLayout.Scale"/>), null when none is: what is drawn over its art
    /// (the motion detector's plot) is placed from it.</summary>
    public RectangleF? Placed(in ViewModelState state, in ViewModelPose pose, int width, int height, HudStyle style = HudStyle.Classic)
    {
        var frames = state.Visible && width > 0 && height > 0 ? Frames(state.Record) : [];
        return frames.Length == 0 ? null : Place(state.Record, frames[0], width, height, style, pose.ThrowDip);
    }

    private RectangleF Place(int record, HudImage image, int width, int height, HudStyle style, float dip)
    {
        var size = new Size(image.Width, image.Height);
        if (image.FromHires)
        {
            // The low twin's rectangle; half the hi-res frame when the low set lacks it.
            var low = _low.Cel(ViewModelLayout.CfaName(record)!);
            size = low.Length > 0 ? new Size(low[0].Width, low[0].Height) : new Size(image.Width / 2, image.Height / 2);
        }

        return ViewModelLayout.Place(width, height, ViewX(record), size, style, dip);
    }

    public void Dispose()
    {
        _low.Dispose();
        _high?.Dispose();
    }
}

/// <summary>The view-model animation's record, frame and throw dip for one frame (<see cref="ViewModel.Pose"/>),
/// with what a 3D gun moves by: the shot's progress (−1 none) and the animation clock.</summary>
public readonly record struct ViewModelPose(int Record, int Frame, float ThrowDip, float Fire = -1f, float Clock = 0f);
