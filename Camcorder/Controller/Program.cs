using System.Text;
using UnityCamcorder;
using UnityCamcorder.Install;

// 외부 캠코더: 유니티 앱(2018.1~6000.x)을 실행/연결해 배속 촬영하고, 결과를 정상 속도 영상으로 저장합니다.
// 앱 안에서는 BepInEx 5 플러그인(UnityCamcorderPlugin.dll)이 배속과 카메라 촬영을 하고, 이 exe는 그것을 조종하며 인코딩합니다.
// 인코딩은 앱 밖에서 하므로 대상이 갑자기 꺼지거나 멈춰도 지금까지의 프레임으로 영상을 마무리합니다.

// GUI 등 다른 프로그램이 출력을 읽을 때 한글이 깨지지 않도록, 파이프로 나갈 때는 UTF-8로 바로바로 내보냄
if (Console.IsOutputRedirected)
    Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
if (Console.IsErrorRedirected)
    Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true });

if (args.Contains("--help") || args.Contains("-h"))
{
    PrintHelp();
    return 0;
}

string configPath = GetOption(args, "--config") ?? Path.Combine(AppContext.BaseDirectory, "camcorder.json");

// 기본 설정 파일 생성
if (args.Contains("--init"))
{
    var sample = new Settings
    {
        TargetExe = @"C:\Path\To\YourUnityApp.exe",
        OutputPath = @"C:\Recordings\output.mp4",
        Fps = 60,
        DurationSeconds = 10,
        Codec = "auto",
        Quality = 23,
    };
    sample.Save(configPath);
    Console.WriteLine($"기본 설정을 만들었습니다: {configPath}");
    Console.WriteLine("TargetExe, OutputPath 등을 채운 뒤 다시 실행하세요.");
    return 0;
}

var settings = Settings.Load(configPath);

// 명령줄로 설정 덮어쓰기 (설정 파일 없이도 쓸 수 있도록)
ApplyOverride(args, "--target", v => settings.TargetExe = v);
ApplyOverride(args, "--target-args", v => settings.TargetArgs = v);
ApplyOverride(args, "--process", v => settings.TargetProcessName = v);
ApplyOverride(args, "--pid", v => settings.TargetPid = int.Parse(v));
ApplyOverride(args, "--out", v => settings.OutputPath = v);
ApplyOverride(args, "--fps", v => settings.Fps = int.Parse(v));
ApplyOverride(args, "--frames", v => settings.TotalFrames = int.Parse(v));
ApplyOverride(args, "--duration", v => settings.DurationSeconds = double.Parse(v));
ApplyOverride(args, "--codec", v => settings.Codec = v);
ApplyOverride(args, "--quality", v => settings.Quality = int.Parse(v));
ApplyOverride(args, "--ffmpeg", v => settings.FfmpegPath = v);
ApplyOverride(args, "--cameras", v => settings.Cameras = v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList());
if (args.Contains("--keep-open")) settings.CloseTargetWhenDone = false;

// 플러그인/BepInEx 배포본 위치 (기본: exe 옆의 plugin/, bepinex/ 폴더)
string toolDir = GetOption(args, "--tools") ?? AppContext.BaseDirectory;

// 앱 폴더에 BepInEx + 캠코더 플러그인 설치 / 제거 (--target 으로 앱 지정)
if (args.Contains("--install") || args.Contains("--uninstall"))
{
    if (string.IsNullOrWhiteSpace(settings.TargetExe))
    {
        Console.Error.WriteLine("오류: --target <앱 exe 경로> 로 설치할 앱을 지정하세요.");
        return 1;
    }
    try
    {
        void Say(string message) => Console.WriteLine(message);
        if (args.Contains("--uninstall"))
        {
            PluginInstaller.Uninstall(settings.TargetExe!, Say);
            Console.WriteLine("제거했습니다.");
        }
        else
        {
            var state = PluginInstaller.Install(settings.TargetExe!, toolDir, Say);
            Console.WriteLine(state.Describe());
            if (PluginInstaller.IsRunning(settings.TargetExe!))
                Console.WriteLine("앱이 켜져 있습니다. 앱을 다시 시작해야 플러그인이 적용됩니다.");
        }
        return 0;
    }
    catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine("오류: " + ex.Message);
        return 1;
    }
}

