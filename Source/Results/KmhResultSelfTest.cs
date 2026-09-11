using System;
using System.Collections.Generic;

namespace KMHServerAddon.Results
{
    internal static class KmhResultSelfTest
    {
        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();

            int missing = 0, total = 0;
            foreach (KmhErrorCode code in Enum.GetValues(typeof(KmhErrorCode)))
            {
                string msg = KmhErrorText.SafeMessage(code);
                if (code == KmhErrorCode.None)
                {
                    if (msg != "") missing++;   // None must map to empty
                    continue;
                }
                total++;
                if (string.IsNullOrWhiteSpace(msg)) missing++;
            }
            r.Add(("Result: every code has a safe message", missing == 0, $"{total} codes, {missing} missing"));

            bool noLeak = true;
            foreach (KmhErrorCode code in Enum.GetValues(typeof(KmhErrorCode)))
                if (code != KmhErrorCode.None && KmhErrorText.SafeMessage(code).Contains(code.ToString())) noLeak = false;
            r.Add(("Result: messages don't leak the code name", noLeak, "player text, not enum names"));

            bool spot = KmhErrorText.SafeMessage(KmhErrorCode.InvalidState).Length > 0
                     && KmhErrorText.SafeMessage(KmhErrorCode.PermissionDenied).ToLowerInvariant().Contains("permission");
            r.Add(("Result: key messages present", spot, "maintenance + permission wording"));

            return r;
        }
    }
}
