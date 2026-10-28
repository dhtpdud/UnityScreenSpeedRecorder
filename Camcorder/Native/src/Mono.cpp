#include "Mono.h"

namespace Camcorder
{
    namespace Mono
    {
        typedef void* MonoDomain;
        typedef void* MonoImage;
        typedef void* MonoAssembly;
        typedef void* MonoType;
        typedef void* MonoSignature;

        typedef MonoDomain (*get_root_domain_t)();
        typedef MonoDomain (*domain_get_t)();
        typedef void* (*thread_attach_t)(MonoDomain);
        typedef MonoImage (*image_loaded_t)(const char*);
        typedef MonoAssembly (*domain_assembly_open_t)(MonoDomain, const char*);
        typedef MonoImage (*assembly_get_image_t)(MonoAssembly);
        typedef Class (*class_from_name_t)(MonoImage, const char*, const char*);
        typedef Method (*class_get_method_from_name_t)(Class, const char*, int);
        typedef Method (*class_get_methods_t)(Class, void**);
        typedef const char* (*method_get_name_t)(Method);
        typedef MonoSignature (*method_signature_t)(Method);
        typedef uint32_t (*signature_get_param_count_t)(MonoSignature);
        typedef MonoType (*signature_get_params_t)(MonoSignature, void**);
        typedef char* (*type_get_name_t)(MonoType);
        typedef Object (*runtime_invoke_t)(Method, void*, void**, Object*);
        typedef Object (*object_new_t)(MonoDomain, Class);
        typedef void* (*object_unbox_t)(Object);
        typedef int32_t (*class_value_size_t)(Class, uint32_t*);
        typedef MonoType (*class_get_type_t)(Class);
        typedef Object (*type_get_object_t)(MonoDomain, MonoType);
        typedef uintptr_t (*array_length_t)(Object);
        typedef char* (*array_addr_with_size_t)(Object, int, uintptr_t);
        typedef uint32_t (*gchandle_new_t)(Object, int32_t);
        typedef Object (*gchandle_get_target_t)(uint32_t);
        typedef void (*gchandle_free_t)(uint32_t);
        typedef char* (*string_to_utf8_t)(Object);
        typedef void (*free_t)(void*);

        static HMODULE s_module = nullptr;
        static get_root_domain_t p_get_root_domain;
        static domain_get_t p_domain_get;
        static thread_attach_t p_thread_attach;
        static image_loaded_t p_image_loaded;
        static domain_assembly_open_t p_domain_assembly_open;
        static assembly_get_image_t p_assembly_get_image;
        static class_from_name_t p_class_from_name;
        static class_get_method_from_name_t p_class_get_method_from_name;
        static class_get_methods_t p_class_get_methods;
        static method_get_name_t p_method_get_name;
        static method_signature_t p_method_signature;
        static signature_get_param_count_t p_signature_get_param_count;
        static signature_get_params_t p_signature_get_params;
        static type_get_name_t p_type_get_name;
        static runtime_invoke_t p_runtime_invoke;
        static object_new_t p_object_new;
        static object_unbox_t p_object_unbox;
        static class_value_size_t p_class_value_size;
        static class_get_type_t p_class_get_type;
        static type_get_object_t p_type_get_object;
        static array_length_t p_array_length;
        static array_addr_with_size_t p_array_addr_with_size;
        static gchandle_new_t p_gchandle_new;
        static gchandle_get_target_t p_gchandle_get_target;
        static gchandle_free_t p_gchandle_free;
        static string_to_utf8_t p_string_to_utf8;
        static free_t p_free;

        static MonoImage s_coreImage = nullptr;
        static bool s_attached = false;

        template <typename T>
        static void Bind(T& fn, const char* name)
        {
            fn = reinterpret_cast<T>(GetProcAddress(s_module, name));
        }

