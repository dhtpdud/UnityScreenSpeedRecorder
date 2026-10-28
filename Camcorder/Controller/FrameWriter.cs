using System.Collections.Concurrent;
using System.Diagnostics;

namespace UnityCamcorder;

// 영상(카메라) 하나의 프레임을 ffmpeg 표준 입력에 쓰는 전용 스레드입니다. 원본 프로젝트의 RecorderFlusher처럼
// 링 버퍼에서 꺼낸 프레임을 줄에 세워 두고 따로 쓰므로, 공유 메모리 슬롯이 바로 비어 앱이 덜 기다리고
// 카메라 여러 대의 인코딩이 동시에 진행됩니다. 줄이 꽉 차면 넣는 쪽이 기다려 프레임을 버리지 않습니다.
public sealed class FrameWriter : IDisposable
{
    private readonly BlockingCollection<(byte[] Data, int Length)> _queue;
    private readonly ConcurrentBag<byte[]> _pool = new();
    private readonly int _frameBytes;
    private readonly Thread _thread;
    private long _busyTicks;
    private volatile bool _failed;

    public FfmpegEncoder Encoder { get; }
    public int Capacity { get; }
    public bool Failed => _failed;
    public long BusyTicks => Interlocked.Read(ref _busyTicks);   // ffmpeg에 쓰느라 막혀 있던 시간 (Stopwatch 틱, 누적)

    public FrameWriter(FfmpegEncoder encoder, int frameBytes, int capacity, string label)
    {
        Encoder = encoder;
        _frameBytes = frameBytes;
        Capacity = capacity;
        _queue = new BlockingCollection<(byte[], int)>(capacity);
        _thread = new Thread(Run) { IsBackground = true, Name = $"ffmpeg 쓰기 ({label})" };
        _thread.Start();
    }

    // 프레임 한 장을 담을 버퍼 (다 쓴 버퍼를 재사용)
    public byte[] RentBuffer() => _pool.TryTake(out var buffer) ? buffer : new byte[_frameBytes];

    // 줄이 꽉 찼으면 자리가 날 때까지 최대 timeoutMs 기다림. false면 아직 못 넣은 것 (버퍼는 호출한 쪽이 그대로 가짐)
    public bool TryEnqueue(byte[] data, int length, int timeoutMs) => _queue.TryAdd((data, length), timeoutMs);

    public void ReturnBuffer(byte[] data) => _pool.Add(data);

    private void Run()
    {
        foreach (var (data, length) in _queue.GetConsumingEnumerable())
        {
            // ffmpeg가 먼저 죽었으면 남은 프레임은 버리기만 함 (넣는 쪽이 막히지 않도록)
            if (!_failed)
            {
                long start = Stopwatch.GetTimestamp();
                try
                {
                    if (!Encoder.WriteFrame(data, length))
                        _failed = true;
                }
                catch (Exception)
                {
                    _failed = true;   // 이 스레드의 예외가 캠코더 전체를 끝내지 않도록
                }
                Interlocked.Add(ref _busyTicks, Stopwatch.GetTimestamp() - start);
            }
            _pool.Add(data);
        }
    }

    // 줄에 남은 프레임을 다 쓴 뒤 ffmpeg 입력을 닫아 영상을 마무리
    public bool Finish(int timeoutMs = 60000)
    {
        _queue.CompleteAdding();
        if (!_thread.Join(timeoutMs))
        {
            // ffmpeg가 입력을 받지 않고 멈춤: 강제로 끝내 쓰기 스레드를 풀어 줌
            Encoder.Kill();
            _thread.Join(5000);
            return false;
        }
        return Encoder.Finish(timeoutMs);
    }

    public void Dispose()
    {
        if (!_queue.IsAddingCompleted)
            _queue.CompleteAdding();
        if (!_thread.Join(1000))
            Encoder.Kill();
        Encoder.Dispose();
    }
}
