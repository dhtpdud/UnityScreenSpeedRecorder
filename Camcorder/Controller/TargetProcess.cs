using System.Diagnostics;
using System.Runtime.InteropServices;

namespace UnityCamcorder;

// 대상 유니티 앱을 실행하거나 이미 떠 있는 것을 찾고, 비트 수를 판별해 맞는 주입기로 DLL을 넣습니다.
public sealed class TargetProcess
{
    public Process Process { get; }
    public bool Is64Bit { get; }
    public uint Pid => (uint)Process.Id;

    private TargetProcess(Process process, bool is64)
    {
        Process = process;
        Is64Bit = is64;
    }

    public static TargetProcess LaunchOrAttach(Settings settings)
    {
        Process process;
        if (settings.TargetPid > 0)
        {
            process = Process.GetProcessById(settings.TargetPid);
        }
        else if (!string.IsNullOrWhiteSpace(settings.TargetProcessName))
        {
            var name = Path.GetFileNameWithoutExtension(settings.TargetProcessName);
            var matches = Process.GetProcessesByName(name);
            if (matches.Length == 0)
                throw new InvalidOperationException($"'{settings.TargetProcessName}' 프로세스를 찾을 수 없습니다.");
            process = matches[0];
        }
        else
        {
            var exe = settings.TargetExe!;
            if (!File.Exists(exe))
                throw new FileNotFoundException($"대상 exe를 찾을 수 없습니다: {exe}");
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(exe))!,
            };
            if (!string.IsNullOrWhiteSpace(settings.TargetArgs))
                psi.Arguments = settings.TargetArgs;
            process = Process.Start(psi) ?? throw new InvalidOperationException("대상 실행 실패");
        }

        bool is64 = IsProcess64Bit(process);
        return new TargetProcess(process, is64);
    }

    // DLL 파일이 로드되어 공유 메모리를 열 때까지, 맞는 주입기로 주입합니다.
    public void Inject(string toolDir)
    {
        string arch = Is64Bit ? "x64" : "x86";
        string injector = Path.Combine(toolDir, arch, "Inject.exe");
        string dll = Path.Combine(toolDir, arch, "UnityCamcorderHook.dll");
        if (!File.Exists(injector) || !File.Exists(dll))
            throw new FileNotFoundException($"주입기 또는 DLL이 없습니다: {injector} / {dll}");

        var psi = new ProcessStartInfo(injector)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(Pid.ToString());
        psi.ArgumentList.Add(Path.GetFullPath(dll));
        var inj = Process.Start(psi)!;
        string err = inj.StandardError.ReadToEnd();
        inj.WaitForExit();
        if (inj.ExitCode != 0)
            throw new InvalidOperationException($"DLL 주입 실패 (코드 {inj.ExitCode}): {err.Trim()}");
    }

    // 대상이 응답하는지 (멈춤 감지 보조용)
    public bool IsResponding
    {
        get
        {
            try { Process.Refresh(); return Process.Responding; }
            catch { return false; }
        }
    }

    public bool HasExited
    {
        get { try { return Process.HasExited; } catch { return true; } }
    }

    public static bool IsProcess64Bit(Process process)
    {
        if (!Environment.Is64BitOperatingSystem)
            return false;
        IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, process.Id);
        if (handle == IntPtr.Zero)
            handle = OpenProcess(PROCESS_QUERY_INFORMATION, false, process.Id);
        if (handle == IntPtr.Zero)
            return Environment.Is64BitOperatingSystem;   // 알 수 없으면 OS 기준
        try
        {
            // IsWow64Process2: WOW64(32비트)로 돌면 processMachine != IMAGE_FILE_MACHINE_UNKNOWN
            if (IsWow64Process2(handle, out ushort processMachine, out _))
                return processMachine == IMAGE_FILE_MACHINE_UNKNOWN;  // 네이티브 = 64비트 OS에서 64비트
            if (IsWow64Process(handle, out bool wow64))
                return !wow64;
            return Environment.Is64BitOperatingSystem;
        }
        finally { CloseHandle(handle); }
    }

    private const int PROCESS_QUERY_INFORMATION = 0x0400;
    private const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const ushort IMAGE_FILE_MACHINE_UNKNOWN = 0;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsWow64Process(IntPtr process, out bool wow64);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);
}
