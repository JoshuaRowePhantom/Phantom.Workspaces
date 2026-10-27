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

## One nonmodal host

Both top-right shortcuts route to one retained, owned `AccessAdministrationWindow`.
The count key selects Requests; the management shortcut selects Saved approvals.
The host also contains Cached credentials and Provider access. Requests binds the
exact process-owned rows instance, never a copied collection or a second registry.
New requests only update counts; they do not activate or open a window.

The window is shown nonmodally before administration refresh, so live requests
and the main shell remain usable while saved lists load. Initialization tasks and
Closed-triggered disposal tasks remain owned/observed. Closing cancels refresh
and disposes only the three administration models; application shutdown awaits
this lifecycle before the process access graph. Reopening creates fresh models
while retaining process row identity and presentation state. Escape closes the
host and returns keyboard focus to the authorization key. Failures are reported
using fixed safe UI/log text without backend/provider exception content.
