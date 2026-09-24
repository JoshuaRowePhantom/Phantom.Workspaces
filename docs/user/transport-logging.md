# Split transport diagnostics

Split transport metadata tracing is **temporarily on by default** while remote execution is
being diagnosed. Set `PHANTOM_WORKSPACES_VERBOSE_TRANSPORT_METADATA=false` before starting
the GUI, worker, CLI, or standalone server to turn off high-frequency Debug events. The
Information-level lifecycle and connection events remain enabled. The default should be
revisited after the diagnostic period.

Tracing records bounded frame type/method, direction, byte count, local write/read outcome,
and an opaque per-attempt marker. A write means the **local hop accepted bytes**, not that
another process received or executed the request. Worker `request-received` is logged only
after the worker listener is entered. HTTP traces report only method, status and stream byte
counts. Neither verbose mode nor the legacy CLI `--log-chat` / `--log-http-requests` flags
enable request bodies, prompts, headers, URLs, stderr text or exception rendering.

The process rolling file keeps up to seven days of safe records; an in-editor session view
keeps only recent entries, not the complete archive. Default-on tracing increases file
volume with the number of active frames, particularly during streaming. For short
investigations, turn it off with the environment switch after collecting the needed data.
