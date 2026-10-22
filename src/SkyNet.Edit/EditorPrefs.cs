using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using RetroEng.Diagnostics;
using RetroEng.Editor;
using RetroEng.Render;
using SkyNet.Formats;
using SkyNet.Game.Menu;

namespace SkyNet.Edit;

/// <summary>Where the editor was when it closed: the mission (or arena) open, its shown instance and the camera.</summary>
public sealed record EditorPlace(GameKind Game, int Number, bool Arena, string? Instance, Vector3? Eye = null, float Yaw = 0f, float Pitch = 0f);

/// <summary>The editor's preferences (Settings window, View menu, toolbar, panels), kept in SkyNetEdit.json
/// beside the exe: read at start, written a moment after they change and on exit. The command line
/// overrides them for its run.</summary>
public sealed class EditorPrefs
{
    public const string FileName = "SkyNetEdit.json";
    public const int MaxRecent = 8;

    /// <summary>The fly camera's speed in world units a second (Shift: four times).</summary>
    public float CameraSpeed { get; set; } = 1500f;
    /// <summary>The look's speed, a multiple of 0.004 radian a pixel.</summary>
    public float MouseSensitivity { get; set; } = 1f;
    public bool InvertY { get; set; }
    /// <summary>The vertical field of view in degrees.</summary>
    public float FieldOfView { get; set; } = 70f;
    /// <summary>The view's far clip in world units (the game's far command); 0: the map's own.</summary>
    public float DrawDistance { get; set; }
    /// <summary>The view distance's range in world units (RENDER DETAIL's nearest is 1176).</summary>
    public const float MinDrawDistance = 500f, MaxDrawDistance = 100000f;
    public Look DefaultLook { get; set; } = Look.Original;
    /// <summary>The game's data folder (null: SKYNET_DATA, else the usual places).</summary>
    public string? DataDir { get; set; }
    /// <summary>The mods folder (null: mods\ beside the editor).</summary>
    public string? ModsDir { get; set; }
    public const float DefaultFontSize = 13f;
    public const float MinUiScale = 0.5f, MaxUiScale = 3f;
    /// <summary>The panels' scale (ImGui's main font scale and the style's sizes).</summary>
    public float UiScale { get; set; } = 1f;
    /// <summary>The panels' font size in points at scale 1.</summary>
    public float FontSize { get; set; } = DefaultFontSize;
    /// <summary>The scale follows the system's (the display the editor's window is on); false once the user
    /// sets it by hand. A file without a uiScale (the first start) is automatic.</summary>
    public bool UiScaleAuto { get; set; } = true;

    /// <summary>The system's scale <paramref name="systemScale"/> taken (default font size) when the scale
    /// is automatic; true when that changed a preference.</summary>
    public bool FollowSystemScale(float systemScale)
    {
        if (!UiScaleAuto)
        {
            return false;
        }

        var scale = float.IsFinite(systemScale) && systemScale > 0f ? Math.Clamp(systemScale, MinUiScale, MaxUiScale) : 1f;
        if (scale == UiScale && FontSize == DefaultFontSize)
        {
            return false;
        }

        (UiScale, FontSize) = (scale, DefaultFontSize);
        return true;
    }

