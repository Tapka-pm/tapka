namespace Tapka.Terminal;

/// <summary>Adapts the process console without taking ownership of its output streams.</summary>
public sealed class SystemTerminal : ITerminal
{
    public TextWriter Out { get; } = Console.Out;
    public TextWriter Error { get; } = Console.Error;
    public int Width => ReadDimension(() => Console.WindowWidth, 80);
    public int Height => ReadDimension(() => Console.WindowHeight, 24);
    public bool IsInteractive => !Console.IsOutputRedirected && Environment.GetEnvironmentVariable("TERM") != "dumb";

    private static int ReadDimension(Func<int> read, int fallback)
    {
        try
        {
            var value = read();
            return value > 0 ? value : fallback;
        }
        catch (IOException)
        {
            return fallback;
        }
        catch (PlatformNotSupportedException)
        {
            return fallback;
        }
    }
}
