using System;
using System.Collections.Generic;

namespace KMHServerAddon.Diagnostics
{
    internal static class KmhRwtPreflightSelfTest
    {
        // Real stack text, so editing it to read better would stop it matching what RWT actually throws.
        private const string NullConfigStack =
            "System.Reflection.TargetInvocationException: Exception has been thrown by the target of an invocation.\n" +
            " ---> System.NullReferenceException: Object reference not set to an instance of an object.\n" +
            "   at RTServer.Hooks.Shared.ServerPrinter.CheckIfShouldPrint(Verbosity importance)\n" +
            "   at RTShared.Misc.Printer.Warning(Object toPrint, Verbosity mode)\n" +
            "   at RTServer.PacketManagers.PM_Events.LoadAllEvents()";

        private const string TruncatedConfigStack =
            "System.Reflection.TargetInvocationException: Exception has been thrown by the target of an invocation.\n" +
            " ---> System.Threading.SemaphoreFullException: Adding the specified count to the semaphore would cause " +
            "it to exceed its maximum count.\n" +
            "   at RTShared.Files.FL_Base.Load[T](String savePath, Boolean generateIfNull)\n" +
            "   at RTServer.Core.Program.LoadFiles()";

        public static List<(string, bool, string)> Run()
        {
            var r = new List<(string, bool, string)>();
            RwtPreflight.ConfigState Cls(string s) => RwtPreflight.Classify(s);

            r.Add(("Preflight: 0 bytes -> Empty",  Cls("")     == RwtPreflight.ConfigState.Empty, ""));
            r.Add(("Preflight: blank -> Empty",    Cls("  \r\n") == RwtPreflight.ConfigState.Empty, ""));
            r.Add(("Preflight: null -> Null",      Cls("null")  == RwtPreflight.ConfigState.Null, ""));
            r.Add(("Preflight: padded null -> Null", Cls(" null\n") == RwtPreflight.ConfigState.Null, ""));

            var trunc = Cls("{\n \"Name\": \"srv\",\n \"Port\": 270");
            r.Add(("Preflight: truncated -> Unparseable", trunc == RwtPreflight.ConfigState.Unparseable, ""));
            r.Add(("Preflight: truncated is not auto-replaceable", !RwtPreflight.CarriesNoSettings(trunc), ""));

            r.Add(("Preflight: object -> Ok", Cls("{\"Port\":27015}") == RwtPreflight.ConfigState.Ok, ""));
            r.Add(("Preflight: Ok is not auto-replaceable",
                   !RwtPreflight.CarriesNoSettings(RwtPreflight.ConfigState.Ok), ""));

            r.Add(("Preflight: empty/null are auto-replaceable",
                   RwtPreflight.CarriesNoSettings(RwtPreflight.ConfigState.Empty)
                   && RwtPreflight.CarriesNoSettings(RwtPreflight.ConfigState.Null), ""));

            string nullWhy = RwtPreflight.ExplainStartupFailure(new Exception(NullConfigStack));
            r.Add(("Preflight: explains the logger NRE", nullWhy != null && nullWhy.Contains("Configs"), nullWhy ?? "(none)"));

            string truncWhy = RwtPreflight.ExplainStartupFailure(new Exception(TruncatedConfigStack));
            r.Add(("Preflight: explains the semaphore failure", truncWhy != null && truncWhy.Contains("Configs"), truncWhy ?? "(none)"));

            r.Add(("Preflight: unrelated failure not misdiagnosed",
                   RwtPreflight.ExplainStartupFailure(new Exception("Port 27015 already in use")) == null, ""));
            r.Add(("Preflight: null exception is safe",
                   RwtPreflight.ExplainStartupFailure(null) == null, ""));

            r.Add(("Preflight: byte sizes read naturally",
                   RwtPreflight.Bytes(512) == "512 bytes"
                   && RwtPreflight.Bytes(2L << 30).EndsWith("GB")
                   && RwtPreflight.Bytes(5L << 20).EndsWith("MB"), RwtPreflight.Bytes(2L << 30)));

            return r;
        }
    }
}
