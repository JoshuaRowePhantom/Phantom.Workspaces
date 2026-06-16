# Trust-scoped tool execution — open questions

Open questions to resolve before implementing the two remaining trust-execution todos:
`remote-trust-scoped-tool-execution` and `reverse-tunnel-trust-execution`. References:
`docs/design/trust-models.md`, `docs/design/reverse-tunnel-trust-execution.md`,
`docs/design/llm-trust-profile.md`.

## Context (what exists today)

- The trust model is built and unit-tested: `TrustProfile` + restrictive `TrustProfileComposer`;
  `TrustProfileEntityReader` + `ITrustProfileProvider`/`DictionaryTrustProfileProvider`;
  `ITrustedExecutor` + `TrustedExecutorSelector` (computer-set enforcement) + `LocalTrustedExecutor`
  + `TrustToolCallAuthorizer`; `RemoteTrustedExecutor` + `WebRemoteChatClient`.
- `AgentFactory.CreateAgentChatAsync` enforces a trust profile via `AgentTrustProfileResolver`.
- `POST /agent/respond` (`AgentRespondHandler`) is mapped and tested for routing, but it does **not**
  yet construct the agent under a resolved trust profile / `LocalTrustedExecutor`.
- The reverse-tunnel transport core is built/tested (frame DTOs, socket-backed
  `ReverseExecutionRegistry`, `/reverse/connect`, `LocalReverseExecutionHandler`,
  `ReverseExecutionClientHost`).

## remote-trust-scoped-tool-execution

The task: in `AgentRespondHandler`, construct the agent via `LocalTrustedExecutor` under the resolved
trust profile so `TrustToolCallAuthorizer` is enforced during remote execution.

Questions:

1. **Trust-profile source on the server.** `/agent/respond` currently has no trust-profile input.
   Where does the server obtain the caller's trust profile?
   - (a) From the request itself (the caller sends a trust-profile id / claimed identity), then the
     server resolves it via `TrustProfileEntityReader` from its own repository?
   - (b) From the authenticated transport identity (e.g., the dev tunnel `X-Tunnel-Authorization`
     GitHub identity, mapped to a user/computer entity), then resolved server-side?
   - (c) A fixed server-configured trust profile for all remote callers?

    a. We trust the caller. The caller provides the trust profile content (not entity) for the server to use.
  
2. **Untrusted/unknown caller default.** When no profile resolves, do we deny all tool execution
   (most restrictive), allow a configured "anonymous remote" profile, or reject the request?



3. **Identity trust.** Should the server trust the caller's *claimed* user-computer-profile id, or
   must it be cryptographically tied to the transport auth (GitHub token identity)?

   Trust the caller's user-computer-profile it.

4. **Scope of enforcement.** Only tool-call authorization, or also container/process execution limits
   (the Llm.Core trust layer) for the remote agent?

   We'll enforce container / tool-call authorizations as well, but we haven't implemented them yet, correct?

## reverse-tunnel-trust-execution

The design is approved (`docs/design/reverse-tunnel-trust-execution.md`); remaining wiring questions:

1. **Selector composition in production.** `ITrustedExecutorSelector` is not composed in the running
   server today. Where is it built, and how are `LocalTrustedExecutor` /
   `RemoteTrustedExecutor` / `ReverseTrustedExecutor` registered and chosen per request (by the
   target computer set in the trust profile)?

   The single trust executor should be created at application startup. The selector wil lbe composed
   of the registry-based one and a local based one.

2. **Reverse identity validation.** `/reverse/connect` validates a *claimed* user-computer-profile
   id. Same question as remote #3: is the claim trusted as-is, or must it be bound to the GitHub
   transport identity?

   We trust the caller. The caller already authenticated to github transport, so we have
   confidence that the caller is a user who has no reason to maliciously claim a different user-computer-profile.

3. **Opt-in default.** The `RemoteAccess` "accept reverse execution" opt-in — default off? Per-peer
   allow-list, or any authenticated peer once enabled?

   any authenticated peer

4. **Reconnect/registry lifetime.** In-memory registry with client reconnect is decided; on server
   restart the registry is empty — should clients auto-reconnect-and-re-register on a backoff, and is
   any inbound request during a gap rejected immediately or briefly queued?

   Clients auto-reconnect with backoff to max 2 minute poll interval. inbound requests should be rejected.

5. **Connection-status UI scope.** The `ConnectionStatusViewModel` / `ConnectionStatusWindow` (from a
   top-right network icon) — minimum viable: list outbound + inbound connections with live state. Any
   actions needed (disconnect, retry), or display-only for the first version?

   display-only, except have ability to restart the devtunnel host.

## Please answer

For each numbered question, a short choice/answer is enough. The most consequential are
remote #1/#3 and reverse #1/#2 (the identity-trust and selector-composition decisions), since they
determine the security model and where the wiring lives.
