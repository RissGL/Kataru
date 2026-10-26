# -*- coding: utf-8 -*-
"""基准测试：拿人工译文字幕当标准答案，量流水线的对齐精度。

做法：
  1. 人工 .zh.srt 按「第N話」标记拆成章节，包成一个临时 epub
     （模拟"你手上有中文电子书"这个前提）；
  2. 用日文 SRT 的真实时间轴 + 有声书 m4b 的真实章节跑完整流水线；
  3. 逐条比对时间点上我们的文本与人工译文。

用法：python bench.py "<书目录>" [--limit 分钟]
"""
import argparse
import difflib
import os
import re
import sys
import tempfile
import zipfile

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import srt_align as S


def norm(text: str) -> str:
    return re.sub(r"[\s　、。,.．，！!？?…「」『』（）()\[\]【】〈〉《》・:：;；—―\-~〜\"'“”‘’]", "", text or "")


def find_triplet(folder: str):
    ja = zh = audio = None
    for root, _, files in os.walk(folder):
        for fn in files:
            p = os.path.join(root, fn)
            low = fn.lower()
            if low.endswith(".zh.srt") and zh is None:
                zh = p
            elif low.endswith(".srt") and ja is None:
                ja = p
            elif low.endswith(".m4b") and audio is None:
                audio = p
    return ja, zh, audio


def build_epub(chapters, out_path: str):
    """chapters: [(标题, [行...]), ...]"""
    with zipfile.ZipFile(out_path, "w") as z:
        z.writestr("mimetype", "application/epub+zip")
        z.writestr("META-INF/container.xml",
                   '<?xml version="1.0"?><container version="1.0" '
                   'xmlns="urn:oasis:names:tc:opendocument:xmlns:container">'
                   '<rootfiles><rootfile full-path="OEBPS/content.opf" '
                   'media-type="application/oebps-package+xml"/></rootfiles></container>')
        items, spine = [], []
        for i, (title, lines) in enumerate(chapters):
            name = f"Text/ch{i:04d}.xhtml"
            body = "".join(f"<p>{line}</p>" for line in lines if line.strip())
            z.writestr(f"OEBPS/{name}",
                       f'<?xml version="1.0" encoding="utf-8"?><html><body>'
                       f"<h2>{title}</h2>{body}</body></html>")
            items.append(f'<item id="c{i}" href="{name}" media-type="application/xhtml+xml"/>')
            spine.append(f'<itemref idref="c{i}"/>')
        z.writestr("OEBPS/content.opf",
                   '<?xml version="1.0" encoding="utf-8"?><package xmlns="http://www.idpf.org/2007/opf" '
                   'version="2.0"><manifest>' + "".join(items) + "</manifest><spine>"
                   + "".join(spine) + "</spine></package>")
    return len(chapters)


def chapters_from_markers(zh_cues):
    """按译文里的「第N話」标记造章节（模拟"电子书自带章节目录"）。"""
    chapters, cur = [], None
    for c in zh_cues:
        m = re.search(r"第\s*(\d+)\s*[話话章]", c["text"])
        if m:
            cur = [f"第{int(m.group(1))}話", []]
            chapters.append(cur)
            cur[1].append(c["text"])
        elif cur is not None:
            cur[1].append(c["text"])
    if not chapters:
        chapters = [["第1話", [c["text"] for c in zh_cues]]]
    return [(t, lines) for t, lines in chapters]


def chapters_from_audio(audio, folder, end_time, zh_cues):
    """按音频锚点造章节：每条锚点时间窗内的译文单独成章。

    这样两侧章节边界一致，测到的是**段句对齐本身**的精度，
    不受"电子书章节名和音频章节名对不对得上"这一层影响。
    """
    anchors = S.build_anchors(audio, folder, end_time)
    chapters = []
    spans = []
    for idx, a in enumerate(anchors):
        if a["key"][0] == "unknown":
            continue                      # 没有章节名的锚点：真实流程里也会被跳过
        cues = [c for c in zh_cues if a["start"] <= c["start"] < a["end"]]
        if not cues:
            continue
        spans.append((a["start"], a["end"]))
        kind, num = a["key"]
        if kind == "chapter":
            title = f"第{num}章"
        elif kind == "prologue":
            title = "序章"
        elif kind == "epilogue":
            title = "终章"
        elif kind == "afterword":
            title = "后记"
        else:
            title = f"第{900 + idx}章"
        chapters.append((title, [c["text"] for c in cues]))
    return chapters, spans


