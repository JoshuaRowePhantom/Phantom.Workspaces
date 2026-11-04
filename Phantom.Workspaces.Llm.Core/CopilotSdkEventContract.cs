namespace Phantom.Workspaces.Llm;

/// <summary>
/// Reviewed SDK 1.0.13 wire union. Keep this explicit: a new SDK variant must fail the
/// contract test until its lifecycle implications have been reviewed.
/// </summary>
internal static class CopilotSdkEventContract
{
    internal static (string Category, string Reason) Classify(string type) => type switch
    {
        "subagent.started" or "subagent.configured" or "subagent.completed" or "subagent.failed"
            or "assistant.turn_start" =>
            ("lifecycle", "Correlated invocation, configuration or child-turn evidence."),
        "session.background_tasks_changed" or "session.idle" or "abort" or "agent.interrupted" =>
            ("reconcile", "Invalidate authoritative task/session state; never infer child success."),
        "assistant.message" or "assistant.message_delta" or "assistant.reasoning_delta"
            or "assistant.usage" or "system.notification" or "tool.execution_start"
            or "tool.execution_complete" or "session.error" =>
            ("transcript", "Translated content; typed notifications also reconcile task lifecycle."),
        "assistant.idle" =>
            ("ignore", "Only the parent processing loop is idle; background work may still execute."),
        "subagent.selected" or "subagent.deselected" =>
            ("ignore", "Agent selection changes configuration, not child execution."),
        "assistant.server_tool_progress" or "tool.execution_partial_result" or "tool.execution_progress"
            or "assistant.streaming_delta" or "assistant.tool_call_delta" or "assistant.message_start"
            or "assistant.reasoning" or "assistant.intent" or "assistant.turn_end" or "assistant.turn_retry" =>
            ("ignore", "Progress/turn metadata is not authoritative child completion or transcript delta."),
        "session.task_complete" =>
            ("ignore", "Session/autopilot review outcome, not a child task-registry completion."),
        "assistant.fusion_phase_activity" or "assistant.fusion_phase_completed" or "assistant.fusion_phase_failed"
            or "assistant.fusion_phase_started" or "factory.run_settled" or "factory.run_started" or "factory.run_updated"
            or "session.fusion_completed" or "session.fusion_resolved" or "session.fusion_route_failed"
            or "session.fusion_route_started" =>
            ("ignore", "Experimental orchestration telemetry is not a subagent lifecycle event."),
        "auto_mode_switch.completed" or "auto_mode_switch.requested" or "capabilities.changed"
            or "command.completed" or "command.execute" or "command.queued" or "commands.changed"
            or "elicitation.completed" or "elicitation.requested" or "exit_plan_mode.completed"
            or "exit_plan_mode.requested" or "external_tool.completed" or "external_tool.requested"
            or "hook.end" or "hook.progress" or "hook.start" or "mcp_app.tool_call_complete"
            or "mcp.headers_refresh_completed" or "mcp.headers_refresh_required"
            or "mcp.oauth_completed" or "mcp.oauth_required" or "mcp.prompts.list_changed"
            or "mcp.resources.list_changed" or "mcp.tools.list_changed" or "permission.completed"
            or "permission.requested" or "sampling.completed" or "sampling.requested" or "sandbox.decision"
            or "tool_search.activated" or "tool.user_requested" or "ui.ephemeral_query"
            or "user_input.completed" or "user_input.requested" =>
            ("ignore", "SDK control/permission/tool RPC metadata; no child execution transition."),
        "model.call_failure" or "model.call_finished" or "model.call_start" or "prompt_cache_break"
            or "session.usage_checkpoint" or "session.usage_info" =>
            ("ignore", "Model accounting telemetry is not a child outcome."),
        "session_limits_exhausted.completed" or "session_limits_exhausted.requested"
            or "session.auto_mode_resolved" or "session.auto_tier_switch_failed"
            or "session.autopilot_objective_changed" or "session.binary_asset"
            or "session.canvas.closed" or "session.canvas.opened" or "session.canvas.recorded"
            or "session.canvas.registry_changed" or "session.canvas.removed" or "session.canvas.unavailable"
            or "session.compaction_complete" or "session.compaction_start" or "session.completion_receipt"
            or "session.context_changed" or "session.context_cleared" or "session.custom_agents_updated"
            or "session.custom_notification" or "session.extensions_loaded" or "session.extensions.attachments_pushed"
            or "session.handoff" or "session.info" or "session.managed_settings_enforced"
            or "session.managed_settings_resolved" or "session.mcp_server_needs_reconnect"
            or "session.mcp_server_removed" or "session.mcp_server_status_changed" or "session.mcp_servers_loaded"
            or "session.mode_changed" or "session.mode_notice_delivered" or "session.model_change"
            or "session.permissions_changed" or "session.plan_changed" or "session.remote_steerable_changed"
            or "session.resume" or "session.schedule_cancelled" or "session.schedule_created" or "session.schedule_rearmed"
            or "session.session_limits_changed" or "session.shutdown" or "session.skills_loaded"
            or "session.snapshot_rewind" or "session.start" or "session.title_changed" or "session.todos_changed"
            or "session.tools_updated" or "session.truncation" or "session.warning" or "session.workspace_file_changed"
            or "skill.invoked" or "system.message" or "user.message" or "pending_messages.modified" =>
            ("ignore", "Session configuration/history metadata; not evidence about a correlated child epoch."),
        _ => ("unsupported", "SDK discarded unknown discriminator/data; reconcile state, do not synthesize transcript content."),
    };
}
