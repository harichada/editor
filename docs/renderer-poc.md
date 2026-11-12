# Renderer proof of concept

The custom text surface has three behaviors that a stock text box would have provided. They are implemented directly, and the parts that do not need WinUI are isolated so they can run under xUnit.

## 1. UI Automation text pattern

`TextPatternModel` answers the questions a text pattern is asked:

- document text for a UTF-16 range, read line by line
- the selection and the caret
- the visible line window
- movement by grapheme, word, line, and document

`EditorAutomationPeer` is the WinUI `AutomationPeer` for the surface. It returns itself for `PatternInterface.Text` and `PatternInterface.Text2`, and it implements `ITextProvider2` and `ITextRangeProvider`. `GetCaretRange` reports whether the surface has keyboard focus. `GetSelection` returns the caret or the selection. `GetVisibleRanges` returns the lines the viewport has indexed. `GetAttributeValue` returns the reserved UI Automation "not supported" object from `UiaGetReservedNotSupportedValue` when that export is available.

A range that means "through the end of the document" does not scan a large file just to discover the length. `GetText(-1)` on a document bigger than 64 MiB returns the first 8192 characters. `Move` on a document that large leaves the endpoint where it is and reports that nothing moved.

## 2. Inline text input

`TextInputModel` is the composition state machine:

- begin records the caret
- update replaces the current composition text and keeps the caret at the end of it
- complete keeps the last text
- cancel deletes the composition
- a text update that is not a composition (keyboard text, paste from the input framework, or an emoji-panel commit) replaces the requested range

`TextInputController` creates a `CoreTextEditContext` from `CoreTextServicesManager.GetForCurrentView()` after the surface has loaded, with input scope `Text`. It forwards `TextRequested`, `SelectionRequested`, `TextUpdating`, `SelectionUpdating`, `CompositionStarted`, `CompositionCompleted`, `LayoutRequested`, and `FocusRemoved`. Layout bounds are the control rectangle and the caret rectangle. Enter, Backspace, and Delete are handled by the surface so the context does not insert them a second time. If the context cannot be created, the error is kept and ordinary characters fall through to `CharacterReceived`.

## 3. Grapheme caret

Caret movement, hit testing, and the cells that get drawn all go through `GraphemeNavigator`. A ZWJ sequence, a combining mark, a flag, a skin-tone sequence, and a surrogate pair are one caret step. The caret is drawn on a cell boundary, never in the middle of a cluster. Backspace and Delete remove the same cluster.

The paint loop lays out only the visible rows and, within a row, only the cells in the horizontal window.

## What was executed

These are xUnit tests in `Editor.Core.Tests`. They run on Linux and do not start WinUI:

| Behavior | Test |
| --- | --- |
| ZWJ family, combining marks, flags, skin tone, and surrogate pairs move as one cluster | `NavigationTests` |
| A CRLF pair is one line break | `NavigationTests` |
| The first page of a 4000-line document does not finish the line index | `RendererModelTests` |
| A long line returns only the cells inside the horizontal window | `RendererModelTests` |
| Hit testing snaps to cluster boundaries | `RendererModelTests` |
| Text pattern text, selection, caret, and grapheme movement | `RendererModelTests` |
| Composition begin, update, complete, and cancel | `RendererModelTests` |
| An emoji commit inserts the character and leaves the caret after it | `RendererModelTests` |

The piece-table fuzz test checks that ASCII edits, including newlines, match a string model across undo and redo.

## What was checked by compiling, not by running

The Windows CI job compiles the WinUI project, which type-checks:

- `EditorAutomationPeer` against `ITextProvider2` and `ITextRangeProvider`
- `TextInputController` against `CoreTextEditContext`
- the Win2D `CanvasControl` draw loop

That compilation does not prove the peers are wired the way Narrator expects, and it does not prove a glyph was painted.

## What was not run

- Narrator reading the document, the caret, and the selection
- NVDA doing the same
- A live IME composition
- The emoji panel (`Win+.`) inserting into the surface
- A cold start on a machine with a desktop session, unless the startup probe in CI managed to create a window

Those are the checks for a Windows tester. The acceptance list is `docs/m1-acceptance.md`.
