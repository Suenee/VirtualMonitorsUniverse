# VMU built-in avatar specification

`Assets/Avatars` is the authoritative catalog of built-in monitor avatars. VMU discovers valid PNG files in this directory automatically; adding a compliant file adds an avatar and deleting it removes that avatar from the catalog. No source-code registration is required and there is no fixed limit on the number of avatars.

## Required file format

- PNG only (`.png`).
- Exactly 256 × 256 pixels.
- Maximum file size: 256 kB (262,144 bytes).
- The image must decode as a valid PNG.
- A transparent background is required. The image must contain transparent or semi-transparent pixels.
- The file name without `.png` is the stable avatar ID. Use lowercase ASCII letters, digits and hyphens only. The ID must start with a letter or digit and may be at most 64 characters long. Examples: `fox.png`, `red-panda.png`, `space-cat-2.png`.

Files that violate any of these rules are ignored by the avatar catalog. A malformed avatar must never prevent VMU, Tray, Web Client, capture, input or other core services from operating.

## Visual consistency

For a coherent UI, keep the subject optically centered and use approximately the same visual scale and outer padding as the existing avatars. Prefer a single clear subject, a transparent canvas and strong shapes that remain recognizable at approximately 20–40 pixels. Avoid tiny details, decorative backgrounds and embedded text. Preserve enough transparent margin that the artwork does not touch the 256 × 256 canvas edges.

Use normal RGB/RGBA PNG output. Optimize the file after export; a 256 × 256 avatar should normally be far below the 256 kB hard limit.

## Runtime behavior

VMU validates the directory into an in-memory catalog and serves/renders avatars from that cache. It does not read and decode every PNG whenever a menu or page is opened. Directory changes invalidate the cosmetic catalog and are reloaded outside critical capture/input paths. If a previously selected built-in avatar is removed or becomes invalid, VMU uses a safe fallback image until another valid avatar is selected.

The avatar subsystem is deliberately best-effort: cosmetics must yield to capture, Terminal, input and service reliability.
