using RetroEng.Platform;

namespace SkyNet.Game.Menu;

/// <summary>The key bindings as the CONTROLS screen needs them (the engine's <see cref="Input"/>
/// through <see cref="InputBindings"/>; a fake in tests).</summary>
public interface IBindings
{
    /// <summary>The id of an action by name, -1 when unknown.</summary>
    int Find(string action);

    string Name(int action);

    int Count { get; }

    IReadOnlyList<string> Bindings(int action);

    bool Bind(int action, string control);

    /// <summary>One control off an action, or (null) all of its keys and mouse buttons.</summary>
    void Unbind(int action, string? control = null);

    void ResetBindings();

    /// <summary>The names of an action's gamepad buttons (<see cref="GamepadMap.Name"/>: "A", "RT").</summary>
    IReadOnlyList<string> PadBindings(int action);

    bool BindPad(int action, string control);

    /// <summary>One gamepad button off an action, or (null) all of them.</summary>
    void UnbindPad(int action, string? control = null);

    void ResetPadBindings();
}

/// <summary><see cref="IBindings"/> over the engine's <see cref="Input"/>.</summary>
public sealed class InputBindings(Input input) : IBindings
{
    public int Find(string action) => input.Actions.Find(action);
    public string Name(int action) => input.Actions.Name(action);
    public int Count => input.Actions.Count;
    public IReadOnlyList<string> Bindings(int action) => input.Bindings(action);
    public bool Bind(int action, string control) => input.Bind(action, control);
    public void Unbind(int action, string? control = null) => input.Unbind(action, control);
    public void ResetBindings() => input.ResetBindings();
    public IReadOnlyList<string> PadBindings(int action) => input.PadBindings(action);
    public bool BindPad(int action, string control) => input.BindPad(action, control);
    public void UnbindPad(int action, string? control = null) => input.UnbindPad(action, control);
    public void ResetPadBindings() => input.ResetPadBindings();
}

/// <summary>Rebinding as the DOS screen does it: a control serves one box only.</summary>
public static class Rebind
{
    /// <summary>
    /// <paramref name="action"/> gets <paramref name="control"/> as its only key or mouse button;
    /// any other action of <paramref name="boxes"/> holding it lets go (DOS refuses a key on two
    /// boxes). Actions outside the boxes (menu keys, weapon keys) keep their bindings.
    /// </summary>
    public static bool Apply(IBindings bindings, int action, string control, IEnumerable<int> boxes)
    {
        if (action < 0 || control.Length == 0)
        {
            return false;
        }

        foreach (var other in boxes)
        {
            if (other != action && other >= 0 && bindings.Bindings(other).Contains(control, StringComparer.OrdinalIgnoreCase))
            {
                bindings.Unbind(other, control);
            }
        }

        var before = bindings.Bindings(action).ToArray();
        bindings.Unbind(action);
        if (bindings.Bind(action, control))
        {
            return true;
        }

        foreach (var c in before)
        {
            bindings.Bind(action, c);
        }

        return false;
    }

    /// <summary>Every action's controls by name, for the settings file.</summary>
    public static Dictionary<string, string[]> Snapshot(IBindings bindings)
    {
        var map = new Dictionary<string, string[]>(StringComparer.Ordinal);
        for (var i = 0; i < bindings.Count; i++)
        {
            map[bindings.Name(i)] = [.. bindings.Bindings(i)];
        }

        return map;
    }

    /// <summary>Puts saved bindings back: each named action gets exactly its saved controls
    /// (unknown actions and controls are skipped).</summary>
    public static void Restore(IBindings bindings, IReadOnlyDictionary<string, string[]> saved)
    {
        foreach (var (name, controls) in saved)
        {
            var action = bindings.Find(name);
            if (action < 0)
            {
                continue;
            }

            // Already its keys: left alone, as rebinding them reverses the engine's order
            // (S, Down → Down, S) and the CONTROLS box would show another key each start.
            if (bindings.Bindings(action).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(controls))
            {
                continue;
            }

            bindings.Unbind(action);
            foreach (var c in controls)
            {
                bindings.Bind(action, c);
            }
        }
    }
}

/// <summary>Gamepad rebinding (the GAMEPAD BUTTONS screen): a button serves one action only.</summary>
public static class PadRebind
{
    /// <summary>
    /// <paramref name="action"/> gets <paramref name="button"/> as its only gamepad button; every
    /// other action holding it lets go (A on JUMP, then on USE: JUMP has none). Start stays the menu's.
    /// </summary>
    public static bool Apply(IBindings bindings, int action, string button)
    {
        if (action < 0 || button.Length == 0)
        {
            return false;
        }

        var before = bindings.PadBindings(action).ToArray();
        bindings.UnbindPad(action);
        if (!bindings.BindPad(action, button))
        {
            foreach (var b in before)
            {
                bindings.BindPad(action, b);
            }

            return false;
        }

        for (var other = 0; other < bindings.Count; other++)
        {
            if (other != action && bindings.PadBindings(other).Contains(button, StringComparer.Ordinal))
            {
                bindings.UnbindPad(other, button);
            }
        }

        return true;
    }

    /// <summary>Every action's gamepad buttons by name, for the settings file.</summary>
    public static Dictionary<string, string[]> Snapshot(IBindings bindings)
    {
        var map = new Dictionary<string, string[]>(StringComparer.Ordinal);
        for (var i = 0; i < bindings.Count; i++)
        {
            map[bindings.Name(i)] = [.. bindings.PadBindings(i)];
        }

        return map;
    }

    /// <summary>Puts saved gamepad buttons back: each named action gets exactly its saved buttons
    /// (unknown actions and buttons are skipped; Start stays the menu's).</summary>
    public static void Restore(IBindings bindings, IReadOnlyDictionary<string, string[]> saved)
    {
        foreach (var (name, buttons) in saved)
        {
            var action = bindings.Find(name);
            if (action < 0)
            {
                continue;
            }

            bindings.UnbindPad(action);
            foreach (var b in buttons)
            {
                bindings.BindPad(action, b);
            }
        }
    }
}
