#include "FrameBuffer.h"

namespace Camcorder
{
    //프레임 공유 메모리 레이아웃: [CamcorderShared 제어 블록을 공유하지 않고]
    //캠코더가 slotCount * slotSize 크기의 별도 매핑을 만들어 둡니다.
    //슬롯 메타(가로/세로/형식)는 제어 블록의 slots[]에 기록합니다.

    bool FrameRing::Open(int sessionId, int width, int height, int bytesPerPixel)
    {
        Close();
        if (!g_shared)
            return false;

        m_slotCount = g_shared->slotCount > 0 ? g_shared->slotCount : 8;
        if (m_slotCount > CAMCORDER_MAX_SLOTS)
            m_slotCount = CAMCORDER_MAX_SLOTS;
        m_slotSize = g_shared->slotSize;
        int64_t needed = (int64_t)width * height * bytesPerPixel;
        if (m_slotSize < needed)
        {
            SetLastErrorText("프레임 슬롯이 너무 작음 (%lld < %lld)", (long long)m_slotSize, (long long)needed);
            return false;
        }

        wchar_t name[160];
        swprintf_s(name, CAMCORDER_FRAMES_NAME, GetCurrentProcessId(), sessionId);
        m_mapping = OpenFileMappingW(FILE_MAP_ALL_ACCESS, FALSE, name);
        if (!m_mapping)
        {
            SetLastErrorText("프레임 공유 메모리를 열 수 없음 (session %d, err %lu)", sessionId, GetLastError());
            return false;
        }
        m_base = (uint8_t*)MapViewOfFile(m_mapping, FILE_MAP_ALL_ACCESS, 0, 0, (SIZE_T)(m_slotSize * m_slotCount));
        if (!m_base)
        {
            SetLastErrorText("프레임 공유 메모리 매핑 실패");
            Close();
            return false;
        }

        swprintf_s(name, CAMCORDER_FRAME_READY_NAME, GetCurrentProcessId());
        m_frameReady = OpenEventW(EVENT_MODIFY_STATE | SYNCHRONIZE, FALSE, name);
        swprintf_s(name, CAMCORDER_SLOT_FREE_NAME, GetCurrentProcessId());
        m_slotFree = OpenEventW(EVENT_MODIFY_STATE | SYNCHRONIZE, FALSE, name);

        m_sessionId = sessionId;
        m_width = width;
        m_height = height;
        m_bytesPerPixel = bytesPerPixel;
        m_nextFrame = 0;
        Log("프레임 링 버퍼 열림: session %d, %dx%d, 슬롯 %d개 x %lld바이트", sessionId, width, height, m_slotCount, (long long)m_slotSize);
        return true;
    }

    void FrameRing::Close()
    {
        if (m_base) { UnmapViewOfFile(m_base); m_base = nullptr; }
        if (m_mapping) { CloseHandle(m_mapping); m_mapping = nullptr; }
        if (m_frameReady) { CloseHandle(m_frameReady); m_frameReady = nullptr; }
        if (m_slotFree) { CloseHandle(m_slotFree); m_slotFree = nullptr; }
        m_sessionId = -1;
    }

    bool FrameRing::BeginWrite(int& slotIndex, uint8_t*& dest, int64_t& capacity)
    {
        if (!g_shared || !m_base)
            return false;
        //링 버퍼에 빈 슬롯이 있는가? write - read < slotCount 이면 OK
        int64_t inFlight = g_shared->writeCount - g_shared->readCount;
        if (inFlight >= m_slotCount)
            return false;   //캠코더가 못 따라옴 → 이번 프레임은 버림
        slotIndex = (int)(g_shared->writeCount % m_slotCount);
        dest = m_base + (int64_t)slotIndex * m_slotSize;
        capacity = m_slotSize;
        return true;
    }

    void FrameRing::EndWrite(int slotIndex, int width, int height, int dxgiFormat, int bytesPerPixel, int64_t dataSize)
    {
        if (!g_shared)
            return;
        CamcorderSlot& slot = g_shared->slots[slotIndex];
        slot.width = width;
        slot.height = height;
        slot.dxgiFormat = dxgiFormat;
        slot.bytesPerPixel = bytesPerPixel;
        slot.dataSize = dataSize;
        slot.frameIndex = m_nextFrame++;
        //쓰기 카운터를 올린 뒤 캠코더를 깨움 (읽기 쪽은 writeCount를 보고 슬롯을 집음)
        InterlockedIncrement64(&g_shared->writeCount);
        if (m_frameReady)
            SetEvent(m_frameReady);
    }
}
