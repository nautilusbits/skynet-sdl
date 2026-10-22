using System.Collections.Immutable;
using System.Drawing;
using System.Globalization;
using System.Numerics;
using SkyNet.Formats;
using SkyNet.Game.Net;
using static SkyNet.Game.Menu.MenuLayout;

namespace SkyNet.Game.Menu;

/// <summary>NETJOIN1.IMG, NEW GAME → MULTI-PLAYER (SKYNET.EXE v1.01 0x11e6a5): NEW GAME hosts
/// (<see cref="NetHostScreen"/>), JOIN GAME looks for games (<see cref="NetJoinScreen"/>), EXIT
/// goes back; NETJOIN2.CFA lights them.</summary>
public sealed class MultiplayerScreen : PanelScreen
{
    public MultiplayerScreen() : base(NetChoiceImage, NetChoiceSize, NetChoiceScale)
    {
        Origin = NetChoiceOrigin;
    }

    public override string Name => "multiplayer";

    public override void Opened()
    {
        if (Items.Count > 0)
        {
            return;
        }

        Add(new MenuItem("new", At(NetChoiceNew)) { Lit = Lit(NetChoiceLit, NetChoiceLitAt, 0), Activate = () => Menu.Push(new NetHostScreen()) });
        Add(new MenuItem("join", At(NetChoiceJoin)) { Lit = Lit(NetChoiceLit, NetChoiceLitAt, 1), Activate = () => Menu.Push(new NetJoinScreen()) });
        Add(new MenuItem("exit", At(NetChoiceExit)) { Lit = Lit(NetChoiceLit, NetChoiceLitAt, 2), Activate = Menu.Pop });
        Highlight = 0;
    }
}

/// <summary>
/// NETMENU1.IMG made live, the base of the host and join screens (the Godot port's menu.gd
/// _build_netmenu_screen): the baked boxes hold the values in the menus' small font, text and
/// number boxes take typing while highlighted (a number box's first digit replaces it; left /
/// right step it, so a gamepad sets every number), choice boxes cycle on Accept or left / right,
/// START / EXIT light with NETMENU2.CFA. The fields DOS's screen did not have go in the message
/// box along the bottom.
/// </summary>
public abstract class NetMenuScreen : PanelScreen
{
    public static readonly Vector4 Label = new(0.8f, 0.85f, 0.85f, 1f);
    public static readonly Vector4 Hint = new(0.6f, 0.66f, 0.66f, 1f);
    public static readonly Vector4 FieldFill = new(0.16f, 0.19f, 0.2f, 1f);
    public static readonly Vector4 Cursor = new(1f, 0.85f, 0.4f, 1f);

    /// <summary>A box that takes typing: its text, how long, digits only (with the range START
    /// clamps to), and whether the next digit replaces it.</summary>
    protected sealed class TextField
    {
        public string Value { get; set; } = "";
        public int MaxLength { get; init; }
        public bool Digits { get; init; }
        public int Min { get; init; }
        public int Max { get; init; }
        /// <summary>Letters as typed (an address) rather than upper case (a name).</summary>
        public bool KeepCase { get; init; }
        /// <summary>Shown as stars (a password).</summary>
        public bool Mask { get; init; }
        public bool Centred { get; init; }
        public bool Fresh { get; set; }

        public int Number(int fallback) =>
            int.TryParse(Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? Math.Clamp(n, Min, Max) : fallback;
    }

    private readonly Dictionary<string, TextField> _fields = new(StringComparer.Ordinal);
    private readonly List<(Rectangle Box, Func<string> Text, bool Centred)> _shown = [];
    private readonly List<(Rectangle Box, string Text)> _labels = [];
    private int _lastHighlight = -1;

    protected NetMenuScreen() : base(NetMenuImage, NetMenuSize, NetMenuScale)
    {
        Origin = Point.Empty;
    }

    public override bool TakesText => true;

    protected TextField Field(string id) => _fields[id];

    protected MenuItem AddText(string id, Rectangle box, string value, int maxLength, bool keepCase = false, Action? accept = null, bool mask = false)
    {
        _fields[id] = new TextField { Value = value, MaxLength = maxLength, KeepCase = keepCase, Mask = mask };
        return Add(new MenuItem(id, box) { Activate = accept });
    }

    protected MenuItem AddNumber(string id, Rectangle box, int value, int min, int max)
    {
        var f = new TextField
        {
            Value = value.ToString(CultureInfo.InvariantCulture), MaxLength = max.ToString(CultureInfo.InvariantCulture).Length,
            Digits = true, Min = min, Max = max, Centred = true,
        };
        _fields[id] = f;
        return Add(new MenuItem(id, box)
        {
            Adjust = d =>
            {
                f.Value = Math.Clamp(f.Number(min) + d, min, max).ToString(CultureInfo.InvariantCulture);
                f.Fresh = true;
            },
        });
    }

    /// <summary>A box that cycles: Accept or a click the next value, left / right either way.</summary>
    protected MenuItem AddChoice(string id, Rectangle box, Func<string> text, Action<int> step)
    {
        _shown.Add((box, text, true));
        return Add(new MenuItem(id, box) { Activate = () => step(1), Adjust = step });
    }

    /// <summary>A value shown in a box, not chosen there.</summary>
    protected void Show(Rectangle box, Func<string> text, bool centred = true) => _shown.Add((box, text, centred));

    /// <summary>A caption in the message box.</summary>
    protected void Caption(Rectangle box, string text) => _labels.Add((box, text));

    protected void AddStartExit(Action start)
    {
        Add(new MenuItem("start", NetStart) { Lit = new LitArt(NetMenuLit, 0, NetMenuLitAt), Activate = start });
        Add(new MenuItem("exit", NetExit) { Lit = new LitArt(NetMenuLit, 1, NetMenuLitAt), Activate = Back });
    }

