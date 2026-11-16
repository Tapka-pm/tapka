# Tapka.Terminal.Testing

Tapka.Terminal.Testing is a virtual terminal for .NET tests. Pass it to code that accepts a `Tapka.Terminal.ITerminal`, then assert on the text left on the screen. It uses [LibGhostty.Net](https://github.com/jkelin/libghostty-net) to interpret ANSI sequences, so a test can verify that a progress update replaced a previous line or that text wrapped at a given width.

## Add it to a test project

Use the .NET 11 SDK and an x64 .NET runtime on Windows. The LibGhostty.Net NuGet dependency supplies the native terminal library.

From the client checkout, set `$testProject` to your test project and add a project reference. For inline snapshots, also install [Meziantou.Framework.InlineSnapshotTesting](https://www.nuget.org/packages/Meziantou.Framework.InlineSnapshotTesting/6.0.24) in that project.

```powershell
$testProject = 'tests/YourProject/YourProject.csproj'
dotnet reference add src/Tapka.Terminal.Testing/Tapka.Terminal.Testing.csproj --project $testProject
dotnet package add Meziantou.Framework.InlineSnapshotTesting --version 6.0.24 --project $testProject
```

## Write a test

```csharp
using System.Threading.Tasks;
using Meziantou.Framework.InlineSnapshotTesting;
using Tapka.Terminal;
using Tapka.Terminal.Testing;
using Xunit;

public sealed class ProgressTests
{
    [Fact]
    public void ProgressReplacesPreviousLine()
    {
        using var console = new TerminalTestConsole(width: 40, height: 10);

        WriteProgress(console);

        InlineSnapshot.Validate(console.GetScreenText(), """
            Done
            """);
    }

    [Fact]
    public async Task WarningAppearsBelowStatus()
    {
        using var console = new TerminalTestConsole(width: 40, height: 10);

        await WriteStatusAsync(console);

        InlineSnapshot.Validate(console.GetScreenText(), """
            Checked packages
            Warning: one update failed
            """);
    }

    private static void WriteProgress(ITerminal terminal)
    {
        terminal.Out.Write("Downloading 10%");
        terminal.Out.Write("\r\u001b[2KDone");
    }

    private static async Task WriteStatusAsync(ITerminal terminal)
    {
        await terminal.Out.WriteLineAsync("Checked packages");
        await terminal.Error.WriteLineAsync("Warning: one update failed");
    }
}
```

The constructor takes a required `width` in columns and `height` in rows. The emulator uses those dimensions for wrapping and scrolling. It reports `IsInteractive` as true by default. Pass `isInteractive: false` to exercise code that writes plain output. `Out` and `Error` are separate writers into the same screen, each with its own `NewLine` setting. Await async writes before reading the screen. Use `using` to release the emulator after each test.

`GetScreenText()` joins the visible rows with LF. It preserves leading spaces and blank rows between lines, trims trailing spaces from each row, and omits blank rows at the bottom. The result contains screen text only. Scrollback, cursor position, colors, and styles are outside the snapshot. Tests provide their own code under test and control when the snapshot is taken. Process hosting, PTYs, image rendering, and animation timing are outside the package's scope.

## Run tests and update inline snapshots

From the client checkout, run the package's xUnit tests.

```powershell
dotnet test src/Tapka.Terminal.Testing/Tests
```

The default strategy reports mismatches without editing source files. To insert a snapshot, start with `InlineSnapshot.Validate(console.GetScreenText())`. To insert or replace the expected literal, run locally with the explicit overwrite strategy, review the diff, and rerun the tests with the default strategy.

```powershell
$env:INLINESNAPSHOTTESTING_STRATEGY = 'OverwriteWithoutFailure'
try {
    dotnet test src/Tapka.Terminal.Testing/Tests
} finally {
    Remove-Item Env:INLINESNAPSHOTTESTING_STRATEGY
}
git diff
dotnet test src/Tapka.Terminal.Testing/Tests
```

For a consuming project, use its test project path in the same commands. The snapshot library disables updates in detected CI environments.
