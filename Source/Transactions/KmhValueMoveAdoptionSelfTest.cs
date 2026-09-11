using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace KMHServerAddon.Transactions
{
    // A tripwire: it MUST fail once something implements IKmhValueMove, because recovery still cannot refund a Reserved move.
    internal static class KmhValueMoveAdoptionSelfTest
    {
        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();

            List<string> implementers;
            try
            {
                implementers = typeof(IKmhValueMove).Assembly.GetTypes()
                    .Where(t => typeof(IKmhValueMove).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract)
                    .Where(t => !IsTestScaffolding(t))
                    .Select(t => t.FullName)
                    .OrderBy(n => n, StringComparer.Ordinal)
                    .ToList();
            }
            catch (ReflectionTypeLoadException ex)
            {
                implementers = ex.Types.Where(t => t != null)
                    .Where(t => typeof(IKmhValueMove).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract)
                    .Where(t => !IsTestScaffolding(t))
                    .Select(t => t.FullName)
                    .OrderBy(n => n, StringComparer.Ordinal)
                    .ToList();
            }

            r.Add(("Settlement: adopting IKmhValueMove requires a boot refund first",
                   implementers.Count == 0,
                   implementers.Count == 0
                       ? "unadopted"
                       : "now implemented by " + string.Join(", ", implementers)
                         + " - implement the Refund case in KmhTransactionRepository.RecoverOnBoot, then delete this test"));

            r.Add(("Settlement: a crashed Reserve still classifies as Refund",
                   KmhTransactionRecovery.DecideForStuck(KmhTxState.Reserved) == KmhTxRecoveryAction.Refund
                   && KmhTransactionRecovery.DecideForStuck(KmhTxState.Approved) == KmhTxRecoveryAction.Refund, ""));

            return r;
        }

        private static bool IsTestScaffolding(Type t)
            => (t.DeclaringType?.Name ?? "").EndsWith("SelfTest", StringComparison.Ordinal)
            || t.Name.EndsWith("SelfTest", StringComparison.Ordinal);
    }
}
