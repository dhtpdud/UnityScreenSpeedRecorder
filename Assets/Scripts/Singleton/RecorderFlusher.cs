using Cysharp.Threading.Tasks;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

//ScreenRecorder가 요청한 프레임을 GPU에서 비동기로 읽어 와(AsyncGPUReadback), 워커 스레드에서 .png 파일로 인코딩하여 저장합니다.
//촬영과 저장이 동시에 진행되며, 메모리에 올라간 프레임 수가 FrameChunkSize에 도달하면 저장이 따라올 때까지 메인 스레드를 잠시 멈춥니다.
//(메인 스레드가 멈춰 있는 동안에는 게임 시간도 흐르지 않으므로, 프레임이 누락되지 않습니다.)
public class RecorderFlusher : OSY.Singleton<RecorderFlusher>
{
    //녹화기 하나가 요청했지만, 아직 저장되지 않은 프레임 수
    public class SaveProgress
    {
        public int remainingFrameCount;
        public bool IsDone => Volatile.Read(ref remainingFrameCount) == 0;
    }

    private struct FrameData
    {
        public AsyncGPUReadbackRequest request;
        public NativeArray<byte> pixels;
        public string path;
        public int width;
        public int height;
        public SaveProgress progress;
    }

    [SerializeField, Tooltip(
        "PNG 인코딩 & 저장을 동시에 처리할 워커 스레드 수입니다.\n" +
        "0 이하이면 (논리 코어 수 - 1)개를 사용합니다.")]
    private int workerCount = 0;

    [SerializeField, ReadOnly(false)] private int readbackFrameCount;   //GPU에서 읽어 오는 중인 프레임 수
    [SerializeField, ReadOnly(false)] private int encodeQueueCount;     //인코딩 대기 중인 프레임 수
    [SerializeField, ReadOnly(false)] private int savedFrameCount;      //저장 완료된 프레임 수 (누적)

    //메인 스레드에서만 접근
    private readonly Queue<FrameData> pendingReadbacks = new Queue<FrameData>();
    //메인 스레드 → 워커 스레드
    private readonly BlockingCollection<FrameData> encodeQueue = new BlockingCollection<FrameData>();
    //프레임마다 버퍼를 새로 할당하지 않도록 재사용
    private readonly ConcurrentBag<NativeArray<byte>> pixelBufferPool = new ConcurrentBag<NativeArray<byte>>();
    //메모리에 동시에 올라갈 수 있는 프레임 수 제한
    private SemaphoreSlim frameSlots;
    private CancellationTokenSource workerCTS;
    private Thread[] workers;

    void Start()
    {
        int maxBufferedFrames = RecorderManager.Instance.OptionFrameChunkSize > 0 ? RecorderManager.Instance.OptionFrameChunkSize : 64;
        frameSlots = new SemaphoreSlim(maxBufferedFrames, maxBufferedFrames);

        workerCTS = new CancellationTokenSource();
        workers = new Thread[workerCount > 0 ? workerCount : Mathf.Max(1, SystemInfo.processorCount - 1)];
        for (int i = 0; i < workers.Length; i++)
        {
            workers[i] = new Thread(EncodeWorker) { IsBackground = true, Name = $"RecorderFlusher Worker {i}" };
            workers[i].Start();
        }
        Debug.Log($"RecorderFlusher 시작, 워커 스레드: {workers.Length}개 / 최대 버퍼 프레임: {maxBufferedFrames}");
    }
    void Update()
    {
        ProcessCompletedReadbacks();
        readbackFrameCount = pendingReadbacks.Count;
        encodeQueueCount = encodeQueue.Count;
    }
    private void OnDestroy()
    {
        if (workers == null)
            return;
        workerCTS.Cancel();
        bool isAllWorkersStopped = true;
        foreach (Thread worker in workers)
            isAllWorkersStopped &= worker.Join(5000);
        if (!isAllWorkersStopped)
        {
            //아직 버퍼를 사용 중인 워커가 있으므로 해제하지 않음
            Debug.LogWarning("RecorderFlusher 워커 스레드가 종료되지 않아, 프레임 버퍼를 해제하지 않습니다.");
            return;
        }
        AsyncGPUReadback.WaitAllRequests();
        while (pendingReadbacks.Count > 0)
            pendingReadbacks.Dequeue().pixels.Dispose();
        while (encodeQueue.TryTake(out FrameData frame))
            frame.pixels.Dispose();
        while (pixelBufferPool.TryTake(out NativeArray<byte> pixels))
            pixels.Dispose();
        workerCTS.Dispose();
    }

