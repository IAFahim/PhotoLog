import html
import os
import shutil
import subprocess
import sys
import tempfile
import zipfile
from datetime import datetime
from pathlib import Path

import gradio as gr
from PIL import ExifTags, Image, ImageDraw, ImageFont, ImageOps

EXTS = {".jpg", ".jpeg", ".png", ".webp", ".tif", ".tiff"}
DATE_FMT = "%b %-d, %Y at %-I:%M:%S %p"  # Jul 28, 2026 at 8:23:59 AM
# hosted mode: browser upload + zip download, no server-disk access (--share tunnels to visitors, so same rules)
WEB = bool(os.environ.get("PHOTOLOG_WEB")) or "--share" in sys.argv

THUMB_ROOT = Path(tempfile.gettempdir()) / "photolog"
THUMB_ROOT.mkdir(exist_ok=True)
# gradio only serves whitelisted dirs; env var (not launch kwarg) so it survives `gradio` reload mode
os.environ["GRADIO_ALLOWED_PATHS"] = ",".join(
    filter(None, [os.environ.get("GRADIO_ALLOWED_PATHS"), str(THUMB_ROOT)]))

CSS = """
#plsel-carrier {display:none !important;}
#pltop {display:flex; justify-content:space-between; align-items:center; margin-bottom:6px;}
#pltop button {padding:4px 14px; border:1px solid #999; border-radius:6px; background:transparent; cursor:pointer;}
#plwrap {display:flex; gap:14px; align-items:flex-start;}
#plgrid {flex:3; display:grid; grid-template-columns:repeat(auto-fill,minmax(150px,1fr)); gap:10px;
         max-height:600px; overflow-y:auto; padding:4px;}
.plcell {position:relative; cursor:pointer; border-radius:8px; overflow:hidden; line-height:0;}
.plcell img {width:100%; height:140px; object-fit:cover; display:block;}
.plcell .plbox {position:absolute; top:6px; left:6px; width:22px; height:22px; border:2px solid #333;
  border-radius:5px; background:#fff; color:transparent; font-weight:bold; font-size:15px;
  text-align:center; line-height:20px;}
.plcell.sel .plbox {background:#22c55e; color:#fff; border-color:#166534;}
.plcell .plname {position:absolute; bottom:0; left:0; right:0; font-size:11px; line-height:1.3;
  background:rgba(0,0,0,.55); color:#fff; padding:2px 4px; white-space:nowrap; overflow:hidden;
  text-overflow:ellipsis;}
#plside {flex:2; position:sticky; top:8px; text-align:center;}
#plside img {max-width:100%; max-height:520px; border-radius:8px;}
#plside img:not([src]) {display:none;}
#plpname {font-size:13px; margin:6px 0; opacity:.85;}
#pldl {display:none; padding:6px 14px; border:1px solid #999; border-radius:6px; text-decoration:none;}
"""

# ponytail: all selection/preview interaction is client-side (gradio 6 ignores synthetic DOM events,
# so the server only reads selection at real-click time via the js input-transform below)
JS = """
() => {
  window.plSel = () => Array.from(document.querySelectorAll('.plcell.sel'))
      .map(c => c.dataset.name).join('\\n');
  window.plCount = () => {
    const el = document.getElementById('plcount');
    if (el) el.textContent = document.querySelectorAll('.plcell.sel').length + '/' +
        document.querySelectorAll('.plcell').length + ' selected';
  };
  document.addEventListener('click', (e) => {
    const cell = e.target.closest('.plcell');
    if (cell) {
      cell.classList.toggle('sel');
      window.plCount();
      const img = document.getElementById('plpimg'), name = document.getElementById('plpname'),
            dl = document.getElementById('pldl');
      if (img) {
        img.src = cell.querySelector('img').src;
        name.textContent = cell.title + (cell.classList.contains('sel') ? '  [selected \\u2713]' : '  [not selected]');
        dl.href = img.src;
        dl.download = cell.dataset.name;
        dl.style.display = 'inline-block';
      }
      return;
    }
    if (e.target.id === 'plall' || e.target.id === 'plnone') {
      document.querySelectorAll('.plcell').forEach(c => c.classList.toggle('sel', e.target.id === 'plall'));
      window.plCount();
    }
  });
}
"""
HARVEST_SCAN = "(f, d, a, s, sel) => [f, d, a, s, window.plSel ? window.plSel() : '']"
HARVEST_APPLY = "(s, sel, o, d, a) => [s, window.plSel ? window.plSel() : '', o, d, a]"
HARVEST_APPLY_WEB = "(s, sel, d, a) => [s, window.plSel ? window.plSel() : '', d, a]"


