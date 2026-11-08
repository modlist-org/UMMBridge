using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MelonLoader;
using MonoMod.RuntimeDetour;
using UnityEngine;
using UnityModManagerNet;

namespace UMMBridge;

public class Bridge : MelonPlugin
{
    // Every UMM patch is applied by MelonLoader's HarmonyX.  0Harmony_UMM still
    // collects patches and sorts them, but its PatchFunctions.UpdateWrapper is
    // redirected here (see AssemblyInterceptor.LoadUmmHarmonyAs_UMM), so UMM and
    // MelonLoader patches on one method stack on a single detour instead of
    // fighting over it, and UMM mods work where UMM's own MonoMod cannot detour
    // (Apple Silicon).

    private const string TranspilerOwner = "UMMBridge.Transpilers";
    private const string ReplacementOwner = "UMMBridge.Replacements";

    private static readonly (string field, HarmonyPatchType type)[] RoutedPatchKinds =
    {
        ("prefixes", HarmonyPatchType.Prefix),
        ("postfixes", HarmonyPatchType.Postfix),
        ("finalizers", HarmonyPatchType.Finalizer),
    };

    // Per target method: UMM owner -> signature of the patches already mirrored into HarmonyX.
    // Only touched from UpdateWrapper and ReplaceMethod, which run under 0Harmony_UMM's
    // PatchProcessor lock (JALib takes it too).
    private static readonly Dictionary<MethodBase, Dictionary<string, string>> RoutedOwners = new();

    // Read by TranspilerAdapter while HarmonyX holds its lock, so it must never wait on ours.
    private static readonly ConcurrentDictionary<MethodBase, MethodInfo[]> UmmTranspilers = new();

    // Methods built by UMM-side patchers (JALib), hosted in HarmonyX.  Read by
    // ReplacementForwarder under HarmonyX's lock.
    private static readonly ConcurrentDictionary<MethodBase, MethodInfo> Replacements = new();

    private static readonly Dictionary<MethodBase, NativeDetour> ReverseDetours = new();

