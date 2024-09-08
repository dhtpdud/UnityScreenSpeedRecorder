using Cysharp.Threading.Tasks;
using OSY;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using Debug = UnityEngine.Debug;

//capturedFrames Queue에 캐싱되어 있는 프레임 데이터 들을 .png 파일로 인코딩하여 저장합니다.
public class RecorderFlusher : OSY.Singleton<RecorderFlusher>
{
    [SerializeField, ReadOnly(false)]
    private int capturedCount;

    [SerializeField, ReadOnly(false)]
    private ManageableQueue<FrameInfo> capturedFrames;
    public ManageableQueue<FrameInfo> CapturedFrames { get => capturedFrames; }
    //Task 진행 방식
    private enum TaskType
    {
        AsyncFull,
        AsyncSemi,
        SyncFull
    }

    [SerializeField, Tooltip(
        "작업의 비동기 여부를 정합니다.\n" +
        "FullAsync  - 모든 작업이 동시에 시작됩니다. (비추천)\n" +
        "SemiAsync  - 현재 작업이 한 프레임이라도 진행됐다면, 다음 작업이 실행됩니다. (권장)\n" +
        "FullSync   - 현재 작업이 모두 끝나면, 다음 작업을 실행합니다.")]
    private TaskType taskType = TaskType.AsyncSemi;

    [SerializeField, Tooltip(
        "작성된 숫자 만큼 해당 작업이 동시에 실행됩니다.\n" +
        "0) 1번 작업: 프레임 촬영 작업    (수정 불가) (GPU 작업)\n" + //GPU Memory에 캐싱
        "1) 2번 작업: RenderTexture       → Texture2D (GPU 작업)\n" + //GPU Memory → CPU Memory로 이동
        "2) 3번 작업: Texture2D           → PixelsData(byte[])\n" + //이후 CPU 멀티스래딩 작업
        "3) 4번 작업: PixelsData(byte[])  → PNGData(byte[])\n" +
        "4) 5번 작업: PNGData(byte[])     → .png파일 저장")]

    private int[] originTasksWeight;

    [SerializeField, ReadOnly(false)]
    private List<int> currentTasksWeight = new List<int>();

    [SerializeField]
    [Tooltip(
        "작업 분배기 실행 여부\n" +
        "True   -    스래드가 할당된 작업을 완료하면, 곧바로 해당 스래드에게 남은 작업을 할당합니다. (권장)\n" +
        "False  -    n번째 작업이 모두 완료된 후, n+1번째 작업을 스래드들에게 할당합니다.")]
    private bool isEnableTaskDistributer = true;

    public bool isFlushing { get; private set; }

