# Design: `secret-store`

> **Bug title prefix:** `[secret-store]`
>
> **Authoritative source root:** `C:\dev\Phantom.Workspaces-Skills\features` (git worktree,
> HEAD `9a0439c4`). `C:\dev\Phantom.Workspaces-LLM` is a separate clone that is a
> couple of commits behind and is used only to cross-reference. All file:line citations
> below are from `features\` unless noted.

---

## Requirements

Restated from the owner as unambiguous, testable statements.

### Functional

1. **Platform-backed secret store (Windows-only in this feature).** Provide a
   way for users to input secret values (API tokens, API keys, passwords) once
   and later retrieve them for use in agent definitions and other consumers.
   The default (and only shipping) backend for this feature is the local
   **Windows Credential Manager**, wrapped via the maintained NuGet
   `Meziantou.Framework.Win32.CredentialManager`. The backend sits behind a
   clean `IPlatformSecretStore` seam so future platforms (macOS Keychain,
   libsecret) can plug in — see §Future work. Non-Windows platforms fall back
   to `NullPlatformSecretStore` for this feature; only login-based sources
   (e.g. `GitHubLoginSecretSource`) work off-Windows, and `CredentialStore`
   value sources deterministically produce `SecretRequestFailure`.

2. **Global `ISecretProvider` service.** A single `ISecretProvider` is registered
   once at app startup and is resolvable from anywhere in `Phantom.Workspaces`
   (main GUI process). Consumers such as `AgentFactory` obtain it through the
   existing service-container mechanism (see §Detailed design → Code organisation).

3. **Manifest-driven secret uses.** Agent manifests may reference secrets with
   the placeholder syntax `${SECRET:Name}` (analogous to the existing
   `${GITHUB_TOKEN}` env-var syntax used in
   `AgentFactory.ResolveApiKey` /
   `AgentDefinitionParameterSubstitutor.SubstitutePlaceholders`). At agent
   materialization time these uses are discovered, each is turned into a
   `SecretRequest`, and (subject to consent, §5) the values are injected in place
   of the placeholders before the chat client / tools are constructed.

4. **Consent dialog (`SecretUseDialog`).** When any requested secret is not
   already covered by a stored consent decision, a single modal dialog is shown
   listing every unresolved use, in one row per `(SecretName, UseDisplayString)`
   pair, using the layout below. Columns 3 (`Remember?`) and 4 (`Value Source`)
   are drop-downs.

   > Would you like to allow using the following secrets?
   >
   > | Secret | Use | Remember? | Value Source |
   > |---|---|---|---|
   > | `${SECRET:GithubApiToken}` | `definition.model.additionalOptions.ApiToken` | This Key in This Manifest | Current GitHub Login |
   > | `${SECRET:AwsApiKey}` | `definition.tools.aws.additionalOptions.ApiKey` | All Uses | Current AWS Login |
   > | `${SECRET:AwsApiKey}` | `definition.tools.awsVersion2.additionalOptions.ApiKey` | Any Manifest | Saved credential: `AwsProdKey`  `[…]` |
   > | `${SECRET:AzureApiKey}` | `definition.tools.azure.additionalOptions.ApiKey` | Always Ask | Current Azure Login |
   >
   > `[Yes]` `[No]`

   The dialog is **manifest-agnostic**: it accepts a generic input model
   (`IReadOnlyList<SecretRequest>` + candidate `SecretUseMemory`s per row + candidate
   `SecretSource`s per row) and returns a generic result. It does not import any
   `AgentSchema` type. Critically, **the dialog never sees `SecretUseScope`
   values or any manifest content**; it only sees the `DisplayString`s of the
   caller-supplied `SecretUseMemory` candidates and the `SecretSource`s.

5. **Skip dialog when unnecessary.** If every requested secret already has a
   stored consent (matched by hash — see §6/§7) **or** the request list is empty,
   `ISecretProvider.RequestSecretsAsync` resolves silently without showing UI.

6. **Consent scope hierarchy — caller-supplied `SecretUseMemory` candidates.**
   The `Remember?` drop-down offers a caller-defined set of scopes, ordered
   broadest → narrowest. The agent-manifest caller (via
   `AgentManifestSecretUseMemoryFactory`) supplies these candidates for every
   `SecretRequest`:

   1. **`AllUses`** — "All Uses". Broadest. This secret is allowed for
      absolutely everything, in any manifest, for any key, for any secret name.
      A single grant covers the entire app for this user.
   2. **`AnyManifest`** — "Any Manifest". This specific `secretName` in any
      manifest, at any use path.
   3. **`KeyInAnyManifest`** — "This Key in Any Manifest". This specific
      `secretName` at this specific use path, across any manifest.
   4. **`ManifestIdentity`** — "This Manifest, Even if Changed". This specific
      `secretName` in this specific manifest **identity** (a stable id — see
      §7). The consent survives edits to the manifest content.
   5. **`ManifestContent`** — "This Manifest". This specific `secretName` in
      this specific manifest **content**. Any edit to the manifest content
      invalidates the consent.
   6. **`KeyInManifestContent`** — "This Key in This Manifest". Narrowest
      content-keyed scope. This specific `secretName` at this specific use path
      in this specific manifest content. Any edit invalidates.
   7. **`AlwaysAsk`** — "Always Ask". A guaranteed non-matching, non-persisted
      choice; forces a prompt on every future use.

   The `ISecretProvider` stays scope-agnostic: it walks a request's candidate
   `SecretUseMemory` list and checks whether any candidate's `Hash` matches a
   stored `MemorizedSecret`; if so, that `MemorizedSecret.SecretSource` is
   auto-selected. Otherwise it prompts and persists the user-chosen candidate.

7. **Security invariant — content-keyed hashes invalidate on edit.** For every
   scope that carries the word "This Manifest" in its content form
   (`ManifestContent`, `KeyInManifestContent`), the SHA-256 preimage embeds
   `canonicalJson(manifest.Template)`. Any edit to the manifest content changes
   that hash, which means the previously granted consent **does not match**, and
   the dialog is shown again. This prevents a user (or an attacker with write
   access to a manifest file) from reusing a prior consent to leak a secret into
   a differently-shaped agent.

   `ManifestIdentity` **deliberately** trades that security property for
   ergonomics: it hashes only the stable manifest id (`entity-id`), so edits do
   not invalidate consent. The dialog surface (`"This Manifest, Even if
   Changed"`) makes this trade-off legible to the user.

   `AnyManifest`, `KeyInAnyManifest`, and `AllUses` do not embed manifest
   content or identity at all; they are inherently coarser and are documented
   as such in the dialog labels.

8. **Per-scope hash preimages.** All hashes are hex SHA-256 of a UTF-8 string
   preimage built with the version prefix `"phantom.workspaces/secret-store/v1"`
   and pipe-delimited fields (see §Detailed design → `SecretUseScope` for the
   exact per-scope preimage). `AlwaysAsk`'s hash is the empty string `""` and
   is defined never to match any stored record.

9. **Local per-user consent store.** Consent records
   (hash → `MemorizedSecret`) are persisted to a JSON file
   `%APPDATA%\Phantom.Workspaces\allowed-secrets.json`, alongside
   `config.json` (see `ConfigurationPersistenceService.GetDefaultConfigurationPath`,
   features\Phantom.Workspaces\Configuration\ConfigurationPersistenceService.cs:54).
   The file contains **no secret values** — only the source descriptor
   (`SecretSource`) and the memory descriptor (`SecretUseMemory`). This aligns
   with the existing invariant documented at
   `ConfigurationPersistenceService.cs:15-17`: *"Only secret sources … are
   persisted; raw secret values are never written."*

10. **Fail-closed on per-secret resolution failure.** If
    `RequestSecretsResult.FailedSecrets` contains any entry that corresponds to
    a placeholder in the manifest — or if `ISecretProvider.RequestSecretsAsync`
    returns `null` (global refusal / not permitted) —
    `AgentDefinitionSecretMaterializer` throws and
    `AgentFactory.CreateChatClientAsync` propagates the exception so chat-client
    creation **fails as a whole**. Placeholders are never silently dropped and
    an unresolved `${SECRET:...}` value is never allowed to flow into a chat
    client or tool constructor. (An earlier draft considered a "silently drop
    the placeholder" mode analogous to `AgentDefinitionParameterSubstitutor`'s
    optional-parameter behaviour; that mode is rejected and appears only in
    §Considered / Background.)

11. **AWS / Azure login sources ship as visible "Not yet implemented"
    placeholders.** `AwsLoginSecretSource` and `AzureLoginSecretSource` appear
    as selectable entries in the `Value Source` drop-down. Their resolvers
    always yield a `SecretRequestFailure` with `Reason = Other` and a
    `FailureReasonDisplayString` such as `"AWS login is not yet implemented"` /
    `"Azure login is not yet implemented"`. Because of §10 (fail-closed),
    picking one of these sources at consent time will cause the subsequent
    chat-client creation to fail with a clear message identifying the
    unimplemented source — until real integrations land.

12. **Cross-platform backends — Windows only in this feature.** The
    `IPlatformSecretStore` abstraction is designed for multiple OS backends,
    but only the Windows backend (`WindowsCredentialManagerSecretStore`, built
    on `Meziantou.Framework.Win32.CredentialManager`) ships in this feature.
    macOS and Linux backends are explicitly deferred to future work — see
    §Future work. On any non-Windows platform, `NullPlatformSecretStore` is
    installed as the fallback: `CredentialStoreSecretSource` value sources
    fail with `SecretRequestFailure` (`Reason.Other`), and only login-based
    sources (e.g. `GitHubLoginSecretSource`) remain usable.

13. **Fold existing secret uses into the scheme.** All current secret/token
   acquisition paths must be reachable through `ISecretProvider` or through a
   `SecretSource` implementation. Specifically:
   * `GitHubAuthTokenResolver.Resolve[Async]` (Phantom.Workspaces.Llm.Core\GitHubAuthTokenResolver.cs:39,54) is exposed as a
     `GitHubLoginSecretSource`.
   * `EnvironmentApiKeyResolver` / `AgentFactory.ResolveApiKey[Async]`
     (Phantom.Workspaces.Llm.Core\EnvironmentApiKeyResolver.cs:8,
     Phantom.Workspaces.Llm.Core\AgentFactory.cs `ResolveApiKey`) continues to
     handle `${VAR}` env-var expansion; the new `${SECRET:Name}` expansion is a
     sibling, not a replacement.
   * `IGitHubAccountUpsertService` / GitHub Copilot client construction in
     `AgentFactory.CreateGitHubCopilotClient` continues to work but obtains its
     token through the new provider when the manifest uses `${SECRET:...}`.

### Non-functional / security invariants

14. Secret values are held in `SecureString` at rest in memory, and only ever
    materialized to a `string` inside `AgentFactory` at the moment they are handed
    to a chat-client / tool constructor.
15. `allowed-secrets.json` is written with `FileMode.Create` and uses the same
    `JsonSerializerOptions` (camel-case, `WhenWritingNull`, indented,
    string-enum) as `ConfigurationPersistenceService`
    (features\Phantom.Workspaces\Configuration\ConfigurationPersistenceService.cs:20-26).
16. All Credential-Manager calls happen on Windows only; the abstraction returns
    an appropriate `SecretRequestFailure` (`FailureReason.Other`) on non-Windows
    platforms when no other source is configured.
17. **Windows backend NuGet dependency.** The Windows backend takes a single
    new runtime NuGet reference: `Meziantou.Framework.Win32.CredentialManager`
    (v3.0.1, MIT, zero runtime dependencies — its `Microsoft.Windows.CsWin32`
    dependency is a dev-time source generator only). No hand-rolled Win32
    P/Invoke is added by this feature. The package is added to
    `Directory.Packages.props` as part of the platform-store commit.
18. **Saved-credential enter/select flow.** In the dialog's `Value Source`
    column, when the user selects the `[Saved Credential]` source, a **[…]**
    button next to that dropdown launches a native picker/entry flow that
    lets the user either **select an existing** saved credential or **enter a
    new one**. On Windows this is
    `Meziantou.Framework.Win32.CredentialManager.CredentialManager.PromptForCredentials`
    (which internally calls `CredUIPromptForWindowsCredentials` with
    `CREDUIWIN_ENUMERATE_CURRENT_USER`, so the OS-native dialog both
    enumerates existing current-user credentials and permits entering a new
    one). If the user enters a new credential, the flow calls
    `CredentialManager.WriteCredential(...)` before returning the chosen
    name. The resulting `CredentialName` is written back into the row's
    `CredentialStoreSecretSource`. The dialog remains manifest-agnostic —
    this affordance is entirely about `SecretSource` choice, not about
    manifests or scopes.

