using System.Diagnostics;
using System.Globalization;
using UnityCamcorder.Install;
using UnityCamcorder.Protocol;

namespace UnityCamcorder;

// 전체 녹화 흐름을 묶습니다: (필요하면 BepInEx + 플러그인 설치) → 대상 실행/붙기 → 앱 안의 플러그인 연결 →
// 배속 촬영 시작 → 공유 메모리 링 버퍼에서 프레임을 받아 ffmpeg로 흘려보냄 → 목표 프레임/멈춤/크래시 시 안전하게 마무리.
//
// 촬영 방식 (플러그인이 정함)
// - 카메라 촬영: 카메라의 targetTexture를 RenderTexture로 돌려 화면 대신 그곳에 그리게 하고 GPU 비동기 읽기.
//   촬영 카메라를 고르면 카메라마다 스트림(= 영상 파일) 하나. 안 고르면 화면에 그리던 카메라 전부를 한 영상으로.
// - 화면 촬영: 화면에 그리는 카메라가 없으면 프레임 끝에서 화면(백버퍼)을 읽음.
public sealed class RecordingSession
{
    private readonly Settings _settings;
    private readonly string _toolDir;
    private readonly CancellationToken _stop;

    // 스트림(카메라)마다 하나
    private sealed class StreamState
    {
        public string Label = "";
        public string OutputPath = "";
        public FrameWriter? Writer;   // ffmpeg + 전용 쓰기 스레드 (첫 프레임에서 시작)
        public long Received;      // 받은 프레임 (워밍업 포함)
        public long Written;       // 영상에 넣은 프레임
        public int Width, Height;
        public bool Failed;
    }

    public RecordingSession(Settings settings, string toolDir, CancellationToken stop = default)
    {
        _settings = settings;
        _toolDir = toolDir;
        _stop = stop;
    }

    // 대상 실행/붙기 → 앱 안의 플러그인이 연결될 때까지. 실패하면 null
    private (TargetProcess target, SharedControl shared)? Connect()
    {
        _settings.Validate();
        bool launch = _settings.TargetPid <= 0 && string.IsNullOrWhiteSpace(_settings.TargetProcessName);
        // 앱을 켜기 전에 앱 폴더에 BepInEx + 플러그인을 준비 (BepInEx는 앱이 켜질 때 플러그인을 불러옴)
        if (launch && !PreparePlugin(Path.GetFullPath(_settings.TargetExe!)))
            return null;

        var target = TargetProcess.LaunchOrAttach(_settings);
        Log($"대상 프로세스: PID {target.Pid} ({(target.Is64Bit ? "64비트" : "32비트")})");
        string? exe = launch ? Path.GetFullPath(_settings.TargetExe!) : target.ImagePath;
        if (!launch && exe != null && !CheckAttachedPlugin(exe))
            return null;

        var shared = new SharedControl(target.Pid, _settings.SlotCount);
        if (shared.StalePlugin)
        {
            Fail($"이 앱 안의 캠코더 플러그인(v{shared.StalePluginVersion})이 이 캠코더(v{SharedControl.Version})와 다른 버전입니다. " +
                 "플러그인은 앱이 꺼질 때까지 바뀌지 않으니, 앱을 완전히 종료한 뒤 다시 녹화하세요.");
            shared.Dispose();
            return null;
        }

        Log("앱 안의 캠코더 플러그인 연결 대기 중...");
        // 앱을 막 켰다면 로딩까지 넉넉히, 이미 켜진 앱이면 플러그인이 0.5초마다 확인하므로 짧게
        int timeout = launch ? _settings.PluginConnectTimeoutMs : Math.Min(_settings.PluginConnectTimeoutMs, 20000);
        if (!WaitFor(() => shared.StatusFlags.HasFlag(Status.Loaded | Status.Ready) && shared.ScreenWidth > 0,
                     timeout, () => target.HasExited || _stop.IsCancellationRequested))
        {
            if (_stop.IsCancellationRequested)
                Fail("플러그인이 연결되기 전에 중지 요청을 받았습니다.");
            else if (target.HasExited)
                Fail("플러그인이 연결되기 전에 대상이 종료되었습니다.");
            else
                Fail(DescribeNoPlugin(shared, exe, target));
            shared.Dispose();
            return null;
        }
        Log($"플러그인 연결 완료. {shared.RuntimeInfo} / 화면 {shared.ScreenWidth}x{shared.ScreenHeight}");
        return (target, shared);
    }

