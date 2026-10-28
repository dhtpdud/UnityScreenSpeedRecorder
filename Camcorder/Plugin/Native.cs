using System;
using System.Runtime.InteropServices;

namespace UnityCamcorder.Plugin
{
    // 캠코더가 만든 이름 있는 공유 메모리/이벤트를 여는 Windows API.
    // 유니티 2018의 .NET 3.5 Mono에는 MemoryMappedFile이 없어 kernel32를 직접 부릅니다.
    internal static class Native
    {
        public const uint FILE_MAP_ALL_ACCESS = 0xF001F;
        public const uint EVENT_MODIFY_STATE = 0x0002;
        public const uint SYNCHRONIZE = 0x00100000;
        public const uint WAIT_TIMEOUT = 0x102;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "OpenFileMappingW")]
        public static extern IntPtr OpenFileMapping(uint access, bool inherit, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr MapViewOfFile(IntPtr mapping, uint access, uint offsetHigh, uint offsetLow, UIntPtr bytes);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool UnmapViewOfFile(IntPtr address);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "OpenEventW")]
        public static extern IntPtr OpenEvent(uint access, bool inherit, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetEvent(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentProcessId();

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")]
        public static extern void CopyMemory(IntPtr dest, IntPtr src, UIntPtr count);

        public static void Close(ref IntPtr handle)
        {
            if (handle != IntPtr.Zero)
                CloseHandle(handle);
            handle = IntPtr.Zero;
        }
    }
}
