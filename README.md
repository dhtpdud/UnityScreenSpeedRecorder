# UnityScreenRapidFire
해당 프로젝트는 Unity 내부 카메라들의 영상을 비디오 파일로 저장하는 솔루션을 제공한다.

Time.Timescale을 이용하여 배속촬영을 하고,  
Unitask를 이용하여 png 인코딩 작업을 병렬, 멀티스레드 처리한다.  
때문에 녹화절차를 보다 빠르게 마무리 할 수 있다.

## 빨리가기
1. [PNG 인코딩 & 저장(Unitask, 멀티스레드)](https://github.com/dhtpdud/UnityScreenRapidFire/blob/main/Assets/Scripts/Singleton/RecorderFlusher.cs)

## 영상
https://youtu.be/GyUiG9uafMc

## 원리
![Flow](https://github.com/user-attachments/assets/5131fa3b-03e4-4e85-8cb2-a0227632202e)

## 종속성
1. Unitask
2. Newtonsoft.Json
3. ffmpeg(선택, 미포함)
