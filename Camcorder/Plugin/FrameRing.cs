using System;
using UnityCamcorder.Protocol;

namespace UnityCamcorder.Plugin
{
    // 캡처한 프레임을 캠코더로 넘기는 공유 메모리 링 버퍼. 플러그인이 생산자, 캠코더가 소비자입니다.
    // 캠코더가 녹화 세션마다 slotCount x slotSize 크기의 매핑을 만들어 두고, 슬롯 메타(가로/세로/형식)는 제어 블록의 slots[]에 씁니다.
    internal sealed unsafe class FrameRing : IDisposable
    {
        private ControlBlock _control;
        private IntPtr _mapping;
        private byte* _base;
        private int _session = -1;
        private int _slotCount;
        private long _slotSize;
        private long _nextFrame;

        public bool IsOpen { get { return _base != null; } }

        public bool Open(ControlBlock control, int session, int width, int height, int bytesPerPixel, out string error)
        {
            Close();
            error = null;
            _control = control;
            _slotCount = control.ReadInt(Offsets.SlotCount);
            if (_slotCount <= 0) _slotCount = 8;
            if (_slotCount > Shared.MaxSlots) _slotCount = Shared.MaxSlots;
            _slotSize = control.ReadLong(Offsets.SlotSize);
            long needed = (long)width * height * bytesPerPixel;
            if (_slotSize < needed)
            {
                error = "프레임 슬롯이 너무 작음 (" + _slotSize + " < " + needed + ")";
                return false;
            }

            _mapping = Native.OpenFileMapping(Native.FILE_MAP_ALL_ACCESS, false, Shared.FramesName(control.Pid, session));
            if (_mapping == IntPtr.Zero)
            {
                error = "프레임 공유 메모리를 열 수 없음 (session " + session + ", 오류 " + System.Runtime.InteropServices.Marshal.GetLastWin32Error() + ")";
                return false;
            }
            long total = _slotSize * _slotCount;
            IntPtr view = Native.MapViewOfFile(_mapping, Native.FILE_MAP_ALL_ACCESS, 0, 0, new UIntPtr((ulong)total));
            if (view == IntPtr.Zero)
            {
                error = "프레임 공유 메모리 매핑 실패 (" + (total >> 20) + "MB, 오류 " + System.Runtime.InteropServices.Marshal.GetLastWin32Error() + ")";
                Close();
                return false;
            }
            _base = (byte*)view;
            _session = session;
            _nextFrame = 0;
            PluginLog.Info("프레임 링 버퍼 열림: session " + session + ", " + width + "x" + height + ", 슬롯 " + _slotCount + "개 x " + _slotSize + "바이트");
            return true;
        }

        public void Close()
        {
            if (_base != null)
                Native.UnmapViewOfFile((IntPtr)_base);
            _base = null;
            Native.Close(ref _mapping);
            _session = -1;
        }

        // 빈 슬롯을 얻습니다. 꽉 찼으면(캠코더가 인코딩을 못 따라오면) wait=true일 땐 빌 때까지 기다려
        // 게임도 함께 멈추게 하고(배속 촬영: 프레임을 버리지 않음), wait=false일 땐 이번 프레임을 버립니다.
        public bool BeginWrite(out int slot, out byte* dest, out long capacity, bool wait)
        {
            slot = 0;
            dest = null;
            capacity = 0;
            if (_base == null)
                return false;
            while (_control.ReadLong(Offsets.WriteCount) - _control.ReadLong(Offsets.ReadCount) >= _slotCount)
            {
                if (!wait)
                    return false;
                // 녹화가 끝났거나 캠코더가 죽었으면 포기
                if (!_control.IsRecordRequested() || _control.ReadInt(Offsets.SessionId) != _session)
                    return false;
                if (_control.SlotFreeEvent != IntPtr.Zero)
                    Native.WaitForSingleObject(_control.SlotFreeEvent, 100);
                else
                    System.Threading.Thread.Sleep(1);
            }
            slot = (int)(_control.ReadLong(Offsets.WriteCount) % _slotCount);
            dest = _base + (long)slot * _slotSize;
            capacity = _slotSize;
            return true;
        }

        // 복사를 마치고 캠코더에 알립니다. stream = 몇 번째 촬영 카메라의 프레임인지
        public void EndWrite(int slot, int width, int height, int format, int bytesPerPixel, long dataSize, int stream)
        {
            int o = Offsets.Slot(slot);
            _control.WriteInt(o + Offsets.SlotWidth, width);
            _control.WriteInt(o + Offsets.SlotHeight, height);
            _control.WriteInt(o + Offsets.SlotFormat, format);
            _control.WriteInt(o + Offsets.SlotBytesPerPixel, bytesPerPixel);
            _control.WriteLong(o + Offsets.SlotDataSize, dataSize);
            _control.WriteLong(o + Offsets.SlotFrameIndex, _nextFrame++);
            _control.WriteInt(o + Offsets.SlotStream, stream);
            // 쓰기 카운터를 올린 뒤 캠코더를 깨움 (캠코더는 writeCount를 보고 슬롯을 집음)
            _control.IncrementLong(Offsets.WriteCount);
            _control.SignalFrameReady();
        }

        // 유니티 텍스처는 아래쪽 줄부터 담겨 있으므로 위아래를 뒤집어 한 프레임을 씁니다.
        // 슬롯을 못 얻었으면(녹화 끝, 실시간 캡처에서 캠코더가 밀림) false
        public bool WriteFlipped(byte* source, int width, int height, int stream, bool wait)
        {
            if (source == null)
                return false;
            long rowBytes = (long)width * 4;
            long frameBytes = rowBytes * height;
            int slot;
            byte* dest;
            long capacity;
            if (!BeginWrite(out slot, out dest, out capacity, wait) || frameBytes > capacity)
                return false;
            var row = new UIntPtr((ulong)rowBytes);
            for (int y = 0; y < height; y++)
                Native.CopyMemory((IntPtr)(dest + y * rowBytes), (IntPtr)(source + (height - 1 - y) * rowBytes), row);
            EndWrite(slot, width, height, Shared.FormatRgba8, 4, frameBytes, stream);
            return true;
        }

        public void Dispose()
        {
            Close();
        }
    }
}
