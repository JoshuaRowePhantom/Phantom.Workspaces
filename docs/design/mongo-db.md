# MongoDB Backend Design

## Overview

Phantom.Workspaces will support MongoDB as a backend data store through a broker-based connection model.
MongoDB access will be defined by JSON connection definitions validated against a JSON schema.

## Goals

- Support MongoDB as a first-class backend option.
- Keep connection setup declarative and testable.
- Support both local container-backed MongoDB and Azure Cosmos DB MongoDB API endpoints.
- Avoid embedding environment-specific connection logic in feature code.

## Proposed Project

- `Phantom.Workspaces.Data.MongoDB`
  - Hosts MongoDB data access integration.
  - Provides `MongoConnectionBroker`.

## MongoConnectionBroker

`MongoConnectionBroker` accepts a MongoDB connection definition and returns MongoDB client objects for the requested connection.

Responsibilities:

- parse and validate the JSON connection definition,
- create or reuse the correct MongoDB client,
- hide connection-source differences from callers,
- support both container-backed and cloud-backed connections.

## Connection Definition

MongoDB connections will be described by JSON.
The schema should be explicit enough to distinguish the supported connection kinds.

Planned connection kinds:

1. **Local container connection**
   - Uses a locally managed MongoDB container.
   - Broker resolves the running container endpoint and credentials if needed.
2. **Azure Cosmos DB connection**
   - Uses provided Cosmos DB MongoDB API credentials.
   - Broker builds a MongoDB client from the supplied endpoint and auth material.

## Suggested Shape

The connection definition should include:

- a connection kind discriminator,
- a stable connection name/id,
- host/port or endpoint information,
- authentication information when required,
- optional database defaults,
- optional container reference when the connection is container-backed.

## Notes

- The JSON schema should live alongside the definition model and be versioned.
- The broker should stay focused on MongoDB client creation and not own container lifecycle.
- Local-container connections should lease a container from the container broker and keep it alive for the configured keepalive window.
- Local-container setup depends on the platform-specific container engine installer preparing the machine, especially on Windows.