### API-shape reconciliation

The owner's sketch contains three inconsistencies. Reconciled decisions:

| Inconsistency in sketch | Reconciled decision |
|---|---|
| `RequestSecretsAsync` returns `List<SecretResult>` in one place and `RequestSecretsResult` in another; `SecretResult` is otherwise undefined. | Method returns `Task<RequestSecretsResult?>`. `SecretResult` is dropped in favour of `SecretRetriever` (for successes) and `SecretRequestFailure` (for failures). |
| "Returns null if secrets are not permitted"; "Returns empty if no secrets requested". | `null` is reserved for the *global-refusal* case (user clicked `[No]`, or a policy disables secret use entirely). A **non-null** result with empty `AcquiredSecrets` and empty `FailedSecrets` is returned when the request list itself is empty. |
| `SecretRetriever.Secret` typed as `Func<Task<SecureString>>`. | Kept as `Func<CancellationToken, Task<SecureString>>` so retrieval is lazy (the platform store is only hit when the caller actually needs the value) and cancellable. |

---

## Options

Five decisions have real alternatives worth discussing:
**A.** Backend store abstraction.
**B.** Where secret-use scanning + consent hooks into agent materialization.
**C.** The consent-memory scope model — a single hash vs a scope hierarchy.
**D.** How many OS backends ship in this feature.
**E.** Failure policy on per-secret resolution failure.

### A. Backend store abstraction

#### Option A1 — Use the maintained `Meziantou.Framework.Win32.CredentialManager` NuGet (chosen)

`WindowsCredentialManagerSecretStore : IPlatformSecretStore` is a thin
adapter over `Meziantou.Framework.Win32.CredentialManager.CredentialManager`:

- `ReadAsync(name)` → `CredentialManager.ReadCredential($"Phantom.Workspaces:{name}")`
- `WriteAsync(name, secret)` → `CredentialManager.WriteCredential(applicationName: $"Phantom.Workspaces:{name}", …)`
- `DeleteAsync(name)` → `CredentialManager.DeleteCredential(...)`
- `EnumerateNamesAsync(prefix)` → `CredentialManager.EnumerateCredentials("Phantom.Workspaces:*")`
- `PromptForCredentialAsync(ownerHwnd, message, caption)` (new; used by the
  Value-Source `[…]` picker flow) → `CredentialManager.PromptForCredentials(owner, message, caption, userName)`.

**Package facts (research-verified):**
- Package id: `Meziantou.Framework.Win32.CredentialManager`.
- Current version: **v3.0.1** (2026-07-08 release), semantically-versioned,
  actively maintained (~weekly cadence), ~500k downloads.
- **Licence: MIT.**
- **Zero runtime dependencies.** The only listed dependency,
  `Microsoft.Windows.CsWin32`, is a build-time Roslyn source generator that
  produces the P/Invoke stubs at compile time and does not ship at runtime.
- Types are annotated `[SupportedOSPlatform("windows")]`.
- Wraps `CredRead` / `CredWrite` / `CredDelete` / `CredEnumerate`
  (`EnumerateCredentials(filter?)` supports wildcard `"Phantom.Workspaces:*"`
  for a saved-credential picker list) and
  `CredUIPromptForWindowsCredentials` via `PromptForCredentials(owner,
  message, caption, userName)` with `CREDUIWIN_ENUMERATE_CURRENT_USER` so
  the native dialog **both** enumerates existing current-user credentials
  **and** allows entering a new one — exactly what the `[Saved Credential]
  […]` flow needs.

**Pros:**
- Zero hand-rolled P/Invoke; smaller maintenance surface.
- Actively maintained, MIT-licensed, zero runtime deps.
- Includes the native prompt API needed for the `[…]` picker flow — nothing
  else in the .NET BCL surface offers this.
- Enumeration support (`EnumerateCredentials("Phantom.Workspaces:*")`) is
  built-in — useful both for the in-app source dropdown and for the picker.
- The class stays `[SupportedOSPlatform("windows")]` and the non-Windows
  composition selects `NullPlatformSecretStore`, matching the existing
  OS-gated pattern in `GitHubAuthTokenResolver.cs:23`
  (`OperatingSystem.IsWindows()`).

**Cons:**
- Adds one new runtime NuGet reference. Mitigated by the licence (MIT),
  zero runtime deps, active maintenance, and the fact that the alternative
  (hand-rolled P/Invoke plus a hand-rolled `CredUIPromptForWindowsCredentials`
  wrapper for the `[…]` flow) would materially exceed the risk it saves.

#### Considered / Background — alternatives not chosen

- **Hand-rolled `advapi32` P/Invoke (`CredReadW`/`CredWriteW`/`CredDeleteW`/`CredEnumerateW`, plus `credui.dll` `CredUIPromptForWindowsCredentialsW`).**
  Viable and dependency-free but redundant given the Meziantou package
  covers the same functions with an identical shape, including the native
  prompt. Rejected in favour of A1 to avoid re-implementing a maintained
  wrapper. Retained here only for context.
- **`AdysTech.CredentialManager` NuGet.** MIT-licensed alternative wrapper
  with similar API surface. Rejected against Meziantou on lower adoption /
  maintenance activity; retained here as a fallback if Meziantou ever
  becomes unavailable.
- **`Devlooped.CredentialManager` (repackaged Git Credential Manager store).**
  Rejected for this use even as a fallback: has **no credential
  enumeration** (breaks the source-picker dropdown and the `[…]` picker
  list), **no native prompt** (breaks the `[…]` enter-new flow), and has an
  OSMF (Open Software for Startups / MSFT-adjacent) licence with a
  commercial-fee concern that would need review. Left as a distant fallback
  in future work only.
- **WinRT `Windows.Security.Credentials.PasswordVault`.** Windows-only,
  no native picker/prompt, and lives in an app-scoped vault distinct from
  the Windows Credential Manager the user manages in Control Panel —
  contrary to the owner's directive.
- **DPAPI (`System.Security.Cryptography.ProtectedData`).** An encryption
  primitive, not a keyed store; would still require inventing a file
  format, key namespace, and enumeration. Not a substitute for a real
  credential store. Also fails the "visible in the Credential Manager
  control panel" ergonomics goal.
- **User Secrets (`dotnet user-secrets`).** Dev-time plaintext store for
  ASP.NET Core configuration; unencrypted; not intended for runtime user
  secrets.
- **MAUI `SecureStorage`.** MAUI-only; not available to an Avalonia
  application.
