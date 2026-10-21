# DirectX 미리보기 출력 (0.2.0)

기본 경로는 WPF HwndHost의 자식 HWND를 사용합니다. 렌더링은 캡처 전용 스레드에서 실행합니다.

- NativeCapture가 Windows Graphics Capture의 최신 프레임을 같은 장치의 shader-resource 텍스처로 GPU 복사합니다.
- PreviewCreateOnDevice가 캡처 장치를 공유해 스왑 체인을 만듭니다.
- PreviewDrawTexture가 텍스처를 렌더링합니다. CPU readback과 재업로드가 없습니다.
- WPF 크기 변경과 WM_PAINT는 재출력 요청만 게시합니다.
- DirectX 출력 실패 시 해당 창은 WPF 비트맵 출력으로 전환합니다.
- --wpf-preview는 처음부터 CPU 비트맵 경로를 선택합니다.
- NativePreview와 NativeCapture의 DLL은 같은 소스 빌드 결과를 함께 배포해야 합니다.

프로젝트 루트에서 dotnet build를 실행하면 변경된 네이티브 소스를 자동 빌드합니다.
공통 build-native.cmd는 vswhere로 Visual Studio C++ 도구를 찾습니다.
build.ps1은 오프라인 빌드 후 DLL의 SHA-256 manifest를 기록합니다.

--diagnose-architecture는 숨겨진 WARP 창에서 공유 장치 텍스처, 셰이더 결과 픽셀, 크기 변경을 검사합니다.
이는 실제 가상 모니터의 장시간 캡처나 모든 GPU 조합의 성능 검사를 대신하지 않습니다.

현재 전체 구조와 검증 범위: [ARCHITECTURE.md](../ARCHITECTURE.md).
