/** 筛选检查总预算：RGB 分块/样本与额外 alpha 分块检查合计 ≤96M。 */
export const DUPLICATE_WORK_BUDGET: number = 96_000_000;
/** 精比阶段逐像素比较总量 ≤ 32M：相似位置过多时显式拒绝。 */
export const DUPLICATE_PRECISE_BUDGET = 32_000_000;
