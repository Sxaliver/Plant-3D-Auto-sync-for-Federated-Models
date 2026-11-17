# Auto sync 0.4.21 — Windows x64 / Plant 3D 2024

Extract the whole ZIP before running Install.ps1 in PowerShell. The installer does not close your applications. Save and fully restart Plant, then run SYNC.

Required separately: licensed AutoCAD Plant 3D 2024, Autodesk Desktop Connector and Navisworks NWC Export Utility matching AutoCAD 2024. Do not redistribute Autodesk SDK/runtime DLLs.

The package includes the plugin, Newtonsoft.Json, portable Tectonic 0.17.0, a neutral TeX report template and an installer that configures per-user report paths. First PDF compilation requires Internet for Tectonic's TeX package cache. This is not an offline-complete TeX distribution. Arial and Microsoft YaHei or SimSun must be available on Windows.

Set your own Connector DWG folder, local models, assembly folder and NWC output root. The original IWMF system-number matching and default insertion point / -4.42-degree rotation remain project-specific: review all coordinates, scales, systems and previews before applying this tool to another project. No actual models, user settings, account credentials or project logs are included.

Each model exports its own NWC into YYYY-MM-DD. Automatic English PDF reports and model-named before / after PNGs are stored beside the NWC. A real before image is captured only when a model is updated through the plugin; export-only runs cannot reconstruct the previous state. Failed screenshots or PDF compilation are reported explicitly, without discarding successful NWC files.

Manual export uses the current local model without forcing cloud synchronization. The tool does not automatically upload NWC or generate the final federated assembly. Desktop Connector local availability is not proof that ACC server synchronization is finished.

Existing report settings and templates are preserved. Install.ps1 -ForceReportSettings backs up and rewrites report compiler settings, but does not overwrite a custom template. Weekly automation is not scheduled by this installer; it requires separate project configuration and validation.

Third-party notices are in Tools and the plugin bundle. Release validation: clean public-source build and report smoke tests; not a new full-project Plant/NWC acceptance run.
