using System.Numerics;
using RetroEng.Editor;
using RetroEng.Physics;
using SkyNet.Formats;
using SkyNet.Game.Collision;

namespace SkyNet.Edit.Tests;

/// <summary>The Terrain mode's UI logic (plan step T7) against a fake model: the brush's ray on the
/// terrain, strokes as one undo step, tool switching and Shift, the pickers, and the panel's file and
/// generator commands.</summary>
public sealed class TerrainModeTests
{
    private const float Cell = BrushMath.CellSize;
    private static readonly float ZOrigin = (float)WldTerrain.ZFlipK;

    private static Vector3 Corner(float col, float row, float h) => new(col * Cell, h, row * Cell - ZOrigin);

    [Fact]
    public void A_ray_straight_down_meets_flat_ground_at_its_height()
    {
        var model = new FakeTerrain(17, 100f);
        var at = Corner(3.5f, 4.25f, 5000f);
        var hit = BrushMath.Raycast(model, at, -Vector3.UnitY);
        Assert.NotNull(hit);
        Assert.Equal(100f, hit.Value.Y, 2);
        Assert.Equal(at.X, hit.Value.X, 2);
        Assert.Equal(at.Z, hit.Value.Z, 2);
    }

    [Fact]
    public void A_slanted_ray_from_outside_the_field_meets_a_slope_where_the_ground_is()
    {
        // Height rises 64 per column: a ramp east.
        var model = new FakeTerrain(17, (c, _) => c * 64f);
        var from = Corner(-4f, 8.3f, 2000f);
        var dir = Vector3.Normalize(new Vector3(1f, -0.35f, 0.02f));
        var hit = BrushMath.Raycast(model, from, dir);
        Assert.NotNull(hit);
        Assert.Equal(BrushMath.HeightAt(model, hit.Value.X, hit.Value.Z), hit.Value.Y, 1);
        Assert.InRange(hit.Value.X, 0f, 16f * Cell);
    }

    [Fact]
    public void A_ray_up_or_beside_the_field_misses()
    {
        var model = new FakeTerrain(9, 0f);
        Assert.Null(BrushMath.Raycast(model, Corner(4f, 4f, 100f), Vector3.UnitY));
        Assert.Null(BrushMath.Raycast(model, Corner(-20f, 4f, 100f), -Vector3.UnitY));
    }

    [Fact]
    public void The_inner_ring_sits_where_the_falloff_weighs_half()
    {
        Assert.Equal(50f, BrushMath.HalfWeight(100f, BrushFalloff.Linear), 1);
        Assert.Equal(50f, BrushMath.HalfWeight(100f, BrushFalloff.Smooth), 1);
        Assert.Equal(100f, BrushMath.HalfWeight(100f, BrushFalloff.Constant), 1);
        Assert.Equal(86.6f, BrushMath.HalfWeight(100f, BrushFalloff.Sphere), 1);
    }

    [Fact]
    public void A_change_rebuilds_only_the_chunks_it_touches()
    {
        Assert.Equal([0, 1, 16, 17], BrushMath.Chunks(new GridRect(15, 15, 2, 2), WldTerrain.GridW).Order());
        Assert.Equal([255], BrushMath.Chunks(new GridRect(250, 250, 40, 40), WldTerrain.GridW));
    }

    [Fact]
    public void A_stroke_held_over_many_frames_is_one_undo_step()
    {
        var model = new FakeTerrain(17, 0f);
        var brush = new SculptBrush { Tool = TerrainTool.Raise };
        var hit = Corner(8f, 8f, 0f);
        Hold(brush, model, hit, frames: 5);
        Assert.Equal(1, model.Begun);
        Assert.Equal(5, model.Dabs.Count);
        Assert.Equal(1, model.Ended);
        Assert.Equal(1, model.History.UndoCount);
        Assert.Equal("Sculpt Raise", model.History.UndoLabel);

        Hold(brush, model, hit, frames: 3);
        Assert.Equal(2, model.History.UndoCount);
        Assert.True(model.History.Undo());
        Assert.Equal(5 * 1f, model.Height(8, 8), 3);
    }

    [Fact]
    public void A_stroke_on_the_terrain_model_raises_the_ground_as_one_undo_step()
    {
        var history = new UndoStack();
        var model = new TerrainModeModel(TerrainEdit.Create(999, history: history));
        var brush = new SculptBrush { Tool = TerrainTool.Raise, Radius = 1024f, Strength = 1f };
        Hold(brush, model, Corner(128f, 128f, 0f), frames: 30);
        Assert.True(model.Height(128, 128) > 0f);
        Assert.Equal(0f, model.Height(100, 128));
        Assert.Equal(1, history.UndoCount);
        Assert.Contains(BrushMath.Chunks(model.TakeDirty().Single(), model.Corners), c => c == 8 * 16 + 8);

        Assert.True(history.Undo());
        Assert.Equal(0f, model.Height(128, 128));
    }

