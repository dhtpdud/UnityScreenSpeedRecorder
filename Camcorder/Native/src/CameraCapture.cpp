#include "Common.h"
#include "Mono.h"
#include "FrameBuffer.h"
#include <deque>
#include <vector>

//카메라 촬영: 원본 프로젝트(ScreenRecorder + RecorderFlusher)와 같은 방식입니다.
//카메라의 targetTexture를 캠코더용 RenderTexture로 돌려 화면 대신 그곳에 그리게 하고,
//매 프레임 AsyncGPUReadback으로 GPU에서 비동기로 읽어 와 공유 메모리 링 버퍼로 캠코더에 넘깁니다.
//화면(백버퍼)을 다시 읽지 않으므로 GPU가 같은 장면을 두 번 다루지 않고, GPU 동기 대기도 없습니다.
//
//촬영 카메라를 고르지 않으면, 화면에 그리던 카메라를 모두 하나의 RenderTexture로 모아 한 영상으로 찍습니다.
//고르면 카메라마다 스트림 하나(= 영상 하나)가 됩니다. 앱이 이미 자기 RenderTexture로 그리던 카메라는
//그 텍스처를 그대로 읽어 앱 동작을 바꾸지 않습니다.
//모든 함수는 유니티 메인 스레드(MainThreadHook의 틱)에서만 호출됩니다.
namespace Camcorder
{
    bool IsMonoReady();

    volatile LONG g_cameraCaptureSession = -1;

    //--- 유니티 API ----------------------------------------------------------------
    static bool s_apiResolved = false;
    static bool s_apiOk = false;

    static Mono::Class c_camera, c_renderTexture, c_object, c_request;
    static Mono::Method m_allCameras, m_allCamerasCount, m_mainCamera, m_findObjectsOfType;
    static Mono::Method m_getTargetTexture, m_setTargetTexture, m_getTargetDisplay, m_getDepth;
    static Mono::Method m_getEnabled, m_setEnabled, m_getName, m_getInstanceId, m_destroy;
    static Mono::Method m_rtCtor, m_rtCreate, m_rtRelease, m_rtWidth, m_rtHeight;
    static Mono::Method m_screenWidth, m_screenHeight, m_supportsReadback;
    static Mono::Method m_request, m_reqUpdate, m_reqDone, m_reqError, m_reqData, m_reqDataSize, m_reqWait;
    static bool s_requestHasFormat = false;
    static int s_requestArgs = 0;
    static int s_requestSize = 0;

    static const int kTextureFormatRGBA32 = 4;      // UnityEngine.TextureFormat.RGBA32
    static const int kDxgiRGBA8 = 28;               // DXGI_FORMAT_R8G8B8A8_UNORM (캠코더가 ffmpeg rgba로 넘김)
    static const size_t kMaxInFlight = 3;           // 스트림마다 동시에 진행할 읽기 요청 수

