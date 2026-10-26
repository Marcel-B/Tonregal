# SPDX-License-Identifier: AGPL-3.0-or-later
"""BPM und Tonart einer Audiodatei mit librosa; Ausgabe als eine Zeile JSON auf stdout.

Aufruf: python analyze.py <datei>
Ausgabe: {"bpm": 123.0, "key": "Am", "keyConfidence": 0.81, "version": "0.11.0"}
"""
import json
import sys
import warnings

import librosa
import numpy as np

# Krumhansl-Kessler-Profile; Index 0 = Grundton C.
MAJOR = np.array([6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88])
MINOR = np.array([6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17])
NAMES = ["C", "C#", "D", "Eb", "E", "F", "F#", "G", "Ab", "A", "Bb", "B"]

# Längere Stücke: nur ein Ausschnitt ab Sekunde 30, das reicht für Tempo und Tonart.
OFFSET = 30.0
WINDOW = 180.0
SAMPLE_RATE = 22050


def detect_key(chroma: np.ndarray) -> tuple[str, float]:
    """Tonart mit der höchsten Korrelation zwischen mittlerem Chroma und den 24 Profilen."""
    profile = chroma.mean(axis=1)
    best_name, best_score = "C", -1.0
    for root in range(12):
        for template, suffix in ((MAJOR, ""), (MINOR, "m")):
            score = float(np.corrcoef(profile, np.roll(template, root))[0, 1])
            if score > best_score:
                best_name, best_score = NAMES[root] + suffix, score
    return best_name, max(0.0, best_score)


def detect_tempo(y: np.ndarray, sr: int) -> float:
    onset = librosa.onset.onset_strength(y=y, sr=sr)
    tempo_fn = getattr(librosa.feature, "tempo", None) or librosa.beat.tempo
    return float(np.atleast_1d(tempo_fn(onset_envelope=onset, sr=sr))[0])


def analyze(path: str) -> dict:
    duration = librosa.get_duration(path=path)
    offset = OFFSET if duration > OFFSET + 60 else 0.0
    y, sr = librosa.load(path, sr=SAMPLE_RATE, mono=True, offset=offset, duration=min(WINDOW, duration - offset))
    if y.size == 0 or not np.any(y):
        raise ValueError("keine Audiodaten")
    key, confidence = detect_key(librosa.feature.chroma_cqt(y=y, sr=sr))
    return {
        "bpm": round(detect_tempo(y, sr), 1),
        "key": key,
        "keyConfidence": round(confidence, 3),
        "version": librosa.__version__,
    }


def main() -> int:
    if len(sys.argv) != 2:
        print("Aufruf: analyze.py <datei>", file=sys.stderr)
        return 2
    if sys.argv[1] == "--version":
        print(json.dumps({"version": librosa.__version__}))
        return 0
    warnings.filterwarnings("ignore")
    try:
        print(json.dumps(analyze(sys.argv[1])))
    except Exception as e:  # noqa: BLE001 – jede Ursache landet als Meldung beim Aufrufer
        print(f"{type(e).__name__}: {e}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