    protected override void Awake()
    {
        capturedFrames = new ManageableQueue<FrameInfo>();
        base.Awake();
    }
    void Start()
    {
        UniTask.RunOnThreadPool(MemoryManagingTask, true, destroyCancellationToken).Forget();
    }
    async UniTaskVoid MemoryManagingTask()
    {
        while (!destroyCancellationToken.IsCancellationRequested)
        {
            capturedCount = capturedFrames.queue.Count;
            if (capturedCount >= RecorderManager.Instance.OptionFrameChunkSize)
            {
                Debug.Log("청크 꽉참");
                await FlushFramesTask(capturedFrames);

                if (RecorderManager.Instance.capturingRecorderCount > 0)
                    RecorderManager.Instance.CapturingSetting();
            }
            else if (RecorderManager.Instance.capturingRecorderCount == 0 && RecorderManager.Instance.flushDestinationQueue.Count > 0)
            {
                Debug.Log("캡쳐 모두 완료");
                await FlushFramesTask(capturedFrames);

                lock (this)
                {
                    string destination = RecorderManager.Instance.flushDestinationQueue.Dequeue();
                    string ffmpeg = $"\"{Environment.CurrentDirectory}\\ffmpeg.exe\"";
                    Process.Start(ffmpeg, $"-y -framerate {RecorderManager.Instance.OptionCaptureTargetFramerate} -f image2 -i \"{destination}%04d.png\" -c:v libsvtav1 -pix_fmt yuva420p \"{destination}result.webm\"");
                    if (RecorderManager.Instance.flushDestinationQueue.Count == 0)
                        RecorderManager.Instance.ResetSetting();
                }
            }
            await OSYUtils.YieldCaches.UniTaskYield;
        }
    }
    public async UniTask FlushFramesTask(ManageableQueue<FrameInfo> targetFrameQueue)
    {
        capturedFrames.isLock = true;
        await FlushFramesTask(capturedFrames.queue);

        //
        capturedFrames = new ManageableQueue<FrameInfo>(true);
        /* FlushingTask 이후, 반드시 위 코드처럼 capturedFrames 객체를 다시 생성해주어야함.
         * 본래, 해당 코드가 없더라도 코드상 문제가 없어 보이지만,
         * UniTask로 인한 "컨텍스트 스위칭" 과 GC가 돌아가는 과정에서 capturedFrames.queue의 메모리에 문제가 생기는 것으로 보임.
         * 때문에, 해당 코드를 통해 객체를 아예 새로 생성하고, 메모리를 다시 잡아주는 것.
         * 
         * 해당 버그는 위 코드를 비활성화시 확인 가능
         */
        capturedFrames.isLock = false;
    }
    public async UniTask FlushFramesTask(Queue<FrameInfo> targetFrameQueue)
    {
        lock (this)
        {
            if (isFlushing)
                return;
            isFlushing = true;
        }
        Debug.Log($"Flushing 시작");
        int capturedFramesCount = targetFrameQueue.Count;
        Debug.Log("처리 해야할 프레임 수: " + capturedFramesCount);
        if (capturedFramesCount == 0)
            return;

        await UniTask.SwitchToMainThread();
        RecorderManager.Instance.FlushingSetting();

        currentTasksWeight.Clear();
        currentTasksWeight = originTasksWeight.ToList();

        await UniTask.SwitchToThreadPool();

        //2번 작업 참조 아웃풋 변수
        Queue<FrameInfo> convertedFrames = new Queue<FrameInfo>();

        //3번 작업 참조 아웃풋 변수
        Queue<FrameInfo> pixelDataOfFrames = new Queue<FrameInfo>();

        //4번 작업 참조 아웃풋 변수
        Queue<FrameInfo> encodedFrames = new Queue<FrameInfo>();

        //5번 작업 참조 아웃풋 변수
        NativeArray<int> saveCount = new NativeArray<int>(1, Allocator.Persistent);

        Stopwatch benchmarkWatch = new Stopwatch();
        benchmarkWatch.Start();
        NativeArray<float> lastBenchmarkTime = new NativeArray<float>(1, Allocator.Persistent);

        Func<bool> moveToNextTaskCondition;
        #region 2. 촬영된 프레임(RenderTexture) Texture2D로 변환
        for (int i = 0; i < currentTasksWeight[1]; i++)
        {
            UniTask.RunOnThreadPool(() => FrameToTexture2DTask(targetFrameQueue, convertedFrames, capturedFramesCount), true, destroyCancellationToken).Forget();
        }
        moveToNextTaskCondition = taskType == TaskType.SyncFull ? () => convertedFrames.Count >= capturedFramesCount : () => convertedFrames.Count > 0;
        await WaitForTaskManager("2번 - (RenderTexture → Texture2D)", moveToNextTaskCondition, () => targetFrameQueue.Count == 0, () =>
        {
            int weightThreadHav = currentTasksWeight[1];
            currentTasksWeight[1] = 0;
            for (int i = 0; i < weightThreadHav / 3f; i++)
            {
                if (convertedFrames.Count > 0)
                    UniTask.RunOnThreadPool(() => Texture2DFrameToPixelData(convertedFrames, pixelDataOfFrames, capturedFramesCount), true, destroyCancellationToken).Forget();
                if (pixelDataOfFrames.Count > 0)
                    UniTask.RunOnThreadPool(() => PixelDataFrameToPNG(pixelDataOfFrames, encodedFrames, capturedFramesCount), true, destroyCancellationToken).Forget();
                if (encodedFrames.Count > 0)
                    UniTask.RunOnThreadPool(() => PNGSaveTask(encodedFrames, capturedFramesCount, saveCount), true, destroyCancellationToken).Forget();
                lock (currentTasksWeight)
                {
                    currentTasksWeight[2]++;
                    currentTasksWeight[3]++;
                    currentTasksWeight[4]++;
                }
            }
        }, benchmarkWatch, lastBenchmarkTime);
        #endregion

        benchmarkWatch.Start();
        #region 3. 변환된 Texture2D 픽셀데이터로 변환
        for (int i = 0; (taskType == TaskType.AsyncFull ? true : convertedFrames.Count > 0) && i < currentTasksWeight[2]; i++)
        {
            UniTask.RunOnThreadPool(() => Texture2DFrameToPixelData(convertedFrames, pixelDataOfFrames, capturedFramesCount), true, destroyCancellationToken).Forget();
        }
        moveToNextTaskCondition = taskType == TaskType.SyncFull ? () => pixelDataOfFrames.Count >= capturedFramesCount : () => pixelDataOfFrames.Count > 0;
        await WaitForTaskManager("3번 - (Texture2D → pixelData 바이트 배열)", moveToNextTaskCondition, () => convertedFrames.Count == 0, () =>
        {
            int weightThreadHav = currentTasksWeight[2];
            currentTasksWeight[2] = 0;
            for (int i = 0; i < weightThreadHav / 2f; i++)
            {
                if (pixelDataOfFrames.Count > 0)
                    UniTask.RunOnThreadPool(() => PixelDataFrameToPNG(pixelDataOfFrames, encodedFrames, capturedFramesCount), true, destroyCancellationToken).Forget();
                if (encodedFrames.Count > 0)
                    UniTask.RunOnThreadPool(() => PNGSaveTask(encodedFrames, capturedFramesCount, saveCount), true, destroyCancellationToken).Forget();
                lock (currentTasksWeight)
                {
                    currentTasksWeight[3]++;
                    currentTasksWeight[4]++;
                }
            }
        }, benchmarkWatch, lastBenchmarkTime);
        #endregion

        benchmarkWatch.Start();
        #region 4. 프레임 픽셀데이터 PNG로 인코딩
        for (int i = 0; (taskType == TaskType.AsyncFull ? true : pixelDataOfFrames.Count > 0) && i < currentTasksWeight[3]; i++)
        {
            UniTask.RunOnThreadPool(() => PixelDataFrameToPNG(pixelDataOfFrames, encodedFrames, capturedFramesCount), true, destroyCancellationToken).Forget();
        }
        moveToNextTaskCondition = taskType == TaskType.SyncFull ? () => encodedFrames.Count >= capturedFramesCount : () => encodedFrames.Count > 0;
        await WaitForTaskManager("4번 - (pixelData 바이트 배열 → PNG 인코딩 바이트 배열)", moveToNextTaskCondition, () => pixelDataOfFrames.Count == 0, () =>
        {
            int weightThreadHav = currentTasksWeight[3];
            currentTasksWeight[3] = 0;
            for (int i = 0; i < weightThreadHav; i++)
            {
                if (encodedFrames.Count > 0)
                    UniTask.RunOnThreadPool(() => PNGSaveTask(encodedFrames, capturedFramesCount, saveCount), true, destroyCancellationToken).Forget();
                lock (currentTasksWeight)
                    currentTasksWeight[4]++;
            }
        }, benchmarkWatch, lastBenchmarkTime);
        #endregion

        benchmarkWatch.Start();
        #region 5. 인코딩된 프레임 .png 파일로 저장
        for (int i = 0; (taskType == TaskType.AsyncFull ? true : encodedFrames.Count > 0) && i < currentTasksWeight[4]; i++)
        {
            UniTask.RunOnThreadPool(() => PNGSaveTask(encodedFrames, capturedFramesCount, saveCount), true, destroyCancellationToken).Forget();
        }
        #endregion

        Debug.Log("작업 분배 완료, 모든 작업 완료까지 대기");
        await OSYUtils.WaitUntil(() => saveCount[0] >= capturedFramesCount, UniTask.Yield(PlayerLoopTiming.Update), destroyCancellationToken);

        Debug.Log("메모리 정리 시작");
        await UniTask.SwitchToMainThread();
#if !UNITY_EDITOR
        UnityEngine.Scripting.GarbageCollector.GCMode = UnityEngine.Scripting.GarbageCollector.Mode.Enabled;
#endif
        lastBenchmarkTime.Dispose();
        saveCount.Dispose();
        targetFrameQueue.Clear();
        await Resources.UnloadUnusedAssets();
        GC.Collect();
        Debug.Log("메모리 정리 완료");

        benchmarkWatch.Stop();
        Debug.Log($"Flushing 끝, {capturedFramesCount}프레임 Flushed \n" +
            $"TaskWeight: {originTasksWeight[0]}, {originTasksWeight[1]}, {originTasksWeight[2]}, {originTasksWeight[3]}, {originTasksWeight[4]} / 총 작업 시간: {benchmarkWatch.ElapsedMilliseconds / 1000f}초");

        isFlushing = false;
    }
    async UniTask WaitForTaskManager(string currentTaskName, Func<bool> moveToNextTaskCondition, Func<bool> taskCompleteCondition, Action TaskDistributeAction, Stopwatch benchmarkWatch, NativeArray<float> lastBenchmarkTime)
    {
        if (destroyCancellationToken.IsCancellationRequested) return;
        switch (taskType)
        {
            case TaskType.AsyncFull:
                UniTask.RunOnThreadPool(async () =>
                {
                    await OSYUtils.WaitUntil(moveToNextTaskCondition, UniTask.Yield(PlayerLoopTiming.Update), destroyCancellationToken);
                    await OSYUtils.WaitUntilStopwatch(taskCompleteCondition, benchmarkWatch, lastBenchmarkTime, currentTaskName, destroyCancellationToken);
                }, true, destroyCancellationToken).Forget();
                break;
            case TaskType.AsyncSemi:
                await OSYUtils.WaitUntil(moveToNextTaskCondition, UniTask.Yield(PlayerLoopTiming.Update), destroyCancellationToken);
                break;
            case TaskType.SyncFull:
                await OSYUtils.WaitUntilStopwatch(moveToNextTaskCondition, benchmarkWatch, lastBenchmarkTime, currentTaskName, destroyCancellationToken);
                break;
        }
        if (isEnableTaskDistributer)
        {
            UniTask.RunOnThreadPool(async () =>
            {
                if (taskType == TaskType.AsyncSemi)
                    await OSYUtils.WaitUntilStopwatch(taskCompleteCondition, benchmarkWatch, lastBenchmarkTime, currentTaskName, destroyCancellationToken);
                TaskDistributeAction.Invoke();
            }, true, destroyCancellationToken).Forget();
        }
    }

