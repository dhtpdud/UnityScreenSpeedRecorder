#include "Capture.h"
#include "MinHook.h"
#include <d3d11.h>
#include <dxgi1_2.h>
#include <wrl/client.h>

using Microsoft::WRL::ComPtr;

//IDXGISwapChain::Present / Present1 / ResizeBuffers 를 후킹합니다.
//유니티가 프레임을 화면에 내보낼 때(Present) 백버퍼를 읽어 FrameRing에 넣습니다.
//스왑체인을 처음 보면 D3D11/D3D12 중 맞는 캡처기를 만듭니다.
namespace Camcorder
{
    void TryInstallD3D12QueueHook();

    typedef HRESULT (STDMETHODCALLTYPE* Present_t)(IDXGISwapChain*, UINT, UINT);
    typedef HRESULT (STDMETHODCALLTYPE* Present1_t)(IDXGISwapChain1*, UINT, UINT, const DXGI_PRESENT_PARAMETERS*);
    typedef HRESULT (STDMETHODCALLTYPE* ResizeBuffers_t)(IDXGISwapChain*, UINT, UINT, UINT, DXGI_FORMAT, UINT);

    static Present_t s_originalPresent = nullptr;
    static Present1_t s_originalPresent1 = nullptr;
    static ResizeBuffers_t s_originalResizeBuffers = nullptr;

    static ICapturer* s_capturer = nullptr;
    static FrameRing s_ring;
    static int s_activeSession = -1;
    static CRITICAL_SECTION s_presentLock;
    static bool s_presentLockReady = false;

    static ICapturer* EnsureCapturer(IDXGISwapChain* swapChain)
    {
        if (s_capturer)
            return s_capturer;
        s_capturer = CreateD3D11Capturer(swapChain);
        if (!s_capturer)
            s_capturer = CreateD3D12Capturer(swapChain);
        if (s_capturer)
        {
            g_shared->graphicsApi = s_capturer->GraphicsApi();
            Log("캡처기 생성: %s", s_capturer->GraphicsApi() == API_D3D11 ? "D3D11" : "D3D12");
        }
        return s_capturer;
    }

    //녹화 세션 전환 처리: command/sessionId를 보고 링 버퍼를 열거나 닫음
    static void UpdateSession(IDXGISwapChain* swapChain)
    {
        int session = g_shared ? g_shared->sessionId : 0;
        //메인 스레드가 이 세션을 처리한 뒤(배속 적용, 촬영 방식 결정)에만, 그리고 카메라 촬영이 아닐 때만 화면을 캡처
        bool want = IsRecordRequested() && g_shared->appliedSessionId == session && g_cameraCaptureSession != session;

        if (want && (!s_ring.IsOpen() || session != s_activeSession))
        {
            //새 세션: 백버퍼 크기를 확인해 링 버퍼를 엶
            DXGI_SWAP_CHAIN_DESC scDesc;
            if (FAILED(swapChain->GetDesc(&scDesc)))
                return;
            int bpp = CamcorderBytesPerPixel((int)scDesc.BufferDesc.Format);
            if (bpp == 0)
                bpp = 4;
            g_shared->backbufferWidth = (int)scDesc.BufferDesc.Width;
            g_shared->backbufferHeight = (int)scDesc.BufferDesc.Height;
            g_shared->backbufferFormat = (int)scDesc.BufferDesc.Format;
            s_ring.Close();
            if (s_ring.Open(session, (int)scDesc.BufferDesc.Width, (int)scDesc.BufferDesc.Height, bpp))
            {
                s_activeSession = session;
                AddStatus(ST_CAPTURING);
            }
        }
        else if (!want && s_ring.IsOpen())
        {
            s_ring.Close();
            s_activeSession = -1;
            RemoveStatus(ST_CAPTURING);
        }
    }

    //idle일 때도 캠코더가 해상도를 알 수 있도록 백버퍼 크기를 매 프레임 보고
    static void ReportBackbuffer(IDXGISwapChain* swapChain)
    {
        DXGI_SWAP_CHAIN_DESC scDesc;
        if (SUCCEEDED(swapChain->GetDesc(&scDesc)))
        {
            g_shared->backbufferWidth = (int)scDesc.BufferDesc.Width;
            g_shared->backbufferHeight = (int)scDesc.BufferDesc.Height;
            g_shared->backbufferFormat = (int)scDesc.BufferDesc.Format;
        }
    }

    static void CaptureFrame(IDXGISwapChain* swapChain)
    {
        if (!g_shared)
            return;
        InterlockedIncrement64(&g_shared->presentCount);
        EnterCriticalSection(&s_presentLock);
        ReportBackbuffer(swapChain);
        UpdateSession(swapChain);
        if (s_ring.IsOpen())
        {
            ICapturer* cap = EnsureCapturer(swapChain);
            if (cap)
                cap->Capture(swapChain, s_ring);
        }
        LeaveCriticalSection(&s_presentLock);
    }

