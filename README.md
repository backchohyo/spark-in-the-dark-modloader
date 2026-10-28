# SparkModLoader

Spark in the Dark (Steam, Unity 2022.3 Mono) 용 모드 로더입니다.

게임 파일을 수정하지 않습니다. 게임이 시작될 때 `Mods` 폴더의 모드 DLL을 읽어 게임 코드를 메모리에서만 고친 뒤 실행합니다. 그래서 게임이 업데이트돼도 로더와 모드가 지워지지 않고, 업데이트로 맞지 않게 된 모드는 그 모드만 건너뛰고 실행됩니다.

## 기능

- `Mods` 폴더의 모드 DLL 자동 로드
- 메인 메뉴 [설정] 아래에 [모드] 버튼 추가
  - 모드 켜기/끄기 (다음 실행부터 적용)
  - 모드별 [설정] 화면: 모드가 선언한 설정값으로 화면이 자동으로 만들어짐
- 모드 공용 저장 기능: 여러 모드가 세이브에 각자 데이터를 붙여도 충돌하지 않음
- 실행 기록과 성능 기록: `Mods\ModLoader.log` (1분마다 FPS, 끊김, 모드별 소요 시간)

## 설치

1. 스팀에서 게임 우클릭 > 관리 > 로컬 파일 보기
2. 열린 폴더(exe 가 있는 곳)에 `release` 폴더의 내용을 그대로 넣습니다.

```
Spark in the Dark\
├─ Spark in the Dark.exe
├─ winhttp.dll
├─ doorstop_config.ini
└─ Mods\
    └─ ModLoader.dll
```

모드는 `Mods` 폴더에 DLL을 넣으면 됩니다. 게임을 한 번 켜면 `Mods\ModLoader.log` 가 생기는데, 로그 파일이 생기지 않으면 설치 위치를 확인하세요.

**제거:** `winhttp.dll`, `doorstop_config.ini`, `Mods` 폴더를 지웁니다. 로더만 잠시 끄려면 `doorstop_config.ini` 의 `enabled=true` 를 `false` 로 바꿉니다.

## 모드 만들기

`ModLoader.dll` 을 참조하고, static 메서드에 `[Hook]` 을 붙이면 로더가 그 자리에 호출을 넣습니다.

```csharp
using ModLoader;

[assembly: ModInfo("내 모드", "1.0.0", Description = "설명", Author = "이름")]

public static class MyMod
{
    // 무기 피해량을 10% 올리기
    [Hook(HookKind.Postfix, "HandItems.Weapon", "GetDamage", Params = 1)]
    public static float Damage(float result, HandItems.Weapon self) => result * 1.1f;
}
```

| HookKind | 동작 | 훅 메서드 형태 |
|---|---|---|
| Prefix | 메서드 시작에서 호출 | `void Hook(Args...)` |
| PrefixSkip | true 를 반환하면 원본을 건너뜀 (void 메서드) | `bool Hook(Args...)` |
| PrefixReturn | null/NaN 이 아니면 그 값을 바로 반환 | `T Hook(Args...)` |
| PrefixSkipReturn | true 면 out 값을 바로 반환 (값 형식도 가능) | `bool Hook(Args..., out T)` |
| PrefixArg | 인자 하나(ArgIndex)를 바꿈 | `T Hook(Args...)` |
| Postfix | 반환 직전에 결과를 바꿈 | `T Hook(T result, Args...)` |
| ReplaceCall | 메서드 안의 특정 호출을 바꿈 | `T Hook(원래 인자..., Args...)` |

- `Args = new[] { 0, 1 }`: 넘길 인자 번호 (0 = this)
- `Params`, `ParamTypes`: 같은 이름의 메서드가 여럿일 때 구분
- `Assembly = "UnityEngine.UI"`: Assembly-CSharp 가 아닌 게임 어셈블리를 고칠 때
- 한 모드의 훅이 하나라도 게임 버전과 맞지 않으면 그 모드 전체를 적용하지 않습니다.

**설정 화면:** static 필드에 `[Setting]` 을 붙이면 [모드] > [설정] 화면에 자동으로 나타나고 `Mods\config\<모드>.cfg` 에 저장됩니다. bool, int, float 를 지원하고, int 에 `Options = "가|나|다"` 를 주면 선택지가 됩니다.

```csharp
[Setting("일반", "등장 확률", Unit = "%", Min = 0, Max = 100, Desc = "마우스를 올리면 보이는 설명")]
public static int Chance = 15;
[Setting("일반", "최대", Inline = true)] public static int Max = 3;   // 앞 항목과 같은 줄에 표시
```

**세이브 데이터:** `[ModInit]` 메서드에서 `SaveExt.Register("키", 저장, 불러오기)` 를 호출하면 게임 오브젝트의 세이브 JSON 에 `"키":"값"` 이 붙습니다.

**성능 기록:** `Perf.Begin()` / `Perf.End("이름", t)` 로 감싸면 1분마다 로그에 소요 시간이 남습니다.

## 빌드

필요한 것: .NET SDK 6 이상, 게임 설치본 (게임 DLL 은 저장소에 포함하지 않고 설치 폴더에서 참조합니다)

```
git submodule add https://github.com/jbevain/cecil.git external/cecil
git -C external/cecil checkout 0.11.5
dotnet build -c Release
```

게임이 기본 경로가 아니면 `dotnet build -c Release -p:GameDir="D:/Games/Spark in the Dark"` 처럼 지정합니다. 결과물은 `bin/ModLoader.dll` 입니다. Mono.Cecil 소스를 함께 컴파일해서 DLL 하나로 만듭니다.

`doorstop/winhttp.dll` 은 [Unity Doorstop](https://github.com/NeighTools/UnityDoorstop) v4.6.0 소스를 수정 없이 x64 로 빌드한 것입니다.

## 사용한 오픈소스

- [Unity Doorstop](https://github.com/NeighTools/UnityDoorstop) 4.6.0 — LGPL-2.1 (`doorstop/LICENSE-Doorstop.txt`)
- [Mono.Cecil](https://github.com/jbevain/cecil) 0.11.5 — MIT (`LICENSE-Mono.Cecil.txt`)
