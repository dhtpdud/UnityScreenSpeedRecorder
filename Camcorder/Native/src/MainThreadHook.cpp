#include "Common.h"
#include "Mono.h"
#include <limits.h>

//유니티 메인 스레드에서 매 프레임 한 번 불리는 지점(틱)을 만듭니다.
//시간 제어(captureFramerate)와 카메라 촬영은 유니티 API라서 반드시 메인 스레드에서 호출해야 합니다.
//
//유니티의 프레임 이벤트 Application.onBeforeRender(5.4+)에, 네이티브 함수를 감싼 대리자를 등록합니다.
//(창 메시지 처리는 유니티 버전·설정에 따라 메인 스레드가 아닌 곳에서 돌 수 있어, 메시지 펌프 대신 유니티 이벤트를 씁니다.)
//Canvas.willRenderCanvases에도 함께 등록하고, Time.frameCount로 프레임당 한 번만 처리합니다.

namespace Camcorder
{
    bool EnsureRuntime();
    void ApplyCaptureTiming(int fps);
    void RestoreTiming();
    bool StartCameraCapture(int session);
    void StopCameraCapture();
    void TickCameraCapture();
    void HandleCameraListRequest();
    void ReportScreenSizeIfNeeded();

    volatile LONG g_tickRegistered = 0;

    static int s_lastHandledSession = -1;
    static Mono::Method s_getFrameCount = nullptr;
    static int s_lastFrame = INT_MIN;
    static bool s_firstTick = true;
    static uint32_t s_delegates[2] = {};   // GC가 대리자를 치우지 않도록 붙잡아 둠

    //메인 스레드에서 매 프레임 한 번 호출됨
    static void OnMainThreadTick()
    {
        if (!g_shared)
            return;
        //Mono 런타임 준비(앱 시작 직후면 준비될 때까지 가끔 다시 시도), 카메라 목록 요청 응답
        EnsureRuntime();
        ReportScreenSizeIfNeeded();
        HandleCameraListRequest();

        bool wantRecord = IsRecordRequested();
        int session = g_shared->sessionId;

        if (wantRecord)
        {
            if (session != s_lastHandledSession)
            {
                s_lastHandledSession = session;
                if (g_shared->appliedSessionId == session)
                {
                    //틱이 늦게 시작해 Present 훅이 이미 화면 캡처로 맡은 세션: 중간에 방식을 바꾸지 않음
                    Log("세션 %d은 이미 화면 캡처로 진행 중이라 그대로 둠", session);
                    return;
                }
                //새 녹화 세션: 배속 적용 → 카메라 촬영 준비(안 되면 Present 훅의 화면 캡처) → 처리 완료 표시
                StopCameraCapture();
                ApplyCaptureTiming(g_shared->fps);
                StartCameraCapture(session);
                MemoryBarrier();
                g_shared->appliedSessionId = session;   // Present 훅은 이 값을 본 뒤에야 화면 캡처를 시작
            }
            TickCameraCapture();
        }
        else if (s_lastHandledSession != -1)
        {
            //명령이 멈췄거나 캠코더가 죽었으면 카메라와 속도를 원래대로 복원
            StopCameraCapture();
            RestoreTiming();
            s_lastHandledSession = -1;
        }
    }

    //유니티가 메인 스레드에서 부르는 콜백 (인자 없는 void 대리자)
    static void WINAPI TickCallback()
    {
        int frame = 0;
        if (s_getFrameCount && Mono::GetInt(s_getFrameCount, nullptr, frame))
        {
            if (frame == s_lastFrame)
                return;   // 같은 프레임에 두 이벤트가 모두 불린 경우
            s_lastFrame = frame;
        }
        if (s_firstTick)
        {
            s_firstTick = false;
            g_mainThreadId = GetCurrentThreadId();
            if (g_shared)
                g_shared->mainThreadId = (int32_t)g_mainThreadId;
            AddStatus(ST_MAIN_THREAD);
            Log("메인 스레드 틱 시작 (스레드 %lu, 프레임 %d)", g_mainThreadId, frame);
        }
        OnMainThreadTick();
    }

