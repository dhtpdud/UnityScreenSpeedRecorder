using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

//ScreenRecorder가 요청한 프레임을 GPU에서 비동기로 읽어 와(AsyncGPUReadback), 녹화기마다 실행한 ffmpeg의 표준 입력으로 바로 넘깁니다.
//PNG 인코딩 & 저장 없이 ffmpeg가 촬영과 동시에 영상을 인코딩하며, 가능하면 GPU 하드웨어 인코더(NVENC, QSV, AMF)를 사용합니다.
//메모리에 올라간 프레임 수가 FrameChunkSize에 도달하면 ffmpeg가 따라올 때까지 메인 스레드를 잠시 멈춥니다.
//(메인 스레드가 멈춰 있는 동안에는 게임 시간도 흐르지 않으므로, 프레임이 누락되지 않습니다.)
public class RecorderFlusher : OSY.Singleton<RecorderFlusher>
{
    internal struct FrameData
    {
        public AsyncGPUReadbackRequest request;
        public NativeArray<byte> pixels;
        public VideoSession session;
    }

    //녹화기 하나의 영상 인코딩 작업 (ffmpeg 프로세스 1개)
    public class VideoSession
    {
        public string OutputPath { get; internal set; }
        public string EncoderName { get; internal set; }
        public bool IsDone => isDone;
        public int ExitCode { get; internal set; }
        public bool IsSucceeded { get; internal set; }   //인코딩과 최종 경로로 옮기기까지 성공했는지
        public int WrittenFrameCount => writtenFrameCount;

        internal Process process;
        internal Thread writer;
        internal readonly BlockingCollection<FrameData> frames = new BlockingCollection<FrameData>();
        internal volatile bool isDone;
        internal int writtenFrameCount;
        internal string arguments;
        internal string encodingPath;   //ffmpeg가 실제로 쓰는 임시 파일 경로
        //실패 원인을 남기기 위해 ffmpeg가 출력한 마지막 로그를 보관
        internal readonly ConcurrentQueue<string> ffmpegLog = new ConcurrentQueue<string>();
    }

    //빠른 순서대로 시도할 H.264 인코더 (GPU 하드웨어 인코더 우선)
    private static readonly (string name, string args)[] videoEncoders =
    {
        ("h264_nvenc", "-c:v h264_nvenc -preset p1 -rc vbr -cq 23 -b:v 0"),         //NVIDIA
        ("h264_qsv", "-c:v h264_qsv -preset veryfast -global_quality 23"),          //Intel
        ("h264_amf", "-c:v h264_amf -quality speed -rc cqp -qp_i 23 -qp_p 23"),     //AMD
        ("libx264", "-c:v libx264 -preset ultrafast -crf 23 -pix_fmt yuv420p"),     //CPU (하드웨어 인코더가 없을 때)
    };

    [SerializeField, ReadOnly(false)] private string ffmpegPath;
    [SerializeField, ReadOnly(false)] private string videoEncoderName;
    [SerializeField, ReadOnly(false)] private int readbackFrameCount;   //GPU에서 읽어 오는 중인 프레임 수

    //메인 스레드에서만 접근
    private readonly Queue<FrameData> pendingReadbacks = new Queue<FrameData>();
    //프레임마다 버퍼를 새로 할당하지 않도록 재사용
    private readonly ConcurrentBag<NativeArray<byte>> pixelBufferPool = new ConcurrentBag<NativeArray<byte>>();
    private readonly List<VideoSession> activeSessions = new List<VideoSession>();
    //메모리에 동시에 올라갈 수 있는 프레임 수 제한
    private SemaphoreSlim frameSlots;
    private Task<(string ffmpegPath, (string name, string args) encoder)> videoEncoderTask;

