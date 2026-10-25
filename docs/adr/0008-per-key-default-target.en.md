# 0008: Per-key default targets

[中文](0008-per-key-default-target.md) | English

## Decision

An application key can store a target without protocol sessions. Targeted tools resolve explicit instance (canonical path/12-character ID), key default, then the only active instance; otherwise list candidates in an error. Global read-only tools have their own parameter contracts.

godot_connect stores the target. With no arguments it tries the old key target, project.godot directly in cwd, then the only active instance. Explicit offline projects may be stored; subsequent calls report TARGET_NOT_RUNNING with a startup-path hint. The daemon neither launches editors nor scans recursively.

SessionTargetResolver stays independent of GroupService and delegates addressing to InstanceManager to avoid a construction cycle. Paths/short IDs remain; no connection_id is introduced. Version visibility remains a union across instances.

## Current limits

“Resolve and pin at tools/call entry” is a design requirement **not yet implemented**. Subcalls currently reread the default, so a same-key switch can cross projects mid-call. Sequence switches or pass instance when interleaving projects.

Omitted groups preserve the set; explicit groups is designed to replace it, but stale GroupService buckets can be revived by browsing. Use discover reset and verify the list. Restart replay is not complete final-state restoration; see the [daemon README](../../plugin/godot-mcp-unified/server-dotnet/README.en.md).

## Alternatives

Always requiring instance is easy to omit. Using defaults only with a unique candidate adds nothing. New hashed handles are irreversible and cannot resolve offline targets, so retain paths. This revises the no-application-state premise of [0003](0003-instance-addressing.en.md), without changing runtime routing through its editor instance.
