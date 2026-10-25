# GodotMCP

[中文](CONTEXT.md) | English

A Godot control-plane ecosystem where assistant sessions operate multiple editor/runtime instances through a resident daemon.

## Language

**Daemon**: machine-level resident process multiplexing sessions and Godot instances. Avoid broker/hub.

**Session**: an assistant’s MCP connection. Avoid client/user when referring to this domain identity.

**Godot instance**: an editor process with the toolkit loaded. A project path and an instance are distinct concepts.

**Runtime instance**: a game process exposing a runtime control plane.

**Registry**: shared discovery information published by instances; a file name is not the domain concept.

**Singleton lock**: an operating-system-held exclusion for one daemon lifetime.

**Daemon token**: stable machine-level credential read by the shim to connect to the daemon. Distinct from session state identity.

**Dispatch lane**: instance-side serialized mutation execution.

**Scene lease**: bounded exclusive claim on the scene being edited.

**Bridge**: translation from MCP calls to instance commands; also the retired Node implementation.

**Shim**: stdio host entry that forwards to and bootstraps the daemon.

**Host entry**: the host configuration record selecting its MCP connection. See [0005](docs/adr/0005-host-entry-stdio-shim.en.md).

**Session key**: identity owning application state for a shim connection, including groups/default target; not an authorization boundary.

**Tool face**: the tools discoverable/callable by a session at its current connection/group state.

**Default target**: the project selected for later calls omitting instance; explicit instance selection wins. Avoid global-current-project or fixed-workspace.
