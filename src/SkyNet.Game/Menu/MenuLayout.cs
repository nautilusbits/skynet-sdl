using System.Collections.Frozen;
using System.Drawing;

namespace SkyNet.Game.Menu;

/// <summary>
/// The menu art and where its baked buttons are, in DOS pixels of each image. A button's rectangle is
/// the bounding box of DOS's lit frame of it (MAIN1BTN.CFA, NETGAME2.CFA, OPTBTN.CFA, DETBTN.CFA,
/// CONBTN.CFA, QUITBTN.CFA, RESTBTN.CFA, LOADBTN.IMG), placed where DOS puts that art over the panel
/// (the .CFA's screen position less the panel .IMG's); the slots and the CONTROLS key boxes, which
/// have no lit art, are the Godot port's measurements (menu.gd). Panels are centred on the screen
/// and scaled as the Godot port scales them (<see cref="MenuFrame.Panel"/>).
/// </summary>
public static class MenuLayout
{
    // Palettes (MDMDIMGS.BSA).
    public const string MenuPalette = "MENU.COL";
    public const string TitlePalette = "SKYNTRMP.COL";
    public const string BriefPalette = "BRIEF.COL";
    public const string ScenePalette = "BRIEF2.COL";

    // The title screen and its bar.
    public const string TitleImage = "SKYNTRMP.IMG";
    public const string TitleBar = "MAIN1.IMG";
    public const string GameBar = "MAIN2.IMG";
    public const int BarHeight = 17;

    /// <summary>MAIN1.IMG: NEW GAME / LOAD / SAVE / OPTIONS / QUIT; MAIN2.IMG has RETURN in the first place.
    /// MAIN1BTN.CFA / MAIN2BTN.CFA frames 0-4 light them, at (0, 0).</summary>
    public static readonly Rectangle[] BarItems =
    [
        new(0, 0, 92, BarHeight), new(92, 0, 52, BarHeight), new(144, 0, 53, BarHeight), new(197, 0, 78, BarHeight), new(275, 0, 45, BarHeight),
    ];

    public const string TitleBarLit = "MAIN1BTN.CFA";
    public const string GameBarLit = "MAIN2BTN.CFA";

    // NETGAME1.IMG, the NEW GAME dialog (198 × 132).
    public const string NewGameImage = "NETGAME1.IMG";
    public static readonly Size NewGameSize = new(198, 132);
    public static readonly Rectangle NewGameMulti = new(15, 10, 169, 19);
    public static readonly Rectangle NewGameOne = new(15, 35, 169, 19);
    public static readonly Rectangle NewGameFutureShock = new(15, 85, 169, 19);
    public static readonly Rectangle NewGameExit = new(154, 114, 44, 18);
    /// <summary>NETGAME2.CFA: MULTI-PLAYER, ONE PLAYER, TUTORIAL, FUTURE SHOCK, EXIT lit; DOS (76, 44) over the panel's (61, 34).</summary>
    public const string NewGameLit = "NETGAME2.CFA";
    public static readonly Point NewGameLitAt = new(15, 10);
    /// <summary>POPUPFS1.IMG: "Future Shock data missing".</summary>
    public const string FutureShockMissing = "POPUPFS1.IMG";

    // NETJOIN1.IMG (198 × 80), MULTI-PLAYER: NEW GAME / JOIN GAME / EXIT (SKYNET.EXE v1.01
    // 0x11e6a5). Its header puts it at (50, 38); NETJOIN2.CFA lights the three at DOS (65, 47).
    public const string NetChoiceImage = "NETJOIN1.IMG";
    public static readonly Size NetChoiceSize = new(198, 80);
    public static readonly Point NetChoiceOrigin = new(50, 38);
    public static readonly Rectangle NetChoiceNew = new(15, 9, 169, 20);
    public static readonly Rectangle NetChoiceJoin = new(15, 34, 169, 20);
    public static readonly Rectangle NetChoiceExit = new(154, 61, 44, 19);
    public const string NetChoiceLit = "NETJOIN2.CFA";
    public static readonly Point NetChoiceLitAt = new(15, 9);
    public const float NetChoiceScale = 4f;

