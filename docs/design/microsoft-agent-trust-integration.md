# Microsoft Agent Governance Toolkit — Integration with Phantom.Workspaces

> **Status:** Draft  
> **Date:** 2026-07-07  
> **Repository cloned:** `C:\dev\microsoft\agent-governance-toolkit`  
> **Source:** https://github.com/microsoft/agent-governance-toolkit

---

## 1. Summary

Microsoft recently released the **Agent Governance Toolkit (AGT)** — a multi-language (Python,
TypeScript, .NET, Rust, Go) framework for deterministic policy enforcement, zero-trust identity,
execution sandboxing, and SRE observability for autonomous AI agents. It covers all 10 OWASP
Agentic Top 10 risks and is in public preview (v5.0.0).

Phantom.Workspaces already has a mature trust model centred on static, authored
`llm-trust-profile` entities that drive container-level and MCP tool-call-level enforcement.
AGT adds a complementary layer that Phantom.Workspaces does not currently have:
**dynamic trust scoring, cryptographic agent identity, YAML-based policy rules, tamper-proof
audit trails, and execution-ring enforcement.** The two models are architecturally distinct
and largely non-overlapping, which makes a dual-layer integration the natural fit.

**Recommended approach: Option C — Dual-layer.** Keep Phantom.Workspaces trust profiles as
the primary API surface for container and tool-call policy; adopt AGT underneath for dynamic
governance, identity, audit, and ring enforcement.

---

## 2. Microsoft Agent Governance Toolkit — Overview

### 2.1 What it is

AGT intercepts every agent action **before execution**, in deterministic application code, and
evaluates it against policy documents in sub-millisecond time. It is explicitly not a
prompt-safety tool; it enforces at the application layer and describes actions the agent is
_structurally incapable_ of performing, not merely unlikely to perform.

### 2.2 Trust tiers

AGT assigns every agent a numeric trust score on a **0–1000 scale** with five named tiers:

| Score | Tier | Typical capabilities |
|---|---|---|
| 900–1000 | **Verified Partner** | Full access, cross-org delegation, production deploys |
| 700–899 | **Trusted** | Elevated privileges, write access, sensitive data |
| 500–699 | **Standard** | Default for new agents; read access, standard API calls |
| 300–499 | **Probationary** | Read-only, limited tools, all actions logged |
| 0–299 | **Untrusted** | Blocked or sandboxed, no external access |

Trust is computed from four weighted dimensions: policy compliance (35%), task success (25%),
behavioral anomaly absence (25%), and identity/credential freshness (15%). New agents start at
**500 (Standard)**.

Trust scores **decay** when an agent is inactive and **propagate** through delegation chains:
a child agent's maximum score is capped at `parent_score × 0.7`.

### 2.3 Identity model

Every agent has a **Decentralized Identifier (DID)** in the format `did:mesh:<hex-128-bit>`,
bound to an Ed25519 keypair, and anchored to a human sponsor (email). No orphan agents.

Authentication between agents uses the **Inter-Agent Trust Protocol (IATP)** — an
Ed25519 challenge-response handshake. Credentials are short-lived bearer tokens scoped to
capability subsets. The toolkit also supports SPIFFE/SVID for workload identity in mTLS
contexts.

### 2.4 Execution rings

The **Agent Hypervisor** maps trust scores to CPU-ring-inspired privilege levels:

| Ring | Trust threshold | `AllowWrites` | `AllowNetwork` | `AllowDelegation` | Calls/min |
|---|---|---|---|---|---|
| Ring 0 (Root) | 0.95 + SRE Witness | ✅ | ✅ | ✅ | Unlimited |
| Ring 1 (Privileged) | 0.80 | ✅ | ✅ | ✅ | 1 000 |
| Ring 2 (Standard) | 0.60 | ✅ | ✅ | ❌ | 100 |
| Ring 3 (Sandbox) | 0.0 | ❌ | ❌ | ❌ | 10 |

