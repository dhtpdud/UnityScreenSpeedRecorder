using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

// 녹화할 유니티 앱 폴더에 BepInEx 5와 캠코더 플러그인을 설치/제거합니다.
// 창 프로그램(.NET Framework 4.8)과 캠코더(.NET 8)가 이 파일을 같이 컴파일하므로 두 런타임에 다 있는 API만 씁니다.
//
// 캠코더 배포 폴더:
//   plugin\UnityCamcorderPlugin.dll            캠코더 플러그인
//   bepinex\BepInEx_win_x64_<버전>.zip          BepInEx 5 (64비트 앱용, 공식 배포본 그대로)
//   bepinex\BepInEx_win_x86_<버전>.zip          BepInEx 5 (32비트 앱용)
// 앱 폴더에 설치되는 것:
//   winhttp.dll, doorstop_config.ini, BepInEx\core\...   (BepInEx가 없을 때만)
//   BepInEx\plugins\UnityCamcorder\UnityCamcorderPlugin.dll
//   BepInEx\plugins\UnityCamcorder\install.txt         (제거할 때 지울 파일 기록)
namespace UnityCamcorder.Install
{
    public sealed class AppInstallState
    {
        public string ExePath = "";
        public string GameDir = "";
        public bool Is64Bit = true;
        public string UnityVersion = "";          // 알 수 없으면 ""
        public string? BepInExVersion;            // 앱에 있는 BepInEx 5 버전 (없으면 null)
        public bool PluginInstalled;
        public string? PluginVersion;
        public bool PluginUpToDate;               // 캠코더에 들어 있는 플러그인과 같은 파일인지
        public bool BepInExByCamcorder;           // BepInEx를 캠코더가 설치했는지 (제거할 때 BepInEx까지 지움)
        public string? Problem;                   // 자동 설치를 할 수 없는 이유 (없으면 null)

        public bool Ready => Problem == null && BepInExVersion != null && PluginInstalled && PluginUpToDate;

        public string Bits => Is64Bit ? "64비트" : "32비트";

        // 창 프로그램에 보여 줄 한 줄 설명
        public string Describe()
        {
            string app = (UnityVersion.Length > 0 ? $"Unity {UnityVersion}" : "유니티 앱") + $" ({Bits})";
            if (Problem != null)
                return Problem;
            if (BepInExVersion == null)
                return $"{app}: BepInEx와 캠코더 플러그인이 아직 없습니다. 녹화를 시작하면 자동으로 설치합니다.";
            if (!PluginInstalled)
                return $"{app}: BepInEx {BepInExVersion}가 있고 캠코더 플러그인은 없습니다. 녹화를 시작하면 자동으로 설치합니다.";
            if (!PluginUpToDate)
                return $"{app}: 캠코더 플러그인이 다른 버전입니다({PluginVersion}). 녹화를 시작하면 자동으로 바꿉니다.";
            return $"{app}: 설치됨 (BepInEx {BepInExVersion}, 캠코더 플러그인 {PluginVersion})";
        }
    }

    public static class PluginInstaller
    {
        public const string PluginFileName = "UnityCamcorderPlugin.dll";
        public const string PluginFolderName = "UnityCamcorder";
        private const string ManifestName = "install.txt";

        // --- 캠코더 배포 폴더 안의 위치 ---
        public static string BundledPluginPath(string toolDir) => Path.Combine(toolDir, "plugin", PluginFileName);

        public static string? BundledBepInExZip(string toolDir, bool is64Bit)
        {
            string dir = Path.Combine(toolDir, "bepinex");
            if (!Directory.Exists(dir))
                return null;
            // 여러 개면 이름(버전)이 가장 뒤인 것
            return Directory.GetFiles(dir, $"BepInEx_win_{(is64Bit ? "x64" : "x86")}_*.zip")
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).LastOrDefault();
        }

        public static string PluginDir(string gameDir) => Path.Combine(gameDir, "BepInEx", "plugins", PluginFolderName);

        // BepInEx가 앱 시작 때 남기는 기록 (플러그인이 안 붙을 때 원인 확인용)
        public static string BepInExLogPath(string gameDir) => Path.Combine(gameDir, "BepInEx", "LogOutput.log");

