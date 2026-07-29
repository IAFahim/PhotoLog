# PhotoLog

Local desktop app that stamps a date, optional address, and caption on photos — then exports copies. Originals are never modified.

**Avalonia only.** There is no Gradio / Hugging Face Space UI.

## Run

```bash
cd PhotoLog.Avalonia
dotnet run
# open a folder path at launch:
dotnet run -- ~/Pictures/trip
```

Headless pipeline check (no window, no model required):

```bash
dotnet run -- --selfcheck
```

Publish a self-contained binary:

```bash
dotnet publish -c Release -r linux-x64 --self-contained true -o bin/publish/linux-x64
```

## Features

- Recursive folder scan with stable name dedup
- EXIF date (or file mtime); optional day override keeps each photo’s clock
- Optional multi-line address stamp
- Optional AI caption (Gemma 4 E2B via llama.cpp, one-time download, then offline)
- **Captioned exports are renamed** from a filesystem-safe slug of that caption; collisions get `_1`, `_2`, …
- Empty caption keeps the original file name

## Caption → file name

| Caption | Export base name |
|---------|------------------|
| `A quiet street at sunset.` | `A quiet street at sunset.jpg` |
| (empty) | original name, e.g. `IMG_0123.jpg` |
| same caption twice | second file gets `_1` before the extension |

Tidy strips chat-template debris, keeps one short line, drops a trailing period — that cleaned line is what stamps and renames.

## Layout

1. Load a folder  
2. Select photos (click thumbnails)  
3. Optional: date override, address, caption (or **Caption** / **Caption selected**)  
4. **Apply to selected** → stamped copies in the output folder  

Model download lives under the platform app-data folder (`…/PhotoLog/models`).
