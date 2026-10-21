# VirtualMonitor

**Windows 가상 모니터를 관리하고, 가상 화면을 별도의 창으로 확인하는 데스크톱 앱입니다.**

MttVDD 가상 디스플레이 드라이버를 기반으로 모니터 추가·삭제, 개별 켜기·끄기, 해상도·주사율 변경을 제공합니다. 물리 모니터에 직접 표시되지 않는 가상 화면은 실시간 미리보기로 확인할 수 있습니다.

## 주요 기능

| 기능 | 설명 |
| --- | --- |
| 가상 모니터 관리 | 최대 16개 추가, 선택 모니터 삭제, 모두 삭제 |
| 개별 켜기·끄기 | 선택한 가상 모니터의 활성 상태 변경 |
| 화면 구성 | Windows 확장 모드 적용, 활성 디스플레이 목록 확인 |
| 이름 지정 | 모니터별 별칭 저장 및 앱 화면에 표시 |
| 화면 모드 변경 | 켜진 모니터의 지원 해상도·주사율 선택 |
| 실시간 미리보기 | DirectX 출력, 전체 화면, 항상 위, 간결 모드, 원본 크기 보기 |
| 트레이 실행 | 메인 창을 숨긴 상태에서도 미리보기 유지 |
| 설정 보호·진단 | 파일 교체·백업, 변경 실패 시 복구 시도, 로그 및 회귀 검증 |

## 실행 환경

- Windows 11 이상, Intel/AMD **x64** PC — 현재 배포 설치 스크립트 기준
- 설치된 **MttVDD 가상 디스플레이 드라이버**와 `vdd_settings.xml`
- 관리자 권한 — 앱 실행 시 UAC 권한 요청
- 프레임워크 종속 실행 파일을 사용하는 경우 **.NET 10 Desktop Runtime (x64)**

모니터 추가·삭제는 MttVDD 어댑터가 하나 설치된 구성을 지원합니다. 앱을 빌드하는 것만으로 드라이버가 설치되지는 않습니다.

## 설치 및 시작

### 배포 패키지를 사용하는 경우

`Install.bat`, `Install.ps1`, `App`, `Driver`가 함께 포함된 배포 패키지 기준입니다.

1. ZIP 전체를 같은 폴더에 압축 해제합니다.
2. `Install.bat`을 실행하고 관리자 권한 요청을 승인합니다.
3. 설치 중 게시자 인증서 등록 안내가 나오면 내용을 확인하고 진행합니다.
4. 재시작 안내가 나오면 Windows를 재시작합니다.
5. `App\VirtualMonitor.exe`를 실행합니다.

설치 스크립트는 기존 MttVDD 장치를 재사용하고, 기존 설정 파일은 덮어쓰지 않습니다. 게시자 인증서가 없으면 확인 후 Windows의 **신뢰할 수 있는 게시자** 저장소에 등록합니다. 테스트 서명 모드와 보안 부팅 설정은 변경하지 않습니다.

> 저장소의 `packaging` 폴더에는 설치 스크립트가 있습니다. 해당 폴더만으로 설치할 수는 없으며, 스크립트가 요구하는 앱과 드라이버 파일이 함께 있어야 합니다.

### 소스에서 실행하는 경우