    void Start()
    {
        int maxBufferedFrames = RecorderManager.Instance.OptionFrameChunkSize > 0 ? RecorderManager.Instance.OptionFrameChunkSize : 64;
        frameSlots = new SemaphoreSlim(maxBufferedFrames, maxBufferedFrames);

        //녹화 시작 전에 미리 ffmpeg를 준비하고, 사용 가능한 인코더를 찾아 둠
        bool isWindows = Application.platform == RuntimePlatform.WindowsPlayer || Application.platform == RuntimePlatform.WindowsEditor;
        string streamingAssetsPath = Application.streamingAssetsPath;
        //persistentDataPath(AppData\LocalLow)에 푼 실행 파일은 '낮은 무결성' 레이블을 물려받아,
        //실행된 ffmpeg가 LocalLow 밖(Temp, 바탕 화면 등)에 파일을 쓰지 못함. 따라서 AppData\Local에 풂
        string extractDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Application.companyName, Application.productName, "ffmpeg");
        string oldExtractDirectory = Path.Combine(Application.persistentDataPath, "ffmpeg");
        string preferredEncoder = RecorderManager.Instance.OptionVideoEncoder;
        videoEncoderTask = Task.Run(() =>
        {
            DeleteDirectory(oldExtractDirectory);
            string path = FindFfmpeg(isWindows, streamingAssetsPath, extractDirectory);
            return (path, SelectVideoEncoder(path, preferredEncoder));
        });
    }
    void Update()
    {
        ProcessCompletedReadbacks();
        readbackFrameCount = pendingReadbacks.Count;
    }
    private void OnDestroy()
    {
        if (frameSlots == null)
            return;
        //인코딩 중인 ffmpeg를 종료하고, 프레임 쓰기 스레드가 끝날 때까지 대기
        VideoSession[] sessions;
        lock (activeSessions)
            sessions = activeSessions.ToArray();
        bool isAllWritersStopped = true;
        foreach (VideoSession session in sessions)
        {
            session.frames.CompleteAdding();
            try
            {
                if (!session.process.HasExited)
                    session.process.Kill();
            }
            catch (Exception)
            {
            }
            isAllWritersStopped &= session.writer.Join(5000);
        }
        if (!isAllWritersStopped)
        {
            //아직 버퍼를 사용 중인 스레드가 있으므로 해제하지 않음
            Debug.LogWarning("RecorderFlusher 프레임 쓰기 스레드가 종료되지 않아, 프레임 버퍼를 해제하지 않습니다.");
            return;
        }
        AsyncGPUReadback.WaitAllRequests();
        while (pendingReadbacks.Count > 0)
            pendingReadbacks.Dequeue().pixels.Dispose();
        ClearBufferPool();
    }

    //프로젝트에 포함된 ffmpeg(StreamingAssets/ffmpeg/ffmpeg.zip) → 실행 폴더 → PATH 순으로 ffmpeg를 찾습니다. (스레드풀에서 실행)
    static string FindFfmpeg(bool isWindows, string streamingAssetsPath, string extractDirectory)
    {
        string fileName = isWindows ? "ffmpeg.exe" : "ffmpeg";
        string bundledZipPath = Path.Combine(streamingAssetsPath, "ffmpeg", "ffmpeg.zip");
        if (isWindows && File.Exists(bundledZipPath))
        {
            try
            {
                return ExtractBundledFfmpeg(bundledZipPath, extractDirectory, fileName);
            }
            catch (Exception e)
            {
                Debug.LogError($"포함된 ffmpeg 압축 해제 실패: {bundledZipPath}\n{e}");
            }
        }
        string localPath = Path.Combine(Environment.CurrentDirectory, fileName);
        return File.Exists(localPath) ? localPath : fileName;
    }
    //이전 버전에서 LocalLow에 풀어 둔 ffmpeg를 정리합니다.
    static void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"이전 ffmpeg 폴더를 지우지 못했습니다: {path}\n{e.Message}");
        }
    }
    //ffmpeg.exe는 100MB가 넘어 GitHub에 그대로 올릴 수 없으므로 zip으로 포함하고, 처음 실행할 때 한 번만 풀어서 사용합니다.
    static string ExtractBundledFfmpeg(string zipPath, string extractDirectory, string fileName)
    {
        string exePath = Path.Combine(extractDirectory, fileName);
        using (ZipArchive archive = new ZipArchive(File.OpenRead(zipPath), ZipArchiveMode.Read))
        {
            ZipArchiveEntry entry = archive.GetEntry(fileName);
            if (entry == null)
                throw new FileNotFoundException($"{zipPath} 안에 {fileName}이 없습니다.");
            //이미 같은 크기로 풀려 있으면 재사용
            if (File.Exists(exePath) && new FileInfo(exePath).Length == entry.Length)
                return exePath;

            Debug.Log($"포함된 ffmpeg 압축 해제: {exePath}");
            Directory.CreateDirectory(extractDirectory);
            string tempPath = exePath + ".tmp";
            using (Stream source = entry.Open())
            using (FileStream target = File.Create(tempPath))
                source.CopyTo(target);
            if (File.Exists(exePath))
                File.Delete(exePath);
            File.Move(tempPath, exePath);
        }
        return exePath;
    }
    //사용 가능한 인코더 중 가장 빠른 것을 고릅니다. (스레드풀에서 실행)
    static (string name, string args) SelectVideoEncoder(string ffmpegPath, string preferredEncoder)
    {
        if (!string.IsNullOrEmpty(preferredEncoder) && preferredEncoder != "auto")
        {
            foreach (var encoder in videoEncoders)
                if (encoder.name == preferredEncoder)
                    return encoder;
            return (preferredEncoder, $"-c:v {preferredEncoder}");
        }
        foreach (var encoder in videoEncoders)
        {
            //짧은 테스트 영상을 실제로 인코딩해 보고, 성공한 인코더를 사용 (GPU나 드라이버가 없으면 실패함)
            int exitCode = RunFfmpeg(ffmpegPath, $"-hide_banner -loglevel error -f lavfi -i color=c=black:s=640x360:r=24 -frames:v 5 {encoder.args} -f null -", 15000);
            if (exitCode == 0)
                return encoder;
            if (exitCode == int.MinValue)
                break;
        }
        return (null, null);
    }
    //ffmpeg를 실행하고 종료 코드를 반환합니다. ffmpeg를 찾을 수 없으면 int.MinValue를 반환합니다.
    static int RunFfmpeg(string ffmpegPath, string arguments, int timeoutMilliseconds)
    {
        try
        {
            using (Process process = Process.Start(new ProcessStartInfo(ffmpegPath, arguments) { UseShellExecute = false, CreateNoWindow = true }))
            {
                if (process.WaitForExit(timeoutMilliseconds))
                    return process.ExitCode;
                process.Kill();
                return -1;
            }
        }
        catch (Win32Exception)
        {
            return int.MinValue;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"ffmpeg 실행 실패: {e.Message}");
            return -1;
        }
    }

    //녹화기 하나의 영상 인코딩을 시작합니다. 이후 RequestFrame으로 넘긴 프레임이 순서대로 인코딩됩니다. (메인 스레드 전용)
    public VideoSession StartVideoSession(string outputPath, int width, int height)
    {
        //ffmpeg 준비와 인코더 탐색이 아직 끝나지 않았으면, 게임 시간이 흐르지 않도록 메인 스레드에서 대기
        (string name, string args) encoder;
        (ffmpegPath, encoder) = videoEncoderTask.Result;
        videoEncoderName = encoder.name;
        if (encoder.name == null)
        {
            Debug.LogError($"사용 가능한 ffmpeg 또는 영상 인코더를 찾을 수 없습니다. ({ffmpegPath})");
            return null;
        }

        //ffmpeg는 임시 폴더에 쓰고, 끝나면 Unity가 최종 경로로 옮김
        //(Windows 랜섬웨어 보호(제어된 폴더 액세스)가 바탕 화면 등에 대한 ffmpeg의 쓰기를 막는 경우가 있음)
        string encodingDirectory = Path.Combine(Application.temporaryCachePath, "Recording");
        Directory.CreateDirectory(encodingDirectory);
        string encodingPath = Path.Combine(encodingDirectory, $"{Guid.NewGuid():N}{Path.GetExtension(outputPath)}");
        //Unity 텍스처 데이터는 아래쪽 줄부터 저장되어 있으므로 vflip으로 뒤집음
        string arguments = "-hide_banner -loglevel warning -nostats -y " +
            $"-f rawvideo -pix_fmt rgba -video_size {width}x{height} -framerate {RecorderManager.Instance.OptionCaptureTargetFramerate} -i - " +
            $"-vf vflip {encoder.args} \"{encodingPath}\"";
        ProcessStartInfo startInfo = new ProcessStartInfo(ffmpegPath, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true
        };
        Process process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Win32Exception)
        {
            Debug.LogError($"ffmpeg을 찾을 수 없습니다. ({ffmpegPath})");
            return null;
        }
        VideoSession session = new VideoSession { OutputPath = outputPath, EncoderName = encoder.name, process = process, arguments = arguments, encodingPath = encodingPath };
        process.ErrorDataReceived += (sender, e) =>
        {
            if (string.IsNullOrEmpty(e.Data))
                return;
            Debug.LogWarning($"[ffmpeg] {e.Data}");
            session.ffmpegLog.Enqueue(e.Data);
            while (session.ffmpegLog.Count > 30)
                session.ffmpegLog.TryDequeue(out _);
        };
        process.BeginErrorReadLine();

        session.writer = new Thread(() => WriteFramesWorker(session)) { IsBackground = true, Name = $"RecorderFlusher Writer ({outputPath})" };
        lock (activeSessions)
            activeSessions.Add(session);
        session.writer.Start();
        Debug.Log($"영상 인코딩 시작: {outputPath} / 인코더: {encoder.name}");
        return session;
    }
    //녹화기의 마지막 프레임을 넘긴 뒤 호출합니다. 남은 프레임을 모두 쓰면 ffmpeg 입력을 닫습니다. (메인 스레드 전용)
    public void FinishVideoSession(VideoSession session)
    {
        //이 세션의 프레임이 아직 GPU에서 읽히는 중일 수 있으므로 먼저 완료시킴
        CompletePendingReadbacks();
        session.frames.CompleteAdding();
    }

    //source의 현재 내용을 GPU에서 비동기로 읽어 오도록 요청합니다. (메인 스레드 전용)
    public void RequestFrame(RenderTexture source, VideoSession session)
    {
        ProcessCompletedReadbacks();
        //빈 슬롯이 없으면, 가장 오래된 읽기 요청부터 완료시켜 ffmpeg 쪽으로 넘기고
        //그래도 없으면 ffmpeg가 프레임을 가져갈 때까지 대기
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
        AsyncGPUReadbackRequest request = AsyncGPUReadback.RequestIntoNativeArray(ref pixels, source, 0, TextureFormat.RGBA32);
        pendingReadbacks.Enqueue(new FrameData { request = request, pixels = pixels, session = session });
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
            Debug.LogError($"GPU 리드백 실패, 프레임이 누락됩니다: {frame.session.OutputPath}");
            ReleaseFrame(frame);
            return;
        }
        frame.session.frames.Add(frame);
    }
    void ReleaseFrame(FrameData frame)
    {
        pixelBufferPool.Add(frame.pixels);
        frameSlots.Release();
    }

    //임시 폴더에 인코딩된 영상을 최종 경로로 옮깁니다.
    static bool MoveEncodedVideo(VideoSession session)
    {
        try
        {
            if (File.Exists(session.OutputPath))
                File.Delete(session.OutputPath);
            File.Move(session.encodingPath, session.OutputPath);
            return true;
        }
        catch (Exception e)
        {
            Debug.LogError($"인코딩된 영상을 옮기지 못했습니다: {session.encodingPath} → {session.OutputPath}\n{e.Message}\n(영상은 임시 경로에 남아 있습니다)");
            return false;
        }
    }
    //세션의 프레임을 순서대로 ffmpeg 표준 입력에 쓰고, 인코딩이 끝날 때까지 기다립니다. (세션마다 전용 스레드)
    void WriteFramesWorker(VideoSession session)
    {
        byte[] writeBuffer = new byte[1 << 20];
        Stream input = session.process.StandardInput.BaseStream;
        bool isInputClosed = false;
        foreach (FrameData frame in session.frames.GetConsumingEnumerable())
        {
            try
            {
                if (!isInputClosed)
                {
                    for (int offset = 0; offset < frame.pixels.Length; offset += writeBuffer.Length)
                    {
                        int count = Math.Min(writeBuffer.Length, frame.pixels.Length - offset);
                        NativeArray<byte>.Copy(frame.pixels, offset, writeBuffer, 0, count);
                        input.Write(writeBuffer, 0, count);
                    }
                    Interlocked.Increment(ref session.writtenFrameCount);
                }
            }
            catch (IOException)
            {
                //ffmpeg가 먼저 종료됨. 남은 프레임은 버리고 버퍼만 반환 (원인은 ffmpeg 종료 후 로그로 남김)
                isInputClosed = true;
            }
            finally
            {
                ReleaseFrame(frame);
            }
        }
        try
        {
            input.Close();
        }
        catch (IOException)
        {
        }
        session.process.WaitForExit();
        session.ExitCode = session.process.ExitCode;
        session.process.Dispose();
        if (isInputClosed || session.ExitCode != 0)
            Debug.LogError($"ffmpeg가 비정상 종료되었습니다 (exit code: {session.ExitCode}, 전달한 프레임: {session.writtenFrameCount}): {session.OutputPath}\n" +
                $"인자: {session.arguments}\n" +
                $"ffmpeg 로그:\n{string.Join("\n", session.ffmpegLog)}");
        else
            session.IsSucceeded = MoveEncodedVideo(session);
        lock (activeSessions)
            activeSessions.Remove(session);
        session.isDone = true;
    }
}
