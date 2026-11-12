# Architecture

Editor is a single-document plain text editor. The working name is a placeholder. The text engine has no UI dependency and is the part that is tested on Linux. The window, the Win2D surface, UI Automation, and the Windows text input context live in the app project and build only on Windows.

## Projects

| Project | Role |
| --- | --- |
| `src/Editor.Core` | Piece table, line index, encoding, line endings, caret session, find/replace, atomic save, and the UI-free renderer models. `net8.0`. |
| `src/Editor.App` | WinUI 3 single-project MSIX. One window. `net8.0-windows10.0.19041.0`, x64 and ARM64. |
| `tests/Editor.Core.Tests` | xUnit. Each behavioral theory runs with every feature on and with each feature off. |
| `tests/Editor.Core.PerfTests` | Headless open, memory, and insert gates. |

`Editor.Core` is the public surface the app calls. Feature code reads `EditorSettings.Features` and does not keep a second copy of a switch.

## Document model

A document is a piece table.

- The original file is an immutable byte source. Files under 1 MiB are copied into a `byte[]`. Larger files are a read-only memory-mapped view, so opening a file does not copy it onto the managed heap.
- Edits append UTF-8 bytes to an add buffer. The buffer is never truncated by undo. A logical length tracks the live prefix. A new edit drops the abandoned redo branch and then appends.
- The table is an ordered list of pieces `(source, start, length, encoding)`. A prefix-sum array locates a byte offset by binary search. The original piece starts after the BOM, so the BOM is not part of the visible text.
- Undo and redo restore the piece list and the logical add length. They do not discard bytes the other stack still names.

Coordinates:

- Storage and the piece table use byte offsets.
- The caret, find, UI Automation, and the text input context use UTF-16 offsets snapped to grapheme boundaries.
- The status column is a display column. A tab stops every 8 columns. Every other grapheme is one column wide, including emoji. This is a deliberate M1 simplification.

`IsModified` is true only when the revision differs from the revision at the last save. A freshly opened file and a new empty document are clean. Undo back to that revision is clean again.

## Line index

The index is lazy. It records the byte offset where each scanned line starts and remembers how far the scan reached. An edit before that point drops the index. An edit at or past it does not.

`LayoutFirstPage` scans only a bounded window, about `lines * (columns * 4 + 2)` bytes, so the first page of a large file does not walk the file. A newline that straddles the window is still recognized: the scanner peeks one unit past the window so a CR/LF pair is not split.

Line breaks are CR, LF, and CRLF. UTF-8 and ANSI are scanned as bytes (`0x0D` and `0x0A` cannot be UTF-8 continuation bytes). UTF-16 is scanned as 16-bit units. U+2028 is not a line break.

## Encoding

Detection order:

1. A BOM is honored even when encoding detection is off (`EF BB BF`, `FF FE`, `FE FF`).
2. When detection is on, the sample is checked as strict UTF-8 (no overlong sequences, no surrogates). The sample is the whole file when the file is at most 64 KiB, otherwise the first 64 KiB.
3. Anything else is ANSI. The default code page is 1252 so Linux and Windows agree in tests. The Windows shell substitutes the active ANSI code page when that page is a real ANSI page.

An unedited document (`revision == 0`) is saved by copying the original bytes, including the BOM and any bytes the detector never inspected. A wrong guess cannot change the file until the user edits it. After an edit, the save re-encodes with the detected encoding and preserves the BOM.

UTF-32 is not detected. A UTF-16 file without a BOM is not detected as UTF-16. DBCS ANSI is best-effort; the tests use Windows-1252.

## Line endings

The predominant break in the examined sample is the preferred ending. More than one kind is mixed. A file with no breaks is not mixed; Enter inserts CRLF unless the settings change the default. Mixed files keep each existing break until the user converts.

Conversion is one undo step. It rebuilds the document as a single UTF-8 add piece, and the next save re-encodes with the document encoding.

## Editing session

`EditingSession` owns the caret and the anchor. Left and right move by grapheme (`StringInfo` / ICU). Word characters are letters, digits, and underscore. Page Down asks the index for the destination line before it moves, so a short index does not collapse the move into a single line. Home, End, and Select All can scan forward to the end of the file. That scan is not on the open path.

Backspace and Delete remove one grapheme, or join the neighboring lines at the edge of a line. Typing and paste replace the selection as one undo step.

## Find and go to line

Find and replace are literal. Match case is ordinal or ordinal-ignore-case. Matches do not overlap. An empty pattern matches nothing. `RegexTextSearcher` is the extension point and throws `NotSupportedException` until a later milestone.

Find walks the document in byte windows and keeps the last `pattern.Length - 1` characters, so a match that crosses a window is reported once, including when the window ends inside a multibyte character. A cancelled search, or a search whose document changes between windows, throws and does not return a partial list. The find bar runs that work off the UI thread. `GetText` and pattern movement still refuse a document larger than 64 MiB. With the feature off, find returns no matches and replace changes nothing.

