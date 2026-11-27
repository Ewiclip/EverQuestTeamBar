# Team Bar application artwork

The approved main design is a red leather shield and gold sword between a blue **Assist** tile and a green **Attack** tile, in a classic medieval fantasy style.

- `TeamBar.png`: approved transparent main artwork.
- `TeamBar.ico`: Windows application/installer icon at 16, 20, 24, 32, 40, 48, 64, 96, 128 and 256 pixels. Frames through 32 pixels use the central shield for legibility; larger frames include both labeled tiles.
- `TeamBarTray.png`: matching shield-only small-icon artwork.
- `TeamBarTray.ico`: tray frames at 16, 20, 24, 32, 40, 48 and 64 pixels.

Both PNGs were created with the built-in image-generation tool, with transparent backgrounds. No CLI/API image-generation fallback was used. Rebuild the ICO packaging with `tools/build-team-bar-icons.ps1` from the repository root. The PNG originals remain intact; the packager fits the visible artwork into square Windows icon sizes.

The app embeds both ICOs, so runtime windows and the notification area do not depend on loose image files. Inno Setup embeds the application ICO in the installer. Shortcuts and Installed apps use the executable's icon.

## Approved artwork prompts

Style refinement: "Redesign the attached Team Bar application icon to feel strongly like classic EverQuest medieval high-fantasy UI and spell gems. Keep three overlapping hotkey/spell tiles representing a team. Use weathered antique bronze and warm hammered gold, chiseled corners and aged patina. The center tile is a sturdy shield-shaped key with dark oxblood leather and a large golden sword pointing upward. The rear tiles are worn cobalt-blue and forest-green enamel. Hand-painted 1999 fantasy RPG equipment and spell-button materials, broad readable shapes, dark outlines, warm highlights, transparent background; no neon, orbital rings or wordmark."

Final main edit: "Edit only the two side tile symbols in this Team Bar fantasy app icon. Replace the rune on the LEFT BLUE tile with the exact word \"Assist\". Replace the rune on the RIGHT GREEN tile with the exact word \"Attack\". Both labels should be warm ivory/gold, clear bold classic fantasy serif lettering, one horizontal line each, centered within the visible face of their respective tile, entirely readable and not covered by the central shield. Exact capitalization: Assist and Attack. Remove the old runes entirely. Preserve the central red leather shield and golden upright sword, aged bronze/gold beveled frames, hand-painted medieval EverQuest-style materials, composition, proportions, lighting and transparent background. Do not add any other lettering or decoration. Keep the center shield and sword unchanged."

Tray variant: "Create the matching tiny system-tray variant of this exact app icon. Isolate ONLY the central red shield and its gold upright sword, completely remove the two side button tiles and their Assist/Attack labels. Keep the same shield silhouette, antique gold frame, red leather and sword placement. Simplify the texture and small rivets subtly so the silhouette and broad gold sword stay bold and readable at 16 to 32 pixels. Fill 90% of a square canvas, center exactly, leave a little transparent margin, genuinely transparent background, no external shadow or glow, no text. This is an app-icon size variant, not a new logo design."
