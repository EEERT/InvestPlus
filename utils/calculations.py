"""
Calculation utilities for InvestPlus investment monitoring.
"""

from __future__ import annotations

import re
from datetime import datetime, date
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


def calc_remaining_years(maturity: object) -> Optional[float]:
    """
    Calculate remaining years to maturity from today.
    Accepts datetime, date, Timestamp or string.
    Returns 0.0 if maturity has already passed, None if maturity is null/unparseable.
    """
    try:
        if maturity is None:
            return None
        if isinstance(maturity, float) and pd.isna(maturity):
            return None
        if isinstance(maturity, pd.Timestamp):
            if pd.isna(maturity):
                return None
            maturity = maturity.date()
        elif isinstance(maturity, datetime):
            maturity = maturity.date()
        elif isinstance(maturity, str):
            ts = pd.to_datetime(maturity, errors="coerce")
            if pd.isna(ts):
                return None
            maturity = ts.date()

        today = date.today()
        delta = (maturity - today).days
        return round(delta / 365.25, 4) if delta > 0 else 0.0
    except Exception:
        return None


def calc_put_trigger_days(stock_code: str, put_trigger_price: float) -> int:
    """
    Count consecutive trading days (from most recent backwards) where the
    closing price is below `put_trigger_price`.
    """
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


def _normalise_bond_code(code: str) -> str:
    """Strip exchange prefix, whitespace and float suffix from a bond/stock code.

    Handles float-formatted codes produced when AKShare's fetch_paginated_data
    runs pd.to_numeric on the code field (e.g. 110044.0 → '110044').
    """
    s = str(code).strip()
    # Remove trailing ".0" (or ".00" etc.) left by float → str conversion
    s = re.sub(r"\.0+$", "", s)
    return _strip_exchange(s)


# Supplementary columns fetched from RPT_BOND_CB_LIST (detail_df)
# to merge into the active-bond comparison result.
_DETAIL_SUPPLEMENT_COLS = ["债券评级", "到期时间", "剩余规模", "正股PB", "转债占比", "发行规模"]

# Keywords in 强赎状态 that indicate the issuer has announced they will NOT
# exercise the forced-redemption right.  Per CB regulations the issuer cannot
# exercise the right again during the exchange's restricted period after such
# an announcement, so the trigger-day counter should be reset to 0.
_REDEEM_WAIVER_PATTERN = r"不赎|放弃|waiv"


# ---------------------------------------------------------------------------
# Data merge & enrichment
# ---------------------------------------------------------------------------

