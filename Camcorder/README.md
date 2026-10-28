# UnityCamcorder — 유니티 앱 외부 배속 캠코더

유니티로 만든 거의 모든 Windows 앱(Unity 2018.1 ~ Unity 6000.x)을 **외부에서** 녹화하는 독립 실행 프로그램입니다.
대상 앱 안에 코드를 넣는 모드가 아니라, 별도의 `.exe`가 대상 프로세스에 캡처용 DLL을 주입해 관리합니다.
그래서 대상 앱이 **갑자기 꺼지거나 멈춰도** 캠코더는 지금까지 받은 프레임으로 영상을 안전하게 마무리합니다.

원본 프로젝트(UnityScreenSpeedRecorder)가 앱 내부에서 하던 **배속 촬영**을, 소스 수정 없이 외부에서 재현합니다.

## 핵심 아이디어

- **카메라 촬영 (Mono 앱, 기본)**: 원본 프로젝트와 같은 방식입니다. 카메라의 `targetTexture`를 캠코더가 만든 `RenderTexture`로 바꿔 **화면 대신** 그곳에 그리게 하고, 매 프레임 `AsyncGPUReadback`으로 GPU에서 비동기로 읽어 옵니다(쓸 수 없는 환경에서는 `Texture2D.ReadPixels`로 동기 읽기). 화면(백버퍼)을 다시 읽지 않으므로 GPU가 같은 장면을 두 번 다루지 않습니다. 녹화 중에는 앱 화면이 멈춘 것처럼 보이는 것이 정상이며, 녹화가 끝나면 카메라는 원래대로 돌아옵니다.
- **촬영 카메라 선택**: 앱 안의 카메라 목록을 불러와 여러 대를 고를 수 있고, 카메라마다 영상 파일을 따로 저장합니다. 고르지 않으면 화면에 그리던 카메라 전부를 하나의 RenderTexture로 모아 한 영상으로 찍습니다. 평소 꺼져 있는 카메라는 촬영 동안만 켜고, 이미 자기 RenderTexture로 그리던 카메라는 그 텍스처를 그대로 읽습니다.
- **화면 캡처 (IL2CPP 등 대체 경로)**: 카메라를 다룰 수 없는 앱은 DirectX `IDXGISwapChain::Present`를 후킹해 화면에 나가는 백버퍼를 읽습니다. (OBS의 "게임 캡처"와 같은 방식, D3D11·D3D12)
- **배속 촬영**: 대상의 Mono 런타임을 통해 `UnityEngine.Time.captureDeltaTime = 1/fps` 로 설정합니다(2019.1 이하는 `captureFramerate`, 예: 2018.1). 동시에 vSync와 프레임 제한을 풀고, 다른 창을 눌러도 멈추지 않게 `runInBackground`를 켭니다(녹화가 끝나면 원래 값으로 복원). 그러면 게임 시간이 실제 경과 시간과 무관하게 프레임당 정확히 `1/fps`씩 흐르므로, 하드웨어가 허락하는 한 최대 속도로 프레임이 쏟아집니다. 결과 영상을 `fps`로 재생하면 **정상 속도**로 보입니다.
- **메인 스레드 진입**: 유니티 API는 메인 스레드에서만 부를 수 있으므로, DLL이 `Application.onBeforeRender`(와 `Canvas.willRenderCanvases`) 이벤트에 콜백을 등록해 매 프레임 메인 스레드에서 배속·카메라 작업을 합니다. 등록에 실패하면 화면을 실시간으로 캡처합니다.
- **인코딩**: 읽어 온 원본 프레임을 `ffmpeg` 표준 입력으로 바로 흘려보내 실시간 인코딩합니다. 가능하면 GPU 하드웨어 인코더(NVENC → QSV → AMF → libx264)를 자동 선택합니다.
- **공유 메모리 링 버퍼**로 DLL(생산자)과 캠코더(소비자)를 연결합니다. 배속 촬영 중 캠코더가 인코딩을 못 따라오면 게임이 잠깐 기다리게 해서 **프레임을 버리지 않습니다**(게임 시간이 1/fps씩만 흐르므로 영상이 끊기지 않음). 배속을 쓸 수 없는 실시간 캡처에서만 프레임을 버립니다.

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
2. **촬영 카메라 (선택)**: **카메라 불러오기**를 누르면 앱을 켜고(또는 켜진 앱에 연결해) 카메라 목록을 보여 줍니다. 찍을 카메라를 체크하세요. 여러 대를 체크하면 `저장파일_카메라이름.mp4`로 카메라마다 따로 저장됩니다. 체크하지 않으면 화면에 보이는 그대로 한 영상으로 찍습니다.
3. **결과 영상**: 저장할 파일, FPS, 길이(초 / 프레임 / 중지할 때까지), 코덱, 화질을 정합니다. ffmpeg.exe가 캠코더 옆이나 PATH에 없으면 직접 골라 줍니다.
4. **● 녹화 시작**을 누르면 진행률, 배속, 기록이 실시간으로 보입니다. **■ 중지**를 누르면 지금까지 찍은 프레임으로 영상을 정상 마무리합니다. (한 번 더 누르면 강제 종료)
5. 끝나면 **영상 열기**, **저장 폴더 열기**로 바로 확인합니다.

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
| `Cameras` | `--cameras` | 촬영 카메라 이름 또는 `#번호` 목록 (비우면 화면 전체, `--list-cameras`로 확인) |
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

