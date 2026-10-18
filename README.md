# Save Store

MemoryPack과 원자적 파일 저장소를 기반으로, **도메인 단위·체크포인트 기반**으로 저장하는 세이브 시스템입니다.

PlayerPrefs에 세이브 전체를 JSON 문자열로 저장하던 구조는 값이 바뀔 때마다 전체를 다시 직렬화해 큰 문자열과 GC 멈춤을 만들었습니다. 이 패키지는 그 원인을 구조로 막습니다.

| 원칙 | 구현 |
|---|---|
| 게임 코드는 저장을 모른다 | 도메인 변경 메서드가 `MarkChanged()`로 dirty 표시만 한다 |
| 언제 쓰나: 체크포인트에서만 | `SaveScheduler`: 주기(기본 60초) · 백그라운드 전환 · 중요 거래 |
| 무엇을 쓰나: 바뀐 도메인만 | `SaveSystem.WriteDirty()`가 재사용 버퍼에 직렬화한다(큰 string이 생기지 않음) |
| 어디에 쓰나: 원자적 커밋 | 도메인별 세대 blob → manifest 교체로 커밋, CRC32, 백업 세대. 매체는 `ISaveStorage`로 교체 가능 |
| 복구는 로드에서만 | `Validate()`는 로드 직후 한 번. 진입점이 `internal`이라 조회 코드가 부를 수 없다 |
| 파생값은 메모이즈 | `VersionedComputed<T>`: 의존 도메인 버전이 바뀔 때만 다시 계산 |

**기능 한눈에 보기**

