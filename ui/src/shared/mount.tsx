import { StrictMode, type ReactElement } from "react";
import { createRoot } from "react-dom/client";
import { App as AntApp, ConfigProvider } from "antd";
import zhCN from "antd/locale/zh_CN";
import { consoleTheme } from "./theme";

/** Mounts one console entry with the shared theme, locale and message/notification context. */
export function mountConsole(element: ReactElement, rootId = "root"): void {
  const container = document.getElementById(rootId);
  if (!container) {
    throw new Error(`Console root element '#${rootId}' is missing`);
  }

  createRoot(container).render(
    <StrictMode>
      <ConfigProvider locale={zhCN} theme={consoleTheme}>
        <AntApp>{element}</AntApp>
      </ConfigProvider>
    </StrictMode>,
  );
}
