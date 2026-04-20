"""
InvestPlus 投资监测助手 – Main Streamlit Application
"""

from __future__ import annotations

import io
import logging
from datetime import datetime

import pandas as pd
import streamlit as st

# ── Page configuration (must be first Streamlit call) ────────────────────────
st.set_page_config(
    page_title="InvestPlus 投资监测助手",
    page_icon="📈",
    layout="wide",
    initial_sidebar_state="expanded",
)

# ── Local imports ─────────────────────────────────────────────────────────────
from data.bond_data import (
    fetch_bond_all_list,
    fetch_bond_comparison,
    fetch_bond_redeem,
    fetch_bond_spot,
)
from utils.calculations import merge_bond_data, split_active_inactive


# ─────────────────────────────────────────────────────────────────────────────
# Helpers
# ─────────────────────────────────────────────────────────────────────────────

def highlight_rows(row: pd.Series):
    """
    Pandas Styler row-highlight function.
    - Yellow (#FFF3CD) : 10 < 强赎触发天数 < 15
    - Red   (#F8D7DA) : 回售触发天数 > 25
    """
    redeem_days = pd.to_numeric(row.get("强赎触发天数", None), errors="coerce")
    put_days = pd.to_numeric(row.get("回售触发天数", None), errors="coerce")

    if pd.notna(redeem_days) and 10 < redeem_days < 15:
        return ["background-color: #FFF3CD"] * len(row)
    if pd.notna(put_days) and put_days > 25:
        return ["background-color: #F8D7DA"] * len(row)
    return [""] * len(row)


def to_excel_bytes(df: pd.DataFrame) -> bytes:
    """Serialise DataFrame to Excel bytes using openpyxl."""
    buf = io.BytesIO()
    with pd.ExcelWriter(buf, engine="openpyxl") as writer:
        df.to_excel(writer, index=False, sheet_name="可转债数据")
    return buf.getvalue()


def _fmt_numeric(df: pd.DataFrame, cols: list[str], decimals: int = 2) -> pd.DataFrame:
    """Round numeric columns for display."""
    for col in cols:
        if col in df.columns:
            df[col] = pd.to_numeric(df[col], errors="coerce").round(decimals)
    return df


# Canonical rating order used both in the filter widget and the "其他" category logic.
_KNOWN_RATINGS = ["AAA", "AA+", "AA", "AA-", "A+", "A", "A-", "BBB+", "BBB"]
_RATING_OPTIONS = _KNOWN_RATINGS + ["其他"]


# ─────────────────────────────────────────────────────────────────────────────
# Sidebar – Navigation
# ─────────────────────────────────────────────────────────────────────────────

with st.sidebar:
    st.image("https://img.icons8.com/color/96/investment-portfolio.png", width=60)
    st.title("InvestPlus")
    st.markdown("---")
    page = st.radio(
        "导航",
        options=["中国A股", "可转债", "ETF基金"],
        index=1,
        label_visibility="collapsed",
    )


# ─────────────────────────────────────────────────────────────────────────────
# Page: 中国A股
# ─────────────────────────────────────────────────────────────────────────────

if page == "中国A股":
    st.title("🇨🇳 中国A股")
    st.info("功能开发中，敬请期待。", icon="🚧")


# ─────────────────────────────────────────────────────────────────────────────
# Page: ETF基金
# ─────────────────────────────────────────────────────────────────────────────

elif page == "ETF基金":
    st.title("📦 ETF基金")
    st.info("功能开发中，敬请期待。", icon="🚧")


# ─────────────────────────────────────────────────────────────────────────────
# Page: 可转债
# ─────────────────────────────────────────────────────────────────────────────

