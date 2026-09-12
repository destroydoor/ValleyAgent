#nullable enable

namespace ValleyAgent.TestMod.Infrastructure;

public sealed class ScreenshotResult
{
    public ScreenshotResult(string? path, int width, int height, string? error = null)
    {
        Path = path;
        Width = width;
        Height = height;
        Error = error;
    }

    public string? Path { get; }
    public int Width { get; }
    public int Height { get; }
    public string? Error { get; }

    public bool Success
    {
        get => Path != null;
    }
}