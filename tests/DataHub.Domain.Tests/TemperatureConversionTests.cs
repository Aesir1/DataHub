using DataHub.Domain.Containers;

namespace DataHub.Domain.Tests;

public class TemperatureConversionTests
{
    [Theory]
    [InlineData(5, TemperatureUnit.C, 5)]
    [InlineData(41, TemperatureUnit.F, 5)]
    [InlineData(-40, TemperatureUnit.F, -40)]
    [InlineData(32, TemperatureUnit.F, 0)]
    [InlineData(273.15, TemperatureUnit.K, 0)]
    [InlineData(0, TemperatureUnit.K, -273.15)]
    [InlineData(100, TemperatureUnit.F, 37.778)]
    public void Converts_to_celsius(decimal value, TemperatureUnit unit, decimal expected) =>
        TemperatureConversion.ToCelsius(value, unit).ShouldBe(expected);

    [Fact]
    public void Unknown_unit_throws() =>
        Should.Throw<ArgumentOutOfRangeException>(() => TemperatureConversion.ToCelsius(1, (TemperatureUnit)42));
}
