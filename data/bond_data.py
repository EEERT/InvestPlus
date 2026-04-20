"""
Data fetching module for convertible bonds using AKShare.
All functions are cached for 120 seconds and return empty DataFrames on failure.

Data sources:
- bond_cov_comparison()   : Eastmoney comparison list – ONLY currently trading bonds.
                            Provides real-time price, change%, underlying stock info,
                            conversion metrics, put/call trigger prices.
- fetch_bond_all_list()   : Eastmoney RPT_BOND_CB_LIST – ALL bonds (active + inactive).
                            Provides credit rating, maturity date, remaining balance,
                            underlying stock PBV ratio.
- bond_cb_redeem_jsl()    : JiSiLu forced-redemption countdown (no auth required).
                            Provides redemption day count and status.
- bond_zh_hs_cov_spot()   : Sina real-time spot data (supplementary price feed).
"""

import re

import akshare as ak
import pandas as pd
import requests
import streamlit as st
from datetime import datetime, timedelta


# ── Eastmoney RPT_BOND_CB_LIST direct API ─────────────────────────────────────

_EM_CB_LIST_URL = "https://datacenter-web.eastmoney.com/api/data/v1/get"
_EM_CB_LIST_QUOTE_COLS = (
    "f2~01~CONVERT_STOCK_CODE~CONVERT_STOCK_PRICE,"
    "f235~10~SECURITY_CODE~TRANSFER_PRICE,"
    "f236~10~SECURITY_CODE~TRANSFER_VALUE,"
    "f2~10~SECURITY_CODE~CURRENT_BOND_PRICE,"
    "f237~10~SECURITY_CODE~TRANSFER_PREMIUM_RATIO,"
    "f239~10~SECURITY_CODE~RESALE_TRIG_PRICE,"
    "f240~10~SECURITY_CODE~REDEEM_TRIG_PRICE,"
    "f23~01~CONVERT_STOCK_CODE~PBV_RATIO"
)

# Known mappings from Eastmoney API field names to our standard column names.
_EM_FIELD_MAP = {
    "SECURITY_CODE": "转债代码",
    "SECURITY_SHORT_NAME": "转债名称",
    "BOND_SHORT_NAME": "转债名称",
    "CONVERT_STOCK_CODE": "正股代码",
    "CONVERT_STOCK_NAME": "正股名称",
    "STOCK_SHORT_NAME": "正股名称",
    "CREDIT_RATING": "债券评级",
    "LISTING_DATE": "上市时间",
    "MATURITY_DATE": "到期时间",
    "EXPIRE_DATE": "到期时间",
    "CURR_ISS_AMT": "剩余规模",
    "CURRENT_BOND_PRICE": "转债现价",
    "CONVERT_STOCK_PRICE": "正股价",
    "TRANSFER_PRICE": "转股价",
    "TRANSFER_VALUE": "转股价值",
    "TRANSFER_PREMIUM_RATIO": "转股溢价率",
    "RESALE_TRIG_PRICE": "回售触发价",
    "REDEEM_TRIG_PRICE": "强赎触发价",
    "PBV_RATIO": "正股PB",
    "ISSUE_AMT": "发行规模",
    "PUBLIC_START_DATE": "申购日期",
    "CONVERT_START_DATE": "转股起始日",
}


def _fetch_em_cb_list_page(page: int, page_size: int = 500) -> dict:
    """Fetch one page from the Eastmoney RPT_BOND_CB_LIST API.
    Returns an empty dict on any network or parse error.
    """
    try:
        params = {
            "sortColumns": "PUBLIC_START_DATE",
            "sortTypes": "-1",
            "pageSize": str(page_size),
            "pageNumber": str(page),
            "reportName": "RPT_BOND_CB_LIST",
            "columns": "ALL",
            "quoteColumns": _EM_CB_LIST_QUOTE_COLS,
            "source": "WEB",
            "client": "WEB",
        }
        r = requests.get(_EM_CB_LIST_URL, params=params, timeout=15)
        r.raise_for_status()
        return r.json()
    except Exception:
        return {}


