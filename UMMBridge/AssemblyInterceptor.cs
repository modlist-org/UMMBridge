using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MelonLoader;
using MelonLoader.Utils;
using Mono.Cecil;
using Mono.Cecil.Cil;

// ── Assembly interceptor ────────────────────────────────────────────

namespace UMMBridge
{
    internal static partial class AssemblyInterceptor
    {
        private static bool _initialized;
        internal static Assembly _ummHarmony;
        private static Assembly _harmonyX;

        /// <summary>
        /// Directory name under the game root where UMM mods reside.
        /// Used for path checks, Cecil IL rewriting, and runtime resolution.
        /// </summary>
        internal const string ModsDirName = "UMMMods";

        /// <summary>
        /// Full path to the UMM mods directory.
        /// Derived from ModsDirName at first access.
        /// </summary>
        internal static string ModsRootPath =>
            Path.Combine(MelonEnvironment.GameRootDirectory, ModsDirName);

        private static readonly Dictionary<string, string> _assemblyToDir = new();
        private static readonly Dictionary<string, Assembly> _assemblyCache = new();

        static AssemblyInterceptor()
        {
            AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
        }

        private static Assembly OnAssemblyResolve(object sender, ResolveEventArgs args)
        {
            var name = args.Name.Split(',')[0].Trim();

            if (name == "0Harmony_UMM" && _ummHarmony != null)
                return _ummHarmony;

            if (name == "0Harmony" && _harmonyX != null)
                return _harmonyX;

            // Return an already-loaded assembly if one exists.  Prevents
            // duplicate loads of game assemblies from UMMMods directories,
            // which would create second copies of the same generic types
            // and break cross-runtime as casts (Type#1 ≠ Type#2).
            var existing = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == name);
            if (existing != null)
                return existing;

            lock (_assemblyToDir)
            {
                if (_assemblyCache.TryGetValue(name, out var cached))
                    return cached;

                if (_assemblyToDir.TryGetValue(name, out var dir))
                {
                    var path = Path.Combine(dir, name + ".dll");
                    if (File.Exists(path))
                    {
                        Melon<Bridge>.Logger.Msg($"AssemblyResolve: served {name} from {dir}");
                        var asm = Assembly.LoadFrom(path);
                        _assemblyCache[name] = asm;
                        return asm;
                    }
                    return null;
                }
            }

            if (Directory.Exists(ModsRootPath))
            {
                try
                {
                    var found = Directory.GetFiles(ModsRootPath, name + ".dll", SearchOption.AllDirectories)
                                        .FirstOrDefault();
                    if (found != null)
                    {
                        var modDir = Path.GetDirectoryName(found);
                        lock (_assemblyToDir)
                        {
                            _assemblyToDir[name] = modDir;
                            var asm = Assembly.LoadFrom(found);
                            _assemblyCache[name] = asm;
                            Melon<Bridge>.Logger.Msg($"AssemblyResolve: served {name} (scanned from {modDir})");
                            return asm;
                        }
                    }
                }
                catch { }
            }

            return null;
        }

        /// <summary>
        /// True when the assembly was seen in a UMMMods directory.  Mods are
        /// registered here before their Cecil rewrite runs, so this is also
        /// how the bridge tells a mod call apart from UMM's own core calls.
        /// </summary>
        internal static bool IsRegisteredModAssembly(Assembly assembly)
        {
            if (assembly == null) return false;
            var name = assembly.GetName().Name;
            lock (_assemblyToDir)
                return _assemblyToDir.ContainsKey(name);
        }

        private static void RegisterModDirectory(string modAssemblyPath)
        {
            var dir = Path.GetDirectoryName(modAssemblyPath);
            if (dir == null) return;

            lock (_assemblyToDir)
            {
                var mainName = Path.GetFileNameWithoutExtension(modAssemblyPath);
                if (!_assemblyToDir.ContainsKey(mainName))
                    _assemblyToDir[mainName] = dir;

                foreach (var dll in Directory.GetFiles(dir, "*.dll"))
                {
                    var name = Path.GetFileNameWithoutExtension(dll);
                    if (!_assemblyToDir.ContainsKey(name))
                        _assemblyToDir[name] = dir;
                }
            }
        }

