using System;
using System.Collections.Generic;
using System.Reflection;
using UnityCamcorder.Protocol;
using UnityEngine;

namespace UnityCamcorder.Plugin
{
    // 카메라 촬영: 원본 프로젝트(ScreenRecorder + RecorderFlusher)와 같은 방식입니다.
    // 카메라의 targetTexture를 캠코더용 RenderTexture로 돌려 화면 대신 그곳에 그리게 하고,
    // 매 프레임 AsyncGPUReadback으로 GPU에서 비동기로 읽어 와 공유 메모리 링 버퍼로 캠코더에 넘깁니다.
    // AsyncGPUReadback을 쓸 수 없는 환경(미지원 그래픽 API 등)에서는 Texture2D.ReadPixels로 동기 읽기를 합니다.
    //
    // 촬영 카메라를 고르지 않으면 화면에 그리던 카메라를 모두 하나의 RenderTexture로 모아 한 영상으로 찍고,
    // 그런 카메라가 하나도 없으면 프레임 끝에서 화면(백버퍼)을 읽습니다.
    // 고르면 카메라마다 스트림 하나(= 영상 하나)가 됩니다. 앱이 이미 자기 RenderTexture로 그리던 카메라는
    // 그 텍스처를 그대로 읽어 앱 동작을 바꾸지 않습니다.
    // 모든 함수는 유니티 메인 스레드에서만 부릅니다.
    internal sealed unsafe class CameraCapture
    {
        // 스트림마다 동시에 진행할 읽기 요청 수. 배속 촬영에선 프레임이 빨리 돌아 GPU 읽기가 몇 프레임 늦게 끝나므로,
        // 너무 적으면 매 프레임 가장 오래된 요청을 기다리느라(GPU 동기화) 앱이 느려짐 (원본 프로젝트는 최대 64)
        private const int MaxInFlight = 8;

        private sealed class Stream
        {
            public int Index;
            public readonly List<Camera> Retargeted = new List<Camera>();   // targetTexture를 우리 RT로 바꾼 카메라
            public readonly List<Camera> EnabledByUs = new List<Camera>();  // 꺼져 있어서 촬영 동안 켠 카메라
            public RenderTexture OwnTarget;                                 // 캠코더가 만든 RenderTexture (없으면 null)
            public RenderTexture Source;                                    // 읽어 올 텍스처 (OwnTarget 또는 카메라가 원래 쓰던 RT)
            public Texture2D ReadTexture;                                   // 동기 읽기용
            public int Width, Height;
            public readonly Queue<object> Pending = new Queue<object>();   // AsyncGPUReadbackRequest (상자)
            public bool ErrorLogged;
        }

        private readonly ControlBlock _control;
        private readonly FrameRing _ring = new FrameRing();
        private readonly List<Stream> _streams = new List<Stream>();
        private AsyncReadback _async;
        private bool _asyncResolved;
        private bool _useAsync;
        private bool _composite;
        private bool _screen;                  // 카메라 대신 화면(백버퍼)을 읽는 중
        private Texture2D _screenTexture;
        private int _lastAllCamerasCount = -1;
        private int _rescanTick;
        private bool _waitForSlots;

        public int Session { get; private set; }
        public bool IsActive { get { return Session >= 0; } }
        public bool IsScreen { get { return _screen; } }

        public CameraCapture(ControlBlock control)
        {
            _control = control;
            Session = -1;
        }

        // --- 카메라 목록 (캠코더/창 프로그램이 촬영 카메라를 고를 때) ---------------------
        public void HandleCameraListRequest()
        {
            int request = _control.ReadInt(Offsets.CameraListRequest);
            if (request == _control.ReadInt(Offsets.CameraListDone))
                return;

            int recordW, recordH;
            RecordSize(out recordW, out recordH);
            Camera main = Camera.main;
            int written = 0;
            foreach (Camera camera in AllCameras())
            {
                if (written >= Shared.MaxCameras)
                    break;
                if (camera == null)
                    continue;
                byte* info = _control.Base + Offsets.Camera(written);
                for (int i = 0; i < Shared.CameraStride; i++)
                    info[i] = 0;
                int flags = 0;
                if (camera.enabled) flags |= (int)CameraFlags.Enabled;
                if (main != null && camera == main) flags |= (int)CameraFlags.Main;
                if (IsUrpOverlay(camera)) flags |= (int)CameraFlags.Overlay;
                int width = recordW, height = recordH;
                RenderTexture target = camera.targetTexture;
                if (target != null)
                {
                    flags |= (int)CameraFlags.OwnTarget;
                    width = target.width;
                    height = target.height;
                }
                *(int*)(info + Offsets.CamInstanceId) = CameraId(camera);
                *(int*)(info + Offsets.CamWidth) = width;
                *(int*)(info + Offsets.CamHeight) = height;
                *(int*)(info + Offsets.CamFlags) = flags;
                *(float*)(info + Offsets.CamDepth) = camera.depth;
                *(int*)(info + Offsets.CamTargetDisplay) = camera.targetDisplay;
                ControlBlock.WriteUtf8(info + Offsets.CamName, Shared.CameraNameBytes, camera.name);
                written++;
            }
            _control.WriteInt(Offsets.CameraCount, written);
            System.Threading.Thread.MemoryBarrier();
            _control.WriteInt(Offsets.CameraListDone, request);
            PluginLog.Info("카메라 목록 보고: " + written + "대");
        }

