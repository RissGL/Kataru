# -*- coding: utf-8 -*-
"""通用流水线：把「已有的 SRT（任意语言，只用它的时间轴）+ 译文电子书」合成译文字幕。

设计原则——不特化任何一本书：
  * 不认识书名、作者、出版社，也不认 "朗読/発行/Audible" 这类词；
  * 章节锚点优先取音频文件自带的章节表（m4b 的 chpl / m3u8 播放列表），
    没有就自动把整条时间轴按等价时长切成若干块；
  * 章节归属靠"标题模式（序章/第N章/终章/后记）→ 序号 → 顺序+时长比例"三级匹配；
  * 段与句的对齐只依赖三个通用量：源语言字符数、目标语言字符数、时长；
  * 片头广告、念章节标题、片尾字幕这类"书里没有的内容"，由对齐器自己用
    **自由的首尾偏移** 吸收——也就是允许开头/结尾若干段不分配文本，代价很小。

用法:
    python srt_align.py <有声书目录或单个音频> [--srt x.srt] [--book y.epub]
                        [--lang zh] [--out z.srt] [--report r.txt]

输出的 srt 时间轴来自输入的 srt，文本来自电子书；不调用任何翻译接口。
"""
from __future__ import annotations

import argparse
import difflib
import os
import re
import struct
import sys
import unicodedata
import zipfile

# ------------------------------------------------------------------ 基础工具

CN_DIGITS = {"零": 0, "〇": 0, "一": 1, "二": 2, "两": 2, "三": 3, "四": 4,
             "五": 5, "六": 6, "七": 7, "八": 8, "九": 9}


def cn_number(s: str):
    s = s.strip()
    if not s:
        return None
    if s.isdigit():
        return int(s)
    if s in CN_DIGITS:
        return CN_DIGITS[s]
    if "十" in s:
        left, _, right = s.partition("十")
        tens = CN_DIGITS.get(left, 1) if left else 1
        ones = CN_DIGITS.get(right, 0) if right else 0
        return tens * 10 + ones
    return None


def clean(s: str) -> str:
    s = (s or "").replace("\u3000", " ")
    s = re.sub(r"[\r\n\t]+", " ", s)
    s = re.sub(r" {2,}", " ", s)
    return s.strip()


def src_len(text: str) -> int:
    """源语言有效字符数（日文/中文都适用）：去掉空白与标点。"""
    return sum(1 for ch in text if not ch.isspace() and ch not in
               "、。,.．，！!？?…「」『』（）()[]【】〈〉《》・:：;；—―-~〜\"'“”‘’")


def norm_key(text: str) -> str:
    return re.sub(r"[\s　、。,.．，！!？?…「」『』（）()\[\]【】—―\-~〜:：;；\"'・0-9０-９]", "",
                  text or "")


def has_kana(text: str) -> bool:
    return any("\u3040" <= ch <= "\u30ff" for ch in text)


# ------------------------------------------------------------------ SRT

SRT_TIME = re.compile(r"(\d+):(\d+):(\d+)[,.](\d+)\s*-->\s*(\d+):(\d+):(\d+)[,.](\d+)")


def parse_srt(path: str):
    with open(path, "r", encoding="utf-8-sig", errors="ignore") as f:
        raw = f.read()
    out = []
    for block in re.split(r"\r?\n\r?\n", raw.strip()):
        lines = block.split("\n")
        if len(lines) < 3:
            continue
        m = SRT_TIME.search(lines[1])
        if not m:
            continue
        g = [int(x) for x in m.groups()]
        start = g[0] * 3600 + g[1] * 60 + g[2] + g[3] / 1000
        end = g[4] * 3600 + g[5] * 60 + g[6] + g[7] / 1000
        text = " ".join(x.strip() for x in lines[2:] if x.strip())
        if end < start:
            end = start
        out.append({"start": start, "end": end, "text": text})
    return out