    // NETMENU1.IMG (320 × 200), the network game screen (0x127bc2): its baked boxes as the Godot
    // port's menu.gd measured them; NETMENU2.CFA lights START / EXIT at DOS (213, 144).
    public const string NetMenuImage = "NETMENU1.IMG";
    public static readonly Size NetMenuSize = new(320, 200);
    public const float NetMenuScale = 3.2f;
    public static readonly Rectangle NetName = new(33, 9, 96, 9);
    public static readonly Rectangle NetSkill = new(63, 24, 14, 7);
    /// <summary>LOCKED (DOS's box beside SKILL LEVEL): Y for a game with a password.</summary>
    public static readonly Rectangle NetLocked = new(114, 24, 13, 7);
    public static readonly Rectangle NetArea = new(168, 9, 145, 9);
    public static readonly Rectangle NetMaxPlayers = new(209, 24, 13, 7);
    public static readonly Rectangle NetTime = new(249, 24, 63, 7);
    public static readonly Rectangle NetGoal = new(166, 38, 65, 9);
    public static readonly Rectangle NetKills = new(247, 38, 65, 9);
    public static readonly Rectangle NetScoreKill = new(212, 51, 25, 8);
    public static readonly Rectangle NetScoreDeath = new(277, 51, 25, 8);
    public static readonly Rectangle NetScoreHit = new(212, 63, 25, 8);
    public static readonly Rectangle NetReplenish = new(181, 144, 13, 8);
    /// <summary>The big box on the left (the Godot port's turning body; the join screen's server list).</summary>
    public static readonly Rectangle NetLeftBox = new(8, 35, 121, 124);
    /// <summary>The message box along the bottom (the fields DOS's screen did not have).</summary>
    public static readonly Rectangle NetBottomBox = new(8, 165, 305, 30);
    /// <summary>The item boxes: the left column JEEPS … HEALTH, the right SLUGTHROWERS … ROCKETS, 11 apart from y 74.</summary>
    public static readonly string[] NetItemsLeft = ["jeeps", "hks", "bullets", "energy", "armor", "health"];
    public static readonly string[] NetItemsRight = ["slugthrowers", "lasers", "plasmas", "launchers", "grenades", "rockets"];
    public static Rectangle NetItem(int column, int row) => new(column == 0 ? 181 : 282, 74 + 11 * row, 13, 8);
    public static readonly Rectangle NetStart = new(213, 144, 66, 17);
    public static readonly Rectangle NetExit = new(279, 144, 41, 17);
    public const string NetMenuLit = "NETMENU2.CFA";
    public static readonly Point NetMenuLitAt = new(213, 144);
    /// <summary>The fields' font (the menus' small one, as the LOAD slots).</summary>
    public const string NetFont = "FONT0004.FNT";

    // NETSCAN1.IMG (119 × 43), SCANNING NETWORK / APPS. FOUND (0x127ab7), here at the foot of the
    // left box; NETSCAN2.CFA's bar at (10, 23) of it.
    public const string NetScanImage = "NETSCAN1.IMG";
    public static readonly Size NetScanSize = new(119, 43);
    public static readonly Point NetScanAt = new(9, 115);
    public static readonly Point NetScanBarAt = new(10, 23);
    public static readonly Rectangle NetScanFound = new(79, 30, 16, 9);
    public const string NetScanBar = "NETSCAN2.CFA";

