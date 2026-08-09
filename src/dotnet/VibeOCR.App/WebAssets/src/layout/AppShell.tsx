import {
  Badge,
  Button,
  Menu,
  MenuItemRadio,
  MenuList,
  MenuPopover,
  MenuTrigger,
  Toolbar,
  ToolbarButton,
  ToolbarDivider,
  Tooltip,
} from "@fluentui/react-components";
import {
  InfoRegular,
  AppsListDetailRegular,
  BarcodeScannerRegular,
  DocumentPdfRegular,
  ImageRegular,
  SettingsRegular,
  ShieldTaskRegular,
  WeatherMoonRegular,
} from "@fluentui/react-icons";
import { NavLink, Outlet } from "react-router";

import type { AppActions, AppViewState, ThemePreference } from "../app/types";

interface AppShellProps {
  readonly viewState: AppViewState;
  readonly actions: AppActions;
  readonly theme: ThemePreference;
  readonly onThemeChange: (theme: ThemePreference) => void;
}

const navigation = [
  ["recognition", "单次识别", ImageRegular],
  ["batch", "批量识别", AppsListDetailRegular],
  ["qrcode", "二维码", BarcodeScannerRegular],
  ["pdf", "PDF", DocumentPdfRegular],
  ["settings", "设置", SettingsRegular],
  ["about", "关于", InfoRegular],
  ["diagnostics", "诊断与修复", ShieldTaskRegular],
] as const;

export function AppShell({
  viewState,
  actions,
  theme,
  onThemeChange,
}: AppShellProps) {
  return (
    <div className="app-shell">
      <aside className="navigation-rail" aria-label="主导航">
        <div className="brand-lockup">
          <span className="brand-mark" aria-hidden="true">
            V
          </span>
          <span>VibeOCR</span>
        </div>
        <nav className="navigation-list">
          {navigation.map(([route, label, Icon]) => (
            <NavLink
              aria-label={label}
              className={({ isActive }) =>
                `navigation-link${isActive ? " is-active" : ""}`
              }
              key={route}
              onClick={() => actions.navigate(route)}
              to={`/${route}`}
            >
              <Icon aria-hidden="true" />
              <span>{label}</span>
            </NavLink>
          ))}
        </nav>
        <div className="navigation-footnote">本地 OCR 工作台</div>
      </aside>
      <div className="shell-content">
        <Toolbar aria-label="应用命令" className="app-toolbar">
          <Badge appearance="tint" color="informative" shape="rounded">
            {viewState.runtimeLabel}
          </Badge>
          <ToolbarDivider />
          <span className="toolbar-context">本地处理 · 不上传至网页服务</span>
          <div className="toolbar-spacer" />
          <Menu
            checkedValues={{ theme: [theme] }}
            onCheckedValueChange={(_, data) =>
              onThemeChange(data.checkedItems[0] as ThemePreference)
            }
          >
            <MenuTrigger disableButtonEnhancement>
              <Tooltip content="切换主题" relationship="label">
                <ToolbarButton
                  aria-label="切换主题"
                  icon={<WeatherMoonRegular />}
                />
              </Tooltip>
            </MenuTrigger>
            <MenuPopover>
              <MenuList>
                <MenuItemRadio name="theme" value="system">
                  跟随系统
                </MenuItemRadio>
                <MenuItemRadio name="theme" value="light">
                  浅色
                </MenuItemRadio>
                <MenuItemRadio name="theme" value="dark">
                  深色
                </MenuItemRadio>
              </MenuList>
            </MenuPopover>
          </Menu>
          <Button
            appearance="primary"
            disabled={!viewState.capabilities.includes("recognition.capture")}
            onClick={() => actions.run({ type: "recognition.captureScreen" })}
          >
            截图识别
          </Button>
        </Toolbar>
        <main className="page-region">
          <Outlet />
        </main>
      </div>
    </div>
  );
}