    /// <summary>Settings' Reset to system: the scale automatic again and the system's taken.</summary>
    public void ResetToSystemScale(float systemScale)
    {
        UiScaleAuto = true;
        FollowSystemScale(systemScale);
    }
    /// <summary>The mods enabled, in load order (later wins).</summary>
    public List<string> Mods { get; set; } = [];
    /// <summary>The missions opened last, newest first.</summary>
    public List<int> Recent { get; set; } = [];
    public bool ShowOutliner { get; set; } = true;
    public bool ShowProperties { get; set; } = true;
    public bool ShowLog { get; set; } = true;
    public bool ShowPlace { get; set; } = true;
    public bool ShowTriggers { get; set; } = true;
    public bool ShowMission { get; set; }
    public bool ShowValidation { get; set; }
    public bool ShowAssets { get; set; }
    /// <summary>The grid sizes offered (DOS units).</summary>
    public static readonly int[] GridSizes = [8, 16, 32, 64, 128, 256, 512, 1024];
    /// <summary>The angle steps offered (11-bit units, 2048 a turn).</summary>
    public static readonly int[] AngleSteps = [16, 32, 64, 128, 256, 512];
    public bool SnapGrid { get; set; }
    /// <summary>The move grid in DOS units.</summary>
    public int GridSize { get; set; } = 64;
    public bool SnapAngle { get; set; }
    /// <summary>The angle step in 11-bit units.</summary>
    public int AngleStep { get; set; } = 128;
    public bool SnapFloor { get; set; }
    /// <summary>The gizmo on the entity's own axes (else the world's).</summary>
    public bool GizmoLocal { get; set; }
    /// <summary>The main window's placement when it last closed (null: the default, centred).</summary>
    public RetroEng.Editor.WindowState? Window { get; set; }
    // View menu: what the viewport draws.
    public bool ShowLinks { get; set; } = true;
    public bool ShowMarkers { get; set; } = true;
    public bool ShowLights { get; set; } = true;
    public bool ShowSprites { get; set; } = true;
    public bool ShowRobots { get; set; } = true;
    /// <summary>The enhanced look's lamp-post pools, flames and particles.</summary>
    public bool ShowEffects { get; set; } = true;
    /// <summary>The toolbar's gizmo (W move, E rotate).</summary>
    public GizmoMode Gizmo { get; set; } = GizmoMode.Translate;
    /// <summary>The Place panel's tab.</summary>
    public PlaceKind PlaceTab { get; set; } = PlaceKind.Robot;
    // The Assets panel's filters (kind null: the first).
    public string? AssetKind { get; set; }
    public AssetOrigin? AssetOrigin { get; set; }
    public bool AssetMapOnly { get; set; }
    public string AssetSearch { get; set; } = "";
    /// <summary>The folder mod a plain Save writes to (null: the last enabled folder mod).</summary>
    public string? SaveMod { get; set; }
    /// <summary>Where the editor was when it closed (null: mission 1 from its start).</summary>
    public EditorPlace? Place { get; set; }
    /// <summary>The MCP port when nothing else names one (a fixed one: an agent's config keeps working).</summary>
    public const int DefaultMcpPort = 47800;
    /// <summary>AI agents drive the editor over MCP (localhost only); off by default.</summary>
    public bool Mcp { get; set; }
    /// <summary>The start-up splash (title art, version, loading status); automated runs skip it.</summary>
    public bool ShowSplash { get; set; } = true;
    /// <summary>The MCP server's port (0: any free one).</summary>
    public int McpPort { get; set; } = DefaultMcpPort;

    // The game's ENHANCED OPTIONS (its defaults): the view lit as the game lights it.
    private static readonly MenuSettings GameDefaults = new();
    public ShadowQuality Shadows { get; set; } = GameDefaults.Shadows;
    public AntiAliasing AntiAliasing { get; set; } = GameDefaults.AntiAliasing;
    /// <summary><see cref="MenuSettings.LowLightBudget"/> or <see cref="MenuSettings.HighLightBudget"/>.</summary>
    public int LightBudget { get; set; } = GameDefaults.LightBudget;
    /// <summary>In % (<see cref="MenuSettings.MinAmbient"/>..<see cref="MenuSettings.MaxAmbient"/>).</summary>
    public int Ambient { get; set; } = GameDefaults.Ambient;
    public bool AmbientOcclusion { get; set; } = GameDefaults.AmbientOcclusion;
    public bool Glow { get; set; } = GameDefaults.Glow;
    public ReliefLevel Relief { get; set; } = GameDefaults.Relief;
    public ParticleLevel Particles { get; set; } = GameDefaults.Particles;

