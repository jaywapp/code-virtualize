# Navigation 정답

이 목록은 source를 직접 읽어 작성했다. `Fixture.Navigation.csproj`는 `../shared/LinkedHelper.cs`를 프로젝트 내부 경로 `Linked/LinkedHelper.cs`로 link한다. 따라서 물리 source 경로와 project document path를 함께 보존해야 한다.

## 선언 정답

| ID | Symbol | 선언 위치 | 판정 |
|---|---|---|---|
| N-01 | `ILoader.Load(string)` | `Contracts.cs:5` | interface method |
| N-02 | `Catalog` | `Contracts.cs:8`, `Catalog.Partial.cs:3`, `RefKinds.cs:3` | 하나의 partial type, 선언 3개 |
| N-03 | `Catalog.Load(string)` | `Contracts.cs:12` | public overload 1 |
| N-04 | `Catalog.Load(string, int)` | `Contracts.cs:16` | public overload 2 |
| N-05 | `Catalog.Load<T>(T)` | `Contracts.cs:20` | private generic, arity 1 |
| N-06 | `ILoader.Load(string)` implementation | `Contracts.cs:25` | explicit interface implementation; N-03와 별도 declaration |
| N-07 | `Catalog.Name` | `Catalog.Partial.cs:5` | partial declaration의 public property |
| N-08 | `LinkedHelper` | physical `../shared/LinkedHelper.cs:3`; linked `Linked/LinkedHelper.cs:3` | linked document |
| N-09 | `LinkedHelper.BuildLabel()` | physical `../shared/LinkedHelper.cs:5`; linked `Linked/LinkedHelper.cs:5` | linked document method |
| N-10 | `Worker.Run()` | `DynamicCandidates.cs:12` | public implementation |
| N-11 | `Catalog.Mutate(int)` | `RefKinds.cs:5` | internal overload |
| N-12 | `Catalog.Mutate(ref int)` | `RefKinds.cs:6` | protected ref-kind overload; N-11과 별도 identity |
| N-13 | `CallSites.Run()` | `CallSites.cs:12` | caller expansion의 2단계 caller |

## 정적 참조 정답

| ID | 대상 | 호출 위치 | 근거 |
|---|---|---|---|
| R-01 | `Catalog.Load(string)` | `CallSites.cs:7` | `catalog.Load("one")` |
| R-02 | `Catalog.Load(string, int)` | `CallSites.cs:8` | `catalog.Load("two", 2)` |
| R-03 | `Catalog.Name` | `CallSites.cs:9` | `catalog.Name` |
| R-04 | `ILoader.Load(string)` | `CallSites.cs:19` | static receiver type is `ILoader` |
| R-05 | `Worker` | `DynamicCandidates.cs:22` | `typeof(Worker)` type reference |
| R-06 | `Catalog.Load(string)` | `Contracts.cs:27` | explicit implementation 내부의 직접 호출 |
| R-07 | `CallSites.Use(Catalog)` | `CallSites.cs:14` | `Run()`에서 `Use(...)`를 호출하는 caller expansion |

`Catalog.Load<T>(T)`과 explicit implementation은 이 source 안에서 직접 호출하지 않는다. 그 사실은 정적 참조 0건이며 영향 0건을 뜻하지 않는다.

## 동적 후보 정답

| ID | 후보 대상 | 위치 | provenance | 이유 |
|---|---|---|---|---|
| D-01 | `Worker` constructor | `DynamicCandidates.cs:22` | reflection | `Activator.CreateInstance(typeof(Worker))` |
| D-02 | `IWorker` | `DynamicCandidates.cs:25` | lexical-string | fully qualified interface name string |
| D-03 | `Worker.Run()` | `DynamicCandidates.cs:27` | lexical-string | member-name string |
| D-04 | `Worker` | `DynamicCandidates.cs:31` | reflection-string-qualified-type | `Type.GetType(...)` 문자열 후보 |

D-01~04는 static reference result에 합쳐서는 안 된다. 이 fixture에는 실제 DI container registration을 위한 외부 package를 추가하지 않았으므로 문자열 후보로만 유지한다.

## 주석 정답

| ID | 대상 | 위치 | kind | 원문 |
|---|---|---|---|---|
| M-01 | `Catalog.Load(string)` | `Contracts.cs:10` | xml-documentation | `/// <summary>Loads one value.</summary>` |
| M-02 | `Catalog.Load(string)` | `Contracts.cs:11` | line-comment | `// This text is resolved lazily from the verified current source.` |

M-01/M-02는 generation에 저장된 본문이 아니라 현재 source digest를 재검증한 뒤 반환해야 한다. 같은 길이의 주석 수정이라도 이전 span·원문을 반환하면 실패다.
