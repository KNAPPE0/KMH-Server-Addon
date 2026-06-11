namespace KMHServerAddon.Extensibility
{
    // Auto-written to kmh-extensions/README.txt on first server boot so admins discover the extension folder
    // without having to find the SDK docs first
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

See EXTENSIONS.md in the KMH-Server-Addon repo for the full SDK
reference + example extension code.
";
    }
}
