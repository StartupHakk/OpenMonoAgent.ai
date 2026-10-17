using OpenMono.Moe.Family;
using OpenMono.Moe.Runtime;

namespace OpenMono.Moe.Forward;

/// <summary>
/// Tiny MiMo-shaped CPU reference: hybrid SWA/GA, dense layer 0, MoE after,
/// residual + RMSNorm, SwiGLU experts. Used to lock routing and residual
/// math. Not a quality stand-in for the 309B model.
/// </summary>
public sealed class ToyMimoCpuForward
{
    public const int HiddenSize = 8;
    public const int VocabSize = 32;
    public const int NumLayers = 2;
    public const int NExperts = 4;
    public const int TopK = 2;
    public const int Heads = 4;
    public const int GaKvHeads = 1;
    public const int SwaKvHeads = 2;
    public const int HeadDim = 2;
    public const int Window = 4;
    public const int DenseFfn = 16;
    public const int MoeFfn = 8;

    public ModelShape Shape { get; }
    public ToyWeights Weights { get; }

    public ToyMimoCpuForward(int seed = 7)
    {
        Shape = new ModelShape
        {
            Architecture = "toy-mimo",
            TotalParameters = 1,
            ActiveParameters = 1,
            HiddenSize = HiddenSize,
            VocabSize = VocabSize,
            NumLayers = NumLayers,
            NRoutedExperts = NExperts,
            NumExpertsPerTok = TopK,
            MoeIntermediateSize = MoeFfn,
            DenseIntermediateSize = DenseFfn,
            NumAttentionHeads = Heads,
            GaKvHeads = GaKvHeads,
            SwaKvHeads = SwaKvHeads,
            HeadDim = HeadDim,
            VHeadDim = HeadDim,
            SlidingWindow = Window,
            MaxContext = 64,
            HybridLayerPattern = [0, 1],
            MoeLayerFreq = [0, 1],
            ScoringFunc = "sigmoid",
            TopkMethod = "noaux_tc",
            NormTopkProb = true,
        };
        Weights = ToyWeights.Seeded(seed);
    }

    public ForwardResult Forward(int[] tokenIds)
    {
        if (tokenIds.Length == 0)
            throw new ArgumentException("Need at least one token.");

        int t = tokenIds.Length;
        var x = new float[t * HiddenSize];
        for (int i = 0; i < t; i++)
        {
            int id = tokenIds[i];
            if ((uint)id >= VocabSize)
                throw new ArgumentOutOfRangeException(nameof(tokenIds));
            Array.Copy(Weights.Embed, id * HiddenSize, x, i * HiddenSize, HiddenSize);
        }

        var routed = new List<int[]>(t);
        for (int i = 0; i < t; i++)
            routed.Add([]);

        for (int layer = 0; layer < NumLayers; layer++)
        {
            var xn = RmsNorm(x, Weights.AttnNorm[layer]);
            var attn = Attention(xn, layer, t);
            AddInPlace(x, attn);
            var fn = RmsNorm(x, Weights.FfnNorm[layer]);
            float[] ffn;
            if (!Shape.IsMoeLayer(layer))
            {
                ffn = DenseSwiglu(fn, Weights.DenseGate, Weights.DenseUp, Weights.DenseDown);
            }
            else
            {
                ffn = new float[t * HiddenSize];
                for (int i = 0; i < t; i++)
                {
                    var h = fn.AsSpan(i * HiddenSize, HiddenSize);
                    var decision = ExpertRouter.Route(h, Weights.Router, Shape);
                    routed[i] = decision.ExpertIds;
                    var acc = new float[HiddenSize];
                    for (int k = 0; k < decision.ExpertIds.Length; k++)
                    {
                        int e = decision.ExpertIds[k];
                        var y = Swiglu(h, Weights.ExpertGate[e], Weights.ExpertUp[e], Weights.ExpertDown[e]);
                        for (int d = 0; d < HiddenSize; d++)
                            acc[d] += decision.Weights[k] * y[d];
                    }
                    acc.CopyTo(ffn, i * HiddenSize);
                }
            }
            AddInPlace(x, ffn);
        }

        var final = RmsNorm(x, Weights.FinalNorm);
        var logits = MatVecBatch(final, Weights.LmHead, t, HiddenSize, VocabSize);
        return new ForwardResult(logits, routed);
    }