Ring 0 is never auto-granted; it requires explicit human SRE Witness attestation.

### 2.5 Policy engine

Policies are YAML/JSON `PolicyDocument` files evaluated by `PolicyEvaluator` in under 0.1 ms.
Rules have conditions over an execution-context dictionary (`field`, `operator`, `value`), a
priority, and an action (`allow`, `deny`, `require_approval`). Default is configurable.

The .NET SDK exposes `GovernanceKernel`, which composes all subsystems:

```csharp
var kernel = new GovernanceKernel(new GovernanceOptions
{
    PolicyPaths = new() { "policies/default.yaml" },
    EnableRings = true,
    EnablePromptInjectionDetection = true,
    EnableCircuitBreaker = true,
});

var result = kernel.EvaluateToolCall("did:mesh:analyst-001", "file_write",
    new() { ["path"] = "/etc/config" });
```

### 2.6 Sandbox interface

The .NET SDK defines a backend-agnostic `ISandboxProvider` interface with three operations:
`CreateSessionAsync`, `ExecuteCodeAsync`, and `DestroySessionAsync`. The provided implementation,
`DockerSandboxProvider`, wraps the Docker CLI and targets **Linux containers only** (default
image `python:3.11-slim`; uses `/dev/null` and POSIX entrypoints). **There is no Windows
container support in the AGT sandbox layer.**

### 2.7 Audit

The Agent Hypervisor maintains a **Merkle-chain append-only audit log**: every operation is
SHA-256 linked to its predecessor, making tampering detectable. The `AuditEmitter` emits
structured `GovernanceEvent` records that can be routed to any sink (Azure Monitor, write-once
storage, etc.).

### 2.8 Key .NET types

| Type | Role |
|---|---|
| `GovernanceKernel` | Central orchestrator: composes policy engine, ring enforcer, rate limiter, audit, SRE |
| `PolicyEvaluator` | Evaluates `PolicyDocument` YAML/JSON against execution context |
| `RingEnforcer` | Computes execution ring from trust score; checks ring access |
| `ISandboxProvider` | Backend-agnostic sandbox session interface |
| `DockerSandboxProvider` | Linux-Docker implementation of `ISandboxProvider` |
| `AgentIdentity` | DID + Ed25519 keypair + sponsor binding |
| `TrustVerifier` | Validates trust scores and tier thresholds |
| `AuditEmitter` | Tamper-proof event log with Merkle chaining |
| `KillSwitch` | Emergency agent termination with step handoff |

The MCP extension (`AgentGovernance.Extensions.ModelContextProtocol`) integrates `GovernanceKernel`
into MCP server tool-call interception via `builder.Services.AddMcpServer().WithGovernance(...)`.

---

## 3. Phantom.Workspaces Trust Model — Overview

### 3.1 Current model

Phantom.Workspaces uses **static, authored trust profiles** — persisted `llm-trust-profile`
entities that define the execution policy for an agent or tool. There are no numeric scores,
no dynamic tiers, and no cryptographic agent identity. Trust is declared and composed at
authoring time; enforcement is entirely structural.

### 3.2 What a trust profile defines

A `TrustProfile` (runtime/composed) carries:

| Field | Type | Description |
|---|---|---|
| `HostingWorkspacesClientInstances` | `string[]` | Computers permitted to run the agent; `"."` = local, `"*"` = any |
| `MountPoints` | `TrustMountPoint[]` | Docker bind/volume/tmpfs mounts with read-only or read-write access |
| `NetworkAccessPolicy` | enum | `NoNetwork` / `LocalNetwork` / `NattedNetwork` / `HostNetwork` |
| `HttpsProxyPolicy` | record | `Disabled` / `Optional` / `Required` + proxy URL |
| `AllowedMcpToolCallSchema` | `JsonObject` | Composed `anyOf` JSON Schema; tool calls are validated against it |