    /// <summary>Typing and Backspace go to the highlighted text or number box. The space bar is
    /// also a menu Accept key: a space typed into a name is taken here.</summary>
    public override bool Intercept(in MenuInput input)
    {
        if (Highlight != _lastHighlight)
        {
            _lastHighlight = Highlight;
            if (Highlighted is { } h && _fields.TryGetValue(h.Id, out var nf))
            {
                nf.Fresh = nf.Digits;
            }
        }

        if (Highlighted is not { } item || !_fields.TryGetValue(item.Id, out var f))
        {
            return false;
        }

        var used = false;
        foreach (var raw in input.Typed)
        {
            if (f.Digits)
            {
                if (raw is < '0' or > '9')
                {
                    continue;
                }

                if (f.Fresh)
                {
                    f.Value = "";
                    f.Fresh = false;
                }

                if (f.Value.Length < f.MaxLength)
                {
                    f.Value = f.Value == "0" ? raw.ToString() : f.Value + raw;
                }

                continue;
            }

            var c = f.KeepCase ? raw : char.ToUpperInvariant(raw);
            if (char.IsAsciiLetterOrDigit(c) || c is ' ' or '.' or ':' or '-' or '_' or '\'' or '!' or '?' || (f.Mask && c > ' ' && c < 127))
            {
                if (f.Value.Length < f.MaxLength)
                {
                    f.Value += c;
                }

                used |= c == ' ';
            }
        }

        if (input.Erase)
        {
            if (f.Value.Length > 0)
            {
                f.Value = f.Value[..^1];
            }

            f.Fresh = false;
            used = true;
        }

        return used;
    }

    protected override void DrawContents(MenuPainter p)
    {
        foreach (var (box, text) in _labels)
        {
            p.Text(NetFont, text, box.X, box.Y + 2, Label);
        }

        foreach (var item in Items)
        {
            if (_fields.TryGetValue(item.Id, out var f))
            {
                if (NetBottomBox.Contains(item.Rect))
                {
                    p.Fill(item.Rect, FieldFill);
                }

                var editing = Highlighted == item && Menu.Current == this;
                Value(p, item.Rect, f.Mask ? new string('*', f.Value.Length) : f.Value, f.Centred, editing && !(f.Digits && f.Fresh) ? "_" : null);
            }
        }

        foreach (var (box, text, centred) in _shown)
        {
            if (NetBottomBox.Contains(box))
            {
                p.Fill(box, FieldFill);
            }

            Value(p, box, text(), centred, null);
        }
    }

    /// <summary>A value in its box: centred, or from the left (the end kept when it is too long).</summary>
    protected static void Value(MenuPainter p, Rectangle box, string text, bool centred, string? cursor, Vector4? colour = null)
    {
        var c = colour ?? MenuPainter.TextGreen;
        if (p.Font(NetFont) is not { } font)
        {
            return;
        }

        var y = box.Y + MathF.Floor((box.Height - font.LineHeight) / 2f);
        if (centred && cursor == null)
        {
            p.TextIn(NetFont, text, box, c);
            return;
        }

        while (text.Length > 0 && font.Measure(text + (cursor ?? "")) > box.Width - 3)
        {
            text = text[1..];
        }

        var w = p.Text(NetFont, text, box.X + 2, y, c);
        if (cursor != null)
        {
            p.Text(NetFont, cursor, box.X + 2 + w, y, Cursor);
        }
    }

    protected static string ClassName(bool terminator) => terminator ? "TERMINATOR" : "HUMAN";

    /// <summary>TERMINATOR VISION's row: what it reads.</summary>
    public static string VisionName(bool pointCloud) => pointCloud ? "POINT CLOUD" : "NORMAL";

    /// <summary>TERMINATOR VISION's choice (a TERMINATOR's machine vision drawn as a point cloud; off by
    /// default). Its row takes it only while a machine avatar is picked; a HUMAN turns it off.</summary>
    protected bool PointCloudVision { get; set; }

    /// <summary>The row follows the avatar's class: live for a machine, off and dim for a HUMAN.</summary>
    protected void VisionFor(MenuItem vision, bool machine)
    {
        vision.Enabled = machine;
        PointCloudVision &= machine;
    }

    /// <summary>The row's value, as shown.</summary>
    public string Vision => VisionName(PointCloudVision);

    /// <summary>TERMINATOR VISION's row: enabled while <paramref name="machine"/> is the avatar's class.</summary>
    protected MenuItem AddVision(Rectangle box, bool machine)
    {
        var item = Add(new MenuItem("vision", box) { Activate = () => PointCloudVision = !PointCloudVision, Adjust = _ => PointCloudVision = !PointCloudVision });
        item.Enabled = machine;
        return item;
    }

    /// <summary>The saved avatar when it is one of the saved class, else that class's default.</summary>
    protected static int SavedAvatar(NetMenuSettings net) =>
        DmAvatarTable.Resolve(net.Avatar, net.Terminator ? DmClass.Terminator : DmClass.Human);

    /// <summary>What <paramref name="cls"/> is under <paramref name="rules"/>, for the host screen's
    /// left box: DOS's 150-point bar for all; the HUMAN carries the motion detector, the TERMINATOR
    /// has the machine vision instead and, under BALANCED, 0.9 of the pace.</summary>
    public static string[] ClassLines(DmClass cls, DmClassRules rules)
    {
        var health = string.Create(CultureInfo.InvariantCulture, $"{DmProtocol.DosHealth:0} HEALTH");
        var pace = DmProtocol.SpeedFactor(cls, rules) is var f && f < 1f
            ? string.Create(CultureInfo.InvariantCulture, $"{f:0.0#} SPEED")
            : "FULL SPEED";
        return cls == DmClass.Terminator ? [health, "MACHINE VISION", "NO DETECTOR", pace] : [health, "MOTION DETECTOR", pace];
    }

