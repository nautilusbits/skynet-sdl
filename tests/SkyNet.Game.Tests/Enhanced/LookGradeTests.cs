using System.Numerics;
using RetroEng.Render;
using SkyNet.Game.Enhanced;

namespace SkyNet.Game.Tests.Enhanced;

/// <summary>The enhanced look's post: AgX, auto exposure and a cached procedural grade per preset.</summary>
public class LookGradeTests
{
    [Theory]
    [InlineData(LightPreset.Night)]
    [InlineData(LightPreset.Dusk)]
    [InlineData(LightPreset.Interior)]
    public void PresetsTakeAgxAutoExposureAndTheirGrade(LightPreset preset)
    {
        var lighting = LookLighting.For(preset, null, 2000f, 1f);
        Assert.Equal(ToneCurve.AgX, lighting.ToneCurve);
        Assert.Same(LookLighting.ExposureOf(preset), lighting.AutoExposure);
        Assert.Same(LookGrade.Of(preset), lighting.Grade);
    }

    [Fact]
    public void BrightnessStillScalesTheExposure()
    {
        var lighting = LookLighting.For(LightPreset.Interior, null, 2000f, 1.5f);
        Assert.Equal(1.5f * LookLighting.ExposureMood, lighting.Exposure);
    }

    [Fact]
    public void EachPresetKeepsOneGrade()
    {
        Assert.Same(LookGrade.Of(LightPreset.Night), LookGrade.Of(LightPreset.Night));
        Assert.Same(LookLighting.For(LightPreset.Dusk, null, 2000f, 1f).Grade, LookLighting.For(LightPreset.Dusk, null, 900f, 2f).Grade);
        Assert.NotSame(LookGrade.Of(LightPreset.Night), LookGrade.Of(LightPreset.Dusk));
        Assert.NotSame(LookGrade.Of(LightPreset.Night), LookGrade.Of(LightPreset.Interior));
    }

    [Fact]
    public void StripRunsRedAcrossGreenDownBlueBySquare()
    {
        const int size = 4;
        var strip = LookGrade.Strip(size, c => c);
        Assert.Equal(size * size * size * 4, strip.Length);

        // Pixel (b·size + r, g) holds (r, g, b) / (size - 1).
        static int At(int x, int y) => (y * size * size + x) * 4;
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, strip[At(0, 0)..(At(0, 0) + 4)]);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, strip[At(3, 0)..(At(3, 0) + 4)]);
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, strip[At(0, 3)..(At(0, 3) + 4)]);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, strip[At(12, 0)..(At(12, 0) + 4)]);
        Assert.Equal(new byte[] { 85, 170, 85, 255 }, strip[At(5, 2)..(At(5, 2) + 4)]);

        // The engine reads it back as the identity.
        var grade = ColourGrade.FromStrip(LookGrade.Strip(size, c => c), size);
        var identity = ColourGrade.Identity(size);
        for (var i = 0; i < size; i++)
        {
            Assert.True(Vector3.Distance(identity[i, size - 1 - i, i], grade[i, size - 1 - i, i]) < 1e-3f);
        }
    }

    [Fact]
    public void GradesAreSubtleAndKeepTheEnds()
    {
        foreach (var preset in new[] { LightPreset.Night, LightPreset.Dusk, LightPreset.Interior })
        {
            var grade = LookGrade.Of(preset);
            var top = grade.Size - 1;
            Assert.True(Vector3.Distance(Vector3.One, grade[top, top, top]) < 0.01f);
            Assert.True(Vector3.Distance(Vector3.Zero, grade[0, 0, 0]) < 0.05f);
            for (var i = 0; i <= top; i += 4)
            {
                var input = new Vector3(i / (float)top);
                Assert.True(Vector3.Distance(input, grade[i, i, i]) < 0.06f, $"{preset} grey {i}");
            }
        }
    }

    [Fact]
    public void NightShadowsGoBlueDuskMidsWarm()
    {
        var night = LookGrade.Apply(new Vector3(0.15f), LookGrade.NightShadows, LookGrade.NightMids);
        Assert.True(night.Z > night.X);
        var dusk = LookGrade.Apply(new Vector3(0.5f), LookGrade.DuskShadows, LookGrade.DuskMids);
        Assert.True(dusk.X > dusk.Z);
    }
}
