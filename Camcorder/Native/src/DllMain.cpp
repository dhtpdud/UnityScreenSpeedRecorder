#include "Common.h"
#include "MinHook.h"
#include <stdarg.h>
#include <stdio.h>
#include <shlobj.h>

namespace Camcorder
{
    CamcorderShared* g_shared = nullptr;
    HWND g_unityWindow = nullptr;
    DWORD g_mainThreadId = 0;

    static HANDLE s_controlMapping = nullptr;
    static FILE* s_logFile = nullptr;
    static CRITICAL_SECTION s_logLock;
    static HANDLE s_controllerProcess = nullptr;
    static int32_t s_controllerPid = 0;
    static ULONGLONG s_lastControllerCheck = 0;
    static bool s_controllerAlive = true;

    void Log(const char* format, ...)
    {
        char message[1024];
        va_list args;
        va_start(args, format);
        vsnprintf(message, sizeof(message), format, args);
        va_end(args);

        SYSTEMTIME time;
        GetLocalTime(&time);
        EnterCriticalSection(&s_logLock);
        if (s_logFile)
        {
            fprintf(s_logFile, "[%02d:%02d:%02d.%03d][%lu] %s\n", time.wHour, time.wMinute, time.wSecond, time.wMilliseconds, GetCurrentThreadId(), message);
            fflush(s_logFile);
        }
        LeaveCriticalSection(&s_logLock);
        OutputDebugStringA("[UnityCamcorder] ");
        OutputDebugStringA(message);
        OutputDebugStringA("\n");
    }

    void SetLastErrorText(const char* format, ...)
    {
        char message[512];
        va_list args;
        va_start(args, format);
        vsnprintf(message, sizeof(message), format, args);
        va_end(args);
        Log("오류: %s", message);
        if (g_shared)
            strncpy_s(g_shared->lastError, sizeof(g_shared->lastError), message, _TRUNCATE);
    }

    void SetRuntimeInfo(const char* format, ...)
    {
        char message[256];
        va_list args;
        va_start(args, format);
        vsnprintf(message, sizeof(message), format, args);
        va_end(args);
        Log("%s", message);
        if (g_shared)
            strncpy_s(g_shared->runtimeInfo, sizeof(g_shared->runtimeInfo), message, _TRUNCATE);
    }

    void AddStatus(int32_t flags)
    {
        if (g_shared)
            InterlockedOr((volatile LONG*)&g_shared->status, flags);
    }

    void RemoveStatus(int32_t flags)
    {
        if (g_shared)
            InterlockedAnd((volatile LONG*)&g_shared->status, ~flags);
    }

    bool HasStatus(int32_t flags)
    {
        return g_shared && (g_shared->status & flags) == flags;
    }

    bool IsControllerAlive()
    {
        if (!g_shared)
            return false;
        ULONGLONG now = GetTickCount64();
        int32_t pid = g_shared->controllerPid;
        //캠코더를 다시 실행해서 다시 주입한 경우, 새 PID를 따라감
        if (pid != s_controllerPid)
        {
            if (s_controllerProcess)
                CloseHandle(s_controllerProcess);
            s_controllerPid = pid;
            s_controllerProcess = OpenProcess(SYNCHRONIZE, FALSE, (DWORD)pid);
            s_controllerAlive = s_controllerProcess != nullptr;
            s_lastControllerCheck = now;
        }
        else if (now - s_lastControllerCheck > 500)
        {
            s_lastControllerCheck = now;
            s_controllerAlive = s_controllerProcess && WaitForSingleObject(s_controllerProcess, 0) == WAIT_TIMEOUT;
        }
        return s_controllerAlive;
    }

    bool IsRecordRequested()
    {
        return g_shared && g_shared->command == CMD_RECORD && IsControllerAlive();
    }