    // 앱 폴더에 BepInEx + 캠코더 플러그인이 있는지 보고, 없거나 다른 버전이면 설치 (AutoInstallPlugin)
    private bool PreparePlugin(string exe)
    {
        AppInstallState state = PluginInstaller.Inspect(exe, _toolDir);
        if (state.Problem != null)
        {
            Fail(state.Problem);
            return false;
        }
        if (!state.Ready)
        {
            if (!_settings.AutoInstallPlugin)
            {
                Fail($"{state.Describe()} (자동 설치가 꺼져 있습니다. UnityCamcorder.exe --install --target \"{exe}\" 로 설치하세요)");
                return false;
            }
            try { state = PluginInstaller.Install(exe, _toolDir, Log); }
            catch (InvalidOperationException ex)
            {
                Fail(ex.Message);
                return false;
            }
        }
        Log("플러그인: " + state.Describe());
        return true;
    }

    // 이미 켜진 앱에 붙을 때: 플러그인은 앱이 켜질 때만 로드되므로 지금 설치해도 소용없음
    private bool CheckAttachedPlugin(string exe)
    {
        AppInstallState state = PluginInstaller.Inspect(exe, _toolDir);
        if (state.Problem != null)
        {
            Fail(state.Problem);
            return false;
        }
        if (state.BepInExVersion == null || !state.PluginInstalled)
        {
            Fail("이 앱 폴더에는 캠코더 플러그인이 없습니다. 앱을 닫고 '앱을 실행해서 녹화'(--target)로 녹화하면 자동으로 설치합니다.");
            return false;
        }
        if (!state.PluginUpToDate)
            Log("경고: 앱 폴더의 캠코더 플러그인이 이 캠코더와 다른 파일입니다. 앱을 다시 켜기 전까지는 지금 앱에 로드된 플러그인으로 녹화합니다.");
        return true;
    }

    // 플러그인이 연결되지 않을 때 이유 추측
    private static string DescribeNoPlugin(SharedControl shared, string? exe, TargetProcess target)
    {
        int payload = shared.PayloadVersion;
        if (payload != 0 && payload != SharedControl.Version)
            return $"앱 안의 캠코더 플러그인(v{payload})이 이 캠코더(v{SharedControl.Version})와 다른 버전입니다. 앱을 닫고 다시 녹화하면 플러그인을 바꿉니다.";
        if (shared.StatusFlags.HasFlag(Status.Loaded))
            return "플러그인은 연결됐지만 앱 화면 크기를 아직 알려 주지 않았습니다. 앱이 로딩 중이면 잠시 뒤 다시 시도하세요.";
        if (exe != null)
        {
            string log = PluginInstaller.BepInExLogPath(Path.GetDirectoryName(exe)!);
            DateTime? started = target.StartTime;
            if (!File.Exists(log) || (started != null && File.GetLastWriteTime(log) < started.Value.AddSeconds(-5)))
                return "BepInEx가 이 앱 안에서 실행되지 않았습니다. 플러그인을 설치하기 전부터 켜져 있던 앱이면 앱을 다시 시작하세요. " +
                       "(백신이 앱 폴더의 winhttp.dll을 막았는지도 확인해 보세요)";
            if (shared.PayloadVersion == 0)
                return "앱 안의 캠코더 플러그인이 응답하지 않습니다. 앱 창이 최소화됐거나 비활성 상태라 멈춰 있으면 앱 창을 한 번 눌러 주세요. " +
                       $"BepInEx 기록: {log}";
        }
        return "앱 안의 캠코더 플러그인이 응답하지 않습니다. 앱 창이 최소화됐거나 비활성 상태라 멈춰 있으면 앱 창을 한 번 눌러 주세요.";
    }

    // 배속·카메라 촬영이 안 될 때 플러그인이 남긴 이유
    private static string DescribePlugin(SharedControl shared)
    {
        if (!shared.StatusFlags.HasFlag(Status.Tick))
            return "캠코더 플러그인이 연결되지 않았습니다.";
        return string.IsNullOrWhiteSpace(shared.LastError) ? "" : shared.LastError;
    }