def fmt_time(sec: float) -> str:
    sec = max(0.0, sec)
    h = int(sec // 3600)
    m = int(sec % 3600 // 60)
    s = int(sec % 60)
    ms = min(999, int(round((sec - int(sec)) * 1000)))
    return f"{h:02d}:{m:02d}:{s:02d},{ms:03d}"


# ------------------------------------------------------------------ 音频章节锚点

def m4b_chapters(path: str):
    """读 m4b/mp4 的 chpl 章节表（章节名 + 起始秒）。没有就返回 []。"""
    def walk(f, start, end, out):
        pos = start
        while pos + 8 <= end:
            f.seek(pos)
            hdr = f.read(8)
            if len(hdr) < 8:
                return
            size = struct.unpack(">I", hdr[:4])[0]
            typ = hdr[4:8].decode("latin-1", "ignore")
            if size == 0:
                size = end - pos
            if size < 8:
                return
            out.append((typ, pos, size))
            if typ in ("moov", "trak", "udta", "mdia", "minf", "stbl", "edts"):
                walk(f, pos + 8, min(pos + size, end), out)
            pos += size

    try:
        with open(path, "rb") as f:
            f.seek(0, 2)
            total = f.tell()
            boxes = []
            walk(f, 0, total, boxes)
            hit = [b for b in boxes if b[0] == "chpl"]
            if not hit:
                return []
            _, pos, size = hit[0]
            f.seek(pos + 8)
            payload = f.read(max(0, size - 8))
    except OSError:
        return []

    if len(payload) < 5:
        return []
    ver = payload[0]
    i = 4 + (4 if ver == 1 else 0)
    count = payload[i]
    i += 1
    out = []
    for _ in range(count):
        if i + 9 > len(payload):
            break
        ts = struct.unpack(">Q", payload[i:i + 8])[0]
        i += 8
        ln = payload[i]
        i += 1
        title = payload[i:i + ln].decode("utf-8", "ignore")
        i += ln
        out.append({"start": ts / 1e7, "title": title})
    return out


M3U8_EXTINF = re.compile(r"#EXTINF:\s*([0-9.]+)\s*,(.*)")


def m3u8_chapters(path: str):
    out = []
    acc = 0.0
    try:
        with open(path, "r", encoding="utf-8-sig", errors="ignore") as f:
            for line in f:
                m = M3U8_EXTINF.match(line.strip())
                if not m:
                    continue
                dur = float(m.group(1) or 0)
                title = clean(m.group(2))
                out.append({"start": acc, "title": title, "dur": dur})
                acc += dur
    except OSError:
        return []
    return out


CHAPTER_PATTERNS = [
    (("prologue", 0), r"序章|序曲|序言|楔子|プロローグ|prologue"),
    (("epilogue", 0), r"終章|终章|尾声|尾聲|エピローグ|epilogue"),
    (("afterword", 0), r"後記|后记|あとがき|謝辞|谢辞|奥付|author'?s note"),
]
CHAPTER_NUM = re.compile(r"第\s*([零〇一二三四五六七八九十两\d]+)\s*[章話话节節篇卷]")


def chapter_key(title: str):
    t = unicodedata.normalize("NFKC", title or "")
    for key, pat in CHAPTER_PATTERNS:
        if re.search(pat, t, re.I):
            return key
    m = CHAPTER_NUM.search(t)
    if m:
        n = cn_number(m.group(1))
        if n is not None:
            return ("chapter", n)
    return ("unknown", 0)


# ------------------------------------------------------------------ 译文电子书

BLOCK_SPLIT = re.compile(r"</(?:p|div|h[1-6]|li|blockquote|section)\s*>|<br\s*/?>", re.I)
HEADING_TAGS = re.compile(r"<h[1-6][^>]*>(.*?)</h[1-6]>", re.I | re.S)


def epub_spine(epub: zipfile.ZipFile):
    """按 OPF spine 顺序返回 xhtml/html 条目名；解析失败就按文件名排序。"""
    try:
        container = epub.read("META-INF/container.xml").decode("utf-8", "ignore")
        opf_path = re.search(r'full-path="([^"]+)"', container).group(1)
        opf = epub.read(opf_path).decode("utf-8", "ignore")
        base = os.path.dirname(opf_path)
        ids = dict(re.findall(r'<item\b[^>]*\bid="([^"]+)"[^>]*\bhref="([^"]+)"', opf))
        ids.update({k: v for v, k in
                    re.findall(r'<item\b[^>]*\bhref="([^"]+)"[^>]*\bid="([^"]+)"', opf)})
        order = re.findall(r'<itemref\b[^>]*\bidref="([^"]+)"', opf)
        files = []
        for ref in order:
            href = ids.get(ref)
            if not href:
                continue
            name = os.path.normpath(os.path.join(base, href)).replace("\\", "/")
            if name in epub.namelist():
                files.append(name)
        if files:
            return files
    except (KeyError, AttributeError, zipfile.BadZipFile):
        pass
    return sorted(n for n in epub.namelist() if n.lower().endswith((".xhtml", ".html", ".htm")))


def split_sentences(text: str):
    out, buf = [], []
    for ch in text:
        buf.append(ch)
        if ch in "。！？!?…":
            out.append("".join(buf))
            buf = []
    if buf:
        out.append("".join(buf))
    res = []
    for s in out:
        s = s.strip()
        if not s:
            continue
        if re.fullmatch(r"[\d\s\W_]+", s):
            # 纯符号/数字：若是收尾引号，并回上一句
            if res and s in "」』”’":
                res[-1] += s
            continue
        res.append(s)
    return res


def read_book(path: str, want_lang: str | None = None):
    """返回章节列表：{key, title, sentences(目标语言), src_sentences(若有双语), chars}"""
    chapters = []
    with zipfile.ZipFile(path) as z:
        for name in epub_spine(z):
            try:
                raw = z.read(name).decode("utf-8", "ignore")
            except (KeyError, UnicodeDecodeError):
                continue
            raw = re.sub(r"<(script|style)[^>]*>.*?</\1>", "", raw, flags=re.S | re.I)
            raw = re.sub(r"<rt>.*?</rt>", "", raw, flags=re.S | re.I)
            raw = re.sub(r"<rp>.*?</rp>", "", raw, flags=re.S | re.I)
            paras = []
            for chunk in BLOCK_SPLIT.split(raw):
                text = re.sub(r"<[^>]+>", "", chunk)
                text = (text.replace("&nbsp;", " ").replace("&amp;", "&")
                            .replace("&lt;", "<").replace("&gt;", ">")
                            .replace("&quot;", '"').replace("&#8226;", "・"))
                text = clean(text)
                if len(text) >= 2:
                    paras.append(text)
            if not paras:
                continue

            heading = None
            m = HEADING_TAGS.search(raw)
            if m:
                heading = clean(re.sub(r"<[^>]+>", "", m.group(1)))

            # 标题：优先 <h1>；否则在章首一小段文字里按"章节名模式"找，
            # 这样不管排版有没有用标题标签都能认出来（通用做法）
            title = heading or ""
            matched_span = None
            if not title:
                head_text = " ".join(paras[:2])[:120]
                # 标题后面不能是引号或句末标点（否则会把正文台词一起吞掉）
                pat = (r"(第\s*[零〇一二三四五六七八九十两\d]+\s*[章話话节節篇卷][^。！？!?「『\n]{0,24}"
                       r"|(?:序章|序曲|終章|終幕|プロローグ|エピローグ|後記|后记|あとがき|楔子|尾声)"
                       r"[^。！？!?「『\n]{0,16})")
                mt = re.search(pat, head_text)
                if mt:
                    title = clean(mt.group(1))
                    matched_span = mt.group(0)
                elif len(norm_key(paras[0])) <= 30:
                    title = paras[0]

            # 把标题从正文里摘掉（可能重复出现两遍，循环剥）
            body = []
            for idx, p in enumerate(paras):
                if not title:
                    body.append(p)
                    continue
                if idx == 0 and matched_span and matched_span in p:
                    rest = p
                    for _ in range(3):
                        if matched_span not in rest:
                            break
                        rest = rest.replace(matched_span, " ", 1)
                    rest = clean(rest)
                    if rest:
                        body.append(rest)
                    continue
                if norm_key(p) == norm_key(title):
                    continue
                body.append(p)
            if not body:
                body = paras[1:]

            # 双语电子书：日文段和中文段交替 → 按 kana 判断，配成对
            if want_lang:
                zh_paras, ja_paras = [], []
                for p in body:
                    (ja_paras if has_kana(p) else zh_paras).append(p)
                mixed = len(zh_paras) > 0 and len(ja_paras) > 0
            else:
                zh_paras, ja_paras, mixed = body, [], False

            sentences = []
            for p in zh_paras:
                sentences.extend(split_sentences(p))
            if not sentences:
                continue
            chapters.append({
                "key": chapter_key(title),
                "title": title,
                "sentences": sentences,
                "src_sentences": [s for p in ja_paras for s in split_sentences(p)] if mixed else [],
                "chars": sum(len(s) for s in sentences),
                "file": name,
            })

    # 合并 key 相同（同章被拆成多个文件）与没有标题的续页
    merged = []
    for c in chapters:
        if merged and c["key"] == merged[-1]["key"] and c["key"][0] in ("chapter", "prologue",
                                                                        "epilogue", "afterword"):
            merged[-1]["sentences"].extend(c["sentences"])
            merged[-1]["src_sentences"].extend(c["src_sentences"])
            merged[-1]["chars"] += c["chars"]
        else:
            merged.append(c)
    return merged


# ------------------------------------------------------------------ 锚点归一化

def build_anchors(audio_path: str, media_dir: str, end_time: float):
    """返回 [{start, end, title, key, dur}]，覆盖整条时间轴；没有锚点就均分。"""
    raw = []
    if audio_path and audio_path.lower().endswith((".m4b", ".mp4", ".m4a")):
        raw = m4b_chapters(audio_path)
    if not raw and media_dir:
        for root, _, files in os.walk(media_dir):
            for fn in files:
                if fn.lower().endswith(".m3u8"):
                    cand = m3u8_chapters(os.path.join(root, fn))
                    if cand:
                        raw = cand
                        break
            if raw:
                break

    if not raw:
        # 没有任何锚点：把时间轴切成 N 块（每块约 80 句左右的量），仅用于限制 DP 规模
        block = max(300.0, end_time / max(1, int(end_time / 420) or 1))
        n = max(1, int(round(end_time / block)))
        raw = [{"start": i * end_time / n, "title": f"block {i + 1}"} for i in range(n)]

    anchors = []
    for i, item in enumerate(raw):
        start = max(0.0, item["start"])
        nxt = raw[i + 1]["start"] if i + 1 < len(raw) else max(end_time, start)
        anchors.append({
            "start": start,
            "end": max(start, nxt),
            "title": item.get("title", ""),
            "key": chapter_key(item.get("title", "")),
        })
    if anchors:
        anchors[-1]["end"] = max(anchors[-1]["end"], end_time)
    return anchors


def map_chapters(anchors, chapters):
    """音频锚点 → 译文章节。

    规则（不认任何具体书名）：
      1. 能按章节名模式对上的，直接配对；
      2. 两端（片头/片尾）的锚点**保持不配对** —— 那些是出版信息、广告、演职员表，
         书里本来就没有，交给对齐器用首尾偏移吸收；
      3. 只有中间的空档才按"顺序 + 时长/字数比例"插值补齐；
      4. 一个都没对上（音频章节名和书完全不像）时才退化为整体按顺序配对。
    """
    by_key = {}
    for c in chapters:
        by_key.setdefault(c["key"], c)

    keyed = []          # (anchor_index, chapter, chapter_order_index)
    chapter_order = {id(c): i for i, c in enumerate(chapters)}
    for i, a in enumerate(anchors):
        if a["key"][0] == "unknown":
            continue
        c = by_key.get(a["key"])
        if c is not None:
            keyed.append((i, c))

    if not keyed:
        # 音频完全没有章节名（例如 mp3、或章节表只有 "Chapter 001"）：
        # 这时把块硬配到章节上是猜的，不如交给按比例整体切分
        if all(a["key"][0] == "unknown" for a in anchors):
            return []
        return _pair_by_order(list(range(len(anchors))), chapters, anchors)

    # 中间空档插值：相邻两次命中之间，如果书里还有章节、音频也有锚点，就按顺序补上
    pairs = list(keyed)
    for (i1, c1), (i2, c2) in zip(keyed, keyed[1:]):
        gap_a = list(range(i1 + 1, i2))
        gap_c = chapters[chapter_order[id(c1)] + 1: chapter_order[id(c2)]]
        if gap_a and gap_c:
            # 按数量对齐（多的那边多余部分丢弃）
            for k, ai in enumerate(gap_a):
                ci = min(k, len(gap_c) - 1)
                pairs.append((ai, gap_c[ci]))
    pairs.sort()
    return pairs


def _pair_by_order(anchor_idx, chapters, anchors):
    """按累计时长比例把锚点和章节一一对应（单调）。"""
    if not anchor_idx or not chapters:
        return []
    dur = [max(0.1, anchors[i]["end"] - anchors[i]["start"]) for i in anchor_idx]
    total_d = sum(dur)
    chars = [max(1, c["chars"]) for c in chapters]
    total_c = sum(chars)

    pairs = []
    j = 0
    acc_d = 0.0
    acc_c = 0.0
    for k, i in enumerate(anchor_idx):
        acc_d += dur[k] / total_d
        # 累进到当前比例，尽量多吞章节
        while j < len(chapters) - 1 and acc_c + chars[j] / total_c <= acc_d:
            acc_c += chars[j] / total_c
            j += 1
        pairs.append((i, chapters[j]))
    return pairs


def proportional_pairs(anchors, chapters):
    """完全没有章节锚点时的退化：把整本书的句子按字数比例切成与锚点等量的份。

    不试图猜"这块音频对应哪一章"——没有章节信息时那是猜不准的；
    直接按全局字数比例分配，再在每份内部做单调对齐。
    """
    flat = [s for c in chapters for s in c["sentences"]]
    if not flat or not anchors:
        return []

    total_chars = sum(len(s) for s in flat) or 1
    dur = [max(0.1, a["end"] - a["start"]) for a in anchors]
    total_dur = sum(dur) or 1

    pairs = []
    pos = 0
    acc_c = 0.0
    total_sent = len(flat)
    for i, a in enumerate(anchors):
        acc_c += dur[i] / total_dur
        if i == len(anchors) - 1:
            take_to = total_sent
        else:
            # 按"累计字数比例"而不是"句数比例"切：句子长短差别很大
            want_chars = total_chars * acc_c
            take_to = pos
            consumed = sum(len(s) for s in flat[:pos])
            while take_to < total_sent and consumed < want_chars:
                consumed += len(flat[take_to])
                take_to += 1
        chunk = flat[pos:take_to]
        pos = take_to
        if not chunk:
            continue
        pairs.append((i, {"key": ("synthetic", i), "title": "(按比例切分)",
                          "sentences": chunk, "chars": sum(len(s) for s in chunk),
                          "file": ""}))
    return pairs


# ------------------------------------------------------------------ 对齐

def align_block(segments, sentences, ratio, cps, lead_in=4, lead_out=4, lead_sent=3):
    """单调对齐一段音频的所有 ASR 段与译文的句子。

    - 期望字数 = 0.7 × (源文字数 × ratio) + 0.3 × (时长 × cps)
    - 允许一段吞 0..6 句
    - 允许开头 lead_in 段 / 结尾 lead_out 段不分配文本（片头广告、片尾），
      也允许跳过开头 lead_sent 句（插图说明等），代价很小但不为零 —— 这就是
      "不认关键词也能吸收片头" 的通用做法。
    """
    n, m = len(segments), len(sentences)
    if m == 0:
        return [[] for _ in range(n)]
    if n == 0:
        return []

    cum = [0]
    for s in sentences:
        cum.append(cum[-1] + len(s))

    expects = []
    for s in segments:
        d = max(0.4, s["end"] - s["start"])
        expects.append(max(1.0, 0.7 * (src_len(s["text"]) * ratio) + 0.3 * (d * cps)))

    INF = float("inf")
    dp = [[INF] * (m + 1) for _ in range(n + 1)]
    back = [[None] * (m + 1) for _ in range(n + 1)]

    # 开头：允许前 lead_in 段"免费"不取文本（片头广告 / 念书名 / 出版社）
    for i in range(0, min(lead_in, n) + 1):
        dp[i][0] = 0.0
    # 开头：允许跳过最前面最多 lead_sent 句（插图说明等），代价很小但不为零
    for j in range(0, min(lead_sent, m) + 1):
        dp[0][j] = min(dp[0][j], 0.6 * j)

    for i in range(1, n + 1):
        expect = expects[i - 1]
        for j in range(0, m + 1):
            # 这一段的两种"不取文本"情形代价为 0：块首的片头、块尾的片尾
            skip_cost = 0.0 if (j == 0 and i <= lead_in) or (j == m and i > n - lead_out) else expect
            best, bj = dp[i - 1][j] + skip_cost, j
            for take in range(1, 7):
                pj = j - take
                if pj < 0:
                    break
                prev = dp[i - 1][pj]
                if prev == INF:
                    continue
                got = cum[j] - cum[pj]
                cost = prev + (got - expect) ** 2 / expect + 0.4 * take
                if cost < best:
                    best, bj = cost, pj
            dp[i][j] = best
            back[i][j] = bj

    # 结尾：最后 lead_out 段可以不取文本
    best_end, best_i = INF, n
    for i in range(max(0, n - lead_out), n + 1):
        if dp[i][m] < best_end:
            best_end, best_i = dp[i][m], i

    assign = []
    i, j = best_i, m
    while i > 0:
        pj = back[i][j]
        if pj is None or pj > j:
            pj = j
        assign.append((i - 1, pj, j))
        i, j = i - 1, pj
    assign.reverse()

    result = [[] for _ in range(n)]
    for idx, a, b in assign:
        if 0 <= idx < n:
            result[idx] = sentences[a:b]
    return result


def split_cue(text: str, start: float, end: float, max_chars=22, min_dur=0.7):
    """把一句太长的译文按标点拆成多条字幕。"""
    dur = max(min_dur, end - start)
    if len(text) <= max_chars or dur <= 1.2:
        return [(text, start, max(end, start + min_dur))]
    parts, buf = [], ""
    for ch in text:
        buf += ch
        if len(buf) >= max_chars and ch in "，。！？、；：…,.!?;:":
            parts.append(buf)
            buf = ""
    if buf:
        parts.append(buf)
    if len(parts) <= 1:
        # 没有合适标点：硬切
        parts = [text[k:k + max_chars] for k in range(0, len(text), max_chars)]

    # 纯标点的碎片并回上一条（否则会出现只有「。」的一条字幕）
    cleaned = []
    for p in parts:
        if re.fullmatch(r"[\s\W_]+", p) and cleaned:
            cleaned[-1] += p
        else:
            cleaned.append(p)
    parts = [p for p in cleaned if p.strip()]
    total = sum(len(p) for p in parts) or 1
    out, t = [], start
    for p in parts:
        share = (end - start) * len(p) / total
        out.append((p, t, t + max(0.4, share)))
        t += share
    return out


# ------------------------------------------------------------------ 主流程

def find_inputs(target: str):
    audio = srt = book = None
    if os.path.isfile(target):
        low = target.lower()
        if low.endswith((".m4b", ".mp3", ".m4a", ".mp4", ".flac", ".wav", ".ogg", ".opus")):
            audio = target
        elif low.endswith(".srt"):
            srt = target
        elif low.endswith(".epub"):
            book = target
        media_dir = os.path.dirname(target)
    else:
        media_dir = target
        for root, _, files in os.walk(target):
            for fn in sorted(files):
                p = os.path.join(root, fn)
                low = fn.lower()
                if low.endswith(".srt") and not low.endswith(".zh.srt") and srt is None:
                    srt = p
                elif low.endswith(".epub") and book is None:
                    book = p
                elif low.endswith((".m4b", ".mp4", ".m4a", ".mp3")) and audio is None:
                    audio = p
    return audio, srt, book, media_dir


def main(argv=None):
    ap = argparse.ArgumentParser(description="用译文电子书给现成 SRT 生成译文字幕")
    ap.add_argument("target", help="有声书目录 / 单个音频 / 单个 srt")
    ap.add_argument("--srt", default=None, help="源 SRT（默认自动找）")
    ap.add_argument("--book", default=None, help="译文 epub（默认自动找）")
    ap.add_argument("--out", default=None, help="输出 srt（默认同名 .zh.srt）")
    ap.add_argument("--report", default=None, help="输出对齐明细")
    args = ap.parse_args(argv)

    audio, srt_path, book_path, media_dir = find_inputs(args.target)
    srt_path = args.srt or srt_path
    book_path = args.book or book_path
    if not srt_path:
        print("找不到源 SRT")
        return 2
    if not book_path:
        print("找不到译文 epub")
        return 2

    segs = parse_srt(srt_path)
    if not segs:
        print("SRT 是空的")
        return 2
    end_time = segs[-1]["end"]

    chapters = read_book(book_path, want_lang="zh")
    anchors = build_anchors(audio, media_dir, end_time)
    pairs = map_chapters(anchors, chapters)
    mode = "按章匹配"
    if not pairs:
        pairs = proportional_pairs(anchors, chapters)
        mode = "无章节信息，按字数比例整体切分"

    print(f"源 SRT  : {os.path.basename(srt_path)}  {len(segs)} 段 / {end_time/60:.1f} 分钟")
    print(f"译文    : {os.path.basename(book_path)}  {len(chapters)} 个章节 / "
          f"{sum(c['chars'] for c in chapters)} 字")
    print(f"锚点    : {os.path.basename(audio) if audio else '(无音频)'}  共 {len(anchors)} 个")
    print(f"配对方式: {mode}，配上 {len(pairs)} / {len(anchors)}")
    for i, c in pairs:
        a = anchors[i]
        print(f"  [{a['start']/60:7.1f}分 +{(a['end']-a['start'])/60:6.1f}] "
              f"{a['title'][:28]:28s} -> {c['title'][:28]}")

    cues = []
    covered_sent = 0
    for i, c in pairs:
        a = anchors[i]
        block = [s for s in segs if a["start"] - 0.01 <= s["start"] < a["end"] + 0.01]
        if not block:
            continue
        dur = sum(max(0.4, s["end"] - s["start"]) for s in block)
        src_chars = sum(src_len(s["text"]) for s in block) or 1
        ratio = c["chars"] / src_chars
        cps = c["chars"] / max(1e-6, dur)
        assigned = align_block(block, c["sentences"], ratio, cps)
        covered_sent += sum(len(x) for x in assigned)
        for seg, got in zip(block, assigned):
            text = "".join(got).strip()
            if not text:
                continue
            for piece, s0, s1 in split_cue(text, seg["start"], seg["end"]):
                cues.append({"start": s0, "end": s1, "text": piece})

    cues.sort(key=lambda c: c["start"])
    out_path = args.out or (os.path.splitext(srt_path)[0] + ".zh.srt")
    with open(out_path, "w", encoding="utf-8") as f:
        for n, c in enumerate(cues, 1):
            f.write(f"{n}\n{fmt_time(c['start'])} --> {fmt_time(c['end'])}\n{c['text']}\n\n")

    # ---- 体检 ----
    total_sent = sum(len(c["sentences"]) for _, c in pairs)
    gaps = [(b["start"] - a["end"]) for a, b in zip(cues, cues[1:])]
    big_gaps = [g for g in gaps if g > 20]
    dense = [c for c in cues if len(c["text"]) / max(0.5, c["end"] - c["start"]) > 14]
    print()
    print(f"输出    : {out_path}")
    print(f"  字幕 {len(cues)} 条；用掉译文 {covered_sent}/{total_sent} 句 "
          f"({covered_sent/max(1,total_sent)*100:.1f}%)")
    print(f"  时间轴 {cues[0]['start']/60:.1f} -> {cues[-1]['end']/60:.1f} 分钟，"
          f">20s 空隙 {len(big_gaps)} 处")
    print(f"  过密行(>14字/秒) {len(dense)} 条")
    if args.report:
        with open(args.report, "w", encoding="utf-8") as f:
            for c in cues:
                f.write(f"{c['start']:9.2f} {c['end']:9.2f}  {c['text']}\n")
        print(f"  明细: {args.report}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