### 3.3 Composition

Trust profiles inherit from base profiles in one of two modes:

- **Restrictive** (default): intersection of computer sets, most-restrictive network, intersect
  mounts (read-only wins), strongest proxy requirement.
- **Permissive**: union of computer sets, most-permissive network, union mounts (read-write
  wins), weakest proxy requirement.

MCP tool-call schemas (both allowed and restricted) always compose additively (`anyOf` union)
regardless of mode. Composition is commutative; cycle detection is enforced.

### 3.4 Enforcement

Enforcement is structural and happens at the layer responsible:

1. **Computer set** — `TrustedExecutorSelector` refuses execution on a client instance not in
   the effective `HostingWorkspacesClientInstances`.
2. **Tool calls** — `TrustToolCallAuthorizer` validates `{ toolName, input }` envelopes against
   the composed JSON Schema. Calls that do not validate are denied; restricted schemas override
   allowed schemas via `allOf: [allowed, { not: { anyOf: restricted } }]`.
3. **Container policy** — `DockerContainerTrustProfileMaterializer` converts the effective
   profile into Docker CLI arguments: mount flags (`:ro`/`:rw`), network mode, proxy env vars.
   Settings are written once at container start and treated as immutable.

### 3.5 Container support

Phantom.Workspaces has full **Windows + macOS + Linux** Docker support:

- `WindowsDockerDesktopEngine` — Windows Docker Desktop CLI wrapper
- `WindowsContainerDEngine` — native Windows container engine (containerd)
- `MacOSContainerDEngine` / `LinuxContainerDEngine` — macOS/Linux equivalents

---

## 4. Comparison

| Concept | Phantom.Workspaces | Microsoft AGT |
|---|---|---|
| **Trust model** | Static authored profiles, entity-based | Dynamic 0–1000 numeric score + tiers |
| **Trust levels** | Not formally named; profiles are policy documents | Verified Partner / Trusted / Standard / Probationary / Untrusted |
| **Identity** | Client-instance string (`"."`, `"*"`) | DID `did:mesh:<hex>`, Ed25519 keypairs, human sponsors |
| **Expression** | JSON Schema entity (`llm-trust-profile.json`) | YAML/JSON `PolicyDocument` + `AgentIdentity` |
| **Enforcement** | Structural: JSON Schema validation + Docker args | Deterministic application-layer interception via `GovernanceKernel` |
| **Container** | ✅ Windows, macOS, Linux Docker | ❌ Linux only (`python:3.11-slim`); `ISandboxProvider` is abstract |
| **Windows container support** | ✅ Full (`WindowsContainerDEngine`, `WindowsDockerDesktopEngine`) | ❌ None in AGT (POSIX-only `DockerSandboxProvider`) |
| **MCP tool-call policy** | ✅ Core feature: `allowed-mcp-tool-call-schemas` + `restricted-mcp-tool-call-schemas` | Via `AgentGovernance.Extensions.ModelContextProtocol` extension |
| **Mount / network policy** | ✅ Rich: `mount-points`, `network-access-policy`, `https-proxy-policy` | Basic: `SandboxConfig.NetworkEnabled`, `MemoryMb`, `CpuLimit` |
| **Execution rings** | ❌ No ring concept | ✅ Rings 0–3 derived from trust score |
| **Trust propagation** | Profile inheritance (base-profile chains) | Delegation chains with `parent_score × 0.7` ceiling; network contagion |
| **Dynamic trust** | ❌ No; profiles are static | ✅ Continuous behavioral scoring with decay |
| **Audit** | ❌ No dedicated audit trail | ✅ Merkle-chain append-only audit log |
| **Kill switch** | ❌ Not implemented | ✅ `KillSwitch` with step handoff |
| **Prompt injection detection** | ❌ Not implemented | ✅ Configurable via `EnablePromptInjectionDetection` |
| **Circuit breaker / SRE** | ❌ Not implemented | ✅ `EnableCircuitBreaker`, SLO engine, error budgets |
| **Multi-agent identity** | Remote client instance routing via dev tunnel | `AgentMesh` mesh with IATP handshakes, endorsement registry |
| **Primary language** | .NET (C#) | Python primary; .NET/TypeScript/Rust/Go SDKs |
| **License** | — | MIT |

---

## 5. Integration Approach

### Option A — Adopt Microsoft model as primary

Replace Phantom.Workspaces trust profiles with AGT's `PolicyDocument` + `AgentIdentity` model.
Migrate `llm-trust-profile` entities to YAML policy files; remove `TrustProfile`,
`TrustProfileComposer`, and `TrustToolCallAuthorizer`.

**Pros:** Single model; leverage AGT's ecosystem (OWASP coverage, audit, SRE).  
**Cons:** AGT's policy engine is action-condition-based, not JSON-Schema-based. It cannot
directly express Phantom.Workspaces's rich MCP tool-call schema validation (e.g., matching on
`input` field shapes, nested property patterns). The container policy layer (`mount-points`,
`network-access-policy`) would need to be rebuilt from scratch in AGT terms — AGT's sandbox
only has `NetworkEnabled` and `MemoryMb`. AGT has no Windows container support. This would be
a large, breaking change.