def merge_bond_data(
    spot_df: pd.DataFrame,
    comparison_df: pd.DataFrame,
    redeem_df: pd.DataFrame,
    detail_df: "pd.DataFrame | None" = None,
) -> pd.DataFrame:
    """
    Merge spot, comparison, redemption and (optional) detail DataFrames,
    compute all derived fields and return a final DataFrame with standardised
    column names.

    Parameters
    ----------
    spot_df       : Sina real-time spot data (optional price supplement).
    comparison_df : Eastmoney comparison table – ONLY currently trading bonds.
                    Columns from bond_cov_comparison():
                      转债代码, 转债名称, 转债最新价, 转债涨跌幅,
                      正股代码, 正股名称, 正股最新价, 正股涨跌幅,
                      转股价, 转股价值, 转股溢价率, 纯债溢价率,
                      回售触发价, 强赎触发价, …
    redeem_df     : JiSiLu forced-redemption countdown.
    detail_df     : Eastmoney RPT_BOND_CB_LIST with extra fields (optional).
                    Provides: 债券评级, 到期时间, 剩余规模, 正股PB, etc.

    Output columns
    --------------
    序号, 转债代码, 转债名称, 现价, 涨跌幅, 正股名称, 正股价, 正股涨跌,
    正股PB, 转股价, 转股价值, 转股溢价率, 债券评级, 回售触发价,
    回售触发天数, 强赎触发价, 强赎触发天数, 转债占比, 到期时间,
    剩余年限, 剩余规模
    """

    # ── 1. Normalise comparison DataFrame (currently trading bonds only) ──
    if comparison_df is None or comparison_df.empty:
        result = pd.DataFrame()
    else:
        result = comparison_df.copy()

        # Explicit mapping: bond_cov_comparison() exact column names → targets
        comp_col_map = {
            "转债代码": ["转债代码", "bond_code", "cb_code", "代码", "债券代码"],
            "转债名称": ["转债名称", "bond_name", "cb_name", "名称", "债券简称"],
            "正股代码": ["正股代码", "stock_code"],
            "正股名称": ["正股名称", "stock_name", "正股简称"],
            "正股价": ["正股最新价", "正股现价", "正股价", "stock_price"],
            "正股涨跌": ["正股涨跌幅", "正股涨跌", "stock_change", "stock_pct"],
            "正股PB": ["正股PB", "PBV_RATIO", "pb", "PB", "市净率"],
            "转股价": ["转股价", "conversion_price", "转股价格", "TRANSFER_PRICE"],
            "转股价值": ["转股价值", "conversion_value", "TRANSFER_VALUE"],
            "转股溢价率": ["转股溢价率", "premium_rate", "TRANSFER_PREMIUM_RATIO"],
            "债券评级": ["债券评级", "rating", "评级", "bond_rating", "信用评级", "CREDIT_RATING"],
            "回售触发价": ["回售触发价", "put_trigger", "回售价格", "RESALE_TRIG_PRICE"],
            "强赎触发价": ["强赎触发价", "redeem_trigger", "强赎价格", "REDEEM_TRIG_PRICE"],
            "转债现价": ["转债最新价", "转债现价", "债现价", "CURRENT_BOND_PRICE"],
            "转债涨跌": ["转债涨跌幅", "转债涨跌"],
            "到期时间": ["到期时间", "到期日", "maturity", "MATURITY_DATE", "EXPIRE_DATE"],
            "剩余规模": ["剩余规模", "转债剩余规模", "余额", "CURR_ISS_AMT", "remaining_size"],
            "转债占比": ["转债占比", "cb_ratio", "占比"],
        }

        rename = {}
        for target, candidates in comp_col_map.items():
            found = _find_col(result, candidates)
            if found and found != target:
                rename[found] = target
        result = result.rename(columns=rename)

        if "转债代码" in result.columns:
            result["转债代码"] = result["转债代码"].astype(str).apply(_normalise_bond_code)

    if result.empty:
        return pd.DataFrame(columns=["序号", "转债代码", "转债名称", "现价", "涨跌幅"])

    # ── 2. Merge detail_df (supplementary fields: rating, maturity, PB, etc.) ─
    if detail_df is not None and not detail_df.empty:
        det = detail_df.copy()
        if "转债代码" in det.columns:
            det["转债代码"] = det["转债代码"].astype(str).apply(_normalise_bond_code)

            extra_cols = _DETAIL_SUPPLEMENT_COLS
            available_extras = [
                c for c in extra_cols
                if c in det.columns and c not in result.columns
            ]
            if available_extras:
                det_merge = det[["转债代码"] + available_extras].drop_duplicates("转债代码")
                result = result.merge(det_merge, on="转债代码", how="left")

            # Override bond name with authoritative data from RPT_BOND_CB_LIST.
            # bond_cov_comparison() (push2 API) may return stock names in the
            # 转债名称 field; the datacenter API is more reliable.
            if "转债名称" in det.columns:
                _det_names = (
                    det[["转债代码", "转债名称"]]
                    .drop_duplicates("转债代码")
                    .rename(columns={"转债名称": "_det_转债名称"})
                )
                result = result.merge(_det_names, on="转债代码", how="left")
                _valid_name = (
                    result["_det_转债名称"].notna()
                    & (result["_det_转债名称"].astype(str).str.strip() != "")
                )
                if "转债名称" not in result.columns:
                    result["转债名称"] = None
                result["转债名称"] = result["_det_转债名称"].where(
                    _valid_name, result["转债名称"]
                )
                result = result.drop(columns=["_det_转债名称"])

    # ── 2a. Filter to currently trading bonds (positive remaining balance) ──
    # Bonds with 剩余规模 = 0 have been fully redeemed/matured; remove them.
    # Bonds where 剩余规模 is NaN (data unavailable) are retained to avoid
    # false negatives when the datacenter API doesn't return balance data.
    if "剩余规模" in result.columns:
        result = result[result["剩余规模"].isna() | (result["剩余规模"] > 0)]

    # ── 3. Merge spot data (real-time price supplement + name fallback) ───
    if spot_df is not None and not spot_df.empty:
        spot_norm = spot_df.copy()
        code_col = _find_col(spot_norm, ["代码", "bond_id", "code"])
        price_col = _find_col(spot_norm, ["现价", "最新价", "close", "price"])
        chg_col = _find_col(spot_norm, ["涨跌幅", "change_pct", "pct_chg"])
        # Sina spot data carries the correct bond short-name in "名称".
        # Capture it so we can use it as a fallback when the detail_df
        # name lookup yields nothing (e.g. the bond was just listed).
        name_col = _find_col(spot_norm, ["名称", "bond_name", "name"])

        spot_rename = {}
        if code_col:
            spot_rename[code_col] = "转债代码_spot"
        if price_col:
            spot_rename[price_col] = "现价_spot"
        if chg_col:
            spot_rename[chg_col] = "涨跌幅_spot"
        if name_col:
            spot_rename[name_col] = "名称_spot"
        spot_norm = spot_norm.rename(columns=spot_rename)

        if "转债代码_spot" in spot_norm.columns:
            spot_norm["转债代码_spot"] = (
                spot_norm["转债代码_spot"].astype(str).apply(_normalise_bond_code)
            )
            spot_merge = spot_norm[
                [c for c in ["转债代码_spot", "现价_spot", "涨跌幅_spot", "名称_spot"]
                 if c in spot_norm.columns]
            ].copy()
            if "转债代码" in result.columns:
                result = result.merge(
                    spot_merge,
                    left_on="转债代码",
                    right_on="转债代码_spot",
                    how="left",
                )
                result = result.drop(columns=["转债代码_spot"], errors="ignore")

    # Resolve bond name: priority → detail_df (already applied in step 2) →
    # Sina spot name → original comparison name.
    # bond_cov_comparison() (push2 API) sometimes returns the underlying
    # stock's name in the 转债名称 field.  The detail_df override in step 2
    # fixes most cases; the Sina spot fallback covers any remaining gaps.
    if "名称_spot" in result.columns:
        _missing_name = (
            result["转债名称"].isna()
            | (result["转债名称"].astype(str).str.strip() == "")
        )
        _spot_valid = result["名称_spot"].notna() & (
            result["名称_spot"].astype(str).str.strip() != ""
        )
        result.loc[_missing_name & _spot_valid, "转债名称"] = result.loc[
            _missing_name & _spot_valid, "名称_spot"
        ]
        result = result.drop(columns=["名称_spot"], errors="ignore")

    # Resolve price: prefer spot, then comparison
    if "现价_spot" in result.columns:
        comp_price = pd.to_numeric(
            result.get("转债现价", pd.Series(dtype=float)), errors="coerce"
        )
        result["现价"] = (
            pd.to_numeric(result["现价_spot"], errors="coerce").combine_first(comp_price)
        )
        result = result.drop(columns=["现价_spot"], errors="ignore")
    elif "转债现价" in result.columns:
        result["现价"] = pd.to_numeric(result["转债现价"], errors="coerce")
    elif "现价" not in result.columns:
        result["现价"] = float("nan")

    # Resolve change pct
    if "涨跌幅_spot" in result.columns:
        result["涨跌幅"] = pd.to_numeric(result["涨跌幅_spot"], errors="coerce")
        result = result.drop(columns=["涨跌幅_spot"], errors="ignore")
    elif "涨跌幅" not in result.columns:
        if "转债涨跌" in result.columns:
            result["涨跌幅"] = pd.to_numeric(result["转债涨跌"], errors="coerce")
        else:
            result["涨跌幅"] = float("nan")

    # ── 4. Merge forced-redemption progress ───────────────────────────────
    if redeem_df is not None and not redeem_df.empty and not result.empty:
        redeem_norm = redeem_df.copy()
        code_col_r = _find_col(redeem_norm, ["代码", "转债代码", "bond_code"])
        days_col_r = _find_col(
            redeem_norm,
            ["强赎天计数", "已满足天数", "满足天数", "强赎天数", "redeem_days", "days"],
        )
        status_col_r = _find_col(redeem_norm, ["强赎状态", "redeem_status", "状态"])
        if code_col_r and days_col_r:
            cols_to_keep = [code_col_r, days_col_r]
            if status_col_r and status_col_r not in cols_to_keep:
                cols_to_keep.append(status_col_r)
            redeem_norm = redeem_norm[cols_to_keep].copy()
            redeem_norm[days_col_r] = pd.to_numeric(
                redeem_norm[days_col_r]
                .astype(str)
                .str.extract(r"(\d+)", expand=False),
                errors="coerce",
            )
            rename_r: dict = {code_col_r: "转债代码_r", days_col_r: "强赎触发天数_r"}
            if status_col_r:
                rename_r[status_col_r] = "强赎状态_r"
            redeem_norm = redeem_norm.rename(columns=rename_r)
            redeem_norm["转债代码_r"] = (
                redeem_norm["转债代码_r"].astype(str).apply(_normalise_bond_code)
            )
            if "转债代码" in result.columns:
                result = result.merge(
                    redeem_norm,
                    left_on="转债代码",
                    right_on="转债代码_r",
                    how="left",
                )
                result = result.drop(columns=["转债代码_r"], errors="ignore")
                result["强赎触发天数"] = pd.to_numeric(
                    result["强赎触发天数_r"], errors="coerce"
                )
                result = result.drop(columns=["强赎触发天数_r"], errors="ignore")

                # Merge redemption status so users can see whether the issuer
                # has announced they will not exercise the redemption right.
                if "强赎状态_r" in result.columns:
                    result["强赎状态"] = result["强赎状态_r"].where(
                        result["强赎状态_r"].notna(), None
                    )
                    result = result.drop(columns=["强赎状态_r"], errors="ignore")

                # Per CB regulations: once an issuer announces they will NOT
                # exercise the redemption right, they cannot do so again in the
                # exchange's restricted period.  Reset the trigger day count to
                # 0 for bonds whose status indicates a waiver so that alert
                # thresholds are not incorrectly triggered.
                if "强赎状态" in result.columns:
                    _waived = result["强赎状态"].astype(str).str.contains(
                        _REDEEM_WAIVER_PATTERN, case=False, na=False
                    )
                    result.loc[_waived, "强赎触发天数"] = 0

    # ── 5. Compute derived fields ──────────────────────────────────────────
    # Numeric conversions
    for col in ["现价", "正股价", "正股涨跌", "正股PB", "转股价", "回售触发价", "强赎触发价"]:
        if col in result.columns:
            result[col] = pd.to_numeric(result[col], errors="coerce")

    # 转股价值 – recompute from 正股价/转股价 when the column is all NaN
    if "转股价值" not in result.columns or result["转股价值"].isna().all():
        result["转股价值"] = result.apply(
            lambda r: calc_conversion_value(
                r.get("正股价", float("nan")),
                r.get("转股价", float("nan")),
            ),
            axis=1,
        )
    else:
        result["转股价值"] = pd.to_numeric(result["转股价值"], errors="coerce")

    # 转股溢价率 – recompute when the column is all NaN
    if "转股溢价率" not in result.columns or result["转股溢价率"].isna().all():
        result["转股溢价率"] = result.apply(
            lambda r: calc_premium_rate(
                r.get("现价", float("nan")),
                r.get("转股价值") if pd.notna(r.get("转股价值")) else float("nan"),
            ),
            axis=1,
        )
    else:
        result["转股溢价率"] = pd.to_numeric(result["转股溢价率"], errors="coerce")

    # 强赎触发天数 – default to NaN if not yet merged
    if "强赎触发天数" not in result.columns:
        result["强赎触发天数"] = float("nan")

    # 回售触发天数 – calculated from stock closing price history
    def _put_days(row: pd.Series) -> int:
        stock_code = row.get("正股代码", "")
        put_price = row.get("回售触发价", float("nan"))
        if not stock_code or pd.isna(put_price):
            return 0
        return calc_put_trigger_days(str(stock_code), float(put_price))

    if "正股代码" in result.columns:
        result["回售触发天数"] = result.apply(_put_days, axis=1)
    else:
        result["回售触发天数"] = 0

    # 到期时间 → normalise to datetime
    if "到期时间" in result.columns:
        result["到期时间"] = pd.to_datetime(result["到期时间"], errors="coerce")

    # 剩余年限 – vectorised computation from 到期时间 when available
    if "到期时间" in result.columns:
        today_ts = pd.Timestamp(date.today())
        days_left = (result["到期时间"] - today_ts).dt.days
        result["剩余年限"] = (days_left / 365.25).clip(lower=0).where(
            result["到期时间"].notna()
        ).round(4)
    elif "剩余年限" in result.columns:
        result["剩余年限"] = pd.to_numeric(result["剩余年限"], errors="coerce")
    else:
        result["剩余年限"] = float("nan")

    # 剩余规模 numeric
    if "剩余规模" in result.columns:
        result["剩余规模"] = pd.to_numeric(result["剩余规模"], errors="coerce")
    else:
        result["剩余规模"] = float("nan")

    # 转债占比 numeric
    if "转债占比" in result.columns:
        result["转债占比"] = pd.to_numeric(result["转债占比"], errors="coerce")

    # Ensure all required output columns exist (fill with None/NaN if absent)
    for col in [
        "转债名称", "正股名称", "正股价", "正股涨跌", "正股PB",
        "转股价", "债券评级", "回售触发价", "强赎触发价",
        "转债占比", "到期时间", "剩余年限", "强赎状态",
    ]:
        if col not in result.columns:
            result[col] = None

    if "涨跌幅" not in result.columns:
        if "转债涨跌" in result.columns:
            result["涨跌幅"] = pd.to_numeric(result["转债涨跌"], errors="coerce")
        else:
            result["涨跌幅"] = float("nan")

    # ── 6. Select & rename final columns ──────────────────────────────────
    final_cols = [
        "转债代码", "转债名称", "现价", "涨跌幅", "正股名称",
        "正股价", "正股涨跌", "正股PB", "转股价", "转股价值",
        "转股溢价率", "债券评级", "回售触发价", "回售触发天数",
        "强赎触发价", "强赎触发天数", "强赎状态", "转债占比", "到期时间",
        "剩余年限", "剩余规模",
    ]
    available = [c for c in final_cols if c in result.columns]
    result = result[available].reset_index(drop=True)

    # Add sequential index
    result.insert(0, "序号", range(1, len(result) + 1))

    return result


