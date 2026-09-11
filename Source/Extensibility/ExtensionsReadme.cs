namespace KMHServerAddon.Extensibility
{
    internal static class ExtensionsReadme
    {
        public const string Content =
@"KMH Server Addon - extensions folder
====================================

Drop *.dll files (or subfolders containing *.dll) into this folder
to load server-side extensions at next server startup.

Each extension is a .NET 8 class library that:
  - References KMH.Sdk.Server.dll (shipped alongside KMHServerAddon
    .exe in Deploy/).
  - Defines at least one public class that implements
    KMH.Sdk.Server.IKmhServerExtension with a parameterless ctor.

The addon logs 'Extensions: loaded <name> v<ver>' for each one at
startup. Failed loads are logged with the underlying exception but
don't block KMH itself.

Per-extension persistent state lives at:
  ../kmh-data/extensions/<extension-name>/
created on first save. Backing up that folder backs up extension
state without touching KMH's own kmh-data files.

Two ways to plug in:
  - host.Events (IKmhEvents) - OBSERVE what happened (a sale, a
    quest completed, ...). 28 events.
  - host.Hooks  (IKmhHooks)  - DECIDE what's allowed BEFORE it
    happens (custom rules). With no hook registered, KMH behaves
    exactly as stock. Example - ban listing weapons:
      host.Hooks.OnMarketplaceListing(ctx =>
        ctx.ItemDefName.Contains(""Gun_"")
          ? KmhHookVerdict.Deny(""No weapons on this server."")
          : KmhHookVerdict.Allow);
    First hook to Deny wins; a hook that throws is treated as Allow
    (it can never freeze the economy). Keep hooks fast. Hook points:
    OnMarketplaceListing, OnAuctionListing, OnWant, OnQuestPost.

SDK compatibility (optional but recommended):
  Also implement KMH.Sdk.Server.IKmhSdkTargeted and return
  KmhSdkContract.Version so KMH can detect an extension built for a
  different SDK and skip it with a clear message instead of a cryptic
  error. Extensions that don't implement it are assumed to target the
  baseline (1). Run 'kmh extensions' to see each one's contract and
  the server's current contract version.

See EXTENSIONS.md in the KMH-Server-Addon repo for the full SDK
reference + example extension code.
";
    }
}
