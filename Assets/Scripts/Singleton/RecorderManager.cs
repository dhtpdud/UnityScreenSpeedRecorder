using Cysharp.Threading.Tasks;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

//Recording 앱의 설정값을 관리합니다.
public class RecorderManager : OSY.Singleton<RecorderManager>
{
    [ReadOnly(false)] public List<ScreenRecorder> screenRecorders;
    public int OptionCaptureTargetFramerate { get; private set; }//FPS
    public int OptionTimescale { get; private set; }
    public bool OptionIsDebug { get; private set; }
    public int OptionFrameChunkSize { get; private set; }

    [ReadOnly(false)] public int capturingRecorderCount;
    [ReadOnly(false)] public Queue<string> flushDestinationQueue = new Queue<string>();

    protected override void Awake()
    {
        base.Awake();
        screenRecorders = FindObjectsOfType<ScreenRecorder>(true).ToList();
        //_textureMemoryRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "Texture Memory");
        string rootPath = Environment.CurrentDirectory;
        string envPath = (rootPath + "/envRecorder.json").Replace(@"\", "/");
        try
        {
            JObject json = null;
            if (System.IO.File.Exists(envPath))
            {
                json = JObject.Parse(System.IO.File.ReadAllText(envPath));
                Debug.Log("Recorder Start : JSON1=" + json);
            }

            OptionCaptureTargetFramerate = (int)json["CaptureTargetFramerate"];
            OptionTimescale = (int)json["Timescale"];
            OptionIsDebug = (bool)json["IsDebug"];
            OptionFrameChunkSize = (int)json["FrameChunkSize"];
            //SystemInfo.graphicsMemorySize * OptionMaxVramLimit = 전체 Vram사이즈 중 ~%
        }
        catch (FileNotFoundException)
        {
            Debug.LogError($"{envPath}파일을 찾을 수 없습니다.");
        }
    }

    void Start()
    {
        Debug.Log("Recorder Start : realtimeSinceStartup=" + Time.realtimeSinceStartup);
        Debug.Log("Recorder Start : fixedTime=" + Time.fixedTime);
        Debug.Log("Recorder Start : width=" + Screen.width + " , height=" + Screen.height);

        string[] args = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length; i++)
            Debug.Log($"Recorder Start : args[{i}]=" + args[i]);
    }
    public void StartRecordAll()
    {
        screenRecorders.ForEach(recorder => UniTask.RunOnThreadPool(recorder.CaptureFramesTask, true, destroyCancellationToken).Forget());
    }
    public void CapturingSetting()
    {
        //유니티앱은 서버측에서 호출시에만 실행됨. 따라서 Reset세팅은 무필요
        Debug.Log("촬영세팅");
#if !UNITY_EDITOR
        UnityEngine.Scripting.GarbageCollector.GCMode = UnityEngine.Scripting.GarbageCollector.Mode.Disabled;
#endif
        GameManager.Instance.mainCam.enabled = false;
        Time.timeScale = OptionTimescale;
        var targetFR = OptionTimescale * OptionCaptureTargetFramerate;
        Application.targetFrameRate = targetFR; // 50 * 30 = 1800fps
        Time.captureFramerate = targetFR; //게임 속도를 조절하여, 하드웨어의 성능에 관계없이 해당 프레임 강제 고정
        QualitySettings.vSyncCount = 0;
        QualitySettings.maxQueuedFrames = 4;
    }
    public void FlushingSetting()
    {
        Debug.Log("인코딩세팅");
#if !UNITY_EDITOR
        UnityEngine.Scripting.GarbageCollector.GCMode = UnityEngine.Scripting.GarbageCollector.Mode.Disabled;
#endif
        GameManager.Instance.mainCam.enabled = false;

        Time.timeScale = 0.000001f;
        //TimeScale을 0으로 하지 않는 이유
        // ※ Live2D Cubism SDK 5-r.2 - 2024-04-04 버전 기준 ※
        //큐비즘 모델의 물리 움직임(머리카락, 옷, 뱃살 등)을 담당하는 CubismPhysicsRig가 Time.deltaTime을 참조함.
        //CubismPhysicsRig.cs의 178번째 줄에서 deltatime변수가 0일 경우 물리연산 함수를 실행하지 않는다는 조건문이 걸려있음.
        //이것으로 인해,
        //deltatime이 0 이거나 작을때는 CubismPhysicsRig의 물리연산이 적용되지 않은 모습이 렌더링되며,
        //deltatime이 0 보다 클때는 CubismPhysicsRig의 물리연산이 적용된 모습이 렌더링됨.
        //따라서, deltatime이 0일 때와 아닐때의 모습이 다르게 보이게 되며, 최종 렌더링 화면에서 이것이 프레임이 튀는 듯한 모습으로 보이게 됨

        //만일 해당 조건문을 무시하고, 0일 경우에도 로직을 실행하게되면, CubismPhysicsRig.cs의 225번째 줄의 while문에 의해 무한 루프에 빠지게됨.

        Application.targetFrameRate = GameManager.Instance.originTargetFramerate;
        Time.captureFramerate = GameManager.Instance.origincaptureFramerate;
        QualitySettings.vSyncCount = 0;
    }

