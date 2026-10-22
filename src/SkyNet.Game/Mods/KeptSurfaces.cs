using RetroEng.Diagnostics;
using RetroEng.Render;

namespace SkyNet.Game.Mods;

/// <summary>
/// A mod's surfaces kept on the GPU across maps: the engine makes a colour texture (its layers and
/// mips) per index texture and palette, and the map's scope destroys them with the map, so a model on
/// every map (a pickup, a robot, the gun in hand) or a map entered again (an interior and back) would
/// make them all again. These index textures and the palette are made outside the map's scope
/// (<see cref="BeforeMap"/>, between the old map's release and the new one's mark), each given one
/// surface's colours on first use; the current map's and the last one's stay, the rest within
/// <see cref="UnusedBudget"/>.
/// <code>
///   Release(map) ─► BeforeMap (spares) ─► Mark ─► build: Texture(surface) per part ─► Trim
///                                                   └─ kept: the colour texture is already made
/// </code>
/// One per game context, handed to the drawers that build a mod's meshes; without one a surface gets
/// an index texture of the map's scope, as before.
/// </summary>
public sealed class KeptSurfaces(Renderer renderer, TextureHandle palette)
{
    /// <summary>GPU bytes of kept surfaces neither the current map nor the last one use.</summary>
    public const long UnusedBudget = 1L << 30;

    /// <summary>Index textures made ahead for the next map's new surfaces.</summary>
    public const int Spares = 512;

    /// <summary>The index texture standing for a colour texture: 4 x 4, as <see cref="ReplacementMesh"/> makes them.</summary>
    public const int IndexSide = 4;

    private KeptBudget<ReplacementSurface> _budget = new(UnusedBudget, ReferenceEqualityComparer.Instance);
    /// <summary>The DETAIL the kept colour textures were made at (<see cref="ModContent.Detail"/>).</summary>
    private Detail _detail = ModContent.Detail;
    /// <summary>Each kept surface's index texture and its colour texture's GPU bytes (its pixels are let go).</summary>
    private readonly Dictionary<ReplacementSurface, (TextureHandle Texture, long Bytes)> _textures = new(ReferenceEqualityComparer.Instance);
    private readonly Stack<TextureHandle> _spare = new();
    private readonly Lock _lock = new();

    /// <summary>The palette the kept surfaces' materials name (outlives the maps).</summary>
    public TextureHandle Palette { get; } = palette;

    private readonly Renderer _renderer = renderer;

    /// <summary>A map is about to be built: call after the old map's release and before the new
    /// map's mark (the spare index textures must outlive the map).</summary>
    public void BeforeMap()
    {
        lock (_lock)
        {
            if (_detail != ModContent.Detail)
            {
                Drop();
            }

            _budget.NextMap();
            var opaque = new byte[IndexSide * IndexSide];
            Array.Fill(opaque, (byte)1);
            while (_spare.Count < Spares)
            {
                _spare.Push(_renderer.CreateIndexTexture(IndexSide, IndexSide, opaque));
            }
        }
    }

    /// <summary>DETAIL changed: every kept colour texture goes (made again at the new size on use).</summary>
    private void Drop()
    {
        foreach (var (texture, _) in _textures.Values)
        {
            _renderer.Destroy(texture);
        }

        Log.Info("Mods", $"Kept surfaces: {_textures.Count} dropped for DETAIL {ModContent.Detail}");
        _textures.Clear();
        _budget = new(UnusedBudget, ReferenceEqualityComparer.Instance);
        _detail = ModContent.Detail;
    }

    /// <summary>The map is built: frees the surfaces it does not use beyond the budget.</summary>
    public void Trim()
    {
        lock (_lock)
        {
            var freed = _budget.Over();
            foreach (var surface in freed)
            {
                _renderer.Destroy(_textures[surface].Texture);
                _textures.Remove(surface);
            }

            if (_textures.Count > 0)
            {
                Log.Info("Mods", $"Kept surfaces: {_textures.Count}, {_budget.Bytes >> 20} MB" + (freed.Count > 0 ? $" ({freed.Count} freed)" : ""));
            }
        }
    }

    /// <summary>Whether <paramref name="surface"/>'s colour texture is kept (its pixels not needed).</summary>
    public bool Holds(ReplacementSurface surface)
    {
        lock (_lock)
        {
            return _textures.ContainsKey(surface);
        }
    }

    /// <summary>The index texture standing for <paramref name="surface"/>'s colours (kept, made from
    /// a spare on first use); null when no spare is left (the caller makes one in the map's scope).</summary>
    public TextureHandle? Texture(ReplacementSurface surface)
    {
        lock (_lock)
        {
            if (_textures.TryGetValue(surface, out var made))
            {
                _budget.Use(surface, made.Bytes);
                return made.Texture;
            }

            if (_spare.Count == 0)
            {
                return null;
            }
        }

        // The pixels made outside the lock (a decode): other builders go on meanwhile.
        var colour = surface.Colour;
        lock (_lock)
        {
            if (!_textures.TryGetValue(surface, out var kept))
            {
                if (!_spare.TryPop(out var texture))
                {
                    return null;
                }

                // Released: this texture meets one palette (Palette) and one layer count, so its
                // colour texture is made once; the surface makes its pixels again if it is freed.
                _renderer.ReplaceTexture(texture, colour.Width, colour.Height, colour.Rgba, null, HdPixels.Released);
                surface.ReleasePixels();
                // A colour texture's bytes on the GPU: its layers and their mips (a third more).
                _textures[surface] = kept = (texture, colour.Rgba.LongLength * 4 / 3);
            }

            _budget.Use(surface, kept.Bytes);
            return kept.Texture;
        }
    }
}