    static bool ResolveApi()
    {
        if (s_apiResolved)
            return s_apiOk;
        s_apiResolved = true;

        c_camera = Mono::FindClass("UnityEngine", "Camera");
        c_renderTexture = Mono::FindClass("UnityEngine", "RenderTexture");
        c_object = Mono::FindClass("UnityEngine", "Object");
        Mono::Class behaviour = Mono::FindClass("UnityEngine", "Behaviour");
        Mono::Class screen = Mono::FindClass("UnityEngine", "Screen");
        Mono::Class systemInfo = Mono::FindClass("UnityEngine", "SystemInfo");
        Mono::Class readback = Mono::FindClass("UnityEngine.Rendering", "AsyncGPUReadback");
        c_request = Mono::FindClass("UnityEngine.Rendering", "AsyncGPUReadbackRequest");
        if (!readback)
        {
            //Unity 2018.1~2018.2
            readback = Mono::FindClass("UnityEngine.Experimental.Rendering", "AsyncGPUReadback");
            c_request = Mono::FindClass("UnityEngine.Experimental.Rendering", "AsyncGPUReadbackRequest");
        }

        m_allCameras = Mono::FindMethod(c_camera, "get_allCameras", 0);
        m_allCamerasCount = Mono::FindMethod(c_camera, "get_allCamerasCount", 0);
        m_mainCamera = Mono::FindMethod(c_camera, "get_main", 0);
        m_getTargetTexture = Mono::FindMethod(c_camera, "get_targetTexture", 0);
        m_setTargetTexture = Mono::FindMethod(c_camera, "set_targetTexture", 1);
        m_getTargetDisplay = Mono::FindMethod(c_camera, "get_targetDisplay", 0);
        m_getDepth = Mono::FindMethod(c_camera, "get_depth", 0);
        m_getEnabled = Mono::FindMethod(behaviour, "get_enabled", 0);
        m_setEnabled = Mono::FindMethod(behaviour, "set_enabled", 1);
        m_getName = Mono::FindMethod(c_object, "get_name", 0);
        m_getInstanceId = Mono::FindMethod(c_object, "GetInstanceID", 0);
        m_destroy = Mono::FindMethodSig(c_object, "Destroy", { "UnityEngine.Object" });
        //꺼진 카메라도 목록에 넣기 위해 FindObjectsOfType(Type) 사용 (없으면 Camera.allCameras = 켜진 것만)
        m_findObjectsOfType = Mono::FindMethodSig(c_object, "FindObjectsOfType", { "System.Type" });

        m_rtCtor = Mono::FindMethodSig(c_renderTexture, ".ctor", { "System.Int32", "System.Int32", "System.Int32" });
        m_rtCreate = Mono::FindMethod(c_renderTexture, "Create", 0);
        m_rtRelease = Mono::FindMethod(c_renderTexture, "Release", 0);
        m_rtWidth = Mono::FindMethod(c_renderTexture, "get_width", 0);
        m_rtHeight = Mono::FindMethod(c_renderTexture, "get_height", 0);
        m_screenWidth = Mono::FindMethod(screen, "get_width", 0);
        m_screenHeight = Mono::FindMethod(screen, "get_height", 0);
        m_supportsReadback = Mono::FindMethod(systemInfo, "get_supportsAsyncGPUReadback", 0);

        //AsyncGPUReadback.Request: 원본 프로젝트처럼 RGBA32로 받는 오버로드를 우선 사용
        if ((m_request = Mono::FindMethodSig(readback, "Request", { "UnityEngine.Texture", "System.Int32", "UnityEngine.TextureFormat", "System.Action*" })))
        { s_requestHasFormat = true; s_requestArgs = 4; }
        else if ((m_request = Mono::FindMethodSig(readback, "Request", { "UnityEngine.Texture", "System.Int32", "UnityEngine.TextureFormat" })))
        { s_requestHasFormat = true; s_requestArgs = 3; }
        else if ((m_request = Mono::FindMethodSig(readback, "Request", { "UnityEngine.Texture", "System.Int32", "System.Action*" })))
        { s_requestHasFormat = false; s_requestArgs = 3; }
        else if ((m_request = Mono::FindMethodSig(readback, "Request", { "UnityEngine.Texture", "System.Int32" })))
        { s_requestHasFormat = false; s_requestArgs = 2; }
        else if ((m_request = Mono::FindMethodSig(readback, "Request", { "UnityEngine.Texture" })))
        { s_requestHasFormat = false; s_requestArgs = 1; }

        m_reqUpdate = Mono::FindMethod(c_request, "Update", 0);
        m_reqWait = Mono::FindMethod(c_request, "WaitForCompletion", 0);
        m_reqDone = Mono::FindMethod(c_request, "IsDone", 0);
        if (!m_reqDone) m_reqDone = Mono::FindMethod(c_request, "get_done", 0);
        m_reqError = Mono::FindMethod(c_request, "HasError", 0);
        if (!m_reqError) m_reqError = Mono::FindMethod(c_request, "get_hasError", 0);
        m_reqData = Mono::FindMethod(c_request, "GetDataRaw", 1);
        m_reqDataSize = Mono::FindMethod(c_request, "GetLayerDataSize", 0);
        if (!m_reqDataSize) m_reqDataSize = Mono::FindMethod(c_request, "get_layerDataSize", 0);
        s_requestSize = Mono::ValueSize(c_request);

        s_apiOk = c_camera && m_getTargetTexture && m_setTargetTexture && m_getInstanceId && m_getName &&
                  (m_findObjectsOfType || m_allCameras) && m_rtCtor && m_rtCreate &&
                  m_request && m_reqUpdate && m_reqDone && m_reqError && m_reqData &&
                  s_requestSize > 0 && s_requestSize <= 32;
        if (!s_apiOk)
            Log("카메라 촬영에 필요한 유니티 API를 찾지 못함 (Request=%p, GetDataRaw=%p, ctor=%p) — 화면 캡처로 진행", m_request, m_reqData, m_rtCtor);
        else
            Log("카메라 촬영 API 준비 완료 (Request 인자 %d개, 형식 지정 %s)", s_requestArgs, s_requestHasFormat ? "예" : "아니오");
        return s_apiOk;
    }

