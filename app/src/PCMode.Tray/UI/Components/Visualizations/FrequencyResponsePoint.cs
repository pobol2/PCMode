namespace PCMode.Tray.Elements.Audio;

public readonly record struct FrequencyResponsePoint(
    double FrequencyHz,
    double GainDb);