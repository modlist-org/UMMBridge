# Changelog

## v0.0.7

### 中文

- **修复 `PatchClassProcessor.Unpatch()` 的 `MissingMethodException`** — 带「加载器 + 核心 DLL」结构的 UMM mod（如 JipperOverlayer：`JipperOverlayer.Loader.UMM.dll` 引用同目录的 `JipperOverlayer.dll`）此前会绕过 Cecil 重写：加载器本身没有 `0Harmony` 引用，重写无事可做，于是回退到 UMM 自己的 `Assembly.LoadFrom`，CLR 随即用 LoadFrom 上下文的同目录探测把核心 DLL 原样绑定，`0Harmony` 落到了 HarmonyX 2.10.2 上。而 HarmonyX 并没有 `PatchClassProcessor.Unpatch()`（也没有 `HarmonyLib.AccessToolsExtensions`、`AccessTools.MethodDelegate(MethodInfo, object, bool, Type[])`），mod 一按按钮就抛异常
- **改写 `Assembly.LoadFrom` 前缀** — 即使文件里没有可重写的内容，也改为 `Assembly.Load(byte[])` 从字节加载；无上下文程序集无法自行探测同目录依赖，于是依赖会走 `AssemblyResolve`，在那里完成重写后再加载。实测日志：`JipperOverlayer.dll → ProxyPatch` / `Patched assembly: JipperOverlayer.dll` / `AssemblyResolve: served JipperOverlayer from ...\UMMMods\JipperOverlayer`
- **解析器兜底改为 2.3.6** — 若某个已注册 mod 仍未重写就来要 `0Harmony`，现在返回 0Harmony_UMM（真实 2.3.x API 面）而不是 HarmonyX，只会记一条告警而不是直接崩溃；非 mod 调用方仍拿 HarmonyX
- **核心程序集短路** — `OnAssemblyResolve` 对 `0Harmony`/`UnityModManager`/`System.*`/`UnityEngine.*` 等核心名字只返回已加载的实例，绝不再从 mod 目录里加载同名副本，避免第二份类型标识；同时按简单名回退，保证 `Managed\UnityModManager` 里的 UnityModManager 能被 mod 绑定
- **离线核对** — 用 Cecil 扫描了全部已装 UMM mod 对 `0Harmony` 的引用：真实 2.3.6 全部命中，HarmonyX 2.10.2 缺 5 个成员（`PatchClassProcessor.Unpatch`、`HarmonyLib.AccessToolsExtensions` 的 Field/Method/Property、`AccessTools.MethodDelegate(MethodInfo, object, bool, Type[])`）

### English

- **Fixes the `MissingMethodException` on `PatchClassProcessor.Unpatch()`** — UMM mods shaped as "loader + core DLL" (e.g. JipperOverlayer: `JipperOverlayer.Loader.UMM.dll` references the sibling `JipperOverlayer.dll`) bypassed the Cecil rewrite entirely: the loader has no `0Harmony` reference, so there was nothing to rewrite and the bridge handed the load back to UMM's own `Assembly.LoadFrom`. The CLR then satisfied the loader's reference to the core DLL by probing the same directory inside the LoadFrom context, binding it raw; its `0Harmony` reference landed on HarmonyX 2.10.2, which has no `PatchClassProcessor.Unpatch()` (nor `HarmonyLib.AccessToolsExtensions`, nor `AccessTools.MethodDelegate(MethodInfo, object, bool, Type[])`), so the first button press threw
- **`Assembly.LoadFrom` prefix reworked** — even when a file contains nothing to rewrite it is now loaded with `Assembly.Load(byte[])`. A no-context assembly cannot probe sibling files on its own, so its dependencies come back through `AssemblyResolve` and get rewritten before loading. Observed: `JipperOverlayer.dll → ProxyPatch` / `Patched assembly: JipperOverlayer.dll` / `AssemblyResolve: served JipperOverlayer from ...\UMMMods\JipperOverlayer`
- **Resolver fallback now serves 2.3.6** — if a registered mod still asks for `0Harmony` without having been rewritten, it now receives 0Harmony_UMM (the real 2.3.x API surface) instead of HarmonyX, which logs a warning instead of crashing; non-mod callers keep getting HarmonyX
- **Core assembly short-circuit** — `OnAssemblyResolve` returns only an already-loaded instance for core names (`0Harmony`, `UnityModManager`, `System.*`, `UnityEngine.*`, …) and never loads a same-named copy out of a mods folder, avoiding a second type identity; the simple-name fallback keeps the `Managed\UnityModManager` copy bindable by mods
- **Offline audit** — Cecil-scanned every installed UMM mod's `0Harmony` references: real 2.3.6 satisfies all of them, while HarmonyX 2.10.2 is missing 5 members (`PatchClassProcessor.Unpatch`, `HarmonyLib.AccessToolsExtensions` Field/Method/Property, `AccessTools.MethodDelegate(MethodInfo, object, bool, Type[])`)

