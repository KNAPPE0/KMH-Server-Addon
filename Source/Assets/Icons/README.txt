KMH Discord embed icons
=======================

This is KMH-Data/Icons - the one folder the bot reads its embed icons from. It
ships pre-filled (this set is generated into KMH-Data/Icons on every build and
packaged in every release) and the addon reads straight from here, so the icons
travel with the server - no external image hosting needed. Drop your own PNGs
here to override any of them.

How it works
------------
Names are extension-less. For each icon below, drop a file with ANY format
Discord can render as a thumbnail - the bot uses the first it finds, in this
order:  .png  ->  .webp  ->  .gif  ->  .jpg / .jpeg

So "kmh_logo" is satisfied by kmh_logo.png, kmh_logo.webp, etc. A missing icon
just posts the embed without a thumbnail, and kmh_logo is the fallback when a
specific icon has no file.

IMPORTANT: Discord cannot display RimWorld's .dds / .dds.zstd textures. Those
are only for the in-game client icons (KMH-Patch/Textures/KMHPatch/UI). Use a
raster format here.

Toggle the whole feature with "UseBundledIcons" in
Config/Discord/DiscordConfig.json.

Icon names the bot looks for
----------------------------
  kmh_logo         - default / server-online announcement (also the fallback)
  marketplace      - new listing, item sold
  leaderboard      - leaderboard embeds
  site_created     - new site built
  site_destroyed   - site removed
  quest            - quest completed
  guild            - guild created
  treasury         - treasury updates
  warning          - warnings
  error            - errors

Recommended: square images, 128x128 or 256x256, transparent background.