    // NAME.IMG (198 × 57), the new-game dialog: NAME and CALL SIGN (SKYNET.EXE v1.01 0x135345).
    public const string NameImage = "NAME.IMG";
    public static readonly Size NameSize = new(198, 57);
    /// <summary>Drawn centred on (160, 100) (0x13af00, flags 0x12): a row lower than <see cref="Centred"/> puts it.</summary>
    public static readonly Point NameOrigin = new(61, 72);
    /// <summary>Where each field's text starts over the image (0x53929); the boxes round them are the art's.</summary>
    public static readonly Point[] NameTextAt = [new(70, 21), new(70, 41)];
    public static readonly Rectangle NameField = new(69, 19, 113, 10);
    public static readonly Rectangle CallSignField = new(69, 39, 113, 10);
    public const string NameFont = "FONT0004.FNT";
    /// <summary>The edit's cursor (0x53939), after the text of the field being edited; it does not blink.</summary>
    public const string NameCursor = "_";
    /// <summary>The text's MENU.COL index (0x12f7bb with ecx 0x104B5: font 4, colour 0xB5).</summary>
    public const int NameTextIndex = 0xB5;
    /// <summary>Scaled as the NEW GAME dialog it opens from.</summary>
    public const float NameScale = NewGameScale;

    // LOAD.IMG / SAVE.IMG (232 × 164): ten slot bars and EXIT.
    public const string LoadImage = "LOAD.IMG";
    public const string SaveImage = "SAVE.IMG";
    public static readonly Size SlotsSize = new(232, 164);
    public const int Slots = 10;
    public static Rectangle Slot(int i) => new(12, 17 + 12 * i, 208, 12);
    public static readonly Rectangle SlotsExit = new(189, 147, 43, 17);
    /// <summary>LOADBTN.IMG, EXIT lit: DOS (234, 163) over the panel's (45, 16).</summary>
    public const string SlotsExitLit = "LOADBTN.IMG";

    // OPTIONS.IMG (198 × 123).
    public const string OptionsImage = "OPTIONS.IMG";
    public static readonly Size OptionsSize = new(198, 123);
    public static readonly Rectangle OptionsControls = new(0, 105, 93, 18);
    public static readonly Rectangle OptionsDetail = new(93, 105, 62, 18);
    public static readonly Rectangle OptionsExit = new(155, 106, 43, 17);
    public static readonly Rectangle OptionsSound = new(59, 25, 112, 8);
    public static readonly Rectangle OptionsMusic = new(59, 42, 112, 8);
    public static readonly Rectangle OptionsStereo = new(9, 68, 54, 28);
    public static readonly Rectangle[] OptionsDifficulty = [new(80, 74, 35, 18), new(115, 74, 35, 18), new(150, 74, 36, 18)];
    /// <summary>OPTBTN.CFA, drawn at (0, 25) of the panel: 0 / 1 the SOUND / MUSIC bars' red fill,
    /// 2 REVERSE STEREO, 3-5 and 6-8 LOW / MED / HIGH (two lit sets), 9 CONTROLS, 10 DETAIL, 11 EXIT.</summary>
    public const string OptionsLit = "OPTBTN.CFA";
    public static readonly Point OptionsLitAt = new(0, 25);

    // DETAIL.IMG (198 × 102), RENDER DETAIL: LOW / MED / HIGH, RESOLUTION 320 / 640 (GAMEPLAY in the port), EXIT
    // (measured off the art for this port).
    public const string DetailImage = "DETAIL.IMG";
    public static readonly Size DetailSize = new(198, 102);
    public static readonly Rectangle[] DetailLevels = [new(46, 27, 35, 18), new(81, 27, 35, 18), new(116, 27, 36, 18)];
    /// <summary>DOS's RESOLUTION caption and its two buttons, covered by the port's GAMEPLAY.</summary>
    internal static readonly Rectangle DetailResolutionArea = new(4, 48, 190, 34);
    /// <summary>GAMEPLAY (<see cref="GameplayScreen"/>), across both RESOLUTION buttons.</summary>
    internal static readonly Rectangle DetailGameplay = new(32, 61, 132, 19);
    public static readonly Rectangle DetailExit = new(155, 85, 43, 17);
    /// <summary>DETBTN.CFA, DOS (82, 65) over the panel's (50, 38): 0-2 and 3-5 LOW / MED / HIGH,
    /// 6-7 and 8-9 320 × 200 / 640 × 480 (two lit sets each), 10 EXIT.</summary>
    public const string DetailLit = "DETBTN.CFA";
    public static readonly Point DetailLitAt = new(32, 27);

