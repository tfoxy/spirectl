import type {
  CreateSts2ClientOptions,
  HotReloadResult,
  HotReloadStatusResult,
  LogsOptions,
  ScreenshotOptions,
  Sts2Client,
} from "./index.js";
import type { TestType } from "@playwright/test";

export interface Sts2FailureArtifactsOptions {
  enabled?: boolean;
  logs?: Pick<LogsOptions, "tail" | "limit" | "level" | "target" | "afterCursor">;
  screenshot?: Pick<ScreenshotOptions, "preset" | "width" | "height">;
}

export interface WithSts2Options {
  client?: CreateSts2ClientOptions;
  failureArtifacts?: Sts2FailureArtifactsOptions;
}

export interface HotReloadAndWaitOptions {
  project: string;
  build?: boolean;
  timeoutMs?: number;
  intervalMs?: number;
  expectGenerationChanged?: boolean;
}

export function hotReloadAndWait(
  sts2: Sts2Client,
  options: HotReloadAndWaitOptions,
): Promise<{
  before: HotReloadStatusResult;
  result: HotReloadResult;
  after: HotReloadStatusResult;
  generationChanged: boolean;
}>;

export function withSts2<TArgs extends {}, TWorkerArgs extends {}>(
  base: TestType<TArgs, TWorkerArgs>,
  options?: WithSts2Options,
): TestType<TArgs & { sts2: Sts2Client }, TWorkerArgs>;
