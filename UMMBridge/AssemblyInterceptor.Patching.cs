using System;
using System.IO;
using System.Linq;
using System.Reflection;
using MelonLoader;
using Mono.Cecil;
using Mono.Cecil.Rocks;
using Mono.Cecil.Cil;

// ── Cecil IL rewriting for UMM mod assemblies ─────────────────────

namespace UMMBridge
{
    internal static partial class AssemblyInterceptor
    {
        /// <summary>
        /// Cecil-rewrite a UMM mod assembly: replace 0Harmony→0Harmony_UMM,
        /// optionally redirect Harmony.Patch() calls through ProxyPatch
        /// (when no transpiler is present), rewrite get_Location() calls
        /// and hardcoded "Mods" paths.
        /// </summary>
        private static byte[] PatchAssemblyFile(string filePath)
        {
            byte[] originalBytes;
            try { originalBytes = File.ReadAllBytes(filePath); }
            catch { return null; }

            AssemblyDefinition asmDef;
            try
            {
                asmDef = AssemblyDefinition.ReadAssembly(new MemoryStream(originalBytes));
            }
            catch
            {
                Melon<Bridge>.Logger.Warning(
                    $"Skipping (not a .NET assembly): {Path.GetFileName(filePath)}");
                return null;
            }

            using (asmDef)
            {
                bool modified = false;
                var harmonyRef = asmDef.MainModule.AssemblyReferences
                    .FirstOrDefault(r => r.Name == "0Harmony");

                if (harmonyRef != null)
                {
                    harmonyRef.Name = "0Harmony_UMM";
                    harmonyRef.PublicKeyToken = Array.Empty<byte>();
                    modified = true;

                    // Assembly-level transpiler check.  With the transpiler
                    // bridge enabled, Patch() calls stay rewritten even for
                    // assemblies that contain transpilers: ProxyPatch converts
                    // each transpiler to HarmonyX instead of letting it claim a
                    // second native detour.  Only a disabled bridge keeps such
                    // assemblies on the native runtime.
                    var bridgeTranspilers = TranspilerBridge.Enabled;
                    var hasTranspiler = ModUsesTranspiler(asmDef);
                    if (bridgeTranspilers || !hasTranspiler)
                    {
                        var rewrittenPatchCalls = RewriteHarmonyPatchCalls(asmDef);
                        modified |= rewrittenPatchCalls;
                        Melon<Bridge>.Logger.Msg(
                            $"{Path.GetFileName(filePath)} → " +
                            (rewrittenPatchCalls
                                ? "ProxyPatch" + (bridgeTranspilers && hasTranspiler ? " (transpilers bridged)" : "")
                                : "0Harmony_UMM (no Patch() calls rewritten)"));
                    }
                    else
                    {
                        Melon<Bridge>.Logger.Msg(
                            $"{Path.GetFileName(filePath)} → 0Harmony_UMM (transpiler assembly)");
                    }
                }

                // Creplay: fix cross-runtime as cast.  PriorityQueue<SkyHookEvent,ulong>
                // lives on Mono's heap; isinst in .NET 4.8 fails type-identity check.
                // Change isinst PriorityQueue`2 → isinst object so the cast always passes.
                if (asmDef.Name.Name == "Creplay")
                    modified |= FixCreplayCrossRuntime(asmDef);

                // Import Bridge.GetAssemblyLocation for Cecil rewriting
                MethodReference getLocationHelper = null;
                try
                {
                    var helper = typeof(Bridge).GetMethod(
                        nameof(Bridge.GetAssemblyLocation),
                        BindingFlags.Public | BindingFlags.Static,
                        null, new[] { typeof(Assembly) }, null);
                    if (helper != null)
                        getLocationHelper = asmDef.MainModule.ImportReference(helper);
                }
                catch { }

                foreach (var type in asmDef.MainModule.GetAllTypes())
                    foreach (var method in type.Methods)
                    {
                        if (!method.HasBody) continue;
                        modified |= RewriteHardcodedPaths(method);
                        if (getLocationHelper != null)
                            modified |= RewriteAssemblyGetLocation(method, getLocationHelper);
                    }

                if (!modified) return null;

                using var outMs = new MemoryStream();
                asmDef.Write(outMs);
                Melon<Bridge>.Logger.Msg($"Patched assembly: {Path.GetFileName(filePath)}");
                return outMs.ToArray();
            }
        }

