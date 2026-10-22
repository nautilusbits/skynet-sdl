using SkyNet.Tables;

// dotnet run --project tools/SkyNet.Tables -- <path-to-SKYNET.EXE> [--out <dir>] [--check]
// Writes data/tables/*.json (the repository's, found from the working folder) from SKYNET.EXE v1.01;
// --check only compares and exits 1 when a file differs.
// dotnet run --project tools/SkyNet.Tables -- textures <data-dir> --out <dir>
// Exports every TEXTURE.NNN record as a palette PNG plus manifest.json (the input of tools/upscale).
if (args.Length >= 1 && args[0] == "textures")
{
    if (args.Length != 4 || args[2] != "--out")
    {
        Console.Error.WriteLine("usage: SkyNet.Tables textures <data-dir> --out <dir>");
        return 2;
    }

    return TextureExport.Run(args[1], args[3]);
}

string? exePath = null, outDir = null;
var check = false;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--out" when i + 1 < args.Length: outDir = args[++i]; break;
        case "--check": check = true; break;
        case "-h" or "--help": exePath = null; i = args.Length; break;
        default: exePath ??= args[i]; break;
    }
}

if (exePath == null)
{
    Console.Error.WriteLine("usage: SkyNet.Tables <path-to-SKYNET.EXE> [--out <dir>] [--check]\n       SkyNet.Tables textures <data-dir> --out <dir>");
    return 2;
}

if (!File.Exists(exePath))
{
    Console.Error.WriteLine($"no such file: {exePath}");
    return 2;
}

var bytes = File.ReadAllBytes(exePath);
if (Extractor.Reject(bytes) is { } why)
{
    Console.Error.WriteLine(why);
    return 2;
}

outDir ??= TableFiles.RepoTablesDir(Directory.GetCurrentDirectory()) ?? Path.Combine("data", "tables");
var differ = 0;
foreach (var (name, json) in Extractor.Extract(bytes))
{
    var path = Path.Combine(outDir, name + ".json");
    var same = File.Exists(path) && TableFiles.Normalise(File.ReadAllText(path)) == json;
    if (check)
    {
        Console.WriteLine($"{(same ? "same   " : "DIFFERS")} {path}");
        differ += same ? 0 : 1;
        continue;
    }

    Directory.CreateDirectory(outDir);
    if (!same)
    {
        File.WriteAllText(path, json);
    }

    Console.WriteLine($"{(same ? "same   " : "written")} {path}");
}

return differ == 0 ? 0 : 1;
