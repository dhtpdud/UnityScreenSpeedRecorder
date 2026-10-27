# UnityScreenSpeedRecorder
해당 프로젝트는 Unity 내부 카메라들의 영상을 비디오 파일로 저장하는 솔루션을 제공합니다.

![image](https://github.com/user-attachments/assets/6252cec6-4868-49d9-8a04-a64dd52234e3)


`Camera`                = 촬영할 카메라  
`captureFrameCount`     = 목표 캡처 프레임 수  
`saveDirPath`           = 저장 경로  
`outputImageExtension`  = 프레임 이미지 확장자  
`canvas`                = 로그용 캔버스 (envRecorder.json의 IsDebug를 통해 On/Off)

## 코드 빨리가기
1. [GPU 비동기 읽기 & PNG 인코딩 & 저장(AsyncGPUReadback, 멀티스레드) 코드](https://github.com/dhtpdud/UnityScreenRapidFire/blob/main/Assets/Scripts/Singleton/RecorderFlusher.cs)

## 절차
1. Time.Timescale을 이용하여 배속촬영을 진행합니다. 카메라는 녹화기마다 하나씩 있는 RenderTexture에 렌더링됩니다.
2. 매 프레임 렌더링 결과를 AsyncGPUReadback으로 RAM에 비동기로 읽어 옵니다. GPU를 멈춰 세우지 않으며, VRAM 사용량도 프레임 수와 관계없이 일정합니다.
3. 읽어 온 픽셀 데이터는 워커 스레드들이 PNG로 인코딩하여 저장합니다. 촬영과 저장은 동시에 진행되며, 메모리에 올라간 프레임 수가 `FrameChunkSize`에 도달하면 저장이 따라올 때까지 촬영을 잠시 멈춥니다. (멈춘 동안에는 게임 시간도 흐르지 않으므로 프레임이 누락되지 않습니다.)
4. 저장된 PNG 파일들을 ffmpeg로 영상 인코딩합니다. 실행 폴더의 ffmpeg를 우선 사용하고, 없으면 PATH에 등록된 ffmpeg를 사용합니다.

## 영상
https://youtu.be/g2rJE-xJrpw

## Pipeline
![Pipeline](https://github.com/user-attachments/assets/264bb378-90e7-49fc-a4b3-0d5a4b3789a4)

## 종속성
1. Unitask
2. Newtonsoft.Json
3. ffmpeg(미포함)
