"""
Calculation utilities for InvestPlus investment monitoring.
"""

from __future__ import annotations

import re
from datetime import datetime
from typing import Optional

import pandas as pd


# ---------------------------------------------------------------------------
# Core financial formulas
# ---------------------------------------------------------------------------

def calc_conversion_value(stock_price: float, conversion_price: float) -> Optional[float]:
    """
    Convertible bond conversion value = (stock_price / conversion_price) * 100.
    Returns None when conversion_price is zero or non-positive.
    """
    try:
        if conversion_price and conversion_price > 0:
            return (stock_price / conversion_price) * 100
    except (TypeError, ZeroDivisionError):
        pass
    return None


def calc_premium_rate(bond_price: float, conversion_value: float) -> Optional[float]:
    """
    Conversion premium rate = (bond_price / conversion_value - 1) * 100.
    Returns None when conversion_value is zero or non-positive.
    """
    try:
        if conversion_value and conversion_value > 0:
            return (bond_price / conversion_value - 1) * 100
    except (TypeError, ZeroDivisionError):
        pass
    return None


def calc_put_trigger_days(stock_code: str, put_trigger_price: float) -> int:
    """
    Count consecutive trading days (from most recent backwards) where the
    closing price is below `put_trigger_price`.

    Parameters
    ----------
    stock_code : str
        A-share stock code (exchange prefix is handled inside fetch_stock_hist).
    put_trigger_price : float
        The put (回售) trigger price threshold.

    Returns
    -------
    int – number of consecutive recent days below the trigger price (0 if none).
    """
    # Import here to avoid circular imports
    from data.bond_data import fetch_stock_hist  # noqa: PLC0415

    try:
        if not stock_code or pd.isna(put_trigger_price) or put_trigger_price <= 0:
            return 0

        df = fetch_stock_hist(str(stock_code), days=60)
        if df.empty or "收盘" not in df.columns:
            return 0

        closes = df["收盘"].dropna().tolist()
        closes.reverse()  # most recent first

        count = 0
        for price in closes:
            if price < put_trigger_price:
                count += 1
            else:
                break
        return count
    except Exception:
        return 0


# ---------------------------------------------------------------------------
# Column-name helpers
# ---------------------------------------------------------------------------

def _find_col(df: pd.DataFrame, candidates: list[str]) -> Optional[str]:
    """Return the first candidate column name present in *df*, or None."""
    for c in candidates:
        if c in df.columns:
            return c
    return None


def _strip_exchange(code: str) -> str:
    """Remove SH/SZ/BJ prefix from a stock or bond code string."""
    if isinstance(code, str):
        return re.sub(r"^(SH|SZ|BJ)", "", code.upper())
    return str(code)


# ---------------------------------------------------------------------------
# Data merge & enrichment
# ---------------------------------------------------------------------------

