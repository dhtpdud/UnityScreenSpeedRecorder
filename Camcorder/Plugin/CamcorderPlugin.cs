using System;
using System.Collections;
using BepInEx;
using BepInEx.Logging;
using UnityCamcorder.Protocol;
using UnityEngine;

namespace UnityCamcorder.Plugin
{
    // BepInEx 5 플러그인 진입점. 앱이 켜질 때 BepInEx가 자동으로 불러옵니다.
    // 평소에는 0.5초마다 캠코더(UnityCamcorder.exe)가 이 앱용 제어 블록을 만들었는지 확인만 하고,
    // 캠코더가 연결되면 그 명령에 따라 배속(captureFramerate)과 카메라 촬영을 합니다.
    [BepInPlugin(Guid, Name, Version)]
    public sealed class CamcorderPlugin : BaseUnityPlugin
    {
        public const string Guid = "com.dhtpdud.unitycamcorder";
        public const string Name = "UnityCamcorder";
        public const string Version = "2.0.0";

        private void Awake()
        {
            PluginLog.Source = Logger;
            // 매 프레임 메인 스레드 틱: 우리 전용 숨은 오브젝트의 LateUpdate + Application.onBeforeRender
            // (앱이 BepInEx 관리 오브젝트를 지워도 계속 돌도록 둘 다 걸고, 프레임 번호로 한 번만 처리)
            var runner = new GameObject("UnityCamcorder");
            runner.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(runner);
            runner.AddComponent<CamcorderRunner>();
            Application.onBeforeRender += Recorder.Tick;
            Logger.LogInfo("UnityCamcorder 플러그인 " + Version + " 로드됨 (Unity " + Application.unityVersion + ", PID " + Native.GetCurrentProcessId() + "). 캠코더 연결을 기다립니다.");
        }
    }

    // 숨은 오브젝트에 붙는 컴포넌트: 프레임 틱과 프레임 끝(화면 촬영용)
    internal sealed class CamcorderRunner : MonoBehaviour
    {
        private void LateUpdate()
        {
            Recorder.Tick();
        }

        private IEnumerator Start()
        {
            var endOfFrame = new WaitForEndOfFrame();
            while (true)
            {
                yield return endOfFrame;
                Recorder.EndOfFrame();
            }
        }

        private void OnApplicationQuit()
        {
            Recorder.Shutdown();
        }
    }

    // 녹화 상태 (모두 메인 스레드에서)
    internal static class Recorder
    {
        private static ControlBlock s_control;
        private static CameraCapture s_capture;
        private static float s_nextAttachTry;
        private static string s_lastAttachProblem;
        private static int s_lastFrame = -1;
        private static long s_ticks;
        private static int s_lastHandledSession = -1;
        private static int s_screenWidth, s_screenHeight;
        private static bool s_failed;

        public static void Tick()
        {
            if (s_failed)
                return;
            try
            {
                int frame = Time.frameCount;
                if (frame == s_lastFrame)
                    return;   // 같은 프레임에 두 이벤트가 모두 불린 경우
                s_lastFrame = frame;
                if (s_control == null && !TryAttach())
                    return;
                Step();
            }
            catch (Exception e)
            {
                // 앱이 망가지지 않도록 플러그인만 멈춤
                s_failed = true;
                PluginLog.Error("예상하지 못한 오류로 캠코더 플러그인을 멈춥니다: " + e);
                SetLastError("플러그인 오류: " + e.Message);
                try { Shutdown(); } catch (Exception) { }
            }
        }

        public static void EndOfFrame()
        {
            if (s_capture == null || s_failed)
                return;
            try { s_capture.EndOfFrame(); }
            catch (Exception e)
            {
                s_failed = true;
                PluginLog.Error("화면 촬영 중 오류로 캠코더 플러그인을 멈춥니다: " + e);
                SetLastError("플러그인 오류: " + e.Message);
                try { Shutdown(); } catch (Exception) { }
            }
        }