        // ── Initialization ──────────────────────────────────────────────

        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            _harmonyX = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "0Harmony" &&
                                     a.GetName().Version.Major >= 2);

            var ummHarmonyBytes = LoadUmmHarmonyAs_UMM();
            if (ummHarmonyBytes == null)
            {
                Melon<Bridge>.Logger.Warning(
                    "UMM 0Harmony.dll not found alongside UnityModManager.dll — " +
                    "mods using Harmony 2.x internals will NOT load.  Install/restore UnityModManager.");
                return;
            }
            _ummHarmony = Assembly.Load(ummHarmonyBytes);
            Melon<Bridge>.Logger.Msg($"Loaded UMM 0Harmony as 0Harmony_UMM v{_ummHarmony.GetName().Version}");

            // Hook 0Harmony_UMM's Harmony.Patch() via HarmonyX prefix.
            // Prefix_UMMPatch hands the patch to HarmonyX whenever HarmonyX
            // already owns the method entry (and for a UMM mod patch that
            // carries a transpiler), so both runtimes' patches coexist on one
            // detour instead of racing for the same JMP.
            try
            {
                var ummHarmonyType = _ummHarmony.GetType("HarmonyLib.Harmony");
                if (ummHarmonyType != null)
                {
                    var patchMethod = ummHarmonyType.GetMethod("Patch", new[] {
                        typeof(MethodBase),
                        ummHarmonyType.Assembly.GetType("HarmonyLib.HarmonyMethod"),
                        ummHarmonyType.Assembly.GetType("HarmonyLib.HarmonyMethod"),
                        ummHarmonyType.Assembly.GetType("HarmonyLib.HarmonyMethod"),
                        ummHarmonyType.Assembly.GetType("HarmonyLib.HarmonyMethod")
                    });
                    if (patchMethod != null)
                    {
                        var bridgePrefix = typeof(Bridge).GetMethod(
                            nameof(Bridge.Prefix_UMMPatch),
                            BindingFlags.Public | BindingFlags.Static);
                        if (bridgePrefix != null)
                        {
                            var h = new HarmonyLib.Harmony("UMMBridge.UMMHook");
                            h.Patch(patchMethod, new HarmonyMethod(bridgePrefix));
                            Melon<Bridge>.Logger.Msg(
                                "0Harmony_UMM::Harmony.Patch() hooked — transpiler bridge active");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Melon<Bridge>.Logger.Warning(
                    $"Failed to hook 0Harmony_UMM Harmony.Patch(): {ex.Message}");
            }

            try
            {
                var h = new HarmonyLib.Harmony("UMMBridge.AssemblyInterceptor");
                var loadFrom = AccessTools.Method(typeof(Assembly), "LoadFrom", new[] { typeof(string) });
                var prefix = AccessTools.Method(typeof(AssemblyInterceptor), nameof(Prefix_LoadFrom));

                if (loadFrom != null && prefix != null)
                {
                    h.Patch(loadFrom, new HarmonyMethod(prefix));
                    Melon<Bridge>.Logger.Msg(
                        "Assembly.LoadFrom interceptor active");
                }
            }
            catch (Exception ex)
            {
                Melon<Bridge>.Logger.Warning($"Interceptor initialization failed: {ex.Message}");
            }
        }

        private static byte[] LoadUmmHarmonyAs_UMM()
        {
            Assembly ummAssembly;
            try
            {
                ummAssembly = typeof(UnityModManagerNet.UnityModManager).Assembly;
            }
            catch (Exception ex)
            {
                Melon<Bridge>.Logger.Warning(
                    $"UnityModManagerNet.UnityModManager type not found. UnityModManager may be missing: {ex.Message}");
                return null;
            }

            var ummDir = Path.GetDirectoryName(ummAssembly.Location);
            var path = Path.Combine(ummDir, "0Harmony.dll");
            if (!File.Exists(path)) return null;

            byte[] originalBytes;
            try { originalBytes = File.ReadAllBytes(path); }
            catch { return null; }

            using (var ms = new MemoryStream(originalBytes))
            using (var asmDef = AssemblyDefinition.ReadAssembly(ms))
            {
                var originalVersion = asmDef.Name.Version;
                asmDef.Name = new AssemblyNameDefinition("0Harmony_UMM", originalVersion);
                asmDef.Name.PublicKeyToken = Array.Empty<byte>();
                asmDef.Name.PublicKey = Array.Empty<byte>();

                // ── Only Cecil injection: PatchFunctions.UpdateWrapper() ──
                // CreateClassProcessor/PatchAll calls bypass Harmony.Patch()
                // and reach UpdateWrapper via ProcessPatchJob.  This injection
                // routes a conflicting job through RouteConflictUpdateWrapper,
                // which applies it on HarmonyX instead of writing a second
                // native detour.
                var patchFunctionsType = asmDef.MainModule.GetType("HarmonyLib.PatchFunctions");
                if (patchFunctionsType == null)
                {
                    Melon<Bridge>.Logger.Warning(
                        "0Harmony_UMM: HarmonyLib.PatchFunctions not found — PatchAll() jobs will bypass the conflict router");
                }
                else
                {
                    var tracker3 = typeof(Bridge).GetMethod(
                        nameof(Bridge.IsMethodPatchedByProxy),
                        BindingFlags.Public | BindingFlags.Static,
                        null, new[] { typeof(MethodBase) }, null);
                    var routeMethod = typeof(Bridge).GetMethod(
                        nameof(Bridge.RouteConflictUpdateWrapper),
                        BindingFlags.Public | BindingFlags.Static,
                        null, new[] { typeof(MethodBase), typeof(object) }, null);

                    if (tracker3 == null || routeMethod == null)
                    {
                        Melon<Bridge>.Logger.Warning(
                            "0Harmony_UMM: Bridge conflict-routing methods not found — PatchAll() jobs will bypass the conflict router");
                    }
                    else
                    {
                        var trackerRef3 = asmDef.MainModule.ImportReference(tracker3);
                        var routeRef = asmDef.MainModule.ImportReference(routeMethod);
                        var injected = 0;

                        foreach (var m in patchFunctionsType.Methods)
                        {
                            if (m.Name != "UpdateWrapper" || !m.IsStatic || !m.HasBody) continue;

                            // The hand-written IL below assumes exactly
                            //   static MethodInfo UpdateWrapper(MethodBase, PatchInfo)
                            // Anything else (extra parameter, PatchInfo turned into
                            // a value type, different return) would emit IL that is
                            // invalid for the callee and only fails when
                            // 0Harmony_UMM is loaded — check the shape up front.
                            if (m.ReturnType.FullName != "System.Reflection.MethodInfo") continue;
                            if (m.Parameters.Count != 2) continue;
                            if (m.Parameters[0].ParameterType.FullName != "System.Reflection.MethodBase") continue;
                            if (m.Parameters[1].ParameterType.FullName != "HarmonyLib.PatchInfo") continue;
                            if (m.Body.Instructions.Count == 0) continue;

                            // A second rewrite of the same assembly must not inject
                            // the router twice (it would recurse).
                            if (m.Body.Instructions.Any(inst =>
                                    inst.OpCode == OpCodes.Call &&
                                    inst.Operand is MethodReference call &&
                                    call.Name == nameof(Bridge.IsMethodPatchedByProxy)))
                                continue;

                            var il = m.Body.GetILProcessor();
                            var first = m.Body.Instructions[0];

                            // ldarg.0 → IsMethodPatchedByProxy → brfalse FIRST
                            il.InsertBefore(first, il.Create(OpCodes.Ldarg, m.Parameters[0]));
                            il.InsertBefore(first, il.Create(OpCodes.Call, trackerRef3));
                            il.InsertBefore(first, il.Create(OpCodes.Brfalse, first));

                            // Conflict → let RouteConflictUpdateWrapper apply the
                            // job on HarmonyX (it returns the wrapper method).
                            il.InsertBefore(first, il.Create(OpCodes.Ldarg, m.Parameters[0]));
                            il.InsertBefore(first, il.Create(OpCodes.Ldarg, m.Parameters[1]));
                            il.InsertBefore(first, il.Create(OpCodes.Call, routeRef));
                            il.InsertBefore(first, il.Create(OpCodes.Ret));

                            injected++;
                        }

                        if (injected == 0)
                        {
                            // Silently shipping without the router would let
                            // PatchAll() write a second native detour onto a
                            // HarmonyX-owned method, so say so loudly.
                            Melon<Bridge>.Logger.Warning(
                                "0Harmony_UMM: no PatchFunctions.UpdateWrapper(MethodBase, PatchInfo) overload found — " +
                                "PatchAll() jobs will bypass the conflict router");
                        }
                        else
                        {
                            Melon<Bridge>.Logger.Msg(
                                $"0Harmony_UMM: conflict router injected into {injected} UpdateWrapper overload(s)");
                        }
                    }
                }

                using var outMs = new MemoryStream();
                asmDef.Write(outMs);
                return outMs.ToArray();
            }
        }

        // ── Harmony Prefix: LoadFrom ────────────────────────────────────

        /// <summary>
        /// Tracks the original file path for assemblies loaded via
        /// Assembly.Load(byte[]) so Bridge.GetAssemblyLocation() can
        /// recover the real path at runtime (used by Cecil-rewritten
        /// get_Location calls in patched mods).
        /// </summary>
        internal static readonly ConditionalWeakTable<Assembly, string> _assemblyOriginalPath = new();

        /// <summary>
        /// Set before Assembly.Load() so that static constructors
        /// can still resolve their original path via Bridge.GetAssemblyLocation()
        /// before the CWT entry is populated.
        /// </summary>
        [ThreadStatic]
        internal static string _currentLoadingPath;

        private static bool Prefix_LoadFrom(string assemblyFile, ref Assembly __result)
        {
            if (!assemblyFile.Contains(ModsDirName))
                return true;

            RegisterModDirectory(assemblyFile);

            var patched = PatchAssemblyFile(assemblyFile);
            if (patched == null)
                return true;

            // Write to temp file and load via LoadFrom to preserve CLR type
            // identity (LoadFrom context).  Assembly.Load(byte[]) creates a
            // no-context assembly whose generic types differ from the game's.
            _currentLoadingPath = assemblyFile;
            try
            {
                // Load from bytes (no-context) so that all assembly references
                // resolve through the default Load context — no duplicate
                // loading of game assemblies (Assembly-CSharp stays single).
                var asm = Assembly.Load(patched);
                _assemblyOriginalPath.Add(asm, assemblyFile);
                __result = asm;
                return false;
            }
            finally
            {
                _currentLoadingPath = null;
            }
        }
    }
}