def merge_bond_data(
    spot_df: pd.DataFrame,
    comparison_df: pd.DataFrame,
    redeem_df: pd.DataFrame,
) -> pd.DataFrame:
    """
    Merge spot, comparison and redemption DataFrames, compute all derived
    fields and return a final DataFrame with standardised column names.

    Output columns
    --------------
    序号, 转债代码, 转债名称, 现价, 涨跌幅, 正股名称, 正股价, 正股涨跌,
    正股PB, 转股价, 转股价值, 转股溢价率, 债券评级, 回售触发价,
    回售触发天数, 强赎触发价, 强赎触发天数, 转债占比, 到期时间,
    剩余年限, 剩余规模
    """

    # ── 1. Normalise spot DataFrame ────────────────────────────────────────
    if spot_df is None or spot_df.empty:
        spot_norm = pd.DataFrame(columns=["转债代码", "现价", "涨跌幅"])
    else:
        spot_norm = spot_df.copy()
        code_col = _find_col(spot_norm, ["代码", "bond_id", "code"])
        price_col = _find_col(spot_norm, ["现价", "最新价", "close", "price"])
        chg_col = _find_col(spot_norm, ["涨跌幅", "change_pct", "pct_chg"])

        rename = {}
        if code_col:
            rename[code_col] = "转债代码_spot"
        if price_col:
            rename[price_col] = "现价_spot"
        if chg_col:
            rename[chg_col] = "涨跌幅_spot"
        spot_norm = spot_norm.rename(columns=rename)

        if "转债代码_spot" in spot_norm.columns:
            spot_norm["转债代码_spot"] = spot_norm["转债代码_spot"].astype(str).apply(_strip_exchange)

    # ── 2. Normalise comparison DataFrame ─────────────────────────────────
    if comparison_df is None or comparison_df.empty:
        result = pd.DataFrame()
    else:
        result = comparison_df.copy()

        # Flexible column mapping for comparison data
        comp_col_map = {
            "转债代码": ["转债代码", "bond_code", "cb_code", "代码"],
            "转债名称": ["转债名称", "bond_name", "cb_name", "名称"],
            "正股名称": ["正股名称", "stock_name", "正股"],
            "正股价": ["正股现价", "正股价", "stock_price", "正股最新价"],
            "正股涨跌": ["正股涨跌幅", "正股涨跌", "stock_change", "stock_pct"],
            "正股PB": ["正股PB", "pb", "PB", "市净率"],
            "转股价": ["转股价", "conversion_price", "转股价格"],
            "债券评级": ["债券评级", "rating", "评级", "bond_rating"],
            "回售触发价": ["回售触发价", "put_trigger", "回售价格", "回售触发价格"],
            "强赎触发价": ["强赎触发价", "redeem_trigger", "强赎价格", "强赎触发价格"],
            "转债剩余规模": ["转债剩余规模", "余额", "剩余规模", "remaining_size"],
            "到期时间": ["到期时间", "maturity", "到期日", "maturity_date"],
            "剩余年限": ["剩余年限", "remaining_years", "years_to_maturity"],
            "转债占比": ["转债占比", "cb_ratio", "占比"],
        }

        rename = {}
        for target, candidates in comp_col_map.items():
            found = _find_col(result, candidates)
            if found and found != target:
                rename[found] = target
        result = result.rename(columns=rename)

        if "转债代码" in result.columns:
            result["转债代码"] = result["转债代码"].astype(str).apply(_strip_exchange)

    # ── 3. Merge spot into comparison ──────────────────────────────────────
    if not result.empty and "转债代码_spot" in spot_norm.columns:
        spot_merge = spot_norm[
            [c for c in ["转债代码_spot", "现价_spot", "涨跌幅_spot"] if c in spot_norm.columns]
        ].copy()
        if "转债代码" in result.columns:
            result = result.merge(
                spot_merge,
                left_on="转债代码",
                right_on="转债代码_spot",
                how="left",
            )
            result = result.drop(columns=["转债代码_spot"], errors="ignore")

        # Prefer spot price when available
        if "现价_spot" in result.columns:
            if "转债现价" not in result.columns:
                result["转债现价"] = pd.to_numeric(result.get("现价_spot"), errors="coerce")
            result["现价"] = result["现价_spot"].combine_first(
                pd.to_numeric(result.get("转债现价", pd.Series(dtype=float)), errors="coerce")
            )
            result = result.drop(columns=["现价_spot"], errors="ignore")
        elif "转债现价" in result.columns:
            result["现价"] = pd.to_numeric(result["转债现价"], errors="coerce")

        if "涨跌幅_spot" in result.columns:
            result["涨跌幅"] = result["涨跌幅_spot"]
            result = result.drop(columns=["涨跌幅_spot"], errors="ignore")

    elif not result.empty:
        # Fall back to comparison's own price column
        for c in ["转债现价", "现价"]:
            if c in result.columns:
                result["现价"] = pd.to_numeric(result[c], errors="coerce")
                break

    # ── 4. Merge forced-redemption progress ───────────────────────────────
    if not redeem_df.empty and not result.empty:
        redeem_norm = redeem_df.copy()
        code_col_r = _find_col(redeem_norm, ["转债代码", "bond_code", "代码"])
        days_col_r = _find_col(
            redeem_norm,
            ["已满足天数", "满足天数", "强赎天数", "redeem_days", "days"],
        )
        if code_col_r and days_col_r:
            redeem_norm = redeem_norm[[code_col_r, days_col_r]].copy()
            redeem_norm = redeem_norm.rename(
                columns={code_col_r: "转债代码_r", days_col_r: "强赎触发天数_r"}
            )
            redeem_norm["转债代码_r"] = redeem_norm["转债代码_r"].astype(str).apply(_strip_exchange)
            if "转债代码" in result.columns:
                result = result.merge(
                    redeem_norm,
                    left_on="转债代码",
                    right_on="转债代码_r",
                    how="left",
                )
                result = result.drop(columns=["转债代码_r"], errors="ignore")
                result["强赎触发天数"] = pd.to_numeric(result["强赎触发天数_r"], errors="coerce")
                result = result.drop(columns=["强赎触发天数_r"], errors="ignore")

    # ── 5. Compute derived fields ──────────────────────────────────────────
    if not result.empty:
        # Numeric conversions
        for col in ["现价", "正股价", "正股涨跌", "正股PB", "转股价", "回售触发价", "强赎触发价"]:
            if col in result.columns:
                result[col] = pd.to_numeric(result[col], errors="coerce")
            else:
                result[col] = float("nan")

        # 转股价值
        result["转股价值"] = result.apply(
            lambda r: calc_conversion_value(r.get("正股价", float("nan")), r.get("转股价", 0)),
            axis=1,
        )

        # 转股溢价率
        result["转股溢价率"] = result.apply(
            lambda r: calc_premium_rate(r.get("现价", float("nan")), r.get("转股价值") or 0),
            axis=1,
        )

        # 强赎触发天数 – if not merged from redeem_df, default to NaN
        if "强赎触发天数" not in result.columns:
            result["强赎触发天数"] = float("nan")

        # 回售触发天数 – calculated from stock history
        def _put_days(row: pd.Series) -> int:
            stock_code = row.get("正股代码", "")
            put_price = row.get("回售触发价", float("nan"))
            if not stock_code or pd.isna(put_price):
                return 0
            return calc_put_trigger_days(str(stock_code), float(put_price))

        # Only compute if we have stock codes; otherwise leave as 0
        if "正股代码" in result.columns:
            result["回售触发天数"] = result.apply(_put_days, axis=1)
        else:
            result["回售触发天数"] = 0

        # 剩余年限 numeric
        if "剩余年限" in result.columns:
            result["剩余年限"] = pd.to_numeric(result["剩余年限"], errors="coerce")

        # 剩余规模 numeric
        for c in ["转债剩余规模", "剩余规模"]:
            if c in result.columns:
                result["剩余规模"] = pd.to_numeric(result[c], errors="coerce")
                break
        if "剩余规模" not in result.columns:
            result["剩余规模"] = float("nan")

        # Ensure all required output columns exist
        for col in [
            "转债名称", "正股名称", "正股价", "正股涨跌", "正股PB",
            "转股价", "债券评级", "回售触发价", "强赎触发价",
            "转债占比", "到期时间", "剩余年限",
        ]:
            if col not in result.columns:
                result[col] = None

        if "涨跌幅" not in result.columns:
            result["涨跌幅"] = float("nan")

        # ── 6. Select & rename final columns ──────────────────────────────
        final_cols = [
            "转债代码", "转债名称", "现价", "涨跌幅", "正股名称",
            "正股价", "正股涨跌", "正股PB", "转股价", "转股价值",
            "转股溢价率", "债券评级", "回售触发价", "回售触发天数",
            "强赎触发价", "强赎触发天数", "转债占比", "到期时间",
            "剩余年限", "剩余规模",
        ]
        available = [c for c in final_cols if c in result.columns]
        result = result[available].reset_index(drop=True)

        # Add sequential index
        result.insert(0, "序号", range(1, len(result) + 1))

    return result if not result.empty else pd.DataFrame(
        columns=["序号", "转债代码", "转债名称", "现价", "涨跌幅"]
    )