else:
    st.title("🔄 可转债监测")

    # ── Session state initialisation ─────────────────────────────────────────
    for key, default in [
        ("cb_data", pd.DataFrame()),
        ("cb_inactive", pd.DataFrame()),
        ("cb_last_update", None),
    ]:
        if key not in st.session_state:
            st.session_state[key] = default

    # Filter defaults (session-level cache – remembered for the duration of the session)
    _filter_defaults: dict = {
        "filter_price_min": 80.0,
        "filter_price_max": 200.0,
        "filter_premium_min": -50.0,
        "filter_premium_max": 100.0,
        "filter_years_min": 0.0,
        "filter_years_max": 8.0,
        "filter_scale_max": 500.0,
        "filter_cb_ratio_max": 100.0,
        "filter_ytm_min": -20.0,
        "filter_ratings": [],
        "filter_redeem_status": [],
    }
    for _fk, _fv in _filter_defaults.items():
        if _fk not in st.session_state:
            st.session_state[_fk] = _fv

    # ── Sidebar filters ───────────────────────────────────────────────────────
    with st.sidebar:
        st.markdown("---")
        st.subheader("🔍 筛选条件")

        # ── 价格区间 ──────────────────────────────────────────────────────────
        st.markdown("**💰 价格区间（元）**")
        _pc1, _pc2 = st.columns(2)
        price_min = _pc1.number_input(
            "最低 元",
            min_value=0.0,
            max_value=10000.0,
            step=1.0,
            format="%.1f",
            key="filter_price_min",
        )
        price_max = _pc2.number_input(
            "最高 元",
            min_value=0.0,
            max_value=10000.0,
            step=1.0,
            format="%.1f",
            key="filter_price_max",
        )
        if price_min > price_max:
            st.error("⚠️ 最小值不能大于最大值", icon="🚨")

        # ── 转股溢价率 ────────────────────────────────────────────────────────
        st.markdown("**📊 转股溢价率（%）**")
        _pr1, _pr2 = st.columns(2)
        premium_min = _pr1.number_input(
            "最低 %",
            min_value=-100.0,
            max_value=2000.0,
            step=1.0,
            format="%.1f",
            key="filter_premium_min",
        )
        premium_max = _pr2.number_input(
            "最高 %",
            min_value=-100.0,
            max_value=2000.0,
            step=1.0,
            format="%.1f",
            key="filter_premium_max",
        )
        if premium_min > premium_max:
            st.error("⚠️ 最小值不能大于最大值", icon="🚨")

        # ── 剩余年限 ──────────────────────────────────────────────────────────
        st.markdown("**📅 剩余年限（年）**")
        _yr1, _yr2 = st.columns(2)
        years_min = _yr1.number_input(
            "最低 年",
            min_value=0.0,
            max_value=30.0,
            step=0.25,
            format="%.2f",
            key="filter_years_min",
        )
        years_max = _yr2.number_input(
            "最高 年",
            min_value=0.0,
            max_value=30.0,
            step=0.25,
            format="%.2f",
            key="filter_years_max",
        )
        if years_min > years_max:
            st.error("⚠️ 最小值不能大于最大值", icon="🚨")

        # ── 剩余规模 ──────────────────────────────────────────────────────────
        st.markdown("**💹 剩余规模 ≤（亿元）**")
        scale_max = st.number_input(
            "最大规模 亿元",
            min_value=0.0,
            max_value=5000.0,
            step=0.5,
            format="%.1f",
            key="filter_scale_max",
            help="默认 500 = 不限；输入小于 500 的值时过滤生效",
        )

        # ── 转债占比 ──────────────────────────────────────────────────────────
        st.markdown("**📈 转债占比 ≤（%）**")
        cb_ratio_max = st.number_input(
            "最大转债占比 %",
            min_value=0.0,
            max_value=100.0,
            step=0.5,
            format="%.1f",
            key="filter_cb_ratio_max",
            help="默认 100% = 不限；输入小于 100 的值时过滤生效",
        )

        # ── 到期收益率 YTM ────────────────────────────────────────────────────
        st.markdown("**💰 到期收益率 YTM ≥（%）**")
        ytm_min = st.number_input(
            "最小 YTM %",
            min_value=-100.0,
            max_value=100.0,
            step=0.1,
            format="%.1f",
            key="filter_ytm_min",
            help="默认 -20 = 不限下限；输入大于 -20 的值时过滤生效",
        )

        # ── 债券评级 ──────────────────────────────────────────────────────────
        selected_ratings = st.multiselect(
            "🏦 债券评级",
            options=_RATING_OPTIONS,
            key="filter_ratings",
            placeholder="不限（全选）",
        )

        # ── 强赎状态 ──────────────────────────────────────────────────────────
        redeem_status_list = st.multiselect(
            "🔔 强赎状态",
            options=["接近强赎（10-15天）", "已触发强赎"],
            key="filter_redeem_status",
            placeholder="不限（全部）",
        )

        st.markdown("---")
        _count_placeholder = st.empty()
        if st.button("🔄 重置筛选", use_container_width=True):
            for _fk, _fv in _filter_defaults.items():
                st.session_state[_fk] = _fv
            st.rerun()

        st.markdown("---")
        auto_refresh = st.checkbox("自动刷新（120秒）", value=False)

    # ── Auto-refresh ──────────────────────────────────────────────────────────
    if auto_refresh:
        try:
            from streamlit_autorefresh import st_autorefresh  # type: ignore
            st_autorefresh(interval=125_000, key="cb_autorefresh")
        except ImportError:
            st.sidebar.warning("streamlit-autorefresh 未安装，自动刷新不可用。")

    # ── Controls row ─────────────────────────────────────────────────────────
    col_ctrl1, col_ctrl2, col_ctrl3 = st.columns([2, 2, 6])

    with col_ctrl1:
        load_btn = st.button("🔄 加载 / 刷新数据", use_container_width=True)
    with col_ctrl2:
        if st.session_state["cb_last_update"]:
            st.caption(f"最后更新：{st.session_state['cb_last_update']}")

    # ── Data loading ──────────────────────────────────────────────────────────
    if load_btn or st.session_state["cb_data"].empty:
        with st.spinner("正在获取数据，请稍候…"):
            if load_btn:
                fetch_bond_spot.clear()
                fetch_bond_comparison.clear()
                fetch_bond_redeem.clear()
                fetch_bond_all_list.clear()

            try:
                spot_df = fetch_bond_spot()
                comparison_df = fetch_bond_comparison()
                redeem_df = fetch_bond_redeem()
                detail_df = fetch_bond_all_list()

                merged = merge_bond_data(spot_df, comparison_df, redeem_df, detail_df)
                inactive = split_active_inactive(comparison_df, detail_df)

                st.session_state["cb_data"] = merged
                st.session_state["cb_inactive"] = inactive
                st.session_state["cb_last_update"] = datetime.now().strftime("%Y-%m-%d %H:%M:%S")
            except Exception as _load_err:
                logging.exception("数据加载异常")
                st.error("数据加载时发生意外错误，请检查网络连接后重试。")

        if not st.session_state["cb_data"].empty:
            st.success(f"数据加载成功，共 {len(st.session_state['cb_data'])} 条正在交易的可转债。")
        elif st.session_state["cb_last_update"]:
            # Data was attempted but resulted in empty – give a helpful hint
            st.error(
                "数据加载失败：未能获取到可转债列表。\n"
                "可能原因：行情数据源暂时不可用或网络连接异常。\n"
                "请稍后点击「加载 / 刷新数据」重试。"
            )

    df: pd.DataFrame = st.session_state["cb_data"].copy()

    if df.empty:
        st.info("请点击「加载 / 刷新数据」按钮获取数据。", icon="ℹ️")
        st.stop()

    # ── Apply filters ─────────────────────────────────────────────────────────
    _price_valid = price_min <= price_max
    _premium_valid = premium_min <= premium_max
    _years_valid = years_min <= years_max

    if "现价" in df.columns and _price_valid:
        df = df[
            df["现价"].between(price_min, price_max, inclusive="both")
            | df["现价"].isna()
        ]

    if "转股溢价率" in df.columns and _premium_valid:
        df = df[
            df["转股溢价率"].between(premium_min, premium_max, inclusive="both")
            | df["转股溢价率"].isna()
        ]

    if "剩余年限" in df.columns and _years_valid:
        df = df[
            df["剩余年限"].between(years_min, years_max, inclusive="both")
            | df["剩余年限"].isna()
        ]

    if "剩余规模" in df.columns and scale_max < 500.0:
        df = df[df["剩余规模"].isna() | (df["剩余规模"] <= scale_max)]

    if "转债占比" in df.columns and cb_ratio_max < 100.0:
        df = df[df["转债占比"].isna() | (df["转债占比"] <= cb_ratio_max)]

    if "到期税前收益" in df.columns and ytm_min > -20.0:
        df = df[df["到期税前收益"].isna() | (df["到期税前收益"] >= ytm_min)]

    if selected_ratings and "债券评级" in df.columns:
        has_other = "其他" in selected_ratings
        main_ratings = [r for r in selected_ratings if r != "其他"]
        mask = df["债券评级"].isin(main_ratings) if main_ratings else pd.Series(False, index=df.index)
        if has_other:
            mask = mask | (~df["债券评级"].isin(_KNOWN_RATINGS) & df["债券评级"].notna())
        df = df[mask]

    if "强赎触发天数" in df.columns and redeem_status_list:
        _redeem_masks = []
        if "接近强赎（10-15天）" in redeem_status_list:
            _redeem_masks.append(df["强赎触发天数"].between(10, 15, inclusive="both"))
        if "已触发强赎" in redeem_status_list:
            _redeem_masks.append(df["强赎触发天数"] >= 15)
        if _redeem_masks:
            _combined_mask = _redeem_masks[0]
            for _m in _redeem_masks[1:]:
                _combined_mask = _combined_mask | _m
            df = df[_combined_mask]

    # Reset sequential index after filtering
    df = df.reset_index(drop=True)
    if "序号" in df.columns:
        df["序号"] = range(1, len(df) + 1)

    # Update sidebar real-time count
    _count_placeholder.success(f"🎯 符合条件：**{len(df)}** 只")

    # ── Round numeric columns ─────────────────────────────────────────────────
    df = _fmt_numeric(
        df,
        ["现价", "正股价", "正股PB", "转股价", "转股价值", "转股溢价率",
         "回售触发价", "强赎触发价", "剩余年限", "剩余规模", "转债占比"],
    )
    df = _fmt_numeric(df, ["涨跌幅", "正股涨跌"], decimals=2)

    # ── Summary metrics ───────────────────────────────────────────────────────
    m1, m2, m3, m4 = st.columns(4)
    m1.metric("筛选后数量", len(df))

    if "转股溢价率" in df.columns:
        avg_premium = df["转股溢价率"].mean()
        m2.metric("平均转股溢价率", f"{avg_premium:.1f}%" if pd.notna(avg_premium) else "N/A")

    near_redeem = (
        int(df["强赎触发天数"].between(10, 15, inclusive="neither").sum())
        if "强赎触发天数" in df.columns
        else 0
    )
    m3.metric("接近强赎", near_redeem, help="强赎触发天数在10~15天之间")

    put_risk = (
        int((df["回售触发天数"] > 25).sum())
        if "回售触发天数" in df.columns
        else 0
    )
    m4.metric("回售风险", put_risk, help="回售触发天数 > 25天")

    st.markdown("---")

    # ── Column configuration for st.dataframe ────────────────────────────────
    column_config: dict = {}
    if "现价" in df.columns:
        column_config["现价"] = st.column_config.NumberColumn("现价 (元)", format="%.2f")
    if "涨跌幅" in df.columns:
        column_config["涨跌幅"] = st.column_config.NumberColumn("涨跌幅 (%)", format="%.2f")
    if "正股价" in df.columns:
        column_config["正股价"] = st.column_config.NumberColumn("正股价", format="%.2f")
    if "正股涨跌" in df.columns:
        column_config["正股涨跌"] = st.column_config.NumberColumn("正股涨跌 (%)", format="%.2f")
    if "正股PB" in df.columns:
        column_config["正股PB"] = st.column_config.NumberColumn("正股PB", format="%.2f")
    if "转股价值" in df.columns:
        column_config["转股价值"] = st.column_config.NumberColumn("转股价值", format="%.2f")
    if "转股溢价率" in df.columns:
        column_config["转股溢价率"] = st.column_config.NumberColumn("转股溢价率 (%)", format="%.2f")
    if "转债占比" in df.columns:
        column_config["转债占比"] = st.column_config.NumberColumn("转债占比 (%)", format="%.2f")
    if "回售触发天数" in df.columns:
        column_config["回售触发天数"] = st.column_config.NumberColumn("回售触发天数", format="%.0f 天")
    if "强赎触发天数" in df.columns:
        column_config["强赎触发天数"] = st.column_config.NumberColumn("强赎触发天数", format="%.0f 天")
    if "剩余年限" in df.columns:
        column_config["剩余年限"] = st.column_config.NumberColumn("剩余年限 (年)", format="%.2f")
    if "剩余规模" in df.columns:
        column_config["剩余规模"] = st.column_config.NumberColumn("剩余规模 (亿)", format="%.2f")
    if "到期时间" in df.columns:
        column_config["到期时间"] = st.column_config.DatetimeColumn("到期时间", format="YYYY-MM-DD")

    # ── Styled DataFrame ──────────────────────────────────────────────────────
    try:
        styled = df.style.apply(highlight_rows, axis=1)
        st.dataframe(
            styled,
            use_container_width=True,
            column_config=column_config,
            hide_index=True,
        )
    except Exception:
        st.dataframe(
            df,
            use_container_width=True,
            column_config=column_config,
            hide_index=True,
        )

    # ── Legend ────────────────────────────────────────────────────────────────
    st.caption(
        "🟡 黄色：强赎触发天数在10~15天之间（即将触发强赎）　"
        "🔴 红色：回售触发天数 > 25天（存在回售风险）"
    )

    # ── Export ────────────────────────────────────────────────────────────────
    st.markdown("---")
    export_col1, export_col2 = st.columns([2, 8])
    with export_col1:
        excel_bytes = to_excel_bytes(df)
        st.download_button(
            label="📥 导出 Excel",
            data=excel_bytes,
            file_name=f"可转债数据_{datetime.now().strftime('%Y%m%d_%H%M%S')}.xlsx",
            mime="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            use_container_width=True,
        )

    # ── Inactive / historical bonds table ────────────────────────────────────
    inactive_df: pd.DataFrame = st.session_state.get("cb_inactive", pd.DataFrame())
    if not inactive_df.empty:
        with st.expander(
            f"📋 历史及未上市可转债（{len(inactive_df)} 只）— 已强赎/退市/到期/未上市",
            expanded=False,
        ):
            st.info(
                "以下可转债当前不在交易所正常交易，包括已强赎、已退市、已到期或尚未上市的品种。",
                icon="ℹ️",
            )
            st.dataframe(
                inactive_df,
                use_container_width=True,
                hide_index=True,
            )
            inactive_excel = to_excel_bytes(inactive_df)
            st.download_button(
                label="📥 导出历史转债 Excel",
                data=inactive_excel,
                file_name=f"历史转债_{datetime.now().strftime('%Y%m%d_%H%M%S')}.xlsx",
                mime="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            )
    elif st.session_state["cb_last_update"]:
        st.caption("📋 未加载到历史转债数据（历史数据源暂不可用）")
