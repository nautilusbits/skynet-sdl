using System.Globalization;
using System.Numerics;
using Hexa.NET.ImGui;
using RetroEng.Editor;
using RetroEng.Render;
using SkyNet.Data;
using SkyNet.Formats;
using SkyNet.Game;
using SkyNet.Game.Campaign;
using SkyNet.Game.Mods;
using SkyNet.Game.Weapons;

namespace SkyNet.Edit;

/// <summary>What a sprite is to the game (T3): its pickup item, its world size, the open mission's entities showing it.</summary>
public static class SpriteUsage
{
    private const int RecordBits = 7;

    /// <summary>The sprite index the map and the item table use: bank &lt;&lt; 7 | record.</summary>
    public static int IndexOf(int bank, int record) => bank << RecordBits | record;

    /// <summary>World units per texel: items 1:1, other sprites 2 (as the level draws them).</summary>
    public static float UnitsPerTexel(int sprite) => SpriteRules.IsItem(sprite) ? Level.ItemScale : Level.SpriteScale;

    /// <summary>The pickup item <paramref name="sprite"/> is ("50cal: weapon MACHINE GUN, 100 ammo (pool 0)"); null: none.</summary>
    public static string? Item(int sprite)
    {
        if (!PickupTable.Items.TryGetValue(sprite, out var i))
        {
            return null;
        }

        var parts = new List<string>();
        if (i.Weapon >= 0)
        {
            parts.Add("weapon " + (i.Weapon < WeaponTable.Names.Count ? WeaponTable.Names[i.Weapon] : Inv($"{i.Weapon}")));
        }

        if (i.Pool >= 0 && i.Amount > 0)
        {
            parts.Add(Inv($"{i.Amount} ammo (pool {i.Pool})"));
        }

        if (i.HealPercent > 0)
        {
            parts.Add(Inv($"heals {i.HealPercent}%"));
        }

        if (i.Armour > 0)
        {
            parts.Add(Inv($"armour {i.Armour}"));
        }

        var name = i.MessageKey.Replace("Str_", "", StringComparison.Ordinal);
        return parts.Count == 0 ? name : $"{name}: {string.Join(", ", parts)}";
    }

    /// <summary>The entities of <paramref name="maps"/> (instance, its map) drawn as <paramref name="sprite"/>, in instance then file order.</summary>
    public static List<OutlinerItem> Uses(IEnumerable<(string Instance, MapFile? Map)> maps, int sprite) =>
        [.. maps.SelectMany(m => (m.Map?.Entities ?? []).Where(e => Shows(e, sprite)).Select(e => new OutlinerItem(m.Instance, e.FileOffset)))];

    private static bool Shows(MapEntity e, int sprite) => e.Variant == MapEntityVariant.Sprite && !e.IsMarker && e.SpriteIndex == sprite;

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}

/// <summary>What the sprite viewer asks the editor: the open mission's uses, the mapped model's row, the actions.</summary>
public interface ISpriteHost
{
    /// <summary>The open mission's entities drawn as <paramref name="sprite"/>; null: no mission open.</summary>
    IReadOnlyList<OutlinerItem>? Uses(int sprite);

    /// <summary>Selects the entity and flies to it.</summary>
    void JumpTo(OutlinerItem item);

    /// <summary>The <c>sprite_models</c> row of <paramref name="key"/> as the Tables grid has it; null: none.</summary>
    SpriteModelRow? Mapped(string key);

    /// <summary>The mod "Replace with 3D model..." writes into; null: none chosen.</summary>
    string? SaveMod { get; }

    /// <summary>Asks for a .glb and maps <paramref name="key"/> to it.</summary>
    void Replace(string key);

    /// <summary>Shows <paramref name="key"/>'s row in the Tables window.</summary>
    void EditRow(string key);
}

/// <summary>
/// The Assets panel's sprite viewer (editor-tools plan, T3): the record blown up in the game palette (index 0
/// a hole), its frames played or stepped, its key, size and uses, and its model mapping.
/// <code>
///   214/007  TEXTURE.214 #007   24 x 16 px, 1 frame
///   In the world: 24 x 16 units (1 a texel)
///   Pickup: Armor4: armour 32768
///   On the open mission: 9 entities   [214 @15953] [216 @50772] ...  (a click flies there)
///   [Replace with 3D model...]
///   3D (enhanced look): medkit.glb  scale 0, yaw 0, spin 90, bob 4, offsetY 0  [Edit row]
///   [&lt;] [Pause] [&gt;] frame 1/4
///   (the sprite)                      Model Viewer window: the mapped model, as the game places it
/// </code>
/// </summary>
public sealed class SpriteViewer(ISpriteHost host, ModelViewer? viewer) : IPlayback
{
    private const float UsesHeight = 96f;
    private static readonly Vector4 Mapped = new(0.55f, 0.85f, 0.55f, 1f);

    private string? _key;
    private bool _playing = true;
    private int _frame;
    private double _frameStart;

    /// <summary>The frame shown (0-based).</summary>
    public int Frame => _frame;

    /// <summary>Whether an animated record's frames run.</summary>
    public bool Playing => _playing;

    public void TogglePlay() => _playing = !_playing;

