# GitHub Copilot provider support design

## Purpose

Define the implementation work required to support a new `github-copilot` model provider in `Phantom.Workspaces.Llm.Core`, while keeping the existing `github-models` provider path explicit.

## Current state

1. Provider dispatch is centralized in `AgentFactory.CreateChatClient`.
2. `github-models` is the current provider identifier for GitHub Models.
3. The agent definition schema provider enum is validated in `AgentDefinition.json`.

## Target state

1. Add `github-copilot` as a first-class provider value in schema and runtime dispatch.
2. Keep `github-models` and `github-copilot` as distinct providers with explicit behavior.
3. Use Microsoft Agent Framework integration points to connect GitHub Copilot SDK capabilities.

## Planned implementation slices

1. **Schema and model parsing**
   - Add `github-copilot` to provider enum validation.
   - Validate connection settings required by Copilot flows.

2. **Runtime provider dispatch**
   - Add a `github-copilot` branch in `AgentFactory.CreateChatClient`.
   - Keep provider-specific display naming and diagnostics explicit.

3. **SDK integration via Microsoft Agent Framework**
   - Add required package references for GitHub Copilot integration through the Microsoft Agent Framework.
   - Implement provider-specific client construction and option mapping.
   - Surface clear startup errors when required auth/material is missing.

4. **Configuration and docs**
   - Add/refresh example agent definitions for `github-copilot`.
   - Document configuration requirements and expected auth flow.

## New classes

1. `GitHubCopilotProviderClientFactory`
   - Creates the provider-specific chat client using Microsoft Agent Framework integration points.
2. `GitHubCopilotConnectionOptions`
   - Typed configuration model for copilot provider connection/auth settings.
3. `GitHubCopilotModelOptionsMapper`
   - Maps `AgentDefinition.Model.Options` into runtime options for the copilot client path.

## Key integration points

1. `AgentFactory.CreateChatClient`
   - Add explicit `github-copilot` provider dispatch.
2. `AgentDefinition` schema validation (`AgentDefinition.json`)
   - Add accepted provider value and related configuration constraints.
3. `AgentFactory.ConfigureChatOptions`
   - Apply provider-specific option translation for `github-copilot`.
4. Example definitions and loader tests
   - Add sample agent documents and ensure parser/tests cover the new provider.

## Test tasks

1. Add factory parsing/dispatch tests for `github-copilot`.
2. Add schema validation tests for accepted provider values.
3. Add negative tests for missing/invalid connection config.
4. Add example definition loading tests for new provider examples.

## Non-goals

1. Replacing the `github-models` path.
2. Adding fallback behavior between provider types.
