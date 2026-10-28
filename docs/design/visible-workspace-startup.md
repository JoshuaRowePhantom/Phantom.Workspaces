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
