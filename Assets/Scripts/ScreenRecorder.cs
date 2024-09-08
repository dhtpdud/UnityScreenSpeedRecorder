using Cysharp.Threading.Tasks;
using OSY;
using UnityEngine;

//targetFrameCount만큼 화면을 캡쳐하며, 캡쳐한 프레임들은 capturedFrames Queue에 캐싱합니다.
//이후 캐싱된 프레임들은 RecorderFlusher에 의해 .png파일로 저장됩니다.
public class ScreenRecorder : MonoBehaviour
{
    [SerializeField, ReadOnly(false)] private bool isCapturing;
    public bool IsCapturing { get => isCapturing; }
    public new Camera camera = null;                                        //카메라
    [SerializeField] private int targetFrameCount = -1;                     //목표 캡쳐 프레임 수
    [SerializeField, ReadOnly(false)] private int capturedFrameCount;       //현재 캡쳐한 프레임 수
    [SerializeField] private string saveDirPath;                            //캐싱한 프레임들이 마지막에 저장될 경로
    [SerializeField] private string outputImageExtension = ".png";          //저장될 프레임의 이미지 확장자
    private ManageableQueue<FrameInfo> capturedFrames                       //캡쳐한 프레임들을 캐싱해둘 Queue
    {
        get => RecorderFlusher.Instance.CapturedFrames;
    }

    [SerializeField]
    private Canvas canvas;                                              //로그를 위한 캔버스

    private void Awake()
    {
        var tokenCreate = destroyCancellationToken;
        camera ??= GetComponent<Camera>();
        camera.backgroundColor = Color.clear;

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

        this.targetFrameCount = targetFrameCount;
        capturedFrameCount = 0;
        this.saveDirPath = saveDirPath;
        this.outputImageExtension = imageExtension;
    }
    public async UniTask CaptureFramesTask()
    {
        Debug.Log("Capturing 시작");
        lock (RecorderFlusher.Instance)
            RecorderManager.Instance.capturingRecorderCount++;
        try
        {
            await UniTask.SwitchToMainThread();
            isCapturing = true;
            camera.enabled = true;
            //1. 모든 프레임 캡쳐 후, capturedFrames(Queue)에 촬영된 프레임(RenderTexture) 저장
            while (capturedFrameCount < targetFrameCount)
            {
                await OSYUtils.WaitUntil(() => !capturedFrames.isLock, OSYUtils.YieldCaches.UniTaskYield, destroyCancellationToken);
                RenderTexture frame = new RenderTexture(GameManager.Instance.ScreenWidth, GameManager.Instance.ScreenHeight, 16);
                camera.targetTexture = frame;
                camera.Render();
                capturedFrames.queue.Enqueue(new FrameInfo(frame, saveDirPath + $"{capturedFrameCount++}".PadLeft(4, '0') + outputImageExtension));
                await OSYUtils.YieldCaches.UniTaskYield;
            }
        }
        finally
        {
            camera.targetTexture = null;
            camera.enabled = false;
            isCapturing = false;
            Debug.Log("Capturing 끝");
            lock (RecorderFlusher.Instance)
            {
                RecorderManager.Instance.capturingRecorderCount--;
                RecorderManager.Instance.flushDestinationQueue.Enqueue(saveDirPath);
            }
        }
    }
}
