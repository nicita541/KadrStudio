namespace KadrStudio.Application.Automation.Editorial;

public interface IModelTokenCounter
{
    int CountTokens(string model, string text);
}

public sealed record ModelContextBudget(
    int ContextWindowTokens,
    int MaximumInputTokens,
    int MaximumOutputTokens,
    int ReserveTokens)
{
    public static ModelContextBudget Create(int contextWindowTokens)
    {
        if (contextWindowTokens < 1_024)
            throw new ArgumentOutOfRangeException(nameof(contextWindowTokens));
        var input = (int)Math.Floor(contextWindowTokens * 0.65);
        var output = (int)Math.Floor(contextWindowTokens * 0.20);
        return new ModelContextBudget(
            contextWindowTokens,
            input,
            output,
            contextWindowTokens - input - output);
    }

    public void EnsureFits(string model, string payload, IModelTokenCounter counter)
    {
        ArgumentNullException.ThrowIfNull(counter);
        var actual = counter.CountTokens(model, payload ?? string.Empty);
        if (actual > MaximumInputTokens)
            throw new ModelContextOverflowException(model, actual, MaximumInputTokens);
    }
}

public sealed class ModelContextOverflowException(
    string model,
    int actualTokens,
    int maximumTokens)
    : InvalidOperationException(
        $"Input for model '{model}' uses {actualTokens} tokens; stage budget is {maximumTokens}.")
{
    public string Model { get; } = model;
    public int ActualTokens { get; } = actualTokens;
    public int MaximumTokens { get; } = maximumTokens;
}