    // CONTROLS.IMG (269 × 166), CONTROL CONFIGURATION: the 17 boxes of CONTROLS.DAT.
    public const string ControlsImage = "CONTROLS.IMG";
    public static readonly Size ControlsSize = new(269, 166);
    public static readonly (string Slot, Rectangle Box)[] ControlBoxes =
    [
        ("forward", new(77, 25, 48, 8)), ("back", new(77, 37, 48, 8)), ("turn_left", new(77, 49, 48, 8)),
        ("turn_right", new(77, 61, 48, 8)), ("left", new(77, 73, 48, 8)), ("right", new(77, 85, 48, 8)),
        ("fire", new(77, 102, 48, 8)), ("throw", new(77, 115, 48, 8)), ("activate", new(77, 128, 48, 8)),
        ("slide", new(201, 25, 48, 8)), ("sprint", new(201, 37, 48, 8)), ("up", new(201, 49, 48, 8)),
        ("down", new(201, 61, 48, 8)), ("look_up", new(201, 82, 48, 8)), ("look_down", new(201, 95, 48, 8)),
        ("center_view", new(201, 108, 48, 8)), ("automap", new(201, 128, 48, 8)),
    ];
    public static readonly Rectangle ControlsJoystick = new(0, 149, 85, 17);
    public static readonly Rectangle ControlsMouse = new(85, 149, 63, 17);
    public static readonly Rectangle ControlsDefault = new(148, 149, 77, 17);
    public static readonly Rectangle ControlsExit = new(226, 149, 43, 17);
    /// <summary>CONBTN.CFA, DOS (30, 166) over the panel's (30, 17): JOYSTICK, MOUSE, DEFAULT, EXIT lit.</summary>
    public const string ControlsLit = "CONBTN.CFA";
    public static readonly Point ControlsLitAt = new(0, 149);

    // MOUSE.IMG (198 × 96), MOUSE SENSITIVITY (CONTROLS → MOUSE, SKYNET.EXE v1.01 loop 0x140c77):
    // HORIZONTAL and VERTICAL bars of 11 red segments, REVERSE VERTICAL, EXIT.
    public const string MouseImage = "MOUSE.IMG";
    public static readonly Size MouseSize = new(198, 96);
    /// <summary>The bars' wells; their segments are 7 wide (the last 8), 10 apart, from x 74, rows 27 and 44, 6 high.</summary>
    public static readonly Rectangle MouseHorizontal = new(72, 26, 112, 8);
    public static readonly Rectangle MouseVertical = new(72, 43, 112, 8);
    public const int MouseSegmentX = 74;
    public const int MouseSegmentPitch = 10;
    public const int MouseSegmentWidth = 7;
    public static readonly int[] MouseSegmentY = [27, 44];
    public const int MouseSegmentHeight = 6;
    /// <summary>A gap of the well between two segments, stretched over a segment that is not lit.</summary>
    public static readonly Rectangle MouseSegmentGap = new(81, 27, 3, 6);
    public static readonly Rectangle MouseReverse = new(10, 64, 94, 17);
    public static readonly Rectangle MouseExit = new(155, 79, 43, 17);
    /// <summary>MOUSBTN.CFA: REVERSE VERTICAL, EXIT lit, at the REVERSE VERTICAL button's top-left.</summary>
    public const string MouseLit = "MOUSBTN.CFA";
    public static readonly Point MouseLitAt = new(10, 64);

