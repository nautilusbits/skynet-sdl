using System.Globalization;

namespace SkyNet.Edit;

/// <summary>--script: a timeline of editor steps for scripted takes (docs/video.md). The steps of a frame run
/// together up to a <c>wait N</c>, which holds the rest N frames; a step is an editor command or one of
/// EditorApp's script verbs (cam, tool, preview, hold, ...).</summary>
internal sealed class EditorScript
{
    private readonly Queue<string> _steps;
    private int _wait;

    private EditorScript(IEnumerable<string> steps) => _steps = new Queue<string>(steps);

    /// <summary>Steps not run yet (waits included).</summary>
    public int Left => _steps.Count;

    /// <summary>The steps of <paramref name="text"/>: a file's lines when it names a file (one step a line, so a
    /// step may hold ';'), else ';' between them; blank ones and '#' comments left out.</summary>
    public static EditorScript Parse(string text)
    {
        var lines = File.Exists(text) ? File.ReadAllLines(text) : text.Split(';');
        return new EditorScript(lines.Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')));
    }

    /// <summary>This frame's steps: none while a wait holds, else the steps up to the next wait (taken).</summary>
    public List<string> Due()
    {
        var due = new List<string>();
        if (_wait > 0)
        {
            _wait--;
            return due;
        }

        while (_steps.TryDequeue(out var step))
        {
            if (step.StartsWith("wait ", StringComparison.Ordinal)
                && int.TryParse(step.AsSpan(5), NumberStyles.Integer, CultureInfo.InvariantCulture, out var frames))
            {
                _wait = Math.Max(frames - 1, 0);
                break;
            }

            due.Add(step);
        }

        return due;
    }
}
