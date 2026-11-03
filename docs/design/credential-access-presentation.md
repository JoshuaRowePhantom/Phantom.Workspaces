# Credential access presentation

The user-facing vocabulary is centralized in `CredentialAccessText`:

* Requests: current approval/provider work.
* Saved approvals: policy only. Forgetting does not delete credentials or revoke access.
* Saved Credentials: named platform secrets plus a distinct cached/provider-managed group.
  Deleting a cached generation does not delete a saved entry, forget approvals or revoke access.
* Provider access: supported provider-side revocation, with affected cache clearing only
  after success. Saved approvals remain. Failures must not imply either operation succeeded.

Friendly scope text is presentation only. Persisted enums, selector identifiers,
binding fingerprints and policy matching are unchanged.

## Saved approval details

Optional `SafeApprovalContext` persists only four closed enums: credential
provider/kind, selected source kind and originating use kind. Broker construction
captures the selected source's kind; the policy validates the complete context
before saving it, and reload/listing validates it again. No supplied display
string, account name, raw identifier, credential material or provider payload is
copied into this context. Friendly strings are built exclusively from fixed
allowlisted vocabulary.

Missing/invalid context displays **Details unavailable** without changing policy
identity or matching. Version-1 records remain usable and forgettable. Origin is
explicitly historical ("Originally approved for"), never a restriction on an
AllUses approval. Rendered contextual action names carry these same safe details.
Duplicate descriptions are valid: commands always remove the selected identifier.
Optional context is deserialized independently: unknown enum strings/numbers,
missing required context fields and malformed shapes produce Details unavailable,
not an unreadable approval file. The surrounding document and policy identity
remain strictly validated.

## One nonmodal host

One top-right Credential access glyph routes to one retained, owned
`AccessAdministrationWindow`. Its live badge counts unique actionable process
rows, including local submitting/rejection transitions. Zero shows no alert.
Manual navigation selects Requests when attention is needed, otherwise Saved approvals.
The host also contains Saved Credentials and Provider access. Requests binds the
exact process-owned rows instance, never a copied collection or a second registry.
Fresh actionable prompts automatically show Requests after the shell opens,
without activation. The registry issues a monotonic prompt generation, independent
of source-only revisions; accepted snapshots deduplicate these generations even
when intermediate Performing snapshots are skipped. Dismissal acknowledges only
presentation: source refresh, count decreases and projection changes do not reopen
the unchanged prompt. A new request or renewal generation does. Broker requests
carry an opaque presentation-owner identifier into safe snapshots (including nested
acquisitions); the process host supplies the default identity and shells attach to
their scope. A matching visible shell accepts each presentation once. Unscoped
process work can use any visible shell, but scoped work never opens an unrelated
owner. Hidden shells do not consume prompts. Successful show/refresh acknowledges
the delivered generations; a failed presentation leaves them pending for a later
availability change or accepted snapshot. Restoring a hidden shell retries pending
work, not genuinely dismissed generations. Bootstrap does not open a competing
runtime surface. Shutdown detaches presentation callbacks.
Automatic visibility never executes an authorization or provider action.
If management currently owns keyboard focus, the exact retained Requests control
is presented beside that unchanged page instead of hiding its focused control.
Actual tab-header selection returns Requests to its tab and collapses the side
column, as does explicit programmatic navigation. This is one host and one
Requests control, not an additional popup/window or copied rows collection.

The window is shown nonmodally before administration refresh, so live requests
and the main shell remain usable while saved lists load. Initialization tasks and
Closed-triggered disposal tasks remain owned/observed. Closing cancels refresh
and disposes only the administration models and sensitive editor; application shutdown awaits
this lifecycle before the process access graph. Reopening creates fresh models
while retaining process row identity and presentation state. Escape closes the
host and returns keyboard focus to the credential key. Failures are reported
using fixed safe UI/log text without backend/provider exception content.
The real owned-host regression keeps thirty process-owned requests at a nonzero
scroll offset across page changes, provider revisions and close/reopen, checking
source/scope selection, details and focus alongside row identity. Rendered scope
choices are also exercised from real broker-generated options, including AllUses;
provider revocation coverage distinguishes denied/generic failures from success.

## Named saved credentials

The third tab inventories names from the exact process platform store, including
unused entries; refresh and View metadata do not read values. Windows explicitly
supports management; unsupported backends disable it without creating a fallback
vault. Provider-owned colon namespaces (including oauth:, mcp-oauth:, devtunnel:
and legacy versions) and ambiguous/invalid names are metadata-only, managed elsewhere.
Ordinary logical names remain editable. Generic names are bounded to 256 characters,
without controls, placeholder syntax, surrounding whitespace or namespace punctuation.
Values are limited to the Windows generic-credential blob bound (1280 UTF-16 characters).

View starts masked and reads only on explicit Reveal; Edit is an explicit value
read. Add uses a blank name/value, while updates keep the exact name immutable.
The dedicated sensitive control alone holds Avalonia's unavoidable transient
plaintext TextBox buffer; no list/general view model or serialized entity holds
values. Its custom automation peer exposes no value/text provider or descendant
text, even during visual reveal. Focus loss and the 30-second reveal timer remask;
cancel, row switch, tab/window close and disposal clear control buffers and cancel
stale completions. SecureString owners and BSTR conversion buffers are disposed/
cleared; immutable managed strings cannot be guaranteed erased. No automatic copy.

The shared trusted-local administrator serializes operations and rechecks names
before create/update/delete. Add never intentionally overwrites a collision;
missing rows are not recreated by Update. This is not external-process CAS:
simultaneous external editors remain last-writer-wins, explicitly warned in the editor.
Save updates storage only. Existing leases retain fixed material until normal
generation rotation on a later authorized acquisition; saves never grant approval
or hot-replace a lease. Separate confirmations distinguish saved-entry deletion
from cached-generation deletion, Forget approval and provider Revoke access.
