# Container Broker Design

## Overview

Phantom.Workspaces will manage local containers through a dedicated container broker abstraction.
Container definitions will be JSON documents validated against a JSON schema.
The container project itself will also act as a small command-line executable so elevated installers can be invoked directly.

## Goals

- Centralize container start/stop behavior.
- Keep container definitions declarative.
- Make container-backed services testable and reproducible.
- Support local dev/test lifecycle management without hard-coding runtime details.

## Proposed Project

- `Phantom.Workspaces.Containers`
  - Contains `ContainerBroker`, container definition models, container engine abstractions, and the CLI entrypoint.
- `Phantom.Workspaces.Containers.Test`
  - Contains lifecycle and definition tests.

## ContainerBroker

`ContainerBroker` accepts a container definition and provides lifecycle control for that container.

Responsibilities:

- create or update a container installation from the current definition,
- validate the container JSON definition,
- lease a container instance to callers,
- start a container,
- stop a container,
- destroy a container,
- dispose a leased container handle,
- expose container runtime details needed by callers,
- keep container lifecycle state isolated from feature code.

## Container Engine

The container layer also needs platform-level engine support.
Engine and installer types will likely be platform-specific for Windows, Linux, and macOS.

Planned types:

- `ContainerEngine`
- `WindowsContainerEngine : ContainerEngine`
- `LinuxContainerEngine : ContainerEngine`
- `MacOSContainerEngine : ContainerEngine`
- `DockerDesktopEngine : ContainerEngine`
- `ContainerEngineInstaller`
- `WindowsContainerEngineInstaller : ContainerEngineInstaller`
- `LinuxContainerEngineInstaller : ContainerEngineInstaller`
- `MacOSContainerEngineInstaller : ContainerEngineInstaller`
- `DockerDesktopEngineInstaller : ContainerEngineInstaller`

`ContainerEngineInstaller` responsibilities:

- expose `Usable` to report whether the engine is installed and usable by the current user,
- expose `Configure()` to install or configure the engine,
- support command-line invocation for elevated setup tasks when needed.

`WindowsContainerEngineInstaller` will own Windows containerd setup, including:

- Windows feature installation,
- software download and install,
- current-user ACL setup for container operations,
- elevated execution when required.

`LinuxContainerEngineInstaller` and `MacOSContainerEngineInstaller` will carry the platform-specific installation and configuration steps for those systems.

`DockerDesktopEngine` and `DockerDesktopEngineInstaller` will represent the Docker Desktop-backed engine path, including discovery and configuration of the Docker Desktop-managed container runtime on supported hosts.

## Lifecycle API

The container lifecycle API is:

- `Create()`
- `Start()`
- `Stop()`
- `Destroy()`
- `Dispose()`

The installer/engine executable can be used to prepare the platform before lifecycle calls are made.

Behavior:

- `Create()` installs or downloads the container if needed and updates it to the current configuration.
- `Start()` starts the container and keeps it alive until `Stop()` or `Dispose()`.
- `Stop()` stops the container explicitly.
- `Destroy()` removes the container and its managed state.
- `Dispose()` releases a lease without necessarily stopping the underlying container immediately.

## Lease and Keepalive Model

Container usage is lease-based.

- A client that calls `Start()` receives a leased container handle.
- The container remains alive for at least its keepalive duration while any client has an active lease.
- Disposing one client handle does not stop the container if other leases remain active.
- Releasing the last lease allows the container to expire after its keepalive window.
- This behavior is intended to support MongoDB containers during tests without restarting them for every caller.

## Container Definition

Container definitions will be JSON and validated by schema.
The schema should capture the minimum runtime data needed to create and manage a container.

Planned fields:

- container kind/type,
- image reference,
- container name or generated identity,
- environment variables,
- ports,
- volumes or mounts,
- startup and shutdown behavior,
- optional health/readiness details.

## MongoDB Usage

MongoDB will use the container broker for the local-container connection type.
That means:

- the container broker owns start/stop,
- the MongoDB broker owns MongoDB client creation,
- the connection definition ties the two together.
- test code can lease a MongoDB container and leave it running for the keepalive window after disposal.

## Notes

- The container broker should be reusable for other container-backed services later.
- The JSON schema should be versioned so definitions can evolve safely.
- Tests should cover definition parsing and lifecycle transitions.
- On Windows, containerd installation and configuration are expected to happen through the container engine installer.