        bool Load()
        {
            if (s_module)
                return true;
            //유니티 Mono 백엔드는 mono-2.0-bdwgc.dll(최근) 또는 mono.dll(구버전)로 들어옴
            const wchar_t* names[] = { L"mono-2.0-bdwgc.dll", L"mono.dll", L"monobdwgc-2.0.dll" };
            HMODULE module = nullptr;
            for (const wchar_t* n : names)
                if ((module = GetModuleHandleW(n)) != nullptr)
                    break;
            if (!module)
                return false;
            s_module = module;
            Bind(p_get_root_domain, "mono_get_root_domain");
            Bind(p_domain_get, "mono_domain_get");
            Bind(p_thread_attach, "mono_thread_attach");
            Bind(p_image_loaded, "mono_image_loaded");
            Bind(p_domain_assembly_open, "mono_domain_assembly_open");
            Bind(p_assembly_get_image, "mono_assembly_get_image");
            Bind(p_class_from_name, "mono_class_from_name");
            Bind(p_class_get_method_from_name, "mono_class_get_method_from_name");
            Bind(p_class_get_methods, "mono_class_get_methods");
            Bind(p_method_get_name, "mono_method_get_name");
            Bind(p_method_signature, "mono_method_signature");
            Bind(p_signature_get_param_count, "mono_signature_get_param_count");
            Bind(p_signature_get_params, "mono_signature_get_params");
            Bind(p_type_get_name, "mono_type_get_name");
            Bind(p_runtime_invoke, "mono_runtime_invoke");
            Bind(p_object_new, "mono_object_new");
            Bind(p_object_unbox, "mono_object_unbox");
            Bind(p_class_value_size, "mono_class_value_size");
            Bind(p_class_get_type, "mono_class_get_type");
            Bind(p_type_get_object, "mono_type_get_object");
            Bind(p_array_length, "mono_array_length");
            Bind(p_array_addr_with_size, "mono_array_addr_with_size");
            Bind(p_gchandle_new, "mono_gchandle_new");
            Bind(p_gchandle_get_target, "mono_gchandle_get_target");
            Bind(p_gchandle_free, "mono_gchandle_free");
            Bind(p_string_to_utf8, "mono_string_to_utf8");
            Bind(p_free, "mono_free");
            bool ok = p_get_root_domain && p_class_from_name && p_class_get_method_from_name && p_runtime_invoke &&
                      (p_image_loaded || (p_domain_assembly_open && p_assembly_get_image));
            if (!ok)
                s_module = nullptr;
            return ok;
        }

        static MonoDomain Domain()
        {
            MonoDomain domain = p_domain_get ? p_domain_get() : nullptr;
            return domain ? domain : p_get_root_domain();
        }

        bool Init()
        {
            if (s_coreImage)
                return true;
            if (!Load() || p_get_root_domain() == nullptr)
                return false;
            if (!s_attached)
            {
                if (p_thread_attach)
                    p_thread_attach(p_get_root_domain());
                s_attached = true;
            }
            //이미 로드된 어셈블리를 이름으로 찾음 (mono_domain_assembly_open은 파일 경로를 받으므로 이름만으로는 실패할 수 있음)
            MonoImage image = nullptr;
            if (p_image_loaded)
            {
                image = p_image_loaded("UnityEngine.CoreModule");   // Unity 2017.2+
                if (!image)
                    image = p_image_loaded("UnityEngine");          // 구버전
            }
            if (!image && p_domain_assembly_open && p_assembly_get_image)
            {
                MonoAssembly assembly = p_domain_assembly_open(p_get_root_domain(), "UnityEngine.CoreModule");
                if (assembly)
                    image = p_assembly_get_image(assembly);
            }
            s_coreImage = image;
            return image != nullptr;
        }

        Class FindClass(const char* nameSpace, const char* name)
        {
            if (!s_coreImage)
                return nullptr;
            Class klass = p_class_from_name(s_coreImage, nameSpace, name);
            //구버전에서 모듈이 나뉘기 전 클래스는 UnityEngine 어셈블리에 있음
            if (!klass && p_image_loaded)
            {
                MonoImage legacy = p_image_loaded("UnityEngine");
                if (legacy && legacy != s_coreImage)
                    klass = p_class_from_name(legacy, nameSpace, name);
            }
            return klass;
        }

        Method FindMethod(Class klass, const char* name, int paramCount)
        {
            return klass ? p_class_get_method_from_name(klass, name, paramCount) : nullptr;
        }

        //Mono는 기본 형식을 "System.Int32" 또는 "int"로 적을 수 있어 둘 다 받음
        static bool TypeNameMatches(const char* actual, const char* expected)
        {
            if (!actual || !expected)
                return false;
            size_t len = strlen(expected);
            if (len > 0 && expected[len - 1] == '*')
                return strncmp(actual, expected, len - 1) == 0;
            if (strcmp(actual, expected) == 0)
                return true;
            static const char* aliases[][2] = {
                { "System.Int32", "int" }, { "System.Single", "single" }, { "System.Single", "float" },
                { "System.Boolean", "bool" }, { "System.IntPtr", "intptr" },
            };
            for (auto& a : aliases)
                if (strcmp(expected, a[0]) == 0 && strcmp(actual, a[1]) == 0)
                    return true;
            return false;
        }

