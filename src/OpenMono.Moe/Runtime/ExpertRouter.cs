using OpenMono.Moe.Family;

namespace OpenMono.Moe.Runtime;

public readonly record struct RoutingDecision(int[] ExpertIds, float[] Weights);

/// <summary>
/// CPU reference for MiMo's sigmoid + top-k router (`scoring_func=sigmoid`,
/// `topk_method=noaux_tc` simplified: no bias-correction term on the toy
/// path, `norm_topk_prob` as advertised).
/// </summary>
public static class ExpertRouter
{
    public static RoutingDecision Route(ReadOnlySpan<float> hidden, ReadOnlySpan<float> routerWeights, ModelShape shape)
    {
        int experts = shape.NRoutedExperts;
        int hiddenSize = shape.HiddenSize;
        if (hidden.Length != hiddenSize)
            throw new ArgumentException($"Hidden size {hidden.Length} != {hiddenSize}.");
        if (routerWeights.Length != hiddenSize * experts)
            throw new ArgumentException("Router weight matrix is the wrong shape.");

        Span<float> scores = experts <= 512 ? stackalloc float[experts] : new float[experts];
        for (int e = 0; e < experts; e++)
        {
            float dot = 0f;
            int row = e * hiddenSize;
            for (int i = 0; i < hiddenSize; i++)
                dot += hidden[i] * routerWeights[row + i];
            scores[e] = shape.ScoringFunc == "sigmoid" ? Sigmoid(dot) : dot;
        }

        int k = Math.Min(shape.NumExpertsPerTok, experts);
        var ids = new int[k];
        var weights = new float[k];
        Span<int> taken = experts <= 512 ? stackalloc int[experts] : new int[experts];
        for (int i = 0; i < k; i++)
        {
            int best = -1;
            float bestScore = float.NegativeInfinity;
            for (int e = 0; e < experts; e++)
            {
                if (taken[e] != 0)
                    continue;
                if (scores[e] > bestScore)
                {
                    bestScore = scores[e];
                    best = e;
                }
            }
            taken[best] = 1;
            ids[i] = best;
            weights[i] = bestScore;
        }

        if (shape.NormTopkProb)
        {
            float sum = 0f;
            for (int i = 0; i < k; i++)
                sum += weights[i];
            if (sum > 0f)
            {
                for (int i = 0; i < k; i++)
                    weights[i] /= sum;
            }
        }

        return new RoutingDecision(ids, weights);
    }

    public static float Sigmoid(float x) => 1f / (1f + MathF.Exp(-x));
}
