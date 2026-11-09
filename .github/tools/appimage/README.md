# AppImage build tools (fallback copies)

Used by `.github/scripts/appimage-tools.sh` when GitHub release downloads fail (504s from
`github.com/.../releases/download`, which broke the Linux packaging job more than once).

| File | Source |
|------|--------|
| `appimagetool-x86_64.AppImage` | https://github.com/AppImage/appimagetool/releases/tag/1.9.1 |
| `runtime-x86_64`, `runtime-aarch64` | https://github.com/AppImage/type2-runtime/releases/tag/continuous (2026-10-19 build) |

CI always tries a fresh download first and uses it when it works; these copies are only the
fallback. To refresh them, download the same files and commit them over these.
