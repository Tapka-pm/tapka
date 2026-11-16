namespace Tapka.Terminal;

/// <summary>Output streams and display capabilities supplied by the terminal host.</summary>
public interface ITerminal
{
    TextWriter Out { get; }
    TextWriter Error { get; }
    int Width { get; }
    int Height { get; }
    bool IsInteractive { get; }
}
