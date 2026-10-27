#pragma once
#include "Common.h"
#include "FrameBuffer.h"
#include <dxgi.h>

//그래픽스 API별 프레임 캡처기의 공통 인터페이스.
//Present 훅이 백버퍼를 받아 CPU로 읽어 와 FrameRing에 복사합니다.
namespace Camcorder
{
    class ICapturer
    {
    public:
        virtual ~ICapturer() {}
        //스왑체인 하나에서 한 프레임을 캡처해 ring에 씁니다. 성공 시 true.
        virtual bool Capture(IDXGISwapChain* swapChain, FrameRing& ring) = 0;
        //스왑체인 크기가 바뀌었을 때 내부 스테이징 리소스를 버립니다.
        virtual void OnResize() = 0;
        virtual int GraphicsApi() const = 0;
    };

    //D3D11 디바이스에서 만들어진 스왑체인이면 캡처기를 돌려줍니다. 아니면 nullptr.
    ICapturer* CreateD3D11Capturer(IDXGISwapChain* swapChain);
    //D3D12 큐가 포착되어 있고 이 스왑체인이 D3D12이면 캡처기를 돌려줍니다.
    ICapturer* CreateD3D12Capturer(IDXGISwapChain* swapChain);

    //D3D12 커맨드 큐 포착을 위한 ExecuteCommandLists 훅 (D3D12 디바이스가 있을 때만 설치)
    void TryInstallD3D12QueueHook();
}
