using System.Numerics;

namespace SkyNet.Edit;

/// <summary>Which edge of a node a pin sits on: inputs left, outputs right.</summary>
public enum PinSide { In, Out }

/// <summary>How a node's parameter row is edited: a number, one of fixed choices, an entity of the map, or text only.</summary>
public enum ParamKind { Number, Choice, Entity, Label }

/// <summary>How bad a graph issue is (its outline colour).</summary>
public enum IssueLevel { Warning, Error }

/// <summary>One value a choice or entity parameter can take, with its label.</summary>
public readonly record struct ParamChoice(long Value, string Label);

/// <summary>A node's parameter row. <paramref name="Text"/> is the shown value where the number alone says
/// little (an entity's label, a label row's text).</summary>
public sealed record CanvasParam(string Key, string Label, ParamKind Kind, long Value,
    IReadOnlyList<ParamChoice>? Choices = null, string? Text = null);

/// <summary>A node as the canvas draws it: its title bar (kind colour), pins and parameter rows, at
/// <paramref name="Position"/> (its top left corner, graph units).</summary>
public sealed record CanvasNode(int Id, string Title, Vector4 Colour, Vector2 Position,
    IReadOnlyList<string> Inputs, IReadOnlyList<string> Outputs, IReadOnlyList<CanvasParam> Params);

/// <summary>A pin: node, side and index among that side's pins.</summary>
public readonly record struct CanvasPin(int Node, PinSide Side, int Index);

/// <summary>A link from output pin <paramref name="FromPin"/> of one node to input pin <paramref name="ToPin"/> of another.</summary>
public readonly record struct CanvasLink(int FromNode, int FromPin, int ToNode, int ToPin)
{
    /// <summary>The output pin the link leaves.</summary>
    public CanvasPin From => new(FromNode, PinSide.Out, FromPin);

    /// <summary>The input pin the link enters.</summary>
    public CanvasPin To => new(ToNode, PinSide.In, ToPin);

    /// <summary>The link between two pins of opposite sides, in either order.</summary>
    public static CanvasLink Between(CanvasPin a, CanvasPin b) =>
        a.Side == PinSide.Out ? new(a.Node, a.Index, b.Node, b.Index) : new(b.Node, b.Index, a.Node, a.Index);
}

/// <summary>A validation finding on a node.</summary>
public sealed record CanvasIssue(int Node, IssueLevel Level, string Text);

/// <summary>A kind of node the "add node" menu offers, under <paramref name="Category"/>.</summary>
public sealed record CanvasNodeKind(string Id, string Label, string Category);

/// <summary>One chain's verification result; <paramref name="Nodes"/> are the nodes it walks, <paramref name="Problems"/>
/// what failed, by node.</summary>
public sealed record CanvasChain(string Name, bool Passed, string Text, IReadOnlyList<int> Nodes, IReadOnlyList<CanvasIssue>? Problems = null);

/// <summary>
/// The graph the node canvas (<see cref="GraphCanvas"/>) shows and edits. The canvas reads it every frame
/// and calls the edit methods once per finished gesture (a move on release, not every frame), so each call
/// can be one undo step. Edit methods return a line for the log.
/// </summary>
public interface IGraphView
{
    /// <summary>The nodes in draw order (later on top).</summary>
    IReadOnlyList<CanvasNode> Nodes { get; }

    IReadOnlyList<CanvasLink> Links { get; }

    IReadOnlyList<CanvasIssue> Issues { get; }

    /// <summary>What the "add node" menu offers.</summary>
    IReadOnlyList<CanvasNodeKind> Kinds { get; }

    string Connect(CanvasLink link);

    string Disconnect(CanvasLink link);

    /// <summary>Moves link <paramref name="old"/> to <paramref name="link"/>: one edit.</summary>
    string Relink(CanvasLink old, CanvasLink link);

    /// <summary>Moves <paramref name="nodes"/> by <paramref name="delta"/> graph units.</summary>
    string Move(IReadOnlyList<int> nodes, Vector2 delta);

    string Delete(IReadOnlyList<int> nodes);

    /// <summary>Adds a node of kind <paramref name="kind"/> with its top left corner at <paramref name="at"/>; its id, null when none was added.</summary>
    int? Add(string kind, Vector2 at);

    string SetParam(int node, string key, long value);

    /// <summary>The entities an entity parameter can point at (the picker's rows).</summary>
    IReadOnlyList<ParamChoice> Targets(int node, string key);

    /// <summary>Runs the chains through the game's verifier.</summary>
    IReadOnlyList<CanvasChain> Verify();
}
