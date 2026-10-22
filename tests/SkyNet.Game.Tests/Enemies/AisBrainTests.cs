using SkyNet.Formats;
using SkyNet.Game.Enemies;

namespace SkyNet.Game.Tests.Enemies;

/// <summary>The AIS VM's op semantics on hand-built scripts.</summary>
public class AisBrainTests
{
    private const uint Base = 0x50000;
    private const uint Walk = 0x60000, Shoot = 0x60100;

    private static AisOp Op(int i, AisOpKind kind, int a = 0, int b = 0, int c = 0, AisPredicate p = AisPredicate.Unknown,
        AisCompare cmp = AisCompare.None, int target = -1, uint anim = 0) =>
        new(Base + (uint)(16 * i), kind, 0, a, b, c, p, 0, cmp, target >= 0 ? Base + (uint)(16 * target) : 0, anim);

    private static Dictionary<uint, AisOp> Script(params AisOp[] ops) => ops.ToDictionary(o => o.Va);

    private static readonly Dictionary<uint, AisAnimBlock> Anims = new()
    {
        [Walk] = new AisAnimBlock(Walk, 1, 4, 10, AisAnimBlock.Loop, [new(6, 0), new(7, 0), new(8, 0), new(9, 1)]),
        [Shoot] = new AisAnimBlock(Shoot, 1, 3, 10, AisAnimBlock.OneShot | AisAnimBlock.FiringPose, [new(31, 0), new(32, 0), new(33, 1)]),
    };

    private static readonly AisSenses Seen = new(true, 500f, 0, 0, false);
    private static readonly AisSenses Unseen = new(false, 5000f, 0, 0, false);

    [Fact]
    public void IfGotoAndSoundRunOnAnimEndsTheTick()
    {
        // 0: if see <= 0 goto 3   1: sound 7   2: anim walk   3: sound 9   4: anim shoot   5: goto 0
        var b = new AisBrain(Script(
            Op(0, AisOpKind.If, b: 0, p: AisPredicate.See, cmp: AisCompare.Le, target: 3),
            Op(1, AisOpKind.Sound, a: 7),
            Op(2, AisOpKind.Anim, anim: Walk),
            Op(3, AisOpKind.Sound, a: 9),
            Op(4, AisOpKind.Anim, anim: Shoot),
            Op(5, AisOpKind.Goto, target: 0)), Anims, Base);

        b.Update(0f, Unseen);
        Assert.Equal(Walk, b.AnimVa);
        Assert.Equal(Base + 48, b.Pc);           // stopped after the ANIM
        Assert.Equal(6, b.Frame);
        Assert.Equal([7], b.Sounds);
        Assert.True(b.Looping);
        Assert.False(b.FiringPose);

        b.RunScript(0f, Seen);                   // 3: sound 9, 4: anim shoot
        Assert.Equal(Shoot, b.AnimVa);
        Assert.True(b.FiringPose);
        Assert.Equal(Base + 80, b.Pc);
        b.RunScript(0f, Seen);                   // 5: goto 0, see → 3, sound, anim shoot again
        Assert.Equal(Base + 80, b.Pc);
    }

    [Fact]
    public void WaitHoldsForTicksOf70thsAndCanFreezeTheAnimation()
    {
        var b = new AisBrain(Script(
            Op(0, AisOpKind.Anim, anim: Walk),
            Op(1, AisOpKind.Wait, a: 70, c: 1),
            Op(2, AisOpKind.Set, a: 56, b: 5),
            Op(3, AisOpKind.Goto, target: 3)), Anims, Base);

        b.Update(0f, Unseen);                     // anim
        b.Update(0f, Unseen);                     // wait 1 s, frozen
        Assert.True(b.FreezeAnim);
        Assert.Equal(1f, b.WaitLeft, 3);
        var frame = b.Frame;
        for (var i = 0; i < 30; i++)
        {
            b.Update(1f / 35f, Unseen);          // 0.86 s: still waiting, the legs still
        }

        Assert.Equal(frame, b.Frame);
        Assert.False(b.HasVar(56));
        for (var i = 0; i < 10; i++)
        {
            b.Update(1f / 35f, Unseen);
        }

        Assert.False(b.FreezeAnim);
        Assert.Equal(5, b.Var(56, 0));
    }

    [Fact]
    public void SetAddAndSetFnWriteVariablesOnePerTick()
    {
        var b = new AisBrain(Script(
            Op(0, AisOpKind.Set, a: 48, b: 100),
            Op(1, AisOpKind.Add, a: 48, b: -30),
            Op(2, AisOpKind.SetFn, a: 44, p: AisPredicate.AngleToPlayer),
            Op(3, AisOpKind.SetFn, a: 40, p: AisPredicate.Random)), Anims, Base);

        var s = new AisSenses(true, 100f, 0, 1500, false, Rand: 321);
        b.RunScript(0f, s);
        Assert.Equal(100, b.Var(48, 0));
        Assert.False(b.HasVar(44));
        b.RunScript(0f, s);
        Assert.Equal(70, b.Var(48, 0));
        b.RunScript(0f, s);
        Assert.Equal(1500, b.Var(44, 0));
        b.RunScript(0f, s);
        Assert.Equal(321, b.Var(40, 0));
        b.RunScript(0f, s);                       // walks off the end: the script is over
        Assert.False(b.HasScript);
    }