- **DPAPI-protected JSON file under `%APPDATA%\Phantom.Workspaces\`.**
  Considered but fails the owner's explicit "use Windows Credential
  Manager" directive; user discoverability and rotation semantics are
  worse.

There is **no first-party portable .NET BCL API** for named OS secrets;
`Meziantou.Framework.Win32.CredentialManager` is the closest to a
canonical wrapper in the .NET ecosystem for the Windows case.

**Recommendation: Option A1.**

### B. Where secret-use scanning + consent hooks in

#### Option B1 — Inside `AgentDefinitionParameterSubstitutor.Substitute`

Extend the loop at
Phantom.Workspaces.Llm.Core\AgentDefinitionParameterSubstitutor.cs:30-49 so that
after user-parameter substitution any residual `${SECRET:...}` placeholder in
`Model.Options.AdditionalProperties` is discovered.

**Pros:** Single, already-central substitution seam.
**Cons:** `Substitute` is *synchronous* today; showing a dialog is not. Also does
not cover `Tool.Options` (which currently never receives substitution — the
subagent research confirmed that tool options flow untouched into
`AgentChat.InitializeMcpToolsAsync` and `AgentFactory.CreateAgentChatAsync`).
Making the substitutor async and expanding its scope pulls too much into one
seam.

#### Option B2 — New async wrapper `AgentDefinitionSecretMaterializer` called between substitute and `CreateChatClient`

Add a new async step invoked by the code paths that today go
`Substitute` → `CreateChatClientAsync`. The concrete call site is
`AgentFactory.CreateChatClientAsync` (main entry, at
Phantom.Workspaces.Llm.Core\AgentFactory.cs), which currently receives a
substituted `AgentDefinition`. The new materializer:

1. Walks the definition (both `PromptAgent.Model.Options.AdditionalProperties`
   and every `Tool.Options`) with a `SecretUsageScanner` that finds every
   `${SECRET:Name}` string value and records its JSON path
   (`"definition.model.additionalOptions.ApiToken"`,
   `"definition.tools.aws.additionalOptions.ApiKey"`).
2. Builds one `SecretRequest` per hit.
3. Calls `ISecretProvider.RequestSecretsAsync(requests, manifestContentHash)`.
4. Rewrites the placeholders in-place with the resolved values (or removes
   the option / fails the materialization if resolution failed, depending on a
   per-request policy — default: fail materialization when any required secret
   fails; see §Detailed design).

**Pros:** Async by design; covers both model options and tool options; keeps
`Substitute` narrow and synchronous; localises the security-critical step in one
class that is easy to test in isolation.
**Cons:** One additional class in the pipeline.

#### Option B3 — Inside each `CreateXxxClient` (`CreateGitHubCopilotClient`, etc.)

Have every provider call an `IApiKeyResolver`-like resolver that also handles
`${SECRET:...}`.

**Pros:** Reuses existing `IApiKeyResolver` shape.
**Cons:** N call-sites to keep aligned; the dialog would fire N times (once per
provider) unless a batching layer sits above; secrets in tool options
(non-provider paths) are missed entirely.

**Recommendation: Option B2.**

### C. Consent-memory scope model

The invariant is: *a content edit to the manifest that affects how or where a
secret would be used must invalidate the prior consent, but a user should not
be re-prompted every session for the same secret in stable contexts.* Three
coherent options for the memory model:

#### Option C1 — Single hash key, one scope per record (superseded)

The original recommendation was a single fixed preimage
`SHA256("phantom.workspaces/secret-store/v1" + canonical(manifest.Template) +
secretName + useDisplayString)` per consent record, with only "This Use",
"All Uses", and "Always Ask" exposed to the user. Two problems: (a) it
conflates "this manifest's content" with "this specific use path", offering
no way for the user to consent at intermediate scopes; (b) "All Uses" was
under-specified — it could mean "any manifest, any path" or "this manifest,
any path".

**Superseded by C2** below. Retained here for context.

#### Option C2 — Caller-supplied `SecretUseMemory` candidates over a scope hierarchy (chosen)

The `Remember?` drop-down offers a caller-defined **ordered set** of scopes,
broadest → narrowest. Each scope has (i) a `DisplayString` shown in the
drop-down, and (ii) a scope-specific SHA-256 hash preimage. The
`ISecretProvider` is scope-agnostic: for each request it walks the caller's
candidate list and auto-approves against any stored hash match; otherwise it
prompts and persists the chosen candidate.

For the agent-manifest caller, the ordered scope list is:

| Scope | Display | Preimage suffix |
|---|---|---|
| `AllUses` | "All Uses" | `\|scope=all-uses` |
| `AnyManifest` | "Any Manifest" | `\|scope=any-manifest\|secret={secretName}` |
| `KeyInAnyManifest` | "This Key in Any Manifest" | `\|scope=key-any-manifest\|secret={secretName}\|use={useDisplayString}` |
| `ManifestIdentity` | "This Manifest, Even if Changed" | `\|scope=manifest-identity\|manifestId={stableManifestIdentity}\|secret={secretName}` |
| `ManifestContent` | "This Manifest" | `\|scope=manifest-content\|manifestHash={sha256(canonical(manifest.Template))}\|secret={secretName}` |
| `KeyInManifestContent` | "This Key in This Manifest" | `\|scope=key-manifest-content\|manifestHash={…}\|secret={secretName}\|use={useDisplayString}` |
| `AlwaysAsk` | "Always Ask" | (hash is `""`, never matches, never persisted) |

Every preimage is prefixed with the literal
`phantom.workspaces/secret-store/v1`. All fields are UTF-8 and pipe-delimited.
See §Detailed design → `SecretUseScope` for the concrete C# code.

**On `AllUses` and whether it embeds `secretName`.** The owner said "All Uses
means literally all uses". The strict reading is that `AllUses` is not even
secret-name-specific: a single `AllUses` grant tells the app "for this user,
never ask again about any secret in any manifest at any key". We adopt that
reading. The trade-off is that `AllUses` is *the most powerful* consent the
user can grant and is deliberately placed at the top of the drop-down where a
user is unlikely to select it by accident. Users who want "any manifest, any
use, but only *this* named secret" should select `AnyManifest` instead;
`AnyManifest` embeds `secretName` and gives them exactly that.

**Pros:**
- Exactly matches the owner's mental model of scoped consent.
- Content-keyed scopes (`ManifestContent`, `KeyInManifestContent`) preserve
  the security invariant: any edit to `manifest.Template` invalidates them.
- Identity-keyed scope (`ManifestIdentity`) gives an ergonomic opt-in for
  users who accept the "edits may reuse the consent" trade-off, and is
  labelled to make that obvious.
- The `ISecretProvider` and the dialog stay manifest-agnostic; scope
  construction is entirely on the caller side
  (`AgentManifestSecretUseMemoryFactory`), so future callers can define their
  own scope hierarchies without touching provider/dialog code.

**Cons:**
- Seven scopes is a lot to expose in a drop-down. Mitigated by the ordered
  broadest → narrowest presentation and by a sensible default selection
  (§Detailed design recommends `KeyInManifestContent` as the pre-selected
  narrowest content-keyed scope).

**Recommendation: Option C2.**

#### Option C3 — Hash the whole manifest JSON (background)

Canonicalise the entire `AgentManifest` document and SHA-256 it as the
manifest key. Considered under the earlier single-hash model. Rejected as
too aggressive: an unrelated edit (e.g. `displayName`) invalidates every
consent for that manifest. Superseded by C2, which lets the caller pick the
appropriate scope granularity.

### D. How many OS backends ship in this feature

#### Option D1 — Windows only, `NullPlatformSecretStore` elsewhere (chosen)

Only `WindowsCredentialManagerSecretStore` is implemented. Non-Windows
platforms get `NullPlatformSecretStore`, which returns `null` from reads and
throws `PlatformNotSupportedException` from writes. Users on macOS/Linux
still get login-source paths (GitHub, AWS placeholder, Azure placeholder) but
cannot persist their own credentials. This is the shape the owner confirmed
after Q4: macOS and Linux backends are **future work**, not part of this
feature. The `IPlatformSecretStore` seam is preserved so a later feature can
add them without disturbing this one — see §Future work.

**Pros:** Minimum scope; fits owner's explicit requirements literally;
clean cross-platform seam preserved for later.
**Cons:** Non-Windows users can't use `CredentialStoreSecretSource` in this
feature. Accepted trade-off.

#### Option D2 — Windows + macOS + Linux (rejected for this feature — see Future work)

Add `MacOsKeychainSecretStore` (Security.framework `SecItem*` P/Invoke, or
shelling to `security` CLI) and `LinuxSecretServiceSecretStore` (libsecret
P/Invoke or DBus Secret Service) as additional concrete implementations
selected at composition time by an `OperatingSystem.IsXxx()` check.
Considered and deferred: extra P/Invoke surface, needs runtime testing on
macOS/Linux CI, and no user of Phantom.Workspaces has yet asked for it.
Retained in §Future work with concrete implementation options.

**Recommendation: Option D1.** macOS and Linux backends are out of scope for
this feature; the `IPlatformSecretStore` seam keeps them additive.

### E. Failure policy on per-secret resolution failure

#### Option E1 — Silently drop the placeholder (background / rejected)

If a `${SECRET:X}` resolves to a `SecretRequestFailure`, remove the option
value from the outbound definition (analogous to
`AgentDefinitionParameterSubstitutor.cs:43-48`, which removes keys whose
value is an unresolved optional parameter placeholder).

**Cons:** Silently strips a security-sensitive value; hides misconfiguration
from the user; can cause bewildering downstream failures inside chat-client
constructors that expected an API key.

**Rejected.**

#### Option E2 — Fail chat-client creation (chosen)

If any placeholder resolves to a `SecretRequestFailure`, or if the provider
returned `null` (global refusal), `AgentDefinitionSecretMaterializer` throws
and `AgentFactory.CreateChatClientAsync` propagates the exception. The user
sees a clear error identifying (a) which secret failed and (b) why (e.g.
"AWS login is not yet implemented", "Credential 'AwsProdKey' does not
exist").

**Pros:** Fail-closed; no silent secret dropping; clean error surface for the
"Not yet implemented" AWS/Azure placeholders.
**Cons:** None material.

**Recommendation: Option E2.**

---

## Chosen design

**Approach:** **A1** (Windows Credential Manager via the
`Meziantou.Framework.Win32.CredentialManager` NuGet, v3.0.1, MIT, zero
runtime deps) + **B2** (new async `AgentDefinitionSecretMaterializer`
between substitution and chat-client creation) + **C2** (caller-supplied
`SecretUseMemory` candidates over a seven-scope hierarchy, with
content-keyed scopes hashing `canonicalJson(manifest.Template)` and
identity-keyed scope hashing the manifest `entity-id`) + **D1**
(**Windows-only** in this feature; `NullPlatformSecretStore` on macOS/Linux;
macOS/Linux backends moved to §Future work) + **E2** (fail-closed on
per-secret resolution failure). The dialog's `Value Source` column gains a
**[…]** button on the `[Saved Credential]` source that invokes
`CredentialManager.PromptForCredentials` for a native enter-new/select-existing
flow.

**Rationale:**

- **A1** picks a maintained, MIT-licensed, zero-runtime-dependency wrapper
  over hand-rolling `advapi32` and `credui.dll` P/Invoke. It is the only
  option that gives the codebase both `CredEnumerate` (needed for saved-
  credential picker lists) and `CredUIPromptForWindowsCredentials` (needed
  for the `[…]` enter/select flow) without inventing a second parallel
  P/Invoke surface. The single new NuGet dependency is justified by the
  amount of Win32 surface it avoids; see §Options A → Considered/Background
  for alternatives.

- **B2** is the only option that (a) is async-friendly and (b) covers both
  `Model.Options` and `Tool.Options` in one pass. The extra class is worth it
  because it makes the security-critical secret-scan-and-inject step a single
  named type with a single seam, which is exactly what we want to review, test,
  and reason about.

- **C2** replaces a single per-record hash with a caller-side scope hierarchy.
  Content-keyed scopes preserve the "if the manifest changes the secret is not
  leaked" invariant; identity-keyed and cross-manifest scopes give users
  ergonomic escapes from re-prompting where they have consciously chosen to
  accept the trade-off; the dialog stays manifest-agnostic. `AllUses` is
  interpreted literally per the owner: not even secret-name-specific — the
  broadest reasonable option, clearly labelled.

- **D1** ships Windows-only for this feature per owner confirmation of Q4.
  The `IPlatformSecretStore` seam is preserved so a follow-up feature can
  add macOS (Keychain / Security.framework) and Linux (libsecret /
  Secret-Service DBus) backends additively — see §Future work.

- **E2** fail-closed is required to keep the AWS/Azure placeholder sources
  honest: because those resolvers always return `SecretRequestFailure`, any
  manifest that opts into them for a real secret will fail chat-client
  creation with a legible error. Silent-drop mode is explicitly rejected.

- The `ManifestIdentity` scope depends on a **stable manifest identity**.
  Agent manifests are stored as workspace entities and inherit an
  `entity-id` (uuid) from `entity.json`
  (`Phantom.Workspaces.Data.Core\JsonSchemas\agent-manifest.json`, which
  extends `entity.json`). This uuid survives edits to any manifest field
  including `name`, `displayName`, `template`. It is the correct choice for
  `ManifestIdentity` because it is (i) globally unique per manifest instance
  and (ii) truly stable across content edits. When a manifest is not persisted
  as an entity (e.g. in tests that pass a synthesised `AgentManifest`), the
  `AgentManifestSecretUseMemoryFactory` omits `ManifestIdentity` from the
  candidate list.

- The **manifest content hash** used by `ManifestContent` /
  `KeyInManifestContent` is `SHA256(CanonicalJson.Encode(manifest.Template))`.
  `template` is the `AgentDefinition` field of the manifest schema
  (features\Phantom.Workspaces.Llm.Core\JsonSchemas\agent-manifest.json;
  referenced at features\Phantom.Workspaces.Llm.Core\AgentDefinitionParameterSubstitutor.cs:22-24)
  and is where every `${SECRET:...}` placeholder actually lives, so hashing it
  is both sufficient and minimal for the content-keyed invariant.

---

## Detailed design

### Code organisation

New projects: **none.** Everything lives in existing projects.

New files:

| Path (under `features\`) | Purpose |
|---|---|
| `Phantom.Workspaces.Llm.Core\Secrets\ISecretProvider.cs` | Public contract (§Classes). |
| `Phantom.Workspaces.Llm.Core\Secrets\SecretRequest.cs` | Data model. |
| `Phantom.Workspaces.Llm.Core\Secrets\SecretSource.cs` | Base + sealed subclasses (`GitHubLoginSecretSource`, `AwsLoginSecretSource`, `AzureLoginSecretSource`, `CredentialStoreSecretSource`). |
| `Phantom.Workspaces.Llm.Core\Secrets\SecretUseMemory.cs` | Data model (`DisplayString`, `Hash`). |
| `Phantom.Workspaces.Llm.Core\Secrets\SecretUseScope.cs` | Enum of the seven scopes + `SecretUseScopePreimage` static helper. |
| `Phantom.Workspaces.Llm.Core\Secrets\MemorizedSecret.cs` | Persisted record. |
| `Phantom.Workspaces.Llm.Core\Secrets\SecretRetriever.cs` | Lazy `SecureString` accessor. |
| `Phantom.Workspaces.Llm.Core\Secrets\RequestSecretsResult.cs` | Aggregate result. |
| `Phantom.Workspaces.Llm.Core\Secrets\SecretRequestFailure.cs` | Failure record + enum. |
| `Phantom.Workspaces.Llm.Core\Secrets\IPlatformSecretStore.cs` | Backend contract. |
| `Phantom.Workspaces.Llm.Core\Secrets\WindowsCredentialManagerSecretStore.cs` | Thin adapter over `Meziantou.Framework.Win32.CredentialManager.CredentialManager` (`ReadCredential` / `WriteCredential` / `DeleteCredential` / `EnumerateCredentials(filter)` / `PromptForCredentials(owner, message, caption, userName)`). `[SupportedOSPlatform("windows")]`. |
| `Phantom.Workspaces.Llm.Core\Secrets\NullPlatformSecretStore.cs` | Fallback for macOS/Linux and any platform without a concrete backend. |
| `Phantom.Workspaces.Llm.Core\Secrets\IAllowedSecretsStore.cs` + `AllowedSecretsStore.cs` | JSON-backed hash → `MemorizedSecret` map. |
| `Phantom.Workspaces.Llm.Core\Secrets\CanonicalJson.cs` | Sorted-key canonical encoder. |
| `Phantom.Workspaces.Llm.Core\Secrets\SecretUsageScanner.cs` | Walks an `AgentDefinition` for `${SECRET:Name}` uses. |
| `Phantom.Workspaces.Llm.Core\Secrets\ICredentialPicker.cs` | Abstraction over "let the user enter a new or select an existing saved credential" for the dialog's `[…]` button. Returns the chosen `CredentialName` (or `null` if cancelled). |
| `Phantom.Workspaces\Services\Secrets\WindowsCredentialPicker.cs` | Windows implementation of `ICredentialPicker` — calls `CredentialManager.PromptForCredentials(ownerHwnd, message, caption, userName)`; if the user entered a new credential, calls `CredentialManager.WriteCredential(...)` before returning the resulting credential name. Takes an `IHwndProvider` to obtain the owner HWND from the current Avalonia `Window`. |
| `Phantom.Workspaces\Services\Secrets\NullCredentialPicker.cs` | Non-Windows fallback that returns `null` (the `[…]` button is disabled off-Windows). |
| `Phantom.Workspaces\Services\Secrets\IHwndProvider.cs` + `AvaloniaHwndProvider.cs` | Resolves the current dialog's owner HWND via `((IClassicDesktopStyleApplicationLifetime)App.Current!.ApplicationLifetime!).MainWindow?.TryGetPlatformHandle()?.Handle`, mirroring how the existing settings dialog obtains its owner window at features\Phantom.Workspaces\App.axaml.cs:97-209 (`desktop.MainWindow` is passed to `ShowDialog(mainWindow)` in the settings-window code path). |
| `Phantom.Workspaces.Llm.Core\Secrets\AgentManifestSecretUseMemoryFactory.cs` | Caller-side factory that maps a `(manifest, useDisplayString, secretName)` triple to the ordered `SecretUseMemory` candidate list. Uses the manifest `entity-id` for `ManifestIdentity` and `CanonicalJson.Encode(manifest.Template)` for the content-keyed scopes. |
| `Phantom.Workspaces.Llm.Core\Secrets\AgentDefinitionSecretMaterializer.cs` | Orchestrates scan → request → rewrite. |
| `Phantom.Workspaces.Llm.Core\Secrets\SecretMaterializationRefusedException.cs` | Thrown when provider returns `null` (global refusal). |
| `Phantom.Workspaces.Llm.Core\Secrets\SecretMaterializationFailedException.cs` | Thrown when any requested secret is in `FailedSecrets`. Carries the failure list. |
| `Phantom.Workspaces.Llm.Core\Secrets\SecretUseDialogInput.cs` | Manifest-agnostic dialog input. |
| `Phantom.Workspaces.Llm.Core\Secrets\SecretUseDialogResult.cs` | Manifest-agnostic dialog result. |
| `Phantom.Workspaces.Llm.Core\Secrets\ISecretUseDialogHost.cs` | Interface implemented by the GUI project so `Llm.Core` never sees Avalonia. |
| `Phantom.Workspaces\Views\SecretUseDialogWindow.axaml` (+ `.cs`) | Avalonia view. |
| `Phantom.Workspaces\ViewModels\SecretUseDialogViewModel.cs` | ViewModel with rows, drop-downs, `[Yes]`/`[No]` commands. Mirrors `ShellSettingsDialogViewModel` pattern (features\Phantom.Workspaces\ViewModels\ShellSettingsDialogViewModel.cs). |
| `Phantom.Workspaces\Services\Secrets\AvaloniaSecretUseDialogHost.cs` | `ISecretUseDialogHost` implementation that calls `Window.ShowDialog(owner)`. |
| `Phantom.Workspaces\Services\Secrets\SecretProvider.cs` | Concrete `ISecretProvider` composing the store, allowed-secrets store, dialog host. Kept here (not in `Llm.Core`) because it depends on the GUI dialog host. |

Modified files:

| Path | Change |
|---|---|
| `Directory.Packages.props` | Add `<PackageVersion Include="Meziantou.Framework.Win32.CredentialManager" Version="3.0.1" />` (MIT, zero runtime deps). |
| `Phantom.Workspaces.Llm.Core\Phantom.Workspaces.Llm.Core.csproj` | Add `<PackageReference Include="Meziantou.Framework.Win32.CredentialManager" />` (centrally versioned). Guarded by Windows-only usage inside `WindowsCredentialManagerSecretStore`. |
| `Phantom.Workspaces\Services\ApplicationServices.cs` | Add `ISecretProvider SecretProvider` property + ctor parameter. |
| `Phantom.Workspaces\App.axaml.cs` (`OnFrameworkInitializationCompleted`, around the current construction of `ApplicationServices` — this is the same seam that today constructs `RunningAgentChatTable`, `AgentPersistenceStoreCache`, and `ConfigurationPersistenceService`) | Construct `WindowsCredentialManagerSecretStore` (or `NullPlatformSecretStore`), `AllowedSecretsStore`, `AvaloniaSecretUseDialogHost`, and `SecretProvider`; pass the provider into `ApplicationServices`. |
| `Phantom.Workspaces.Llm.Core\AgentFactory.cs` (`CreateChatClientAsync`) | After `AgentDefinitionParameterSubstitutor.Substitute`, call `AgentDefinitionSecretMaterializer.MaterializeAsync(definition, manifest, secretProvider, cancellationToken)`; abort with a typed exception if it returns `null`. `services.SecretProvider` is passed in via `AgentServices`. |
| `Phantom.Workspaces.Llm.Core\AgentServices` (existing service bag consumed by `AgentFactory`) | Add `ISecretProvider? SecretProvider` slot. |
| `Phantom.Workspaces.Llm.Core\AgentFactory.CreateGitHubCopilotClient` (line ~761–800) | Where the token is currently fetched via `IApiKeyResolver` / `GitHubAuthTokenResolver`, if the manifest uses `${SECRET:...}` for the API key the resolved value is already in-place. Otherwise the existing GitHub-CLI path is used unchanged. |

The GUI dialog and DI host live in `Phantom.Workspaces` (main GUI project),
matching where `ShellSettingsDialogWindow.axaml.cs` and `ShellSettingsDialogViewModel`
live today (features\Phantom.Workspaces\ShellSettingsDialogWindow.axaml.cs,
features\Phantom.Workspaces\ViewModels\ShellSettingsDialogViewModel.cs).

### Classes and interfaces

#### `ISecretProvider`

**Namespace:** `Phantom.Workspaces.Llm.Secrets`
**Kind:** `interface`
**Responsibility:** The single globally-available entry point for turning a
list of `SecretRequest`s into resolved `SecretRetriever`s (or failures),
including any required user consent.

**Members:**
- `Task<RequestSecretsResult?> RequestSecretsAsync(IReadOnlyList<SecretRequest> requests, CancellationToken cancellationToken)`
  — returns `null` iff the user refused globally (clicked `[No]`, or a policy
  bans secret use). Returns a non-null `RequestSecretsResult` in every other
  case, including the empty-list and no-consent-needed-for-any-request cases.

`ISecretProvider` is completely scope-agnostic and completely manifest-agnostic.
It receives the ordered `SecretUseMemory` candidate list embedded in each
`SecretRequest` and simply checks the stored `allowed-secrets.json` for any
matching `Hash`. Scope construction is entirely the caller's responsibility
(the agent-manifest caller uses `AgentManifestSecretUseMemoryFactory`; a future
caller — e.g. an MCP tool config screen — can define its own factory with a
different scope hierarchy).

#### `SecretRequest`

**Namespace:** `Phantom.Workspaces.Llm.Secrets`
**Kind:** `sealed record`
**Responsibility:** One row of the consent dialog — one specific use of one
specific named secret in a materialization.

**Members:**
- `string SecretName { get; init; }` — e.g. `"GithubApiToken"`.
- `string UseDisplayString { get; init; }` — e.g.
  `"definition.model.additionalOptions.ApiToken"`. Human-readable JSON path.
- `IReadOnlyList<SecretUseMemory> Memories { get; init; }` — the drop-down
  options in the `Remember?` column, ordered broadest → narrowest as produced
  by the caller-side factory. Always ends with `AlwaysAsk`. If a stored
  `MemorizedSecret` already matches one of these hashes, the request is
  auto-approved (no dialog).
- `SecretSource DefaultSecretSource { get; init; }` — the pre-selected value in
  the `Value Source` drop-down.
- `IReadOnlyList<SecretSource> CandidateSecretSources { get; init; }` — the full
  drop-down list.

#### `SecretUseScope` + `SecretUseScopePreimage`

**Namespace:** `Phantom.Workspaces.Llm.Secrets`
**Kind:** `enum` + `internal static class`

```csharp
public enum SecretUseScope
{
    AllUses,
    AnyManifest,
    KeyInAnyManifest,
    ManifestIdentity,
    ManifestContent,
    KeyInManifestContent,
    AlwaysAsk,
}

