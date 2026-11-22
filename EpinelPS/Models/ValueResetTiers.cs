namespace EpinelPS.Models;

public static class ValueResetTiers
{
    public const int Count = 15;
    public static decimal[] DefaultRates() => [12, 12, 12, 12, 12, 7, 7, 7, 7, 7, 1, 1, 1, 1, 1];

    public static bool IsValid(decimal[] rates) =>
        rates.Length == Count && rates.All(x => x >= 0m && x <= 100m && decimal.Round(x, 2) == x) &&
        rates.Sum() == 100m;

    public static int DrawLevel(decimal[] rates, Random random)
    {
        decimal ticket = random.Next(10000) / 100m;
        for (int index = 0; index < rates.Length; index++)
        {
            if (ticket < rates[index]) return index + 1;
            ticket -= rates[index];
        }
        throw new InvalidDataException("Value reset tier probabilities must total 100%.");
    }
}
