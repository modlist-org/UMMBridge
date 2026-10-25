using System.Linq;
using System.Reflection;
using MelonLoader;
using Mono.Cecil;
using Mono.Cecil.Rocks;
using Mono.Cecil.Cil;

// ── Transpiler detection + Harmony.Patch() call rewriting ──────────

namespace UMMBridge
{
    internal static partial class AssemblyInterceptor
    {
        /// <summary>
        /// Check if an assembly uses Harmony transpilers (which require
        /// 0Harmony_UMM's native Patch() — HarmonyX's transpiler calling
        /// convention is incompatible).  Detection: [HarmonyTranspiler]
        /// attribute plus IL signature heuristic as safety net.
        /// </summary>
        private static bool ModUsesTranspiler(AssemblyDefinition asmDef)
        {
            foreach (var type in asmDef.MainModule.GetAllTypes())
                foreach (var method in type.Methods)
                {
                    if (method.CustomAttributes.Any(a =>
                            a.AttributeType.Name == "HarmonyTranspiler"))
                        return true;

                    // Safety net: return type + param both contain
                    // "IEnumerable" and "CodeInstruction" in their
                    // FullName → strongly suggests IEnumerable<CodeInstruction>
                    // or IEnumerator<CodeInstruction>.  The triple AND is tight
                    // enough that false positives are effectively zero.
                    if (method.ReturnType.FullName.Contains("IEnumerable") &&
                        method.ReturnType.FullName.Contains("CodeInstruction") &&
                        method.Parameters.Any(p =>
                            p.ParameterType.FullName.Contains("IEnumerable") &&
                            p.ParameterType.FullName.Contains("CodeInstruction")))
                        return true;
                }
            return false;
        }

        /// <summary>
        /// Rewrite every Harmony.Patch() call within an assembly to
        /// Bridge.ProxyPatch(), so detours go through HarmonyX's MonoMod
        /// instead of 0Harmony_UMM's.  Used for mods without transpilers.
        /// </summary>
        private static bool RewriteHarmonyPatchCalls(AssemblyDefinition asmDef)
        {
            var proxyPatch = typeof(Bridge).GetMethod(
                nameof(Bridge.ProxyPatch),
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(object), typeof(MethodBase), typeof(object),
                        typeof(object), typeof(object), typeof(object) },
                null);
            if (proxyPatch == null) return false;

            var proxyRef = asmDef.MainModule.ImportReference(proxyPatch);
            bool modified = false;

            foreach (var type in asmDef.MainModule.GetAllTypes())
                foreach (var method in type.Methods)
                    modified |= RewriteHarmonyPatchCalls(method, proxyRef);

            return modified;
        }

        private static bool RewriteHarmonyPatchCalls(MethodDefinition method, MethodReference proxyRef)
        {
            if (!method.HasBody) return false;

            // Collect call sites FIRST (snapshot), then modify.  Iterating the
            // original collection and calling InsertBefore/InsertAfter would
            // invalidate the enumerator (InvalidOperationException).
            var targets = method.Body.Instructions
                .Where(inst =>
                {
                    if (inst.OpCode != OpCodes.Call && inst.OpCode != OpCodes.Callvirt)
                        return false;
                    if (inst.Operand is not MethodReference mr) return false;
                    if (mr.DeclaringType.FullName != "HarmonyLib.Harmony") return false;
                    if (mr.Name != "Patch") return false;
                    if (mr.Parameters.Count < 1) return false;
                    return mr.Parameters[0].ParameterType.FullName == "System.Reflection.MethodBase";
                })
                .ToList();

            if (targets.Count == 0) return false;

            var il = method.Body.GetILProcessor();

            foreach (var inst in targets)
            {
                var mr = (MethodReference)inst.Operand;

                // ProxyPatch() takes (harmony, original, prefix, postfix,
                // transpiler, finalizer).  Harmony 2.3.x exposes overloads with
                // 1..5 of those, and the compiler only emits the arguments the
                // mod actually passed, so pad the tail with nulls.
                if (mr.Parameters.Count > 5)
                {
                    // Wider overload (e.g. HarmonyX's ilmanipulator): the extra
                    // argument has no counterpart in the proxy, and rewriting the
                    // call would leave the evaluation stack unbalanced.
                    Melon<Bridge>.Logger.Warning(
                        $"Not rewriting Harmony.Patch() with {mr.Parameters.Count} parameters in " +
                        $"{method.DeclaringType?.Name}.{method.Name} — call kept on the native runtime");
                    continue;
                }

                for (int i = mr.Parameters.Count; i < 5; i++)
                {
                    il.InsertBefore(inst, il.Create(OpCodes.Ldnull));
                }

                // No stack fix-up is required: Harmony.Patch() and ProxyPatch()
                // both return System.Reflection.MethodInfo, so the single value
                // the original call pushed is exactly what the caller expects.
                //
                // Discarding it and pushing null instead (as this rewrite used
                // to do) silently changed every "MethodInfo m = harmony.Patch(…)"
                // into a null reference, which then failed far away from here.
                inst.OpCode = OpCodes.Call; // instance method → static proxy
                inst.Operand = proxyRef;
            }

            return true;
        }
    }
}