    [Fact]
    public void After_a_dab_the_live_collision_field_matches_a_fresh_build()
    {
        var model = new TerrainModeModel(TerrainEdit.Create(999));
        var live = LiveGround.Of(model.Ground()!);
        model.TakeDirty();
        var brush = new SculptBrush { Tool = TerrainTool.Raise, Radius = 1024f, Strength = 1f };

        // Mid-stroke: one frame held, no release.
        brush.Frame(model, new BrushInput(Corner(128f, 128f, 0f), Left: true, Shift: false, Ctrl: false), 0.1f);
        Assert.True(brush.Stroking);
        foreach (var cells in model.TakeDirty())
        {
            live.Apply(model, cells);
        }

        var fresh = LevelCollision.Ground(model.Ground()!);
        Assert.True(fresh.HeightAt(128f * Cell, 128f * Cell - ZOrigin) > 0f);
        var liveWorld = new CollisionWorld().Add(live.Field);
        var freshWorld = new CollisionWorld().Add(fresh);
        for (var col = 120f; col <= 136f; col += 1.37f)
        {
            foreach (var dir in new[] { -Vector3.UnitY, Vector3.Normalize(new Vector3(0.6f, -1f, 0.3f)) })
            {
                var from = Corner(col, col * 0.9f + 12f, 20000f);
                Assert.Equal(freshWorld.Raycast(from, dir, 60000f, out var want), liveWorld.Raycast(from, dir, 60000f, out var got));
                Assert.Equal(want.Point, got.Point);
                var ray = new PickRay(from, dir);
                Assert.Equal(EntityPicking.GroundDistance(ray, fresh), EntityPicking.GroundDistance(ray, live.Field));
            }
        }
    }

    [Fact]
    public void Export_import_and_generate_go_through_the_terrain_model_as_previews()
    {
        var history = new UndoStack();
        var model = new TerrainModeModel(TerrainEdit.Create(999, history: history));
        var actions = new TerrainActions(() => model);
        Assert.True(actions.Generate(new GenerateSettings(3, 0.5f, 0f, 2400f)));
        Assert.Equal(model.Corners, actions.Pending!.Size);
        Assert.Equal(0, history.UndoCount);
        Assert.True(actions.ApplyPending());
        Assert.Equal(1, history.UndoCount);

        var png = Path.Combine(Path.GetTempPath(), $"terrain-mode-{Guid.NewGuid():N}.png");
        try
        {
            Assert.True(actions.ExportPng(png));
            Assert.True(actions.ImportPng(png));
            var grid = actions.Pending!;
            Assert.Equal(model.Height(40, 50), grid.At(40, 50), 1f);
        }
        finally
        {
            File.Delete(png);
        }
    }

    [Fact]
    public void A_press_off_the_terrain_starts_no_stroke()
    {
        var model = new FakeTerrain(9, 0f);
        var brush = new SculptBrush();
        brush.Frame(model, new BrushInput(null, Left: true, Shift: false, Ctrl: false), 0.016f);
        brush.Frame(model, new BrushInput(Corner(2f, 2f, 0f), Left: true, Shift: false, Ctrl: false), 0.016f);
        brush.Frame(model, new BrushInput(null, Left: false, Shift: false, Ctrl: false), 0.016f);
        Assert.Equal(0, model.Begun);
        Assert.Empty(model.Dabs);
    }

    [Fact]
    public void Shift_turns_raise_into_lower_and_back()
    {
        var brush = new SculptBrush { Tool = TerrainTool.Raise };
        Assert.Equal(TerrainTool.Lower, brush.Effective(shift: true));
        brush.Tool = TerrainTool.Lower;
        Assert.Equal(TerrainTool.Raise, brush.Effective(shift: true));
        brush.Tool = TerrainTool.Smooth;
        Assert.Equal(TerrainTool.Smooth, brush.Effective(shift: true));

        var model = new FakeTerrain(9, 0f);
        brush.Tool = TerrainTool.Raise;
        brush.Frame(model, new BrushInput(Corner(2f, 2f, 0f), Left: true, Shift: true, Ctrl: false), 0.1f);
        Assert.Equal(TerrainTool.Lower, model.Dabs[^1].Tool);
        Assert.Equal(brush.Strength * SculptBrush.SculptRate * 0.1f, model.Dabs[^1].Amount, 3);

    }