아래 [소스 빌드](#소스-빌드)를 진행한 뒤 출력 폴더의 `VirtualMonitor.exe`를 실행합니다. MttVDD 드라이버와 설정 파일은 별도로 준비해야 합니다.

## 사용 방법

### 1. 가상 모니터 켜기

1. 메인 화면의 콤보박스에서 사용할 가상 모니터를 선택합니다.
2. **켜기**를 누릅니다.
3. 활성 디스플레이 목록에서 상태를 확인합니다.
4. 화면이 활성화되지 않으면 **확장 모드 적용**을 누릅니다.

설정된 모니터가 없으면 **가상 모니터 관리 → 모니터 1개 추가**로 먼저 추가합니다. 목록을 다시 확인하려면 **새로 고침**을 누릅니다.

### 2. 가상 화면 보기

켜진 가상 모니터를 선택하고 **화면 보기**를 누릅니다. 가상 화면에 앱 창을 배치하면 미리보기에서 해당 화면을 확인할 수 있습니다. Windows의 `Win + Shift + ←/→` 단축키로 현재 창을 다른 모니터로 이동할 수도 있습니다.

| 조작 | 동작 |
| --- | --- |
| `F11` / 전체 화면 버튼 | 전체 화면 전환 |
| `Esc` | 전체 화면 해제 |
| 항상 위 | 미리보기 창을 다른 일반 창보다 앞에 유지 |
| 간결 모드 / 기본 모드 | 미리보기 창의 표시 방식 전환 |
| 원본 크기 (1:1) | 원본 픽셀 크기로 보기 |

같은 모니터의 미리보기는 중복으로 열리지 않습니다. 기본 출력은 DirectX이며, 출력 실패 시 해당 창은 WPF 비트맵 출력으로 전환합니다. 실제 갱신 속도는 화면 변화, Windows 캡처 처리 및 실행 환경에 따라 달라집니다.

### 3. 모니터 개수·이름·화면 모드 변경

메인 화면에서 **가상 모니터 관리**를 엽니다.

- **모니터 1개 추가:** 가상 모니터를 추가합니다. 앱에서 최대 16개까지 지원합니다.
- **선택 모니터 삭제 / 모두 삭제:** 가상 모니터 구성을 제거합니다. 실제 모니터는 삭제하지 않습니다.
- **이름 저장:** 선택한 모니터의 앱 내 별칭을 저장합니다. Windows 장치명은 바뀌지 않습니다.
- **화면 모드 적용:** 켜진 모니터에서 목록에 표시되는 해상도·주사율을 선택해 적용합니다.

모니터 개수 변경 시 드라이버가 재시작될 수 있습니다. 모니터를 끄거나 구성을 변경하면 Windows가 열린 창의 위치를 바꿀 수 있습니다.

### 4. 끄기 및 앱 종료

- 모니터 사용을 잠시 중단하려면 해당 모니터를 선택하고 **끄기**를 누릅니다. 다시 **켜기**로 사용할 수 있습니다.
- 메인 창을 최소화하거나 `X`를 누르면 트레이로 숨겨집니다. 열린 미리보기 창은 독립적으로 유지됩니다.
- 트레이 아이콘을 더블클릭하면 메인 화면을 다시 엽니다.
- 앱을 완전히 종료하려면 **트레이 아이콘 우클릭 → 종료**를 선택합니다.

앱 종료와 가상 모니터 끄기는 별도 동작입니다. 모니터를 끄려면 종료 전에 **끄기**를 사용하세요.

## 소스 빌드

### 필요한 도구

- .NET 10 SDK
- Visual Studio 또는 Build Tools의 **C++를 사용한 데스크톱 개발** 워크로드
- C++ x64 빌드 도구와 Windows SDK

앱은 C# / WPF와 C++ 네이티브 DLL을 함께 사용합니다. 프로젝트 빌드 시 `build-native.cmd`가 캡처·렌더링 DLL을 자동으로 빌드합니다. 앱 빌드에는 `Driver` 폴더의 드라이버 프로젝트 빌드가 포함되지 않습니다.

프로젝트 루트에서 PowerShell로 실행합니다.

```powershell
# 최초 빌드 전 복원 (환경에 따라 패키지 소스 접속 필요)
dotnet restore .\VirtualMonitor.csproj

# Release 빌드
.\build.ps1 -Configuration Release
```

출력 위치:

```text
bin/Release/net10.0-windows/
├── VirtualMonitor.exe
├── VirtualMonitor.dll
├── VirtualMonitor.Capture.dll
├── VirtualMonitor.Preview.dll
└── build-manifest.json
```

위 목록은 주요 파일입니다. 실행·배포 시에는 필요한 런타임 설정 파일 등을 포함한 출력 폴더를 함께 사용하세요. 캡처 DLL과 렌더링 DLL은 같은 소스에서 빌드한 결과를 함께 사용해야 합니다.

`build.ps1`은 `--no-restore`로 빌드하며, 주요 DLL의 SHA-256을 `build-manifest.json`에 기록합니다.

## 설정 저장 및 진단

### 저장 위치

| 위치 | 내용 |
| --- | --- |
| `C:\VirtualDisplayDriver\vdd_settings.xml` | 드라이버 설정 및 모니터 식별 정보 |
| `C:\IddSampleDriver\vdd_settings.xml` | 함께 확인하는 드라이버 설정 경로 |
| `%LOCALAPPDATA%\VirtualMonitor\monitor-names.json` | 모니터 별칭 |
| `%LOCALAPPDATA%\VirtualMonitor\Recovery\` | 변경 전 상태와 복구 진행 상태 |
| `%LOCALAPPDATA%\VirtualMonitor\Logs\` | 날짜별 동작·오류 로그 (`.jsonl`) |

두 XML 파일이 모두 존재하면 모니터 개수와 식별 정보가 일치해야 합니다. 설정 저장에는 `AtomicFile`을 사용하여 임시 파일 작성 후 기존 파일을 교체하고 `.bak` 백업을 남깁니다.

구성 변경 실패 시 변경 전 상태로 복구를 시도합니다. 복구가 완료되지 않은 기록이 있으면 다음 실행 시 안내합니다. 이 안내는 복구 완료를 의미하지 않으므로 현재 모니터 상태와 로그를 확인해야 합니다.

### 회귀 검증

```powershell
# 빌드 후 아키텍처 진단 실행
.\build.ps1 -Configuration Release -Verify

# 이미 빌드된 결과에서 진단만 실행
dotnet .\bin\Release\net10.0-windows\VirtualMonitor.dll --diagnose-architecture
```

아키텍처 진단은 모니터 식별 정보 유지, 저장 실패 시 원본 보존, 백업 생성, 구성 변경 직렬화와 복구 순서, 캡처 수명, 네이티브 렌더링 등을 검사합니다. 실제 디스플레이 구성을 변경하지 않는 진단이며, 결과 파일은 실행 파일 위치의 `DiagnosticArtifacts` 아래에 생성됩니다.

실제 장치의 장시간 동작이나 모든 GPU 환경에서의 성능을 보장하는 검사는 아닙니다. 캡처 경로와 측정 도구는 [NativePreview 문서](NativePreview/README.md)를 참고하세요.

## 문제 해결

| 증상 | 확인할 내용 |
| --- | --- |
| 설치된 MttVDD 장치가 없다고 표시됨 | 드라이버 설치 여부와 Windows 장치 관리자 상태 확인 |
| 설정된 가상 모니터가 없음 | 관리 창에서 모니터 추가 |
| 설정 파일을 찾을 수 없음 | 위 XML 경로와 드라이버 설치 상태 확인 |
| 두 설정 파일의 개수·식별자가 다르다는 오류 | 두 XML 파일의 설정 일치 여부 확인 |
| 켰지만 화면이 활성화되지 않음 | 확장 모드 적용 후 새로 고침, Windows 디스플레이 설정 확인 |
| 미리보기 출력 문제 | 아래 WPF 출력 옵션으로 실행해 비교 |
| 구성 복구 안내가 표시됨 | 현재 모니터 구성, `Recovery` 기록과 `Logs` 확인 |
| 창을 닫아도 앱이 실행 중임 | 트레이 아이콘 우클릭 → 종료 |

WPF 비트맵 출력으로 실행하려면:

```powershell
.\bin\Release\net10.0-windows\VirtualMonitor.exe --wpf-preview
```

## 기술 구성

| 영역 | 기술 및 구현 |
| --- | --- |
| UI | C# · .NET 10 · WPF · MVVM |
| 장치·디스플레이 제어 | Win32 API · SetupAPI · Configuration Manager |
| 비동기 처리 | `Task.Run`, `async/await`, `SemaphoreSlim`을 통한 구성 변경 직렬화 |
| 화면 캡처 | C++ · Windows Graphics Capture |
| 미리보기 출력 | Direct3D 11 · DXGI · WPF `HwndHost` |
| 설정·복구 | XML · JSON · 임시 파일 교체 및 백업 |
| 진단 | JSONL 로그 · 시나리오 기반 회귀 검증 |

기본 미리보기는 캡처와 렌더링이 Direct3D 장치를 공유하고 GPU 텍스처를 전달합니다. 자세한 출력 경로는 [NativePreview/README.md](NativePreview/README.md)에 설명되어 있습니다.

```text
VirtualMonitor/
├── View/              # WPF 창과 미리보기 UI
├── ViewModels/        # 화면 상태와 사용자 명령
├── Models/            # 모니터·디스플레이 모델
├── Services/          # 장치 제어, 구성 변경, 설정 및 복구
├── Infrastructure/    # 서비스 조립, 파일 저장, 로그
├── NativeCapture/     # Windows Graphics Capture 네이티브 DLL
├── NativePreview/     # DirectX 렌더링 네이티브 DLL
├── Diagnostics/       # 회귀 진단 및 캡처 측정 도구
├── Driver/            # IddSample 기반 드라이버 소스 및 참고 자료
├── packaging/         # 배포용 설치 스크립트 및 MttVDD 라이선스
├── build-native.cmd   # 네이티브 DLL 빌드
└── build.ps1          # 앱 빌드, 해시 기록 및 선택적 진단
```

## 외부 구성 요소

실행 시 사용하는 MttVDD 드라이버의 라이선스는 [packaging/MttVDD-LICENSE.txt](packaging/MttVDD-LICENSE.txt)를 참고하세요. `Driver` 폴더의 샘플 기반 소스에 관한 설명과 라이선스는 [Driver/UPSTREAM.md](Driver/UPSTREAM.md), [Driver/LICENSE](Driver/LICENSE)에 있습니다.
