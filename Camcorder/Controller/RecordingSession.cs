using System.Diagnostics;
using System.Globalization;

namespace UnityCamcorder;

// 전체 녹화 흐름을 묶습니다: 대상 실행/붙기 → DLL 주입 → 배속 촬영 시작 →
// 공유 메모리 링 버퍼에서 프레임을 받아 ffmpeg로 흘려보냄 → 목표 프레임/멈춤/크래시 시 안전하게 마무리.
//
// 촬영 방식 (DLL이 정함)
// - 카메라 촬영(Mono 앱): 카메라의 targetTexture를 RenderTexture로 돌려 화면 대신 그곳에 그리게 하고 GPU 비동기 읽기.
//   촬영 카메라를 고르면 카메라마다 스트림(= 영상 파일) 하나. 안 고르면 화면에 그리던 카메라 전부를 한 영상으로.
// - 화면 캡처(IL2CPP 등): Present 직전 백버퍼를 읽음 (배속 없이 실시간).
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
        public FfmpegEncoder? Encoder;
        public long Received;      // 받은 프레임 (워밍업 포함)
        public long Written;       // 영상에 쓴 프레임
        public int Width, Height;
        public bool Failed;
    }

    public RecordingSession(Settings settings, string toolDir, CancellationToken stop = default)
    {
        _settings = settings;
        _toolDir = toolDir;
        _stop = stop;
    }

    // 대상 실행/붙기 → 주입 → 후킹 완료까지. 실패하면 null
    private (TargetProcess target, SharedControl shared)? Connect()
    {
        _settings.Validate();
        var target = TargetProcess.LaunchOrAttach(_settings);
        Log($"대상 프로세스: PID {target.Pid} ({(target.Is64Bit ? "64비트" : "32비트")})");

        var shared = new SharedControl(target.Pid, _settings.SlotCount);
        if (shared.StaleDll)
        {
            Fail($"이 앱에는 이전 버전 캠코더 DLL(v{shared.StaleDllVersion})이 이미 들어가 있습니다. " +
                 "DLL은 앱이 꺼질 때까지 바뀌지 않으니, 앱을 완전히 종료하고 다시 실행한 뒤 시도하세요.");
            shared.Dispose();
            return null;
        }
        try
        {
            target.Inject(_toolDir);
            Log("DLL 주입 완료, 후킹 대기 중...");
            // 앱이 막 켜진 경우 Mono(UnityEngine)가 올라올 때까지 DLL이 기다리므로 넉넉히
            if (!WaitFor(() => shared.StatusFlags.HasFlag(SharedControl.Status.Hooked) && shared.BackbufferWidth > 0,
                         90000, () => target.HasExited || _stop.IsCancellationRequested))
            {
                if (_stop.IsCancellationRequested)
                    Fail("후킹 전에 중지 요청을 받았습니다.");
                else if (target.HasExited)
                    Fail("후킹 전에 대상이 종료되었습니다.");
                else
                    Fail($"후킹 시간 초과. 상태={shared.StatusFlags}, 오류={shared.LastError}");
                shared.Dispose();
                return null;
            }
        }
        catch
        {
            shared.Dispose();
            throw;
        }
        Log($"후킹 완료. 런타임: {shared.RuntimeInfo} / 그래픽스 API: {ApiName(shared.GraphicsApi)} / 해상도: {shared.BackbufferWidth}x{shared.BackbufferHeight}");
        if (!shared.StatusFlags.HasFlag(SharedControl.Status.Tick))
            Log($"경고: {DescribeMainThread(shared)} 배속·카메라 촬영 없이 화면을 실시간으로 캡처합니다.");
        return (target, shared);
    }

    // 메인 스레드 틱(배속·카메라 촬영에 필요)이 왜 안 되는지 사람이 읽을 문장으로
    private static string DescribeMainThread(SharedControl shared)
    {
        var flags = shared.StatusFlags;
        if (flags.HasFlag(SharedControl.Status.RuntimeIl2cpp))
            return "IL2CPP로 빌드된 앱이라 유니티 메인 스레드에 들어갈 수 없습니다.";
        if (!flags.HasFlag(SharedControl.Status.Tick))
            return string.IsNullOrWhiteSpace(shared.LastError)
                ? "유니티 메인 스레드 콜백을 등록하지 못했습니다."
                : $"유니티 메인 스레드 콜백을 등록하지 못했습니다: {shared.LastError}";
        if (!flags.HasFlag(SharedControl.Status.MainThread))
            return "콜백은 등록했지만 앱이 아직 한 프레임도 부르지 않았습니다. (앱이 멈춰 있거나, 창이 최소화됐거나, 로딩 중)";
        return string.IsNullOrWhiteSpace(shared.LastError) ? "" : shared.LastError;
    }

    // 앱 안의 카메라 목록을 받아 옴. 카메라 촬영을 쓸 수 없는 앱이면 null
    private List<SharedControl.CameraInfo>? RequestCameraList(SharedControl shared, TargetProcess target)
    {
        if (!shared.StatusFlags.HasFlag(SharedControl.Status.Tick))
        {
            Log($"카메라 목록을 받을 수 없습니다. {DescribeMainThread(shared)} 화면 전체 촬영만 가능합니다.");
            return null;
        }
        int request = shared.CameraListRequest + 1;
        shared.CameraListRequest = request;
        if (!WaitFor(() => shared.CameraListDone == request, 20000, () => target.HasExited || _stop.IsCancellationRequested))
        {
            Log($"카메라 목록을 받지 못했습니다. {DescribeMainThread(shared)}");
            return null;
        }
        int count = shared.CameraCount;
        if (count < 0)
        {
            Log("이 앱은 카메라를 골라 찍을 수 없습니다. (IL2CPP 빌드 등) 화면 전체 촬영만 가능합니다.");
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
                if (c.Flags.HasFlag(SharedControl.CameraFlags.Main)) notes.Add("메인");
                if (!c.Flags.HasFlag(SharedControl.CameraFlags.Enabled)) notes.Add("꺼짐");
                if (c.Flags.HasFlag(SharedControl.CameraFlags.OwnTarget)) notes.Add("자체 RenderTexture");
                Log($"  #{c.InstanceId}  {c.Name}  {c.Width}x{c.Height}  depth {c.Depth.ToString(CultureInfo.InvariantCulture)}" +
                    (notes.Count > 0 ? $"  ({string.Join(", ", notes)})" : ""));
                // 기계용 한 줄: id, 가로, 세로, 켜짐, 메인, 자체RT, depth, 디스플레이, 이름
                Console.WriteLine(string.Join("\t", "@camera", c.InstanceId, c.Width, c.Height,
                    c.Flags.HasFlag(SharedControl.CameraFlags.Enabled) ? 1 : 0,
                    c.Flags.HasFlag(SharedControl.CameraFlags.Main) ? 1 : 0,
                    c.Flags.HasFlag(SharedControl.CameraFlags.OwnTarget) ? 1 : 0,
                    c.Depth.ToString(CultureInfo.InvariantCulture), c.TargetDisplay, c.Name.Replace('\t', ' ')));
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

        if (shared.StatusFlags.HasFlag(SharedControl.Status.TimeFailed))
            Log("경고: 배속(captureFramerate) 제어를 쓸 수 없습니다. 실시간 속도로 캡처합니다.");

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

        // 영상 크기: 카메라가 자기 RT 없이 그릴 땐 지금 화면(백버퍼) 크기로 RenderTexture를 만듦
        int width = shared.BackbufferWidth;
        int height = shared.BackbufferHeight;
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

        int session = shared.SessionId + 1;
        shared.OpenFrameRing(session);

        // 촬영 시작 명령
        shared.Fps = _settings.Fps;
        shared.SpeedLimitPermille = _settings.SpeedLimit * 1000;
        shared.StreamCount = cameras.Count;
        for (int i = 0; i < cameras.Count; i++)
            shared.SetStreamCameraId(i, cameras[i].InstanceId);
        // 이전 실행에서 쌓인 카운터를 이어서 씀 (DLL이 이미 들어가 있던 앱에 다시 붙은 경우)
        shared.ReadCount = shared.WriteCount;
        shared.Command = SharedControl.CmdRecord;
        shared.SessionId = session;   // 마지막에 세션을 올려 DLL이 링 버퍼를 열게 함

        int result;
        if (!WaitFor(() => shared.AppliedSessionId == session, 15000, () => target.HasExited || _stop.IsCancellationRequested))
        {
            result = Fail(target.HasExited ? "촬영 준비 중에 대상이 종료되었습니다."
                                           : $"앱이 촬영 준비에 응답하지 않습니다. {DescribeMainThread(shared)}".TrimEnd());
        }
        else if (cameras.Count > 0 && !shared.StatusFlags.HasFlag(SharedControl.Status.CameraCapture))
        {
            result = Fail($"고른 카메라로 촬영을 시작하지 못했습니다. {shared.LastError}");
        }
        else
        {
            bool cameraMode = shared.StatusFlags.HasFlag(SharedControl.Status.CameraCapture);
            Log(cameraMode
                ? $"촬영 방식: 카메라 RenderTexture (화면에 그리지 않고 카메라 출력을 GPU에서 바로 읽음), 영상 {streams.Count}개"
                : "촬영 방식: 화면(백버퍼) 캡처");
            Log(shared.StatusFlags.HasFlag(SharedControl.Status.TimeControl)
                ? $"배속 촬영: 켜짐 (게임 시간은 프레임당 1/{_settings.Fps}초, 프레임은 하드웨어가 허락하는 최대 속도)"
                : $"배속 촬영: 꺼짐 (실시간 캡처). {DescribeMainThread(shared)}".TrimEnd());
            foreach (var s in streams)
                Log($"  - {s.Label} → {Path.GetFullPath(s.OutputPath)}");
            Log($"촬영 시작 (session {session}, fps {_settings.Fps})");
            result = CaptureLoop(shared, target, streams);
        }

        // 촬영 중지 → DLL이 카메라와 시간 설정을 원래대로 되돌림
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
        var buffer = new byte[shared.SlotSize];

        var freezeWatch = Stopwatch.StartNew();
        long lastWrite = shared.WriteCount;
        var progress = Stopwatch.StartNew();

        long MinWritten() => streams.Where(s => !s.Failed).Select(s => s.Written).DefaultIfEmpty(0).Min();

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
                    if (dataSize > buffer.Length) dataSize = buffer.Length;
                    shared.ReadSlot(slot, buffer, (int)dataSize);

                    // 소비 완료 표시 (DLL이 슬롯을 재사용하도록)
                    shared.ReadCount = frameIndex + 1;
                    shared.SignalSlotFree();

                    if (streamIndex < 0 || streamIndex >= streams.Count)
                        continue;
                    var stream = streams[streamIndex];
                    if (stream.Failed)
                        continue;

                    // 시간 제어 적용 직후의 불안정한 프레임은 버림
                    if (stream.Received++ < warmup)
                        continue;
                    if (totalFrames > 0 && stream.Written >= totalFrames)
                        continue;

                    // 첫 유효 프레임에서 ffmpeg 시작
                    if (stream.Encoder == null)
                    {
                        stream.Width = w; stream.Height = h;
                        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(stream.OutputPath))!);
                        stream.Encoder = FfmpegEncoder.Start(_settings, w, h, fmt, stream.OutputPath);
                        Log($"인코딩 시작: {stream.Label} — {stream.Encoder.EncoderName}, {w}x{h}");
                    }
                    else if (w != stream.Width || h != stream.Height)
                    {
                        Log("캡처 중 해상도가 바뀌어 녹화를 마칩니다.");
                        goto finish;
                    }

                    if (!stream.Encoder.WriteFrame(buffer, (int)dataSize))
                    {
                        Log($"ffmpeg가 프레임을 받지 못했습니다 ({stream.Label}). 최근 로그:\n{stream.Encoder.RecentLog()}");
                        stream.Failed = true;
                        if (streams.All(s => s.Failed))
                            goto finish;
                        continue;
                    }
                    stream.Written++;

                    if (progress.ElapsedMilliseconds > 1000)
                    {
                        Log($"진행: {MinWritten()}" + (totalFrames > 0 ? $"/{totalFrames}" : "") + " 프레임");
                        progress.Restart();
                    }
                    if (totalFrames > 0 && streams.All(s => s.Failed || s.Written >= totalFrames))
                    {
                        Log("목표 프레임 수 도달.");
                        goto finish;
                    }
                }

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
            // 남은 프레임을 더 받지 않도록 먼저 촬영을 멈추고 ffmpeg를 닫음
            shared.Command = SharedControl.CmdIdle;
            foreach (var stream in streams)
            {
                var encoder = stream.Encoder;
                if (encoder == null)
                    continue;
                bool ok = encoder.Finish();
                if (ok)
                    Log($"영상 저장 완료: {Path.GetFullPath(stream.OutputPath)} ({encoder.EncoderName}, {encoder.WrittenFrames}프레임)");
                else
                    Log($"ffmpeg 종료 코드 {encoder.ExitCode}. 영상이 불완전할 수 있습니다 ({stream.Label}). 로그:\n{encoder.RecentLog()}");
                encoder.Dispose();
            }
        }

        if (streams.All(s => s.Encoder == null))
            return Fail("캡처된 프레임이 없습니다.");
        return 0;
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

    private static string ApiName(int api) => api switch { 11 => "D3D11", 12 => "D3D12", _ => "알 수 없음" };

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