internal static class SecretUseScopePreimage
{
    public const string VersionPrefix = "phantom.workspaces/secret-store/v1";

    public static string Build(
        SecretUseScope scope,
        string secretName,
        string useDisplayString,
        string? stableManifestIdentity,
        string? manifestContentHash);
}
```

**Preimage rules** (all fields UTF-8, pipe-delimited):

| Scope | Preimage |
|---|---|
| `AllUses` | `"phantom.workspaces/secret-store/v1\|scope=all-uses"` |
| `AnyManifest` | `"phantom.workspaces/secret-store/v1\|scope=any-manifest\|secret={secretName}"` |
| `KeyInAnyManifest` | `"phantom.workspaces/secret-store/v1\|scope=key-any-manifest\|secret={secretName}\|use={useDisplayString}"` |
| `ManifestIdentity` | `"phantom.workspaces/secret-store/v1\|scope=manifest-identity\|manifestId={stableManifestIdentity}\|secret={secretName}"` |
| `ManifestContent` | `"phantom.workspaces/secret-store/v1\|scope=manifest-content\|manifestHash={manifestContentHash}\|secret={secretName}"` |
| `KeyInManifestContent` | `"phantom.workspaces/secret-store/v1\|scope=key-manifest-content\|manifestHash={manifestContentHash}\|secret={secretName}\|use={useDisplayString}"` |
| `AlwaysAsk` | (n/a — `SecretUseMemory.Hash` is `""` and never persisted) |

`AllUses` deliberately embeds neither `secretName` nor manifest data — see
§Options C2 for the rationale.

#### `SecretSource` hierarchy

**Namespace:** `Phantom.Workspaces.Llm.Secrets`
**Kind:** `abstract record` + sealed subclasses. Uses
`[JsonPolymorphic]`/`[JsonDerivedType]` so it round-trips through the
`AllowedSecretsStore` JSON with the same serializer as
`ConfigurationPersistenceService`.

- `abstract record SecretSource(string DisplayString);`
- `sealed record GitHubLoginSecretSource() : SecretSource("Current GitHub Login");`
  — delegates to `GitHubAuthTokenResolver.ResolveAsync`
  (features\Phantom.Workspaces.Llm.Core\GitHubAuthTokenResolver.cs:39).
- `sealed record AwsLoginSecretSource() : SecretSource("Current AWS Login (not yet implemented)");`
  — **placeholder**. Its resolver in `SecretProvider` always returns
  `SecretRequestFailure(SecretName, "AWS login is not yet implemented",
  SecretRequestFailureReason.Other)`. Visible in the dialog drop-down so users
  can see it as an intended future path.
- `sealed record AzureLoginSecretSource() : SecretSource("Current Azure Login (not yet implemented)");`
  — same placeholder pattern; failure message `"Azure login is not yet implemented"`.
- `sealed record CredentialStoreSecretSource(string CredentialName) : SecretSource($"Saved credential: {CredentialName}");`
  — reads from `IPlatformSecretStore`. In the dialog, this source is the one
  that exposes the **[…]** button next to the Value Source dropdown; the
  button invokes `ICredentialPicker.PickAsync(...)` (see below) and, on a
  non-null result, replaces the row's source with a new
  `CredentialStoreSecretSource(pickedCredentialName)`.

Because of the fail-closed policy (§Requirement 10), selecting an
`AwsLoginSecretSource` or `AzureLoginSecretSource` at consent time will cause
`AgentDefinitionSecretMaterializer` to throw a
`SecretMaterializationFailedException` at chat-client-creation time carrying
the "not yet implemented" message. This is intentional: the user is not
silently misled into thinking the source works.

#### `SecretUseMemory`

**Namespace:** `Phantom.Workspaces.Llm.Secrets`
**Kind:** `sealed record`
**Members:**
- `SecretUseScope Scope { get; init; }` — which scope produced this memory.
- `string DisplayString { get; init; }` — e.g. `"This Manifest"`,
  `"Any Manifest"`, `"All Uses"`, `"Always Ask"`.
- `string Hash { get; init; }` — hex SHA-256 of the scope-specific preimage
  (see `SecretUseScopePreimage`). For `AlwaysAsk` the hash is `""` and never
  matches any stored record.

#### `AgentManifestSecretUseMemoryFactory`

**Namespace:** `Phantom.Workspaces.Llm.Secrets`
**Kind:** `sealed class`
**Responsibility:** Caller-side factory that turns a `(manifest,
useDisplayString, secretName)` triple into the ordered `SecretUseMemory`
candidate list expected by `SecretRequest.Memories`.

**Members:**
- `IReadOnlyList<SecretUseMemory> Build(AgentManifest manifest, string secretName, string useDisplayString)`
  — returns memories in the order:
  `AllUses, AnyManifest, KeyInAnyManifest, ManifestIdentity, ManifestContent,
  KeyInManifestContent, AlwaysAsk`.
  `ManifestIdentity` is omitted (skipped) when `manifest` has no stable id —
  see below.

**Stable manifest identity.** The factory reads the manifest's `entity-id`
uuid (inherited from `entity.json` — see
`Phantom.Workspaces.Data.Core\JsonSchemas\agent-manifest.json` which extends
`entity.json`, and `Phantom.Workspaces.Llm.Core\JsonSchemas\agent-manifest.json`
for the payload shape). If the manifest instance provided to the factory has a
non-empty `entity-id`, that string is used as `stableManifestIdentity`. If
not (test fixtures, ad-hoc manifests), `ManifestIdentity` is dropped from the
candidate list — those callers can still use `ManifestContent` /
`KeyInManifestContent`.

**Manifest content hash.** `SHA256(CanonicalJson.Encode(manifest.Template))`.
`manifest.Template` is the `AgentDefinition` field of the manifest schema
(features\Phantom.Workspaces.Llm.Core\JsonSchemas\agent-manifest.json;
consumed at features\Phantom.Workspaces.Llm.Core\AgentDefinitionParameterSubstitutor.cs:22-24)
— exactly the substrate the `${SECRET:...}` placeholders live in.

**Default selection in the dialog.** The factory records
`KeyInManifestContent` as the recommended default. The dialog view-model
pre-selects the recommended default when it renders a row.

#### `MemorizedSecret`

**Namespace:** `Phantom.Workspaces.Llm.Secrets`
**Kind:** `sealed record`
**Responsibility:** The persisted acceptance record. Stored in
`allowed-secrets.json`.
**Members:**
- `SecretUseMemory Memory { get; init; }`
- `SecretSource Source { get; init; }`
- `DateTime GrantedAt { get; init; }`

#### `SecretRetriever`

**Namespace:** `Phantom.Workspaces.Llm.Secrets`
**Kind:** `sealed class`
**Members:**
- `string SecretName { get; }`
- `Func<CancellationToken, Task<SecureString>> Secret { get; }` — lazy accessor.

#### `SecretRequestFailure` + `enum SecretRequestFailureReason`

**Namespace:** `Phantom.Workspaces.Llm.Secrets`
**Members:**
- `string SecretName`
- `string FailureReasonDisplayString`
- `enum { DoesntExist, ErrorReading, Other } Reason`.

#### `RequestSecretsResult`

**Namespace:** `Phantom.Workspaces.Llm.Secrets`
**Members:**
- `IReadOnlyList<SecretRetriever> AcquiredSecrets { get; init; }`
- `IReadOnlyList<SecretRequestFailure> FailedSecrets { get; init; }`

#### `IPlatformSecretStore`

**Namespace:** `Phantom.Workspaces.Llm.Secrets`
**Members:**
- `Task<SecureString?> ReadAsync(string name, CancellationToken ct);`
- `Task WriteAsync(string name, SecureString value, CancellationToken ct);`
- `Task DeleteAsync(string name, CancellationToken ct);`
- `Task<IReadOnlyList<string>> EnumerateNamesAsync(string prefix, CancellationToken ct);`

The composition root (`App.axaml.cs`) selects an implementation with an
`OperatingSystem.IsXxx()` cascade:

```csharp
IPlatformSecretStore platformStore =
    OperatingSystem.IsWindows() ? new WindowsCredentialManagerSecretStore() :
                                  new NullPlatformSecretStore();
