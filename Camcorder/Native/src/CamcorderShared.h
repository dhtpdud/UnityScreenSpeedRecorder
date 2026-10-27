#pragma once
#include <stddef.h>
#include <stdint.h>

//캠코더(UnityCamcorder.exe)와 주입 DLL(UnityCamcorderHook.dll)이 함께 쓰는 공유 메모리 구조
//C# 쪽(Controller/SharedMemory.cs)의 오프셋과 반드시 같아야 합니다.

#define CAMCORDER_MAGIC 0x524D4355u   // 'UCMR'
#define CAMCORDER_VERSION 1
#define CAMCORDER_MAX_SLOTS 64

//공유 메모리 / 이벤트 이름 (뒤에 대상 프로세스 PID가 붙음)
#define CAMCORDER_CONTROL_NAME L"Local\\UnityCamcorder_%lu"
#define CAMCORDER_FRAMES_NAME L"Local\\UnityCamcorder_%lu_Frames_%d"     //뒤의 숫자는 sessionId
#define CAMCORDER_FRAME_READY_NAME L"Local\\UnityCamcorder_%lu_FrameReady"
#define CAMCORDER_SLOT_FREE_NAME L"Local\\UnityCamcorder_%lu_SlotFree"

enum CamcorderCommand : int32_t
{
    CMD_IDLE = 0,
    CMD_RECORD = 1,
};

enum CamcorderStatus : int32_t
{
    ST_LOADED = 1 << 0,             //DLL이 로드되어 공유 메모리를 열었음
    ST_HOOKED = 1 << 1,             //Present 후킹 완료
    ST_RUNTIME_MONO = 1 << 2,       //Mono 런타임 발견
    ST_RUNTIME_IL2CPP = 1 << 3,     //IL2CPP 런타임 발견
    ST_TIME_CONTROL = 1 << 4,       //captureFramerate(captureDeltaTime) 적용 성공
    ST_TIME_FAILED = 1 << 5,        //시간 제어 실패 (실시간 캡처로 진행)
    ST_CAPTURING = 1 << 6,          //프레임 캡처 중
    ST_MAIN_THREAD = 1 << 7,        //메인 스레드 훅이 한 번 이상 실행됨
};

enum CamcorderGraphicsApi : int32_t
{
    API_UNKNOWN = 0,
    API_D3D11 = 11,
    API_D3D12 = 12,
};

#pragma pack(push, 8)
struct CamcorderSlot
{
    int32_t width;
    int32_t height;
    int32_t dxgiFormat;
    int32_t bytesPerPixel;
    int64_t frameIndex;
    int64_t dataSize;
};

struct CamcorderShared
{
    // 캠코더 → DLL
    int32_t magic;                  // 0
    int32_t version;                // 4
    int32_t controllerPid;          // 8   캠코더가 죽으면 DLL이 스스로 녹화를 멈추고 설정을 되돌림
    int32_t sessionId;              // 12  녹화(세그먼트)를 시작할 때마다 1씩 증가
    int32_t command;                // 16  CamcorderCommand
    int32_t fps;                    // 20  결과 영상 fps = captureFramerate
    int32_t speedLimitPermille;     // 24  최대 배속 x1000 (0이면 제한 없음)
    int32_t slotCount;              // 28
    int64_t slotSize;               // 32
    int32_t recordWidth;            // 40
    int32_t recordHeight;           // 44
    int32_t skipFrames;             // 48  시간 제어 적용 직후 버릴 프레임 수
    int32_t reserved0;              // 52
    // DLL → 캠코더
    int32_t status;                 // 56  CamcorderStatus 플래그
    int32_t payloadVersion;         // 60
    int64_t presentCount;           // 64
    int32_t backbufferWidth;        // 72
    int32_t backbufferHeight;       // 76
    int32_t backbufferFormat;       // 80  DXGI_FORMAT
    int32_t graphicsApi;            // 84  CamcorderGraphicsApi
    int32_t appliedSessionId;       // 88  메인 스레드가 시간 제어를 처리한 sessionId
    int32_t mainThreadId;           // 92
    int64_t writeCount;             // 96  DLL이 링 버퍼에 쓴 프레임 수 (누적)
    int64_t readCount;              // 104 캠코더가 읽어 간 프레임 수 (누적)
    int64_t reserved1;              // 112
    char runtimeInfo[256];          // 120
    char lastError[512];            // 376
    CamcorderSlot slots[CAMCORDER_MAX_SLOTS]; // 888
};
#pragma pack(pop)

static_assert(sizeof(CamcorderSlot) == 32, "CamcorderSlot size");
static_assert(offsetof(CamcorderShared, status) == 56, "status offset");
static_assert(offsetof(CamcorderShared, presentCount) == 64, "presentCount offset");
static_assert(offsetof(CamcorderShared, writeCount) == 96, "writeCount offset");
static_assert(offsetof(CamcorderShared, runtimeInfo) == 120, "runtimeInfo offset");
static_assert(offsetof(CamcorderShared, lastError) == 376, "lastError offset");
static_assert(offsetof(CamcorderShared, slots) == 888, "slots offset");
static_assert(sizeof(CamcorderShared) == 888 + 32 * CAMCORDER_MAX_SLOTS, "CamcorderShared size");

//DXGI_FORMAT → 픽셀당 바이트 수 (지원하지 않는 형식은 0)
inline int CamcorderBytesPerPixel(int dxgiFormat)
{
    switch (dxgiFormat)
    {
    case 27: case 28: case 29:      // R8G8B8A8 TYPELESS/UNORM/UNORM_SRGB
    case 87: case 90: case 91:      // B8G8R8A8 UNORM/TYPELESS/UNORM_SRGB
    case 88: case 92: case 93:      // B8G8R8X8 UNORM/TYPELESS/UNORM_SRGB
    case 23: case 24:               // R10G10B10A2 TYPELESS/UNORM
        return 4;
    case 9: case 10:                // R16G16B16A16 TYPELESS/FLOAT
        return 8;
    default:
        return 0;
    }
}
