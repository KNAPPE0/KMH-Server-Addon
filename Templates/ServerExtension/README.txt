Welcome Bonus - example KMH server extension
============================================

A minimal complete extension that gives every new player a one-time
silver bonus on their first server join. Use it as a starting point
for your own server-side extensions.

Source tree:
  WelcomeBonus.csproj   build configuration (references KMH.Sdk.Server)
  WelcomeBonus.cs       extension implementation
  README.txt            this file

To build:
  cd Templates/ServerExtension
  dotnet build -c Debug

Output lands at:
  bin/Debug/net8.0/WelcomeBonus.dll

To install on your server:
  1. Copy bin/Debug/net8.0/WelcomeBonus.dll
  2. Drop it in <server-folder>/kmh-extensions/
  3. Restart KMHServerAddon.exe

You should see at startup:
  [KMH-Addon] Extensions: loaded 'Welcome Bonus' v1.0.0 (from WelcomeBonus.dll)

When a new player connects:
  [KMH-Addon] [ext:Welcome Bonus] Welcomed Alice with 100 silver.

Per-extension state lives at:
  <server-folder>/kmh-data/extensions/Welcome_Bonus/welcomed_users.json

To customise:
  - Change BonusAmount / WelcomeMessage constants at the top of
    WelcomeBonus.cs.
  - Subscribe to more events on host.Events (PlayerLeft,
    MarketplaceBuy, QuestCompleted, etc.).
  - Call other APIs on host: host.Marketplace, host.Quests,
    host.Guilds, host.LinkedAccounts, host.PlayerStats,
    host.ItemLabels.
  - See EXTENSIONS.md in the addon repo for the full API reference.
