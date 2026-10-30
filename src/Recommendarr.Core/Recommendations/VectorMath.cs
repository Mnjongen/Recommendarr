using System.Numerics.Tensors;

namespace Recommendarr.Core.Recommendations;

public static class VectorMath
{
    public static float Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length)
            throw new ArgumentException($"Dimension mismatch: {a.Length} vs {b.Length}.");

        return TensorPrimitives.CosineSimilarity(a, b);
    }

    /// <summary>Returns a unit-length copy of <paramref name="v"/>; a zero vector is returned unchanged.</summary>
    public static float[] Normalize(ReadOnlySpan<float> v)
    {
        var result = v.ToArray();
        var norm = TensorPrimitives.Norm(v);
        if (norm > 0)
            TensorPrimitives.Divide(result, norm, result);
        return result;
    }

    /// <summary>Weighted mean of equally sized vectors.</summary>
    public static float[] WeightedMean(IReadOnlyList<float[]> vectors, IReadOnlyList<double> weights)
    {
        if (vectors.Count == 0)
            throw new ArgumentException("At least one vector is required.", nameof(vectors));
        if (vectors.Count != weights.Count)
            throw new ArgumentException("Each vector needs a weight.", nameof(weights));

        var dim = vectors[0].Length;
        var sum = new double[dim];
        double total = 0;
        for (var i = 0; i < vectors.Count; i++)
        {
            if (vectors[i].Length != dim)
                throw new ArgumentException($"Dimension mismatch at index {i}.");
            for (var d = 0; d < dim; d++)
                sum[d] += vectors[i][d] * weights[i];
            total += weights[i];
        }

        var result = new float[dim];
        if (total > 0)
        {
            for (var d = 0; d < dim; d++)
                result[d] = (float)(sum[d] / total);
        }

        return result;
    }
}
