using System.Runtime.InteropServices;

internal sealed class SetupSqlite(string directory) : IDisposable
{
    private IntPtr handle;

    public static SetupSqlite Load()
    {
        using var resource = typeof(SetupSqlite).Assembly.GetManifestResourceStream("tapka.e_sqlite3.dll")
            ?? throw new InvalidOperationException("The setup binary is missing its SQLite library.");
        var directory = Directory.CreateTempSubdirectory("tapka-sqlite-").FullName;
        var library = new SetupSqlite(directory);
        try
        {
            var path = Path.Combine(directory, "e_sqlite3.dll");
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            {
                resource.CopyTo(file);
            }
            library.handle = NativeLibrary.Load(path);
            NativeLibrary.SetDllImportResolver(typeof(SQLitePCL.SQLite3Provider_e_sqlite3).Assembly,
                (name, _, _) => name == "e_sqlite3" ? library.handle : IntPtr.Zero);
            return library;
        }
        catch
        {
            library.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (handle != IntPtr.Zero)
        {
            NativeLibrary.Free(handle);
            handle = IntPtr.Zero;
        }
        try
        {
            File.Delete(Path.Combine(directory, "e_sqlite3.dll"));
            Directory.Delete(directory);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"tapka-setup: could not clean up '{directory}': {error.Message}");
        }
    }
}