// 중지 요청: Ctrl+C(한 번 더 누르면 강제 종료) 또는 표준 입력으로 "stop" 한 줄 (GUI가 사용)
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    if (stop.IsCancellationRequested) return;
    e.Cancel = true;
    stop.Cancel();
};
if (Console.IsInputRedirected)
{
    new Thread(() =>
    {
        string? line;
        while ((line = Console.In.ReadLine()) != null)
            if (line.Trim().Equals("stop", StringComparison.OrdinalIgnoreCase)) { stop.Cancel(); break; }
    }) { IsBackground = true }.Start();
}

try
{
    var session = new RecordingSession(settings, toolDir, stop.Token);
    if (args.Contains("--list-cameras"))
    {
        // 목록만 보고 앱은 켜 둠 (이어서 --pid 로 녹화)
        settings.CloseTargetWhenDone = false;
        return session.ListCameras();
    }
    return session.Run();
}
catch (Exception ex)
{
    Console.Error.WriteLine("오류: " + ex.Message);
    return 1;
}

static void ApplyOverride(string[] args, string name, Action<string> apply)
{
    var value = GetOption(args, name);
    if (value != null) apply(value);
}

static string? GetOption(string[] args, string name)
{
    for (int i = 0; i < args.Length - 1; i++)
        if (args[i] == name)
            return args[i + 1];
    return null;
}

static void PrintHelp()
{
    Console.WriteLine(@"UnityCamcorder - 유니티 앱 배속 캠코더 (BepInEx 5 플러그인 방식)

사용법:
  UnityCamcorder --init                       기본 설정 파일(camcorder.json) 생성
  UnityCamcorder [--config <경로>] [옵션...]    녹화 실행
  UnityCamcorder --install --target <exe>     앱 폴더에 BepInEx 5 + 캠코더 플러그인 설치 (녹화할 때도 자동으로 함)
  UnityCamcorder --uninstall --target <exe>   설치한 플러그인 제거 (BepInEx도 캠코더가 설치했고 다른 플러그인이 없으면 함께)

대상 지정 (셋 중 하나):
  --target <exe경로>      유니티 앱을 실행하고 녹화
  --process <이름>        이미 떠 있는 프로세스 이름으로 연결
  --pid <PID>             이미 떠 있는 프로세스 PID로 연결

촬영 카메라:
  --list-cameras          앱 안의 카메라 목록만 보여 주고 끝냄 (앱은 켜 둠)
  --cameras <목록>        찍을 카메라 이름 또는 #번호를 쉼표로 (예: ""Main Camera,#12345"")
                          카메라마다 영상이 따로 저장됨: out_카메라이름.mp4
                          지정하지 않으면 화면에 보이는 카메라 전부를 한 영상으로 촬영

결과 영상:
  --out <경로>            저장 경로 (확장자로 컨테이너 결정: .mp4 .mkv .webm .mov)
  --fps <정수>            결과 영상 프레임레이트 (기본 60)
  --frames <정수>         캡처할 총 프레임 수
  --duration <초>         총 영상 길이(초). --frames 미지정 시 사용
  --codec <이름>          auto(GPU H.264 자동) 또는 ffmpeg 인코더 이름(libx264, hevc_nvenc, libvpx-vp9 ...)
  --quality <정수>        CQ/CRF 값 (낮을수록 고화질, 기본 23)
  --ffmpeg <경로>         ffmpeg.exe 경로 (미지정 시 exe 옆 → PATH 탐색)

기타:
  --target-args <문자열>  대상 앱에 전달할 실행 인자
  --tools <폴더>          플러그인/BepInEx 배포본 폴더 (plugin/, bepinex/ 하위 폴더 포함, 기본 exe 옆)
  --keep-open             녹화 후 대상 앱을 종료하지 않음
                          (녹화 중 Ctrl+C 또는 표준 입력 ""stop""으로 중지하면 그때까지의 영상을 마무리)
  --config <경로>         설정 파일 경로
  --help                  이 도움말");
}
