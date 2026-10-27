using System.Diagnostics;

namespace UnityCamcorder;

// 전체 녹화 흐름을 묶습니다: 대상 실행/붙기 → DLL 주입 → 배속 촬영 시작 →
// 공유 메모리 링 버퍼에서 프레임을 받아 ffmpeg로 흘려보냄 → 목표 프레임/멈춤/크래시 시 안전하게 마무리.
public sealed class RecordingSession
{
    private readonly Settings _settings;
    private readonly string _toolDir;

    public RecordingSession(Settings settings, string toolDir)
    {
        _settings = settings;
        _toolDir = toolDir;
    }

    public int Run()
    {
        _settings.Validate();
        // ffmpeg를 녹화 시작 전에 미리 찾아 둠 (없으면 바로 실패)
        string ffmpeg = FfmpegEncoder.ResolveFfmpeg(_settings);
        Log($"ffmpeg: {ffmpeg}");

        var target = TargetProcess.LaunchOrAttach(_settings);
        Log($"대상 프로세스: PID {target.Pid} ({(target.Is64Bit ? "64비트" : "32비트")})");

        using var shared = new SharedControl(target.Pid, _settings.SlotCount);
        target.Inject(_toolDir);
        Log("DLL 주입 완료, 후킹 대기 중...");

        if (!WaitFor(() => shared.StatusFlags.HasFlag(SharedControl.Status.Hooked) && shared.BackbufferWidth > 0,
                     30000, () => target.HasExited))
        {
            if (target.HasExited)
                return Fail("후킹 전에 대상이 종료되었습니다.");
            return Fail($"후킹 시간 초과. 상태={shared.StatusFlags}, 오류={shared.LastError}");
        }
        Log($"후킹 완료. 런타임: {shared.RuntimeInfo} / 그래픽스 API: {ApiName(shared.GraphicsApi)} / 해상도: {shared.BackbufferWidth}x{shared.BackbufferHeight}");
        if (shared.StatusFlags.HasFlag(SharedControl.Status.TimeFailed))
            Log("경고: 배속(captureFramerate) 제어를 쓸 수 없습니다. 실시간 속도로 캡처합니다.");

        // 백버퍼 해상도에 맞춰 링 버퍼 크기 결정 (RGBA 4바이트 기준)
        int width = shared.BackbufferWidth;
        int height = shared.BackbufferHeight;
        shared.SlotSize = (long)width * height * 4;
        int session = shared.SessionId + 1;
        shared.OpenFrameRing(session);

        // 촬영 시작 명령
        shared.Fps = _settings.Fps;
        shared.SpeedLimitPermille = _settings.SpeedLimit * 1000;
        shared.ReadCount = 0;
        shared.Command = SharedControl.CmdRecord;
        shared.SessionId = session;   // 마지막에 세션을 올려 DLL이 링 버퍼를 열게 함
        Log($"촬영 시작 (session {session}, fps {_settings.Fps})");

        int result = CaptureLoop(shared, target, ffmpeg);

        // 촬영 중지 → DLL이 시간 설정을 원래대로 되돌림
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

    private int CaptureLoop(SharedControl shared, TargetProcess target, string ffmpeg)
    {
        int totalFrames = _settings.ResolveTotalFrames();
        int warmup = Math.Max(0, _settings.WarmupFrames);
        FfmpegEncoder? encoder = null;
        var buffer = new byte[shared.SlotSize];
        long videoFrames = 0;
        int encWidth = 0, encHeight = 0;

        var freezeWatch = Stopwatch.StartNew();
        long lastWrite = 0;
        var progress = Stopwatch.StartNew();

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
                    long dataSize = shared.SlotDataSize(slot);
                    if (dataSize > buffer.Length) dataSize = buffer.Length;
                    shared.ReadSlot(slot, buffer, (int)dataSize);

                    // 소비 완료 표시 (DLL이 슬롯을 재사용하도록)
                    shared.ReadCount = frameIndex + 1;
                    shared.SignalSlotFree();

                    // 시간 제어 적용 직후의 불안정한 프레임은 버림
                    if (frameIndex < warmup)
                        continue;

                    // 첫 유효 프레임에서 ffmpeg 시작
                    if (encoder == null)
                    {
                        encWidth = w; encHeight = h;
                        encoder = FfmpegEncoder.Start(_settings, w, h, fmt);
                        Log($"인코딩 시작: {encoder.EncoderName}, {w}x{h}");
                    }
                    else if (w != encWidth || h != encHeight)
                    {
                        Log("캡처 중 해상도가 바뀌어 녹화를 마칩니다.");
                        goto finish;
                    }

                    if (!encoder.WriteFrame(buffer, (int)dataSize))
                    {
                        Log($"ffmpeg가 프레임을 받지 못했습니다. 최근 로그:\n{encoder.RecentLog()}");
                        goto finish;
                    }
                    videoFrames++;

                    if (progress.ElapsedMilliseconds > 1000)
                    {
                        Log($"진행: {videoFrames}" + (totalFrames > 0 ? $"/{totalFrames}" : "") + $" 프레임");
                        progress.Restart();
                    }
                    if (totalFrames > 0 && videoFrames >= totalFrames)
                    {
                        Log("목표 프레임 수 도달.");
                        goto finish;
                    }
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
            if (encoder != null)
            {
                bool ok = encoder.Finish();
                if (ok)
                    Log($"영상 저장 완료: {Path.GetFullPath(_settings.OutputPath)} ({encoder.EncoderName}, {encoder.WrittenFrames}프레임)");
                else
                    Log($"ffmpeg 종료 코드 {encoder.ExitCode}. 영상이 불완전할 수 있습니다. 로그:\n{encoder.RecentLog()}");
                encoder.Dispose();
            }
        }

        if (encoder == null)
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
