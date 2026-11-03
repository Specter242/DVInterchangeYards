# Interchange Yards

A Derail Valley mod (Unity Mod Manager) that adds two interchange yards to the stock map: the
West Interchange (WI) beside the Machine Factory, and the Northeast Interchange (NEI) on the
plateau west of Iron Ore Mine East, each with a line into a tunnel off the map, an office with a
career manager kiosk and a job validator, and a place on the maps, in the station booklet and on
the route map.

- **Download and read about it** on Nexus Mods: https://www.nexusmods.com/derailvalley/mods/1815
- With [Railroad Contracts](https://github.com/Specter242/DVRailroadContracts) the yards become
  the only place cars enter and leave the map.
- **Updates:** Unity Mod Manager reads `repository.json` here and offers each new version.

## Building

Needs the .NET SDK and Derail Valley installed (the build references the game's and Unity Mod
Manager's assemblies in place; set `-p:DvInstallDir=...` if the game lives elsewhere than
Steam's default folder on Linux).

    scripts/package.sh

builds `dist/Interchanges-<version>.zip` for Unity Mod Manager. `src/Interchanges/` holds the mod
and the yard layouts it builds (`layout-*.json`).

MIT licensed (`LICENSE`). The changelog is `CHANGELOG.md`.
