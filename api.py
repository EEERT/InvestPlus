"""
InvestPlus FastAPI backend.

Exposes bond data over HTTP so that the WinForms (or any other) client can
consume it without running a Streamlit server.

Usage:
    python api.py          # starts on http://0.0.0.0:8000
    uvicorn api:app --host 0.0.0.0 --port 8000 --reload
"""

from __future__ import annotations

import logging
import math
from datetime import datetime
from typing import Any, Optional

import pandas as pd
from fastapi import FastAPI, HTTPException
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import JSONResponse

try:
    import numpy as _np  # optional – only used for type narrowing in _safe()
except ImportError:  # pragma: no cover
    _np = None  # type: ignore[assignment]

# ── Logging ──────────────────────────────────────────────────────────────────
logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(name)s: %(message)s",
)
logger = logging.getLogger(__name__)

# ── App ───────────────────────────────────────────────────────────────────────
app = FastAPI(
    title="InvestPlus API",
    version="1.0.0",
    description="可转债行情数据 REST API，供 WinForms 客户端调用",
)

app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_methods=["GET"],
    allow_headers=["*"],
)


# ── Helpers ───────────────────────────────────────────────────────────────────

def _safe(val: Any) -> Any:
    """Convert pandas/numpy scalars to JSON-safe Python primitives."""
    if val is None:
        return None
    if isinstance(val, float) and (math.isnan(val) or math.isinf(val)):
        return None
    if isinstance(val, pd.Timestamp):
        return val.strftime("%Y-%m-%d") if not pd.isna(val) else None
    # numpy integer / float subtypes
    if _np is not None:
        if isinstance(val, _np.integer):
            return int(val)
        if isinstance(val, _np.floating):
            v = float(val)
            return None if (math.isnan(v) or math.isinf(v)) else v
    return val


def _df_to_records(df: pd.DataFrame) -> list[dict]:
    """Convert a DataFrame to a list of JSON-serialisable dicts."""
    records = []
    for _, row in df.iterrows():
        records.append({col: _safe(row[col]) for col in df.columns})
    return records


# ── Routes ────────────────────────────────────────────────────────────────────

@app.get("/api/bonds", summary="获取正在交易的可转债列表")
def get_active_bonds():
    """
    返回所有正在交易的可转债的行情与指标数据。

    数据来源（依次尝试）：
    1. 东方财富 push2 API（模拟人工浏览，规避反爬）
    2. AKShare bond_cov_comparison()
    3. 东方财富 RPT_BOND_CB_LIST（兜底）

    名称修正：以东方财富 RPT_BOND_CB_LIST 中的 SECURITY_SHORT_NAME 为准，
    若缺失则以新浪财经实时行情名称补充，确保返回可转债名称而非正股名称。
    """
    try:
        from data.bond_data import (  # noqa: PLC0415
            fetch_bond_all_list,
            fetch_bond_comparison,
            fetch_bond_redeem,
            fetch_bond_spot,
        )
        from utils.calculations import merge_bond_data  # noqa: PLC0415

        spot_df = fetch_bond_spot()
        comparison_df = fetch_bond_comparison()
        redeem_df = fetch_bond_redeem()
        detail_df = fetch_bond_all_list()

        merged = merge_bond_data(spot_df, comparison_df, redeem_df, detail_df)
    except Exception as exc:
        logger.exception("get_active_bonds failed: %s", exc)
        raise HTTPException(status_code=500, detail=f"数据获取失败: {exc}") from exc

    if merged.empty:
        return JSONResponse(
            content={
                "bonds": [],
                "count": 0,
                "last_update": datetime.now().strftime("%Y-%m-%d %H:%M:%S"),
            }
        )

    # Serialise DataFrame, replacing NaN/Inf with null and Timestamps with strings
    bonds = _df_to_records(merged)

    return JSONResponse(
        content={
            "bonds": bonds,
            "count": len(bonds),
            "last_update": datetime.now().strftime("%Y-%m-%d %H:%M:%S"),
        }
    )


@app.get("/api/health", summary="健康检查")
def health():
    return {"status": "ok", "time": datetime.now().isoformat()}


# ── Entry point ───────────────────────────────────────────────────────────────

if __name__ == "__main__":
    import uvicorn  # noqa: PLC0415

    uvicorn.run("api:app", host="0.0.0.0", port=8000, reload=False)
