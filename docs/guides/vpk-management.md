---
description: How to open VPK files in Source 2 Viewer, extract files or whole packages, and create or edit VPKs using the GUI or the command line.
---

# How to Open, Extract and Edit VPK Files

VPK (Valve Pak) is the archive format Source and Source 2 games pack their files into. Large packages are split into a `pak01_dir.vpk` index and numbered chunk files such as `pak01_000.vpk`. Source 2 Viewer can open, extract, create and edit them.

## Browsing VPK Archives

- Open a VPK with File > Open or from the Explorer. For a split package, open the `_dir.vpk`; its chunk files are read automatically.
- VPKs inside other VPKs, such as maps in workshop items, open directly.
- To check a package, right-click the root and select **Verify package contents**, or use `--vpk_verify`.

![CS2 pak01 open in Source 2 Viewer](./images/vpk-management-package.png)

## Extracting Files

Right-click a file, a folder, or the root of the tree for the whole VPK, then choose:

- **Export as is** to save it unchanged
- **Decompile & export** to convert it to formats you can edit

![Export as is in the context menu](./images/vpk-management-export-as-is.png)

![Export as is on the tree root](./images/vpk-management-root-menu.png)

**Decompile & export** on a folder or the whole VPK first asks which format to use for each file type, see [Getting Started](./getting-started.md#batch-operations).

![Save dialog for the exported model](./images/vpk-management-export-as-is-save.png)

### Using the CLI

To extract a whole VPK from a script:

```sh
Source2Viewer-CLI -i "pak01_dir.vpk" -o "output_folder/"
```

Add `-d` to decompile the files:

```sh
Source2Viewer-CLI -i "pak01_dir.vpk" -o "output_folder/" -d
```

## Creating and Editing VPK Archives

1. In the **File** menu, choose:

    - **Create VPK** to start an empty VPK
    - **Create VPK from folder** to pack everything inside a folder
    - **Edit VPK** to change an existing VPK

    You can also right-click the root of an open VPK and select **Edit this VPK**.

    ![Create VPK from folder in the File menu](./images/vpk-management-create-file-menu.png)

    ![Folder picker for the new VPK](./images/vpk-management-create-folder-picker.png)

2. Right-click in the editing tab to add files and folders, create folders, or rename and remove them. Paths are lowercased.

    - **Add existing folder** adds the folder itself, and you can pick several.
    - If a file already exists, you choose to **Replace** or **Skip** it.
    - Empty folders are not saved.

    ![Add existing files in the context menu](./images/vpk-management-create-context-menu.png)

    ![File picker for the files to add](./images/vpk-management-create-add-files.png)

    ![The added files in the sounds folder](./images/vpk-management-create-added.png)

    ![Folder picker for adding folders](./images/vpk-management-create-add-folder.png)

    ![Replace existing files prompt](./images/vpk-management-create-replace-prompt.png)

3. Press Ctrl+S, or right-click and select **Save** or **Save as**. Tabs with unsaved changes show `*` in their title. To split the VPK into chunk files, pick **Valve Pak split into chunk files** as the type and name it `*_dir.vpk`.

    ![Save in the context menu](./images/vpk-management-create-save-menu.png)

    ![Save VPK package dialog](./images/vpk-management-create-save-dialog.png)

    ![Save as type list](./images/vpk-management-create-save-types.png)

### Using the CLI

To pack a folder into a VPK from a script:

```sh
Source2Viewer-CLI -i "my_addon" --vpk_create "my_addon.vpk"
```

To split it into chunk files of 100 MiB, name it `*_dir.vpk`:

```sh
Source2Viewer-CLI -i "my_addon" --vpk_create "my_addon_dir.vpk" --vpk_create_chunk_size 100
```

See the [command-line utility](./command-line.md#create-a-vpk-from-a-folder) for more.

### Good to Know

- Saving removes the VPK's signature, so games that check signatures won't load it.
- A single-file VPK must stay under 2 GiB; save bigger ones split into chunk files.
- In a split VPK, added files go into chunk files right away and stay there even if you don't save.

## Recovering Deleted Files

Chunk files can still contain files that a game update removed or replaced. Recovering them lets you compare assets across updates.

1. Open a split package such as `pak01_dir.vpk`. Single-file VPKs don't have this option.
2. Right-click the root and select **Recover deleted files**.

    ![Recover deleted files in the context menu](./images/vpk-management-recover-menu.png)

3. Recovered files appear under **Deleted files**; files whose names can't be guessed are under **Undetected filenames**.

    ![Deleted files node after recovery](./images/vpk-management-recovered.png)

::: warning
Recovered files can be incomplete, corrupted or from older game versions.
:::

## VPK File Format

Source 2 Viewer uses the [ValvePak](https://www.nuget.org/packages/ValvePak/) library to read and write VPKs, which you can also use in your own programs. The [Source 2 Wiki](https://www.source2.wiki/FileFormats/vpk) describes the format in detail.
