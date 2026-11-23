import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { ConfigProvider, theme } from "antd";
import zhCN from "antd/locale/zh_CN";
import { Dashboard } from "./Dashboard";

const container = document.getElementById("root");
if (!container) {
  throw new Error("WebConsole root element is missing");
}

createRoot(container).render(
  <StrictMode>
    <ConfigProvider locale={zhCN} theme={{ algorithm: theme.darkAlgorithm, token: { colorPrimary: "#38bdf8" } }}>
      <Dashboard />
    </ConfigProvider>
  </StrictMode>,
);
