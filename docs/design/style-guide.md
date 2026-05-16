# Phantom.Workspaces Style Guide

## Naming

- Use full words.
- Do not use abbreviations in names unless they are already part of the domain language and universally understood.
- Prefer explicit names over short names.

## Parameters

- Put one argument per line in method declarations and multi-argument calls.
- Start with vertical formatting for anything with more than one argument.

## Types and Members

- Use one clear responsibility per type.
- Prefer interfaces for replaceable behavior.
- Keep public surface area explicit and stable.

## Data Types

- Prefer domain-specific types over primitive types.
- Wrap identifiers, concurrency tags, timestamps, and other meaningful values in dedicated types.
- Prefer enums over booleans when more than one meaningful state exists.

## Code Layout

- Prefer readable formatting over dense expressions.
- Keep related members close together.
- Avoid cleverness when a direct expression is clear.
