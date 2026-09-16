# AssetGraph — 에셋 참조 인덱스

**v1.1** — UniTask 의존 제거, 코드 스캔 범위를 `Assets/` 전체로, MCP 툴은 define으로 켜기

언리얼 Reference Viewer / Asset Registry의 참조 정보에 해당하는 도구. 유니티에는 "누가 이 에셋을 쓰나"(역참조)를 답하는 기본 기능이 없어서 만들었다.

## 설치

`Editor/AssetGraph` 폴더를 프로젝트의 `Assets/` 아래 아무 곳에나 복사한다. 폴더 이름에 `Editor`가 들어 있어야
유니티가 에디터 전용 스크립트로 인식한다.

- 의존성은 `com.unity.nuget.newtonsoft-json` 하나다. 보통 다른 Unity 패키지를 통해 이미 들어와 있고, 없으면 Package Manager에서 추가한다.
- 사람용 에디터 창(`BBAssetGraphWindow`)은 그 외 의존성 없이 바로 동작한다. Unity 6000.6.0f1에서 확인했다.
- AI용 MCP 커스텀 툴(`bb_asset_graph`, `BBAssetGraphMcpTool.cs`)은 기본으로 꺼져 있다. [MCP for Unity](https://github.com/CoplayDev/unity-mcp) 브리지가 설치된 프로젝트에서 Player Settings의 Scripting Define Symbols에 `ASSETGRAPH_MCP`를 넣으면 켜진다.
- 네임스페이스가 `BeastBlood.Editor.AssetGraph`로 돼 있다(원 프로젝트에서 추출). 다른 프로젝트에 맞게 바꿔도 동작에는 지장 없다.

## 기능

- **사람용 에디터 창** `BBAssetGraphWindow`: Project 창에서 에셋 우클릭(또는 상단 `Assets` 메뉴) → `에셋 참조 그래프 보기`. 메뉴 등록은 이것 하나뿐이다.
  (Shortcuts 창의 `Window/Panels/N 에셋 참조 그래프`는 창이 열려 있는 동안 Unity가 자동으로 붙이는 항목이라 없앨 수 없다.)
  기본은 양쪽 1단계. 클릭 = Project 창에서 핑, 더블클릭 = 그 파일을 탐색 대상으로, `2단계 표시` = 한 단계 더, ◀ = 이전 대상, 우클릭/휠 드래그 = 화면 이동. 대상이 바뀌면 대상 노드가 화면 가운데에 온다.
  `확장자` 드롭다운 = 확장자별로 켜고 끄기, `이것만 보기`(숨긴 중간 노드는 건너뛰고 가장 가까운 조상에 선을 잇는다).
  `새로고침` = 지금 보이는 파일만 다시 읽기(즉시), `전체 재스캔` = 캐시 무시하고 전부(30초 이상). 인덱스가 바뀌면 창은 자동으로 다시 그린다.
  그래프는 **저장된 파일** 기준이다. 대상에 저장 안 한 변경이 있으면 창에 경고가 뜬다.
- **AI용** MCP 커스텀 툴 `bb_asset_graph` (메뉴 없음). 둘 다 읽기 전용이고 같은 인덱스를 쓴다.
- 캐시: `Library/BBAssetGraph/RefIndex.json` (SVN 비대상. 지워도 다음 `build`에서 다시 만든다)

## 사용법

| action | 하는 일 | 주요 파라미터 |
|---|---|---|
| `build` | 백그라운드 전체 갱신. **처음 한 번**이면 된다 | `full`(true면 캐시 무시) |
| `stats` | 갱신 진행 여부(`building`), 규모, 마지막 갱신 소요 시간 | |
| `refs` | 대상을 **쓰는** 에셋 (역참조) | `target`, `depth`, `include_third_party`, `limit` |
| `deps` | 대상이 **쓰는** 에셋 (정참조) | 위와 같음 |
| `unused` | 폴더 아래 아무도 참조하지 않는 에셋 | `folder`(필수), `extension` |
| `missing` | 프로젝트에 없는 GUID를 가리키는 끊긴 참조 | `folder`, `include_third_party` |

- `target`은 GUID, `Assets/...` 경로, 파일 이름(확장자 생략 가능) 모두 된다. 이름이 겹치면 후보 목록을 돌려준다.
- 결과의 `via`: `asset` = 직렬화 참조, `code` = C# 문자열 리터럴로 추정한 연결.
- 서드파티(`Assets/20_Plugins`, `Assets/30_ThirdParty`, `Assets/Plugins`, `Packages/`)는 기본으로 결과에서 빠지고 더 따라가지도 않는다.

## 동작 방식

1. `Assets/**/*.meta`에서 GUID → 경로 사전을 만든다. `Packages/` 쪽 GUID는 `AssetDatabase.GUIDToAssetPath`로 찾는다.
2. YAML 에셋(.prefab .asset .unity .controller .anim .mat .playable 등)에서 `guid: ` / `GUID: ` 뒤 32자리를 뽑는다.
   `GUID: `는 Addressables `m_AssetGUID`·그룹 엔트리 `m_GUID`를 잡기 위한 것이다. `AssetDatabase.GetDependencies`는 이 문자열 필드를 놓친다.
   `ProjectSettings/*.asset`(Preloaded Assets 등)도 소스로 스캔한다. .meta가 없어 경로를 GUID 자리에 쓰며, 임포트 대상이 아니라 **전체 갱신 때만** 반영된다.
3. 빌드 설정 씬 이름이 `Assets/` 아래 C# 파일(서드파티 폴더 제외)의 문자열 리터럴(`"Title"` 등)로 나오면 코드 경유 선을 만든다.
4. 파일 I/O는 스레드 풀(`Task.Run`)에서 돌고, 반영은 메인 스레드에서 한다. 이후 변경은 `BBAssetRefPostprocessor`가 임포트 시 파일 단위로 반영한다.

## 잡는 것 / 못 잡는 것 (2026-09-15 실측)

| 경로 | 결과 |
|---|---|
| 프리팹·씬·SO·애니메이터·머티리얼의 일반 참조 | 잡음 |
| 터레인 레이어(`.terrainlayer`), 신형 스프라이트 아틀라스(`.spriteatlasv2`), 프리셋(`.preset`) | 잡음 — 2026-09-15 전수 조사로 추가. `.asmdef`(JSON `"GUID:"`)·`.signal`·`.wlt`는 에셋 참조가 아니라 제외 |
| Odin `SerializedScriptableObject` | 잡음 — 참조는 `ReferencedUnityObjects:`에 평문 GUID. 바이너리 포맷(`SerializedFormat: 1`) 에셋 0건 |
| Addressables `AssetReference` | 잡음 (`m_AssetGUID`) |
| Opsive BT 태스크가 참조하는 에셋(서브트리 등) | 잡음 — `m_UnityObjects:` 목록에 평문 GUID. blob(`m_Values`) 안의 수치·문자열은 참조가 아니라 대상 아님 |
| 코드에서 씬 이름 문자열로 로드 | **추정만** (`via=code`). 같은 문자열을 다른 용도로 쓰면 오탐 |
| Addressables 주소 문자열 직접 로드, Lua·CSV의 에셋 이름 | **못 잡음** (2026-09-15 기준 주소 문자열 직접 호출 0건) |
| 에디터 확장이 타입 검색(`FindAssets("t:...")`)으로 찾는 설정 에셋 (예: Dialogue System `BB Lua Function Info`) | **못 잡음** — 직렬화 참조가 원래 없음. `unused`에 나와도 지우면 안 되는 부류 |
| 바이너리 에셋(LightingData, NavMesh, Terrain) | 스캔 안 함 |

## 주의

- `unused` 교차검증(2026-09-15, `Assets/05_SO` 후보 12건을 grep으로 독립 확인): 11건 실제 미참조, 오탐 1건(`VContainerSettings` — ProjectSettings 스캔 추가로 수정됨).
- `unused`는 **삭제 판단의 근거가 아니라 후보 목록**이다. 위 "못 잡음" 경로와, 미참조 에셋끼리만 서로 참조하는 고아 사슬은 걸러지지 않는다. 삭제는 사람이 확인한다.
- `missing`에는 이미 지워진 에셋·스크립트를 가리키는 오래된 참조가 섞여 있다(서드파티 샘플 포함). 폴더를 좁혀서 본다.
- 전체 갱신은 디스크 캐시가 식어 있으면 수십 초 걸린다. MCP 요청이 제한 시간에 끊기지 않게 백그라운드로 돈다. 갱신 중 조회는 에러로 돌려준다.
