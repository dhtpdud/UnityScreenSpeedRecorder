using System.IO.MemoryMappedFiles;
using System.Text;
using UnityCamcorder.Protocol;

// 캠코더는 Windows 전용 (이름 있는 공유 메모리/이벤트 사용)
[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]

namespace UnityCamcorder;

// 앱 안의 BepInEx 플러그인과 주고받는 제어 블록. 오프셋은 Shared/CamcorderProtocol.cs를 플러그인과 같이 씁니다.
// 캠코더가 제어 블록과 프레임 링 버퍼(공유 메모리)와 이벤트를 만들고, 플러그인이 매 프레임 그것을 찾아 엽니다.
public sealed class SharedControl : IDisposable
{
    public const int Version = Shared.Version;
    public const int MaxSlots = Shared.MaxSlots;
    public const int MaxCameras = Shared.MaxCameras;
    public const int MaxStreams = Shared.MaxStreams;
    public const int CmdIdle = Shared.CmdIdle;
    public const int CmdRecord = Shared.CmdRecord;

    public sealed record CameraInfo(int InstanceId, int Width, int Height, CameraFlags Flags, float Depth, int TargetDisplay, string Name);

    private readonly MemoryMappedFile _controlFile;
    private readonly MemoryMappedViewAccessor _control;
    private MemoryMappedFile? _framesFile;
    private MemoryMappedViewAccessor? _frames;
    private readonly EventWaitHandle _frameReady;
    private readonly EventWaitHandle _slotFree;

    // 녹화 세션을 열기 전에만 바꿈 (플러그인은 링 버퍼를 열 때 이 값을 읽음)
    private int _slotCount;
    public int SlotCount
    {
        get => _slotCount;
        set { _slotCount = value; _control.Write(Offsets.SlotCount, value); }
    }
    private long _slotSize;
    public long SlotSize
    {
        get => _slotSize;
        set { _slotSize = value; _control.Write(Offsets.SlotSize, value); }   // 제어 블록에도 기록
    }
    public uint TargetPid { get; }

    public SharedControl(uint targetPid, int slotCount)
    {
        TargetPid = targetPid;
        _slotCount = slotCount;

        // 플러그인이 이미 연결돼 있던 앱(카메라 목록을 먼저 불러온 경우 등)이면 플러그인이 열어 둔 제어 블록을 그대로 다시 씀
        _controlFile = MemoryMappedFile.CreateOrOpen(Shared.ControlName(targetPid), Shared.ControlSize, MemoryMappedFileAccess.ReadWrite);
        _control = _controlFile.CreateViewAccessor(0, Shared.ControlSize, MemoryMappedFileAccess.ReadWrite);
        _frameReady = new EventWaitHandle(false, EventResetMode.AutoReset, Shared.FrameReadyName(targetPid));
        _slotFree = new EventWaitHandle(false, EventResetMode.AutoReset, Shared.SlotFreeName(targetPid));

        // 버전이 다른 플러그인이 붙어 있던 블록인지 (플러그인은 앱이 꺼질 때까지 바뀌지 않으므로 앱을 다시 켜야 함)
        int oldPayload = _control.ReadInt32(Offsets.PayloadVersion);
        StalePlugin = StatusFlags.HasFlag(Status.Loaded) && oldPayload != 0 && oldPayload != Version;
        StalePluginVersion = oldPayload;

        // 제어 블록 초기화. 이전 캠코더가 녹화 중에 죽었다면 남은 녹화 명령도 지움
        _control.Write(Offsets.Magic, Shared.Magic);
        _control.Write(Offsets.Version, Version);
        _control.Write(Offsets.SlotCount, slotCount);
        _control.Write(Offsets.Command, CmdIdle);
        SetControllerPid((uint)Environment.ProcessId);
    }

    // --- 제어 블록 필드 ---
    public void SetControllerPid(uint pid) => _control.Write(Offsets.ControllerPid, (int)pid);
    public int SessionId { get => _control.ReadInt32(Offsets.SessionId); set => _control.Write(Offsets.SessionId, value); }
    public int Command { get => _control.ReadInt32(Offsets.Command); set => _control.Write(Offsets.Command, value); }
    public int Fps { get => _control.ReadInt32(Offsets.Fps); set => _control.Write(Offsets.Fps, value); }
    public int SpeedLimitPermille { get => _control.ReadInt32(Offsets.SpeedLimitPermille); set => _control.Write(Offsets.SpeedLimitPermille, value); }
    public int RecordWidth { get => _control.ReadInt32(Offsets.RecordWidth); set => _control.Write(Offsets.RecordWidth, value); }
    public int RecordHeight { get => _control.ReadInt32(Offsets.RecordHeight); set => _control.Write(Offsets.RecordHeight, value); }