    static BOOL CALLBACK FindUnityWindowProc(HWND hwnd, LPARAM lParam)
    {
        DWORD pid = 0;
        DWORD threadId = GetWindowThreadProcessId(hwnd, &pid);
        if (pid != GetCurrentProcessId())
            return TRUE;
        wchar_t className[64];
        if (GetClassNameW(hwnd, className, 64) && wcscmp(className, L"UnityWndClass") == 0)
        {
            g_unityWindow = hwnd;
            g_mainThreadId = threadId;
            return FALSE;
        }
        return TRUE;
    }

    bool FindUnityWindow()
    {
        EnumWindows(FindUnityWindowProc, 0);
        if (g_unityWindow && g_shared)
            g_shared->mainThreadId = (int32_t)g_mainThreadId;
        return g_unityWindow != nullptr;
    }

    static void OpenLogFile()
    {
        wchar_t localAppData[MAX_PATH];
        if (FAILED(SHGetFolderPathW(nullptr, CSIDL_LOCAL_APPDATA, nullptr, 0, localAppData)))
            return;
        wchar_t path[MAX_PATH];
        swprintf_s(path, L"%s\\UnityCamcorder", localAppData);
        CreateDirectoryW(path, nullptr);
        swprintf_s(path, L"%s\\UnityCamcorder\\Logs", localAppData);
        CreateDirectoryW(path, nullptr);
        swprintf_s(path, L"%s\\UnityCamcorder\\Logs\\hook_%lu.log", localAppData, GetCurrentProcessId());
        s_logFile = _wfsopen(path, L"a", _SH_DENYWR);
    }

    static DWORD WINAPI InitThread(LPVOID)
    {
        OpenLogFile();
        Log("UnityCamcorderHook 로드됨 (PID %lu)", GetCurrentProcessId());

        //캠코더가 주입 전에 만들어 둔 제어용 공유 메모리를 엶
        wchar_t name[128];
        swprintf_s(name, CAMCORDER_CONTROL_NAME, GetCurrentProcessId());
        s_controlMapping = OpenFileMappingW(FILE_MAP_ALL_ACCESS, FALSE, name);
        if (!s_controlMapping)
        {
            Log("제어용 공유 메모리를 열 수 없습니다 (%lu). 캠코더 없이 로드됨", GetLastError());
            return 0;
        }
        g_shared = (CamcorderShared*)MapViewOfFile(s_controlMapping, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(CamcorderShared));
        if (!g_shared || g_shared->magic != CAMCORDER_MAGIC || g_shared->version != CAMCORDER_VERSION)
        {
            Log("공유 메모리 버전이 맞지 않습니다");
            g_shared = nullptr;
            return 0;
        }
        g_shared->payloadVersion = CAMCORDER_VERSION;
        AddStatus(ST_LOADED);

        //유니티 창이 아직 없으면 잠시 기다림 (실행 직후 주입된 경우)
        for (int i = 0; i < 600 && !FindUnityWindow(); i++)
            Sleep(100);
        if (!g_unityWindow)
        {
            SetLastErrorText("유니티 창(UnityWndClass)을 찾을 수 없습니다");
            return 0;
        }
        Log("유니티 창 발견: HWND=%p, 메인 스레드=%lu", g_unityWindow, g_mainThreadId);

        if (MH_Initialize() != MH_OK)
        {
            SetLastErrorText("MinHook 초기화 실패");
            return 0;
        }
        if (!InstallMainThreadHooks())
            return 0;
        if (!InstallPresentHooks())
            return 0;
        AddStatus(ST_HOOKED);
        Log("후킹 완료");
        return 0;
    }
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls(module);
        InitializeCriticalSection(&Camcorder::s_logLock);
        //DllMain 안에서는 로더 잠금 때문에 할 수 있는 일이 적으므로, 별도 스레드에서 초기화
        HANDLE thread = CreateThread(nullptr, 0, Camcorder::InitThread, nullptr, 0, nullptr);
        if (thread)
            CloseHandle(thread);
    }
    //후킹이 남아 있으므로 DLL은 프로세스가 끝날 때까지 내리지 않음
    return TRUE;
}
