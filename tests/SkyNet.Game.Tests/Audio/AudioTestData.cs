using SkyNet.Formats;

namespace SkyNet.Game.Tests.Audio;

/// <summary>The player's game data through SKYNET_DATA; the audio tests that need it skip without it.</summary>
static class AudioTestData
{
    public const string EnvVar = "SKYNET_DATA";

    public static string? Dir
    {
        get
        {
            var d = Environment.GetEnvironmentVariable(EnvVar);
            return !string.IsNullOrWhiteSpace(d) && GameDataLocator.GameOf(d) == GameKind.SkyNet ? d : null;
        }
    }

    public static GameData Open() => GameData.Open(Dir!) ?? throw new InvalidOperationException("no game data");
}

/// <summary>A fact skipped unless SKYNET_DATA points at SkyNET's data folder.</summary>
sealed class AudioDataFactAttribute : FactAttribute
{
    public AudioDataFactAttribute()
    {
        if (AudioTestData.Dir == null)
        {
            Skip = $"{AudioTestData.EnvVar} is not set to a SkyNET data folder";
        }
    }
}
