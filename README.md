# UnityScreenRapidFire
해당 프로젝트는 Unity 내부 카메라들의 영상을 비디오 파일로 저장하는 솔루션을 제공한다.

## 절차
1. Time.Timescale을 이용하여 배속촬영을 한다. 촬영된 프레임(RenderTexture)들은 VRAM에 캐싱된다.
2. VRAM에 캐싱되어있는 RenderTexture 프레임들을 Texture2D로 변환, RAM으로 캐싱한다. 이로써 CPU에서 해당 프레임 데이터에 접근이 가능하다.
3. PNG 인코딩 & 저장 작업을 진행한다. 해당 작업은 Unitask를 이용하여 병렬, 멀티스레드로 빠르게 처리한다.
4. 저장된 PNG 파일들을 비디오 인코더를 사용하여 비디오 파일로 인코딩한다.

## 영상
https://youtu.be/g2rJE-xJrpw

## 원리
![Flow](https://github.com/user-attachments/assets/5131fa3b-03e4-4e85-8cb2-a0227632202e)

## 빨리가기
1. [PNG 인코딩 & 저장(Unitask, 멀티스레드)](https://github.com/dhtpdud/UnityScreenRapidFire/blob/main/Assets/Scripts/Singleton/RecorderFlusher.cs)

## 종속성
1. Unitask
2. Newtonsoft.Json
3. ffmpeg
