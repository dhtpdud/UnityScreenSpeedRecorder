#include "Capture.h"
#include <d3d11.h>
#include <wrl/client.h>

using Microsoft::WRL::ComPtr;

//D3D11 백버퍼 캡처: 백버퍼 → (필요 시 리졸브) → STAGING 텍스처로 CopyResource → Map 하여 CPU에서 읽기
namespace Camcorder
{
    class D3D11Capturer : public ICapturer
    {
    public:
        explicit D3D11Capturer(ID3D11Device* device)
        {
            m_device = device;
            m_device->GetImmediateContext(&m_context);
        }

        int GraphicsApi() const override { return API_D3D11; }

        void OnResize() override
        {
            m_staging.Reset();
            m_resolved.Reset();
            m_width = m_height = 0;
        }

        bool Capture(IDXGISwapChain* swapChain, FrameRing& ring) override
        {
            ComPtr<ID3D11Texture2D> backBuffer;
            if (FAILED(swapChain->GetBuffer(0, __uuidof(ID3D11Texture2D), (void**)&backBuffer)))
                return false;
            D3D11_TEXTURE2D_DESC desc;
            backBuffer->GetDesc(&desc);

            int bpp = CamcorderBytesPerPixel((int)desc.Format);
            if (bpp == 0)
            {
                SetLastErrorText("지원하지 않는 백버퍼 형식: %d", (int)desc.Format);
                return false;
            }

            ID3D11Texture2D* source = backBuffer.Get();
            //MSAA 백버퍼면 먼저 리졸브
            if (desc.SampleDesc.Count > 1)
            {
                if (!EnsureResolved(desc))
                    return false;
                m_context->ResolveSubresource(m_resolved.Get(), 0, backBuffer.Get(), 0, desc.Format);
                source = m_resolved.Get();
            }

            if (!EnsureStaging(desc))
                return false;
            m_context->CopyResource(m_staging.Get(), source);

            D3D11_MAPPED_SUBRESOURCE mapped;
            if (FAILED(m_context->Map(m_staging.Get(), 0, D3D11_MAP_READ, 0, &mapped)))
                return false;

            int slot; uint8_t* dest; int64_t capacity;
            bool wrote = false;
            if (ring.BeginWrite(slot, dest, capacity, HasStatus(ST_TIME_CONTROL)))
            {
                int64_t rowBytes = (int64_t)desc.Width * bpp;
                if (rowBytes * desc.Height <= capacity)
                {
                    const uint8_t* src = (const uint8_t*)mapped.pData;
                    for (UINT y = 0; y < desc.Height; y++)
                        memcpy(dest + y * rowBytes, src + (size_t)y * mapped.RowPitch, (size_t)rowBytes);
                    ring.EndWrite(slot, (int)desc.Width, (int)desc.Height, (int)desc.Format, bpp, rowBytes * desc.Height);
                    wrote = true;
                }
            }
            m_context->Unmap(m_staging.Get(), 0);
            return wrote;
        }

    private:
        bool EnsureStaging(const D3D11_TEXTURE2D_DESC& src)
        {
            if (m_staging && m_width == (int)src.Width && m_height == (int)src.Height && m_format == src.Format)
                return true;
            m_staging.Reset();
            D3D11_TEXTURE2D_DESC desc = {};
            desc.Width = src.Width;
            desc.Height = src.Height;
            desc.MipLevels = 1;
            desc.ArraySize = 1;
            desc.Format = src.Format;
            desc.SampleDesc.Count = 1;
            desc.Usage = D3D11_USAGE_STAGING;
            desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
            if (FAILED(m_device->CreateTexture2D(&desc, nullptr, &m_staging)))
            {
                SetLastErrorText("D3D11 스테이징 텍스처 생성 실패");
                return false;
            }
            m_width = src.Width;
            m_height = src.Height;
            m_format = src.Format;
            return true;
        }

        bool EnsureResolved(const D3D11_TEXTURE2D_DESC& src)
        {
            if (m_resolved && m_rWidth == (int)src.Width && m_rHeight == (int)src.Height && m_rFormat == src.Format)
                return true;
            m_resolved.Reset();
            D3D11_TEXTURE2D_DESC desc = src;
            desc.SampleDesc.Count = 1;
            desc.SampleDesc.Quality = 0;
            desc.Usage = D3D11_USAGE_DEFAULT;
            desc.BindFlags = 0;
            desc.CPUAccessFlags = 0;
            desc.MiscFlags = 0;
            if (FAILED(m_device->CreateTexture2D(&desc, nullptr, &m_resolved)))
            {
                SetLastErrorText("D3D11 MSAA 리졸브 텍스처 생성 실패");
                return false;
            }
            m_rWidth = src.Width; m_rHeight = src.Height; m_rFormat = src.Format;
            return true;
        }

        ComPtr<ID3D11Device> m_device;
        ComPtr<ID3D11DeviceContext> m_context;
        ComPtr<ID3D11Texture2D> m_staging;
        ComPtr<ID3D11Texture2D> m_resolved;
        int m_width = 0, m_height = 0;
        DXGI_FORMAT m_format = DXGI_FORMAT_UNKNOWN;
        int m_rWidth = 0, m_rHeight = 0;
        DXGI_FORMAT m_rFormat = DXGI_FORMAT_UNKNOWN;
    };

    ICapturer* CreateD3D11Capturer(IDXGISwapChain* swapChain)
    {
        ComPtr<ID3D11Device> device;
        if (FAILED(swapChain->GetDevice(__uuidof(ID3D11Device), (void**)&device)) || !device)
            return nullptr;
        return new D3D11Capturer(device.Get());
    }
}
