using System.Text.Json;
using System.Text.Json.Serialization;

namespace UnityCamcorder;

// 캠코더 설정. camcorder.json 에서 읽고, 명령줄 인자로 덮어쓸 수 있습니다.
public sealed class Settings
{
    // --- 대상 유니티 앱 ---
    // 둘 중 하나: 실행할 exe 경로(TargetExe) 또는 이미 떠 있는 프로세스(TargetProcessName/TargetPid)
    public string? TargetExe { get; set; }
    public string? TargetArgs { get; set; }
    public string? TargetProcessName { get; set; }
    public int TargetPid { get; set; }

    // --- 촬영 카메라 ---
    // 비우면 화면에 보이는 그대로(화면에 그리던 카메라 전부) 한 영상으로 찍습니다.
    // 채우면 카메라마다 영상 하나씩: 카메라 이름 또는 "#instanceId" (UnityCamcorder --list-cameras 로 확인)
    public List<string> Cameras { get; set; } = new();

    // --- 결과 영상 ---
    public string OutputPath { get; set; } = "output.mp4";   // 확장자로 컨테이너(mp4/mkv/webm/mov) 결정
    public int Fps { get; set; } = 60;                        // 결과 영상 프레임레이트(= 촬영 captureFramerate)
    public int TotalFrames { get; set; } = 0;                 // 캡처할 총 프레임 수 (0이면 DurationSeconds 사용)
    public double DurationSeconds { get; set; } = 0;          // 총 영상 길이(초). TotalFrames가 0일 때 사용

    // --- 인코딩 ---
    // "auto" = GPU H.264 자동 탐지(NVENC→QSV→AMF→libx264). 그 외에는 ffmpeg 인코더 이름을 그대로 사용.
    public string Codec { get; set; } = "auto";
    public int Quality { get; set; } = 23;                    // CQ/CRF 값 (낮을수록 고화질)
    public string ExtraFfmpegArgs { get; set; } = "";         // 고급 사용자용 추가 인자
    public string? FfmpegPath { get; set; }                   // 비우면 exe 옆 → PATH 순으로 탐색

    // --- 촬영 ---
    public int SpeedLimit { get; set; } = 0;                  // 최대 배속 제한(0 = 하드웨어 최대). 참고용
    public int WarmupFrames { get; set; } = 2;               // 시간 제어 적용 직후 버릴 프레임 수
    public int SlotCount { get; set; } = 16;                  // 링 버퍼(공유 메모리) 슬롯 수. 카메라가 여러 대면 카메라당 4개 이상으로 자동으로 늘림
    public int BufferMemoryMB { get; set; } = 512;            // ffmpeg에 넘기기 전 프레임을 쌓아 둘 메모리 (카메라 전체 합계)

    // --- 안정성 ---
    public int FreezeTimeoutMs { get; set; } = 10000;         // 이 시간 동안 새 프레임이 없으면 멈춘 것으로 보고 마무리
    public bool CloseTargetWhenDone { get; set; } = true;     // 녹화가 끝나면 대상 앱을 종료

    // --- 플러그인 ---
    // 앱을 실행해서 녹화할 때 앱 폴더에 BepInEx + 캠코더 플러그인이 없거나 다른 버전이면 자동으로 설치
    public bool AutoInstallPlugin { get; set; } = true;
    public int PluginConnectTimeoutMs { get; set; } = 90000;  // 앱 안의 플러그인이 연결될 때까지 기다리는 시간 (앱 로딩 포함)

    public static Settings Load(string path)
    {
        if (!File.Exists(path))
            return new Settings();
        var json = File.ReadAllText(path);
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        return JsonSerializer.Deserialize<Settings>(json, options) ?? new Settings();
    }

    public void Save(string path)
    {
        var options = new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
        File.WriteAllText(path, JsonSerializer.Serialize(this, options));
    }

    // 캡처할 총 프레임 수 (0이면 무제한 = 대상이 끝날 때까지)
    public int ResolveTotalFrames()
    {
        if (TotalFrames > 0) return TotalFrames;
        if (DurationSeconds > 0) return (int)Math.Round(DurationSeconds * Fps);
        return 0;
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(TargetExe) && string.IsNullOrWhiteSpace(TargetProcessName) && TargetPid <= 0)
            throw new ArgumentException("대상이 지정되지 않았습니다. TargetExe 또는 TargetProcessName 또는 TargetPid 중 하나를 설정하세요.");
        if (Fps <= 0)
            throw new ArgumentException("Fps는 1 이상이어야 합니다.");
        if (Cameras.Count > SharedControl.MaxStreams)
            throw new ArgumentException($"촬영 카메라는 최대 {SharedControl.MaxStreams}대까지 고를 수 있습니다.");
        if (SlotCount is < 2 or > SharedControl.MaxSlots)
            throw new ArgumentException($"SlotCount는 2~{SharedControl.MaxSlots} 사이여야 합니다.");
        if (BufferMemoryMB < 0)
            throw new ArgumentException("BufferMemoryMB는 0 이상이어야 합니다.");
    }
}
