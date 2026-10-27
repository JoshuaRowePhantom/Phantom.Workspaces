# Credential access presentation

The user-facing vocabulary is centralized in `CredentialAccessText`:

* Requests: current approval/provider work.
* Saved approvals: policy only. Forgetting does not delete credentials or revoke access.
* Cached credentials: local material only. Deleting does not forget approvals or revoke access.
* Provider access: supported provider-side revocation, with affected cache clearing only
  after success. Saved approvals remain. Failures must not imply either operation succeeded.

Friendly scope text is presentation only. Persisted enums, selector identifiers,
binding fingerprints and policy matching are unchanged.
