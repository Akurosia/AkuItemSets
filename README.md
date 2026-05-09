# AkuItemSets

Dalamud plugin for tracking FFXIV MirageStore item set collection progress per character.

## Usage

- `/akuis` opens the tracker.
- `/akuis scan` scans the current character and opens the tracker.

The scanner stores collection snapshots separately by character name and home world. It currently reads:

- Inventory
- Armoury chest and equipped gear
- Saddlebag and premium saddlebag
- Retainer containers currently available to the client
- Glamour dresser single items
- Glamour dresser item-set entries, expanded into their set pieces
- Armoire entries after the armoire cache has loaded

Open the relevant in-game storage once if a client cache is not populated yet, then scan again.