        // 장면에 있는 카메라 전부 (꺼진 카메라 컴포넌트 포함, 꺼진 게임오브젝트와 프리팹 에셋은 제외)
        // FindObjectsOfType는 Unity 2023부터 사라지는 중이라 모든 버전에 있는 Resources.FindObjectsOfTypeAll을 씀
        private static List<Camera> AllCameras()
        {
            var cameras = new List<Camera>();
            foreach (UnityEngine.Object found in Resources.FindObjectsOfTypeAll(typeof(Camera)))
            {
                var camera = found as Camera;
                if (camera == null || camera.hideFlags == HideFlags.HideAndDontSave)
                    continue;   // 유니티 내부용(미리보기 등) 카메라
                GameObject go = camera.gameObject;
                if (go.activeInHierarchy && go.scene.IsValid())
                    cameras.Add(camera);
            }
            return cameras;
        }

        // 카메라 번호: 앱이 켜져 있는 동안 처음 본 순서대로 붙이는 번호 (#번호로 고를 때 씀)
        // Object.GetInstanceID()는 Unity 6000.6부터 쓸 수 없게(오류) 바뀌어 직접 매김
        private static readonly List<Camera> s_cameraIds = new List<Camera>();

        private static int CameraId(Camera camera)
        {
            for (int i = 0; i < s_cameraIds.Count; i++)
                if (ReferenceEquals(s_cameraIds[i], camera))
                    return i + 1;
            s_cameraIds.Add(camera);
            return s_cameraIds.Count;
        }

        // URP 카메라 스택의 오버레이 카메라인지. 오버레이는 혼자 그릴 수 없고 베이스 카메라의 출력에 함께 그려짐
        private static Type s_urpCameraData;
        private static PropertyInfo s_urpRenderType;
        private static bool s_urpResolved;

        private static bool IsUrpOverlay(Camera camera)
        {
            if (!s_urpResolved)
            {
                s_urpResolved = true;
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type t;
                    try { t = assembly.GetType("UnityEngine.Rendering.Universal.UniversalAdditionalCameraData", false); }
                    catch (Exception) { continue; }
                    if (t == null)
                        continue;
                    s_urpCameraData = t;
                    s_urpRenderType = t.GetProperty("renderType", BindingFlags.Instance | BindingFlags.Public);
                    break;
                }
            }
            if (s_urpCameraData == null || s_urpRenderType == null)
                return false;
            Component data = camera.GetComponent(s_urpCameraData);
            if (data == null)
                return false;
            try { return Convert.ToString(s_urpRenderType.GetValue(data, null)) == "Overlay"; }
            catch (Exception) { return false; }
        }

        private static Camera FindCamera(int instanceId)
        {
            foreach (Camera camera in AllCameras())
                if (camera != null && CameraId(camera) == instanceId)
                    return camera;
            return null;
        }

        // 카메라가 자기 RenderTexture 없이 그릴 때 쓸 영상 크기: 캠코더가 정한 크기 → 지금 화면 크기
        private void RecordSize(out int width, out int height)
        {
            width = _control.ReadInt(Offsets.RecordWidth);
            height = _control.ReadInt(Offsets.RecordHeight);
            if (width > 0 && height > 0)
                return;
            width = Screen.width;
            height = Screen.height;
        }