    /// <summary>Draws the viewer of record <paramref name="record"/> of bank <paramref name="bank"/>:
    /// <paramref name="image"/> holds its <paramref name="frames"/> frames stacked downwards.</summary>
    public void Draw(int bank, int record, TextureHandle image, (int Width, int Height, int Frames, int MsPerFrame) frames)
    {
        var key = SpriteKey(bank, record);
        if (key != _key)
        {
            (_key, _playing, _frame, _frameStart) = (key, true, 0, ImGui.GetTime());
        }

        var sprite = SpriteUsage.IndexOf(bank, record);
        Header(key, bank, record, sprite, frames);
        Uses(sprite);
        Mapping(key, frames.Height * SpriteUsage.UnitsPerTexel(sprite));
        Playback(frames.Frames, frames.MsPerFrame);
        var (w, h, count, _) = frames;
        AssetPreview.Image(image, w, h * count, ImGui.GetContentRegionAvail(), count, _frame);
    }

    /// <summary>The <c>sprite_models</c> key of a record: "214/007".</summary>
    public static string SpriteKey(int bank, int record) => SpriteModels.KeyOf(SpriteUsage.IndexOf(bank, record));

    private static void Header(string key, int bank, int record, int sprite, (int Width, int Height, int Frames, int MsPerFrame) f)
    {
        var units = SpriteUsage.UnitsPerTexel(sprite);
        ImGui.TextUnformatted(key);
        ImGui.SameLine();
        ImGui.TextDisabled(string.Create(CultureInfo.InvariantCulture,
            $"TEXTURE.{bank:D3} #{record:D3}   {f.Width} x {f.Height} px, {f.Frames} frame{(f.Frames == 1 ? "" : "s")}{(f.Frames > 1 ? $" @ {f.MsPerFrame} ms" : "")}"));
        ImGui.TextUnformatted(string.Create(CultureInfo.InvariantCulture, $"In the world: {f.Width * units:0.#} x {f.Height * units:0.#} units ({units:0.#} a texel)"));
    }

    // The pickup it is, and the open mission's entities showing it (a click flies there).
    private void Uses(int sprite)
    {
        if (SpriteUsage.Item(sprite) is { } item)
        {
            ImGui.TextUnformatted("Pickup: " + item);
        }

        if (host.Uses(sprite) is not { } uses)
        {
            ImGui.TextDisabled("Open a mission to see its entities using this sprite.");
            return;
        }

        ImGui.TextUnformatted(uses.Count == 0 ? "Not on the open mission's maps." : string.Create(CultureInfo.InvariantCulture, $"On the open mission: {uses.Count} entit{(uses.Count == 1 ? "y" : "ies")}"));
        if (uses.Count == 0)
        {
            return;
        }

        var height = MathF.Min(UsesHeight, uses.Count * ImGui.GetTextLineHeightWithSpacing() + 4f);
        if (ImGui.BeginChild("##spriteUses", new Vector2(0f, height)))
        {
            foreach (var use in uses)
            {
                if (ImGui.Selectable(string.Create(CultureInfo.InvariantCulture, $"{use.Instance}  @{use.Entity}")))
                {
                    host.JumpTo(use);
                }
            }
        }

        ImGui.EndChild();
    }

    // "Replace with 3D model...", the row the sprite maps to, and that model in the Model Viewer window.
    private void Mapping(string key, float height)
    {
        ImGui.BeginDisabled(host.SaveMod == null);
        if (ImGui.Button("Replace with 3D model..."))
        {
            host.Replace(key);
        }

        ImGui.EndDisabled();
        if (host.SaveMod == null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip("No mod to write to: File > Save As picks one.");
        }

        if (host.Mapped(key) is not { } row)
        {
            return;
        }

        ImGui.TextColored(Mapped, string.Create(CultureInfo.InvariantCulture,
            $"3D (enhanced look): {row.Model}  scale {row.Scale}, yaw {row.Yaw}, spin {row.Spin}, bob {row.Bob}, offsetY {row.OffsetY}"));
        ImGui.SameLine();
        if (ImGui.SmallButton("Edit row"))
        {
            host.EditRow(key);
        }

        viewer?.SpriteModel(row, height);
    }

    // Play/pause, step, and the frame (animated records only).
    private void Playback(int frames, int msPerFrame)
    {
        if (frames <= 1)
        {
            _frame = 0;
            return;
        }

        var now = ImGui.GetTime();
        var ms = Math.Max(msPerFrame, GpuTexture.FallbackMsPerFrame);
        if (_playing && (now - _frameStart) * 1000.0 >= ms)
        {
            _frame = (_frame + 1) % frames;
            _frameStart = now;
        }

        if (ImGui.Button("<"))
        {
            (_playing, _frame) = (false, (_frame + frames - 1) % frames);
        }

        ImGui.SameLine();
        if (ImGui.Button(_playing ? "Pause" : "Play"))
        {
            (_playing, _frameStart) = (!_playing, now);
        }

        PlayKey.Tooltip();
        if (PlayKey.Handle(this, PlayKey.Read(PlayFocus.AssetViewer)))
        {
            _frameStart = now;
        }

        ImGui.SameLine();
        if (ImGui.Button(">"))
        {
            (_playing, _frame) = (false, (_frame + 1) % frames);
        }

        ImGui.SameLine();
        ImGui.TextUnformatted(string.Create(CultureInfo.InvariantCulture, $"frame {_frame + 1}/{frames}"));
    }
}
