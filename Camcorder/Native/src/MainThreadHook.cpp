#include "Common.h"
#include "MinHook.h"

//유니티 메인 스레드는 매 프레임 메시지 펌프(PeekMessageW)를 돕니다.
//그 호출을 가로채서, 시간 제어(captureDeltaTime) 설정/복원을 반드시 메인 스레드에서 실행합니다.
//(Mono runtime_invoke는 유니티 메인 스레드에서 호출해야 안전합니다.)

namespace Camcorder
{
    void ApplyCaptureTiming(int fps);
    void RestoreTiming();
    bool IsTimingApplied();

    typedef BOOL (WINAPI* PeekMessageW_t)(LPMSG, HWND, UINT, UINT, UINT);
    static PeekMessageW_t s_originalPeekMessageW = nullptr;
    static int s_lastHandledSession = -1;

    //메인 스레드에서 매 프레임 한 번 호출됨
    static void OnMainThreadTick()
    {
        if (GetCurrentThreadId() != g_mainThreadId)
            return;
        if (!HasStatus(ST_MAIN_THREAD))
            AddStatus(ST_MAIN_THREAD);

        bool wantRecord = IsRecordRequested();
        int session = g_shared ? g_shared->sessionId : 0;

        if (wantRecord)
        {
            //새 녹화 세션이 시작되면 시간 제어를 (다시) 적용
            if (!IsTimingApplied() || session != s_lastHandledSession)
            {
                ApplyCaptureTiming(g_shared->fps);
                s_lastHandledSession = session;
            }
        }
        else
        {
            //명령이 멈췄거나 캠코더가 죽었으면 원래 속도로 복원
            if (IsTimingApplied())
            {
                RestoreTiming();
                s_lastHandledSession = -1;
            }
        }
    }

    static BOOL WINAPI PeekMessageW_Detour(LPMSG msg, HWND hwnd, UINT min, UINT max, UINT remove)
    {
        BOOL result = s_originalPeekMessageW(msg, hwnd, min, max, remove);
        //PeekMessage는 프레임당 여러 번 불릴 수 있으므로, 큐가 빈(= 프레임 끝) 시점에만 처리
        if (!result && GetCurrentThreadId() == g_mainThreadId)
            OnMainThreadTick();
        return result;
    }

    bool InstallMainThreadHooks()
    {
        HMODULE user32 = GetModuleHandleW(L"user32.dll");
        if (!user32)
        {
            SetLastErrorText("user32.dll 없음");
            return false;
        }
        void* target = (void*)GetProcAddress(user32, "PeekMessageW");
        if (!target)
        {
            SetLastErrorText("PeekMessageW 주소를 찾을 수 없음");
            return false;
        }
        if (MH_CreateHook(target, (void*)PeekMessageW_Detour, (void**)&s_originalPeekMessageW) != MH_OK ||
            MH_EnableHook(target) != MH_OK)
        {
            SetLastErrorText("PeekMessageW 후킹 실패");
            return false;
        }
        Log("메인 스레드 훅(PeekMessageW) 설치 완료");
        return true;
    }
}
