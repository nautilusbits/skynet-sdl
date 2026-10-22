using System.Diagnostics;
using SkyNet.Game.DevTools;

namespace SkyNet.Game.Tests.DevTools;

/// <summary>--capture: a fixed step a frame whatever the wall clock, numbered files, the switches' checks.</summary>
public class FrameCaptureTests
{
    [Fact]
    public void Each_frame_taken_moves_the_clock_one_fixed_step()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"capture-{Guid.NewGuid():N}");
        try
        {
            var capture = new FrameCapture(dir, 30);
            Assert.True(Directory.Exists(dir));
            Assert.Equal(0L, capture.Timestamp);

            Assert.Equal(Path.Combine(dir, "frame_000000.bmp"), capture.Take());
            Assert.Equal(Path.Combine(dir, "frame_000001.bmp"), capture.Take());
            Thread.Sleep(20);
            Assert.Equal(Path.Combine(dir, "frame_000002.bmp"), capture.Take());

            // Three frames at 30 fps: 100 ms, however long they took.
            Assert.Equal(3, capture.Taken);
            Assert.Equal(100L, capture.Milliseconds);
            Assert.Equal(Stopwatch.Frequency / 10, capture.Timestamp);
            Assert.Equal(0.1f, capture.Seconds, 1e-6f);
            Assert.Equal(1.0 / 30, capture.Step, 1e-12);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void A_fixed_step_without_a_folder_saves_nothing_and_keeps_the_clock()
    {
        var steps = new FrameCapture(null, 60);
        Assert.Null(steps.Dir);
        Assert.Null(steps.Take());
        Assert.Null(steps.Take());
        Assert.Equal(2, steps.Taken);
        Assert.Equal(33L, steps.Milliseconds);
    }

    [Fact]
    public void A_frame_with_its_own_screenshot_is_copied_into_the_capture()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"capture-{Guid.NewGuid():N}");
        try
        {
            var capture = new FrameCapture(dir);
            var shot = Path.Combine(dir, "shot.bmp");
            File.WriteAllBytes(shot, [1, 2, 3]);
            var frame = capture.Take();

            FrameCapture.Mirror(null, frame);
            Assert.False(File.Exists(frame));
            FrameCapture.Mirror(shot, frame);
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(frame));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("60", 60)]
    [InlineData("10", 10)]
    [InlineData("240", 240)]
    public void The_frame_rate_parses(string value, int fps) => Assert.Equal(fps, FrameCapture.ParseFps(value));

    [Theory]
    [InlineData(null)]
    [InlineData("9")]
    [InlineData("241")]
    [InlineData("sixty")]
    public void A_bad_frame_rate_is_a_format_error(string? value) => Assert.Throws<FormatException>(() => FrameCapture.ParseFps(value));

    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData(null)]
    public void A_bad_frame_count_is_a_format_error(string? value) => Assert.Throws<FormatException>(() => FrameCapture.ParseFrames(value));
}