| 기능 | API | 절 |
|---|---|---|
| 매체 교체 (파일·메모리·WebGL·직접 구현) | `ISaveStorage`, `FileSaveStorage`, `MemorySaveStorage`, `WebGLSaveStorage` | [저장소](#저장소매체-교체-isavestorage) |
| 세이브 슬롯·프로필 | `ISaveSlotProvider`, `FileSaveSlotProvider`, `SaveSlots.Inspect` | [슬롯](#세이브-슬롯) |
| 압축·암호화·서명 | `ISavePayloadTransform`, `DeflateCompressionTransform`, `HmacSignatureTransform`, `AesHmacEncryptionTransform`, `TransformChain` | [변환](#압축암호화서명-isavepayloadtransform) |
| 신뢰 시각 주입 | `IClock`, `SystemClock`, `OffsetClock`, `ManualClock` | [시계](#시계-iclock) |
| 메모리 트랜잭션 | `SaveSystem.BeginTransaction`, `SaveTransaction` | [트랜잭션](#트랜잭션) |
| 클라우드 동기화 훅 | `SaveSystem.OnCommitted`, `CaptureSnapshot`, `ApplySnapshot`, `SaveSnapshot` | [클라우드](#클라우드-동기화-훅) |
| 동적 도메인 (청크 등) | `SaveSystem.Register`, `Unregister`, `Forget` | [동적 도메인](#동적-도메인) |
| 도구 | `Tools/Save Store/Save Viewer`, `SaveGoldenFiles` | [도구](#도구) |

## 설치

1. MemoryPack: NuGetForUnity로 `MemoryPack`(Core + Generator)를 설치하고, UPM `com.cysharp.memorypack`(git: `https://github.com/Cysharp/MemoryPack.git?path=src/MemoryPack.Unity/Assets/MemoryPack.Unity`)을 추가합니다. Unity 2022.3.12 이상이 필요합니다.
2. 이 패키지를 `Packages/com.nkstudio.savestore`에 둡니다(임베디드).
3. 레코드를 정의하는 어셈블리에 MemoryPack 소스 제너레이터가 동작해야 합니다. `overrideReferences: true`인 asmdef는 `MemoryPack.Core.dll`과 `System.Runtime.CompilerServices.Unsafe.dll`을 precompiledReferences에 넣어야 합니다(생성 코드가 Unsafe를 쓴다).

## 사용법

```csharp
// 1) 레코드: 데이터만, VersionTolerant
[MemoryPackable(GenerateType.VersionTolerant)]
public sealed partial class WalletRecord
{
    [MemoryPackOrder(0)] public long Gold;
}

// 2) 도메인: 규칙과 불변식, 변경 시 MarkChanged
public sealed class WalletDomain : SaveDomain<WalletRecord>
{
    public override string Key => "wallet";               // 파일 이름. 출시 후 바꾸지 않는다
    public long Gold => Record.Gold;

    public bool TrySpend(long amount)
    {
        if (amount <= 0 || Record.Gold < amount) return false;
        Record.Gold -= amount;
        MarkChanged();
        return true;
    }

    protected override WalletRecord CreateDefault() => new WalletRecord { Gold = 100 };
    protected override void Validate() { if (Record.Gold < 0) Record.Gold = 0; }   // 로드 직후 한 번
}

// 3) 부트: 등록 → Load → 스케줄러
var wallet = new WalletDomain();
var storage = new FileSaveStorage(Path.Combine(Application.persistentDataPath, "Save"));
var options = new SaveOptions { Clock = new OffsetClock(), PayloadTransform = new DeflateCompressionTransform() }; // 생략 가능
var saveSystem = new SaveSystem(storage, new SaveDomain[] { wallet }, options);
saveSystem.Load();
var scheduler = gameObject.AddComponent<SaveScheduler>();
scheduler.Bind(saveSystem);

// 4) 게임 진행: 일반 변경은 그냥 호출, 중요 거래만 즉시 커밋
if (wallet.TrySpend(30)) scheduler.RequestCritical();
```

## 샘플

샘플은 이 저장소가 아니라 패키지를 개발한 Unity 프로젝트의 `Assets/SaveStoreSample/SaveStoreSample.unity`에 있습니다. 모험가 테마의 UI Toolkit 화면입니다. 씬을 열고 플레이하면 됩니다. 슬롯(slot-1~3)별 폴더에 Deflate로 압축해 저장하고, 시간은 `OffsetClock`에서 가져오며, 젬 구매·우편 받기는 트랜잭션으로 묶여 있습니다.

| 도메인 | 보여 주는 것 |
|---|---|
| `wallet` | 코인/젬. 유료 재화(젬) 차감은 중요 거래로 즉시 커밋합니다. |
| `stamina` | 시간이 지나면 차오르는 값을 `(기준값, 기준 시각)`으로 저장합니다. 시간이 흐르는 것만으로는 dirty가 되지 않습니다. |
| `inventory` | 장비/소모품 Union. 전투력은 `VersionedComputed`로 캐시합니다. |
| `mailbox` | 우편 보상 Union(재화/장비). 받기 한 번으로 세 도메인을 한 커밋에 반영합니다. |

- **화면 구성:** 위쪽 상태 줄(코인·젬·스태미나·전투력·커밋 세대)은 항상 보입니다. 아래는 탭으로 나뉩니다.
  - `Play`: 일반 행동(다음 체크포인트에 저장)과 중요 행동(즉시 커밋)
  - `Inventory`, `Mailbox`: 탭 이름에 개수가 표시됩니다
  - `Save`: 도메인별 dirty, 다음 체크포인트까지 남은 시간, 마지막 커밋에서 바뀐 도메인(클라우드 업로드 대상), 마지막 로드 결과, 슬롯 전환과 슬롯별 마지막 저장 시각, 파일 목록, 세이브 도구
- **한국어 UI와 폰트:** 화면 문구는 한국어입니다. 폰트는 `Fonts/`의 IBM Plex Sans KR(Regular/Bold, SIL Open Font License 1.1, 원본 그대로 동봉 — `Fonts/OFL.txt`)을 UI Toolkit 동적 FontAsset으로 씁니다. Bold는 Regular의 굵기 표(700)에 연결돼 있어 `-unity-font-style: bold`가 진짜 Bold 서체로 그려집니다.
- **표시 이름과 세이브 분리:** 세이브에는 아이템 코드(`steel_sword` 등)만 저장하고, 표시 이름은 `ItemCatalog`에서 찾습니다. 번역이나 이름을 바꿔도 세이브는 그대로입니다.
- **손상 복구 체험:** "Corrupt inventory file + reload" 버튼을 누르면 세이브 전체가 직전 세대로 함께 되돌아가는 것을 볼 수 있습니다.
- **파일 구성:**
  - `Scripts/Records/AdventureRecords.cs`: 저장 레코드
  - `Scripts/Domains/*`: 도메인 규칙
  - `Scripts/AdventureGame.cs`: 세이브 배선과 여러 도메인에 걸친 행동
  - `Scripts/SaveStoreSampleScreen.cs`, `UI/*`: UI Toolkit(UXML/USS/PanelSettings)

## 레코드 규칙 (세이브 호환이 걸린 규칙)

- **세이브용 타입은 전부 `GenerateType.VersionTolerant`로 만든다.** 기본 모드는 멤버가 바뀌면 이전 세이브를 읽지 못합니다.
- **`[MemoryPackOrder]`는 추가만 허용한다.** 번호 재사용, 순서 변경, 삭제는 금지입니다. 필요 없어진 필드는 `[Obsolete]`로 남깁니다.
- **⚠️ 새로 추가한 필드는 옛 세이브에서 필드 초기값이 아니라 `default`(0/null)로 들어온다.** `public int FrameId = 7;`은 0이 되고, `public List<T> Tags = new();`는 **null**이 됩니다. 기본값이 필요하면 도메인의 `Validate()`에서 채웁니다(`Record.Tags ??= new();`). 테스트 `Test_Load_AppendOnlyFieldAdded_ReadsOldSaveWithDefault`가 이 동작을 고정합니다.
- **다형성은 숫자 태그 `[MemoryPackUnion(tag, type)]`로 표현하고, 베이스는 추상 클래스로 만든다.** 타입 이름이 세이브에 들어가면 클래스 이름만 바꿔도 세이브가 깨집니다. Unity(.NET Standard)에는 정적 추상 인터페이스 멤버가 없어서, Union **인터페이스**를 `List<T>` 멤버로 쓰면 생성 코드가 컴파일되지 않습니다(CS0311).
- **Union 베이스의 공통 접근자는 속성이 아니라 메서드로 둔다.** 베이스의 `abstract` 속성은 VersionTolerant에서 직렬화 멤버로 잡혀 `[MemoryPackOrder]`를 요구하고(MEMPACK025), 재정의 쪽에는 `[MemoryPackIgnore]`를 달 수 없습니다(MEMPACK009). 샘플은 `GetInstanceId()`/`GetDisplayName()`을 씁니다.
- **호환이 깨지는 변경은 `SchemaVersion`을 올리고 `Migrate()`에서 옮긴다.** 예전 레코드 타입으로 읽어 새 타입으로 변환합니다. 앱보다 새 스키마의 세이브는 읽지 않고 `SaveDataException`을 던집니다(옛 앱이 모르는 필드를 버린 채 다시 저장하는 것을 막기 위해서).
- **시간에 따라 변하는 값은 `(기준값, 기준 시각)`으로 저장하고 읽을 때 계산한다.** 틱마다 저장하면 도메인 버전과 dirty가 계속 흔들립니다.
- **자주 바뀌는 값은 별도 도메인으로 뗀다.** `VersionedComputed`는 도메인 단위로 무효화되기 때문입니다.

## 저장소(매체) 교체: `ISaveStorage`

세이브의 안전 규칙(세대, manifest 커밋, 체크섬, 스냅샷 롤백, 고아 blob 정리)은 `SaveRepository`가 모든 매체에 공통으로 처리합니다. 매체 쪽은 이름 붙은 바이트 덩어리(blob)를 다루는 다섯 동작만 구현하면 됩니다.

| 구현 | 용도 |
|---|---|
| `FileSaveStorage(folder)` | Android·iOS·PC·에디터의 로컬 파일 |
| `MemorySaveStorage()` | 테스트, 저장하지 않는 게스트·체험 모드, 새 플랫폼 구현 전 임시 |
| `WebGLSaveStorage(folder)` | WebGL. 파일 동작은 FileSaveStorage와 같고, 커밋 지점(manifest 교체)과 삭제 뒤에 `FS.syncfs`로 IndexedDB에 내립니다(`Runtime/Plugins/WebGL/SaveStoreWebGL.jslib`). syncfs가 비동기라 끝나기 전에 탭이 닫히면 마지막 커밋을 잃을 수 있습니다(깨진 상태로 남지는 않음). **실제 WebGL 빌드에서는 아직 검증하지 않았습니다.** |
| 직접 구현 | 콘솔 세이브 API, 암호화 컨테이너 등 |

```csharp
public sealed class MyPlatformStorage : ISaveStorage
{
    public string Location => "platform-save";
    public void Open() { /* 마운트 등 */ }
    public bool TryRead(string name, out byte[] data) { ... }
    public void Write(string name, ReadOnlySpan<byte> data) { ... }   // 반환 시 매체에 확정(fsync)
    public void Rename(string source, string destination) { ... }   // 원자적, 대상이 있으면 예외
    public void Delete(string name) { ... }                          // 없으면 무시
    public IReadOnlyList<string> List() { ... }
}

var saveSystem = new SaveSystem(new MyPlatformStorage(), domains);
```

**구현 계약:**
- **`Write`:** 반환 시점에 내용이 매체에 확정돼 있어야 합니다. 커밋 순서 보장이 이것에 기댑니다.
- **`Rename`:** 원자적이어야 하고, 대상이 이미 있으면 덮어쓰지 않고 예외를 던집니다. 테스트 `Test_Storage_RenameOntoExisting_Throws`로 확인할 수 있습니다.
- **스레드:** SaveRepository가 한 번에 한 스레드에서만 부르므로 동시 호출 안전성은 필요 없습니다.
- **검증 방법:** 패키지 테스트는 모든 시나리오를 `FileSaveStorage`와 `MemorySaveStorage`에서 각각 돌립니다. 새 구현은 테스트의 `StorageKind`에 하나 추가하면 같은 검증을 받습니다.

## 세이브 슬롯

슬롯 하나 = 독립된 `ISaveStorage` 하나입니다. `FileSaveSlotProvider(root)`는 슬롯마다 하위 폴더(`{root}/{slotId}/`)를 씁니다.

```csharp
var slots = new FileSaveSlotProvider(Path.Combine(Application.persistentDataPath, "Saves"));

// 슬롯 목록 화면: 슬롯을 열지 않고(저장소를 고치지 않고) 마지막 커밋만 읽는다
foreach (SaveSlotInfo info in SaveSlots.InspectAll(slots, options))
    Debug.Log($"{info.SlotId}: 세대 {info.Generation}, {info.CommittedAtUtc}");

// 슬롯 전환·계정 전환: 지금 SaveSystem 을 Dispose(남은 변경 저장) → 새 슬롯으로 Load
saveSystem.Dispose();
saveSystem = new SaveSystem(slots.OpenSlot("slot-2"), domains, options);
saveSystem.Load();
scheduler.Bind(saveSystem);
```

- **도메인 재사용:** 도메인 인스턴스는 그대로 다시 써도 됩니다. Load가 레코드를 갈아 끼우고 버전을 올리므로 파생값 캐시도 따라옵니다.
- **슬롯 지우기:** `slots.DeleteSlot(id)`. 로그아웃할 때 계정 스코프 슬롯을 지우는 데 씁니다.
- **변환과 함께 쓸 때:** 슬롯 요약(`Inspect`)도 manifest를 읽으므로, 암호화·서명을 쓰면 같은 `SaveOptions`를 넘깁니다.

## 압축·암호화·서명 (`ISavePayloadTransform`)

`SaveOptions.PayloadTransform`을 지정하면 모든 blob(도메인과 manifest)이 저장 직전에 변환되고, 헤더에 변환 Id가 기록됩니다. CRC는 저장된(변환된) 바이트 기준이라 매체 손상은 변환 전에 걸러지고, 변환은 위조·키 불일치만 판단합니다.

| 구현 | Id | 형식 / 용도 |
|---|---|---|
| `DeflateCompressionTransform` | 1 | `[원본 길이][deflate]`. 반복 구조가 많은 세이브의 크기를 줄입니다. |
| `HmacSignatureTransform(key)` | 2 | `[payload][HMAC-SHA256]`. 내용은 평문이고, 손으로 고치면 로드가 거부됩니다. |
| `AesHmacEncryptionTransform(key)` | 3 | `[IV][AES-256-CBC][HMAC]` (encrypt-then-MAC). 마스터 키 하나에서 암호화 키와 MAC 키를 따로 유도합니다. |
| `TransformChain(id, a, b, …)` | 지정 | 순서대로 적용하고 역순으로 복원합니다. 예: `new TransformChain(100, new DeflateCompressionTransform(), new AesHmacEncryptionTransform(key))` |

- **평문에서 암호화로 넘어갈 때:** 이전 버전의 평문 세이브를 읽어야 하는 기간에만 `AcceptsUntransformedBlobs = true`로 둡니다. 켜 두면 서명 없는 평문 파일을 넣어 서명 검사를 우회할 수 있습니다.
- **⚠️ 키 관리:** 클라이언트에 든 키는 앱을 풀면 꺼낼 수 있습니다. 암호화·서명은 손쉬운 세이브 수정을 막을 뿐이고, 경제에 결정적인 값은 서버가 검증해야 합니다.
- **Id 규칙:** 알고리즘이나 키 형식을 바꾸면 Id도 바꿉니다. 같은 Id로 다른 변환을 쓰면 기존 세이브를 읽지 못합니다.

## 시계 (`IClock`)

커밋 시각 기록과, 시간에 따라 변하는 게임 값(스태미나 등)의 기준 시각에 씁니다.

| 구현 | 용도 |
|---|---|
| `SystemClock.Instance` | 기기 시계(기본값). 사용자가 바꿀 수 있습니다. |
| `OffsetClock` | 서버 응답을 받을 때마다 `SyncTo(serverUnixMs)`로 오프셋을 맞추고, 그 사이에는 기준 시계 + 오프셋으로 흐릅니다. 보상·진행이 걸린 판정에 씁니다. |
| `ManualClock` | 테스트에서 `Advance(ms)`로 시간 경과를 재현합니다. |

도메인이 시각을 쓰면 생성자로 `IClock`을 받습니다(샘플 `StaminaDomain(IClock)`). 쓰기 스레드에서도 읽히므로 구현은 스레드 안전해야 합니다.

## 트랜잭션

여러 도메인을 고치는 행동이 중간에 실패해도 메모리에 반쯤 바뀐 상태가 남지 않게 합니다. 디스크 커밋은 원래 원자적이고, 이 기능은 메모리 쪽을 보호합니다.

```csharp
using (SaveTransaction tx = saveSystem.BeginTransaction(wallet, inventory))   // 인자 없으면 등록된 전부
{
    if (wallet.TrySpendGems(2) == false) return;   // Complete 없이 끝나면 시작 시점으로 되돌린다
    inventory.AddPotions(3);                      // 여기서 예외가 나도 되돌린다
    tx.Complete();
}
scheduler.RequestCritical();
```

- **시작할 때:** 대상 도메인 레코드를 MemoryPack으로 떠 둡니다(인벤토리 74 KB 기준 약 0.1 ms 수준).
- **열려 있는 동안:** `WriteDirty()`는 아무것도 쓰지 않습니다(0을 반환). dirty는 남아 있다가 트랜잭션이 끝난 뒤 체크포인트에 쓰입니다.
- **되돌린 뒤:** 도메인 `Version`이 올라가 파생값 캐시가 무효화됩니다. 레코드 객체가 새로 만들어지므로, 레코드 내부 객체를 밖에서 붙잡고 있지 않습니다.
- **중첩:** 지원하지 않습니다(예외).

## 클라우드 동기화 훅

| API | 용도 |
|---|---|
| `OnCommitted(SaveCommitInfo)` | 커밋마다(메인 스레드) 이번 세대에 바뀐 도메인의 키·스키마·payload(평문 MemoryPack)와 지워진 키를 알려 줍니다. **차등 업로드**에 씁니다. 구독자가 있을 때만 payload 사본을 만듭니다. |
| `CaptureSnapshot()` → `SaveSnapshot` | 등록된 모든 도메인의 현재 메모리 상태를 뜹니다. `ToBytes()`로 그대로 전송합니다(전체 백업·기기 이전). |
| `ApplySnapshot(snapshot)` | 받은 스냅샷으로 메모리 상태를 바꿉니다. 스냅샷에 없는 등록 도메인은 기본값이 되고, 등록되지 않은 도메인 항목은 다음 커밋에 그대로 기록됩니다. 보통 이어서 `SaveNow()`를 부릅니다. |

- **충돌 판단은 게임 몫입니다.** 예를 들어 `snapshot.CapturedAtUnixMs`와 `saveSystem.Repository.CommittedAtUnixMs`를 비교하거나, 진행도 같은 기준으로 고릅니다.
- **시각 비교:** `OffsetClock`을 쓰면 기기마다 다른 시계 때문에 판단이 틀어지는 것을 줄일 수 있습니다.
- **스레드:** 이벤트는 `WriteDirty`/`Flush`/`PumpEvents` 때 메인 스레드에서 전달됩니다. `SaveScheduler`가 매 프레임 `PumpEvents`를 부릅니다.

## 동적 도메인

맵 청크, 기간 한정 이벤트처럼 도메인 수가 실행 중에 바뀌는 경우입니다.

```csharp
var chunk = new ChunkDomain("chunk-3-4");
saveSystem.Register(chunk);      // Load 뒤라면 그 자리에서 저장된 데이터를 읽는다
// ... 청크가 화면 밖으로 나가면
saveSystem.Unregister(chunk);    // dirty 면 먼저 저장한다. 데이터는 남는다(다시 Register 하면 읽힌다)
// ... 청크가 파괴되면
saveSystem.Forget("chunk-3-4");  // 다음 커밋에서 manifest 에서 빠지고 blob 이 정리된다
```

- **손상 처리:** 나중에 등록한 도메인은 단독으로 읽히므로, 손상되면 그 도메인만 직전 세대로 대체됩니다(스냅샷 롤백 대상이 아닙니다).
- **키 규칙:** 키는 소문자·숫자·`_`·`-` 32자 이내입니다.

## 도구

- **세이브 뷰어** (메뉴 `Tools/Save Store/Save Viewer`): 세이브 폴더를 **고치지 않고** 읽어 manifest·세대·커밋 시각과 도메인별 내용을 JSON으로 보여 주고, 파일로 내보냅니다.
  - **타입 찾기:** 프로젝트의 `SaveDomain<T>`를 찾아 키에 맞는 레코드 타입으로 읽습니다. 키가 상수(`Key => "wallet"`)인 도메인만 자동으로 찾습니다.
  - **암호화된 세이브 보기:** 에디터 코드에서 `SaveViewerWindow.ProjectOptionsProvider = () => 같은 SaveOptions;`를 등록하고, 뷰어의 변환 항목에서 "Project"를 고릅니다.
- **골든 파일** (`SaveGoldenFiles`): 출시할 때마다 실제 세이브를 픽스처로 떠 두고(메뉴 `Tools/Save Store/Capture Save Folder as Golden Fixture...` 또는 `Capture`), EditMode 테스트에서 모든 과거 세이브가 최신 코드로 읽히는지 검사합니다. 레코드 규칙을 어기면 빌드 전에 실패합니다.

```csharp
[Test]
public void Test_AllReleasedSavesStillLoad()
{
    foreach (string fixture in SaveGoldenFiles.ListFixtures("Assets/Tests/SaveFixtures"))
        SaveGoldenFiles.AssertLoads(fixture, () => MySave.CreateDomains(), MySave.CreateOptions());
}
```

패키지 자체도 `Tests/Editor/Fixtures/profile-v1`(v1 스키마로 저장한 실제 세이브)을 V2(필드 추가)와 V3(마이그레이션) 코드로 읽는 테스트를 갖고 있습니다.

## 파일 배치와 복구

```
{root}/manifest.bin    커밋된 manifest (도메인별 유효 세대·길이·CRC·스키마)
{root}/manifest.bak    직전 manifest
{root}/wallet.g42.bin  도메인 파일: [매직 NKSS][포맷 v][변환 Id][스키마 v][길이][CRC32][payload]
```

- **커밋 순서:** 도메인 세대 파일을 쓰고 디스크까지 flush합니다 → `manifest.tmp`를 쓰고 flush합니다 → `.bin`을 `.bak`으로, `.tmp`를 `.bin`으로 바꿉니다. 쓰다가 끊겨도 이전 manifest가 가리키는 세대는 그대로 유효합니다.
- **manifest 고르기:** `.bin`/`.tmp`/`.bak` 중 검증을 통과한 것 가운데 **세대가 가장 높은 것**을 현재로, 그다음을 직전으로 씁니다. 커밋이 어느 단계에서 끊겼든 마지막으로 완성된 커밋을 찾습니다.
- **도메인 파일 손상 시:** 직전 세대가 온전하면 **스냅샷째 전체를 되돌립니다**(`report.IsRolledBack`). 도메인 하나만 되돌리면 여러 도메인에 걸친 거래(재화 지급 + 아이템 지급)가 반만 남기 때문입니다. 잃는 것은 마지막 커밋 하나입니다. 직전 세대까지 깨졌을 때만 도메인 단위로 복구하고, 그래도 안 되면 기본값으로 초기화한 뒤 오류 로그를 남깁니다.
- **파일 정리:** 커밋할 때 밀려난 세대가 가리키던 blob만 지웁니다(매 커밋마다 저장소 전체를 나열하지 않음). manifest에 실리지 못한 고아 blob은 로드할 때 한 번 정리합니다.

## 스레드

공개 API는 모두 메인 스레드 전용입니다. 직렬화는 `WriteDirty()`를 부른 스레드(메인)에서 해 일관된 스냅샷을 만들고, 파일 쓰기와 변환(압축·암호화)은 쓰기 스레드 하나에서 순서대로 합니다(`SaveOptions.IsBackgroundWriteEnabled`). WebGL은 같은 스레드에서 바로 씁니다. 쓰기에 실패하면 해당 도메인(과 지우기 예약)을 다시 dirty로 만들어 다음 체크포인트에 재시도하고, `OnWriteFailed`로 알립니다.

## 결정이 필요한 것 (프로젝트마다)

- **체크포인트 주기:** 포그라운드에서 크래시가 났을 때 잃을 수 있는 최대 진행 시간입니다(기본 60초).
- **플랫폼 백업:** iOS iCloud 백업과 Android Auto Backup에 세이브 폴더를 넣을지 정합니다(클라우드 세이브와 충돌하는지 확인).
- **변조 방지:** CRC32는 손상 감지용입니다. 수정 방지가 필요하면 `HmacSignatureTransform`이나 `AesHmacEncryptionTransform`을 씁니다. 키는 클라이언트에 들어가므로 결정적인 검증은 서버가 맡습니다.
- **시계:** 보상·진행 판정에 쓸 시계(`OffsetClock` + 서버 시각 동기화)를 정합니다.
- **서버 백업:** 로컬 바이너리를 서버 계약으로 쓰지 말고, 도메인별 DTO로 투영합니다.

## 라이선스

MIT — [LICENSE.md](LICENSE.md)