@st.cache_data(ttl=300)
def fetch_bond_all_list() -> pd.DataFrame:
    """
    Fetch ALL convertible bonds (active + historical) from Eastmoney RPT_BOND_CB_LIST.

    Returns a DataFrame with standardised column names including:
    转债代码, 转债名称, 正股代码, 正股名称, 债券评级, 上市时间,
    到期时间, 剩余规模, 转债现价, 正股价, 转股价, 转股价值,
    转股溢价率, 回售触发价, 强赎触发价, 正股PB.
    """
    try:
        first = _fetch_em_cb_list_page(1)
        result = first.get("result", {})
        if not result:
            return pd.DataFrame()

        total_pages = result.get("pages", 1)
        rows = result.get("data", [])

        for p in range(2, total_pages + 1):
            resp = _fetch_em_cb_list_page(p)
            rows.extend(resp.get("result", {}).get("data", []))

        if not rows:
            return pd.DataFrame()

        df = pd.DataFrame(rows)

        # Rename known fields to standard names
        rename = {}
        for col in df.columns:
            if col in _EM_FIELD_MAP and _EM_FIELD_MAP[col] not in rename.values():
                rename[col] = _EM_FIELD_MAP[col]
        df = df.rename(columns=rename)

        # Numeric conversions
        for col in ["剩余规模", "转债现价", "正股价", "转股价", "转股价值",
                    "转股溢价率", "回售触发价", "强赎触发价", "正股PB", "发行规模"]:
            if col in df.columns:
                df[col] = pd.to_numeric(df[col], errors="coerce")

        # Date conversions
        for col in ["上市时间", "到期时间", "申购日期", "转股起始日"]:
            if col in df.columns:
                df[col] = pd.to_datetime(df[col], errors="coerce")

        # Ensure 转债代码 is string
        if "转债代码" in df.columns:
            df["转债代码"] = df["转债代码"].astype(str).str.strip()

        return df

    except Exception:
        return pd.DataFrame()


@st.cache_data(ttl=120)
def fetch_bond_spot() -> pd.DataFrame:
    """
    Fetch convertible bond spot market data from Sina Finance.
    Returns DataFrame with columns: 代码, 名称, 现价, 涨跌幅.
    Only currently listed bonds appear in this feed.
    """
    try:
        df = ak.bond_zh_hs_cov_spot()
        if df is None or df.empty:
            return pd.DataFrame(columns=["代码", "名称", "现价", "涨跌幅"])

        col_map = {}
        for c in ["代码", "bond_id", "code", "symbol"]:
            if c in df.columns:
                col_map[c] = "代码"
                break
        for c in ["名称", "bond_name", "name"]:
            if c in df.columns:
                col_map[c] = "名称"
                break
        for c in ["最新价", "现价", "close", "price", "trade"]:
            if c in df.columns:
                col_map[c] = "现价"
                break
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
    Fetch CURRENTLY TRADING convertible bonds from Eastmoney comparison table.

    Uses bond_cov_comparison() which only returns bonds currently listed and
    actively traded (approximately 338 bonds as of April 2026).
    Does NOT fall back to bond_zh_cov() which contains historical bonds.

    Returns DataFrame with columns from bond_cov_comparison():
    序号, 转债代码, 转债名称, 转债最新价, 转债涨跌幅, 正股代码, 正股名称,
    正股最新价, 正股涨跌幅, 转股价, 转股价值, 转股溢价率, 纯债溢价率,
    回售触发价, 强赎触发价, 到期赎回价, 纯债价值, 开始转股日, 上市日期, 申购日期.
    """
    try:
        df = ak.bond_cov_comparison()
        if df is not None and not df.empty:
            return df.copy()
    except Exception:
        pass

    return pd.DataFrame()


@st.cache_data(ttl=120)
def fetch_bond_redeem() -> pd.DataFrame:
    """
    Fetch convertible bond forced-redemption progress data from JiSiLu.
    Returns DataFrame with redemption countdown info for bonds in the
    forced-redemption tracking period.
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
        clean_code = code.upper()
        for prefix in ("SH", "SZ", "BJ"):
            if clean_code.startswith(prefix):
                clean_code = clean_code[len(prefix):]
                break

        end_date = datetime.today()
        start_date = end_date - timedelta(days=days * 2)

        df = ak.stock_zh_a_hist(
            symbol=clean_code,
            period="daily",
            start_date=start_date.strftime("%Y%m%d"),
            end_date=end_date.strftime("%Y%m%d"),
            adjust="qfq",
        )
        if df is None or df.empty:
            return pd.DataFrame()

        col_map = {}
        for c in df.columns:
            if "日期" in c or c.lower() in ("date",):
                col_map[c] = "日期"
            elif "收盘" in c or c.lower() in ("close",):
                col_map[c] = "收盘"
        df = df.rename(columns=col_map)

        if "收盘" in df.columns:
            df["收盘"] = pd.to_numeric(df["收盘"], errors="coerce")

        if "日期" in df.columns:
            df = df.sort_values("日期").tail(days).reset_index(drop=True)

        return df
    except Exception:
        return pd.DataFrame()