        // --- 촬영 시작/끝 ------------------------------------------------------------------
        // 녹화 세션 시작. waitForSlots = 배속 촬영 중이면 캠코더를 기다리고(프레임 안 버림), 실시간이면 밀린 프레임을 버림
        public bool Start(int session, bool waitForSlots, out string error)
        {
            Stop();
            error = null;
            _waitForSlots = waitForSlots;
            if (!_asyncResolved)
            {
                _asyncResolved = true;
                string reason;
                _async = AsyncReadback.Create(out reason);
                PluginLog.Info(_async != null
                    ? "AsyncGPUReadback 사용 가능 (RGBA32 지정 " + (_async.RequestsRgba32 ? "예" : "아니오") + ")"
                    : "AsyncGPUReadback을 쓸 수 없어 ReadPixels(동기 읽기)를 씁니다: " + reason);
            }
            _useAsync = _async != null && SystemInfo.supportsAsyncGPUReadback;

            int recordW, recordH;
            RecordSize(out recordW, out recordH);
            int streamCount = Math.Max(0, Math.Min(Shared.MaxStreams, _control.ReadInt(Offsets.StreamCount)));

            bool ok = true;
            if (streamCount == 0)
            {
                // 화면 전체: 화면에 그리던 카메라들을 한 RT로 모음. 그런 카메라가 없으면 화면(백버퍼)을 읽음
                _composite = true;
                var stream = new Stream { Index = 0 };
                _streams.Add(stream);
                ok = recordW > 0 && recordH > 0 && MakeOwnTarget(stream, recordW, recordH) && RetargetDisplayCameras(stream) > 0;
                _lastAllCamerasCount = Camera.allCamerasCount;
                if (!ok)
                {
                    RestoreAll();
                    return StartScreen(session, out error);
                }
            }
            else
            {
                _composite = false;
                for (int i = 0; i < streamCount && ok; i++)
                {
                    var stream = new Stream { Index = i };
                    _streams.Add(stream);
                    int id = _control.ReadInt(Offsets.StreamCameraIds + i * 4);
                    Camera camera = FindCamera(id);
                    if (camera == null)
                    {
                        error = "선택한 카메라를 찾을 수 없음 (#" + id + "). 장면이 바뀌었다면 카메라 목록을 다시 불러오세요.";
                        ok = false;
                        break;
                    }
                    if (IsUrpOverlay(camera))
                    {
                        error = "'" + camera.name + "'은 URP 오버레이 카메라라 혼자 찍을 수 없습니다. 이 카메라를 쌓아 둔 베이스 카메라를 고르면 함께 찍힙니다.";
                        ok = false;
                        break;
                    }
                    RenderTexture target = camera.targetTexture;
                    if (target != null)
                    {
                        // 앱이 이미 자기 RT로 그리는 카메라: 그 RT를 그대로 읽음
                        stream.Source = target;
                        stream.Width = target.width;
                        stream.Height = target.height;
                        ok = stream.Width > 0 && stream.Height > 0;
                    }
                    else
                    {
                        ok = recordW > 0 && recordH > 0 && MakeOwnTarget(stream, recordW, recordH);
                        if (ok)
                        {
                            camera.targetTexture = stream.OwnTarget;
                            stream.Retargeted.Add(camera);
                        }
                    }
                    if (ok && !camera.enabled)
                    {
                        // 원본 프로젝트의 녹화 카메라처럼 평소 꺼져 있던 카메라는 촬영 동안만 켬
                        camera.enabled = true;
                        stream.EnabledByUs.Add(camera);
                    }
                    if (!ok && error == null)
                        error = "카메라 '" + camera.name + "' 촬영 준비 실패";
                }
            }

            int maxW = 0, maxH = 0;
            foreach (Stream stream in _streams)
            {
                if ((long)stream.Width * stream.Height > (long)maxW * maxH) { maxW = stream.Width; maxH = stream.Height; }
                if (ok && !_useAsync)
                    stream.ReadTexture = new Texture2D(stream.Width, stream.Height, TextureFormat.RGBA32, false);
            }
            string ringError = null;
            if (ok && !_ring.Open(_control, session, maxW, maxH, 4, out ringError))
                ok = false;
            if (!ok)
            {
                error = error ?? ringError;
                RestoreAll();
                return false;
            }

            Session = session;
            _control.AddStatus(Status.CameraCapture);
            string mode = _useAsync ? "AsyncGPUReadback" : "ReadPixels";
            if (_composite)
                PluginLog.Info("카메라 촬영 시작: 화면 전체 (카메라 " + _streams[0].Retargeted.Count + "대 → " + maxW + "x" + maxH + " RenderTexture, " + mode + ")");
            else
                PluginLog.Info("카메라 촬영 시작: 카메라 " + _streams.Count + "대 (각각 영상 하나, " + mode + ")");
            return true;
        }

