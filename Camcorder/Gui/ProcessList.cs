using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace UnityCamcorderGui
{
    // "실행 중인 앱에 연결"용 목록 항목
    public sealed class ProcessItem
    {
        public int Pid { get; set; }
        public string Name { get; set; } = "";
        public string Title { get; set; } = "";
        public bool IsUnity { get; set; }

        public override string ToString()
        {
            string title = string.IsNullOrWhiteSpace(Title) || Title == Name ? "" : $" — {Title}";
            return (IsUnity ? "[유니티] " : "") + $"{Name}{title}  (PID {Pid})";
        }
    }

    // 앱 안의 카메라 (UnityCamcorder --list-cameras 결과 한 줄)
    public sealed class CameraItem
    {
        public int InstanceId { get; set; }
        public string Name { get; set; } = "";
        public int Width { get; set; }
        public int Height { get; set; }
        public bool Enabled { get; set; }
        public bool IsMain { get; set; }
        public bool OwnTarget { get; set; }
        public float Depth { get; set; }
        public int Display { get; set; }

        public override string ToString()
        {
            var notes = new List<string>();
            if (IsMain) notes.Add("메인");
            if (!Enabled) notes.Add("꺼져 있음 → 촬영 중에만 켬");
            if (OwnTarget) notes.Add("자체 RenderTexture");
            if (Display != 0) notes.Add($"Display {Display + 1}");
            return $"{Name}   {Width}x{Height}" + (notes.Count > 0 ? $"   ({string.Join(", ", notes)})" : "");
        }
    }

    public static class ProcessList
    {
        // 창이 있는 프로세스만, 유니티 앱을 맨 위에 놓아 반환
        public static List<ProcessItem> GetWindowed()
        {
            int self = Process.GetCurrentProcess().Id;
            var items = new List<ProcessItem>();
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.Id == self || p.MainWindowHandle == IntPtr.Zero)
                        continue;
                    items.Add(new ProcessItem
                    {
                        Pid = p.Id,
                        Name = p.ProcessName,
                        Title = p.MainWindowTitle,
                        IsUnity = LooksLikeUnity(p.Id),
                    });
                }
                catch { /* 접근 불가 프로세스는 건너뜀 */ }
                finally { p.Dispose(); }
            }
            return items
                .OrderByDescending(i => i.IsUnity)
                .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // 유니티 빌드는 exe 옆에 UnityPlayer.dll 또는 "<exe이름>_Data" 폴더가 있습니다.
        private static bool LooksLikeUnity(int pid)
        {
            string? exe = GetImagePath(pid);
            if (exe == null) return false;
            string dir = Path.GetDirectoryName(exe) ?? "";
            return File.Exists(Path.Combine(dir, "UnityPlayer.dll"))
                || Directory.Exists(Path.Combine(dir, Path.GetFileNameWithoutExtension(exe) + "_Data"));
        }

        // 32/64비트 상관없이 exe 전체 경로를 얻음
        private static string? GetImagePath(int pid)
        {
            IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (handle == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(handle, 0, sb, ref size) ? sb.ToString() : null;
            }
            finally { CloseHandle(handle); }
        }

        private const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(int access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);
    }
}