    /// <summary>The master server of this run: <c>--master</c>, the settings' <c>net.master</c>, masters.txt
    /// (<see cref="MasterAddresses"/>, through the context), else the settings' (maybe empty).</summary>
    protected static string MasterAddress(MenuSystem menu) => menu.Context.MasterAddress() ?? menu.Settings.Net.Master;

    /// <summary>The name the others see: the network screens' last, else the new-game dialog's call
    /// sign or name, else PLAYER.</summary>
    protected static string DefaultName(MenuSettings s)
    {
        foreach (var n in new[] { s.Net.Name, s.CallSign, s.PlayerName })
        {
            if (DmProtocol.CleanText(n, DmProtocol.MaxNameLength) is { Length: > 0 } clean)
            {
                return clean.ToUpperInvariant();
            }
        }

        return "PLAYER";
    }

    protected string PlayerName() =>
        DmProtocol.CleanText(Field("name").Value, DmProtocol.MaxNameLength) is { Length: > 0 } n ? n : "PLAYER";
}

/// <summary>
/// NETMENU1.IMG for NEW GAME (hosting), as the Godot port's menu.gd lays it out: NAME, SKILL LEVEL
/// (the bots' skill, 1-3), AREA (the arenas of NETLEVEL.PRS, cycled as DOS does), MAX # PLAYERS,
/// TIME (minutes, 0 none), GOAL (POINTS / KILLS / LIVES / TIME, cycled as DOS's FUN_00128f77 does,
/// NETLEVEL.PRS's to start) and its limit (0 none), the arena's SCORING, the item
/// counts the host scatters and REPLENISH (from the arena, changeable); the big left box holds the
/// class (HUMAN / TERMINATOR, the Godot port's turning body) and what the boxes mean, the message box
/// BOTS, PORT, CHEATS, DEDICATED, PUBLIC, PASSWORD (the server keeps its key; never saved, LOCKED shows
/// Y while it is set) and CLASSES (DOS / BALANCED). START hosts as <c>--host</c> does and keeps the
/// choices but the password in the settings; EXIT goes back.
/// </summary>
public sealed class NetHostScreen : NetMenuScreen
{
    private static readonly string[] SkillNames = ["EASY", "NORMAL", "HARD"];
    private IReadOnlyList<NetLevel> _levels = [];
    private int _arena;
    private int _skill;
    private bool _replenish;
    /// <summary>The avatar of the left box (DOS's 1..13; 7 and 13 are the TERMINATOR).</summary>
    private int _avatar = 1;
    private MenuItem? _vision;
    private double _clock;
    private bool _cheats;
    private bool _dedicated;
    private bool _public;
    private DmClassRules _classes = DmClassRules.Balanced;
    private DmGoal _goal = DmGoal.Kills;

    public override string Name => "nethost";

    /// <summary>The arena chosen.</summary>
    public NetLevel? Arena => _levels.Count > 0 ? _levels[_arena] : null;

    /// <summary>The avatar chosen in the left box.</summary>
    public DmAvatarLook Avatar => DmAvatarTable.Get(_avatar);

    public override void Tick(float seconds)
    {
        base.Tick(seconds);
        _clock += seconds;
    }

    /// <summary>NETMENU1's LOCKED box: Y while the PASSWORD box holds a password.</summary>
    public string Locked => Field("password").Value.Length > 0 ? "Y" : "N";

