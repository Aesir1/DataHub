namespace DataHub.Domain.Containers;

public enum TemperatureUnit
{
    C,
    F,
    K,
}

public static class TemperatureConversion
{
    public static decimal ToCelsius(decimal value, TemperatureUnit unit)
    {
        var celsius = unit switch
        {
            TemperatureUnit.C => value,
            TemperatureUnit.F => (value - 32m) * 5m / 9m,
            TemperatureUnit.K => value - 273.15m,
            _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, "Unknown temperature unit."),
        };
        return Math.Round(celsius, 3, MidpointRounding.AwayFromZero);
    }
}