    private float[] Attention(float[] x, int layer, int tokens)
    {
        bool ga = Shape.IsGlobalAttention(layer);
        int kvHeads = ga ? GaKvHeads : SwaKvHeads;
        int qHeads = Heads;
        var wq = Weights.Wq[layer];
        var wk = Weights.Wk[layer];
        var wv = Weights.Wv[layer];
        var wo = Weights.Wo[layer];

        var q = MatVecBatch(x, wq, tokens, HiddenSize, qHeads * HeadDim);
        var k = MatVecBatch(x, wk, tokens, HiddenSize, kvHeads * HeadDim);
        var v = MatVecBatch(x, wv, tokens, HiddenSize, kvHeads * HeadDim);
        var ctx = new float[tokens * qHeads * HeadDim];
        float scale = 1f / MathF.Sqrt(HeadDim);
        int reps = qHeads / kvHeads;
        var scores = new float[tokens];

        for (int t = 0; t < tokens; t++)
        {
            int start = ga ? 0 : Math.Max(0, t - Window + 1);
            for (int h = 0; h < qHeads; h++)
            {
                int kvh = h / reps;
                float max = float.NegativeInfinity;
                int n = 0;
                for (int s = start; s <= t; s++, n++)
                {
                    float dot = 0f;
                    int qOff = t * qHeads * HeadDim + h * HeadDim;
                    int kOff = s * kvHeads * HeadDim + kvh * HeadDim;
                    for (int d = 0; d < HeadDim; d++)
                        dot += q[qOff + d] * k[kOff + d];
                    scores[n] = dot * scale;
                    if (scores[n] > max) max = scores[n];
                }
                float sum = 0f;
                for (int i = 0; i < n; i++)
                {
                    scores[i] = MathF.Exp(scores[i] - max);
                    sum += scores[i];
                }
                int oOff = t * qHeads * HeadDim + h * HeadDim;
                for (int i = 0; i < n; i++)
                {
                    float a = scores[i] / sum;
                    int s = start + i;
                    int vOff = s * kvHeads * HeadDim + kvh * HeadDim;
                    for (int d = 0; d < HeadDim; d++)
                        ctx[oOff + d] += a * v[vOff + d];
                }
            }
        }
        return MatVecBatch(ctx, wo, tokens, qHeads * HeadDim, HiddenSize);
    }

    private static float[] DenseSwiglu(float[] x, float[] gate, float[] up, float[] down)
    {
        int tokens = x.Length / HiddenSize;
        var y = new float[x.Length];
        for (int i = 0; i < tokens; i++)
        {
            var row = Swiglu(x.AsSpan(i * HiddenSize, HiddenSize), gate, up, down);
            row.CopyTo(y, i * HiddenSize);
        }
        return y;
    }

    private static float[] Swiglu(ReadOnlySpan<float> x, float[] gate, float[] up, float[] down)
    {
        int inter = gate.Length / HiddenSize;
        var g = new float[inter];
        var u = new float[inter];
        for (int j = 0; j < inter; j++)
        {
            float gd = 0, ud = 0;
            int row = j * HiddenSize;
            for (int i = 0; i < HiddenSize; i++)
            {
                gd += x[i] * gate[row + i];
                ud += x[i] * up[row + i];
            }
            g[j] = Silu(gd) * ud;
        }
        var y = new float[HiddenSize];
        for (int i = 0; i < HiddenSize; i++)
        {
            float acc = 0;
            for (int j = 0; j < inter; j++)
                acc += g[j] * down[i * inter + j];
            y[i] = acc;
        }
        return y;
    }

    private static float Silu(float x) => x / (1f + MathF.Exp(-x));

