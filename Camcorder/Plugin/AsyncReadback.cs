using System;
using System.Reflection;
using UnityEngine;

namespace UnityCamcorder.Plugin
{
    // AsyncGPUReadback을 리플렉션으로 씁니다.
    // Unity 2018.1~2018.2는 UnityEngine.Experimental.Rendering, 2018.3부터는 UnityEngine.Rendering에 있어
    // 한 DLL로 모든 버전에서 돌도록 이름으로 찾습니다. 요청(AsyncGPUReadbackRequest)은 상자에 담긴 구조체(object)로 다룹니다.
    internal sealed class AsyncReadback
    {
        private const int TextureFormatRgba32 = 4;   // UnityEngine.TextureFormat.RGBA32

        private readonly MethodInfo _request;
        private readonly object[] _requestArgs;
        private readonly int _textureArg;
        private readonly MethodInfo _update;
        private readonly MethodInfo _done;
        private readonly MethodInfo _hasError;
        private readonly MethodInfo _getDataRaw;
        private readonly MethodInfo _waitForCompletion;
        private readonly MethodInfo _layerDataSize;
        private readonly MethodInfo _getDataBytes;      // GetDataRaw가 없을 때: GetData<byte>(int) → NativeArray<byte>
        private readonly MethodInfo _unsafePtr;         // NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr<byte>
        private readonly object[] _layerZero = { 0 };

        public bool RequestsRgba32 { get; private set; }

        private AsyncReadback(MethodInfo request, object[] args, MethodInfo update, MethodInfo done, MethodInfo hasError,
            MethodInfo getDataRaw, MethodInfo getDataBytes, MethodInfo unsafePtr, MethodInfo wait, MethodInfo layerDataSize, bool rgba32)
        {
            _request = request;
            _requestArgs = args;
            _textureArg = 0;
            _update = update;
            _done = done;
            _hasError = hasError;
            _getDataRaw = getDataRaw;
            _getDataBytes = getDataBytes;
            _unsafePtr = unsafePtr;
            _waitForCompletion = wait;
            _layerDataSize = layerDataSize;
            RequestsRgba32 = rgba32;
        }

        // 쓸 수 없으면 null (reason에 이유)
        public static AsyncReadback Create(out string reason)
        {
            reason = null;
            Type readback = FindType("UnityEngine.Rendering.AsyncGPUReadback") ?? FindType("UnityEngine.Experimental.Rendering.AsyncGPUReadback");
            if (readback == null)
            {
                reason = "AsyncGPUReadback이 없는 유니티 버전";
                return null;
            }
            Type requestType = readback.Namespace == "UnityEngine.Rendering"
                ? FindType("UnityEngine.Rendering.AsyncGPUReadbackRequest")
                : FindType("UnityEngine.Experimental.Rendering.AsyncGPUReadbackRequest");
            if (requestType == null)
            {
                reason = "AsyncGPUReadbackRequest를 찾지 못함";
                return null;
            }

            // Request(Texture, int mip, TextureFormat[, Action]) 를 우선: 원본 프로젝트처럼 RGBA32로 받음
            MethodInfo best = null;
            int bestScore = 0;
            foreach (MethodInfo m in readback.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name != "Request")
                    continue;
                ParameterInfo[] p = m.GetParameters();
                if (p.Length == 0 || p[0].ParameterType != typeof(Texture))
                    continue;
                int score = 0;
                if (p.Length >= 3 && p[1].ParameterType == typeof(int) && p[2].ParameterType == typeof(TextureFormat) &&
                    (p.Length == 3 || (p.Length == 4 && typeof(Delegate).IsAssignableFrom(p[3].ParameterType))))
                    score = 3;
                else if (p.Length == 1 || (p.Length >= 2 && p[1].ParameterType == typeof(int) &&
                         (p.Length == 2 || (p.Length == 3 && typeof(Delegate).IsAssignableFrom(p[2].ParameterType)))))
                    score = 1;
                if (score > bestScore || (score == bestScore && best != null && p.Length < best.GetParameters().Length))
                {
                    best = m;
                    bestScore = score;
                }
            }
            if (best == null)
            {
                reason = "AsyncGPUReadback.Request(Texture...)를 찾지 못함";
                return null;
            }
            ParameterInfo[] bp = best.GetParameters();
            var args = new object[bp.Length];
            if (bp.Length >= 2) args[1] = 0;   // mipIndex
            if (bestScore == 3) args[2] = Enum.ToObject(typeof(TextureFormat), TextureFormatRgba32);

