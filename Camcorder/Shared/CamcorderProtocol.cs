using System;

// 캠코더(UnityCamcorder.exe)와 BepInEx 플러그인(UnityCamcorderPlugin.dll)이 함께 쓰는 공유 메모리 규약입니다.
// 두 프로젝트가 이 파일 하나를 같이 컴파일하므로 오프셋이 서로 어긋날 수 없습니다.
// 플러그인은 대상 앱 안의 Mono(.NET 3.5)에서도 돌아야 하므로 이 파일에는 상수와 간단한 함수만 둡니다.
//
// 흐름: 캠코더가 제어 블록(Local\UnityCamcorder_<대상PID>)과 이벤트 2개를 만들고, 앱 안의 플러그인이
// 매 프레임 그것을 찾아 엽니다. 녹화할 때는 캠코더가 프레임 링 버퍼(..._Frames_<session>)를 만들고
// 플러그인이 카메라 출력을 거기에 쓰면 캠코더가 읽어 ffmpeg로 넘깁니다.
namespace UnityCamcorder.Protocol
{
    public static class Shared
    {
        public const uint Magic = 0x524D4355u;   // 'UCMR'
        // 3: 메인 스레드 틱을 유니티 이벤트로 (주입 DLL), 4: BepInEx 플러그인, 5: 속도 진단(플러그인이 기다린 시간)
        // magic(0), version(4), payloadVersion(60) 세 자리는 버전이 달라도 서로 알아볼 수 있도록 앞으로도 옮기지 않습니다.
        public const int Version = 5;

        public const int MaxSlots = 64;
        public const int MaxCameras = 64;
        public const int MaxStreams = 16;
        public const int SlotStride = 48;
        public const int CameraStride = 128;
        public const int CameraNameBytes = 104;
        public const int RuntimeInfoBytes = 256;
        public const int LastErrorBytes = 512;
        public const int StatsBytes = 64;
        public const int ControlSize = Offsets.Stats + StatsBytes;   // 12296

        public const int CmdIdle = 0;
        public const int CmdRecord = 1;

        // 링 버퍼 슬롯의 픽셀 형식 (DXGI_FORMAT 번호를 그대로 씀)
        public const int FormatRgba8 = 28;   // R8G8B8A8_UNORM: 유니티 RGBA32 텍스처, ffmpeg rgba
        public const int FormatBgra8 = 87;   // B8G8R8A8_UNORM: ffmpeg bgra

        // 공유 메모리 / 이벤트 이름 (대상 프로세스 PID가 붙음)
        public static string ControlName(uint pid) { return "Local\\UnityCamcorder_" + pid; }
        public static string FramesName(uint pid, int session) { return "Local\\UnityCamcorder_" + pid + "_Frames_" + session; }
        public static string FrameReadyName(uint pid) { return "Local\\UnityCamcorder_" + pid + "_FrameReady"; }
        public static string SlotFreeName(uint pid) { return "Local\\UnityCamcorder_" + pid + "_SlotFree"; }
    }

    // 제어 블록 안 위치 (바이트)
    public static class Offsets
    {
        // 캠코더 → 플러그인
        public const int Magic = 0;
        public const int Version = 4;
        public const int ControllerPid = 8;         // 캠코더가 죽으면 플러그인이 스스로 녹화를 멈추고 설정을 되돌림
        public const int SessionId = 12;            // 녹화를 시작할 때마다 1씩 증가
        public const int Command = 16;              // CmdIdle / CmdRecord
        public const int Fps = 20;                  // 결과 영상 fps = Time.captureFramerate
        public const int SpeedLimitPermille = 24;   // 최대 배속 x1000 (0이면 제한 없음, 참고용)
        public const int SlotCount = 28;
        public const int SlotSize = 32;             // int64
        public const int RecordWidth = 40;          // 자기 RenderTexture가 없는 카메라를 찍을 영상 크기
        public const int RecordHeight = 44;
        public const int SkipFrames = 48;

        // 플러그인 → 캠코더
        public const int Status = 56;               // Status 플래그
        public const int PayloadVersion = 60;       // 플러그인이 아는 규약 버전
        public const int FrameCount = 64;           // int64, 플러그인 틱 수
        public const int ScreenWidth = 72;          // 앱 화면 크기
        public const int ScreenHeight = 76;
        public const int ScreenFormat = 80;
        public const int GraphicsApi = 84;          // GraphicsApi
        public const int AppliedSessionId = 88;     // 플러그인이 녹화 준비(배속, 카메라)를 마친 sessionId
        public const int MainThreadId = 92;
        public const int WriteCount = 96;           // int64, 플러그인이 링 버퍼에 쓴 프레임 수 (누적)
        public const int ReadCount = 104;           // int64, 캠코더가 읽어 간 프레임 수 (누적)
        public const int RuntimeInfo = 120;         // UTF-8, RuntimeInfoBytes
        public const int LastError = 376;           // UTF-8, LastErrorBytes
        public const int Slots = 888;               // SlotStride x MaxSlots

