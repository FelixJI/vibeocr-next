import {
  webDarkTheme,
  webLightTheme,
  type Theme,
} from "@fluentui/react-components";

export const vibeLightTheme: Theme = {
  ...webLightTheme,
  colorBrandBackground: "#974600",
  colorBrandBackgroundHover: "#7f3b04",
  colorBrandBackgroundPressed: "#642f03",
  colorBrandForeground1: "#843d06",
  colorBrandForegroundLink: "#843d06",
  colorBrandForegroundLinkHover: "#642f03",
  colorBrandStroke1: "#974600",
  colorNeutralBackground1: "#ffffff",
  colorNeutralBackground2: "#f8f7f4",
  colorNeutralBackground3: "#f1f0eb",
  colorNeutralStroke1: "#dedcd5",
  colorNeutralForeground1: "#22221f",
  colorNeutralForeground2: "#62645e",
};

export const vibeDarkTheme: Theme = {
  ...webDarkTheme,
  colorBrandBackground: "#d66b20",
  colorBrandBackgroundHover: "#eb8140",
  colorBrandBackgroundPressed: "#b45112",
  colorBrandForeground1: "#ffad70",
  colorBrandForegroundLink: "#ffad70",
  colorBrandForegroundLinkHover: "#ffc29a",
  colorBrandStroke1: "#ff9d52",
  colorNeutralBackground1: "#20211e",
  colorNeutralBackground2: "#171815",
  colorNeutralBackground3: "#11120f",
  colorNeutralStroke1: "#3c3e37",
  colorNeutralForeground1: "#f2f2ee",
  colorNeutralForeground2: "#b4b6ad",
};
