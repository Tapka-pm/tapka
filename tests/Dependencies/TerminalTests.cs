using System.Diagnostics;
using Tapka;
using Tapka.Terminal;
using Tapka.Terminal.Testing;

internal static class TerminalTests
{
    public static async Task RunAsync(string installation)
    {
        var standardOut = Console.Out;
        var standardError = Console.Error;
        using (var terminal = new TerminalTestConsole(width: 120, height: 10, isInteractive: false))
        {
            var code = await TapkaCli.RunAsync(["list", "--explicit", "--root", installation], terminal);
            var screen = terminal.GetScreenText();
            Check(code == 0 && screen.Contains("VERSION") && screen.Contains("explicit"),
                $"Expected the installed package on the virtual screen, got exit code {code}: {screen}");
        }

        using var output = new StringWriter();
        using var error = new StringWriter();
        ITerminal recording = new RecordingTerminal(output, error);

        var helpCode = await TapkaCli.RunAsync(["--help"], recording);
        Check(helpCode == 0 && output.ToString().Contains("List installed packages") && error.ToString().Length == 0,
            "Expected CLI help on the supplied stdout writer.");

        output.GetStringBuilder().Clear();
        var parseCode = await TapkaCli.RunAsync(["--unknown-option"], recording);
        Check(parseCode != 0 && error.ToString().Contains("--unknown-option"),
            "Expected parsing diagnostics on the supplied stderr writer.");

        output.GetStringBuilder().Clear();
        error.GetStringBuilder().Clear();
        var handlerCode = await TapkaCli.RunAsync(["info", "missing", "--root", installation], recording);
        Check(handlerCode == 1 && output.ToString().Length == 0 && error.ToString().Contains("Package 'missing' is not installed."),
            "Expected inventory errors on the supplied stderr writer.");

        error.GetStringBuilder().Clear();
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"));
        foreach (var argument in new[] { "/d", "/c", "echo child output& echo child error 1>&2" })
            start.ArgumentList.Add(argument);
        await InstallProcess.RunAsync(start, "test process", recording, CancellationToken.None);
        Check(output.ToString().Trim() == "child output" && error.ToString().Trim() == "child error",
            "Expected child process output on the corresponding terminal streams.");
        Check(ReferenceEquals(Console.Out, standardOut) && ReferenceEquals(Console.Error, standardError),
            "Invoking CLI commands must not replace the process console writers.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed record RecordingTerminal(TextWriter Out, TextWriter Error) : ITerminal
    {
        public int Width => 80;
        public int Height => 24;
        public bool IsInteractive => false;
    }
}
