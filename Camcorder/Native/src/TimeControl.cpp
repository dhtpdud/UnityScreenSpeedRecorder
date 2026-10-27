#include "Common.h"
#include "MinHook.h"

//유니티 메인 스레드에서 Time.captureFramerate(캡처 고정 프레임레이트)를 켜고 끄는 부분입니다.
//원본 프로젝트의 RecorderManager.CapturingSetting()이 하던 일을, 외부에서 Mono/IL2CPP 런타임을 통해 합니다.
//핵심: Time.captureDeltaTime = 1/fps 로 두면, 게임의 Time.deltaTime이 실제 경과 시간과 무관하게 1/fps로 고정됩니다.
//그러면 하드웨어가 허락하는 한 최대 속도로 프레임이 흘러가지만, 각 프레임이 "1/fps만큼 지났다"고 여기므로
//결과 영상을 fps로 재생하면 정상 속도로 보입니다. 이것이 배속 촬영입니다.

namespace Camcorder
{
    //--- Mono 런타임 (Unity 2018~ Mono 백엔드) -----------------------------------
    typedef void* MonoDomain;
    typedef void* MonoAssembly;
    typedef void* MonoImage;
    typedef void* MonoClass;
    typedef void* MonoMethod;
    typedef void* MonoObject;
    typedef void* MonoThread;

    typedef MonoDomain (*mono_get_root_domain_t)();
    typedef MonoThread (*mono_thread_attach_t)(MonoDomain);
    typedef void (*mono_thread_detach_t)(MonoThread);
    typedef MonoImage (*mono_get_corlib_t)();     //사용 안 함(참고용)
    typedef MonoAssembly (*mono_domain_assembly_open_t)(MonoDomain, const char*);
    typedef void (*mono_assembly_foreach_t)(void (*)(void*, void*), void*);
    typedef MonoImage (*mono_assembly_get_image_t)(MonoAssembly);
    typedef const char* (*mono_assembly_name_get_name_t)(void*);
    typedef void* (*mono_assembly_get_name_t)(MonoAssembly);
    typedef MonoClass (*mono_class_from_name_t)(MonoImage, const char*, const char*);
    typedef MonoMethod (*mono_class_get_method_from_name_t)(MonoClass, const char*, int);
    typedef MonoObject (*mono_runtime_invoke_t)(MonoMethod, void*, void**, MonoObject*);

    struct MonoApi
    {
        HMODULE module = nullptr;
        mono_get_root_domain_t get_root_domain = nullptr;
        mono_thread_attach_t thread_attach = nullptr;
        mono_domain_assembly_open_t domain_assembly_open = nullptr;
        mono_assembly_get_image_t assembly_get_image = nullptr;
        mono_class_from_name_t class_from_name = nullptr;
        mono_class_get_method_from_name_t class_get_method_from_name = nullptr;
        mono_runtime_invoke_t runtime_invoke = nullptr;
        bool valid() const { return get_root_domain && domain_assembly_open && assembly_get_image && class_from_name && class_get_method_from_name && runtime_invoke; }
    };

    static MonoApi s_mono;
    static MonoMethod s_setCaptureDeltaTime = nullptr;   // UnityEngine.Time::set_captureDeltaTime(float)
    static MonoMethod s_getCaptureDeltaTime = nullptr;
    static MonoMethod s_setTimeScale = nullptr;          // UnityEngine.Time::set_timeScale(float)
    static MonoMethod s_setTargetFrameRate = nullptr;    // UnityEngine.Application::set_targetFrameRate(int)
    static MonoMethod s_setVSyncCount = nullptr;         // UnityEngine.QualitySettings::set_vSyncCount(int)
    static bool s_monoReady = false;
    static bool s_monoAttached = false;

    template <typename T>
    static T GetProc(HMODULE module, const char* name)
    {
        return reinterpret_cast<T>(GetProcAddress(module, name));
    }

    static HMODULE FindMonoModule()
    {
        //Unity Mono 백엔드는 mono-2.0-bdwgc.dll(최근) 또는 mono.dll(구버전)로 들어옴
        const wchar_t* names[] = { L"mono-2.0-bdwgc.dll", L"mono.dll", L"monobdwgc-2.0.dll" };
        for (const wchar_t* n : names)
        {
            HMODULE m = GetModuleHandleW(n);
            if (m)
                return m;
        }
        return nullptr;
    }

    static bool LoadMonoApi()
    {
        HMODULE module = FindMonoModule();
        if (!module)
            return false;
        s_mono.module = module;
        s_mono.get_root_domain = GetProc<mono_get_root_domain_t>(module, "mono_get_root_domain");
        s_mono.thread_attach = GetProc<mono_thread_attach_t>(module, "mono_thread_attach");
        s_mono.domain_assembly_open = GetProc<mono_domain_assembly_open_t>(module, "mono_domain_assembly_open");
        s_mono.assembly_get_image = GetProc<mono_assembly_get_image_t>(module, "mono_assembly_get_image");
        s_mono.class_from_name = GetProc<mono_class_from_name_t>(module, "mono_class_from_name");
        s_mono.class_get_method_from_name = GetProc<mono_class_get_method_from_name_t>(module, "mono_class_get_method_from_name");
        s_mono.runtime_invoke = GetProc<mono_runtime_invoke_t>(module, "mono_runtime_invoke");
        return s_mono.valid();
    }