**Not recommended.**

### Option B — Wrap Microsoft model

Keep Phantom.Workspaces trust profiles as the user-facing API; implement enforcement
underneath using `GovernanceKernel`. Every `TrustProfile` field maps to a corresponding AGT
policy rule.

**Pros:** Single user-facing model.  
**Cons:** The mapping is lossy and awkward. AGT's policy rules operate on `action.type` and
field conditions; `TrustProfile` JSON Schema validation is fundamentally different. The
impedance mismatch would result in a thin shim that provides little real AGT value and still
requires a custom container policy layer. Likely gives the worst of both worlds.

**Not recommended.**

### Option C — Dual-layer (Recommended)

Maintain two distinct, non-overlapping governance layers:

**Layer 1 (Static, Phantom.Workspaces):** `TrustProfile` continues to own:
- MCP tool-call schema policy (`allowed-mcp-tool-call-schemas`, `restricted-mcp-tool-call-schemas`)
- Container enforcement (mounts, network mode, proxy)
- Computer-set routing (`hosting-workspaces-client-instances`)

**Layer 2 (Dynamic, AGT):** `GovernanceKernel` adds:
- YAML-based action-condition policy rules (complement to JSON Schema; handle cases like
  "deny all `file_write` to paths outside `/workspace`")
- Execution-ring enforcement (`RingEnforcer`) for multi-agent scenarios
- Tamper-proof audit trail (`AuditEmitter`) with Merkle-chain integrity
- Prompt injection scanning (`EnablePromptInjectionDetection`) on tool-call arguments
- Agent DID identity for multi-agent mesh use cases (`AgentIdentity`, IATP handshake)
- Dynamic trust scoring for long-running autonomous agents

**Rationale:** The two models are complementary, not competing:

- Phantom.Workspaces trust profiles are declarative container-and-schema policies; they
  express *what* a container is allowed to do structurally. AGT adds *who* is making the
  request (identity), *how much trust* they have earned behaviorally (scoring), *did anything
  suspicious happen* (audit), and *should this agent still be running* (kill switch, SRE).
- Integration surface is narrow: insert a `GovernanceKernel.EvaluateToolCall` call inside
  `TrustAuthorizingAIFunction.InvokeCoreAsync`, after the existing JSON Schema check.
  Both layers must allow the call for it to proceed.

**Integration seam (illustrative):**