        // 화면에 그리는 카메라가 없을 때: 프레임 끝(WaitForEndOfFrame)에서 화면을 ReadPixels로 읽음 (UI까지 포함)
        private bool StartScreen(int session, out string error)
        {
            int width = Screen.width, height = Screen.height;
            if (width <= 0 || height <= 0)
            {
                error = "화면 크기를 알 수 없습니다";
                return false;
            }
            if (!_ring.Open(_control, session, width, height, 4, out error))
                return false;
            _screenTexture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            _screen = true;
            Session = session;
            _control.AddStatus(Status.ScreenCapture);
            PluginLog.Info("화면에 그리는 카메라를 찾지 못해 화면(백버퍼)을 프레임 끝에서 읽습니다: " + width + "x" + height);
            return true;
        }

        private static bool MakeOwnTarget(Stream stream, int width, int height)
        {
            var rt = new RenderTexture(width, height, 24);   // 깊이 + 스텐실
            rt.name = "UnityCamcorder";
            if (!rt.Create())
            {
                UnityEngine.Object.Destroy(rt);
                return false;
            }
            stream.OwnTarget = rt;
            stream.Source = rt;
            stream.Width = width;
            stream.Height = height;
            return true;
        }

        // 화면 전체 촬영: 화면(Display 1)에 그리던 켜진 카메라를 모두 우리 RT로 돌림. 새로 생긴 카메라도 따라감
        // (URP 오버레이 카메라는 베이스 카메라를 따라 RT에 함께 그려지므로 건드리지 않음)
        private static int RetargetDisplayCameras(Stream stream)
        {
            int added = 0;
            foreach (Camera camera in Camera.allCameras)
            {
                if (camera == null || !camera.enabled || camera.targetTexture != null || camera.targetDisplay != 0)
                    continue;
                if (IsUrpOverlay(camera))
                    continue;
                if (stream.Retargeted.Contains(camera))
                    continue;
                camera.targetTexture = stream.OwnTarget;
                stream.Retargeted.Add(camera);
                added++;
            }
            return added;
        }

        public void Stop()
        {
            if (Session < 0 && _streams.Count == 0 && !_screen)
                return;
            RestoreAll();
            PluginLog.Info("촬영 종료, 카메라 설정 복원");
        }

        private void RestoreAll()
        {
            foreach (Stream stream in _streams)
                RestoreStream(stream);
            _streams.Clear();
            if (_screenTexture != null)
                UnityEngine.Object.Destroy(_screenTexture);
            _screenTexture = null;
            _screen = false;
            _ring.Close();
            Session = -1;
            _control.RemoveStatus(Status.CameraCapture | Status.ScreenCapture);
        }

        private void RestoreStream(Stream stream)
        {
            while (stream.Pending.Count > 0)
            {
                object request = stream.Pending.Dequeue();
                try { _async.WaitDone(request); } catch (Exception) { }
            }
            foreach (Camera camera in stream.Retargeted)
            {
                // 앱이 그새 다른 텍스처로 바꿨으면 건드리지 않음
                if (camera != null && stream.OwnTarget != null && camera.targetTexture == stream.OwnTarget)
                    camera.targetTexture = null;
            }
            foreach (Camera camera in stream.EnabledByUs)
                if (camera != null)
                    camera.enabled = false;
            stream.Retargeted.Clear();
            stream.EnabledByUs.Clear();
            if (stream.OwnTarget != null)
            {
                if (RenderTexture.active == stream.OwnTarget)
                    RenderTexture.active = null;
                stream.OwnTarget.Release();
                UnityEngine.Object.Destroy(stream.OwnTarget);
            }
            if (stream.ReadTexture != null)
                UnityEngine.Object.Destroy(stream.ReadTexture);
            stream.OwnTarget = null;
            stream.Source = null;
            stream.ReadTexture = null;
        }

