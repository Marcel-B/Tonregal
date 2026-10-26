// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Tonregal.Application;

/// <summary>Analyse-Worker (ReplayGain, BPM, Tonart); Abschnitt <c>Tonregal:Analysis</c>.</summary>
public sealed class AnalysisOptions
{
    public const string SectionName = "Tonregal:Analysis";

    /// <summary>Pfad zu rsgain; ohne Pfad wird es im PATH gesucht.</summary>
    public string RsgainPath { get; set; } = "rsgain";

    /// <summary>Python des Analyse-venv; leer = <c>{MusicRoot}/tools/analysis-venv/bin/python</c>.</summary>
    public string PythonPath { get; set; } = "";

    /// <summary>Analyse-Script; leer = <c>analysis/analyze.py</c> neben der App.</summary>
    public string ScriptPath { get; set; } = "";

    /// <summary>Ohne Start-Knopf läuft die Analyse nur in diesem Fenster (Ortszeit).</summary>
    public bool RunAtNight { get; set; } = true;

    public TimeOnly NightStart { get; set; } = new(1, 0);
    public TimeOnly NightEnd { get; set; } = new(6, 0);

    /// <summary>
    /// Optionaler Befehl (per <c>/bin/sh -c</c>), der mit Exit-Code 0 meldet, dass gerade ein KI-Job
    /// läuft, z. B. eine ComfyUI-Abfrage. Dann wartet die Analyse im Nachtfenster.
    /// </summary>
    public string? BusyCommand { get; set; }

    /// <summary>Höchstdauer eines Werkzeugaufrufs (ein Track bzw. ein Album bei rsgain).</summary>
    public int TimeoutMinutes { get; set; } = 10;

    public bool InNightWindow(TimeOnly now) =>
        NightStart <= NightEnd ? now >= NightStart && now < NightEnd : now >= NightStart || now < NightEnd;
}