```csharp
protected override async ValueTask<object?> InvokeCoreAsync(
    AIFunctionArguments arguments, CancellationToken cancellationToken)
{
    // Layer 1: Phantom.Workspaces JSON Schema policy (existing)
    if (!this.authorizer.IsToolCallAllowed(this.Name, ToInput(arguments)))
        return $"Tool call '{this.Name}' was denied by the trust profile.";

    // Layer 2: AGT dynamic policy (new)
    if (this.governanceKernel is not null)
    {
        var result = this.governanceKernel.EvaluateToolCall(
            this.agentDid, this.Name,
            arguments.ToDictionary(kvp => kvp.Key, kvp => (object?)kvp.Value));
        if (!result.Allowed)
            return $"Tool call '{this.Name}' was denied by governance policy: {result.Reason}";
    }

    return await this.innerFunction.InvokeAsync(arguments, cancellationToken)
                                   .ConfigureAwait(false);
}
```

The `GovernanceKernel` is optional at construction time (null = AGT layer disabled), so the
existing behavior is fully preserved with zero-cost for deployments that do not adopt AGT.

---

## 6. Windows Container Support

### AGT's current state

`DockerSandboxProvider` is POSIX-only:
- Default image is `python:3.11-slim`
- Entrypoint uses `tail -f /dev/null` (POSIX-only command)
- Execute uses `docker exec ... python` with stdin piping
- `--cap-drop ALL` and `--read-only` work on Linux; `--cap-drop` is a no-op for Windows
  containers

The `ISandboxProvider` interface is abstract and carries no OS assumptions. The implementation
is the problem, not the contract.

### Phantom.Workspaces's current state

Phantom.Workspaces has full Windows container support today:
- `WindowsContainerDEngine` — Windows containers via the Windows containerd engine
- `WindowsDockerDesktopEngine` — Windows Docker Desktop (Linux containers on Windows OR
  Windows containers)
- `ContainerDefinition` with mount, network, and port-mapping support

### Work needed for Windows container support in AGT integration

For the dual-layer approach, Phantom.Workspaces **does not need AGT's sandbox layer** — it
uses its own container infrastructure. The only integration point is `GovernanceKernel`'s
policy and audit layers, which are container-agnostic.

If a future use case requires AGT's `ISandboxProvider` for Windows containers (e.g., to
replace part of the container stack), a `WindowsContainerSandboxProvider : ISandboxProvider`
would need to be implemented that:

1. Replaces `python:3.11-slim` with a Windows base image (e.g., `mcr.microsoft.com/windows/nanoserver`).
2. Replaces `/dev/null` with `NUL` and POSIX entrypoints with Windows equivalents.
3. Uses `--isolation=hyperv` for Hyper-V isolation (stronger Windows sandbox boundary).
4. Maps `--cap-drop ALL` to Windows security options (`--security-opt no-new-privileges`
   where supported, or Hyper-V isolation as the equivalent boundary).

This is straightforward but non-trivial work if Windows execution sandboxing via AGT's
interface is desired. **For the initial integration, skip this: use Phantom.Workspaces's
existing container infrastructure and integrate only AGT's governance layers.**

---

## 7. Open Questions / Design Decisions Needed

1. **Trust score persistence:** AGT trust scores decay and accumulate over time. Where should
   scores be persisted? Options: Phantom.Workspaces entity store (MongoDB), in-memory only,
   or an external AGT-managed store. In-memory loses state on restart; persistent store needs
   a new entity type.

2. **Agent DID assignment:** Should every Phantom.Workspaces agent definition get a DID
   automatically, or only agents that opt into multi-agent mesh scenarios? DIDs are UUIDs
   under the hood; generating one at agent-definition creation time is cheap. Recommend
   automatic, but needs a decision.

3. **Initial trust score:** What should new agents start at? AGT defaults to 500 (Standard).
   Phantom.Workspaces agents that are tied to a verified human user profile might deserve a
   higher initial score (600–700). Agents spawned by other agents should get `parent × 0.7`.