    // 앱 안의 카메라 목록을 받아 옴. 카메라 촬영을 쓸 수 없는 앱이면 null
    private List<SharedControl.CameraInfo>? RequestCameraList(SharedControl shared, TargetProcess target)
    {
        if (!shared.StatusFlags.HasFlag(Status.Tick))
        {
            Log($"카메라 목록을 받을 수 없습니다. {DescribePlugin(shared)} 화면 전체 촬영만 가능합니다.");
            return null;
        }
        int request = shared.CameraListRequest + 1;
        shared.CameraListRequest = request;
        if (!WaitFor(() => shared.CameraListDone == request, 20000, () => target.HasExited || _stop.IsCancellationRequested))
        {
            Log($"카메라 목록을 받지 못했습니다. 앱이 멈춰 있거나 로딩 중입니다. {DescribePlugin(shared)}".TrimEnd());
            return null;
        }
        int count = shared.CameraCount;
        if (count < 0)
        {
            Log("이 앱은 카메라를 골라 찍을 수 없습니다. 화면 전체 촬영만 가능합니다.");
            return null;
        }
        var list = new List<SharedControl.CameraInfo>();
        for (int i = 0; i < count && i < SharedControl.MaxCameras; i++)
            list.Add(shared.ReadCamera(i));
        return list;
    }

    // --list-cameras: 카메라 목록만 출력하고 앱은 켜 둠 (창 프로그램이 이 출력을 읽음)
    public int ListCameras()
    {
        var connected = Connect();
        if (connected == null)
            return 1;
        var (target, shared) = connected.Value;
        using (shared)
        {
            Console.WriteLine($"@target\t{target.Pid}");
            var cameras = RequestCameraList(shared, target);
            if (cameras == null)
                return 2;
            Log($"카메라 {cameras.Count}대:");
            foreach (var c in cameras.OrderBy(c => c.TargetDisplay).ThenBy(c => c.Depth))
            {
                var notes = new List<string>();
                if (c.Flags.HasFlag(CameraFlags.Main)) notes.Add("메인");
                if (!c.Flags.HasFlag(CameraFlags.Enabled)) notes.Add("꺼짐");
                if (c.Flags.HasFlag(CameraFlags.OwnTarget)) notes.Add("자체 RenderTexture");
                if (c.Flags.HasFlag(CameraFlags.Overlay)) notes.Add("URP 오버레이: 베이스 카메라 영상에 함께 찍힘");
                Log($"  #{c.InstanceId}  {c.Name}  {c.Width}x{c.Height}  depth {c.Depth.ToString(CultureInfo.InvariantCulture)}" +
                    (notes.Count > 0 ? $"  ({string.Join(", ", notes)})" : ""));
                // 기계용 한 줄: id, 가로, 세로, 켜짐, 메인, 자체RT, depth, 디스플레이, URP오버레이, 이름(마지막 칸)
                Console.WriteLine(string.Join("\t", "@camera", c.InstanceId, c.Width, c.Height,
                    c.Flags.HasFlag(CameraFlags.Enabled) ? 1 : 0,
                    c.Flags.HasFlag(CameraFlags.Main) ? 1 : 0,
                    c.Flags.HasFlag(CameraFlags.OwnTarget) ? 1 : 0,
                    c.Depth.ToString(CultureInfo.InvariantCulture), c.TargetDisplay,
                    c.Flags.HasFlag(CameraFlags.Overlay) ? 1 : 0, c.Name.Replace('\t', ' ')));
            }
            Log("녹화할 때 --cameras \"이름 또는 #번호, ...\" 로 고르세요. 앱은 켜 둡니다.");
        }
        return 0;
    }

