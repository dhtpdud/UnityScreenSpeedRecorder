# UnityScreenSpeedRecorder
해당 프로젝트는 Unity 내부 카메라들의 영상을 비디오 파일로 저장하는 솔루션을 제공합니다.

## 코드 빨리가기
1. [PNG 인코딩 & 저장(Unitask, 멀티스레드) 코드](https://github.com/dhtpdud/UnityScreenRapidFire/blob/main/Assets/Scripts/Singleton/RecorderFlusher.cs)

## 절차
1. Time.Timescale을 이용하여 배속촬영을 진행합니다. 촬영된 프레임(RenderTexture)들은 VRAM에 캐싱됩니다.
2. VRAM에 캐싱되어있는 RenderTexture 프레임들을 Texture2D로 변환하여 RAM으로 캐싱합니다. 이로써 CPU의 다른 스레드에서 해당 프레임 데이터에 접근이 가능해집니다.
3. PNG 인코딩 & 저장 작업을 진행합니다. 해당 작업은 Unitask를 이용하여 병렬, 멀티스레드로 빠르게 처리합니다.
4. 저장된 PNG 파일들을 비디오 인코더를 사용하여 비디오 파일로 인코딩합니다.

## 영상
https://youtu.be/g2rJE-xJrpw

## Pipeline
![Pipeline](https://github.com/user-attachments/assets/264bb378-90e7-49fc-a4b3-0d5a4b3789a4)

## 종속성
1. Unitask
2. Newtonsoft.Json
3. ffmpeg(미포함)
