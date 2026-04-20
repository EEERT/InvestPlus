"""
InvestPlus 投资监测助手 – Main Streamlit Application
"""

from __future__ import annotations

import io
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
from data.bond_data import fetch_bond_comparison, fetch_bond_redeem, fetch_bond_spot
from utils.calculations import merge_bond_data


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
    if "cb_data" not in st.session_state:
        st.session_state["cb_data"] = pd.DataFrame()
    if "cb_last_update" not in st.session_state:
        st.session_state["cb_last_update"] = None

    # ── Sidebar filters ───────────────────────────────────────────────────────
    with st.sidebar:
        st.markdown("---")
        st.subheader("🔍 筛选条件")

        price_range = st.slider(
            "现价范围（元）",
            min_value=80.0,
            max_value=300.0,
            value=(80.0, 200.0),
            step=1.0,
        )
        premium_range = st.slider(
            "转股溢价率（%）",
            min_value=-50.0,
            max_value=200.0,
            value=(-50.0, 80.0),
            step=1.0,
        )
        remaining_years = st.slider(
            "剩余年限（年）",
            min_value=0.0,
            max_value=8.0,
            value=(0.0, 8.0),
            step=0.5,
        )
        rating_options = ["AAA", "AA+", "AA", "AA-", "A+", "A", "A-", "BBB+", "BBB", "其他"]
        selected_ratings = st.multiselect(
            "债券评级",
            options=rating_options,
            default=[],
            placeholder="不限",
        )
        redeem_status = st.selectbox(
            "强赎状态",
            options=["全部", "接近强赎（10-15天）", "已触发强赎"],
            index=0,
        )

        st.markdown("---")
        auto_refresh = st.checkbox("自动刷新（120秒）", value=False)

    # ── Auto-refresh ──────────────────────────────────────────────────────────
    if auto_refresh:
        try:
            from streamlit_autorefresh import st_autorefresh  # type: ignore
            st_autorefresh(interval=120_000, key="cb_autorefresh")
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
            # Clear caches to force refresh when button is clicked
            if load_btn:
                fetch_bond_spot.clear()
                fetch_bond_comparison.clear()
                fetch_bond_redeem.clear()

            spot_df = fetch_bond_spot()
            comparison_df = fetch_bond_comparison()
            redeem_df = fetch_bond_redeem()

            merged = merge_bond_data(spot_df, comparison_df, redeem_df)
            st.session_state["cb_data"] = merged
            st.session_state["cb_last_update"] = datetime.now().strftime("%Y-%m-%d %H:%M:%S")

        if st.session_state["cb_data"].empty:
            st.error("数据加载失败，请检查网络连接后重试。")
        else:
            st.success(f"数据加载成功，共 {len(st.session_state['cb_data'])} 条记录。")

    df: pd.DataFrame = st.session_state["cb_data"].copy()

    if df.empty:
        st.info("请点击「加载 / 刷新数据」按钮获取数据。", icon="ℹ️")
        st.stop()

    # ── Apply filters ─────────────────────────────────────────────────────────
    if "现价" in df.columns:
        df = df[
            df["现价"].between(price_range[0], price_range[1], inclusive="both")
            | df["现价"].isna()
        ]

    if "转股溢价率" in df.columns:
        df = df[
            df["转股溢价率"].between(premium_range[0], premium_range[1], inclusive="both")
            | df["转股溢价率"].isna()
        ]

    if "剩余年限" in df.columns:
        df = df[
            df["剩余年限"].between(remaining_years[0], remaining_years[1], inclusive="both")
            | df["剩余年限"].isna()
        ]

    if selected_ratings and "债券评级" in df.columns:
        df = df[df["债券评级"].isin(selected_ratings)]

    if redeem_status == "接近强赎（10-15天）" and "强赎触发天数" in df.columns:
        df = df[df["强赎触发天数"].between(10, 15, inclusive="neither")]
    elif redeem_status == "已触发强赎" and "强赎触发天数" in df.columns:
        df = df[df["强赎触发天数"] >= 15]

    # Reset sequential index after filtering
    df = df.reset_index(drop=True)
    if "序号" in df.columns:
        df["序号"] = range(1, len(df) + 1)

    # ── Round numeric columns ─────────────────────────────────────────────────
    df = _fmt_numeric(
        df,
        ["现价", "正股价", "正股PB", "转股价", "转股价值", "转股溢价率", "回售触发价", "强赎触发价", "剩余年限", "剩余规模"],
    )
    df = _fmt_numeric(df, ["涨跌幅", "正股涨跌"], decimals=2)

    # ── Summary metrics ───────────────────────────────────────────────────────
    m1, m2, m3, m4 = st.columns(4)
    m1.metric("可转债数量", len(df))

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
    if "转股价值" in df.columns:
        column_config["转股价值"] = st.column_config.NumberColumn("转股价值", format="%.2f")
    if "转股溢价率" in df.columns:
        column_config["转股溢价率"] = st.column_config.NumberColumn("转股溢价率 (%)", format="%.2f")
    if "回售触发天数" in df.columns:
        column_config["回售触发天数"] = st.column_config.NumberColumn("回售触发天数", format="%d 天")
    if "强赎触发天数" in df.columns:
        column_config["强赎触发天数"] = st.column_config.NumberColumn("强赎触发天数", format="%d 天")
    if "剩余年限" in df.columns:
        column_config["剩余年限"] = st.column_config.NumberColumn("剩余年限 (年)", format="%.2f")
    if "剩余规模" in df.columns:
        column_config["剩余规模"] = st.column_config.NumberColumn("剩余规模 (亿)", format="%.2f")

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
        # Fallback without styling if styler fails
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