    /// <summary>Replaces 0Harmony_UMM's <c>PatchFunctions.UpdateWrapper</c>.</summary>
    public static MethodInfo UpdateWrapper(MethodBase original, object patchInfo)
    {
        var target = NeedsMethodRedirect(original, out var corrected) ? corrected : original;

        var byOwner = new Dictionary<string, List<(HarmonyPatchType type, object patch)>>();
        foreach (var (field, type) in RoutedPatchKinds)
        {
            foreach (var patch in Traverse.Create(patchInfo).Field<Array>(field).Value)
            {
                var owner = Traverse.Create(patch).Field<string>("owner").Value;
                if (!byOwner.TryGetValue(owner, out var list))
                    byOwner[owner] = list = new List<(HarmonyPatchType, object)>();
                list.Add((type, patch));
            }
        }
        var signatures = byOwner.ToDictionary(kv => kv.Key, kv => string.Join("|", kv.Value.Select(p => Signature(p.type, p.patch))));

        var ummTranspilers = Traverse.Create(patchInfo).Field<Array>("transpilers").Value;
        var transpilers = ummTranspilers.Length == 0
            ? Array.Empty<MethodInfo>()
            : ((IEnumerable<MethodInfo>)AccessTools.Method(AssemblyInterceptor._ummHarmony.GetType("HarmonyLib.PatchFunctions"), "GetSortedPatchMethods")
                .Invoke(null, new object[] { original, ummTranspilers, false })).ToArray();
        var hadAdapter = UmmTranspilers.TryGetValue(target, out var previousTranspilers);
        SetTranspilers(target, transpilers);

        if (!RoutedOwners.TryGetValue(target, out var routed))
            RoutedOwners[target] = routed = new Dictionary<string, string>();
        try
        {
            // UMM's patch list is authoritative again; a replacement hosted by
            // ReplaceMethod would apply it twice.
            if (Replacements.TryRemove(target, out _))
                new HarmonyLib.Harmony(ReplacementOwner).Unpatch(target, HarmonyPatchType.All, ReplacementOwner);
            foreach (var owner in routed.Keys.ToList())
            {
                if (!signatures.TryGetValue(owner, out var signature) || signature != routed[owner])
                {
                    routed.Remove(owner);
                    new HarmonyLib.Harmony(owner).Unpatch(target, HarmonyPatchType.All, owner);
                }
            }
            if (hadAdapter && transpilers.Length == 0)
                new HarmonyLib.Harmony(TranspilerOwner).Unpatch(target, HarmonyPatchType.Transpiler, TranspilerOwner);

            MethodInfo replacement = null;
            foreach (var group in byOwner)
            {
                if (routed.ContainsKey(group.Key)) continue;
                var processor = new HarmonyLib.Harmony(group.Key).CreateProcessor(target);
                foreach (var (type, patch) in group.Value)
                {
                    var method = ToHx(patch);
                    if (type == HarmonyPatchType.Prefix) processor.AddPrefix(method);
                    else if (type == HarmonyPatchType.Postfix) processor.AddPostfix(method);
                    else processor.AddFinalizer(method);
                }
                replacement = processor.Patch();
                routed[group.Key] = signatures[group.Key];
            }
            if (!hadAdapter && transpilers.Length > 0)
                replacement = new HarmonyLib.Harmony(TranspilerOwner).Patch(target, transpiler: new HarmonyMethod(typeof(Bridge), nameof(TranspilerAdapter)));
            // Nothing was (re)patched: rebuild once so a changed transpiler list
            // applies and UMM gets a replacement back.
            return replacement ?? new HarmonyLib.Harmony(TranspilerOwner).CreateProcessor(target).Patch();
        }
        catch
        {
            // UMM's Harmony drops a patch set that fails to build; HarmonyX would
            // keep it and fail every later rebuild of this method (MelonLoader
            // mods included), so take back what this call added.
            SetTranspilers(target, previousTranspilers ?? Array.Empty<MethodInfo>());
            foreach (var owner in byOwner.Keys.Where(o => !routed.ContainsKey(o)).Append(TranspilerOwner))
            {
                if (owner == TranspilerOwner && hadAdapter) continue;
                try
                {
                    new HarmonyLib.Harmony(owner).Unpatch(target, HarmonyPatchType.All, owner);
                }
                catch (Exception ex)
                {
                    Melon<Bridge>.Logger.Warning($"Rollback of {owner} on {target.FullDescription()} failed: {ex.Message}");
                }
            }
            throw;
        }
    }

    private static void SetTranspilers(MethodBase target, MethodInfo[] transpilers)
    {
        if (transpilers.Length > 0) UmmTranspilers[target] = transpilers;
        else UmmTranspilers.TryRemove(target, out _);
    }

    private static string Signature(HarmonyPatchType type, object patch)
    {
        var t = Traverse.Create(patch);
        var before = t.Field<string[]>("before").Value ?? Array.Empty<string>();
        var after = t.Field<string[]>("after").Value ?? Array.Empty<string>();
        return $"{type}:{t.Property<MethodInfo>("PatchMethod").Value.MethodHandle.Value}:{t.Field<int>("priority").Value}:" +
               $"{string.Join(",", before)}:{string.Join(",", after)}:{t.Field<bool>("debug").Value}";
    }

    private static HarmonyMethod ToHx(object patch)
    {
        var t = Traverse.Create(patch);
        return new HarmonyMethod(t.Property<MethodInfo>("PatchMethod").Value)
        {
            priority = t.Field<int>("priority").Value,
            before = t.Field<string[]>("before").Value,
            after = t.Field<string[]>("after").Value,
            debug = t.Field<bool>("debug").Value,
        };
    }

    /// <summary>
    /// One HarmonyX transpiler per target that runs that target's UMM
    /// transpilers (already in UMM's sort order), converting between HarmonyX's
    /// and 0Harmony_UMM's <c>CodeInstruction</c> types.
    /// </summary>
    public static IEnumerable<CodeInstruction> TranspilerAdapter(IEnumerable<CodeInstruction> instructions, ILGenerator generator, MethodBase original)
    {
        if (!UmmTranspilers.TryGetValue(original, out var transpilers))
            return instructions;
        IEnumerable current = ConvertInstructions(instructions, AssemblyInterceptor._ummHarmony.GetType("HarmonyLib.CodeInstruction"));
        foreach (var transpiler in transpilers)
        {
            var args = transpiler.GetParameters().Select(p =>
                p.ParameterType.IsAssignableFrom(typeof(ILGenerator)) ? generator :
                p.ParameterType.IsAssignableFrom(typeof(MethodBase)) ? original :
                (object)current).ToArray();
            current = transpiler.Invoke(null, args) as IEnumerable ?? current;
        }
        return (IEnumerable<CodeInstruction>)ConvertInstructions(current, typeof(CodeInstruction));
    }