            const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            MethodInfo update = requestType.GetMethod("Update", Instance, null, Type.EmptyTypes, null);
            MethodInfo done = Getter(requestType, "done") ?? requestType.GetMethod("IsDone", Instance, null, Type.EmptyTypes, null);
            MethodInfo hasError = Getter(requestType, "hasError") ?? requestType.GetMethod("HasError", Instance, null, Type.EmptyTypes, null);
            MethodInfo getDataRaw = requestType.GetMethod("GetDataRaw", Instance, null, new[] { typeof(int) }, null);
            MethodInfo wait = requestType.GetMethod("WaitForCompletion", Instance, null, Type.EmptyTypes, null);
            MethodInfo layerDataSize = Getter(requestType, "layerDataSize") ?? requestType.GetMethod("GetLayerDataSize", Instance, null, Type.EmptyTypes, null);
            if (getDataRaw != null && getDataRaw.ReturnType != typeof(IntPtr))
                getDataRaw = null;
            MethodInfo getDataBytes = null, unsafePtr = null;
            if (getDataRaw == null)
                FindGenericGetData(requestType, out getDataBytes, out unsafePtr);
            if (done == null || hasError == null || (getDataRaw == null && (getDataBytes == null || unsafePtr == null)))
            {
                reason = "AsyncGPUReadbackRequest 메서드를 찾지 못함 (done " + (done != null) + ", hasError " + (hasError != null) +
                         ", GetDataRaw " + (getDataRaw != null) + ", GetData<byte> " + (getDataBytes != null && unsafePtr != null) + ")";
                return null;
            }
            return new AsyncReadback(best, args, update, done, hasError, getDataRaw, getDataBytes, unsafePtr, wait, layerDataSize, bestScore == 3);
        }

        // 공개 API로 픽셀 주소 얻기: request.GetData<byte>(0) → NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(array)
        private static void FindGenericGetData(Type requestType, out MethodInfo getData, out MethodInfo unsafePtr)
        {
            getData = null;
            unsafePtr = null;
            foreach (MethodInfo m in requestType.GetMethods(BindingFlags.Instance | BindingFlags.Public))
            {
                if (m.Name == "GetData" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(int))
                {
                    getData = m.MakeGenericMethod(typeof(byte));
                    break;
                }
            }
            Type utility = FindType("Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility");
            if (getData == null || utility == null)
                return;
            foreach (MethodInfo m in utility.GetMethods(BindingFlags.Static | BindingFlags.Public))
            {
                if (m.Name == "GetUnsafeReadOnlyPtr" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1)
                {
                    try
                    {
                        MethodInfo closed = m.MakeGenericMethod(typeof(byte));
                        if (closed.GetParameters()[0].ParameterType == getData.ReturnType)
                        {
                            unsafePtr = closed;
                            return;
                        }
                    }
                    catch (ArgumentException) { /* 제약 조건이 다른 오버로드 */ }
                }
            }
        }

        private static MethodInfo Getter(Type type, string property)
        {
            PropertyInfo p = type.GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return p != null ? p.GetGetMethod(true) : null;
        }

        private static Type FindType(string fullName)
        {
            Type t = typeof(Texture).Assembly.GetType(fullName, false);
            if (t != null)
                return t;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    t = assembly.GetType(fullName, false);
                    if (t != null)
                        return t;
                }
                catch (Exception) { /* 동적 어셈블리 등 */ }
            }
            return null;
        }

        // 텍스처 0번 밉을 GPU에서 비동기로 읽어 달라고 요청. 실패하면 null
        public object Request(Texture source)
        {
            _requestArgs[_textureArg] = source;
            try { return _request.Invoke(null, _requestArgs); }
            finally { _requestArgs[_textureArg] = null; }
        }

        public bool IsDone(object request)
        {
            if (_update != null)
                _update.Invoke(request, null);
            return (bool)_done.Invoke(request, null);
        }

        public bool HasError(object request)
        {
            return (bool)_hasError.Invoke(request, null);
        }

        // 읽기가 끝날 때까지 기다림 (녹화를 끝낼 때, 요청이 너무 밀렸을 때)
        public void WaitDone(object request)
        {
            if (_waitForCompletion != null)
            {
                try { _waitForCompletion.Invoke(request, null); return; }
                catch (Exception) { /* 아래 폴링으로 */ }
            }
            int start = Environment.TickCount;
            while (!IsDone(request) && unchecked(Environment.TickCount - start) < 2000)
                System.Threading.Thread.Sleep(1);
        }

        // 끝난 요청의 픽셀 주소 (다음 프레임까지만 유효). 크기를 알 수 없으면 dataSize = -1
        public unsafe IntPtr Data(object request, out int dataSize)
        {
            dataSize = -1;
            if (_layerDataSize != null)
                dataSize = (int)_layerDataSize.Invoke(request, null);
            if (_getDataRaw != null)
                return (IntPtr)_getDataRaw.Invoke(request, _layerZero);
            object array = _getDataBytes.Invoke(request, _layerZero);
            object pointer = _unsafePtr.Invoke(null, new[] { array });
            return pointer is Pointer ? (IntPtr)Pointer.Unbox(pointer) : IntPtr.Zero;
        }
    }
}