    [Fact]
    public void Turn_acts_once_per_press_however_long_it_is_held()
    {
        var model = new FakeTerrain(9, 0f);
        var brush = new SculptBrush { Tool = TerrainTool.Turn };
        Hold(brush, model, Corner(2.5f, 2.5f, 0f), frames: 6);
        Hold(brush, model, Corner(3.5f, 2.5f, 0f), frames: 2);
        Assert.Equal([TerrainTool.Turn, TerrainTool.Turn], model.Dabs.Select(d => d.Tool));
        Assert.Equal(2, model.History.UndoCount);
        Assert.Equal("Terrain Turn", model.History.UndoLabel);
    }

    [Fact]
    public void Turn_on_the_terrain_model_turns_the_clicked_cells_tile()
    {
        var history = new UndoStack();
        var terrain = TerrainEdit.Create(999, history: history);
        var model = new TerrainModeModel(terrain);
        var brush = new SculptBrush { Tool = TerrainTool.Turn };
        Hold(brush, model, Corner(10.5f, 20.5f, 0f), frames: 3);
        Assert.Equal(TileTurn.Quarter, terrain.Turn(10, 20));
        Assert.Equal(TileTurn.None, terrain.Turn(11, 20));
        Assert.Equal(1, history.UndoCount);
    }

    [Fact]
    public void Bracket_keys_scale_the_radius_within_its_limits()
    {
        var brush = new SculptBrush { Radius = 1000f };
        brush.Grow();
        Assert.Equal(1000f * SculptBrush.RadiusStep, brush.Radius, 2);
        brush.Shrink();
        brush.Shrink();
        Assert.Equal(1000f / SculptBrush.RadiusStep, brush.Radius, 2);
        for (var i = 0; i < 100; i++)
        {
            brush.Grow();
        }

        Assert.Equal(SculptBrush.MaxRadius, brush.Radius);
    }

    [Fact]
    public void Ctrl_click_picks_flatten_height_and_paint_tile_without_a_stroke()
    {
        var model = new FakeTerrain(9, 320f);
        model.SetTile(3, 4, 0x47);
        var brush = new SculptBrush { Tool = TerrainTool.Flatten };
        Click(brush, model, Corner(3.5f, 4.5f, 0f), ctrl: true);
        Assert.Equal(320f, brush.Target, 2);

        brush.Tool = TerrainTool.Paint;
        Click(brush, model, Corner(3.5f, 4.5f, 0f), ctrl: true);
        Assert.Equal(0x47, brush.Tile);
        Assert.Equal(0, model.Begun);

        // Flatten and Paint dabs carry what was picked.
        Click(brush, model, Corner(1.5f, 1.5f, 0f), ctrl: false);
        Assert.Equal((TerrainTool.Paint, 0x47), (model.Dabs[^1].Tool, model.Dabs[^1].Tile));
    }

    [Fact]
    public void Import_png_stages_a_preview_that_apply_puts_on_as_one_step()
    {
        var model = new FakeTerrain(9, 0f);
        var actions = new TerrainActions(() => model);
        Assert.True(actions.ImportPng("hills.png"));
        Assert.Equal(["ImportPng hills.png"], model.Calls);
        Assert.NotNull(actions.Pending);
        Assert.Equal(0, model.History.UndoCount);

        Assert.True(actions.ApplyPending());
        Assert.Null(actions.Pending);
        Assert.Equal("Apply Import hills.png", model.Calls[^1]);
        Assert.Equal(1, model.History.UndoCount);
        Assert.False(actions.ApplyPending());
    }

    [Fact]
    public void Export_mesh_import_and_generate_reach_their_model_operations()
    {
        var model = new FakeTerrain(9, 0f);
        var actions = new TerrainActions(() => model);
        Assert.True(actions.ExportPng("out.png"));
        Assert.True(actions.ImportMesh("cliff.glb"));
        var settings = new GenerateSettings(7, 0.3f, 100f, 900f);
        Assert.True(actions.Generate(settings));
        Assert.Equal(["ExportPng out.png", "ImportMesh cliff.glb", $"Generate {settings}"], model.Calls);
        Assert.Equal("Generate terrain", actions.PendingLabel);
        actions.Discard();
        Assert.Null(actions.Pending);
        Assert.Equal(0, model.History.UndoCount);
    }

    [Fact]
    public void A_failed_import_is_reported_not_thrown()
    {
        var model = new FakeTerrain(9, 0f) { Fail = new InvalidDataException("not a grayscale PNG") };
        var actions = new TerrainActions(() => model);
        Assert.False(actions.ImportPng("bad.png"));
        Assert.Equal("not a grayscale PNG", actions.Error);
        Assert.Null(actions.Pending);

        Assert.False(new TerrainActions(() => null).ExportPng("x.png"));
    }

