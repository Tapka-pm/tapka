#include <windows.h>
#include <stdio.h>
#include <wchar.h>

#define WINDOWS_PATH_CAPACITY 32768

// Keep this layout in sync with ShimBinary. Volatile reads prevent the
// compiler from substituting the unpatched values from the initializer.
static const volatile struct {
    char marker[32];
    wchar_t root_relative[WINDOWS_PATH_CAPACITY];
    wchar_t target_exe[WINDOWS_PATH_CAPACITY];
    wchar_t path_prefix[WINDOWS_PATH_CAPACITY];
} config = { .marker = "TAPKA_SHIM_CONFIG_V1:0e94b57a3d" };

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

static BOOL copy_setting(wchar_t *destination, const volatile wchar_t *source, BOOL optional)
{
    for (size_t i = 0; i < WINDOWS_PATH_CAPACITY; ++i) {
        wchar_t character = source[i];
        destination[i] = character;
        if (character == L'\0') {
            // Reject rooted paths, including drive-relative paths.
            return i == 0 ? optional : destination[0] != L'\\' &&
                destination[0] != L'/' && wcschr(destination, L':') == NULL;
        }
    }
    return FALSE;
}

static BOOL resolve_path(const wchar_t *base, const wchar_t *relative, wchar_t *absolute)
{
    static wchar_t joined[WINDOWS_PATH_CAPACITY];
    size_t length = wcslen(base);
    BOOL separator = length > 0 && base[length - 1] != L'\\';
    if (length + separator + wcslen(relative) >= ARRAYSIZE(joined)) {
        SetLastError(ERROR_FILENAME_EXCED_RANGE);
        return FALSE;
    }
    wcscpy(joined, base);
    if (separator) {
        joined[length++] = L'\\';
    }
    wcscpy(joined + length, relative);
    DWORD resolved = GetFullPathNameW(joined, ARRAYSIZE(joined), absolute, NULL);
    if (resolved >= ARRAYSIZE(joined)) {
        SetLastError(ERROR_FILENAME_EXCED_RANGE);
        return FALSE;
    }
    return resolved != 0;
}

static BOOL prepend_dependencies(const wchar_t *directory)
{
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
    static wchar_t module[WINDOWS_PATH_CAPACITY];
    DWORD length = GetModuleFileNameW(NULL, module, ARRAYSIZE(module));
    if (length == 0 || length == ARRAYSIZE(module)) {
        return print_windows_error(L"cannot locate the command shim");
    }
    wchar_t *filename = wcsrchr(module, L'\\');
    if (filename == NULL) {
        fwprintf(stderr, L"tapka: invalid command shim path.\n");
        return 1;
    }
    filename[1] = L'\0';

    static wchar_t root_relative[WINDOWS_PATH_CAPACITY];
    static wchar_t target_relative[WINDOWS_PATH_CAPACITY];
    static wchar_t prefix_relative[WINDOWS_PATH_CAPACITY];
    if (!copy_setting(root_relative, config.root_relative, FALSE) ||
        !copy_setting(target_relative, config.target_exe, FALSE) ||
        !copy_setting(prefix_relative, config.path_prefix, TRUE)) {
        fwprintf(stderr, L"tapka: invalid embedded shim settings.\n");
        return 1;
    }

    static wchar_t root[WINDOWS_PATH_CAPACITY];
    static wchar_t target[WINDOWS_PATH_CAPACITY];
    if (!resolve_path(module, root_relative, root) || !resolve_path(root, target_relative, target)) {
        return print_windows_error(L"cannot resolve the command target");
    }
    if (*prefix_relative != L'\0') {
        static wchar_t prefix[WINDOWS_PATH_CAPACITY];
        if (!resolve_path(root, prefix_relative, prefix)) {
            return print_windows_error(L"cannot resolve the package dependency PATH");
        }
        if (wcschr(prefix, L';') != NULL) {
            fwprintf(stderr, L"tapka: the package dependency path contains a semicolon.\n");
            return 1;
        }
        if (!prepend_dependencies(prefix)) {
            return print_windows_error(L"cannot set the package dependency PATH");
        }
    }

    // Pass the argument tail unchanged, including quotes and trailing backslashes.
    const wchar_t *arguments = skip_executable(GetCommandLineW());
    static wchar_t command_line[WINDOWS_PATH_CAPACITY];
    if (wcslen(target) + wcslen(arguments) + 3 > WINDOWS_PATH_CAPACITY) {
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
