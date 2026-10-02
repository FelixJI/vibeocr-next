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
  Camera,
  FileText,
  ListChecks,
  MoonStar,
  ScanLine,
  Settings,
  ShieldCheck,
  Sparkles,
} from "lucide-react";
import { NavLink, Outlet } from "react-router";

import type { AppActions, AppViewState, ThemePreference } from "../app/types";

interface AppShellProps {
  readonly viewState: AppViewState;
  readonly actions: AppActions;
  readonly theme: ThemePreference;
  readonly onThemeChange: (theme: ThemePreference) => void;
}

const primaryNavigation = [
  ["recognition", "单次识别", ScanLine],
  ["batch", "批量识别", ListChecks],
  ["qrcode", "二维码与条码", Sparkles],
  ["pdf", "PDF", FileText],
] as const;

const utilityNavigation = [
  ["settings", "设置", Settings],
  // 关于与诊断合并后的唯一常规入口；旧 #/about 路由在 App 路由层重定向到这里。
  ["diagnostics", "关于与诊断", ShieldCheck],
] as const;

function NavigationItems({
  items,
  navigate,
  connected,
}: {
  readonly items: typeof primaryNavigation | typeof utilityNavigation;
  readonly navigate: AppActions["navigate"];
  readonly connected: boolean;
}) {
  return items.map(([route, label, Icon]) => (
    <NavLink
      aria-label={label}
      className={({ isActive }) =>
        `navigation-link${isActive ? " is-active" : ""}`
      }
      key={route}
      onClick={(event) => {
        if (connected) event.preventDefault();
        navigate(route);
      }}
      title={label}
      to={`/${route}`}
    >
      <Icon aria-hidden="true" size={18} strokeWidth={1.8} />
      <span>{label}</span>
    </NavLink>
  ));
}

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
          <img className="brand-mark" src="./vibeocr-64.png" alt="" />
          <span>VibeOCR</span>
        </div>
        <nav className="navigation-list">
          <NavigationItems
            items={primaryNavigation}
            navigate={actions.navigate}
            connected={viewState.connected}
          />
        </nav>
        <nav
          className="navigation-list navigation-utility"
          aria-label="应用与帮助"
        >
          <NavigationItems
            items={utilityNavigation}
            navigate={actions.navigate}
            connected={viewState.connected}
          />
        </nav>
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
                  icon={<MoonStar aria-hidden="true" size={17} />}
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
          {viewState.route !== "recognition" && (
            <Button
              appearance="primary"
              disabled={!viewState.capabilities.includes("recognition.capture")}
              icon={<Camera aria-hidden="true" size={16} />}
              onClick={() => actions.run({ type: "recognition.captureScreen" })}
            >
              截图识别
            </Button>
          )}
        </Toolbar>
        <main className="page-region">
          <Outlet />
        </main>
      </div>
    </div>
  );
}