    static bool ResolveMonoMethods()
    {
        if (s_mono.get_root_domain() == nullptr)
            return false;
        if (!s_monoAttached)
        {
            if (s_mono.thread_attach)
                s_mono.thread_attach(s_mono.get_root_domain());
            s_monoAttached = true;
        }
        MonoAssembly assembly = s_mono.domain_assembly_open(s_mono.get_root_domain(), "UnityEngine.CoreModule");
        if (!assembly)
            assembly = s_mono.domain_assembly_open(s_mono.get_root_domain(), "UnityEngine");
        if (!assembly)
            return false;
        MonoImage image = s_mono.assembly_get_image(assembly);
        if (!image)
            return false;

        MonoClass timeClass = s_mono.class_from_name(image, "UnityEngine", "Time");
        MonoClass appClass = s_mono.class_from_name(image, "UnityEngine", "Application");
        MonoClass qualityClass = s_mono.class_from_name(image, "UnityEngine", "QualitySettings");
        if (!timeClass)
            return false;
        s_setCaptureDeltaTime = s_mono.class_get_method_from_name(timeClass, "set_captureDeltaTime", 1);
        s_getCaptureDeltaTime = s_mono.class_get_method_from_name(timeClass, "get_captureDeltaTime", 0);
        s_setTimeScale = s_mono.class_get_method_from_name(timeClass, "set_timeScale", 1);
        if (appClass)
            s_setTargetFrameRate = s_mono.class_get_method_from_name(appClass, "set_targetFrameRate", 1);
        if (qualityClass)
            s_setVSyncCount = s_mono.class_get_method_from_name(qualityClass, "set_vSyncCount", 1);
        return s_setCaptureDeltaTime != nullptr;
    }

    static void MonoSetFloat(MonoMethod method, float value)
    {
        if (!method)
            return;
        void* args[1] = { &value };
        MonoObject ex = nullptr;
        s_mono.runtime_invoke(method, nullptr, args, &ex);
    }

    static void MonoSetInt(MonoMethod method, int value)
    {
        if (!method)
            return;
        void* args[1] = { &value };
        MonoObject ex = nullptr;
        s_mono.runtime_invoke(method, nullptr, args, &ex);
    }

    //--- 공개 진입점: 메인 스레드에서만 호출할 것 -------------------------------
    static bool s_timeApplied = false;
    static int s_appliedSession = -1;

    static bool EnsureRuntime()
    {
        if (s_monoReady)
            return true;
        if (LoadMonoApi())
        {
            if (ResolveMonoMethods())
            {
                s_monoReady = true;
                AddStatus(ST_RUNTIME_MONO);
                SetRuntimeInfo("Mono 런타임 (captureDeltaTime 사용 가능)");
                return true;
            }
            SetLastErrorText("Mono는 찾았으나 Time 메서드를 못 찾음");
            return false;
        }
        //IL2CPP: 내보낸 심벌이 없어 외부에서 안전하게 Time을 호출하기 어려움.
        //이 경우 실시간(배속 없이) 캡처로 넘어가고, 캠코더에 알립니다.
        if (!HasStatus(ST_RUNTIME_IL2CPP) && GetModuleHandleW(L"GameAssembly.dll"))
        {
            AddStatus(ST_RUNTIME_IL2CPP | ST_TIME_FAILED);
            SetRuntimeInfo("IL2CPP 런타임 — 배속 제어 불가, 실시간 캡처로 진행");
        }
        return false;
    }

    //녹화 세션이 시작될 때 한 번 호출: captureDeltaTime을 1/fps로 설정
    void ApplyCaptureTiming(int fps)
    {
        if (!EnsureRuntime())
        {
            if (g_shared)
                g_shared->appliedSessionId = g_shared->sessionId;
            return;
        }
        float dt = fps > 0 ? 1.0f / (float)fps : 0.0f;
        MonoSetFloat(s_setCaptureDeltaTime, dt);
        MonoSetInt(s_setVSyncCount, 0);
        MonoSetFloat(s_setTimeScale, 1.0f);
        //targetFrameRate는 풀어 둬야 하드웨어 최대 속도로 프레임이 흘러 배속이 됨
        MonoSetInt(s_setTargetFrameRate, -1);
        s_timeApplied = true;
        AddStatus(ST_TIME_CONTROL);
        if (g_shared)
            g_shared->appliedSessionId = g_shared->sessionId;
        Log("captureDeltaTime 적용: 1/%d초", fps);
    }

    //녹화가 끝나거나 캠코더가 죽으면 원래대로 되돌림
    void RestoreTiming()
    {
        if (!s_timeApplied || !s_monoReady)
            return;
        MonoSetFloat(s_setCaptureDeltaTime, 0.0f);   // 0 = 실시간으로 복귀
        MonoSetInt(s_setVSyncCount, 1);
        s_timeApplied = false;
        RemoveStatus(ST_TIME_CONTROL);
        Log("captureDeltaTime 복원 (실시간)");
    }

    bool IsTimingApplied() { return s_timeApplied; }
}
