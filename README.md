# SP2 Reference Model

Runtime OBJ/MTL reference-model loader for the SimplePlanes 2 designer.

1. Open the designer **Main Menu** and expand **3D Reference Model**.
2. Click **Open OBJ...** and pick a model with the file browser
   (or drop files under `BepInEx/config/SP2ReferenceModel/Models/` and click Reload).
3. Click **Move / Rotate Model** to position it with the game's own gizmos —
   press **2** for the translate gizmo, **3** for the rotate gizmo,
   **Done** to apply or **Esc** to cancel (restores the previous pose).

Models load plain white by default; the **Textures** toggle switches the MTL
colors and textures on. The model is visual-only, is not added to craft XML,
has no colliders, and disappears outside the designer. Named OBJ objects/groups
become individually toggleable mesh entries in paged native controls.

## Usage examples

Load an OBJ reference model, then move and rotate it with the in-game designer
gizmos:

![Loading and positioning a reference model](docs/media/usage-load-and-position.gif)

Use the reference model while refining a craft in the designer:

![Using a reference model in the designer](docs/media/usage-designer-example.gif)

Edit and translate orientation of mesh submodules:

![Highlighting and restoring reference-model state](docs/media/usage-hover-and-state.gif)

Restore a saved session for a particular model:

![Restoring a saved session for a particular model](docs/media/usage-restore-specific-model.gif)

## Installation

1. Install BepInEx 5.x for the 64-bit Windows version of SimplePlanes 2.
2. Run the game once so BepInEx creates its folders.
3. Copy `SP2ReferenceModel.dll` into:

```text
SimplePlanes 2\BepInEx\plugins\
```

4. Restart SimplePlanes 2.
5. Check `BepInEx\LogOutput.log` if **3D Reference Model** does not appear in
   the designer main menu.

## Building from source

The project targets .NET Framework 4.8 and references assemblies from the
installed game and BepInEx.

By default, the project expects SimplePlanes 2 at:

```text
C:\Program Files (x86)\Steam\steamapps\common\SimplePlanes 2
```

Build a release DLL with:

```powershell
dotnet build .\SP2ReferenceModel.csproj -c Release
```

The compiled plugin is written to:

```text
bin\Release\SP2ReferenceModel.dll
```

The build also attempts to copy `SP2ReferenceModel.dll` into
`SimplePlanes 2\BepInEx\plugins\`. If the game is running and the copy fails,
copy the DLL manually.

To build against a different installation directory:

```powershell
dotnet build .\SP2ReferenceModel.csproj -c Release -p:GameDir="D:\Path\To\SimplePlanes 2"
```
