using System.Diagnostics;

namespace UnityCamcorder;

// 원본 RGBA/BGRA 프레임을 ffmpeg 표준 입력으로 흘려보내 영상을 인코딩합니다.
// 원본 프로젝트(RecorderFlusher)처럼 가능하면 GPU 하드웨어 인코더를 자동 선택합니다.
public sealed class FfmpegEncoder : IDisposable
{
    private readonly string _ffmpegPath;
    private Process? _process;
    private Stream? _stdin;
    private readonly List<string> _recentLog = new();
    private readonly object _logLock = new();

    public string EncoderName { get; private set; } = "";
    public long WrittenFrames { get; private set; }
    public bool HasExited => _process?.HasExited ?? true;
    public int ExitCode => _process?.ExitCode ?? -1;

    private FfmpegEncoder(string ffmpegPath) => _ffmpegPath = ffmpegPath;

    // ffmpeg.exe 탐색: 설정 → 캠코더 exe 옆 → PATH
    public static string ResolveFfmpeg(Settings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.FfmpegPath) && File.Exists(settings.FfmpegPath))
            return settings.FfmpegPath!;
        string dir = AppContext.BaseDirectory;
        foreach (var candidate in new[] { Path.Combine(dir, "ffmpeg.exe"), Path.Combine(dir, "ffmpeg", "ffmpeg.exe") })
            if (File.Exists(candidate))
                return candidate;
        // PATH
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var p in path.Split(Path.PathSeparator))
        {
            try { var c = Path.Combine(p.Trim(), "ffmpeg.exe"); if (File.Exists(c)) return c; }
            catch { /* 잘못된 PATH 항목 무시 */ }
        }
        throw new FileNotFoundException("ffmpeg.exe를 찾을 수 없습니다. 설정의 FfmpegPath를 지정하거나 캠코더 exe 옆에 두세요.");
    }

    public static FfmpegEncoder Start(Settings settings, int width, int height, int dxgiFormat)
    {
        var encoder = new FfmpegEncoder(ResolveFfmpeg(settings));
        string pixFmt = PixelFormatFor(dxgiFormat);
        (string name, string codecArgs) = encoder.SelectEncoder(settings);
        encoder.EncoderName = name;

        // 입력: 원본 프레임(파이프), 출력: 선택한 코덱으로 인코딩
        string args =
            $"-y -hide_banner -loglevel error " +
            $"-f rawvideo -pixel_format {pixFmt} -video_size {width}x{height} -framerate {settings.Fps} -i pipe:0 " +
            $"-vf vflip -an {codecArgs} -pix_fmt yuv420p " +
            (string.IsNullOrWhiteSpace(settings.ExtraFfmpegArgs) ? "" : settings.ExtraFfmpegArgs + " ") +
            $"\"{settings.OutputPath}\"";
        // 주: DXGI 백버퍼는 위→아래 순서지만, 알파 처리/일부 플립 모델에서 상하가 뒤집혀 보이는 경우가 있어 vflip로 맞춥니다.

        var psi = new ProcessStartInfo(encoder._ffmpegPath, args)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        var process = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg 실행 실패");
        encoder._process = process;
        encoder._stdin = process.StandardInput.BaseStream;
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (encoder._logLock)
            {
                encoder._recentLog.Add(e.Data);
                if (encoder._recentLog.Count > 30) encoder._recentLog.RemoveAt(0);
            }
        };
        process.BeginErrorReadLine();
        return encoder;
    }

    // 한 프레임(원본 바이트)을 ffmpeg로 보냅니다. ffmpeg가 먼저 죽으면 false.
    public bool WriteFrame(byte[] data, int length)
    {
        if (_stdin == null || (_process?.HasExited ?? true))
            return false;
        try
        {
            _stdin.Write(data, 0, length);
            WrittenFrames++;
            return true;
        }
        catch (IOException) { return false; }   // ffmpeg stdin이 닫힘(ffmpeg 종료)
    }

    // 입력을 닫고 ffmpeg가 남은 프레임을 다 쓰고 끝날 때까지 기다립니다 → 재생 가능한 영상 완성.
    public bool Finish(int timeoutMs = 60000)
    {
        if (_process == null) return false;
        try { _stdin?.Flush(); _stdin?.Close(); } catch { /* 이미 닫힘 */ }
        if (!_process.WaitForExit(timeoutMs))
        {
            try { _process.Kill(); } catch { }
            return false;
        }
        return _process.ExitCode == 0;
    }

    public string RecentLog()
    {
        lock (_logLock) return string.Join("\n", _recentLog);
    }

    // DXGI_FORMAT → ffmpeg 픽셀 포맷
    private static string PixelFormatFor(int dxgiFormat) => dxgiFormat switch
    {
        87 or 90 or 91 => "bgra",   // B8G8R8A8 UNORM / TYPELESS / SRGB
        88 or 92 or 93 => "bgra",   // B8G8R8X8
        27 or 28 or 29 => "rgba",   // R8G8B8A8
        _ => "bgra",                 // 알 수 없으면 가장 흔한 bgra로 시도
    };

    // GPU 인코더 자동 탐지 (auto일 때) — NVENC → QSV → AMF → libx264 순으로 실제 테스트 인코딩
    private (string name, string args) SelectEncoder(Settings settings)
    {
        if (!settings.Codec.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return (settings.Codec, $"-c:v {settings.Codec} {QualityArgs(settings.Codec, settings.Quality)}");

        int q = settings.Quality;
        var candidates = new (string name, string args)[]
        {
            ("h264_nvenc", $"-c:v h264_nvenc -preset p1 -rc vbr -cq {q} -b:v 0"),
            ("h264_qsv",   $"-c:v h264_qsv -preset veryfast -global_quality {q}"),
            ("h264_amf",   $"-c:v h264_amf -quality speed -rc cqp -qp_i {q} -qp_p {q}"),
            ("libx264",    $"-c:v libx264 -preset veryfast -crf {q}"),
        };
        foreach (var c in candidates)
        {
            if (TestEncoder(c.args))
                return c;
        }
        return ("libx264", $"-c:v libx264 -preset veryfast -crf {q}");
    }

    private static string QualityArgs(string codec, int q)
    {
        if (codec.Contains("nvenc")) return $"-rc vbr -cq {q} -b:v 0";
        if (codec.Contains("qsv")) return $"-global_quality {q}";
        if (codec.Contains("amf")) return $"-rc cqp -qp_i {q} -qp_p {q}";
        return $"-crf {q}";   // libx264/libx265/libvpx-vp9/libsvtav1 등
    }

    // 검은 화면 5프레임을 실제로 인코딩해 보고 성공하면 그 인코더를 사용 (RecorderFlusher와 동일한 방식)
    private bool TestEncoder(string codecArgs)
    {
        try
        {
            var psi = new ProcessStartInfo(_ffmpegPath,
                $"-hide_banner -loglevel error -f lavfi -i color=c=black:s=640x360:r=24 -frames:v 5 {codecArgs} -f null -")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            p.StandardError.ReadToEnd();
            if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } return false; }
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    public void Dispose()
    {
        try { if (_process != null && !_process.HasExited) _process.Kill(); } catch { }
        _process?.Dispose();
    }
}
