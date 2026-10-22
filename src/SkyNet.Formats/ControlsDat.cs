using System.Buffers.Binary;
using System.Collections.Immutable;

namespace SkyNet.Formats;

/// <summary>
/// CONTROLS.DAT, the DOS key bindings of the CONTROL CONFIGURATION screen (17 bind boxes). It lives
/// in the game's install folder, not the data folder. Both shipped files (SkyNET and Future Shock)
/// are 154 bytes: 17 little-endian i32 scancodes at +0x00, then 86 bytes whose meaning is not
/// decoded (<see cref="Trailer"/>). Scancodes are PC set-1 make codes; 0x81 and 0x82 appear where a
/// mouse button is bound (UNCERTAIN). The Godot port does not read this file.
/// </summary>
public sealed class ControlsDat
{
    public const int Bindings = 17;
    public const int BindingsSize = Bindings * 4;
    public const int ShippedSize = 154;

    /// <summary>
    /// The action of each slot, in the Godot port's 17-action order. The shipped SkyNET defaults
    /// (W, S, Left, Right, A, D, 0x81, Right Shift, F, M, Left Shift, Space, Ctrl, Up, Down, Home, Tab)
    /// fit this order; UNCERTAIN until the DOS screen code is read.
    /// </summary>
    public static readonly ImmutableArray<string> Actions =
    [
        "forward", "back", "turn_left", "turn_right", "left", "right", "fire", "throw", "activate",
        "slide", "sprint", "up", "down", "look_up", "look_down", "center_view", "automap",
    ];

    public ImmutableArray<int> Scancodes { get; }
    /// <summary>The bytes after the 17 scancodes, kept as read.</summary>
    public ImmutableArray<byte> Trailer { get; }

    ControlsDat(ImmutableArray<int> scancodes, ImmutableArray<byte> trailer)
    {
        Scancodes = scancodes;
        Trailer = trailer;
    }

    /// <summary>Parse; null when shorter than the 68 bytes of scancodes.</summary>
    public static ControlsDat? Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < BindingsSize)
            return null;
        var codes = ImmutableArray.CreateBuilder<int>(Bindings);
        for (int i = 0; i < Bindings; i++)
            codes.Add(BinaryPrimitives.ReadInt32LittleEndian(bytes[(i * 4)..]));
        return new ControlsDat(codes.MoveToImmutable(), [.. bytes[BindingsSize..]]);
    }

    public int this[int slot] => Scancodes[slot];

    public int ScancodeOf(string action) => Actions.IndexOf(action) is var i and >= 0 ? Scancodes[i] : -1;
}
