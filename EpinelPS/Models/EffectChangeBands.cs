using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace EpinelPS.Models;

public sealed class EffectChangeBands : IValidatableObject
{
    [BindRequired, Range(1, 13)]
    public int LowEnd { get; set; } = 5;
    [BindRequired, Range(2, 14)]
    public int MiddleEnd { get; set; } = 10;
    [BindRequired, Range(typeof(decimal), "0", "100")]
    public decimal LowPercent { get; set; } = 60;
    [BindRequired, Range(typeof(decimal), "0", "100")]
    public decimal MiddlePercent { get; set; } = 35;
    [BindRequired, Range(typeof(decimal), "0", "100")]
    public decimal HighPercent { get; set; } = 5;

    public IEnumerable<ValidationResult> Validate(ValidationContext context)
    {
        if (MiddleEnd <= LowEnd)
            yield return new ValidationResult("中档上限必须大于低档上限。");
        decimal[] rates = [LowPercent, MiddlePercent, HighPercent];
        if (rates.Any(x => x < 0 || x > 100 || decimal.Round(x, 2) != x))
            yield return new ValidationResult("每个区间的概率须在 0% 到 100% 之间，最多两位小数。");
        else if (rates.Sum() != 100)
            yield return new ValidationResult("三个区间的概率合计必须为 100%。");
    }

    public int DrawLevel(Random random)
    {
        decimal ticket = random.Next(10000) / 100m;
        if (ticket < LowPercent) return random.Next(1, LowEnd + 1);
        if (ticket < LowPercent + MiddlePercent) return random.Next(LowEnd + 1, MiddleEnd + 1);
        return random.Next(MiddleEnd + 1, 16);
    }
}