    //--- 작은 도우미 ----------------------------------------------------------------
    static int InstanceId(Mono::Object obj)
    {
        int id = 0;
        Mono::GetInt(m_getInstanceId, obj, id);
        return id;
    }

    static Mono::Object TargetTexture(Mono::Object camera)
    {
        Mono::Object rt = nullptr;
        Mono::GetObject(m_getTargetTexture, camera, rt);
        return rt;
    }

    static bool IsEnabled(Mono::Object behaviour)
    {
        bool enabled = true;
        Mono::GetBool(m_getEnabled, behaviour, enabled);
        return enabled;
    }

    //카메라 목록 (꺼진 카메라 포함). 배열 객체를 돌려줌
    static Mono::Object AllCameraArray()
    {
        Mono::Object array = nullptr;
        if (m_findObjectsOfType)
        {
            Mono::Object type = Mono::TypeOf(c_camera);
            void* args[1] = { type };
            if (type && Mono::Invoke(m_findObjectsOfType, nullptr, args, &array) && array)
                return array;
        }
        Mono::GetObject(m_allCameras, nullptr, array);
        return array;
    }

    //카메라가 자기 RenderTexture 없이 그릴 때 쓸 영상 크기: 캠코더가 정한 크기 → 백버퍼 → Screen
    static void RecordSize(int& width, int& height)
    {
        width = g_shared->recordWidth;
        height = g_shared->recordHeight;
        if (width > 0 && height > 0)
            return;
        width = g_shared->backbufferWidth;
        height = g_shared->backbufferHeight;
        if (width > 0 && height > 0)
            return;
        Mono::GetInt(m_screenWidth, nullptr, width);
        Mono::GetInt(m_screenHeight, nullptr, height);
    }

    static void TextureSize(Mono::Object rt, int& width, int& height)
    {
        width = height = 0;
        Mono::GetInt(m_rtWidth, rt, width);
        Mono::GetInt(m_rtHeight, rt, height);
    }

    static Mono::Object CreateRenderTexture(int width, int height)
    {
        Mono::Object rt = Mono::New(c_renderTexture);
        if (!rt)
            return nullptr;
        int depth = 24;   // 깊이 + 스텐실
        void* args[3] = { &width, &height, &depth };
        if (!Mono::Invoke(m_rtCtor, rt, args))
            return nullptr;
        bool created = false;
        Mono::GetBool(m_rtCreate, rt, created);
        return rt;
    }

    static void DestroyRenderTexture(Mono::Object rt)
    {
        if (!rt)
            return;
        Mono::Invoke(m_rtRelease, rt, nullptr);
        Mono::SetObject(m_destroy, nullptr, rt);
    }

    //--- 카메라 목록 (캠코더/창 프로그램이 촬영 카메라를 고를 때) ---------------------
    void ReportScreenSizeIfNeeded()
    {
        //Present가 한 번도 안 불린 앱(OpenGL/Vulkan 등)은 Screen 크기를 대신 알려 줌
        if (!g_shared || g_shared->presentCount != 0 || g_shared->backbufferWidth > 0 || !IsMonoReady() || !ResolveApi())
            return;
        int w = 0, h = 0;
        Mono::GetInt(m_screenWidth, nullptr, w);
        Mono::GetInt(m_screenHeight, nullptr, h);
        if (w > 0 && h > 0)
        {
            g_shared->backbufferWidth = w;
            g_shared->backbufferHeight = h;
            g_shared->backbufferFormat = kDxgiRGBA8;
        }
    }

