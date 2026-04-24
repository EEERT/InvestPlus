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

import functools
import logging
import random
import re
import time
from datetime import datetime, timedelta
from typing import Optional

import akshare as ak
import pandas as pd
import requests
from requests.adapters import HTTPAdapter

logger = logging.getLogger(__name__)


# ── Lightweight TTL cache (no external dependencies) ─────────────────────────

def _ttl_cache(ttl: float):
    """Decorator that caches the return value for *ttl* seconds.

    Unlike :func:`functools.lru_cache` this cache expires entries based on
    wall-clock time, which is useful for data-fetching functions that should
    return fresh results after a configurable interval.
    """
    def decorator(fn):
        _store: dict = {}

        @functools.wraps(fn)
        def wrapper(*args, **kwargs):
            key = (args, tuple(sorted(kwargs.items())))
            entry = _store.get(key)
            if entry is not None:
                result, ts = entry
                if time.monotonic() - ts < ttl:
                    return result
            result = fn(*args, **kwargs)
            _store[key] = (result, time.monotonic())
            return result

        return wrapper
    return decorator

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

# ── Anti-scraping: rotating User-Agent pool ───────────────────────────────────
_ROTATING_USER_AGENTS = [
    (
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
        "AppleWebKit/537.36 (KHTML, like Gecko) "
        "Chrome/120.0.0.0 Safari/537.36"
    ),
    (
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
        "AppleWebKit/537.36 (KHTML, like Gecko) "
        "Chrome/121.0.0.0 Safari/537.36 Edg/121.0.2277.98"
    ),
    (
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) "
        "AppleWebKit/537.36 (KHTML, like Gecko) "
        "Chrome/120.0.0.0 Safari/537.36"
    ),
    (
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:121.0) "
        "Gecko/20100101 Firefox/121.0"
    ),
    (
        "Mozilla/5.0 (X11; Linux x86_64) "
        "AppleWebKit/537.36 (KHTML, like Gecko) "
        "Chrome/120.0.0.0 Safari/537.36"
    ),
]

# ── Anti-scraping: module-level persistent session ───────────────────────────
# A single long-lived session preserves the cookies obtained by visiting the
# Eastmoney HTML page, which prevents the push2 API from detecting that
# consecutive requests come from an automated tool rather than a browser.
_em_push2_session: Optional[requests.Session] = None
_em_session_created_at: float = 0.0
_em_last_api_call: float = 0.0
_EM_SESSION_TTL: float = 600.0   # Recreate session after 10 minutes
_EM_MIN_INTERVAL: float = 3.5    # Minimum seconds between consecutive push2 calls


def _get_push2_session(force_new: bool = False) -> requests.Session:
    """Return a persistent requests.Session pre-warmed with Eastmoney cookies.

    On first call (or after TTL expiry) the function visits the Eastmoney
    bond-comparison HTML page so the session accumulates the same cookies a
    real browser would have before it hits the JSON API endpoint.  This
    prevents the anti-scraping layer from blocking the first API call.
    """
    global _em_push2_session, _em_session_created_at

    now = time.time()
    if (
        force_new
        or _em_push2_session is None
        or now - _em_session_created_at > _EM_SESSION_TTL
    ):
        session = requests.Session()
        adapter = HTTPAdapter(pool_connections=2, pool_maxsize=2, max_retries=0)
        session.mount("http://", adapter)
        session.mount("https://", adapter)

        ua = random.choice(_ROTATING_USER_AGENTS)
        session.headers.update(
            {
                "User-Agent": ua,
                "Accept": (
                    "text/html,application/xhtml+xml,application/xml;"
                    "q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8"
                ),
                "Accept-Language": "zh-CN,zh;q=0.9,en;q=0.8",
                "Accept-Encoding": "gzip, deflate, br",
                "Connection": "keep-alive",
                "DNT": "1",
                "Upgrade-Insecure-Requests": "1",
            }
        )

        # Visit the HTML comparison page to establish browser-like cookies.
        try:
            resp = session.get(
                "https://quote.eastmoney.com/center/fullscreenlist.html",
                timeout=15,
                allow_redirects=True,
            )
            logger.info(
                "_get_push2_session: warmed session, status=%d, cookies=%s",
                resp.status_code,
                list(session.cookies.keys()),
            )
            # Brief pause – mimics the time a human takes to read the page.
            time.sleep(random.uniform(1.5, 3.0))
        except Exception as exc:
            logger.warning("_get_push2_session: warm-up request failed: %s", exc)

        _em_push2_session = session
        _em_session_created_at = time.time()

    return _em_push2_session