    /// <summary>The ENHANCED OPTIONS onto <paramref name="s"/> (the look's settings the view renders with).</summary>
    public void ApplyTo(MenuSettings s)
    {
        (s.Shadows, s.AntiAliasing, s.LightBudget, s.Ambient) = (Shadows, AntiAliasing, LightBudget, Ambient);
        (s.AmbientOcclusion, s.Glow, s.Relief, s.Particles) = (AmbientOcclusion, Glow, Relief, Particles);
    }

    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, FileName);

    /// <summary>Settings > Reset settings: every setting back to its default. Kept: the layout (the window's
    /// place, the panels shown: View > Reset layout's), the history (recent missions, where the editor was)
    /// and what the editor needs to start (the data and mods folders, the mods enabled).</summary>
    public EditorPrefs Reset() => new()
    {
        Window = Window, Recent = [.. Recent], Place = Place, DataDir = DataDir, ModsDir = ModsDir, Mods = [.. Mods],
        ShowOutliner = ShowOutliner, ShowProperties = ShowProperties, ShowLog = ShowLog, ShowPlace = ShowPlace,
        ShowTriggers = ShowTriggers, ShowMission = ShowMission, ShowValidation = ShowValidation, ShowAssets = ShowAssets,
    };

    /// <summary>Puts <paramref name="mission"/> first in the recent list.</summary>
    public void Opened(int mission)
    {
        Recent.Remove(mission);
        Recent.Insert(0, mission);
        if (Recent.Count > MaxRecent)
        {
            Recent.RemoveRange(MaxRecent, Recent.Count - MaxRecent);
        }
    }

    /// <summary>The preferences in <paramref name="path"/>; defaults where it is missing or does not read
    /// (logged), and for every value out of range.</summary>
    public static EditorPrefs Load(string path)
    {
        var p = new EditorPrefs();
        if (!File.Exists(path))
        {
            return p;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            return Read(doc.RootElement);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warn("Edit", $"{path} does not read ({e.Message}): the default preferences");
            return new EditorPrefs();
        }
    }

    /// <summary>The preferences in <paramref name="r"/>: defaults for what is missing, of another type or out of range.</summary>
    private static EditorPrefs Read(JsonElement r)
    {
        var p = new EditorPrefs();
        if (r.ValueKind != JsonValueKind.Object)
        {
            return p;
        }

        {
            float F(string name, float def, float min, float max) =>
                r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetSingle(out var f) && float.IsFinite(f) ? Math.Clamp(f, min, max) : def;
            bool B(string name, bool def) => r.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : def;
            string? S(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
            p.CameraSpeed = F("cameraSpeed", p.CameraSpeed, 50f, 20000f);
            p.MouseSensitivity = F("mouseSensitivity", p.MouseSensitivity, 0.1f, 10f);
            p.InvertY = B("invertY", p.InvertY);
            p.FieldOfView = F("fieldOfView", p.FieldOfView, 30f, 120f);
            p.DrawDistance = ViewDistance(F("drawDistance", p.DrawDistance, 0f, MaxDrawDistance));
            p.DefaultLook = S("defaultLook") == "enhanced" ? Look.Enhanced : Look.Original;
            p.DataDir = S("dataDir");
            p.ModsDir = S("modsDir");
            p.UiScale = F("uiScale", p.UiScale, MinUiScale, MaxUiScale);
            // A scale already kept and no flag: the user's (from before the flag).
            p.UiScaleAuto = !r.TryGetProperty("uiScale", out _) || B("uiScaleAuto", false);
            p.FontSize = F("fontSize", p.FontSize, 8f, 40f);
            p.ShowOutliner = B("showOutliner", p.ShowOutliner);
            p.ShowProperties = B("showProperties", p.ShowProperties);
            p.ShowLog = B("showLog", p.ShowLog);
            p.ShowPlace = B("showPlace", p.ShowPlace);
            p.ShowTriggers = B("showTriggers", p.ShowTriggers);
            p.ShowMission = B("showMission", p.ShowMission);
            p.ShowValidation = B("showValidation", p.ShowValidation);
            p.ShowAssets = B("showAssets", p.ShowAssets);
            p.SnapGrid = B("snapGrid", p.SnapGrid);
            p.GridSize = (int)F("gridSize", p.GridSize, 1f, 4096f);
            p.SnapAngle = B("snapAngle", p.SnapAngle);
            p.AngleStep = (int)F("angleStep", p.AngleStep, 1f, 1024f);
            p.SnapFloor = B("snapFloor", p.SnapFloor);
            p.GizmoLocal = B("gizmoLocal", p.GizmoLocal);
            p.Window = ReadWindow(r);
            p.ShowLinks = B("showLinks", p.ShowLinks);
            p.ShowMarkers = B("showMarkers", p.ShowMarkers);
            p.ShowLights = B("showLights", p.ShowLights);
            p.ShowSprites = B("showSprites", p.ShowSprites);
            p.ShowRobots = B("showRobots", p.ShowRobots);
            p.ShowEffects = B("showEffects", p.ShowEffects);
            p.Gizmo = Named(S("gizmo"), p.Gizmo);
            p.PlaceTab = Named(S("placeTab"), p.PlaceTab);
            p.AssetKind = S("assetKind");
            p.AssetOrigin = S("assetOrigin") is { } origin ? Named(origin, RetroEng.Editor.AssetOrigin.Base) : null;
            p.AssetMapOnly = B("assetMapOnly", p.AssetMapOnly);
            p.AssetSearch = S("assetSearch") ?? "";
            p.SaveMod = S("saveMod") is { } mod && SkyNet.Game.Mods.ModOrder.IsName(mod) ? mod : null;
            p.Place = ReadPlace(r);
            p.Mcp = B("mcp", p.Mcp);
            p.ShowSplash = B("showSplash", p.ShowSplash);
            p.McpPort = (int)F("mcpPort", p.McpPort, 0f, ushort.MaxValue);
            p.Shadows = Named(S("shadows"), p.Shadows);
            p.AntiAliasing = Named(S("antiAliasing"), p.AntiAliasing);
            // Only the two budgets the game offers.
            var budget = (int)F("lightBudget", p.LightBudget, 0f, MenuSettings.HighLightBudget);
            p.LightBudget = budget is MenuSettings.LowLightBudget or MenuSettings.HighLightBudget ? budget : p.LightBudget;
            p.Ambient = (int)F("ambient", p.Ambient, MenuSettings.MinAmbient, MenuSettings.MaxAmbient);
            p.AmbientOcclusion = B("ambientOcclusion", p.AmbientOcclusion);
            p.Glow = B("glow", p.Glow);
            p.Relief = Named(S("relief"), p.Relief);
            p.Particles = Named(S("particles"), p.Particles);
            if (r.TryGetProperty("mods", out var mods) && mods.ValueKind == JsonValueKind.Array)
            {
                p.Mods = SkyNet.Game.Mods.ModOrder.Clean(mods.EnumerateArray().Where(m => m.ValueKind == JsonValueKind.String).Select(m => m.GetString()!));
            }

            if (r.TryGetProperty("recent", out var recent) && recent.ValueKind == JsonValueKind.Array)
            {
                p.Recent = [.. recent.EnumerateArray().Where(m => m.ValueKind == JsonValueKind.Number && m.TryGetInt32(out _))
                    .Select(m => m.GetInt32()).Distinct().Take(MaxRecent)];
            }
        }

        return p;
    }

    /// <summary>The enum value named <paramref name="name"/> (any case); <paramref name="def"/> for none or another word.</summary>
    private static T Named<T>(string? name, T def) where T : struct, Enum =>
        name != null && Enum.TryParse<T>(name, ignoreCase: true, out var v) && Enum.IsDefined(v) ? v : def;

    /// <summary>The "place" object; null when missing or broken.</summary>
    private static EditorPlace? ReadPlace(JsonElement root)
    {
        if (!root.TryGetProperty("place", out var o) || o.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!o.TryGetProperty("number", out var n) || n.ValueKind != JsonValueKind.Number || !n.TryGetInt32(out var number))
        {
            return null;
        }

        float? F(string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetSingle(out var f) && float.IsFinite(f) ? f : null;
        var game = o.TryGetProperty("game", out var g) && g.ValueKind == JsonValueKind.String ? Named(g.GetString(), GameKind.SkyNet) : GameKind.SkyNet;
        var arena = o.TryGetProperty("arena", out var a) && a.ValueKind == JsonValueKind.True;
        var instance = o.TryGetProperty("instance", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null;
        Vector3? eye = F("x") is { } x && F("y") is { } y && F("z") is { } z ? new Vector3(x, y, z) : null;
        return new EditorPlace(game, number, arena, instance, eye, F("yaw") ?? 0f, Math.Clamp(F("pitch") ?? 0f, -MaxPitch, MaxPitch));
    }

    /// <summary>The camera's pitch limit in radians (the fly camera's).</summary>
    public const float MaxPitch = 1.55f;

    /// <summary><paramref name="units"/> as a view distance: 0 or less the map's own (0), else within the range.</summary>
    public static float ViewDistance(float units) => units <= 0f ? 0f : Math.Clamp(units, MinDrawDistance, MaxDrawDistance);

    /// <summary>These preferences with <paramref name="name"/> (its key in the file, e.g. "showRobots") set to
    /// <paramref name="value"/> (JSON, or a bare word as a string), read back as the file would be; null with
    /// the reason in <paramref name="error"/> for an unknown name.</summary>
    public EditorPrefs? With(string name, string value, out string? error)
    {
        error = null;
        var o = JsonNode.Parse(ToJson())!.AsObject();
        if (!o.ContainsKey(name))
        {
            error = $"no preference \"{name}\" (one of {string.Join(", ", o.Select(kv => kv.Key))})";
            return null;
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(value);
        }
        catch (JsonException)
        {
            // A bare word: a string ("enhanced", "rotate").
            node = JsonValue.Create(value);
        }

        o[name] = node;
        using var doc = JsonDocument.Parse(o.ToJsonString());
        return Read(doc.RootElement);
    }

    /// <summary>The value of <paramref name="name"/> as the file holds it (null: no such preference).</summary>
    public string? Get(string name) =>
        JsonNode.Parse(ToJson())!.AsObject().TryGetPropertyValue(name, out var v) ? v?.ToJsonString() ?? "null" : null;

    /// <summary>The "window" object: its rectangle (sizes at least 1), its state and display; null when missing or broken.</summary>
    private static RetroEng.Editor.WindowState? ReadWindow(JsonElement root)
    {
        if (!root.TryGetProperty("window", out var w) || w.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        int? I(string name) => w.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;
        bool B(string name) => w.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        if (I("x") is not { } x || I("y") is not { } y || I("width") is not { } width || I("height") is not { } height
            || width <= 0 || height <= 0)
        {
            return null;
        }

        var display = w.TryGetProperty("display", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
        return new RetroEng.Editor.WindowState(x, y, width, height, B("maximized"), B("fullscreen"), display);
    }

    /// <summary>Writes the preferences to <paramref name="path"/>; false (logged) when it cannot be written.</summary>
    public bool Save(string path)
    {
        try
        {
            File.WriteAllBytes(path, ToJson());
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Edit", $"{path} cannot be written ({e.Message})");
            return false;
        }
    }

    /// <summary>The file's text: indented JSON, UTF-8.</summary>
    public byte[] ToJson()
    {
        using var ms = new MemoryStream();
        {
            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                w.WriteNumber("cameraSpeed", CameraSpeed);
                w.WriteNumber("mouseSensitivity", MouseSensitivity);
                w.WriteBoolean("invertY", InvertY);
                w.WriteNumber("fieldOfView", FieldOfView);
                w.WriteNumber("drawDistance", DrawDistance);
                w.WriteString("defaultLook", DefaultLook == Look.Enhanced ? "enhanced" : "original");
                w.WriteString("dataDir", DataDir);
                w.WriteString("modsDir", ModsDir);
                w.WriteNumber("uiScale", UiScale);
                w.WriteNumber("fontSize", FontSize);
                w.WriteBoolean("uiScaleAuto", UiScaleAuto);
                w.WriteBoolean("showOutliner", ShowOutliner);
                w.WriteBoolean("showProperties", ShowProperties);
                w.WriteBoolean("showLog", ShowLog);
                w.WriteBoolean("showPlace", ShowPlace);
                w.WriteBoolean("showTriggers", ShowTriggers);
                w.WriteBoolean("showMission", ShowMission);
                w.WriteBoolean("showValidation", ShowValidation);
                w.WriteBoolean("showAssets", ShowAssets);
                w.WriteBoolean("snapGrid", SnapGrid);
                w.WriteNumber("gridSize", GridSize);
                w.WriteBoolean("snapAngle", SnapAngle);
                w.WriteNumber("angleStep", AngleStep);
                w.WriteBoolean("snapFloor", SnapFloor);
                w.WriteBoolean("gizmoLocal", GizmoLocal);
                w.WriteBoolean("showLinks", ShowLinks);
                w.WriteBoolean("showMarkers", ShowMarkers);
                w.WriteBoolean("showLights", ShowLights);
                w.WriteBoolean("showSprites", ShowSprites);
                w.WriteBoolean("showRobots", ShowRobots);
                w.WriteBoolean("showEffects", ShowEffects);
                w.WriteString("gizmo", Gizmo.ToString().ToLowerInvariant());
                w.WriteString("placeTab", PlaceTab.ToString().ToLowerInvariant());
                w.WriteString("assetKind", AssetKind);
                w.WriteString("assetOrigin", AssetOrigin?.ToString().ToLowerInvariant());
                w.WriteBoolean("assetMapOnly", AssetMapOnly);
                w.WriteString("assetSearch", AssetSearch);
                w.WriteString("saveMod", SaveMod);
                w.WriteBoolean("mcp", Mcp);
                w.WriteBoolean("showSplash", ShowSplash);
                w.WriteNumber("mcpPort", McpPort);
                w.WriteString("shadows", Shadows.ToString().ToLowerInvariant());
                w.WriteString("antiAliasing", AntiAliasing.ToString().ToLowerInvariant());
                w.WriteNumber("lightBudget", LightBudget);
                w.WriteNumber("ambient", Ambient);
                w.WriteBoolean("ambientOcclusion", AmbientOcclusion);
                w.WriteBoolean("glow", Glow);
                w.WriteString("relief", Relief.ToString().ToLowerInvariant());
                w.WriteString("particles", Particles.ToString().ToLowerInvariant());
                if (Place is { } place)
                {
                    w.WriteStartObject("place");
                    w.WriteString("game", place.Game.ToString().ToLowerInvariant());
                    w.WriteNumber("number", place.Number);
                    w.WriteBoolean("arena", place.Arena);
                    w.WriteString("instance", place.Instance);
                    if (place.Eye is { } eye)
                    {
                        w.WriteNumber("x", eye.X);
                        w.WriteNumber("y", eye.Y);
                        w.WriteNumber("z", eye.Z);
                        w.WriteNumber("yaw", place.Yaw);
                        w.WriteNumber("pitch", place.Pitch);
                    }

                    w.WriteEndObject();
                }
                if (Window is { } window)
                {
                    w.WriteStartObject("window");
                    w.WriteNumber("x", window.X);
                    w.WriteNumber("y", window.Y);
                    w.WriteNumber("width", window.Width);
                    w.WriteNumber("height", window.Height);
                    w.WriteBoolean("maximized", window.Maximized);
                    w.WriteBoolean("fullscreen", window.Fullscreen);
                    w.WriteString("display", window.Display);
                    w.WriteEndObject();
                }

                w.WriteStartArray("mods");
                foreach (var m in Mods)
                {
                    w.WriteStringValue(m);
                }

                w.WriteEndArray();
                w.WriteStartArray("recent");
                foreach (var m in Recent)
                {
                    w.WriteNumberValue(m);
                }

                w.WriteEndArray();
                w.WriteEndObject();
            }
        }

        return ms.ToArray();
    }
}