```

macOS/Linux backends are **not** installed by this feature; see §Future work.
The cascade is kept as an `OperatingSystem.IsXxx()` chain (rather than a bare
`?:`) so future backends can be added by inserting arms without restructuring
the composition site.

#### `WindowsCredentialManagerSecretStore`

**Namespace:** `Phantom.Workspaces.Llm.Secrets`
**Attributes:** `[SupportedOSPlatform("windows")]`
**Responsibility:** Thin adapter over
`Meziantou.Framework.Win32.CredentialManager.CredentialManager` (v3.0.1,
MIT). Uses target-name prefix `"Phantom.Workspaces:"`.

**Sketch:**

```csharp
using Meziantou.Framework.Win32;

[SupportedOSPlatform("windows")]
public sealed class WindowsCredentialManagerSecretStore : IPlatformSecretStore
{
    private const string Prefix = "Phantom.Workspaces:";

    public Task<SecureString?> ReadAsync(string name, CancellationToken ct)
    {
        var cred = CredentialManager.ReadCredential(Prefix + name);
        return Task.FromResult(cred is null ? null : ToSecureString(cred.Password));
    }

    public Task WriteAsync(string name, SecureString value, CancellationToken ct)
    {
        CredentialManager.WriteCredential(
            applicationName: Prefix + name,
            userName: Environment.UserName,
            secret: FromSecureString(value),
            persistence: CredentialPersistence.LocalMachine);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string name, CancellationToken ct)
    {
        CredentialManager.DeleteCredential(Prefix + name);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> EnumerateNamesAsync(string prefix, CancellationToken ct)
    {
        var creds = CredentialManager.EnumerateCredentials(Prefix + "*");
        return Task.FromResult<IReadOnlyList<string>>(
            creds.Select(c => c.ApplicationName[Prefix.Length..]).ToArray());
    }
}
```

#### `WindowsCredentialPicker` (implements `ICredentialPicker`)

**Namespace:** `Phantom.Workspaces.Services.Secrets`
**Attributes:** `[SupportedOSPlatform("windows")]`
**Responsibility:** Powers the dialog's `[…]` button on the
`[Saved Credential]` value source. Delegates to
`CredentialManager.PromptForCredentials`, which internally calls
`CredUIPromptForWindowsCredentials` with `CREDUIWIN_ENUMERATE_CURRENT_USER`
— the OS-native dialog that both **enumerates existing current-user
credentials** and **allows the user to enter a new one**. If the user
entered a new credential, `WriteCredential` is called so the credential is
persisted under the `"Phantom.Workspaces:"` prefix and the resolved
`CredentialName` is returned to the dialog.

**Sketch:**

```csharp
[SupportedOSPlatform("windows")]
public sealed class WindowsCredentialPicker : ICredentialPicker
{
    private readonly IHwndProvider _hwnd;
    public WindowsCredentialPicker(IHwndProvider hwnd) => _hwnd = hwnd;

    public Task<string?> PickAsync(string? initialCredentialName, CancellationToken ct)
    {
        var result = CredentialManager.PromptForCredentials(
            owner: _hwnd.GetActiveHwnd(),
            messageText: "Select or enter a credential to use for this secret.",
            captionText: "Phantom.Workspaces — choose credential",
            userName: initialCredentialName ?? string.Empty);

        if (result is null)
            return Task.FromResult<string?>(null); // user cancelled

        // Persist newly-entered credentials so future picks enumerate them.
        var name = "Phantom.Workspaces:" + result.UserName;
        if (CredentialManager.ReadCredential(name) is null)
        {
            CredentialManager.WriteCredential(
                applicationName: name,
                userName: result.UserName,
                secret: result.Password,
                persistence: CredentialPersistence.LocalMachine);
        }
        return Task.FromResult<string?>(result.UserName);
    }
}
```

The owner HWND is obtained by `AvaloniaHwndProvider`, which resolves
`((IClassicDesktopStyleApplicationLifetime)App.Current!.ApplicationLifetime!).MainWindow`
— the same `desktop.MainWindow` used at features\Phantom.Workspaces\App.axaml.cs:97
and passed to `settingsWindow.ShowDialog(mainWindow)` at
features\Phantom.Workspaces\App.axaml.cs:209 (and to `settingsWindow.ShowDialog(this)` in
features\Phantom.Workspaces\MainWindow.axaml.cs:174) — and calls
`window.TryGetPlatformHandle()?.Handle` to get the Win32 HWND.
`SecretUseDialogWindow` (opened via `ShowDialog(mainWindow)` mirroring the
same pattern) can additionally supply its own handle if the picker is
launched while the dialog itself is on-screen.

#### `NullPlatformSecretStore`

Returns `null` from `ReadAsync`, throws `PlatformNotSupportedException` from
`WriteAsync` and `DeleteAsync`, empty list from `EnumerateNamesAsync`.
Selected as the fallback on macOS and Linux (until a future feature adds
concrete backends — see §Future work).

#### `IAllowedSecretsStore` / `AllowedSecretsStore`

**Namespace:** `Phantom.Workspaces.Llm.Secrets`
**Responsibility:** Read/write the `hash → MemorizedSecret` JSON file at
`%APPDATA%\Phantom.Workspaces\allowed-secrets.json`.
**Members:**
- `Task<MemorizedSecret?> TryGetAsync(string hash, CancellationToken ct);`
- `Task PutAsync(string hash, MemorizedSecret record, CancellationToken ct);`
- `Task<IReadOnlyDictionary<string, MemorizedSecret>> LoadAllAsync(CancellationToken ct);`

The JSON serializer options are **shared with** `ConfigurationPersistenceService`
(features\Phantom.Workspaces\Configuration\ConfigurationPersistenceService.cs:20-26):
camel-case, `WhenWritingNull`, indented, camel-case string-enum converter.

#### `CanonicalJson`

**Namespace:** `Phantom.Workspaces.Llm.Secrets`
**Kind:** `internal static class`
**Members:**
- `static string Encode(JsonElement element)` — deterministic form with sorted
  object keys and no insignificant whitespace.

#### `SecretUsageScanner`

**Namespace:** `Phantom.Workspaces.Llm.Secrets`
**Responsibility:** Walk an `AgentDefinition` (`PromptAgent.Model.Options.AdditionalProperties`
and every `Tool.Options` dictionary) collecting every string value that matches
`${SECRET:Name}`. For each hit, record `(SecretName, JsonPath)`.
**Pattern:** `@"\$\{SECRET:([^}]+)\}"` — sibling to
`AgentDefinitionParameterSubstitutor.SubstitutePlaceholders`
(features\Phantom.Workspaces.Llm.Core\AgentDefinitionParameterSubstitutor.cs L133-143 per subagent report).

**Members:**
- `IReadOnlyList<SecretUsage> Scan(AgentDefinition definition);`
- `void RewritePlaceholders(AgentDefinition definition, IReadOnlyDictionary<SecretUsage, string> resolvedValues);`

#### `AgentDefinitionSecretMaterializer`

**Namespace:** `Phantom.Workspaces.Llm.Secrets`
**Responsibility:** The orchestrator (see §Data flow). Fail-closed: throws
rather than silently dropping placeholders.

**Members:**
- `Task<AgentDefinition> MaterializeAsync(AgentManifest manifest, AgentDefinition definition, ISecretProvider secretProvider, CancellationToken ct);`
  — never returns `null`. Throws:
  * `SecretMaterializationRefusedException` when the provider returns `null`
    (global refusal / policy denial).
  * `SecretMaterializationFailedException` when
    `RequestSecretsResult.FailedSecrets` contains any entry that corresponds
    to a `${SECRET:...}` placeholder in the definition. The exception carries
    the full `IReadOnlyList<SecretRequestFailure>` so `AgentFactory` can
    surface an actionable error to the user (e.g. `"AWS login is not yet
    implemented"`, `"Credential 'AwsProdKey' does not exist"`).
  * On success, returns the definition with every `${SECRET:...}` placeholder
    substituted with the retrieved value.

The materializer uses `AgentManifestSecretUseMemoryFactory` internally to
build the ordered candidate list for each usage; the manifest object never
crosses the `ISecretProvider` boundary.

#### `ICredentialPicker`

**Namespace:** `Phantom.Workspaces.Llm.Secrets`
**Kind:** `interface`
**Responsibility:** Abstraction over the "Saved Credential `[…]`" enter/select
flow. Implemented on Windows by `WindowsCredentialPicker`, and by
`NullCredentialPicker` elsewhere. Injected into `SecretUseDialogViewModel`
so the view-model stays platform-agnostic and the view can bind the `[…]`
button to a command.

**Members:**
- `Task<string?> PickAsync(string? initialCredentialName, CancellationToken ct);`
  — returns the chosen `CredentialName`, or `null` if the user cancelled.
  On Windows, wraps `CredentialManager.PromptForCredentials(ownerHwnd,
  message, caption, userName)` (which surfaces the native
  `CredUIPromptForWindowsCredentials` with `CREDUIWIN_ENUMERATE_CURRENT_USER`
  — the OS-native picker/entry dialog) and calls `WriteCredential` for
  newly-entered values.
- `bool IsSupported { get; }` — `false` on non-Windows so the view can
  disable/hide the `[…]` button.

#### `SecretUseDialogInput` / `SecretUseDialogResult` / `ISecretUseDialogHost`

**Namespace:** `Phantom.Workspaces.Llm.Secrets`
**Responsibility:** Manifest-agnostic dialog contract. `Llm.Core` never sees
Avalonia; the GUI implements `ISecretUseDialogHost`.

- `record SecretUseDialogInput(IReadOnlyList<SecretRequest> Rows);`
- `record SecretUseDialogRow(SecretRequest Request, SecretUseMemory ChosenMemory, SecretSource ChosenSource);`
- `record SecretUseDialogResult(bool Accepted, IReadOnlyList<SecretUseDialogRow> Rows);`
- `interface ISecretUseDialogHost { Task<SecretUseDialogResult> ShowAsync(SecretUseDialogInput input, CancellationToken ct); }`

#### `SecretUseDialogViewModel` + `SecretUseDialogWindow.axaml`

**Namespace:** `Phantom.Workspaces.ViewModels` (viewmodel) /
`Phantom.Workspaces.Views` (view).
**Pattern:** Mirrors `ShellSettingsDialogViewModel`
(features\Phantom.Workspaces\ViewModels\ShellSettingsDialogViewModel.cs) — a
plain `ViewModelBase`-derived class with `RelayCommand`/`AsyncRelayCommand`
`YesCommand`/`NoCommand`. The view is opened via `Window.ShowDialog(owner)`
mirroring the shell-details dialog seam. The `AvaloniaSecretUseDialogHost`
wrapper resolves the current main window via the same convention used elsewhere
and awaits `ShowDialog`.

**Members:**
- `ObservableCollection<SecretUseDialogRowViewModel> Rows { get; }` (each has
  `SecretName`, `UseDisplayString`, `AvailableMemories`, `SelectedMemory`,
  `AvailableSources`, `SelectedSource`, and `AsyncRelayCommand PickCredentialCommand`).
- `RelayCommand YesCommand { get; }`
- `RelayCommand NoCommand { get; }`
- `bool? DialogResult { get; }`

**The `[…]` button (Saved-Credential enter/select flow).** Each row's
`PickCredentialCommand` is bound to a `[…]` button in the view. The command
is `CanExecute` only when the row's `SelectedSource` is a
`CredentialStoreSecretSource` and `ICredentialPicker.IsSupported` is true.
On execution the view-model calls
`await picker.PickAsync(currentSource.CredentialName, ct)`; if the result is
non-null it replaces the row's `SelectedSource` with a new
`CredentialStoreSecretSource(picked)` and refreshes `AvailableSources` so the
picked credential appears in the dropdown (the same list is populated at
startup by `IPlatformSecretStore.EnumerateNamesAsync("Phantom.Workspaces:")`).
The view-model still contains **no** manifest or scope references — the
`[…]` flow operates entirely on `SecretSource` values.

#### `SecretProvider` (concrete)

**Namespace:** `Phantom.Workspaces.Services.Secrets`
**Responsibility:** Composes `IAllowedSecretsStore`, `IPlatformSecretStore`,
`ISecretUseDialogHost`, and the `SecretSource` resolvers into the algorithm in
§Data flow.

### Data flow

The full end-to-end for one agent chat creation:

1. **Trigger.** A user opens an agent chat. `AgentFactory.CreateChatClientAsync`
   is called on the current `AgentManifest` with user parameter values.

2. **Substitute.** Existing call
   `AgentDefinitionParameterSubstitutor.Substitute(manifest, parameters)`
   expands `${user-parameter}` placeholders (features\Phantom.Workspaces.Llm.Core\AgentDefinitionParameterSubstitutor.cs:15-52).

3. **Scan for secret uses.** New call
   `AgentDefinitionSecretMaterializer.MaterializeAsync(manifest, definition, secretProvider, ct)`:
   1. `SecretUsageScanner.Scan(definition)` returns a list of
      `SecretUsage(SecretName, JsonPath)`.
   2. For each usage, call
      `AgentManifestSecretUseMemoryFactory.Build(manifest, secretName, useDisplayString)`
      which computes:
      * `stableManifestIdentity` = manifest's `entity-id` uuid (from the
        workspace-entity envelope). Omitted if the manifest has no entity id.
      * `manifestContentHash` = `SHA256(CanonicalJson.Encode(manifest.Template))`.
      * Emits the ordered `SecretUseMemory` candidate list (broadest →
        narrowest, ending in `AlwaysAsk`) with each memory's `Hash` derived
        via `SecretUseScopePreimage.Build(...)`.
   3. Build a `SecretRequest` per usage with those candidates, a
      `DefaultSecretSource` (heuristic: `GitHubLoginSecretSource` when
      `SecretName` contains `Github`; `AwsLoginSecretSource` when it contains
      `Aws`; `AzureLoginSecretSource` when it contains `Azure`; otherwise the
      first matching `CredentialStoreSecretSource` from
      `IPlatformSecretStore.EnumerateNamesAsync`), and a
      `CandidateSecretSources` list including all enumerated stored credentials
      plus the three login sources.
   4. Call `secretProvider.RequestSecretsAsync(requests, ct)`.

