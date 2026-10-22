using SkyNet.Game.Menu;

namespace SkyNet.Game.Tests.Menu;

/// <summary>DETAIL's first-start default from the GPU's name, and the setting kept in the file.</summary>
public class DetailGuessTests
{
    [Theory]
    [InlineData("NVIDIA GeForce RTX 4070", Detail.High)]
    [InlineData("AMD Radeon RX 7800 XT", Detail.High)]
    [InlineData("Intel(R) Arc(TM) A770 Graphics", Detail.High)]
    [InlineData("", Detail.High)]
    [InlineData("Intel(R) UHD Graphics 620", Detail.Medium)]
    [InlineData("Intel(R) Iris(R) Xe Graphics", Detail.Medium)]
    [InlineData("Intel(R) Arc(TM) Graphics", Detail.Medium)]
    [InlineData("AMD Radeon(TM) Graphics", Detail.Medium)]
    [InlineData("AMD Radeon Graphics", Detail.Medium)]
    [InlineData("AMD Radeon(TM) Vega 8 Graphics", Detail.Medium)]
    [InlineData("AMD Radeon 780M", Detail.Medium)]
    [InlineData("Apple M1", Detail.Medium)]
    [InlineData("NVIDIA GeForce MX450", Detail.Medium)]
    [InlineData("NVIDIA GeForce GTX 1650", Detail.Medium)]
    [InlineData("Radeon RX 6500 XT", Detail.Medium)]
    [InlineData("Intel(R) HD Graphics 520", Detail.Low)]
    [InlineData("Adreno (TM) 740", Detail.Low)]
    [InlineData("Mali-G78", Detail.Low)]
    [InlineData("llvmpipe (LLVM 17.0.6, 256 bits)", Detail.Low)]
    public void The_gpu_name_picks_the_first_detail(string gpu, Detail detail) => Assert.Equal(detail, DetailGuess.Pick(gpu));

    [Fact]
    public void Only_a_saved_detail_counts_as_chosen()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("skynet-detail-").FullName, "settings.json");
        try
        {
            Assert.False(SettingsStore.HasDetail(path));
            File.WriteAllText(path, $$"""{"format":"{{SettingsStore.Format}}","version":{{SettingsStore.Version}}}""");
            Assert.False(SettingsStore.HasDetail(path));

            Assert.Equal(SaveResult.Saved, SettingsStore.Save(new MenuSettings { Detail = Detail.Low }, path));

            Assert.True(SettingsStore.HasDetail(path));
            Assert.Equal(Detail.Low, SettingsStore.Load(path).Detail);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }
}