Go to line is 1-based, clamps to the scanned line count, and moves the caret when the feature is on. When it is off, the call returns false and the caret stays.

## Save

`AtomicFileWriter` checks that the destination is not read-only and can be opened with exclusive access before anything is truncated. The atomic path writes `.{name}.{guid}.tmp` in the same directory, flushes, then `File.Replace` (or `File.Move` when the destination does not exist). A failure deletes the temp file and leaves the destination as it was.

With atomic save off, the same access check runs, and the write then goes to the destination directly.

A modified memory-mapped file that is saved back to the same path cannot be replaced while the map is open. The save copies the original bytes into a private backing file when the live piece table or the undo stack still names them, unmaps, then replaces the destination. Piece offsets now name the backing file, so undo and redo stay valid. The destination replace is still atomic when that flag is on. A later save of the same path uses the normal writer, because the destination is no longer the mapped source. The backing file is removed when the document is disposed. Unmodified saves copy the original bytes.

A modifying save copies unedited original pieces as raw bytes and encodes only the new text. If the encoding was a provisional guess from the first 64 KiB, the save first rechecks the whole original file. A later byte that is not UTF-8 selects ANSI, and the unedited bytes are still copied rather than replaced with a fallback character. Save As can choose UTF-8, UTF-8 with BOM, UTF-16 LE, UTF-16 BE, or ANSI, and CRLF, LF, or CR. ANSI uses the system code page.

Ctrl+End, Go to a line far from the indexed prefix, and a scrollbar jump use a short probe around an estimated byte offset. They do not walk the line index from the start of a large file. The line number on that path is an estimate.

## Feature flags

`settings.json` is a single object. Every module reads `features.<name>`.

| Name | On | Off |
| --- | --- | --- |
| `findReplace` | Find bar, menu command, find and replace. | Menu command is omitted. Find returns nothing. Replace does not edit. |
| `goToLine` | Go to line command and dialog. | Command is omitted. The request does not move the caret. |
| `statusBar` | Line, column, encoding, line ending. The ending button converts. | The bar is collapsed. Status queries return null. |
| `atomicSave` | Temp file, then replace. | In-place write after the same read-only and lock check. |
| `encodingDetection` | BOM, then strict UTF-8, then ANSI. | BOM, otherwise UTF-8. Unedited bytes are still copied exactly. |

`EDITOR_DISABLE_FEATURES` adjusts `EditorSettings.CreateDefault()`: empty or `none` leaves everything on, `*` turns everything off, and a comma-separated list turns those names off. `AllEnabled()` and `WithFeature()` ignore the variable so the theory matrix stays stable. The Windows app loads `%LocalAppData%\Editor\settings.json` and falls back to `CreateDefault()` when the file is missing or unreadable. Unknown JSON names are ignored.

## Window

The shell is one window: File (new, open, save, save as, exit), Edit (undo, redo, cut, copy, paste, select all, and the commands the flags leave enabled). A disabled feature is absent from the menu, not a hidden control that can still be invoked. The find bar stays collapsed until Find is used. The go-to dialog is created only when that command runs. Closing a modified document asks to save, discard, or cancel.

New documents are UTF-8 without a BOM and use CRLF. The title is `name — Editor`, with a leading `*` when the document is modified.

The editor surface is a Win2D `CanvasControl` the size of the viewport. Each paint asks the line index only for the visible rows and asks the layout helper only for the cells inside the horizontal window. The scrollbar range for a file that is not fully indexed is an estimate. Dragging it far down indexes forward from the start; that work is user-driven and is not part of open.

## Renderer models

These types are in `Editor.Core` so they can be tested without WinUI:

- `ViewportLayout` chooses the line window.
- `MonospaceLayout` returns the visible cells and hit-tests a pixel to a UTF-16 offset.
- `TextPatternModel` is the document, selection, caret, visible range, and grapheme movement used by UI Automation.
- `TextInputModel` is the composition state machine. A composition update replaces the current composition range. An emoji-panel commit and an IME commit are both text replacements.

The WinUI types are wrappers: `EditorAutomationPeer` implements `ITextProvider2` and `ITextRangeProvider`, and `TextInputController` connects `CoreTextEditContext`. See `docs/renderer-poc.md` for what is executed and what is only reasoned through.

## Out of this milestone

Regular-expression search, word wrap, printing, drag-and-drop, multiple windows, a recent-file list, and double-width emoji columns are not in this slice. `GetText` and pattern movement refuse a document larger than 64 MiB. Drag-and-drop, wide-glyph column width, scroll-rate gates, and moving GitHub Actions off Node 20 are tracked for a later milestone.
