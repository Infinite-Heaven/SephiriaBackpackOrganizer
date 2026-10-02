# macOS 설치 안내

🌏 **언어 / Language / 语言**: [한국어](README.ko.md) · [English](README.en.md) · [中文](README.md)

macOS 네이티브 버전 *Sephiria*에 배낭 정리 플러그인을 설치하는 방법입니다.

플러그인 본체와 BepInEx는 모두 Releases의 공식 원본 파일 그대로 사용하며, **단 1바이트도 수정하지 않았습니다**.
macOS에서만 로더를 교체했습니다. Windows는 `winhttp.dll` + `doorstop_config.ini`로 인젝션하지만,
macOS는 `DYLD_INSERT_LIBRARIES`와 dylib 하나에 의존합니다.

macOS 26.5.2 / Apple M2 Max 및 macOS 27.0 / Apple M1 Pro / Sephiria 1.0.33(Unity 6000.3.21f1) + 플러그인 v2.5.4 환경에서 테스트를 통과했습니다.

메인 README: [한국어](../docs/README.ko.md) · [English](../docs/README.en.md) · [中文](../docs/README.md)

## 설치

```sh
cd macos
./install.sh
```

스크립트가 자동으로 처리합니다: Steam 내 게임 디렉터리 탐색(다른 Steam 라이브러리에 설치된 경우 포함),
필요 시 Rosetta 2 설치, Releases에서 최신 전체 패키지 다운로드, 인젝션 라이브러리 현장 컴파일, 게임에 설치.

전체 패키지를 이미 수동으로 다운로드했다면 직접 지정해서 다운로드를 생략할 수 있습니다:

```sh
./install.sh --zip ~/Downloads/SephiriaBackpackOrganizer-v2.5.2.zip
```

인젝션 라이브러리 컴파일에는 Xcode 명령줄 도구(`xcode-select --install`)가 필요합니다.

## 실행

Steam 라이브러리 → Sephiria 우클릭 → 속성 → 일반 → 실행 옵션에 아래를 붙여넣으세요(경로는 자신의 환경에 맞게 변경):

```
"/Users/사용자이름/Library/Application Support/Steam/steamapps/common/Sephiria/run_bepinex.sh" %command%
```

이후에는 평소대로 「게임 시작」을 누르면 됩니다. 설치 스크립트가 마지막에 이 한 줄 전체를 그대로 출력해 줍니다.

## 사용법

**fn + F8**을 눌러 배낭을 정리합니다. 신기를 마우스 가운데 버튼으로 클릭하면 수동 우선순위를 지정합니다(P1→P2→P3→P4 순서로 순환, 한 번 더 누르면 취소, 아이템마다 독립적).

> macOS에서 F8은 기본적으로 「재생/일시정지」 미디어 키라서 시스템이 먼저 가로채고 게임까지 전달되지 않으므로 fn이 필요합니다.
> F8을 직접 누르고 싶다면: 시스템 설정 → 키보드 → 키보드 단축키 → 기능 키 →
> 「F1, F2 등의 키를 표준 기능 키로 사용」을 켜세요.
> 키 변경도 가능합니다: `BepInEx/config/com.sephiria.backpack-organizer.cfg`의
> `[General] Hotkey`를 편집한 뒤 게임을 재시작하세요.

그 외 설정 항목은 Windows 버전과 완전히 동일하며, [메인 README](../docs/README.ko.md)를 참고하세요.

문제가 생기면 먼저 `게임 디렉터리/BepInEx/LogOutput.log`를 확인하세요. BepInEx 자체가 실행되지 않았다면
실행 로그 `게임 디렉터리/BepInEx/run_bepinex.log`(스크립트 실행과 인젝션 과정 기록)
또는 `Sephiria.app/Contents/MacOS/preloader_<타임스탬프>.log`를 확인하세요.

## 제거

```sh
./uninstall.sh
```

## 파일 설명

| 파일 | 역할 |
| --- | --- |
| `install.sh` / `uninstall.sh` | 설치 / 제거 |
| `doorstop_shim.c` | macOS용 인젝션 라이브러리 소스, 설치 시 현장 컴파일 |
| `run_bepinex.sh` | 실행 스크립트(Rosetta 전환 + DYLD 처리) |

---

## 별도의 doorstop을 작성해야 했던 이유

공식 UnityDoorstop의 macOS 버전은 이 게임에서 동작하지 않으며, 파일 교체로는 우회할 수 없는 세 가지 연쇄 함정을 밟습니다. 나중에 다시 조사하지 않도록 여기에 기록합니다.

