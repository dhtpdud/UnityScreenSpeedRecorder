# UnityCamcorder — 유니티 앱 외부 배속 캠코더

유니티로 만든 거의 모든 Windows 앱(Unity 2018.1 ~ Unity 6000.x)을 **외부에서** 녹화하는 독립 실행 프로그램입니다.
대상 앱 안에 코드를 넣는 모드가 아니라, 별도의 `.exe`가 대상 프로세스에 캡처용 DLL을 주입해 관리합니다.
그래서 대상 앱이 **갑자기 꺼지거나 멈춰도** 캠코더는 지금까지 받은 프레임으로 영상을 안전하게 마무리합니다.

원본 프로젝트(UnityScreenSpeedRecorder)가 앱 내부에서 하던 **배속 촬영**을, 소스 수정 없이 외부에서 재현합니다.

## 핵심 아이디어

- **프레임 캡처**: 대상의 DirectX `IDXGISwapChain::Present`를 후킹해, 화면에 나가는 백버퍼를 매 프레임 CPU로 읽어 옵니다. (OBS의 "게임 캡처"와 같은 방식, D3D11·D3D12 지원)
- **배속 촬영**: 대상의 Mono 런타임을 통해 `UnityEngine.Time.captureDeltaTime = 1/fps` 로 설정합니다. 그러면 게임 시간이 실제 경과 시간과 무관하게 프레임당 정확히 `1/fps`씩 흐르므로, 하드웨어가 허락하는 한 최대 속도로 프레임이 쏟아집니다. 결과 영상을 `fps`로 재생하면 **정상 속도**로 보입니다.
- **인코딩**: 읽어 온 원본 프레임을 `ffmpeg` 표준 입력으로 바로 흘려보내 실시간 인코딩합니다. 가능하면 GPU 하드웨어 인코더(NVENC → QSV → AMF → libx264)를 자동 선택합니다.
- **공유 메모리 링 버퍼**로 DLL(생산자)과 캠코더(소비자)를 연결합니다. 캠코더가 인코딩을 못 따라오면 그 프레임은 버려 대상 앱이 막히지 않게 합니다.

## 구성

```
UnityCamcorderGui.exe       쉬운 사용을 위한 창 프로그램 (더블클릭해서 실행)
UnityCamcorder.exe          캠코더 본체 (x64, .NET 자체 포함 단일 파일, 명령줄)
camcorder.json              설정 파일 (exe 옆에 두면 자동 사용)
x64/UnityCamcorderHook.dll  64비트 대상용 캡처 DLL
x64/Inject.exe              64비트 대상용 주입기
x86/UnityCamcorderHook.dll  32비트 대상용 캡처 DLL
x86/Inject.exe              32비트 대상용 주입기
ffmpeg.exe                  (직접 두거나 PATH에 있어야 함)
```

캠코더는 대상의 비트 수(32/64)를 자동 판별해 맞는 폴더의 DLL·주입기를 사용합니다.

## 준비물

- 64비트 Windows 10/11
- `ffmpeg.exe` — `UnityCamcorder.exe` 옆에 두거나, 설정 `FfmpegPath`로 지정하거나, PATH에 있으면 됩니다.
  (원본 프로젝트가 쓰는 `Assets/StreamingAssets/ffmpeg/ffmpeg.zip` 안의 ffmpeg를 풀어서 써도 됩니다.)

## 사용법

### 창으로 쓰기 (추천)

`UnityCamcorderGui.exe`를 더블클릭합니다. (Windows 10/11 기본 .NET Framework 4.8로 실행되어 따로 설치할 것이 없습니다.)

1. **녹화할 유니티 앱**: "앱을 실행해서 녹화"에서 exe를 고르거나(창에 끌어다 놓아도 됨), "이미 켜진 앱에 연결"에서 목록의 앱을 고릅니다. 유니티 앱은 목록 맨 위에 `[유니티]`로 표시됩니다.
2. **결과 영상**: 저장할 파일, FPS, 길이(초 / 프레임 / 중지할 때까지), 코덱, 화질을 정합니다. ffmpeg.exe가 캠코더 옆이나 PATH에 없으면 직접 골라 줍니다.
3. **● 녹화 시작**을 누르면 진행률, 배속, 기록이 실시간으로 보입니다. **■ 중지**를 누르면 지금까지 찍은 프레임으로 영상을 정상 마무리합니다. (한 번 더 누르면 강제 종료)
4. 끝나면 **영상 열기**, **저장 폴더 열기**로 바로 확인합니다.

창은 같은 폴더의 `UnityCamcorder.exe`를 실행해 녹화하므로 두 파일과 `x64/`, `x86/` 폴더를 함께 두어야 합니다.
마지막 설정은 `%LOCALAPPDATA%\UnityCamcorder\gui.json`에 저장되어 다음에 그대로 열립니다.

### 명령줄로 쓰기

1. 설정 파일 만들기:
   ```
   UnityCamcorder.exe --init
   ```
   생성된 `camcorder.json`에서 `TargetExe`(대상 앱 경로)와 `OutputPath`(저장 경로)를 채웁니다.

