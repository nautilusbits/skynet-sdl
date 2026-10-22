using System.Numerics;
using RetroEng.Render;
using SkyNet.Formats;
using SkyNet.Game.Mods;

namespace SkyNet.Game.Vehicles;

/// <summary>
/// Draws the vehicle as the DOS player sees it from inside: its own .3D (HUMMER.3D, HKCOCKPT.3D;
/// HUD table 0x44700) at <see cref="VehicleController.CockpitWorld"/>. Back faces are culled, so
/// from inside only the windscreen frame, the roll bar and the bonnet show; drawn flat and out of
/// the fog, as DOS draws it (the Godot port's _attach_cockpit, unshaded). PANEL1/PANEL2.IMG are
/// never loaded by the executable: the panel stays PANEL0. In the enhanced look a mod's replacement
/// of the cockpit model (<see cref="ReplacedModels"/>, frame 0) is drawn instead, lit as the world.
/// Both meshes are built when the cockpit's DOS model is first read (the game context does it, before
/// any level's renderer scope), so a map change never releases them.
/// </summary>
public sealed class CockpitRenderer(Renderer renderer, GameData data, TextureHandle palette)
{
    private readonly TextureStore _textures = new(renderer, data);
    private readonly Dictionary<VehicleMode, ModelMesh?> _meshes = [];
    private readonly Dictionary<VehicleMode, Model3D> _models = [];
    private readonly ReplacedModels _replaced = new(renderer, palette, new TextureStore(renderer, data));

    /// <summary>The cockpit mesh of a mode, built on first use; null when the model is missing.</summary>
    public ModelMesh? Mesh(VehicleMode mode)
    {
        if (mode == VehicleMode.Foot)
        {
            return null;
        }

        if (!_meshes.TryGetValue(mode, out var mesh))
        {
            mesh = null;
            var file = VehicleController.CockpitModel(mode);
            foreach (var archive in new[] { "MDMDOBJS.BSA", "MDMDENMS.BSA" })
            {
                if (!data.Exists(archive))
                {
                    continue;
                }

                using var bsa = data.OpenArchive(archive);
                if (bsa.Read(file) is { Length: > 0 } bytes && Model3D.Parse(bytes) is { } model
                    && ModelMesh.Build(renderer, model, _textures, palette, RenderState.Solid) is { } built)
                {
                    mesh = built;
                    _models[mode] = model;
                    // A mod's replacement is built with the DOS mesh, outside any level's scope (the
                    // game context makes both cockpits): built lazily while a map is up, it would go
                    // with that map's resources on the next map change and leave a dead mesh cached.
                    _replaced.Mesh(file, model, 0);
                    break;
                }
            }

            _meshes[mode] = mesh;
        }

        return mesh;
    }

    /// <summary>The materials of both cockpits, for <see cref="Renderer.WarmUp"/>.</summary>
    public IEnumerable<Material> Materials() =>
        new[] { VehicleMode.Jeep, VehicleMode.Hk }.Select(Mesh).Where(m => m != null).SelectMany(m => m!.Parts).Select(Flat)
            .Concat(_replaced.Materials());

    /// <summary>Queues the cockpit of <paramref name="vehicle"/> for this frame (nothing on foot);
    /// <paramref name="enhanced"/>: the enhanced look, where a mod's replacement stands in.</summary>
    public void Draw(VehicleController vehicle, float alpha, long nowMs, bool enhanced = false) =>
        Draw(vehicle.Mode, vehicle.CockpitWorld(alpha), nowMs, enhanced);

    /// <summary>Queues the cockpit of a <paramref name="mode"/> vehicle placed by <paramref name="world"/>
    /// (<see cref="VehicleController.CockpitWorld"/>, caught by the simulation).</summary>
    public void Draw(VehicleMode mode, Matrix4x4 world, long nowMs, bool enhanced = false)
    {
        if (Mesh(mode) is not { } mesh)
        {
            return;
        }

        if (enhanced && _models.TryGetValue(mode, out var model)
            && _replaced.Mesh(VehicleController.CockpitModel(mode), model, 0) is { } replaced)
        {
            _replaced.Draw(replaced, world);
            return;
        }

        foreach (var part in mesh.Parts)
        {
            renderer.Draw(mesh.Mesh, part.First, part.Count, Flat(part), part.Texture?.FrameAt(nowMs) ?? 0, world);
        }
    }

    private static Material Flat(ModelMesh.Part part) => part.Material with { Unfogged = true };
}
