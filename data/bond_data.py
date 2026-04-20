"""
Data fetching module for convertible bonds using AKShare.
All functions are cached and return empty DataFrames on failure.

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

import logging
import re
import time

import akshare as ak
import pandas as pd
import requests
from requests.adapters import HTTPAdapter
import streamlit as st
from datetime import datetime, timedelta

logger = logging.getLogger(__name__)

# ── Common HTTP headers for direct API requests ──────────────────────────────
_HTTP_HEADERS = {
    "User-Agent": (
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
        "AppleWebKit/537.36 (KHTML, like Gecko) "
        "Chrome/120.0.0.0 Safari/537.36"
    ),
    "Referer": "https://data.eastmoney.com/",
    "Accept": "application/json, text/plain, */*",
    "Accept-Language": "zh-CN,zh;q=0.9,en;q=0.8",
}

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


def _fetch_em_cb_list_page(
    page: int,
    page_size: int = 500,
    *,
    include_quotes: bool = True,
) -> dict:
    """Fetch one page from the Eastmoney RPT_BOND_CB_LIST API.

    Parameters
    ----------
    page : int
        Page number (1-based).
    page_size : int
        Number of records per page.
    include_quotes : bool
        Whether to include real-time quote columns.  When the quote system
        is unavailable this can be set to False to fetch basic data only.

    Returns an empty dict on any network or parse error.
    """
    params: dict = {
        "sortColumns": "PUBLIC_START_DATE",
        "sortTypes": "-1",
        "pageSize": str(page_size),
        "pageNumber": str(page),
        "reportName": "RPT_BOND_CB_LIST",
        "columns": "ALL",
        "source": "WEB",
        "client": "WEB",
    }
    if include_quotes:
        params["quoteColumns"] = _EM_CB_LIST_QUOTE_COLS

    # Retry up to 3 times with exponential back-off
    for attempt in range(3):
        try:
            with requests.Session() as session:
                adapter = HTTPAdapter(pool_connections=1, pool_maxsize=1)
                session.mount("http://", adapter)
                session.mount("https://", adapter)
                r = session.get(
                    _EM_CB_LIST_URL,
                    params=params,
                    headers=_HTTP_HEADERS,
                    timeout=30,
                )
                r.raise_for_status()
                data = r.json()
                if data.get("result"):
                    return data
                # API returned a response but with no result – may be transient
                logger.warning(
                    "Eastmoney API page %d returned no result (attempt %d): %s",
                    page, attempt + 1, str(data)[:300],
                )
        except Exception as exc:
            logger.warning(
                "Eastmoney API page %d attempt %d failed: %s", page, attempt + 1, exc
            )
        if attempt < 2:
            time.sleep(1.5 * (attempt + 1))

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

        # Fallback: retry without quoteColumns if the first attempt got nothing
        if not result:
            logger.info("fetch_bond_all_list: retrying without quoteColumns")
            first = _fetch_em_cb_list_page(1, include_quotes=False)
            result = first.get("result", {})

        if not result:
            logger.error("fetch_bond_all_list: Eastmoney API returned no result")
            return pd.DataFrame()

        total_pages = result.get("pages", 1)
        rows = result.get("data", [])

        for p in range(2, total_pages + 1):
            resp = _fetch_em_cb_list_page(p)
            page_data = resp.get("result", {}).get("data", [])
            if not page_data:
                resp = _fetch_em_cb_list_page(p, include_quotes=False)
                page_data = resp.get("result", {}).get("data", [])
            rows.extend(page_data)

        if not rows:
            logger.error("fetch_bond_all_list: API returned 0 data rows")
            return pd.DataFrame()

        logger.info("fetch_bond_all_list: fetched %d rows across %d pages", len(rows), total_pages)
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

    except Exception as exc:
        logger.exception("fetch_bond_all_list failed: %s", exc)
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
            logger.warning("fetch_bond_spot: Sina API returned empty data")
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

        logger.info("fetch_bond_spot: fetched %d rows", len(df))
        return df
    except Exception as exc:
        logger.warning("fetch_bond_spot failed (non-critical): %s", exc)
        return pd.DataFrame(columns=["代码", "名称", "现价", "涨跌幅"])


@st.cache_data(ttl=120)
def fetch_bond_comparison() -> pd.DataFrame:
    """
    Fetch CURRENTLY TRADING convertible bonds from Eastmoney comparison table.

    Primary source: bond_cov_comparison() (Eastmoney push2 real-time API).
    Fallback source: RPT_BOND_CB_LIST datacenter API filtered to active bonds,
    used when the primary source is unreachable.

    Returns DataFrame with columns from bond_cov_comparison() when primary
    source is available, or standardised columns from the datacenter API when
    falling back.
    """
    # ── Primary source: AKShare bond_cov_comparison ───────────────────────
    try:
        df = ak.bond_cov_comparison()
        if df is not None and not df.empty:
            df = df.copy()
            # AKShare's fetch_paginated_data calls pd.to_numeric on the code
            # field (f3), so 转债代码 arrives as float (e.g. 110044.0).
            # Convert to a clean 6-digit string so it matches codes from
            # other data sources (RPT_BOND_CB_LIST returns plain strings).
            for col in ("转债代码", "正股代码"):
                if col in df.columns:
                    df[col] = (
                        df[col]
                        .apply(
                            lambda x: str(int(float(x))).zfill(6)
                            if pd.notna(x) and str(x).strip() not in ("", "-", "--")
                            else ""
                        )
                    )
            logger.info(
                "fetch_bond_comparison: primary source returned %d rows", len(df)
            )
            return df
    except Exception as exc:
        logger.warning("fetch_bond_comparison: primary source failed: %s", exc)

    # ── Fallback: Eastmoney datacenter RPT_BOND_CB_LIST (active bonds only) ─
    # Uses the same stable endpoint as fetch_bond_all_list(), filtered to
    # bonds with a positive remaining balance (currently listed bonds).
    logger.info("fetch_bond_comparison: trying fallback (RPT_BOND_CB_LIST)")
    for include_quotes in (True, False):
        try:
            first = _fetch_em_cb_list_page(1, include_quotes=include_quotes)
            result = first.get("result", {})
            if not result:
                if include_quotes:
                    logger.warning(
                        "fetch_bond_comparison fallback: no result with quotes, "
                        "retrying without"
                    )
                    continue
                logger.error(
                    "fetch_bond_comparison fallback: Eastmoney API returned no result"
                )
                return pd.DataFrame()

            total_pages = result.get("pages", 1)
            rows = result.get("data", [])
            for p in range(2, total_pages + 1):
                resp = _fetch_em_cb_list_page(
                    p, include_quotes=include_quotes
                )
                rows.extend(resp.get("result", {}).get("data", []))

            if not rows:
                if include_quotes:
                    continue
                logger.error("fetch_bond_comparison fallback: 0 data rows")
                return pd.DataFrame()

            df = pd.DataFrame(rows)

            # Rename using the standard field map
            rename = {}
            for col in df.columns:
                if col in _EM_FIELD_MAP and _EM_FIELD_MAP[col] not in rename.values():
                    rename[col] = _EM_FIELD_MAP[col]
            df = df.rename(columns=rename)

            # Numeric conversions for filtering
            for col in ["剩余规模", "转债现价", "转股价", "转股价值",
                        "转股溢价率", "回售触发价", "强赎触发价", "正股PB", "正股价"]:
                if col in df.columns:
                    df[col] = pd.to_numeric(df[col], errors="coerce")

            # Filter to currently active bonds: positive remaining balance
            if "剩余规模" in df.columns:
                df = df[df["剩余规模"].notna() & (df["剩余规模"] > 0)]
            elif "转债现价" in df.columns:
                df = df[df["转债现价"].notna() & (df["转债现价"] > 0)]

            if not df.empty:
                logger.info(
                    "fetch_bond_comparison fallback: %d active bonds "
                    "(quotes=%s)", len(df), include_quotes,
                )
                return df
        except Exception as exc:
            logger.warning(
                "fetch_bond_comparison fallback (quotes=%s) error: %s",
                include_quotes, exc,
            )

    logger.error("fetch_bond_comparison: all sources exhausted, returning empty")
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
            logger.warning("fetch_bond_redeem: JiSiLu API returned empty data")
            return pd.DataFrame()
        logger.info("fetch_bond_redeem: fetched %d rows", len(df))
        return df.copy()
    except Exception as exc:
        logger.warning("fetch_bond_redeem failed (non-critical): %s", exc)
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
