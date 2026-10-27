using System.IO.MemoryMappedFiles;
using System.Text;

namespace UnityCamcorder;

// CamcorderShared.h 의 구조를 그대로 읽고 씁니다. 오프셋이 C++ 쪽과 반드시 같아야 합니다.
// 캠코더가 제어 블록과 프레임 링 버퍼(공유 메모리)와 이벤트를 만들고, DLL이 그것을 엽니다.
public sealed class SharedControl : IDisposable
{
    public const uint Magic = 0x524D4355u;   // 'UCMR'
    public const int Version = 1;
    public const int MaxSlots = 64;
    public const int ControlSize = 888 + 32 * MaxSlots;  // 2936

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
    }

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

        _controlFile = MemoryMappedFile.CreateNew(ControlName(targetPid), ControlSize, MemoryMappedFileAccess.ReadWrite);
        _control = _controlFile.CreateViewAccessor(0, ControlSize, MemoryMappedFileAccess.ReadWrite);
        _frameReady = new EventWaitHandle(false, EventResetMode.AutoReset, FrameReadyName(targetPid));
        _slotFree = new EventWaitHandle(false, EventResetMode.AutoReset, SlotFreeName(targetPid));

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

    // --- 프레임 슬롯 메타 (slots[] at offset 888, 각 32바이트) ---
    private long SlotOffset(int index) => 888 + (long)index * 32;
    public int SlotWidth(int i) => _control.ReadInt32(SlotOffset(i) + 0);
    public int SlotHeight(int i) => _control.ReadInt32(SlotOffset(i) + 4);
    public int SlotFormat(int i) => _control.ReadInt32(SlotOffset(i) + 8);
    public int SlotBytesPerPixel(int i) => _control.ReadInt32(SlotOffset(i) + 12);
    public long SlotFrameIndex(int i) => _control.ReadInt64(SlotOffset(i) + 16);
    public long SlotDataSize(int i) => _control.ReadInt64(SlotOffset(i) + 24);

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
