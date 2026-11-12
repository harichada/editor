# M1 acceptance

Run the packaged app or the portable zip on Windows 10 22H2 or Windows 11. The window title ends with "Editor".

Settings live in `%LocalAppData%\Editor\settings.json`. Missing or unreadable settings mean every feature is on. Set a `features` value to `false` to turn that feature off, then restart.

```json
{
  "features": {
    "findReplace": true,
    "goToLine": true,
    "statusBar": true,
    "atomicSave": true,
    "encodingDetection": true
  },
  "ansiCodePage": 1252,
  "defaultLineEnding": "crlf"
}
```

## Document

- New document is empty, UTF-8, CRLF, and not marked modified.
- Type, Enter, Backspace, Delete, arrows, Home, End, Page Up, Page Down, and word movement behave as a plain editor.
- Undo and redo cover typing, paste, and a line-ending conversion. Undo back to the saved text clears the modified mark.
- Cut, copy, paste, and select all use the clipboard.
- Closing a modified document offers save, discard, and cancel.

## Files

- Open and save a UTF-8 file, a UTF-8 file with BOM, UTF-16 LE with BOM, UTF-16 BE with BOM, and an ANSI file. The status encoding matches. Save without an edit and the bytes are unchanged, including an empty file, a file with no trailing newline, and a file that contains NUL.
- CRLF, LF, CR, and mixed files keep their breaks across an unedited save. The status names the ending. Choosing another ending converts, and undo restores the previous breaks.
- Save As writes the chosen path. A read-only or locked target leaves the original file intact and shows an error.
- A file of about 100 MB shows its first page without a long wait. Scrolling is the thing that indexes further lines.

## Find, go to, status

- Ctrl+F shows the find bar. Next, replace, and replace all are literal. Match case is honored. There is no regular-expression control.
- Ctrl+G asks for a line and moves there.
- The status bar shows 1-based line and column, the encoding, and the line ending.
- With `findReplace` off, the Find command is absent and the bar cannot be opened.
- With `goToLine` off, the Go to line command is absent.
- With `statusBar` off, the bar is not shown.

## Renderer

- The caret does not land inside an emoji ZWJ sequence, a flag, a skin-tone sequence, or a combining mark. Backspace removes the whole cluster.
- Win+. inserts the chosen emoji at the caret.
- An IME composition is underlined and replaces itself until it commits.
- Narrator and NVDA can read the text, the caret, and the selection.

## Not in this slice

Regular expressions, word wrap, printing, drag-and-drop, more than one window, encoding conversion, and double-width emoji columns.