4. **Inside `SecretProvider.RequestSecretsAsync`:**
   1. Load allowed-secrets store: `allowed = await allowedSecretsStore.LoadAllAsync(ct)`.
   2. Partition requests:
      * *Pre-approved:* those where any `SecretUseMemory.Hash` in the request's
        `Memories` list matches a stored `MemorizedSecret`. The matched
        `MemorizedSecret.Source` is used directly.
      * *Needs consent:* everything else.
   3. If any *needs-consent* rows remain, build a `SecretUseDialogInput` and
      call `dialogHost.ShowAsync(input, ct)`.
      * If the result is `Accepted == false` → return `null` immediately.
      * Otherwise for each row where the chosen memory's `Scope != AlwaysAsk`,
        `await allowedSecretsStore.PutAsync(chosenMemory.Hash, new MemorizedSecret(...))`.
        `AlwaysAsk` selections are never persisted.
   4. For each request (pre-approved and newly-consented), materialize a
      `SecretRetriever` from its `SecretSource`:
      * `GitHubLoginSecretSource` → `GitHubAuthTokenResolver.ResolveAsync`
        (features\Phantom.Workspaces.Llm.Core\GitHubAuthTokenResolver.cs:39).
      * `CredentialStoreSecretSource` → `platformStore.ReadAsync(credentialName, ct)`.
      * `AwsLoginSecretSource` → `SecretRequestFailure(secretName, "AWS login is not yet implemented", Reason.Other)`.
      * `AzureLoginSecretSource` → `SecretRequestFailure(secretName, "Azure login is not yet implemented", Reason.Other)`.
      * Any read exception → `SecretRequestFailure(Reason.ErrorReading)`.
      * `null` return from platform store → `SecretRequestFailure(Reason.DoesntExist)`.
   5. Return `new RequestSecretsResult(retrievers, failures)`.

5. **Rewrite (fail-closed).** Back in `AgentDefinitionSecretMaterializer`:
   * If `RequestSecretsAsync` returned `null` → throw
     `SecretMaterializationRefusedException`. `AgentFactory.CreateChatClientAsync`
     propagates this (surfaced to the user as a benign "operation cancelled" /
     "consent denied" error).
   * If `RequestSecretsResult.FailedSecrets` contains any entry whose
     `SecretName` corresponds to a `${SECRET:...}` usage in the definition →
     throw `SecretMaterializationFailedException` with the failure list.
     `AgentFactory.CreateChatClientAsync` propagates this so chat-client
     creation fails as a whole; the caller shows the user which secret failed
     and why (including "not yet implemented" for the AWS/Azure placeholders).
   * Otherwise, for every `SecretUsage`, retrieve the matching `SecureString`,
     marshal to `string` for the minimum window required, and call
     `scanner.RewritePlaceholders` to replace the `${SECRET:Name}` substring
     in the JSON path. Return the rewritten `AgentDefinition`.

6. **Continue existing pipeline.** `AgentFactory.CreateChatClientAsync` proceeds
   to `CreateGitHubCopilotClient` / `CreateGitHubModelsClient` /
   `CreateOpenAiClient` / etc. Their existing `IApiKeyResolver` path
   (features\Phantom.Workspaces.Llm.Core\EnvironmentApiKeyResolver.cs:14) now
   sees a fully-resolved string and returns it unchanged.

### Tests

Test framework: **xUnit** (already used across the tree). GUI tests use
`[AvaloniaFact(Timeout=15_000)]` matching
`Phantom.Workspaces.Tests\ShellSettingsDialogViewModelTests.cs`. Method naming:
`Method_Scenario_ExpectedOutcome`.

#### `CanonicalJsonTests` — `Phantom.Workspaces.Llm.Core.Tests\Secrets\`

- `Encode_ObjectWithReorderedKeys_ProducesSameOutput`
- `Encode_NestedObjects_SortsAtEveryLevel`
- `Encode_NumberFormat_UsesInvariantCulture`
- `Encode_WhitespaceVariants_ProducesSameOutput`

#### `SecretUsageScannerTests` — `Phantom.Workspaces.Llm.Core.Tests\Secrets\`

- `Scan_NoPlaceholders_ReturnsEmpty`
- `Scan_ModelOptionsContainsSecret_ReturnsOneUsageWithModelPath`
- `Scan_ToolOptionsContainsSecret_ReturnsUsageWithToolPath`
- `Scan_SameSecretUsedTwice_ReturnsTwoUsagesWithDistinctPaths`
- `Scan_MixedEnvVarAndSecretPlaceholders_OnlyReturnsSecrets`
- `RewritePlaceholders_ReplacesInModelOptions`
- `RewritePlaceholders_ReplacesInToolOptions`

#### `SecretUseScopePreimageTests`

- `Build_AllUses_ProducesFixedPreimageIndependentOfSecretName`
- `Build_AnyManifest_EmbedsSecretNameNotUsePath`
- `Build_KeyInAnyManifest_EmbedsSecretNameAndUsePath`
- `Build_ManifestIdentity_EmbedsIdentityAndSecretNameNotContent`
- `Build_ManifestContent_EmbedsContentHashAndSecretName`
- `Build_KeyInManifestContent_EmbedsContentHashAndSecretNameAndUsePath`
- `Build_AllScopes_UseVersionPrefix`
- `Build_AllUses_TwoDifferentSecrets_ProduceSameHash`

#### `AgentManifestSecretUseMemoryFactoryTests`

- `Build_ReturnsCandidatesOrderedBroadestToNarrowest_EndingInAlwaysAsk`
- `Build_ManifestWithoutEntityId_OmitsManifestIdentityCandidate`
- `Build_ManifestWithEntityId_IncludesManifestIdentityCandidate`
- `Build_TwoManifestsSameSecret_KeyInManifestContentHashesDiffer`
- `Build_SameManifestEditedAnywhere_ContentScopeHashesChange`
- `Build_SameManifestEditedAnywhere_ManifestIdentityScopeHashUnchanged`
- `Build_AllUsesCandidate_HashIndependentOfSecretName`
- `Build_AlwaysAskCandidate_HashIsEmpty`

#### `WindowsCredentialManagerSecretStoreTests` — `Phantom.Workspaces.Llm.Core.Tests\Secrets\`

Uses the real `Meziantou.Framework.Win32.CredentialManager` package against
credentials named `"Phantom.Workspaces.Tests:{guid}"` to avoid colliding
with the user's real credentials; each test deletes its credential in a
`finally`.

- `Write_ThenRead_ReturnsSameValue` (`SkipUnless(OperatingSystem.IsWindows)`)
- `Read_Missing_ReturnsNull`
- `Delete_ExistingCredential_RemovesIt`
- `EnumerateNamesAsync_WithPrefix_ReturnsMatchingNames`

#### `WindowsCredentialPickerTests` — `Phantom.Workspaces.Tests\`

Uses a fake `IHwndProvider` and a wrapper seam over
`CredentialManager.PromptForCredentials` so the test does not actually
display the OS dialog.

- `PickAsync_UserEntersNewCredential_WritesItAndReturnsName` (`SkipUnless(OperatingSystem.IsWindows)`)
- `PickAsync_UserSelectsExistingCredential_ReturnsNameWithoutRewrite`
- `PickAsync_UserCancels_ReturnsNull`
- `IsSupported_OnWindows_IsTrue`
- `IsSupported_OnNonWindows_IsFalse` (via the `NullCredentialPicker` under test on Linux/macOS CI)

