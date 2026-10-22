using System.Globalization;
using System.Text;
using RetroEng.Commands;
using RetroEng.Platform;
using SkyNet.Game.Menu;

namespace SkyNet.Game.DevTools;

/// <summary>
/// Quake's bind: a key pressed in play (menus and console shut) runs a console line, through the
/// registry as if typed, so a cheat stays gated in a network game. <c>bind KEY +ACTION</c> puts the
/// key on a game action instead (the CONTROLS screen's bindings, held as the action's other keys).
/// A key runs one line: binding a line takes the key off every action.
/// <code>
///   bind f noclip          F toggles noclip
///   bind f9 "screenshot a" F9 saves a shot
///   bind q "+quick save"   Q is one more key of Quick save
/// </code>
/// Key names are SDL's ("F9", "Left Shift", "Keypad 5") and the mouse's ("Left mouse"), any case.
/// </summary>
public sealed class KeyBinds(IBindings actions)
{
    /// <summary>A bind target starting with it names a game action.</summary>
    public const char ActionPrefix = '+';

    /// <summary>Keys a console line may not take: the menu's and the console's own.</summary>
    private static readonly string[] Reserved = ["Escape", "`"];

    private static readonly Lock ProbeLock = new();
    private static Input? _probe;
    private static int _probeAction;

    private readonly SortedDictionary<string, string> _lines = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A bind changed (the app saves the settings).</summary>
    public event Action? Changed;

    /// <summary>Key name → the console line it runs.</summary>
    public IReadOnlyDictionary<string, string> Lines => _lines;

    /// <summary>The line <paramref name="control"/> (a pressed key's name) runs, or null.</summary>
    public string? LineOf(string control) =>
        control.Length > 0 && _lines.TryGetValue(control, out var line) ? line : null;

    /// <summary>Runs the line of <paramref name="control"/> in <paramref name="registry"/>: its output,
    /// or null when the key runs nothing.</summary>
    public IReadOnlyList<string>? Run(string control, CommandRegistry registry) =>
        LineOf(control) is { } line ? registry.Run(line) : null;

    /// <summary>The settings file's binds back (unknown keys and empty lines skipped).</summary>
    public void Restore(IReadOnlyDictionary<string, string> saved)
    {
        _lines.Clear();
        foreach (var (key, line) in saved)
        {
            if (KeyName(key) is { } name && line.Trim().Length > 0)
            {
                _lines[name] = line.Trim();
            }
        }
    }

    /// <summary>The engine's name of a key or mouse button typed in any case ("f9" → "F9",
    /// "left mouse" → "Left mouse"), or null when it names none.</summary>
    public static string? KeyName(string name)
    {
        name = name.Trim();
        if (name.Length == 0)
        {
            return null;
        }

        // The engine knows its names: a probe action takes the key and reports it as the engine spells it.
        lock (ProbeLock)
        {
            if (_probe == null)
            {
                var set = new ActionSet();
                _probeAction = set.Add("probe");
                _probe = new Input(set);
            }

            // Keys are matched in any case; the mouse's names only as spelled ("Left mouse").
            var mouse = char.ToUpperInvariant(name[0]) + name[1..].ToLowerInvariant();
            foreach (var candidate in new[] { name, mouse })
            {
                _probe.Unbind(_probeAction);
                if (_probe.Bind(_probeAction, candidate) && _probe.Bindings(_probeAction) is [var spelled])
                {
                    return spelled;
                }
            }

            return null;
        }
    }

    /// <summary>The console's bind, unbind, unbindall and bindlist on <paramref name="binds"/>.</summary>
    public static void Register(CommandRegistry registry, KeyBinds binds)
    {
        registry.Add(new Command("bind", "bind <key> [command... | +action]", args =>
        {
            if (KeyName(args.Word(0)) is not { } key)
            {
                return $"Unknown key '{args.Word(0)}'";
            }

            if (args.Count == 1)
            {
                return binds.Show(key);
            }

            var target = args.Rest(1).Trim();
            if (target.StartsWith(ActionPrefix))
            {
                return binds.BindAction(key, target[1..].Trim());
            }

            var verb = target.Split([' ', ';'], 2)[0];
            return registry.TryGet(verb, out _) ? binds.BindLine(key, target) : $"Unknown command '{verb}'";
        })
        {
            Help = "a key runs a console line in play (bind f noclip; bind f9 \"screenshot a.bmp\"), or +action adds it to a game action (bind q \"+quick save\"); no line: what the key does",
            Arguments = i => i == 0 ? binds.BoundKeys() : i == 1 ? CommandNames(registry).Concat(binds.ActionTargets()) : [],
        });
        registry.Add(new Command("unbind", "unbind <key>", args =>
        {
            return KeyName(args.Word(0)) is { } key ? binds.Unbind(key) : $"Unknown key '{args.Word(0)}'";
        })
        { Help = "the key runs nothing and leaves every game action", Arguments = i => i == 0 ? binds.BoundKeys() : [] });
        registry.Add("unbindall", "unbindall", _ => binds.UnbindAll(), "drops every key's console line (the game actions keep their keys)");
        registry.Add("bindlist", "bindlist", _ => binds.List(), "every key's console line and game actions");
    }

