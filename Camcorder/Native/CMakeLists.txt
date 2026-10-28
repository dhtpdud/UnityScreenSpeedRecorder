cmake_minimum_required(VERSION 3.15)
project(UnityCamcorderHook CXX C)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# MSVC: VC++ 재배포 패키지 없이 어디서나 실행되도록 런타임 정적 링크(/MT)
if(MSVC)
    set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded")
    add_compile_options(/W3)
    # 소스와 문자열을 UTF-8로 (한국어 로그·오류 문구가 시스템 코드 페이지에 따라 깨지지 않도록)
    add_compile_options($<$<COMPILE_LANGUAGE:C,CXX>:/utf-8>)
    # 최신 익스플로잇 완화 기법을 명시적으로 켠다: 제어 흐름 보호(CFG) + ASLR/DEP(+64비트 고엔트로피 ASLR).
    # 이런 완화가 "빠진" 작고 서명 없는 실행 파일일수록 백신 휴리스틱/ML이 더 의심하므로,
    # 정상 소프트웨어처럼 보이도록 강화한다. (탐지 회피가 아니라 실제 보안 강화)
    add_compile_options($<$<COMPILE_LANGUAGE:C,CXX>:/guard:cf>)
    add_link_options(/guard:cf /DYNAMICBASE /NXCOMPAT)
    if(CMAKE_SIZEOF_VOID_P EQUAL 8)
        add_link_options(/HIGHENTROPYVA)
    endif()
endif()

# 재현 가능한 빌드: 빌드 시각을 파일에 넣지 않아, 소스가 같으면 매번 똑같은 파일(같은 해시)이 나옴.
# 백신은 처음 보는 파일을 더 의심하고, 오탐 해제(Microsoft 신고 등)도 파일 해시 단위로 적용되므로
# 소스가 그대로인 Inject.exe가 빌드마다 새 파일로 바뀌지 않게 합니다.
if(MSVC)
    add_compile_options($<$<COMPILE_LANGUAGE:C,CXX>:/Brepro>)
    add_link_options(/Brepro)
elseif(MINGW)
    add_link_options(-Wl,--no-insert-timestamp)
endif()

# 버전 정보 리소스 (제조사·설명·버전). 백신의 일반(ML) 오탐을 줄이고 파일 속성에 정체를 밝힘
set(CAMCORDER_VERSION "1.2.0.0")
# Inject.exe는 따로 고정된 버전을 씀. Injector.cpp나 주입기 리소스(매니페스트·아이콘)를 바꿀 때만 올릴 것
# (올리면 새 파일이 되어 오탐 해제를 다시 받아야 함)
set(INJECT_VERSION "1.0.0.0")
function(camcorder_version_resource target version description original type manifest icon)
    set(CAMCORDER_VERSION "${version}")
    string(REPLACE "." "," CAMCORDER_VERSION_COMMA "${version}")
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
    if(icon)
        set(CAMCORDER_ICON_LINE "1 ICON \"${icon}\"")
    else()
        set(CAMCORDER_ICON_LINE "")
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
camcorder_version_resource(UnityCamcorderHook "${CAMCORDER_VERSION}"
    "UnityCamcorder frame capture module (captures Unity camera frames for recording)"
    "UnityCamcorderHook.dll" "VFT_DLL" "" "")

# DLL 주입기 (대상과 같은 비트 수여야 함)
add_executable(Inject src/Injector.cpp)
camcorder_version_resource(Inject "${INJECT_VERSION}"
    "UnityCamcorder helper: loads the capture module into the Unity app the user chose to record"
    "Inject.exe" "VFT_APP" "${CMAKE_CURRENT_SOURCE_DIR}/res/Inject.manifest" "${CMAKE_CURRENT_SOURCE_DIR}/res/app.ico")
target_compile_definitions(Inject PRIVATE UNICODE _UNICODE)
if(MINGW)
    target_link_options(Inject PRIVATE -static -static-libgcc -static-libstdc++ -municode)
endif()