    void HandleCameraListRequest()
    {
        if (!g_shared)
            return;
        int32_t request = g_shared->cameraListRequest;
        if (request == g_shared->cameraListDone)
            return;
        if (!IsMonoReady())
        {
            //IL2CPP 등 카메라를 다룰 수 없는 앱이면 바로 "불가"로 답함 (Mono는 준비될 때까지 기다림)
            if (HasStatus(ST_RUNTIME_IL2CPP))
            {
                g_shared->cameraCount = -1;
                MemoryBarrier();
                g_shared->cameraListDone = request;
            }
            return;
        }
        if (!ResolveApi())
        {
            g_shared->cameraCount = -1;
            MemoryBarrier();
            g_shared->cameraListDone = request;
            return;
        }

        int recordW = 0, recordH = 0;
        RecordSize(recordW, recordH);
        Mono::Object mainCamera = nullptr;
        Mono::GetObject(m_mainCamera, nullptr, mainCamera);
        int mainId = mainCamera ? InstanceId(mainCamera) : 0;

        Mono::Object array = AllCameraArray();
        uintptr_t count = Mono::ArrayLength(array);
        int written = 0;
        for (uintptr_t i = 0; i < count && written < CAMCORDER_MAX_CAMERAS; i++)
        {
            Mono::Object camera = Mono::ArrayGet(array, i);
            if (!camera)
                continue;
            CamcorderCameraInfo& info = g_shared->cameras[written];
            memset(&info, 0, sizeof(info));
            info.instanceId = InstanceId(camera);
            std::string name;
            Mono::Object nameObj = nullptr;
            if (Mono::GetObject(m_getName, camera, nameObj))
                name = Mono::ToUtf8(nameObj);
            strncpy_s(info.name, sizeof(info.name), name.c_str(), _TRUNCATE);
            int flags = 0;
            if (IsEnabled(camera))
                flags |= CAMF_ENABLED;
            if (info.instanceId == mainId && mainId != 0)
                flags |= CAMF_MAIN;
            Mono::Object target = TargetTexture(camera);
            if (target)
            {
                flags |= CAMF_OWN_TARGET;
                TextureSize(target, info.width, info.height);
            }
            else
            {
                info.width = recordW;
                info.height = recordH;
            }
            info.flags = flags;
            Mono::GetFloat(m_getDepth, camera, info.depth);
            Mono::GetInt(m_getTargetDisplay, camera, info.targetDisplay);
            written++;
        }
        g_shared->cameraCount = written;
        MemoryBarrier();
        g_shared->cameraListDone = request;
        Log("카메라 목록 보고: %d개", written);
    }

    //--- 촬영 스트림 ------------------------------------------------------------------
    struct PendingRead
    {
        alignas(8) uint8_t request[32];   // AsyncGPUReadbackRequest 값 복사본
    };

    struct Stream
    {
        int index = 0;
        std::vector<uint32_t> cameras;       // targetTexture를 우리 RT로 바꾼 카메라
        std::vector<uint32_t> enabledByUs;   // 꺼져 있어서 촬영 동안 켠 카메라
        uint32_t ownRt = 0;                  // 캠코더가 만든 RenderTexture (없으면 0)
        uint32_t source = 0;                 // 읽어 올 텍스처 (ownRt 또는 카메라가 원래 쓰던 RT)
        int width = 0, height = 0;
        std::deque<PendingRead> pending;
        bool errorLogged = false;
    };

    static std::vector<Stream> s_streams;
    static FrameRing s_ring;
    static int s_session = -1;
    static bool s_composite = false;
    static int s_lastAllCamerasCount = -1;
    static int s_rescanTick = 0;

    static void Hold(std::vector<uint32_t>& list, Mono::Object obj)
    {
        uint32_t handle = Mono::Hold(obj);
        if (handle)
            list.push_back(handle);
    }

