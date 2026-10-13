# Optim PDF — Implementation Status

## Phase 4 — release readiness (2026-10-13)

| Item | Status | How it was checked |
|---|---|---|
| **MSI installer** (`installer\build.ps1` → `artifacts\OptimPdf-0.4.0-x64.msi`, 65 MB, WiX 5.0.2) | Built and tested on this PC | Silent per-user install: files, Start menu and desktop shortcuts, "Open with", Default apps registration, Apps & Features entry (with icon). The installed app opens a PDF. Uninstall removes files, shortcuts and registry entries, and keeps the user's settings and signatures. Per-machine install (`ALLUSERS=1`) goes to `C:\Program Files\Optim Square\Optim PDF` with system-wide registration; tested end to end, and the uninstall is clean. Upgrade 0.4.0 → 0.4.1 replaces the old version (one entry remains). The setup wizard shows the logo, license page, scope choice and "Start Optim PDF" |
| Self-contained build | Done | No .NET installation needed; ReadyToRun-compiled for faster start (first window ≈ 4 s cold, in the test) |
| Default PDF app | By design, never forced | Optim PDF is offered in "Open with" and Settings › Default apps. The user's current default PDF app was unchanged after installing |
| License files shipped | Done | `licenses\`: PDFium and each bundled component (abseil, agg23, fast_float, FreeType, ICU, lcms, libjpeg-turbo, OpenJPEG, libpng, llvm-libc, simdutf, zlib) from the exact pdfium-binaries release (chromium/8076, binary hash verified), plus pdfium-binaries (MIT), PDFiumCore (Apache-2.0), PDFsharp (MIT), .NET runtime and WPF (MIT and third-party notices), and C#/WinRT (MIT). The notices file was corrected: pdfium-binaries is MIT, not Apache-2.0 |
| **Code signing** | Ready; needs a certificate | `build.ps1 -CertThumbprint …` (or `-PfxPath`) signs OptimPdf.exe, OptimPdf.dll, OptimPdf.Core.dll and the MSI with SHA-256 and an RFC 3161 time-stamp. Tested with a temporary self-signed certificate: all four signed and time-stamped. The certificate was deleted afterwards. **Required from Optim Square:** an OV or EV code-signing certificate (EV avoids SmartScreen warnings from the first download) |
| **Large-file performance** | Improved, measured | See below |
| Branding | Done | Splash screen with the logo; About window with version, components and a "View licenses" button; header checked in light and dark themes |
| License | Done | Optim PDF is open source under the MIT License; the installer shows it and installs it in the licenses folder with the third-party texts |
| Clean install on fresh Windows 10 and 11 | Not done | Needs a clean virtual machine; tested only on this Windows 11 PC |

### Performance (measured with a Release build)

| Document | Step | Before | After |
|---|---|---|---|
| 1,000 text pages (1 MB) | Page sizes on open/after edit | 352 ms | 18 ms |
| | Open; edit (rotate one page); search | 106 ms; 159 ms; 1.1 s (window frozen) | 10 ms; 174 ms; 1.2 s **in the background** (window stays responsive; a new search cancels the old one) |
| 150 photo pages (400 MB) | Memory after opening | 831 MB | 431 MB (the file is no longer copied for PDFium) |
| | Listing comments and fields after every edit | 200 ms, +800 MB | 12 ms, +1 MB (skipped when the file has none; loaded once when it does) |
| | One edit | 715 ms, 3.4 GB allocated | 482 ms, 2.0 GB allocated; memory is compacted afterwards |

Remaining limitation: an edit still rewrites the whole file. Peak memory during an edit is about 5× the file size, so a 400 MB scan needs about 2.4 GB while saving a change. Incremental saving for small edits (annotations, form values) would remove this, and is the next performance step.

---

## 0.4.0 — usability update (2026-10-12)


| Feature | Status | How it was checked |
|---|---|---|
| **Command bar:** logo, quick access (Open, Save, Print, Undo, Redo) on every tab, document name, a "Search in document" box (Ctrl+F; Ctrl+Shift+F opens the panel), theme switch, help, and collapsing the ribbon (Ctrl+F1). The ribbon uses lighter, inset group dividers | Built, screenshot-checked (dark) | Screens of the header with a document and a form open |
| **Select object:** click text, an image, a signature or a comment to get a dashed outline with corner handles. Drag to move, drag a corner to resize (uniform, the opposite corner stays put), arrow keys nudge (Shift: 10 pt), Delete removes, Esc deselects. Undo works | Tested (6 core tests), GUI-verified with real mouse | Placed text moved +200/−100 pt then nudged; resized from 82 to 160 pt wide by its corner; positions confirmed in the saved file |
| **Placed items stay one unit:** text boxes, images and visual signatures are tagged with marked content (`/OptimItem <</Id n>> BDC`), so a multi-line text or a drawn signature made of many strokes moves as one item, and still does after later edits | Tested | Tag survives PDFium's content rewrite |
| **Items are selected right after placing them,** so they can be moved immediately | GUI-verified | |
| **Add text without a dialog:** click and type on the page, in the chosen font, size and color. Enter adds a line; Ctrl+Enter or clicking outside places it; Esc cancels. Text-box comments work the same way | GUI-verified | "Signed in Dubai" typed in place, then extracted from the saved file |
| **Fill forms on the page:** click a text field and type in place. Tab and Shift+Tab move between fields in reading order; Enter saves. Dropdowns open a list in place. Check boxes toggle on click. Fields are tinted (Fill & Sign › Highlight fields toggles this), and the pointer shows an I-beam over text fields | GUI-verified with real input | Full name and Email typed, with Tab between them, and the check box clicked; all three values in the saved file |

Comments and stamps move and resize too: the rectangle, quad points, ink, vertices and lines are all transformed, and their popups move along (core-tested). Content of other PDFs can be selected one object at a time; shapes covering more than a quarter of the page are skipped, so a page background is not grabbed by accident.

---

**Build:** 0.3.0 (Phase 3) · Branding: app icon from the Optim Square logo (Assets/Logo.png), accent colors matched to it · **Last verified:** 2026-10-12 · Windows 11, .NET 10.0.203, Microsoft Office 16 installed

Status meanings:
- **Tested**: automated tests pass. Core tests reopen the output independently (PDFium for rendering, text and passwords; PDFsharp for structure; a raw byte scan that inflates every stream) and check pixels, text, passwords or bytes, not just that no exception was thrown.
- **GUI-verified**: exercised in the running app through UI Automation, with the saved file then checked by a separate program.
- **GUI pending**: core-tested, but the click or drag on the page in the app has not been driven by automation (see below).

## Test summary

- `dotnet test`: **181 passed, 0 failed** (48 MVP + 77 phase 2 + 56 phase 3). The 5 Office tests drive the installed Microsoft Office and report "skipped" on PCs without it.
- **Independent signature check:** signed files were validated with pyHanko 0.37 (an open-source PDF signature validator, used for testing only and not shipped). Single, double, invisible, GUI-signed and time-stamped files all reported `INTACT:TRUSTED, UNTOUCHED` (the first of two signatures: `ACCEPTABLE_MODIFICATIONS`).
- **GUI runs on 2026-10-12.** Clicks were posted to the app window so that the user's mouse was never moved. WPF only accepts such clicks when the real cursor happens to be over the window, so page clicks and drags could not be driven reliably (see "GUI pending").

| Flow | Result |
|------|--------|
| Edit text: Edit text tool, click a heading, type a new line with an em dash, Enter, Save | Saved file shows the new text (U+2014 intact). The old text is absent from the file bytes, including compressed streams |
| Redact: Mark text "board", Apply with "also remove hidden information", Save | "board" and the rest of its run are gone from the text layer and the bytes; a black box is drawn; no metadata left |
| Sign invisibly: create a self-signed ID in the dialog, Sign, Save | Banner reads "Signed (1). All signatures are valid." Our validator and pyHanko: valid, whole file covered. The test ID was deleted from the certificate store afterwards |
| Recognize text on a 200 DPI scan, Save | 17 words recognized; the saved file's text matches the page exactly |
| Batch: Add password (AES-256) with name template `{n:00} {name} protected` | Output `01 batch protected.pdf` requires the password, revision 6 |
| Ribbon tabs and the signed-document banner, dark theme | Screenshots checked |
| **Real mouse and keyboard (user allowed):** Add text by dragging a box; create a text field, check box and dropdown; drag a redaction area and Apply; Office → PDF through the file picker | All worked; the saved file has the text, the three fields (dropdown value "Gold") and the account line removed; letter.docx opened as a new PDF tab |
| **Real input, second session:** click to place an image; drag a visual signature (typed signature created in the dialog); drag a signature field; click a word to redact, Apply; drag a visible digital signature with a new digital ID, Save | All worked. Saved file: signature text drawn, `ClientSignature` field created, the word's run removed, digital signature valid (our validator; pyHanko `INTACT:TRUSTED, UNTOUCHED`). Test ID deleted and the user's saved signatures restored afterwards |

## Phase 3 features

| Area | Feature | Status | Notes |
|------|---------|--------|-------|
| Edit | Edit existing text: click a line, edit in place, Enter | Tested, GUI-verified | The original text objects are removed (PDFium) and the line is redrawn at the same baseline, angle, size and color. The font is matched to an installed family (e.g. Helvetica → Arial, subset prefixes stripped); bold and italic are kept. Characters the font lacks fall back per character. Works on rotated pages. An empty line deletes it |
| Redact | Mark areas (drag), words (click) or every match of a search; Apply | Tested, GUI-verified | Content touching a mark is deleted from the file. The visible remainder of affected runs is kept as a 300 DPI image rendered with the marked pixels destroyed first. Annotations and form fields in the area are deleted, unused fonts and images are dropped, and page thumbnails are removed. Verified by text extraction and raw byte scans |
| OCR | Recognize text (Windows OCR) | Tested, GUI-verified | Invisible text (render mode 3) over every word, scaled to the word's box, so search hits line up with the image. Rotated pages and skipping pages that already have text are supported. Offline, using the OCR languages installed in Windows (en-US on this PC) |
| Convert | Word, Excel, PowerPoint → PDF | Tested (Office 16) | Uses the Microsoft Office installed on the PC, invisibly, with macros disabled. Password-protected files fail cleanly instead of prompting. Office is closed only if Optim PDF started it. There is a 3-minute timeout |
| Convert | PDF → Word (.docx) | Tested (Word 16) | Word's own PDF reflow; the layout is rebuilt for editing. Word's "will now convert" notice is suppressed for the conversion only, and the user's setting is restored afterwards |
| Forms | Create text fields, check boxes, dropdowns and signature fields; delete fields | Tested, GUI-verified (creation) | Appearances are generated, so every viewer shows them. Fields are deleted with their widgets from every page |
| Sign | PAdES digital signatures (ETSI.CAdES.detached, SHA-256, signing-certificate-v2) | Tested, pyHanko-verified, GUI-verified (visible and invisible) | Written as an incremental update, so the original bytes are untouched and earlier signatures stay valid. Visible (with an optional handwritten mark) or invisible. Signs empty signature fields too. Reason and location are supported. Works with files that use cross-reference or object streams |
| Sign | Trusted time-stamp (RFC 3161, PAdES B-T) | Tested against timestamp.digicert.com | Optional and needs internet; only a hash is sent |
| Sign | Validate signatures: Signatures panel and banner | Tested | Checks the byte range, digest and signature, flags changes after signing, and builds the certificate chain. Trusted roots, your own IDs and "Trust this signer" (Trusted People) count as trusted. Revocation is not checked offline |
| Sign | Digital IDs | GUI-verified (create) | Certificates from the Windows store (including smart cards), a .pfx file, or a new self-signed ID (RSA 3072, 5 years) stored in Windows. Private keys never leave Windows |
| Batch | Compress, add password, remove hidden information, OCR, rotate, convert to PDF, export images, rename, merge | Tested; Add password GUI-verified | Inputs are never modified; existing outputs are never overwritten. A failed file is reported and the batch continues. Name template fields: {name} {n} {n:000} {date} {title} |
| Text | Arabic, Persian, Urdu (basic letters) and Hebrew | Tested | Contextual shaping with lam-alef ligatures and Unicode bidi ordering. Extracts in logical order. Right-aligned in boxes. Used by Add text, Edit text, text boxes, stamps and form field values (right-aligned) |

## Problems found and fixed in this phase

1. **PDFium bitmap format numbers** were off by one in the new editing code: BGR is 2, not 1. The result was garbled redaction remainders and a crash, caught by pixel tests.
2. **Images inserted through PDFium read their pixels at save time**, so a managed buffer produced black images. The buffer is now owned by PDFium.
3. **Images → PDF ignored the DPI of PNG and TIFF files** (a phase 2 bug): a 300 DPI scan came out 3× too large. The DPI is now kept, and PNG's 96.012 DPI rounding is snapped to 96.
4. **PDFium extraction misorders Arabic words when /ActualText is present.** The marker was removed; plain visual-order text extracts correctly.
5. **Word showed a hidden "convert PDF" notice and hung** (DisplayAlerts doesn't cover it). It is now suppressed for the conversion only.
6. **A signed file grew by about 64 KB** because of a fixed-size signature placeholder. The placeholder is now sized to the certificate chain (6.7 KB measured for a self-signed 2048-bit certificate; about 23 KB with a time-stamp).
7. **One missing symbol switched the whole edited line to a fallback font.** Fallback now applies per character.
8. **New empty text fields and dropdowns were invisible on the page** (no border or background), found in the real-mouse run. They now get a light box and border (/MK), drawn in their appearance.
9. **Empty signature fields were counted as signatures**: PDFium lists unsigned signature fields, so a document with one showed "at least one signature is invalid" and warned that edits would invalidate it. Unsigned fields are now skipped (regression test added). Found in the real-input run.
10. **Click-placed images and signatures could run off the page edge** (default box from a click near the edge). The default box now shrinks to stay on the page.
11. **Screen readers read raw object text** for items in the digital ID and OCR language lists. They now have proper names. Pages are also exposed to screen readers as "Page N".

## Known limitations

1. **Redaction rasterizes the rest of a touched text run.** Words that shared a text run with redacted words stay visible but become part of an image, so they are no longer searchable (in the GUI test, the whole line "Prepared for the … of directors." became an image). Running OCR afterwards makes it searchable again. Redaction does not search bookmarks, metadata or attachments; the Apply dialog offers to remove hidden information as well.
2. **Editing existing text works one line at a time.** It does not reflow paragraphs. The line is redrawn with an installed font, so the result differs from the original when the original font is not installed (the status bar says so). Text inside form XObjects and Type 3 fonts cannot be edited.
3. **Scripts that need a shaping engine are refused**, not written incorrectly: Devanagari, Bengali, Tamil and other Indic scripts, Myanmar, Khmer, Tibetan, Syriac, and Urdu letters without presentation forms (ٹ ڈ ڑ ں ھ ہ ے). This corrects 0.2.0, which said "any left-to-right script": Indic scripts were not refused there and would have been laid out wrongly. Arabic diacritics are placed approximately. Arabic words inside a left-to-right line extract in visual order.
4. **Digital signatures:**
   - Password-protected documents cannot be signed.
   - Any later edit other than another signature rewrites the file and invalidates the signatures. The app warns before that happens.
   - No certification (DocMDP) signatures yet.
   - No long-term validation (PAdES B-LT/LTA).
   - Revocation is not checked unless asked. The time-stamp server's own certificate is verified, but its trust is not evaluated.
5. **Office conversion needs Microsoft Office** on the PC (no Office: the buttons explain this). Only PDF → Word is offered, not PDF → Excel or PowerPoint.
6. **OCR quality and languages are those of Windows OCR.** It does not deskew or correct page orientation, and very small text (under about 8 pt at 300 DPI) may be missed.
7. **Every edit re-serializes the whole file in memory.** Large files (hundreds of MB) feel slow to edit; viewing is unaffected.
8. **XFA forms are not supported.**
9. **GUI testing note:** the app offers crash recovery for force-closed sessions; automated runs must clear their own recovery snapshots first, or the recovery prompt (correctly) blocks the page. The Office → PDF picker and every page gesture are now GUI-verified.

Earlier phases: all MVP and phase 2 features remain as described in 0.2.0, and their tests still pass. The phase 2 limitation "Arabic and Hebrew can't be added" is resolved.
