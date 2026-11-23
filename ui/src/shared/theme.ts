import type { ThemeConfig } from "antd";
import { theme } from "antd";

/**
 * One visual language for both consoles (task-65): same primary colour, same spacing scale, same
 * dark surface. Two consoles must not drift into two designs.
 */
export const consoleTheme: ThemeConfig = {
  algorithm: theme.darkAlgorithm,
  token: {
    colorPrimary: "#38bdf8",
    colorInfo: "#38bdf8",
    borderRadius: 6,
    padding: 16,
    fontSize: 13,
    fontFamily:
      "-apple-system, BlinkMacSystemFont, 'Segoe UI', 'Noto Sans SC', 'PingFang SC', 'Microsoft YaHei', sans-serif",
    fontFamilyCode: "ui-monospace, SFMono-Regular, Menlo, Consolas, monospace",
  },
  components: {
    Layout: { bodyBg: "#0b1220", headerBg: "#0f172a" },
    Card: { headerFontSize: 14 },
    Table: { headerBg: "#111c2e", cellPaddingBlockSM: 6 },
  },
};