        // --- 상태 확인 ---
        public static AppInstallState Inspect(string exePath, string toolDir)
        {
            var state = new AppInstallState { ExePath = Path.GetFullPath(exePath) };
            string gameDir = Path.GetDirectoryName(state.ExePath) ?? "";
            state.GameDir = gameDir;
            if (!File.Exists(state.ExePath))
            {
                state.Problem = $"앱 파일을 찾을 수 없습니다: {state.ExePath}";
                return state;
            }

            ushort machine = PeMachine(state.ExePath);
            state.Is64Bit = machine != 0x014C;   // IMAGE_FILE_MACHINE_I386이면 32비트
            if (machine == 0xAA64)
            {
                state.Problem = "ARM64용 앱은 지원하지 않습니다. (BepInEx 5는 x64/x86 앱용)";
                return state;
            }

            // 유니티 Mono 빌드인지: <이름>_Data\Managed\UnityEngine*.dll 이 있어야 하고, IL2CPP 흔적이 없어야 함
            string dataDir = Path.Combine(gameDir, Path.GetFileNameWithoutExtension(state.ExePath) + "_Data");
            string managed = Path.Combine(dataDir, "Managed");
            bool hasUnityPlayer = File.Exists(Path.Combine(gameDir, "UnityPlayer.dll"));
            state.UnityVersion = ReadUnityVersion(hasUnityPlayer ? Path.Combine(gameDir, "UnityPlayer.dll") : state.ExePath);
            if (!Directory.Exists(dataDir) && !hasUnityPlayer)
            {
                state.Problem = "유니티 앱이 아닌 것 같습니다. (앱 옆에 <앱이름>_Data 폴더가 없음)";
                return state;
            }
            if (File.Exists(Path.Combine(gameDir, "GameAssembly.dll")) || Directory.Exists(Path.Combine(dataDir, "il2cpp_data")))
            {
                state.Problem = "IL2CPP로 빌드된 앱이라 녹화할 수 없습니다. 캠코더 플러그인(BepInEx 5)은 Mono로 빌드된 앱에서만 동작합니다.";
                return state;
            }
            if (!File.Exists(Path.Combine(managed, "UnityEngine.CoreModule.dll")) && !File.Exists(Path.Combine(managed, "UnityEngine.dll")))
            {
                state.Problem = $"유니티 Mono 앱이 아닌 것 같습니다. ({managed}\\UnityEngine.dll 없음)";
                return state;
            }

            // 이미 있는 모드 로더
            string bepCore = Path.Combine(gameDir, "BepInEx", "core");
            if (File.Exists(Path.Combine(bepCore, "BepInEx.Core.dll")) || File.Exists(Path.Combine(bepCore, "BepInEx.Unity.Mono.dll")))
            {
                state.Problem = "이 앱에는 BepInEx 6이 설치되어 있습니다. 캠코더 플러그인은 BepInEx 5용이라 자동으로 설치하지 않습니다.";
                return state;
            }
            string bepDll = Path.Combine(bepCore, "BepInEx.dll");
            if (File.Exists(bepDll))
                state.BepInExVersion = FileVersionOf(bepDll) ?? "5";
            else if (Directory.Exists(Path.Combine(gameDir, "MelonLoader")))
            {
                state.Problem = "이 앱에는 MelonLoader가 설치되어 있어 BepInEx를 함께 설치하지 않습니다.";
                return state;
            }
            else if (File.Exists(Path.Combine(gameDir, "winhttp.dll")))
            {
                state.Problem = "앱 폴더에 이미 winhttp.dll이 있습니다(다른 도구가 넣은 파일). 겹치지 않도록 BepInEx를 자동으로 설치하지 않습니다.";
                return state;
            }
            if (state.BepInExVersion != null && !state.BepInExVersion.StartsWith("5", StringComparison.Ordinal))
            {
                state.Problem = $"이 앱의 BepInEx({state.BepInExVersion})는 캠코더 플러그인(BepInEx 5용)과 맞지 않습니다.";
                return state;
            }

            // 캠코더 플러그인
            string installed = Path.Combine(PluginDir(gameDir), PluginFileName);
            state.PluginInstalled = File.Exists(installed);
            if (state.PluginInstalled)
            {
                state.PluginVersion = FileVersionOf(installed) ?? "?";
                string bundled = BundledPluginPath(toolDir);
                state.PluginUpToDate = !File.Exists(bundled) || SameFile(installed, bundled);
            }
            state.BepInExByCamcorder = ReadManifest(gameDir).BepInExByCamcorder;
            return state;
        }

