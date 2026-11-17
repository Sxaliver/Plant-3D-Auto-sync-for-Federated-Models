# 0.4.21 public sharing package

Included: current plugin source, automatic English report generation, before/after model evidence and safe model-named PNGs, date-folder NWC export, neutral TeX template, portable Tectonic 0.17.0 and per-user installer. Original project-branded template, actual DWGs/NWCs, logs, credentials and user-specific settings are excluded.

Validated from the public checkout: production build (zero warnings/errors), 650 isolated workflow checks including real Tectonic report compilation with the neutral template, and installer execution under isolated APPDATA/LOCALAPPDATA directories. ZIP contents inspected; SHA256.json lists package file hashes. No user Plant windows or project files were operated. This is not a fresh full-project native NWC acceptance run.

Install: extract ZIP, run Install.ps1, save/restart Plant 3D 2024 and run SYNC. Required external products: licensed Plant 3D 2024, matching Navisworks NWC Export Utility, Desktop Connector. First PDF compile requires Internet to populate Tectonic's TeX cache. Original insertion coordinates, rotation and system-number rules must be reviewed for other projects. Existing custom templates/report settings are preserved by default.

Build from source: ./build.ps1. Package with ./Package-Release.ps1 -TectonicPath <official-0.17.0-Windows-x64-tectonic.exe>. Compiler third-party notices accompany the release. Binary dependencies are release assets, not committed source artifacts. Weekly scheduling and automatic NWC upload are not enabled by installation.
