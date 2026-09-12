import { test, expect } from "bun:test";
import { CORE_VERSION } from "../src/index";

test("CORE_VERSION is defined", () => {
  expect(CORE_VERSION).toBe("0.1.0");
});
