#pragma once
#include "Common.h"
#include <initializer_list>
#include <string>

//유니티 Mono 런타임(mono-2.0-bdwgc.dll / mono.dll)의 임베딩 API를 얇게 감싼 것입니다.
//유니티 C# API(Time, Camera, RenderTexture, AsyncGPUReadback ...)를 외부에서 호출할 때 씁니다.
//유니티 API 호출은 유니티 메인 스레드에서만 해야 합니다. (MainThreadHook의 틱에서 호출)
namespace Camcorder
{
    namespace Mono
    {
        typedef void* Object;
        typedef void* Class;
        typedef void* Method;

        //Mono 모듈과 함수를 찾음. IL2CPP 등 Mono가 없으면 false
        bool Load();
        //UnityEngine 어셈블리를 찾음. 아직 로드 전이면 false (나중에 다시 시도)
        bool Init();
        //Mono 루트 도메인과 UnityEngine 어셈블리가 올라왔는지
        bool IsCoreLoaded();
        //유니티 스레드가 아닌 스레드(초기화 스레드)에서 잠깐 Mono를 쓸 때 붙였다가 뗌
        void* AttachThread();
        void DetachThread(void* thread);

        Class FindClass(const char* nameSpace, const char* name);
        //이름으로 지정한 어셈블리에서 검색 (예: "UnityEngine.UIModule")
        Class FindClassIn(const char* imageName, const char* nameSpace, const char* name);
        //mscorlib에서 검색 (예: System.Runtime.InteropServices.Marshal)
        Class FindCorlibClass(const char* nameSpace, const char* name);
        //중첩 형식 (예: Canvas 안의 WillRenderCanvases)
        Class FindNestedClass(Class outer, const char* name);
        //이름과 인자 수로 메서드 검색 (private 포함)
        Method FindMethod(Class klass, const char* name, int paramCount);
        //이름과 인자 형식으로 메서드 검색. 형식은 "UnityEngine.Texture" 처럼 전체 이름, 끝이 '*'면 앞부분만 비교
        Method FindMethodSig(Class klass, const char* name, std::initializer_list<const char*> paramTypes);

        //예외가 나면 false. result는 반환값(값 형식이면 박싱된 객체)
        bool Invoke(Method method, void* self, void** args, Object* result = nullptr);

        //자주 쓰는 형태의 호출 도우미. 실패하면 false
        bool GetInt(Method method, void* self, int& value);
        bool GetBool(Method method, void* self, bool& value);
        bool GetFloat(Method method, void* self, float& value);
        bool GetObject(Method method, void* self, Object& value);
        bool SetInt(Method method, void* self, int value);
        bool SetBool(Method method, void* self, bool value);
        bool SetFloat(Method method, void* self, float value);
        bool SetObject(Method method, void* self, Object value);

        Object New(Class klass);
        void* Unbox(Object boxed);
        int ValueSize(Class klass);
        //System.Type 객체 (Object.FindObjectsOfType(Type) 인자용)
        Object TypeOf(Class klass);

        uintptr_t ArrayLength(Object array);
        Object ArrayGet(Object array, uintptr_t index);
        //값 형식 배열(Color32[] 등)의 첫 원소 주소
        void* ArrayData(Object array, int elementSize);

        //GC가 객체를 치우거나 옮기지 않도록 붙잡아 둠
        uint32_t Hold(Object obj);
        Object Held(uint32_t handle);
        void Release(uint32_t handle);

        std::string ToUtf8(Object monoString);
    }
}
