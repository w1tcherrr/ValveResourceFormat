---
description: Download Source 2 Viewer and learn the basics of browsing game files in the Explorer, opening files and VPK links, and exporting assets.
---

# Getting Started

This guide walks you through the basics of using Source 2 Viewer to browse, preview, and export Source 2 game assets.

## Downloading Source 2 Viewer

Download the latest release from [s2v.app](https://s2v.app).

Source 2 Viewer is portable and requires no installation. Just run the executable directly.

- **Source 2 Viewer** (GUI) is Windows-only. It can be run on Linux/macOS using Wine.
- **Source2Viewer-CLI** (command-line) is cross-platform. Download for your target platform is available in the changelog section.

## Using the Explorer

The **Explorer** tab is the main way to browse game files. It opens by default on launch (this can be disabled in settings) and is accessible from the top bar. It automatically discovers all installed Steam games and their VPK archives, including workshop content. Click a game to expand it and browse its files.

Files opened from a game folder load the textures, materials and other files they reference from that game. For files outside of a game folder, see [game search paths](./game-search-paths.md).

The Explorer also shows:

- **Recent files** at the top, so you can quickly reopen files you've worked with before
- **Bookmarks** for files you've saved for later. Right-click any file and choose **Add to Bookmarks** to add it

![Explorer showing detected Steam games](./images/getting-started-game-explorer.png)

## Opening Files Manually

There are several ways to open files:

- Use **File → Open** (<kbd>Ctrl</kbd>+<kbd>O</kbd>) to browse for any `.vpk` file or individual Source 2 files (`.vmdl_c`, `.vtex_c`, etc.)
- Drag and drop files or folders directly onto the window

### Opening VPK Links

A file inside a VPK can be opened directly with a `vpk:` link, made of the package path, a colon, and the path inside it:

```
vpk:steam:730/game/csgo/maps/de_dust2.vpk:maps/de_dust2.vmap_c
```

- The package path is either a full path, or relative to an installed Steam app as `steam:<appid>/`, which works on any computer with that game installed.
- Right-click a file and choose **Copy URL** to get its link. It uses the `steam:` form when the package belongs to an installed Steam app.
- Pass the link to `Source2Viewer.exe` on the command line to open the file.
- After **Register .vpk file association** in settings, clicking a `vpk:` link anywhere opens it in Source 2 Viewer.
- A file in a VPK nested inside another uses one more colon: `vpk:outer_dir.vpk:maps/inner.vpk:models/file.vmdl_c`.
- A `%` in a path is written as `%25`.

## Searching

Press <kbd>Ctrl</kbd>+<kbd>F</kbd> to open the search dialog, or right-click a folder and choose **Search in folder**.

- **Search in** picks what the text is matched against: the file name, the full path, or the file contents. In a name search without a regular expression, text with a `/` in it matches the full path instead.
- The buttons next to the text box toggle **Match case** (<kbd>Alt</kbd>+<kbd>C</kbd>), **Match whole word** (<kbd>Alt</kbd>+<kbd>W</kbd>) and **Use regular expression** (<kbd>Alt</kbd>+<kbd>R</kbd>). They apply to contents too, which are read as UTF-8 text. With a regular expression, `^name$` matches a whole name exactly.
- **0x** (<kbd>Alt</kbd>+<kbd>X</kbd>) searches the contents for hex bytes such as `56 42 4B 56` instead of text.
- **Only in** limits the search to the folder selected in the tree and its subfolders.
- **Extension** limits the search to one file type, listing how many files of each type the package, or the **Only in** folder, has, ignoring case.
- **Asset info** limits the search to files with a given searchable user data key, and optionally a value, from the package's tools asset info. Only keys and values of files in the package are listed, with how many files have each. Both boxes accept typing and complete from the list.
- Input that can not be searched, such as an invalid regular expression or odd hex digits, is shown in red with the reason below the text box, and **Find** stays disabled until it is fixed.

The text can be left empty to list every file that the filters allow. Results are shown in a list with file name, size, type and folder columns, and the toolbar above them says how many files matched what. A contents search that is cancelled lists the files found so far and says so. The dialog keeps its options until Source 2 Viewer is closed.

## Previewing Assets

Double-click any file to open it in a new tab. Different file types get specialized viewers:

- **Models** (`.vmdl_c`) → 3D viewer with orbit, pan, zoom
- **Textures** (`.vtex_c`) → Image viewer with channel isolation (RGB, Alpha)
- **Sounds** (`.vsnd_c`) → Audio player with waveform
- **Maps** (`.vmap_c`) → 3D map viewer
- **Materials** (`.vmat_c`) → Material parameter viewer
- Other formats → Text/hex viewer

A preview mode can be enabled in settings. When active, selecting a file in the tree will automatically display it instead of showing the file list.

Relevant keyboard shortcuts are shown at the bottom of each viewer.

## Exporting Files

Right-click a file to access two export modes:

- **Export as is** saves the raw compiled file without any conversion. Use this when you need the original Source 2 format.
- **Decompile & export** converts files to standard editable formats:
    - Models → glTF (.gltf/.glb) or decompiled .vmdl, picked in the save dialog
    - Maps → glTF or decompiled .vmap (for Hammer Editor)
    - Textures → PNG, or EXR for HDR textures. Textures stored as JPEG or WebP keep that format
    - Sounds → WAV or MP3, matching the stored audio
    - Materials → .vmat
    - Physics (`.vphys_c`) and navigation meshes (`.nav`) → glTF
    - Other resources → Their decompiled text format

::: tip
"Decompile & export" is what most people want. It produces files you can directly open in Blender, image editors, audio players, or Hammer Editor.
:::

## Batch Operations

- **Select multiple files:** Hold <kbd>Ctrl</kbd> and click to select individual files, or hold <kbd>Shift</kbd> to select a range
- **Export entire folders:** Right-click a folder and choose an export option to export everything inside it

::: tip
For large batch exports, consider using the [command-line utility](./command-line.md) which supports multi-threaded extraction.
:::

## Issues

If you have questions or want to chat, join the [Discord server](https://discord.gg/s9QQ7Wg7r4).

If you want to report a bug or request a feature, see the [reporting issues guide](./reporting-issues.md).
