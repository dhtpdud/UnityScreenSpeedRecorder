using System;
using UnityEngine;

namespace UnityCamcorder.Plugin
{
    // 배속 촬영: 원본 프로젝트의 RecorderManager.CapturingSetting()이 하던 일을 앱 안에서 합니다.
    // Time.captureFramerate = fps 로 두면 Time.deltaTime이 실제 경과 시간과 무관하게 1/fps로 고정됩니다.
    // 그러면 하드웨어가 허락하는 한 최대 속도로 프레임이 흘러가지만 각 프레임이 "1/fps만큼 지났다"고 여기므로,
    // 결과 영상을 fps로 재생하면 정상 속도로 보입니다.
    // (Unity 2019.2+의 captureDeltaTime도 정수 fps에서는 captureFramerate와 같으므로 모든 버전에서 captureFramerate를 씁니다)
    internal static class TimeControl
    {
        private static bool s_applied;
        private static int s_fps;
        private static bool s_enforceLogged;
        private static int s_savedCaptureFramerate;
        private static int s_savedTargetFrameRate = -1;
        private static int s_savedVSyncCount = 1;

        public static bool IsApplied { get { return s_applied; } }

        // 녹화 세션을 시작할 때 한 번. 실패하면 false (실시간 속도로 캡처)
        public static bool Apply(int fps, out string error)
        {
            error = null;
            if (fps <= 0)
            {
                error = "fps가 0입니다";
                return false;
            }
            try
            {
                if (!s_applied)
                {
                    // 녹화가 끝나면 원래 값으로 되돌리도록 기억
                    s_savedCaptureFramerate = Time.captureFramerate;
                    s_savedTargetFrameRate = Application.targetFrameRate;
                    s_savedVSyncCount = QualitySettings.vSyncCount;
                }
                Time.captureFramerate = fps;
                // 수직 동기화와 프레임 제한을 풀어야 하드웨어 최대 속도로 프레임이 흘러 배속이 됨
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = -1;
            }
            catch (Exception e)
            {
                error = "captureFramerate 설정 실패: " + e.Message;
                return false;
            }
            s_applied = true;
            s_fps = fps;
            s_enforceLogged = false;
            PluginLog.Info("배속 촬영 적용: 프레임당 1/" + fps + "초, vSync 끔, 프레임 제한 해제");
            return true;
        }

        // 녹화 중 매 프레임: 앱이 프레임 제한·수직 동기화·captureFramerate를 다시 걸었으면(장면 전환, 품질 설정 변경 등)
        // 다시 풉니다. 그대로 두면 앱이 실시간 속도로 묶여 배속이 안 나옴. 앱이 바꾼 값은 녹화가 끝나면 그 값으로 되돌림
        public static void Enforce()
        {
            if (!s_applied)
                return;
            int capture = Time.captureFramerate, vSync = QualitySettings.vSyncCount, target = Application.targetFrameRate;
            if (capture == s_fps && vSync == 0 && target == -1)
                return;
            if (!s_enforceLogged)
            {
                s_enforceLogged = true;
                PluginLog.Warn("앱이 녹화 중에 속도 설정을 바꿔서 다시 풉니다 (captureFramerate=" + capture + ", vSyncCount=" + vSync +
                               ", targetFrameRate=" + target + "). 이런 설정이 남아 있으면 배속이 나오지 않습니다.");
            }
            if (capture != s_fps) { s_savedCaptureFramerate = capture; Time.captureFramerate = s_fps; }
            if (vSync != 0) { s_savedVSyncCount = vSync; QualitySettings.vSyncCount = 0; }
            if (target != -1) { s_savedTargetFrameRate = target; Application.targetFrameRate = -1; }
            PluginStats.FrameLimitResets++;
        }

        // 녹화가 끝나거나 캠코더가 죽으면 원래대로
        public static void Restore()
        {
            if (!s_applied)
                return;
            try
            {
                Time.captureFramerate = s_savedCaptureFramerate;
                QualitySettings.vSyncCount = s_savedVSyncCount;
                Application.targetFrameRate = s_savedTargetFrameRate;
            }
            catch (Exception e)
            {
                PluginLog.Warn("배속 설정 복원 중 오류: " + e.Message);
            }
            s_applied = false;
            PluginLog.Info("배속 촬영 해제 (실시간으로 복귀, 원래 설정 복원)");
        }
    }
}
