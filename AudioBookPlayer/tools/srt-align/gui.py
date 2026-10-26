# -*- coding: utf-8 -*-
"""中文字幕生成器 —— 图形界面（双击「生成中文字幕.bat」即可打开）。

做三件事：
  1. 扫描有声书根目录，列出每本书「能不能生成」以及为什么不能；
  2. 一键批量生成（后台线程，可中途停止）；
  3. 把过程日志和体检数据显示出来。

底层就是同目录的 srt_align.py（+ 可选的 embed_scorer.py 语义对齐），
这个界面只是把参数和批量逻辑包起来，不改变对齐算法。
"""
from __future__ import annotations

import contextlib
import io
import os
import queue
import re
import sys
import threading
import time
import traceback

import tkinter as tk
from tkinter import filedialog, messagebox, ttk

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import srt_align as S

AUDIO_EXT = (".m4b", ".m4a", ".mp3", ".mp4", ".flac", ".wav", ".ogg", ".opus")

COLOR_BG = "#121011"
COLOR_PANEL = "#1B1717"
COLOR_TEXT = "#F1EBE1"
COLOR_DIM = "#9A9188"
COLOR_ACCENT = "#9E2B25"
COLOR_GOLD = "#C9A227"


# ------------------------------------------------------------------ 环境探测

def find_model() -> str | None:
    """找本地 bge-m3 快照；没有返回 None（则退回纯长度对齐）。"""
    base = os.path.join(HERE, "_models", "models--BAAI--bge-m3", "snapshots")
    if not os.path.isdir(base):
        return None
    for name in sorted(os.listdir(base)):
        p = os.path.join(base, name)
        if os.path.isfile(os.path.join(p, "pytorch_model.bin")) or \
           os.path.isfile(os.path.join(p, "model.safetensors")):
            return p
    return None


def venv_ready() -> bool:
    return os.path.isfile(os.path.join(HERE, ".venv", "Scripts", "python.exe"))


def epub_is_chinese(path: str) -> str:
    """粗略判断电子书语言：有假名 = 日文；没有假名且有汉字 = 中文。"""
    try:
        import zipfile
        with zipfile.ZipFile(path) as z:
            names = [n for n in z.namelist() if n.lower().endswith((".xhtml", ".html", ".htm"))]
            kana = 0
            han = 0
            for n in names[:6]:
                text = z.read(n).decode("utf-8", "ignore")
                text = re.sub(r"<[^>]+>", "", text)
                kana += sum(1 for ch in text if "\u3040" <= ch <= "\u30ff")
                han += sum(1 for ch in text if "\u4e00" <= ch <= "\u9fff")
            if kana > han * 0.15:
                return "ja"
            if han > 200:
                return "zh"
            return "unknown"
    except Exception:
        return "unknown"


def probe_book(folder: str) -> dict:
    """看一个目录里有什么，判断能不能生成。"""
    audio = srt = epub = zh_srt = None
    if os.path.isfile(folder):
        ext = os.path.splitext(folder)[1].lower()
        if ext in AUDIO_EXT:
            audio = folder
        elif ext == ".srt":
            srt = folder
        elif ext == ".epub":
            epub = folder
        folder = os.path.dirname(folder)
    else:
        for fn in sorted(os.listdir(folder)):
            p = os.path.join(folder, fn)
            if not os.path.isfile(p):
                continue
            low = fn.lower()
            if low.endswith(".zh.srt"):
                zh_srt = p
            elif low.endswith(".srt") and srt is None:
                srt = p
            elif low.endswith(".epub") and epub is None:
                epub = p
            elif low.endswith(AUDIO_EXT) and audio is None:
                audio = p

    lang = epub_is_chinese(epub) if epub else ""
    if not audio and not srt:
        state, why = "skip", "缺音频"
    elif not srt:
        state, why = "skip", "缺日文字幕"
    elif not epub:
        state, why = "skip", "缺中文电子书"
    elif lang == "ja":
        state, why = "skip", "电子书是日文版"
    elif lang == "unknown":
        state, why = "warn", "语言不确定"
    else:
        state, why = "ok", "可以生成"

    return {
        "folder": folder,
        "name": os.path.basename(os.path.normpath(folder)),
        "audio": audio,
        "srt": srt,
        "epub": epub,
        "zh_srt": zh_srt,
        "state": state,
        "why": why,
    }


def scan_books(root: str) -> list[dict]:
    """递归找所有「含 srt 或音频」的目录，返回探测结果。"""
    books = []
    seen = set()
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames[:] = [d for d in dirnames if not d.startswith(".")]
        low = [f.lower() for f in filenames]
        if not any(f.endswith(".srt") or f.endswith(AUDIO_EXT) for f in low):
            continue
        if dirpath in seen:
            continue
        seen.add(dirpath)
        info = probe_book(dirpath)
        if info["state"] != "skip" or info["srt"] or info["audio"]:
            books.append(info)
    books.sort(key=lambda b: b["name"])
    return books