**1. UnityDoorstop 4.5.0은 Unity 6에서 바로 abort됩니다**

plthook이 Mach-O의 `LC_DYLD_CHAINED_FIXUPS`를 수동으로 파싱하는데, Unity 6 /
macOS 26으로 빌드된 바이너리를 만나면 `unknown imports format 0` 오류를 냅니다.

`doorstop_shim.c`는 Mach-O를 전혀 파싱하지 않고, dyld 네이티브 `__interpose`로
Unity 플레이어의 `dlsym("mono_jit_init_version")` 조회를 가로챕니다. 이는 dyld 자체가 바인딩 시점에
수행하는 동작이라 Mach-O의 구체적인 포맷과 무관합니다. 동일한 `DOORSTOP_*` 환경 변수를 읽고 동일한
매니지드 측 변수를 내보내므로 `BepInEx.Unity.Mono.Preloader.dll`은 수정할 필요가 없습니다.

**2. `System.Native`을 찾지 못해 게임 본체까지 함께 죽습니다**

BepInEx의 `DoorstopEntrypoint.Start()` 첫 줄은 `DateTime.Now`입니다. Unix에서는
이 경로가 `TimeZoneInfo` → `File.Exists` → `Interop.Sys`로 이어지며, 정적 생성자가
`System.Native`으로 P/Invoke해야 합니다.

문제는 Unity가 `mono_jit_init_version`이 반환된 **이후**에야 `mono_config_parse`를 호출해
dllmap(`System.Native` → `libmono-native.dylib`)을 장착한다는 점입니다. 진입점이 너무 일찍 실행되어 조회는 반드시
실패합니다. 그리고 **실패한 정적 생성자는 CLR에 영구적으로 캐시됩니다**. 이후 게임 자체의 모든
`File.Exists` 호출이 `TypeInitializationException`을 다시 던지며, 오프닝 애니메이션에서 멈춰 세이브조차 읽지 못합니다.
Windows에서는 `System.IO`가 Win32를 직접 호출하므로 절대 발생하지 않습니다.

shim은 이제 매니지드 진입점을 실행하기 전에 절대 경로로 해당 dllmap을 직접 등록합니다.

**3. 도메인 설정이 없어 `Trace` 초기화에서 터집니다**

Unity는 `AppDomain.CurrentDomain.SetupInformation.ConfigurationFile`을 설정하지 않으므로,
BepInEx가 `TraceLogSource.CreateSource()`에서 `System.Diagnostics.Trace`를 건드리면
`ConfigurationManager`가
`ConfigurationErrorsException: The 'ExeConfigFilename' argument cannot be null.`을 던집니다.
shim은 공식 doorstop의 방식을 따라 `mono_domain_set_config`를 보완했습니다.

## 반드시 Rosetta에서 실행해야 하는 이유

BepInEx 6.0.0-be.697에 포함된 MonoMod 22.5.1.1의 detour 엔진은 x86/x64용
트램펄린 코드만 생성합니다. 네이티브 arm64에서는 `DetourHelper.Runtime`이 null이라 첫 Harmony 패치에서 바로
`IL Compile Error → NullReferenceException`이 발생해 플러그인이 하나도 로드되지 않습니다.

게임은 universal 바이너리이므로 `run_bepinex.sh`는 Apple Silicon에서
`arch -x86_64`로 자신을 다시 exec해 게임을 Rosetta 2에서 실행합니다. 상위 BepInEx가
arm64를 지원하는 MonoMod(v25+)로 교체되면 이 단계는 제거할 수 있습니다.

(디버그용: `DOORSTOP_NO_ROSETTA=1`을 설정하면 네이티브 arm64를 강제할 수 있지만, Harmony를 사용하는 플러그인은 동작하지 않습니다.)

## SIP를 끈 머신에서의 추가 함정

정상적인 경우 dyld는 Apple 자사 arm64e 시스템 바이너리에 대해 `DYLD_INSERT_LIBRARIES`를 무시합니다.
그러나 사용자가 SIP를 껐다면 dyld는 더 이상 무시하지 않습니다. 그런데 Steam 오버레이
`gameoverlayrenderer.dylib`에는 x86_64와 arm64만 있고 arm64e가 없습니다. 결과적으로
Steam 실행 옵션을 통해 실행하면, 스크립트에서 fork되는 모든 `uname` / `sysctl` / `arch`가
`missing compatible architecture ... need 'arm64e'`에서 abort되어 스크립트가 한 줄도 실행되지 못합니다.