    public override void Opened()
    {
        if (Items.Count > 0)
        {
            return;
        }

        var net = Menu.Settings.Net;
        _levels = Menu.Context.NetLevels();
        _arena = Math.Max(0, _levels.ToList().FindIndex(l => l.Map == net.Arena));
        _skill = Math.Clamp(net.BotSkill, 0, 2);
        _avatar = SavedAvatar(net);
        PointCloudVision = net.TerminatorVision;
        _cheats = net.Cheats;
        _dedicated = net.Dedicated && !Menu.InGame;
        _public = net.Public;
        _classes = net.Classes;
        _goal = net.Goal is { } g ? g : DmSettings.ParseGoal(Arena?.Goal);

        AddText("name", NetName, DefaultName(Menu.Settings), DmProtocol.MaxNameLength);
        AddChoice("skill", NetSkill, () => (_skill + 1).ToString(CultureInfo.InvariantCulture), d => _skill = (_skill + d + 3) % 3);
        Show(NetLocked, () => Locked);
        AddChoice("area", NetArea, () => Arena?.Name.ToUpperInvariant() ?? "NO ARENAS", d =>
        {
            if (_levels.Count > 0)
            {
                _arena = (_arena + d + _levels.Count) % _levels.Count;
                FromArena(null);
            }
        });
        AddNumber("maxplayers", NetMaxPlayers, net.MaxPlayers, 0, DmProtocol.MaxPeers);
        AddNumber("time", NetTime, net.TimeLimit, 0, NetMenuSettings.MaxTime);
        AddChoice("goal", NetGoal, () => _goal.ToString().ToUpperInvariant(), d => _goal = (DmGoal)(((int)_goal + d + 4) % 4));
        AddNumber("kills", NetKills, net.FragLimit, 0, NetMenuSettings.MaxFrags);
        Show(NetScoreKill, () => Score(l => l.ScoringKill));
        Show(NetScoreDeath, () => Score(l => l.ScoringDeath));
        Show(NetScoreHit, () => Score(l => l.ScoringHit));
        for (var row = 0; row < NetItemsLeft.Length; row++)
        {
            AddNumber(NetItemsLeft[row], NetItem(0, row), 0, 0, NetMenuSettings.MaxItems);
            AddNumber(NetItemsRight[row], NetItem(1, row), 0, 0, NetMenuSettings.MaxItems);
        }

        AddChoice("replenish", NetReplenish, () => _replenish ? "Y" : "N", _ => _replenish = !_replenish);
        // The left box cycles DOS's thirteen avatars (FUN_00128b3b: Accept or a click the next, left / right either way).
        Add(new MenuItem("class", ClassArea) { Activate = () => PickAvatar(1), Adjust = PickAvatar });
        // Its foot: TERMINATOR VISION, the machine's only.
        _vision = AddVision(VisionRow, Avatar.Machine);

        // The message box: BOTS, PORT and what the boxes mean.
        Caption(new Rectangle(14, 168, 24, 8), "BOTS");
        AddNumber("bots", new Rectangle(40, 167, 18, 9), net.Bots, 0, NetMenuSettings.MaxBots);
        Caption(new Rectangle(70, 168, 24, 8), "PORT");
        AddNumber("port", new Rectangle(96, 167, 36, 9), net.Port, 1024, 65535);
        Caption(new Rectangle(146, 168, 40, 8), "CHEATS");
        AddChoice("cheats", new Rectangle(188, 167, 13, 9), () => _cheats ? "Y" : "N", _ => _cheats = !_cheats);
        // The port's own: a server with no player of its own, run behind SERVER STATUS (title only).
        Caption(new Rectangle(210, 168, 52, 8), "DEDICATED");
        AddChoice("dedicated", new Rectangle(264, 167, 13, 9), () => _dedicated ? "Y" : "N", _ => _dedicated = !_dedicated).Enabled = !Menu.InGame;
        // PUBLIC: announced to the master server (the settings' net.master) for the INTERNET lists.
        Caption(new Rectangle(14, 182, 32, 8), "PUBLIC");
        AddChoice("public", new Rectangle(48, 181, 13, 9), () => _public ? "Y" : "N", _ => _public = !_public);
        // PASSWORD: joiners prove it (the server keeps only its key; not saved). LOCKED shows it is set.
        Caption(new Rectangle(66, 182, 44, 8), "PASSWORD");
        AddText("password", new Rectangle(116, 181, 60, 9), "", 32, keepCase: true, mask: true);
        // CLASSES: DOS (equal classes) or BALANCED (the machine at 0.9 of the pace).
        Caption(new Rectangle(184, 182, 40, 8), "CLASSES");
        AddChoice("classes", new Rectangle(229, 181, 52, 9), () => DmProtocol.RulesName(_classes),
            _ => _classes = _classes == DmClassRules.Dos ? DmClassRules.Balanced : DmClassRules.Dos);

        AddStartExit(StartHost);
        FromArena(net.Arena == Arena?.Map ? net : null);
        Highlight = Items.FindIndex(i => i.Id == "start");
    }

    private void PickAvatar(int step)
    {
        _avatar = DmAvatarTable.Step(_avatar, step);
        VisionFor(_vision!, Avatar.Machine);
    }

    private string Score(Func<NetLevel, int> pick) => Arena is { } l ? pick(l).ToString(CultureInfo.InvariantCulture) : "";

