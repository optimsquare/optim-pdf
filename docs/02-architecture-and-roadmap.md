# Optim PDF — Architecture, Licensing & Roadmap

## 1. Stack

| Layer | Choice | Why |
|-------|--------|-----|
| Runtime | .NET 10 (LTS), x64 | Installed on the dev machine; long support window |
| UI | **WPF** with the built-in Fluent theme (`ThemeMode`) | Mature, high-DPI aware, light/dark built in, simple unpackaged deployment. WinUI 3 was considered, but its packaging and CLI testing costs more for no core benefit. |
| Rendering, text, search, outline | **PDFium** via PDFiumCore | Chromium's PDF engine: fast and very compatible |
| Document writing/manipulation | **PDFsharp 6** | Pure managed code. Handles page operations, annotations, AcroForm, encryption and digital signatures |
| OCR | **Windows OCR** (Windows.Media.Ocr, part of Windows 10/11) | Offline, nothing to bundle or license; languages follow the Windows language packs. Tesseract remains an option if more languages or deskewing are needed |
| Office conversion | **The user's installed Microsoft Office** (COM automation, invisible) | Best fidelity, nothing bundled. LibreOffice was the plan, but Office is what is installed here; LibreOffice (separate process, MPL-2.0) can be added as a fallback |
| Digital signatures | Own incremental-update writer (`IO/RawPdf.cs`) + .NET `SignedCms` | Signing must not rewrite the file: PDFsharp normalizes documents on load, which would break existing signatures |
| PDF object editing | PDFium edit API (fpdf_edit) | Removes page content for redaction and text editing |
| Installer | WiX Toolset 5.0.2 MSI (local dotnet tool; MS-RL; 5.x chosen because WiX 6+ requires the paid Open Source Maintenance Fee EULA for commercial users) | Per-user or per-machine, silent install for IT (`msiexec /i OptimPdf-x.y.z-x64.msi /qn ALLUSERS=1`) |

## 2. License review (verified from package `.nuspec` files on 2026-10-12)

| Component | Version | License | Commercial closed-source use | Obligation |
|-----------|---------|---------|------------------------------|-----------|
| PDFiumCore | 156.0.8076 | Apache-2.0 | Yes | Ship license + NOTICE |
| bblanchon.PDFium.Win32 (pdfium.dll, chromium/8076) | 156.0.8076 | MIT build scripts; PDFium BSD-3 plus third-party parts (abseil Apache-2.0, FreeType FTL, ICU, lcms MIT, libjpeg-turbo BSD/IJG, OpenJPEG BSD-2, libpng, zlib, agg23, fast_float, simdutf, llvm-libc) | Yes | Ship license texts (installer/licenses) |
| PDFsharp | 6.2.4 | MIT | Yes | Keep copyright notice |
| C#/WinRT projection (Microsoft.Windows.SDK.NET, WinRT.Runtime), for Windows OCR | 10.0.19041 | MIT | Yes | Keep notice |
| System.Security.Cryptography.Pkcs (CMS) | in .NET 10 | MIT | Yes | Part of .NET |
| Microsoft Office (external, the user's own) | 16 | Microsoft license | Automation of the user's licensed desktop copy | Nothing shipped |
| LibreOffice (optional future fallback, external) | — | MPL-2.0 | Yes, as an unmodified separate program | Do not modify or statically link |
| **Rejected: iText 7** | — | AGPL-3.0 / commercial | No (without a paid license) | — |
| **Rejected: MuPDF** | — | AGPL-3.0 / commercial | No (without a paid license) | — |
| **Rejected: Ghostscript** | — | AGPL-3.0 / commercial | No (without a paid license) | — |

A `THIRD-PARTY-NOTICES.txt` will ship with the installer.

## 3. Module structure

```
OptimPdf.slnx
├─ src/OptimPdf.Core        net10.0-windows10.0.19041   UI-free engine (fully unit-testable)
│   ├─ Rendering/            PdfiumDocument: open, render, text, search, outline (thread-safe)
│   ├─ Pages/                PageOperations: merge, split, extract, insert, delete, reorder, rotate, crop
│   ├─ Annotations/          AnnotationService: markup, notes, ink, stamps
│   ├─ Security/             (phase 2) encryption, permissions, metadata scrub, redaction
│   ├─ Editing/              ContentEditor, ExistingTextEditor, PdfiumEditor, RtlText (shaping + bidi)
│   ├─ Signatures/           Visual marks, PAdES signing, validation
│   ├─ Ocr/ Conversion/ Optimization/ Batch/   OCR, image/Office conversion, compression, batch runner
│   └─ IO/                   SafeFileWriter, RecoveryStore, RawPdf (byte-exact reader + incremental writer)
├─ src/OptimPdf.App         net10.0-windows WPF shell: ribbon, tabs, panes, themes
└─ tests/OptimPdf.Core.Tests  xUnit; generates its own PDFs at test time (no binary fixtures)
```

Design rules:
- **The core never touches UI types**, so every feature can be tested headless.
- **Edits go through PDFsharp and are written to a new file atomically.** The original stays untouched until the replace step succeeds, and a `.bak` is kept. PDFium then reloads the result for display.
- **PDFium is not thread-safe**, so all calls go through one global lock.

## 4. Roadmap

| Phase | Scope | Exit criteria |
|-------|-------|---------------|
| **MVP** (done) | Viewer (tabs, zoom, rotate, continuous scroll, thumbnails, search, bookmarks, properties), annotations (highlight, underline, strikeout, note, free text, ink, stamp), page management, safe save, light/dark theme | Core unit tests green; app builds; manual smoke test of open/annotate/save |
| 2 | Add text/images, form filling, visual signatures, passwords/permissions/AES-256, metadata removal, compression, image↔PDF, undo/redo, autosave and crash recovery | Each feature has round-trip tests (write, reopen with PDFium, verify) |
| 3 (done, 0.3.0) | OCR, Office conversion, editing existing text, creating form fields, PAdES digital signatures, true redaction, batch processing, RTL text | Redaction verified by text-extraction tests; signatures validated by an independent verifier |
| 4 (done, except a purchased certificate and the EULA text) | Branding polish, splash, icons, installer, code signing, performance work on 1,000+ page files | Install, upgrade and uninstall tested on Windows 11; Windows 10 VM pending |

## 5. Known technical limits (to be refined as phases land)

- Editing existing text depends on the fonts embedded in the file. Subset fonts may lack the glyphs needed for new text, so the editor must substitute fonts and say so.
- XFA (dynamic) forms are not supported. PDFium renders only their static fallback, if there is one.
- Office conversion quality is Microsoft Office's. It is available only when Office is installed.
