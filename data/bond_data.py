"""
Data fetching module for convertible bonds using AKShare.
All functions are cached for 120 seconds and return empty DataFrames on failure.
"""

import streamlit as st
import akshare as ak
import pandas as pd
from datetime import datetime, timedelta


@st.cache_data(ttl=120)
def fetch_bond_spot() -> pd.DataFrame:
    """
    Fetch convertible bond spot market data.
    Returns DataFrame with columns: 代码, 名称, 现价, 涨跌幅
    """
    try:
        df = ak.bond_zh_hs_cov_spot()
        if df is None or df.empty:
            return pd.DataFrame(columns=["代码", "名称", "现价", "涨跌幅"])

        col_map = {}
        # Map code column – include Sina API field name "symbol"
        for c in ["代码", "bond_id", "code", "symbol"]:
            if c in df.columns:
                col_map[c] = "代码"
                break
        # Map name column – include Sina API field name "name"
        for c in ["名称", "bond_name", "name"]:
            if c in df.columns:
                col_map[c] = "名称"
                break
        # Map current price column – include Sina API field name "trade"
        for c in ["最新价", "现价", "close", "price", "trade"]:
            if c in df.columns:
                col_map[c] = "现价"
                break
        # Map change pct column – include Sina API field name "changepercent"
        for c in ["涨跌幅", "change_pct", "pct_chg", "changepercent"]:
            if c in df.columns:
                col_map[c] = "涨跌幅"
                break

        df = df.rename(columns=col_map)
        needed = [c for c in ["代码", "名称", "现价", "涨跌幅"] if c in df.columns]
        df = df[needed].copy()

        for col in ["现价", "涨跌幅"]:
            if col in df.columns:
                df[col] = pd.to_numeric(df[col], errors="coerce")

        return df
    except Exception:
        return pd.DataFrame(columns=["代码", "名称", "现价", "涨跌幅"])


@st.cache_data(ttl=120)
def fetch_bond_comparison() -> pd.DataFrame:
    """
    Fetch convertible bond comparison data including fundamental metrics.
    Tries bond_cov_comparison() first; falls back to bond_zh_cov() if that fails.
    Returns DataFrame with standardised column names.
    """
    # Primary source: Eastmoney comparison table
    try:
        df = ak.bond_cov_comparison()
        if df is not None and not df.empty:
            return df.copy()
    except Exception:
        pass

    # Fallback source: Eastmoney comprehensive bond list
    try:
        df = ak.bond_zh_cov()
        if df is not None and not df.empty:
            return df.copy()
    except Exception:
        pass

    return pd.DataFrame()


@st.cache_data(ttl=120)
def fetch_bond_redeem() -> pd.DataFrame:
    """
    Fetch convertible bond forced-redemption progress data from JiSiLu.
    Returns DataFrame with 转债代码 and 已满足天数 columns.
    """
    try:
        df = ak.bond_cb_redeem_jsl()
        if df is None or df.empty:
            return pd.DataFrame()
        return df.copy()
    except Exception:
        return pd.DataFrame()


@st.cache_data(ttl=120)
def fetch_stock_hist(code: str, days: int = 60) -> pd.DataFrame:
    """
    Fetch recent N trading days of daily OHLCV data for a given A-share stock.

    Parameters
    ----------
    code : str
        Stock code, optionally prefixed with SH/SZ (e.g. 'SH600519' or '600519').
    days : int
        Number of calendar days to look back (default 60).

    Returns
    -------
    pd.DataFrame with columns including 日期, 收盘 (and others from AKShare).
    """
    try:
        # Strip exchange prefix if present
        clean_code = code.upper()
        for prefix in ("SH", "SZ", "BJ"):
            if clean_code.startswith(prefix):
                clean_code = clean_code[len(prefix):]
                break

        end_date = datetime.today()
        start_date = end_date - timedelta(days=days * 2)  # fetch extra to cover holidays

        df = ak.stock_zh_a_hist(
            symbol=clean_code,
            period="daily",
            start_date=start_date.strftime("%Y%m%d"),
            end_date=end_date.strftime("%Y%m%d"),
            adjust="qfq",
        )
        if df is None or df.empty:
            return pd.DataFrame()

        # Normalise column names
        col_map = {}
        for c in df.columns:
            if "日期" in c or c.lower() in ("date",):
                col_map[c] = "日期"
            elif "收盘" in c or c.lower() in ("close",):
                col_map[c] = "收盘"
        df = df.rename(columns=col_map)

        if "收盘" in df.columns:
            df["收盘"] = pd.to_numeric(df["收盘"], errors="coerce")

        # Sort ascending and keep the most recent `days` rows
        if "日期" in df.columns:
            df = df.sort_values("日期").tail(days).reset_index(drop=True)

        return df
    except Exception:
        return pd.DataFrame()