    /// <summary>The item counts and REPLENISH of the arena, or as last set for it.</summary>
    private void FromArena(NetMenuSettings? saved)
    {
        if (Arena is not { } level)
        {
            return;
        }

        _replenish = saved?.Replenish ?? level.Replenish;
        foreach (var key in NetLevelList.ItemKeys)
        {
            var n = saved?.Items != null && saved.Items.TryGetValue(key, out var v) ? v : level.Count(key);
            Field(key).Value = Math.Clamp(n, 0, NetMenuSettings.MaxItems).ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>What START hosts with.</summary>
    public DmRunOptions Options()
    {
        var items = NetLevelList.ItemKeys.ToImmutableDictionary(k => k, k => Field(k).Number(0), StringComparer.Ordinal);
        return new DmRunOptions
        {
            HostMap = Arena?.Map ?? 605,
            Port = Field("port").Number(NetMenuSettings.DefaultPort),
            Name = PlayerName(),
            Class = Avatar.Class,
            Avatar = _avatar,
            PointCloudVision = PointCloudVision,
            Classes = _classes,
            Password = Field("password").Value.Length > 0 ? Field("password").Value : null,
            Bots = Field("bots").Number(0),
            BotSkill = _skill,
            TimeLimitMinutes = Field("time").Number(0),
            Goal = _goal,
            GoalLimit = Field("kills").Number(0),
            Cheats = _cheats,
            MaxPlayers = Field("maxplayers").Number(NetMenuSettings.DefaultMaxPlayers),
            Replenish = _replenish,
            Items = items,
            Dedicated = _dedicated,
            Public = _public,
            Master = MasterAddress(Menu),
        };
    }

    private void StartHost()
    {
        if (Arena == null)
        {
            Menu.ShowToast("NO NETWORK ARENAS (NETLEVEL.PRS)");
            return;
        }

        var o = Options();
        var net = Menu.Settings.Net;
        net.Name = o.Name;
        net.Terminator = Avatar.Machine;
        net.TerminatorVision = PointCloudVision;
        net.Avatar = _avatar;
        net.Arena = o.HostMap ?? 0;
        net.MaxPlayers = o.MaxPlayers ?? NetMenuSettings.DefaultMaxPlayers;
        net.TimeLimit = o.TimeLimitMinutes;
        net.FragLimit = o.GoalLimit ?? 0;
        net.Goal = _goal;
        net.Cheats = _cheats;
        net.Classes = _classes;
        net.Bots = o.Bots;
        net.BotSkill = o.BotSkill;
        net.Port = o.Port;
        net.Replenish = _replenish;
        net.Items = new Dictionary<string, int>(o.Items!, StringComparer.Ordinal);
        net.Public = _public;
        if (!Menu.InGame)
        {
            net.Dedicated = _dedicated;
        }

        Menu.Changed();
        if (_public && !NetMenuSettings.MasterSet(o.Master))
        {
            Menu.ShowToast("PUBLIC: NO MASTER SERVER SET (SETTINGS OR MASTERS.TXT)");
            return;
        }

        if (_dedicated)
        {
            if (Menu.Actions.StartDedicated(o, out var why) is not { } server)
            {
                Menu.ShowToast((why ?? "THE SERVER DID NOT START").ToUpperInvariant());
                return;
            }

            Menu.Push(new ServerStatusScreen(server));
            return;
        }

        if (Menu.Actions.StartNetGame(o) is { } error)
        {
            Menu.ShowToast(error.ToUpperInvariant());
            return;
        }

        Menu.Close();
    }

    protected override void DrawContents(MenuPainter p)
    {
        base.DrawContents(p);
        // The left box: DOS's chosen avatar turning (FUN_00128b3b, the Godot port's menu.gd), its name,
        // and the class it plays under CLASSES.
        var box = NetLeftBox;
        var look = Avatar;
        p.TextIn(NetFont, "PLAY AS", new Rectangle(box.X, box.Y + 3, box.Width, 9), Label);
        p.TextIn(NetFont, $"< {look.Name} >", new Rectangle(box.X, box.Y + 11, box.Width, 9), MenuPainter.TextGreen);
        p.Avatars.Draw(look.Number, p.Frame.Canvas(AvatarArea), _clock);
        p.TextIn(NetFont, ClassName(look.Machine), new Rectangle(box.X, ClassLine, box.Width, 9), MenuPainter.TextGreen);
        var lines = ClassLines(look.Class, _classes);
        for (var i = 0; i < lines.Length; i++)
        {
            p.TextIn(NetFont, lines[i], new Rectangle(box.X, ClassLine + 9 + 7 * i, box.Width, 9), Hint);
        }

        // TERMINATOR VISION: the caption and the choice, dim while a HUMAN is picked.
        var row = VisionRow;
        var on = _vision?.Enabled == true;
        p.Text(NetFont, "VISION", row.X + 2, row.Y + 1, on ? Label : Hint);
        p.Text(NetFont, Vision, row.Right - 2, row.Y + 1, on ? MenuPainter.TextGreen : Hint, RetroEng.Render.TextAlign.Right);
    }

    /// <summary>Where the avatar turns: the left box under its name, above the class.</summary>
    public static readonly Rectangle AvatarArea = new(NetLeftBox.X + 2, NetLeftBox.Y + 20, NetLeftBox.Width - 4, 53);

    /// <summary>The class's name under the avatar; its lines follow, 7 apart.</summary>
    private static readonly int ClassLine = NetLeftBox.Y + 74;

    /// <summary>TERMINATOR VISION's row at the left box's foot, under the class's lines.</summary>
    public static readonly Rectangle VisionRow = new(NetLeftBox.X + 2, NetLeftBox.Y + 113, NetLeftBox.Width - 4, 9);

    /// <summary>The class pick: the left box above <see cref="VisionRow"/>.</summary>
    public static readonly Rectangle ClassArea = new(NetLeftBox.X, NetLeftBox.Y, NetLeftBox.Width, VisionRow.Y - NetLeftBox.Y);
}

/// <summary>One game of the join screen's list: a LAN discovery reply or a master server's entry
/// (its ping measured straight to the game port; <see cref="ViaMaster"/>: joined through the master's
/// NAT introduction).</summary>
public sealed record JoinRow(string Name, string Map, int Players, int Max, int Bots, bool Password, int? PingMs,
    System.Net.IPAddress Address, int Port, bool ViaMaster, bool Nat = false)
{
    public string Endpoint => $"{Address}:{Port}";
}

/// <summary>
/// NETMENU1.IMG for JOIN GAME. The big left box has two tabs: LAN (the games the LAN discovery
/// finds, refreshed as the replies come) and INTERNET (the master server's list of public games for
/// this game and build, with each game's ping measured straight to it, a lock on a passworded one).
/// Below the list NETSCAN1.IMG's SCANNING NETWORK / APPS. FOUND (DOS's scan, 0x127ab7); AREA, MAX #
/// PLAYERS and LOCKED (Y: a password) describe the highlighted game; the message box has the ADDRESS
/// box (host or ip[:port], for a game no list reaches), the class, and the PASSWORD box (sent as a
/// proof, never as text; used for any join). A listed game joins on Accept or a click (an INTERNET game through the master's
/// introduction, so one behind a NAT is reached too); START joins the address. The screen waits for
/// the arena ("CONNECTING"); a refused or failed join comes back here with the reason. More games than
/// rows scroll with up / down past the last row.
/// </summary>
public sealed class NetJoinScreen : NetMenuScreen
{
    public const int Rows = 4;
    public const int RowHeight = 16;
    public static readonly Rectangle LanTab = new(NetLeftBox.X + 2, NetLeftBox.Y + 2, 58, 10);
    public static readonly Rectangle InternetTab = new(NetLeftBox.X + 61, NetLeftBox.Y + 2, 58, 10);

    private RetroEng.Net.DiscoveryBrowser? _browser;
    private bool _browserTried;
    private RetroEng.Net.Master.MasterBrowser? _master;
    private bool _masterTried;
    private string _masterAddress = "";
    private double _clock;
    private List<JoinRow> _lan = [];
    private List<JoinRow> _internet = [];
    private int _avatar = 1;
    private bool _internetTab;
    private int _scroll;
    private readonly ListScroll _rowsScroll = new(Rows, RowHeight);
    private string _status = "";

    public override string Name => "netjoin";

    /// <summary>The games of the tab shown, as listed.</summary>
    public IReadOnlyList<JoinRow> Games => _internetTab ? _internet : _lan;

    /// <summary>The INTERNET tab is up.</summary>
    public bool OnInternet => _internetTab;

    /// <summary>The first game shown in the rows.</summary>
    public int Scroll => _scroll;

    public string Status => _status;

    /// <summary>NETMENU1's LOCKED box: Y when the game AREA describes has a password, N when it has
    /// none, empty with no game chosen.</summary>
    public string Locked => Chosen() is { } g ? g.Password ? "Y" : "N" : "";

    public override void Opened()
    {
        if (Items.Count > 0)
        {
            return;
        }

        var net = Menu.Settings.Net;
        _avatar = SavedAvatar(net);
        PointCloudVision = net.TerminatorVision;
        _masterAddress = MasterAddress(Menu);
        AddText("name", NetName, DefaultName(Menu.Settings), DmProtocol.MaxNameLength);
        Show(NetArea, () => Chosen() is { } g ? ArenaName(g) : "");
        Show(NetMaxPlayers, () => Chosen() is { Max: > 0 } g ? g.Max.ToString(CultureInfo.InvariantCulture) : "");
        Show(NetLocked, () => Locked);
        Add(new MenuItem("tablan", LanTab) { Activate = () => ShowTab(false) });
        Add(new MenuItem("tabinternet", InternetTab) { Activate = () => ShowTab(true) });
        for (var i = 0; i < Rows; i++)
        {
            var row = i;
            Add(new MenuItem($"game{i}", Row(i)) { Enabled = false, Activate = () => Join(_scroll + row) });
        }

        Caption(new Rectangle(14, 168, 40, 8), "ADDRESS");
        AddText("address", new Rectangle(56, 167, 150, 9), net.Address, 40, keepCase: true, accept: JoinAddress);
        Caption(new Rectangle(212, 168, 40, 8), "PLAY AS");
        // PLAY AS cycles DOS's thirteen avatars as the host screen's left box does (the class is the avatar's).
        MenuItem? vision = null;
        AddChoice("class", new Rectangle(248, 167, 68, 9), () => DmAvatarTable.Get(_avatar).Name, d =>
        {
            _avatar = DmAvatarTable.Step(_avatar, d);
            VisionFor(vision!, DmAvatarTable.Get(_avatar).Machine);
        });
        // Under it TERMINATOR VISION, the machine's only.
        Caption(new Rectangle(212, 182, 34, 8), "VISION");
        vision = AddVision(JoinVision, DmAvatarTable.Get(_avatar).Machine);
        // The password is kept for this screen only (never in the settings file).
        Caption(new Rectangle(14, 182, 44, 8), "PASSWORD");
        AddText("password", new Rectangle(60, 181, 52, 9), "", 32, keepCase: true, accept: JoinAddress, mask: true);
        AddStartExit(JoinAddress);
        Highlight = Items.FindIndex(i => i.Id == "address");
        if (!_browserTried)
        {
            _browserTried = true;
            _browser = Menu.Context.LanBrowser();
        }

        _status = _browser == null ? "LAN DISCOVERY UNAVAILABLE - TYPE AN ADDRESS" : "SCANNING NETWORK";
    }

    public override void Closed()
    {
        _browser?.Dispose();
        _browser = null;
        _master?.Dispose();
        _master = null;
    }

    /// <summary>LAN or INTERNET; Accept on the tab already shown asks the master again.</summary>
    public void ShowTab(bool internet)
    {
        if (internet && _internetTab)
        {
            _master?.Refresh();
            return;
        }

        _internetTab = internet;
        _scroll = 0;
        if (internet && !_masterTried)
        {
            _masterTried = true;
            if (NetMenuSettings.MasterSet(_masterAddress))
            {
                _master = Menu.Context.MasterBrowser(_masterAddress);
            }
            else
            {
                Menu.ShowToast("SET NET.MASTER IN THE SETTINGS OR A MASTERS.TXT BESIDE THE GAME");
            }
        }

        UpdateRows();
        UpdateStatus();
    }

    /// <summary>TERMINATOR VISION's box in the message box, under PLAY AS.</summary>
    public static readonly Rectangle JoinVision = new(248, 181, 68, 9);

    /// <summary>The status line's start and widest text: after the PASSWORD box, before VISION (x 212).</summary>
    private const int StatusX = 115, StatusWidth = 95;

    private static Rectangle Row(int i) => new(NetLeftBox.X + 2, NetLeftBox.Y + 13 + RowHeight * i, NetLeftBox.Width - 4, RowHeight);

    private string ArenaName(JoinRow g) =>
        NetLevelList.ParseMapName(g.Map) is { } map && Menu.Context.NetLevels().FirstOrDefault(l => l.Map == map) is { } l
            ? l.Name.ToUpperInvariant()
            : g.Map;

    /// <summary>The game AREA describes: the highlighted row, else the one the address names.</summary>
    private JoinRow? Chosen()
    {
        if (Highlighted is { } h && h.Id.StartsWith("game", StringComparison.Ordinal) && _scroll + int.Parse(h.Id[4..], CultureInfo.InvariantCulture) is var i && i < Games.Count)
        {
            return Games[i];
        }

        var address = Field("address").Value.Trim();
        return Games.FirstOrDefault(g => g.Endpoint == address || g.Address.ToString() == address);
    }

    public override void Tick(float seconds)
    {
        _clock += seconds;
        var changed = false;
        if (_browser != null && _browser.Poll(_clock))
        {
            _lan = [.. DmLan.Games(_browser).Select(g => new JoinRow(g.Name, g.Map, g.Players, g.Max, g.Bots, g.Password, null, g.Address, g.Port, false))];
            changed = true;
        }

        if (_master != null && _master.Poll(_clock))
        {
            _internet = [.. _master.Servers.Select(s => new JoinRow(s.Info.Name, s.Info.Map, s.Info.Players, s.Info.MaxPlayers, s.Info.Bots,
                s.Info.Password, s.PingMs, s.Address.Address, s.Address.Port, true, s.Nat))];
            changed = true;
        }

        if (changed)
        {
            UpdateRows();
        }

        if (!Menu.Context.NetConnecting())
        {
            UpdateStatus();
        }
    }

    private void UpdateRows()
    {
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, Games.Count - Rows));
        for (var i = 0; i < Rows; i++)
        {
            Items[Items.FindIndex(it => it.Id == $"game{i}")].Enabled = _scroll + i < Games.Count;
        }

        if (Highlighted is { Enabled: false })
        {
            Select(_internetTab ? "tabinternet" : "tablan");
        }
    }