    //네이티브 함수 포인터를 delegateType 형식의 대리자로 만들어 이벤트 add 메서드에 넘김
    static bool AddEventHandler(Mono::Method getDelegate, Mono::Class delegateType, Mono::Method addMethod, uint32_t& handle)
    {
        Mono::Object type = Mono::TypeOf(delegateType);
        if (!getDelegate || !type || !addMethod)
            return false;
        void* function = (void*)&TickCallback;
        void* args[2] = { &function, type };   // (IntPtr ptr, Type t)
        Mono::Object callback = nullptr;
        if (!Mono::Invoke(getDelegate, nullptr, args, &callback) || !callback)
            return false;
        handle = Mono::Hold(callback);
        void* addArgs[1] = { callback };
        return Mono::Invoke(addMethod, nullptr, addArgs);
    }

    static bool RegisterTick()
    {
        Mono::Class marshal = Mono::FindCorlibClass("System.Runtime.InteropServices", "Marshal");
        Mono::Method getDelegate = Mono::FindMethodSig(marshal, "GetDelegateForFunctionPointer", { "System.IntPtr", "System.Type" });
        Mono::Class time = Mono::FindClass("UnityEngine", "Time");
        s_getFrameCount = Mono::FindMethod(time, "get_frameCount", 0);

        //1) Application.onBeforeRender (UnityAction)
        Mono::Class application = Mono::FindClass("UnityEngine", "Application");
        Mono::Class unityAction = Mono::FindClass("UnityEngine.Events", "UnityAction");
        bool beforeRender = AddEventHandler(getDelegate, unityAction,
            Mono::FindMethod(application, "add_onBeforeRender", 1), s_delegates[0]);

        //2) Canvas.willRenderCanvases (UI 모듈). 프레임 번호로 중복을 거를 수 있을 때만
        bool canvas = false;
        if (s_getFrameCount)
        {
            Mono::Class canvasClass = Mono::FindClassIn("UnityEngine.UIModule", "UnityEngine", "Canvas");
            if (!canvasClass)
                canvasClass = Mono::FindClass("UnityEngine", "Canvas");
            canvas = AddEventHandler(getDelegate, Mono::FindNestedClass(canvasClass, "WillRenderCanvases"),
                Mono::FindMethod(canvasClass, "add_willRenderCanvases", 1), s_delegates[1]);
        }

        Log("메인 스레드 틱 등록: onBeforeRender %s, willRenderCanvases %s (Marshal=%p, UnityAction=%p)",
            beforeRender ? "성공" : "실패", canvas ? "성공" : "실패", getDelegate, unityAction);
        return beforeRender || canvas;
    }

    bool InstallMainThreadTick()
    {
        //Mono와 UnityEngine 어셈블리가 올라올 때까지 기다림 (실행 직후 주입된 경우)
        bool ready = false;
        for (int i = 0; i < 600 && !ready; i++)
        {
            if (GetModuleHandleW(L"GameAssembly.dll"))
            {
                AddStatus(ST_RUNTIME_IL2CPP | ST_TIME_FAILED);
                SetRuntimeInfo("IL2CPP 런타임 — 배속·카메라 촬영 불가, 화면을 실시간으로 캡처");
                return false;
            }
            ready = Mono::IsCoreLoaded();
            if (!ready)
                Sleep(100);
        }
        if (!ready)
        {
            SetLastErrorText("Mono 런타임(UnityEngine 어셈블리)을 찾지 못해 화면을 실시간으로 캡처합니다");
            return false;
        }

        //이 초기화 스레드를 잠깐 Mono에 붙여 이벤트에 등록 (실제 틱은 유니티 메인 스레드에서 불림)
        void* thread = Mono::AttachThread();
        bool ok = Mono::Init() && RegisterTick();
        Mono::DetachThread(thread);
        if (!ok)
        {
            SetLastErrorText("유니티 프레임 이벤트에 등록하지 못해 화면을 실시간으로 캡처합니다");
            return false;
        }
        InterlockedExchange(&g_tickRegistered, 1);
        AddStatus(ST_TICK);
        return true;
    }
}
