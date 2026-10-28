using System.IO.MemoryMappedFiles;
using System.Text;

// 캠코더는 Windows 전용 (이름 있는 공유 메모리/이벤트 사용)
[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]

namespace UnityCamcorder;

// CamcorderShared.h 의 구조를 그대로 읽고 씁니다. 오프셋이 C++ 쪽과 반드시 같아야 합니다.
// 캠코더가 제어 블록과 프레임 링 버퍼(공유 메모리)와 이벤트를 만들고, DLL이 그것을 엽니다.
public sealed class SharedControl : IDisposable
{
    public const uint Magic = 0x524D4355u;   // 'UCMR'
    public const int Version = 3;
    public const int MaxSlots = 64;
    public const int MaxCameras = 64;
    public const int MaxStreams = 16;
    private const int SlotStride = 48;
    private const int CameraStride = 128;
    public const int ControlSize = 4040 + CameraStride * MaxCameras;  // 12232

    // 상태 플래그 (CamcorderStatus)
    [Flags]
    public enum Status
    {
        Loaded = 1 << 0,
        Hooked = 1 << 1,
        RuntimeMono = 1 << 2,
        RuntimeIl2cpp = 1 << 3,
        TimeControl = 1 << 4,
        TimeFailed = 1 << 5,
        Capturing = 1 << 6,
        MainThread = 1 << 7,
        CameraCapture = 1 << 8,
        Tick = 1 << 9,
    }

    // 카메라 목록 항목 플래그 (CamcorderCameraFlags)
    [Flags]
    public enum CameraFlags
    {
        Enabled = 1 << 0,
        OwnTarget = 1 << 1,
        Main = 1 << 2,
    }

    public sealed record CameraInfo(int InstanceId, int Width, int Height, CameraFlags Flags, float Depth, int TargetDisplay, string Name);

    public const int CmdIdle = 0;
    public const int CmdRecord = 1;

    private readonly MemoryMappedFile _controlFile;
    private readonly MemoryMappedViewAccessor _control;
    private MemoryMappedFile? _framesFile;
    private MemoryMappedViewAccessor? _frames;
    private readonly EventWaitHandle _frameReady;
    private readonly EventWaitHandle _slotFree;

    public int SlotCount { get; private set; }
    private long _slotSize;
    public long SlotSize
    {
        get => _slotSize;
        set { _slotSize = value; _control.Write(32, value); }   // 제어 블록에도 기록
    }
    public uint TargetPid { get; }

    public SharedControl(uint targetPid, int slotCount)
    {
        TargetPid = targetPid;
        SlotCount = slotCount;

        // 이미 DLL이 들어가 있는 앱(카메라 목록을 먼저 불러온 경우 등)이면 DLL이 열어 둔 제어 블록을 그대로 다시 씀
        _controlFile = MemoryMappedFile.CreateOrOpen(ControlName(targetPid), ControlSize, MemoryMappedFileAccess.ReadWrite);
        _control = _controlFile.CreateViewAccessor(0, ControlSize, MemoryMappedFileAccess.ReadWrite);
        _frameReady = new EventWaitHandle(false, EventResetMode.AutoReset, FrameReadyName(targetPid));
        _slotFree = new EventWaitHandle(false, EventResetMode.AutoReset, SlotFreeName(targetPid));

        // 예전 버전 DLL이 이미 들어가 있는 앱인지 (DLL은 프로세스가 끝날 때까지 내려가지 않으므로 앱을 다시 켜야 함)
        int oldPayload = _control.ReadInt32(60);
        StaleDll = ((Status)_control.ReadInt32(56)).HasFlag(Status.Loaded) && oldPayload != 0 && oldPayload != Version;
        StaleDllVersion = oldPayload;

        // 제어 블록 초기화
        _control.Write(0, Magic);
        _control.Write(4, Version);
        _control.Write(28, slotCount);   // slotCount
        SetControllerPid((uint)Environment.ProcessId);
    }

    private static string ControlName(uint pid) => $"Local\\UnityCamcorder_{pid}";
    private static string FramesName(uint pid, int session) => $"Local\\UnityCamcorder_{pid}_Frames_{session}";
    private static string FrameReadyName(uint pid) => $"Local\\UnityCamcorder_{pid}_FrameReady";
    private static string SlotFreeName(uint pid) => $"Local\\UnityCamcorder_{pid}_SlotFree";

    // --- 제어 블록 필드 (오프셋은 CamcorderShared.h 와 일치) ---
    public void SetControllerPid(uint pid) => _control.Write(8, (int)pid);
    public int SessionId { get => _control.ReadInt32(12); set => _control.Write(12, value); }
    public int Command { get => _control.ReadInt32(16); set => _control.Write(16, value); }
    public int Fps { get => _control.ReadInt32(20); set => _control.Write(20, value); }
    public int SpeedLimitPermille { get => _control.ReadInt32(24); set => _control.Write(24, value); }
    public int RecordWidth { get => _control.ReadInt32(40); set => _control.Write(40, value); }
    public int RecordHeight { get => _control.ReadInt32(44); set => _control.Write(44, value); }

