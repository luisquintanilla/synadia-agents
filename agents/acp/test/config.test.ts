import { describe, expect, test } from "bun:test";
import { DEFAULT_CONFIG_PATH, loadConfigFromSources, parseArgs, renderConfigTemplate } from "../src/config.js";

const base = { argv: ["start"], env: { USER: "alice" }, readFile: () => "", cwd: "/tmp/project-main" };

describe("config", () => {
  test("parses CLI flags", () => {
    const flags = parseArgs(["start", "--owner", "alice", "--session", "project-main", "--agent", "custom", "--mode", "fake", "--permission-policy", "reject", "--sender-identity", "signed", "--min-sender-trust", "signed"]);
    expect(flags.command).toBe("start");
    expect(flags.owner).toBe("alice");
    expect(flags.session).toBe("project-main");
    expect(flags.agent).toBe("custom");
    expect(flags.mode).toBe("fake");
    expect(flags.permissionPolicy).toBe("reject");
    expect(flags.senderIdentity).toBe("signed");
    expect(flags.minSenderTrust).toBe("signed");
  });

  test("defaults to the grok preset", () => {
    const cfg = loadConfigFromSources(base);
    expect(cfg.acp.preset).toBe("grok");
    expect(cfg.acp.agentId).toBe("grok");
    expect(cfg.agent.subjectToken).toBe("grok");
    expect(cfg.acp.bin).toBe("grok");
    expect(cfg.acp.args).toEqual(["agent", "stdio"]);
    expect(cfg.acp.homeEnvVar).toBe("GROK_HOME");
    expect(cfg.acp.mode).toBe("fake");
    expect(cfg.acp.permissionPolicy).toBe("reject");
    expect(cfg.agent.owner).toBe("alice");
    expect(cfg.agent.session).toBe("project-main");
    expect(cfg.nats.senderIdentity).toBe("off");
    expect(cfg.agent.minSenderTrust).toBe("any");
  });

  test("unknown preset is rejected with the available list", () => {
    expect(() => loadConfigFromSources({ ...base, argv: ["start", "--agent", "clippy"] })).toThrow("acp.agent must be one of");
  });

  test("custom preset requires agent id and bin", () => {
    expect(() => loadConfigFromSources({ ...base, argv: ["start", "--agent", "custom"] })).toThrow("--agent-id");
    expect(() => loadConfigFromSources({ ...base, argv: ["start", "--agent", "custom", "--agent-id", "myagent"] })).toThrow("--acp-bin");
    const cfg = loadConfigFromSources({ ...base, argv: ["start", "--agent", "custom", "--agent-id", "myagent", "--acp-bin", "my-agent", "--acp-args", "acp --stdio"] });
    expect(cfg.acp.agentId).toBe("myagent");
    expect(cfg.agent.subjectToken).toBe("myagent");
    expect(cfg.acp.bin).toBe("my-agent");
    expect(cfg.acp.args).toEqual(["acp", "--stdio"]);
  });

  test("per-agent env beats channel env beats fleet env", () => {
    const cfg = loadConfigFromSources({
      ...base,
      env: {
        USER: "alice",
        SYNADIA_OWNER: "fleet-owner",
        SYNADIA_ACP_OWNER: "channel-owner",
        SYNADIA_GROK_OWNER: "grok-owner",
        SYNADIA_NAME: "fleet-session",
        SYNADIA_ACP_SESSION: "channel-session",
      },
    });
    expect(cfg.agent.owner).toBe("grok-owner");
    expect(cfg.agent.session).toBe("channel-session");
    const fleetOnly = loadConfigFromSources({ ...base, env: { USER: "alice", SYNADIA_OWNER: "fleet-owner", SYNADIA_NAME: "fleet-session" } });
    expect(fleetOnly.agent.owner).toBe("fleet-owner");
    expect(fleetOnly.agent.session).toBe("fleet-session");
  });

  test("derives the default owner from USERNAME when USER is unset (Windows)", () => {
    const cfg = loadConfigFromSources({ ...base, env: { USERNAME: "Bob.Smith" } });
    expect(cfg.agent.owner).toBe("bob-smith");
    expect(loadConfigFromSources({ ...base, env: {} }).agent.owner).toBe("unknown");
  });

  test("CLI overrides env, file, and defaults", () => {
    const file = `[nats]\nurl = "nats://file:4222"\n\n[agent]\nowner = "file-owner"\nsession = "file-session"\n\n[acp]\nmode = "managed"\npermission_policy = "allow"\n`;
    const cfg = loadConfigFromSources({
      argv: ["start", "--owner", "cli-owner", "--session", "cli-session", "--mode", "fake", "--permission-policy", "reject"],
      env: {
        USER: "env-user",
        SYNADIA_GROK_OWNER: "env-owner",
        SYNADIA_ACP_MODE: "managed",
        SYNADIA_ACP_PERMISSION_POLICY: "query",
      },
      readFile: () => file,
      cwd: "/tmp/project-name",
    });
    expect(cfg.agent.owner).toBe("cli-owner");
    expect(cfg.agent.session).toBe("cli-session");
    expect(cfg.acp.mode).toBe("fake");
    expect(cfg.acp.permissionPolicy).toBe("reject");
    expect(cfg.nats.url).toBe("nats://file:4222");
  });

  test("copilot preset resolves spawn, home, and per-agent env", () => {
    const cfg = loadConfigFromSources({
      ...base,
      argv: ["start", "--agent", "copilot"],
      env: { USER: "alice", SYNADIA_COPILOT_OWNER: "copilot-owner", SYNADIA_GROK_OWNER: "grok-owner" },
    });
    expect(cfg.acp.agentId).toBe("copilot");
    expect(cfg.agent.subjectToken).toBe("copilot");
    expect(cfg.acp.bin).toBe("copilot");
    expect(cfg.acp.args).toEqual(["--acp"]);
    expect(cfg.acp.homeEnvVar).toBe("COPILOT_HOME");
    expect(cfg.acp.homeExample).toBe("~/.copilot");
    expect(cfg.acp.scrubEnv).toEqual(["COPILOT_ALLOW_ALL"]);
    expect(cfg.acp.permissionPolicy).toBe("reject");
    expect(cfg.agent.owner).toBe("copilot-owner");
  });

  test("model and reasoning effort are appended via the preset flags", () => {
    const cli = loadConfigFromSources({ ...base, argv: ["start", "--agent", "copilot", "--model", "claude-haiku-4.5", "--reasoning-effort", "low"] });
    expect(cli.acp.args).toEqual(["--acp", "--model", "claude-haiku-4.5", "--reasoning-effort", "low"]);
    expect(cli.acp.model).toBe("claude-haiku-4.5");
    expect(cli.acp.reasoningEffort).toBe("low");
    const env = loadConfigFromSources({
      ...base,
      argv: ["start", "--agent", "copilot"],
      env: { USER: "alice", SYNADIA_COPILOT_MODEL: "gpt-5", SYNADIA_ACP_MODEL: "ignored", SYNADIA_ACP_REASONING_EFFORT: "high" },
    });
    expect(env.acp.args).toEqual(["--acp", "--model", "gpt-5", "--reasoning-effort", "high"]);
    const file = loadConfigFromSources({ ...base, readFile: () => `[acp]\nagent = "copilot"\nmodel = "auto"\nreasoning_effort = ""\n` });
    expect(file.acp.args).toEqual(["--acp", "--model", "auto"]);
    expect(file.acp.reasoningEffort).toBeUndefined();
  });

  test("model pass-through is rejected for presets without a model flag", () => {
    expect(() => loadConfigFromSources({ ...base, argv: ["start", "--model", "grok-4"] })).toThrow("preset grok has no model flag");
    expect(() => loadConfigFromSources({ ...base, argv: ["start", "--agent", "custom", "--agent-id", "x", "--acp-bin", "x", "--reasoning-effort", "low"] })).toThrow("--acp-args");
    expect(() => loadConfigFromSources({ ...base, argv: ["start", "--agent", "copilot", "--model", "a b"] })).toThrow("whitespace");
  });

  test("subject token override is validated, not rewritten", () => {
    const cfg = loadConfigFromSources({ ...base, argv: ["start", "--subject-token", "g"] });
    expect(cfg.agent.subjectToken).toBe("g");
    expect(() => loadConfigFromSources({ ...base, argv: ["start", "--subject-token", "Not.Valid"] })).toThrow("agent.subject_token");
  });

  test("agent-home requires a preset with a home env var", () => {
    const cfg = loadConfigFromSources({ ...base, argv: ["start", "--agent-home", "/tmp/grok-home"] });
    expect(cfg.acp.agentHome).toBe("/tmp/grok-home");
    expect(() => loadConfigFromSources({ ...base, argv: ["start", "--agent", "custom", "--agent-id", "antigravity", "--acp-bin", "agy-acp", "--agent-home", "/tmp/agy-home"] })).toThrow("home env var");
  });

  test("rejects invalid numeric and enum config values", () => {
    expect(() => loadConfigFromSources({ ...base, readFile: () => `[agent]\nheartbeat_interval_s = nope\n` })).toThrow("agent.heartbeat_interval_s must be a positive number");
    expect(() => loadConfigFromSources({ ...base, readFile: () => `[acp]\nmode = "magic"\n` })).toThrow("acp.mode must be fake or managed");
    expect(() => loadConfigFromSources({ ...base, readFile: () => `[acp]\npermission_policy = "maybe"\n` })).toThrow("acp.permission_policy must be reject, query, or allow");
    expect(() => loadConfigFromSources({ ...base, env: { NATS_SENDER_IDENTITY: "auto" } })).toThrow("nats.sender_identity must be off or signed");
    expect(() => loadConfigFromSources({ ...base, env: { NATS_MIN_SENDER_TRUST: "trusted" } })).toThrow("agent.min_sender_trust must be any or signed");
  });

  test("strips inline TOML comments before parsing numeric fields", () => {
    const cfg = loadConfigFromSources({ ...base, readFile: () => `[agent]\nheartbeat_interval_s = 30 # seconds\nkeepalive_interval_s = 45 # seconds\n` });
    expect(cfg.agent.heartbeatIntervalS).toBe(30);
    expect(cfg.agent.keepaliveIntervalS).toBe(45);
  });

  test("renders a config template", () => {
    const template = renderConfigTemplate();
    expect(template).toContain("[nats]");
    expect(template).toContain("[agent]");
    expect(template).toContain("[acp]");
    expect(template).toContain('agent = "grok"');
    expect(template).toContain('permission_policy = "reject"');
    expect(template).toContain('sender_identity = "off"');
    expect(template).toContain('min_sender_trust = "any"');
    expect(DEFAULT_CONFIG_PATH).toContain("acp-nats-channel.toml");
  });
});