    // Press, held for frames, release: frames dabs at 1/frames s each... one per frame held.
    private static void Hold(SculptBrush brush, ITerrainModel model, Vector3 hit, int frames)
    {
        for (var i = 0; i < frames; i++)
        {
            brush.Frame(model, new BrushInput(hit, Left: true, Shift: false, Ctrl: false), 1f / SculptBrush.SculptRate / brush.Strength);
        }

        brush.Frame(model, new BrushInput(hit, Left: false, Shift: false, Ctrl: false), 0.016f);
    }

    private static void Click(SculptBrush brush, FakeTerrain model, Vector3 hit, bool ctrl)
    {
        brush.Frame(model, new BrushInput(hit, Left: true, Shift: false, Ctrl: ctrl), 0.016f);
        brush.Frame(model, new BrushInput(hit, Left: false, Shift: false, Ctrl: ctrl), 0.016f);
    }

    /// <summary>A terrain model in memory: Raise dabs add their amount at the corner nearest the
    /// centre, each dab a command merged into the open stroke; file operations only recorded.</summary>
    private sealed class FakeTerrain : ITerrainModel
    {
        private readonly float[] _heights;
        private readonly int[] _tiles;
        private readonly List<GridRect> _dirty = [];
        private object? _stroke;
        private string _label = "";

        public FakeTerrain(int corners, float height) : this(corners, (_, _) => height)
        {
        }

        public FakeTerrain(int corners, Func<int, int, float> height)
        {
            Corners = corners;
            _heights = new float[corners * corners];
            _tiles = new int[corners * corners];
            for (var i = 0; i < _heights.Length; i++)
            {
                _heights[i] = height(i % corners, i / corners);
            }
        }

        public UndoStack History { get; } = new();
        public List<BrushDab> Dabs { get; } = [];
        public List<string> Calls { get; } = [];
        public int Begun { get; private set; }
        public int Ended { get; private set; }
        public Exception? Fail { get; init; }

        public int Corners { get; }

        public float Height(int col, int row) => _heights[row * Corners + col];

        public int Tile(int col, int row) => _tiles[row * Corners + col];

        public int Map => 999;

        public WldTerrain? Ground() => null;

        public byte[]? Edited() => null;

        public string? Save(string modFolder) => null;

        public void WriteCopy(string modFolder)
        {
        }

        public void SetTile(int col, int row, int tile) => _tiles[row * Corners + col] = tile;

        public void BeginStroke(string label)
        {
            Begun++;
            (_stroke, _label) = (new object(), label);
        }

        public void Dab(in BrushDab dab)
        {
            Dabs.Add(dab);
            var (col, row) = ((int)MathF.Round(dab.Centre.X / Cell), (int)MathF.Round((dab.Centre.Y + ZOrigin) / Cell));
            var sign = dab.Tool == TerrainTool.Lower ? -1f : dab.Tool == TerrainTool.Raise ? 1f : 0f;
            History.Execute(new Step(this, row * Corners + col, sign * dab.Amount, _label, _stroke));
        }

        public void EndStroke()
        {
            Ended++;
            _stroke = null;
            History.EndMerge();
        }

        public IReadOnlyList<GridRect> TakeDirty()
        {
            var taken = _dirty.ToList();
            _dirty.Clear();
            return taken;
        }

        public HeightGrid ImportPng(string path) => Grid($"ImportPng {path}");

        public void ExportPng(string path) => Grid($"ExportPng {path}");

        public HeightGrid ImportMesh(string path) => Grid($"ImportMesh {path}");

        public HeightGrid Generate(GenerateSettings settings) => Grid($"Generate {settings}");

        public void Apply(HeightGrid grid, string label)
        {
            Calls.Add($"Apply {label}");
            History.Execute(new Step(this, 0, 0f, label, null));
        }

        private HeightGrid Grid(string call)
        {
            if (Fail != null)
            {
                throw Fail;
            }

            Calls.Add(call);
            return new HeightGrid(Corners, new float[Corners * Corners]);
        }

        private sealed class Step(FakeTerrain t, int index, float delta, string label, object? stroke) : IUndoCommand
        {
            private float _delta = delta;

            public string Label => label;

            public void Do() => Change(_delta);

            public void Undo() => Change(-_delta);

            public bool TryMerge(IUndoCommand next)
            {
                if (stroke == null || next is not Step s || !ReferenceEquals(s.Stroke, stroke) || s.Index != index)
                {
                    return false;
                }

                _delta += s.Delta;
                return true;
            }

            private object? Stroke => stroke;
            private int Index => index;
            private float Delta => _delta;

            private void Change(float d)
            {
                t._heights[index] += d;
                t._dirty.Add(new GridRect(index % t.Corners, index / t.Corners, 1, 1));
            }
        }
    }
}
