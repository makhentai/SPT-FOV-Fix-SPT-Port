# SPT-FOV-Fix (SPT 4.1.3 port)

Client-side port of Fontaine's [SPT-FOV-Fix](https://github.com/space-commits/SPT-FOV-Fix) to SPT 4.1.3. Removes the FOV decrease when aiming down sights, adds FOV/camera-position tuning, mouse sensitivity scaling by scope magnification, and a wider Min/Max Base FOV range in settings.

Also fixes a crouch bug not present in the original: the vanilla engine itself resets ADS FOV to a hardcoded value on every pose change (crouch/stand) while aiming, bypassing this mod's own FOV logic — see `README_PORT_NOTES.txt` for the exact root cause and fix.

CC BY-NC-SA License (same as upstream) — non-commercial use, attribution to the original author (Fontaine) required, derivatives must stay under the same license terms.
