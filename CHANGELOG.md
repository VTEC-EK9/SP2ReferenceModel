# Changelog

## 0.7.0 - 2026-07-08

- Added session-state persistence for loaded reference models, including root pose,
  scale, visibility, per-piece visibility/pose, and deleted pieces.
- Added mesh-row hover outlines so the corresponding reference-model piece is
  highlighted in the designer.
- Saved model state after gizmo edits, visibility changes, texture toggles,
  scale resets, mesh toggles, and mesh deletes.
- Added README usage examples as GIFs showing the reference-model designer
  workflow, mesh highlighting, restored state, and model-specific session restore.
- Expanded README installation and source-build instructions to match the other
  SP2 plugin docs.
- Fixed OBJ axis conversion by negating X, reversing triangle winding, and
  replacing the old Y/Z swap with an explicit `SourceIsZUp` option.
- Made the OBJ file picker asynchronous so folder navigation in the native
  dialog cannot freeze the game while the picker is open.
