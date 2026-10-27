#pragma once
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#ifdef __MINGW32__
#define MINGW_HAS_SECURE_API 1   //MinGW에서 swprintf_s/strncpy_s 등 _s 함수 활성화
#endif
#include <windows.h>
#include <share.h>
#include <stdio.h>
#include <string.h>
#include <wchar.h>
#include "CamcorderShared.h"

//주입 DLL 전체에서 함께 쓰는 상태
namespace Camcorder
{
    extern CamcorderShared* g_shared;   //캠코더가 만든 제어용 공유 메모리
    extern HWND g_unityWindow;          //유니티 메인 창 (UnityWndClass)
    extern DWORD g_mainThreadId;        //유니티 메인 창을 가진 스레드 = 유니티 메인 스레드

    void Log(const char* format, ...);
    void SetLastErrorText(const char* format, ...);
    void SetRuntimeInfo(const char* format, ...);
    void AddStatus(int32_t flags);
    void RemoveStatus(int32_t flags);
    bool HasStatus(int32_t flags);

    //캠코더 프로세스가 살아 있는지 (죽었으면 녹화를 멈추고 설정을 되돌림)
    bool IsControllerAlive();
    //지금 녹화 명령이 유효한지 (명령이 RECORD이고 캠코더가 살아 있음)
    bool IsRecordRequested();

    //유니티 메인 창을 찾아 g_unityWindow, g_mainThreadId를 채움
    bool FindUnityWindow();

    //메인 스레드 훅 (PeekMessage/GetMessage)
    bool InstallMainThreadHooks();
    //Present 후킹 (D3D11/D3D12)
    bool InstallPresentHooks();
}
