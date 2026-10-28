using System;
using System.Text;
using System.Threading;
using UnityCamcorder.Protocol;

namespace UnityCamcorder.Plugin
{
    // 캠코더가 만든 제어 블록(공유 메모리)과 이벤트. 오프셋은 Shared/CamcorderProtocol.cs를 캠코더와 같이 씁니다.
    // 모든 메서드는 유니티 메인 스레드에서만 부릅니다.
    internal sealed unsafe class ControlBlock : IDisposable
    {
        private IntPtr _mapping;
        private byte* _base;
        private IntPtr _frameReady;     // 플러그인 → 캠코더: 새 프레임 있음
        private IntPtr _slotFree;       // 캠코더 → 플러그인: 슬롯 비었음

        private IntPtr _controller;
        private int _controllerPid;
        private int _lastControllerCheck;
        private bool _controllerAlive;

        public uint Pid { get; private set; }
        public byte* Base { get { return _base; } }
        public IntPtr FrameReadyEvent { get { return _frameReady; } }
        public IntPtr SlotFreeEvent { get { return _slotFree; } }

        private ControlBlock() { }

        // 캠코더가 이 앱(PID)용 제어 블록을 만들어 두었으면 엽니다. 없으면 null.
        // 규약 버전이 다르면 null을 돌려주고 problem에 이유를 담습니다 (캠코더가 알아보도록 payloadVersion은 적어 둠).
        public static ControlBlock TryOpen(uint pid, out string problem)
        {
            problem = null;
            IntPtr mapping = Native.OpenFileMapping(Native.FILE_MAP_ALL_ACCESS, false, Shared.ControlName(pid));
            if (mapping == IntPtr.Zero)
                return null;   // 캠코더가 아직 없음 (평소 상태)
            IntPtr view = Native.MapViewOfFile(mapping, Native.FILE_MAP_ALL_ACCESS, 0, 0, new UIntPtr((uint)Shared.ControlSize));
            if (view == IntPtr.Zero)
            {
                problem = "제어 블록 매핑 실패 (오류 " + System.Runtime.InteropServices.Marshal.GetLastWin32Error() + ")";
                Native.CloseHandle(mapping);
                return null;
            }
            byte* b = (byte*)view;
            uint magic = (uint)Thread.VolatileRead(ref *(int*)(b + Offsets.Magic));
            int version = Thread.VolatileRead(ref *(int*)(b + Offsets.Version));
            if (magic != Shared.Magic || version != Shared.Version)
            {
                // 캠코더와 플러그인 버전이 다름: 캠코더가 원인을 알려 줄 수 있게 이쪽 버전만 남김
                Thread.VolatileWrite(ref *(int*)(b + Offsets.PayloadVersion), Shared.Version);
                problem = "캠코더 규약 버전이 다릅니다 (캠코더 v" + version + ", 플러그인 v" + Shared.Version + "). 캠코더와 플러그인을 같은 버전으로 맞추세요.";
                Native.UnmapViewOfFile(view);
                Native.CloseHandle(mapping);
                return null;
            }

            var block = new ControlBlock { _mapping = mapping, _base = b, Pid = pid };
            block._frameReady = Native.OpenEvent(Native.EVENT_MODIFY_STATE | Native.SYNCHRONIZE, false, Shared.FrameReadyName(pid));
            block._slotFree = Native.OpenEvent(Native.EVENT_MODIFY_STATE | Native.SYNCHRONIZE, false, Shared.SlotFreeName(pid));
            block.WriteInt(Offsets.PayloadVersion, Shared.Version);
            return block;
        }

        // --- 필드 읽기/쓰기 (다른 프로세스와 공유하므로 매번 메모리에서 직접 읽음) ---
        public int ReadInt(int offset) { return Thread.VolatileRead(ref *(int*)(_base + offset)); }
        public void WriteInt(int offset, int value) { Thread.VolatileWrite(ref *(int*)(_base + offset), value); }
        public long ReadLong(int offset) { return Interlocked.Read(ref *(long*)(_base + offset)); }
        public void WriteLong(int offset, long value) { Interlocked.Exchange(ref *(long*)(_base + offset), value); }
        public long IncrementLong(int offset) { return Interlocked.Increment(ref *(long*)(_base + offset)); }
        public float ReadFloat(int offset) { Thread.MemoryBarrier(); return *(float*)(_base + offset); }
        public void WriteFloat(int offset, float value) { *(float*)(_base + offset) = value; Thread.MemoryBarrier(); }

        public Status StatusFlags { get { return (Status)ReadInt(Offsets.Status); } }

        public void AddStatus(Status flags)
        {
            int* p = (int*)(_base + Offsets.Status);
            int old;
            do { old = *p; } while (Interlocked.CompareExchange(ref *p, old | (int)flags, old) != old);
        }

        public void RemoveStatus(Status flags)
        {
            int* p = (int*)(_base + Offsets.Status);
            int old;
            do { old = *p; } while (Interlocked.CompareExchange(ref *p, old & ~(int)flags, old) != old);
        }

        public bool HasStatus(Status flags) { return (StatusFlags & flags) == flags; }

        // UTF-8 문자열을 0으로 끝나게 기록 (글자 중간에서 자르지 않음)
        public void WriteText(int offset, int maxBytes, string text)
        {
            WriteUtf8(_base + offset, maxBytes, text);
        }

        public static void WriteUtf8(byte* dest, int maxBytes, string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text ?? "");
            int length = Math.Min(bytes.Length, maxBytes - 1);
            while (length > 0 && length < bytes.Length && (bytes[length] & 0xC0) == 0x80)
                length--;   // 이어지는 바이트 앞에서 자르면 글자가 깨지므로 글자 시작까지 물러남
            for (int i = 0; i < length; i++)
                dest[i] = bytes[i];
            dest[length] = 0;
            Thread.MemoryBarrier();
        }

        // 캠코더가 살아 있는지 (0.5초마다 확인). 캠코더를 다시 실행하면 새 PID를 따라감
        public bool IsControllerAlive()
        {
            int pid = ReadInt(Offsets.ControllerPid);
            int now = Environment.TickCount;
            if (pid != _controllerPid)
            {
                Native.Close(ref _controller);
                _controllerPid = pid;
                _controller = pid > 0 ? Native.OpenProcess(Native.SYNCHRONIZE, false, pid) : IntPtr.Zero;
                _controllerAlive = _controller != IntPtr.Zero;
                _lastControllerCheck = now;
            }
            else if (unchecked(now - _lastControllerCheck) > 500)
            {
                _lastControllerCheck = now;
                _controllerAlive = _controller != IntPtr.Zero && Native.WaitForSingleObject(_controller, 0) == Native.WAIT_TIMEOUT;
            }
            return _controllerAlive;
        }

        public bool IsRecordRequested()
        {
            return ReadInt(Offsets.Command) == Shared.CmdRecord && IsControllerAlive();
        }

        public void SignalFrameReady()
        {
            if (_frameReady != IntPtr.Zero)
                Native.SetEvent(_frameReady);
        }

        public void Dispose()
        {
            if (_base != null)
                Native.UnmapViewOfFile((IntPtr)_base);
            _base = null;
            Native.Close(ref _mapping);
            Native.Close(ref _frameReady);
            Native.Close(ref _slotFree);
            Native.Close(ref _controller);
        }
    }
}