    public void ResetSetting()
    {
        Debug.LogWarning("대기세팅");
#if !UNITY_EDITOR
                UnityEngine.Scripting.GarbageCollector.GCMode = UnityEngine.Scripting.GarbageCollector.Mode.Enabled;
#endif
        GameManager.Instance.mainCam.enabled = true;
        QualitySettings.vSyncCount = 1;
        Application.targetFrameRate = GameManager.Instance.originTargetFramerate;
        Time.captureFramerate = GameManager.Instance.origincaptureFramerate;
        QualitySettings.vSyncCount = GameManager.Instance.originVSyncCount;
        Time.timeScale = 1;
    }
    public void OnApplicationQuit()
    {
        ResetSetting();
    }
}

/*
    ~/Desktop/avt-recording-unity.app/Contents/MacOS/avt-recording-unity /Users/leehome/Desktop/recording/project/slide/config.json
    ~/Desktop/recDD.app/Contents/MacOS/avt-recording-unity -batchmode -screen-width 720 -screen-height 1280 -screen-quality Beautiful
    open -a recDD --args /Users/leehome/Desktop/recording/slide0/config.json -batchmode -screen-width 720 -screen-height 1280 -screen-quality Beautiful
    /Applications/Unity/Unity.app/Contents/MacOS/Unity -projectPath ~/Documents/avt-recording-unity -executeMethod MyEditorScript.PerformBuild
        
    ffmpeg -y -framerate 30 -f image2 -i ~/Desktop/recording/project0/slide0/0/frames/%04d.png -c:v libvpx-vp9 -pix_fmt yuva420p ~/Desktop/recording/project0/slide0/0/0.webm
    ffmpeg -y -i ~/Desktop/recording/project0/slide0/0/speech.FHD.wav -vcodec libvpx-vp9 -i ~/Desktop/recording/project0/slide0/0/0.webm \
    -map "0:a" -map "1:v" -r 30 -pix_fmt yuv420p -crf 18 -preset fast -c:v libx264 ~/Desktop/recording/project0/slide0/0/0.mp4

    ffmpeg -y -framerate 30 -f image2 -i 0/frames/%04d.png -c:v libvpx-vp9 -pix_fmt yuva420p 0.webm
    ffmpeg -y -i 0/speech.FHD.wav -vcodec libvpx-vp9 -i 0.webm -map "0:a" -map "1:v" -r 30 -pix_fmt yuv420p -crf 18 -preset fast -c:v libx264 0.mp4

    ffmpeg -y -framerate 30 -f image2 -i 2/frames/%04d.png -c:v libvpx-vp9 -pix_fmt yuva420p 2.webm
    ffmpeg -y -i 2/speech.FHD.wav -vcodec libvpx-vp9 -i 2.webm -map "0:a" -map "1:v" -r 30 -pix_fmt yuv420p -crf 18 -preset fast -c:v libx264 2.mp4

    ffmpeg -y -framerate 30 -f image2 -i 3/frames/%04d.png -c:v libvpx-vp9 -pix_fmt yuva420p 3.webm
    ffmpeg -y -i 3/speech.FHD.wav -vcodec libvpx-vp9 -i 3.webm -map "0:a" -map "1:v" -r 30 -pix_fmt yuv420p -crf 18 -preset fast -c:v libx264 3.mp4

    ffmpeg -y -framerate 30 -f image2 -i 5/frames/%04d.png -c:v libvpx-vp9 -pix_fmt yuva420p 5.webm
    ffmpeg -y -i 5/speech.FHD.wav -vcodec libvpx-vp9 -i 5.webm -map "0:a" -map "1:v" -r 30 -pix_fmt yuv420p -crf 18 -preset fast -c:v libx264 5.mp4

    ffmpeg -i "0.mp4" -vsync 2 -i "2.mp4" -vsync 2 -i "3.mp4" -vsync 2 -i "5.mp4" -vsync 2 \
    -filter_complex "[0:v][0:a][1:v][1:a][2:v][2:a][3:v][3:a]concat=n=4:v=1:a=1[outv][outa]" -map "[outv]" -map "[outa]" 0235.mp4
*/