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

static DWORD read_target(const wchar_t *file, wchar_t *target, DWORD capacity)
{
    HANDLE reader = CreateFileW(file, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_DELETE,
        NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (reader == INVALID_HANDLE_VALUE) {
        return 0;
    }

    DWORD bytes = 0;
    BOOL read = ReadFile(reader, target, capacity * sizeof(wchar_t), &bytes, NULL);
    DWORD error = GetLastError();
    CloseHandle(reader);
    if (!read) {
        SetLastError(error);
        return 0;
    }
    DWORD length = bytes / sizeof(wchar_t);
    if (length < 4 || length == capacity || bytes % sizeof(wchar_t) != 0 || target[length - 1] != L'\0') {
        SetLastError(ERROR_INVALID_DATA);
        return 0;
    }

    return length;
}

static BOOL prepend_dependencies(const wchar_t *directory)
{
    DWORD attributes = GetFileAttributesW(directory);
    if (attributes == INVALID_FILE_ATTRIBUTES) {
        DWORD error = GetLastError();
        return error == ERROR_FILE_NOT_FOUND || error == ERROR_PATH_NOT_FOUND;
    }
    if (!(attributes & FILE_ATTRIBUTE_DIRECTORY)) {
        SetLastError(ERROR_DIRECTORY);
        return FALSE;
    }

    // The updated environment is local to the shim and inherited by its child.
    static wchar_t path[WINDOWS_PATH_CAPACITY];
    size_t prefix = wcslen(directory);
    if (prefix + 2 > ARRAYSIZE(path)) {
        SetLastError(ERROR_BUFFER_OVERFLOW);
        return FALSE;
    }
    wcscpy(path, directory);
    DWORD capacity = (DWORD)(ARRAYSIZE(path) - prefix - 1);
    SetLastError(ERROR_SUCCESS);
    DWORD inherited = GetEnvironmentVariableW(L"PATH", path + prefix + 1, capacity);
    if (inherited == 0 && GetLastError() != ERROR_SUCCESS && GetLastError() != ERROR_ENVVAR_NOT_FOUND) {
        return FALSE;
    }
    if (inherited >= capacity) {
        SetLastError(ERROR_BUFFER_OVERFLOW);
        return FALSE;
    }
    if (inherited != 0) {
        path[prefix] = L';';
    }
    return SetEnvironmentVariableW(L"PATH", path);
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

    static wchar_t target[2 * WINDOWS_PATH_CAPACITY];
    DWORD target_length = read_target(executable, target, ARRAYSIZE(target));
    if (target_length == 0) {
        return print_windows_error(L"cannot read the command target");
    }
    size_t executable_length = wcslen(target);
    const wchar_t *dependencies = target + executable_length + 1;
    if (executable_length == 0 || executable_length + 2 >= target_length ||
        wcslen(dependencies) + executable_length + 2 != target_length) {
        fwprintf(stderr, L"tapka: invalid command target file.\n");
        return 1;
    }
    if (!prepend_dependencies(dependencies)) {
        return print_windows_error(L"cannot set the package dependency PATH");
    }

    // Pass the argument tail unchanged, including quotes and trailing backslashes.
    const wchar_t *arguments = skip_executable(GetCommandLineW());
    static wchar_t command_line[WINDOWS_PATH_CAPACITY];
    if (executable_length + wcslen(arguments) + 3 > WINDOWS_PATH_CAPACITY) {
        fwprintf(stderr, L"tapka: the command line is too long.\n");
        return 1;
    }
    wcscpy(command_line, L"\"");
    wcscat(command_line, target);
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
    if (!CreateProcessW(target, command_line, NULL, NULL, TRUE, 0, NULL, NULL, &startup, &child)) {
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
