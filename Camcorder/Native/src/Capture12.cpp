#include "Capture.h"
#include "MinHook.h"
#include <d3d12.h>
#include <dxgi1_4.h>
#include <wrl/client.h>

using Microsoft::WRL::ComPtr;

//D3D12 백버퍼 캡처.
//D3D12는 스왑체인에서 큐를 얻을 수 없으므로, ID3D12CommandQueue::ExecuteCommandLists를 후킹해
//유니티가 Present에 쓰는 그래픽스 큐를 포착해 둡니다. 그 큐로 백버퍼를 리드백 버퍼에 복사합니다.
namespace Camcorder
{
    static ComPtr<ID3D12CommandQueue> s_capturedQueue;   //포착한 그래픽스 큐

    typedef void (STDMETHODCALLTYPE* ExecuteCommandLists_t)(ID3D12CommandQueue*, UINT, ID3D12CommandList* const*);
    static ExecuteCommandLists_t s_originalExecuteCommandLists = nullptr;

    static void STDMETHODCALLTYPE ExecuteCommandLists_Detour(ID3D12CommandQueue* queue, UINT count, ID3D12CommandList* const* lists)
    {
        if (!s_capturedQueue && queue->GetDesc().Type == D3D12_COMMAND_LIST_TYPE_DIRECT)
        {
            s_capturedQueue = queue;
            Log("D3D12 그래픽스 큐 포착됨");
        }
        s_originalExecuteCommandLists(queue, count, lists);
    }

    void TryInstallD3D12QueueHook()
    {
        if (s_originalExecuteCommandLists)
            return;
        HMODULE d3d12 = GetModuleHandleW(L"d3d12.dll");
        if (!d3d12)
            return;
        auto createDevice = (PFN_D3D12_CREATE_DEVICE)GetProcAddress(d3d12, "D3D12CreateDevice");
        if (!createDevice)
            return;

        //vtable 주소를 알아내기 위해 더미 디바이스/큐를 하나 만듭니다.
        ComPtr<ID3D12Device> device;
        if (FAILED(createDevice(nullptr, D3D_FEATURE_LEVEL_11_0, __uuidof(ID3D12Device), (void**)&device)))
            return;
        D3D12_COMMAND_QUEUE_DESC qd = {};
        qd.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
        ComPtr<ID3D12CommandQueue> queue;
        if (FAILED(device->CreateCommandQueue(&qd, __uuidof(ID3D12CommandQueue), (void**)&queue)))
            return;

        void** vtable = *(void***)queue.Get();
        //ID3D12CommandQueue::ExecuteCommandLists 는 vtable 인덱스 10
        void* target = vtable[10];
        if (MH_CreateHook(target, (void*)ExecuteCommandLists_Detour, (void**)&s_originalExecuteCommandLists) == MH_OK &&
            MH_EnableHook(target) == MH_OK)
        {
            Log("D3D12 ExecuteCommandLists 후킹 설치 완료");
        }
    }

