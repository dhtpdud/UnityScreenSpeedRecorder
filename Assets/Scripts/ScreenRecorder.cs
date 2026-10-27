using Cysharp.Threading.Tasks;
using OSY;
using System.Diagnostics;
using System.IO;
using UnityEngine;
using Debug = UnityEngine.Debug;

//targetFrameCount만큼 화면을 캡쳐합니다.
//캡쳐한 프레임은 RecorderFlusher가 GPU에서 비동기로 읽어 와(AsyncGPUReadback) .png파일로 저장합니다.
//모든 프레임이 저장되면 ffmpeg로 영상을 인코딩합니다.
public class ScreenRecorder : MonoBehaviour
{
    [SerializeField, ReadOnly(false)] private bool isCapturing;
    public bool IsCapturing { get => isCapturing; }
    public new Camera camera = null;                                        //카메라
    [SerializeField] private int captureFrameCount = -1;                     //목표 캡쳐 프레임 수
    [SerializeField, ReadOnly(false)] private int capturedFrameCount;       //현재 캡쳐한 프레임 수
    [SerializeField] private string saveDirPath;                            //캡쳐한 프레임들이 저장될 경로
    [SerializeField] private string outputImageExtension = ".png";          //저장될 프레임의 이미지 확장자

    [SerializeField]
    private Canvas canvas;                                              //로그를 위한 캔버스

    private void Awake()
    {
        var tokenCreate = destroyCancellationToken;
        camera ??= GetComponent<Camera>();
        camera.backgroundColor = Color.clear;
        DirectoryInfo di = new DirectoryInfo(saveDirPath);

        // 만약 폴더가 존재하지 않으면
        if (di.Exists == false)
        {
            di.Create();
        }
        if (canvas != null)
        {
            //Overlay Canvas는 RenderTexture를 통해 캡쳐되지 않음
            //따라서 Canvas renderMode모드를 ScreenSpaceCamera로 변경
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.planeDistance = 0.01f;
            canvas.worldCamera = camera;
        }
    }
    public void InitRecorderSetting(int targetFrameCount, string saveDirPath, string imageExtension = ".png")
    {
        if (targetFrameCount < 0)
        {
            return;
        }
        if (saveDirPath == null || saveDirPath.Equals(OSYUtils.EmptyString))
        {
            Debug.LogError("저장 경로 지정 안됨");
            return;
        }

        this.captureFrameCount = targetFrameCount;
        capturedFrameCount = 0;
        this.saveDirPath = saveDirPath;
        this.outputImageExtension = imageExtension;
    }
    public async UniTask CaptureFramesTask()
    {
        await UniTask.SwitchToMainThread();
        Debug.Log("Capturing 시작");
        Directory.CreateDirectory(saveDirPath);
        RecorderManager manager = RecorderManager.Instance;
        RecorderFlusher flusher = RecorderFlusher.Instance;
        RecorderFlusher.SaveProgress progress = new RecorderFlusher.SaveProgress();
        Stopwatch benchmarkWatch = Stopwatch.StartNew();
        manager.capturingRecorderCount++;
        manager.activeRecorderCount++;

        //프레임마다 RenderTexture를 새로 만들지 않고, 하나를 재사용함
        //(AsyncGPUReadback은 요청 시점의 내용을 복사하므로, 다음 프레임에 덮어써도 안전함)
        RenderTexture frame = new RenderTexture(GameManager.Instance.ScreenWidth, GameManager.Instance.ScreenHeight, 16);
        frame.Create();
        try
        {
            isCapturing = true;
            //카메라는 매 프레임 끝에 frame으로 한 번만 자동 렌더링됨 (camera.Render()를 따로 호출하지 않음)
            camera.targetTexture = frame;
            camera.enabled = true;
            await UniTask.Yield(PlayerLoopTiming.Update, destroyCancellationToken);

            //1. 직전 프레임에 렌더링된 결과를 GPU 비동기 읽기로 요청. 이후 인코딩 & 저장은 RecorderFlusher의 워커 스레드가 처리
            while (capturedFrameCount < captureFrameCount)
            {
                flusher.RequestFrame(frame, saveDirPath + $"{capturedFrameCount++}".PadLeft(4, '0') + outputImageExtension, progress);
                await UniTask.Yield(PlayerLoopTiming.Update, destroyCancellationToken);
            }
        }
        finally
        {
            camera.targetTexture = null;
            camera.enabled = false;
            isCapturing = false;
            //읽기 요청이 끝나기 전에 RenderTexture를 해제하지 않도록, 남은 요청을 먼저 완료시킴
            flusher.CompletePendingReadbacks();
            frame.Release();
            Destroy(frame);
            Debug.Log($"Capturing 끝, {capturedFrameCount}프레임 / 캡쳐 시간: {benchmarkWatch.ElapsedMilliseconds / 1000f}초");
            if (--manager.capturingRecorderCount == 0 && !destroyCancellationToken.IsCancellationRequested)
                manager.FlushingSetting();
        }

        //2. 이 녹화기의 프레임이 모두 저장될 때까지 대기
        await UniTask.WaitUntil(() => progress.IsDone, PlayerLoopTiming.Update, destroyCancellationToken);
        Debug.Log($"Flushing 끝, {saveDirPath} / 캡쳐 시작부터 총 작업 시간: {benchmarkWatch.ElapsedMilliseconds / 1000f}초");
        if (--manager.activeRecorderCount == 0)
        {
            flusher.ClearBufferPool();
            manager.ResetSetting();
        }

        //3. 저장된 프레임들을 영상으로 인코딩
        await flusher.EncodeVideoTask(saveDirPath);
    }
}
