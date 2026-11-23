import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { ConfigProvider, theme } from "antd";
import zhCN from "antd/locale/zh_CN";
import { ConversationConsole } from "./ConversationConsole";

const container = document.getElementById("root");
if (!container) {
  throw new Error("ConversationConsole root element is missing");
}

createRoot(container).render(
  <StrictMode>
    <ConfigProvider locale={zhCN} theme={{ algorithm: theme.darkAlgorithm }}>
      <ConversationConsole />
    </ConfigProvider>
  </StrictMode>,
);