## v0.0.6

### 中文

- **Transpiler 桥接** — 新增 `TranspilerBridge`：在 0Harmony_UMM 与 HarmonyX 的 `CodeInstruction` 之间做双向转换（opcode、operand、labels、exception blocks），把 UMM mod 的 transpiler 包装为 HarmonyX 兼容的 transpiler 并注册到同一运行时；`PatchAll`（UpdateWrapper 注入）与直接 `Patch()`（ProxyPatch）两条路径均已接入
- **消除核心限制** — UMM mod 的 transpiler 与 MelonLoader mod 的补丁现在可以在同一方法上共存（前提是该方法未被 UMM core 补丁原生占有）；UMM mod 之间不再依赖加载顺序（全部落在 HarmonyX）
- **不再静默丢弃** — 转换失败会记录具体原因；0Harmony_UMM 已持有入口时该补丁加入既有原生表，仅 HarmonyX 持有入口时才跳过（拒绝写入第二个 detour 破坏已有补丁）
- **补丁去重** — UpdateWrapper 路由前检查 HarmonyX 上是否已注册同一补丁方法，避免重复执行
- **开关** — `UMMBridge.cfg` 中 `TranspilerBridge=false` 或环境变量 `UMMBRIDGE_TRANSPILER_BRIDGE=0` 可恢复旧行为
- **离线验证** — 使用真实 0Harmony 2.3.6 与 HarmonyX 2.10.2 验证了转换、包装、label 与 operand 往返（无游戏环境）
- **构建** — 原有 `HintPath` 保持不变；缺少游戏目录时自动回退到仓库 `Libs/`（可用 `-p:LibsDir=...` 覆盖），`Libs/` 已忽略

#### 关键修复（v0.0.6 复审）

- **空方法体** — transpiler 返回 `null` 时原样回放输入指令流；返回非指令序列或元素类型不符时抛出带 transpiler 名称的异常。此前会产出空序列，HarmonyX 据此生成空方法体，被补丁的代码静默消失
- **热路径去反射** — `CodeInstruction` 的 opcode/operand/labels/blocks 读写、指令与异常块的构造、列表分配全部改为一次性编译的委托，不再逐指令反射
- **注入校验** — `PatchFunctions.UpdateWrapper` 的 Cecil 注入现在校验 `(MethodBase, PatchInfo)` 参数形状、`MethodInfo` 返回类型并做幂等检查；签名不符或找不到重载时明确告警，此前会静默不注入
- **反射缓存** — 跨程序集读取 0Harmony_UMM 的成员改为按 `(类型, 名称)` 缓存；读取失败时输出“该补丁未生效”的明确告警，不再静默 continue
- **移除永不失效的负缓存** — 冲突检查原先把“已确认无 HarmonyX 补丁”缓存到进程结束；任何 mod 之后加的补丁都会被忽略，导致原生 detour 覆盖 HarmonyX detour。现改为每次查询
- **调用栈只走一次** — 冲突检查原先每条日志各构造一次 `StackTrace`；现在一次遍历同时得出“是否 UMM mod 调用”与调用者名，诊断日志需 `UMMBRIDGE_VERBOSE=1` 才输出
- **移除补丁迁移** — `MigratePatches`/`TryResolveConflict` 整条路径已删除：bridge 启用时 mod transpiler 直接桥接到 HarmonyX，无需把已有的 HarmonyX 补丁搬到原生运行时；bridge 关闭时含 transpiler 的程序集保持原生。两条路径都不再存在“迁移到一半”的状态
- **detour 唯一性** — `Prefix_UMMPatch`（含非 transpiler 分支）、`InvokeNativePatch`、`ProxyPatch` 现在都会在“HarmonyX 已持有、且 0Harmony_UMM 尚未持有该方法入口”时拒绝原生 detour，而不是覆盖它；0Harmony_UMM 已持有入口时新补丁只是加入既有原生表，不再被误拒
- **Cecil 重写返回值** — 重写 `Harmony.Patch()` 调用时不再 `pop; ldnull`（把返回的 `MethodInfo` 变成 null），保留代理的真实返回值；参数多于 5 个的重载不再改写，避免求值栈失衡
- **GUI** — 消息显示的 `GUIStyle` 改为缓存，消除每帧分配

