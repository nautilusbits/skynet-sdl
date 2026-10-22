using System.IO.Compression;
using RetroEng.Jobs;
using RetroEng.Platform;

namespace SkyNet.Upscale;

/// <summary>
/// <see cref="RealEsrgan"/> as an <see cref="IImageUpscaler"/>: the batch is written as PNGs into a
/// work folder, upscaled 4x in one run of the tool, and each result decoded when it is asked for
/// (so a batch of thousands of frames never sits in memory at once). Disposing the batch deletes
/// the work folder.
/// </summary>
public sealed class RealEsrganUpscaler(RealEsrgan tool, string workFolder, UpscaleModel model = UpscaleModel.General) : IImageUpscaler
{
    public int Factor => RealEsrgan.Factor(model, 4);

    public IUpscaledBatch Upscale(IReadOnlyList<RgbImage> images, Action<int>? done, CancellationToken cancel)
    {
        if (Directory.Exists(workFolder))
        {
            Directory.Delete(workFolder, recursive: true);
        }

        var input = Path.Combine(workFolder, "in");
        var output = Path.Combine(workFolder, "out");
        Directory.CreateDirectory(input);
        JobSystem.Shared.ParallelFor(images.Count, 4, new ActionFor(k =>
        {
            cancel.ThrowIfCancellationRequested();
            var image = images[k];
            File.WriteAllBytes(Path.Combine(input, Name(k)), PngWriter.Encode(image.Width, image.Height, 3, image.Rgb, CompressionLevel.Fastest));
        }));

        var batch = new Batch(workFolder, output);
        try
        {
            tool.Run(input, output, model, Factor, done, cancel);
            return batch;
        }
        catch
        {
            batch.Dispose();
            throw;
        }
    }

    private static string Name(int k) => $"{k:D6}.png";

    private sealed class Batch(string work, string output) : IUpscaledBatch
    {
        public RgbImage this[int index]
        {
            get
            {
                var path = Path.Combine(output, Name(index));
                var (w, h, rgba) = PngImage.Decode(File.ReadAllBytes(path)) ?? throw new InvalidDataException($"{path} does not decode");
                var rgb = new byte[w * h * 3];
                for (var i = 0; i < w * h; i++)
                {
                    rgb[i * 3] = rgba[i * 4];
                    rgb[i * 3 + 1] = rgba[i * 4 + 1];
                    rgb[i * 3 + 2] = rgba[i * 4 + 2];
                }

                return new RgbImage(w, h, rgb);
            }
        }

        public void Dispose()
        {
            if (Directory.Exists(work))
            {
                Directory.Delete(work, recursive: true);
            }
        }
    }
}
