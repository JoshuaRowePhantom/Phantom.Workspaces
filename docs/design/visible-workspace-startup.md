# Visible workspace startup

`PrepareShellAsync` owns broker/catalog/navigation/profile prerequisites and an
empty initialized dock. The splash covers only this phase. App then constructs,
wires and shows MainWindow, handing over the exact process authorization rows,
and dismisses the splash.

`CompleteVisibleStartupAsync` yields at dispatcher Loaded priority (after the
render opportunity) before restoring default workspaces in their original
order, or Getting Started when no default exists. It awaits startup pane
population before web hosting, scheduling, auto-resume and usage tracking.
`InitializeAsync` still means both phases completed, not merely a visible shell.
All entry points are idempotent and lifetime-owned.

Startup state distinguishes shell preparation, prepared shell, loading, ready,
failure and cancellation. The main window displays safe loading/failure status.
Post-show failure is logged and leaves the shell available for diagnostics and
controlled shutdown. Disposal cancels and observes phase work before resource
teardown; cancelled panes cannot receive late population updates.

Default-workspace queries, both dock descriptor branches, tab declarations, runtime
host-context composition, optional hosted route publication,
host-name/pause/history/reconciliation, auto-resume and
usage initialization receive the window lifetime token. Periodic refresh starts
only after required runtime initialization. Failed/cancelled tab batches dispose
successfully fetched but unpublished tabs before propagating the failure.
Credential-free acceptance tests use the real
loopback data and transport endpoints, holding actual query, tab and runtime requests while
verifying shell rendering, dispatcher responsiveness and the shared authorization
cancel action. They independently cover successful release, remote failure and
concurrent disposal before the held request is released.
Multiple-default coverage compares the original query order with the resulting
pane order through both phased startup and the full initialization API.

Restored view population links window lifetime, population lifetime and any
subscription owner's lifetime. The linked token reaches associated-note and
get/query subscriptions, referenced subview reads, hierarchy assembly, and card
shortcut discovery (including filesystem profile/tunnel lookups). Interrupted
view factories dispose unpublished populations, cards and query observers;
interrupted reconciliation also disposes its temporary population.
The broker publishes get/query subscriptions to its shared cache only after the
initial refresh succeeds, so a superseded population's cancelled read cannot
poison replacement populations with an unfinished, empty subscription.
The query satisfier also excludes flights with no remaining callers from new
deduplication/subsumption, even while the cancelled remote call is still unwinding.

Matching auto-resume sessions forward lifetime cancellation through persistence
acquisition, session-context composition, owner-profile lookup, transport/chat
acquisition and remote prompt enqueue. Shared persistence creation remains owned
and observed by the process cache; cancelling a session's wait does not cancel
another session's shared store. An acquired chat lease transfers to startup only
after the resume prompt succeeds; cancellation/failure before transfer releases it.
Downstream loopback gates run after a real restored-view target fetch or an actual
matching-session discovery, not merely at the earlier descriptor/discovery calls.
