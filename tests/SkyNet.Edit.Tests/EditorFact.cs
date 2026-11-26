using SDL;
using static SDL.SDL3;

namespace SkyNet.Edit.Tests;

/// <summary>A fact that runs SkyNetEdit: skipped unless SKYNET_DATA points at SkyNET's data and a
/// GPU backend (Vulkan, Direct3D 12 or Metal) is there, as on a box with no GPU and no lavapipe.</summary>
public sealed class EditorFactAttribute : FactAttribute
{
    public EditorFactAttribute()
    {
        Skip = EditDocumentTests.DataDir == null ? "SKYNET_DATA is not set to a SkyNET data folder" : EditorGpu.Skip;
    }
}

/// <summary>Whether the editor could make its graphics device here: SDL's own check, as the editor
/// starts SDL (video, then a GPU backend for any of its shader formats). Asked once.</summary>
static class EditorGpu
{
    private const SDL_GPUShaderFormat Formats =
        SDL_GPUShaderFormat.SDL_GPU_SHADERFORMAT_SPIRV | SDL_GPUShaderFormat.SDL_GPU_SHADERFORMAT_DXIL | SDL_GPUShaderFormat.SDL_GPU_SHADERFORMAT_MSL;

    private static readonly Lazy<bool> Present = new(Probe);

    public static string? Skip => Present.Value ? null : "no SDL GPU backend (Vulkan 1.0, Direct3D 12 or Metal) for SkyNetEdit";

    private static bool Probe()
    {
        if (!SDL_InitSubSystem(SDL_InitFlags.SDL_INIT_VIDEO))
        {
            return false;
        }

        try
        {
            return SDL_GPUSupportsShaderFormats(Formats, (Utf8String)null);
        }
        finally
        {
            SDL_QuitSubSystem(SDL_InitFlags.SDL_INIT_VIDEO);
        }
    }
}
