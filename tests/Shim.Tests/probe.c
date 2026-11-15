#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <wchar.h>

#define CAPACITY 32768

// Use ASCII even on a redirected pipe, preserving every UTF-16 code unit.
static void print_setting(const char *name, const wchar_t *value)
{
    printf("%s=", name);
    for (; *value != L'\0'; ++value) {
        printf("%04x", (unsigned int)*value);
    }
    puts("");
}

static HANDLE interrupted;
static DWORD expected_event;
static BOOL WINAPI handle_event(DWORD event)
{
    return event == expected_event && SetEvent(interrupted);
}

static int generate_interrupt(DWORD event)
{
    expected_event = event;
    interrupted = CreateEventW(NULL, TRUE, FALSE, NULL);
    if (interrupted == NULL || !SetConsoleCtrlHandler(handle_event, TRUE) ||
        !GenerateConsoleCtrlEvent(event, 0)) {
        return 91;
    }
    return WaitForSingleObject(interrupted, 5000) == WAIT_OBJECT_0 ?
        (event == CTRL_C_EVENT ? 41 : 42) : 92;
}

static int run_in_new_console(const wchar_t *shim, const wchar_t *argument)
{
    static wchar_t command_line[CAPACITY];
    if (swprintf_s(command_line, ARRAYSIZE(command_line), L"\"%ls\" %ls", shim, argument) < 0) {
        return 93;
    }
    STARTUPINFOW startup = {
        .cb = sizeof(STARTUPINFOW),
        .dwFlags = STARTF_USESHOWWINDOW,
        .wShowWindow = SW_HIDE
    };
    PROCESS_INFORMATION child = {0};
    if (!CreateProcessW(shim, command_line, NULL, NULL, FALSE, CREATE_NEW_CONSOLE, NULL, NULL, &startup, &child)) {
        return 94;
    }

    CloseHandle(child.hThread);
    DWORD exit_code;
    if (WaitForSingleObject(child.hProcess, 10000) != WAIT_OBJECT_0 ||
        !GetExitCodeProcess(child.hProcess, &exit_code)) {
        TerminateProcess(child.hProcess, 95);
        exit_code = 95;
    }
    CloseHandle(child.hProcess);
    ExitProcess(exit_code);
}

int wmain(int argc, wchar_t **argv)
{
    if (argc == 4 && wcscmp(argv[1], L"--new-console") == 0) {
        return run_in_new_console(argv[2], argv[3]);
    }
    if (argc == 2 && wcscmp(argv[1], L"--ctrl-c") == 0) {
        return generate_interrupt(CTRL_C_EVENT);
    }
    if (argc == 2 && wcscmp(argv[1], L"--ctrl-break") == 0) {
        return generate_interrupt(CTRL_BREAK_EVENT);
    }
    if (argc < 2) {
        return 90;
    }

    static wchar_t value[CAPACITY];
    if (GetModuleFileNameW(NULL, value, ARRAYSIZE(value)) >= ARRAYSIZE(value)) {
        return 90;
    }
    print_setting("module", value);
    if (GetCurrentDirectoryW(ARRAYSIZE(value), value) >= ARRAYSIZE(value)) {
        return 90;
    }
    print_setting("cwd", value);
    *value = L'\0';
    if (GetEnvironmentVariableW(L"PATH", value, ARRAYSIZE(value)) >= ARRAYSIZE(value)) {
        return 90;
    }
    print_setting("path", value);
    for (int index = 2; index < argc; ++index) {
        print_setting("arg", argv[index]);
    }

    char input[256] = {0};
    if (fgets(input, sizeof(input), stdin) != NULL) {
        printf("stdin=%s", input);
    }
    fputs("probe stderr\n", stderr);
    fflush(stdout);
    ExitProcess(wcstoul(argv[1], NULL, 0));
}
