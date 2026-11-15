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

## Voice (ElevenLabs)
- **A singing voice as an instrument**, in Beginner and Advanced: add a "Voice" stave from the
  instrument picker, type or say lyrics, and generate a sung (or spoken) line that follows the
  notes of that stave with ElevenLabs (text-to-speech / voice models), then play it in time with
  the band. Not built yet: today "Choir" is the only voice-like instrument (a SoundFont "aah").
- Pick a voice (soprano, tenor, rapper, narrator...) and a language.
- Cache the generated audio per stave, so playback stays deterministic and works offline once made.

## Venues
- Audience members, applause and background crowd sound are switched off for frame rate and
  because they were too loud (VR Composer object > Show Audience / Venue Ambience / Applause).
  Bring them back with real recorded applause and lighter, merged audience meshes.

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