4. **Policy file location and authoring:** AGT policies are YAML files on disk. Should
   Phantom.Workspaces surface them as entities in the entity store, or manage them as files?
   Entity storage fits the existing model better (searchable, versioned), but needs an
   AGT-compatible serializer from entity JSON → YAML/JSON policy documents.

5. **Audit log routing:** `AuditEmitter` can route to any sink. What is the target for
   Phantom.Workspaces? Options: structured log entries via the existing logging infrastructure,
   a dedicated entity type in the store, or an external sink (Azure Monitor, write-once blob).
   This affects the tamper-evidence guarantee.

6. **Ring assignment for Phantom.Workspaces trust profiles:** If execution rings are adopted,
   how do existing `TrustProfile` definitions map to ring thresholds? A profile that grants
   `HostNetwork` + read-write mounts might map to Ring 2; a profile with `NoNetwork` and
   read-only mounts might map to Ring 3. This mapping needs to be designed explicitly.

7. **Kill switch integration:** AGT's `KillSwitch` terminates agent processes. Phantom.Workspaces
   agents run in Docker containers managed by `ContainerEngine`. A kill-switch implementation
   must call `ContainerEngine.DestroyAsync` rather than a POSIX signal. The existing
   container abstraction supports this.

8. **Prompt injection detection scope:** AGT scans tool-call arguments. Should this be enabled
   for all tool calls, or only for user-facing / high-risk tools? False positives on developer
   tools (e.g., code search, shell) could be disruptive.

9. **AgentMesh (multi-agent mesh) adoption timeline:** IATP handshakes and trust score
   propagation only matter when agents communicate with each other across process/machine
   boundaries. Is that a near-term or long-term goal for Phantom.Workspaces?

---

## 8. Proposed Implementation Steps

Implementation is ordered by dependency; each step can proceed once its dependencies are done.

### Phase 0 — Package reference (no behavior change)

| Step | Work |
|---|---|
| 0.1 | Add `Microsoft.AgentGovernance` NuGet reference to `Phantom.Workspaces.Llm.Core` (optional dependency — do not make it required for the base package). |
| 0.2 | Add `Microsoft.AgentGovernance.Extensions.ModelContextProtocol` reference to the agent execution project. |

### Phase 1 — Policy evaluation layer

| Step | Depends on | Work |
|---|---|---|
| 1.1 | 0.1 | Create `GovernanceKernelFactory` in `Phantom.Workspaces.Llm.Core`: reads YAML policy files from a configurable path; constructs a `GovernanceKernel` with `EnableAudit=true`. Returns `null` when no policy path is configured (opt-in). |
| 1.2 | 1.1 | Thread an optional `GovernanceKernel?` into `TrustAuthorizingAIFunction` and `TrustToolCallAuthorizer`. Insert `EvaluateToolCall` after the JSON Schema check (see §5 seam above). Both layers must allow; first deny wins. |
| 1.3 | 1.2 | Add tests: tool calls allowed by JSON Schema but denied by AGT policy are rejected; tool calls denied by JSON Schema are rejected without reaching AGT. |

### Phase 2 — Audit trail

| Step | Depends on | Work |
|---|---|---|
| 2.1 | 1.1 | Route `AuditEmitter` events to Phantom.Workspaces's `ILogger` (structured log). This gives immediate observability at zero infrastructure cost. |
| 2.2 | 2.1, 7 (DID decision) | If entity-store audit is desired: define an `audit-event` entity type; write an `EntityAuditSink` that persists `GovernanceEvent` records as entities. This provides queryable, Merkle-verifiable history via the existing DAL. |

### Phase 3 — Agent DID identity

| Step | Depends on | Work |
|---|---|---|
| 3.1 | Decision from §7 Q2 | Add a `did` field to `agent-definition.json` schema (optional; auto-populated at first use if absent). |
| 3.2 | 3.1 | In `AgentFactory.CreateAgentChatAsync`, generate or load an `AgentIdentity` for the agent definition. Store the private key in the Phantom.Workspaces secrets store (not in the entity). |
| 3.3 | 3.2 | Pass the agent's DID to `TrustAuthorizingAIFunction` so it can be forwarded to `GovernanceKernel.EvaluateToolCall`. |