    private void UpdateStatus()
    {
        if (!_internetTab)
        {
            _status = _browser == null ? "LAN DISCOVERY UNAVAILABLE - TYPE AN ADDRESS"
                : _lan.Count == 0 ? "SCANNING NETWORK" : $"{_lan.Count} GAME{(_lan.Count == 1 ? "" : "S")} FOUND";
            return;
        }

        _status = _master == null
            ? NetMenuSettings.MasterSet(_masterAddress) ? "THE MASTER SERVER'S ADDRESS IS NOT VALID" : "NO MASTER SERVER SET"
            : _master.State switch
            {
                RetroEng.Net.Master.MasterBrowserState.Resolving => "LOOKING UP THE MASTER SERVER",
                RetroEng.Net.Master.MasterBrowserState.Querying => "ASKING THE MASTER SERVER",
                RetroEng.Net.Master.MasterBrowserState.NoAnswer => "NO ANSWER FROM THE MASTER SERVER",
                RetroEng.Net.Master.MasterBrowserState.Unreachable => "THE MASTER SERVER DOES NOT RESOLVE",
                _ => $"{_internet.Count} GAME{(_internet.Count == 1 ? "" : "S")} ON THE MASTER SERVER",
            };
    }

    /// <summary>Up / down past the shown rows scroll the list; the rest is the text boxes'.</summary>
    public override bool Intercept(in MenuInput input)
    {
        // Wheel and dragged rows over the games.
        var list = Rectangle.Union(Row(0), Row(Rows - 1));
        if (_rowsScroll.Update(input, PointerAt, ref _scroll, Games.Count, list, Rectangle.Empty))
        {
            return true;
        }

        if (Highlighted is { } h && h.Id.StartsWith("game", StringComparison.Ordinal) && int.Parse(h.Id[4..], CultureInfo.InvariantCulture) is var row)
        {
            if (input.Down && row == Rows - 1 && _scroll + Rows < Games.Count)
            {
                _scroll++;
                return true;
            }

            if (input.Up && row == 0 && _scroll > 0)
            {
                _scroll--;
                return true;
            }
        }

        return base.Intercept(input);
    }