### English

- **Transpiler bridge** — new `TranspilerBridge` converts `CodeInstruction` both ways between 0Harmony_UMM and HarmonyX (opcode, operand, labels, exception blocks) and wraps a UMM transpiler into a HarmonyX-compatible one, so it is registered on a single runtime; both `PatchAll` (UpdateWrapper injection) and direct `Patch()` (ProxyPatch) paths are covered
- **Core limitation removed** — UMM mod transpilers can now coexist with MelonLoader mod patches on the same method (provided no UMM core patch owns the method natively); UMM mod-to-mod patches no longer depend on load order (all live on HarmonyX)
- **No silent drops** — conversion failures log the reason; when 0Harmony_UMM already owns the entry the patch joins the existing native table, and only a method owned solely by HarmonyX is skipped (a second detour is refused instead of corrupting the existing patch)
- **Patch deduplication** — UpdateWrapper routing checks HarmonyX for the same patch method before adding it, avoiding double execution
- **Toggle** — `TranspilerBridge=false` in `UMMBridge.cfg`, or `UMMBRIDGE_TRANSPILER_BRIDGE=0`, restores the previous behaviour
- **Offline verification** — conversion, wrapping, label and operand round-trips exercised against real 0Harmony 2.3.6 and HarmonyX 2.10.2 (no game install)
- **Build** — existing `HintPath`s are untouched; missing game folders fall back to the repository's `Libs/` (override with `-p:LibsDir=...`), which is git-ignored

#### Reliability fixes (v0.0.6 review)

- **Empty method body** — a transpiler returning `null` now replays the incoming instruction stream, and a non-sequence/wrong-element result throws with the transpiler's name. Previously an empty sequence was returned, which HarmonyX turned into an empty method body, silently deleting the patched code
- **No per-instruction reflection** — reading/writing `opcode`/`operand`/`labels`/`blocks`, constructing instructions and exception blocks, and allocating the lists all go through once-compiled delegates
- **Injection validation** — the `PatchFunctions.UpdateWrapper` Cecil injection now verifies the `(MethodBase, PatchInfo)` shape, the `MethodInfo` return type, and is idempotent; a mismatch or missing overload is reported instead of silently skipping the injection
- **Cached reflection** — 0Harmony_UMM member lookups are cached per `(type, name)`; a failed read logs an explicit "this patch was NOT applied" warning instead of `continue`-ing silently
- **Stale cache removed** — the conflict check used to cache "verified clean" for the process lifetime, so any patch added later was ignored and a native detour could land on a HarmonyX detour. It now queries every time
- **One stack walk** — the check used to build a `StackTrace` per log line; a single pass now yields both "is this a UMM mod?" and the caller name, and diagnostics require `UMMBRIDGE_VERBOSE=1`
- **Patch migration removed** — the whole `MigratePatches`/`TryResolveConflict` path is deleted: with the bridge on, a mod transpiler is converted to HarmonyX and needs no move of the existing HarmonyX patches to the native runtime; with the bridge off, assemblies containing transpilers stay native. There is no longer any half-migrated state
- **Single detour invariant** — `Prefix_UMMPatch` (including its non-transpiler branch), `InvokeNativePatch` and `ProxyPatch` now refuse a native detour when HarmonyX already owns the method and 0Harmony_UMM does not, instead of overwriting it; when 0Harmony_UMM already owns the entry a new patch only joins the existing native table instead of being wrongly refused
- **Cecil rewrite return value** — rewriting `Harmony.Patch()` no longer emits `pop; ldnull` (turning the returned `MethodInfo` into null); overloads with more than five parameters are left alone to keep the evaluation stack balanced
- **GUI** — the message `GUIStyle` is cached, removing a per-frame allocation

