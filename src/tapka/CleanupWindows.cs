using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;
using Windows.Win32.System.Diagnostics.ToolHelp;
using Windows.Win32.System.Threading;
using static Windows.Win32.Foundation.WIN32_ERROR;

namespace Tapka;

internal sealed record CleanupProcess(uint Id, uint ParentId, string Name, string? Image);

internal static class CleanupWindows
{
    public static unsafe List<CleanupProcess> Processes(CancellationToken token)
    {
        using var snapshot = PInvoke.CreateToolhelp32Snapshot_SafeHandle(CREATE_TOOLHELP_SNAPSHOT_FLAGS.TH32CS_SNAPPROCESS, 0);
        if (snapshot.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());

        var entry = new PROCESSENTRY32W { dwSize = (uint)sizeof(PROCESSENTRY32W) };
        var result = new List<CleanupProcess>();
        var image = new char[32768];
        for (var found = PInvoke.Process32FirstW(snapshot, ref entry); found; found = PInvoke.Process32NextW(snapshot, ref entry))
        {
            token.ThrowIfCancellationRequested();
            if (entry.th32ProcessID == 0) continue;
            using var process = PInvoke.OpenProcess_SafeHandle(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, entry.th32ProcessID);
            if (process.IsInvalid && (WIN32_ERROR)Marshal.GetLastPInvokeError() == ERROR_INVALID_PARAMETER) continue;

            var length = (uint)image.Length;
            var path = !process.IsInvalid && PInvoke.QueryFullProcessImageName(process, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, image, ref length)
                ? new string(image, 0, (int)length) : null;
            result.Add(new CleanupProcess(entry.th32ProcessID, entry.th32ParentProcessID, entry.szExeFile.ToString(), path));
        }
        var error = (WIN32_ERROR)Marshal.GetLastPInvokeError();
        if (error != ERROR_NO_MORE_FILES) throw new Win32Exception((int)error);
        return result;
    }

    public static string? FileUse(IReadOnlyList<string> files, CancellationToken token)
    {
        if (files.Count == 0) return null;

        Span<char> key = stackalloc char[(int)PInvoke.CCH_RM_SESSION_KEY + 1];
        var error = PInvoke.RmStartSession(out var session, key);
        if (error != ERROR_SUCCESS) return Failure(error);
        try
        {
            error = PInvoke.RmRegisterResources(session, files.ToArray(), [], []);
            if (error != ERROR_SUCCESS) return Failure(error);
            token.ThrowIfCancellationRequested();
            uint capacity = 0;
            error = PInvoke.RmGetList(session, out var needed, ref capacity, [], out var rebootReasons);
            if ((error == ERROR_MORE_DATA && needed > 0) || (error == ERROR_SUCCESS && (needed > 0 || rebootReasons != 0)))
                return "Windows reports files in use";
            if (error != ERROR_SUCCESS) return Failure(error);
        }
        finally
        {
            PInvoke.RmEndSession(session);
        }
        return CheckSharing(files, token);
    }

    public static string? CheckSharing(IReadOnlyList<string> files, CancellationToken token)
    {
        foreach (var path in files)
        {
            token.ThrowIfCancellationRequested();
            using var file = OpenForDeletion(path);
            if (!file.IsInvalid) continue;
            var error = (WIN32_ERROR)Marshal.GetLastPInvokeError();
            return error is ERROR_SHARING_VIOLATION or ERROR_LOCK_VIOLATION ? $"file in use '{path}'" :
                $"cannot check '{path}' ({new Win32Exception((int)error).Message})";
        }
        return null;
    }

    public static void DeleteReadOnlyFile(string path)
    {
        using var file = OpenForDeletion(path);
        var disposition = new FILE_DISPOSITION_INFO_EX
        {
            Flags = FILE_DISPOSITION_INFO_EX_FLAGS.FILE_DISPOSITION_FLAG_DELETE |
                FILE_DISPOSITION_INFO_EX_FLAGS.FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE
        };
        var bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in disposition, 1));
        if (file.IsInvalid || !PInvoke.SetFileInformationByHandle(file, FILE_INFO_BY_HANDLE_CLASS.FileDispositionInfoEx, bytes))
        {
            var error = new Win32Exception(Marshal.GetLastPInvokeError());
            throw new IOException($"Could not delete '{path}': {error.Message}", error);
        }
    }

    private static SafeFileHandle OpenForDeletion(string path) =>
        PInvoke.CreateFile(path, (uint)FILE_ACCESS_RIGHTS.DELETE, FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE |
            FILE_SHARE_MODE.FILE_SHARE_DELETE, null, FILE_CREATION_DISPOSITION.OPEN_EXISTING, FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_OPEN_REPARSE_POINT, null);

    private static string Failure(WIN32_ERROR error) => $"Restart Manager could not check files ({new Win32Exception((int)error).Message})";
}
