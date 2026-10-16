import { afterEach, beforeEach, vi } from "vitest";
import { resetEnvCacheForTests } from "@/lib/env";
import { clearRegisteredSecrets } from "@/lib/redact";
import { TEST_ENV } from "./test-env";

beforeEach(() => {
  for (const [k, v] of Object.entries(TEST_ENV)) vi.stubEnv(k, v);
  resetEnvCacheForTests();
  clearRegisteredSecrets();
});

afterEach(() => {
  vi.unstubAllEnvs();
  vi.unstubAllGlobals();
});
