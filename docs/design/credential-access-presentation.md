# Credential access presentation

The user-facing vocabulary is centralized in `CredentialAccessText`:

* Requests: current approval/provider work.
* Saved approvals: policy only. Forgetting does not delete credentials or revoke access.
* Cached credentials: local material only. Deleting does not forget approvals or revoke access.
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
The host also contains Cached credentials and Provider access. Requests binds the
exact process-owned rows instance, never a copied collection or a second registry.
Fresh actionable prompts automatically show Requests after the shell opens,
without activation. The registry issues a monotonic prompt generation, independent
of source-only revisions; accepted snapshots deduplicate these generations even
when intermediate Performing snapshots are skipped. Dismissal acknowledges only
presentation: source refresh, count decreases and projection changes do not reopen
the unchanged prompt. A new request or renewal generation does. One attached
runtime owner claims presentation for the shared process rows; Bootstrap does not
open a competing runtime surface. Shutdown detaches presentation callbacks.
Automatic visibility never executes an authorization or provider action.
If management currently owns keyboard focus, the exact retained Requests control
is presented beside that unchanged page instead of hiding its focused control.
Explicit page navigation returns Requests to its tab. This is one host and one
Requests control, not an additional popup/window or copied rows collection.

The window is shown nonmodally before administration refresh, so live requests
and the main shell remain usable while saved lists load. Initialization tasks and
Closed-triggered disposal tasks remain owned/observed. Closing cancels refresh
and disposes only the three administration models; application shutdown awaits
this lifecycle before the process access graph. Reopening creates fresh models
while retaining process row identity and presentation state. Escape closes the
host and returns keyboard focus to the credential key. Failures are reported
using fixed safe UI/log text without backend/provider exception content.
The real owned-host regression keeps thirty process-owned requests at a nonzero
scroll offset across page changes, provider revisions and close/reopen, checking
source/scope selection, details and focus alongside row identity. Rendered scope
choices are also exercised from real broker-generated options, including AllUses;
provider revocation coverage distinguishes denied/generic failures from success.