### 촬영 카메라 고르기 (명령줄)
```
UnityCamcorder.exe --target "C:\Games\App\App.exe" --list-cameras
UnityCamcorder.exe --pid 12345 --cameras "Main Camera,MinimapCamera" --out "C:\rec\out.mp4" --duration 10
```
`--list-cameras`는 카메라 목록(이름, `#번호`, 크기)만 보여 주고 앱은 켜 둡니다. `--cameras`에는 이름이나 `#번호`를 쉼표로 적습니다.
설정 파일에서는 `"Cameras": ["Main Camera", "MinimapCamera"]` 로 적습니다. 결과는 `out_Main Camera.mp4`, `out_MinimapCamera.mp4`처럼 저장됩니다.

## 백신이 Inject.exe를 바이러스로 표시할 때 (오탐)

`Inject.exe`는 사용자가 고른 유니티 앱에 캡처 DLL을 넣는 작은 도구입니다. "다른 프로세스에 DLL을 넣는" 기법(`CreateRemoteThread` + `LoadLibraryW`)이
악성코드와 같기 때문에, 일부 백신이 동작만 보고 `Trojan:Win32/Wacatac.C!ml`, `Suspicious.low.ml.score` 같은 **일반(휴리스틱/ML) 탐지**를 냅니다.
이름 끝의 `!ml`, `ml.score`는 머신러닝 추정이라는 뜻이며, 같은 방식을 쓰는 OBS 게임 캡처나 모드 로더도 자주 겪는 문제입니다.
코드는 모두 이 저장소에 공개되어 있고, 배포 zip은 GitHub Actions가 이 소스로 직접 빌드합니다.

오탐을 줄이기 위해 다음을 적용했습니다.

- 모든 실행 파일과 DLL에 제조사·제품·설명·버전 정보와 매니페스트(관리자 권한 요구 없음)를 넣음
- 최신 익스플로잇 완화 기법(CFG, ASLR, DEP)을 명시적으로 켜서 정상 소프트웨어와 같은 PE 특성을 갖춤
- 배포 zip은 GitHub Actions가 이 소스로 직접 빌드 (재현 가능)

> **중요:** 남은 탐지는 전부 `!ml`·`ml.score` 같은 **동작 기반(행위 휴리스틱)** 추정입니다.
> "다른 프로세스에 DLL을 넣는다"는 행위 자체가 원인이라, 코드 수정만으로는 더 내릴 수 없습니다.
> 실제로 효과가 있는 정식 방법은 **코드 서명**과 **백신사 오탐 신고** 두 가지뿐입니다.

