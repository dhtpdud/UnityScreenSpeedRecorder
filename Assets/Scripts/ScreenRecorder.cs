using Cysharp.Threading.Tasks;
using OSY;
using System.Diagnostics;
using System.IO;
using UnityEngine;
using Debug = UnityEngine.Debug;

//targetFrameCount만큼 화면을 캡쳐합니다.
//캡쳐한 프레임은 RecorderFlusher가 GPU에서 비동기로 읽어 와(AsyncGPUReadback), 촬영과 동시에 ffmpeg로 영상 인코딩합니다.
public class ScreenRecorder : MonoBehaviour
{
    [SerializeField, ReadOnly(false)] private bool isCapturing;
    public bool IsCapturing { get => isCapturing; }
    public new Camera camera = null;                                        //카메라
    [SerializeField] private int captureFrameCount = -1;                     //목표 캡쳐 프레임 수
    [SerializeField, ReadOnly(false)] private int capturedFrameCount;       //현재 캡쳐한 프레임 수
    [SerializeField] private string saveDirPath;                            //영상이 저장될 경로
    [SerializeField] private string outputVideoFileName = "result.mp4";     //저장될 영상 파일 이름

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
    public void InitRecorderSetting(int targetFrameCount, string saveDirPath, string videoFileName = "result.mp4")
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
        this.outputVideoFileName = videoFileName;
    }
    public async UniTask CaptureFramesTask()
    {
        await UniTask.SwitchToMainThread();
        Debug.Log("Capturing 시작");
        Directory.CreateDirectory(saveDirPath);
        RecorderManager manager = RecorderManager.Instance;
        RecorderFlusher flusher = RecorderFlusher.Instance;
        int width = GameManager.Instance.ScreenWidth;
        int height = GameManager.Instance.ScreenHeight;
        //ffmpeg를 먼저 실행해 두고, 캡쳐한 프레임을 순서대로 넘김
        RecorderFlusher.VideoSession session = flusher.StartVideoSession(saveDirPath + outputVideoFileName, width, height);
        if (session == null)
        {
            if (manager.activeRecorderCount == 0)
                manager.ResetSetting();
            return;
        }
        Stopwatch benchmarkWatch = Stopwatch.StartNew();
        manager.capturingRecorderCount++;
        manager.activeRecorderCount++;

        //프레임마다 RenderTexture를 새로 만들지 않고, 하나를 재사용함
        //(AsyncGPUReadback은 요청 시점의 내용을 복사하므로, 다음 프레임에 덮어써도 안전함)
        RenderTexture frame = new RenderTexture(width, height, 16);
        frame.Create();
        try
        {
            isCapturing = true;
            //카메라는 매 프레임 끝에 frame으로 한 번만 자동 렌더링됨 (camera.Render()를 따로 호출하지 않음)
            camera.targetTexture = frame;
            camera.enabled = true;
            await UniTask.Yield(PlayerLoopTiming.Update, destroyCancellationToken);

            //1. 직전 프레임에 렌더링된 결과를 GPU 비동기 읽기로 요청. 읽어 온 프레임은 RecorderFlusher가 ffmpeg로 넘김
            while (capturedFrameCount < captureFrameCount)
            {
                flusher.RequestFrame(frame, session);
                capturedFrameCount++;
                await UniTask.Yield(PlayerLoopTiming.Update, destroyCancellationToken);
            }
        }
        finally
        {
            camera.targetTexture = null;
            camera.enabled = false;
            isCapturing = false;
            //읽기 요청이 끝나기 전에 RenderTexture를 해제하지 않도록, 남은 요청을 먼저 완료시킨 뒤 ffmpeg 입력을 마무리함
            flusher.FinishVideoSession(session);
            frame.Release();
            Destroy(frame);
            Debug.Log($"Capturing 끝, {capturedFrameCount}프레임 / 캡쳐 시간: {benchmarkWatch.ElapsedMilliseconds / 1000f}초");
            if (--manager.capturingRecorderCount == 0 && !destroyCancellationToken.IsCancellationRequested)
                manager.FlushingSetting();
        }

        //2. ffmpeg가 남은 프레임을 모두 인코딩할 때까지 대기
        await UniTask.WaitUntil(() => session.IsDone, PlayerLoopTiming.Update, destroyCancellationToken);
        if (session.ExitCode == 0)
            Debug.Log($"영상 인코딩 완료: {session.OutputPath} ({session.EncoderName}, {session.WrittenFrameCount}프레임) / 캡쳐 시작부터 총 작업 시간: {benchmarkWatch.ElapsedMilliseconds / 1000f}초");
        else
            Debug.LogError($"ffmpeg 인코딩 실패 (exit code: {session.ExitCode}): {session.OutputPath}");
        if (--manager.activeRecorderCount == 0)
        {
            flusher.ClearBufferPool();
            manager.ResetSetting();
        }
    }
}