    class D3D12Capturer : public ICapturer
    {
    public:
        explicit D3D12Capturer(ID3D12Device* device) : m_device(device)
        {
            m_device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, __uuidof(ID3D12CommandAllocator), (void**)&m_allocator);
            m_device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, m_allocator.Get(), nullptr, __uuidof(ID3D12GraphicsCommandList), (void**)&m_list);
            m_list->Close();
            m_device->CreateFence(0, D3D12_FENCE_FLAG_NONE, __uuidof(ID3D12Fence), (void**)&m_fence);
            m_fenceEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        }

        ~D3D12Capturer() override
        {
            if (m_fenceEvent) CloseHandle(m_fenceEvent);
        }

        int GraphicsApi() const override { return API_D3D12; }

        void OnResize() override
        {
            m_readback.Reset();
            m_rbSize = 0;
        }

        bool Capture(IDXGISwapChain* swapChain, FrameRing& ring) override
        {
            if (!s_capturedQueue || !m_list || !m_fence)
                return false;
            ComPtr<IDXGISwapChain3> sc3;
            if (FAILED(swapChain->QueryInterface(__uuidof(IDXGISwapChain3), (void**)&sc3)))
                return false;
            UINT index = sc3->GetCurrentBackBufferIndex();
            ComPtr<ID3D12Resource> backBuffer;
            if (FAILED(sc3->GetBuffer(index, __uuidof(ID3D12Resource), (void**)&backBuffer)))
                return false;

            D3D12_RESOURCE_DESC desc = backBuffer->GetDesc();
            int bpp = CamcorderBytesPerPixel((int)desc.Format);
            if (bpp == 0)
            {
                SetLastErrorText("지원하지 않는 D3D12 백버퍼 형식: %d", (int)desc.Format);
                return false;
            }

            D3D12_PLACED_SUBRESOURCE_FOOTPRINT footprint;
            UINT64 totalBytes = 0, rowBytes = 0;
            UINT numRows = 0;
            m_device->GetCopyableFootprints(&desc, 0, 1, 0, &footprint, &numRows, &rowBytes, &totalBytes);
            if (!EnsureReadback(totalBytes))
                return false;

            m_allocator->Reset();
            m_list->Reset(m_allocator.Get(), nullptr);

            //PRESENT → COPY_SOURCE 로 전이 후 복사, 다시 PRESENT 로 복귀
            D3D12_RESOURCE_BARRIER barrier = {};
            barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
            barrier.Transition.pResource = backBuffer.Get();
            barrier.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
            barrier.Transition.StateBefore = D3D12_RESOURCE_STATE_PRESENT;
            barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_COPY_SOURCE;
            m_list->ResourceBarrier(1, &barrier);

            D3D12_TEXTURE_COPY_LOCATION dst = {};
            dst.pResource = m_readback.Get();
            dst.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
            dst.PlacedFootprint = footprint;
            D3D12_TEXTURE_COPY_LOCATION srcLoc = {};
            srcLoc.pResource = backBuffer.Get();
            srcLoc.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
            srcLoc.SubresourceIndex = 0;
            m_list->CopyTextureRegion(&dst, 0, 0, 0, &srcLoc, nullptr);

            barrier.Transition.StateBefore = D3D12_RESOURCE_STATE_COPY_SOURCE;
            barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_PRESENT;
            m_list->ResourceBarrier(1, &barrier);
            m_list->Close();

            ID3D12CommandList* lists[] = { m_list.Get() };
            s_capturedQueue->ExecuteCommandLists(1, lists);
            const UINT64 fenceValue = ++m_fenceValue;
            s_capturedQueue->Signal(m_fence.Get(), fenceValue);
            if (m_fence->GetCompletedValue() < fenceValue)
            {
                m_fence->SetEventOnCompletion(fenceValue, m_fenceEvent);
                WaitForSingleObject(m_fenceEvent, 2000);
            }

            void* mapped = nullptr;
            D3D12_RANGE readRange = { 0, (SIZE_T)totalBytes };
            if (FAILED(m_readback->Map(0, &readRange, &mapped)))
                return false;

            int slot; uint8_t* destPtr; int64_t capacity;
            bool wrote = false;
            if (ring.BeginWrite(slot, destPtr, capacity))
            {
                int64_t outRow = (int64_t)desc.Width * bpp;
                if (outRow * desc.Height <= capacity)
                {
                    const uint8_t* src = (const uint8_t*)mapped + footprint.Offset;
                    for (UINT y = 0; y < desc.Height; y++)
                        memcpy(destPtr + y * outRow, src + (size_t)y * footprint.Footprint.RowPitch, (size_t)outRow);
                    ring.EndWrite(slot, (int)desc.Width, (int)desc.Height, (int)desc.Format, bpp, outRow * desc.Height);
                    wrote = true;
                }
            }
            D3D12_RANGE empty = { 0, 0 };
            m_readback->Unmap(0, &empty);
            return wrote;
        }

    private:
        bool EnsureReadback(UINT64 size)
        {
            if (m_readback && m_rbSize >= size)
                return true;
            m_readback.Reset();
            D3D12_HEAP_PROPERTIES heap = {};
            heap.Type = D3D12_HEAP_TYPE_READBACK;
            D3D12_RESOURCE_DESC rd = {};
            rd.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
            rd.Width = size;
            rd.Height = 1;
            rd.DepthOrArraySize = 1;
            rd.MipLevels = 1;
            rd.Format = DXGI_FORMAT_UNKNOWN;
            rd.SampleDesc.Count = 1;
            rd.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
            if (FAILED(m_device->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &rd,
                D3D12_RESOURCE_STATE_COPY_DEST, nullptr, __uuidof(ID3D12Resource), (void**)&m_readback)))
            {
                SetLastErrorText("D3D12 리드백 버퍼 생성 실패");
                return false;
            }
            m_rbSize = size;
            return true;
        }

        ComPtr<ID3D12Device> m_device;
        ComPtr<ID3D12CommandAllocator> m_allocator;
        ComPtr<ID3D12GraphicsCommandList> m_list;
        ComPtr<ID3D12Fence> m_fence;
        ComPtr<ID3D12Resource> m_readback;
        HANDLE m_fenceEvent = nullptr;
        UINT64 m_fenceValue = 0;
        UINT64 m_rbSize = 0;
    };

    ICapturer* CreateD3D12Capturer(IDXGISwapChain* swapChain)
    {
        ComPtr<ID3D12Device> device;
        if (FAILED(swapChain->GetDevice(__uuidof(ID3D12Device), (void**)&device)) || !device)
            return nullptr;
        return new D3D12Capturer(device.Get());
    }
}
