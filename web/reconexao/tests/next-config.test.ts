import { describe, expect, it } from "vitest";
import config from "../next.config";

describe("next.config headers", () => {
  it("Referrer-Policy é same-origin (no-referrer faria o POST sair com Origin: null)", async () => {
    const rules = await config.headers!();
    const headers = rules.flatMap((r) => r.headers);
    const policy = headers.filter((h) => h.key === "Referrer-Policy");
    expect(policy).toEqual([{ key: "Referrer-Policy", value: "same-origin" }]);
  });
});