def split_active_inactive(
    comparison_df: pd.DataFrame,
    all_list_df: pd.DataFrame,
) -> pd.DataFrame:
    """
    Derive a DataFrame of INACTIVE bonds = all_list_df minus currently active.

    Parameters
    ----------
    comparison_df : Currently trading bonds (from bond_cov_comparison()).
    all_list_df   : All bonds ever listed (from fetch_bond_all_list()).

    Returns
    -------
    DataFrame of inactive bonds with basic display columns.
    """
    if all_list_df is None or all_list_df.empty:
        return pd.DataFrame()

    active_codes: set = set()
    if comparison_df is not None and not comparison_df.empty:
        code_col = _find_col(comparison_df, ["转债代码", "bond_code", "代码"])
        if code_col:
            active_codes = {
                _normalise_bond_code(c)
                for c in comparison_df[code_col].astype(str)
            }

    det = all_list_df.copy()
    if "转债代码" not in det.columns:
        return pd.DataFrame()

    det["_code_norm"] = det["转债代码"].astype(str).apply(_normalise_bond_code)
    inactive = det[~det["_code_norm"].isin(active_codes)].copy()
    inactive = inactive.drop(columns=["_code_norm"], errors="ignore")

    cols_wanted = [
        "转债代码", "转债名称", "上市时间", "到期时间", "债券评级",
        "发行规模", "剩余规模", "转股价", "正股代码", "正股名称",
    ]
    available = [c for c in cols_wanted if c in inactive.columns]
    inactive = inactive[available].reset_index(drop=True)

    for dc in ["上市时间", "到期时间"]:
        if dc in inactive.columns:
            inactive[dc] = pd.to_datetime(inactive[dc], errors="coerce").dt.strftime("%Y-%m-%d")

    inactive.insert(0, "序号", range(1, len(inactive) + 1))
    return inactive
