import { writeFile, rename } from "fs/promises";

/**
 * 原子写盘（2026-08-23 审计修复：账本/记忆 JSON 直接 writeFile 崩溃可截断成半截文件，
 * 下次 load 解析失败 → 账本降级重建/记忆丢失）。先写同目录 `.tmp` 再 rename 覆盖目标——
 * 同目录内 rename 是原子操作，Windows 上也允许替换已存在目标。
 * rename 失败保底抛错并打日志（不静默吞；残留的 .tmp 无害，下次写覆盖）。
 * 注意：不负责建父目录——调用方沿用各自原有的 mkdir 流程。
 */
export async function writeFileAtomic(filePath: string, data: string): Promise<void> {
  const tmpPath = `${filePath}.tmp`;
  await writeFile(tmpPath, data, "utf-8");
  try {
    await rename(tmpPath, filePath);
  } catch (err) {
    console.error(`[atomic-fs] rename failed: ${tmpPath} -> ${filePath}: ${(err as Error).message}`);
    throw err;
  }
}
