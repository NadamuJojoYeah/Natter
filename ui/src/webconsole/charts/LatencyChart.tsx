import { useEffect, useRef } from "react";
import * as echarts from "echarts/core";
import { BarChart } from "echarts/charts";
import { GridComponent, TooltipComponent } from "echarts/components";
import { CanvasRenderer } from "echarts/renderers";

echarts.use([BarChart, GridComponent, TooltipComponent, CanvasRenderer]);

const OPTION: echarts.EChartsCoreOption = {
  backgroundColor: "transparent",
  grid: { left: 40, right: 16, top: 16, bottom: 24 },
  tooltip: { trigger: "axis" },
  xAxis: { type: "category", data: ["started", "step", "tool", "final"], axisLabel: { color: "#94a3b8" } },
  yAxis: { type: "value", axisLabel: { color: "#94a3b8" }, splitLine: { lineStyle: { color: "#1f2937" } } },
  series: [
    {
      type: "bar",
      data: [12, 45, 380, 8],
      itemStyle: { color: "#38bdf8", borderRadius: [3, 3, 0, 0] },
    },
  ],
};

/** Placeholder chart proving the ECharts + lazy-chunk pipeline works inside CSP `script-src 'self'`. */
export default function LatencyChart() {
  const host = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!host.current) {
      return undefined;
    }

    const chart = echarts.init(host.current, "dark", { renderer: "canvas" });
    chart.setOption(OPTION);

    const observer = new ResizeObserver(() => chart.resize());
    observer.observe(host.current);

    return () => {
      observer.disconnect();
      chart.dispose();
    };
  }, []);

  return <div ref={host} style={{ height: 220 }} aria-label="latency placeholder" />;
}
