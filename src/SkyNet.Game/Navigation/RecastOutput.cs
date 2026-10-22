using System.Text;
using RetroEng.Diagnostics;

namespace SkyNet.Game.Navigation;

/// <summary>
/// DotRecast's diagnostics ("delaunayHull: Removing dangling face …", "rcBuildRegions: …") go to
/// Console.Out and Console.Error directly (RcContext.Warn is not virtual). While a bake runs, the
/// console's writers are wrapped: lines written on a thread inside <see cref="Quiet"/> go to the
/// log at debug level; every other thread's writes pass through untouched. The wrappers go when
/// the last bake ends (unless someone replaced them meanwhile).
/// <code>
///   bake thread (Quiet) ─► Console.WriteLine ─► Filter ─► Log.Debug("Nav", line)
///   any other thread    ─► Console.WriteLine ─► Filter ─► the original writer
/// </code>
/// </summary>
internal static class RecastOutput
{
    private const string Category = "Nav";

    [ThreadStatic]
    private static bool _quiet;

    [ThreadStatic]
    private static StringBuilder? _line;

    private static readonly Lock Gate = new();
    private static int _bakes;
    private static (TextWriter Out, TextWriter Error)? _original;
    /// <summary>The console's writers as set with ours (Console wraps each in a synchronised one).</summary>
    private static (TextWriter Out, TextWriter Error)? _installed;

    /// <summary>A bake begins: the console's writers wrapped until the matching <see cref="Release"/>.</summary>
    internal static void Hold()
    {
        lock (Gate)
        {
            if (_bakes++ > 0)
            {
                return;
            }

            var (o, e) = (Console.Out, Console.Error);
            Console.SetOut(new Filter(o));
            Console.SetError(new Filter(e));
            (_original, _installed) = ((o, e), (Console.Out, Console.Error));
        }
    }

    /// <summary>A bake ended: the last one puts the original writers back.</summary>
    internal static void Release()
    {
        lock (Gate)
        {
            if (--_bakes > 0 || _original is not { } original || _installed is not { } installed)
            {
                return;
            }

            // Set back only where ours is still in place.
            if (ReferenceEquals(Console.Out, installed.Out))
            {
                Console.SetOut(original.Out);
            }

            if (ReferenceEquals(Console.Error, installed.Error))
            {
                Console.SetError(original.Error);
            }

            (_original, _installed) = (null, null);
        }
    }

    /// <summary>This thread's console lines go to the log until disposed (a tile's build).</summary>
    internal static Scope Quiet()
    {
        _quiet = true;
        return default;
    }

    internal readonly struct Scope : IDisposable
    {
        public void Dispose()
        {
            EndLine();
            _quiet = false;
        }
    }

    /// <summary>A line written on a quiet thread, logged (not while logging: a console sink writes back).</summary>
    private static void Log(string text)
    {
        _quiet = false;
        try
        {
            RetroEng.Diagnostics.Log.Debug(Category, text);
        }
        finally
        {
            _quiet = true;
        }
    }

    private static void EndLine()
    {
        if (_line is not { Length: > 0 } line)
        {
            return;
        }

        var text = line.ToString();
        line.Clear();
        Log(text);
    }

    /// <summary>A console writer: a quiet thread's lines to the log, the rest to <paramref name="inner"/>.</summary>
    private sealed class Filter(TextWriter inner) : TextWriter
    {
        public override Encoding Encoding => inner.Encoding;

        public override void Write(char value)
        {
            if (!_quiet)
            {
                inner.Write(value);
                return;
            }

            if (value == '\n')
            {
                EndLine();
                return;
            }

            if (value != '\r')
            {
                (_line ??= new StringBuilder()).Append(value);
            }
        }

        public override void Write(string? value)
        {
            if (!_quiet)
            {
                inner.Write(value);
                return;
            }

            foreach (var c in value ?? "")
            {
                Write(c);
            }
        }

        public override void WriteLine(string? value)
        {
            if (!_quiet)
            {
                inner.WriteLine(value);
                return;
            }

            Write(value);
            EndLine();
        }

        public override void Flush() => inner.Flush();
    }
}