    static bool IsTracked(const Stream& stream, int instanceId)
    {
        for (uint32_t h : stream.cameras)
        {
            Mono::Object cam = Mono::Held(h);
            if (cam && InstanceId(cam) == instanceId)
                return true;
        }
        return false;
    }

    //화면 전체 촬영: 화면(Display 1)에 그리던 켜진 카메라를 모두 우리 RT로 돌림. 새로 생긴 카메라도 따라감
    static int RetargetDisplayCameras(Stream& stream)
    {
        Mono::Object rt = Mono::Held(stream.ownRt);
        Mono::Object array = nullptr;
        Mono::GetObject(m_allCameras, nullptr, array);   // 켜진 카메라만
        if (!array)
            array = AllCameraArray();
        uintptr_t count = Mono::ArrayLength(array);
        int added = 0;
        for (uintptr_t i = 0; i < count; i++)
        {
            Mono::Object camera = Mono::ArrayGet(array, i);
            if (!camera || !IsEnabled(camera) || TargetTexture(camera))
                continue;
            int display = 0;
            Mono::GetInt(m_getTargetDisplay, camera, display);
            if (display != 0)
                continue;
            int id = InstanceId(camera);
            if (IsTracked(stream, id))
                continue;
            if (Mono::SetObject(m_setTargetTexture, camera, rt))
            {
                Hold(stream.cameras, camera);
                added++;
            }
        }
        return added;
    }

    static Mono::Object FindCameraById(int instanceId)
    {
        Mono::Object array = AllCameraArray();
        uintptr_t count = Mono::ArrayLength(array);
        for (uintptr_t i = 0; i < count; i++)
        {
            Mono::Object camera = Mono::ArrayGet(array, i);
            if (camera && InstanceId(camera) == instanceId)
                return camera;
        }
        return nullptr;
    }

    static bool IsDone(PendingRead& read)
    {
        Mono::Invoke(m_reqUpdate, read.request, nullptr);
        bool done = false;
        Mono::GetBool(m_reqDone, read.request, done);
        return done;
    }

    static void WaitDone(PendingRead& read)
    {
        if (m_reqWait && Mono::Invoke(m_reqWait, read.request, nullptr))
            return;
        ULONGLONG start = GetTickCount64();
        while (!IsDone(read) && GetTickCount64() - start < 2000)
            Sleep(1);
    }

    //읽기가 끝난 프레임을 링 버퍼로 넘김. 유니티 텍스처는 아래쪽 줄부터 담겨 있으므로 위아래를 뒤집어 복사
    static void Deliver(Stream& stream, PendingRead& read)
    {
        bool error = true;
        Mono::GetBool(m_reqError, read.request, error);
        if (error)
        {
            if (!stream.errorLogged)
                Log("스트림 %d: GPU 읽기 실패 (이 프레임은 건너뜀)", stream.index);
            stream.errorLogged = true;
            return;
        }
        int layer = 0;
        void* args[1] = { &layer };
        Mono::Object boxedPtr = nullptr;
        if (!Mono::Invoke(m_reqData, read.request, args, &boxedPtr) || !boxedPtr)
            return;
        const uint8_t* data = *(const uint8_t**)Mono::Unbox(boxedPtr);
        int dataSize = 0;
        Mono::GetInt(m_reqDataSize, read.request, dataSize);
        int64_t rowBytes = (int64_t)stream.width * 4;
        int64_t frameBytes = rowBytes * stream.height;
        if (!data || (m_reqDataSize && dataSize < frameBytes))
            return;

        int slot; uint8_t* dest; int64_t capacity;
        //캠코더가 인코딩을 못 따라오면 여기서 기다림 → 게임도 기다리므로 프레임을 버리지 않음
        if (!s_ring.BeginWrite(slot, dest, capacity, true) || frameBytes > capacity)
            return;
        for (int y = 0; y < stream.height; y++)
            memcpy(dest + (int64_t)y * rowBytes, data + (int64_t)(stream.height - 1 - y) * rowBytes, (size_t)rowBytes);
        s_ring.EndWrite(slot, stream.width, stream.height, kDxgiRGBA8, 4, frameBytes, stream.index);
    }