    private static IEnumerable<string> CommandNames(CommandRegistry registry) =>
        registry.Commands.SelectMany(c => c.Names);

    private IEnumerable<string> ActionTargets() =>
        Enumerable.Range(0, actions.Count).Select(a => ActionPrefix + actions.Name(a)).Where(n => !n.Contains(' ', StringComparison.Ordinal));

    /// <summary>Keys with a line or a game action: what unbind completes.</summary>
    private IEnumerable<string> BoundKeys() =>
        _lines.Keys.Concat(Enumerable.Range(0, actions.Count).SelectMany(actions.Bindings)).Distinct(StringComparer.OrdinalIgnoreCase);

    private string BindLine(string key, string line)
    {
        if (Reserved.Contains(key, StringComparer.OrdinalIgnoreCase))
        {
            return $"{key} stays the menu's and the console's";
        }

        // One thing a key: off every action that held it.
        var off = OffActions(key);
        _lines[key] = line;
        Changed?.Invoke();
        return off.Count == 0 ? $"{key}: {line}" : $"{key}: {line} (taken off {string.Join(", ", off.Select(a => ActionPrefix + a))})";
    }

    private string BindAction(string key, string name)
    {
        var action = Enumerable.Range(0, actions.Count).FirstOrDefault(a => actions.Name(a).Equals(name, StringComparison.OrdinalIgnoreCase), -1);
        if (action < 0)
        {
            return $"No action '{name}' (bindlist shows them)";
        }

        _lines.Remove(key);
        if (!actions.Bindings(action).Contains(key, StringComparer.OrdinalIgnoreCase) && !actions.Bind(action, key))
        {
            return $"{key} cannot be bound";
        }

        Changed?.Invoke();
        return Show(key);
    }

    private string Unbind(string key)
    {
        var had = _lines.Remove(key);
        var off = OffActions(key);
        if (!had && off.Count == 0)
        {
            return $"{key} is not bound";
        }

        Changed?.Invoke();
        return $"{key} unbound";
    }

    private string UnbindAll()
    {
        var count = _lines.Count;
        _lines.Clear();
        if (count > 0)
        {
            Changed?.Invoke();
        }

        return string.Create(CultureInfo.InvariantCulture, $"{count} key bind(s) dropped");
    }

    /// <summary>Takes <paramref name="key"/> off every game action: their names.</summary>
    private List<string> OffActions(string key)
    {
        var off = new List<string>();
        for (var a = 0; a < actions.Count; a++)
        {
            foreach (var held in actions.Bindings(a).Where(c => c.Equals(key, StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                actions.Unbind(a, held);
                off.Add(actions.Name(a));
            }
        }

        return off;
    }

    /// <summary>"F: noclip", "W: +Forward", or that nothing is bound.</summary>
    private string Show(string key)
    {
        var targets = Targets(key);
        return targets.Count == 0 ? $"{key} is not bound" : $"{key}: {string.Join(", ", targets)}";
    }

    private List<string> Targets(string key)
    {
        var targets = new List<string>();
        if (LineOf(key) is { } line)
        {
            targets.Add(line);
        }

        for (var a = 0; a < actions.Count; a++)
        {
            if (actions.Bindings(a).Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                targets.Add(ActionPrefix + actions.Name(a));
            }
        }

        return targets;
    }

    private string List()
    {
        var text = new StringBuilder();
        foreach (var key in BoundKeys().Order(StringComparer.OrdinalIgnoreCase))
        {
            text.Append(key).Append(": ").AppendJoin(", ", Targets(key)).Append('\n');
        }

        return text.Length == 0 ? "No key is bound" : text.ToString().TrimEnd('\n');
    }
}
