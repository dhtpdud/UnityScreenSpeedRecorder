using System.Collections;
using System.Diagnostics;
using UnityEngine;
//using OpenCVForUnity.CoreModule;
using System.Collections.Generic;
using Application = UnityEngine.Application;
using Directory = System.IO.Directory;
using File = System.IO.File;
using System;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;
using UnityEngine.Scripting; // 가비지 컬랙터 용이니까 지우지 말것
using UnityEngine.Profiling;
using System.Threading;

public class ScreenRecorder : MonoBehaviour
{
    [Header("Options")]
    [SerializeField]
    private int recordingSec = 30;
    public string RecordingSec
    {
        set
        {
            recordingSec = int.Parse(value);
        }
    }
    [SerializeField]
    private int captureFrameRate = 30;
    public string CaptureFrameRate
    {
        set
        {
            captureFrameRate = int.Parse(value);
        }
    }
    [SerializeField]
    private float gameSpeed = 30;
    public string GameSpeed
    {
        set
        {
            gameSpeed = float.Parse(value);
        }
    }
    public bool VSyncEnable
    {
        set
        {
            QualitySettings.vSyncCount = value ? originVSyncCount : 0;
        }
    }
    [SerializeField]
    private string directoryPath = "FrameCaptures";
    public string DirectoryPath
    {
        get => directoryPath;
        set
        {
            directoryPath = value;
        }
    }


    [Header("UI")]
    [SerializeField]
    Canvas canvas;
    [SerializeField]
    private Text textTimer;
    [SerializeField]
    private Text textCounter;
    [SerializeField]
    private Text textFPS;

    private int counterIndex = 1;

    private Camera mainCam = null;
    private int ScreenWidth = Screen.width;
    private int ScreenHeight = Screen.height;
    private Rect screenRect;


    //캐싱용 변수 모음
    private int originVSyncCount;
    private int originTargetFrameRate;

    private float deltatimeCache;
    private float lastFPS;

    private IEnumerator updateRoutine;
    private IEnumerator recordingRoutine;
    private IEnumerator encodingRoutine;
    private YieldInstruction yieldCacheWaitUpdate = new WaitForEndOfFrame();

    private Stopwatch bechmarkWatch = new Stopwatch();

    private void Awake()
    {
        originVSyncCount = QualitySettings.vSyncCount;
        mainCam = Camera.main;
        canvas.worldCamera = mainCam;
    }
    private void Update()
    {
        deltatimeCache = Time.deltaTime;
        textFPS.text = string.Format("{0:N1} FPS", lastFPS = 1.0f / deltatimeCache);
    }
    IEnumerator RecordingUpdateRoutine()
    {
        float recordingTime = 0;
        counterIndex = 1;
        while (true)
        {
            textTimer.text = $"{recordingTime += deltatimeCache}초";
            textCounter.text = counterIndex++.ToString();
            yield return yieldCacheWaitUpdate;
        }
    }

    //Record 버튼 클릭시 호출
    public void ToggleRecord()
    {
        if (recordingRoutine != null)
        {
            Debug.Log("녹화 중단");
            StopCoroutine(recordingRoutine);
            recordingRoutine = null;
            return;
        }
        if (encodingRoutine != null)
        {
            Debug.Log("인코딩 중단");
            StopCoroutine(encodingRoutine);
            encodingRoutine = null;
            return;
        }
        directoryPath = $"{directoryPath}\\{recordingSec}s_{captureFrameRate}fps_{gameSpeed}x";

        if (string.IsNullOrEmpty(directoryPath))
        {
            Debug.Log("잘못된 경로");
            return;
        }

        if (!Directory.Exists(directoryPath))
        {
            Debug.Log("폴더 생성");
            Directory.CreateDirectory(directoryPath);
        }

        screenRect = new Rect(0, 0, ScreenWidth, ScreenHeight);

        //녹화 시작
        StartCoroutine(recordingRoutine = RecordingRoutine());
    }

