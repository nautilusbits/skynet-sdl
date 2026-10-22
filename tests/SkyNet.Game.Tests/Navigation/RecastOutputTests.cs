using SkyNet.Game.Navigation;

namespace SkyNet.Game.Tests.Navigation;

/// <summary>Console is process-wide: these run alone.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConsoleCollection
{
    public const string Name = "Console";
}

/// <summary>DotRecast's console lines during a bake go to the log; other threads' writes pass.</summary>
[Collection(ConsoleCollection.Name)]
public class RecastOutputTests
{
    [Fact]
    public void A_bake_threads_lines_leave_the_console_and_others_pass()
    {
        var (was, wasError) = (Console.Out, Console.Error);
        var (output, error) = (new StringWriter(), new StringWriter());
        Console.SetOut(output);
        Console.SetError(error);
        try
        {
            RecastOutput.Hold();
            using (RecastOutput.Quiet())
            {
                Console.WriteLine("rcBuildRegions: 3 overlapping regions.");
                Console.Error.WriteLine("delaunayHull: Removing dangling face 1 [0,1,-1]");
                var other = new Thread(() => Console.WriteLine("other thread"));
                other.Start();
                other.Join();
            }

            Console.WriteLine("after the tile");
            RecastOutput.Release();
            // The filter gone: a quiet thread writes to the console again.
            using (RecastOutput.Quiet())
            {
                Console.WriteLine("after the bake");
            }

            Assert.Equal(["other thread", "after the tile", "after the bake"], output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
            Assert.Equal("", error.ToString());
        }
        finally
        {
            Console.SetOut(was);
            Console.SetError(wasError);
        }
    }
}
