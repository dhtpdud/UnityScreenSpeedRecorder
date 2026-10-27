#pragma once
#include "Common.h"

//캡처한 프레임을 캠코더로 넘기는 공유 메모리 링 버퍼입니다.
//DLL이 생산자(Present 훅), 캠코더가 소비자(읽어서 ffmpeg로 보냄)입니다.
namespace Camcorder
{
    class FrameRing
    {
    public:
        //녹화 세션 시작 시 호출. 캠코더가 만들어 둔 Frames 공유 메모리를 엽니다.
        bool Open(int sessionId, int width, int height, int bytesPerPixel);
        void Close();
        bool IsOpen() const { return m_base != nullptr; }

        int Width() const { return m_width; }
        int Height() const { return m_height; }
        int SessionId() const { return m_sessionId; }

        //빈 슬롯을 하나 얻어 픽셀을 복사하도록 포인터와 슬롯 번호를 돌려줍니다.
        //빈 슬롯이 없으면(캠코더가 못 따라오면) false. 이때 Present 훅은 프레임을 버립니다.
        bool BeginWrite(int& slotIndex, uint8_t*& dest, int64_t& capacity);
        //복사를 마치고 캠코더에 알립니다.
        void EndWrite(int slotIndex, int width, int height, int dxgiFormat, int bytesPerPixel, int64_t dataSize);

    private:
        HANDLE m_mapping = nullptr;
        HANDLE m_frameReady = nullptr;  //DLL → 캠코더: 새 프레임 있음
        HANDLE m_slotFree = nullptr;    //캠코더 → DLL: 슬롯 비었음
        uint8_t* m_base = nullptr;      //슬롯 데이터 영역 시작
        int m_sessionId = -1;
        int m_width = 0;
        int m_height = 0;
        int m_bytesPerPixel = 0;
        int m_slotCount = 0;
        int64_t m_slotSize = 0;
        int64_t m_nextFrame = 0;
    };
}