`NullPlatformSecretStoreTests`

- `ReadAsync_Always_ReturnsNull`
- `WriteAsync_Throws_PlatformNotSupportedException`

`NullCredentialPickerTests`

- `PickAsync_Always_ReturnsNull`
- `IsSupported_Always_ReturnsFalse`

#### `AllowedSecretsStoreTests`

- `PutAsync_Then_TryGetAsync_ReturnsSameRecord`
- `TryGetAsync_MissingHash_ReturnsNull`
- `LoadAllAsync_EmptyFile_ReturnsEmptyMap`
- `PutAsync_SecretValuesNeverPersisted` — inspects the file bytes to assert no
  `SecureString`-sourced content leaks in.

#### `AgentDefinitionSecretMaterializerTests`

Uses a fake `ISecretProvider`.

- `MaterializeAsync_NoSecretPlaceholders_ReturnsDefinitionUnchanged_AndProviderNotCalled`
- `MaterializeAsync_SinglePlaceholder_CallsProviderAndRewrites`
- `MaterializeAsync_ProviderReturnsNull_ThrowsSecretMaterializationRefusedException`
- `MaterializeAsync_ProviderReturnsFailureForRequestedSecret_ThrowsSecretMaterializationFailedException`
- `MaterializeAsync_ProviderReturnsFailureForRequestedSecret_ExceptionCarriesAllFailures`
- `MaterializeAsync_SecretInToolOptions_RewritesToolOption`
- `MaterializeAsync_PlaceholderNeverSilentlyDropped_OnAnyFailurePath`

#### `SecretProviderTests`

Uses a fake `IAllowedSecretsStore`, fake `IPlatformSecretStore`, fake
`ISecretUseDialogHost`, and a fake `GitHubAuthTokenResolver` shim.

- `RequestSecretsAsync_EmptyRequestList_ReturnsEmptyResult_WithoutShowingDialog`
- `RequestSecretsAsync_AllRequestsPreApproved_SkipsDialog`
- `RequestSecretsAsync_UnapprovedRequest_ShowsDialog`
- `RequestSecretsAsync_UserClicksNo_ReturnsNull`
- `RequestSecretsAsync_UserClicksYesWithContentScope_PersistsMemorizedSecret`
- `RequestSecretsAsync_UserClicksYesWithAlwaysAsk_DoesNotPersist`
- `RequestSecretsAsync_ManifestContentChanged_PreviousContentScopeConsentDoesNotMatch`
- `RequestSecretsAsync_ManifestContentChanged_ManifestIdentityScopeConsentStillMatches`
- `RequestSecretsAsync_AnyManifestConsentMatchesAcrossManifests`
- `RequestSecretsAsync_AllUsesConsentMatchesAcrossManifestsAndAcrossSecretNames`
- `RequestSecretsAsync_GitHubLoginSource_DelegatesToGitHubAuthTokenResolver`
- `RequestSecretsAsync_AwsLoginSource_ReturnsNotYetImplementedFailure`
- `RequestSecretsAsync_AzureLoginSource_ReturnsNotYetImplementedFailure`
- `RequestSecretsAsync_CredentialStoreSource_MissingCredential_ReturnsDoesntExistFailure`
- `RequestSecretsAsync_CredentialStoreSource_ReadThrows_ReturnsErrorReadingFailure`

#### `SecretUseDialogViewModelTests` — `Phantom.Workspaces.Tests\`

`[AvaloniaFact]`, matching `ShellSettingsDialogViewModelTests`.

- `Ctor_PopulatesRowsFromInput`
- `Row_MemoryDropdown_DisplaysAllProvidedCandidatesInOrder`
- `Row_MemoryDropdown_DefaultsToCallerRecommendedMemory`
- `Row_SourceDropdown_DisplaysAwsAndAzurePlaceholderEntries`
- `Row_SourceDropdown_DefaultsToDefaultSecretSource`
- `Row_SavedCredentialEllipsisCommand_CanExecute_OnlyWhenCredentialStoreSourceSelected`
- `Row_SavedCredentialEllipsisCommand_InvokesCredentialPicker_WithCurrentCredentialName`
- `Row_SavedCredentialEllipsisCommand_PickerReturnsName_ReplacesRowSourceWithCredentialStoreSource`
- `Row_SavedCredentialEllipsisCommand_PickerReturnsNull_LeavesRowUnchanged`
- `Row_SavedCredentialEllipsisCommand_PickerIsSupportedFalse_CommandCanExecuteFalse`
- `YesCommand_SetsDialogResultTrue_WithSelectedRows`
- `NoCommand_SetsDialogResultFalse`
- `Rendering_ViewModelDoesNotReferenceManifestOrScopeTypes` (compile-only assertion / mirror test)

#### `AgentFactoryTests` (additions)

- `CreateChatClientAsync_SecretInManifest_CallsSecretProviderBeforeCreatingClient`
- `CreateChatClientAsync_SecretProviderRefuses_ThrowsSecretMaterializationRefusedException`
- `CreateChatClientAsync_SecretProviderReportsFailure_ThrowsSecretMaterializationFailedException`
- `CreateChatClientAsync_AwsPlaceholderSelected_ThrowsWithNotYetImplementedMessage`
- `CreateChatClientAsync_NoSecretsInManifest_DoesNotCallSecretProvider`

#### `GitHubAuthTokenResolverTests` — coverage of the folding-in

- `ResolveAsync_ThroughGitHubLoginSecretSource_ReturnsEnvironmentValue_WhenSet`
  (a regression test of the existing resolver via the new `SecretSource` seam).

---

## Implementation plan

Each commit leaves the tree building with all fast tests passing. The plan
is 12 commits, all Windows-focused. macOS/Linux backends are §Future work
and are not part of this plan.

### Commit 1 — `[secret-store]` Data model + canonical JSON + scope preimages