    // 설정의 카메라 지정("#id" 또는 이름)을 목록에서 찾아 스트림 순서대로 돌려줌
    private List<SharedControl.CameraInfo>? ResolveCameras(List<SharedControl.CameraInfo> list)
    {
        var chosen = new List<SharedControl.CameraInfo>();
        foreach (var raw in _settings.Cameras)
        {
            string spec = raw.Trim();
            if (spec.Length == 0) continue;
            SharedControl.CameraInfo? found = null;
            if (spec.StartsWith('#') && int.TryParse(spec.AsSpan(1), out int id))
                found = list.FirstOrDefault(c => c.InstanceId == id);
            found ??= list.FirstOrDefault(c => !chosen.Contains(c) && c.Name.Equals(spec, StringComparison.Ordinal));
            found ??= list.FirstOrDefault(c => !chosen.Contains(c) && c.Name.Equals(spec, StringComparison.OrdinalIgnoreCase));
            if (found == null)
            {
                Log($"카메라 '{spec}'를 찾을 수 없습니다. 있는 카메라: {string.Join(", ", list.Select(c => $"{c.Name}(#{c.InstanceId})"))}");
                return null;
            }
            if (!chosen.Contains(found))
                chosen.Add(found);
        }
        return chosen;
    }

    public int Run()
    {
        _settings.Validate();
        // ffmpeg를 녹화 시작 전에 미리 찾아 둠 (없으면 바로 실패)
        string ffmpeg = FfmpegEncoder.ResolveFfmpeg(_settings);
        Log($"ffmpeg: {ffmpeg}");

        var connected = Connect();
        if (connected == null)
            return 1;
        var (target, shared) = connected.Value;
        using var _ = shared;

        // 촬영 카메라
        var cameras = new List<SharedControl.CameraInfo>();
        if (_settings.Cameras.Any(c => !string.IsNullOrWhiteSpace(c)))
        {
            var list = RequestCameraList(shared, target);
            if (list == null)
                return Fail("카메라 목록을 받을 수 없어 고른 카메라로 촬영할 수 없습니다.");
            var resolved = ResolveCameras(list);
            if (resolved == null || resolved.Count == 0)
                return Fail("고른 카메라를 찾을 수 없습니다.");
            cameras = resolved;
        }

        // 영상 크기: 카메라가 자기 RT 없이 그릴 땐 지금 앱 화면 크기로 RenderTexture를 만듦
        int width = shared.ScreenWidth;
        int height = shared.ScreenHeight;
        shared.RecordWidth = width;
        shared.RecordHeight = height;
        long maxPixels = (long)width * height;
        foreach (var c in cameras)
            maxPixels = Math.Max(maxPixels, (long)c.Width * c.Height);
        shared.SlotSize = maxPixels * 4;

        // 스트림별 저장 경로
        var streams = new List<StreamState>();
        if (cameras.Count == 0)
            streams.Add(new StreamState { Label = "화면", OutputPath = _settings.OutputPath });
        else
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in cameras)
            {
                string path = cameras.Count == 1 ? _settings.OutputPath : PathForCamera(_settings.OutputPath, c.Name, used);
                streams.Add(new StreamState { Label = c.Name, OutputPath = path });
            }
        }

        // 카메라가 여러 대면 링 버퍼를 카메라당 4슬롯 이상으로 (한 카메라가 슬롯을 다 차지해 다른 카메라가 기다리지 않도록)
        shared.SlotCount = Math.Clamp(Math.Max(_settings.SlotCount, 4 * streams.Count), 2, SharedControl.MaxSlots);

        int session = shared.SessionId + 1;
        shared.OpenFrameRing(session);

        // 촬영 시작 명령
        shared.Fps = _settings.Fps;
        shared.SpeedLimitPermille = _settings.SpeedLimit * 1000;
        shared.StreamCount = cameras.Count;
        for (int i = 0; i < cameras.Count; i++)
            shared.SetStreamCameraId(i, cameras[i].InstanceId);
        // 이전 실행에서 쌓인 카운터를 이어서 씀 (플러그인이 이미 연결돼 있던 앱에 다시 붙은 경우)
        shared.ReadCount = shared.WriteCount;
        shared.Command = SharedControl.CmdRecord;
        shared.SessionId = session;   // 마지막에 세션을 올려 DLL이 링 버퍼를 열게 함

        int result;
        if (!WaitFor(() => shared.AppliedSessionId == session, 15000, () => target.HasExited || _stop.IsCancellationRequested))
        {
            result = Fail(target.HasExited ? "촬영 준비 중에 대상이 종료되었습니다."
                                           : "앱이 촬영 준비에 응답하지 않습니다. 앱이 멈춰 있거나, 창이 최소화/비활성 상태라 프레임이 돌지 않습니다. 앱 창을 한 번 눌러 주세요.");
        }
        else if (cameras.Count > 0 && !shared.StatusFlags.HasFlag(Status.CameraCapture))
        {
            result = Fail($"고른 카메라로 촬영을 시작하지 못했습니다. {shared.LastError}");
        }
        else if (!shared.StatusFlags.HasFlag(Status.CameraCapture) && !shared.StatusFlags.HasFlag(Status.ScreenCapture))
        {
            result = Fail($"촬영을 시작하지 못했습니다. {shared.LastError}");
        }
        else
        {
            bool cameraMode = shared.StatusFlags.HasFlag(Status.CameraCapture);
            Log(cameraMode
                ? $"촬영 방식: 카메라 RenderTexture (화면에 그리지 않고 카메라 출력을 GPU에서 바로 읽음), 영상 {streams.Count}개"
                : "촬영 방식: 화면 읽기 (화면에 그리는 카메라가 없어 프레임 끝에서 화면을 읽음)");
            Log(shared.StatusFlags.HasFlag(Status.TimeControl)
                ? $"배속 촬영: 켜짐 (게임 시간은 프레임당 1/{_settings.Fps}초, 프레임은 하드웨어가 허락하는 최대 속도)"
                : $"경고: 배속 촬영을 쓸 수 없어 실시간 속도로 캡처합니다. {DescribePlugin(shared)}".TrimEnd());
            foreach (var s in streams)
                Log($"  - {s.Label} → {Path.GetFullPath(s.OutputPath)}");
            Log($"촬영 시작 (session {session}, fps {_settings.Fps})");
            result = CaptureLoop(shared, target, streams);
        }

        // 촬영 중지 → 플러그인이 카메라와 시간 설정을 원래대로 되돌림
        shared.Command = SharedControl.CmdIdle;
        shared.CloseFrameRing();

        if (_settings.CloseTargetWhenDone && !target.HasExited)
        {
            Log("대상 앱 종료 중...");
            try { target.Process.CloseMainWindow(); if (!target.Process.WaitForExit(5000)) target.Process.Kill(); }
            catch { }
        }
        return result;
    }

