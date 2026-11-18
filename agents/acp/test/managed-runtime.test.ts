import { describe, expect, test } from "bun:test";
import { spawnSync } from "node:child_process";
import { existsSync, mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { ManagedAcpRuntime, scrubbedEnv } from "../src/managed-runtime.js";
import type { AcpChannelConfig } from "../src/config.js";

function config(overrides: Partial<AcpChannelConfig["acp"]> = {}): AcpChannelConfig {
  return {
    nats: { url: "nats://127.0.0.1:4222" },
    agent: { owner: "test", session: "managed", subjectToken: "grok", heartbeatIntervalS: 1, keepaliveIntervalS: 1 },
    acp: {
      mode: "managed",
      preset: "grok",
      agentId: "grok",
      bin: "grok",
      args: ["agent", "stdio"],
      homeEnvVar: "GROK_HOME",
      cwd: process.cwd(),
      permissionPolicy: "reject",
      ...overrides,
    },
  };
}

const fixture = { command: "bun", args: ["scripts/fake-acp-agent.ts"] } as const;

describe("ManagedAcpRuntime", () => {
  test("defers isolated home creation until start and removes it on close", async () => {
    const runtime = new ManagedAcpRuntime({ config: config(), ...fixture });
    expect(runtime.agentHome).toBeUndefined();
    try {
      await runtime.start();
      const home = runtime.agentHome;
      expect(home).toBeDefined();
      expect(existsSync(home!)).toBe(true);
    } finally {
      const home = runtime.agentHome;
      await runtime.close();
      if (home) expect(existsSync(home)).toBe(false);
    }
  });

  test("creates a user-supplied agent home on start and does not delete it on close", async () => {
    const root = mkdtempSync(join(tmpdir(), "managed-acp-user-home-test-"));
    const agentHome = join(root, "existing-auth-home");
    const runtime = new ManagedAcpRuntime({ config: config({ agentHome }), ...fixture });
    expect(runtime.agentHome).toBe(agentHome);
    expect(existsSync(agentHome)).toBe(false);
    try {
      await runtime.start();
      expect(existsSync(agentHome)).toBe(true);
    } finally {
      await runtime.close();
      expect(existsSync(agentHome)).toBe(true);
      rmSync(root, { recursive: true, force: true });
    }
  });

  test("streams text chunks, drops thoughts, surfaces tool calls as status", async () => {
    const runtime = new ManagedAcpRuntime({ config: config(), ...fixture });
    try {
      await runtime.start();
      const events = [];
      for await (const event of runtime.prompt({ prompt: "hello", publicSession: "managed", permissionPolicy: "reject" })) events.push(event);
      const text = events.filter((e) => e.type === "response").map((e) => (e as { text: string }).text).join("");
      expect(text).toBe("fake ACP response to hello");
      expect(text).not.toContain("pondering");
      expect(events.some((e) => e.type === "response" && (e as { text: string }).text === "")).toBe(false);
      expect(events.some((e) => e.type === "status" && (e as { text: string }).text.includes("tool: echo fixture tool"))).toBe(true);
      expect(events.at(-1)).toEqual({ type: "done" });
    } finally {
      await runtime.close();
    }
  });

  test("defaults permission requests to deny (reject_once)", async () => {
    const runtime = new ManagedAcpRuntime({ config: config(), ...fixture, permissionTimeoutMs: 100 });
    try {
      await runtime.start();
      const chunks: string[] = [];
      for await (const event of runtime.prompt({ prompt: "permission", publicSession: "managed", permissionPolicy: "reject" })) {
        if (event.type === "response") chunks.push(event.text);
      }
      expect(chunks.join("")).toContain("permission:reject-once");
    } finally {
      await runtime.close();
    }
  });

  test("query policy relays approval through the adapter callback", async () => {
    const runtime = new ManagedAcpRuntime({ config: config({ permissionPolicy: "query" }), ...fixture, permissionTimeoutMs: 100 });
    try {
      await runtime.start();
      const chunks: string[] = [];
      for await (const event of runtime.prompt({
        prompt: "permission",
        publicSession: "managed",
        permissionPolicy: "query",
        askPermission: async () => "approve",
      })) {
        if (event.type === "response") chunks.push(event.text);
      }
      expect(chunks.join("")).toContain("permission:allow-once");
    } finally {
      await runtime.close();
    }
  });

  test("query timeout degrades to the cancelled outcome", async () => {
    const runtime = new ManagedAcpRuntime({ config: config({ permissionPolicy: "query" }), ...fixture, permissionTimeoutMs: 50 });
    try {
      await runtime.start();
      const chunks: string[] = [];
      for await (const event of runtime.prompt({
        prompt: "permission",
        publicSession: "managed",
        permissionPolicy: "query",
        askPermission: () => new Promise(() => { /* never answers */ }),
      })) {
        if (event.type === "response") chunks.push(event.text);
      }
      expect(chunks.join("")).toContain("permission:cancelled");
    } finally {
      await runtime.close();
    }
  });

  test("scrubbed env vars never reach the spawned agent (case-insensitive)", () => {
    const base = { ...process.env, COPILOT_ALLOW_ALL: "true", copilot_allow_all: "1", KEEP_ME: "yes" };
    const overrides = scrubbedEnv(["COPILOT_ALLOW_ALL"], base);
    const child = spawnSync("bun", ["-e", "console.log(JSON.stringify(Object.keys(process.env).filter((k) => /allow_all|keep_me/i.test(k))))"], {
      env: { ...base, ...overrides },
      encoding: "utf8",
    });
    expect(JSON.parse(child.stdout.trim())).toEqual(["KEEP_ME"]);
    expect(scrubbedEnv(undefined, base)).toEqual({});
  });

  test("agent-side prompt errors propagate for handler 500 coverage", async () => {
    const runtime = new ManagedAcpRuntime({ config: config(), ...fixture });
    try {
      await runtime.start();
      // Explicit try/catch: bun 1.4's `expect(...).rejects/toThrow` hangs on
      // this async-generator rejection even though the runtime rejects promptly.
      let error: unknown;
      try {
        for await (const _event of runtime.prompt({ prompt: "explode", publicSession: "managed", permissionPolicy: "reject" })) {
          // drain
        }
      } catch (err) {
        error = err;
      }
      expect(error).toBeInstanceOf(Error);
      expect((error as Error).message).toContain("fake ACP agent exploded");
    } finally {
      await runtime.close();
    }
  });
});