    [Theory]
    [InlineData(AisCompare.Le, 500, 500, true)]
    [InlineData(AisCompare.Le, 501, 500, false)]
    [InlineData(AisCompare.Lt, 500, 500, false)]
    [InlineData(AisCompare.Lt, 499, 500, true)]
    [InlineData(AisCompare.Gt, 501, 500, true)]
    [InlineData(AisCompare.Gt, 500, 500, false)]
    [InlineData(AisCompare.Eq0, 0, 99, true)]
    [InlineData(AisCompare.Ne0, 5, 99, true)]
    [InlineData(AisCompare.Ne0, 0, 99, false)]
    public void IfComparesThePredicateWithB(AisCompare cmp, int distance, int b, bool jumps)
    {
        var brain = new AisBrain(Script(
            Op(0, AisOpKind.If, b: b, p: AisPredicate.Distance, cmp: cmp, target: 2),
            Op(1, AisOpKind.Set, a: 1, b: 1),
            Op(2, AisOpKind.Set, a: 2, b: 2)), Anims, Base);
        brain.RunScript(0f, new AisSenses(true, distance, 0, 0, false));
        Assert.Equal(jumps, brain.HasVar(2));
        Assert.Equal(!jumps, brain.HasVar(1));
    }

    [Fact]
    public void PredicatesReadTheSenses()
    {
        var b = new AisBrain(Script(Op(0, AisOpKind.End)), Anims, Base);
        var s = new AisSenses(false, 12.7f, 1800, 77, true, Rand: 5);
        Assert.Equal(1, b.Predicate(AisPredicate.See, 0, s));                 // 1 = NOT perceived
        Assert.Equal(0, b.Predicate(AisPredicate.See, 0, s with { See = true }));
        Assert.Equal(12, b.Predicate(AisPredicate.Distance, 0, s));
        Assert.Equal(1800, b.Predicate(AisPredicate.Bearing, 0, s));
        Assert.Equal(248, b.Predicate(AisPredicate.BearingAbs, 0, s));
        Assert.Equal(77, b.Predicate(AisPredicate.AngleToPlayer, 0, s));
        Assert.Equal(AisBrain.FlagBlocked, b.Predicate(AisPredicate.Flags, 0x1000, s));
        Assert.Equal(0, b.Predicate(AisPredicate.Flags, 0x1000, s with { Blocked = false }));
        Assert.Equal(5, b.Predicate(AisPredicate.Random, 0, s));
    }

    [Fact]
    public void AnimationLoopsWrapAndOneShotsEndOnTheLastFrame()
    {
        var b = new AisBrain(Script(Op(0, AisOpKind.Anim, anim: Walk), Op(1, AisOpKind.Wait, a: 100000)), Anims, Base,
            events: [new AisFrameEvent(8, 42)]);
        b.Update(0f, Unseen);
        var seen = new List<int> { b.Frame };
        var sounds = new List<int>();
        for (var i = 0; i < 10; i++)
        {
            b.Update(0.1001f, Unseen);           // 10 fps: one frame per step
            seen.Add(b.Frame);
            sounds.AddRange(b.Sounds);
        }

        Assert.Equal([6, 7, 8, 9, 6, 7, 8, 9, 6, 7, 8], seen);
        Assert.Equal([42, 42, 42], sounds);       // the frame event on every frame 8

        b.PlayAnim(Shoot);
        for (var i = 0; i < 6; i++)
        {
            b.Update(0.1f, Unseen);
        }

        Assert.Equal(33, b.Frame);
        Assert.True(b.OneShotDone);
        Assert.Equal(AisBrain.AnimDone, b.Predicate(AisPredicate.AnimFlags, AisBrain.AnimDone, AisSenses.None));
    }

    [Fact]
    public void ALoopingBlockAlreadyPlayingIsNotRestarted()
    {
        var b = new AisBrain(Script(Op(0, AisOpKind.End)), Anims, Base);
        b.PlayAnim(Walk);
        b.Update(0.25f, Unseen);
        var f = b.Frame;
        Assert.NotEqual(6, f);
        b.PlayAnim(Walk);
        Assert.Equal(f, b.Frame);
        b.PlayAnim(Shoot);
        b.Update(0.15f, Unseen);
        b.PlayAnim(Shoot);                        // a one-shot restarts
        Assert.Equal(31, b.Frame);
    }

    [Fact]
    public void ScriptRunsAtTwelveHertz()
    {
        var b = new AisBrain(Script(Op(0, AisOpKind.Add, a: 4, b: 1), Op(1, AisOpKind.Goto, target: 0)), Anims, Base);
        for (var i = 0; i < 70; i++)
        {
            b.Update(1f / 70f, Unseen);          // one second
        }

        // ADD ends the tick, GOTO runs on: one ADD per evaluation, 12 evaluations a second.
        Assert.InRange(b.Var(4, 0), 11, 12);
    }
}
