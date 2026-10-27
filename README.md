# UnityScreenSpeedRecorder
해당 프로젝트는 Unity 내부 카메라들의 영상을 비디오 파일로 저장하는 솔루션을 제공합니다.

![image](https://github.com/user-attachments/assets/6252cec6-4868-49d9-8a04-a64dd52234e3)


`Camera`                = 촬영할 카메라  
`captureFrameCount`     = 목표 캡처 프레임 수  
`saveDirPath`           = 저장 경로  
`outputVideoFileName`   = 저장될 영상 파일 이름 (기본값 `result.mp4`)  
`canvas`                = 로그용 캔버스 (envRecorder.json의 IsDebug를 통해 On/Off)

## 코드 빨리가기
1. [GPU 비동기 읽기 & ffmpeg 하드웨어 인코딩(AsyncGPUReadback, 멀티스레드) 코드](https://github.com/dhtpdud/UnityScreenRapidFire/blob/main/Assets/Scripts/Singleton/RecorderFlusher.cs)

## 절차
1. Time.Timescale을 이용하여 배속촬영을 진행합니다. 카메라는 녹화기마다 하나씩 있는 RenderTexture에 렌더링됩니다.
2. 매 프레임 렌더링 결과를 AsyncGPUReadback으로 RAM에 비동기로 읽어 옵니다. GPU를 멈춰 세우지 않으며, VRAM 사용량도 프레임 수와 관계없이 일정합니다.
3. 읽어 온 원본 프레임(RGBA)은 PNG로 저장하지 않고, 녹화기마다 실행한 ffmpeg의 표준 입력으로 바로 넘깁니다. ffmpeg는 촬영과 동시에 H.264(mp4)로 인코딩합니다.
4. 메모리에 올라간 프레임 수가 `FrameChunkSize`에 도달하면 ffmpeg가 따라올 때까지 촬영을 잠시 멈춥니다. (멈춘 동안에는 게임 시간도 흐르지 않으므로 프레임이 누락되지 않습니다.)

## 영상 인코더
`envRecorder.json`의 `VideoEncoder`로 정합니다.
- `auto` (기본값): 시작할 때 아래 순서로 짧은 테스트 인코딩을 해 보고, 처음 성공한 인코더를 사용합니다.
  1. `h264_nvenc` (NVIDIA GPU)
  2. `h264_qsv` (Intel GPU)
  3. `h264_amf` (AMD GPU)
  4. `libx264` (CPU, 하드웨어 인코더가 없을 때)
- 인코더 이름을 직접 적으면 테스트 없이 해당 인코더를 사용합니다. (예: `"h264_nvenc"`)

`FrameChunkSize`는 RAM에 동시에 올라갈 수 있는 최대 프레임 수입니다. (1080p 기준 프레임당 약 8MB)

## 영상
https://youtu.be/g2rJE-xJrpw

## Pipeline
![Pipeline](https://github.com/user-attachments/assets/264bb378-90e7-49fc-a4b3-0d5a4b3789a4)

## 종속성
1. Unitask
2. Newtonsoft.Json
3. ffmpeg 9.0.2 ([gyan.dev](https://www.gyan.dev/ffmpeg/builds/) essentials 빌드, GPL v3)
   - ffmpeg.exe가 100MB를 넘어 GitHub에 그대로 올릴 수 없으므로 `Assets/StreamingAssets/ffmpeg/ffmpeg.zip`으로 포함합니다. 처음 실행할 때 `Application.persistentDataPath/ffmpeg/ffmpeg.exe`로 한 번 압축을 풀어 사용합니다.
   - 포함된 ffmpeg → 실행 폴더의 ffmpeg.exe → PATH 순으로 찾습니다. Windows 이외의 플랫폼에서는 PATH의 ffmpeg를 사용합니다.
   - ffmpeg를 업데이트하려면 새 ffmpeg.exe 하나만 담은 zip으로 `ffmpeg.zip`을 교체하면 됩니다. (크기가 다르면 다시 압축을 풉니다.)
