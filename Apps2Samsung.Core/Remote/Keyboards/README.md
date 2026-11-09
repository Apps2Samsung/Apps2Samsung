# On-screen keyboard profiles

How the remote types into apps that draw their own keyboard, and how to add an app.

## Why this exists

`SendInputString` targets the TV's **system IME**, so it types into Smart Hub search, the browser and
settings — but not into apps like YouTube, Netflix or TizenTube. Those paint their own keyboard: they
emit no `ms.remote.ime*` events at all, and the page has no native `<input>` to put text in (measured
on a QA55S90FAKXXT, Tizen 9.0). Their grids do accept the remote's **digit key codes** (`KEY_1`…`KEY_9`),
which is the tell — 1-2-3 lands in a YouTube search box while letters never do. Letters exist only on
their grid, so the grid is what gets pressed: arrows to the cell, `ENTER` to type it.

## Adding an app

1. Copy `youtube.json` to `<app>.json` and set `id`, `label` and `appId` (the app's Tizen id, e.g.
   `org.tizen.netflixapp` — `/api/v2/applications/<id>` on the TV confirms it).
2. Edit `rows`, `cells`, `opensOn` and `homeKeys` to match what that app's keyboard actually looks like.
   Everything is measured by hand once, on a real set (see *Measuring* below).
3. Add a test asserting the geometry you measured (`Apps2Samsung.Core.Tests/Remote/SamsungRemoteKeyboardProfileTests.cs`)
   and run `dotnet test`.
4. Nothing else: the file is embedded, `SamsungRemoteKeyboardProfiles` picks it up, and it appears in
   the remote's **App keyboard** picker.

Users can also supply a profile without shipping one: paste the same JSON into the remote's keyboard
panel and save, and it is stored per app id in settings (a user profile beats the built-in one).

## The format

```json
{
  "id": "youtube-search",
  "label": "YouTube",
  "appId": "9Ur5IzDKqV.TizenYouTube",
  "rows": ["abcdefg", "hijklmn", "opqrstu", "vwxyz-'", ""],
  "cells": { "DELETE": [0,7], "SPACE": [4,0], "CLEAR": [4,1], "SUBMIT": [4,2] },
  "opensOn": [0,0],
  "homeKeys": ["KEY_UP","KEY_UP","KEY_UP","KEY_UP","KEY_UP","KEY_UP","KEY_DOWN"],
  "wrapHorizontal": false,
  "columnResetRows": [4],
  "directKeys": { "1": "KEY_1" }
}
```

| Field | Meaning |
|---|---|
| `rows` | The cells, top row first, as the characters they type. An action cell is named in `cells` instead; its column is padded automatically. |
| `cells` | Named action cells: `SPACE`, `DELETE`, `CLEAR`, `SUBMIT` (aliases: `BACKSPACE`, `SEARCH`) → `[row, col]`. A column past 64 is rejected rather than padded — no keyboard is that wide and a typo would otherwise blow the row up. |
| `opensOn` | Where the app leaves the cursor when its keyboard opens. Plans start here, so nothing has to hunt for a corner. |
| `homeKeys` | Presses that put the cursor back on `opensOn` from anywhere. **Measured, never derived** — "one Left per column" is wrong on a wrapping row. |
| `wrapHorizontal` | Whether Left/Right loop within a row. Leave `false` unless you have seen it wrap. |
| `columnResetRows` | Rows entered at column 0 whatever column you came from (wide button rows). |
| `directKeys` | Characters typed with a real key code instead of a cell — digits, because the remote has digit keys. |

## Measuring a new keyboard (what the tests pin)

Open the app's keyboard on the TV and read the rows off the screen. Then confirm the mechanics by
pressing, using **digit key codes as separators** — they always type, so they label the output:

- `ENTER` on a cell types it; `RIGHT`/`LEFT` move within the row (a `LEFT` at column 0 leaves the grid
  for the side nav, so never plan one).
- `DOWN`/`UP` move between rows and keep the column.
- Where does `DOWN` from the letter rows land — the same column, or the first button of a wide row?
- Which keys put the cursor back on `opensOn` from the bottom row? (On YouTube three `UP`s was not
  enough; six was.)
- Does typing append or replace? (YouTube appends — hence `CLEAR` before a new query.)
- Does typing alone search? (It does not; `SUBMIT` does.)

Write the answers into the JSON and into a test. A wrong profile types the wrong letters silently, which
is why the planner refuses to guess when an app has no profile at all.
