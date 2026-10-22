using System.Numerics;
using RetroEng.Loop;
using RetroEng.World;
using SkyNet.Formats;
using SkyNet.Game.Baking;
using SkyNet.Game.Campaign;

namespace SkyNet.Edit;

/// <summary>
/// Play > Preview (editor plan step E9), the part without a window: the edited mission played by the
/// game's own <see cref="GameSession"/> and <see cref="CampaignControls"/>. The session reads its own copy
/// of the data (so its layer choices stay out of the editor's), and every map it loads goes through the
/// document: a layer the document holds plays as edited (<see cref="GameData.LayerResolver"/> wrapped
/// while the preview runs), any other map as the game would play it. The document itself is only read.
/// </summary>
public sealed class PreviewSession : IDisposable
{
    private readonly Func<int, string, MapFile, MapFile>? _resolver;
    private readonly Func<int, byte[]?>? _wldResolver;
    private bool _disposed;

    /// <summary>Starts the preview: <paramref name="mission"/> (the document's mission file, or the game's
    /// own when the document has none, as an arena) entered at <paramref name="instance"/> by arrival set
    /// <paramref name="arrival"/>; with <paramref name="feet"/> the soldier then stands there looking along
    /// <paramref name="yaw"/>. <paramref name="cleanBlocks"/> gives a map's baked block when its layer has
    /// no edits (null: the session builds collision from the map). <paramref name="terrains"/> gives a map's
    /// edited WLD (null: as the game loads it), <see cref="GameData.WldResolver"/> wrapped likewise.</summary>
    public PreviewSession(GameData data, EditDocument doc, Mission? mission, string instance, int arrival,
        Func<WorldInstance, MapBlock?>? cleanBlocks = null, Vector3? feet = null, float yaw = 0f, Func<int, byte[]?>? terrains = null)
    {
        _wldResolver = GameData.WldResolver;
        var previousWld = _wldResolver;
        GameData.WldResolver = map => terrains?.Invoke(map) ?? previousWld?.Invoke(map);
        _resolver = GameData.LayerResolver;
        var previous = _resolver;
        GameData.LayerResolver = (number, layer, map) =>
            doc.Layers.Contains(layer) && doc.MapNumber(layer) == number ? doc.Map(layer)
            : previous != null ? previous(number, layer, map) : map;
        Data = GameData.Open(data.Dir, data.SkyNetDir) ?? data;
        Session = new GameSession(Data)
        {
            AutoRestart = true,
            Missions = _ => doc.Mission,
            Logger = text => RetroEng.Diagnostics.Log.Info("Preview", text),
        };
        Session.Blocks = _ => Session.Instance is { } i && cleanBlocks != null ? cleanBlocks(i) : null;
        try
        {
            if (doc.Mission?.World.Instance(instance) is not { } start)
            {
                throw new ArgumentException($"the document has no instance {instance}", nameof(instance));
            }

            if (mission is { } m)
            {
                Session.StartMission(m);
                if (Session.Instance?.Id != instance)
                {
                    Session.EnterInstance(instance, arrival);
                }
            }
            else
            {
                // An arena: its map entered on its own.
                Session.StartAt(MissionFile.LayerMap(start));
            }
        }
        catch
        {
            (GameData.LayerResolver, GameData.WldResolver) = (_resolver, _wldResolver);
            throw;
        }

        if (feet is { } at)
        {
            Session.Player.Spawn(at, yaw);
        }
    }

    /// <summary>The session's own data (its layer choices are its own).</summary>
    public GameData Data { get; }

    public GameSession Session { get; }

    public CampaignControls Controls { get; } = CampaignControls.Create();

    /// <summary>One game tick from <paramref name="input"/>, as the game's loop runs it.</summary>
    public void Tick(in TickInput input) => Controls.Tick(Session, input);

    /// <summary>Ends the preview: the maps load as the editor reads them again.</summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            (GameData.LayerResolver, GameData.WldResolver) = (_resolver, _wldResolver);
        }
    }
}