        // --- 설치 / 업데이트 ---
        // 실패하면 InvalidOperationException (사람이 읽을 이유). 앱이 켜져 있으면 새 플러그인은 앱을 다시 켤 때부터 적용됩니다.
        public static AppInstallState Install(string exePath, string toolDir, Action<string> log)
        {
            AppInstallState state = Inspect(exePath, toolDir);
            if (state.Problem != null)
                throw new InvalidOperationException(state.Problem);
            string pluginSource = BundledPluginPath(toolDir);
            if (!File.Exists(pluginSource))
                throw new InvalidOperationException($"캠코더 폴더에 플러그인이 없습니다: {pluginSource}");

            string gameDir = state.GameDir;
            Manifest manifest = ReadManifest(gameDir);
            if (state.BepInExVersion == null)
            {
                string zip = BundledBepInExZip(toolDir, state.Is64Bit)
                    ?? throw new InvalidOperationException($"캠코더 폴더에 {state.Bits}용 BepInEx가 없습니다: {Path.Combine(toolDir, "bepinex")}");
                log($"BepInEx 설치 ({Path.GetFileName(zip)}) → {gameDir}");
                string root = Path.GetFullPath(gameDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                using (ZipArchive archive = ZipFile.OpenRead(zip))
                {
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        if (entry.FullName.EndsWith("/", StringComparison.Ordinal) || entry.Name.Length == 0)
                            continue;   // 폴더
                        string relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                        string target = Path.GetFullPath(Path.Combine(gameDir, relative));
                        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                            continue;   // 앱 폴더 밖을 가리키는 항목은 무시
                        if (File.Exists(target))
                            continue;   // 이미 있는 파일은 건드리지 않음
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        entry.ExtractToFile(target);
                        manifest.Files.Add(relative);
                    }
                }
                manifest.BepInExByCamcorder = true;
            }

            string pluginDir = PluginDir(gameDir);
            Directory.CreateDirectory(pluginDir);
            string pluginTarget = Path.Combine(pluginDir, PluginFileName);
            try
            {
                File.Copy(pluginSource, pluginTarget, true);
            }
            catch (IOException ex)
            {
                throw new InvalidOperationException($"플러그인 파일을 바꾸지 못했습니다. 앱이 켜져 있으면 끄고 다시 시도하세요. ({ex.Message})");
            }
            catch (UnauthorizedAccessException)
            {
                throw new InvalidOperationException($"앱 폴더에 쓸 권한이 없습니다: {gameDir}\n(Program Files 아래라면 앱 폴더를 다른 곳으로 옮기거나 관리자 권한으로 실행하세요)");
            }
            WriteManifest(gameDir, manifest);
            log($"캠코더 플러그인 설치: {pluginTarget}");
            return Inspect(exePath, toolDir);
        }

        // --- 제거 ---
        // 캠코더 플러그인을 지우고, BepInEx를 캠코더가 설치했고 다른 플러그인이 없으면 BepInEx도 지웁니다.
        public static void Uninstall(string exePath, Action<string> log)
        {
            string full = Path.GetFullPath(exePath);
            string gameDir = Path.GetDirectoryName(full) ?? "";
            if (IsRunning(full))
                throw new InvalidOperationException("앱이 켜져 있어 파일을 지울 수 없습니다. 앱을 먼저 종료하세요.");
            Manifest manifest = ReadManifest(gameDir);
            string pluginDir = PluginDir(gameDir);
            if (Directory.Exists(pluginDir))
            {
                Directory.Delete(pluginDir, true);
                log($"캠코더 플러그인 제거: {pluginDir}");
            }
            if (!manifest.BepInExByCamcorder)
                return;

            string bepDir = Path.Combine(gameDir, "BepInEx");
            string pluginsDir = Path.Combine(bepDir, "plugins");
            string patchersDir = Path.Combine(bepDir, "patchers");
            bool others = (Directory.Exists(pluginsDir) && Directory.EnumerateFileSystemEntries(pluginsDir).Any()) ||
                          (Directory.Exists(patchersDir) && Directory.EnumerateFileSystemEntries(patchersDir).Any());
            if (others)
            {
                log("다른 BepInEx 플러그인이 있어 BepInEx는 그대로 둡니다.");
                return;
            }
            foreach (string relative in manifest.Files)
            {
                string target = Path.Combine(gameDir, relative);
                try { if (File.Exists(target)) File.Delete(target); }
                catch (Exception ex) { log($"지우지 못함: {target} ({ex.Message})"); }
            }
            if (Directory.Exists(bepDir))
                Directory.Delete(bepDir, true);   // 실행 중에 생긴 config, cache, 로그까지
            log($"BepInEx 제거: {bepDir}");
        }