1. **코드 서명 (가장 효과적)**: 코드 서명 인증서(OV/EV `.pfx`)가 있으면, CI가 빌드할 때 자동으로 서명하도록 준비해 두었습니다.
   GitHub 저장소 → Settings → Secrets and variables → Actions 에서 아래 시크릿을 등록하면 이후 모든 빌드가 서명됩니다.
   - `SIGN_PFX_BASE64`: 인증서 `.pfx`를 base64로 인코딩한 문자열 (`[Convert]::ToBase64String([IO.File]::ReadAllBytes("cert.pfx"))`)
   - `SIGN_PFX_PASSWORD`: `.pfx` 비밀번호
   - `SIGN_TIMESTAMP_URL`(선택): 타임스탬프 서버 (기본 `http://timestamp.digicert.com`)

   서명하면 대부분의 ML 탐지와 SmartScreen 경고가 사라집니다. 인증서는 유료이며, EV 인증서는 즉시 평판을 얻고 OV 인증서는 사용량이 쌓이며 평판이 올라갑니다.
2. **백신사 오탐 신고 (무료, 보통 수일 내 반영)**: 탐지한 백신사에 파일을 "오탐"으로 직접 제출합니다.
   - Microsoft Defender: <https://www.microsoft.com/en-us/wdsi/filesubmission> (Submission type: *Incorrectly detected as malware*)
   - 그 외 백신도 각자 오탐 신고 페이지가 있습니다 (Bkav, SecureAge, Trapmine 등).
3. **내 PC에서 바로 쓰기**: Windows 보안 → 바이러스 및 위협 방지 → 설정 관리 → 제외에 캠코더 폴더를 추가합니다. (직접 빌드했거나 이 저장소의 Actions에서 받은 파일에만 하세요.)

## 문제 해결

- **"이전 버전 캠코더 DLL이 이미 들어가 있습니다"**: 주입된 DLL은 앱이 꺼질 때까지 내려가지 않습니다. 캠코더를 새 버전으로 바꿨다면 대상 앱을 완전히 종료하고 다시 실행한 뒤 녹화하세요.
- **"앱이 촬영 준비에 응답하지 않습니다"**: 뒤에 붙는 이유를 확인하세요. "아직 한 프레임도 부르지 않았습니다"면 앱이 멈춰 있거나 최소화됐거나 로딩 중입니다. 앱 창을 띄워 둔 채 다시 시도하세요.
- 자세한 기록은 `%LOCALAPPDATA%\UnityCamcorder\Logs\hook_<PID>.log`에 있습니다. "메인 스레드 틱 시작" 줄이 있으면 배속·카메라 촬영이 동작하는 상태입니다.

## 알려진 한계

- **IL2CPP 백엔드**: 내보낸 심벌이 없어 외부에서 유니티 API를 안전하게 호출하기 어렵습니다. 이 경우 카메라 선택과 배속 없이 화면을 **실시간**으로 캡처합니다(녹화 자체는 됩니다). Mono 백엔드에서 배속과 카메라 촬영이 동작합니다.
- **Screen Space - Overlay UI**: 이 방식의 캔버스는 카메라가 아니라 화면에 직접 그려지므로, 원본 프로젝트와 마찬가지로 카메라 촬영 영상에는 들어가지 않습니다. UI까지 찍으려면 앱에서 캔버스를 Screen Space - Camera로 두세요.
- **그래픽스 API**: D3D11, D3D12를 지원합니다. OpenGL/Vulkan으로 강제된 앱은 현재 지원하지 않습니다(유니티 Windows 기본값은 D3D11이라 대부분 해당 없음).
- **백버퍼 형식**: 8비트 BGRA/RGBA를 지원합니다. HDR(10비트) 스왑체인은 아직 다루지 않습니다.
- **오디오**: 현재 영상만 녹화합니다.
- 보호된 창(안티치트/DRM)이 있는 앱에는 주입이 막힐 수 있습니다. 본 도구는 사용자가 소유·제어하는 앱 녹화를 위한 것입니다.
