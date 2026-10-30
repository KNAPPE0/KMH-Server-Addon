using System;
using System.Collections.Generic;
using System.Reflection;

namespace KMHServerAddon.Maintenance
{
    // Which method a boot path actually calls cannot be observed without running that boot, so it is read from IL.
    internal static class KmhIlReader
    {
        // Opcode scanning can hit an operand byte that looks like a call, so assert "calls X", never "all the calls".
        public static List<string> CallsMadeBy(string typeName, string methodName, string namePrefix = null)
            => CallsMadeBy(typeName, methodName, -1, namePrefix);

        // paramCount picks an overload; -1 takes the parameterless one, or the only one if there is exactly one.
        public static List<string> CallsMadeBy(string typeName, string methodName, int paramCount,
                                               string namePrefix = null)
        {
            var names = new List<string>();
            Type t = typeof(KmhIlReader).Assembly.GetType(typeName);
            MethodInfo m = Resolve(t, methodName, paramCount);
            // Async/iterator bodies compile into a nested state machine; the method itself only shows Create/Start/get_Task.
            if (t != null) Walk(t, methodName, namePrefix, names, 0);

            foreach (string n in Body(m))
                if (namePrefix == null || n.StartsWith(namePrefix, StringComparison.Ordinal)) names.Add(n);
            return names;
        }

        private static void Walk(Type t, string methodName, string namePrefix, List<string> names, int depth)
        {
            if (depth > 4) return;
            foreach (Type nested in t.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            {
                bool mine = nested.Name.IndexOf(methodName, StringComparison.Ordinal) >= 0;
                foreach (MethodInfo nm in nested.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                                                          | BindingFlags.Static | BindingFlags.Instance
                                                          | BindingFlags.DeclaredOnly))
                {
                    if (!mine && nm.Name.IndexOf(methodName, StringComparison.Ordinal) < 0) continue;
                    foreach (string n in Body(nm))
                        if (namePrefix == null || n.StartsWith(namePrefix, StringComparison.Ordinal)) names.Add(n);
                }
                Walk(nested, methodName, namePrefix, names, depth + 1);
            }
        }

        private static IEnumerable<string> Body(MethodBase m)
        {
            byte[] il = null;
            try { il = m?.GetMethodBody()?.GetILAsByteArray(); } catch { }
            if (il == null) yield break;
            Module mod = m.Module;
            for (int i = 0; i + 4 < il.Length; i++)
            {
                int tokenAt;
                // ldftn/ldvirtftn are two-byte opcodes and wire every method-group delegate, scheduler steps included.
                if (il[i] == 0x28 || il[i] == 0x6F) tokenAt = i + 1;
                else if (il[i] == 0xFE && i + 5 < il.Length && (il[i + 1] == 0x06 || il[i + 1] == 0x07)) tokenAt = i + 2;
                else continue;
                if (tokenAt + 3 >= il.Length) continue;
                int tok = il[tokenAt] | (il[tokenAt + 1] << 8) | (il[tokenAt + 2] << 16) | (il[tokenAt + 3] << 24);
                string name = null;
                try { name = mod.ResolveMethod(tok)?.Name; } catch { /* an operand byte that happened to look like a token */ }
                if (name != null) yield return name;
            }
        }

        // Resolved by hand: GetMethod(name) throws AmbiguousMatchException on overloads, which reads as "not there".
        private static MethodInfo Resolve(Type t, string methodName, int paramCount)
        {
            if (t == null) return null;
            var candidates = new List<MethodInfo>();
            foreach (MethodInfo mi in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                                                 | BindingFlags.Static | BindingFlags.Instance
                                                 | BindingFlags.DeclaredOnly))
                if (mi.Name == methodName) candidates.Add(mi);

            if (paramCount >= 0)
            {
                foreach (MethodInfo mi in candidates) if (mi.GetParameters().Length == paramCount) return mi;
                return null;
            }
            foreach (MethodInfo mi in candidates) if (mi.GetParameters().Length == 0) return mi;
            return candidates.Count == 1 ? candidates[0] : null;
        }
    }
}
