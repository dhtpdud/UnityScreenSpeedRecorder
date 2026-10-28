cmake_minimum_required(VERSION 3.15)
project(UnityCamcorderHook CXX C)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# MSVC: VC++ 재배포 패키지 없이 어디서나 실행되도록 런타임 정적 링크(/MT)
if(MSVC)
    set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded")
    add_compile_options(/W3)
endif()

# MinHook (C 라이브러리)
add_library(minhook STATIC
    third_party/minhook/src/hook.c
    third_party/minhook/src/buffer.c
    third_party/minhook/src/trampoline.c
    third_party/minhook/src/hde/hde32.c
    third_party/minhook/src/hde/hde64.c
)
target_include_directories(minhook PUBLIC third_party/minhook/include)

# 주입 DLL
add_library(UnityCamcorderHook SHARED
    src/DllMain.cpp
    src/Mono.cpp
    src/TimeControl.cpp
    src/CameraCapture.cpp
    src/MainThreadHook.cpp
    src/FrameBuffer.cpp
    src/PresentHook.cpp
    src/Capture11.cpp
    src/Capture12.cpp
)
target_link_libraries(UnityCamcorderHook PRIVATE minhook d3d11 dxgi ole32 shell32)
target_include_directories(UnityCamcorderHook PRIVATE src third_party/minhook/include)

if(MINGW)
    # 런타임 DLL 의존성을 없애 어떤 PC에서도 바로 실행되게 정적 링크
    target_link_options(UnityCamcorderHook PRIVATE -static -static-libgcc -static-libstdc++)
    target_compile_definitions(UnityCamcorderHook PRIVATE UNICODE _UNICODE)
endif()

set_target_properties(UnityCamcorderHook PROPERTIES PREFIX "" OUTPUT_NAME "UnityCamcorderHook")

# DLL 주입기 (대상과 같은 비트 수여야 함)
add_executable(Inject src/Injector.cpp)
target_compile_definitions(Inject PRIVATE UNICODE _UNICODE)
if(MINGW)
    target_link_options(Inject PRIVATE -static -static-libgcc -static-libstdc++ -municode)
endif()