따라서 `run_bepinex.sh`는 **맨 처음**에 `DYLD_INSERT_LIBRARIES`와 `DYLD_LIBRARY_PATH`를
`DOORSTOP_SAVED_*`에 저장하고 비운 뒤, 마지막에 게임을 exec하기 직전에 복원하며 그 사이에는 어떤
자식 프로세스도 만들지 않습니다. 이렇게 하면 Steam 오버레이는 그대로 게임에 인젝션되고, 스크립트도 자신의 변수에 당하지 않습니다.

(저장 시의 `if [ -z "${VAR+x}" ]` 판정은 생략하면 안 됩니다. 스크립트는 `arch`를 통해 자신을 다시
exec하므로, 이 판정이 없으면 두 번째 실행에서 빈 값으로 첫 번째 실행에서 저장한 값을 덮어써 Steam 오버레이가 조용히 사라집니다.)

## macOS 27+ 대응 및 실패 원인 분석

macOS 27 환경에서 실행 로그조차 남지 않고 Mod가 동작하지 않는 문제가 발생했으며, 조사 결과 시스템 샌드박스와 실행 메커니즘 강화로 인한 연쇄 문제였습니다:

### 1. `defaults read`의 크로스 디렉터리 샌드박스 거부로 스크립트가 조용히 종료 (핵심 원인)

기존 실행 스크립트는 `.app` 내부 주 바이너리 파일명을 확인할 때 시스템 명령을 호출했습니다:

```sh
inner_executable_name=$(defaults read "${real_executable_name}/Contents/Info" CFBundleExecutable)
```

macOS 27에서는 `defaults` 뒤의 `cfprefsd` 데몬에 더 엄격한 파일 샌드박스 격리가 적용됩니다. Steam 라이브러리 내 `Info.plist`를 크로스 디렉터리로 읽으면 `cfprefsd`가 바로 권한 예외를 던집니다:

```text
sandbox_extension_issue_file failed ... 1 (Operation not permitted)
Error: Domain '.../Contents/Info' not found.
```

스크립트 맨 앞에서 `set -e`(오류 발생 시 즉시 종료)를 켜 두었기 때문에, **`run_bepinex.sh`는 게임 바이너리를 실행하기도 전에 Exit Code 1로 조용히 종료**됩니다. 게임은 BepInEx 인젝션 절차에 들어갈 기회조차 없습니다.

**해결책**: XML/바이너리 plist를 그 자리에서 직접 파싱하고 `cfprefsd` 샌드박스 제한을 받지 않는 시스템 내장 `plutil -extract CFBundleExecutable raw`와 `/usr/libexec/PlistBuddy`로 변경하고, 다층 안전 폴백을 유지합니다.

### 2. Steam 실행 환경에서의 "로그 블랙홀"

Steam의 "실행 옵션"으로 게임을 실행하면 자식 프로세스에 보이는 터미널 콘솔이 마운트되지 않습니다. `run_bepinex.sh`와 `doorstop_shim.c`의 기존 오류 및 상태 추적은 모두 표준 오류 스트림 `stderr`에 출력되었습니다.

- 스크립트가 실행 초기에 오류로 종료되면(위 `defaults read` 종료 등) 모든 오류 출력이 시스템에 의해 조용히 버려집니다.
- 이때 BepInEx 엔진이 아직 초기화되지 않아 `BepInEx/LogOutput.log`가 전혀 생성되지 않습니다.
- 최종적으로 사용자 측에서는 Mod가 로드되지 않고, 게임 디렉터리 전체에서 "실행 로그가 하나도 보이지 않는" 현상으로 나타납니다.

**해결책**: `run_bepinex.sh`가 (Steam 실행 같은) 비터미널 실행 환경을 감지하면 `stdout`과 `stderr`를 자동으로 리다이렉트해 아래 파일에 이어 기록합니다:

```text
게임 디렉터리/BepInEx/run_bepinex.log
```

실행 스크립트, `doorstop_shim` 인젝션 심, 엔진 초기의 모든 진단 정보를 전 과정에 걸쳐 투명하게 확인할 수 있습니다.

### 3. `DYLD_INSERT_LIBRARIES` 인젝션 경로 절대화

기존 스크립트는 `export DYLD_INSERT_LIBRARIES="libdoorstop.dylib"`와 `DYLD_LIBRARY_PATH` 조합으로 상대 탐색을 했습니다. 새 시스템에서는 예상치 못한 작업 디렉터리에서 동적 라이브러리 탐색 실패 또는 제한 규칙 필터링이 발생할 수 있습니다.

**해결책**: 인젝션 경로를 절대 경로 `${doorstop_directory}${doorstop_name}`으로 통일했습니다.