        Method FindMethodSig(Class klass, const char* name, std::initializer_list<const char*> paramTypes)
        {
            if (!klass || !p_class_get_methods || !p_method_get_name || !p_method_signature ||
                !p_signature_get_param_count || !p_signature_get_params || !p_type_get_name)
                return nullptr;
            void* iter = nullptr;
            while (Method method = p_class_get_methods(klass, &iter))
            {
                if (strcmp(p_method_get_name(method), name) != 0)
                    continue;
                MonoSignature sig = p_method_signature(method);
                if (!sig || p_signature_get_param_count(sig) != paramTypes.size())
                    continue;
                bool match = true;
                void* paramIter = nullptr;
                for (const char* expected : paramTypes)
                {
                    MonoType type = p_signature_get_params(sig, &paramIter);
                    char* actual = type ? p_type_get_name(type) : nullptr;
                    bool ok = TypeNameMatches(actual, expected);
                    if (actual && p_free)
                        p_free(actual);
                    if (!ok)
                    {
                        match = false;
                        break;
                    }
                }
                if (match)
                    return method;
            }
            return nullptr;
        }

        bool Invoke(Method method, void* self, void** args, Object* result)
        {
            if (!method)
                return false;
            Object exception = nullptr;
            Object ret = p_runtime_invoke(method, self, args, &exception);
            if (exception)
                return false;
            if (result)
                *result = ret;
            return true;
        }

        template <typename T>
        static bool GetValue(Method method, void* self, T& value)
        {
            Object ret = nullptr;
            if (!Invoke(method, self, nullptr, &ret) || !ret || !p_object_unbox)
                return false;
            value = *(T*)p_object_unbox(ret);
            return true;
        }

        bool GetInt(Method method, void* self, int& value) { return GetValue<int>(method, self, value); }
        bool GetFloat(Method method, void* self, float& value) { return GetValue<float>(method, self, value); }

        bool GetBool(Method method, void* self, bool& value)
        {
            uint8_t raw = 0;   // C# bool은 1바이트
            if (!GetValue<uint8_t>(method, self, raw))
                return false;
            value = raw != 0;
            return true;
        }

        bool GetObject(Method method, void* self, Object& value)
        {
            value = nullptr;
            return Invoke(method, self, nullptr, &value);
        }

        bool SetInt(Method method, void* self, int value)
        {
            void* args[1] = { &value };
            return Invoke(method, self, args);
        }

        bool SetBool(Method method, void* self, bool value)
        {
            uint8_t raw = value ? 1 : 0;
            void* args[1] = { &raw };
            return Invoke(method, self, args);
        }

        bool SetFloat(Method method, void* self, float value)
        {
            void* args[1] = { &value };
            return Invoke(method, self, args);
        }

        bool SetObject(Method method, void* self, Object value)
        {
            void* args[1] = { value };   //참조 형식 인자는 객체 포인터를 그대로 넘김
            return Invoke(method, self, args);
        }

        Object New(Class klass)
        {
            return klass && p_object_new ? p_object_new(Domain(), klass) : nullptr;
        }

        void* Unbox(Object boxed)
        {
            return boxed && p_object_unbox ? p_object_unbox(boxed) : nullptr;
        }

        int ValueSize(Class klass)
        {
            return klass && p_class_value_size ? p_class_value_size(klass, nullptr) : 0;
        }

        Object TypeOf(Class klass)
        {
            if (!klass || !p_class_get_type || !p_type_get_object)
                return nullptr;
            return p_type_get_object(Domain(), p_class_get_type(klass));
        }

        uintptr_t ArrayLength(Object array)
        {
            return array && p_array_length ? p_array_length(array) : 0;
        }

        Object ArrayGet(Object array, uintptr_t index)
        {
            if (!array || !p_array_addr_with_size)
                return nullptr;
            return *(Object*)p_array_addr_with_size(array, (int)sizeof(void*), index);
        }

        uint32_t Hold(Object obj)
        {
            return obj && p_gchandle_new ? p_gchandle_new(obj, 0) : 0;
        }

        Object Held(uint32_t handle)
        {
            return handle && p_gchandle_get_target ? p_gchandle_get_target(handle) : nullptr;
        }

        void Release(uint32_t handle)
        {
            if (handle && p_gchandle_free)
                p_gchandle_free(handle);
        }

        std::string ToUtf8(Object monoString)
        {
            if (!monoString || !p_string_to_utf8)
                return std::string();
            char* utf8 = p_string_to_utf8(monoString);
            std::string result = utf8 ? utf8 : "";
            if (utf8 && p_free)
                p_free(utf8);
            return result;
        }
    }
}