    static HRESULT STDMETHODCALLTYPE Present_Detour(IDXGISwapChain* sc, UINT sync, UINT flags)
    {
        if (!(flags & DXGI_PRESENT_TEST))
            CaptureFrame(sc);
        return s_originalPresent(sc, sync, flags);
    }

    static HRESULT STDMETHODCALLTYPE Present1_Detour(IDXGISwapChain1* sc, UINT sync, UINT flags, const DXGI_PRESENT_PARAMETERS* params)
    {
        if (!(flags & DXGI_PRESENT_TEST))
            CaptureFrame(sc);
        return s_originalPresent1(sc, sync, flags, params);
    }

    static HRESULT STDMETHODCALLTYPE ResizeBuffers_Detour(IDXGISwapChain* sc, UINT count, UINT w, UINT h, DXGI_FORMAT fmt, UINT flags)
    {
        EnterCriticalSection(&s_presentLock);
        //크기 변경 전 스테이징 리소스/링 버퍼를 비움 (백버퍼 참조를 놓아야 ResizeBuffers 성공)
        if (s_capturer)
            s_capturer->OnResize();
        s_ring.Close();
        s_activeSession = -1;
        RemoveStatus(ST_CAPTURING);
        LeaveCriticalSection(&s_presentLock);
        return s_originalResizeBuffers(sc, count, w, h, fmt, flags);
    }

    //vtable 주소를 얻기 위해 숨긴 창에 더미 D3D11 스왑체인을 만듭니다.
    static bool GetSwapChainVTable(void**& vtable, int& vtableSize)
    {
        WNDCLASSEXW wc = { sizeof(wc) };
        wc.lpfnWndProc = DefWindowProcW;
        wc.hInstance = GetModuleHandleW(nullptr);
        wc.lpszClassName = L"CamcorderDummyWnd";
        RegisterClassExW(&wc);
        HWND hwnd = CreateWindowExW(0, wc.lpszClassName, L"", WS_OVERLAPPEDWINDOW, 0, 0, 2, 2, nullptr, nullptr, wc.hInstance, nullptr);
        if (!hwnd)
            return false;

        DXGI_SWAP_CHAIN_DESC scd = {};
        scd.BufferCount = 1;
        scd.BufferDesc.Width = 2;
        scd.BufferDesc.Height = 2;
        scd.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        scd.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        scd.OutputWindow = hwnd;
        scd.SampleDesc.Count = 1;
        scd.Windowed = TRUE;
        scd.SwapEffect = DXGI_SWAP_EFFECT_DISCARD;

        ComPtr<IDXGISwapChain> swapChain;
        ComPtr<ID3D11Device> device;
        D3D_FEATURE_LEVEL level = D3D_FEATURE_LEVEL_11_0;
        HRESULT hr = D3D11CreateDeviceAndSwapChain(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0,
            &level, 1, D3D11_SDK_VERSION, &scd, &swapChain, &device, nullptr, nullptr);
        if (FAILED(hr))
            hr = D3D11CreateDeviceAndSwapChain(nullptr, D3D_DRIVER_TYPE_WARP, nullptr, 0,
                &level, 1, D3D11_SDK_VERSION, &scd, &swapChain, &device, nullptr, nullptr);
        if (FAILED(hr))
        {
            DestroyWindow(hwnd);
            return false;
        }
        //IDXGISwapChain vtable: QueryInterface..(0) ~ Present(8) ~ ResizeBuffers(13)
        vtable = *(void***)swapChain.Get();
        vtableSize = 18;
        DestroyWindow(hwnd);
        return true;
    }

    bool InstallPresentHooks()
    {
        if (!s_presentLockReady)
        {
            InitializeCriticalSection(&s_presentLock);
            s_presentLockReady = true;
        }

        void** vtable = nullptr;
        int size = 0;
        if (!GetSwapChainVTable(vtable, size))
        {
            SetLastErrorText("더미 스왑체인을 만들 수 없어 Present 주소를 못 얻음");
            return false;
        }

        void* presentTarget = vtable[8];        // IDXGISwapChain::Present
        void* resizeTarget = vtable[13];        // IDXGISwapChain::ResizeBuffers
        if (MH_CreateHook(presentTarget, (void*)Present_Detour, (void**)&s_originalPresent) != MH_OK ||
            MH_EnableHook(presentTarget) != MH_OK)
        {
            SetLastErrorText("Present 후킹 실패");
            return false;
        }
        if (MH_CreateHook(resizeTarget, (void*)ResizeBuffers_Detour, (void**)&s_originalResizeBuffers) == MH_OK)
            MH_EnableHook(resizeTarget);

        //IDXGISwapChain1::Present1 (vtable 22) 도 있으면 후킹 (플립 모델)
        if (size > 22 && vtable[22])
        {
            if (MH_CreateHook(vtable[22], (void*)Present1_Detour, (void**)&s_originalPresent1) == MH_OK)
                MH_EnableHook(vtable[22]);
        }

        //D3D12 앱이면 큐 포착 훅도 설치
        TryInstallD3D12QueueHook();
        Log("Present 후킹 설치 완료");
        return true;
    }
}