    #region 2 ~ 5 작업 함수
    async UniTaskVoid FrameToTexture2DTask(Queue<FrameInfo> targetFrames, Queue<FrameInfo> convertedFrames, int totalFrameCount)
    {
        await UniTask.SwitchToMainThread();
        while (!destroyCancellationToken.IsCancellationRequested)
        {
            //Debug.Log($"2번 작업자: {Task.CurrentId}사로 입장!");
            //2. 촬영된 프레임(RenderTexture) Texture2D로 변환
            FrameInfo frame;
            try
            {
                lock (targetFrames)
                {
                    if (convertedFrames.Count >= totalFrameCount) break;
                    frame = targetFrames.Dequeue();
                }
            }
            catch (InvalidOperationException)
            {
                await UniTask.Yield(PlayerLoopTiming.Update);
                continue;
            }
            if (frame.data == null)
            {
                //Debug.LogWarning("Null 파일!");
                continue;
            }
            Texture2D outputTexture = new Texture2D(GameManager.Instance.ScreenWidth, GameManager.Instance.ScreenHeight, TextureFormat.RGBA32, false);
            frame.data = RenderTextureToTexture2D((RenderTexture)frame.data, ref outputTexture);
            convertedFrames.Enqueue(frame);
        }
    }
    Texture2D RenderTextureToTexture2D(RenderTexture target, ref Texture2D result)
    {
        //읽어보면 좋은글:
        //https://medium.com/google-developers/real-time-image-capture-in-unity-458de1364a4c
        //https://forum.unity.com/threads/rendertexture-to-texture2d-too-slow.693850/


        //방법 1.
        //GPU 병목구간
        //RenderTexture.active, ReadPixels은 메인스레드에서만 동작가능
        //코루틴을 통해 여러 스래드에서 gpu에게 ReadPixels을 한번에 요청이 가능 할까?
        RenderTexture.active = target;
        result.ReadPixels(GameManager.Instance.ScreenRect, 0, 0);
        result.name = target.name;
        result.Apply();

        //방법 2.
        //코드 출처 - https://forum.unity.com/threads/any-way-to-readpixels-from-another-thread-so-as-to-not-block-the-main-thread.1421267/
        //에러: UnityException: get_activeColorSpace can only be called from the main thread.
        /*AsyncGPUReadback.Request(rTex, 0, TextureFormat.RGBA32, (AsyncGPUReadbackRequest request) =>
        {
            if (request.hasError)
                Debug.LogError("GPU readback error.");
            else if (tex != null)
            {
                // copy all data into a regular, CPU-accessible texture.
                //You might want to just fetch the data for a specific pixel from GetData directly,
                // or use one of the overloads to request a specific rect in the texture.
                var temp = request.GetData<byte>();
                tex.LoadRawTextureData(temp);
                tex.Apply();
                temp.Dispose();
            }
        });*/

        //방법 3. 4.
        //오류 회색 이미지가 저장됨
        /*RenderTexture.active = rTex;
        Graphics.CopyTexture(rTex, tex);
        //Graphics.ConvertTexture(rTex, tex);
        tex.name = rTex.name;
        await UniTask.Yield();
        tex.Apply();*/

        //방법 5.
        //코드 출처 - https://forum.unity.com/threads/using-copytexture-from-rendertexture-to-texture2d-not-working.502451/#post-6981458
        //에러: UnityException: Request_Internal_Texture_1 can only be called from the main thread.
        /*await AsyncGPUReadback.Request(rTex, 0, (AsyncGPUReadbackRequest asyncAction) =>
        {
            tex.SetPixelData(asyncAction.GetData<byte>(), 0);
            tex.Apply();
        });*/

        return result;
    }
    async UniTaskVoid Texture2DFrameToPixelData(Queue<FrameInfo> targetFrames, Queue<FrameInfo> pixelDataOfFrames, int totalFrameCount)
    {
        //Debug.Log($"3번 작업자: {Task.CurrentId}사로 입장!");
        //3. 변환된 Texture2D 픽셀데이터로 변환
        while (!destroyCancellationToken.IsCancellationRequested)
        {
            FrameInfo frame;
            try
            {
                lock (targetFrames)
                {
                    if (pixelDataOfFrames.Count >= totalFrameCount) break;
                    frame = targetFrames.Dequeue();
                }
            }
            catch (InvalidOperationException)
            {
                await UniTask.Yield(PlayerLoopTiming.Update);
                continue;
            }
            if (frame.data == null)
            {
                //Debug.LogWarning("Null 파일!");
                await UniTask.Yield(PlayerLoopTiming.Update);
                continue;
            }
            while (!destroyCancellationToken.IsCancellationRequested)
            {
                try
                {
                    frame.data = ((Texture2D)frame.data).GetPixelData<byte>(0).ToArray();
                    break;
                }
                catch (UnityException)
                {
                    await UniTask.Yield(PlayerLoopTiming.Update);
                    continue;
                }
            }
            lock (pixelDataOfFrames)
                pixelDataOfFrames.Enqueue(frame);
        }

    }
    async UniTaskVoid PixelDataFrameToPNG(Queue<FrameInfo> targetFrames, Queue<FrameInfo> encodedFrames, int totalFrameCount)
    {
        //Debug.Log($"4번 작업자: {Task.CurrentId}사로 입장!");
        //4. 프레임 픽셀데이터 PNG로 인코딩
        while (!destroyCancellationToken.IsCancellationRequested)
        {
            FrameInfo frame;
            try
            {
                lock (targetFrames)
                {
                    if (encodedFrames.Count >= totalFrameCount) break;
                    frame = targetFrames.Dequeue();
                }
            }
            catch (InvalidOperationException)
            {
                await UniTask.Yield(PlayerLoopTiming.Update);
                continue;
            }
            if (frame.data == null)
            {
                //Debug.LogWarning("Null 파일!");
                await UniTask.Yield(PlayerLoopTiming.Update);
                continue;
            }
            frame.data = ImageConversion.EncodeArrayToPNG((byte[])frame.data, GraphicsFormat.R8G8B8A8_SRGB, (uint)GameManager.Instance.ScreenWidth, (uint)GameManager.Instance.ScreenHeight);
            lock (encodedFrames)
                encodedFrames.Enqueue(frame);
        }
    }
    async UniTaskVoid PNGSaveTask(Queue<FrameInfo> targetFrames, int totalFrameCount, NativeArray<int> saveCount)
    {
        //Debug.Log($"5번 작업자: {Task.CurrentId}사로 입장!");
        //5. 인코딩된 프레임 .png 파일로 저장
        while (!destroyCancellationToken.IsCancellationRequested && saveCount != null)
        {
            FrameInfo frame;
            try
            {
                lock (this)
                    if (saveCount[0] >= totalFrameCount) break;
                lock (targetFrames)
                    frame = targetFrames.Dequeue();
            }
            catch (InvalidOperationException)
            {
                await UniTask.Yield(PlayerLoopTiming.Update);
                continue;
            }
            if (frame.data == null)
            {
                //Debug.LogWarning("Null 파일!");
                await UniTask.Yield(PlayerLoopTiming.Update);
                lock (this)
                    saveCount[0]++;
                continue;
            }

            UniTask.RunOnThreadPool(async () =>
            {
                await File.WriteAllBytesAsync(frame.path, (byte[])frame.data, destroyCancellationToken);
                lock (this)
                    saveCount[0]++;
            }, true, destroyCancellationToken).Forget();
        }
    }
    #endregion
}