    private static float[] RmsNorm(float[] x, float[] g)
    {
        int tokens = x.Length / HiddenSize;
        var y = new float[x.Length];
        const float eps = 1e-6f;
        for (int t = 0; t < tokens; t++)
        {
            float ms = 0;
            int off = t * HiddenSize;
            for (int i = 0; i < HiddenSize; i++)
                ms += x[off + i] * x[off + i];
            float inv = 1f / MathF.Sqrt(ms / HiddenSize + eps);
            for (int i = 0; i < HiddenSize; i++)
                y[off + i] = x[off + i] * inv * g[i];
        }
        return y;
    }

    private static void AddInPlace(float[] a, float[] b)
    {
        for (int i = 0; i < a.Length; i++)
            a[i] += b[i];
    }

    private static float[] MatVecBatch(float[] x, float[] w, int tokens, int inDim, int outDim)
    {
        var y = new float[tokens * outDim];
        for (int t = 0; t < tokens; t++)
        {
            for (int o = 0; o < outDim; o++)
            {
                float acc = 0;
                int row = o * inDim;
                int xOff = t * inDim;
                for (int i = 0; i < inDim; i++)
                    acc += x[xOff + i] * w[row + i];
                y[t * outDim + o] = acc;
            }
        }
        return y;
    }

    public sealed record ForwardResult(float[] Logits, IReadOnlyList<int[]> RoutedExpertsPerToken);

    public sealed class ToyWeights
    {
        public required float[] Embed { get; init; }
        public required float[] LmHead { get; init; }
        public required float[] FinalNorm { get; init; }
        public required float[][] AttnNorm { get; init; }
        public required float[][] FfnNorm { get; init; }
        public required float[][] Wq { get; init; }
        public required float[][] Wk { get; init; }
        public required float[][] Wv { get; init; }
        public required float[][] Wo { get; init; }
        public required float[] DenseGate { get; init; }
        public required float[] DenseUp { get; init; }
        public required float[] DenseDown { get; init; }
        public required float[] Router { get; init; }
        public required float[][] ExpertGate { get; init; }
        public required float[][] ExpertUp { get; init; }
        public required float[][] ExpertDown { get; init; }

        public static ToyWeights Seeded(int seed)
        {
            var rng = new Random(seed);
            float[] R(int n)
            {
                var a = new float[n];
                for (int i = 0; i < n; i++)
                    a[i] = (float)(rng.NextDouble() * 0.4 - 0.2);
                return a;
            }
            float[] Ones(int n)
            {
                var a = new float[n];
                Array.Fill(a, 1f);
                return a;
            }

            return new ToyWeights
            {
                Embed = R(VocabSize * HiddenSize),
                LmHead = R(VocabSize * HiddenSize),
                FinalNorm = Ones(HiddenSize),
                AttnNorm = [Ones(HiddenSize), Ones(HiddenSize)],
                FfnNorm = [Ones(HiddenSize), Ones(HiddenSize)],
                Wq = [R(Heads * HeadDim * HiddenSize), R(Heads * HeadDim * HiddenSize)],
                Wk = [R(GaKvHeads * HeadDim * HiddenSize), R(SwaKvHeads * HeadDim * HiddenSize)],
                Wv = [R(GaKvHeads * HeadDim * HiddenSize), R(SwaKvHeads * HeadDim * HiddenSize)],
                Wo = [R(HiddenSize * Heads * HeadDim), R(HiddenSize * Heads * HeadDim)],
                DenseGate = R(DenseFfn * HiddenSize),
                DenseUp = R(DenseFfn * HiddenSize),
                DenseDown = R(HiddenSize * DenseFfn),
                Router = R(HiddenSize * NExperts),
                ExpertGate = Enumerable.Range(0, NExperts).Select(_ => R(MoeFfn * HiddenSize)).ToArray(),
                ExpertUp = Enumerable.Range(0, NExperts).Select(_ => R(MoeFfn * HiddenSize)).ToArray(),
                ExpertDown = Enumerable.Range(0, NExperts).Select(_ => R(HiddenSize * MoeFfn)).ToArray(),
            };
        }
    }
}
