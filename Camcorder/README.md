# UnityCamcorder — 유니티 앱 배속 캠코더

유니티로 만든 Windows 앱(Unity 2018.1 ~ Unity 6000.6, Mono 빌드)을 **소스 수정 없이** 배속 녹화하는 프로그램입니다.
원본 프로젝트(UnityScreenSpeedRecorder)가 앱 내부에서 하던 **배속 촬영**과 **카메라별 녹화**를, 이미 빌드된 앱에서 그대로 재현합니다.

앱 안에서는 널리 쓰이는 공개 모딩 프레임워크 [BepInEx 5](https://github.com/BepInEx/BepInEx)가 캠코더 플러그인을 불러오고,
캠코더(별도 exe)가 그 플러그인을 조종하며 영상을 인코딩합니다. 인코딩은 앱 밖에서 하므로 대상 앱이 **갑자기 꺼지거나 멈춰도**
지금까지 받은 프레임으로 영상을 안전하게 마무리합니다.

## 핵심 아이디어

- **BepInEx 플러그인**: 창 프로그램이 녹화할 앱 폴더에 BepInEx 5와 캠코더 플러그인(`UnityCamcorderPlugin.dll`)을 처음 한 번 설치합니다.
  그 뒤로는 앱이 켜질 때 BepInEx가 플러그인을 자동으로 불러옵니다. 다른 프로세스에 코드를 넣는 주입기는 쓰지 않습니다.
- **카메라 촬영**: 원본 프로젝트와 같은 방식입니다. 카메라의 `targetTexture`를 캠코더가 만든 `RenderTexture`로 바꿔 **화면 대신** 그곳에 그리게 하고,
  매 프레임 `AsyncGPUReadback`으로 GPU에서 비동기로 읽어 옵니다(쓸 수 없는 환경에서는 `Texture2D.ReadPixels`로 동기 읽기).
  녹화 중에는 앱 화면이 멈춘 것처럼 보이는 것이 정상이며, 녹화가 끝나면 카메라는 원래대로 돌아옵니다.
- **촬영 카메라 선택**: 앱 안의 카메라 목록을 불러와 여러 대를 고를 수 있고, 카메라마다 영상 파일을 따로 저장합니다(`저장파일_카메라이름.mp4`).
  고르지 않으면 화면에 그리던 카메라 전부를 하나의 RenderTexture로 모아 한 영상으로 찍습니다. 평소 꺼져 있는 카메라는 촬영 동안만 켜고,
  이미 자기 RenderTexture로 그리던 카메라는 그 텍스처를 그대로 읽습니다. 화면에 그리는 카메라가 하나도 없으면 프레임 끝에서 화면을 읽습니다.
- **URP 카메라 스택**: 오버레이 카메라는 혼자 그릴 수 없으므로 베이스 카메라를 찍으면 그 위에 쌓인 오버레이까지 함께 찍힙니다.
- **배속 촬영**: 플러그인이 `Time.captureFramerate = fps`로 두고 vSync와 프레임 제한을 풉니다(녹화가 끝나면 원래 값으로 복원).
  그러면 게임 시간이 실제 경과 시간과 무관하게 프레임당 정확히 `1/fps`씩 흐르므로, 하드웨어가 허락하는 한 최대 속도로 프레임이 쏟아집니다.
  결과 영상을 `fps`로 재생하면 **정상 속도**로 보입니다.
- **인코딩**: 읽어 온 원본 프레임을 `ffmpeg` 표준 입력으로 바로 흘려보내 실시간 인코딩합니다. 가능하면 GPU 하드웨어 인코더(NVENC → QSV → AMF → libx264)를 자동 선택합니다.
- **공유 메모리 링 버퍼**로 플러그인(생산자)과 캠코더(소비자)를 연결합니다. 배속 촬영 중 캠코더가 인코딩을 못 따라오면 게임이 잠깐 기다리게 해서
  **프레임을 버리지 않습니다**(게임 시간이 1/fps씩만 흐르므로 영상이 끊기지 않음).
- 캠코더는 원본 프로젝트(RecorderFlusher)처럼 **카메라(영상)마다 ffmpeg 전용 쓰기 스레드**를 두고, 받은 프레임을 메모리(`BufferMemoryMB`)에 줄 세워
  슬롯을 바로 비웁니다. 그래서 카메라 여러 대의 인코딩이 동시에 진행됩니다.

## 구성

```
UnityCamcorderGui.exe                  쉬운 사용을 위한 창 프로그램 (더블클릭해서 실행)
UnityCamcorder.exe                     캠코더 본체 (x64, .NET 자체 포함 단일 파일, 명령줄)
camcorder.json                         설정 파일 (exe 옆에 두면 자동 사용)
plugin/UnityCamcorderPlugin.dll        앱 안에서 도는 BepInEx 5 플러그인 (32/64비트 앱 공용)
bepinex/BepInEx_win_x64_<버전>.zip     64비트 앱에 설치할 BepInEx 5 (공식 배포본 그대로)
bepinex/BepInEx_win_x86_<버전>.zip     32비트 앱에 설치할 BepInEx 5
bepinex/LICENSE-BepInEx.txt            BepInEx 라이선스 (LGPL-2.1)
ffmpeg.exe                             (직접 두거나 PATH에 있어야 함)
```

녹화할 앱 폴더에는 다음이 설치됩니다 (BepInEx가 이미 있으면 플러그인만):

```
<앱 폴더>/winhttp.dll, doorstop_config.ini, .doorstop_version, changelog.txt   BepInEx 로더
<앱 폴더>/BepInEx/core/...                                                    BepInEx 본체
<앱 폴더>/BepInEx/plugins/UnityCamcorder/UnityCamcorderPlugin.dll             캠코더 플러그인
<앱 폴더>/BepInEx/plugins/UnityCamcorder/install.txt                          제거할 때 쓰는 설치 기록
```

## 준비물

- 64비트 Windows 10/11
- Mono로 빌드된 유니티 앱 (Unity 2018.1 ~ 6000.6). IL2CPP 빌드는 BepInEx 5가 지원하지 않아 녹화할 수 없습니다.
- `ffmpeg.exe` — `UnityCamcorder.exe` 옆에 두거나, 설정 `FfmpegPath`로 지정하거나, PATH에 있으면 됩니다.
  (원본 프로젝트가 쓰는 `Assets/StreamingAssets/ffmpeg/ffmpeg.zip` 안의 ffmpeg를 풀어서 써도 됩니다.)

## 사용법

### 창으로 쓰기 (추천)

`UnityCamcorderGui.exe`를 더블클릭합니다. (Windows 10/11 기본 .NET Framework 4.8로 실행되어 따로 설치할 것이 없습니다.)

1. **녹화할 유니티 앱**: "앱을 실행해서 녹화"에서 exe를 고르거나(창에 끌어다 놓아도 됨), "이미 켜진 앱에 연결"에서 목록의 앱을 고릅니다.
   **플러그인** 줄에 그 앱의 유니티 버전과 BepInEx/플러그인 설치 상태가 보입니다.
   - 설치되어 있지 않으면 **녹화 시작**이나 **카메라 불러오기**를 누를 때 자동으로 설치합니다. **설치/업데이트** 버튼으로 미리 설치해도 됩니다.
   - 이미 켜져 있는 앱에 처음 설치했다면, 플러그인은 앱이 켜질 때 로드되므로 앱을 한 번 닫고 다시 녹화하세요. (창이 "앱을 실행해서 녹화"로 바꿔 둡니다)
   - **제거**는 캠코더 플러그인을 지우고, BepInEx도 캠코더가 설치했고 다른 BepInEx 플러그인이 없으면 함께 지웁니다.
2. **촬영 카메라 (선택)**: **카메라 불러오기**를 누르면 앱을 켜고(또는 켜진 앱에 연결해) 카메라 목록을 보여 줍니다. 찍을 카메라를 체크하세요.
   여러 대를 체크하면 `저장파일_카메라이름.mp4`로 카메라마다 따로 저장됩니다. 체크하지 않으면 화면에 보이는 그대로 한 영상으로 찍습니다.
3. **결과 영상**: 저장할 파일, FPS, 길이(초 / 프레임 / 중지할 때까지), 코덱, 화질을 정합니다. ffmpeg.exe가 캠코더 옆이나 PATH에 없으면 직접 골라 줍니다.
4. **● 녹화 시작**을 누르면 진행률, 배속, 기록이 실시간으로 보입니다. **■ 중지**를 누르면 지금까지 찍은 프레임으로 영상을 정상 마무리합니다. (한 번 더 누르면 강제 종료)
5. 끝나면 **영상 열기**, **저장 폴더 열기**로 바로 확인합니다.

창은 같은 폴더의 `UnityCamcorder.exe`, `plugin/`, `bepinex/`를 쓰므로 zip을 푼 폴더 구성을 그대로 두세요.
마지막 설정은 `%LOCALAPPDATA%\UnityCamcorder\gui.json`에 저장되어 다음에 그대로 열립니다.

### 명령줄로 쓰기

1. 설정 파일 만들기:
   ```
   UnityCamcorder.exe --init
   ```
   생성된 `camcorder.json`에서 `TargetExe`(대상 앱 경로)와 `OutputPath`(저장 경로)를 채웁니다.

2. 녹화 (앱 폴더에 플러그인이 없으면 자동으로 설치한 뒤 앱을 실행):
   ```
   UnityCamcorder.exe
   ```

설정 파일 없이 명령줄만으로도 쓸 수 있습니다:
```
UnityCamcorder.exe --target "C:\Games\App\App.exe" --out "C:\rec\out.mp4" --fps 60 --duration 10
```
이미 떠 있는 앱에 붙기 (플러그인이 설치된 상태로 켜진 앱이어야 함):
```
UnityCamcorder.exe --process App --out out.mp4 --frames 600
```
플러그인만 설치 / 제거:
```
UnityCamcorder.exe --install --target "C:\Games\App\App.exe"
UnityCamcorder.exe --uninstall --target "C:\Games\App\App.exe"
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
| `AutoInstallPlugin` | — | 앱을 실행해서 녹화할 때 BepInEx/플러그인이 없거나 다른 버전이면 자동 설치 (기본 켜짐) |
| `PluginConnectTimeoutMs` | — | 앱 안의 플러그인이 연결될 때까지 기다리는 시간 (기본 90초, 앱 로딩 포함) |

녹화 중 `Ctrl+C`를 누르거나 표준 입력으로 `stop` 한 줄을 보내면, 지금까지의 프레임으로 영상을 정상 마무리합니다. (`Ctrl+C`를 한 번 더 누르면 바로 종료)

### 촬영 카메라 고르기 (명령줄)
```
UnityCamcorder.exe --target "C:\Games\App\App.exe" --list-cameras
UnityCamcorder.exe --pid 12345 --cameras "Main Camera,MinimapCamera" --out "C:\rec\out.mp4" --duration 10
```
`--list-cameras`는 카메라 목록(이름, `#번호`, 크기)만 보여 주고 앱은 켜 둡니다. `--cameras`에는 이름이나 `#번호`를 쉼표로 적습니다.
`#번호`는 앱이 켜져 있는 동안만 유효하고, 앱을 다시 켜면 이름으로 고르세요.
설정 파일에서는 `"Cameras": ["Main Camera", "MinimapCamera"]` 로 적습니다. 결과는 `out_Main Camera.mp4`, `out_MinimapCamera.mp4`처럼 저장됩니다.

## 안정성 (대상이 꺼지거나 멈출 때)

- 캠코더는 프레임을 받는 즉시 ffmpeg로 흘려보내므로, 어느 시점에 중단되어도 그때까지의 영상이 **재생 가능한 완성본**으로 저장됩니다.
- 대상 프로세스가 **종료**되면 즉시 감지해 남은 프레임을 쓰고 영상을 닫습니다.
- `FreezeTimeoutMs` 동안 새 프레임이 없으면 **멈춤**으로 보고 영상을 마무리합니다.
- 캠코더가 먼저 죽으면, 플러그인이 이를 감지(캠코더 PID 감시)해 `captureFramerate`, 카메라 등 바꿔 둔 설정을 **원래대로 되돌립니다**. 대상 앱이 느려진 채로 남지 않습니다.
- 플러그인 안에서 예상하지 못한 오류가 나면 앱은 그대로 두고 플러그인만 멈춥니다.

## 빌드

```
dotnet build   Camcorder/Plugin/UnityCamcorderPlugin.csproj -c Release      # 플러그인 (.NET 3.5, Unity 2018.1.9 참조)
dotnet publish Camcorder/Controller/UnityCamcorder.csproj -c Release -o dist # 캠코더 (.NET 8 단일 파일)
dotnet publish Camcorder/Gui/UnityCamcorderGui.csproj -c Release -o dist     # 창 프로그램 (.NET Framework 4.8)
```

플러그인은 컴파일에만 BepInEx와 유니티 참조 DLL(BepInEx NuGet 피드의 `BepInEx.Core`, `UnityEngine.Modules`)을 쓰고, 실제로는 앱 폴더의 것을 씁니다.
배포용은 지원하는 가장 오래된 Unity 2018.1.9 참조로 빌드하고, 버전마다 자리가 바뀐 API(`AsyncGPUReadback`, URP 카메라 데이터)는 리플렉션으로 찾습니다.
다른 유니티 버전 참조로 컴파일해 보려면:
```
dotnet build Camcorder/Plugin/UnityCamcorderPlugin.csproj -p:UnityVersion=2021.3.45
dotnet build Camcorder/Plugin/UnityCamcorderPlugin.csproj -p:UnityApiCheck=true -p:UnityVersion=6000.6.6   # 2022.3 이후
```

GitHub Actions(`.github/workflows/camcorder.yml`)가 플러그인을 Unity 2018.4 · 2019.4 · 2020.3 · 2021.3 · 2022.3 · 6000.0 · 6000.6 참조로도 컴파일해
쓰는 API가 모든 버전에 있는지 확인하고, BepInEx 공식 배포본을 받아 해시를 확인한 뒤 배포용 zip을 만듭니다.
저장소 시크릿에 코드 서명 인증서(`SIGN_PFX_BASE64`, `SIGN_PFX_PASSWORD`, 선택 `SIGN_TIMESTAMP_URL`)가 있으면 우리 exe/dll에 자동으로 서명합니다.

## 백신 오탐에 대해

예전 버전은 자체 주입기(`Inject.exe`)로 앱에 DLL을 넣었고, 악성코드도 흔히 쓰는 방식이라 일부 백신이 ML/휴리스틱으로 오탐했습니다.
지금은 주입기 없이 BepInEx(수많은 게임 모드가 쓰는 공개 프레임워크)가 앱 시작 때 플러그인을 불러오므로 그 원인이 사라졌습니다.
BepInEx의 로더(`winhttp.dll`)는 공식 배포본을 해시까지 확인해 그대로 넣습니다. 혹시 백신이 앱 폴더의 `winhttp.dll`을 지우면 플러그인이 로드되지 않으니,
그 앱 폴더를 백신 예외에 추가하세요.

## 문제 해결

- **"BepInEx가 이 앱 안에서 실행되지 않았습니다"**: 플러그인을 설치하기 전부터 켜져 있던 앱이면 앱을 다시 시작하세요.
  그래도 같으면 앱 폴더에 `winhttp.dll`이 남아 있는지(백신이 지우지 않았는지) 확인하세요.
- **"앱 안의 캠코더 플러그인이 응답하지 않습니다"**: 창이 비활성일 때 멈추는 앱이면 앱 창을 한 번 눌러 주세요.
  캠코더가 한 번 연결된 뒤로는 앱이 꺼질 때까지 창이 비활성이어도 멈추지 않게 합니다.
- **"다른 버전입니다"**: 플러그인은 앱이 꺼질 때까지 바뀌지 않습니다. 캠코더를 새 버전으로 바꿨다면 앱을 완전히 종료하고 다시 녹화하세요(녹화 전에 플러그인을 자동으로 바꿉니다).
- **배속이 잘 안 나옴**: 녹화 중 1초마다 나오는 진행 줄과 녹화가 끝날 때 나오는 **속도 요약**을 보세요.
  `초당 85프레임 (1.4배속) · 앱 대기: 인코딩 62%, GPU 읽기 3%, 복사 4% · ffmpeg 쓰기 97%`처럼 앱이 무엇을 기다렸는지 나옵니다.
  - **인코딩**이 크면 ffmpeg가 못 따라오는 것입니다. 앱 창 크기(= 영상 크기)나 촬영 카메라 수를 줄이고, 인코더가 GPU(NVENC/QSV/AMF)인지 확인하세요.
  - **GPU 읽기**가 크면 GPU에서 프레임을 읽어 오길 기다린 것입니다. 영상 크기를 줄이면 나아집니다.
  - 셋 다 작으면 앱이 한 프레임을 만드는 시간(게임 로직·렌더링) 자체가 한계입니다. 이 PC에서 그 앱이 낼 수 있는 속도에 가깝습니다.
  - 앱이 녹화 중에 프레임 제한이나 수직 동기화를 다시 걸면 플러그인이 매번 다시 풀고, 요약에 그 횟수를 알려 줍니다.
  - 배속은 Unity 버전과 상관없습니다. 무거운 작업(인코딩)은 앱 밖의 캠코더가 하므로 Unity 2018.1 같은 오래된 버전도 같은 방식으로 동작합니다.
- 자세한 기록은 앱 폴더의 `BepInEx\LogOutput.log`에 있습니다. `[Info   :UnityCamcorder]` 줄을 보면 플러그인이 무엇을 했는지 알 수 있습니다.

## 알려진 한계

- **IL2CPP 빌드**: BepInEx 5는 Mono 빌드만 지원하므로 녹화할 수 없습니다.
- **Screen Space - Overlay UI**: 이 방식의 캔버스는 카메라가 아니라 화면에 직접 그려지므로, 원본 프로젝트와 마찬가지로 카메라 촬영 영상에는 들어가지 않습니다.
  UI까지 찍으려면 앱에서 캔버스를 Screen Space - Camera로 두세요.
- **URP 오버레이 카메라**는 혼자 고를 수 없습니다. 그 카메라를 쌓아 둔 베이스 카메라를 고르세요.
- **HDR**: 8비트 RGBA로 기록합니다.
- **오디오**: 현재 영상만 녹화합니다.
- BepInEx 6이나 MelonLoader가 이미 설치된 앱에는 자동으로 설치하지 않습니다.
- 본 도구는 사용자가 소유·제어하는 앱 녹화를 위한 것입니다.