    public bool StalePlugin { get; }
    public int StalePluginVersion { get; }
    public Status StatusFlags => (Status)_control.ReadInt32(Offsets.Status);
    public int PayloadVersion => _control.ReadInt32(Offsets.PayloadVersion);
    public long FrameCount => _control.ReadInt64(Offsets.FrameCount);
    public int ScreenWidth => _control.ReadInt32(Offsets.ScreenWidth);
    public int ScreenHeight => _control.ReadInt32(Offsets.ScreenHeight);
    public GraphicsApi GraphicsApi => (GraphicsApi)_control.ReadInt32(Offsets.GraphicsApi);
    public int AppliedSessionId => _control.ReadInt32(Offsets.AppliedSessionId);
    public long WriteCount => _control.ReadInt64(Offsets.WriteCount);
    public long ReadCount { get => _control.ReadInt64(Offsets.ReadCount); set => _control.Write(Offsets.ReadCount, value); }

    // 속도 진단: 플러그인이 녹화 중 기다리거나 복사한 시간 (마이크로초, 누적)
    public long StatSlotWaitUs => _control.ReadInt64(Offsets.StatSlotWaitUs);
    public long StatGpuWaitUs => _control.ReadInt64(Offsets.StatGpuWaitUs);
    public long StatCopyUs => _control.ReadInt64(Offsets.StatCopyUs);
    public int StatFrameLimitResets => _control.ReadInt32(Offsets.StatFrameLimitResets);

    public string RuntimeInfo => ReadUtf8(Offsets.RuntimeInfo, Shared.RuntimeInfoBytes);
    public string LastError => ReadUtf8(Offsets.LastError, Shared.LastErrorBytes);

    private string ReadUtf8(long offset, int max)
    {
        var buffer = new byte[max];
        _control.ReadArray(offset, buffer, 0, max);
        int len = Array.IndexOf(buffer, (byte)0);
        if (len < 0) len = max;
        return Encoding.UTF8.GetString(buffer, 0, len);
    }

    // --- 프레임 슬롯 메타 ---
    public int SlotWidth(int i) => _control.ReadInt32(Offsets.Slot(i) + Offsets.SlotWidth);
    public int SlotHeight(int i) => _control.ReadInt32(Offsets.Slot(i) + Offsets.SlotHeight);
    public int SlotFormat(int i) => _control.ReadInt32(Offsets.Slot(i) + Offsets.SlotFormat);
    public int SlotBytesPerPixel(int i) => _control.ReadInt32(Offsets.Slot(i) + Offsets.SlotBytesPerPixel);
    public long SlotFrameIndex(int i) => _control.ReadInt64(Offsets.Slot(i) + Offsets.SlotFrameIndex);
    public long SlotDataSize(int i) => _control.ReadInt64(Offsets.Slot(i) + Offsets.SlotDataSize);
    public int SlotStream(int i) => _control.ReadInt32(Offsets.Slot(i) + Offsets.SlotStream);

    // --- 카메라 선택 ---
    public int CameraListRequest { get => _control.ReadInt32(Offsets.CameraListRequest); set => _control.Write(Offsets.CameraListRequest, value); }
    public int CameraListDone => _control.ReadInt32(Offsets.CameraListDone);
    public int CameraCount => _control.ReadInt32(Offsets.CameraCount);
    public int StreamCount { get => _control.ReadInt32(Offsets.StreamCount); set => _control.Write(Offsets.StreamCount, value); }
    public void SetStreamCameraId(int stream, int instanceId) => _control.Write(Offsets.StreamCameraIds + stream * 4, instanceId);

    public CameraInfo ReadCamera(int index)
    {
        long o = Offsets.Camera(index);
        var name = new byte[Shared.CameraNameBytes];
        _control.ReadArray(o + Offsets.CamName, name, 0, name.Length);
        int len = Array.IndexOf(name, (byte)0);
        if (len < 0) len = name.Length;
        return new CameraInfo(
            _control.ReadInt32(o + Offsets.CamInstanceId), _control.ReadInt32(o + Offsets.CamWidth), _control.ReadInt32(o + Offsets.CamHeight),
            (CameraFlags)_control.ReadInt32(o + Offsets.CamFlags), _control.ReadSingle(o + Offsets.CamDepth), _control.ReadInt32(o + Offsets.CamTargetDisplay),
            Encoding.UTF8.GetString(name, 0, len));
    }

    // --- 프레임 링 버퍼 (녹화 세션마다 새로 만듦) ---
    public void OpenFrameRing(int session)
    {
        CloseFrameRing();
        long size = SlotSize * SlotCount;
        _framesFile = MemoryMappedFile.CreateNew(Shared.FramesName(TargetPid, session), size, MemoryMappedFileAccess.ReadWrite);
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