    // Field-for-field copy; both CodeInstruction types have the Harmony 2 shape
    // (opcode, operand, labels, blocks).
    internal static IList ConvertInstructions(IEnumerable source, Type codeInstructionType)
    {
        var blockType = codeInstructionType.GetField("blocks").FieldType.GetGenericArguments()[0];
        var blockKindType = blockType.GetField("blockType").FieldType;
        var result = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(codeInstructionType));
        foreach (var instruction in source)
        {
            var src = Traverse.Create(instruction);
            var converted = Activator.CreateInstance(codeInstructionType, src.Field("opcode").GetValue(), src.Field("operand").GetValue());
            var dst = Traverse.Create(converted);
            dst.Field("labels").SetValue(new List<Label>(src.Field<List<Label>>("labels").Value ?? new List<Label>()));
            var blocks = dst.Field<IList>("blocks").Value;
            foreach (var block in src.Field<IEnumerable>("blocks").Value ?? Array.Empty<object>())
            {
                var b = Traverse.Create(block);
                blocks.Add(Activator.CreateInstance(blockType,
                    Enum.ToObject(blockKindType, Convert.ToInt32(b.Field("blockType").GetValue())),
                    b.Field("catchType").GetValue()));
            }
            result.Add(converted);
        }
        return result;
    }

    /// <summary>
    /// Called where UMM's own UpdateWrapper would detour <paramref name="original"/>.
    /// Only copies of that body get here: JALib builds every JAPatch (and every
    /// UMM unpatch) that way.  The replacement already contains the method's UMM
    /// patches, so it takes their place in HarmonyX and MelonLoader patches keep
    /// wrapping it.
    /// </summary>
    public static void ReplaceMethod(MethodBase original, MethodBase replacement)
    {
        var hosted = Replacements.ContainsKey(original);
        Replacements[original] = (MethodInfo)replacement;
        try
        {
            if (RoutedOwners.TryGetValue(original, out var routed))
            {
                foreach (var owner in routed.Keys)
                    new HarmonyLib.Harmony(owner).Unpatch(original, HarmonyPatchType.All, owner);
                routed.Clear();
            }
            if (UmmTranspilers.TryRemove(original, out _))
                new HarmonyLib.Harmony(TranspilerOwner).Unpatch(original, HarmonyPatchType.All, TranspilerOwner);
            if (hosted)
                new HarmonyLib.Harmony(ReplacementOwner).CreateProcessor(original).Patch();
            else
                new HarmonyLib.Harmony(ReplacementOwner).Patch(original,
                    transpiler: new HarmonyMethod(typeof(Bridge), nameof(ReplacementForwarder)) { priority = HarmonyLib.Priority.Last });
        }
        catch (Exception ex)
        {
            // Keep the mod working the way UMM would have, at the cost of
            // MelonLoader patches on this method.
            Replacements.TryRemove(original, out _);
            try { new HarmonyLib.Harmony(ReplacementOwner).Unpatch(original, HarmonyPatchType.All, ReplacementOwner); }
            catch { }
            Melon<Bridge>.Logger.Warning($"Could not host {original.FullDescription()} in HarmonyX, detouring directly: {ex.Message}");
            DetourMethod(original, replacement);
        }
    }

    /// <summary>Body of a hosted method: forward every argument to the replacement.</summary>
    public static IEnumerable<CodeInstruction> ReplacementForwarder(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        if (!Replacements.TryGetValue(original, out var replacement))
            return instructions;
        var argCount = original.GetParameters().Length + (original.IsStatic ? 0 : 1);
        var body = Enumerable.Range(0, argCount).Select(i => new CodeInstruction(OpCodes.Ldarg, i)).ToList();
        body.Add(new CodeInstruction(OpCodes.Call, replacement));
        body.Add(new CodeInstruction(OpCodes.Ret));
        return body;
    }

    /// <summary>
    /// Replaces 0Harmony_UMM's <c>PatchTools.DetourMethod</c>, which only reverse
    /// patches reach now.  The MonoMod.Core bundled with UMM's Harmony throws on
    /// Arm64; MelonLoader's MonoMod does not.
    /// </summary>
    public static void DetourMethod(MethodBase method, MethodBase replacement)
    {
        lock (ReverseDetours)
        {
            if (ReverseDetours.TryGetValue(method, out var old))
                old.Dispose();
            ReverseDetours[method] = new NativeDetour(method, replacement);
        }
    }

    private static readonly MethodInfo OriginalModsPathGetter = typeof(UnityModManager).GetProperty(nameof(UnityModManager.modsPath), BindingFlags.Public | BindingFlags.Static)?.GetGetMethod();
    private static readonly MethodInfo RedirectMethod = typeof(Bridge).GetMethod(nameof(RedirectModsPath), BindingFlags.Static | BindingFlags.NonPublic);

    public override void OnApplicationStarted()
    {
        AssemblyInterceptor.Initialize();

        var harmony = new HarmonyLib.Harmony("UMMBridge");
        harmony.Patch(OriginalModsPathGetter, new HarmonyMethod(RedirectMethod));

        LoggerInstance.Msg("Starting UMM");
        UnityModManager.Start();
        LoggerInstance.Msg("Loading UI");
        AccessTools.Method(typeof(Injector), "RunUI").Invoke(null, null);

        PatchUI();
        PatchCreplayTriggerMessage();
    }

    /// <summary>
    /// Creplay.TriggerMessage(string, float) calls UMM UI notifications
    /// which aren't fully functional under MelonLoader — the underlying
    /// singleton or UI component is null, throwing NRE.  This prefix
    /// intercepts the call, logs the message via MelonLoader instead,
    /// and skips the original to prevent the crash.
    /// </summary>
    private static void PatchCreplayTriggerMessage()
    {
        try
        {
            var creplayType = AccessTools.TypeByName("CREPLAY.Creplay");
            if (creplayType == null) return;

            var triggerMethod = AccessTools.Method(creplayType, "TriggerMessage",
                new[] { typeof(string), typeof(float) });
            if (triggerMethod == null) return;

            new HarmonyLib.Harmony("UMMBridge.CreplayFix")
                .Patch(triggerMethod, prefix: new HarmonyMethod(
                    typeof(Bridge), nameof(Pre_TriggerMessage)));

            Melon<Bridge>.Logger.Msg("Patched Creplay.TriggerMessage → MelonLoader log");
        }
        catch (Exception ex)
        {
            Melon<Bridge>.Logger.Warning($"Failed to patch Creplay.TriggerMessage: {ex.Message}");
        }
    }


    private static string _displayMessage = null;
    private static float _messageEndTime = 0f;

    // 在 Pre_TriggerMessage 中设置消息（并启动显示）
    public static bool Pre_TriggerMessage(string message, float dur = 5)
    {
        // 设置屏幕显示
        _displayMessage = message;
        _messageEndTime = Time.unscaledTime + dur; // dur 是显示时长（秒）

        // 如果尚未创建显示 GameObject，则创建
        if (_displayInstance == null)
        {
            var go = new GameObject("UMMBridge_MessageDisplay");
            GameObject.DontDestroyOnLoad(go);
            _displayInstance = go.AddComponent<MessageDisplay>();
        }

        return false; // 跳过原始方法
    }

    // MonoBehaviour 负责 OnGUI 绘制
    private class MessageDisplay : MonoBehaviour
    {
        // OnGUI runs every frame while a message is visible.  Building the style
        // (and its nested GUIStyleState) per frame produced steady garbage, so
        // it is created once on the GUI thread and reused.
        private GUIStyle _style;

        private void OnGUI()
        {
            if (string.IsNullOrEmpty(_displayMessage) || Time.unscaledTime > _messageEndTime)
            {
                _displayMessage = null;
                return;
            }

            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 20,
                    fontStyle = FontStyle.Normal,
                    normal = { textColor = Color.white },
                    alignment = TextAnchor.UpperLeft
                };
            }

            var rect = new Rect(10, 10, Screen.width - 20, 60);
            // 黑色阴影
            var shadowRect = new Rect(rect.x + 1, rect.y + 1, rect.width, rect.height);
            GUI.Label(shadowRect, _displayMessage, _style);
            GUI.Label(rect, _displayMessage, _style);
        }
    }

    private static MessageDisplay _displayInstance;

    private static bool RedirectModsPath(ref string __result)
    {
        var ummModsDir = AssemblyInterceptor.ModsRootPath;
        Directory.CreateDirectory(ummModsDir);
        __result = ummModsDir;
        return false;
    }

    /// <summary>
    /// Called from Cecil-rewritten mod assemblies to resolve
    /// Assembly.get_Location() for byte-loaded assemblies.
    /// Returns the original file path if cached, otherwise falls
    /// back to the real Location.
    /// </summary>
    public static string GetAssemblyLocation(Assembly asm)
    {
        // Fast path — most calls happen after CWT is populated
        if (AssemblyInterceptor._assemblyOriginalPath.TryGetValue(asm, out var path))
            return path;

        // Fallback for static constructors that run inside Assembly.Load():
        // the CWT entry hasn't been added yet, but the thread-local
        // _currentLoadingPath is set before Assembly.Load().
        var loadingPath = AssemblyInterceptor._currentLoadingPath;
        if (loadingPath != null)
            return loadingPath;

        return asm.Location;
    }

    /// <summary>
    /// Detect and redirect cross-runtime generic type identity splits.
    /// When a mod assembly is loaded via LoadFrom context, its references
    /// to generic type instantiations can resolve to different CLR types
    /// than the game's already-loaded copies — even when both claim to
    /// be from the same assembly (e.g. Assembly-CSharp loaded twice).
    /// Harmony detours on the wrong copy are silent no-ops.
    /// </summary>
    private static bool NeedsMethodRedirect(MethodBase original, out MethodBase corrected)
    {
        corrected = null;

        // Only handle PriorityQueue<SkyHookEvent, ulong>.Enqueue for now
        if (original.Name != "Enqueue") return false;
        if (original.DeclaringType?.Name != "PriorityQueue`2") return false;

        try
        {
            var scrCtrl = AccessTools.TypeByName("scrController");
            if (scrCtrl == null) return false;
            var field = scrCtrl.GetField("sortedKeyQueue",
                BindingFlags.Instance | BindingFlags.NonPublic |
                BindingFlags.Public | BindingFlags.Static);
            if (field == null) return false;
            var gameType = field.FieldType;

            // Same CLR type identity — no redirect needed
            if (original.DeclaringType == gameType) return false;

            // Find Enqueue on the game's actual type
            corrected = AccessTools.Method(gameType, "Enqueue");
            return corrected != null;
        }
        catch { return false; }
    }

    private void PatchUI()
    {
        try
        {
            var uiType = typeof(UnityModManager).Assembly.GetType("UnityModManagerNet.UnityModManager+UI");
            if (uiType == null)
            {
                LoggerInstance.Warning("UI nested type not found");
                return;
            }
            var windowFunc = AccessTools.Method(uiType, "WindowFunction");
            if (windowFunc == null)
            {
                LoggerInstance.Warning("UI.WindowFunction not found");
                return;
            }
            new HarmonyLib.Harmony("UMMBridge.UI").Patch(windowFunc, postfix: new HarmonyMethod(typeof(Bridge), nameof(Post_WindowFunction)));
            LoggerInstance.Msg("Mods Dir button added to UMM window");
        }
        catch (Exception ex)
        {
            LoggerInstance.Warning($"Failed to patch UMM UI: {ex.Message}");
        }
    }

    private static void Post_WindowFunction(object __instance)
    {
        var rect = Traverse.Create(__instance).Field("mWindowRect").GetValue<Rect>();
        var btnRect = new Rect(rect.width - 105, 3, 100, 22);
        if (GUI.Button(btnRect, "Mods Dir"))
        {
            var path = AssemblyInterceptor.ModsRootPath;
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
    }
}
