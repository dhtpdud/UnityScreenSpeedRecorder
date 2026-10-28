# 함께 배포하는 외부 소프트웨어

## BepInEx 5

- 파일: `bepinex/BepInEx_win_x64_<버전>.zip`, `bepinex/BepInEx_win_x86_<버전>.zip` (공식 GitHub 릴리스 원본 그대로, CI가 SHA-256 확인)
- 프로젝트: <https://github.com/BepInEx/BepInEx>
- 라이선스: GNU Lesser General Public License v2.1 (`bepinex/LICENSE-BepInEx.txt`)
- 소스 코드: <https://github.com/BepInEx/BepInEx/tree/v5-lts> 및 각 릴리스 태그

캠코더는 BepInEx를 수정하지 않고, 사용자가 고른 앱 폴더에 그대로 풀어 넣습니다.
BepInEx 안에는 HarmonyX, MonoMod, Mono.Cecil(MIT), Unity Doorstop(LGPL-2.1)이 포함되어 있으며 각 라이선스를 따릅니다.