def exif_dt(img: Image.Image, path: Path) -> datetime:
    exif = img.getexif()
    raw = exif.get_ifd(ExifTags.IFD.Exif).get(36867) or exif.get(306)  # DateTimeOriginal, DateTime
    if raw:
        try:
            return datetime.strptime(str(raw), "%Y:%m:%d %H:%M:%S")
        except ValueError:
            pass
    return datetime.fromtimestamp(path.stat().st_mtime)


def stamp_lines(img: Image.Image, path: Path, custom_date=None, custom_addr: str = "") -> list[str]:
    dt = exif_dt(img, path)  # time ALWAYS comes from the photo
    if isinstance(custom_date, datetime):
        dt = custom_date.replace(hour=dt.hour, minute=dt.minute, second=dt.second)
    return [dt.strftime(DATE_FMT)] + custom_addr.strip().splitlines()


def stamp(img: Image.Image, lines: list[str]) -> Image.Image:
    size = max(14, img.width // 30)
    text = "\n".join(lines)
    kw = dict(font=ImageFont.load_default(size), anchor="ra", align="right", spacing=size // 3)
    d = ImageDraw.Draw(img)
    x, y, off = img.width - size // 2, size // 2, max(1, size // 12)
    d.multiline_text((x + off, y + off), text, fill=(0, 0, 0), **kw)  # soft shadow
    d.multiline_text((x, y), text, fill=(255, 255, 255), **kw)
    return img


def load_thumb(p: Path, max_side: int = 768) -> Image.Image:
    img = Image.open(p)
    img.draft("RGB", (max_side * 2, max_side * 2))  # ponytail: decode JPEGs small — big speedup on scan
    img = ImageOps.exif_transpose(img)
    img.thumbnail((max_side, max_side))
    return img.convert("RGB")


def grid_html(st: dict | None) -> str:
    if not st:
        return '<p style="opacity:.6">Load a folder to see previews.</p>'
    cells = []
    for i, n in enumerate(st["names"]):
        sel = " sel" if n in st["sel"] else ""
        cells.append(
            f'<div class="plcell{sel}" data-name="{html.escape(n)}" title="{html.escape(st["caps"][n])}">'
            f'<img src="/gradio_api/file={st["dir"]}/{i}.jpg" loading="lazy">'
            f'<span class="plbox">✓</span><span class="plname">{html.escape(n)}</span></div>'
        )
    return (
        f'<div id="pltop"><span id="plcount">{len(st["sel"])}/{len(st["names"])} selected</span>'
        f'<span><button id="plall" type="button">Select all</button> '
        f'<button id="plnone" type="button">Select none</button></span></div>'
        f'<div id="plwrap"><div id="plgrid">{"".join(cells)}</div>'
        f'<div id="plside"><img id="plpimg">'
        f'<div id="plpname">Click an image to preview it and toggle selection</div>'
        f'<a id="pldl">Download this image</a></div></div>'
    )


def pick_folder(cur: str) -> str:
    # ponytail: app runs locally, so a server-side native dialog IS the user's own desktop
    r = subprocess.run(["zenity", "--file-selection", "--directory", "--title", "Pick photo folder"],
                       capture_output=True, text=True)
    return r.stdout.strip() or cur


def scan(folder: str, custom_date=None, custom_addr: str = "", st: dict | None = None, sel_csv: str = ""):
    root = Path(folder).expanduser() if folder else None
    if not root or not root.is_dir():
        return grid_html(None), None, "Pick a folder with Browse, or type its path and press Enter."
    paths = sorted((p for p in root.rglob("*") if p.suffix.lower() in EXTS), key=lambda p: p.name)
    return _scan_paths(paths, custom_date, custom_addr, st, sel_csv)


def scan_upload(files: list[str] | None, custom_date=None, custom_addr: str = "",
                st: dict | None = None, sel_csv: str = ""):
    paths = sorted((Path(f) for f in files or [] if Path(f).suffix.lower() in EXTS), key=lambda p: p.name)
    return _scan_paths(paths, custom_date, custom_addr, st, sel_csv)


def _scan_paths(paths: list[Path], custom_date, custom_addr, st, sel_csv):
    if not paths:
        return grid_html(None), None, "No images in that folder."
    if st:
        shutil.rmtree(st["dir"], ignore_errors=True)
    tmpdir = tempfile.mkdtemp(prefix="scan_", dir=THUMB_ROOT)
    files_map, caps = {}, {}
    for p in paths:
        name, i = p.name, 1
        while name in files_map:  # same filename in nested subfolders
            name, i = f"{p.stem}_{i}{p.suffix}", i + 1
        files_map[name] = str(p)
    for i, (name, src) in enumerate(files_map.items()):
        img = load_thumb(Path(src))
        lines = stamp_lines(img, Path(src), custom_date, custom_addr)
        stamp(img, lines)
        caps[name] = f"{name} — {lines[0]}"
        img.save(f"{tmpdir}/{i}.jpg", quality=88)
    prev_sel = set(filter(None, (sel_csv or "").splitlines()))
    new_st = {"dir": tmpdir, "files": files_map, "names": list(files_map), "caps": caps,
              "sel": [n for n in files_map if n in prev_sel]}
    return grid_html(new_st), new_st, f"{len(new_st['names'])} image(s) loaded."


def apply(st: dict | None, sel_csv: str, out_dir: str, custom_date=None, custom_addr: str = ""):
    selected = [n for n in (st["names"] if st else []) if n in set((sel_csv or "").splitlines())]
    if not selected:
        raise gr.Error("No images selected — click some previews first.")
    out = Path(out_dir).expanduser()
    out.mkdir(parents=True, exist_ok=True)
    for name in selected:
        p = Path(st["files"][name])
        img = ImageOps.exif_transpose(Image.open(p))
        stamped = stamp(img, stamp_lines(img, p, custom_date, custom_addr))
        if p.suffix.lower() in {".jpg", ".jpeg"} and stamped.mode != "RGB":
            stamped = stamped.convert("RGB")
        stamped.save(out / name, exif=img.info.get("exif") or b"")
    # per-run dir so concurrent users/sessions never clobber each other's zip
    zip_path = Path(tempfile.mkdtemp(prefix="zip_", dir=THUMB_ROOT)) / f"{out.name}.zip"
    with zipfile.ZipFile(zip_path, "w") as z:
        for name in selected:
            z.write(out / name, name)
    return (
        f"Done — {len(selected)} image(s) written to {out}",
        gr.File(value=str(zip_path), visible=True),
    )


def apply_web(st: dict | None, sel_csv: str, custom_date=None, custom_addr: str = ""):
    out = Path(tempfile.mkdtemp(prefix="out_", dir=THUMB_ROOT)) / "stamped"
    _, zip_file = apply(st, sel_csv, str(out), custom_date, custom_addr)
    n = len([n for n in (st["names"] if st else []) if n in set((sel_csv or "").splitlines())])
    return f"Done — {n} image(s) stamped. Download the zip below.", zip_file


with gr.Blocks(title="PhotoLog") as demo:
    gr.HTML(f"<style>{CSS}</style>")  # ponytail: style/js as components — survives `gradio` reload mode, which drops launch() kwargs
    gr.Markdown("# PhotoLog\nStamp the date (and your address text, if given) on each photo's top-right corner.")
    if WEB:
        upload = gr.File(label="Upload your photo folder", file_count="directory", height=120)
    else:
        with gr.Row():
            folder_tb = gr.Textbox(label="Photo folder", placeholder="/path/to/photos — or click Browse", scale=4)
            browse_btn = gr.Button("Browse…", scale=1)
            load_btn = gr.Button("Load", variant="primary", scale=1)
    status = gr.Markdown()
    grid = gr.HTML(grid_html(None))
    with gr.Row():
        custom_date = gr.DateTime(label="Custom date (optional — time always comes from the photo itself)",
                                  include_time=False, type="datetime")
        custom_addr = gr.Textbox(label="Custom address (optional — one line per row)", lines=3,
                                 placeholder="1521 Meander Rd\nTimmonsville SC 29161\nUnited States")
        refresh_btn = gr.Button("Refresh previews", size="sm")
    if WEB:
        apply_btn = gr.Button("Apply", variant="primary")
    else:
        with gr.Row():
            out_dir = gr.Textbox(label="Output folder", value=str(Path.home() / "PhotoLog-output"), scale=4)
            apply_btn = gr.Button("Apply", variant="primary", scale=1)
    result_md = gr.Markdown()
    download = gr.File(label="Download all (zip)", visible=False)
    plsel = gr.Textbox(elem_id="plsel-carrier")
    st = gr.State(None)

    if WEB:
        rescan = dict(fn=scan_upload, inputs=[upload, custom_date, custom_addr, st, plsel],
                      outputs=[grid, st, status], js=HARVEST_SCAN)
        upload.change(**rescan)
        refresh_btn.click(**rescan)
        apply_btn.click(apply_web, [st, plsel, custom_date, custom_addr], [result_md, download],
                        js=HARVEST_APPLY_WEB).then(**rescan)
    else:
        rescan = dict(fn=scan, inputs=[folder_tb, custom_date, custom_addr, st, plsel],
                      outputs=[grid, st, status], js=HARVEST_SCAN)
        browse_btn.click(pick_folder, folder_tb, folder_tb).then(**rescan)
        folder_tb.submit(**rescan)
        load_btn.click(**rescan)
        refresh_btn.click(**rescan)
        apply_btn.click(apply, [st, plsel, out_dir, custom_date, custom_addr], [result_md, download],
                        js=HARVEST_APPLY).then(**rescan)
    demo.load(None, None, None, js=JS)


def check():
    img = Image.new("RGB", (400, 300), "gray")
    before = img.copy()
    stamp(img, ["Jul 28, 2026 at 8:23:59 AM", "1521 Meander Rd", "United States"])
    assert img.tobytes() != before.tobytes(), "stamp drew nothing"
    mt = datetime.fromtimestamp(Path(__file__).stat().st_mtime)
    lines = stamp_lines(before, Path(__file__), datetime(2020, 1, 2), "A\nB")
    assert lines[1:] == ["A", "B"], lines
    assert lines[0].startswith("Jan 2, 2020 at ") and mt.strftime("%-I:%M:%S %p") in lines[0], \
        f"custom date must keep the photo's own time: {lines[0]}"
    assert stamp_lines(before, Path(__file__))[0] == mt.strftime(DATE_FMT)  # no custom → photo's date
    s = {"dir": "/d", "names": ["a", "b"], "caps": {"a": "A", "b": "B"}, "sel": ["b"],
         "files": {"a": "/x/a.jpg", "b": "/x/b.jpg"}}
    h = grid_html(s)
    assert 'class="plcell sel"' in h and "/gradio_api/file=/d/1.jpg" in h and "1/2 selected" in h
    assert scan("")[1] is None and scan("/nonexistent")[1] is None  # bad folder → friendly message
    print("ok")


if __name__ == "__main__":
    # share only ever pairs with WEB mode: a public tunnel to local mode would expose the whole disk
    check() if "check" in sys.argv else demo.launch(inbrowser=True, share=WEB)
