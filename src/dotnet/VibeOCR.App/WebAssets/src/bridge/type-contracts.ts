import type { AppAction } from "../app/types";
import type { AppRoute, BridgeEnvelope } from "./client";

// tsc 编译期契约：放宽桥接版本、路由或动作类型会使反例约束失败。
type Assert<T extends true> = T;
export type ValidVersion = Assert<
  2 extends BridgeEnvelope["version"] ? true : false
>;
export type InvalidVersion = Assert<
  1 extends BridgeEnvelope["version"] ? false : true
>;
export type ValidRoute = Assert<"settings" extends AppRoute ? true : false>;
export type InvalidRoute = Assert<"admin" extends AppRoute ? false : true>;
export type ValidAction = Assert<
  "recognition.copy" extends AppAction["type"] ? true : false
>;
export type InvalidAction = Assert<
  "recognition.deleteAll" extends AppAction["type"] ? false : true
>;