## v0.0.5

### 中文

- **跨运行时类型兼容** — 对 Creplay 执行 IL 重写，将 `sortedKeyQueue_instance` 类型及所有 `isinst PriorityQueue` 改为 `object`，绕过 CLR 类型身份检查；在 `ProxyPatch` 中增加 `NeedsMethodRedirect`，将泛型方法补丁重定向到游戏已加载的实例
- **程序集加载策略** — 改用 `LoadFrom` 临时文件加载，保留加载上下文，避免泛型类型分裂；`AssemblyResolve` 拦截 `Assembly-CSharp` 等核心程序集，直接返回已加载实例，防止重复加载
- **冲突迁移（Migration）** — 当 transpiler 补丁到达已被 ProxyPatch 占用的方法时，自动将 HarmonyX 上的非 transpiler 补丁迁移到 0Harmony_UMM，使同一运行时可叠加所有补丁
- **三层冲突解决** — UpdateWrapper IL 注入（CreateClassProcessor 路径）、Harmony.Patch() 前缀（直接 Patch 路径）、ProxyPatch 路由（Cecil 重写），三层兜底无死角
- **InvokeNativePatch 提取** — 统一原生补丁入口，解决加载顺序导致非 transpiler 补丁被静默丢弃的问题
- **MigratePatches 异常安全** — TryResolveConflict 层 catch，迁移失败走路由回退，不再崩模载
- **Creplay 消息显示** — 补丁 `TriggerMessage`，在游戏左上角显示消息，使用 `Time.unscaledTime` 不受暂停影响
- **Assembly.Read 异常保护** — 非 .NET 程序集跳过报错，不再抛 BadImageFormatException
- **已知限制** — 跨运行时 transpiler 补同一函数必失其一（CodeInstruction 类型属于不同程序集）

---

### English

- **Cross-runtime type compatibility** — IL-rewrite Creplay to change `sortedKeyQueue_instance` field type and all `isinst PriorityQueue` to `object`, bypassing CLR type identity checks; add `NeedsMethodRedirect` in `ProxyPatch` to redirect generic method patches to the game's actual instance
- **Assembly loading strategy** — Switch to `LoadFrom` via temp file to preserve load context, preventing generic type splitting; intercept `Assembly-CSharp` in `AssemblyResolve` to reuse already-loaded instances, avoiding duplicate loads
- **Conflict migration** — When a transpiler patch arrives at a method already claimed by ProxyPatch, non-transpiler patches on HarmonyX are automatically migrated to 0Harmony_UMM, allowing a single runtime to host all patches
- **Three-layer resolution** — UpdateWrapper IL injection (CreateClassProcessor path), Harmony.Patch() prefix (direct Patch path), ProxyPatch routing (Cecil rewrite) — no gap
- **InvokeNativePatch extracted** — Unified native entry point, fixing silent drop of non-transpiler patches due to load order
- **MigratePatches exception safety** — Caught in TryResolveConflict, routing fallback on failure, no crash
- **Creplay message display** — Patch `TriggerMessage` to show on-screen in top-left corner using unscaled time (unaffected by pause)
- **Assembly.Read guard** — Skip non-.NET assemblies with warning instead of BadImageFormatException
- **Known limitation** — Cross-runtime transpiler patches on the same method cannot coexist (CodeInstruction types belong to different assemblies)
---



### 中文

