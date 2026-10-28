cmake_minimum_required(VERSION 3.15)
project(UnityCamcorderHook CXX C)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# MSVC: VC++ 재배포 패키지 없이 어디서나 실행되도록 런타임 정적 링크(/MT)
if(MSVC)
    set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded")
    add_compile_options(/W3)
endif()

# 버전 정보 리소스 (제조사·설명·버전). 백신의 일반(ML) 오탐을 줄이고 파일 속성에 정체를 밝힘
set(CAMCORDER_VERSION "1.1.0.0")
string(REPLACE "." "," CAMCORDER_VERSION_COMMA "${CAMCORDER_VERSION}")
function(camcorder_version_resource target description original type manifest)
    set(CAMCORDER_FILE_DESCRIPTION "${description}")
    set(CAMCORDER_ORIGINAL_FILENAME "${original}")
    get_filename_component(CAMCORDER_INTERNAL_NAME "${original}" NAME_WE)
    set(CAMCORDER_FILE_TYPE "${type}")
    if(manifest)
        # 1 = CREATEPROCESS_MANIFEST_RESOURCE_ID, 24 = RT_MANIFEST
        set(CAMCORDER_MANIFEST_LINE "1 24 \"${manifest}\"")
        if(MSVC)
            # MSVC 링커가 만드는 기본 매니페스트와 겹치지 않도록 끔 (우리 매니페스트를 리소스로 넣음)
            target_link_options(${target} PRIVATE /MANIFEST:NO)
        endif()
    else()
        set(CAMCORDER_MANIFEST_LINE "")
    endif()
    set(rc "${CMAKE_CURRENT_BINARY_DIR}/${target}_version.rc")
    configure_file("${CMAKE_CURRENT_SOURCE_DIR}/res/version.rc.in" "${rc}" @ONLY)
    target_sources(${target} PRIVATE "${rc}")
endfunction()

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
camcorder_version_resource(UnityCamcorderHook
    "UnityCamcorder frame capture module (captures Unity camera frames for recording)"
    "UnityCamcorderHook.dll" "VFT_DLL" "")

# DLL 주입기 (대상과 같은 비트 수여야 함)
add_executable(Inject src/Injector.cpp)
camcorder_version_resource(Inject
    "UnityCamcorder helper: loads the capture module into the Unity app the user chose to record"
    "Inject.exe" "VFT_APP" "${CMAKE_CURRENT_SOURCE_DIR}/res/Inject.manifest")
target_compile_definitions(Inject PRIVATE UNICODE _UNICODE)
if(MINGW)
    target_link_options(Inject PRIVATE -static -static-libgcc -static-libstdc++ -municode)
endif()