# ------------------------------------------------------------------ 单个任务

def run_one(book: dict, model: str | None, overwrite: bool, log) -> bool:
    """调 srt_align 生成一本书。返回是否成功。"""
    out = os.path.splitext(book["srt"])[0] + ".zh.srt"
    if os.path.exists(out) and not overwrite:
        log(f"  跳过：已存在 {os.path.basename(out)}")
        return True

    argv = [book["folder"]]
    if book["srt"]:
        argv += ["--srt", book["srt"]]
    if book["epub"]:
        argv += ["--book", book["epub"]]
    argv += ["--out", out]
    if model:
        argv += ["--embed", model]

    buf = io.StringIO()
    try:
        with contextlib.redirect_stdout(buf):
            code = S.main(argv)
    except Exception:
        log("  失败：")
        for line in traceback.format_exc().splitlines()[-6:]:
            log("    " + line)
        return False

    text = buf.getvalue()
    # 只把关键行显示出来，避免刷屏
    for line in text.splitlines():
        if any(k in line for k in ("配对方式", "字幕 ", "时间轴", "过密", "没有生成",
                                   "找不到", "语义对齐", "->")):
            log("  " + line.strip())
    return code == 0


# ------------------------------------------------------------------ 界面

class App(tk.Tk):
    def __init__(self, root_dir: str | None = None, auto_start: bool = False):
        super().__init__()
        self.title("KATARU · 中文字幕生成器")
        self.geometry("1020x700")
        self.minsize(900, 600)
        self.configure(bg=COLOR_BG)

        self.model = find_model()
        self.books: list[dict] = []
        self.queue: queue.Queue = queue.Queue()
        self.worker: threading.Thread | None = None
        self.stop_flag = False

        self._build_style()
        self._build_ui()

        if root_dir:
            self.path_var.set(root_dir)
            self.refresh()

        self.after(120, self._drain)
        if auto_start and self.books:
            self.start(all_ok=True)

    # ---------------- 样式 ----------------

    def _build_style(self):
        style = ttk.Style(self)
        style.theme_use("clam")
        style.configure(".", background=COLOR_BG, foreground=COLOR_TEXT, fieldbackground=COLOR_PANEL)
        style.configure("TFrame", background=COLOR_BG)
        style.configure("Panel.TFrame", background=COLOR_PANEL)
        style.configure("TLabel", background=COLOR_BG, foreground=COLOR_TEXT)
        style.configure("Dim.TLabel", background=COLOR_BG, foreground=COLOR_DIM)
        style.configure("Title.TLabel", background=COLOR_BG, foreground=COLOR_TEXT,
                        font=("Microsoft YaHei UI", 15, "bold"))
        style.configure("TButton", background=COLOR_PANEL, foreground=COLOR_TEXT,
                        borderwidth=0, focuscolor=COLOR_BG, padding=(12, 6))
        style.map("TButton",
                  background=[("active", "#2A2424"), ("disabled", "#1A1616")],
                  foreground=[("disabled", COLOR_DIM)])
        style.configure("Accent.TButton", background=COLOR_ACCENT, foreground="#FFFFFF")
        style.map("Accent.TButton", background=[("active", "#B93530"), ("disabled", "#4A1F1D")])
        style.configure("Treeview", background=COLOR_PANEL, fieldbackground=COLOR_PANEL,
                        foreground=COLOR_TEXT, rowheight=26, borderwidth=0)
        style.configure("Treeview.Heading", background="#241D1D", foreground=COLOR_DIM,
                        borderwidth=0, font=("Microsoft YaHei UI", 9))
        style.map("Treeview", background=[("selected", "#3A2A28")])
        style.configure("TProgressbar", background=COLOR_ACCENT, troughcolor=COLOR_PANEL,
                        borderwidth=0, thickness=14)
        style.configure("TCheckbutton", background=COLOR_BG, foreground=COLOR_TEXT)
        style.map("TCheckbutton", background=[("active", COLOR_BG)])

    # ---------------- 布局 ----------------

    def _build_ui(self):
        top = ttk.Frame(self, padding=(16, 14, 16, 8))
        top.pack(fill="x")
        ttk.Label(top, text="中文字幕生成器", style="Title.TLabel").pack(side="left")
        ttk.Label(top, text="  日文 SRT 的时间轴 + 中文电子书的文本",
                  style="Dim.TLabel").pack(side="left", pady=(6, 0))

        bar = ttk.Frame(self, padding=(16, 0, 16, 10))
        bar.pack(fill="x")
        self.path_var = tk.StringVar()
        entry = ttk.Entry(bar, textvariable=self.path_var)
        entry.pack(side="left", fill="x", expand=True, ipady=3)
        ttk.Button(bar, text="选择文件夹", command=self.browse).pack(side="left", padx=(8, 0))
        ttk.Button(bar, text="重新扫描", command=self.refresh).pack(side="left", padx=(8, 0))

        opts = ttk.Frame(self, padding=(16, 0, 16, 8))
        opts.pack(fill="x")
        self.use_embed = tk.BooleanVar(value=bool(self.model))
        cb = ttk.Checkbutton(opts, text="语义对齐（bge-m3，精度更高）", variable=self.use_embed)
        cb.pack(side="left")
        if not self.model:
            cb.state(["disabled"])
            ttk.Label(opts, text="  （未找到模型，退回纯长度对齐）", style="Dim.TLabel").pack(side="left")
        self.overwrite = tk.BooleanVar(value=False)
        ttk.Checkbutton(opts, text="覆盖已有 .zh.srt", variable=self.overwrite).pack(side="left", padx=(18, 0))

        self.btn_all = ttk.Button(opts, text="生成全部可行", style="Accent.TButton",
                                  command=lambda: self.start(all_ok=True))
        self.btn_sel = ttk.Button(opts, text="生成选中", command=lambda: self.start(all_ok=False))
        self.btn_stop = ttk.Button(opts, text="停止", command=self.stop, state="disabled")
        self.btn_all.pack(side="right")
        self.btn_sel.pack(side="right", padx=(0, 8))
        self.btn_stop.pack(side="right", padx=(0, 8))

        # 书单
        mid = ttk.Frame(self, padding=(16, 0, 16, 0))
        mid.pack(fill="both", expand=True)
        cols = ("name", "state", "audio", "srt", "epub", "zh")
        self.tree = ttk.Treeview(mid, columns=cols, show="headings", selectmode="extended")
        for col, title, width in (
            ("name", "书名", 290), ("state", "状态", 140), ("audio", "音频", 55),
            ("srt", "日文字幕", 75), ("epub", "中文电子书", 95), ("zh", "已有中文", 75),
        ):
            self.tree.heading(col, text=title)
            self.tree.column(col, width=width, anchor="w" if col == "name" else "center")
        vsb = ttk.Scrollbar(mid, orient="vertical", command=self.tree.yview)
        self.tree.configure(yscrollcommand=vsb.set)
        self.tree.pack(side="left", fill="both", expand=True)
        vsb.pack(side="right", fill="y")
        self.tree.tag_configure("ok", foreground=COLOR_TEXT)
        self.tree.tag_configure("warn", foreground=COLOR_GOLD)
        self.tree.tag_configure("skip", foreground=COLOR_DIM)
        self.tree.tag_configure("done", foreground=COLOR_GOLD)

        bottom = ttk.Frame(self, padding=(16, 10, 16, 14))
        bottom.pack(fill="both")

        prow = ttk.Frame(bottom)
        prow.pack(fill="x")
        self.progress = ttk.Progressbar(prow, mode="determinate", maximum=1000)
        self.progress.pack(side="left", fill="x", expand=True)
        self.pct = ttk.Label(prow, text="0%", width=6, anchor="e", style="Dim.TLabel")
        self.pct.pack(side="left", padx=(10, 0))

        self.activity = ttk.Label(bottom, text="就绪", style="Dim.TLabel")
        self.activity.pack(fill="x", pady=(6, 6))

        self.log_box = tk.Text(bottom, height=8, bg=COLOR_PANEL, fg=COLOR_TEXT,
                               insertbackground=COLOR_TEXT, relief="flat",
                               font=("Consolas", 9), padx=10, pady=8)
        self.log_box.pack(fill="both", expand=True, pady=(8, 0))
        self.log_box.configure(state="disabled")

        self.status = ttk.Label(self, text="就绪", style="Dim.TLabel", padding=(16, 0, 16, 8))
        self.status.pack(fill="x")

    # ---------------- 交互 ----------------

    def log(self, line: str):
        self.queue.put(("log", line))

    def _drain(self):
        try:
            while True:
                kind, payload = self.queue.get_nowait()
                if kind == "log":
                    self.log_box.configure(state="normal")
                    self.log_box.insert("end", payload + "\n")
                    self.log_box.see("end")
                    self.log_box.configure(state="disabled")
                elif kind == "sub":
                    text, frac = payload
                    self.activity.configure(text=text)
                    if frac is not None:
                        self._set_frac(frac)
                elif kind == "progress":
                    done, total = payload
                    self.total_books = max(1, total)
                    self.current_index = done
                    self._set_frac(0.0)
                    self.status.configure(text=f"进行中 {done}/{total}")
                elif kind == "state":
                    book, state = payload
                    self._mark(book, state)
                elif kind == "done":
                    self.status.configure(text=payload)
                    self.activity.configure(text=payload)
                    self._set_frac(1.0)
                    self.btn_all.configure(state="normal")
                    self.btn_sel.configure(state="normal")
                    self.btn_stop.configure(state="disabled")
        except queue.Empty:
            pass
        self.after(120, self._drain)

    def _set_frac(self, sub: float) -> None:
        """把「第几本 + 这本完成到几成」换算成总进度。"""
        total = max(1, getattr(self, "total_books", 1))
        done = getattr(self, "current_index", 0)
        overall = (done + max(0.0, min(1.0, sub))) / total
        self.progress["value"] = overall * 1000
        self.pct.configure(text=f"{overall * 100:3.0f}%")

    def _on_progress(self, text: str, frac) -> None:
        """srt_align 里的进度回调（在工作线程里被调用）。"""
        self.queue.put(("sub", (text, frac)))
    def browse(self):
        d = filedialog.askdirectory(title="选择有声书根目录")
        if d:
            self.path_var.set(d)
            self.refresh()

    def refresh(self):
        root = self.path_var.get().strip()
        if not root or not os.path.isdir(root):
            messagebox.showwarning("提示", "请先选择一个存在的文件夹")
            return
        self.log(f"扫描 {root} ...")
        self.books = scan_books(root)
        for item in self.tree.get_children():
            self.tree.delete(item)
        for b in self.books:
            self.tree.insert("", "end", iid=b["folder"], values=(
                b["name"], b["why"],
                "✓" if b["audio"] else "—",
                "✓" if b["srt"] else "—",
                "✓" if b["epub"] else "—",
                "✓" if b["zh_srt"] else "—",
            ), tags=(b["state"],))
        ok = sum(1 for b in self.books if b["state"] == "ok")
        self.status.configure(text=f"共 {len(self.books)} 个目录，其中 {ok} 本可以直接生成")
        self.log(f"共 {len(self.books)} 个目录，{ok} 本可直接生成")

    def _mark(self, book, state):
        iid = book["folder"]
        if self.tree.exists(iid):
            self.tree.set(iid, "state", state)
            self.tree.item(iid, tags=("done",))

    def start(self, all_ok: bool):
        if self.worker and self.worker.is_alive():
            return
        if all_ok:
            targets = [b for b in self.books if b["state"] == "ok"]
        else:
            selected = set(self.tree.selection())
            targets = [b for b in self.books if b["folder"] in selected and b["state"] != "skip"]
        if not targets:
            messagebox.showinfo("提示", "没有可生成的书目" + ("（选中行都不满足条件）" if not all_ok else ""))
            return

        self.stop_flag = False
        self.total_books = max(1, len(targets))
        self.current_index = 0
        self._set_frac(0.0)
        self.activity.configure(text="准备中…")
        self.btn_all.configure(state="disabled")
        self.btn_sel.configure(state="disabled")
        self.btn_stop.configure(state="normal")
        model = self.model if self.use_embed.get() else None
        if model:
            self.log("语义对齐：已启用 bge-m3")
        else:
            self.log("语义对齐：未启用（纯长度对齐）")

        def work():
            total = len(targets)
            done = 0
            S.PROGRESS = self._on_progress
            self.queue.put(("progress", (0, total)))
            for b in targets:
                if self.stop_flag:
                    self.log("已停止")
                    break
                self.log(f"=== {b['name']} ===")
                self.activity.configure(text=f"{done + 1}/{total} {b['name'][:26]}")
                started = time.time()
                ok = run_one(b, model, self.overwrite.get(), self.log)
                done += 1
                self.log(f"  用时 {time.time() - started:.0f} 秒")
                self.queue.put(("progress", (done, total)))
                self.queue.put(("state", (b, "✓ 完成" if ok else "✗ 失败")))
            S.PROGRESS = None
            self.queue.put(("done", f"完成 {done}/{total}"))

        self.worker = threading.Thread(target=work, daemon=True)
        self.worker.start()

    def stop(self):
        self.stop_flag = True
        self.btn_stop.configure(state="disabled")


def main(argv=None):
    argv = list(sys.argv[1:] if argv is None else argv)
    root_dir = next((a for a in argv if not a.startswith("-")), None)
    auto = "--run" in argv

    if "--check" in argv:
        # 自检：不建窗口，只跑扫描逻辑
        print(f"venv  : {venv_ready()}")
        print(f"model : {find_model()}")
        if root_dir:
            books = scan_books(root_dir)
            ok = [b for b in books if b["state"] == "ok"]
            print(f"scan  : {len(books)} dirs, {len(ok)} ready")
            for b in books[:8]:
                print(f"  [{b['state']:4s}] {b['name'][:40]:40s} {b['why']}")
        return 0

    app = App(root_dir, auto_start=auto)
    app.mainloop()
    return 0


if __name__ == "__main__":
    sys.exit(main())