    static void IssueRequest(Stream& stream)
    {
        Mono::Object source = Mono::Held(stream.source);
        if (!source)
            return;
        int mip = 0;
        int format = kTextureFormatRGBA32;
        void* args[4] = { source, &mip, nullptr, nullptr };
        if (s_requestHasFormat)
            args[2] = &format;
        Mono::Object boxed = nullptr;
        if (!Mono::Invoke(m_request, nullptr, args, &boxed) || !boxed)
            return;
        PendingRead read;
        memset(read.request, 0, sizeof(read.request));
        memcpy(read.request, Mono::Unbox(boxed), (size_t)s_requestSize);
        stream.pending.push_back(read);
    }

    static void RestoreStream(Stream& stream)
    {
        for (PendingRead& read : stream.pending)
            WaitDone(read);
        stream.pending.clear();

        Mono::Object ownRt = Mono::Held(stream.ownRt);
        for (uint32_t h : stream.cameras)
        {
            Mono::Object camera = Mono::Held(h);
            //게임이 그새 다른 텍스처로 바꿨으면 건드리지 않음
            if (camera && ownRt && TargetTexture(camera) == ownRt)
                Mono::SetObject(m_setTargetTexture, camera, nullptr);
            Mono::Release(h);
        }
        for (uint32_t h : stream.enabledByUs)
        {
            Mono::Object camera = Mono::Held(h);
            if (camera)
                Mono::SetBool(m_setEnabled, camera, false);
            Mono::Release(h);
        }
        if (ownRt)
            DestroyRenderTexture(ownRt);
        if (stream.source && stream.source != stream.ownRt)
            Mono::Release(stream.source);
        Mono::Release(stream.ownRt);
        stream.cameras.clear();
        stream.enabledByUs.clear();
        stream.ownRt = stream.source = 0;
    }

    void StopCameraCapture()
    {
        if (s_session < 0 && s_streams.empty())
            return;
        for (Stream& stream : s_streams)
            RestoreStream(stream);
        s_streams.clear();
        s_ring.Close();
        s_session = -1;
        InterlockedExchange(&g_cameraCaptureSession, -1);
        RemoveStatus(ST_CAMERA_CAPTURE);
        Log("카메라 촬영 종료, 카메라 설정 복원");
    }

    static bool MakeOwnTarget(Stream& stream, int width, int height)
    {
        Mono::Object rt = CreateRenderTexture(width, height);
        if (!rt)
            return false;
        stream.ownRt = Mono::Hold(rt);
        stream.source = stream.ownRt;
        stream.width = width;
        stream.height = height;
        return stream.ownRt != 0;
    }