2. 녹화:
   ```
   UnityCamcorder.exe
   ```

설정 파일 없이 명령줄만으로도 쓸 수 있습니다:
```
UnityCamcorder.exe --target "C:\Games\App\App.exe" --out "C:\rec\out.mp4" --fps 60 --duration 10
```
이미 떠 있는 앱에 붙기:
```
UnityCamcorder.exe --process App --out out.mp4 --frames 600
```

### 주요 옵션 / 설정

| 설정(camcorder.json) | 명령줄 | 설명 |
|---|---|---|
| `TargetExe` | `--target` | 실행할 유니티 앱 경로 |
| `TargetProcessName` | `--process` | 이미 떠 있는 프로세스 이름 |
| `TargetPid` | `--pid` | 이미 떠 있는 프로세스 PID |
| `OutputPath` | `--out` | 저장 경로(확장자로 컨테이너 결정: mp4/mkv/webm/mov) |
| `Fps` | `--fps` | 결과 영상 프레임레이트(= 촬영 고정 프레임레이트) |
| `TotalFrames` | `--frames` | 캡처할 총 프레임 수 |
| `DurationSeconds` | `--duration` | 총 영상 길이(초). `TotalFrames` 미지정 시 사용 |
| `Codec` | `--codec` | `auto`(GPU H.264 자동) 또는 ffmpeg 인코더 이름 |
| `Quality` | `--quality` | CQ/CRF 값(낮을수록 고화질) |
| `FfmpegPath` | `--ffmpeg` | ffmpeg.exe 경로 |
| `CloseTargetWhenDone` | `--keep-open` | 녹화 후 대상 앱 종료 여부 |
| `FreezeTimeoutMs` | — | 이 시간 동안 새 프레임이 없으면 멈춘 것으로 보고 마무리 |

녹화 중 `Ctrl+C`를 누르거나 표준 입력으로 `stop` 한 줄을 보내면, 지금까지의 프레임으로 영상을 정상 마무리합니다. (`Ctrl+C`를 한 번 더 누르면 바로 종료)

## 안정성 (대상이 꺼지거나 멈출 때)

- 캠코더는 프레임을 받는 즉시 ffmpeg로 흘려보내므로, 어느 시점에 중단되어도 그때까지의 영상이 **재생 가능한 완성본**으로 저장됩니다.
- 대상 프로세스가 **종료**되면 즉시 감지해 남은 프레임을 쓰고 영상을 닫습니다.
- `FreezeTimeoutMs` 동안 새 프레임이 없으면 **멈춤**으로 보고 영상을 마무리합니다.
- 캠코더가 먼저 죽으면, 주입된 DLL이 이를 감지(캠코더 PID 감시)해 `captureDeltaTime` 등 바꿔 둔 설정을 **원래대로 되돌립니다**. 대상 앱이 느려진 채로 남지 않습니다.

## 빌드

### 네이티브(DLL + 주입기)

MSVC(권장):
```
cmake -S Camcorder/Native -B build-x64 -A x64   && cmake --build build-x64 --config Release
cmake -S Camcorder/Native -B build-x86 -A Win32 && cmake --build build-x86 --config Release
```
MinGW(교차 컴파일도 가능):
```
cmake -S Camcorder/Native -B build-x64 -DCMAKE_SYSTEM_NAME=Windows \
  -DCMAKE_CXX_COMPILER=x86_64-w64-mingw32-g++-posix -DCMAKE_C_COMPILER=x86_64-w64-mingw32-gcc-posix
cmake --build build-x64
```

### 캠코더(C#)
```
dotnet publish Camcorder/Controller/UnityCamcorder.csproj -c Release -o dist
```

### 창 프로그램(C#, WinForms, .NET Framework 4.8)
```
dotnet publish Camcorder/Gui/UnityCamcorderGui.csproj -c Release -o dist
```

GitHub Actions(`.github/workflows/camcorder.yml`)가 위 과정을 자동으로 수행해 배포용 zip을 만듭니다.

## 알려진 한계

- **IL2CPP 백엔드**: 내보낸 심벌이 없어 외부에서 `Time` API를 안전하게 호출하기 어렵습니다. 이 경우 배속 없이 **실시간**으로 캡처합니다(녹화 자체는 됩니다). Mono 백엔드에서 배속이 동작합니다.
- **그래픽스 API**: D3D11, D3D12를 지원합니다. OpenGL/Vulkan으로 강제된 앱은 현재 지원하지 않습니다(유니티 Windows 기본값은 D3D11이라 대부분 해당 없음).
- **백버퍼 형식**: 8비트 BGRA/RGBA를 지원합니다. HDR(10비트) 스왑체인은 아직 다루지 않습니다.
- **오디오**: 현재 영상만 녹화합니다.
- 보호된 창(안티치트/DRM)이 있는 앱에는 주입이 막힐 수 있습니다. 본 도구는 사용자가 소유·제어하는 앱 녹화를 위한 것입니다.