    private void Join(int index)
    {
        if (index < Games.Count)
        {
            var g = Games[index];
            Field("address").Value = g.Endpoint;
            if (g.Password && Field("password").Value.Length == 0)
            {
                Select("password");
                Menu.ShowToast("THIS GAME NEEDS A PASSWORD");
                return;
            }

            Connect(g.Address.ToString(), g.Port, g.ViaMaster);
        }
    }

    private void JoinAddress()
    {
        var a = Field("address").Value.Trim();
        if (a.Length == 0)
        {
            Menu.ShowToast("TYPE AN ADDRESS OR PICK A GAME");
            return;
        }

        var host = a;
        var port = DmProtocol.DefaultPort;
        var colon = a.LastIndexOf(':');
        if (colon > 0)
        {
            if (!int.TryParse(a[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535)
            {
                Menu.ShowToast("THE PORT MUST BE 1-65535");
                return;
            }

            host = a[..colon];
        }

        // A typed address of a game on the INTERNET list joins as the list does.
        var listed = _internet.FirstOrDefault(g => g.Port == port && g.Address.ToString() == host);
        Connect(host, port, listed != null);
    }

    private void Connect(string host, int port, bool viaMaster)
    {
        if (Menu.Context.NetConnecting())
        {
            return;
        }

        var net = Menu.Settings.Net;
        net.Name = PlayerName();
        net.Terminator = DmAvatarTable.Get(_avatar).Machine;
        net.TerminatorVision = PointCloudVision;
        net.Avatar = _avatar;
        net.Address = Field("address").Value.Trim();
        Menu.Changed();
        var password = Field("password").Value;
        var o = new DmRunOptions
        {
            JoinHost = host, Port = port, Name = net.Name, Class = DmAvatarTable.Get(_avatar).Class, Avatar = _avatar,
            PointCloudVision = PointCloudVision,
            Password = password.Length > 0 ? password : null,
            Master = viaMaster ? _masterAddress : null, Introduce = viaMaster,
        };
        if (Menu.Actions.StartNetGame(o) is { } error)
        {
            _status = error.ToUpperInvariant();
            Menu.ShowToast(_status);
            return;
        }

        _status = $"CONNECTING TO {host}:{port}{(viaMaster ? " VIA THE MASTER" : "")}".ToUpperInvariant();
    }

    /// <summary>Back while connecting gives the connection up.</summary>
    public override void Back()
    {
        if (Menu.Context.NetConnecting())
        {
            Menu.Actions.StopNetGame();
            UpdateStatus();
            return;
        }

        base.Back();
    }

    protected override void DrawContents(MenuPainter p)
    {
        base.DrawContents(p);
        // TERMINATOR VISION under PLAY AS, dim while a HUMAN is picked.
        p.Fill(JoinVision, FieldFill);
        Value(p, JoinVision, Vision, true, null, Find("vision")?.Enabled == true ? null : Hint);
        Tab(p, LanTab, "LAN", !_internetTab);
        Tab(p, InternetTab, "INTERNET", _internetTab);
        for (var i = 0; i < Rows && _scroll + i < Games.Count; i++)
        {
            var g = Games[_scroll + i];
            var r = Row(i);
            var right = g.ViaMaster ? (g.PingMs is { } ms ? $"{Math.Min(ms, 999)}MS" : "---") : "";
            var rightWidth = right.Length > 0 && p.Font(NetFont) is { } f ? (int)f.Measure(right) + 3 : 0;
            var lockWidth = g.Password ? 8 : 0;
            Value(p, new Rectangle(r.X, r.Y, r.Width - lockWidth, 8), g.Name.ToUpperInvariant(), false, null);
            if (g.Password)
            {
                Lock(p, r.Right - 7, r.Y + 1);
            }

            // Map, players and bots, shortened (no MAP., then B for BOTS) to leave room for the ping.
            var players = $"{g.Players}{(g.Max > 0 ? $"/{g.Max}" : "")}";
            var space = r.Width - rightWidth - 3;
            var who = $"{g.Map} {players}{(g.Bots > 0 ? $" +{g.Bots} BOTS" : "")}";
            var map = g.Map.StartsWith("MAP.", StringComparison.Ordinal) ? g.Map[4..] : g.Map;
            foreach (var shorter in new[] { $"{map} {players}{(g.Bots > 0 ? $" +{g.Bots} BOTS" : "")}", $"{map} {players}{(g.Bots > 0 ? $" +{g.Bots}B" : "")}" })
            {
                if (p.Font(NetFont) is not { } nf || nf.Measure(who) <= space)
                {
                    break;
                }

                who = shorter;
            }

            Value(p, new Rectangle(r.X, r.Y + 8, r.Width - rightWidth, 8), who, false, null, Hint);
            if (right.Length > 0)
            {
                p.Text(NetFont, right, r.Right - 2, r.Y + 8, g.PingMs != null ? MenuPainter.TextGreen : Hint, RetroEng.Render.TextAlign.Right);
            }
        }

        if (_scroll > 0)
        {
            p.Text(NetFont, "^", NetLeftBox.Right - 4, Row(0).Y, Cursor, RetroEng.Render.TextAlign.Right);
        }

        if (_scroll + Rows < Games.Count)
        {
            p.Text(NetFont, "V", NetLeftBox.Right - 4, Row(Rows - 1).Y + 8, Cursor, RetroEng.Render.TextAlign.Right);
        }

        // NETSCAN1 at the foot of the box: the bar fills over each probe, APPS. FOUND counts.
        var at = NetScanAt;
        if (p.Image(NetScanImage, MenuPalette, at.X, at.Y))
        {
            var bar = new Point(at.X + NetScanBarAt.X, at.Y + NetScanBarAt.Y);
            p.Cel(NetScanBar, 1, bar);
            if (_internetTab ? _master != null : _browser != null)
            {
                var period = _internetTab ? RetroEng.Net.Master.MasterBrowser.PingSeconds : RetroEng.Net.DiscoveryBrowser.ProbeIntervalSeconds;
                var phase = (float)(_clock % period / period);
                p.Cel(NetScanBar, 0, bar, (int)(99 * phase));
            }

            var found = NetScanFound;
            p.TextIn(NetFont, Games.Count.ToString(CultureInfo.InvariantCulture), new Rectangle(at.X + found.X, at.Y + found.Y, found.Width, found.Height), MenuPainter.TextGreen);
        }

        // The status between the PASSWORD box and TERMINATOR VISION's caption.
        var status = _status;
        if (p.Font(NetFont) is { } font)
        {
            while (status.Length > 1 && font.Measure(status) > StatusWidth)
            {
                status = status[..^1];
            }
        }

        p.Text(NetFont, status, StatusX, 184, Hint);
    }

    /// <summary>A tab: the shown one filled and lit.</summary>
    private static void Tab(MenuPainter p, Rectangle box, string text, bool on)
    {
        p.Fill(box, on ? FieldFill : new Vector4(0.08f, 0.1f, 0.1f, 1f));
        if (on)
        {
            p.Fill(new Rectangle(box.X, box.Bottom - 1, box.Width, 1), Cursor);
        }

        p.TextIn(NetFont, text, box, on ? MenuPainter.TextGreen : Hint);
    }

    /// <summary>A 5 × 7 padlock (a passworded game).</summary>
    private static void Lock(MenuPainter p, int x, int y)
    {
        var c = Cursor;
        p.Fill(new Rectangle(x + 1, y, 3, 1), c);
        p.Fill(new Rectangle(x, y + 1, 1, 2), c);
        p.Fill(new Rectangle(x + 4, y + 1, 1, 2), c);
        p.Fill(new Rectangle(x, y + 3, 5, 4), c);
        p.Fill(new Rectangle(x + 2, y + 4, 1, 2), FieldFill);
    }
}