    // out.mp4 + "Main Camera" → out_Main Camera.mp4 (파일 이름에 못 쓰는 글자는 _로)
    private static string PathForCamera(string outputPath, string cameraName, HashSet<string> used)
    {
        string dir = Path.GetDirectoryName(outputPath) ?? "";
        string stem = Path.GetFileNameWithoutExtension(outputPath);
        string ext = Path.GetExtension(outputPath);
        var invalid = Path.GetInvalidFileNameChars();
        string safe = new string(cameraName.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
        if (safe.Length == 0) safe = "Camera";
        string path = Path.Combine(dir, $"{stem}_{safe}{ext}");
        for (int n = 2; !used.Add(path); n++)
            path = Path.Combine(dir, $"{stem}_{safe}_{n}{ext}");
        return path;
    }

    private int CaptureLoop(SharedControl shared, TargetProcess target, List<StreamState> streams)
    {
        int totalFrames = _settings.ResolveTotalFrames();
        int warmup = Math.Max(0, _settings.WarmupFrames);

        var freezeWatch = Stopwatch.StartNew();
        long lastWrite = shared.WriteCount;
        var speed = new SpeedMeter(shared, streams, _settings.Fps, totalFrames);

        // 쓰기 스레드에서 ffmpeg가 먼저 죽은 스트림을 찾아 한 번만 알림
        bool CheckFailed(StreamState stream)
        {
            if (!stream.Failed && stream.Writer is { Failed: true })
            {
                Log($"ffmpeg가 프레임을 받지 못했습니다 ({stream.Label}). 최근 로그:\n{stream.Writer.Encoder.RecentLog()}");
                stream.Failed = true;
            }
            return stream.Failed;
        }

        try
        {
            while (true)
            {
                bool gotFrame = shared.WaitFrame(200);

                // 링 버퍼에 쌓인 프레임을 모두 소비
                while (shared.ReadCount < shared.WriteCount)
                {
                    long frameIndex = shared.ReadCount;
                    int slot = (int)(frameIndex % shared.SlotCount);
                    int w = shared.SlotWidth(slot);
                    int h = shared.SlotHeight(slot);
                    int fmt = shared.SlotFormat(slot);
                    int streamIndex = shared.SlotStream(slot);
                    long dataSize = shared.SlotDataSize(slot);

                    StreamState? stream = null;
                    if (streamIndex >= 0 && streamIndex < streams.Count && !streams[streamIndex].Failed)
                    {
                        stream = streams[streamIndex];
                        // 시간 제어 적용 직후의 불안정한 프레임과 목표를 넘은 프레임은 버림
                        if (stream.Received++ < warmup || (totalFrames > 0 && stream.Written >= totalFrames))
                            stream = null;
                    }
                    if (stream == null)
                    {
                        ReleaseSlot(shared, frameIndex);
                        continue;
                    }

                    // 첫 유효 프레임에서 ffmpeg와 전용 쓰기 스레드 시작
                    if (stream.Writer == null)
                    {
                        stream.Width = w; stream.Height = h;
                        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(stream.OutputPath))!);
                        var encoder = FfmpegEncoder.Start(_settings, w, h, fmt, stream.OutputPath);
                        int frameBytes = (int)Math.Max(dataSize, (long)w * h * 4);
                        int queue = QueueCapacity(frameBytes, streams.Count);
                        stream.Writer = new FrameWriter(encoder, frameBytes, queue, stream.Label);
                        Log($"인코딩 시작: {stream.Label} — {encoder.EncoderName}, {w}x{h} (ffmpeg 대기 줄 {queue}프레임)");
                        speed.Begin();
                    }
                    else if (w != stream.Width || h != stream.Height)
                    {
                        ReleaseSlot(shared, frameIndex);
                        Log("캡처 중 해상도가 바뀌어 녹화를 마칩니다.");
                        goto finish;
                    }

                    // 슬롯을 우리 버퍼로 복사한 즉시 비워 플러그인이 다음 프레임을 쓸 수 있게 함
                    byte[] data = stream.Writer.RentBuffer();
                    int length = (int)Math.Min(dataSize, data.Length);
                    shared.ReadSlot(slot, data, length);
                    ReleaseSlot(shared, frameIndex);

                    // ffmpeg 쪽 줄이 꽉 찼으면 자리가 날 때까지 기다림 (그동안 슬롯이 차서 앱도 기다림 = 프레임을 버리지 않음)
                    bool queued;
                    while (!(queued = stream.Writer.TryEnqueue(data, length, 100)))
                    {
                        if (stream.Writer.Failed || target.HasExited || _stop.IsCancellationRequested)
                            break;
                    }
                    if (queued)
                        stream.Written++;
                    else
                        stream.Writer.ReturnBuffer(data);

                    if (CheckFailed(stream))
                    {
                        if (streams.All(s => s.Failed))
                            goto finish;
                        continue;
                    }
                    if (!queued)
                        break;   // 중지 요청 또는 대상 종료: 아래에서 마무리

                    speed.Report();
                    if (totalFrames > 0 && streams.All(s => s.Failed || s.Written >= totalFrames))
                    {
                        Log("목표 프레임 수 도달.");
                        goto finish;
                    }
                }

                foreach (var stream in streams)
                    CheckFailed(stream);
                if (streams.All(s => s.Failed))
                    goto finish;
                speed.Report();

                if (_stop.IsCancellationRequested)
                {
                    Log("중지 요청을 받았습니다. 지금까지 캡처한 프레임으로 영상을 마무리합니다.");
                    goto finish;
                }

                // 멈춤/크래시 감지
                if (shared.WriteCount != lastWrite)
                {
                    lastWrite = shared.WriteCount;
                    freezeWatch.Restart();
                }
                if (target.HasExited)
                {
                    Log("대상 앱이 종료되었습니다. 지금까지 캡처한 프레임으로 영상을 마무리합니다.");
                    goto finish;
                }
                if (!gotFrame && freezeWatch.ElapsedMilliseconds > _settings.FreezeTimeoutMs)
                {
                    bool responding = target.IsResponding;
                    Log($"{_settings.FreezeTimeoutMs}ms 동안 새 프레임이 없습니다 (응답 {(responding ? "정상" : "없음")}). 영상을 마무리합니다.");
                    goto finish;
                }
            }

            finish: ;
        }
        finally
        {
            // 남은 프레임을 더 받지 않도록 먼저 촬영을 멈춤. 줄에 남은 프레임은 쓰기 스레드가 마저 넘긴 뒤 ffmpeg를 닫음
            shared.Command = SharedControl.CmdIdle;
            speed.LogSummary();
            foreach (var stream in streams)
            {
                var writer = stream.Writer;
                if (writer == null)
                    continue;
                bool ok = writer.Finish();
                var encoder = writer.Encoder;
                if (ok)
                    Log($"영상 저장 완료: {Path.GetFullPath(stream.OutputPath)} ({encoder.EncoderName}, {encoder.WrittenFrames}프레임)");
                else
                    Log($"ffmpeg 종료 코드 {encoder.ExitCode}. 영상이 불완전할 수 있습니다 ({stream.Label}). 로그:\n{encoder.RecentLog()}");
                writer.Dispose();
            }
        }