    /// <summary>The CONTROLS boxes → the C# app's action names (SkyNet.App's ActionSet).</summary>
    public static readonly FrozenDictionary<string, string> DefaultControlActions = new Dictionary<string, string>
    {
        ["forward"] = "Forward", ["back"] = "Back", ["turn_left"] = "Turn left", ["turn_right"] = "Turn right",
        ["left"] = "Strafe left", ["right"] = "Strafe right", ["fire"] = "Fire", ["throw"] = "Throw", ["activate"] = "Use",
        ["slide"] = "Slide", ["sprint"] = "Run", ["up"] = "Jump", ["down"] = "Crouch", ["look_up"] = "Look up",
        ["look_down"] = "Look down", ["center_view"] = "Center view", ["automap"] = "Automap",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    // QUIT.IMG ("QUIT TO DOS?"), QUITMAIN.IMG ("QUIT TO MAIN MENU?"), RESTART.IMG
    // ("RESTART MISSION?"): the same 96 × 37 box, YES and NO baked in.
    public const string QuitImage = "QUIT.IMG";
    public const string QuitMainImage = "QUITMAIN.IMG";
    public const string RestartImage = "RESTART.IMG";
    public static readonly Size BoxSize = new(96, 37);
    public static readonly Rectangle BoxYes = new(0, 20, 53, 17);
    public static readonly Rectangle BoxNo = new(53, 20, 43, 17);
    /// <summary>QUITBTN.CFA (QUIT, QUITMAIN) and RESTBTN.CFA (RESTART): YES, NO lit, at (0, 20) of the box.</summary>
    public const string QuitLit = "QUITBTN.CFA";
    public const string RestartLit = "RESTBTN.CFA";
    public static readonly Point BoxLitAt = new(0, 20);

    // The Godot port's panel scales on its 1280 x 720 canvas (menu.gd *_PANEL_SCALE, main.gd RESTART_BOX_SCALE).
    public const float NewGameScale = 4f;
    public const float SlotsScale = 3f;
    public const float OptionsScale = 3f;
    public const float DetailScale = 3f;
    public const float ControlsScale = 4f;
    public const float MouseScale = 3f;
    public const float BoxScale = 4f;

    // The mission screen (FUN_0012c300): BUTTON04..11 tabs, the picture, MENU000.IMG with the text.
    public const string BriefPanel = "MENU000.IMG";
    public const string TacticalBackdrop = "TACTBAK.IMG";
    /// <summary>TACTICAL / STATISTICS palette: FUN_0012ddad sets BRIEF2.COL only for the BRIEFING
    /// tab's scene; TACTBAK.IMG in it turns into cyan noise.</summary>
    public const string TacticalPalette = BriefPalette;
    public static readonly Rectangle BriefPicture = new(0, 14, 320, 136);
    public static readonly Rectangle BriefPanelRect = new(0, 150, 320, 50);
    public static readonly Rectangle BriefUp = new(299, 150, 21, 25);
    public static readonly Rectangle BriefDown = new(299, 175, 21, 25);
    public static readonly Rectangle BriefText = new(7, 154, 288, 42);

    public static readonly (MissionTab Tab, Rectangle Rect, string Grey, string Green)[] BriefTabs =
    [
        (MissionTab.Begin, new(0, 0, 55, 14), "BUTTON04.IMG", "BUTTON05.IMG"),
        (MissionTab.Briefing, new(55, 0, 80, 14), "BUTTON06.IMG", "BUTTON07.IMG"),
        (MissionTab.Tactical, new(135, 0, 82, 14), "BUTTON08.IMG", "BUTTON09.IMG"),
        (MissionTab.Statistics, new(217, 0, 103, 14), "BUTTON10.IMG", "BUTTON11.IMG"),
    ];

    /// <summary>A panel of <paramref name="size"/> centred on the 320 × 200 screen.</summary>
    public static Point Centred(Size size) => new((MenuFrame.ScreenWidth - size.Width) / 2, (MenuFrame.ScreenHeight - size.Height) / 2);

    public static Rectangle At(Point origin, Rectangle r) => new(origin.X + r.X, origin.Y + r.Y, r.Width, r.Height);
}

public enum MissionTab { Begin, Briefing, Tactical, Statistics }
