import { lazy, Suspense } from "react";
import { Card, Skeleton } from "antd";

// ECharts is heavy; it is split into its own chunk and loaded on demand so the initial bundle stays
// small (the console must open on a machine where everything else is already broken).
const LatencyChart = lazy(() => import("./LatencyChart"));

export function LatencyPanel() {
  return (
    <Card size="small" title="耗时分布（占位）">
      <Suspense fallback={<Skeleton active paragraph={{ rows: 3 }} title={false} />}>
        <LatencyChart />
      </Suspense>
    </Card>
  );
}