def compare(ours, truth):
    """对每条人工字幕，取覆盖它的生成文本，算相似度。"""
    res = []
    for h in truth:
        got = "".join(o["text"] for o in ours
                      if o["start"] < h["end"] - 0.01 and o["end"] > h["start"] + 0.01)
        a, b = norm(got), norm(h["text"])
        if not b:
            continue
        if not a:
            res.append((0.0, h, got))
            continue
        if a == b:
            res.append((1.0, h, got))
            continue
        res.append((difflib.SequenceMatcher(None, a, b).ratio(), h, got))
    return res


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("folder")
    ap.add_argument("--limit", type=float, default=0, help="只测前 N 分钟（调试用）")
    ap.add_argument("--chapters", default="from-audio", choices=["from-audio", "from-markers"])
    ap.add_argument("--embed", default=None, help="启用句向量语义对齐")
    ap.add_argument("--sim-weight", type=float, default=2.0)
    args = ap.parse_args()

    ja, zh, audio = find_triplet(args.folder)
    if not (ja and zh):
        print("缺 ja.srt 或 zh.srt")
        return 2
    print(f"日文 SRT: {os.path.basename(ja)}")
    print(f"人工译文: {os.path.basename(zh)}")
    print(f"音频    : {os.path.basename(audio) if audio else '(无)'}")

    zh_cues = S.parse_srt(zh)
    if args.limit:
        cut = args.limit * 60
        zh_cues = [c for c in zh_cues if c["start"] <= cut]

    tmpdir = tempfile.mkdtemp(prefix="bench-")
    fake_epub = os.path.join(tmpdir, "zh.epub")
    end_time = zh_cues[-1]["end"] if zh_cues else 0
    if args.chapters == "from-audio":
        chapters, spans = chapters_from_audio(audio, args.folder, end_time, zh_cues)
        if spans:
            zh_cues = [c for c in zh_cues if any(s <= c["start"] < e for s, e in spans)]
            print(f"评测范围: 只算有章节名的 {sum(e-s for s,e in spans)/60:.1f} 分钟 / {len(zh_cues)} 条")
    else:
        chapters = chapters_from_markers(zh_cues)
    n_ch = build_epub(chapters, fake_epub)
    print(f"构造临时 epub: {n_ch} 章 / {len(zh_cues)} 条译文（{args.chapters}）")

    out = os.path.join(tmpdir, "out.srt")
    argv = [audio or args.folder, "--srt", ja, "--book", fake_epub, "--out", out]
    if args.embed:
        argv += ["--embed", args.embed, "--sim-weight", str(args.sim_weight)]
    if args.limit:
        # 用限长后的 srt 才能和 zh_cues 对齐
        ja_cues = [c for c in S.parse_srt(ja) if c["start"] <= args.limit * 60]
        short = os.path.join(tmpdir, "ja.srt")
        with open(short, "w", encoding="utf-8") as f:
            for i, c in enumerate(ja_cues, 1):
                f.write(f"{i}\n{S.fmt_time(c['start'])} --> {S.fmt_time(c['end'])}\n{c['text']}\n\n")
        argv[2] = short
    S.main(argv)

    ours = S.parse_srt(out)
    res = compare(ours, zh_cues)
    total = len(res)
    exact = sum(1 for r, _, _ in res if r >= 0.999)
    ge90 = sum(1 for r, _, _ in res if r >= 0.9)
    ge70 = sum(1 for r, _, _ in res if r >= 0.7)
    mean = sum(r for r, _, _ in res) / max(1, total)
    empty = sum(1 for r, _, g in res if not norm(g))

    print()
    print("=" * 62)
    print(f"比对条目: {total}")
    print(f"  完全相同        : {exact:5d}  {exact/total*100:5.1f}%")
    print(f"  相似度 ≥ 0.90   : {ge90:5d}  {ge90/total*100:5.1f}%")
    print(f"  相似度 ≥ 0.70   : {ge70:5d}  {ge70/total*100:5.1f}%")
    print(f"  平均相似度      : {mean:.3f}")
    print(f"  没有分配到文本  : {empty:5d}  {empty/total*100:5.1f}%")
    print()
    print("最差的 8 条：")
    for r, h, g in sorted(res, key=lambda x: x[0])[:8]:
        t = h["start"]
        print(f"  {int(t//60):3d}:{int(t%60):02d}  sim={r:.2f}")
        print(f"        人工: {h['text'][:44]}")
        print(f"        生成: {g[:44] if g else '(空)'}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