    public bool StaleDll { get; }
    public int StaleDllVersion { get; }
    public Status StatusFlags => (Status)_control.ReadInt32(56);
    public long PresentCount => _control.ReadInt64(64);
    public int BackbufferWidth => _control.ReadInt32(72);
    public int BackbufferHeight => _control.ReadInt32(76);
    public int BackbufferFormat => _control.ReadInt32(80);
    public int GraphicsApi => _control.ReadInt32(84);
    public int AppliedSessionId => _control.ReadInt32(88);
    public long WriteCount => _control.ReadInt64(96);
    public long ReadCount { get => _control.ReadInt64(104); set => _control.Write(104, value); }

    public string RuntimeInfo => ReadAscii(120, 256);
    public string LastError => ReadAscii(376, 512);

    private string ReadAscii(int offset, int max)
    {
        var buffer = new byte[max];
        _control.ReadArray(offset, buffer, 0, max);
        int len = Array.IndexOf(buffer, (byte)0);
        if (len < 0) len = max;
        return Encoding.UTF8.GetString(buffer, 0, len);
    }

    // --- 프레임 슬롯 메타 (slots[] at offset 888, 각 48바이트) ---
    private long SlotOffset(int index) => 888 + (long)index * SlotStride;
    public int SlotWidth(int i) => _control.ReadInt32(SlotOffset(i) + 0);
    public int SlotHeight(int i) => _control.ReadInt32(SlotOffset(i) + 4);
    public int SlotFormat(int i) => _control.ReadInt32(SlotOffset(i) + 8);
    public int SlotBytesPerPixel(int i) => _control.ReadInt32(SlotOffset(i) + 12);
    public long SlotFrameIndex(int i) => _control.ReadInt64(SlotOffset(i) + 16);
    public long SlotDataSize(int i) => _control.ReadInt64(SlotOffset(i) + 24);
    public int SlotStream(int i) => _control.ReadInt32(SlotOffset(i) + 32);

    // --- 카메라 선택 ---
    public int CameraListRequest { get => _control.ReadInt32(3960); set => _control.Write(3960, value); }
    public int CameraListDone => _control.ReadInt32(3964);
    public int CameraCount => _control.ReadInt32(3968);
    public int StreamCount { get => _control.ReadInt32(3972); set => _control.Write(3972, value); }
    public void SetStreamCameraId(int stream, int instanceId) => _control.Write(3976 + stream * 4, instanceId);

    public CameraInfo ReadCamera(int index)
    {
        long o = 4040 + (long)index * CameraStride;
        var name = new byte[104];
        _control.ReadArray(o + 24, name, 0, name.Length);
        int len = Array.IndexOf(name, (byte)0);
        if (len < 0) len = name.Length;
        return new CameraInfo(
            _control.ReadInt32(o), _control.ReadInt32(o + 4), _control.ReadInt32(o + 8),
            (CameraFlags)_control.ReadInt32(o + 12), _control.ReadSingle(o + 16), _control.ReadInt32(o + 20),
            Encoding.UTF8.GetString(name, 0, len));
    }

    // --- 프레임 링 버퍼 (녹화 세션마다 새로 만듦) ---
    public void OpenFrameRing(int session)
    {
        CloseFrameRing();
        long size = SlotSize * SlotCount;
        _framesFile = MemoryMappedFile.CreateNew(FramesName(TargetPid, session), size, MemoryMappedFileAccess.ReadWrite);
        _frames = _framesFile.CreateViewAccessor(0, size, MemoryMappedFileAccess.ReadWrite);
    }

    public void CloseFrameRing()
    {
        _frames?.Dispose();
        _framesFile?.Dispose();
        _frames = null;
        _framesFile = null;
    }

    // 슬롯의 픽셀을 읽어 버퍼에 담습니다.
    public void ReadSlot(int slotIndex, byte[] dest, int length)
    {
        if (_frames == null) throw new InvalidOperationException("프레임 링 버퍼가 열려 있지 않음");
        _frames.ReadArray(slotIndex * SlotSize, dest, 0, length);
    }

    public bool WaitFrame(int timeoutMs) => _frameReady.WaitOne(timeoutMs);
    public void SignalSlotFree() => _slotFree.Set();

    public void Dispose()
    {
        CloseFrameRing();
        _control.Dispose();
        _controlFile.Dispose();
        _frameReady.Dispose();
        _slotFree.Dispose();
    }
}
