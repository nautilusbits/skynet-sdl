using System.Numerics;
using RetroEng.Diagnostics;
using RetroEng.Render;

/// <summary>--churn: a worker thread makes, updates and destroys textures and meshes once per frame
/// while main presents (the renderer's off-main resource path, engine-threads plan T5). Main draws
/// the worker's newest mesh (a point: nothing shows); the worker destroys a mesh only once main has
/// stopped recording it, so a clean run reports no stale draws.
/// <code>
///   worker: create ─► update ─► destroy ─► publish mesh ─► wait for main's frame
///   main:   Draw(published) ─► frame count ─► Present (runs the worker's queued GPU work)
/// </code></summary>
internal sealed class ResourceChurn : IDisposable
{
    /// <summary>Main's frames after a mesh is replaced before the worker destroys it: one may still
    /// record it, the next draws its successor.</summary>
    private const int DrawnFrames = 2;
    private const int PaletteBytes = 256 * 4;

    private readonly Renderer _renderer;
    private readonly Thread _thread;
    private readonly AutoResetEvent _frame = new(false);
    private readonly Material _material = Material.Flat(Vector4.One, RenderState.Overlay);
    private readonly Lock _publish = new();
    private volatile bool _stop;
    private MeshHandle _published;
    private long _frames;
    private int _rounds;

    private ResourceChurn(Renderer renderer)
    {
        _renderer = renderer;
        _thread = new Thread(Run) { Name = "Churn", IsBackground = true };
        _thread.Start();
    }

    public static ResourceChurn Start(Renderer renderer) => new(renderer);

    /// <summary>Main, each frame before Present: the worker's newest mesh drawn, the worker woken.</summary>
    public void Draw()
    {
        MeshHandle mesh;
        lock (_publish)
        {
            mesh = _published;
        }

        // Its meshes join the open scope: a map exit's release takes the newest with the level.
        if (!mesh.IsNone && _renderer.Exists(mesh))
        {
            _renderer.Draw(mesh, 0, 3, _material);
        }

        Interlocked.Increment(ref _frames);
        _frame.Set();
    }

    private void Run()
    {
        Vertex[] point = [new(Vector3.Zero, Vector2.Zero), new(Vector3.Zero, Vector2.Zero), new(Vector3.Zero, Vector2.Zero)];
        // Meshes published and main's frame count then, the oldest first.
        var published = new List<(MeshHandle Mesh, long Frame)>();
        try
        {
            while (!_stop)
            {
                _frame.WaitOne();

                // Made, resized and destroyed within the round: textures, a dynamic mesh.
                var texture = _renderer.CreateIndexTexture(2, 2, new byte[4]);
                _renderer.UpdateTexture(texture, 4, 4, new byte[16]);
                var palette = _renderer.CreatePalette(new byte[PaletteBytes]);
                var dynamic = _renderer.CreateDynamicMesh(3);
                _renderer.UpdateMesh(dynamic, point);
                _renderer.Destroy(texture);
                _renderer.Destroy(palette);
                _renderer.Destroy(dynamic);

                // A mesh main draws: the one before destroyed once main records its successor.
                var mesh = _renderer.CreateMesh(point);
                lock (_publish)
                {
                    _published = mesh;
                }

                published.Add((mesh, Interlocked.Read(ref _frames)));
                while (published.Count > 1 && Interlocked.Read(ref _frames) >= published[1].Frame + DrawnFrames)
                {
                    _renderer.Destroy(published[0].Mesh);
                    published.RemoveAt(0);
                }

                _rounds++;
            }
        }
        catch (Exception e)
        {
            Log.Error("Churn", "the worker stopped", e);
        }
    }

    public void Dispose()
    {
        _stop = true;
        _frame.Set();
        _thread.Join();
        _frame.Dispose();
        Log.Info("Churn", $"{_rounds} rounds; {_renderer.TextureCount} textures, {_renderer.MeshCount} meshes live");
    }
}