    //1. source의 현재 내용을 GPU에서 비동기로 읽어 오도록 요청합니다. (메인 스레드 전용)
    public void RequestFrame(RenderTexture source, string path, SaveProgress progress)
    {
        ProcessCompletedReadbacks();
        //빈 슬롯이 없으면, 가장 오래된 읽기 요청부터 완료시켜 인코딩으로 넘기고
        //그래도 없으면 워커가 프레임을 저장할 때까지 대기
        while (!frameSlots.Wait(0))
        {
            if (pendingReadbacks.Count > 0)
            {
                FrameData oldest = pendingReadbacks.Dequeue();
                oldest.request.WaitForCompletion();
                OnReadbackCompleted(oldest);
            }
            else
            {
                frameSlots.Wait(destroyCancellationToken);
                break;
            }
        }

        int size = source.width * source.height * 4;
        if (!pixelBufferPool.TryTake(out NativeArray<byte> pixels) || pixels.Length != size)
        {
            if (pixels.IsCreated)
                pixels.Dispose();
            pixels = new NativeArray<byte>(size, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        }
        Interlocked.Increment(ref progress.remainingFrameCount);
        AsyncGPUReadbackRequest request = AsyncGPUReadback.RequestIntoNativeArray(ref pixels, source, 0, TextureFormat.RGBA32);
        pendingReadbacks.Enqueue(new FrameData
        {
            request = request,
            pixels = pixels,
            path = path,
            width = source.width,
            height = source.height,
            progress = progress
        });
    }
    //아직 끝나지 않은 읽기 요청을 모두 완료시킵니다. (메인 스레드 전용)
    public void CompletePendingReadbacks()
    {
        while (pendingReadbacks.Count > 0)
        {
            FrameData frame = pendingReadbacks.Dequeue();
            frame.request.WaitForCompletion();
            OnReadbackCompleted(frame);
        }
    }
    //녹화가 모두 끝나면, 재사용하려고 들고 있던 프레임 버퍼를 해제합니다. (메인 스레드 전용)
    public void ClearBufferPool()
    {
        while (pixelBufferPool.TryTake(out NativeArray<byte> pixels))
            pixels.Dispose();
    }
    void ProcessCompletedReadbacks()
    {
        //요청은 순서대로 완료되므로, 앞에서부터 끝난 것만 꺼냄
        while (pendingReadbacks.Count > 0 && pendingReadbacks.Peek().request.done)
            OnReadbackCompleted(pendingReadbacks.Dequeue());
    }
    void OnReadbackCompleted(FrameData frame)
    {
        if (frame.request.hasError)
        {
            Debug.LogError($"GPU 리드백 실패, 프레임이 누락됩니다: {frame.path}");
            ReleaseFrame(frame);
            return;
        }
        encodeQueue.Add(frame);
    }
    void ReleaseFrame(FrameData frame)
    {
        pixelBufferPool.Add(frame.pixels);
        Interlocked.Decrement(ref frame.progress.remainingFrameCount);
        frameSlots.Release();
    }

    //2. 픽셀 데이터 → PNG 인코딩 → .png 파일 저장 (워커 스레드)
    void EncodeWorker()
    {
        byte[] writeBuffer = new byte[1 << 20];
        try
        {
            foreach (FrameData frame in encodeQueue.GetConsumingEnumerable(workerCTS.Token))
            {
                try
                {
                    //EncodeNativeArrayToPNG는 스레드 안전하며, 관리 힙에 할당하지 않음
                    using (NativeArray<byte> png = ImageConversion.EncodeNativeArrayToPNG(frame.pixels, GraphicsFormat.R8G8B8A8_SRGB, (uint)frame.width, (uint)frame.height))
                    using (FileStream file = new FileStream(frame.path, FileMode.Create, FileAccess.Write, FileShare.None, 4096))
                    {
                        for (int offset = 0; offset < png.Length; offset += writeBuffer.Length)
                        {
                            int count = Math.Min(writeBuffer.Length, png.Length - offset);
                            NativeArray<byte>.Copy(png, offset, writeBuffer, 0, count);
                            file.Write(writeBuffer, 0, count);
                        }
                    }
                    Interlocked.Increment(ref savedFrameCount);
                }
                catch (Exception e)
                {
                    Debug.LogError($"프레임 저장 실패: {frame.path}\n{e}");
                }
                finally
                {
                    ReleaseFrame(frame);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    //3. 저장된 .png 파일들을 ffmpeg로 영상 인코딩하고, 끝날 때까지 대기합니다.
    public async UniTask EncodeVideoTask(string destination)
    {
        bool isWindows = Application.platform == RuntimePlatform.WindowsPlayer || Application.platform == RuntimePlatform.WindowsEditor;
        //실행 폴더의 ffmpeg를 우선 사용하고, 없으면 PATH에 등록된 ffmpeg를 사용
        string ffmpegName = isWindows ? "ffmpeg.exe" : "ffmpeg";
        string ffmpegPath = Path.Combine(Environment.CurrentDirectory, ffmpegName);
        if (!File.Exists(ffmpegPath))
            ffmpegPath = ffmpegName;

        ProcessStartInfo startInfo = new ProcessStartInfo(ffmpegPath, $"-y -framerate {RecorderManager.Instance.OptionCaptureTargetFramerate} -f image2 -i \"{destination}%04d.png\" -c:v libsvtav1 -pix_fmt yuva420p \"{destination}result.webm\"")
        {
            //Windows에서는 기존처럼 ffmpeg 콘솔 창을 띄움
            UseShellExecute = isWindows
        };
        Process process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Win32Exception)
        {
            Debug.LogError("ffmpeg을 찾을 수 없습니다.");
            return;
        }
        if (process == null)
            return;

        Debug.Log($"영상 인코딩 시작: {destination}result.webm");
        using (process)
        {
            await UniTask.SwitchToThreadPool();
            process.WaitForExit();
            int exitCode = process.ExitCode;
            await UniTask.SwitchToMainThread();
            if (exitCode == 0)
                Debug.Log($"영상 인코딩 완료: {destination}result.webm");
            else
                Debug.LogError($"ffmpeg 인코딩 실패 (exit code: {exitCode}): {destination}");
        }
    }
}