        if (streams.All(s => s.Writer == null))
            return Fail("캡처된 프레임이 없습니다.");
        return 0;
    }

    // 소비 완료 표시 (플러그인이 슬롯을 다시 쓰도록)
    private static void ReleaseSlot(SharedControl shared, long frameIndex)
    {
        shared.ReadCount = frameIndex + 1;
        shared.SignalSlotFree();
    }

    // ffmpeg에 넘기기 전에 쌓아 둘 프레임 수: 메모리 한도(BufferMemoryMB)를 카메라 수로 나눔
    private int QueueCapacity(int frameBytes, int streamCount)
    {
        long budget = (long)_settings.BufferMemoryMB << 20;
        long perFrame = Math.Max(1L, (long)frameBytes * Math.Max(1, streamCount));
        return (int)Math.Clamp(budget / perFrame, 2L, 64L);
    }

    // 배속과 병목 진단. 1초마다 실제 촬영 속도(초당 프레임, 배속)와 앱이 캠코더 때문에 기다린 시간의 비율을 진행 줄에 붙이고,
    // 녹화가 끝나면 전체 평균과 무엇이 속도를 막았는지 한 번 정리합니다.
    // 앱이 기다린 시간(플러그인이 잰 값): 인코딩 = 빈 슬롯 대기(캠코더·ffmpeg가 못 따라옴), GPU 읽기 = GPU 읽기 완료 대기, 복사 = 공유 메모리로 복사.
    // 셋 다 작으면 나머지 시간은 앱이 한 프레임을 만드는 데(게임 로직·렌더링) 쓴 것입니다.
    private sealed class SpeedMeter
    {
        private readonly SharedControl _shared;
        private readonly List<StreamState> _streams;
        private readonly int _fps;
        private readonly int _totalFrames;
        private readonly bool _screenMode;   // 화면 읽기(동기) 방식인지 (녹화를 멈춘 뒤엔 플래그가 지워지므로 시작할 때 기억)
        private Snapshot _start, _last;
        private bool _started;

        private sealed record Snapshot(long Ticks, long Frames, long SlotWaitUs, long GpuWaitUs, long CopyUs, long[] Busy);

        private sealed record Rates(double Seconds, long Frames, double Fps, double Speed, double SlotWait, double GpuWait, double Copy, double FfmpegBusy);

        public SpeedMeter(SharedControl shared, List<StreamState> streams, int fps, int totalFrames)
        {
            _shared = shared;
            _streams = streams;
            _fps = fps;
            _totalFrames = totalFrames;
            _screenMode = shared.StatusFlags.HasFlag(Status.ScreenCapture);
            _start = _last = Take();
        }

        private long MinWritten() => _streams.Where(s => !s.Failed).Select(s => s.Written).DefaultIfEmpty(0).Min();

        private Snapshot Take() => new(Stopwatch.GetTimestamp(), MinWritten(), _shared.StatSlotWaitUs, _shared.StatGpuWaitUs, _shared.StatCopyUs,
                                       _streams.Select(s => s.Writer?.BusyTicks ?? 0).ToArray());

        // 첫 프레임을 영상에 넣을 때부터 잼
        public void Begin()
        {
            if (_started) return;
            _started = true;
            _start = _last = Take();
        }

        private static Rates Between(Snapshot a, Snapshot b, int fps)
        {
            double seconds = Math.Max(1e-6, (b.Ticks - a.Ticks) / (double)Stopwatch.Frequency);
            long frames = b.Frames - a.Frames;
            double busy = 0;
            for (int i = 0; i < b.Busy.Length && i < a.Busy.Length; i++)
                busy = Math.Max(busy, (b.Busy[i] - a.Busy[i]) / (double)Stopwatch.Frequency / seconds);
            double perSecond = frames / seconds;
            return new Rates(seconds, frames, perSecond, perSecond / fps,
                (b.SlotWaitUs - a.SlotWaitUs) / 1e6 / seconds, (b.GpuWaitUs - a.GpuWaitUs) / 1e6 / seconds,
                (b.CopyUs - a.CopyUs) / 1e6 / seconds, busy);
        }

        private static string Pct(double fraction) =>
            ((int)Math.Round(Math.Clamp(fraction, 0, 1) * 100)).ToString(CultureInfo.InvariantCulture) + "%";

        private static string Num(double value, string format) => value.ToString(format, CultureInfo.InvariantCulture);

        // 1초마다 진행 줄 (창 프로그램이 "진행: N/M 프레임"을 읽음)
        public void Report()
        {
            if (!_started || Stopwatch.GetTimestamp() - _last.Ticks < Stopwatch.Frequency)
                return;
            var now = Take();
            var r = Between(_last, now, _fps);
            _last = now;
            Log($"진행: {now.Frames}" + (_totalFrames > 0 ? $"/{_totalFrames}" : "") + " 프레임" +
                $" · 초당 {Num(r.Fps, "0")}프레임 ({Num(r.Speed, "0.0#")}배속)" +
                $" · 앱 대기: 인코딩 {Pct(r.SlotWait)}, GPU 읽기 {Pct(r.GpuWait)}, 복사 {Pct(r.Copy)} · ffmpeg 쓰기 {Pct(r.FfmpegBusy)}");
        }

        // 녹화를 마칠 때 한 번: 평균 배속과 병목
        public void LogSummary()
        {
            if (!_started)
                return;
            var end = Take();
            var r = Between(_start, end, _fps);
            if (r.Frames <= 0)
                return;
            double frameMs = r.Seconds * 1000 / r.Frames;
            Log($"속도 요약: {r.Frames}프레임을 {Num(r.Seconds, "0.0")}초에 촬영 = 초당 {Num(r.Fps, "0.#")}프레임, {Num(r.Speed, "0.0#")}배속 " +
                $"(영상 {_fps}fps 기준, 프레임당 {Num(frameMs, "0.#")}ms)");
            Log($"  앱 대기: 인코딩 {Pct(r.SlotWait)}, GPU 읽기 {Pct(r.GpuWait)}, 복사 {Pct(r.Copy)} · ffmpeg 쓰기 {Pct(r.FfmpegBusy)}");

            string encoder = _streams.Select(s => s.Writer?.Encoder.EncoderName).FirstOrDefault(n => n != null) ?? "";
            const double Heavy = 0.25;
            if (r.SlotWait >= Heavy && r.SlotWait >= r.GpuWait && r.SlotWait >= r.Copy)
                Log(r.FfmpegBusy >= 0.8
                    ? $"  → 병목: 인코딩. ffmpeg({encoder})가 프레임을 받아 인코딩하는 속도가 한계라 앱이 기다렸습니다. " +
                      "영상 크기(앱 창 크기)를 줄이거나 촬영 카메라 수를 줄이면 빨라집니다. libx264이면 GPU 인코더를 쓸 수 있는지 확인하세요."
                    : "  → 병목: 캠코더가 프레임을 넘기는 쪽. ffmpeg는 여유가 있는데 프레임 전달이 밀렸습니다 (PC 메모리/CPU 여유 부족).");
            else if (r.GpuWait >= Heavy && r.GpuWait >= r.Copy)
                Log("  → 병목: GPU 읽기. GPU에서 프레임을 읽어 오길 기다린 시간이 깁니다. 영상 크기를 줄이면 빨라집니다." +
                    (_screenMode ? " (화면 읽기 방식은 동기 읽기라 느립니다)" : ""));
            else if (r.Copy >= Heavy)
                Log("  → 병목: 프레임 복사. 영상 크기가 크거나 카메라가 많아 복사가 오래 걸립니다.");
            else
                Log($"  → 캠코더 쪽 대기는 적습니다. 시간 대부분(프레임당 약 {Num(frameMs * Math.Max(0, 1 - r.SlotWait - r.GpuWait - r.Copy), "0.#")}ms)을 " +
                    "앱이 한 프레임을 만드는 데(게임 로직·렌더링) 썼으므로, 이 PC에서 이 앱이 낼 수 있는 속도에 가깝습니다.");
            int resets = _shared.StatFrameLimitResets;
            if (resets > 0)
                Log($"  참고: 앱이 녹화 중에 프레임 제한/수직 동기화를 {resets}번 다시 걸어 플러그인이 다시 풀었습니다.");
        }
    }

    // 조건이 참이 되거나 타임아웃/중단될 때까지 폴링
    private static bool WaitFor(Func<bool> condition, int timeoutMs, Func<bool> abort)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            if (abort()) return false;
            Thread.Sleep(50);
        }
        return condition();
    }

    private static int Fail(string message)
    {
        Log("실패: " + message);
        return 1;
    }

    private static void Log(string message)
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
    }
}