- **双 Harmony 路由架构** — UMM 所有 Mod 固定使用 0Harmony_UMM（UMM 自带的 Harmony v2.3.6），MelonLoader 使用 HarmonyX，彻底消除两个 MonoMod 运行时在同一个方法上产生 detour 冲突的问题
- **Assembly 拦截器重构** — Hook Assembly.LoadFrom，通过 Harmony Prefix 在加载 UMM Mod 时进行 Mono.Cecil IL 改写
- **0Harmony → 0Harmony_UMM 重写** — 自动将 Mod 中对 0Harmony 的引用改为 0Harmony_UMM，确保使用 UMM 的 Harmony 运行时
- **Assembly.get_Location() 重写** — 替换为 Bridge.GetAssemblyLocation()，支持 ConditionalWeakTable + ThreadStatic 双路回退，让 byte-loaded 程序集能恢复原始文件路径
- **硬编码路径重写** — 自动将 Mod 中的 "Mods" 目录字符串替换为 "UMMMods"
- **PatchAllUncategorized → PatchAll 重写** — 兼容 Harmony 2.3.x 旧 API
- **HarmonyPatchCategory 跨运行时兼容** — 通过 TypeResolve 存根 + Cecil 扫描注册 Category，避免 HarmonyX 下 TypeLoadException
- **AssemblyResolve 缓存** — 避免重复加载程序集，线程安全
- **ModsDir 按钮** — UMM 窗口中新增 "Mods Dir" 按钮，一键打开 UMMMods 目录
- **已知限制** — 两个独立 MonoMod 运行时无法在同一方法上共存，0Harmony_UMM 的 detour 优先

### English

- **Dual-Harmony routing** — UMM mods always use 0Harmony_UMM (UMM's Harmony v2.3.6), MelonLoader uses HarmonyX, eliminating native detour conflicts between two MonoMod runtimes
- **Assembly interceptor refactored** — Hook Assembly.LoadFrom via Harmony prefix, applying Mono.Cecil IL rewriting when loading UMM mod assemblies
- **0Harmony → 0Harmony_UMM rewrite** — Redirect mod references from 0Harmony to 0Harmony_UMM at the assembly level
- **Assembly.get_Location() rewrite** — Replace with Bridge.GetAssemblyLocation(), backed by ConditionalWeakTable with ThreadStatic fallback for static constructors
- **Hardcoded path rewriting** — Automatically replace "Mods" strings with "UMMMods" in mod assemblies
- **PatchAllUncategorized → PatchAll rewrite** — Backward compatibility with Harmony 2.3.x API
- **Cross-runtime HarmonyPatchCategory** — TypeResolve stub + Cecil scanning to prevent TypeLoadException under HarmonyX
- **AssemblyResolve caching** — Thread-safe concurrent dictionary to prevent duplicate assembly loads
- **Mods Dir button** — New button in the UMM window to open the UMMMods directory
- **Known limitation** — Two independent MonoMod runtimes cannot share the same method's detour; 0Harmony_UMM detour takes priority

---

## v0.0.3

### 中文

- **Harmony 兼容层** — 运行时桥接 `PatchAllUncategorized` / `PatchCategory` 方法（通过 Harmony Prefix + Mono.Cecil IL 重写，在加载 UMM Mod 程序集时自动改写缺失的 Harmony 调用为 `PatchAll`）
- **HarmonyPatchCategory 存根** — 通过 `AppDomain.TypeResolve` 为携带 `[HarmonyPatchCategory]` 属性的 Mod 提供类型存根，避免 TypeLoadException
- **路径重定向** — 自动重写 UMM Mod 中硬编码的 `"Mods"` 目录字符串为 `"UMMMods"`（运行期 IL 改写），兼容不通过 `UnityModManager.modsPath` 获取路径的 Mod

### English

- **Harmony compatibility layer** — Runtime bridging for `PatchAllUncategorized` / `PatchCategory` methods via Harmony prefix + Mono.Cecil IL rewriting on assembly load
- **HarmonyPatchCategory stub** — Type stub via `AppDomain.TypeResolve` for mods using `[HarmonyPatchCategory]`, preventing TypeLoadException under HarmonyX
- **Path redirection** — Automatic rewriting of hardcoded `"Mods"` directory strings to `"UMMMods"` at the IL level
