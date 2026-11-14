# Future features

Not implemented yet. The current version is controller-first, using XRI rays.

## Hands
- **Hand tracking input**: pinch to grab a tile and drag it onto the stave, poke to press
  buttons (XRI `XRPokeInteractor` + `XR Hands`). The drop logic (`StaffZone`, sticky snap) can stay
  as it is; only the pointer source changes.
- **Grab notes with the hand**: pick up a note from the stave like an object and place it back,
  maybe with a magnifier around the hand for precision.
- **Hand models** instead of controller models.
- Pinch-and-twist on a selected note as an alternative to the thumbstick nudge.

## Composition
- Save and load scores (JSON in `Application.persistentDataPath`) and MIDI file export.
- Ties, beams for eighths/16ths, triplets, key signatures with sharps/flats in Advanced.
- More motifs (per style: folk, blues, film), motif variations (invert, reverse, transpose).
- Chord voicing choices and more backing styles (arpeggio, waltz, swing).
- Copy/paste a bar, repeat signs.

## Experience
- Guided tour for first-time users (durhack.4 had one).
- More places, and an import of the Figma styles for the panels.
- Recording a performance (audio file) to share.
- Multi-user: compose together in the same venue.