### Phase 4 — Execution ring enforcement

| Step | Depends on | Work |
|---|---|---|
| 4.1 | Decision from §7 Q6, Phase 3 | Design the mapping from `TrustProfile` to initial ring assignment (ring derives from profile properties, not a numeric score, in the first pass). |
| 4.2 | 4.1 | Instantiate `RingEnforcer` inside `GovernanceKernelFactory` with custom thresholds reflecting the mapping. |
| 4.3 | 4.2 | Enable `EnableRings = true` and test ring enforcement: agents with `NoNetwork+ReadOnly` profiles are correctly placed in Ring 3; agents with full access in Ring 1. |

### Phase 5 — Dynamic trust scoring

| Step | Depends on | Work |
|---|---|---|
| 5.1 | Decisions from §7 Q1+Q3, Phase 3 | Choose persistence backend (in-memory vs entity store). |
| 5.2 | 5.1 | Wire `AgentIdentity.TrustScore` updates: policy violations → negative signal; successful task completions → positive signal. |
| 5.3 | 5.2 | Add UI: surface trust score and tier in the agent status panel alongside existing container/session status indicators. |

### Phase 6 — Kill switch and SRE

| Step | Depends on | Work |
|---|---|---|
| 6.1 | Phase 5 | Implement a `ContainerKillSwitch` adapter: wraps `KillSwitch` and calls `ContainerEngine.DestroyAsync` when a kill is triggered. |
| 6.2 | 6.1 | Wire `KillSwitch` to score crossing the Untrusted threshold (< 300) for long-running autonomous agents. |
| 6.3 | 6.2 | Enable `EnableCircuitBreaker = true` for resilience; configure SLO thresholds. |

### Phase 7 — Prompt injection detection

| Step | Depends on | Work |
|---|---|---|
| 7.1 | 1.2, Decision from §7 Q8 | Enable `EnablePromptInjectionDetection = true` in `GovernanceOptions`; configure per-tool allowlists so developer tools (shell, code search) do not generate false positives. |
| 7.2 | 7.1 | Add integration tests with known injection payloads. |

### Phase 8 — Windows container sandbox provider (optional, long-term)

| Step | Depends on | Work |
|---|---|---|
| 8.1 | Decision from §7 Q9 | Implement `WindowsContainerSandboxProvider : ISandboxProvider` backed by Phantom.Workspaces's existing `WindowsContainerDEngine`. Replaces POSIX assumptions with Windows equivalents. |
| 8.2 | 8.1 | Implement `HyperVSandboxProvider : ISandboxProvider` using `--isolation=hyperv` for stronger isolation. |
| 8.3 | 8.1–8.2 | Register as the default `ISandboxProvider` on Windows hosts; fall back to `DockerSandboxProvider` on Linux. |

---

## References

- Microsoft Agent Governance Toolkit: https://github.com/microsoft/agent-governance-toolkit
- AGT .NET SDK: `C:\dev\microsoft\agent-governance-toolkit\agent-governance-dotnet`
- AGT Architecture: `C:\dev\microsoft\agent-governance-toolkit\docs\ARCHITECTURE.md`
- AGT Specs: `C:\dev\microsoft\agent-governance-toolkit\docs\specs\`
- Phantom.Workspaces trust model: `docs/design/trust-models.md`
- Phantom.Workspaces trust profile schema: `Phantom.Workspaces.Data.Core/JsonSchemas/llm-trust-profile.json`
- Phantom.Workspaces trust runtime: `Phantom.Workspaces.Llm.Core/Trust/TrustProfile.cs`
- Phantom.Workspaces container engines: `Phantom.Workspaces.Containers/`