        // 카메라 선택
        public const int CameraListRequest = 3960;  // 캠코더 → 플러그인: 값을 올리면 카메라 목록을 다시 채움
        public const int CameraListDone = 3964;     // 플러그인 → 캠코더: 처리한 CameraListRequest 값
        public const int CameraCount = 3968;        // 플러그인 → 캠코더: 목록 개수 (-1이면 카메라 촬영 불가)
        public const int StreamCount = 3972;        // 캠코더 → 플러그인: 촬영할 카메라 수 (0이면 화면 전체를 한 영상으로)
        public const int StreamCameraIds = 3976;    // int32 x MaxStreams, 촬영할 카메라 instanceId (stream 번호 순)
        public const int Cameras = 4040;            // CameraStride x MaxCameras

        // 속도 진단 (플러그인 → 캠코더). 녹화 중 앱 메인 스레드가 캠코더 때문에 쓴 시간의 누적값(int64, 마이크로초).
        // 캠코더가 1초마다 차이를 보고 배속이 안 나오는 원인(인코딩/GPU 읽기/앱 자체)을 알려 줍니다.
        public const int Stats = Cameras + Shared.CameraStride * Shared.MaxCameras;   // 12232
        public const int StatSlotWaitUs = Stats;           // 빈 슬롯을 기다린 시간 (캠코더·ffmpeg가 못 따라옴)
        public const int StatGpuWaitUs = Stats + 8;        // GPU 읽기가 끝나길 기다린 시간 (동기 읽기 포함)
        public const int StatCopyUs = Stats + 16;          // 프레임을 공유 메모리로 복사한 시간
        public const int StatFrameLimitResets = Stats + 24;   // int32, 앱이 녹화 중 프레임 제한/수직 동기화를 다시 건 횟수 (플러그인이 다시 풂)

        // 슬롯 하나 안 (48바이트)
        public const int SlotWidth = 0;
        public const int SlotHeight = 4;
        public const int SlotFormat = 8;
        public const int SlotBytesPerPixel = 12;
        public const int SlotFrameIndex = 16;       // int64
        public const int SlotDataSize = 24;         // int64
        public const int SlotStream = 32;           // 몇 번째 촬영 카메라의 프레임인지 (0부터)

        // 카메라 항목 하나 안 (128바이트)
        public const int CamInstanceId = 0;         // Object.GetInstanceID()
        public const int CamWidth = 4;              // 이 카메라를 찍을 때의 영상 크기
        public const int CamHeight = 8;
        public const int CamFlags = 12;             // CameraFlags
        public const int CamDepth = 16;             // float, Camera.depth
        public const int CamTargetDisplay = 20;
        public const int CamName = 24;              // UTF-8, CameraNameBytes

        public static int Slot(int index) { return Slots + index * Shared.SlotStride; }
        public static int Camera(int index) { return Cameras + index * Shared.CameraStride; }
    }

    [Flags]
    public enum Status
    {
        Loaded = 1 << 0,            // 플러그인이 제어 블록에 연결됨
        Ready = 1 << 1,             // 촬영 준비 완료 (화면 크기를 알림)
        RuntimeMono = 1 << 2,       // Mono 런타임 (BepInEx 5)
        TimeControl = 1 << 4,       // captureFramerate 적용 성공 (배속 촬영 중)
        TimeFailed = 1 << 5,        // 시간 제어 실패 (실시간 캡처로 진행)
        MainThread = 1 << 7,        // 메인 스레드 틱이 한 번 이상 실행됨
        CameraCapture = 1 << 8,     // 카메라 RenderTexture로 촬영 중
        Tick = 1 << 9,              // 매 프레임 메인 스레드 틱을 받음
        ScreenCapture = 1 << 10,    // 카메라 대신 화면(백버퍼)을 프레임 끝에서 읽는 중
    }

    [Flags]
    public enum CameraFlags
    {
        Enabled = 1 << 0,           // 카메라 컴포넌트가 켜져 있음
        OwnTarget = 1 << 1,         // 앱이 이미 자기 RenderTexture로 렌더링 중 (그 텍스처를 그대로 읽음)
        Main = 1 << 2,              // Camera.main
        Overlay = 1 << 3,           // URP 카메라 스택의 오버레이 카메라 (혼자 못 찍고 베이스 카메라 영상에 함께 들어감)
    }

    public enum GraphicsApi
    {
        Unknown = 0,
        OpenGL = 1,
        Vulkan = 2,
        D3D11 = 11,
        D3D12 = 12,
    }
}