        // 이 exe로 켜진 프로세스가 있는지
        public static bool IsRunning(string exePath)
        {
            string full = Path.GetFullPath(exePath);
            foreach (Process p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(full)))
            {
                try
                {
                    string? image = ImagePath(p.Id);
                    if (image != null && string.Equals(Path.GetFullPath(image), full, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch { /* 접근 불가 */ }
                finally { p.Dispose(); }
            }
            return false;
        }

        // 32/64비트 상관없이 실행 중인 프로세스의 exe 전체 경로
        public static string? ImagePath(int pid)
        {
            IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (handle == IntPtr.Zero)
                return null;
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(handle, 0, sb, ref size) ? sb.ToString() : null;
            }
            finally { CloseHandle(handle); }
        }

        // --- 도우미 ---
        private static ushort PeMachine(string path)
        {
            try
            {
                using (var stream = File.OpenRead(path))
                using (var reader = new BinaryReader(stream))
                {
                    stream.Seek(0x3C, SeekOrigin.Begin);
                    int peOffset = reader.ReadInt32();
                    stream.Seek(peOffset, SeekOrigin.Begin);
                    if (reader.ReadUInt32() != 0x00004550)   // "PE\0\0"
                        return 0;
                    return reader.ReadUInt16();
                }
            }
            catch { return 0; }
        }

        private static string ReadUnityVersion(string path)
        {
            try
            {
                FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
                string version = (info.ProductVersion ?? "").Trim();
                if (version.Length == 0)
                    version = (info.FileVersion ?? "").Trim();
                int cut = version.IndexOfAny(new[] { ' ', '_', '(' });
                return cut > 0 ? version.Substring(0, cut) : version;
            }
            catch { return ""; }
        }

        private static string? FileVersionOf(string path)
        {
            try
            {
                FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
                return string.IsNullOrWhiteSpace(info.FileVersion) ? null : info.FileVersion!.Trim();
            }
            catch { return null; }
        }

        private static bool SameFile(string a, string b)
        {
            try
            {
                if (new FileInfo(a).Length != new FileInfo(b).Length)
                    return false;
                using (SHA256 sha = SHA256.Create())
                using (var fa = File.OpenRead(a))
                using (var fb = File.OpenRead(b))
                    return sha.ComputeHash(fa).SequenceEqual(sha.ComputeHash(fb));
            }
            catch { return false; }
        }

        private sealed class Manifest
        {
            public bool BepInExByCamcorder;
            public readonly List<string> Files = new List<string>();
        }

        private static string ManifestPath(string gameDir) => Path.Combine(PluginDir(gameDir), ManifestName);

        private static Manifest ReadManifest(string gameDir)
        {
            var manifest = new Manifest();
            try
            {
                string path = ManifestPath(gameDir);
                if (!File.Exists(path))
                    return manifest;
                foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.StartsWith("bepinex=", StringComparison.Ordinal))
                        manifest.BepInExByCamcorder = true;
                    else if (line.StartsWith("file=", StringComparison.Ordinal) && line.Length > 5)
                        manifest.Files.Add(line.Substring(5));
                }
            }
            catch { /* 망가진 기록은 없는 것으로 */ }
            return manifest;
        }

        private static void WriteManifest(string gameDir, Manifest manifest)
        {
            var lines = new List<string>
            {
                "# UnityCamcorder 설치 기록: 캠코더 창의 '제거' 또는 UnityCamcorder.exe --uninstall 이 이 목록으로 지웁니다.",
            };
            if (manifest.BepInExByCamcorder)
                lines.Add("bepinex=installed-by-camcorder");
            lines.AddRange(manifest.Files.Distinct(StringComparer.OrdinalIgnoreCase).Select(f => "file=" + f));
            File.WriteAllLines(ManifestPath(gameDir), lines, new UTF8Encoding(false));
        }

        private const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(int access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
        private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);
    }
}
