#include <windows.h>
#include <stdio.h>
#include <wchar.h>

#define WINDOWS_PATH_CAPACITY 32768

static int print_windows_error(const wchar_t *message)
{
    DWORD error = GetLastError();
    fwprintf(stderr, L"tapka: %ls (Windows error %lu).\n", message, (unsigned long)error);
    return 1;
}

static BOOL WINAPI handle_console_interrupt(DWORD event)
{
    // The child receives the same console event and determines the final exit code.
    return event == CTRL_C_EVENT || event == CTRL_BREAK_EVENT;
}

static const wchar_t *skip_executable(const wchar_t *command_line)
{
    while (*command_line == L' ' || *command_line == L'\t') {
        ++command_line;
    }

    // Windows parses argv[0] as the executable name, with quotes allowing spaces.
    BOOL quoted = FALSE;
    while (*command_line != L'\0') {
        if (*command_line == L'"') {
            quoted = !quoted;
        } else if (!quoted && (*command_line == L' ' || *command_line == L'\t')) {
            break;
        }
        ++command_line;
    }
    return command_line;
}

static BOOL read_target(const wchar_t *file, wchar_t *target, DWORD capacity)
{
    HANDLE reader = CreateFileW(file, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_DELETE,
        NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (reader == INVALID_HANDLE_VALUE) {
        return FALSE;
    }

    DWORD bytes = 0;
    BOOL read = ReadFile(reader, target, capacity * sizeof(wchar_t), &bytes, NULL);
    DWORD error = GetLastError();
    CloseHandle(reader);
    if (!read) {
        SetLastError(error);
        return FALSE;
    }
    if (bytes == 0 || bytes >= capacity * sizeof(wchar_t) || bytes % sizeof(wchar_t) != 0) {
        SetLastError(ERROR_INVALID_DATA);
        return FALSE;
    }

    target[bytes / sizeof(wchar_t)] = L'\0';
    return TRUE;
}

int wmain(void)
{
    static wchar_t executable[WINDOWS_PATH_CAPACITY];
    const wchar_t target_suffix[] = L".target";
    DWORD length = GetModuleFileNameW(NULL, executable, WINDOWS_PATH_CAPACITY);
    if (length == 0 || length + ARRAYSIZE(target_suffix) > WINDOWS_PATH_CAPACITY) {
        return print_windows_error(L"cannot locate the command shim");
    }
    wcscpy(executable + length, target_suffix);

    static wchar_t target[WINDOWS_PATH_CAPACITY];
    if (!read_target(executable, target, WINDOWS_PATH_CAPACITY)) {
        return print_windows_error(L"cannot read the command target");
    }

    wchar_t *filename = wcsrchr(executable, L'\\');
    if (filename != NULL) {
        *filename = L'\0';
    }
    wchar_t *shims = wcsrchr(executable, L'\\');
    const wchar_t packages[] = L"\\packages\\";
    if (filename == NULL || shims == NULL ||
        (size_t)(shims - executable) + ARRAYSIZE(packages) + wcslen(target) > WINDOWS_PATH_CAPACITY) {
        fwprintf(stderr, L"tapka: cannot locate the package directory.\n");
        return 1;
    }
    wcscpy(shims, packages);
    wcscpy(shims + ARRAYSIZE(packages) - 1, target);

    // Pass the argument tail unchanged, including quotes and trailing backslashes.
    const wchar_t *arguments = skip_executable(GetCommandLineW());
    static wchar_t command_line[WINDOWS_PATH_CAPACITY];
    if (wcslen(executable) + wcslen(arguments) + 3 > WINDOWS_PATH_CAPACITY) {
        fwprintf(stderr, L"tapka: the command line is too long.\n");
        return 1;
    }
    wcscpy(command_line, L"\"");
    wcscat(command_line, executable);
    wcscat(command_line, L"\"");
    wcscat(command_line, arguments);

    STARTUPINFOW startup = {
        .cb = sizeof(STARTUPINFOW),
        .dwFlags = STARTF_USESTDHANDLES,
        .hStdInput = GetStdHandle(STD_INPUT_HANDLE),
        .hStdOutput = GetStdHandle(STD_OUTPUT_HANDLE),
        .hStdError = GetStdHandle(STD_ERROR_HANDLE),
    };
    PROCESS_INFORMATION child = {0};
    SetConsoleCtrlHandler(handle_console_interrupt, TRUE);
    if (!CreateProcessW(executable, command_line, NULL, NULL, TRUE, 0, NULL, NULL, &startup, &child)) {
        return print_windows_error(L"cannot launch the installed command");
    }

    CloseHandle(child.hThread);
    DWORD exit_code;
    if (WaitForSingleObject(child.hProcess, INFINITE) != WAIT_OBJECT_0 ||
        !GetExitCodeProcess(child.hProcess, &exit_code)) {
        int error = print_windows_error(L"cannot wait for the installed command");
        CloseHandle(child.hProcess);
        return error;
    }
    CloseHandle(child.hProcess);
    ExitProcess(exit_code);
}