**Scope:** Introduce the pure data types with no dependencies on Avalonia,
`AgentSchema`, or Win32:
`SecretUseScope`, `SecretUseScopePreimage`, `SecretUseMemory`,
`SecretSource` (+ subclasses `GitHubLoginSecretSource`, `AwsLoginSecretSource`,
`AzureLoginSecretSource`, `CredentialStoreSecretSource`),
`SecretRequest`, `MemorizedSecret`, `SecretRetriever`,
`SecretRequestFailure`, `RequestSecretsResult`, `CanonicalJson`,
`SecretMaterializationRefusedException`,
`SecretMaterializationFailedException`.
**Files:** ~13 files under `Phantom.Workspaces.Llm.Core\Secrets\`.
**Tests:** `CanonicalJsonTests`, `SecretUseScopePreimageTests`, minimal
record-equality tests for `SecretUseMemory`/`MemorizedSecret`.
**Dependencies:** none.

### Commit 2 — `[secret-store]` `IPlatformSecretStore` + Windows Credential Manager impl (Meziantou NuGet)

**Scope:** Add the `Meziantou.Framework.Win32.CredentialManager` package
(v3.0.1, MIT, zero runtime deps) to `Directory.Packages.props` and reference
it from `Phantom.Workspaces.Llm.Core.csproj`. Introduce `IPlatformSecretStore`,
`WindowsCredentialManagerSecretStore` (thin adapter over
`CredentialManager.ReadCredential` / `WriteCredential` / `DeleteCredential` /
`EnumerateCredentials`), and `NullPlatformSecretStore` (macOS/Linux fallback).
**Files:** three new files under `Phantom.Workspaces.Llm.Core\Secrets\`;
`Directory.Packages.props`; `Phantom.Workspaces.Llm.Core.csproj`.
**Tests:** `WindowsCredentialManagerSecretStoreTests`
(`SkipUnless(OperatingSystem.IsWindows)`), `NullPlatformSecretStoreTests`.
**Dependencies:** Commit 1.

### Commit 3 — `[secret-store]` `AllowedSecretsStore` (JSON persistence)

**Scope:** `IAllowedSecretsStore` + `AllowedSecretsStore` reusing the
`ConfigurationPersistenceService` serializer options; new file location
`%APPDATA%\Phantom.Workspaces\allowed-secrets.json`.
**Files:** two new files under `Phantom.Workspaces.Llm.Core\Secrets\`.
**Tests:** `AllowedSecretsStoreTests`.
**Dependencies:** Commit 1.

### Commit 4 — `[secret-store]` `SecretUsageScanner` + `AgentManifestSecretUseMemoryFactory`

**Scope:** Pure logic:
* `SecretUsageScanner` walks `AgentDefinition` for `${SECRET:...}`
  placeholders and rewrites them.
* `AgentManifestSecretUseMemoryFactory` maps `(manifest, secretName,
  useDisplayString)` → ordered `SecretUseMemory` candidate list, using the
  manifest `entity-id` for `ManifestIdentity` and
  `CanonicalJson.Encode(manifest.Template)` for the content-keyed scopes.
**Files:** `SecretUsageScanner.cs`, `SecretUsage.cs`,
`AgentManifestSecretUseMemoryFactory.cs`.
**Tests:** `SecretUsageScannerTests`,
`AgentManifestSecretUseMemoryFactoryTests`.
**Dependencies:** Commit 1.

### Commit 5 — `[secret-store]` Dialog contract + no-op dialog host

**Scope:** `SecretUseDialogInput`/`SecretUseDialogResult`/`ISecretUseDialogHost`
in `Llm.Core.Secrets`, plus a `TestDialogHost` in test-support that returns a
scripted result. No Avalonia yet. The contract accepts only
`SecretRequest`/`SecretUseMemory`/`SecretSource` — no manifest, no scope.
**Files:** three new files under `Phantom.Workspaces.Llm.Core\Secrets\`; one
test-support file under `Phantom.Workspaces.Llm.Core.Tests\Secrets\`.
**Tests:** compile-only.
**Dependencies:** Commit 1.

### Commit 6 — `[secret-store]` `SecretProvider` concrete + `ISecretProvider`

**Scope:** `ISecretProvider` in `Llm.Core.Secrets`; concrete `SecretProvider` in
`Phantom.Workspaces.Services.Secrets` composing the store, allowed-secrets
store, and dialog host. Includes source-resolver adapters delegating to
`GitHubAuthTokenResolver` and the AWS/Azure "not yet implemented" placeholder
resolvers (which always return `SecretRequestFailure`).
**Files:** `ISecretProvider.cs`, `SecretProvider.cs`, resolver adapters for the
four `SecretSource` subclasses.
**Tests:** `SecretProviderTests` (fakes for every dependency, including
`TestDialogHost`), covering pre-approval matching per scope, fail-closed
propagation, and the placeholder AWS/Azure "not yet implemented" scenarios.
**Dependencies:** Commits 2, 3, 4, 5.

### Commit 7 — `[secret-store]` `AgentDefinitionSecretMaterializer` + `AgentServices.SecretProvider` wiring

**Scope:** Add `AgentDefinitionSecretMaterializer` (fail-closed: throws on
provider null and on any `FailedSecrets`) and thread an `ISecretProvider?`
slot into the existing `AgentServices` bag
(features\Phantom.Workspaces.Llm.Interfaces\AgentServices.cs:8). Do **not** yet
call it from `AgentFactory.CreateChatClientAsync` (that's Commit 8) — this
commit only introduces the seam and its tests so the following commit is a
small diff.
**Files:** `AgentDefinitionSecretMaterializer.cs`; edit
`Phantom.Workspaces.Llm.Interfaces\AgentServices.cs`.
**Tests:** `AgentDefinitionSecretMaterializerTests` including the
"placeholder never silently dropped" and "exception carries all failures"
scenarios.
**Dependencies:** Commit 6.

### Commit 8 — `[secret-store]` Hook materializer into `AgentFactory.CreateChatClientAsync`

**Scope:** Call `AgentDefinitionSecretMaterializer.MaterializeAsync` immediately
after `AgentDefinitionParameterSubstitutor.Substitute` in
`AgentFactory.CreateChatClientAsync`
(features\Phantom.Workspaces.Llm.Core\AgentFactory.cs:280). When
`services.SecretProvider is null` (test contexts), skip the call to preserve
existing behaviour. Do **not** swallow exceptions from the materializer —
they must bubble out and fail chat-client creation.
**Files:** `Phantom.Workspaces.Llm.Core\AgentFactory.cs`.
**Tests:** `AgentFactoryTests` additions
(`CreateChatClientAsync_SecretInManifest_*`,
`CreateChatClientAsync_AwsPlaceholderSelected_ThrowsWithNotYetImplementedMessage`).
**Dependencies:** Commit 7.

### Commit 9 — `[secret-store]` Avalonia dialog view + view model + host + `ICredentialPicker`

**Scope:** `SecretUseDialogViewModel`, `SecretUseDialogWindow.axaml(.cs)`,
`AvaloniaSecretUseDialogHost`. Mirrors the `ShellSettingsDialogWindow` pattern.
The view-model receives only `SecretRequest.Memories` and
`SecretRequest.CandidateSecretSources`; it never references
`AgentManifest`/`SecretUseScope`. Also introduces:

* `ICredentialPicker` (in `Llm.Core.Secrets`) — the abstraction for the
  `[…]` Saved-Credential enter/select flow.
* `WindowsCredentialPicker` (in `Phantom.Workspaces\Services\Secrets\`) —
  Windows impl delegating to
  `Meziantou.Framework.Win32.CredentialManager.CredentialManager.PromptForCredentials(owner, message, caption, userName)`
  (which surfaces the native `CredUIPromptForWindowsCredentials` with
  `CREDUIWIN_ENUMERATE_CURRENT_USER` so the OS dialog both enumerates
  existing credentials and allows entering a new one). Newly-entered
  credentials are persisted via `CredentialManager.WriteCredential`.
* `NullCredentialPicker` — non-Windows fallback returning `null` /
  `IsSupported == false`.
* `IHwndProvider` + `AvaloniaHwndProvider` — resolves the owner HWND via
  `((IClassicDesktopStyleApplicationLifetime)App.Current!.ApplicationLifetime!).MainWindow?.TryGetPlatformHandle()?.Handle`,
  matching the pattern used by the existing settings dialog at
  features\Phantom.Workspaces\App.axaml.cs:97,209 and
  features\Phantom.Workspaces\MainWindow.axaml.cs:174.

The dialog binds a per-row `PickCredentialCommand` to a `[…]` button on the
`[Saved Credential]` value source; on a non-null pick, the row's
`SelectedSource` becomes a new `CredentialStoreSecretSource(pickedName)`.

**Files:** dialog view/viewmodel/host (three files under `Phantom.Workspaces\`);
`ICredentialPicker.cs` under `Phantom.Workspaces.Llm.Core\Secrets\`;
`WindowsCredentialPicker.cs`, `NullCredentialPicker.cs`,
`IHwndProvider.cs`, `AvaloniaHwndProvider.cs` under
`Phantom.Workspaces\Services\Secrets\`.
**Tests:** `SecretUseDialogViewModelTests` in `Phantom.Workspaces.Tests`
using `[AvaloniaFact]`, including the `Row_SavedCredentialEllipsisCommand_*`
scenarios; `WindowsCredentialPickerTests`.
**Dependencies:** Commits 2, 6 (uses the platform store and the dialog contract).

### Commit 10 — `[secret-store]` Register `ISecretProvider` + `ICredentialPicker` in `ApplicationServices` + `App.axaml.cs`

**Scope:** Add `SecretProvider` (and `CredentialPicker`) properties to
`Phantom.Workspaces\Services\ApplicationServices.cs`
(features\Phantom.Workspaces\Services\ApplicationServices.cs:8). Construct
the `IPlatformSecretStore` via the `OperatingSystem.IsWindows()` cascade
(Windows → `WindowsCredentialManagerSecretStore`; otherwise
`NullPlatformSecretStore`), `AllowedSecretsStore`,
`AvaloniaSecretUseDialogHost`, `AvaloniaHwndProvider`, `WindowsCredentialPicker`
(or `NullCredentialPicker`), and `SecretProvider` in `App.axaml.cs`
`OnFrameworkInitializationCompleted`; pass the provider into the existing
`ApplicationServices` constructor call. Propagate to `AgentServices` at the
point where `AgentServices` is built for chat creation. The macOS/Linux
concrete backends are **not** installed here — see §Future work.
**Files:** `ApplicationServices.cs`, `App.axaml.cs`, wherever `AgentServices`
is currently constructed (grep-verify at implementation time).
**Tests:** smoke test that `ApplicationServices.SecretProvider is not null`
and `ApplicationServices.CredentialPicker is not null` at app start
(extended `AppStartupTests` if such a class exists; otherwise a new
`ApplicationServicesConstructionTests`).
**Dependencies:** Commits 6, 9.

### Commit 11 — `[secret-store]` Fold `GitHubAuthTokenResolver` into `GitHubLoginSecretSource`

**Scope:** Add a `GitHubLoginSecretSource` resolver adapter that delegates to
`GitHubAuthTokenResolver.ResolveAsync`
(features\Phantom.Workspaces.Llm.Core\GitHubAuthTokenResolver.cs:39), and
ensure the manifest-native path (`${GITHUB_TOKEN}` env-var expansion via
`EnvironmentApiKeyResolver`) is left intact for backward compatibility.
Document in `Phantom.Workspaces.Llm.Core\Secrets\SecretSource.cs` that
`${SECRET:GithubApiToken}` + `GitHubLoginSecretSource` is the preferred future
form.
**Files:** additions to `SecretSource.cs`; new `SecretSourceResolvers.cs` if
needed. No behaviour change to `AgentFactory.ResolveApiKey`.
**Tests:** `GitHubAuthTokenResolverTests` regression + a new
`SecretProviderTests` scenario
`GitHubLoginSource_DelegatesToGitHubAuthTokenResolver`.
**Dependencies:** Commit 6.

### Commit 12 — `[secret-store]` End-to-end integration test

**Scope:** A single integration test that constructs a fake `ApplicationServices`
with a real `AllowedSecretsStore` (temp file), a fake platform store, and a
scripted dialog host; loads a manifest containing `${SECRET:GithubApiToken}`
inside `Model.Options.AdditionalProperties`; calls
`AgentFactory.CreateChatClientAsync`; asserts the placeholder is rewritten and
the client receives the resolved value. Then:
* edits an unrelated template field, re-runs, and asserts the content-scope
  consent no longer matches so the dialog is invoked again;
* pre-populates a `ManifestIdentity` consent, re-runs, and asserts the dialog
  is NOT invoked;
* selects `AwsLoginSecretSource` at consent time and asserts
  `CreateChatClientAsync` throws `SecretMaterializationFailedException` with
  `"AWS login is not yet implemented"`.
**Files:** `Phantom.Workspaces.Llm.Core.Tests\Secrets\SecretStoreEndToEndTests.cs`.
**Tests:** the scenarios above.
**Dependencies:** Commits 8, 10, 11.

---

## Future work

The `IPlatformSecretStore` seam is deliberately shaped to accept additional
per-OS backends without disturbing anything in this feature. The following
items are **out of scope** for `secret-store` and should be filed as separate
features when there is user demand.

### macOS Keychain backend

Add `MacOsKeychainSecretStore : IPlatformSecretStore` with
`[SupportedOSPlatform("macos")]`. Two realistic implementation options:

- **Preferred: `Security.framework` `SecItem*` P/Invoke** on
  `/System/Library/Frameworks/Security.framework/Security` —
  `SecItemAdd`, `SecItemCopyMatching`, `SecItemDelete`, with
  `kSecClassGenericPassword`, `kSecAttrService = "Phantom.Workspaces"`,
  `kSecAttrAccount = secretName`. Enumeration via `SecItemCopyMatching`
  with `kSecMatchLimitAll`.
- **Fallback: shell out to the `security` CLI**
  (`security add-generic-password` / `security find-generic-password` /
  `security delete-generic-password`), mirroring the `ProcessRunner`
  pattern used by `GitHubAuthTokenResolver`. Simpler to ship but
  higher-latency and does not scale to enumeration.

Composition-root cascade would then extend to
`OperatingSystem.IsMacOS() ? new MacOsKeychainSecretStore() : ...`.

### Linux Secret Service / libsecret backend

Add `LinuxSecretServiceSecretStore : IPlatformSecretStore` with
`[SupportedOSPlatform("linux")]`. Options:

- **Preferred: `libsecret-1.so.0` P/Invoke** (`secret_password_store_sync`,
  `secret_password_lookup_sync`, `secret_password_clear_sync`) with
  `SECRET_SCHEMA_NONE` and attributes
  `"application" = "Phantom.Workspaces"`, `"name" = secretName`. Works
  against GNOME Keyring, KWallet's Secret Service bridge, and any other
  Secret Service implementation.
- **Fallback: DBus Secret Service protocol directly** if libsecret is not
  present on the target distribution.

### Cross-platform managed fallback — `Devlooped.CredentialManager` (distant fallback only)

The `Devlooped.CredentialManager` package is a repackaging of Microsoft's
Git Credential Manager credential store and does offer read/write access to
the OS keychain on Windows, macOS, and Linux from managed code. It is
retained here **only as a distant fallback**, not a recommendation:

- **No credential enumeration** — cannot populate a saved-credentials
  picker list (breaks the in-app source dropdown and the `[…]` list).
- **No native prompt / entry dialog** — the `[…]` enter-new flow would have
  to be reinvented in-app on every platform.
- **OSMF licence with a commercial-fee concern** that would require review
  before shipping.

If a future feature adopts it despite these limitations (e.g. to reach
macOS/Linux with a single package), those gaps must be closed in-app.

### Windows Credential Manager UI (nice-to-have)

A follow-up feature could add a "Manage saved credentials" pane in the
Phantom.Workspaces settings that lists everything under
`"Phantom.Workspaces:"` (via `CredentialManager.EnumerateCredentials`),
lets the user rename/delete, and pre-populates the `[…]` picker.

---

## Open questions (need owner decision before filing bugs)

1. **`AllUses` scope semantics.** ✅ **Resolved by owner:** "All Uses" means
   literally all uses — the broadest reasonable interpretation. Reflected in
   `SecretUseScopePreimage` as `"phantom.workspaces/secret-store/v1|scope=all-uses"`
   with no `secretName` in the preimage. Users who want per-secret-name
   breadth should choose `AnyManifest` instead.

2. **Failure policy per placeholder.** ✅ **Resolved by owner:** Fail-closed —
   any placeholder in `FailedSecrets`, or a global `null` result, causes
   `AgentFactory.CreateChatClientAsync` to throw. Silent-drop mode is
   explicitly rejected and preserved only in §Options E1 for background.

3. **AWS / Azure login sources.** ✅ **Resolved by owner:** Ship
   `AwsLoginSecretSource` and `AzureLoginSecretSource` as visible drop-down
   entries whose resolvers return
   `SecretRequestFailure(Reason.Other, "…is not yet implemented")`. Combined
   with the fail-closed policy this yields a clear error message rather than
   silent misbehaviour.

4. **Cross-platform backends.** ✅ **Resolved by owner:** macOS and Linux
   backends are **future work**, not part of this feature. This feature
   ships Windows-only secret storage on top of
   `Meziantou.Framework.Win32.CredentialManager`; non-Windows platforms
   install `NullPlatformSecretStore`. The `IPlatformSecretStore` seam is
   kept so those backends can be added additively later — see §Future work.

5. **Manifest identity source.** ✅ **Confirmed by owner:** the agent-manifest
   workspace entity's `entity-id` uuid is the stable manifest identity used
   for the `ManifestIdentity` scope. Sourced from `entity.json` via
   `Phantom.Workspaces.Data.Core\JsonSchemas\agent-manifest.json`, which
   extends `entity.json`. It survives any content edit. If `AgentManifest`
   later grows a different persisted-identity concept, the factory can be
   updated to use it, but for this feature `entity-id` is the identity.

6. **Manifest content hash preimage extent.** ✅ **Confirmed by owner:**
   `manifest.Template` alone (features\Phantom.Workspaces.Llm.Core\JsonSchemas\agent-manifest.json;
   consumed at features\Phantom.Workspaces.Llm.Core\AgentDefinitionParameterSubstitutor.cs:22-24)
   is the correct hash target for the `ManifestContent` /
   `KeyInManifestContent` scopes, because that is where every
   `${SECRET:...}` placeholder lives. If a future change adds non-template
   top-level fields that participate in materialization, extending the
   preimage is a straightforward follow-up.