        // --- 매 프레임 --------------------------------------------------------------------
        // 메인 스레드 틱마다: 끝난 읽기를 넘기고, 이번 프레임 읽기를 요청
        public void Tick()
        {
            if (Session < 0 || _screen)
                return;

            // 화면 전체 촬영 중 새 카메라가 생기면(장면 전환 등) 따라감
            if (_composite && _streams.Count > 0)
            {
                int count = Camera.allCamerasCount;
                if (count != _lastAllCamerasCount || (++_rescanTick % 60) == 0)
                {
                    _lastAllCamerasCount = count;
                    if (RetargetDisplayCameras(_streams[0]) > 0)
                        PluginLog.Info("새 카메라를 촬영에 추가");
                }
            }

            foreach (Stream stream in _streams)
            {
                if (!_useAsync)
                {
                    if (!_control.IsRecordRequested())
                        return;
                    ReadNow(stream);
                    continue;
                }
                while (stream.Pending.Count > 0 && _async.IsDone(stream.Pending.Peek()))
                    Deliver(stream, stream.Pending.Dequeue());
                // 읽기가 너무 밀리면 가장 오래된 것부터 끝내고 넘김 (프레임을 버리지 않음)
                while (stream.Pending.Count >= MaxInFlight)
                {
                    object oldest = stream.Pending.Dequeue();
                    long waitStart = PluginStats.Now();
                    _async.WaitDone(oldest);
                    PluginStats.GpuWaitTicks += PluginStats.Now() - waitStart;
                    Deliver(stream, oldest);
                }
                if (!_control.IsRecordRequested())
                    return;
                // 지난 프레임에 카메라가 그린 결과를 읽기 요청 (원본 프로젝트와 같은 시점)
                if (stream.Source == null)
                    continue;
                object request = _async.Request(stream.Source);
                if (request != null)
                    stream.Pending.Enqueue(request);
            }
        }

        // 프레임 끝(WaitForEndOfFrame)마다: 화면 촬영 중이면 백버퍼를 읽어 넘김
        public void EndOfFrame()
        {
            if (!_screen || _screenTexture == null || !_control.IsRecordRequested())
                return;
            int width = _screenTexture.width, height = _screenTexture.height;
            if (Screen.width != width || Screen.height != height)
                return;   // 창 크기가 바뀜: 캠코더가 해상도 변화를 보고 마무리하도록 이전 크기 그대로 두고 건너뜀
            long readStart = PluginStats.Now();
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = null;
            _screenTexture.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
            RenderTexture.active = previous;
            Color32[] pixels = _screenTexture.GetPixels32();
            PluginStats.GpuWaitTicks += PluginStats.Now() - readStart;
            if (pixels.Length < width * height)
                return;
            fixed (Color32* p = pixels)
                _ring.WriteFlipped((byte*)p, width, height, 0, _waitForSlots);
        }

        // 비동기 읽기가 끝난 프레임을 링 버퍼로 넘김
        private void Deliver(Stream stream, object request)
        {
            if (_async.HasError(request))
            {
                if (!stream.ErrorLogged)
                    PluginLog.Warn("스트림 " + stream.Index + ": GPU 읽기 실패 (이 프레임은 건너뜀)");
                stream.ErrorLogged = true;
                return;
            }
            int dataSize;
            IntPtr data = _async.Data(request, out dataSize);
            if (data == IntPtr.Zero || (dataSize >= 0 && dataSize < (long)stream.Width * 4 * stream.Height))
                return;
            _ring.WriteFlipped((byte*)data, stream.Width, stream.Height, stream.Index, _waitForSlots);
        }

        // 동기 읽기: 카메라가 지난 프레임에 그린 RenderTexture를 Texture2D로 읽어 바로 넘김
        private void ReadNow(Stream stream)
        {
            if (stream.Source == null || stream.ReadTexture == null)
                return;
            long readStart = PluginStats.Now();
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = stream.Source;
            stream.ReadTexture.ReadPixels(new Rect(0, 0, stream.Width, stream.Height), 0, 0, false);
            RenderTexture.active = previous;
            Color32[] pixels = stream.ReadTexture.GetPixels32();
            PluginStats.GpuWaitTicks += PluginStats.Now() - readStart;
            if (pixels.Length < stream.Width * stream.Height)
            {
                if (!stream.ErrorLogged)
                    PluginLog.Warn("스트림 " + stream.Index + ": ReadPixels 실패 (이 프레임은 건너뜀)");
                stream.ErrorLogged = true;
                return;
            }
            fixed (Color32* p = pixels)   // Color32 = RGBA 4바이트
                _ring.WriteFlipped((byte*)p, stream.Width, stream.Height, stream.Index, _waitForSlots);
        }
    }
}
