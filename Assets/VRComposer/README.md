# VR Composer (Quest 2)

Drag real music notation onto a stave in VR, press Play, and hear it through a General MIDI
SoundFont, in a studio, a concert hall, a jazz club or a cathedral.
Playback is deterministic: every note plays exactly as written, and the backing band only plays
the chords you placed.

## Run it

1. Open `Assets/Scenes/SampleScene` (it has the XR Origin and the XR UI EventSystem).
2. **Tools > VR Composer > Reimport Glyph Textures** (once, after updating the scripts).
3. **Tools > VR Composer > Add To Open Scene**, then save the scene. This also creates the URP
   materials in `Resources/VRComposerMaterials` so the venues don't render pink in builds.
4. Press Play with Quest Link, or Build And Run for Android.

A welcome panel asks for a place and a mode. You can change both later from the inspector or
the wrist menu, and your score is kept.

## Two modes

**Beginner**: melodies that always sound good, and you still decide where everything goes.
- Notes snap only to the 5 notes of the pentatonic scale of the key (C, G, F major or A minor),
  so there are no wrong notes and the targets are bigger.
- **Motifs** tab: 12 one-bar phrases ("Rising steps", "Call", "Answer", "Ending", ...). Drag one
  into any bar and its first note goes where you point. Once placed, they are normal notes you can
  move, nudge or delete.
- **Chords** tab: I, IV, V, vi, ii, iii blocks for the chord lane under the stave (whole bar, or
  the second half of the bar).
- Tiles marked **Suggested** fit the next empty bar (rule-based: close to your last note, ending on
  a chord tone, a final motif that ends on the tonic, common chord moves). Nothing is placed for you.
- Backing band: the chord lane plays sustained strings plus a bass note every half bar, and
  drums can be toggled on.

**Advanced**: note by note, with full notation.
- Whole to 16th notes, dotted notes and rests, up to 4 instruments (treble or bass clef).
- **Symbols** tab: sharp, flat, natural, dot, staccato, accent, p, f and clefs. Drop one on a
  note, or select a note and tap the symbol. They change the sound: accidentals change pitch,
  staccato halves the length, accent and f play louder, and p plays softer.
- Any time signature from the inspector, and the same chord lane and backing as Beginner.

## Accurate placement

- Large stave (22 px spaces), 4 bars per page in Beginner and 2 in Advanced (◀ ▶ to change page).
- While dragging, a soft column, a short orange marker on the target line and an orange ghost
  note show exactly where it will land, with a readout like `G4 · bar 2, beat 3`.
- The pointer is smoothed (1€ filter) and the snap is "sticky": the ghost changes line or slot
  only after you clearly cross into the next one. Every change gives a small haptic tick and
  plays the pitch.
- The drop uses the snap that was shown just before you released the trigger, so the pull of the
  trigger does not move the note.
- **Fine-tune**: tap a note to select it (orange). Right thumbstick up/down changes the pitch by
  a step (Beginner: by a scale note), left/right moves it by its own length. B or Esc deselects.
  Teleport and snap-turn are paused while a note is selected.
- Drag a note to move it, or onto the **Bin** to delete it. **Undo** is on the transport.

## Controls

| Where | What |
|---|---|
| Palette (left) | Notes / Motifs / Chords tabs (Beginner) or Notes / Symbols / Chords (Advanced), plus the Bin |
| Score (centre) | staves, chord lane, Play button, page arrows |
| Inspector (right) | mode, place, key, time signature, tempo, bars, backing band, instruments |
| Transport (under the score) | Play/Stop, Loop, Undo, Clear, Recenter, bar/beat |
| Wrist menu (left hand) | Play/Stop, Hide/Show panels, Mode, Recenter, Place |
| Right controller | A = Play/Stop, B = deselect, thumbstick = nudge selected note |
| Keyboard (editor) | Space = Play/Stop, arrows = nudge, Esc = deselect |

Set `paletteOnLeft = false` on the VR Composer object to swap the side panels.

## Places

Studio (your own room, neutral sound), Golden Hall, Recital Room, Jazz Club and Cathedral. Each
venue has its own reverb and its own musicians who play along with your notes. In Beginner mode
the venue's band instrument plays your melody. After a full play-through (Loop off) the audience
applauds and the musicians bow.

## Files

- `Scripts/ScoreModel.cs`: score data in 16th-note ticks, notes with accidentals and
  articulations, the chord lane, keys, undo, and the pitch mapping.
- `Scripts/BeginnerKit.cs`: pentatonic scales, motifs, chords and the "fits next" rules.
- `Scripts/ScorePlayer.cs`: MeltySynth in `OnAudioFilterRead` with sample-accurate events,
  dynamics and articulations, plus the backing band (chords ch 11, bass ch 12, drums ch 9).
- `Scripts/Engraving.cs` + `LelandGlyphs.cs`: SMuFL engraving (stems, flags, ledger lines, dots,
  accidentals, articulations, dynamics) with Leland glyphs.
- `Scripts/ScoreView.cs`: the score panel, drop zones, drag overlays and the playhead.
- `Scripts/DragDrop.cs`: drag sources, the 1€ filter and sticky snapping.
- `Scripts/VRComposerApp.cs`: welcome, palette, inspector, transport, wrist menu, nudge.
- `Scripts/UIFactory.cs`: palette colours and widget builders.
- `Scripts/Themes/`: venues, musicians, venue audio (ported from durhack.4) and `ThemeManager`.
- `Resources/GeneralUser-GS.bytes`: GeneralUser GS (.sf2 renamed). See its licence file.
- `Resources/VRComposerGlyphs/*.png`: Leland glyphs (SIL OFL, see `Leland-LICENSE-OFL.txt`).
- `../Plugins/MeltySynth/`: MeltySynth source (MIT).

See `FUTURE_FEATURES.md` for what is planned next.
