#include "Common.h"
#include "Mono.h"

//유니티 메인 스레드에서 Time.captureDeltaTime(캡처 고정 프레임 간격)을 켜고 끄는 부분입니다.
//원본 프로젝트의 RecorderManager.CapturingSetting()이 하던 일을, 외부에서 Mono 런타임을 통해 합니다.
//핵심: Time.captureDeltaTime = 1/fps 로 두면, 게임의 Time.deltaTime이 실제 경과 시간과 무관하게 1/fps로 고정됩니다.
//그러면 하드웨어가 허락하는 한 최대 속도로 프레임이 흘러가지만, 각 프레임이 "1/fps만큼 지났다"고 여기므로
//결과 영상을 fps로 재생하면 정상 속도로 보입니다. 이것이 배속 촬영입니다.

namespace Camcorder
{
    static Mono::Method s_setCaptureDeltaTime = nullptr;   // Time.captureDeltaTime (2019.2+)
    static Mono::Method s_setCaptureFramerate = nullptr;   // Time.captureFramerate (구버전 대체)
    static Mono::Method s_setTimeScale = nullptr;
    static Mono::Method s_getTargetFrameRate = nullptr;
    static Mono::Method s_setTargetFrameRate = nullptr;
    static Mono::Method s_getVSyncCount = nullptr;
    static Mono::Method s_setVSyncCount = nullptr;
    static Mono::Method s_getRunInBackground = nullptr;
    static Mono::Method s_setRunInBackground = nullptr;
    static bool s_monoReady = false;
    static int s_initAttempts = 0;

    static bool ResolveTimeMethods()
    {
        Mono::Class timeClass = Mono::FindClass("UnityEngine", "Time");
        Mono::Class appClass = Mono::FindClass("UnityEngine", "Application");
        Mono::Class qualityClass = Mono::FindClass("UnityEngine", "QualitySettings");
        if (!timeClass)
            return false;
        s_setCaptureDeltaTime = Mono::FindMethod(timeClass, "set_captureDeltaTime", 1);
        s_setCaptureFramerate = Mono::FindMethod(timeClass, "set_captureFramerate", 1);
        s_setTimeScale = Mono::FindMethod(timeClass, "set_timeScale", 1);
        s_getTargetFrameRate = Mono::FindMethod(appClass, "get_targetFrameRate", 0);
        s_setTargetFrameRate = Mono::FindMethod(appClass, "set_targetFrameRate", 1);
        s_getRunInBackground = Mono::FindMethod(appClass, "get_runInBackground", 0);
        s_setRunInBackground = Mono::FindMethod(appClass, "set_runInBackground", 1);
        s_getVSyncCount = Mono::FindMethod(qualityClass, "get_vSyncCount", 0);
        s_setVSyncCount = Mono::FindMethod(qualityClass, "set_vSyncCount", 1);
        return s_setCaptureDeltaTime != nullptr || s_setCaptureFramerate != nullptr;
    }

    //Mono 런타임 준비. 메인 스레드 틱마다 불러도 되며, 앱 시작 직후처럼 아직 준비 전이면 나중에 다시 시도합니다.
    bool EnsureRuntime()
    {
        if (s_monoReady)
            return true;
        if (HasStatus(ST_RUNTIME_IL2CPP))
            return false;
        //준비 전에는 30틱마다 한 번만 시도
        if ((s_initAttempts++ % 30) != 0)
            return false;
        if (Mono::Load())
        {
            if (Mono::Init() && ResolveTimeMethods())
            {
                s_monoReady = true;
                AddStatus(ST_RUNTIME_MONO);
                SetRuntimeInfo("Mono 런타임 (배속 촬영과 카메라 촬영 사용 가능)");
                return true;
            }
            if (s_initAttempts == 1)
                Log("Mono는 찾았으나 UnityEngine이 아직 준비되지 않음. 다시 시도합니다.");
            return false;
        }
        //IL2CPP: 내보낸 심벌이 없어 외부에서 안전하게 Time을 호출하기 어려움.
        //이 경우 화면(백버퍼)을 실시간(배속 없이)으로 캡처하고, 캠코더에 알립니다.
        if (GetModuleHandleW(L"GameAssembly.dll"))
        {
            AddStatus(ST_RUNTIME_IL2CPP | ST_TIME_FAILED);
            SetRuntimeInfo("IL2CPP 런타임 — 배속·카메라 촬영 불가, 화면을 실시간으로 캡처");
        }
        return false;
    }

    bool IsMonoReady() { return s_monoReady; }

    //--- 시간 제어: 메인 스레드에서만 호출할 것 ----------------------------------
    static bool s_timeApplied = false;
    static int s_savedTargetFrameRate = -1;
    static int s_savedVSyncCount = 1;
    static bool s_savedRunInBackground = false;

    //녹화 세션이 시작될 때 한 번 호출: captureDeltaTime을 1/fps로 설정
    void ApplyCaptureTiming(int fps)
    {
        if (!s_monoReady)
            return;
        if (!s_timeApplied)
        {
            //녹화가 끝나면 원래 값으로 되돌리도록 기억
            Mono::GetInt(s_getTargetFrameRate, nullptr, s_savedTargetFrameRate);
            Mono::GetInt(s_getVSyncCount, nullptr, s_savedVSyncCount);
            Mono::GetBool(s_getRunInBackground, nullptr, s_savedRunInBackground);
        }

        bool ok;
        if (s_setCaptureDeltaTime)
            ok = Mono::SetFloat(s_setCaptureDeltaTime, nullptr, fps > 0 ? 1.0f / (float)fps : 0.0f);
        else
            ok = Mono::SetInt(s_setCaptureFramerate, nullptr, fps);
        //수직 동기화와 프레임 제한을 풀어야 하드웨어 최대 속도로 프레임이 흘러 배속이 됨
        Mono::SetInt(s_setVSyncCount, nullptr, 0);
        Mono::SetInt(s_setTargetFrameRate, nullptr, -1);
        Mono::SetFloat(s_setTimeScale, nullptr, 1.0f);
        //창이 포커스를 잃어도(캠코더 창을 누르는 등) 멈추지 않고 계속 돌도록
        Mono::SetBool(s_setRunInBackground, nullptr, true);

        if (!ok)
        {
            AddStatus(ST_TIME_FAILED);
            SetLastErrorText("captureDeltaTime 설정 실패");
            return;
        }
        s_timeApplied = true;
        RemoveStatus(ST_TIME_FAILED);
        AddStatus(ST_TIME_CONTROL);
        Log("배속 촬영 적용: 프레임당 1/%d초, vSync 끔, 프레임 제한 해제, 백그라운드 실행", fps);
    }

    //녹화가 끝나거나 캠코더가 죽으면 원래대로 되돌림
    void RestoreTiming()
    {
        if (!s_timeApplied || !s_monoReady)
            return;
        if (s_setCaptureDeltaTime)
            Mono::SetFloat(s_setCaptureDeltaTime, nullptr, 0.0f);   // 0 = 실시간으로 복귀
        else
            Mono::SetInt(s_setCaptureFramerate, nullptr, 0);
        Mono::SetInt(s_setVSyncCount, nullptr, s_savedVSyncCount);
        Mono::SetInt(s_setTargetFrameRate, nullptr, s_savedTargetFrameRate);
        Mono::SetBool(s_setRunInBackground, nullptr, s_savedRunInBackground);
        s_timeApplied = false;
        RemoveStatus(ST_TIME_CONTROL);
        Log("배속 촬영 해제 (실시간으로 복귀, 원래 설정 복원)");
    }

    bool IsTimingApplied() { return s_timeApplied; }
}
