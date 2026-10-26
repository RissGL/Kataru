# -*- coding: utf-8 -*-
"""句向量打分器（可选依赖）。

只有用到 --embed 时才 import，没装 sentence-transformers 的工具照常能用。

用 BAAI/bge-m3 这类多语言模型把「日文 ASR 段」和「中文句子」都编码成向量，
余弦相似度就是跨语言的语义匹配分数——不需要翻译，也不需要日文原版电子书。
"""
from __future__ import annotations

import hashlib
import os
import sys

import numpy as np


class EmbeddingScorer:
    """把两个文本列表编码成 L2 归一化向量，返回余弦相似度矩阵。"""

    def __init__(self, model_name: str = "BAAI/bge-m3", device: str | None = None,
                 batch_size: int = 64, cache_dir: str | None = None, verbose: bool = True):
        from sentence_transformers import SentenceTransformer

        if device is None:
            try:
                import torch
                device = "cuda" if torch.cuda.is_available() else "cpu"
            except ImportError:
                device = "cpu"

        self.model_name = model_name
        self.device = device
        self.batch_size = batch_size
        self.verbose = verbose

        if verbose:
            print(f"[embed] 载入 {model_name} (device={device}) ...", flush=True)

        self.model = SentenceTransformer(model_name, device=device, cache_folder=cache_dir)
        # bge-m3 支持 8192，但字幕段很短，512 足够且更快
        self.model.max_seq_length = 512

        # 向量缓存：同一批文本重复跑时直接命中
        self._cache: dict[str, np.ndarray] = {}
        self._cache_dir = cache_dir or os.path.join(os.path.expanduser("~"), ".cache", "srt-align-emb")
        os.makedirs(self._cache_dir, exist_ok=True)

    # ------------------------------------------------------------------ 编码

    def encode(self, texts: list[str]) -> np.ndarray:
        """返回 (n, d) 的 L2 归一化向量。"""
        if not texts:
            return np.zeros((0, 1), dtype=np.float32)

        key = self._batch_key(texts)
        cached = self._cache.get(key)
        if cached is not None:
            return cached

        # 按批编码，长文本截断交给模型的 max_seq_length
        vectors = self.model.encode(
            texts,
            batch_size=self.batch_size,
            normalize_embeddings=True,
            convert_to_numpy=True,
            show_progress_bar=False,
        ).astype(np.float32)

        self._cache[key] = vectors
        return vectors

    def _batch_key(self, texts: list[str]) -> str:
        h = hashlib.sha1()
        h.update(self.model_name.encode("utf-8"))
        h.update(b"\x00".join(t.encode("utf-8", "ignore") for t in texts))
        return h.hexdigest()

    # ------------------------------------------------------------------ 打分

    def score(self, src_texts: list[str], dst_texts: list[str]) -> np.ndarray:
        """(n_src, n_dst) 的余弦相似度矩阵。"""
        if not src_texts or not dst_texts:
            return np.zeros((len(src_texts), len(dst_texts)), dtype=np.float32)
        a = self.encode(src_texts)
        b = self.encode(dst_texts)
        return (a @ b.T).astype(np.float32)


def add_embed_arguments(parser) -> None:
    parser.add_argument("--embed", default=None, metavar="MODEL",
                        help="用句向量做语义对齐，例如 BAAI/bge-m3（需另装 sentence-transformers）")
    parser.add_argument("--sim-weight", type=float, default=2.0,
                        help="语义分数的权重（0 = 纯长度对齐，1 = 纯语义）")
    parser.add_argument("--device", default=None, help="cuda / cpu（默认自动）")
    parser.add_argument("--embed-cache", default=None, help="模型缓存目录")