    IEnumerator RecordingRoutine()
    {
        //초기값 세팅
        originTargetFrameRate = Application.targetFrameRate;
        Application.targetFrameRate = (int)(captureFrameRate * gameSpeed);
        QualitySettings.vSyncCount = 0;
        Time.timeScale = 0;

        //오버레이 Canvas는 RenderTexture를 통해 캡쳐되지 않음
        //따라서 Canvas renderMode모드를 ScreenSpaceCamera로 변경
        canvas.renderMode = RenderMode.ScreenSpaceCamera;
        canvas.planeDistance = 0.4f;

        bechmarkWatch.Reset();
        bechmarkWatch.Start();

        List<RenderTexture> capturedFrames = new List<RenderTexture>();

        //targetFrameRate 변경에 따른 fps 불안정 시간
        yield return new WaitForSecondsRealtime(0.1f);

        StartCoroutine(updateRoutine = RecordingUpdateRoutine());

        //GC 스파이크 방지
#if !UNITY_EDITOR
        GarbageCollector.GCMode = GarbageCollector.Mode.Manual;
#endif

        Time.timeScale = gameSpeed;

        //안전을 위해 Vram 한계선은 70%로...(원한다면 조절가능)
        long limitVRamSize = (long)(SystemInfo.graphicsMemorySize * 0.7f);

        //프레임 캐싱
        for (int i = 0; i < recordingSec * captureFrameRate; i++)
        {
            mainCam.targetTexture = new RenderTexture(ScreenWidth, ScreenHeight, 16);
            capturedFrames.Add(mainCam.targetTexture);

            //VRam 관리
            //VRam이 70%이상 가득차면 인코딩 시작
            if (Profiler.GetAllocatedMemoryForGraphicsDriver() / 1000000 >= limitVRamSize)
            {
                Debug.Log("메모리 가득참, 중간 인코딩 시작");
                yield return encodingRoutine = EncodingRoutine(capturedFrames);
                Debug.Log("중간 인코딩 완료");
            }

            yield return yieldCacheWaitUpdate;
        }
        bechmarkWatch.Stop();
        Debug.Log($"촬영 시간: {bechmarkWatch.ElapsedMilliseconds / 1000f}초");

        StopCoroutine(updateRoutine);
        updateRoutine = null;
        recordingRoutine = null;

        //캐싱된 프레임 인코딩 + 파일 저장 시작
        yield return encodingRoutine = EncodingRoutine(capturedFrames);
    }

    IEnumerator EncodingRoutine(List<RenderTexture> capturedFrame)
    {
#if !UNITY_EDITOR
        GarbageCollector.GCMode = GarbageCollector.Mode.Enabled;
#endif

        Time.timeScale = 0;
        mainCam.targetTexture = null;
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;

        GC.Collect();
        Resources.UnloadUnusedAssets();

        bechmarkWatch.Reset();
        bechmarkWatch.Start();

        //인코딩 시작
        //GPU의 한계를 최대한 끌어 올릴 수 있도록 카메라 OFF
        mainCam.enabled = false;
        for (int i = 0; i < capturedFrame.Count; i++)
        {
            EncodeImage(i, capturedFrame[i], directoryPath);
            yield return yieldCacheWaitUpdate;
        }
        bechmarkWatch.Stop();
        Debug.Log($"인코딩 시간: {bechmarkWatch.ElapsedMilliseconds / 1000f}초");


        //인코딩 완료
        //메모리 해제, 세팅값 초기화
        mainCam.enabled = true;

        Time.timeScale = 1;
        Application.targetFrameRate = originTargetFrameRate;
        QualitySettings.vSyncCount = originVSyncCount;

        capturedFrame.Clear();
        Resources.UnloadUnusedAssets();
        encodingRoutine = null;
    }

    void EncodeImage(int currentCount, RenderTexture frame, string directoryPath)
    {
        Texture2D convertedFrame = ConvertToTexture2D(frame);

        // Texture PNG bytes로 인코딩.
        // GPU 병목구간
        byte[] texturePNGBytes = convertedFrame.EncodeToPNG();
        string filePath = $"{directoryPath}\\{currentCount + 1}.png";

        //OpenCV테스트
        /*OpenCVForUnity.UnityUtils.Utils.texture2DToMat(texture2D, mat);
        OpenCVForUnity.ImgcodecsModule.Imgcodecs.imwrite(filePath, mat);*/


        //멀티스레드 미사용 출력 3.5초 → 멀티스레드 사용 출력 3초, 약 14.2% 성능 향상
        //(i7 - 13세대 기준)
        Thread thread = new Thread(new ThreadStart(() => File.WriteAllBytes(filePath, texturePNGBytes)));
        thread.Start();
    }

    Texture2D ConvertToTexture2D(RenderTexture rTex)
    {
        // TextureFormat에서 RGB24 는 알파가 존재하지 않는다.
        Texture2D tex = new Texture2D(ScreenWidth, ScreenHeight, TextureFormat.RGB24, true);
        RenderTexture.active = rTex;

        // GPU 병목구간
        tex.ReadPixels(screenRect, 0, 0);
        tex.Apply();
        return tex;
    }
}
