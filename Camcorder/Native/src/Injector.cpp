#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <wchar.h>

//아주 작은 DLL 주입기입니다. 사용법: Inject.exe <PID> <DLL 절대경로>
//주입기와 대상 프로세스의 비트 수(32/64)가 같아야 LoadLibraryW 주소가 유효합니다.
//따라서 32비트용/64비트용 두 벌을 빌드하고, 캠코더가 대상에 맞는 것을 고릅니다.
//반환 코드: 0 성공, 그 외 실패(메시지는 stderr).

int wmain(int argc, wchar_t** argv)
{
    if (argc < 3)
    {
        fwprintf(stderr, L"usage: Inject <pid> <dllPath>\n");
        return 2;
    }
    DWORD pid = (DWORD)wcstoul(argv[1], nullptr, 10);
    const wchar_t* dllPath = argv[2];

    DWORD attr = GetFileAttributesW(dllPath);
    if (attr == INVALID_FILE_ATTRIBUTES)
    {
        fwprintf(stderr, L"DLL not found: %s\n", dllPath);
        return 3;
    }

    HANDLE process = OpenProcess(PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION |
        PROCESS_VM_OPERATION | PROCESS_VM_WRITE | PROCESS_VM_READ, FALSE, pid);
    if (!process)
    {
        fwprintf(stderr, L"OpenProcess failed: %lu\n", GetLastError());
        return 4;
    }

    size_t size = (wcslen(dllPath) + 1) * sizeof(wchar_t);
    void* remote = VirtualAllocEx(process, nullptr, size, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!remote)
    {
        fwprintf(stderr, L"VirtualAllocEx failed: %lu\n", GetLastError());
        CloseHandle(process);
        return 5;
    }
    if (!WriteProcessMemory(process, remote, dllPath, size, nullptr))
    {
        fwprintf(stderr, L"WriteProcessMemory failed: %lu\n", GetLastError());
        VirtualFreeEx(process, remote, 0, MEM_RELEASE);
        CloseHandle(process);
        return 6;
    }

    //kernel32는 같은 비트 수의 모든 프로세스에서 같은 주소에 로드되므로, 내 LoadLibraryW 주소를 그대로 씀
    HMODULE kernel32 = GetModuleHandleW(L"kernel32.dll");
    auto loadLibrary = (LPTHREAD_START_ROUTINE)GetProcAddress(kernel32, "LoadLibraryW");
    HANDLE thread = CreateRemoteThread(process, nullptr, 0, loadLibrary, remote, 0, nullptr);
    if (!thread)
    {
        fwprintf(stderr, L"CreateRemoteThread failed: %lu\n", GetLastError());
        VirtualFreeEx(process, remote, 0, MEM_RELEASE);
        CloseHandle(process);
        return 7;
    }

    WaitForSingleObject(thread, 15000);
    DWORD exitCode = 0;
    GetExitCodeThread(thread, &exitCode);
    //x86에서 exitCode(32비트)는 HMODULE 하위 32비트. 0이면 LoadLibrary 실패로 간주
    int result = exitCode != 0 ? 0 : 8;

    //원격 스레드가 끝났으므로 할당 해제 (DLL은 이미 로드됨)
    VirtualFreeEx(process, remote, 0, MEM_RELEASE);
    CloseHandle(thread);
    CloseHandle(process);
    if (result != 0)
        fwprintf(stderr, L"LoadLibraryW returned 0 in target\n");
    return result;
}
