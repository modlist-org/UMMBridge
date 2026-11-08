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
        /// Full path to the UMM mods directory, next to MelonLoader's Mods and
        /// Plugins.  Not GameRootDirectory: on macOS that is
        /// <c>&lt;Game&gt;.app/Contents/MacOS</c>.
        /// </summary>
        internal static string ModsRootPath =>
            Path.Combine(MelonEnvironment.MelonBaseDirectory, ModsDirName);

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

            if (name == "0Harmony")
            {
                // A rewritten mod asks for 0Harmony_UMM, so this only happens
                // when the rewrite was missed.  The mod was compiled against
                // vanilla 0Harmony and HarmonyX is not a superset of it: it has
                // no PatchClassProcessor.Unpatch, no HarmonyLib.AccessToolsExtensions
                // and no AccessTools.MethodDelegate(MethodInfo, object, bool, Type[]).
                // Serve the 2.3.6 surface the mod was built against instead of
                // letting it bind HarmonyX and die on MissingMethodException.
                if (args.RequestingAssembly != null &&
                    IsRegisteredModAssembly(args.RequestingAssembly))
                {
                    Melon<Bridge>.Logger.Warning(
                        $"0Harmony requested by mod assembly " +
                        $"'{args.RequestingAssembly.GetName().Name}' was not rewritten " +
                        "to 0Harmony_UMM; serving the vanilla 2.3.6 surface");

                    if (_ummHarmony != null)
                        return _ummHarmony;
                }
                if (_harmonyX != null)
                    return _harmonyX;
            }

            // Core runtime assemblies must never be served out of a mods folder:
            // mods routinely bundle their own 0Harmony/Newtonsoft copies and a
            // stray copy there cannot satisfy the vanilla typing this bridge relies on.
            if (IsCoreAssemblyName(name))
            {
                // Serve the live copy when there is one: UMM itself lives in
                // Managed\UnityModManager, which the default probe does not
                // cover, so mods referencing UnityModManager only bind because
                // the already-loaded instance is found by simple name.
                return FindLoadedAssembly(name) ?? ResolveNetStandardForward(name);
            }

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
                        // Load through the Cecil pipeline: a mod's compile-time
                        // dependency can itself reference 0Harmony (e.g. a UMM
                        // loader whose core DLL lives next to it), and binding
                        // it raw leaves it on HarmonyX.
                        var asm = LoadModAssembly(path);
                        if (asm != null)
                        {
                            _assemblyCache[name] = asm;
                            Melon<Bridge>.Logger.Msg($"AssemblyResolve: served {name} from {dir}");
                        }
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
                        var asm = LoadModAssembly(found);
                        if (asm != null)
                        {
                            lock (_assemblyToDir)
                            {
                                _assemblyToDir[name] = modDir;
                                _assemblyCache[name] = asm;
                            }
                            Melon<Bridge>.Logger.Msg($"AssemblyResolve: served {name} (scanned from {modDir})");
                        }
                        return asm;
                    }
                }
                catch { }
            }

            return null;
        }

        /// <summary>
        /// A netstandard 2.0 facade (e.g. <c>UserLibs/netstandard.dll</c>) forwards
        /// types such as <c>RuntimeInformation</c> to facade assemblies Unity does
        /// not ship, which breaks SQLitePCL (TUFReplay).  Unity's own
        /// netstandard.dll forwards the same types to mscorlib/System/System.Core,
        /// so hand that out for any assembly a loaded netstandard facade refers to.
        /// </summary>
        private static Assembly ResolveNetStandardForward(string name)
        {
            var path = Path.Combine(Path.GetDirectoryName(typeof(UnityEngine.Object).Assembly.Location), "netstandard.dll");
            var forwardedByFacade = AppDomain.CurrentDomain.GetAssemblies()
                .Any(a => a.GetName().Name == "netstandard" && a.GetReferencedAssemblies().Any(r => r.Name == name));
            if (!forwardedByFacade || !File.Exists(path))
                return null;

            var runtime = Assembly.LoadFrom(path);
            // Never return it for a name it forwards to itself, or Mono would
            // chase that forward forever.
            if (runtime.GetReferencedAssemblies().Any(r => r.Name == name))
                return null;

            Melon<Bridge>.Logger.Msg($"AssemblyResolve: served {name} via runtime netstandard");
            return runtime;
        }

        /// <summary>
        /// Loads a mod-owned assembly through the Cecil rewrite, then from bytes.
        ///
        /// <para>Bytes, not LoadFrom: LoadFrom would publish the file into the
        /// LoadFrom context and let the CLR satisfy the mod's compile-time
        /// dependencies by probing sibling files itself, returning them raw.
        /// That is exactly how JipperOverlayer.dll kept binding HarmonyX
        /// 0Harmony and blew up on PatchClassProcessor.Unpatch().  With the
        /// bytes load the dependency is unresolvable, so it comes back through
        /// <see cref="OnAssemblyResolve"/> and gets rewritten here.</para>
        /// </summary>
        private static Assembly LoadModAssembly(string path)
        {
            try
            {
                RegisterModDirectory(path);

                var patched = PatchAssemblyFile(path);
                if (patched == null)
                {
                    // Nothing to rewrite.  Still load from bytes so that the
                    // assembly's own dependencies cannot be probed from disk
                    // behind the rewriter's back.
                    try { patched = File.ReadAllBytes(path); }
                    catch (Exception e)
                    {
                        Melon<Bridge>.Logger.Warning(
                            $"AssemblyResolve: cannot read {Path.GetFileName(path)}: {e.Message}");
                        return null;
                    }
                }

                _currentLoadingPath = path;
                try
                {
                    var asm = Assembly.Load(patched);
                    _assemblyOriginalPath.Add(asm, path);
                    return asm;
                }
                finally
                {
                    _currentLoadingPath = null;
                }
            }
            catch (Exception e)
            {
                Melon<Bridge>.Logger.Warning(
                    $"AssemblyResolve: failed to load {Path.GetFileName(path)}: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Assemblies owned by the loader/runtime.  A copy of one of these inside
        /// a mods folder must not be loaded by the bridge: it would create a
        /// second identity (and HarmonyX's 0Harmony does not carry the vanilla
        /// APIs UMM mods are compiled against).
        /// </summary>
        private static bool IsCoreAssemblyName(string name)
        {
            switch (name)
            {
                case "0Harmony":
                case "0Harmony_UMM":
                case "0HarmonyX":
                case "UnityModManager":
                case "dnlib":
                case "MelonLoader":
                case "Assembly-CSharp":
                case "Assembly-CSharp-firstpass":
                case "mscorlib":
                case "netstandard":
                    return true;
            }

            return name.StartsWith("System", StringComparison.Ordinal)
                || name.StartsWith("Mono", StringComparison.Ordinal)
                || name.StartsWith("Unity.", StringComparison.Ordinal)
                || name.StartsWith("UnityEngine", StringComparison.Ordinal)
                || name.StartsWith("Il2Cpp", StringComparison.Ordinal)
                || name.StartsWith("Microsoft.", StringComparison.Ordinal)
                || name.StartsWith("Newtonsoft.Json", StringComparison.Ordinal);
        }

        /// <summary>
        /// Assembly simple name of a path, tolerating UMM's cache naming
        /// (<c>Foo.dll.1234.cache</c> names the assembly <c>Foo</c>).
        /// </summary>
        private static string GetModuleName(string path)
        {
            var file = Path.GetFileName(path);
            var cut = file.IndexOf(".dll", StringComparison.OrdinalIgnoreCase);
            return cut >= 0 ? file.Substring(0, cut) : Path.GetFileNameWithoutExtension(file);
        }

        /// <summary>
        /// True when an assembly with this simple name is already live in the
        /// domain.  Re-loading it from a mods folder would fork its identity.
        /// </summary>
        private static bool IsAlreadyLoaded(string name)
            => FindLoadedAssembly(name) != null;

        /// <summary>The already-loaded assembly with this simple name, if any.</summary>
        private static Assembly FindLoadedAssembly(string name)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (string.Equals(asm.GetName().Name, name, StringComparison.OrdinalIgnoreCase))
                    return asm;
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

                // Every patch is built and applied by MelonLoader's HarmonyX; UMM's
                // bundled MonoMod.Core never runs.  UpdateWrapper keeps its old body
                // behind the redirect: JALib reverse patches it to build its own
                // patches, and the detour at its end now hands the finished method
                // to HarmonyX instead.
                var module = asmDef.MainModule;
                var updateWrapper = RedirectBody(module, "HarmonyLib.PatchFunctions", "UpdateWrapper", nameof(Bridge.UpdateWrapper), keepOriginal: true);
                foreach (var inst in updateWrapper?.Body.Instructions ?? Enumerable.Empty<Instruction>())
                {
                    if (inst.Operand is MethodReference { Name: "DetourMethod" } called &&
                        called.DeclaringType.FullName == "HarmonyLib.PatchTools")
                        inst.Operand = module.ImportReference(typeof(Bridge).GetMethod(nameof(Bridge.ReplaceMethod)));
                }
                RedirectBody(module, "HarmonyLib.PatchTools", "DetourMethod", nameof(Bridge.DetourMethod));
                RedirectBody(module, "HarmonyLib.AccessTools", "Identifiable", null);

                using var outMs = new MemoryStream();
                asmDef.Write(outMs);
                return outMs.ToArray();
            }
        }

        /// <summary>
        /// Makes a static method start with a call to the Bridge method taking the
        /// same arguments (or <c>return arg0</c> when bridgeMethod is null).  The old
        /// body is dropped unless keepOriginal, in which case it stays, unreachable.
        /// </summary>
        private static MethodDefinition RedirectBody(ModuleDefinition module, string typeName, string methodName, string bridgeMethod, bool keepOriginal = false)
        {
            var method = module.GetType(typeName)?.Methods.FirstOrDefault(m => m.Name == methodName && m.IsStatic && m.HasBody);
            if (method == null)
            {
                Melon<Bridge>.Logger.Warning($"0Harmony_UMM: {typeName}.{methodName} not found; left as is");
                return null;
            }

            var body = method.Body;
            if (!keepOriginal)
            {
                body.Instructions.Clear();
                body.ExceptionHandlers.Clear();
                body.Variables.Clear();
            }
            var il = body.GetILProcessor();
            var redirect = method.Parameters.Select(p => il.Create(OpCodes.Ldarg, p)).ToList();
            if (bridgeMethod != null)
                redirect.Add(il.Create(OpCodes.Call, module.ImportReference(typeof(Bridge).GetMethod(bridgeMethod))));
            redirect.Add(il.Create(OpCodes.Ret));
            for (var i = 0; i < redirect.Count; i++)
                body.Instructions.Insert(i, redirect[i]);
            return method;
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

            try
            {
                var moduleName = GetModuleName(assemblyFile);

                // A runtime assembly that a mod happens to ship next to itself is
                // not a mod.  Letting the CLR satisfy it keeps a single identity
                // for it; loading it from here would fork the type system.
                if (IsCoreAssemblyName(moduleName) || IsAlreadyLoaded(moduleName))
                    return true;

                RegisterModDirectory(assemblyFile);

                var patched = PatchAssemblyFile(assemblyFile);
                if (patched == null)
                {
                    // Nothing to rewrite in this file, but still take over the
                    // load.  Returning true lets UMM's LoadFrom publish the file
                    // into the LoadFrom context, and the CLR then satisfies the
                    // mod's own compile-time dependencies by probing that same
                    // folder — handing them back raw and unrewritten.
                    // JipperOverlayer.dll (the core DLL sitting next to its UMM
                    // loader) was bound to HarmonyX 0Harmony exactly that way.
                    try { patched = File.ReadAllBytes(assemblyFile); }
                    catch { return true; }
                }

                // Load from bytes, never LoadFrom.  The bytes stay patched, but
                // more importantly a no-context assembly cannot satisfy its own
                // compile-time references by probing sibling files — each of them
                // comes back through OnAssemblyResolve and gets rewritten too.
                // (The no-context copy of a *game* assembly would fork its
                // generic types, so the resolve handler always prefers a live
                // copy.)
                _currentLoadingPath = assemblyFile;
                try
                {
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
            catch (Exception e)
            {
                // Whatever goes wrong in the rewrite, the mod still has to load.
                // Falling back to its own bytes (not to LoadFrom) keeps the
                // no-context guarantee — and therefore keeps the rewrite
                // guarantee for its dependencies — and only leaves this one file
                // unpatched.
                Melon<Bridge>.Logger.Warning(
                    $"LoadFrom interception failed for {Path.GetFileName(assemblyFile)} " +
                    $"({e.GetType().Name}: {e.Message}) — loading it unrewritten");

                _currentLoadingPath = assemblyFile;
                try
                {
                    var raw = Assembly.Load(File.ReadAllBytes(assemblyFile));
                    _assemblyOriginalPath.Add(raw, assemblyFile);
                    __result = raw;
                    return false;
                }
                catch
                {
                    return true;
                }
                finally
                {
                    _currentLoadingPath = null;
                }
            }
        }
    }
}
