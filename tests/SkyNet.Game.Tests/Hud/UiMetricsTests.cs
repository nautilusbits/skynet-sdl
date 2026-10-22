using RetroEng.Render;
using SkyNet.Game.Hud;
using SkyNet.Game.Menu;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Tests.Hud;

/// <summary>Sizes from the screen's physical size (<see cref="UiMetrics"/>): the foldable's two
/// screens (outer 2376 × 1060 at ~480 dpi, inner 2344 × 2156 at 402 dpi) and a 1080p desktop.</summary>
public class UiMetricsTests
{
    private const float OuterMm = 2376f / (480f / 25.4f);
    private const float InnerMm = 2344f / (402f / 25.4f);
    private const float MinMm = 2.5f;
    private const int Line = 9;

    private static float Mm(float px, float pxPerMm) => px / pxPerMm;

    [Fact]
    public void Desktop_without_a_size_keeps_todays_scale()
    {
        Assert.Equal(0f, UiMetrics.PxPerMm(1920, 0f, UiScale.Auto));
        Assert.Equal(6, UiMetrics.TextPixel(6, Line, 0f));
        Assert.Equal(Renderer.CanvasHeight / 1080f, UiMetrics.ConsolePixel(1080, Line, 0f), 5);
    }

    [Fact]
    public void Phone_density_is_measured_and_clamped()
    {
        Assert.Equal(480f / 25.4f, UiMetrics.PxPerMm(2376, OuterMm, UiScale.Auto), 2);
        Assert.InRange(UiMetrics.PxPerMm(2376, 1f, UiScale.Auto), 1f, 32f);   // a bad OEM density
        Assert.InRange(UiMetrics.PxPerMm(2376, 10000f, UiScale.Auto), 2f, 32f);
    }

    [Fact]
    public void Modes_scale_the_minimum()
    {
        var auto = UiMetrics.PxPerMm(2376, OuterMm, UiScale.Auto);
        Assert.Equal(0f, UiMetrics.PxPerMm(2376, OuterMm, UiScale.X1));
        Assert.Equal(auto * 1.5f, UiMetrics.PxPerMm(2376, OuterMm, UiScale.X1_5), 3);
        Assert.Equal(auto * 2f, UiMetrics.PxPerMm(2376, OuterMm, UiScale.X2), 3);
        // A desktop's 2X: its text grows too.
        Assert.True(UiMetrics.TextPixel(1, Line, UiMetrics.PxPerMm(1920, 0f, UiScale.X2)) > 1);
    }

    [Theory]
    [InlineData(2376, 1060, OuterMm)]
    [InlineData(2344, 2156, InnerMm)]
    public void Phone_text_reaches_the_minimum(int w, int h, float widthMm)
    {
        var mm = UiMetrics.PxPerMm(w, widthMm, UiScale.Auto);
        Assert.True(mm > 0f);
        Assert.True(Mm(UiMetrics.TextPixel(1, Line, mm) * Line, mm) >= MinMm);
        var console = UiMetrics.ConsolePixel(h, Line, mm) * h / Renderer.CanvasHeight;
        Assert.True(Mm(console * Line, mm) >= MinMm, $"console line {Mm(console * Line, mm)} mm");
        Assert.True(Mm(ModernLayout.For(w, h, mm).Unit * ModernLayout.RowHeight, mm) >= MinMm);
    }

    [Fact]
    public void Modern_unit_unchanged_without_a_size() =>
        Assert.Equal(ModernLayout.For(1920, 1080).Unit, ModernLayout.For(1920, 1080, 0f).Unit);

    [Theory]
    [InlineData(16f / 9f)]
    [InlineData(4f / 3f)]
    public void Fov_unchanged_at_4_3_and_wider(float aspect) =>
        Assert.Equal(1.2f, UiMetrics.FovY(1.2f, aspect), 6);

    [Fact]
    public void Fov_below_4_3_keeps_the_4_3_width()
    {
        var fov = 70f * MathF.PI / 180f;
        var aspect = 2344f / 2156f;
        var tanX = MathF.Tan(UiMetrics.FovY(fov, aspect) / 2f) * aspect;
        Assert.Equal(MathF.Tan(fov / 2f) * 4f / 3f, tanX, 4);
    }

    [Fact]
    public void Menu_screen_keeps_4_3_on_a_square_screen()
    {
        var f = MenuFrame.Fit(2344, 2156);
        Assert.Equal(1.2f, f.ScaleY / f.ScaleX, 3);
        Assert.Equal(0f, f.ScreenX(0));
        Assert.Equal(2344f, f.ScreenX(MenuFrame.ScreenWidth));
        var top = f.ScreenY(0);
        Assert.Equal(2156f - f.ScreenY(MenuFrame.ScreenHeight), top, 0);
        Assert.Equal(1758f, f.ScreenY(MenuFrame.ScreenHeight) - top, 0);
    }

    [Fact]
    public void Menu_screen_unchanged_when_wide() =>
        Assert.Equal(new MenuFrame(1920, 1080), MenuFrame.Fit(1920, 1080));

    [Fact]
    public void Gun_keeps_its_4_3_size_on_a_square_screen()
    {
        Assert.Equal(ViewModelLayout.Scale(2344, 1758), ViewModelLayout.Scale(2344, 2156));
        Assert.Equal(5.25f, ViewModelLayout.Scale(1920, 1080));
    }
}