def _fetch_bond_cov_comparison_human() -> pd.DataFrame:
    """Fetch the Eastmoney push2 convertible-bond comparison list while
    simulating human-like browsing to bypass anti-scraping measures.

    Eastmoney's push2 API blocks repeated requests that arrive without valid
    browser cookies or with too-short intervals between calls.  This function
    works around the restriction by:

    1. Keeping a **persistent** :class:`requests.Session` that retains the
       cookies obtained from the HTML page visit (see :func:`_get_push2_session`).
    2. Enforcing a minimum gap (``_EM_MIN_INTERVAL``) between consecutive API
       calls and adding random jitter to the wait time.
    3. Rotating the ``User-Agent`` header to reduce fingerprinting.
    4. Setting ``Referer`` and ``Sec-Fetch-*`` headers that match what a real
       browser sends when navigating from the HTML page to the JSON endpoint.
    """
    global _em_last_api_call

    # Enforce minimum interval between calls ─────────────────────────────────
    now = time.time()
    elapsed = now - _em_last_api_call
    if elapsed < _EM_MIN_INTERVAL:
        wait = _EM_MIN_INTERVAL - elapsed + random.uniform(0.5, 2.0)
        logger.info("_fetch_bond_cov_comparison_human: rate-limit wait %.1f s", wait)
        time.sleep(wait)

    session = _get_push2_session()

    url = "https://push2.eastmoney.com/api/qt/clist/get"
    api_headers = {
        "Referer": "https://quote.eastmoney.com/center/fullscreenlist.html",
        "Accept": "application/json, text/plain, */*",
        "Sec-Fetch-Dest": "empty",
        "Sec-Fetch-Mode": "cors",
        "Sec-Fetch-Site": "same-site",
    }

    # f-field → human-readable column name (mirrors AKShare's bond_cov_comparison)
    field_map = {
        "f1":   "序号",
        "f2":   "转债最新价",
        "f3":   "转债涨跌幅",
        "f12":  "转债代码",
        "f14":  "转债名称",
        "f227": "上市日期",
        "f229": "纯债价值",
        "f231": "正股最新价",
        "f232": "正股涨跌幅",
        "f234": "正股代码",
        "f236": "正股名称",
        "f237": "转股价",
        "f238": "转股价值",
        "f239": "转股溢价率",
        "f240": "纯债溢价率",
        "f241": "回售触发价",
        "f242": "强赎触发价",
        "f26":  "到期赎回价",
        "f243": "开始转股日",
    }
    fields_str = (
        "f1,f152,f2,f3,f12,f13,f14,f227,f228,f229,f230,f231,f232,f233,f234,"
        "f235,f236,f237,f238,f239,f240,f241,f242,f26,f243"
    )

    all_rows: list = []
    page = 1

    while True:
        params = {
            "pn": str(page),
            "pz": "100",
            "po": "1",
            "np": "1",
            "ut": "bd1d9ddb04089700cf9c27f6f7426281",
            "fltt": "2",
            "invt": "2",
            "fid": "f243",
            "fs": "b:MK0354",
            "fields": fields_str,
            "_": str(int(time.time() * 1000)),
        }
        try:
            _em_last_api_call = time.time()
            resp = session.get(url, params=params, headers=api_headers, timeout=30)
            resp.raise_for_status()
            data = resp.json()

            diff = (data.get("data") or {}).get("diff", [])
            if not diff:
                logger.warning(
                    "_fetch_bond_cov_comparison_human: page %d returned no diff",
                    page,
                )
                break

            all_rows.extend(diff)
            total = (data.get("data") or {}).get("total", 0)
            if len(all_rows) >= total:
                break

            page += 1
            # Human-like delay between pages
            time.sleep(random.uniform(1.0, 2.5))

        except Exception as exc:
            logger.warning(
                "_fetch_bond_cov_comparison_human: page %d failed: %s – "
                "invalidating session",
                page,
                exc,
            )
            # Drop the session so the next call creates a fresh one with new cookies.
            global _em_push2_session  # noqa: PLW0603
            _em_push2_session = None
            break

    if not all_rows:
        return pd.DataFrame()

    records = [
        {col_name: row.get(field) for field, col_name in field_map.items()}
        for row in all_rows
    ]
    df = pd.DataFrame(records)

    # Numeric conversions ──────────────────────────────────────────────────────
    for col in [
        "转债最新价", "转债涨跌幅", "正股最新价", "正股涨跌幅",
        "转股价", "转股价值", "转股溢价率", "纯债溢价率",
        "回售触发价", "强赎触发价", "纯债价值",
    ]:
        if col in df.columns:
            df[col] = pd.to_numeric(df[col], errors="coerce")

    # Code normalisation ───────────────────────────────────────────────────────
    for col in ("转债代码", "正股代码"):
        if col in df.columns:
            df[col] = df[col].apply(
                lambda x: str(int(float(x))).zfill(6)
                if pd.notna(x) and str(x).strip() not in ("", "-", "--")
                else ""
            )

    logger.info(
        "_fetch_bond_cov_comparison_human: fetched %d rows across %d page(s)",
        len(df),
        page,
    )
    return df

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


@_ttl_cache(ttl=300)
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


@_ttl_cache(ttl=120)
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


@_ttl_cache(ttl=120)
def fetch_bond_comparison() -> pd.DataFrame:
    """
    Fetch CURRENTLY TRADING convertible bonds from Eastmoney comparison table.

    Sources tried in order:
    1. Human-simulation push2 fetch (:func:`_fetch_bond_cov_comparison_human`)
       – persistent session + random delays bypass anti-scraping.
    2. AKShare ``bond_cov_comparison()`` – standard fallback if the above fails.
    3. RPT_BOND_CB_LIST datacenter API – stable fallback when push2 is down.

    Returns DataFrame with standardised column names.
    """
    # ── Primary source: human-simulation push2 fetch ─────────────────────
    try:
        df = _fetch_bond_cov_comparison_human()
        if df is not None and not df.empty:
            logger.info(
                "fetch_bond_comparison: human-sim source returned %d rows", len(df)
            )
            return df
    except Exception as exc:
        logger.warning(
            "fetch_bond_comparison: human-sim source failed: %s", exc
        )

    # ── Secondary source: AKShare bond_cov_comparison ────────────────────
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
                "fetch_bond_comparison: AKShare source returned %d rows", len(df)
            )
            return df
    except Exception as exc:
        logger.warning("fetch_bond_comparison: AKShare source failed: %s", exc)

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


@_ttl_cache(ttl=120)
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


@_ttl_cache(ttl=120)
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