        /// <summary>
        /// Replace calls to Assembly.get_Location() with
        /// Bridge.GetAssemblyLocation(Assembly) so that byte-loaded
        /// UMM mods can recover their original file paths at runtime.
        /// HarmonyX's patching of this virtual getter is unreliable
        /// (see: HarmonyX detour limitations).
        /// </summary>
        private static bool RewriteAssemblyGetLocation(MethodDefinition method, MethodReference helper)
        {
            bool modified = false;
            foreach (var inst in method.Body.Instructions)
            {
                if ((inst.OpCode != OpCodes.Call && inst.OpCode != OpCodes.Callvirt) ||
                    inst.Operand is not MethodReference mr ||
                    mr.Name != "get_Location" ||
                    mr.DeclaringType.FullName != "System.Reflection.Assembly")
                    continue;

                // get_Location is a virtual instance getter → callvirt;
                // our helper is a static method → call.
                inst.OpCode = OpCodes.Call;
                inst.Operand = helper;
                modified = true;
            }
            return modified;
        }

        private static bool RewriteHardcodedPaths(MethodDefinition method)
        {
            bool modified = false;
            foreach (var inst in method.Body.Instructions)
            {
                if (inst.OpCode != OpCodes.Ldstr || inst.Operand is not string s)
                    continue;

                string replaced = null;
                if (s == "Mods")
                    replaced = ModsDirName;
                else if (s.EndsWith("/Mods"))
                    replaced = s.Substring(0, s.Length - 4) + ModsDirName;
                else if (s.EndsWith("\\Mods"))
                    replaced = s.Substring(0, s.Length - 4) + ModsDirName;
                else if (s.EndsWith("/Mods/"))
                    replaced = s.Substring(0, s.Length - 5) + ModsDirName + "/";
                else if (s.EndsWith("\\Mods\\"))
                    replaced = s.Substring(0, s.Length - 5) + ModsDirName + "\\";
                else if (s.StartsWith("Mods/"))
                    replaced = ModsDirName + "/" + s.Substring(5);
                else if (s.StartsWith("Mods\\"))
                    replaced = ModsDirName + "\\" + s.Substring(5);
                else if (s.Contains("/Mods/"))
                    replaced = s.Replace("/Mods/", "/" + ModsDirName + "/");
                else if (s.Contains("\\Mods\\"))
                    replaced = s.Replace("\\Mods\\", "\\" + ModsDirName + "\\");

                if (replaced != null && replaced != s)
                {
                    inst.Operand = replaced;
                    modified = true;
                }
            }
            return modified;
        }

        /// <summary>
        /// Creplay's sortedKeyQueue_instance is populated via reflection at
        /// start_recording().  The PriorityQueue&lt;SkyHookEvent,ulong&gt;
        /// instance lives on Mono's heap; isinst in .NET 4.8 fails because
        /// CLR type identity differs between runtimes.  Fix:
        ///   - Field type: PriorityQueue&lt;...&gt; → System.Object
        ///   - isinst target: PriorityQueue`2 → System.Object
        /// This lets the reference pass through; later == comparison works.
        /// </summary>
        private static bool FixCreplayCrossRuntime(AssemblyDefinition asmDef)
        {
            bool modified = false;
            var objectType = asmDef.MainModule.TypeSystem.Object;

            // Change field type to object
            var creplayType = asmDef.MainModule.GetType("CREPLAY.Creplay");
            if (creplayType == null) return false;

            var skqField = creplayType.Fields.FirstOrDefault(f => f.Name == "sortedKeyQueue_instance");
            if (skqField != null && skqField.FieldType.MetadataType != MetadataType.Object)
            {
                skqField.FieldType = objectType;
                modified = true;
            }

            // Change every isinst PriorityQueue`2 to isinst object
            foreach (var type in asmDef.MainModule.GetAllTypes())
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody) continue;
                    foreach (var inst in method.Body.Instructions)
                    {
                        if (inst.OpCode == OpCodes.Isinst &&
                            inst.Operand is TypeReference tr &&
                            tr.FullName.StartsWith("System.Collections.Generic.PriorityQueue`2"))
                        {
                            inst.Operand = objectType;  // isinst System.Object
                            modified = true;
                        }
                    }
                }

            if (modified)
                Melon<Bridge>.Logger.Msg(
                    "  Creplay fix: sortedKeyQueue_instance → object");
            return modified;
        }
    }
}