        // 캠코더가 이 앱용 제어 블록을 만들었으면 연결 (0.5초마다 확인)
        private static bool TryAttach()
        {
            float now = Time.realtimeSinceStartup;
            if (now < s_nextAttachTry)
                return false;
            s_nextAttachTry = now + 0.5f;

            string problem;
            ControlBlock control = ControlBlock.TryOpen(Native.GetCurrentProcessId(), out problem);
            if (control == null)
            {
                if (problem != null && problem != s_lastAttachProblem)
                    PluginLog.Warn(problem);
                s_lastAttachProblem = problem;
                if (problem != null)
                    s_nextAttachTry = now + 5f;
                return false;
            }
            s_control = control;
            s_capture = new CameraCapture(control);
            s_lastHandledSession = -1;
            s_screenWidth = s_screenHeight = 0;

            control.WriteInt(Offsets.MainThreadId, (int)Native.GetCurrentThreadId());
            control.WriteInt(Offsets.GraphicsApi, (int)GraphicsApiOf(SystemInfo.graphicsDeviceType.ToString()));
            control.WriteInt(Offsets.ScreenFormat, Shared.FormatRgba8);
            control.WriteText(Offsets.RuntimeInfo, Shared.RuntimeInfoBytes,
                "BepInEx 플러그인 " + CamcorderPlugin.Version + " / Unity " + Application.unityVersion + " / " +
                SystemInfo.graphicsDeviceType + " / Mono");
            control.AddStatus(Status.Loaded | Status.RuntimeMono | Status.Tick | Status.MainThread);
            // 캠코더 창을 누르면 앱 창이 비활성이 되는데, 그때 멈추는 앱이면 캠코더 명령을 받을 수 없으므로
            // 캠코더가 한 번 연결된 뒤로는 앱이 꺼질 때까지 백그라운드에서도 계속 돌게 함
            if (!Application.runInBackground)
            {
                Application.runInBackground = true;
                PluginLog.Info("앱 창이 비활성이어도 멈추지 않도록 runInBackground를 켭니다 (앱이 꺼질 때까지)");
            }
            PluginLog.Info("캠코더 연결됨 (캠코더 PID " + control.ReadInt(Offsets.ControllerPid) + ")");
            return true;
        }

        private static GraphicsApi GraphicsApiOf(string deviceType)
        {
            if (deviceType == "Direct3D11") return GraphicsApi.D3D11;
            if (deviceType == "Direct3D12") return GraphicsApi.D3D12;
            if (deviceType.StartsWith("OpenGL")) return GraphicsApi.OpenGL;
            if (deviceType == "Vulkan") return GraphicsApi.Vulkan;
            return GraphicsApi.Unknown;
        }

        private static void Step()
        {
            ControlBlock control = s_control;
            control.WriteLong(Offsets.FrameCount, ++s_ticks);

            // 화면 크기 (캠코더가 카메라 RT 크기를 정할 때 씀)
            int w = Screen.width, h = Screen.height;
            if (w != s_screenWidth || h != s_screenHeight)
            {
                s_screenWidth = w;
                s_screenHeight = h;
                control.WriteInt(Offsets.ScreenWidth, w);
                control.WriteInt(Offsets.ScreenHeight, h);
                if (w > 0 && h > 0)
                    control.AddStatus(Status.Ready);
            }

            s_capture.HandleCameraListRequest();

            bool wantRecord = control.IsRecordRequested();
            int session = control.ReadInt(Offsets.SessionId);
            if (wantRecord)
            {
                if (session != s_lastHandledSession)
                {
                    // 새 녹화 세션: 배속 적용 → 카메라 촬영 준비 → 처리 완료 표시
                    s_lastHandledSession = session;
                    s_capture.Stop();
                    string error;
                    if (TimeControl.Apply(control.ReadInt(Offsets.Fps), out error))
                    {
                        control.RemoveStatus(Status.TimeFailed);
                        control.AddStatus(Status.TimeControl);
                    }
                    else
                    {
                        control.AddStatus(Status.TimeFailed);
                        SetLastError(error);
                    }
                    if (!s_capture.Start(session, TimeControl.IsApplied, out error))
                        SetLastError(error ?? "촬영을 시작하지 못했습니다");
                    System.Threading.Thread.MemoryBarrier();
                    control.WriteInt(Offsets.AppliedSessionId, session);
                }
                s_capture.Tick();
            }
            else if (s_lastHandledSession != -1)
            {
                // 명령이 멈췄거나 캠코더가 죽었으면 카메라와 속도를 원래대로 복원
                s_capture.Stop();
                TimeControl.Restore();
                control.RemoveStatus(Status.TimeControl);
                s_lastHandledSession = -1;
            }
        }

        public static void SetLastError(string message)
        {
            PluginLog.Warn(message);
            if (s_control != null)
                s_control.WriteText(Offsets.LastError, Shared.LastErrorBytes, message);
        }

        // 앱이 꺼질 때: 바꿔 둔 설정을 되돌리고 공유 메모리를 닫음
        public static void Shutdown()
        {
            if (s_capture != null)
                s_capture.Stop();
            TimeControl.Restore();
            if (s_control != null)
            {
                s_control.RemoveStatus(Status.Loaded | Status.Ready | Status.Tick | Status.TimeControl);
                s_control.Dispose();
            }
            s_control = null;
            s_capture = null;
        }
    }

    internal static class PluginLog
    {
        public static ManualLogSource Source;

        public static void Info(string message) { if (Source != null) Source.LogInfo(message); }
        public static void Warn(string message) { if (Source != null) Source.LogWarning(message); }
        public static void Error(string message) { if (Source != null) Source.LogError(message); }
    }
}