    //녹화 세션 시작. 성공하면 이 세션은 카메라 촬영, 실패하면 Present 훅의 화면 캡처로 넘어감
    bool StartCameraCapture(int session)
    {
        StopCameraCapture();
        if (!g_shared || !IsMonoReady() || !ResolveApi())
            return false;
        bool supported = true;
        if (m_supportsReadback && Mono::GetBool(m_supportsReadback, nullptr, supported) && !supported)
        {
            Log("이 그래픽 환경은 AsyncGPUReadback을 지원하지 않아 화면 캡처로 진행");
            return false;
        }

        int recordW = 0, recordH = 0;
        RecordSize(recordW, recordH);
        int streamCount = g_shared->streamCount;
        if (streamCount < 0) streamCount = 0;
        if (streamCount > CAMCORDER_MAX_STREAMS) streamCount = CAMCORDER_MAX_STREAMS;

        bool ok = true;
        if (streamCount == 0)
        {
            //화면 전체: 화면에 그리던 카메라들을 한 RT로 모음
            s_composite = true;
            s_streams.emplace_back();
            Stream& stream = s_streams.back();
            stream.index = 0;
            ok = recordW > 0 && recordH > 0 && MakeOwnTarget(stream, recordW, recordH) && RetargetDisplayCameras(stream) > 0;
            if (!ok)
                Log("화면에 그리는 카메라를 찾지 못해 화면 캡처로 진행");
            Mono::GetInt(m_allCamerasCount, nullptr, s_lastAllCamerasCount);
        }
        else
        {
            s_composite = false;
            for (int i = 0; i < streamCount && ok; i++)
            {
                s_streams.emplace_back();
                Stream& stream = s_streams.back();
                stream.index = i;
                int id = g_shared->streamCameraIds[i];
                Mono::Object camera = FindCameraById(id);
                if (!camera)
                {
                    SetLastErrorText("선택한 카메라를 찾을 수 없음 (instanceId %d). 장면이 바뀌었다면 카메라 목록을 다시 불러오세요.", id);
                    ok = false;
                    break;
                }
                Mono::Object target = TargetTexture(camera);
                if (target)
                {
                    //앱이 이미 자기 RT로 그리는 카메라: 그 RT를 그대로 읽음
                    stream.source = Mono::Hold(target);
                    TextureSize(target, stream.width, stream.height);
                    ok = stream.source != 0 && stream.width > 0 && stream.height > 0;
                }
                else
                {
                    ok = recordW > 0 && recordH > 0 && MakeOwnTarget(stream, recordW, recordH) &&
                         Mono::SetObject(m_setTargetTexture, camera, Mono::Held(stream.ownRt));
                    if (ok)
                        Hold(stream.cameras, camera);
                }
                if (ok && !IsEnabled(camera))
                {
                    //원본 프로젝트의 녹화 카메라처럼 평소 꺼져 있던 카메라는 촬영 동안만 켬
                    if (Mono::SetBool(m_setEnabled, camera, true))
                        Hold(stream.enabledByUs, camera);
                }
                if (!ok)
                    SetLastErrorText("카메라 %d 촬영 준비 실패", id);
            }
        }

        int maxW = 0, maxH = 0;
        for (Stream& stream : s_streams)
        {
            if (stream.width * stream.height > maxW * maxH) { maxW = stream.width; maxH = stream.height; }
        }
        if (ok)
            ok = s_ring.Open(session, maxW, maxH, 4);
        if (!ok)
        {
            for (Stream& stream : s_streams)
                RestoreStream(stream);
            s_streams.clear();
            s_ring.Close();
            return false;
        }

        s_session = session;
        InterlockedExchange(&g_cameraCaptureSession, session);
        AddStatus(ST_CAMERA_CAPTURE);
        if (s_composite)
            Log("카메라 촬영 시작: 화면 전체 (카메라 %d대 → %dx%d RenderTexture)", (int)s_streams[0].cameras.size(), maxW, maxH);
        else
            Log("카메라 촬영 시작: 카메라 %d대 (각각 영상 하나)", (int)s_streams.size());
        return true;
    }

    //매 프레임 메인 스레드에서: 끝난 읽기를 넘기고, 이번 프레임 읽기를 요청
    void TickCameraCapture()
    {
        if (s_session < 0)
            return;

        //화면 전체 촬영 중 새 카메라가 생기면(장면 전환 등) 따라감
        if (s_composite && !s_streams.empty())
        {
            int count = -1;
            Mono::GetInt(m_allCamerasCount, nullptr, count);
            if (count != s_lastAllCamerasCount || (++s_rescanTick % 60) == 0)
            {
                s_lastAllCamerasCount = count;
                if (RetargetDisplayCameras(s_streams[0]) > 0)
                    Log("새 카메라를 촬영에 추가");
            }
        }

        for (Stream& stream : s_streams)
        {
            while (!stream.pending.empty() && IsDone(stream.pending.front()))
            {
                Deliver(stream, stream.pending.front());
                stream.pending.pop_front();
            }
            //읽기가 너무 밀리면 가장 오래된 것부터 끝내고 넘김 (프레임을 버리지 않음)
            while (stream.pending.size() >= kMaxInFlight)
            {
                WaitDone(stream.pending.front());
                Deliver(stream, stream.pending.front());
                stream.pending.pop_front();
            }
            if (!IsRecordRequested())
                return;
            //지난 프레임에 카메라가 그린 결과를 읽기 요청 (원본 프로젝트와 같은 시점)
            IssueRequest(stream);
        }
    }
}
