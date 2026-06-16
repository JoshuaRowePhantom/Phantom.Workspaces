# Tool Execution Result

A `tool-execution-result` entity records a single run of a scheduled tool (see
`docs/design/scheduled-tools.md`).

It is stored under the host entity at the name path:

```
[ <host entity name components...>, "tool-executions", <tool-name>, <start-time> ]
```

## Properties

- `tool-name` — the name of the tool that produced this result.
- `start-time` — the UTC time the run started (RFC 3339 date-time).
- `end-time` — the UTC time the run finished; absent while the run is still in progress.
- `status` — `running`, `succeeded`, or `failed`.
- `content` — optional arbitrary result content.

Child `tool-execution-result` entities record sub-tasks and incremental progress; their name path
extends their parent's name path.
