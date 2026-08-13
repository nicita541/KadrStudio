# Stable core audit ledger

This file is the executable implementation ledger for the stable-editor-core goal.
An item is complete only when its regression tests and release gate are green.

## Confirmed gaps

- [ ] Immutable `ProjectState` is the only live editor state.
- [x] Fractional sequence frame rates survive the WPF compatibility boundary.
- [x] Track identity, order, mute, lock, visibility and names survive every round-trip.
- [ ] Preview uses a persistent media session rather than rebuilding the remaining timeline per seek.
- [ ] Audio meters are calculated from the mixed PCM stream.
- [ ] Proxy, conform, waveform, thumbnails and previews share one artifact catalog and budget.
- [ ] Media fingerprints detect content replacement and drive offline/relink.
- [ ] Multiple active text layers render in preview and export.
- [ ] Timeline drag/trim emits intents and never mutates project objects.
- [ ] Hardware export falls back to CPU and validates the produced streams.
- [ ] Recovery keeps up to twenty versions per project and supports multiple projects.
- [ ] The recording placeholder is removed; the transition workspace is functional.
- [ ] Documentation claims are enforced by architecture and integration tests.
